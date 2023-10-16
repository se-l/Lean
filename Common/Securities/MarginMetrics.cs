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

namespace QuantConnect.Securities
{
    /// <summary>
    /// Messaging class signifying a change in a user's account maintenance margin
    /// </summary>
    public class MarginMetrics
    {
        /// <summary>
        /// Gets the total maintenance margin of the account in USD/>
        /// </summary>
        public decimal FullMaintMarginReq { get; set; }
        public decimal EquityWithLoanValue { get; set; }
        public decimal ExcessLiquidity { get; set; }
        public decimal FullAvailableFunds { get; set; }
        public decimal FullInitMarginReq { get; set; }
        public decimal Leverage { get; set; }

        /// <summary>
        /// Creates an MaintenanceMarginEvent
        /// </summary>
        /// <param name="cashBalance">The total maintenance margin of the account</param>
        public MarginMetrics(decimal fullMaintMarginReq = 0, decimal equityWithLoanValue = 0, decimal excessLiquidity = 0, decimal fullAvailableFunds = 0, decimal initMarginReq = 0, decimal leverage = 0)
        {
            FullMaintMarginReq = fullMaintMarginReq;
            EquityWithLoanValue = equityWithLoanValue;
            ExcessLiquidity = excessLiquidity;
            FullAvailableFunds = fullAvailableFunds;
            FullInitMarginReq = initMarginReq;
            Leverage = leverage;
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
            return Messages.MarginMetrics.ToString(this);
        }

        public void Clear()
        {
            FullMaintMarginReq = 0;
            EquityWithLoanValue = 0;
            ExcessLiquidity = 0;
            FullAvailableFunds = 0;
            FullInitMarginReq = 0;
            Leverage = 0;
        }
    }
}
