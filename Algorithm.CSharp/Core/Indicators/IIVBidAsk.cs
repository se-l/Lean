using System;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public interface IIVBidAsk
    {
        public Symbol Symbol { get; }
        public DateTime Time { get;}
        public decimal UnderlyingMidPrice { get; }
        public decimal Price { get; }
        public double IV { get; }
    }
}
