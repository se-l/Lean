import getpass
from dataclasses import dataclass

from core.pricing.option_contract_wrap import OptionContractWrap
from core.stubs import *
from core.cache import once_per_algo_time
from core.risk.portfolio import PortfolioRisk
from core.utils import get_contract
from core.log import log_order, log_risk

if getpass.getuser() == 'seb':
    from MarketMakeOptionsAlgorithm import MarketMakeOptions as Strategy
else:
    # QC Cloud / Docker Container
    from main import MarketMakeOptions as Strategy


@dataclass
class State:
    risk_is: PortfolioRisk = None
    risk_if: PortfolioRisk = None
    risk_is_net: float = None


def get_risk(algo: Strategy, equity: Equity, filled_only=False) -> float:
    """"
    Simple risk = value(asset) * haircut=1.0 have's -> 0 liabilities -> inf
    Hedged symbol, if quantity_option * delta ~= quantity_stock ±10%
    """
    value_equity = 0
    value_options = 0
    for symbol, security_holding in algo.Portfolio.items():
        if security_holding.Type == SecurityType.Equity and symbol == equity.Symbol:
            price = algo.Securities.get(symbol).Price
            value_equity += security_holding.Quantity * price
        elif security_holding.Type == SecurityType.Option and \
                algo.Securities.get(symbol).Underlying.Symbol == equity.Symbol and \
                security_holding.Quantity != 0 and algo.option_chains.get(equity):
            contract: OptionContract = get_contract(algo, symbol)
            if contract:
                factor_side = -1 if contract.Right == OptionRight.Call else 1
                value_options += 100 * factor_side * security_holding.Quantity * OptionContractWrap(algo, contract).greeks().delta
    if not filled_only:
        for ticket in [t for t in algo.tickets_equity if t.Symbol == equity.Symbol]:
            quantity_open = ticket.Quantity - ticket.QuantityFilled
            value_equity += quantity_open
        for ticket in [t for t in algo.tickets_option_contracts if
                       algo.Securities.get(t.Symbol).Underlying.Symbol == equity.Symbol]:
            if contract := algo.option_chains.get(equity).Contracts.get(ticket.Symbol):
                factor_side = -1 if contract.Right == OptionRight.Call else 1
                quantity_open = ticket.Quantity - ticket.QuantityFilled
                value_options += 100 * quantity_open * OptionContractWrap(algo, contract).greeks().delta * factor_side
    return -100 * value_options + value_equity


def risk_filled(algo: Strategy, equity: Equity) -> float:
    return get_risk(algo, equity, filled_only=True)


def is_contract_quantity(algo: Strategy, equity: Equity, option_right: OptionRight, f_quantity: Callable) -> bool:
    quantity = 0
    for symbol, security_holding in algo.Portfolio.items():
        if security_holding.Type == SecurityType.Option:
            contract: OptionContract = get_contract(algo, symbol)
            if contract.UnderlyingSymbol == equity.Symbol and contract.Right == option_right:
                quantity += security_holding.Quantity
            for ticket in [t for t in algo.tickets_option_contracts if t.Symbol == contract.Symbol]:
                contract: OptionContract = algo.Securities.get(ticket.Symbol)
                if contract.Right == option_right:
                    quantity += ticket.Quantity
    return f_quantity(quantity)

    # underlying_symbols = [c.Symbol.Underlying for c in algo.tickets_option_contracts]
    # contracts = algo.get_subscribed_contracts(option.Symbol.Underlying)
    # return option.Symbol.Underlying in underlying_symbols or \
    #        sum([algo.Securities[c.Symbol].Holdings.Quantity for c in contracts]) > 0


def equity_position_usd(algo: Strategy, equity: Equity):
    value = 0
    for symbol, security_holding in algo.Portfolio.items():
        if symbol == equity.Symbol:
            value += security_holding.Quantity * algo.Securities.get(symbol).Price
    return value


def open_quantity_equity(algo: Strategy, symbol: Symbol) -> int:
    q_open = 0
    q_open += sum([t.Quantity - t.QuantityFilled for t in algo.tickets_equity if not t.CancelRequest and t.Symbol == symbol])
    return q_open


def open_quantity(algo: Strategy, symbol: Symbol) -> int:
    q_open = 0
    q_open += sum([t.Quantity - t.QuantityFilled for t in algo.tickets_equity if not t.CancelRequest and t.Symbol == symbol])
    q_open += sum([100 * (t.Quantity - t.QuantityFilled) for t in algo.tickets_option_contracts if not t.CancelRequest])
    return q_open


def get_state(algo: Strategy) -> State:
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


def update_ticket_price(algo, ticket: OrderTicket, new_price: float):
    if update_requests := ticket.UpdateRequests:
        if update_requests[-1].LimitPrice == new_price:
            return
    limit_price = ticket.Get(OrderField.LimitPrice)
    tag = f'Update Ticket: {ticket}. Set Price: {new_price} from originally: {limit_price}'
    response = ticket.UpdateLimitPrice(new_price, tag)
    algo.Log(f'{tag}. Response: {response}')


@once_per_algo_time()
def hedge_portfolio_risk_is(algo: Strategy):
    """
    Shouldn't this be rather event driven???
    New fill, updated prices, many... options, given risk monitoring and hedging will real-time.
    """
    pf_risk = PortfolioRisk.e(algo)
    for equity in algo.equities:
        for contract in algo.option_chains.get(equity, []):
            """Simplifying assumption: There is at most one contract per option contract"""
            ticket: OrderTicket = next((t for t in algo.tickets_option_contracts if t.Symbol == contract.Symbol), None)
            if ticket:
                order_direction = OrderDirection.Buy if ticket.Quantity > 0 else OrderDirection.Sell
                new_price = algo.price_option_pf_risk_adjusted(contract, pf_risk, order_direction=order_direction)
                if not new_price:
                    algo.Debug(f'Failed to get price for {contract} while hedging portfolio looking to update existing tickets..')
                    continue
                update_ticket_price(algo, ticket, new_price=new_price)
            else:
                ocw_g = OptionContractWrap(algo, contract).greeks()
                quantity = -np.sign(round(ocw_g.delta * pf_risk.delta))
                if not np.isnan(quantity) and quantity != 0:
                    algo.order_option_contract(contract, quantity, order_type=OrderType.Limit)

                    
# def hedge_risk_is_equity(algo: Strategy, risk_is: PortfolioRisk, equity: Equity):
#     """Positive delta: Short Equity and v.v."""
#     relevant_tickets = [t for t in algo.tickets_equity if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending)]
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

# def handle_risk_if(algo: Strategy, risk_if: Risk, equity: Equity):
#     risk_if.unhedged()
#     pass


def order_equity(algo: Strategy, equity: Equity, quantity: int, order_type: OrderType = OrderType.Limit):
    algo.Debug(f'Contract to BuySell Equity: {equity.Symbol}')
    tag = log_order(algo, equity, order_type, quantity)
    if order_type == OrderType.Market:
        algo.MarketOrder(equity.Symbol, quantity, tag=tag)
    else:
        ticket = algo.LimitOrder(equity.Symbol, quantity, limitPrice=algo.mid_price(equity.Symbol), tag=tag)
        algo.tickets_equity.append(ticket)


def unwind_option_contracts(algo: Strategy, option: Option, order_type: OrderType = OrderType.Limit):
    """
    Limit Order if active contract. Otherwise, Exercise if long & ITM, otherwise Market Order sell.
    """
    for symbol, security_holding in algo.Portfolio.items():
        if security_holding.Type == SecurityType.Option and security_holding.Quantity != 0:
            contract: OptionContract = get_contract(algo, symbol)
            if contract and \
                contract.UnderlyingSymbol == option.Underlying.Symbol and not \
                    contract_in(algo.tickets_option_contracts, contract):
                quantity = -security_holding.Quantity
                algo.order_option_contract(contract, quantity, order_type=order_type)


def contract_in(tickets: List[OrderTicket], contract: OptionContract) -> bool:
    return contract.Symbol in [t.Symbol for t in tickets]


def find_best_hedge():
    """stock or option"""
    pass


def increase_risk(algo: Strategy, risk_is: PortfolioRisk, risk_if: PortfolioRisk, option, equity):
    """
    free_position = limit_pos_if - position_if
    effective, position, delta -> get target position, given target delta
    """
    eq_pos_usd = 0  # Setting to zero hence making contracts on all sides... equity_position_usd(algo, equity)
    if (risk_is.delta <= 0 and risk_if.delta <= 0 and eq_pos_usd >= 0) or eq_pos_usd > 100:
        algo.order_option(option, OptionRight.Call, 1, algo.order_type)
        algo.order_option(option, OptionRight.Put, -1, algo.order_type)
    elif (risk_is.delta >= 0 and risk_if.delta >= 0 and eq_pos_usd <= 0) or eq_pos_usd < -100:
        algo.order_option(option, OptionRight.Call, -1, algo.order_type)
        algo.order_option(option, OptionRight.Put, 1, algo.order_type)


# def handle_one_sided_if_risk(algo: Strategy, equity: Equity):
#     # Cancel Options LO one-side if too much equity used to hedge
#     eq_pos_usd = equity_position_usd(algo, equity)
#     for ticket in [t for t in algo.tickets_option_contracts if t.Status not in (OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending)]:
#         # Use function to calc risk_IF - essentially canceling pos/neg IF_delta
#         if eq_pos_usd > 100 and delta_if(algo, ticket) > 0:
#             algo.Log(f'Holdings Symbol={equity.Symbol.ToString()}, Position={algo.Portfolio[equity.Symbol].Quantity}, Value={eq_pos_usd}')
#             ticket.Cancel()
#         elif eq_pos_usd < -100 and delta_if(algo, ticket) < 0:
#             algo.Log(f'Holdings Symbol={equity.Symbol.ToString()}, Position={algo.Portfolio[equity.Symbol].Quantity}, Value={eq_pos_usd}')
#             ticket.Cancel()


def action(algo: Strategy, equity: Equity, option: Option):
    if algo.unwind:
        unwind_option_contracts(algo, option)
    else:
        pf_risk = PortfolioRisk.e(algo)

        # Make Market
        if -1_000 < pf_risk.delta_usd < 1_000 and \
                algo.mm_window.stop > algo.Time.time() >= algo.mm_window.start:
            increase_risk(algo, pf_risk, pf_risk, option, equity)

        if pf_risk.delta != 0:
            hedge_portfolio_risk_is(algo)  # Adjusts Option Contracts to reduce Portfolio Risk

        # Handle IF risks - cancel bad market making orders
        # if abs(state.risk_if.unhedged_position_ratio()) > algo.risk_is_unhedged_position_ratio_lmt:
        #    handle_risk_if(algo, risk_if=state.risk_is, equity=equity)
