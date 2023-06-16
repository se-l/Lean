using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVAsk : IIVBidAsk
    {
        public Symbol Symbol { get; }
        public DateTime Time { get; internal set; }
        public decimal UnderlyingMidPrice { get; internal set; }
        public decimal Price { get; internal set; }
        public double IV { get; internal set; }

        private Foundations algo { get; }
        private readonly OptionContractWrap ocw;

        public IVAsk(Option option, Foundations algo)
        {
            this.algo = algo;
            Symbol = option.Symbol;
            ocw = OptionContractWrap.E(algo, option);
        }

        public void Update(QuoteBar quoteBar)
        {
            if (quoteBar == null || quoteBar.Ask == null)
            {
                return;
            }
            decimal underlyingMidPrice = algo.MidPrice(Symbol.Underlying);

            Time = quoteBar.EndTime;
            UnderlyingMidPrice = underlyingMidPrice;
            Price = quoteBar.Ask.Close;
            IV = ocw.IV(Price, underlyingMidPrice, 0.1) ?? 0;
        }
    }
}
