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
        private readonly QuoteSide _side;
        public QuoteSide Side { get => _side; }
        public Symbol Symbol { get => Option.Symbol; }
        public Symbol Underlying { get => Option.Underlying.Symbol; }
        public Option Option { get; }
        public DateTime Time { get; set; }
        public DateTime EvaluationDate { get; internal set; }
        public decimal MidPriceUnderlying { get; set; }
        public decimal Price { get; set; }
        public double IV { get; set; }
        public IVQuote IVBidAsk { get; internal set; }
        private decimal GetQuote(QuoteBar quoteBar) => _side switch
        {
            QuoteSide.Bid => quoteBar.Bid.Close,
            QuoteSide.Ask => quoteBar.Ask.Close,
        };

        protected readonly Foundations _algo;

        public int WarmUpPeriod => 0;

        public Func<IBaseData, decimal> Selector { get {
                return Side switch
                {
                    QuoteSide.Bid => (IBaseData b) => ((QuoteBar)b).Bid.Close,
                    QuoteSide.Ask => (IBaseData b) => ((QuoteBar)b).Ask.Close,
                };
            }
        }

        public override bool IsReady => Current != null;

        public IVQuoteIndicator(QuoteSide side, Option option, Foundations algo) : base($"IV {side} {option.Symbol}")
        {
            _side = side;
            _algo = algo;
            Option = option;
            algo.RegisterIndicator(Symbol, this, algo.QuoteBarConsolidators[Symbol], Selector);
        }

        public void Update(DateTime time, decimal quote, decimal midPriceUnderlying)
        {
            if (time <= Time || quote == 0) { return; }

            Time = time;
            if (HaveInputsChanged(quote, midPriceUnderlying, Time.Date))
            {
                MidPriceUnderlying = midPriceUnderlying;
                Price = quote;
                IV = OptionContractWrap.E(_algo, Option, 1, Time.Date).IV(Price, MidPriceUnderlying, 0.001);
            }
            IVBidAsk = new IVQuote(Symbol, Time, MidPriceUnderlying, Price, IV);
            Current = new IndicatorDataPoint(Time, (decimal)IVBidAsk.IV);
        }
        public void Update(QuoteBar quoteBar, decimal? underlyingMidPrice = null)
        {
            if (quoteBar == null || quoteBar.EndTime <= Time) { return; }
            Update(quoteBar.EndTime, GetQuote(quoteBar), underlyingMidPrice ?? _algo.MidPrice(Symbol.Underlying));
        }

        public void Update(IVQuote bar)
        {
            Update(bar.Time, bar.Price, bar.UnderlyingMidPrice);
        }
        public void Update()
        {
            Update(
                _algo.Time,
                Side == QuoteSide.Bid ? Option.BidPrice : Option.AskPrice,
                _algo.MidPrice(Underlying)
                );
        }
        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (input.Time <= Time) { return Current; }
            Update(input.Time, input.Value, _algo.MidPrice(Symbol.Underlying));
            return (decimal)IVBidAsk.IV;
        }
        public IVQuote Refresh()
        {
            Update();
            return IVBidAsk;
        }

        protected bool HaveInputsChanged(decimal quote, decimal midPriceUnderlying, DateTime evalDate)
        {
            return quote != Price || midPriceUnderlying != MidPriceUnderlying || evalDate != EvaluationDate;
        }
    }
}
