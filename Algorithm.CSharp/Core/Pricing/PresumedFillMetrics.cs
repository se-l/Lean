using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    class PresumedFillMetrics
    {
        public OrderDirection Direction { get; set; }
        public double IV { get; set; }
        public decimal PresumedFillPrice { get; set; }
        public decimal DiscountedPrice { get; set; }
        public decimal Discount { get; set; }

        public PresumedFillMetrics(QuoteRequest<Option> qr, Foundations algo)
        {
            Direction = qr.OrderDirection;
            IV = algo.PresumedFillIV[qr.Option];
            OptionContractWrap ocw = OptionContractWrap.E(algo, qr.Option, algo.Time.Date);
            PresumedFillPrice = (decimal)ocw.NPV(IV, algo.MidPrice(qr.Underlying));
            Discount = 0; // Math.Abs((decimal)qr.UtilityOrder.Utility / qr.Quantity) / (4*100);  too untested
            DiscountedPrice = qr.OrderDirection switch
            {
                OrderDirection.Buy => PresumedFillPrice + Discount,
                OrderDirection.Sell => PresumedFillPrice - Discount,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            
        }
    }
}
