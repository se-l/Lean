using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class Signal
    {
        public Symbol Symbol { get; }
        public OrderDirection OrderDirection { get; }
        public IUtilityOrder UtilityOrder { get; }
        public string OcaGroup { get; internal set; }
        public int OcaType { get; }

        public Signal(Symbol symbol, OrderDirection orderDirection, IUtilityOrder utilityOrder, string ocaGroup = "", int ocaType = 3)
        {
            Symbol = symbol;
            OrderDirection = orderDirection;
            UtilityOrder = utilityOrder;
            OcaGroup = ocaGroup;
            OcaType = ocaType;
        }

        public void AssignOcaGroup(string ocaGroup)
        {
            OcaGroup = ocaGroup;
        } 

        public override string ToString()
        {
            return $"Signal: {Symbol} {OrderDirection}, OcaGroup/Type={OcaGroup}/{OcaType}, Utility={UtilityOrder.Utility}";
        }
    }
}
