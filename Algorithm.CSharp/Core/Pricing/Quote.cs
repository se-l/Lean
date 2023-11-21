using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class Quote<T> where T : Option
    {
        public Option Option;
        public Symbol Symbol { get => Option.Symbol; }
        public OrderDirection OrderDirection { get => Num2Direction(Quantity); }
        public decimal Quantity;
        public decimal Price { get; internal set; }
        public double IVPrice { get; internal set; }
        public IEnumerable<QuoteDiscount> QuoteDiscounts;
        public string QuoteDiscountsString { get => string.Join(",", QuoteDiscounts.Select(qd => qd.ToString())); }
        public double SpreadFactor { get; internal set; }
        public UtilityOrder UtilityOrder;

        public Quote(Option option, decimal quantity, decimal price, double ivPrice, IEnumerable<QuoteDiscount> quoteDiscounts, UtilityOrder utilityOrder)
        {
            Option = option;
            Quantity = quantity;
            Price = price;
            IVPrice = ivPrice;
            QuoteDiscounts = quoteDiscounts;
            SpreadFactor = quoteDiscounts.Sum(qd => qd.SpreadFactor);
            UtilityOrder = utilityOrder;
        }

        public override string ToString()
        {
            return $"Quote {Symbol}: Quantity={Quantity}, Price={Price}, QuoteDiscounts={QuoteDiscountsString}";
        }
    }
}
