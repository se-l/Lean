using System;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.RealityModeling;
using QuantConnect.Brokerages;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System.Linq;
using QuantConnect.Data.Consolidators;
using QuantConnect.Util;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerIVExporter : BrokerageModelSecurityInitializer
    {
        public int VolatilityPeriodDays { get; set; }

        private Foundations algo;
        public SecurityInitializerIVExporter(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilityPeriodDays)
        : base(brokerageModel, securitySeeder) {
            this.algo = algo;
            VolatilityPeriodDays = volatilityPeriodDays;
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;

            if (!algo.LiveMode)
            {
                // Margin Model
                security.MarginModel = SecurityMarginModel.Null;
                security.SetBuyingPowerModel(new NullBuyingPowerModel());

                // Fill Model
                security.SetFillModel(new FillModelMine());
            }

            if (!algo.QuoteBarConsolidators.ContainsKey(symbol))
            {
                algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromSeconds(1));
            }

            if (security.Type == SecurityType.Equity)
            {
                algo.QuoteBarConsolidators[symbol].DataConsolidated += (object sender, QuoteBar consolidated) =>
                {
                    if (algo.IsEventNewQuote(symbol))
                    {
                        algo.IVBids.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                        algo.IVAsks.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                    }
                };
            }

            else if (security.Type == SecurityType.Option)
            {
                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                // Initialize a Security Specific Hedge Band or Risk Limit object.
                option.RiskLimit = new SecurityRiskLimit(option);

                algo.IVBids[symbol] = new IVQuoteIndicator(QuoteSide.Bid, option, algo);
                algo.IVAsks[symbol] = new IVQuoteIndicator(QuoteSide.Ask, option, algo);
                algo.IVTrades[symbol] = new IVTrade(option, algo);
                // Window size must capture one day of entries. Second resolution ; 6.5*60*60 = 23400. Then it's reset at eod.
                algo.RollingIVBid[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
                algo.RollingIVAsk[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
                algo.RollingIVTrade[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
            }

            if (security.Type == SecurityType.Equity)
            {
                int samplePeriods = algo.resolution switch
                {
                    Resolution.Daily => 1,
                    Resolution.Hour => 1,
                    Resolution.Minute => 5,
                    Resolution.Second => 300,
                    _ => 1
                };
                security.VolatilityModel = new StandardDeviationOfReturnsVolatilityModel(periods: algo.Periods(days: VolatilityPeriodDays) / samplePeriods, algo.resolution, TimeSpan.FromSeconds(samplePeriods));

                foreach (var tradeBar in algo.HistoryWrap(security.Symbol, algo.Periods(days: VolatilityPeriodDays + 1), algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }
                algo.Log($"SecurityInitializer.Initialize: {security.Symbol} WarmedUp Volatility To: {security.VolatilityModel.Volatility}");
            }
        }
    }
}
