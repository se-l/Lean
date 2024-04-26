from core.risk.portfolio import PortfolioRisk
from core.stubs import *
from core.utils import name


def humanize(**kwargs):  # also JSON now
    return ', '.join(f'{k}={v}' for k, v in kwargs.items())


def quick_log(algo: QCAlgorithm, **kwargs):
    algo.Log(humanize(ts=algo.Time, **kwargs))


def log_order(algo: QCAlgorithm,
              security: Union[Equity, Option],
              order_type: OrderType,
              quantity: float) -> str:
    security: Security = algo.Securities[security.Symbol]
    security_type_nm = name(SecurityType, security.Type)
    order_type_nm = name(OrderType, order_type)
    order_direction = OrderDirection.Buy if quantity > 0 else OrderDirection.Sell
    order_direction_nm = name(OrderDirection, order_direction)
    tag = humanize(ts=algo.Time, topic="ORDER",
                   OrderDirection=order_direction_nm, OrderType=order_type_nm, SecurityType=security_type_nm, Symbol=security.Symbol.ToString(),
                   Quantity=abs(quantity), Price=security.Price, BestBidPrice=security.BidPrice, BestAskPrice=security.AskPrice)
    algo.Log(tag)
    return tag


def log_contract(algo: QCAlgorithm, contract: OptionContract, order_direction: OrderDirection = None, limit_price=None, order_type: OrderType = None):
    best_bid = contract.BidPrice
    best_ask = contract.AskPrice
    order_type_nm = name(OrderType, order_type)
    order_direction_nm = name(OrderDirection, order_direction)
    tag = humanize(ts=algo.Time, topic="CONTRACT",
                   OrderDirection=order_direction_nm, OrderType=order_type_nm, Symbol=contract.Symbol.ToString(), Price=str(limit_price or ''),
                   PriceUnderlying=contract.Underlying.Price, BestBid=best_bid, BestAsk=best_ask)

    if hasattr(contract, 'StrikePrice'):
        tag += ', '
        tag += humanize(
            Strike=contract.StrikePrice,
            Expiry=contract.Expiry,
            Contract=contract.ToString()
        )
    algo.Log(tag)
    return tag


def log_dividend(algo: QCAlgorithm, data: Slice, sym: Symbol):
    dividend = data.Dividends[sym]
    tag = humanize(ts=algo.Time, topic="DIVIDEND",
                   symbol=str(dividend.Symbol), Distribution=dividend.Distribution, PortfolioCash=algo.Portfolio.Cash,
                   Price=algo.Portfolio[sym].Price)
    algo.Log(tag)
    return tag


def log_order_event(algo: QCAlgorithm, order_event: OrderEvent):
    if order_event.Status == OrderStatus.UpdateSubmitted:
        return
    security: Security = algo.Securities[order_event.Symbol]
    order_status_nm = name(OrderStatus, order_event.Status)
    order_direction_nm = name(OrderDirection, order_event.Direction)
    security_type_nm = name(SecurityType, security.Type)
    symbol = order_event.Symbol.ToString()
    tag = humanize(
        ts=algo.Time, topic="ORDER EVENT",
        OrderDirection=order_direction_nm, OrderStatus=order_status_nm, SecurityType=security_type_nm, Symbol=symbol,
        FillQuantity=order_event.FillQuantity, LimitPrice=order_event.LimitPrice,
        FillPrice=order_event.FillPrice, Fee=order_event.OrderFee, BestBid=security.BidPrice, BestAsk=security.AskPrice)
    algo.Log(tag)
    return tag


def log_risk(algo: QCAlgorithm) -> str:
    pf_risk = PortfolioRisk.e(algo)
    tag = humanize(ts=algo.Time, topic="RISK", **pf_risk.to_dict(),)
    algo.Log(tag)
    return tag


def log_pl(algo: QCAlgorithm, symbol, **kwargs) -> str:
    tag = humanize(ts=algo.Time, topic="PL", symbol=str(symbol), **kwargs)
    algo.Log(tag)
    return tag


def log_last_price_change(algo: QCAlgorithm, symbol: Symbol):
    if algo.Securities[symbol].Price != algo.last_price[symbol] and algo.order_tickets[symbol]:
        ticket = algo.order_tickets[symbol][0]
        algo.Log(humanize(ts=algo.Time, topic="NEW TRADE", Symbol=symbol,
                 TicketPrice=ticket.Get(OrderField.LimitPrice),
                 NewPrice=algo.Securities[symbol].Price,
                 BestPrice=algo.Securities[symbol].BidPrice,
                 AskPrice=algo.Securities[symbol].AskPrice,
                 Delta=algo.Securities[symbol].Price - algo.last_price[symbol]
                          ))
        algo.last_price[symbol] = algo.Securities[symbol].Price