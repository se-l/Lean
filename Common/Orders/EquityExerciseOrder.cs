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

namespace QuantConnect.Orders
{
    /// <summary>
    /// Option exercise order type definition
    /// </summary>
    public class EquityExerciseOrder : Order
    {
        /// <summary>
        /// Added a default constructor for JSON Deserialization:
        /// </summary>
        public EquityExerciseOrder()
        {
        }

        /// <summary>
        /// New equity option exercise order constructor to handle the assigned equity stock position as an order. We model option exercising as an underlying asset long/short order with strike equal to limit price.
        /// This means that by exercising a call we get into long asset position, by exercising a put we get into short asset position.
        /// </summary>
        /// <param name="symbol">Option symbol we're seeking to exercise</param>
        /// <param name="quantity">Quantity of the option we're seeking to exercise. Must be a positive value.</param>
        /// <param name="time">Time the order was placed</param>
        /// <param name="tag">User defined data tag for this order</param>
        /// <param name="properties">The order properties for this order</param>
        public EquityExerciseOrder(Symbol symbol, decimal quantity, DateTime time, string tag = "", IOrderProperties properties = null)
            : base(symbol, quantity, time, tag, properties)
        {
        }

        /// <summary>
        /// Helper constructor to create order from corresponing option assignment order.
        /// </summary>
        /// <param name="order"></param>
        public EquityExerciseOrder(OptionExerciseOrder order, OrderFillData orderFillData)
            : base(
                  order.Symbol.Underlying, 
                  order.Symbol.ID.OptionRight == OptionRight.Call ? -100*order.Quantity : 100 * order.Quantity, 
                  order.Time, 
                  $"Simulated order based on option assignment at Strike: {order.Symbol.ID.StrikePrice}",
                  order.Properties
                  )
        {
            OrderFillData = orderFillData;
        }

        /// <summary>
        /// Option Exercise Order Type
        /// </summary>
        public override OrderType Type
        {
            get { return OrderType.OptionExercise; }
        }

        /// <summary>
        /// Gets the order value
        /// </summary>
        /// <param name="security">The security matching this order's symbol</param>
        protected override decimal GetValueImpl(Security security)
        {
            return Quantity * security.Price;
        }

        /// <summary>
        /// Creates a deep-copy clone of this order
        /// </summary>
        /// <returns>A copy of this order</returns>
        public override Order Clone()
        {
            var order = new EquityExerciseOrder();
            CopyTo(order);
            return order;
        }
    }
}
