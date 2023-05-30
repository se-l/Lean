namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class EventNewBidAsk
    {
        public Symbol Symbol;

        public EventNewBidAsk(Symbol symbol)
        {
            Symbol = symbol;
        }
    }
}
