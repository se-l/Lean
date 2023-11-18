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

        public IVQuoteIndicator(QuoteSide side, Option option, Foundations algo) : base($"IVQuoteIndicator {side} {option.Symbol}")
        {
            _side = side;
            _algo = algo;
            Option = option;
            IVBidAsk = new IVQuote(Symbol, _algo.Time, 0, 0, 0);  // Default, in case referenced downstream before any successful update.
            _algo.RegisterIndicator(Symbol, this, _algo.QuoteBarConsolidators[Symbol], Selector);
        }

        public void Update(DateTime time, decimal quote, decimal midPriceUnderlying)
        {
            if (time <= Time || quote == 0) return;
            double iv = IV;
            if (HaveInputsChanged(quote, midPriceUnderlying, time.Date))
            {
                iv = OptionContractWrap.E(_algo, Option, time.Date).IV(quote, midPriceUnderlying, 0.001);
            }
            if (iv == 0)
            {
                //_algo.Log($"{_algo.Time} IVQuoteIndicator.Update: IV=0 encountered for {Symbol} {time} P={quote}, arg S={midPriceUnderlying}, MidPrice S={_algo.MidPrice(Underlying)}. Ignoring, not updating indicator. Presumably stale / delayed / unsynced quotes.");
                return;
            }
            else
            {
                IV = iv;                
                Time = time;
                Price = quote;
                MidPriceUnderlying = midPriceUnderlying;
                _samples += 1;
            }
            
            IVBidAsk = new IVQuote(Symbol, Time, this.MidPriceUnderlying, Price, IV);
            Current = new IndicatorDataPoint(Time, (decimal)IVBidAsk.IV);
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
