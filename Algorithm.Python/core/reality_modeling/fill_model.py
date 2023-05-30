from core.stubs import *


class CustomFillModel(ImmediateFillModel):
    def __init__(self):
        super().__init__()
        # self.algorithm = algorithm
        # self.absoluteRemainingByOrderId = {}
        # self.random = Random(387510346)

    def LimitFill(self, asset: Security, order: LimitOrder) -> OrderEvent:
        return super().LimitFill(asset, order)

    # def MarketFill(self, asset: Security, order: MarketOrder) -> OrderEvent:
    #     return super().MarketFill(asset, order)

    # def LimitIfTouchedFill(self, asset: Security, order: LimitIfTouchOrder) -> OrderEvent:
    #     return super().LimitIfTouchedFill(asset, order)
    #
    # def StopMarketFill(self, asset: Security, order: StopMarketOrder) -> OrderEvent:
    #     return super().StopMarketFill(asset, order)
    #
    # def StopLimitFill(self, asset: Security, order: StopLimitOrder) -> OrderEvent:
    #     return super().StopLimitFill(asset, order)
    #
    # def MarketOnOpenFill(self, asset: Security, order: MarketOnOpenOrder) -> OrderEvent:
    #     return super().MarketOnOpenFill(asset, order)
    #
    # def MarketOnCloseFill(self, asset: Security, order: MarketOnCloseOrder) -> OrderEvent:
    #     return super().MarketOnCloseFill(asset, order)
