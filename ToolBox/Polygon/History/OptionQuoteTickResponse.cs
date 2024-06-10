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

namespace QuantConnect.ToolBox.Polygon.History
{
    public class OptionQuoteTickResponse
    {
        [JsonProperty("sequence_number")]
        public long SequenceNumber { get; set; }

        [JsonProperty("bid_price")]
        public decimal BidPrice { get; set; }

        [JsonProperty("ask_price")]
        public decimal AskPrice { get; set; }

        [JsonProperty("bid_size")]
        public decimal BidSize { get; set; }

        [JsonProperty("ask_size")]
        public decimal AskSize { get; set; }

        [JsonProperty("bid_exchange")]
        public int BidExchange { get; set; }

        [JsonProperty("ask_exchange")]
        public int AskExchange { get; set; }

        [JsonProperty("sip_timestamp")]
        public long SipTimestamp { get; set; }
    }
}
