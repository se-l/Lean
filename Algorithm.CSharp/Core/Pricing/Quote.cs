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
        public IEnumerable<QuoteDiscount> QuoteDiscounts;
        public string QuoteDiscountsString { get => string.Join(",", QuoteDiscounts.Select(qd => qd.ToString())); }
        public double SpreadFactor { get; internal set; }

        public Quote(Option option, decimal quantity, decimal price, IEnumerable<QuoteDiscount> quoteDiscounts)
        {
            Option = option;
            Quantity = quantity;
            Price = price;
            QuoteDiscounts = quoteDiscounts;
            SpreadFactor = quoteDiscounts.Sum(qd => qd.SpreadFactor);
        }

        public override string ToString()
        {
            return $"Quote {Symbol}: Quantity={Quantity}, Price={Price}, QuoteDiscounts={QuoteDiscountsString}";
        }
    }
}
