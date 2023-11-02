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

using System.Collections.Generic;
using System.ComponentModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using QuantConnect.Orders.TimeInForces;

namespace QuantConnect.Orders.Serialization
{
    /// <summary>
    /// Data transfer object used for serializing an <see cref="Order"/> that was just generated by an algorithm
    /// </summary>
    public class SerializedOrder
    {
        /// <summary>
        /// The unique order id
        /// </summary>
        [JsonProperty("id", Required = Required.Default)]
        public string Id => $"{AlgorithmId}-{OrderId}";

        /// <summary>
        /// Algorithm Id, BacktestId or DeployId
        /// </summary>
        [JsonProperty("algorithm-id")]
        public string AlgorithmId { get; set; }

        /// <summary>
        /// Order ID
        /// </summary>
        [JsonProperty("order-id")]
        public int OrderId { get; set; }

        /// <summary>
        /// Order id to process before processing this order.
        /// </summary>
        [JsonProperty("contingent-id")]
        public int ContingentId { get; set; }

        /// <summary>
        /// Brokerage Id for this order for when the brokerage splits orders into multiple pieces
        /// </summary>
        [JsonProperty("broker-id")]
        public List<string> BrokerId { get; set; }

        /// <summary>
        /// Symbol of the Asset
        /// </summary>
        [JsonProperty("symbol")]
        public string Symbol { get; set; }

        /// <summary>
        /// Price of the Order.
        /// </summary>
        [JsonProperty("price")]
        public decimal Price { get; set; }

        /// <summary>
        /// Currency for the order price
        /// </summary>
        [JsonProperty("price-currency")]
        public string PriceCurrency { get; set; }

        /// <summary>
        /// Gets the utc time this order was created. Alias for <see cref="Time"/>
        /// </summary>
        [JsonProperty("created-time")]
        public double CreatedTime { get; set; }

        /// <summary>
        /// Gets the utc time the last fill was received, or null if no fills have been received
        /// </summary>
        [JsonProperty("last-fill-time", NullValueHandling = NullValueHandling.Ignore)]
        public double? LastFillTime { get; set; }

        /// <summary>
        /// Gets the utc time this order was last updated, or null if the order has not been updated.
        /// </summary>
        [JsonProperty("last-update-time", NullValueHandling = NullValueHandling.Ignore)]
        public double? LastUpdateTime { get; set; }

        /// <summary>
        /// Gets the utc time this order was canceled, or null if the order was not canceled.
        /// </summary>
        [JsonProperty("canceled-time", NullValueHandling = NullValueHandling.Ignore)]
        public double? CanceledTime { get; set; }

        /// <summary>
        /// Number of shares to execute.
        /// </summary>
        [JsonProperty("quantity")]
        public decimal Quantity { get; set; }

        /// <summary>
        /// Order Type
        /// </summary>
        [JsonProperty("type"), JsonConverter(typeof(StringEnumConverter), true)]
        public OrderType Type { get; set; }

        /// <summary>
        /// Status of the Order
        /// </summary>
        [JsonProperty("status"), JsonConverter(typeof(StringEnumConverter), true)]
        public OrderStatus Status { get; set; }

        /// <summary>
        /// Tag the order with some custom data
        /// </summary>
        [DefaultValue(""), JsonProperty("tag", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string Tag { get; set; }

        /// <summary>
        /// Order Direction Property based off Quantity.
        /// </summary>
        [JsonProperty("direction"), JsonConverter(typeof(StringEnumConverter), true)]
        public OrderDirection Direction { get; set; }

        /// <summary>
        /// The current price at order submission time
        /// </summary>
        [JsonProperty("submission-last-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal SubmissionLastPrice { get; set; }

        /// <summary>
        /// The ask price at order submission time
        /// </summary>
        [JsonProperty("submission-ask-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal SubmissionAskPrice { get; set; }

        /// <summary>
        /// The bid price at order submission time
        /// </summary>
        [JsonProperty("submission-bid-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal SubmissionBidPrice { get; set; }

        /// <summary>
        /// The current stop price
        /// </summary>
        [JsonProperty("stop-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? StopPrice { get; set; }

        /// <summary>
        /// Signal showing the "StopLimitOrder" has been converted into a Limit Order
        /// </summary>
        [JsonProperty("stop-triggered", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool? StopTriggered { get; set; }

        /// <summary>
        /// Signal showing the "LimitIfTouchedOrder" has been converted into a Limit Order
        /// </summary>
        [JsonProperty("trigger-touched", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool? TriggerTouched { get; set; }

        /// <summary>
        /// The price which must first be reached before submitting a limit order.
        /// </summary>
        [JsonProperty("trigger-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? TriggerPrice { get; set; }

        /// <summary>
        /// The current limit price
        /// </summary>
        [JsonProperty("limit-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? LimitPrice { get; set; }

        [JsonProperty("oca-group", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string OcaGroup { get; set; }
        [JsonProperty("oca-type", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int OcaType { get; set; }
        [JsonProperty("delta", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? Delta { get; set; }
        [JsonProperty("starting-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? StartingPrice { get; set; }
        [JsonProperty("stock-ref-price", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? StockRefPrice { get; set; }
        [JsonProperty("underlying-range-low", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? UnderlyingRangeLow { get; set; }
        [JsonProperty("underlying-range-high", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public decimal? UnderlyingRangeHigh { get; set; }       

        /// <summary>
        /// The time in force type
        /// </summary>
        [JsonProperty("time-in-force-type")]
        public string TimeInForceType { get; set; }

        /// <summary>
        /// The time in force expiration time if any
        /// </summary>
        [JsonProperty("time-in-force-expiry", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public double? TimeInForceExpiry { get; set; }

        /// <summary>
        /// The group order manager for combo orders
        /// </summary>
        [JsonProperty("group-order-manager", DefaultValueHandling = DefaultValueHandling.Ignore, NullValueHandling = NullValueHandling.Ignore)]
        public GroupOrderManager GroupOrderManager { get; set; }

        /// <summary>
        /// Empty constructor required for JSON converter.
        /// </summary>
        protected SerializedOrder()
        {
        }

        /// <summary>
        /// Creates a new serialized order instance based on the provided order
        /// </summary>
        public SerializedOrder(Order order, string algorithmId)
        {
            AlgorithmId = algorithmId;

            OrderId = order.Id;
            ContingentId = order.ContingentId;
            BrokerId = order.BrokerId;
            Symbol = order.Symbol.ID.ToString();
            Price = order.Price;
            PriceCurrency = order.PriceCurrency;
            Quantity = order.Quantity;
            Type = order.Type;
            Status = order.Status;
            Tag = order.Tag;
            Direction = order.Direction;

            CreatedTime = Time.DateTimeToUnixTimeStamp(order.CreatedTime);
            if (order.LastFillTime.HasValue)
            {
                LastFillTime = Time.DateTimeToUnixTimeStamp(order.LastFillTime.Value);
            }
            if (order.LastUpdateTime.HasValue)
            {
                LastUpdateTime = Time.DateTimeToUnixTimeStamp(order.LastUpdateTime.Value);
            }
            if (order.CanceledTime.HasValue)
            {
                CanceledTime = Time.DateTimeToUnixTimeStamp(order.CanceledTime.Value);
            }
            if (order.OrderSubmissionData != null)
            {
                SubmissionAskPrice = order.OrderSubmissionData.AskPrice;
                SubmissionBidPrice = order.OrderSubmissionData.BidPrice;
                SubmissionLastPrice = order.OrderSubmissionData.LastPrice;
            }

            var timeInForceType = order.Properties.TimeInForce.GetType().Name;
            // camelcase the type name, lowering the first char
            TimeInForceType = char.ToLowerInvariant(timeInForceType[0]) + timeInForceType.Substring(1);
            if (order.Properties.TimeInForce is GoodTilDateTimeInForce)
            {
                var expiry = (order.Properties.TimeInForce as GoodTilDateTimeInForce).Expiry;
                TimeInForceExpiry = Time.DateTimeToUnixTimeStamp(expiry);
            }

            if (order.Type == OrderType.Limit)
            {
                var limit = order as LimitOrder;
                LimitPrice = limit.LimitPrice;
            }
            else if (order.Type == OrderType.StopLimit)
            {
                var stopLimit = order as StopLimitOrder;
                LimitPrice = stopLimit.LimitPrice;
                StopPrice = stopLimit.StopPrice;
                StopTriggered = stopLimit.StopTriggered;
            }
            else if (order.Type == OrderType.StopMarket)
            {
                var stopMarket = order as StopMarketOrder;
                StopPrice = stopMarket.StopPrice;
            }
            else if (order.Type == OrderType.LimitIfTouched)
            {
                var limitIfTouched = order as LimitIfTouchedOrder;
                LimitPrice = limitIfTouched.LimitPrice;
                TriggerPrice = limitIfTouched.TriggerPrice;
                TriggerTouched = limitIfTouched.TriggerTouched;
            }
            else if (order.Type == OrderType.PeggedToStock)
            {
                var peggedToStock = order as PeggedToStockOrder;
                Delta = peggedToStock.Delta;
                StartingPrice = peggedToStock.StartingPrice;
                StockRefPrice = peggedToStock.StockRefPrice;
                UnderlyingRangeLow = peggedToStock.UnderlyingRangeLow;
                UnderlyingRangeHigh = peggedToStock.UnderlyingRangeHigh;
            }

            GroupOrderManager = order.GroupOrderManager;
        }
    }
}
