using QuantConnect.Orders;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class EventSignal
   {
        public class Signal
        {
            public Symbol Symbol;
            public OrderDirection OrderDirection;
            public bool PfRiskReviewed;

            public Signal(Symbol symbol, OrderDirection orderDirection, bool pfRiskReviewed = false)
            {
                Symbol = symbol;
                OrderDirection = orderDirection;
                PfRiskReviewed = pfRiskReviewed;
            }
        }

        public class EventSignals
        {
            public List<Signal> Signals;

            public EventSignals(List<Signal> signals)
            {
                Signals = signals;
            }
        }
    }
}
