import clr
import json
import random
import getpass

from pathlib import Path
from itertools import chain
from collections import defaultdict, deque
from core.events.handler import publish_event
from core.events.high_portfolio_risk import is_high_portfolio_risk, EventHighPortfolioRisk
from core.events.new_bid_ask import is_event_new_bid_ask, EventNewBidAsk
from core.events.signal import get_signals, filter_signal_by_risk, EventSignals
from core.position import Position
from core.risk.limit import RiskLimit
from core.stubs import *
from core.log import log_contract, log_order_event, log_risk, quick_log
from core.utils import MMWindow, round_tick, get_contract, mid_price, tick_size, cancel_open_tickets, profile_performance
from core.constants import SEB, DIRECTION2NUM
from core.pricing.option_contract_wrap import OptionContractWrap
from core.risk.portfolio import PortfolioRisk
from core.cache import cache

clr.AddReference("QuantConnect.ToolBox")
from QuantConnect.ToolBox.IQFeed.IQ import IQOptionChainProvider

user = getpass.getuser()
if user == SEB:
    import cProfile


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
        self.sod_slices = self.equity_price_increments = self.mm_window = \
            self.order_tickets = self.order_events = self.sym_sod_price_mid = \
            self.last_price = self.order_type = self.ticker = self.equities = self.options = self.option_chains = \
            self.resolution = self.min_correlation = self.fct_is_if = self.simulated_missed_gain = self.start_value = self.hedge_ticker = \
            self.slices = self.option_ticker = self.risk_limit = None

    def Initialize(self):
        self.UniverseSettings.Resolution = self.resolution = Resolution.Minute
        self.SetStartDate(2023, 5, 10)
        self.SetEndDate(2023, 5, 10)
        self.SetCash(100000)
        self.SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin)
        # self.SetSecurityInitializer(MySecurityInitializer(self.BrokerageModel, FuncSecuritySeeder(self.GetLastKnownPrices)))
        self.UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw
        if self.LiveMode:
            self.SetOptionChainProvider(IQOptionChainProvider())
        self.SetSecurityInitializer(lambda x: x.SetMarketPrice(self.GetLastKnownPrice(x)))

        self.equity_price_increments: float = 0  # refactor pct if needed
        self.mm_window = MMWindow(time(9, 30, 0), time(15, 58, 0))
        self.min_correlation = 0.0  # for paper trading. Should be rather 0.3 at least..
        self.risk_limit = RiskLimit(40, 100_000)
        self.volatility_span = 20

        self.order_events: List[OrderEvent] = []
        self.order_tickets: Dict[Symbol, List[OrderTicket]] = defaultdict(list)
        self.order_type = OrderType.Limit

        self.hedge_ticker = ['SPY']
        self.option_ticker = ['HPE', 'IPG', 'AKAM', 'AOS', 'A', 'MO', 'FL', 'ALL', 'ARE', 'ZBRA', 'AES', 'APD', 'ALLE', 'LNT', 'ZTS', 'ZBH']
        self.option_ticker = ['HPE', 'IPG']
        self.ticker = self.option_ticker + self.hedge_ticker
        self.equities: List[Symbol] = []
        self.options: List[Symbol] = []  # Canonical symbols
        self.option_chains: Dict[Symbol, Union[List[OptionContract], None]] = {}

        subscriptions = 0
        for ticker in self.ticker:
            equity = self.AddEquity(ticker, resolution=self.resolution)
            # rather move this into the security initializer
            equity.VolatilityModel = StandardDeviationOfReturnsVolatilityModel(periods=self.volatility_span, resolution=Resolution.Daily)
            # https://www.quantconnect.com/docs/v2/writing-algorithms/reality-modeling/options-models/volatility/key-concepts
            # https://github.com/QuantConnect/Lean/blob/master/Algorithm.Python/CustomVolatilityModelAlgorithm.py
            # https://www.quantconnect.com/docs/v2/writing-algorithms/indicators/manual-indicators
            subscriptions += 1
            self.equities.append(equity.Symbol)
            if ticker in self.option_ticker:

                option = Symbol.CreateCanonicalOption(equity.Symbol, Market.USA)
                self.options.append(option)
                self.option_chains[option] = self.option_chains[equity.Symbol] = None

                history: List[TradeBars] = self.History[TradeBar](symbol=equity.Symbol, start=self.StartDate - timedelta(days=30), end=self.StartDate, resolution=Resolution.Daily)
                try:
                    latest_close = list(history)[-1].Close
                except IndexError as e:
                    self.Debug(f'No history for {ticker}. Not subscribing to any relating options.')
                    continue

                contract_symbols = self.OptionChainProvider.GetOptionContractList(ticker, self.Time)
                for symbol in contract_symbols:
                    if self.Time + timedelta(days=60) < symbol.ID.Date < self.Time + timedelta(days=365) and \
                        symbol.ID.OptionStyle == OptionStyle.American and \
                        latest_close * .9 < symbol.ID.StrikePrice < latest_close * 1.1:
                        # improve: close to ATM +/- 1 Strike. 3 contracts per sym and maturity! can reduce
                         #further. if ATM is O for call, strike +1 => only 2 contracts per sym.

                        option_contract = self.AddOptionContract(symbol, resolution=self.resolution)
                        option_contract.SetFilter(minStrike=-3, maxStrike=3)
                        option_contract.PriceModel = CurrentPriceOptionPriceModel()
                        subscriptions += 1
        self.Debug(f'Subscribing to {subscriptions} securities')

        self.AddUniverseSelection(ManualUniverseSelectionModel(self.equities))
        
        self.simulated_missed_gain = 0
        self.start_value = self.Portfolio.TotalPortfolioValue

        self.SetWarmUp(int(self.volatility_span * 1.5), Resolution.Daily)
        self.sym_sod_price_mid: Dict[Symbol, float] = {}
        self.last_price: Dict[Symbol, float] = defaultdict(lambda: 0)
        self.slices: Deque[Slice] = deque(maxlen=2)
        self.pf_risks: Deque[PortfolioRisk] = deque(maxlen=2)

        # Scheduled functions

        self.Schedule.On(self.DateRules.EveryDay(self.hedge_ticker[0]),
                         self.TimeRules.BeforeMarketClose(self.hedge_ticker[0], 1),
                         self.hedge_with_index)

        self.Schedule.On(self.DateRules.EveryDay(self.hedge_ticker[0]),
                         self.TimeRules.AfterMarketOpen(self.hedge_ticker[0]),
                         self.populate_option_chains)

        self.Schedule.On(self.DateRules.EveryDay(self.hedge_ticker[0]),
                         self.TimeRules.Every(timedelta(minutes=30)),
                         self.log_risk_schedule)

        self.profile = True and self.LiveMode
        if self.profile:
            self.profiler = cProfile.Profile()
            self.profiler.enable()

    def OnData(self, data: Slice):
        self.slices.append(data)
        if self.IsWarmingUp:
            return

        for symbol in self.Securities.Keys:
            if symbol not in self.sym_sod_price_mid:
                self.sym_sod_price_mid[symbol] = mid_price(self, symbol)

            if is_event_new_bid_ask(self, data, symbol):  # proxy for new mid-price!
                publish_event(self, EventNewBidAsk(symbol))

        pf_risk = PortfolioRisk.e(self)  # Log the risk on every price change and order Event!
        self.pf_risks.append(pf_risk)
        if is_high_portfolio_risk(self):
            publish_event(self, EventHighPortfolioRisk(pf_risk))

        if signals := get_signals(self):
            if signals := filter_signal_by_risk(self, signals):  # once anything is filled. this calcs a lot
                publish_event(self, EventSignals(signals))

        if self.Time.time() >= self.mm_window.stop:
            cancel_open_tickets(self)  # maybe removing this to stay in limit order book. Review close-open jumps...

        # if user == SEB:
        #     self.simulate_fill()

    def OnOrderEvent(self, order_event: OrderEvent):
        self.order_events.append(order_event)
        log_order_event(self, order_event)
        # if order_event.Status in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid):
        for t in list(chain(*self.order_tickets.values())):
            # for t in self.order_tickets[order_event.Symbol]:
            if t.Status in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid):
                self.order_tickets[t.Symbol].remove(t)
        publish_event(self, order_event)

    def OnEndOfDay(self, symbol: str):
        self.on_end_of_day()

    def OnEndOfAlgorithm(self):
        self.on_end_of_day()
        profile_performance(self)
        super().OnEndOfAlgorithm()

    #########################################################################################################

    def log_risk_schedule(self):
        if self.IsWarmingUp or not self.IsMarketOpen(self.equities[0]):
            return
        log_risk(self)

    def populate_option_chains(self):
        """Triggered at market open"""
        for symbol in self.option_chains.keys():
            if symbol.SecurityType == SecurityType.Option:
                self.option_chains[symbol] = [c for c in self.Securities.Values if c.Type == SecurityType.Option and c.Underlying.Symbol == symbol.Underlying]
            elif symbol.SecurityType == SecurityType.Equity:
                self.option_chains[symbol] = [c for c in self.Securities.Values if c.Type == SecurityType.Option and c.Underlying.Symbol == symbol]
            else:
                raise ValueError(f'Keys in option chains should either by Equity or Canonical Option. Encountered {symbol.SecurityType}')

    @cache(lambda self: str(self.Time.date()), maxsize=1)
    def on_end_of_day(self):
        """
        Log Positions and corresponding PnL, Risk, etc. for ensued analysis. Time series grid & Group-bys.
        Log Portfolio Risk
        Given a position's risk is calculated with the respect to whole portfolio, their sum should equal PF risk.
        """
        if self.IsWarmingUp:
            return

        positions = [Position.e(self, symbol) for symbol, holding in self.Portfolio.items() if holding.Quantity != 0]
        estimated_position_valuation_gain = sum([abs(p.multiplier * (p.spread / 2) * p.quantity) for p in positions])
        log_risk(self)

        # Write the positions to a .json file
        positions_json = [p.to_json() for p in positions]
        Path(__name__).parent.joinpath(f'positions_{self.Time.date()}.json').write_text(json.dumps(positions_json, indent=4))

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

    def simulate_fill(self):
        if not self.LiveMode:
            for ticket in chain(*self.order_tickets.values()):
                if ticket.Symbol.SecurityType == SecurityType.Option:
                    contract: OptionContract = get_contract(self, ticket.Symbol)
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
        for ticker in self.hedge_ticker:
            # not quite right. If we use multiple ETFs to hedge, would not want to fill with first one in list.
            try:
                pf_risk = PortfolioRisk.e(self)
                # Ideally get an option on SPY matching delta, gamma, vega, theta - unlikely. just hedge delta now...
                if pf_risk.ppi.beta(ticker) > self.min_correlation:  # If correlation is too low, need alternative hedge.
                    quantity = int(-1 / pf_risk.ppi.beta(ticker))
                    self.MarketOrder(ticker, quantity=quantity)
                else:
                    quick_log(self, topic="HEDGE INDEX", msg=f'{ticker} correlation too low. No suitable hedge.')
            except Exception as e:
                quick_log(self, topic="HEDGE INDEX", msg=f'Failed to hedge portfolio with {ticker} due to: {e}')

    def equity_option_from_symbol(self, symbol: Symbol) -> Tuple[Union[Symbol, None], Union[Symbol, None]]:
        """Confusing returning 2 types. Stop working with Security, just use Symbol."""
        security: Union[Equity, OptionContract, Option] = self.Securities.get(symbol)

        if symbol.SecurityType == SecurityType.Option and getattr(security, 'IsOptionContract', False):
            contract: OptionContract = security
            equity: Symbol = getattr(contract, 'UnderlyingSymbol', contract.Underlying.Symbol)
            option = next((o for o in self.options if o.Underlying == equity), None)
        elif symbol.SecurityType == SecurityType.Option and symbol.IsCanonical():
            option: Symbol = symbol
            equity = symbol.Underlying
        elif symbol.SecurityType == SecurityType.Equity:
            equity = symbol
            option = next((o for o in self.options if o.Underlying == equity), None)

            if option is None:
                self.Log(f'Failed to derive Option from symbol {symbol}.')
        else:
            self.Log(f'Failed to derive Security Type of {symbol}.')
            equity = option = None
        return equity, option

    def order_option_contract(
        self,
        contract: OptionContract,
        quantity: float,
        order_type: OrderType = OrderType.Limit
    ):
        """
        Begin of a sort of execution mgmt. Concretely, ignore calls where there is already a corresponding unfilled order ticket.
        """
        if self.mm_window.stop > self.Time.time() < self.mm_window.start:
            quick_log(self, topic='EXECUTION', msg=f'No time to trade...')
            return
        for ticket in self.order_tickets[contract.Symbol]:
            if ticket.Quantity * quantity >= 0:
                quick_log(self, topic='EXECUTION', msg=f'Already have an order ticket for {contract.Symbol} with same sign. Not processing...')
                return

        order_direction = OrderDirection.Buy if quantity > 0 else OrderDirection.Sell
        limit_price = self.price_option_pf_risk_adjusted(contract, PortfolioRisk.e(self), order_direction)

        if limit_price is None:
            quick_log(self, topic='EXECUTION', msg='Failed to derive limit price. Not processing...')
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
            self.order_tickets[contract.Symbol].append(ticket)

    def update_limit_price(self, symbol: Symbol):
        if symbol.SecurityType == SecurityType.Option:
            self.update_contract_limit_price(self.Securities[symbol])
        elif symbol.SecurityType == SecurityType.Equity:
            self.update_equity_limit_prices(self.Securities[symbol])

    def update_contract_limit_price(self, contract: Union[OptionContract, Security]):
        for t in self.order_tickets[contract.Symbol]:
            if t.Status in (OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.UpdateSubmitted):
                tick_size_ = tick_size(self, contract.Symbol)
                limit_price = t.Get(OrderField.LimitPrice)
                order_direction = OrderDirection.Buy if t.Quantity > 0 else OrderDirection.Sell
                ideal_limit_price = price_theoretical = self.price_option_pf_risk_adjusted(contract, PortfolioRisk.e(self), order_direction)
                if price_theoretical is None:
                    self.Log('Failed to derive limit price. Not processing...')

                if t.UpdateRequests and t.UpdateRequests[-1].LimitPrice == ideal_limit_price:
                    continue
                elif ideal_limit_price != limit_price and ideal_limit_price >= tick_size_:
                    if ideal_limit_price < tick_size_:
                        t.Cancel()
                    else:
                        tag = f"Moving limit price from {limit_price} to {ideal_limit_price}"
                        response = t.UpdateLimitPrice(ideal_limit_price, tag)
                        self.Log(f'UpdateLimitPrice response: {response}')

    def update_equity_limit_prices(self, equity: Union[Equity, Security]):
        """
        Should be triggered when equity bid ask changes
        """
        for t in self.order_tickets[equity.Symbol]:
            if t.Status in (OrderStatus.Submitted, OrderStatus.PartiallyFilled, OrderStatus.UpdateSubmitted):
                # bad bug. Canceled already, still went through and created market order... could ruin me 
                continue
            elif self.LiveMode and len(t.UpdateRequests) > 1:  # Chasing the market. Risky. Market Order
                t.Cancel()
                self.MarketOrder(t.Symbol, t.Quantity, tag=t.Tag.replace('Limit', 'Market'))
            else:
                tick_size_ = tick_size(self, equity.Symbol)
                limit_price = t.Get(OrderField.LimitPrice)
                if t.Quantity > 0 and (equity.BidSize > abs(t.Quantity) if self.LiveMode else True):  # should not outbid myself...
                    ideal_limit_price = equity.BidPrice + self.equity_price_increments * tick_size_
                elif t.Quantity < 0 and (equity.BidSize > abs(t.Quantity) if self.LiveMode else True):  # should not outbid myself...
                    ideal_limit_price = equity.AskPrice - self.equity_price_increments * tick_size_
                else:
                    continue
                if round(ideal_limit_price, 2) != round(limit_price, 2) and ideal_limit_price > 0:
                    tag = f"Price not good {limit_price}: Changing to ideal limit price: {ideal_limit_price}"
                    t0 = datetime.now()
                    response = t.UpdateLimitPrice(ideal_limit_price, tag)
                    self.Log(f'UpdateLimitPrice response: {response} in {(datetime.now() - t0).total_seconds()}s')

    # @cache(lambda self, option_chain, f_filter, **kw: self.Time.date().isoformat() + option_chain.Symbol.ToString(), f_filter=)  # , maxsize=len(self.option_tickers))
    # def select_option_contracts(self, option_chain: OptionChain, f_filter: List[Callable]) -> List[OptionContract]:
    #     """
    #     Get an options contract that matches the specified criteria:
    #     Underlying symbol, delta, days till expiration, Option right (put or call)
    #     """
    #     # min_expiry_date = self.Time + timedelta(days=expiry_delta_days)
    #     # cache['f_filter'] = reduce(lambda res, f: res + hash(f), f_filter, 0)
    #     return reduce(lambda res, f: f(res), f_filter, option_chain)

    def risk_spread_adjustment(self, spread, pf_risk: PortfolioRisk, pf_delta_if: float) -> float:
        """
        Reduces the spread the more we:
        - need a hedge (large absolute portfolio delta)
        - fitting is the hedge, primarily how much it'd offset the current portfolio delta
        """
        return spread \
               * max(min(0, pf_risk.delta_usd / 500), 1) \
               * 0.5 * (pf_risk.delta - pf_delta_if) / pf_risk.delta

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
        elif order_direction == OrderDirection.Sell:
            price_theoretical = ask_price_theoretical
        else:
            raise ValueError(f'Invalid order direction: {order_direction}')
        spread = contract.AskPrice - contract.AskPrice

        # # Adjust theoretical price for portfolio risk
        pf_delta_if = get_pf_delta_if_filled(pf_risk, ocw, order_direction)
        increases_pf_delta = pf_delta_if * pf_risk.delta > 0
        # spread_adjustment = self.risk_spread_adjustment(spread, pf_risk, pf_delta_if)

        if increases_pf_delta and pf_risk.delta > 0:  # Don't want this trade much
            limit_price = round_tick(price_theoretical, tick_size=ts)  # todo: want much factor tbd
            # limit_price = round_tick(min(price_theoretical, best_bid), tick_size=ts)
        elif increases_pf_delta and pf_risk.delta < 0:  # Want this trade much
            limit_price = round_tick(price_theoretical, tick_size=ts)  # todo: want much factor tbd
            # limit_price = round_tick(max(price_theoretical, best_ask), tick_size=ts)
        else:  # no pf_risk.delta
            # if order_direction == OrderDirection.Buy:
            limit_price = round_tick(price_theoretical, tick_size=ts)
            # limit_price = min(best_bid, round_tick(price_theoretical, tick_size=ts))
            # elif order_direction == OrderDirection.Sell:
            #     limit_price = max(best_ask, round_tick(price_theoretical, tick_size=ts))
            # else:
            #     raise ValueError(f'Invalid order direction: {order_direction}')
        return limit_price


@cache(lambda pf_risk, ocw, order_direction: (pf_risk.delta > 0, order_direction, str(ocw.contract)))
def get_pf_delta_if_filled(pf_risk: PortfolioRisk, ocw: OptionContractWrap, order_direction: OrderDirection) -> float:
    return DIRECTION2NUM[order_direction] * ocw.greeks().delta * pf_risk.ppi.beta(ocw.underlying_symbol)
