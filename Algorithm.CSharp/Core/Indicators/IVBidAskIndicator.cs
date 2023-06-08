using System;
using System.Collections.Generic;
using System.Linq;
using Accord.MachineLearning.Performance;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVBidAsk
    {
        public DateTime Time { get; }
        public decimal UnderlyingMidPrice { get; }
        public decimal BidPrice { get; }
        public decimal AskPrice { get; }
        public double BidIV { get; }
        public double AskIV { get; }

        public IVBidAsk(DateTime time, decimal underlyingMidPrice, decimal bidPrice, decimal askPrice, double bidIV, double askIV)
        {
            Time = time;
            UnderlyingMidPrice = underlyingMidPrice;
            BidPrice = bidPrice;
            AskPrice = askPrice;
            BidIV = bidIV;
            AskIV = askIV;
        }
    }
    public class IVBidAskIndicator : IndicatorBase<Tick>, IIndicatorWarmUpPeriodProvider
    {
        public new IVBidAsk Current { get; protected set; }
        public Symbol Symbol { get; }
        public List<IVBidAsk> Window { get; }
        public override bool IsReady => Window.Any();
        public int WarmUpPeriod => 1;

        private Foundations algo;
        private readonly OptionContractWrap ocw;
        private DateTime lastUpated { get; set; }

        public IVBidAskIndicator(Symbol symbol, Foundations algo, Option option) : base(symbol.Value + "IVBidAsk")
        {
            Symbol = symbol;
            Window = new List<IVBidAsk>();
            this.algo = algo;
            ocw = OptionContractWrap.E(algo, option);
        }

        public void Update(Tick tick)
        {
            if (tick.Time <= lastUpated)
            {
                return;
            }
            decimal underlyingMidPrice = algo.MidPrice(Symbol.Underlying);

            Window.Add(new IVBidAsk(
                tick.Time,
                underlyingMidPrice,
                tick.BidPrice,
                tick.AskPrice,
                bidIV: ocw.IV(tick.BidPrice, underlyingMidPrice, 0.01) ?? 0,
                askIV: ocw.IV(tick.AskPrice, underlyingMidPrice, 0.01) ?? 0
                )
                );
            lastUpated = tick.Time.RoundUp(TimeSpan.FromSeconds(1));
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
