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
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Globalization;
using QuantConnect.Data;
using QuantConnect.Interfaces;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class TickTest : QCAlgorithm
    {
        //private Symbol _spy = QuantConnect.Symbol.Create("SPY", SecurityType.Equity, Market.USA);
        private Symbol _customDataSymbol;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2013, 10, 07);  //Set Start Date
            SetEndDate(2013, 10, 08);    //Set End Date
            SetCash(100000);             //Set Strategy Cash

            // Find more symbols here: http://quantconnect.com/data
            // Forex, CFD, Equities Resolutions: Tick, Second, Minute, Hour, Daily.
            // Futures Resolution: Tick, Second, Minute
            // Options Resolution: Minute Only.
            //AddEquity("SPY", Resolution.Tick);

            _customDataSymbol = AddData<MyCustomDataType>("SPY", Resolution.Tick).Symbol;

            //var history = History<MyCustomDataType>(_customDataSymbol, 200, Resolution.Tick);
            //Debug($"We got {history.Count()} items from historical data request of {_customDataSymbol}.");

            // There are other assets with similar methods. See "Selecting Options" etc for more details.
            // AddFuture, AddForex, AddCfd, AddOption
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public void OnData(MyCustomDataType data)
        {
            if (!Portfolio.Invested)
            {
                SetHoldings(_customDataSymbol, 1);
                Debug("Purchased Stock");
            }
        }

        public class MyCustomDataType : BaseData
        {
            public override SubscriptionDataSource GetSource(
                SubscriptionDataConfig config,
                DateTime date,
                bool isLive)
            {
                var source = $"C:\\repos\\quantconnect\\Lean\\Data\\equity\\usa\\{config.Resolution}\\{config.Symbol.Value.ToLower()}\\{date:yyyyMMdd}_{config.TickType.ToLower()}.zip";
                return new SubscriptionDataSource(source, SubscriptionTransportMedium.LocalFile, FileFormat.Csv);
            }
  
            //public override BaseData Reader(
            //    SubscriptionDataConfig config,
            //    string line,
            //    DateTime date,
            //    bool isLive)
            //{
            //    if (string.IsNullOrWhiteSpace(line.Trim()))
            //    {
            //        return null;
            //    }

            //    if (isLive)
            //    {
            //        var custom = JsonConvert.DeserializeObject<MyCustomDataType>(line);
            //        custom.EndTime = DateTime.UtcNow.ConvertFromUtc(config.ExchangeTimeZone);
            //        return custom;
            //    }

            //    if (!char.IsDigit(line[0]))
            //    {
            //        return null;
            //    }

            //    var data = line.Split(',');
            //    return new MyCustomDataType()
            //    {
            //        Time = DateTime.ParseExact(data[0], "yyyyMMdd", CultureInfo.InvariantCulture),
            //        EndTime = Time.AddDays(1),
            //        Symbol = config.Symbol,
            //        Value = data[1].IfNotNullOrEmpty(
            //            s => decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture)),
            //    };
            //}
        }

        /// <summary>
        /// This is used by the regression test system to indicate if the open source Lean repository has the required data to run this algorithm.
        /// </summary>
        public bool CanRunLocally { get; } = true;
    }
}
