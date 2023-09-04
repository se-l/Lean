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
    public class SecurityInitializerMine : BrokerageModelSecurityInitializer
    {
        public int VolatilityPeriodDays { get; set; }

        private readonly Foundations algo;
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
                int samplePeriods = algo.resolution switch
                {
                    Resolution.Daily => 1,
                    Resolution.Hour => 1,
                    Resolution.Minute => 5,
                    Resolution.Second => 300,
                    _ => 1
                };
                security.VolatilityModel = new StandardDeviationOfReturnsVolatilityModel(periods: algo.Periods(days: VolatilityPeriodDays) / samplePeriods, algo.resolution, TimeSpan.FromSeconds(samplePeriods));
                
                foreach (var tradeBar in algo.HistoryWrap(symbol, algo.Periods(days: VolatilityPeriodDays + 2), algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }
                algo.Log($"SecurityInitializer.Initialize: {symbol} WarmedUp Volatility To: {security.VolatilityModel.Volatility}");

                // Initialize a Security Specific Hedge Band or Risk Limit object. Constitutes underlying, hence risk limit not just by security but also its derivatives.
                // Adjust delta by underlying's volatility.
                security.RiskLimit = new SecurityRiskLimit(security, delta100BpLong: algo.Cfg.RiskLimitEODDelta100BpUSDTotalLong, delta100BpShort: algo.Cfg.RiskLimitEODDelta100BpUSDTotalShort);

                InitializeIVSurface(symbol);
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
                option.SetOptionAssignmentModel(new DefaultOptionAssignmentModel(0, TimeSpan.FromDays(0)));  //CustomOptionAssignmentModel

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                // Initialize a Security Specific Hedge Band or Risk Limit object.
                option.RiskLimit = new SecurityRiskLimit(option);

                algo.IVBids[symbol] = new IVQuoteIndicator(QuoteSide.Bid, option, algo);
                algo.IVAsks[symbol] = new IVQuoteIndicator(QuoteSide.Ask, option, algo);
            }

            if (security.Resolution == Resolution.Tick)
            {
                security.SetDataFilter(new OptionTickDataFilter(algo));
            }
            WarmUpSecurity(security);
        }

        private void InitializeIVSurface(Symbol underlying)
        {
            if (!algo.IVSurfaceRelativeStrikeBid.ContainsKey(underlying))
            {
                algo.IVSurfaceRelativeStrikeBid[underlying] = new IVSurfaceRelativeStrike(algo, underlying, QuoteSide.Bid);
            }
            if (!algo.IVSurfaceRelativeStrikeAsk.ContainsKey(underlying))
            {
                algo.IVSurfaceRelativeStrikeAsk[underlying] = new IVSurfaceRelativeStrike(algo, underlying, QuoteSide.Ask);
            }
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
                Symbol underlying = symbol.Underlying;
                algo.IVSurfaceRelativeStrikeBid[option.Symbol.Underlying].RegisterSymbol(option);
                algo.IVSurfaceRelativeStrikeAsk[option.Symbol.Underlying].RegisterSymbol(option);

                var volaSyms = algo.Securities.Keys.Where(s => s.Underlying == symbol);
                var history = algo.History<VolatilityBar>(volaSyms, algo.Periods(days: 7), algo.resolution, fillForward: false);

                foreach (DataDictionary<VolatilityBar> data in history)
                {
                    IVQuote bid = null;
                    IVQuote ask = null;
                    if (data.TryGetValue(volaSyms.First(), out volBar))
                    {
                        // Data issue. empty row is loaded.
                        if (volBar.Ask.Close == 0 && volBar.Bid.Close == 0)
                        {
                            continue;
                        }
                        bid = new IVQuote(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceBid.Close, (double)volBar.Bid.Close);
                        ask = new IVQuote(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceAsk.Close, (double)volBar.Ask.Close);                        
                    }
                    if (bid != null)
                    {
                        algo.IVBids[symbol].Update(bid);
                        algo.IVAsks[symbol].Update(ask);
                    }                    
                }

                //Action<string> log = algo.RollingIVStrikeBid[underlying].IsReady ? algo.Log : algo.Error;
                //log($"WarmUpSecurity.RollingIVStrikeBid {underlying}: Samples: {algo.RollingIVStrikeBid[underlying].Samples(symbol)}");
                //log($"WarmUpSecurity.RollingIVStrikeAsk {underlying}: Samples: {algo.RollingIVStrikeBid[underlying].Samples(symbol)}");
            }
        }
    }
}
