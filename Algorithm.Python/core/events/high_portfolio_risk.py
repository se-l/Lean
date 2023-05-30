from dataclasses import dataclass

from core.risk.portfolio import PortfolioRisk


@dataclass
class EventHighPortfolioRisk:
    pf_risk: PortfolioRisk = None


def is_high_portfolio_risk(algo) -> bool:
    return (abs(algo.pf_risks[-1].delta_usd - algo.pf_risks[0].delta_usd) / 2) > 50 or abs(algo.pf_risks[-1].delta_usd) > 100
