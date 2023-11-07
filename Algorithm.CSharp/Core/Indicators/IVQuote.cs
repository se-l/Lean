using System;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVQuote: EventArgs
    {
        public Symbol Symbol { get; }
        public DateTime Time { get; }
        public decimal UnderlyingMidPrice { get; }
        public decimal Price { get; }
        public double IV { get; }
        public double? Delta { get; set; }


        public IVQuote(Symbol symbol, DateTime time, decimal underlyingMidPrice, decimal price, double iv)
        {
            Symbol = symbol;
            Time = time;
            UnderlyingMidPrice = underlyingMidPrice;
            Price = price;
            IV = iv;
        }
    }
}
