from collections import defaultdict
from dataclasses import dataclass, fields
from core.utils import history_symbol_bar, rolling_pearson_corr, get_contract
from core.pricing.option_contract_wrap import OptionContractWrap
from core.stubs import *


@dataclass
class IndexConstituent:
    """
    Weight corresponds to the total absolute values of the positions and for options, corresponding underlying positions.
    But price changes over time historically. Weighting returns to calculate correlations.
    """
    symbol: Symbol
    weight: float = 0

    def price(self, algo: QCAlgorithm, dt: date = None) -> float:
        if not dt or dt == algo.Time.date():
            return algo.Securities.get(self.symbol).Price
        else:
            trade_bars: List[TradeBar] = list(history_symbol_bar(algo=algo, symbol=self.symbol, start=dt - timedelta(days=5), end=dt + timedelta(days=1)))
            return trade_bars[-1].Close


@dataclass
class PortfolioProxyIndex:
    constituents: List[IndexConstituent] = None  # May simply be SPY or a mix of ETFs in future...
    time: datetime = None
    algo: QCAlgorithm = None
    quantity: int = 1  # May change once we have an ETF here and owning it
    window: int = 30
    # can calc here correlation, correlation_gamma of each constituent for multiple time periods

    def value(self, algo: QCAlgorithm, dt: date = None) -> float:
        return sum([c.weight * c.price(algo, dt) for c in self.constituents])

    # @lru_cache()
    def beta(self, symbol: Symbol, window: int = 30, cache={}) -> float:
        """
        Beta of the portfolio to the equity symbol. Pearson correlation coefficient of returns.
        """
        if cache.get('symbol') == symbol and cache.get('window') == window and cache.get('date') == self.algo.Time.date():
            return cache.get('beta')
        else:
            # history: List[TradeBar] = history_symbol_bar(algo=self.algo, symbol=symbol, window=window, slice=TradeBar)
            history_index = self.history(window=window)
            history: List[TradeBar] = list(history_symbol_bar(algo=self.algo, symbol=symbol, start=history_index.index[0], end=history_index.index[-1], slice=TradeBar))
            coefficients, p = rolling_pearson_corr([tb.Close for tb in history], history_index.values, window=len(history))

            cache['symbol'] = symbol
            cache['window'] = window
            cache['date'] = self.algo.Time.date()
            cache['beta'] = coefficients[-1]
            return coefficients[-1]

    # @lru_cache(maxsize=1)
    def history(self, window=None) -> pd.Series:
        date_value = []
        for dt in pd.date_range(start=self.time - timedelta(days=window or self.window), end=self.time - timedelta(days=1)):
            dt = dt.date()
            if dt.weekday() < 5:
                date_value.append((dt, self.value(algo=self.algo, dt=dt)))
        return pd.DataFrame(date_value).set_index(0)[1]

    @classmethod
    def e(cls, algo: QCAlgorithm, order_tickets: List[OrderTicket] = None):
        """
        Constructs an Index from portfolio's positions. Underlying with small positions will be less well presented and the accuracy of the index greeks will diminish for those.
        """
        constituent_weight = defaultdict(float)

        for symbol, security_holding in algo.Portfolio.items():
            if security_holding.Type == SecurityType.Equity:
                constituent_weight[symbol] += security_holding.Quantity
            elif security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
                contract: OptionContract = get_contract(algo, symbol)
                if contract:
                    constituent_weight[contract.UnderlyingSymbol] += security_holding.Quantity * 100

        if order_tickets:  # Scenario Risk if filled, additive is too simple. Ignore for now.
            pass
            # for ticket in [t for t in algo.tickets_equity]:
            # for ticket in [t for t in algo.tickets_option_contracts

        return cls(constituents=[IndexConstituent(symbol, weight) for symbol, weight in constituent_weight.items()], time=algo.Time, algo=algo)


@dataclass
class PortfolioRisk:
    time: datetime = None
    delta: float = 0
    delta_usd: float = 0
    gamma: float = 0
    gamma_usd: float = 0
    theta: float = 0
    vega: float = 0
    rho: float = 0
    ppi: PortfolioProxyIndex = None

    def to_dict(self) -> Dict[str, Any]:
        return {k: getattr(self, k.name) for k in fields(self) if k.name != 'ppi'}

    @classmethod
    def e(cls, algo: QCAlgorithm, order_tickets: List[OrderTicket] = None):
        """
        Constructs an Index from portfolio's positions. Calculates portfolio greeks.
        """
        ppi = PortfolioProxyIndex.e(algo, order_tickets)
        ppi_value = ppi.value(algo=algo)

        delta = 0
        delta_usd = 0
        gamma = 0
        gamma_usd = 0
        theta = 0
        vega = 0

        for symbol, security_holding in algo.Portfolio.items():
            if security_holding.Type == SecurityType.Equity and security_holding.Quantity != 0:
                beta_i = ppi.beta(symbol)
                price = security_holding.Price
                # value_equity += security_holding.Quantity * price
                delta += security_holding.Quantity * beta_i
                # gamma += security_holding.Quantity * beta_i
            elif security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
                contract: OptionContract = get_contract(algo, symbol)
                if contract:
                    ocw = OptionContractWrap(algo, contract)
                    greeks = ocw.greeks()
                    quantity = security_holding.Quantity
                    price_underlying = algo.Securities.get(contract.UnderlyingSymbol).Price
                    beta_i = ppi.beta(contract.UnderlyingSymbol)

                    delta_contract = greeks.delta * beta_i * quantity * 100 / (ppi_value * ppi.quantity)
                    delta += delta_contract
                    delta_usd += delta_contract * price_underlying / (ppi_value * ppi.quantity)
                    gamma_contract = beta_i**2 * (quantity * 100)**2 * greeks.gamma / (ppi_value * ppi.quantity)**2
                    gamma += gamma_contract
                    gamma_usd += gamma_contract * price_underlying**2 / (ppi_value * ppi.quantity)**2
                    theta += greeks.theta
                    vega += greeks.vega  # Assuming perfect volatility correlation

        if order_tickets:
            pass
            # for ticket in [t for t in algo.tickets_equity]:
            # for ticket in [t for t in algo.tickets_option_contracts if

        return cls(
            time=algo.Time,
            delta=delta,
            delta_usd=delta_usd,
            gamma=gamma,
            gamma_usd=gamma_usd,
            theta=theta,
            vega=vega,
            ppi=ppi,
        )
