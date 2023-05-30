from core.cache import cache
from core.stubs import *
from dataclasses import dataclass

from core.utils import DCBase, positions_n, positions_total


@dataclass
class RiskLimit(DCBase):
    positions_n: int = 40
    positions_total: float = 50_000


@cache(lambda algo: str(algo.Time))
def breached_position_limit(algo: QCAlgorithm) -> bool:
    return positions_n(algo) >= algo.risk_limit.positions_n or positions_total(algo) >= algo.risk_limit.positions_total


# self.risk_is_lmt = RiskLmt(100_000, 1_000)
# self.fct_is_if = 5
# self.risk_if_lmt = RiskLmt(self.risk_is_lmt.position * self.fct_is_if, self.risk_is_lmt.unhedged * self.fct_is_if)
