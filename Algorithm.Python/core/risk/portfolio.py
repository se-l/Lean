from collections import defaultdict
from dataclasses import dataclass, fields
from functools import reduce

from core.cache import cache
from core.constants import DIRECTION2NUM
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
    constituents: List[IndexConstituent] = None  # Whatever the portfolio is holding, for options its underlying
    time: datetime = None
    algo: QCAlgorithm = None
    quantity: int = 1
    window: int = 30
    # can calc here correlation, correlation_gamma of each constituent for multiple time periods

    def value(self, algo: QCAlgorithm, dt: date = None) -> float:
        return sum([c.weight * c.price(algo, dt) for c in self.constituents])

    @cache(lambda self, symbol, window, **kw: str(symbol) + str(window) + str(self.algo.Time.date()))
    def beta(self, symbol: Symbol, window: int = 30) -> float:
        """
        Beta of the portfolio to the equity symbol. Pearson correlation coefficient of returns.
        """
        history_index = self.history(window=window)
        if history_index.values.sum() == 0:
            return 1  # No history or proxy index valuations because it has no constituents as of now, so no beta.
        history: List[TradeBar] = list(history_symbol_bar(algo=self.algo, symbol=symbol, start=history_index.index[0], end=history_index.index[-1], bar=TradeBar))
        coefficients, p = rolling_pearson_corr([tb.Close for tb in history], history_index.values, window=len(history))
        return coefficients[-1]

    @cache(lambda self, window, **kw: str(window) + str(self.algo.Time.date()))
    def history(self, window=None) -> pd.Series:
        date_value = []
        # Using Market Symbol to avoid requesting data for holidays / closed market. Not using any SPY values here at all.
        history: List[TradeBar] = list(history_symbol_bar(algo=self.algo, symbol='SPY', window=window, bar=TradeBar))
        for dt in [h.EndTime.date() for h in history]:
            date_value.append((dt, self.value(algo=self.algo, dt=dt)))
        return pd.DataFrame(date_value).set_index(0)[1]

    @classmethod
    def e(cls, algo: QCAlgorithm, order_tickets: List[OrderTicket] = None, cache={}):
        """
        Constructs an Index from portfolio's positions. Underlying with small positions will be less well presented and the accuracy of the index greeks will diminish for those.
        """
        if cache.get('ts') == algo.Time or \
            cache.get('symbol_quantity') == reduce(lambda res, sym_holding: res + str(sym_holding[0]) + str(sym_holding[1].Quantity), sorted(algo.Portfolio.items()), ''):
            # if cache.get('ts') < algo.Time - timedelta(minutes=60):  # limited lifetime. Better becomes decorator
            #     cache.pop('symbol_quantity')
            return cache.get('res')
        else:
            constituent_weight = defaultdict(float)

            for symbol, security_holding in algo.Portfolio.items():
                if str(symbol) in algo.hedge_ticker:
                    continue
                elif security_holding.Type == SecurityType.Equity:
                    constituent_weight[symbol] += security_holding.Quantity * 1
                elif security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
                    contract: OptionContract = get_contract(algo, symbol)
                    if contract:
                        constituent_weight[contract.Underlying.Symbol] += security_holding.Quantity * 100

            if order_tickets:  # Scenario Risk if filled, additive is too simple. Ignore for now.
                pass

            cache['symbol_quantity'] = reduce(lambda res, sym_holding: res + str(sym_holding[0]) + str(sym_holding[1].Quantity), sorted(algo.Portfolio.items()), '')
            cache['res'] = cls(constituents=[IndexConstituent(symbol, weight) for symbol, weight in constituent_weight.items()], time=algo.Time, algo=algo)
            cache['ts'] = algo.Time
            return cache['res']


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
        return {k.name: getattr(self, k.name) for k in fields(self) if k.name != 'ppi'}

    @classmethod
    @cache(lambda algo, **kw: str(algo.Time) + (algo.order_events[-1].UtcTime.isoformat() if algo.order_events else ''), maxsize=1)
    def e(cls, algo: QCAlgorithm, order_tickets: List[OrderTicket] = None):
        """
        Constructs an Index from portfolio's positions. Calculates portfolio greeks.
        """
        ppi: PortfolioProxyIndex = PortfolioProxyIndex.e(algo, order_tickets)
        ppi_value = ppi.value(algo=algo) * ppi.quantity or 1

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
                delta_security = 1 * beta_i * security_holding.Quantity * 1
                delta += delta_security
                delta_usd += delta_security * 1 * price / ppi_value
                # gamma += security_holding.Quantity * beta_i
            elif security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
                contract: OptionContract = get_contract(algo, symbol)
                if contract:
                    ocw = OptionContractWrap(algo, contract)
                    greeks = ocw.greeks()
                    quantity = security_holding.Quantity
                    price_underlying = contract.Underlying.Price
                    beta_i = ppi.beta(contract.Underlying.Symbol)

                    delta_contract = greeks.delta * beta_i * quantity * 100
                    delta += delta_contract
                    delta_usd += delta_contract * 100 * price_underlying / ppi_value
                    gamma_contract = beta_i**2 * (quantity * 100)**2 * greeks.gamma / ppi_value**2
                    gamma += gamma_contract
                    gamma_usd += gamma_contract * price_underlying**2 / ppi_value**2
                    theta += greeks.theta
                    vega += greeks.vega  # Assuming perfect volatility correlation

        if order_tickets:
            pass

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
