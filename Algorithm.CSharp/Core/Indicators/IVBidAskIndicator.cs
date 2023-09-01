using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVBidAskIndicator : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        public QuoteSide BidAsk;
        public Symbol Symbol { get => Option.Symbol; }
        public Symbol Underlying { get => Option.Underlying.Symbol; }
        public Option Option { get; }
        public DateTime Time { get; set; }
        public DateTime EvaluationDate { get; internal set; }
        public decimal MidPriceUnderlying { get; set; }
        public decimal Price { get; set; }
        public double IV { get; set; }
        public IVBidAsk IVBidAsk { get; internal set; }

        protected readonly Foundations _algo;

        public int WarmUpPeriod => 0;

        public Func<IBaseData, decimal> Selector { get {
                return BidAsk switch
                {
                    QuoteSide.Bid => (IBaseData b) => ((QuoteBar)b).Bid.Close,
                    QuoteSide.Ask => (IBaseData b) => ((QuoteBar)b).Ask.Close,
                };
            }
        }

        public override bool IsReady => Current != null;

        public IVBidAskIndicator(QuoteSide bidAsk, Option option, Foundations algo) : base($"IV {bidAsk} {option.Symbol}")
        {
            BidAsk = bidAsk;
            _algo = algo;
            Option = option;
            algo.RegisterIndicator(Symbol, this, algo.QuoteBarConsolidators[Symbol], Selector);
            if (bidAsk == QuoteSide.Bid)
            {   
                Updated += (s, e) => algo.RollingIVStrikeBid[Underlying].Update(IVBidAsk);
            }
            else if (bidAsk == QuoteSide.Ask)
            {
                Updated += (s, e) => algo.RollingIVStrikeAsk[Underlying].Update(IVBidAsk);
            }
        }

        public virtual void Update(QuoteBar quoteBar, decimal? underlyingMidPrice = null) 
        {
            throw new NotImplementedException();
        }

        public void Update(IVBidAsk bar)
        {
            if (bar.Time <= Time) { return; }

            Time = bar.Time;
            decimal midPriceUnderlying = bar.UnderlyingMidPrice;
            decimal quote = bar.Price;
            if (HaveInputsChanged(quote, midPriceUnderlying, Time.Date))
            {
                MidPriceUnderlying = midPriceUnderlying;
                Price = quote;
                IV = bar.IV;
            }
            IVBidAsk = bar;
            Current = new IndicatorDataPoint(Time, (decimal)IVBidAsk.IV);
        }

        public IVBidAsk Refresh()
        {
            Price = Option.AskPrice;
            MidPriceUnderlying = _algo.MidPrice(Symbol.Underlying);
            IV = OptionContractWrap.E(_algo, Option, 1, Time.Date).IV(Price, MidPriceUnderlying, 0.001);
            IVBidAsk = new IVBidAsk(Symbol, Time, MidPriceUnderlying, Price, IV);
            return IVBidAsk;
        }

        protected bool HaveInputsChanged(decimal quote, decimal midPriceUnderlying, DateTime evalDate)
        {
            return quote != Price || midPriceUnderlying != MidPriceUnderlying || evalDate != EvaluationDate;
        }

        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (input.Time <= Time) { return Current; }

            Time = input.Time;
            decimal midPriceUnderlying = _algo.MidPrice(Symbol.Underlying);
            if (HaveInputsChanged(input.Value, midPriceUnderlying, Time.Date))
            {
                MidPriceUnderlying = midPriceUnderlying;
                Price = input.Value;
                IV = OptionContractWrap.E(_algo, Option, 1, Time.Date).IV(Price, MidPriceUnderlying, 0.001);
            }
            IVBidAsk = new IVBidAsk(Symbol, Time, MidPriceUnderlying, Price, IV);
            return (decimal)IVBidAsk.IV;
        }
    }
}
