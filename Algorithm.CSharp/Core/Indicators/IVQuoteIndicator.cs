using System;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;

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
            
            if (HaveInputsChanged(quote, midPriceUnderlying, time.Date) && iv == null)
            {
                IV = OptionContractWrap.E(_algo, Option, time.Date).IV(quote, midPriceUnderlying, 0.001);
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
            
            IVBidAsk = new IVQuote(Symbol, Time, IV);
            Current = new IndicatorDataPoint(Time, (decimal)IV);
        }

        /// <summary>
        /// IVs served pre-computed by the data feed (the R2 iceberg handler). No pricer round trip and
        /// no input-diff bookkeeping: this is the backtest and warm-up path.
        /// </summary>
        public void Update(DateTime time, double iv)
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

            IVBidAsk = new IVQuote(Symbol, Time, iv);
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
            Update(bar.Time, bar.IV);
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
        public IVQuote Refresh()
        {
            Update();
            return IVBidAsk;
        }

        /// <summary>
        /// The pricer is the IV source only when there is no pre-computed IV to read: live trading,
        /// after warm-up. Warm-up history is past data and arrives through the feed (R2 handler), and
        /// backtests read IVs from the warehouse — neither may spend a pricer round trip here.
        /// Ad-hoc and risk calculations call <see cref="Refresh"/> or the contract wrap directly and
        /// are unaffected by this gate.
        /// </summary>
        public bool UseSolver => _algo.LiveMode && !_algo.IsWarmingUp;

        protected bool HaveInputsChanged(decimal quote, decimal midPriceUnderlying, DateTime evalDate)
        {
            return quote != Price || midPriceUnderlying != MidPriceUnderlying || evalDate != EvaluationDate;
        }
    }
}
