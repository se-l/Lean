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
using System.Globalization;
using System.Linq;
using System.Threading;
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
        class Request
        {
            public Symbol Symbol { get; set; }
            public DateTime Start { get; set; }
            public DateTime End { get; set; }
            public Resolution Resolution { get; set; }
            public TickType TickType { get; set; }
        }

        public static IEnumerable<DateTime> TradeDates(string market, MarketHoursDatabase marketHoursDatabase, Symbol symbol, DateTime startDate, DateTime endDate)
        {
            var securityExchangeHours = marketHoursDatabase.GetExchangeHours(market, symbol, symbol.ID.SecurityType);
            return Time.EachTradeableDay(securityExchangeHours, startDate, endDate);  // typically requesting midnight of T+1
        }

        /// <summary>
        /// Primary entry point to the program. This program only supports SecurityType.Equity
        /// </summary>
        public static void PolygonDownloader(IList<string> tickers, string securityTypeString, string market, string resolutionString, DateTime fromDate, DateTime toDate, string apiKey="", IList<string> tickTypeStrings = null, string skipExisting = "Y", int nClients=16)
        {
            if (tickers.IsNullOrEmpty() || securityTypeString.IsNullOrEmpty() || market.IsNullOrEmpty() || resolutionString.IsNullOrEmpty())
            {
                Console.WriteLine("PolygonDownloader ERROR: '--tickers=' or '--security-type=' or '--market=' or '--resolution=' or '--api-key=' parameter is missing");
                Console.WriteLine("--tickers=eg SPY,AAPL");
                Console.WriteLine("--security-type=Equity/Option");
                Console.WriteLine("--market=usa");
                Console.WriteLine("--resolution=Minute/Hour/Daily");
                Console.WriteLine("--tick-types=Trade/Quote");
                Console.WriteLine("--n-clients=16");
                Environment.Exit(1);
            }
            DiskDataCacheProvider _diskDataCacheProvider = new();
            Log.Trace($"PolygonDownloader: n-clients: {nClients}");
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
                var startDate = fromDate;  // .ConvertToUtc(TimeZones.NewYork);
                var endDate = toDate;  // .ConvertToUtc(TimeZones.NewYork);  // midnight in command prompt in HK turns into midight +4 hours EST.

                var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

                // Create an instance of the downloader
                using var downloader = new PolygonDataDownloader();
                IEnumerable<Symbol> symbols = tickers.Select(x => Symbol.Create(x, securityType, market));

                var tradeDates = TradeDates(market, marketHoursDatabase, symbols.First(), startDate, endDate.AddDays(-1));
                Dictionary<Symbol, IEnumerable<DateTime>> symbolDates = new();  // Dont request options for dates where option was not issued yet

                IEnumerable<Request> requests;
                if (securityType == SecurityType.Option)
                {
                    Log.Trace($"Resolving Equity Ticker to Option Contracts...");
                    foreach (DateTime dt in tradeDates)
                    {
                        // Log.Trace($"Requesting {symbols.Count()} symbols for {dt}...");
                        var optionSymbols = symbols.Select(sym => downloader.GetOptionContracts(sym.Underlying, dt)).SelectMany(list => list).OrderBy(s => s.ID.Date);
                        Log.Trace($"{dt}: {optionSymbols.Count()} Contracts from {symbols.Count()} underlyings");
                        foreach (var optionSymbol in optionSymbols)
                        {
                            if (!symbolDates.ContainsKey(optionSymbol))
                            {
                                symbolDates.Add(optionSymbol, new List<DateTime> { dt });
                            }
                            else
                            {
                                ((List<DateTime>)symbolDates[optionSymbol]).Add(dt);
                            }
                        }
                    }
                    symbolDates.Keys.GroupBy(sym => sym.Underlying).ToList().ForEach(group =>
                    {
                        Log.Trace($"For Underlying: {group.Key.Value} fetched {group.Count()} OptionContracts");
                    });

                    requests = symbolDates.SelectMany(kvp => tickTypes.Select(tickType => new Request
                    {
                        Symbol = kvp.Key,
                        Start = kvp.Value.Min(),
                        End = kvp.Value.Max(),
                        Resolution = resolution,
                        TickType = tickType
                    })).ToList();
                }    
                else
                {
                    requests = symbols.SelectMany(symbol => tickTypes.Select(tickType => new Request
                    {
                        Symbol = symbol,
                        Start = startDate,
                        End = endDate,
                        Resolution = resolution,
                        TickType = tickType
                    })).ToList();
                }

                int completedRequests = 0;
                int nRequests = requests.Count();
                double previousProgress = 0.0;

                Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = nClients }, request =>
                {
                    var writer = new LeanDataWriter(resolution, request.Symbol, dataDirectory, request.TickType, _diskDataCacheProvider);
                    var tradeDates = TradeDates(market, marketHoursDatabase, request.Symbol, request.Start, request.End);

                    if (skipExisting == "Y" && tradeDates.All(date => writer.FileEntryExists(date, request.Symbol)))
                    {
                        return;
                    }

                    var securityExchangeHours = marketHoursDatabase.GetExchangeHours(market, symbols.First(), securityType);
                    var exchangeTimeZone = securityExchangeHours.TimeZone;
                    var dataTimeZone = marketHoursDatabase.GetDataTimeZone(market, request.Symbol, securityType);
                    

                    // Download the data
                    var data = downloader.Get(new DataDownloaderGetParameters(request.Symbol, resolution, request.Start, request.End.AddDays(1), request.TickType))
                        .Select(x =>
                        {
                            x.Time = x.Time.ConvertTo(exchangeTimeZone, dataTimeZone);
                            return x;
                        }
                        );
                    if (data.Any())
                    {
                        writer.Write(data); ;  // Save the data across a date range.
                    }

                    // For options, many contracts will have no data, primarily trades, so we need to write an empty file for these
                    tradeDates.DoForEach(date =>
                    {
                        writer.WriteEmptyFileIfNotExists(date, request.Symbol);
                    });

                    // Increment the completed requests counter using Interlocked.Increment
                    Interlocked.Increment(ref completedRequests);
                    double progressPercentage = 100 * (double)completedRequests / nRequests;
                    // Check if the progress has increased by 0.5% or more
                    if (progressPercentage - previousProgress >= 0.2)
                    {
                        Console.WriteLine($"Progress: {progressPercentage.ToString("0.00", CultureInfo.InvariantCulture)}%. Handled {completedRequests} / {nRequests} requests.");
                        previousProgress = progressPercentage;
                    }
                });
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
            finally
            {
                _diskDataCacheProvider.DisposeSafely();
                Console.WriteLine("PolygonDownloader Program Completed.");
            }
        }
    }
}
