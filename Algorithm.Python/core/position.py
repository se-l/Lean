import json
from dataclasses import dataclass, asdict

from core.constants import DIRECTION2NUM, BP
from core.pricing.option_contract_wrap import OptionContractWrap
from core.stubs import *
from core.pricing.greeks_plus import GreeksPlus, PLExplain
from core.utils import mid_price, round_tick, name


@dataclass
class Position:
    time: datetime
    symbol: Symbol
    security_type: SecurityType

    quantity: int = 0
    price_last_traded: float = 0  # Last Traded Price
    price_mid: float = 0
    dP: float = 0  # Delta MidPrices
    dP_underlying: float = 0

    pl: float = 0
    pl_explain: PLExplain = None
    greeks: GreeksPlus = None

    def to_json(self):
        _st = self.security_type
        self.security_type = name(SecurityType, self.security_type)
        res = json.dumps(asdict(self))
        self.security_type = _st
        return res

    @classmethod
    def e(cls, algo: QCAlgorithm, symbol: Symbol):
        order_events: List[OrderEvent] = [e for e in algo.order_events if e.Symbol == symbol]

        security: Equity | OptionContract = algo.Securities[symbol]
        security_type = security.Type
        quantity = algo.Portfolio[symbol].Quantity
        price_last_traded = security.Price
        price_mid = mid_price(algo, security)
        best_bid = security.BidPrice
        best_ask = security.AskPrice
        spread = best_ask - best_bid
        multiplier = 100 if security_type == SecurityType.Option else 1
        dP = round_tick(price_last_traded - algo.sym_sod_price_mid[symbol], BP)

        # Option specific
        dP_underlying = 0
        greeks = 0
        pl_explain = 0  # Not doing any for equity...

        pl = 0
        for e in order_events:
            if e.Status in (OrderStatus.Filled, OrderStatus.PartiallyFilled):  # might be double counting  here...
                pl -= e.FillPrice * e.FillQuantity
            pl += price_mid * quantity * multiplier

        if security_type == SecurityType.Option:
            price_mid_underlying = mid_price(algo, algo.Securities[security.UnderlyingSymbol])
            dP_underlying = price_mid_underlying - algo.sym_sod_price_mid[security.UnderlyingSymbol]

            ocw = OptionContractWrap(algo, security)
            price_theoretical = ocw.price()
            g = ocw.greeks()
            pl_explained = g.pl_explain(dP=dP, dT=1, dIV=0, dR=0)  # need to add dIV, once Volatility model is implemented

        return cls(
            time=algo.Time,
            symbol=symbol,
            security_type=security_type,
            quantity=quantity,
            price_last_traded=price_last_traded,
            price_mid=price_mid,
            dP=dP,
            dP_underlying=dP_underlying,
            pl=pl,
            pl_explain=pl_explained,
            greeks=g,
        )
