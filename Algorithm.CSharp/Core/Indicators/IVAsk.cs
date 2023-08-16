using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVAsk
    {
        public Symbol Symbol { get => Option.Symbol; }
        public Option Option { get; }
        public DateTime Time { get; set; }
        public decimal UnderlyingMidPrice { get; set; }
        public decimal Price { get; set; }
        public double IV { get; set; }
        public IVBidAsk Current { get; internal set; }

        private Foundations algo { get; }

        public IVAsk(Option option, Foundations algo)
        {
            this.algo = algo;
            Option = option;
        }

        public void Update(QuoteBar quoteBar, decimal? underlyingMidPrice = null)
        {
            if (quoteBar == null || quoteBar.Ask == null || quoteBar.EndTime <= Time)
            {
                return;
            }
            Time = quoteBar.EndTime;
            UnderlyingMidPrice = underlyingMidPrice ?? algo.MidPrice(Symbol.Underlying);
            Price = quoteBar.Ask.Close;
            IV = OptionContractWrap.E(algo, Option, 1, Time.Date).IV(Price, UnderlyingMidPrice, 0.001);
            Current = new IVBidAsk(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }

        public void Update(IVBidAsk bar)
        {
            Time = bar.Time;
            UnderlyingMidPrice = bar.UnderlyingMidPrice;
            Price = bar.Price;
            IV = bar.IV;
            Current = bar;
        }
        public void SetDelta(double? delta = null)
        {
            Current.Delta = delta == null ? OptionContractWrap.E(algo, Option, 1, Time.Date).Delta() : delta;
        }
    }
}
