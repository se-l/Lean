from dataclasses import dataclass
from core.stubs import *


@dataclass
class EventNewBidAsk:
    """
    Event that is fired when a new bid or ask is received.
    """
    symbol: Symbol


def is_event_new_bid_ask(algo, data: Slice, symbol: Symbol) -> bool:
    """Make these faster..."""
    qb_0: QuoteBar = algo.slices[0].QuoteBars[symbol] if algo.slices[0].QuoteBars.get(symbol) else None
    qb_1: QuoteBar = data.QuoteBars[symbol] if data.QuoteBars.get(symbol) else None
    return getattr(qb_0.Bid, 'Close', 0) != getattr(qb_1.Bid, 'Close', 0) or getattr(qb_0.Ask, 'Close', 0) != getattr(qb_1.Ask, 'Close', 0) if qb_0 and qb_1 else False
