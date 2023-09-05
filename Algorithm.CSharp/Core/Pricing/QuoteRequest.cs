using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class QuoteRequest<T> where T : Option
    {
        public Option Option;
        public Symbol Symbol { get => Option.Symbol; }
        public Symbol Underlying { get => Option.Symbol.Underlying; }
        public decimal Quantity;
        public OrderDirection OrderDirection { get => Num2Direction(Quantity); }

        public QuoteRequest(Option option, decimal quantity)
        {
            Option = option;
            Quantity = quantity;
        }
    }
}
