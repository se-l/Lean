using QuantConnect.Orders.Fills;
using QuantConnect.Orders;
using System;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using System.Collections.Generic;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    public class FillModelMine : ImmediateFillModel
    {
        public FillModelMine() { }

        public override OrderEvent LimitFill(Security asset, LimitOrder order)
        {
            return InternalLimitFill(asset, order, order.LimitPrice, order.Quantity);
        }

        /// <summary>
        /// Default limit order fill model in the base security class.
        /// </summary>
        private OrderEvent InternalLimitFill(Security asset, Order order, decimal limitPrice, decimal quantity)
        {
            //Initialise;
            var utcTime = asset.LocalTime.ConvertToUtc(asset.Exchange.TimeZone);
            //bool aggressive = order.Time == utcTime;
            var fill = new OrderEvent(order, utcTime, OrderFee.Zero);

            //If its cancelled don't need anymore checks:
            if (order.Status == OrderStatus.Canceled) return fill;
            //if (order.Status == OrderStatus.UpdateSubmitted && order.CreatedTime == fill.UtcTime) return fill;  // Assume we dont hit the market directly. Significant speed up.

            // make sure the exchange is open before filling -- allow pre/post market fills to occur
            //if (!IsExchangeOpen(asset))
            //{
            //    return fill;
            //}
            //Get the range of prices in the last bar:
            var orderDirection = order.Direction;
            var prices = GetPrices(asset, orderDirection);
            var pricesEndTime = prices.EndTime.ConvertToUtc(asset.Exchange.TimeZone);

            // do not fill on stale data
            if (pricesEndTime <= order.Time) return fill;

            //-> Valid Live/Model Order:
            switch (orderDirection)
            {
                case OrderDirection.Buy:

                    if (prices.Low <= limitPrice
                        || (asset.AskPrice <= limitPrice)
                        )
                    {
                        //Set order fill:
                        fill.Status = OrderStatus.Filled;
                        // fill at the worse price this bar or the limit price, this allows far out of the money limits
                        // to be executed properly
                        //fill.FillPrice = Math.Min(prices.High, limitPrice);
                        fill.FillPrice = limitPrice;
                        // assume the order completely filled
                        fill.FillQuantity = quantity;
                    }
                    break;
                case OrderDirection.Sell:

                    if (prices.High > limitPrice 
                        || (prices.High == limitPrice && asset.AskPrice > limitPrice)  // better: if AskPrice was lowered after my limitPrice, I should be filled, first in order book.
                        || (asset.BidPrice >= limitPrice)
                        )
                    {
                        fill.Status = OrderStatus.Filled;
                        // fill at the worse price this bar or the limit price, this allows far out of the money limits
                        // to be executed properly
                        //fill.FillPrice = Math.Max(prices.Low, limitPrice);
                        fill.FillPrice = limitPrice;
                        // assume the order completely filled
                        fill.FillQuantity = quantity;
                    }
                    break;
            }

            return fill;
        }

        protected override Prices GetPrices(Security asset, OrderDirection direction)
        {
            var low = asset.Low;
            var high = asset.High;
            var open = asset.Open;
            var close = asset.Close;
            var current = asset.Price;
            var endTime = asset.Cache.GetData()?.EndTime ?? DateTime.MinValue;

            if (direction == OrderDirection.Hold)
            {
                return new Prices(endTime, current, open, high, low, close);
            }

            // Only fill with data types we are subscribed to
            //var subscriptionTypes = GetSubscribedTypes(asset);
            HashSet<Type> subscriptionTypes = asset.Type switch
            {
                //SecurityType.Option => new() { typeof(Tick) },
                SecurityType.Option => new() { typeof(QuoteBar), typeof(TradeBar) },
                SecurityType.Equity => new() { typeof(QuoteBar), typeof(TradeBar) },
            };

            // Tick
            var tick = asset.Cache.GetData<Tick>();
            if (tick != null && subscriptionTypes.Contains(typeof(Tick)))
            {
                var price = direction == OrderDirection.Sell ? tick.BidPrice : tick.AskPrice;
                if (price != 0m)
                {
                    return new Prices(tick.EndTime, price, 0, 0, 0, 0);
                }

                // If the ask/bid spreads are not available for ticks, try the price
                price = tick.Price;
                if (price != 0m)
                {
                    return new Prices(tick.EndTime, price, 0, 0, 0, 0);
                }
            }

            // Quote
            var quoteBar = asset.Cache.GetData<QuoteBar>();
            var tradeBar = asset.Cache.GetData<TradeBar>();

            if (tradeBar != null && quoteBar != null && tradeBar.EndTime == quoteBar.EndTime && subscriptionTypes.Contains(typeof(TradeBar)) && subscriptionTypes.Contains(typeof(QuoteBar)))
            {
                var bar = direction == OrderDirection.Sell ? quoteBar.Bid : quoteBar.Ask;
                if (bar != null)
                {
                    open = bar.Open;
                    high = Math.Max(bar.High, tradeBar.High);  // no mention whether it's a sell or buy trade. Needs better logic.
                    low = Math.Min(bar.Low, tradeBar.Low);
                    close = bar.Close;
                }
            }

            else if (quoteBar != null && subscriptionTypes.Contains(typeof(QuoteBar)))
            {
                var bar = direction == OrderDirection.Sell ? quoteBar.Bid : quoteBar.Ask;
                if (bar != null)
                {
                    return new Prices(quoteBar.EndTime, bar);
                }
            }

            // Trade
            else if (tradeBar != null && subscriptionTypes.Contains(typeof(TradeBar)))
            {
                return new Prices(tradeBar);  // getting trade ticks that are better fills than quote bar. unrealistically good fills.
            }

            return new Prices(endTime, current, open, high, low, close);
        }
    }
}
