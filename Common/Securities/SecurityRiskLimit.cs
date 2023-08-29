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
    /// Defines a base class for <see cref="Security"/> related Risk Limit used to hedge to band.
    /// </summary>
    public class SecurityRiskLimit
    {
        /// <summary>
        /// Gets the security related to this event
        /// </summary>
        public Security Security { get; }
        public decimal Delta100BpLong { get; }
        public decimal Delta100BpShort { get; }
        public decimal Gamma100BpLong { get; }
        public decimal Gamma100BpShort { get; }
        public decimal DeltaLongUSD { get; } = 1_000;
        public decimal DeltaShortUSD { get; } = -200;

        public decimal DeltaTarget { get => deltaTarget ?? (Delta100BpLong + Delta100BpShort) / 2; }
        public decimal GammaTarget { get => gammaTarget ?? (Gamma100BpLong + Gamma100BpShort) / 2; }
        // if composed of individual deltsa below 0.5 (OTM) and assuming flat underlying, Delta will turn to 0 vs. 1 for deltas slightly above 0.5 (ITM).
        public decimal DeltaTargetUSD { get => deltaTargetUSD ?? (DeltaLongUSD + DeltaShortUSD) / 2; }

        private decimal? deltaTarget { get; set; }
        private decimal? gammaTarget { get; set; }
        private decimal? deltaTargetUSD { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityRiskLimit"/> class
        /// </summary>
        /// <param name="security">The security</param>
        public SecurityRiskLimit(
            Security security,
            decimal delta100BpLong = 5,
            decimal delta100BpShort = -5,
            decimal gamma100BpLong = 5,
            decimal gamma100BpShort = -5,
            decimal deltaLongUSD = 5,
            decimal deltaShortUSD = -5,
            decimal? deltaTarget = null,  // depends on OTM vs ITM.
            decimal? deltaTargetUSD = null
            )
        {
            Security = security;
            Delta100BpLong = delta100BpLong;
            Delta100BpShort = delta100BpShort;
            Gamma100BpLong = gamma100BpLong;
            Gamma100BpShort = gamma100BpShort;
            DeltaLongUSD = deltaLongUSD;
            DeltaShortUSD = deltaShortUSD;
            this.deltaTarget = deltaTarget;
            this.deltaTargetUSD = deltaTargetUSD;
        }
    }
}
