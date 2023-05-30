import QuantLib as ql

from core.cache import cache
from core.constants import STEPS, RISK_FREE_RATE
from core.pricing.greeks_plus import GreeksPlus
from core.stubs import *
from core.utils import handle_error, get_contract

day_count = ql.Actual365Fixed()
calendar = ql.UnitedStates(ql.UnitedStates.NYSE)
SPOT_PRICE = 'spot_price'


class OptionContractWrap:
    """Singleton class for caching contract attributes and calculating Greeks"""
    _instance: Dict[Symbol, Any] = {}
    date = ''

    def __new__(cls, algo: QCAlgorithm, contract: OptionContract, **kwargs):
        contract = get_contract(algo, contract.Symbol)  # Ensure getting the right type (option_chain, not Security)
        if contract.Symbol not in cls._instance or cls.date < algo.Time.date().isoformat():  # caching might be more attributes. On the other hand Contract object constantly changes
            cls._instance[contract.Symbol] = super(OptionContractWrap, cls).__new__(cls)
            cls.date = algo.Time.date().isoformat()
        return cls._instance[contract.Symbol]

    def __init__(self, algo: QCAlgorithm, contract: OptionContract, **kwargs):
        if hasattr(self, 'algo'):
            return
        self.algo = algo
        self.contract = get_contract(algo, contract.Symbol)  # Ensure getting the right type (option_chain, not Security)
        self.underlying_symbol = getattr(self.contract, 'UnderlyingSymbol', None) or self.contract.Underlying.Symbol

        self.calculation_date = ql.Date(algo.Time.day, algo.Time.month, algo.Time.year)
        self.settlement = self.calculation_date
        self.maturity_date = ql.Date(self.contract.Expiry.day, self.contract.Expiry.month, self.contract.Expiry.year)
        self.strike_price = getattr(self.contract, 'StrikePrice', None) or self.contract.Strike
        self.dividend_rate_quote = ql.SimpleQuote(0.0)  # 0.0163
        self.option_type = ql.Option.Call if self.contract.Right == OptionRight.Call else ql.Option.Put
        self.spot_quote = ql.SimpleQuote(kwargs.get(SPOT_PRICE) or algo.Securities[self.underlying_symbol].Price)
        self.rf_quote = ql.SimpleQuote(RISK_FREE_RATE)
        self.hv_quote = ql.SimpleQuote(algo.Securities[self.underlying_symbol].VolatilityModel.Volatility)  # to modified with IV or prediction.

        self.payoff = ql.PlainVanillaPayoff(self.option_type, self.strike_price)
        self.am_exercise = ql.AmericanExercise(self.settlement, self.maturity_date)
        am_option = ql.VanillaOption(self.payoff, self.am_exercise)

        self.bsm_process = get_bsm(self.calculation_date, self.spot_quote, self.hv_quote, self.rf_quote, self.dividend_rate_quote, calendar, day_count)
        self.am_option = engined_option(am_option, self.bsm_process)

    def reset(self):
        self.spot_quote.setValue(self.algo.Securities[self.underlying_symbol].Price)
        self.hv_quote.setValue(self.algo.Securities[self.underlying_symbol].VolatilityModel.Volatility)

    def price(
        self,
        spot_price: Union[float, None] = None,
        calculation_date: date = None,
    ) -> Tuple[Union[float, None], Union[float, None]]:
        # Needs caching logic, once we don't just quote BID/ASK
        algo = self.algo
        _spot_price = spot_price or algo.Securities[self.underlying_symbol].Price
        self.spot_quote.setValue(_spot_price)
        bid_iv, ask_iv = self.iv_bid_ask()

        try:
            if bid_iv is None:
                raise RuntimeError('bid_iv is None')
            self.hv_quote.setValue(bid_iv)
            bid_price = self.am_option.NPV()
        except RuntimeError as e:
            algo.Log(f'Unable to price BID due to invalid IV most likely: {bid_iv}. Setting Bid Price to None. {e}')
            bid_price = None

        try:
            if ask_iv is None:
                raise RuntimeError('ask_iv is None')
            self.hv_quote.setValue(ask_iv)
            ask_price = self.am_option.NPV()
        except RuntimeError as e:
            algo.Log(f'Unable to price ASK due to invalid IV most likely: {ask_iv}. Setting Ask Price to None. {e}')
            ask_price = None

        return bid_price, ask_price

    @handle_error()
    def iv(self, spot_price_contract=None) -> Union[float, None]:
        _spot_price_contract = spot_price_contract or (self.contract.BidPrice + (self.contract.AskPrice - self.contract.BidPrice) / 2)
        return self.am_option.impliedVolatility(_spot_price_contract, self.bsm_process)

    def iv_bid_ask(self, bid_price: float = None, ask_price: float = None) -> Tuple[Union[float, None], Union[float, None]]:
        try:
            bid_iv = max(self.am_option.impliedVolatility(bid_price or self.contract.BidPrice, self.bsm_process), 0.01)
        except RuntimeError as e:
            self.algo.Debug(f'bid iv error. Bid price is too low to derive IV: {e} - setting IV Bid to 0.001')
            bid_iv = None
        try:
            ask_iv = max(self.am_option.impliedVolatility(ask_price or self.contract.AskPrice, self.bsm_process), 0.01)
        except RuntimeError as e:
            self.algo.Debug(f'ask iv error. Ask price is too high to derive IV: {e} - setting IV Ask to None')
            ask_iv = None
        return bid_iv, ask_iv

    @cache(lambda self, **kw: str(self.contract.Symbol) + str(self.algo.Time.date()) + str(kw))
    def greeks(self, spot_price: float = None, hv=None) -> GreeksPlus:
        algo = self.algo

        self.spot_quote.setValue(spot_price or algo.Securities[self.underlying_symbol].Price)
        if hv:
            self.hv_quote.setValue(hv)

        # First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
        delta = self.am_option.delta()
        # delta = finite_difference_approx(spot_quote, am_option, 0.01, 'NPV') ; print(delta) ; print(am_option.delta())
        # theta = am_option.theta()
        # theta = finite_difference_approx(calculation_date, am_option, 1, 'NPV') ; print(theta) ; print(am_option.theta())
        theta = finite_difference_approx_time(self.calculation_date, self.option_type, self.strike_price, self.maturity_date, self.spot_quote, self.hv_quote, self.rf_quote,
                                              self.dividend_rate_quote, calendar, day_count, derive='NPV', n_days=1, method='forward')
        vega = finite_difference_approx(self.hv_quote, self.am_option, 0.01)

        # Second order derivatives using finite difference
        # dP - a) d2V / dP2 (Gamma) b) d2V / dTdP c) d2V / dIVdP
        dPdP = gamma = self.am_option.gamma()
        # gamma = finite_difference_approx(self.spot_quote, self.am_option, 0.01, 'delta') ; print(gamma) ; print(self.am_option.gamma())
        dTdP = finite_difference_approx(self.spot_quote, self.am_option, 0.01, 'theta')
        dIVdP = finite_difference_approx(self.spot_quote, self.am_option, 0.01, 'vega', d1perturbance=self.hv_quote)
        dGdP = finite_difference_approx(self.spot_quote, self.am_option, 0.01, 'gamma')

        # dIV: dV2 / dIVdT (Vega changes towards maturity) ; d2V / dIV2 (Vanna) ; d2V / dIVdP (Vega changes with Delta)
        #  d2V / dPdIV (Delta changes with IV / Color)
        dPdIV = finite_difference_approx(self.hv_quote, self.am_option, 0.01, 'delta', d1perturbance=self.hv_quote)
        dTdIV = finite_difference_approx(self.hv_quote, self.am_option, 0.01, 'theta')
        dIV2 = finite_difference_approx(self.hv_quote, self.am_option, 0.01, 'vega', d1perturbance=self.hv_quote)
        dGdIV = finite_difference_approx(self.hv_quote, self.am_option, 0.01, 'gamma')

        # dt: dV2 / dPdT (Delta decay / Charm); theta decay ; vega decay
        delta_decay = finite_difference_approx_time(self.calculation_date, self.option_type, self.strike_price, self.maturity_date, self.spot_quote, self.hv_quote, self.rf_quote,
                                                    self.dividend_rate_quote, calendar, day_count, derive='delta', n_days=1, method='forward')
        theta_decay = finite_difference_approx_time(self.calculation_date, self.option_type, self.strike_price, self.maturity_date, self.spot_quote, self.hv_quote, self.rf_quote,
                                                    self.dividend_rate_quote, calendar, day_count, derive='theta', n_days=1, method='forward')
        vega_decay = finite_difference_approx_time(self.calculation_date, self.option_type, self.strike_price, self.maturity_date, self.spot_quote, self.hv_quote, self.rf_quote,
                                                   self.dividend_rate_quote, calendar, day_count, derive='vega', n_days=1, method='forward')
        gamma_decay = finite_difference_approx_time(self.calculation_date, self.option_type, self.strike_price, self.maturity_date, self.spot_quote, self.hv_quote, self.rf_quote,
                                                    self.dividend_rate_quote, calendar, day_count, derive='gamma', n_days=1, method='forward')
        return GreeksPlus(delta, gamma, delta_decay, dPdIV, dGdP, gamma_decay, dGdIV, theta, dTdP, theta_decay, dTdIV, vega, dIVdP, vega_decay, dIV2)


def get_bsm(calculation_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count):
    flat_ts = ql.YieldTermStructureHandle(
        ql.FlatForward(calculation_date, ql.QuoteHandle(rf_quote), day_count)
    )
    dividend_yield = ql.YieldTermStructureHandle(
        ql.FlatForward(calculation_date, ql.QuoteHandle(dividend_rate_quote), day_count)
    )
    flat_vol_ts = ql.BlackVolTermStructureHandle(
        ql.BlackConstantVol(calculation_date, calendar, ql.QuoteHandle(hv_quote), day_count)
    )
    return ql.BlackScholesMertonProcess(ql.QuoteHandle(spot_quote), dividend_yield, flat_ts, flat_vol_ts)


def engined_option(option, bsm_process, steps=STEPS):
    binomial_engine = ql.BinomialVanillaEngine(bsm_process, "crr", steps)
    option.setPricingEngine(binomial_engine)
    return option


def finite_difference_approx(quote, option, d_pct=0.01, derive='NPV', method='central', d1perturbance=None):
    # Called 40k times in 10mins. reduce it!
    h0 = quote.value()
    quote.setValue(h0 * (1 + d_pct))
    # if hasattr(option, derive):
    if derive in ['vega']:
        p_plus = finite_difference_approx(d1perturbance, option, derive='NPV')  # VEGA
    else:
        p_plus = option.__getattribute__(derive)()
    quote.setValue(h0 * (1 - d_pct))
    # if hasattr(option, derive):
    if derive in ['vega']:
        p_minus = finite_difference_approx(d1perturbance, option, derive='NPV')  # VEGA
    else:
        p_minus = option.__getattribute__(derive)()
    quote.setValue(h0)
    return (p_plus - p_minus) / (2 * h0 * d_pct)


def finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote,
                                  rf_quote, dividend_rate_quote, calendar, day_count, derive='NPV', n_days=1, method='forward'):
    values = []
    for dt in [calculation_date, calculation_date + n_days]:
        payoff = ql.PlainVanillaPayoff(option_type, strike_price)
        am_exercise = ql.AmericanExercise(dt, maturity_date)
        option = ql.VanillaOption(payoff, am_exercise)
        bsm_process = get_bsm(dt, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count)
        engined_option(option, bsm_process)
        # if hasattr(option, derive):
        if derive in ['vega']:
            values.append(finite_difference_approx(hv_quote, option, 0.01))  # VEGA
        else:
            values.append(option.__getattribute__(derive)())
    return (values[0] - values[-1]) / n_days
