namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class EventNewFairOptionPrice
    {
        public Symbol Symbol { get; }
        public decimal Price { get; }

        public EventNewFairOptionPrice(Symbol symbol, decimal price)
        {
            Symbol = symbol;
            Price = price;
        }
    }
}
