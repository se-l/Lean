from core.reality_modeling.fill_model import CustomFillModel
from core.stubs import *


class MySecurityInitializer(BrokerageModelSecurityInitializer):

    def __init__(self, brokerage_model: IBrokerageModel, security_seeder: ISecuritySeeder) -> None:
        super().__init__(brokerage_model, security_seeder)

    def Initialize(self, security: Security) -> None:
        # First, call the superclass definition
        # This method sets the reality models of each security using the default reality models of the brokerage model
        super().Initialize(security)

        # Next, overwrite some of the reality models
        security.SetFillModel(CustomFillModel())
