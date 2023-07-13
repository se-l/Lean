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

namespace QuantConnect.Orders
{
    /// <summary>
    /// Stores price data at the time the order was filled
    /// </summary>
    public class OrderFillData
    {
        /// <summary>
        /// Time as of Price Snap
        /// </summary>
        public DateTime Time { get; }

        /// <summary>
        /// The bid price at order submission time
        /// </summary>
        public decimal BidPrice { get; }
        public decimal? BidPriceUnderlying { get; }

        /// <summary>
        /// The ask price at order submission time
        /// </summary>
        public decimal AskPrice { get; }
        public decimal? AskPriceUnderlying { get; }

        /// <summary>
        /// The current price at order submission time
        /// </summary>
        public decimal Price { get; }
        public decimal? PriceUnderlying { get; }

        public decimal Fee { get; } = 0;

        /// <summary>
        /// Initializes a new instance of the <see cref="OrderSubmissionData"/> class
        /// </summary>
        /// <remarks>This method is currently only used for testing.</remarks>
        public OrderFillData(DateTime time, decimal bidPrice, decimal askPrice, decimal price, decimal? bidPriceUnderlying=null, decimal? askPriceUnderlying=null, decimal? priceUnderlying=null, decimal fee = 0)
        {
            Time = time;
            BidPrice = bidPrice;
            AskPrice = askPrice;
            Price = price;
            BidPriceUnderlying = bidPriceUnderlying;
            AskPriceUnderlying = askPriceUnderlying;
            PriceUnderlying = priceUnderlying;
            Fee = fee;
        }

        /// <summary>
        /// Return a new instance clone of this object
        /// </summary>
        public OrderFillData Clone()
        {
            return (OrderFillData)MemberwiseClone();
        }
    }
}
