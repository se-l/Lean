using System;
using QuantConnect.Algorithm.CSharp.Core.RealityModeling;
using QuantConnect.Brokerages;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerDataRecorder : BrokerageModelSecurityInitializer
    {
        private readonly ADataRecorder _algo;
        public SecurityInitializerDataRecorder(IBrokerageModel brokerageModel, ADataRecorder algo, ISecuritySeeder securitySeeder)
        : base(brokerageModel, securitySeeder) {
            _algo = algo;
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;
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

            _algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromMinutes(1));
            _algo.TradeBarConsolidators[symbol] = new TradeBarConsolidator(TimeSpan.FromMinutes(1));

            _algo.QuoteBarConsolidators[symbol].DataConsolidated += (object sender, QuoteBar consolidated) => _algo.RecordQuoteBar(consolidated, Resolution.Minute);
            _algo.TradeBarConsolidators[symbol].DataConsolidated += (object sender, TradeBar consolidated) => _algo.RecordTradeBar(consolidated, Resolution.Minute);
        }
    }
}
