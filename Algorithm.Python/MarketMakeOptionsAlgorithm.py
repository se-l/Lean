import random

from collections import defaultdict
from core.option_contract_filters import filter_is_liquid, filter_option_no_position
from core.position import Position
from core.risk.portfolio import PortfolioRisk
from core.stubs import *
from functools import partial, reduce
from core.log import log_dividend, log_contract, log_order_event, log_pl, humanize, log_last_price_change
from core.utils import RiskLmt, MMWindow, round_tick, get_contract, mid_price, tick_size
from core.constants import BP, DIRECTION2NUM
from core.pricing.option_contract_wrap import OptionContractWrap


class MarketMakeOptions(QCAlgorithm):
    """
    In Anaconda Prompt py310
    lean cloud push --project "MarketMakeOptions"
    lean cloud pull --project "MarketMakeOptions"
    lean cloud backtest "MarketMakeOptions" --open --push
    """
    risk_is_lmt = None
    risk_if_lmt = None
    risk_is_unhedged_position_ratio_lmt = 0.1

    def __init__(self):
        super().__init__()
        self.sod_slices = self.log_frequency = self.equity_price_increments = self.mm_window = self.max_strike_distance = self.q_options = \
            self.one_side_cancel_options_equity_position_usd = self.tickets_option_contracts = self.tickets_equity = self.sym_sod_price_mid = \
            self.last_price = self.order_type = self.ticker = self.equities = self.options = self.option_chains = \
            self.unwind = self.resolution = self.fct_is_if = self.simulated_missed_gain = self.start_value = None

    def Initialize(self):
        self.UniverseSettings.Resolution = self.resolution = Resolution.Minute
        self.SetStartDate(2023, 5, 3)
        self.SetEndDate(2023, 5, 3)
        self.SetCash(100000)
        self.SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin)
        self.UniverseSettings.DataNormalizationMode = DataNormalizationMode.SplitAdjusted

        self.order_events: List[OrderEvent] = []
        self.log_frequency = 60 * 5 if self.LiveMode else 60 * 60 * 99999
        self.equity_price_increments: int = 0

        self.mm_window = MMWindow(time(9, 40, 0), time(15, 50, 0))  # after 15:50 may want to hedge with stocks
        self.max_strike_distance = 1
        self.q_options = 1
        self.one_side_cancel_options_equity_position_usd = 100
        self.tickets_option_contracts: List[OrderTicket] = []
        self.tickets_equity: List[OrderTicket] = []
        self.order_type = OrderType.Limit

        self.ticker = ['HPE', 'IPG', 'SPY']
        # self.AddUniverse(self.SelectCoarseCloud, self.SelectFine)
        self.equities: List[Equity] = []
        self.options: List[Option] = []
        self.option_chains: Dict[Union[Option, Equity], Union[OptionChain, None]] = {}
        self.unwind = False

        for ticker in self.ticker:
            equity = self.AddEquity(ticker, resolution=self.resolution)
            self.equities.append(equity)
            if ticker not in ['SPY']:
                option = self.AddOption(ticker, resolution=self.resolution)
                option.PriceModel = CurrentPriceOptionPriceModel()
                option.SetFilter(-3, 3, timedelta(30), timedelta(365))
                self.options.append(option)
                self.option_chains[option] = self.option_chains[equity] = None
        self.AddUniverseSelection(ManualUniverseSelectionModel([e.Symbol for e in self.equities]))
        # self.SetWarmup(5 * 60 * 24, Resolution.Minute)
        self.SetSecurityInitializer(lambda x: x.SetMarketPrice(self.GetLastKnownPrice(x)))
        self.risk_is_lmt = RiskLmt(100_000, 1_000)
        self.fct_is_if = 5
        self.risk_if_lmt = RiskLmt(self.risk_is_lmt.position * self.fct_is_if, self.risk_is_lmt.unhedged * self.fct_is_if)
        self.simulated_missed_gain = 0
        self.start_value = self.Portfolio.TotalPortfolioValue

        for equity in self.equities:
            equity.VolatilityModel = StandardDeviationOfReturnsVolatilityModel(periods=30, resolution=Resolution.Daily)
            # https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/options-models/volatility/key-concepts
            # https://github.com/QuantConnect/Lean/blob/master/Algorithm.Python/CustomVolatilityModelAlgorithm.py
            # https://www.quantconnect.com/docs/v2/writing-algorithms/indicators/manual-indicators
        self.SetWarmUp(30, Resolution.Daily)
        self.sym_sod_price_mid: Dict[Symbol, float] = {}
        self.last_price: Dict[Symbol, float] = defaultdict(lambda: 0)

        self.Schedule.On(self.DateRules.EveryDay("SPY"),
                         self.TimeRules.BeforeMarketClose("SPY", -10),
                         self.hedge_with_index)

    def OnData(self, data: Slice):
        # Check if any of the data requests failed
        from core.state import action
        if self.IsWarmingUp:
            return

        for symbol in data.keys():  # Could be Equity, Option, Dividend
            if symbol not in self.sym_sod_price_mid:
                self.sym_sod_price_mid[symbol] = mid_price(self, symbol)
            if str(symbol) == "SPY":
                continue

            if data.Dividends.ContainsKey(symbol):
                log_dividend(self, data, symbol)

            equity, option = self.equity_option_from_symbol(symbol)
            if option:
                # self.Debug(f'{self.Time} {symbol.Value} {data[symbol].Close}')
                if chain := data.OptionChains.get(option.Symbol):
                    self.option_chains[equity] = self.option_chains[option] = chain
                    for contract in chain:
                        if contract.Symbol not in self.sym_sod_price_mid:
                            self.sym_sod_price_mid[contract] = mid_price(self, symbol)

                        log_last_price_change(self, contract.Symbol)

                    self.update_contract_limit_prices(equity, chain)
            self.update_equity_limit_prices(equity)

            # Minute Bars is not event driven. Triggering actions even if nothing changed... Bad
            action(self, equity, option)

        # CleanUp tickets
        self.tickets_option_contracts = [t for t in self.tickets_option_contracts
                                         if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled)]
        self.tickets_equity = [t for t in self.tickets_equity if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled)]
        if self.Time.time() >= self.mm_window.stop:
            for t in self.tickets_option_contracts:
                t.Cancel()

        # self.simulate_fill()
        # if (self.Portfolio.TotalPortfolioValue - self.start_value) < -200:
        #     for ticket in self.tickets_option_contracts:
        #         ticket.Cancel()
        #     for ticket in self.tickets_equity:
        #         ticket.Cancel()
        #     self.Liquidate()
        #     self.Log(f'Creating RunTime Error: Profit {self.Portfolio.TotalPortfolioValue - self.start_value}')
        #     raise ValueError

    def OnOrderEvent(self, order_event: OrderEvent):
        self.order_events.append(order_event)
        from core.state import action
        log_order_event(self, order_event)
        equity, option = self.equity_option_from_symbol(order_event.Symbol)
        if order_event.Status in (OrderStatus.Filled, OrderStatus.PartiallyFilled) and not self.unwind:
            action(self, equity, option)

    def OnEndOfDay(self, symbol: str):
        self.on_end_of_day()

    def OnEndOfAlgorithm(self):
        self.on_end_of_day()
        self.Log('QC parent OnEndOfAlgorithm called.')
        super().OnEndOfAlgorithm()

    #########################################################################################################

    def on_end_of_day(self, cache=defaultdict(lambda: False)):
        """
        Log Positions and corresponding PnL, Risk, etc. for ensued analysis. Time series grid & Group-bys.
        Log Portfolio Risk
        Given a position's risk is calculated with the respect to whole portfolio, their sum should equal PF risk.
        """
        if self.IsWarmingUp or cache[self.Time.date()]:
            return

        positions = [Position.e(self, symbol) for symbol, holding in self.Portfolio.items() if holding.Quantity != 0]
        estimated_position_valuation_gain = sum([abs(p.multiplier * p.spread * p.quantity) for p in positions])
        pf_risk = PortfolioRisk.e(self)

        # Write the positions to a .json file
        positions_json = [p.to_json() for p in positions]
        import json
        from pathlib import Path
        Path(__name__).parent.joinpath('positions.json').write_text(json.dumps(positions_json, indent=4))

        self.Log(f'simulated_missed_gain: {self.simulated_missed_gain}')
        self.Log(f'Cash: {self.Portfolio.Cash}')
        self.Log(f'UnsettledCash: {self.Portfolio.UnsettledCash}')
        # self.Log(f'TotalHoldingsValue: {self.Portfolio.TotalHoldingsValue}')
        self.Log(f'TotalFees: {self.Portfolio.TotalFees}')
        self.Log(f'TotalNetProfit: {self.Portfolio.TotalNetProfit}')
        self.Log(f'TotalUnrealizedProfit: {self.Portfolio.TotalUnrealizedProfit}')
        self.Log(f'TotalPortfolioValue: {self.Portfolio.TotalPortfolioValue}')

        estimated_portfolio_value = self.Portfolio.TotalPortfolioValue + self.simulated_missed_gain + estimated_position_valuation_gain
        self.Log(f'EstimatedPortfolioValue: {estimated_portfolio_value}')

        cache[self.Time.date()] = True

    def simulate_fill(self):
        if not self.LiveMode:
            for ticket in self.tickets_option_contracts:
                contract: OptionContract = self.Securities.get(ticket.Symbol)
                limit_price = ticket.Get(OrderField.LimitPrice)
                if ticket.Quantity > 0 and limit_price < contract.BidPrice:
                    continue
                elif ticket.Quantity < 0 and limit_price > contract.AskPrice:
                    continue
                p_fill = contract.Volume / (24 * 60 * 1)
                if random.choices([True, False], [p_fill, 1 - p_fill])[0]:
                    ticket.Cancel()
                    self.MarketOrder(contract.Symbol, ticket.Quantity, tag=ticket.Tag)
                    self.simulated_missed_gain += (contract.AskPrice - contract.BidPrice) * 100 * abs(ticket.Quantity)
                    self.Log(f'{self.simulated_missed_gain}: self.simulated_missed_gain')

    def hedge_with_index(self):
        """We want to have a parameterized target delta. If market takes a downturn, update target hedge via MQ and hedge along..."""
        if self.IsWarmingUp:
            return
        from core.risk.portfolio import PortfolioRisk
        pf_risk = PortfolioRisk.e(self)
        # Ideally get an option on SPY matching delta, gamma, vega, theta - unlikely. just hedge delta now...
        quantity = int(-(pf_risk.delta / pf_risk.ppi.beta('SPY')) / self.Securities['SPY'].Price)
        self.MarketOrder('SPY', quantity=quantity)

    def equity_option_from_symbol(self, symbol: Symbol) -> Tuple[Equity | None, Option | None]:
        """Not handling contracts..."""
        security: Equity | OptionContract | Option = self.Securities.get(symbol)

        if symbol.SecurityType == SecurityType.Option:
            if hasattr(security, 'StrikePrice'):  # OptionContract
                contract: OptionContract = security
                equity: Equity | Security = self.Securities[contract.Underlying.Symbol]
                option = next((o for o in self.options if o.Underlying == equity), None)
            else:  # Option
                option: Option = security
                equity = self.Securities.get(option.Underlying.Symbol)
        elif symbol.SecurityType == SecurityType.Equity:
            equity = security
            option = next((o for o in self.options if o.Underlying == equity), None)

            if option is None:
                self.Log(f'Failed to derive Option from Equity {equity.Symbol}. Not processing.... Missed a subscription?')
        else:
            self.Log(f'Failed to derive Security Type of {symbol}. Not processing...')
            equity = option = None
        return equity, option

    def cancel_open_option_bids(self, order_event: OrderEvent):
        sym = order_event.Symbol
        for ticket in self.tickets_option_contracts:
            if ticket.Symbol.Underlying == order_event.Symbol.Underlying:
                ticket.Cancel(f"Cancel other options due to Fill of {sym}")

    def order_option(self, option: Option, option_right: OptionRight, quantity: int,
                     order_type: OrderType = OrderType.Limit):
        """Limit order option contracts within scoped option chain (contract symbols)
        LO put&call expiry > 7 strike <=2 volume >=50 open_interest >= 50 OTM ITM
        """
        from core.state import contract_in
        if option_chain := self.option_chains.get(option):
            for contract in self.select_option_contracts(option_chain, f_filter=[
                # partial(filter_option_otm, option_chain=option_chain, option_right=option_right),
                # partial(filter_option_open_interest),  # No open interest data locally yet. Need other API
                # partial(filter_option_volume),
                partial(filter_is_liquid, algo=self, window=3),
                lambda option_contracts: [c for c in option_contracts if c.BidPrice and c.AskPrice],
                partial(filter_option_no_position, algo=self)
            ]):
                if contract_in(self.tickets_option_contracts, contract):
                    return
                self.order_option_contract(contract, quantity, order_type)

    def order_option_contract(self,
                              contract: OptionContract,
                              quantity: float,
                              order_type: OrderType = OrderType.Limit):
        from core.risk.portfolio import PortfolioRisk

        order_direction = OrderDirection.Buy if quantity > 0 else OrderDirection.Sell
        limit_price = self.price_option_pf_risk_adjusted(contract, PortfolioRisk.e(self), order_direction)

        if limit_price is None:
            self.Log('Failed to derive limit price. Not processing...')
            return
        if limit_price < tick_size(self, contract):
            return
        order_msg = log_contract(self, contract, order_direction, limit_price, order_type)
        if order_type == OrderType.Market:
            self.MarketOrder(contract.Symbol, quantity, tag=order_msg)
        else:
            ticket = self.LimitOrder(
                contract.Symbol,
                quantity,
                limit_price,
                order_msg
            )
            self.tickets_option_contracts.append(ticket)

    def update_contract_limit_prices(self, equity: Equity, chain: OptionChain):
        from core.risk.portfolio import PortfolioRisk
        tick_size = self.Securities[equity.Symbol].SymbolProperties.MinimumPriceVariation
        tickets: List[OrderTicket] = [t for t in self.tickets_option_contracts if t.Symbol.Underlying == equity.Symbol]
        for t in tickets:
            if t.Status in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.CancelPending, OrderStatus.Invalid):
                continue
            limit_price = t.Get(OrderField.LimitPrice)
            contract: OptionContract = chain.Contracts.get(t.Symbol)
            order_direction = OrderDirection.Buy if t.Quantity > 0 else OrderDirection.Sell
            ideal_limit_price = price_theoretical = self.price_option_pf_risk_adjusted(contract, PortfolioRisk.e(self), order_direction)
            if price_theoretical is None:
                self.Log('Failed to derive limit price. Not processing...')

            if t.UpdateRequests and t.UpdateRequests[-1].LimitPrice == ideal_limit_price:
                continue
            elif ideal_limit_price != limit_price and ideal_limit_price >= tick_size:
                tag = f"Price not good {limit_price}: Changing to ideal limit price: {ideal_limit_price}"
                if ideal_limit_price < tick_size:
                    t.Cancel()
                else:
                    response = t.UpdateLimitPrice(ideal_limit_price, tag)
                    self.Log(f'UpdateLimitPrice response: {response}')

    def update_equity_limit_prices(self, equity: Equity):
        tick_size = self.Securities[equity.Symbol].SymbolProperties.MinimumPriceVariation
        tickets: List[OrderTicket] = [t for t in self.tickets_equity if t.Symbol == equity.Symbol]
        for t in tickets:
            if t.Status in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.CancelPending, OrderStatus.Invalid):
                # bad bug. Canceled already, still went through and created market order... could ruin me 
                continue
            elif self.LiveMode and len(t.UpdateRequests) > 1:  # Chasing the market. Risky. Market Order
                t.Cancel()
                self.MarketOrder(t.Symbol, t.Quantity, tag=t.Tag.replace('Limit', 'Market'))
            else:
                limit_price = t.Get(OrderField.LimitPrice)
                if t.Quantity > 0 and (equity.BidSize > abs(t.Quantity) if self.LiveMode else True):  # should not outbid myself...
                    ideal_limit_price = equity.BidPrice + self.equity_price_increments * tick_size
                elif t.Quantity < 0 and (equity.BidSize > abs(t.Quantity) if self.LiveMode else True):  # should not outbid myself...
                    ideal_limit_price = equity.AskPrice - self.equity_price_increments * tick_size
                else:
                    continue
                if round(ideal_limit_price, 2) != round(limit_price, 2) and ideal_limit_price > 0:
                    tag = f"Price not good {limit_price}: Changing to ideal limit price: {ideal_limit_price}"
                    response = t.UpdateLimitPrice(ideal_limit_price, tag)
                    self.Log(f'UpdateLimitPrice response: {response}')

    def select_option_contracts(self,
                                option_chain: OptionChain,
                                f_filter: List[Callable],
                                cache={}
                                ) -> List[OptionContract]:
        """
        Get an options contract that matches the specified criteria:
        Underlying symbol, delta, days till expiration, Option right (put or call)
        """
        # self.Debug(cache)
        if cache.get('date') == self.Time.date():  # and cache['f_filter'] == reduce(lambda res, f: res+hash(f), f_filter, 0):
            return cache.get('contracts')
        else:
            contracts: List[OptionContract] = reduce(lambda res, f: f(res), f_filter, option_chain)
            # min_expiry_date = self.Time + timedelta(days=expiry_delta_days)            
            cache['date'] = self.Time.date()
            cache['f_filter'] = reduce(lambda res, f: res + hash(f), f_filter, 0)
            cache['contracts'] = contracts
            return contracts

    def price_option_pf_risk_adjusted(self, contract: OptionContract, pf_risk: PortfolioRisk, order_direction: OrderDirection) -> float:
        ts = tick_size(self, contract)
        ocw = OptionContractWrap(self, contract)

        # bid_price_theoretical, ask_price_theoretical = get_price_option(algo, contract) if contract.BidPrice and contract.AskPrice else (None, None)
        # if bid_price_theoretical is None or ask_price_theoretical is None:
        #     return None
        # #mid_price_theoretical = bid_price_theoretical + (ask_price_theoretical - bid_price_theoretical) / 2
        # #bid_iv, ask_iv = get_iv(algo, contract)
        # # Use Quantlib
        # # print(contract.ImpliedVolatility)
        bid_price_theoretical = contract.BidPrice
        ask_price_theoretical = contract.AskPrice

        if order_direction == OrderDirection.Buy:
            price_theoretical = bid_price_theoretical
        elif OrderDirection.Sell:
            price_theoretical = ask_price_theoretical
        else:
            raise ValueError(f'Invalid order direction: {order_direction}')
        best_bid = contract.BidPrice
        best_ask = contract.AskPrice

        # # Adjust theoretical price for portfolio risk
        increases_pf_delta = DIRECTION2NUM[order_direction] * ocw.greeks().delta
        if increases_pf_delta and pf_risk.delta > 0:  # Don't want this trade much
            limit_price = round_tick(min(price_theoretical, best_bid), tick_size=ts)
        elif increases_pf_delta and pf_risk.delta < 0:  # Want this trade much
            limit_price = round_tick(max(price_theoretical, best_ask), tick_size=ts)
        else:  # no pf_risk.delta
            if order_direction == OrderDirection.Buy:
                limit_price = min(best_bid, round_tick(price_theoretical, tick_size=ts))
            elif order_direction == OrderDirection.Sell:
                limit_price = max(best_ask, round_tick(price_theoretical, tick_size=ts))
            else:
                raise ValueError(f'Invalid order direction: {order_direction}')
        return limit_price
