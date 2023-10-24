using System;
using QuantConnect.Algorithm.CSharp.Core.RealityModeling;
using QuantConnect.Brokerages;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerDataRecorder : BrokerageModelSecurityInitializer
    {
        public SecurityInitializerDataRecorder(IBrokerageModel brokerageModel, ISecuritySeeder securitySeeder)
        : base(brokerageModel, securitySeeder) {
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            security.SetFillModel(new FillModelMine());
            security.VolatilityModel = VolatilityModel.Null;
            security.MarginModel = SecurityMarginModel.Null;
            security.SetBuyingPowerModel(new NullBuyingPowerModel());

            if (security.Type == SecurityType.Option)
            {
                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();
                option.SetOptionAssignmentModel(new DefaultOptionAssignmentModel(0, TimeSpan.FromDays(0)));  //CustomOptionAssignmentModel
            }
        }
    }
}
