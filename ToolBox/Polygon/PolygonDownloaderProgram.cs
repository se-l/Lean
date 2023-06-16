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
using System.Threading.Tasks;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Logging;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantConnect.ToolBox.Polygon
{
    public class PolygonDownloaderProgram
    {
        private const int NumberOfClients = 16;

        class Request
        {
            public Symbol Symbol { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public Resolution Resolution { get; set; }
            public TickType TickType { get; set; }
        }
        /// <summary>
        /// Primary entry point to the program. This program only supports SecurityType.Equity
        /// </summary>
        public static void PolygonDownloader(IList<string> tickers, string securityTypeString, string market, string resolutionString, DateTime fromDate, DateTime toDate, string apiKey="", IList<string> tickTypeStrings = null, string skipExisting = "Y")
        {
            if (tickers.IsNullOrEmpty() || securityTypeString.IsNullOrEmpty() || market.IsNullOrEmpty() || resolutionString.IsNullOrEmpty())
            {
                Console.WriteLine("PolygonDownloader ERROR: '--tickers=' or '--security-type=' or '--market=' or '--resolution=' or '--api-key=' parameter is missing");
                Console.WriteLine("--tickers=eg SPY,AAPL");
                Console.WriteLine("--security-type=Equity/Option");
                Console.WriteLine("--market=usa");
                Console.WriteLine("--resolution=Minute/Hour/Daily");
                Console.WriteLine("--tick-types=Trade/Quote");
                Environment.Exit(1);
            }

            try
            {
                // Set API Key. Presumably already in Config.
                if (apiKey != "")
                {
                    Config.Set("polygon-api-key", apiKey);
                }

                // Load settings from command line
                var resolution = (Resolution)Enum.Parse(typeof(Resolution), resolutionString);
                var securityType = (SecurityType)Enum.Parse(typeof(SecurityType), securityTypeString);

                IEnumerable<TickType> tickTypes;
                if (tickTypeStrings.IsNullOrEmpty())
                {
                    // Polygon.io does not support Crypto historical quotes
                    tickTypes = securityType switch
                    {
                        SecurityType.Crypto => new List<TickType> { TickType.Trade },
                        SecurityType.Option => new List<TickType> { TickType.Trade, TickType.Quote },
                        _ => SubscriptionManager.DefaultDataTypes()[securityType]
                    };
                }
                else
                {
                    tickTypes = tickTypeStrings.Select(tickType => (TickType)Enum.Parse(typeof(TickType), tickType));
                }                

                // Load settings from config.json
                var dataDirectory = Config.Get("data-folder", "../../../Data");
                var startDate = fromDate.ConvertToUtc(TimeZones.NewYork);
                var endDate = toDate.ConvertToUtc(TimeZones.NewYork);

                var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

                // Create an instance of the downloader
                using var downloader = new PolygonDataDownloader();
                IEnumerable<Symbol> symbols = tickers.Select(x => Symbol.Create(x, securityType, market));
                if (securityType == SecurityType.Option)
                {
                    Log.Error($"Resolving Requity Ticker to OptionContracts...");
                    symbols = symbols.Select(sym => downloader.GetOptionContracts(sym.Underlying, endDate)).SelectMany(list => list);
                    symbols.GroupBy(sym => sym.Underlying).ToList().ForEach(group =>
                    {
                        Log.Error($"For Underlying: {group.Key.Value} fetched {group.Count()} OptionContracts");
                    });
                }
                var requests = symbols.SelectMany(symbol => tickTypes.Select(tickType => new Request
                {
                    Symbol = symbol,
                    Start = startDate,
                    End = endDate,
                    Resolution = resolution,
                    TickType = tickType
                })).ToList();

                Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = NumberOfClients }, request =>
                {
                    var securityExchangeHours = marketHoursDatabase.GetExchangeHours(market, request.Symbol, securityType);
                    var exchangeTimeZone = securityExchangeHours.TimeZone;
                    var dataTimeZone = marketHoursDatabase.GetDataTimeZone(market, request.Symbol, securityType);

                    var writer = new LeanDataWriter(resolution, request.Symbol, dataDirectory, request.TickType);

                    // Download the data
                    //var data = downloader.Get(new DataDownloaderGetParameters(request.Symbol, resolution, startDate, endDate, request.TickType))
                    //    .Select(x =>
                    //    {
                    //        x.Time = x.Time.ConvertTo(exchangeTimeZone, dataTimeZone);
                    //        return x;
                    //    }
                    //    );
                    //writer.Write(data);  // Save the data

                    //// For options, many contracts will have no data, primarily trades, so we need to write an empty file for these
                    //foreach (var date in Time.EachTradeableDay(securityExchangeHours, startDate, endDate.AddDays(-1)))  // typically requesting midnight of T+1
                    //{
                    //    // loop over dates in between startDate and endDate
                    //    writer.WriteEmptyFileIfNotExists(date, request.Symbol);
                    //}


                    foreach (var date in Time.EachTradeableDay(securityExchangeHours, startDate, endDate.AddDays(-1)))  // typically requesting midnight of T+1
                    {
                        if (skipExisting == "Y" && writer.FileEntryExists(date, request.Symbol))
                        {
                            continue;
                        }
                        else
                        {
                            // Download the data
                            var data = downloader.Get(new DataDownloaderGetParameters(request.Symbol, resolution, date, date.AddDays(1), request.TickType))
                                .Select(x =>
                                {
                                    x.Time = x.Time.ConvertTo(exchangeTimeZone, dataTimeZone);
                                    return x;
                                }
                                );

                            // For options, many contracts will have no data, primarily trades, so we need to write an empty file for these
                            if (data.Any())
                            {
                                writer.Write(data);
                            }
                            else
                            {
                                // loop over dates in between startDate and endDate
                                writer.WriteEmptyFileIfNotExists(date, request.Symbol);
                            }
                        }
                    }


                });
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
    }
}
