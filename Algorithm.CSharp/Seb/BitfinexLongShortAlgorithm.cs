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
using System.Linq;
using QuantConnect.Data;
using QuantConnect.Brokerages;
using QuantConnect.Orders;
using Newtonsoft.Json;
using QuantConnect.Util;
using QuantConnect.Data.UniverseSelection;
using NodaTime;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// The demonstration algorithm shows some of the most common order methods when working with Crypto assets.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public class BitfinexLongShortAlgorithm : QCAlgorithm
    {
        private Symbol p_ethusd_sym;
        private Securities.Security feed;
        private Security sec_ethusd;
        private Symbol ethusd;
        private decimal t_long = 1.002m;
        private decimal t_short = 0.998m;
        private double stop_loss = -10.02;
        // 3b) if time position > thresh: liquidate to zero
        private int max_time = 60 * 60 * 3;
        private double ret;
        private decimal best_price = 0;
        private decimal p = 0;
        private decimal price = 0;
        private decimal position = 0;
        private DateTime t_last_deal;
        private DateTime t_thresh;
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            SetTimeZone(TimeZones.Utc);
            //SetStartDate(2022, 2, 7);
            //SetEndDate(2022, 9, 3);
            SetStartDate(2022, 5, 12);
            SetEndDate(2022, 5, 13);
            SetBrokerageModel(BrokerageName.Bitfinex, AccountType.Margin);

            //AddCrypto("BTCUSD", Resolution.Tick);
            sec_ethusd = AddCrypto("ETHUSD", Resolution.Tick);
            ethusd = sec_ethusd.Symbol;
            sec_ethusd.SetFeeModel(new CustomFeeModel(this));
            p_ethusd_sym = AddData<Preds>("PETHUSD", Resolution.Tick).Symbol;

            SetBenchmark(ethusd);
        }

        /// <summary>
        /// OnData event is the primary entry point for your algorithm. Each new data point will be pumped in here.
        /// </summary>
        /// <param name="data">Slice object keyed by symbol containing the stock data</param>
        public override void OnData(Slice data)
        {
            position = Portfolio[ethusd].Quantity;

            if (data.ContainsKey(ethusd) && data[ethusd][0].Price != 0) {
                price = data[ethusd][0].Price;

                if (position > 0 && price > best_price) {
                    best_price = price;
                } 
                else if (position < 0 && price < best_price)
                {
                    best_price = price;
                }
                switch (position)
                {
                    case > 0:
                        ret = (double)(price / best_price - 1);
                        break;
                    case < 0:
                        ret = -(double)(price / best_price - 1);
                        break;
                }

                if (position != 0 && data.Time > t_thresh)
                {
                    Debug("TIME");
                    decimal quantity = -position;
                    t_last_deal = data.UtcTime;
                    t_thresh = t_last_deal.AddSeconds(max_time);
                    best_price = price;
                    ret = 0;
                    MarketOrder(ethusd, quantity);
                }

                if (position != 0 && ret < stop_loss)
                {
                    Debug("STOPLOSS");
                    decimal quantity = -position;
                    t_last_deal = data.UtcTime;
                    t_thresh = t_last_deal.AddSeconds(max_time);
                    best_price = price;
                    ret = 0;
                    MarketOrder(ethusd, quantity);
                }
            }

            if (data.ContainsKey(p_ethusd_sym)) {
                p = data[p_ethusd_sym].Value;

                if (p > t_long && position < 0.9m)
                {
                    Debug("LONG");
                    decimal quantity = 1 - position;
                    t_last_deal = data.UtcTime;
                    t_thresh = t_last_deal.AddSeconds(max_time);
                    best_price = price;
                    ret = 0;
                    MarketOrder(ethusd, quantity);
                } else if(p < t_short && position > -0.9m)
                {
                    Debug("SHORT");
                    decimal quantity = -1 - position;
                    t_last_deal = data.UtcTime;
                    t_thresh = t_last_deal.AddSeconds(max_time);
                    best_price = price;
                    ret = 0;
                    MarketOrder(ethusd, quantity);
                }
            }
        }

        //public void OnData(Preds data)
        //{
        //    var value = data.Value;
        //}

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            Debug(Time + " " + orderEvent);
        }

        public override void OnEndOfAlgorithm()
        {
            Log($"{Time} - TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"{Time} - CashBook: {Portfolio.CashBook}");
        }

        public class CustomFeeModel : FeeModel
        {
            private readonly QCAlgorithm _algorithm;

            public CustomFeeModel(QCAlgorithm algorithm)
            {
                _algorithm = algorithm;
            }

            public override OrderFee GetOrderFee(OrderFeeParameters parameters)
            {
                // custom fee math
                var fee = Math.Max(
                    1m,
                    parameters.Security.Price * parameters.Order.AbsoluteQuantity * 0.00002m);

                _algorithm.Log($"CustomFeeModel: {fee}");
                return new OrderFee(new CashAmount(fee, "USD"));
            }
        }

        public class Preds : BaseData
        {
            [JsonProperty("ts")]
            public DateTime Timestamp = new();
            [JsonProperty("preds")]
            public decimal Value = 0;

            
            public Preds()
            {
                Symbol = "PETHUSD";
            }

            /// <summary>
            /// Specifies the data time zone for this data type. This is useful for custom data types
            /// </summary>
            /// <returns>The <see cref="DateTimeZone"/> of this data type</returns>
            public override DateTimeZone DataTimeZone()
            {
                return TimeZones.Utc;
            }

            public override SubscriptionDataSource GetSource(
                SubscriptionDataConfig config,
                DateTime date,
                bool isLive)
            {
                //var source = $"C:\\repos\\trade\\data\\crypto\\bitfinex\\{config.Resolution.ToLower()}\\{config.Symbol.Value.ToLower()}\\{date:yyyyMMdd}_{config.TickType.ToLower()}.zip";
                var source = $"C:\\repos\\trade\\model\\supervised\\ex2022-09-15_104055-ethusd\\pred_label_ho.json";
                return new SubscriptionDataSource(source, SubscriptionTransportMedium.LocalFile, FileFormat.UnfoldingCollection);
            }

            public override BaseData Reader(
                SubscriptionDataConfig config,
                string line,
                DateTime date,
                bool isLive)
            {
                if (string.IsNullOrWhiteSpace(line.Trim()))
                {
                    return null;
                }

                var objects = JsonConvert.DeserializeObject<List<Preds>>(line).Select(index =>
                {
                    index.Symbol = config.Symbol;
                    index.Time = index.Timestamp;
                    return index;
                }).ToList();

                return new BaseDataCollection(objects.Last().EndTime, config.Symbol, objects);
            }
        }
    }
}
