using QuantConnect.Orders;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class TradesCumulative : Trade
    {
        private Trade? closingTrade;
        public decimal Position { get; set; }
        public decimal OrderQuantity { get; internal set; }
        public override decimal Quantity { get => Position; }
        public override DateTime Ts1 { get => closingTrade?.Ts0 ?? base.Ts1; }
        public override decimal P1 { get => closingTrade?.P0 ?? base.P1; }
        public override double IVPrice1 { get => closingTrade?.IVPrice0 ?? base.IVPrice1; }
        public override decimal Bid1 { get => closingTrade?.Bid0 ?? base.Bid1; }
        public override decimal Ask1 { get => closingTrade?.Ask0 ?? base.Ask1; }
        public override double IVBid1 { get => closingTrade?.IVBid0 ?? base.IVBid1; }
        public override double IVAsk1 { get => closingTrade?.IVAsk0 ?? base.IVAsk1; }
        public override decimal Bid1Underlying { get => closingTrade?.Bid0Underlying ?? base.Bid1Underlying; }
        public override decimal Ask1Underlying { get => closingTrade?.Ask0Underlying ?? base.Ask1Underlying; }

        public TradesCumulative(Foundations algo, Order order, Trade? closingTrade = null, decimal? position = null) : base(algo, order)
        {
            // if openingTrade provided, then this is a closing trade and its Ts1 should equal TS0 as well as all Ts1 variables.
            // if closingTrade provided, then this is an opening trade and its Ts1 should equal TS0 of closing trade and all its Ts1 variables should equal closing Trades Ts1 variables.
            // if position null, this will be an opening order. if position non-zero, positon overrides quantity and this is constitues a closing order (position=0) or a closing/opening trade (has openingOrder and position>0).
            this.closingTrade = closingTrade;
            OrderQuantity = order.Quantity;
            Position = position ?? OrderQuantity;
        }
        
        public static IEnumerable<TradesCumulative> Cumulative(Foundations algo)
        {
            List<TradesCumulative> cumulativePositions = new();
            var orders = algo.Transactions.GetOrders().Where(o => o.Status == OrderStatus.PartiallyFilled || o.Status == OrderStatus.Filled).ToList();

            //var dummyOrders = new List<Order>();
            //foreach (Order order in orders.Where(o => o.Tag.Contains("Assignment")))
            //{
            //    // Add dummy trade to account for receiving stock after assignment
            //    var dummyQuantity = -1 * OptionRight2Int[order.Symbol.ID.OptionRight] * order.Quantity * 100;
            //    OptionExerciseOrder dummyEquityOrder = new(order.Symbol.ID.Underlying.Symbol, dummyQuantity, order.LastFillTime ?? order.Time, "Dummy Order Equity")
            //    {
            //        Status = OrderStatus.Filled
            //    };
            //    dummyOrders.Add(dummyEquityOrder);
            //}
            //orders.AddRange(dummyOrders);

            //orders.Sort((Order o1, Order o2) => (int)((o1.LastFillTime ?? o1.Time) - (o2.LastFillTime ?? o2.Time)).TotalSeconds);

            foreach (var group in orders.GroupBy(o => o.Symbol))
            {
                decimal position = 0;
                Order prevOrder = null;
                foreach (Order order in group.OrderBy(o => o.LastFillTime ?? o.Time))
                {
                    position += order.Quantity;
                    if (prevOrder == null)
                    {
                        // Starting deal
                        cumulativePositions.Add(new TradesCumulative(algo, order));
                    }
                    else
                    {
                        // Every following deal
                        var currentTrade = new TradesCumulative(algo, order, position: position);
                        var prevPosition = cumulativePositions[cumulativePositions.Count - 1];

                        // Updating previous trade, given this order is the previous' closing order
                        cumulativePositions[cumulativePositions.Count - 1] = new TradesCumulative(
                            algo,
                            prevOrder,
                            closingTrade: currentTrade,
                            position: prevPosition.Quantity
                            );
                        cumulativePositions.Add(currentTrade);
                    }
                    prevOrder = order;
                }
            }
            return cumulativePositions;
        }
    }
}
