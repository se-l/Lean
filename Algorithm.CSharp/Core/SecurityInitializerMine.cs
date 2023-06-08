using QuantConnect.Algorithm.CSharp.Core.Pricing.Volatility;
using QuantConnect.Brokerages;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerMine : BrokerageModelSecurityInitializer
    {
        public int VolatilitySpan { get; set; }

        private Foundations algo;
        public SecurityInitializerMine(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilitySpan)
        : base(brokerageModel, securitySeeder) {
            this.algo = algo;
            VolatilitySpan = volatilitySpan;
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);

            // Next, overwrite some of the reality models        
            security.SetBuyingPowerModel(new NullBuyingPowerModel());
            security.SetFillModel(new FillModelMy());

            if (security.Type == SecurityType.Equity)
            {
                //security.VolatilityModel = new EstimatorYangZhang(VolatilitySpan);
                security.VolatilityModel = new StandardDeviationOfReturnsVolatilityModel(VolatilitySpan, Resolution.Daily);
                foreach (var tradeBar in algo.HistoryWrap(security.Symbol, VolatilitySpan, Resolution.Daily))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }
            }
            else
            if (security.Type == SecurityType.Option)
            {
                (security as Option).PriceModel = new CurrentPriceOptionPriceModel();

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;
            }
        }
    }
}
