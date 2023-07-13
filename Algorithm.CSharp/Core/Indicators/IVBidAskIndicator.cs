using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVBidAskIndicator
    {
        public DateTime Time { get; }
        public decimal UnderlyingMidPrice { get; }
        public decimal BidPrice { get; }
        public decimal AskPrice { get; }
        public double BidIV { get; }
        public double AskIV { get; }

        public IVBidAskIndicator(DateTime time, decimal underlyingMidPrice, decimal bidPrice, decimal askPrice, double bidIV, double askIV)
        {
            Time = time;
            UnderlyingMidPrice = underlyingMidPrice;
            BidPrice = bidPrice;
            AskPrice = askPrice;
            BidIV = bidIV;
            AskIV = askIV;
        }
    }
    public class IVBidIndicator : IndicatorBase<Tick>, IIndicatorWarmUpPeriodProvider
    {
        public new IVBidAskIndicator Current { get; protected set; }
        public Symbol Symbol { get; }
        public List<IVBidAskIndicator> Window { get; }
        public override bool IsReady => Window.Any();
        public int WarmUpPeriod => 1;

        private Foundations algo;
        private readonly OptionContractWrap ocw;
        private DateTime lastUpated { get; set; }

        public IVBidIndicator(Symbol symbol, Foundations algo, Option option) : base(symbol.Value + "IVBidAsk")
        {
            Symbol = symbol;
            Window = new List<IVBidAskIndicator>();
            this.algo = algo;
            ocw = OptionContractWrap.E(algo, option);
        }

        public void Update(Tick tick)
        {
            // For performance reasons, need to round the prices to cache better. Calculating >1,000 IVs per asset going towards 300k...
            // Or interpolate? Might be faster...
            // Rounding underlying price to 0.1% of its price in the caching function will yield approximations
            // Reduce accuracy to 0.1 for faster convergence...

            if (tick.Time <= lastUpated)
            {
                return;
            }
            decimal underlyingMidPrice = algo.MidPrice(Symbol.Underlying);

            Window.Add(new IVBidAskIndicator(
                tick.Time,
                underlyingMidPrice,
                tick.BidPrice,
                tick.AskPrice,
                bidIV: ocw.IV(tick.BidPrice, underlyingMidPrice, 0.001) ?? 0,
                askIV: ocw.IV(tick.AskPrice, underlyingMidPrice, 0.001) ?? 0
            )
                );
            lastUpated = tick.Time.RoundUp(TimeSpan.FromSeconds(1));
            Current = Window.Last();
        }

        public void Update(QuoteBar quoteBar)
        {
            if (quoteBar == null || quoteBar.Ask == null || quoteBar.Bid == null)
            {
                return;
            }
            decimal underlyingMidPrice = algo.MidPrice(Symbol.Underlying);

            Window.Add(new IVBidAskIndicator(
                quoteBar.EndTime,
                underlyingMidPrice,
                quoteBar.Bid.Close,
                quoteBar.Ask.Close,
                bidIV: ocw.IV(quoteBar.Bid.Close, underlyingMidPrice, 0.001) ?? 0,
                askIV: ocw.IV(quoteBar.Ask.Close, underlyingMidPrice, 0.001) ?? 0
            )
                );
            Current = Window.Last();
        }

        public override void Reset()
        {
            Window.Clear();
            base.Reset();
        }

        protected override decimal ComputeNextValue(Tick tick)
        {
            return 0m;
        }
    }
}
