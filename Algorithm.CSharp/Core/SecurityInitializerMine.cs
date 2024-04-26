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
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Synchronizer;
using QuantConnect.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;

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

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;

            if (!_algo.LiveMode)
            {
                // Fill Model
                security.SetFillModel(new FillModelMine());
            }
            // Margin Model
            security.MarginModel = SecurityMarginModel.Null;
            security.SetBuyingPowerModel(BuyingPowerModel.Null);
            // security.MarginModel = new BuyingPowerModelMine(_algo);
            // security.SetBuyingPowerModel(new BuyingPowerModelMine(_algo));

            if (!_algo.QuoteBarConsolidators.ContainsKey(symbol))
            {
                _algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromSeconds(1));
                _algo.TradeBarConsolidators[symbol] = new TradeBarConsolidator(TimeSpan.FromSeconds(1));
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

                int totalRollingPeriods = (int)(VolatilityPeriodDays * 6.5 * 60 * 60 / samplePeriods);
                security.VolatilityModel = new VolatilityModelMine(_algo, security, periods: totalRollingPeriods, _algo.resolution, TimeSpan.FromSeconds(samplePeriods));

                foreach (var tradeBar in _algo.HistoryWrap(symbol, _algo.Periods(days: VolatilityPeriodDays + 2), _algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                }                

                // Initialize a Security Specific Hedge Band or Risk Limit object. Constitutes underlying, hence risk limit not just by security but also its derivatives.
                // Adjust delta by underlying's volatility.
                security.RiskLimit = new SecurityRiskLimit(security, delta100BpLong: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalLong, delta100BpShort: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalShort);

                decimal vola = security.VolatilityModel.Volatility;
                InitializeIVSurfaces(symbol);
                _algo.Log($"SecurityInitializer.Initialize: {symbol} WarmedUp Volatility To: PostSurface: {security.VolatilityModel.Volatility}, PreSurface: {vola}");

                _algo.QuoteBarConsolidators[symbol].DataConsolidated += (object sender, QuoteBar consolidated) =>
                {
                    if (_algo.IsEventNewQuote(symbol))
                    {
                        _algo.IVBids.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                        _algo.IVAsks.Where(kvp => kvp.Key.Underlying == symbol).DoForEach(kvp => kvp.Value.Update());
                    }
                };
                _algo.GammaScalpers[symbol] = new(_algo, (Equity)security);
                _algo.PutCallRatios[symbol] = new PutCallRatioIndicator((Equity)security, _algo, TimeSpan.FromDays(_algo.Cfg.PutCallRatioWarmUpDays));
                _algo.IntradayIVDirectionIndicators[symbol] = new IntradayIVDirectionIndicator(_algo, security.Symbol);
                _algo.AtmIVIndicators[symbol] = new AtmIVIndicator(_algo, (Equity)security);
                _algo.IVSurfaceRelativeStrikeBid[symbol].EODATMEventHandler += (object sender, IVQuote e) => _algo.AtmIVIndicators[symbol].Update(e.Time.Date, e.IV, QuoteSide.Bid);
                _algo.IVSurfaceRelativeStrikeAsk[symbol].EODATMEventHandler += (object sender, IVQuote e) => _algo.AtmIVIndicators[symbol].Update(e.Time.Date, e.IV, QuoteSide.Ask);

                // Refactor - turn percentages over to some config.
                _algo.UnderlyingMovedX[(symbol, 0.001m)] = new((Equity)security, 0.001m);
                _algo.UnderlyingMovedX[(symbol, 0.002m)] = new((Equity)security, 0.002m);
                _algo.UnderlyingMovedX[(symbol, 0.005m)] = new((Equity)security, 0.005m);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.001m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.002m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.005m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);

                _algo.ConsecutiveTicksTrend[symbol] = new((Equity)security);
                _algo.RegisterIndicator(symbol, _algo.ConsecutiveTicksTrend[symbol], _algo.QuoteBarConsolidators[symbol], (IBaseData b) => (((QuoteBar)b).Bid.Close + ((QuoteBar)b).Ask.Close) / 2);

                _algo.SignalsLastRun[security.Symbol] = DateTime.MinValue;
                _algo.IsSignalsRunning[security.Symbol] = false;

                _algo.RiskProfiles[security.Symbol] = new RiskProfile(_algo, (Equity)security);
                _algo.UtilityWriters[security.Symbol] = new UtilityWriter(_algo, (Equity)security);
                _algo.OrderEventWriters[security.Symbol] = new OrderEventWriter(_algo, (Equity)security);
                _algo.AbsoluteDiscounts[security.Symbol] = new RiskDiscount(_algo, _algo.Cfg, security.Symbol, Metric.Absolute);
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

                _algo.IVBids[symbol] = new IVQuoteIndicator(QuoteSide.Bid, option, _algo);
                _algo.IVAsks[symbol] = new IVQuoteIndicator(QuoteSide.Ask, option, _algo);
                _algo.IVBids[symbol].Updated += (object sender, IndicatorDataPoint _) => _algo.IVSurfaceRelativeStrikeBid[option.Symbol.Underlying].ScheduleUpdate();
                _algo.IVAsks[symbol].Updated += (object sender, IndicatorDataPoint _) => _algo.IVSurfaceRelativeStrikeAsk[option.Symbol.Underlying].ScheduleUpdate();

                _algo.PutCallRatios[option.Symbol] = new PutCallRatioIndicator(option, _algo, TimeSpan.FromDays(_algo.Cfg.PutCallRatioWarmUpDays));
            }

            if (security.Resolution == Resolution.Tick)
            {
                security.SetDataFilter(new OptionTickDataFilter(_algo));
            }

            if (!symbol.ID.Symbol.Contains("VolatilityBar"))
            {
                _algo.SweepState[symbol] = new();
                foreach (var direction in new[] { Orders.OrderDirection.Buy, Orders.OrderDirection.Sell })
                {
                    _algo.SweepState[symbol][direction] = new Sweep(_algo, symbol, direction);
                }
            }            

            WarmUpSecurity(security);
        }

        public void RegisterIndicators(Option option)
        {
            _algo.Log($"{_algo.Time} SecurityInitializer.RegisterIndicators: {option.Symbol}");
            Symbol symbol = option.Symbol;
            _algo.RegisterIndicator(symbol, _algo.IVBids[symbol], _algo.QuoteBarConsolidators[symbol], _algo.IVBids[symbol].Selector);
            _algo.RegisterIndicator(symbol, _algo.IVAsks[symbol], _algo.QuoteBarConsolidators[symbol], _algo.IVAsks[symbol].Selector);
            _algo.RegisterIndicator(symbol, _algo.PutCallRatios[symbol], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Volume ?? 0);
            //_algo.IVSurfaceAndreasenHuge[(symbol.Underlying, option.Right)].RegisterSymbol(option);
        }

        private void InitializeIVSurfaces(Symbol underlying)
        {
            if (!_algo.IVSurfaceRelativeStrikeBid.ContainsKey(underlying))
            {
                _algo.IVSurfaceRelativeStrikeBid[underlying] = new IVSurfaceRelativeStrike(_algo, underlying, QuoteSide.Bid, true);
            }
            if (!_algo.IVSurfaceRelativeStrikeAsk.ContainsKey(underlying))
            {
                _algo.IVSurfaceRelativeStrikeAsk[underlying] = new IVSurfaceRelativeStrike(_algo, underlying, QuoteSide.Ask, true);
            }
            //if (!_algo.IVSurfaceAndreasenHuge.ContainsKey((underlying, OptionRight.Call)))
            //{
            //    _algo.IVSurfaceAndreasenHuge[(underlying, OptionRight.Call)] = new IVSurfaceAndreasenHuge(_algo, underlying, OptionRight.Call, true);
            //}
            //if (!_algo.IVSurfaceAndreasenHuge.ContainsKey((underlying, OptionRight.Put)))
            //{
            //    _algo.IVSurfaceAndreasenHuge[(underlying, OptionRight.Put)] = new IVSurfaceAndreasenHuge(_algo, underlying, OptionRight.Put, true);
            //}
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
        /// <summary>
        /// FIX ME: The stored historical volatilities were calculated at 0 yield. Need to adjust IV for configured yield term structure.
        /// </summary>
        /// <param name="security"></param>
        public void WarmUpSecurity(Security security)
        {
            Symbol symbol;

            _algo.Log($"SecurityInitializer.WarmUpSecurity: {security}");

            if (security.Type == SecurityType.Option)
            {
                var option = (Option)security;

                if (option.Underlying == null) return;
                symbol = option.Symbol;
                Symbol underlying = symbol.Underlying;
                _algo.IVSurfaceRelativeStrikeBid[option.Symbol.Underlying].RegisterSymbol(option);
                _algo.IVSurfaceRelativeStrikeAsk[option.Symbol.Underlying].RegisterSymbol(option);

                if (_algo.Cfg.SkipWarmUpSecurity) return;

                var volaSyms = _algo.Securities.Keys.Where(s => s.Underlying == symbol); // Only the volatilityBars have an option as Underlying.
                var volaSym = volaSyms.Any() ? volaSyms.First() : null;
                if (volaSym != null)
                {
                    DateTime end = HistoryRequestEndDate(security);
                    //var historyFast = _algo.History<VolatilityBar>(volaSym, start, end, _algo.resolution, fillForward: false);  // Zero Warm Up here, because it's covered during OnData SetWarmUp().
                    var historyFast = _algo.History<VolatilityBar>(volaSym, _algo.Periods(days: 0), _algo.resolution, fillForward: false);  // Zero Warm Up here, because it's covered during OnData SetWarmUp().
                    var historySlow = _algo.History<VolatilityBar>(volaSym, _algo.Periods(Resolution.Daily, days: 60), Resolution.Daily, fillForward: false);
                    // Need to synchronize the 2 histories. Otherwise fast day events update indicator before the slow second events, which would be ignore as no updates from past are processed by IVBid/Ask Indicator.
                    //var history = historySlow;
                    using var history = new SynchronizingVolatilityBarEnumerator(new List<IEnumerator<VolatilityBar>>() { historyFast.GetEnumerator(), historySlow.GetEnumerator() });

                    // IV Bid Ask Indicators which produce events
                    // Slow one must stop before Fast one kicks in, otherwise time updates will be ignored...
                    foreach (VolatilityBar volBar in Statics.ToIEnumerable(history))
                    //foreach (VolatilityBar volBar in history)
                    {
                        IVQuote bid = null;
                        IVQuote ask = null;

                        // Data issue. empty row is loaded.
                        if (volBar.Ask.Close == 0 && volBar.Bid.Close == 0)
                        {
                            continue;
                        }
                        // The UnderlyingPrice.Close here does not match MidPrice(Underlying)
                        bid = new IVQuote(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceBid.Close, (double)volBar.Bid.Close);
                        ask = new IVQuote(symbol, volBar.EndTime, volBar.UnderlyingPrice.Close, volBar.PriceAsk.Close, (double)volBar.Ask.Close);

                        if (bid != null)
                        {
                            _algo.IVBids[symbol].Update(bid);
                            _algo.IVAsks[symbol].Update(ask);
                        }
                    }
                }

                // IV PutCall Ratios
                var historyFastTrades = _algo.History<TradeBar>(symbol, _algo.Periods(days: _algo.Cfg.PutCallRatioWarmUpDays + 1), _algo.resolution, fillForward: false);
                foreach (TradeBar bar in historyFastTrades)
                {
                    _algo.PutCallRatios[option.Symbol].Update(bar);
                }
            }
        }
    }

}
