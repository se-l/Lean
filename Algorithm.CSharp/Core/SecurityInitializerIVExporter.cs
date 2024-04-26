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

        private Foundations _algo;
        public SecurityInitializerIVExporter(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilityPeriodDays)
        : base(brokerageModel, securitySeeder) {
            this._algo = algo;
            VolatilityPeriodDays = volatilityPeriodDays;
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;

            if (!_algo.LiveMode)
            {
                // Margin Model
                security.MarginModel = SecurityMarginModel.Null;
                security.SetBuyingPowerModel(new NullBuyingPowerModel());
                // Fill Model
                security.SetFillModel(new FillModelMine());
            }

            if (!_algo.QuoteBarConsolidators.ContainsKey(symbol))
            {
                _algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromSeconds(1));
            }

            if (security.Type == SecurityType.Equity)
            {
                _algo.QuoteBarConsolidators[symbol].DataConsolidated += (object sender, QuoteBar consolidated) =>
                {
                    if (_algo.IsEventNewQuote(symbol))
                    {
                        _algo.IVBids.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                        _algo.IVAsks.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                    }
                };
            }

            else if (security.Type == SecurityType.Option)
            {
                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                _algo.IVBids[symbol] = new IVQuoteIndicator(QuoteSide.Bid, option, _algo);
                _algo.IVAsks[symbol] = new IVQuoteIndicator(QuoteSide.Ask, option, _algo);
                _algo.IVTrades[symbol] = new IVTrade(option, _algo);
                // Window size must capture one day of entries. Second resolution ; 6.5*60*60 = 23400. Then it's reset at eod.
                _algo.RollingIVBid[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
                _algo.RollingIVAsk[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
                _algo.RollingIVTrade[symbol] = new RollingIVIndicator<IVQuote>(100_000, symbol);
            }

            if (security.Type == SecurityType.Equity)
            {
                int samplePeriods = _algo.resolution switch
                {
                    Resolution.Daily => 1,
                    Resolution.Hour => 1,
                    Resolution.Minute => 5,
                    Resolution.Second => 300,
                    _ => 1
                };
                security.VolatilityModel = new StandardDeviationOfReturnsVolatilityModel(periods: _algo.Periods(days: VolatilityPeriodDays) / samplePeriods, _algo.resolution, TimeSpan.FromSeconds(samplePeriods));

                foreach (var tradeBar in _algo.HistoryWrap(security.Symbol, _algo.Periods(days: VolatilityPeriodDays + 1), _algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }
                _algo.Log($"SecurityInitializer.Initialize: {security.Symbol} WarmedUp Volatility To: {security.VolatilityModel.Volatility}");
            }
        }
        public void RegisterIndicators(Option option)
        {
            _algo.Log($"{_algo.Time} SecurityInitializer.RegisterIndicators: {option.Symbol}");
            Symbol symbol = option.Symbol;
            _algo.RegisterIndicator(symbol, _algo.IVBids[symbol], _algo.QuoteBarConsolidators[symbol], _algo.IVBids[symbol].Selector);
            _algo.RegisterIndicator(symbol, _algo.IVAsks[symbol], _algo.QuoteBarConsolidators[symbol], _algo.IVAsks[symbol].Selector);
        }
    }
}
