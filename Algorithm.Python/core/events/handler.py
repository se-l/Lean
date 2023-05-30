from core.stubs import *
from core.events.new_bid_ask import EventNewBidAsk
from core.events.high_portfolio_risk import EventHighPortfolioRisk
from core.events.signal import EventSignals
from core.log import log_risk
# from core.state import cancel_risk_increasing_order_tickets, hedge_portfolio_risk_is, handle_signals


def publish_event(algo, event, condition=None):
    """
    Publishes an event to the event queue.
    condition: later used to register functions to event handlers
    """
    if isinstance(event, EventNewBidAsk) and event.symbol.SecurityType == SecurityType.Option:
        algo.update_limit_price(event.symbol)

    if isinstance(event, EventNewBidAsk) and event.symbol.SecurityType == SecurityType.Equity:
        from core.state import cancel_risk_increasing_order_tickets, hedge_portfolio_risk_is, handle_signals
        cancel_risk_increasing_order_tickets(algo)
        algo.update_limit_price(event.symbol)

    if isinstance(event, OrderEvent) and event.Status in (OrderStatus.Filled, OrderStatus.PartiallyFilled):
        from core.state import cancel_risk_increasing_order_tickets, hedge_portfolio_risk_is, handle_signals
        cancel_risk_increasing_order_tickets(algo)
        hedge_portfolio_risk_is(algo)
        log_risk(algo)

    if isinstance(event, EventHighPortfolioRisk):
        from core.state import cancel_risk_increasing_order_tickets, hedge_portfolio_risk_is, handle_signals
        hedge_portfolio_risk_is(algo)

    if isinstance(event, EventSignals):
        from core.state import cancel_risk_increasing_order_tickets, hedge_portfolio_risk_is, handle_signals
        handle_signals(algo, event.signals)
