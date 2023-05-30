from core.stubs import *
from dataclasses import dataclass

from core.cache import cache
from core.constants import DIRECTION2NUM, NUM2DIRECTION
from core.pricing.option_contract_wrap import OptionContractWrap
from core.risk.limit import breached_position_limit
from core.risk.portfolio import PortfolioRisk
from core.utils import is_liquid


@cache(lambda pf_risk, ocw, order_direction: (pf_risk.delta > 0, order_direction, str(ocw.contract)))
def get_pf_delta_if_filled(pf_risk: PortfolioRisk, ocw: OptionContractWrap, order_direction: OrderDirection) -> float:
    return DIRECTION2NUM[order_direction] * ocw.greeks().delta * pf_risk.ppi.beta(ocw.underlying_symbol)


@dataclass
class Signal:
    symbol: Symbol
    order_direction: OrderDirection
    option_contract: OptionContract = None
    pf_risk_reviewed: bool = False


@dataclass
class EventSignals:
    signals: List[Signal]


def get_signals(algo: QCAlgorithm) -> List[Signal]:
    """
    Unaware of portfolio state. May simply report over-/underpriced contracts.
    Signal -> PF Risk limitations -> Execute
    """
    contract = {
        c for c in algo.Securities.Values if
        c.Type == SecurityType.Option
        and not c.Symbol.IsCanonical()
        and c.BidPrice and c.AskPrice
        and is_liquid(algo=algo, contract=c, window=5)
    }
    return [Signal(symbol=c.Symbol, option_contract=c, order_direction=OrderDirection.Buy) for c in contract] + \
           [Signal(symbol=c.Symbol, option_contract=c, order_direction=OrderDirection.Sell) for c in contract]


def filter_signal_by_risk(algo: QCAlgorithm, signals: List[Signal]) -> List[Signal]:
    """Aware of Portfolio state and risk. Filter Signals accordingly."""
    pf_risk = PortfolioRisk.e(algo)
    exclude_non_invested = breached_position_limit(algo)

    signals_out = []
    for signal in signals:
        security = algo.Securities[signal.symbol]
        contract = signal.option_contract

        order_direction_sign = DIRECTION2NUM[signal.order_direction]
        if algo.order_tickets[contract.Symbol]:  # Broker says cant have trades on both sides.
            continue

        if exclude_non_invested and security.Holdings.Quantity * order_direction_sign > 0:
            continue

        ocw = OptionContractWrap(algo, contract)
        pf_delta_if = get_pf_delta_if_filled(pf_risk, ocw, signal.order_direction)
        if pf_delta_if * pf_risk.delta > 0:
            # Increases delta risk
            continue

        signals_out.append(signal)
    return signals_out
