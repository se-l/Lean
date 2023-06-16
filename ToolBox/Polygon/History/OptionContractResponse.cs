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

using Newtonsoft.Json;
using System;

namespace QuantConnect.ToolBox.Polygon.History
{
    public class OptionContractResponse
    {
        [JsonProperty("cfi")]
        public string CFI { get; set; }  // https://en.wikipedia.org/wiki/ISO_10962

        [JsonProperty("contract_type")]
        public string Right { get; set; }

        [JsonProperty("exercise_style")]
        public string ExerciseStyle { get; set; }

        [JsonProperty("expiration_date")]
        public string ExpirationDate { get; set; }

        [JsonProperty("primary_exchange")]
        public string PrimaryExchange { get; set; }

        [JsonProperty("shares_per_contract")]
        public long Multiplier { get; set; }

        [JsonProperty("strike_price")]
        public decimal StrikePrice { get; set; }

        [JsonProperty("ticker")]
        public string Ticker { get; set; }

        [JsonProperty("underlying_ticker")]
        public string UnderlyingTicker { get; set; }

        public override int GetHashCode()
        {
            unchecked
            {
                return Ticker.GetHashCode();
            }
        }

        public Symbol ToSymbol(string market = "usa")
        {
            OptionRight optionRight = Right switch
            {
                "call" => OptionRight.Call,
                "put" => OptionRight.Put,
                _ => throw new NotSupportedException($"Unknown option right: {Right}")
            };

            OptionStyle optionStyle = ExerciseStyle switch
            {
                "american" => OptionStyle.American,
                "european" => OptionStyle.European,
                _ => throw new NotSupportedException($"Unknown option style: {ExerciseStyle}")
            };

            
            return Symbol.CreateOption(
                Symbol.Create(UnderlyingTicker, SecurityType.Equity, market),
                market,
                optionStyle, 
                optionRight, 
                StrikePrice,
                DateTime.ParseExact(ExpirationDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                );
        }
    }
}
