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
using System.IO;
using System.Text;
using QuantConnect.Data;
using QuantConnect.Brokerages;
using System;
using QuantConnect.Data.Market;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Bitfinex script to fetch trade and quote data, save to disk.
    /// </summary>
    public class BitfinexDataFetcher : QCAlgorithm
    {
        public override void Initialize()
        {
            SetStartDate(2013, 10, 02);
            SetEndDate(2013, 10, 03);
            SetBrokerageModel(BrokerageName.Bitfinex, AccountType.Margin);
            foreach (string symbol in new List<string>() { 
                "ETHUSD", 
                "BTCUSD"
            })
            {
                AddCrypto(symbol, Resolution.Tick);
            }
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public override void OnData(Slice slice) {}
    }
}
