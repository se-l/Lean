using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVBid : IIVBidAsk
    {
        public Symbol Symbol { get; }
        public DateTime Time { get; internal set; }
        public decimal UnderlyingMidPrice { get; internal set; }
        public decimal Price { get; internal set; }
        public double IV { get; internal set; }

        private Foundations algo { get; }
        private readonly OptionContractWrap ocw;

        public IVBid(Option option, Foundations algo, double iv = 0)
        {
            this.algo = algo;
            Symbol = option.Symbol;
            ocw = OptionContractWrap.E(algo, option);
            Time = algo.Time;
            UnderlyingMidPrice = algo.MidPrice(Symbol.Underlying);
            Price = option.BidPrice;
            IV = iv;
        }

        public void Update(QuoteBar quoteBar)
        {
            if (quoteBar == null || quoteBar.Bid == null)
            {
                return;
            }
            Time = quoteBar.EndTime;
            UnderlyingMidPrice = algo.MidPrice(Symbol.Underlying);
            Price = quoteBar.Bid.Close;
            IV = ocw.IV(Price, UnderlyingMidPrice, 0.1) ?? 0;
        }
    }
}
