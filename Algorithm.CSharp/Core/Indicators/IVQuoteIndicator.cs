using System;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVQuoteIndicator : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        private const double DecimalMaxAsDouble = 79228162514264337593543950335d;
        private readonly QuoteSide _side;
        public QuoteSide Side { get => _side; }
        public Symbol Symbol { get => Option.Symbol; }
        public Symbol Underlying { get => Option.Underlying.Symbol; }
        public Option Option { get; }
        public DateTime Time { get; set; }
        public DateTime EvaluationDate { get; internal set; }
        private decimal MidPriceUnderlying { get; set; }
        private decimal Price { get; set; }
        private double IV { get; set; }
        public IVQuote IVBidAsk { get; internal set; }
        private int _samples;
        public int Samples { get => _samples; }
        private decimal GetQuote(QuoteBar quoteBar) => _side switch
        {
            QuoteSide.Bid => quoteBar.Bid.Close,
            QuoteSide.Ask => quoteBar.Ask.Close,
        };

        private readonly Foundations _algo;

        public int WarmUpPeriod => 0;

        public Func<IBaseData, decimal> Selector { get {
                return Side switch
                {
                    QuoteSide.Bid => (IBaseData b) => ((QuoteBar)b)?.Bid?.Close ?? 0,
                    QuoteSide.Ask => (IBaseData b) => ((QuoteBar)b)?.Ask?.Close ?? 0,
                };
            }
        }

        public override bool IsReady => _samples > 0;

        private static bool CanConvertToDecimal(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value) && value <= DecimalMaxAsDouble && value >= -DecimalMaxAsDouble;
        }

        public IVQuoteIndicator(QuoteSide side, Option option, Foundations algo) : base($"IVQuoteIndicator {side} {option.Symbol}")
        {
            _side = side;
            _algo = algo;
            Option = option;
            IVBidAsk = default(IVQuote);  // Default, in case referenced downstream before any successful update.
        }

        public void Update(DateTime time, decimal quote, decimal midPriceUnderlying, double? iv = null)
        {
            if (time <= Time || quote == 0) return;

            double feedMidIv = 0;
            if (HaveInputsChanged(quote, midPriceUnderlying, time.Date) && iv == null)
            {
                if (_algo.PricerCacheEnabled && TryCacheIV(quote, midPriceUnderlying, time))
                {
                    // Cache hit — takes priority over the feed IV when the cache plane is on:
                    // that is the path under test (the feeder's solved IV is what the greeks are
                    // computed at, so serving the greeks from the same node requires this IV).
                    // During warmup TryCacheIV publishes-only and returns false, falling through.
                    _algo.TryGetFeedIVMid(Symbol, out feedMidIv);
                }
                else if (_algo.TryGetFeedIV(Symbol, Side, out double feedIv))
                {
                    IV = feedIv;
                    // iv_mid rides on the same timestamp as the side IVs (see TryGetFeedIVMid);
                    // carry it through so MidIV can prefer it over averaging the sides.
                    _algo.TryGetFeedIVMid(Symbol, out feedMidIv);
                }
                else
                {
                    // Expected in live mode: there is no precomputed feed IV by design — the
                    // pricer solver IS the source. Not logged; it fired per contract per minute.
                    IV = OptionContractWrap.E(_algo, Option, time.Date).IV(quote, midPriceUnderlying, 0.001);
                }
            }

            if (iv != null)
            {
                IV = iv.Value;
            }

            if (!CanConvertToDecimal(IV))
            {
                _algo.Log($"{_algo.Time} IVQuoteIndicator.Update: Invalid IV encountered for {Symbol}. IV={IV}, Quote={quote}, UnderlyingMid={midPriceUnderlying}");
                return;
            }

            Time = time;
            Price = quote;
            MidPriceUnderlying = midPriceUnderlying;
            _samples += 1;
            
            IVBidAsk = new IVQuote(Symbol, Time, IV, feedMidIv);
            Current = new IndicatorDataPoint(Time, (decimal)IV);
        }

        /// <summary>
        /// IVs served pre-computed by the data feed (the R2 iceberg handler). No pricer round trip and
        /// no input-diff bookkeeping: this is the backtest and warm-up path. iv_mid arrives on the
        /// same timestamp as iv and is carried along in <see cref="IVBidAsk.IVMid"/>; 0 means absent.
        /// </summary>
        public void Update(DateTime time, double iv, double ivMid = 0)
        {
            if (time <= Time) return;

            if (!CanConvertToDecimal(iv))
            {
                _algo.Log($"{_algo.Time} IVQuoteIndicator.Update: Invalid IV encountered for {Symbol}. IV={iv}");
                return;
            }

            Time = time;
            // Prices are not carried by the warehouse feed; make sure a later solver call sees the
            // inputs as changed rather than reusing the previous quote's IV.
            Price = 0;
            MidPriceUnderlying = 0;
            _samples += 1;

            IVBidAsk = new IVQuote(Symbol, Time, iv, ivMid);
            Current = new IndicatorDataPoint(Time, (decimal)iv);
        }
        public void Update(QuoteBar quoteBar, decimal? underlyingMidPrice = null)
        {
            if (quoteBar == null || quoteBar.EndTime <= Time) { return; }
            if (quoteBar.Bid == null || quoteBar.Ask == null) 
            {
                _algo.Log($"{_algo.Time} IVQuoteIndicator.Update: Missing Bid/Ask encountered for {quoteBar.Symbol} {quoteBar.EndTime} {quoteBar.Bid} {quoteBar.Ask}");
                return;
            }
            Update(quoteBar.EndTime, GetQuote(quoteBar), underlyingMidPrice ?? _algo.MidPrice(Symbol.Underlying));
        }

        public void Update(IVQuote bar)
        {
            if (bar.Time <= Time) return;
            Update(bar.Time, bar.IV, bar.IVMid);
        }
        public void Update()
        {
            if (_algo.Time <= Time) return;

            Update(
                _algo.Time,
                Side == QuoteSide.Bid ? Option.BidPrice : Option.AskPrice,
                _algo.MidPrice(Underlying)
                );
        }
        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (!UseSolver) { return Current; }
            if (input.Time <= Time) { return Current; }
            Update(input.Time, input.Value, _algo.MidPrice(Symbol.Underlying));
            return CanConvertToDecimal(IVBidAsk.IV) ? (decimal)IVBidAsk.IV : Current.Value;
        }

        /// <summary>
        /// The pricer-cache plane lookup: serve the side IV from the MQ-fed greek book; on a miss,
        /// publish the request so the feeder warms that node — the same request is a cache hit
        /// next time. The ZMQ fallback below (the existing solver call) stays the source of truth
        /// whenever the book cannot answer.
        /// </summary>
        private bool TryCacheIV(decimal quote, decimal midPriceUnderlying, DateTime time)
        {
            var cache = (_algo as Earnings.EarningsAlgorithm)?.PricerCache;
            if (cache == null) return false;

            var calcTimeUtc = new DateTimeOffset(time.Date.AddHours(9.5), TimeSpan.Zero).ToUnixTimeSeconds();

            var t = ToTenor(Option.Expiry, time);   // same tenor base as OptionContractWrap
            var iv = cache.TryLookupIV(
                Underlying.Value, Option.Right == OptionRight.Call,
                (double)Option.StrikePrice, t,
                (double)midPriceUnderlying, (double)quote);
            if (iv.HasValue)
            {
                IV = iv.Value;
                return true;
            }

            // Miss → warm the feeder (non-blocking; never blocks a Lean call on MQ).
            cache.PublishRequest(
                Underlying.Value, Option.Right == OptionRight.Call,
                (double)Option.StrikePrice, t,
                (double)midPriceUnderlying, (double)quote, calcTimeUtc);
            return false;
        }
        public IVQuote Refresh()
        {
            Update();
            return IVBidAsk;
        }

        /// <summary>
        /// The pricer is the IV source only when there is no pre-computed IV to read: after warm-up
        /// in EITHER mode when the pricer cache misses (or is disabled). Warm-up history is past
        /// data and arrives through the feed (R2 handler), and backtests read IVs from the
        /// warehouse — neither may spend a ZMQ round trip for a served IV. Note this gate is
        /// deliberately live-only for the SOLVER fallback (backtest post-warmup uses the ZMQ
        /// solver only via the explicit cache-miss path, or feed IVs); the pricer-CACHE gate is
        /// warmup-based, not mode-based. Ad-hoc and risk calculations call <see cref="Refresh"/>
        /// or the contract wrap directly and are unaffected by this gate.
        /// </summary>
        public bool UseSolver => _algo.LiveMode && !_algo.IsWarmingUp;

        protected bool HaveInputsChanged(decimal quote, decimal midPriceUnderlying, DateTime evalDate)
        {
            return quote != Price || midPriceUnderlying != MidPriceUnderlying || evalDate != EvaluationDate;
        }
    }
}
