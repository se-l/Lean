using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class QuoteRequest<T> where T : Option
    {
        public Option Option { get; internal set; }
        public Symbol Symbol { get => Option.Symbol; }
        public Symbol Underlying { get => Option.Symbol.Underlying; }
        public decimal Quantity { get; internal set; }
        public OrderDirection OrderDirection { get => Num2Direction(Quantity); }
        public IUtilityOrder UtilityOrder { get; internal set; }

        public QuoteRequest(Option option, decimal quantity, IUtilityOrder utilityOrder)
        {
            Option = option;
            Quantity = quantity;
            UtilityOrder = utilityOrder;
        }
    }
}
