using System;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.RealityModeling;
using QuantConnect.Brokerages;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerMine : BrokerageModelSecurityInitializer
    {
        public int VolatilityPeriodDays { get; set; }

        private Foundations algo;
        public SecurityInitializerMine(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilityPeriodDays)
        : base(brokerageModel, securitySeeder) {
            this.algo = algo;
            VolatilityPeriodDays = volatilityPeriodDays;
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);

            if (!algo.LiveMode)
            {
                // Margin Model
                security.MarginModel = SecurityMarginModel.Null;
                security.SetBuyingPowerModel(new NullBuyingPowerModel());

                // Fill Model
                security.SetFillModel(new FillModelMine());
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
                
                foreach (var tradeBar in algo.HistoryWrap(security.Symbol, algo.Periods(days: VolatilityPeriodDays + 2), algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }
                algo.Log($"SecurityInitializer.Initialize: {security.Symbol} WarmedUp Volatility To: {security.VolatilityModel.Volatility}");

                // Initialize a Security Specific Hedge Band or Risk Limit object. Constitutes underlying, hence risk limit not just by security but also its derivatives.
                // Adjust delta by underlying's volatility.
                security.RiskLimit = new SecurityRiskLimit(security, delta100BpLong: 20, delta100BpShort: -20);
            }
            else

            if (security.Type == SecurityType.Option)
            {
                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                // Initialize a Security Specific Hedge Band or Risk Limit object.
                option.RiskLimit = new SecurityRiskLimit(option);

                algo.IVBids[option.Symbol] = new IVBid(option, algo);
                algo.IVAsks[option.Symbol] = new IVAsk(option, algo);
                algo.RollingIVBid[option.Symbol] = new RollingIVIndicator<IVBidAsk>(1000, option.Symbol, 0.15);
                algo.RollingIVAsk[option.Symbol] = new RollingIVIndicator<IVBidAsk>(1000, option.Symbol, 0.15);
            }

            if (security.Resolution == Resolution.Tick)
            {
                security.SetDataFilter(new OptionTickDataFilter(algo));
            }
            WarmUpSecurity(security);
        }

        public void WarmUpSecurity(Security security)
        {
            VolatilityBar volBar;
            Symbol symbol;

            algo.Log($"SecurityInitializer.WarmUpSecurity: {security}");


            if (security.Type == SecurityType.Option)
            {
                var option = (Option)security;

                if (option.Underlying == null) return;
                symbol = option.Symbol;
                var volaSyms = algo.Securities.Keys.Where(s => s.Underlying == symbol);
                var history = algo.History<VolatilityBar>(volaSyms, algo.Periods(days: 5), algo.resolution, fillForward: false);
                foreach (DataDictionary<VolatilityBar> data in history)
                {
                    if (data.TryGetValue(volaSyms.First(), out volBar))
                    {
                        algo.RollingIVBid[symbol].Update(new IVBidAsk(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceBid.Close, (double)volBar.Bid.Close));
                        algo.RollingIVAsk[symbol].Update(new IVBidAsk(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceAsk.Close, (double)volBar.Ask.Close));
                    }
                }
                if (algo.RollingIVBid[symbol].Last != null)
                {
                    algo.IVBids[symbol].Update(algo.RollingIVBid[symbol].Last);
                    algo.Log($"WarmUpSecurity.RollingIVBid {option.Symbol}: Samples: {algo.RollingIVBid[option.Symbol].Samples}");
                }
                else
                {
                    algo.Error($"WarmUpSecurity.RollingIVBid {symbol}. Missing IV Bid data. Not Warmed up. {algo.Periods(days: 7)} - {algo.resolution}");
                }
                if (algo.RollingIVAsk[symbol].Last != null)
                {
                    algo.IVAsks[symbol].Update(algo.RollingIVAsk[symbol].Last);
                    algo.Log($"WarmUpSecurity.RollingIVAsk {option.Symbol}: Samples: {algo.RollingIVAsk[option.Symbol].Samples}");
                }
                else
                {
                    algo.Error($"WarmUpSecurity.RollingIVAsk {symbol}. Missing IV Ask data. Not Warmed up. {algo.Periods(days: 7)} - {algo.resolution}");
                }
            }
        }

        /// <summary>
        /// Taking too long. May want to load historical IVs from list. Then just calculate EWMA load IV method... Loadings IVs requires much set up work... shortcut?
        /// Works for 1 day of testing securities....
        /// </summary>
        /// <param name="security"></param>
        public void WarmUpSecurityOnTheFlyIV(Security security)
        {
            QuoteBar quoteBar;
            QuoteBar quoteBarUnderlying;
            Symbol symbol;

            algo.Log($"SecurityInitializer.WarmUpSecurity: {security}");


            if (security.Type == SecurityType.Option)
            {
                var option = (Option)security;

                if (option.Underlying == null) return;
                //if (option.Underlying.Price == 0) return;
                //if (option.Underlying.HasData)
                symbol = option.Symbol;
                var history = algo.History<QuoteBar>(new List<Symbol>() { symbol, option.Symbol.Underlying }, algo.Periods(days: 5), algo.resolution, fillForward: false);

                // Loop over symbols, determine time frontier, use latest among iterators until finished.
                decimal underlyingMidPrice = 0;
                foreach (DataDictionary<QuoteBar> data in history)
                {
                    if (data.TryGetValue(option.Symbol.Underlying, out quoteBarUnderlying))
                    {
                        underlyingMidPrice = (quoteBarUnderlying.Bid.Close + quoteBarUnderlying.Ask.Close) / 2;
                    }

                    if (data.TryGetValue(option.Symbol, out quoteBar) && underlyingMidPrice != 0)
                    {
                        algo.IVBids[symbol].Update(quoteBar, underlyingMidPrice);
                        algo.IVAsks[symbol].Update(quoteBar, underlyingMidPrice);
                        algo.RollingIVBid[symbol].Update(algo.IVBids[symbol].Current);
                        algo.RollingIVAsk[symbol].Update(algo.IVAsks[symbol].Current);
                    }
                }
                
                algo.Log($"WarmUpSecurity.RollingIVBid {option.Symbol}: Samples: {algo.RollingIVBid[option.Symbol].Samples}");
                algo.Log($"WarmUpSecurity.RollingIVAsk {option.Symbol}: Samples: {algo.RollingIVAsk[option.Symbol].Samples}");
            }
            else if (security.Type == SecurityType.Equity)
            {

            }
        }
    }
}
