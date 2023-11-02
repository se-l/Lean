/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Orders
{
    /// <summary>
    /// Sometimes referred to as Relative to Stock, a pegged to stock order specifies that the option price will adjust automatically relative to the stock price, using a calculated value based on data you enter.
    /// https://www.interactivebrokers.com/en/trading/orders/pegged-to-stock.php
    /// https://www.ibkrguides.com/tws/usersguidebook/ordertypes/pegged%20to%20stock.htm
    /// </summary>
    public class PeggedToStockOrder : Order
    {
        /// <summary>
        /// Order Type
        /// </summary>
        public override OrderType Type => OrderType.PeggedToStock;

        /// <summary>
        /// the Delta field, enter an absolute value which will be used as a percent, eg. "5" equals 5%. 
        /// This percent value is signed positive for calls and negative for puts.This value is multiplied by the change in the underlying stock price, and the product is added to the starting price to determine the option price.
        /// </summary>
        public decimal Delta { get; internal set; }

        /// <summary>
        /// The Starting Price for the option order is the midpoint of the option NBBO at the time of the order. You can change this value if desired. <see cref="StartingPrice"/>.
        /// </summary>
        public decimal? StartingPrice { get; internal set; }
        public decimal StartingPriceInternal { get; set; }
        public decimal? StockRefPrice { get; internal set; }
        public decimal StockRefPriceInternal { get; set; }
        public decimal? UnderlyingRangeLow { get; internal set; }
        public decimal? UnderlyingRangeHigh { get; internal set; }

        /// <summary>
        /// New <see cref="PeggedToStockOrder"/> constructor.
        /// </summary>
        /// <param name="symbol">Symbol asset we're seeking to trade</param>
        /// <param name="quantity">Quantity of the asset we're seeking to trade</param>
        /// <param name="delta">Delta in %</param>
        /// <param name="startingPrice"
        /// <param name="stockRefPrice"
        /// <param name="underlyingRangeLow"
        /// <param name="underlyingRangeHigh"
        /// <param name="time">Time the order was placed</param>
        /// <param name="tag">User defined data tag for this order</param>
        /// <param name="properties">The order properties for this order</param>
        public PeggedToStockOrder(
            Symbol symbol,
            decimal quantity,
            decimal delta,
            decimal? startingPrice,
            decimal? stockRefPrice,
            decimal? underlyingRangeLow,
            decimal? underlyingRangeHigh,
            DateTime time,
            string tag = "",
            IOrderProperties properties = null
            )
            : base(symbol, quantity, time, tag, properties)
        {
            Delta = delta;
            StartingPrice = startingPrice;
            StartingPriceInternal = StartingPrice ?? 0;
            StockRefPrice = stockRefPrice;
            StockRefPriceInternal = stockRefPrice ?? 0;
            UnderlyingRangeLow = underlyingRangeLow;
            UnderlyingRangeHigh = underlyingRangeHigh;
            if (string.IsNullOrEmpty(tag))
            {
                //Default tag values to display trigger price in GUI.
                Tag = Messages.PeggedToStockOrder.Tag(this);
            }
        }

        /// <summary>
        /// Default constructor for JSON Deserialization:
        /// </summary>
        public PeggedToStockOrder()
        {
        }

        /// <summary>
        /// Modifies the state of this order to match the update request
        /// </summary>
        /// <param name="request">The request to update this order object</param>
        public override void ApplyUpdateOrderRequest(UpdateOrderRequest request)
        {
            base.ApplyUpdateOrderRequest(request);
            if (request.Delta.HasValue)
            {
                Delta = request.Delta.Value;
            }

            if (request.StartingPrice.HasValue)
            {
                StartingPrice = request.StartingPrice.Value;
            }

            if (request.StockReferencePrice.HasValue)
            {
                StockRefPrice = request.StockReferencePrice.Value;
            }

            if (request.UnderlyingRangeLow.HasValue)
            {
                UnderlyingRangeLow = request.UnderlyingRangeLow.Value;
            }

            if (request.UnderlyingRangeHigh.HasValue)
            {
                UnderlyingRangeHigh = request.UnderlyingRangeHigh.Value;
            }
        }

        /// <summary>
        /// Creates a deep-copy clone of this order
        /// </summary>
        /// <returns>A copy of this order</returns>
        public override Order Clone()
        {
            var order = new PeggedToStockOrder
                {Delta = Delta, StartingPrice = StartingPrice, StockRefPrice = StockRefPrice, UnderlyingRangeLow=UnderlyingRangeLow, UnderlyingRangeHigh=UnderlyingRangeHigh};
            CopyTo(order);
            return order;
        }

        /// <summary>
        /// Returns a string that represents the current object.
        /// </summary>
        /// <returns>
        /// A string that represents the current object.
        /// </returns>
        /// <filterpriority>2</filterpriority>
        public override string ToString()
        {
            return Messages.PeggedToStockOrder.ToString(this);
        }

        /// <summary>
        /// Gets the order value in units of the security's quote currency for a single unit.
        /// A single unit here is a single share of stock, or a single barrel of oil, or the
        /// cost of a single share in an option contract.
        /// </summary>
        /// <param name="security">The security matching this order's symbol</param>
        protected override decimal GetValueImpl(Security security)
        {
            Security underlying = ((Option)security).Underlying;
            decimal priceUnderlying = (underlying.BidPrice + underlying.AskPrice) / 2;
            StartingPriceInternal = StartingPriceInternal  == 0 ? StartingPrice ?? (security.BidPrice + security.AskPrice) / 2 : StartingPriceInternal;
            StockRefPriceInternal = StockRefPriceInternal  == 0 ? StockRefPrice ?? (underlying.BidPrice + underlying.AskPrice) / 2 : StockRefPriceInternal;
            decimal limitPrice = StartingPriceInternal + 0.01m * Delta * (StockRefPriceInternal - priceUnderlying);

            // selling, so higher price will be used
            if (Quantity < 0)
            {
                return Quantity * Math.Max(limitPrice, security.Price);
            }

            // buying, so lower price will be used
            if (Quantity > 0)
            {
                return Quantity * Math.Min(limitPrice, security.Price);
            }

            return 0m;
        }
    }
}
