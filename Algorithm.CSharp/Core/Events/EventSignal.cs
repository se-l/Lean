using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class Signal
    {
        public Symbol Symbol { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        public UtilityOrder UtilityOrder { get; internal set; }

        public Signal(Symbol symbol, OrderDirection orderDirection, UtilityOrder utilityOrder)
        {
            Symbol = symbol;
            OrderDirection = orderDirection;
            UtilityOrder = utilityOrder;
        }
    }
}
