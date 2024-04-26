using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class Quote<T> where T : Option
    {
        private Option Option { get; }
        public Symbol Symbol { get => Option.Symbol; }
        public OrderDirection OrderDirection { get => Num2Direction(Quantity); }
        public decimal Quantity { get; internal set; }
        public decimal Price { get; internal set; }
        public double IVPrice { get; internal set; }
        public IUtilityOrder UtilityOrderHigh { get; internal set; }
        public IUtilityOrder UtilityOrderLow { get; internal set; }
        public decimal SpreadDiscount { get; internal set; }

        public Quote(Option option, decimal quantity, decimal price, double ivPrice, IUtilityOrder utilityOrderHigh, IUtilityOrder utilityOrderLow, decimal? spreadDiscount=null)
        {
            Option = option;
            Quantity = quantity;
            Price = price;
            IVPrice = ivPrice;
            UtilityOrderHigh = utilityOrderHigh;
            UtilityOrderLow = utilityOrderLow;
            SpreadDiscount = spreadDiscount ?? 0;
        }

        public override string ToString()
        {
            return $"Quote {Symbol}: Quantity={Quantity}, Price={Price}";
        }
    }
}
