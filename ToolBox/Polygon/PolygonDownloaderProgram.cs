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
using System.Collections.Concurrent;
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

        public static Symbol Underlying(Symbol symbol)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Option => symbol.ID.Underlying.Symbol,
                SecurityType.Equity => symbol,
                _ => throw new NotImplementedException(),
            };
        }

        /// <summary>
        /// Primary entry point to the program. This program only supports SecurityType.Equity
        /// </summary>
        public static void PolygonDownloader(IList<string> tickers, string securityTypeString, string market, string resolutionString, DateTime fromDate, DateTime toDate, string apiKey="", IList<string> tickTypeStrings = null, string skipExisting = "Y", int nClients=16)
        {
            void WriteDataQueueToDisk(ConcurrentQueue<Tuple<Symbol, IEnumerable<BaseData>>> dataQueue, Symbol underlying, TickType tickType, DiskDataCacheProvider diskDataCacheProvider, LeanDataWriter writer, CancellationTokenSource downloadFinished, DateTime startDate, DateTime endDate, Resolution resolution)
            {
                var dataDirectory = Config.Get("data-folder", "../../../Data");
                var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
                
                Console.WriteLine($"PolygonDownloaderProgram.WriteDataQueueToDisk(): {underlying} {tickType} Starting...");

                bool stopWriting = downloadFinished.Token.IsCancellationRequested;
                try
                {
                    List<IEnumerable<BaseData>> dataList = new();
                    HashSet<Symbol> processedSymbols = new();
                    HashSet<DateTime> tradeDates = new();

                    while (!stopWriting)
                    {
                        while (!dataQueue.IsEmpty)
                        {
                            if (dataQueue.TryDequeue(out Tuple<Symbol, IEnumerable<BaseData>> tup))
                            {
                                Symbol symbol = tup.Item1;
                                IEnumerable<BaseData> data = tup.Item2;
                                if (data.Any())
                                {
                                    dataList.Add(data);
                                }                                
                                
                                tradeDates = new HashSet<DateTime>(tradeDates.Union(TradeDates(market, marketHoursDatabase, symbol, startDate, endDate)));
                                processedSymbols.Add(symbol);
                            }
                        }

                        if (dataList.Any())
                        {
                            writer.Write(dataList);
                            dataList.Clear();
                        }

                        // Sleep for a short period to avoid busy waiting
                        Thread.Sleep(100);

                        stopWriting = downloadFinished.Token.IsCancellationRequested && dataQueue.IsEmpty;
                    };
                    LeanData.WriteEmptyFileIfNotExists(dataDirectory, diskDataCacheProvider, tradeDates, processedSymbols, resolution, tickType);

                    Log.Trace($"PolygonDownloaderProgram.WriteDataQueueToDisk(): {underlying} {tickType} Exiting...");
                }
                catch (Exception e)
                {
                    Log.Error($"PolygonDownloaderProgram.WriteDataQueueToDisk(): {underlying} {tickType} Exception: ${e}");
                }
                finally
                {
                    diskDataCacheProvider.DisposeSafely();
                }
            }

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

                Dictionary<TickType, LeanDataWriter> writers = new();
                foreach (TickType tickType in tickTypes)
                {
                    writers.Add(tickType, new LeanDataWriter(dataDirectory, resolution, securityType, tickType, _diskDataCacheProvider));
                }
                
                Dictionary<Tuple<Symbol, TickType>, ConcurrentQueue<Tuple<Symbol, IEnumerable<BaseData>>>> dataQueues = new();
                CancellationTokenSource CTS = new();
                CancellationTokenSource DownloadFinished = new();
                List<Task> tasksWriteToDisk = new();

                foreach (var request in requests)
                {
                    Symbol underlying = Underlying(request.Symbol);
                    var key = new Tuple<Symbol, TickType>(underlying, request.TickType);
                    if (!dataQueues.ContainsKey(key))
                    {
                        dataQueues.Add(key, new ConcurrentQueue<Tuple<Symbol, IEnumerable<BaseData>>>());
                        Action action = () => WriteDataQueueToDisk(dataQueues[key], underlying, request.TickType, _diskDataCacheProvider, writers[request.TickType], DownloadFinished, startDate, endDate, resolution);
                        tasksWriteToDisk.Add(Task.Factory.StartNew(action, CTS.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default));
                    }
                }

                Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = nClients }, request =>
                {
                    var writer = writers[request.TickType];  // new LeanDataWriter(resolution, request.Symbol, dataDirectory, request.TickType, _diskDataCacheProvider);
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

                    var key = new Tuple<Symbol, TickType>(Underlying(request.Symbol), request.TickType);
                    dataQueues[key].Enqueue(new(request.Symbol, data.ToList()));

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

                Console.WriteLine($"PolygonDownloader Download completed. Remaining items in queues: {string.Join(", ", dataQueues.Select(kvp => kvp.Value.Count))}");
                DownloadFinished.Cancel();
                tasksWriteToDisk.DoForEach(t => t.Wait());
                DownloadFinished.Dispose();
                CTS.Dispose();

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
