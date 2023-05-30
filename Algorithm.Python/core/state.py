from core.stubs import *

from dataclasses import dataclass
from itertools import chain

from core.constants import DIRECTION2NUM
from core.events.signal import Signal
from core.pricing.option_contract_wrap import OptionContractWrap
from core.cache import cache
from core.risk.portfolio import PortfolioRisk
from core.utils import get_contract, round_tick, tick_size
from core.log import log_order, humanize


# if getpass.getuser() == 'seb':
#     from MarketMakeOptionsAlgorithm import MarketMakeOptions as QCAlgorithm
# else:
#     # QC Cloud / Docker Container
#     from main import MarketMakeOptions as QCAlgorithm


@dataclass
class State:
    risk_is: PortfolioRisk = None
    risk_if: PortfolioRisk = None
    risk_is_net: float = None


def is_contract_quantity(algo: QCAlgorithm, equity: Equity, option_right: OptionRight, f_quantity: Callable) -> bool:
    quantity = 0
    for symbol, security_holding in algo.Portfolio.items():
        if security_holding.Type == SecurityType.Option:
            contract: OptionContract = get_contract(algo, symbol)
            if contract.Underlying.Symbol == equity.Symbol and contract.Right == option_right:
                quantity += security_holding.Quantity
            for ticket in algo.order_tickets[contract.Symbol]:
                contract: OptionContract = algo.Securities.get(ticket.Symbol)
                if contract.Right == option_right:
                    quantity += ticket.Quantity
    return f_quantity(quantity)


def equity_position_usd(algo: QCAlgorithm, equity: Equity):
    value = 0
    for symbol, security_holding in algo.Portfolio.items():
        if symbol == equity.Symbol:
            value += security_holding.Quantity * algo.Securities.get(symbol).Price
    return value


def get_state(algo: QCAlgorithm) -> State:
    return State(
        risk_is=PortfolioRisk.e(algo),
        risk_if=None,
    )


def update_ticket_quantity(algo, ticket: OrderTicket, quantity: int):
    if update_requests := ticket.UpdateRequests:
        if update_requests[-1].Quantity == quantity:
            return
    tag = f'Update Ticket: {ticket}. Set Quantity: {ticket.Quantity} from originally: {quantity}'
    response = ticket.UpdateQuantity(quantity, '')
    algo.Log(f'{tag}. Response: {response}')


def update_ticket_price_more_aggressive(algo, ticket: OrderTicket, new_price: float):
    if update_requests := ticket.UpdateRequests:
        if update_requests[-1].LimitPrice == new_price:
            return

    limit_price = round_tick(ticket.Get(OrderField.LimitPrice), tick_size=tick_size(algo, ticket.Symbol))
    if limit_price != new_price:
        tag = f'Update Ticket: {ticket}. Set Price: {new_price} from originally: {limit_price}'
        algo.Log(humanize(ts=algo.Time, topic="HEDGE MORE AGGRESSIVELY", symbol=str(ticket.Symbol), current_price=limit_price, new_price=new_price))
        response = ticket.UpdateLimitPrice(new_price, tag)
        # algo.Log(f'{tag}. Response: {response}')


@cache(lambda algo: algo.Time, maxsize=1)
def hedge_portfolio_risk_is(algo: QCAlgorithm):
    """
    todo: This needs to go through central risk and other trade processing function. Dont circumvent.
    This event should be triggered when
    - the absolute risk changes significantly requiring updates to ticket prices or new hedges.
    - Many small changes will result in a big net delta change, then olso hedge...
    """
    pf_risk = PortfolioRisk.e(algo)
    """Simplifying assumption: There is at most one contract per option contract"""
    # Missing the usual Signal -> Risk check here. Suddenly placing orders for illiquid option.
    for ticket in chain(*algo.order_tickets.values()):
        contract: OptionContract = algo.Securities.get(ticket.Symbol)
        order_direction = OrderDirection.Buy if ticket.Quantity > 0 else OrderDirection.Sell
        new_price = algo.price_option_pf_risk_adjusted(contract, pf_risk, order_direction=order_direction)
        if not new_price:
            algo.Debug(f'Failed to get price for {contract} while hedging portfolio looking to update existing tickets..')
            continue
        update_ticket_price_more_aggressive(algo, ticket, new_price=new_price)

                    
# def hedge_risk_is_equity(algo: QCAlgorithm, risk_is: PortfolioRisk, equity: Equity):
#     """Positive delta: Short Equity and v.v."""
#     relevant_tickets = [t for t in chain(*algo.order_tickets.values) if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending)]
#     for ticket in [t for t in relevant_tickets if np.sign(t.Quantity) == np.sign(risk_is.unhedged())]:
#         algo.Log(f'Cancel ticket {ticket} with Quantity: {ticket.Quantity}')
#         ticket.Cancel()
#
#     q_order = int(risk_is.unhedged()) + open_quantity_equity(algo, equity.Symbol)
#     if np.sign(q_order) != np.sign(risk_is.unhedged()):  # Reduce order size on exisiting limit orders
#         for ticket in relevant_tickets:
#             q_open = ticket.Quantity - ticket.QuantityFilled
#             if q_order == 0 or np.sign(q_open) == np.sign(risk_is.unhedged()):
#                 continue
#             if abs(q_order) >= abs(q_open):
#                 ticket.Cancel()
#                 q_order += q_open
#             else:
#                 update_ticket_quantity(algo, ticket, -q_order)
#                 q_order = 0
#
#     if abs(q_order) > 10:
#         log_risk(algo, get_state(algo, equity))
#         order_equity(algo, equity, -q_order, order_type=OrderType.Limit)
#

# def handle_risk_if(algo: QCAlgorithm, risk_if: Risk, equity: Equity):
#     risk_if.unhedged()
#     pass


def order_equity(algo: QCAlgorithm, equity: Equity, quantity: int, order_type: OrderType = OrderType.Limit):
    algo.Debug(f'Contract to BuySell Equity: {equity.Symbol}')
    tag = log_order(algo, equity, order_type, quantity)
    if order_type == OrderType.Market:
        algo.MarketOrder(equity.Symbol, quantity, tag=tag)
    else:
        ticket = algo.LimitOrder(equity.Symbol, quantity, limitPrice=algo.mid_price(equity.Symbol), tag=tag)
        algo.order_tickets.append(ticket)


def unwind_option_contracts(algo: QCAlgorithm, option: Option, order_type: OrderType = OrderType.Limit):
    """
    Limit Order if active contract. Otherwise, Exercise if long & ITM, otherwise Market Order sell.
    """
    for symbol, security_holding in algo.Portfolio.items():
        if security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
            contract: OptionContract = get_contract(algo, symbol)
            if contract and \
                contract.Underlying.Symbol == option.Underlying.Symbol and not algo.order_tickets[contract.Symbol]:
                quantity = -security_holding.Quantity
                algo.order_option_contract(contract, quantity, order_type=order_type)


def handle_signals(algo: QCAlgorithm, signals: List[Signal]):
    """
    This event should be fired whenever
    - an opportunity exists, ie, available slots in order book levels where I can take risk: This is usually not the case, hence can save performance by check and
    turning into event.
    - assumed risk is within risk limits
    - there is enough margin to increase risk
    - time to trade
    """
    # Now it says, go trade, check for opportunities and time consuming scan begins...
    for signal in signals:
        quantity = DIRECTION2NUM[signal.order_direction]
        algo.order_option_contract(signal.option_contract, quantity, order_type=OrderType.Limit)
    # risk_if = risk_is = PortfolioRisk.e(algo)
    # eq_pos_usd = 0  # Setting to zero hence making contracts on all sides... equity_position_usd(algo, equity)
    # for option in algo.options:
    #     if (risk_is.delta <= 0 and risk_if.delta <= 0 and eq_pos_usd >= 0) or eq_pos_usd > 100:
    #         algo.order_option(option, OptionRight.Call, 1, algo.order_type)
    #         algo.order_option(option, OptionRight.Put, -1, algo.order_type)
    #     if (risk_is.delta >= 0 and risk_if.delta >= 0 and eq_pos_usd <= 0) or eq_pos_usd < -100:
    #         algo.order_option(option, OptionRight.Call, -1, algo.order_type)
    #         algo.order_option(option, OptionRight.Put, 1, algo.order_type)


# def handle_one_sided_if_risk(algo: QCAlgorithm, equity: Equity):
#     # Cancel Options LO one-side if too much equity used to hedge
#     eq_pos_usd = equity_position_usd(algo, equity)
#     for ticket in [t for t in chain(*algo.order_tickets.values()) if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending)]:
#         # Use function to calc risk_IF - essentially canceling pos/neg IF_delta
#         if eq_pos_usd > 100 and delta_if(algo, ticket) > 0:
#             algo.Log(f'Holdings Symbol={equity.Symbol.ToString()}, Position={algo.Portfolio[equity.Symbol].Quantity}, Value={eq_pos_usd}')
#             ticket.Cancel()
#         elif eq_pos_usd < -100 and delta_if(algo, ticket) < 0:
#             algo.Log(f'Holdings Symbol={equity.Symbol.ToString()}, Position={algo.Portfolio[equity.Symbol].Quantity}, Value={eq_pos_usd}')
#             ticket.Cancel()

@cache(lambda pf_risk, ocw, order_direction: (pf_risk.delta > 0, order_direction, str(ocw.contract)))
def get_pf_delta_if_filled(pf_risk: PortfolioRisk, ocw: OptionContractWrap, order_direction: OrderDirection) -> float:
    return DIRECTION2NUM[order_direction] * ocw.greeks().delta * pf_risk.ppi.beta(ocw.underlying_symbol)


def cancel_risk_increasing_order_tickets(algo: QCAlgorithm):
    """Should be triggered by events such as:
    - a change in the portfolio risk's delta direction:
        - On any fill.
        - On any underlying's mid-price change
    """
    pf_risk = PortfolioRisk.e(algo)
    # Cancel limit order that would increase risk upon fill - to be refined.
    for ticket in [t for t in chain(*algo.order_tickets.values()) if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending)]:
        ocw = OptionContractWrap(algo, get_contract(algo, ticket.Symbol))
        order_direction = OrderDirection.Buy if ticket.Quantity > 0 else OrderDirection.Sell
        pf_delta_if = get_pf_delta_if_filled(pf_risk, ocw, order_direction)
        if pf_delta_if * pf_risk.delta > 0:  # Don't want this trade much
            ticket.Cancel()
