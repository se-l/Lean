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
using QuantConnect.Securities.Equity;
using QuantConnect.Data;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerMine : BrokerageModelSecurityInitializer
    {
        public int VolatilityPeriodDays { get; set; }

        private readonly Foundations _algo;
        public SecurityInitializerMine(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilityPeriodDays)
        : base(brokerageModel, securitySeeder)
        {
            _algo = algo;
            VolatilityPeriodDays = volatilityPeriodDays;
        }

        private void HoldingsOnQuantityChanged(object sender, SecurityHoldingQuantityChangedEventArgs e)
        {
            // This methods appears to be called before algo updates Positions dictionary through an orderEvent. In that case, this causes a mismatch as a fill is applied as trade incrementing the quantity rather than updating it.
            // This method is only meant to take care of fills for which algo had no order tickets.
            // Ideally becomes an OrderEvent... but there's no order!!!
            Symbol symbol = e.Security.Symbol;
            if (_algo.Positions.TryGetValue(symbol, out Position position) && position.Quantity != e.Security.Holdings.Quantity)
            {
                _algo.Log($"{_algo.Time} HoldingsOnQuantityChanged(): {symbol} Initialize new Position from Security Holding as Quantity differs. Previously: {position.Quantity}, Now: {e.Security.Holdings.Quantity}. May impact post trade reporting.");
                _algo.Positions[symbol] = new Position(_algo, e.Security.Holdings);
            }
            else if (!_algo.Positions.ContainsKey(symbol))
            {
                _algo.Log($"{_algo.Time} HoldingsOnQuantityChanged(): {symbol} Initialize new Position from Security Holding as Symbol is not present.");
                _algo.Positions[symbol] = new Position(_algo, e.Security.Holdings);
            }
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;

            if (!_algo.LiveMode && security.Type == SecurityType.Option)
            {
                // Option Probabilistic Fill Model
                var dailyVolume = _algo.History<TradeBar>(security.Symbol, _algo.Periods(Resolution.Daily, days: 7), Resolution.Daily, fillForward: false).Select(bar => bar.Volume);
                decimal meanDailyVolume = dailyVolume.Any() ? dailyVolume.Average() : 0;
                _algo.Log($"SecurityInitializer.Initialize FillModelVolumeWeighted: {symbol} MeanDailyVolume={meanDailyVolume}.");
                security.SetFillModel(new FillModelVolumeWeighted(meanDailyVolume, 100, 2.0, new StaticLiquiditySurface()));
            }
            else if(!_algo.LiveMode)
            {
                security.SetFillModel(new FillModelMine());
            }
            // Margin Model
            security.MarginModel = SecurityMarginModel.Null;
            security.SetBuyingPowerModel(BuyingPowerModel.Null);
            // security.MarginModel = new BuyingPowerModelMine(_algo);
            // security.SetBuyingPowerModel(new BuyingPowerModelMine(_algo));
            security.Holdings.QuantityChanged += HoldingsOnQuantityChanged;

            if (security.Type == SecurityType.Equity)
            {
                Equity equity = (Equity)security;
                if (!_algo.QuoteBarConsolidators.ContainsKey(symbol))
                {
                    _algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromSeconds(1));
                    _algo.TradeBarConsolidators[symbol] = new TradeBarConsolidator(TimeSpan.FromSeconds(1));
                }

                int samplePeriods = _algo.resolution switch
                {
                    Resolution.Daily => 1,
                    Resolution.Hour => 1,
                    Resolution.Minute => 5,
                    Resolution.Second => 300,
                    _ => 1
                };

                int totalRollingPeriods = (int)(VolatilityPeriodDays * 6.5 * 60 * 60 / samplePeriods);
                security.VolatilityModel = new VolatilityModelMine(_algo, security, periods: totalRollingPeriods, _algo.resolution, TimeSpan.FromSeconds(samplePeriods));
                int samples = 0;
                int historyPeriods = _algo.Periods(days: VolatilityPeriodDays + 2);
                // foreach (var tradeBar in _algo.History(symbol, _algo.Time.AddDays(-(VolatilityPeriodDays + 2)), _algo.Time, _algo.resolution))
                foreach (var tradeBar in _algo.HistoryWrap(symbol, historyPeriods, _algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                    samples++;
                }
                _algo.Log($"SecurityInitializer.Initialized VolatilityModel: {symbol} Resolution={_algo.resolution}, Periods={totalRollingPeriods}, historySamples={samples}.");
                
                if (samples == 0)
                {
                    _algo.Log($"SecurityInitializer.Initialize: {symbol} No history found. Skipping indicator and writer initialization.");
                    return;
                }

                // Initialize a Security Specific Hedge Band or Risk Limit object. Constitutes underlying, hence risk limit not just by security but also its derivatives.
                // Adjust delta by underlying's volatility.
                security.RiskLimit = new SecurityRiskLimit(security, delta100BpLong: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalLong, delta100BpShort: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalShort);

                decimal vola = security.VolatilityModel.Volatility;
                InitializeIVSurfaces(equity);
                _algo.Log($"SecurityInitializer.Initialize: {symbol} WarmedUp Volatility To: PostSurface: {security.VolatilityModel.Volatility}, PreSurface: {vola}");

                _algo.QuoteBarConsolidators[symbol].DataConsolidated += (object sender, QuoteBar consolidated) =>
                {
                    if (_algo.IsEventNewQuote(symbol))  // Can refactor this to event driven. Then IVBids/Asks subscribe to EventNewQuote. Saves costly consolidator logic
                    {
                        _algo.IvBids.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                        _algo.IvAsks.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                    }
                };
                _algo.MarketDataQuotes[security.Symbol] = new();
                _algo.MarketDataTrades[security.Symbol] = new();

                _algo.GammaScalpers[symbol] = new(_algo, equity);
                //_algo.PutCallRatios[symbol] = new PutCallRatioIndicator(equity, _algo, TimeSpan.FromDays(_algo.Cfg.PutCallRatioWarmUpDays));
                //_algo.IntradayIVDirectionIndicators[symbol] = new IntradayIVDirectionIndicator(_algo, security.Symbol);
                //_algo.AtmIVIndicators[symbol] = new AtmIVIndicator(_algo, equity);
                //_algo.IVSurfaceRelativeStrikeBid[symbol].EODATMEventHandler += (object sender, IVQuote e) => _algo.AtmIVIndicators[symbol].Update(e.Time.Date, e.IV, QuoteSide.Bid);
                //_algo.IVSurfaceRelativeStrikeAsk[symbol].EODATMEventHandler += (object sender, IVQuote e) => _algo.AtmIVIndicators[symbol].Update(e.Time.Date, e.IV, QuoteSide.Ask);

                // Refactor - turn percentages over to some config.
                _algo.UnderlyingMovedX[(symbol, 0.001m)] = new(equity, 0.001m);
                _algo.UnderlyingMovedX[(symbol, 0.002m)] = new(equity, 0.002m);
                _algo.UnderlyingMovedX[(symbol, 0.005m)] = new(equity, 0.005m);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.001m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.002m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.005m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);

                //_algo.ConsecutiveTicksTrend[symbol] = new(equity);
                //_algo.RegisterIndicator(symbol, _algo.ConsecutiveTicksTrend[symbol], _algo.QuoteBarConsolidators[symbol], (IBaseData b) => (((QuoteBar)b).Bid.Close + ((QuoteBar)b).Ask.Close) / 2);

                _algo.SignalsLastRun[security.Symbol] = DateTime.MinValue;
                _algo.IsSignalsRunning[security.Symbol] = false;

                _algo.RiskProfiles[security.Symbol] = new RiskProfile(_algo, equity);
                _algo.UtilityWriters[security.Symbol] = new UtilityWriter(_algo, equity);
                _algo.OrderEventWriters[security.Symbol] = new OrderEventWriter(_algo, equity);
                _algo.AbsoluteDiscounts[security.Symbol] = new RiskDiscount(_algo, _algo.Cfg, security.Symbol, Metric.Absolute);
                _algo.RequestContractsHandlers[security.Symbol] = new RequestContractsHandler(_algo, equity);
            }

            else if (security.Type == SecurityType.Option)
            {
                // Need to overrride fee model, given discounts by exchanges (NASDQAQM) matters significantly.

                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();
                option.SetOptionAssignmentModel(new DefaultOptionAssignmentModel(0, new TimeSpan(-1, 2, 30, 0)));  //CustomOptionAssignmentModel

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                // Initialize a Security Specific Hedge Band or Risk Limit object.
                option.RiskLimit = new SecurityRiskLimit(option);

                _algo.MarketDataQuotes[security.Symbol] = new();
                _algo.MarketDataTrades[security.Symbol] = new();

                _algo.IvBids[symbol] = new IVQuoteIndicator(QuoteSide.Bid, option, _algo);
                _algo.IvAsks[symbol] = new IVQuoteIndicator(QuoteSide.Ask, option, _algo);
                int ivSpreadSMAPeriod = _algo.Cfg.IVSpreadSMAPeriod.TryGetValue(symbol, out int period) ? period : _algo.Cfg.IVSpreadSMAPeriod[CfgDefault];
                _algo.IVSpreadSMA[symbol] = new SimpleMovingAverage(ivSpreadSMAPeriod);

                //_algo.IVBids[symbol].Updated += (object sender, IndicatorDataPoint _) => _algo.IVSurfaceSSVIBid[option.Symbol.Underlying].ScheduleUpdate();
                //_algo.IvAsks[symbol].Updated += (object sender, IndicatorDataPoint _) => _algo.IVSurfaceSSVIAsk[option.Symbol.Underlying].ScheduleUpdate();

                foreach (OrderDirection direction in new[] { OrderDirection.Buy, OrderDirection.Sell })
                {
                    _algo.SpreadBuffers[direction][symbol] = new(_algo, option, direction);
                }
            }

            //var equityOptions = new HashSet<SecurityType>() { SecurityType.Option, SecurityType.Equity };
            //if (equityOptions.Contains(security.Type) && !symbol.ID.Symbol.Contains("VolatilityBar"))
            //{
            //    _algo.SweepState[symbol] = new();
            //    foreach (var direction in new[] { OrderDirection.Buy, OrderDirection.Sell })
            //    {
            //        _algo.SweepState[symbol][direction] = new Sweep(_algo, symbol, direction);
            //    }
            //}

            if (security.Resolution == Resolution.Tick)
            {
                security.SetDataFilter(new OptionTickDataFilter(_algo));
            }

            WarmUpSecurity(security);
        }

        private void InitializeIVSurfaces(Equity underlying)
        {
            if (!_algo.IvSurfaceSsviMid.ContainsKey(underlying))
            {
                _algo.IvSurfaceSsviMid[underlying] = new IVSurfaceSSVI(_algo, underlying, null, false);
            }
        }
        public DateTime HistoryRequestEndDate(Security security)
        {
            if (_algo.LiveMode && _algo.Time.TimeOfDay < new TimeSpan(9, 30, 0))
            {
                SecurityExchangeHours SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, security.Symbol, security.Type);
                return Time.EachTradeableDay(SecurityExchangeHours, _algo.Time.Date.AddDays(-4), _algo.Time.Date.AddDays(-1)).Last();
            }
            else if (_algo.LiveMode && _algo.Time.TimeOfDay >= new TimeSpan(9, 30, 0))
            {
                return _algo.Time.Date;
            }
            else
            {
                return _algo.StartDate;
            }
        }

        /// <param name="security"></param>
        public void WarmUpSecurity(Security security)
        {
            Symbol symbol;

            _algo.Log($"SecurityInitializer.WarmUpSecurity: {security}");

            if (security.Type == SecurityType.Option)
            
            {
                /// Any model needs fitting. Provide surface fitting based on this data or should the service pull its own?
                /// Differently, send prices or underlying?
                /// Having surfaces fitted & cached allows a sort of preparation...
                /// Eventually intraday, relying on these prices here. Having this tested during warmup is good.
                
                var option = (Option)security;

                if (option.Underlying == null) return;
                symbol = option.Symbol;

                if (_algo.Cfg.SkipWarmUpSecurity) return;

                var volaSyms = _algo.Securities.Keys.Where(s => s.Underlying == symbol); // Only the volatilityBars have an option as Underlying.
                var volaSym = volaSyms.Any() ? volaSyms.First() : null;
                if (volaSym != null)
                {
                    DateTime end = HistoryRequestEndDate(security);
                    // One request, not two. The daily series only ever fed IVSpreadSMA, and a daily bar is
                    // stamped at the start of its day but enumerated at its end, so it can never be
                    // appended to that forward-only series anyway: 672 extra 60-day requests per run for
                    // nothing. Warm-up reads the warehouse through the R2 handler, so this is the whole cost
                    // of the security initializer.
                    var historyFast = _algo.History<VolatilityQuoteBar>(volaSym, _algo.Periods(days: _algo.Cfg.WarmUpDays), _algo.resolution, fillForward: false);

                    // Hoisted: this loop runs once per volatility bar, millions of times per run.
                    var ivBid = _algo.IvBids[symbol];
                    var ivAsk = _algo.IvAsks[symbol];
                    var ivSpreadSma = _algo.IVSpreadSMA[symbol];

                    int samples = 0;
                    foreach (VolatilityQuoteBar volBar in historyFast)
                    {
                        // Data issue. empty row is loaded.
                        if (volBar.Ask.Close == 0 && volBar.Bid.Close == 0)
                        {
                            continue;
                        }

                        // Past data served by the handler (R2 iceberg): feed the IVs straight in, no pricer.
                        ivBid.Update(volBar.EndTime, (double)volBar.Bid.Close);
                        ivAsk.Update(volBar.EndTime, (double)volBar.Ask.Close);
                        // Spreads are intraday here: never append an out-of-order point to the SMA.
                        if (volBar.Time > ivSpreadSma.Current.Time)
                        {
                            ivSpreadSma.Update(new IndicatorDataPoint(volBar.Time, (decimal)(volBar.Ask.Close - volBar.Bid.Close)));
                        }
                        samples++;
                    }
                    if (samples == 0)
                    {
                        _algo.Log($"SecurityInitializer.WarmUpSecurity: {symbol} No VolatilityQuoteBar history returned to warmup indicators with.");
                    }
                }
                else
                {
                    _algo.Log($"SecurityInitializer.WarmUpSecurity: {symbol} No VolatilityQuoteBar found to warmup indicators with.");
                }
            }
        }
    }

}
