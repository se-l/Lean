namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class EventNewFairOptionPrice
    {
        public Symbol Symbol;
        public decimal Price;

        public EventNewFairOptionPrice(Symbol symbol, decimal price)
        {
            Symbol = symbol;
            Price = price;
        }
    }
}
