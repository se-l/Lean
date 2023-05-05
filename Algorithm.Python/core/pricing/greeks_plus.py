import pandas as pd
from dataclasses import dataclass, fields


@dataclass
class PLExplain:
    delta: float
    gamma: float
    delta_decay: float
    dPdIV: float
    dGdP: float
    gamma_decay: float
    dGdIV: float
    theta: float
    dTdP: float
    theta_decay: float
    dTdIV: float
    vega: float
    dIVdP: float
    vega_decay: float
    dIV2: float
    rho: float
    total: float = 0

    def __post_init__(self):
        self.total = sum([getattr(self, f.name) for f in fields(self) if f.name != 'total'])


@dataclass
class GreeksPlus:
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
        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
        return PLExplain(
            delta=self.delta * dP,
            gamma=0.5 * self.gamma * dP ** 2,
            delta_decay=self.delta_decay * dT * dP,
            dPdIV=self.dPdIV * dP * dIV,
            dGdP=0.5 * self.dGdP * dP ** 3,
            gamma_decay=0.5 * self.gamma_decay * dP ** 2 * dT,
            dGdIV=0.5 * self.dGdIV * dP ** 2 * dIV,
            theta=self.theta * dT,
            dTdP=self.dTdP * dT * dP,
            theta_decay=0.5 * self.theta_decay * dT ** 2,
            dTdIV=self.dTdIV * dT * dIV,
            vega=self.vega * dIV,
            dIVdP=self.dIVdP * dIV * dP,
            vega_decay=self.vega_decay * dIV * dT,
            dIV2=0.5 * self.dIV2 * dIV ** 2,
            rho=self.rho * dR,
        )
