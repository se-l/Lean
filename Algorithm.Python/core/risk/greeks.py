import QuantLib as ql

from AlgorithmImports import *
from dataclasses import dataclass


def hist_vol(prices: pd.Series, span=30, annualized=True, ddof=1):
    std = np.log(prices / prices.shift(1))[-span:].std(ddof=ddof)
    return std * np.sqrt(252) if annualized else std


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


def engined_option(option, bsm_process, steps=200):
    binomial_engine = ql.BinomialVanillaEngine(bsm_process, "crr", steps)
    option.setPricingEngine(binomial_engine)
    return option


def price(option):
    return option.NPV()


def finite_difference_approx(quote, option, d_pct=0.01, derive='NPV', method='central', d1perturbance=None):
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


def finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='NPV', n_days=1, method='forward'):
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


@dataclass
class GreeksMy:
    delta: float = None  # dP ; sensitivity to underlying price
    gamma: float = None  # dP2
    delta_decay: float = None  # dPdT
    dPdIV: float = None  # dPdIV
    dGdP: float = None  # dP3
    gamma_decay: float = None  # dP2dT
    dGdIV: float = None  # dP2dIV
    theta: float = None  # dT ; sensitivity to time
    dTdP: float = None  # dTdP
    theta_decay: float = None  # dT2
    dTdIV: float = None  # dTdIV
    vega: float = None  # dIV ; sensitivity to volatility
    dIVdP: float = None  # dIVdP ; vanna
    vega_decay: float = None  # dIVdT
    dIV2: float = None  # dIV2 ; vomma
    rho: float = 0  # dR ; sensitivity to interest rate

    def to_df(self) -> pd.DataFrame:
        """Matrix and dataclass for greeks. So description and type is in one place. easier to consume."""
        g = self
        return pd.DataFrame([
            [g.delta, g.gamma, g.delta_decay, g.dPdIV],
            [g.gamma, g.dGdP, g.gamma_decay, g.dGdIV],
            [g.theta, g.dTdP, g.theta_decay, g.dTdIV],
            [g.vega, g.dIVdP, g.vega_decay, g.dIV2]],
            columns=['Greek', 'dP', 'dT', 'dIV'],
            index=['Delta', 'Gamma', 'Theta', 'Vega']
        )

    def pl_explain(self, dP=1, dT=0, dIV=0, dR=0):
        dct = {
            'delta': self.delta * dP,
            'gamma': 0.5 * self.gamma * dP**2,
            'delta_decay': self.delta_decay * dT * dP,
            'dPdIV': self.dPdIV * dP * dIV,
            'dGdP': 0.5 * self.dGdP * dP**3,
            'gamma_decay': 0.5 * self.gamma_decay * dP**2 * dT,
            'dGdIV': 0.5 * self.dGdIV * dP**2 * dIV,
            'theta': self.theta * dT,
            'dTdP': self.dTdP * dT * dP,
            'theta_decay': 0.5 * self.theta_decay * dT**2,
            'dTdIV': self.dTdIV * dT * dIV,
            'vega': self.vega * dIV,
            'dIVdP': self.dIVdP * dIV * dP,
            'vega_decay': self.vega_decay * dIV * dT,
            'dIV2': 0.5 * self.dIV2 * dIV**2,
            'rho': self.rho * dR,
        }
        dct['total'] = sum(dct.values())
        return dct


def get_my_greeks(algo: QCAlgorithm, contract: OptionContract) -> GreeksMy:
    # PL breakdown of option positions
    maturity_date = ql.Date(contract.Expiry.day, contract.Expiry.month, contract.Expiry.year)
    strike_price = contract.Strike
    dividend_rate_quote = ql.SimpleQuote(0.0)  # 0.0163
    option_type = ql.Option.Call if contract.Right == OptionRight.Call else ql.Option.Put
    spot_price = algo.Securities[contract.UnderlyingSymbol].Price
    hv = algo.Securities[contract.UnderlyingSymbol].VolatilityModel.Volatility

    day_count = ql.Actual365Fixed()
    calendar = ql.UnitedStates(ql.UnitedStates.NYSE)
    calculation_date = ql.Date(algo.Time.day, algo.Time.month, algo.Time.year)
    # ql.Settings.instance().evaluationDate =
    settlement = calculation_date
    payoff = ql.PlainVanillaPayoff(option_type, strike_price)

    am_exercise = ql.AmericanExercise(settlement, maturity_date)
    am_option = ql.VanillaOption(payoff, am_exercise)
    spot_quote = ql.SimpleQuote(spot_price)
    hv_quote = ql.SimpleQuote(hv)
    rf_quote = ql.SimpleQuote(0.0466)  # 0.001

    flat_ts = ql.YieldTermStructureHandle(
        ql.FlatForward(calculation_date, ql.QuoteHandle(rf_quote), day_count)
    )
    dividend_yield = ql.YieldTermStructureHandle(
        ql.FlatForward(calculation_date, ql.QuoteHandle(dividend_rate_quote), day_count)
    )
    flat_vol_ts = ql.BlackVolTermStructureHandle(
        ql.BlackConstantVol(calculation_date, calendar, ql.QuoteHandle(hv_quote), day_count)
    )
    bsm_process = ql.BlackScholesMertonProcess(ql.QuoteHandle(spot_quote), dividend_yield, flat_ts, flat_vol_ts)

    am_option = engined_option(am_option, get_bsm(calculation_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count))
    print(price(am_option))

    # First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
    delta = am_option.delta()
    # delta = finite_difference_approx(spot_quote, am_option, 0.01, 'NPV') ; print(delta) ; print(am_option.delta())
    # theta = am_option.theta()
    # theta = finite_difference_approx(calculation_date, am_option, 1, 'NPV') ; print(theta) ; print(am_option.theta())
    theta = finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='NPV', n_days=1, method='forward')
    vega = finite_difference_approx(hv_quote, am_option, 0.01)

    # Second order derivatives using finite difference
    # dP - a) d2V / dP2 (Gamma) b) d2V / dTdP c) d2V / dIVdP
    dPdP = gamma = am_option.gamma()
    # gamma = finite_difference_approx(spot_quote, am_option, 0.01, 'delta') ; print(gamma) ; print(am_option.gamma())
    dTdP = finite_difference_approx(spot_quote, am_option, 0.01, 'theta')
    dIVdP = finite_difference_approx(spot_quote, am_option, 0.01, 'vega', d1perturbance=hv_quote)
    dGdP = finite_difference_approx(spot_quote, am_option, 0.01, 'gamma')

    # dIV: dV2 / dIVdT (Vega changes towards maturity) ; d2V / dIV2 (Vanna) ; d2V / dIVdP (Vega changes with Delta)
    #  d2V / dPdIV (Delta changes with IV / Color)
    dPdIV = finite_difference_approx(hv_quote, am_option, 0.01, 'delta', d1perturbance=hv_quote)
    dTdIV = finite_difference_approx(hv_quote, am_option, 0.01, 'theta')
    dIV2 = finite_difference_approx(hv_quote, am_option, 0.01, 'vega', d1perturbance=hv_quote)
    dGdIV = finite_difference_approx(hv_quote, am_option, 0.01, 'gamma')

    # dt: dV2 / dPdT (Delta decay / Charm); theta decay ; vega decay
    delta_decay = finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='delta', n_days=1, method='forward')
    theta_decay = finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='theta', n_days=1, method='forward')
    vega_decay = finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='vega', n_days=1, method='forward')
    gamma_decay = finite_difference_approx_time(calculation_date, option_type, strike_price, maturity_date, spot_quote, hv_quote, rf_quote, dividend_rate_quote, calendar, day_count, derive='gamma', n_days=1, method='forward')

    g = GreeksMy(delta, gamma, delta_decay, dPdIV, dGdP, gamma_decay, dGdIV, theta, dTdP, theta_decay, dTdIV, vega, dIVdP, vega_decay, dIV2)
    # g.to_df()
    return g
