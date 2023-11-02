using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class Signal
    {
        public Symbol Symbol { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        public UtilityOrder UtilityOrder { get; internal set; }
        public string OcaGroup { get; internal set; }
        public int OcaType { get; internal set; }

        public Signal(Symbol symbol, OrderDirection orderDirection, UtilityOrder utilityOrder, string ocaGroup = "", int ocaType = 3)
        {
            Symbol = symbol;
            OrderDirection = orderDirection;
            UtilityOrder = utilityOrder;
            OcaGroup = ocaGroup;
            OcaType = ocaType;
        }
    }
}
