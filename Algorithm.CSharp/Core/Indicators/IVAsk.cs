using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVAsk
    {
        public Symbol Symbol { get; }
        public DateTime Time { get; set; }
        public decimal UnderlyingMidPrice { get; set; }
        public decimal Price { get; set; }
        public double IV { get; set; }
        public IVBidAsk Current { get; internal set; }

        private Foundations algo { get; }
        private readonly OptionContractWrap ocw;

        public IVAsk(Option option, Foundations algo)
        {
            this.algo = algo;
            Symbol = option.Symbol;
            ocw = OptionContractWrap.E(algo, option);
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
            IV = ocw.IV(Price, UnderlyingMidPrice, 0.001) ?? 0;
            Current = new IVBidAsk(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }
    }
}
