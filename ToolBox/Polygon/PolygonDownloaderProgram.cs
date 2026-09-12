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
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
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
            public DateTime Date { get; set; }
            public Resolution Resolution { get; set; }
            public TickType TickType { get; set; }
        }

        class DayGroupKey
        {
            public DateTime Date { get; set; }
            public Symbol Underlying { get; set; }
            public TickType TickType { get; set; }

            public override bool Equals(object obj)
            {
                if (obj is DayGroupKey other)
                {
                    return Date.Date == other.Date.Date &&
                        Underlying.Equals(other.Underlying) &&
                        TickType == other.TickType;
                }

                return false;
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(Date.Date, Underlying, TickType);
            }

            public override string ToString()
            {
                return $"{Date:yyyy-MM-dd}_{Underlying}_{TickType}";
            }
        }

        public static IEnumerable<DateTime> TradeDates(
            string market,
            MarketHoursDatabase marketHoursDatabase,
            Symbol symbol,
            DateTime startDate,
            DateTime endDate
            )
        {
            var securityExchangeHours = marketHoursDatabase.GetExchangeHours(market, symbol, symbol.ID.SecurityType);
            return Time.EachTradeableDay(securityExchangeHours, startDate,
                endDate); // typically requesting midnight of T+1
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
        /// Resolves the option contracts for the given underlyings at the given date.
        /// First pathway: reads the locally available openinterest zip file
        /// (<dataDirectory>/option/usa/tick/<underlying>/<yyyymmdd>_openinterest_american.zip),
        /// whose contents are considered exhaustive.
        /// Second pathway (fallback): queries Massive.com (Polygon) for the option contracts.
        /// </summary>
        public static IEnumerable<Symbol> GetOptionSymbols(
            string dataDirectory,
            string market,
            string resolution,
            PolygonDataDownloader downloader,
            IEnumerable<Symbol> symbols,
            DateTime dt)
        {
            var optionSymbols = new List<Symbol>();
            var unresolvedSymbols = new List<Symbol>();

            foreach (var sym in symbols)
            {
                var zipPath = Path.Combine(dataDirectory, "option", market, resolution,
                    sym.Underlying.Value.ToLowerInvariant(),
                    $"{dt:yyyyMMdd}_openinterest_american.zip");

                if (File.Exists(zipPath))
                {
                    using var stream = File.OpenRead(zipPath);
                    using var archive = new ZipArchive(stream);
                    foreach (var entry in archive.Entries)
                    {
                        optionSymbols.Add(LeanData.ReadSymbolFromZipEntry(sym, Resolution.Tick, entry.FullName));
                    }
                }
                else
                {
                    unresolvedSymbols.Add(sym);
                }
            }

            if (unresolvedSymbols.Count > 0)
            {
                // Fallback: query Massive.com for underlyings without a local openinterest zip
                optionSymbols.AddRange(unresolvedSymbols
                    .Select(sym => downloader.GetOptionContracts(sym.Underlying, dt))
                    .SelectMany(list => list));
            }

            return optionSymbols.OrderBy(s => s.ID.Date);
        }

        /// <summary>
        /// Primary entry point to the program. This program only supports SecurityType.Equity
        /// </summary>
        public static void PolygonDownloader(
            IList<string> tickers,
            string securityTypeString,
            string market,
            string resolutionString,
            DateTime fromDate,
            DateTime toDate,
            string apiKey = "",
            IList<string> tickTypeStrings = null,
            bool skipFilled = true,
            bool skipEmpty = true,
            DateTime? skipModifiedSince = null,
            int nClients = 16,
            int flushInterval = 1000
            )
        {
            if (tickers.IsNullOrEmpty() || securityTypeString.IsNullOrEmpty() || market.IsNullOrEmpty() ||
                resolutionString.IsNullOrEmpty())
            {
                Console.WriteLine(
                    "PolygonDownloader ERROR: '--tickers=' or '--security-type=' or '--market=' or '--resolution=' or '--api-key=' parameter is missing");
                Console.WriteLine("--tickers=eg SPY,AAPL");
                Console.WriteLine("--security-type=Equity/Option");
                Console.WriteLine("--market=usa");
                Console.WriteLine("--resolution=Minute/Hour/Daily");
                Console.WriteLine("--tick-types=Trade/Quote");
                Console.WriteLine("--n-clients=16");
                Console.WriteLine("--flush-interval=1000");
                Environment.Exit(1);
            }

            DiskDataCacheProvider _diskDataCacheProvider = new();
            Log.Trace($"PolygonDownloader: n-clients: {nClients}");
            try
            {
                // Set API Key. Presumably already in Config.
                if (apiKey != "") Config.Set("polygon-api-key", apiKey);

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
                var dataDirectory = Config.Get("data-folder", "../../../trade/data");

                var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

                // Create an instance of the downloader
                using var downloader = new PolygonDataDownloader();
                IEnumerable<Symbol> symbols = tickers.Select(x => Symbol.Create(x, securityType, market));

                var tradeDates = TradeDates(market, marketHoursDatabase, symbols.First(), fromDate, toDate);
                Dictionary<Symbol, IEnumerable<DateTime>>
                    symbolDates = new(); // Dont request options for dates where option was not issued yet

                IEnumerable<Request> requests;
                if (securityType == SecurityType.Option)
                {
                    Log.Trace($"Resolving Equity Ticker to Option Contracts...");
                    foreach (DateTime dt in tradeDates)
                    {
                        // Log.Trace($"Requesting {symbols.Count()} symbols for {dt}...");
                        var optionSymbols = GetOptionSymbols(dataDirectory, market, resolutionString, downloader, symbols, dt).ToList();
                        Log.Trace($"{dt}: {optionSymbols.Count} Contracts from {symbols.Count()} underlyings");
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

                    requests = symbolDates.SelectMany(kvp => tickTypes.SelectMany(tickType => kvp.Value.Select(dt =>
                        new Request
                        {
                            Symbol = kvp.Key,
                            Date = dt,
                            Resolution = resolution,
                            TickType = tickType
                        }))).ToList();
                }
                else
                {
                    requests = symbols.SelectMany(symbol =>
                        {
                            var dates = TradeDates(market, marketHoursDatabase, symbol, fromDate, toDate);
                            return tickTypes.SelectMany(tickType => dates.Select(dt => new Request
                            {
                                Symbol = symbol,
                                Date = dt,
                                Resolution = resolution,
                                TickType = tickType
                            }));
                        })
                        .ToList();
                }

                int completedRequests = 0;
                int nRequests = requests.Count();
                double previousProgress = 0.0;

                Dictionary<TickType, LeanDataWriter> writers = new();
                foreach (TickType tickType in tickTypes)
                {
                    writers.Add(tickType,
                        new LeanDataWriter(dataDirectory, resolution, securityType, tickType, _diskDataCacheProvider));
                }

                // Build a mapping of each (date, underlying, tickType) to the requests that affect it
                var groupToRequests = new Dictionary<DayGroupKey, List<Request>>();

                foreach (var request in requests)
                {
                    var underlying = Underlying(request.Symbol);
                    var groupKey = new DayGroupKey
                    {
                        Date = request.Date,
                        Underlying = underlying,
                        TickType = request.TickType
                    };

                    if (!groupToRequests.ContainsKey(groupKey))
                    {
                        groupToRequests[groupKey] = new List<Request>();
                    }

                    if (!groupToRequests[groupKey].Contains(request))
                    {
                        groupToRequests[groupKey].Add(request);
                    }
                }

                Log.Trace(
                    $"PolygonDownloader: Grouped {nRequests} requests into {groupToRequests.Count} day-groups for batch writing");

                // Track downloaded data grouped by (date, underlying, tickType)
                var downloadedDataByGroup =
                    new ConcurrentDictionary<DayGroupKey, ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>>();
                var completedRequestsByGroup = new ConcurrentDictionary<DayGroupKey, int>();
                var processedSymbolsByGroup = new ConcurrentDictionary<DayGroupKey, ConcurrentBag<Symbol>>();

                // Initialize counters for each group
                foreach (var kvp in groupToRequests)
                {
                    completedRequestsByGroup[kvp.Key] = 0;
                    downloadedDataByGroup[kvp.Key] = new ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>();
                    processedSymbolsByGroup[kvp.Key] = new ConcurrentBag<Symbol>();
                }

                int lastBulkFlushAt = 0;

                try
                {
                    foreach (var groupEntry in groupToRequests)
                    {
                        var groupKey = groupEntry.Key;
                        var groupRequests = groupEntry.Value;

                        // Initialize tracking structures for this group before processing it
                        completedRequestsByGroup[groupKey] = 0;
                        downloadedDataByGroup[groupKey] = new ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>();
                        processedSymbolsByGroup[groupKey] = new ConcurrentBag<Symbol>();

                        Log.Trace(
                            $"PolygonDownloader: Processing group {groupKey} ({groupRequests.Count} requests)...");

                        Parallel.ForEach(groupRequests, new ParallelOptions { MaxDegreeOfParallelism = nClients },
                            request =>
                            {
                                var writer = writers[request.TickType];
                                var underlying = Underlying(request.Symbol);
                                var entryExists = writer.FileEntryExists(request.Date, request.Symbol);
                                var entrySize = writer.FileEntrySize(request.Date, request.Symbol);

                                if ((
                                        skipFilled && entryExists && entrySize > 0
                                    ) || (
                                        skipEmpty && entryExists && entrySize == 0
                                    ))
                                {
                                    Interlocked.Increment(ref completedRequests);

                                    // Mark the group for this request as having one more completed request
                                    var groupKey = new DayGroupKey
                                        { Date = request.Date, Underlying = underlying, TickType = request.TickType };
                                    if (completedRequestsByGroup.ContainsKey(groupKey))
                                    {
                                        var completed =
                                            completedRequestsByGroup.AddOrUpdate(groupKey, 1, (k, v) => v + 1);
                                        CheckAndWriteGroup(groupKey, completed, groupToRequests, downloadedDataByGroup,
                                            processedSymbolsByGroup, writer, dataDirectory, resolution,
                                            _diskDataCacheProvider);
                                    }

                                    MaybeBulkFlush(ref completedRequests, ref lastBulkFlushAt, groupToRequests,
                                        downloadedDataByGroup, processedSymbolsByGroup,
                                        writers, dataDirectory, resolution, _diskDataCacheProvider, flushInterval);

                                    return;
                                }

                                // For the trade date, check if the file has any entries. If not, reload and overwrite if any data came back, otherwise skip.
                                if (skipModifiedSince != null &&
                                    (writer.EntryLastModified(request.Date, request.Symbol) ?? DateTime.MinValue) >=
                                    skipModifiedSince)
                                {
                                    Interlocked.Increment(ref completedRequests);

                                    // Mark the group for this request as having one more completed request
                                    var groupKey = new DayGroupKey
                                        { Date = request.Date, Underlying = underlying, TickType = request.TickType };
                                    if (completedRequestsByGroup.ContainsKey(groupKey))
                                    {
                                        var completed =
                                            completedRequestsByGroup.AddOrUpdate(groupKey, 1, (k, v) => v + 1);
                                        CheckAndWriteGroup(groupKey, completed, groupToRequests, downloadedDataByGroup,
                                            processedSymbolsByGroup, writer, dataDirectory, resolution,
                                            _diskDataCacheProvider);
                                    }

                                    MaybeBulkFlush(ref completedRequests, ref lastBulkFlushAt, groupToRequests,
                                        downloadedDataByGroup, processedSymbolsByGroup,
                                        writers, dataDirectory, resolution, _diskDataCacheProvider, flushInterval);

                                    return;
                                }

                                var securityExchangeHours =
                                    marketHoursDatabase.GetExchangeHours(market, symbols.First(), securityType);
                                var exchangeTimeZone = securityExchangeHours.TimeZone;
                                var dataTimeZone =
                                    marketHoursDatabase.GetDataTimeZone(market, request.Symbol, securityType);

                                // Download the data
                                var startUtc = request.Date.Date.Add(TimeSpan.FromHours(0))
                                    .ConvertToUtc(exchangeTimeZone);
                                var endUtc = request.Date.Date.Add(TimeSpan.FromHours(20))
                                    .ConvertToUtc(exchangeTimeZone);
                                var data = downloader.Get(new DataDownloaderGetParameters(request.Symbol, resolution,
                                        startUtc, endUtc, request.TickType))
                                    .Select(x =>
                                        {
                                            x.Time = x.Time.ConvertTo(exchangeTimeZone, dataTimeZone);
                                            return x;
                                        }
                                    ).ToList();

                                var groupKeyFinal = new DayGroupKey
                                    { Date = request.Date, Underlying = underlying, TickType = request.TickType };

                                if (completedRequestsByGroup.ContainsKey(groupKeyFinal))
                                {
                                    // Add data for this date to the group
                                    downloadedDataByGroup.GetOrAdd(groupKeyFinal,
                                            _ => new ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>())
                                        .Add(new Tuple<Symbol, IEnumerable<BaseData>>(request.Symbol, data));

                                    // Track symbol
                                    processedSymbolsByGroup.GetOrAdd(groupKeyFinal, _ => new ConcurrentBag<Symbol>())
                                        .Add(request.Symbol);

                                    // Increment completed count for this group
                                    var completed =
                                        completedRequestsByGroup.AddOrUpdate(groupKeyFinal, 1, (k, v) => v + 1);
                                    CheckAndWriteGroup(groupKeyFinal, completed, groupToRequests, downloadedDataByGroup,
                                        processedSymbolsByGroup, writer, dataDirectory, resolution,
                                        _diskDataCacheProvider);
                                }

                                // Increment the completed requests counter using Interlocked.Increment
                                Interlocked.Increment(ref completedRequests);
                                double progressPercentage = 100 * (double)completedRequests / nRequests;
                                // Check if the progress has increased by 0.2% or more
                                if (progressPercentage - previousProgress >= 0.2)
                                {
                                    Console.WriteLine(
                                        $"Progress: {progressPercentage.ToString("0.00", CultureInfo.InvariantCulture)}%. Handled {completedRequests} / {nRequests} requests.");
                                    previousProgress = progressPercentage;
                                }

                                MaybeBulkFlush(ref completedRequests, ref lastBulkFlushAt, groupToRequests,
                                    downloadedDataByGroup, processedSymbolsByGroup,
                                    writers, dataDirectory, resolution, _diskDataCacheProvider, flushInterval);
                            });

                        // After all requests in the group finish, free the group's memory explicitly
                        downloadedDataByGroup.TryRemove(groupKey, out _);
                        processedSymbolsByGroup.TryRemove(groupKey, out _);
                        completedRequestsByGroup.TryRemove(groupKey, out _);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"PolygonDownloader: Error during parallel downloads: {ex}");
                    throw;
                }

                Console.WriteLine(
                    $"PolygonDownloader: Download completed. Wrote {groupToRequests.Count} groups to disk.");
                Log.Trace("PolygonDownloader: All operations completed.");
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

        private static void CheckAndWriteGroup(
            DayGroupKey groupKey,
            int completed,
            Dictionary<DayGroupKey, List<Request>> groupToRequests,
            ConcurrentDictionary<DayGroupKey, ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>>
                downloadedDataByGroup,
            ConcurrentDictionary<DayGroupKey, ConcurrentBag<Symbol>> processedSymbolsByGroup,
            LeanDataWriter writer,
            string dataDirectory,
            Resolution resolution,
            IDataCacheProvider diskDataCacheProvider
            )
        {
            if (!groupToRequests.ContainsKey(groupKey))
            {
                return;
            }

            int totalForGroup = groupToRequests[groupKey].Count;

            if (completed == totalForGroup)
            {
                Log.Trace($"PolygonDownloader: Group {groupKey} completed ({completed}/{totalForGroup}).");

                var groupData = downloadedDataByGroup[groupKey].Where(t => t.Item2.Any()).Select(t => t.Item2).ToList();
                var groupSymbols = processedSymbolsByGroup[groupKey].Distinct().ToHashSet();

                if (groupData.Any())
                {
                    Log.Trace(
                        $"PolygonDownloader: Writing fetched data to disk for: Group {groupKey}; date={groupKey.Date}; # groupData={groupData.Count}; # groupSymbols={groupSymbols.Count}");
                    writer.Write(groupData.Select(d => d.OrderBy(x => x.Time)));
                }

                // Write empty files for symbols that were processed but had no data
                LeanData.WriteEmptyFileIfNotExists(dataDirectory, diskDataCacheProvider,
                    new[] { groupKey.Date }, groupSymbols, resolution, groupKey.TickType);

                // Free memory for this group now that it has been written
                downloadedDataByGroup.TryRemove(groupKey, out _);
                processedSymbolsByGroup.TryRemove(groupKey, out _);

                Log.Trace(
                    $"PolygonDownloader: Group {groupKey} write complete. date={groupKey.Date}; # groupData={groupData.Count}; # groupSymbols={groupSymbols.Count}");
            }
        }

        /// <summary>
        /// Checks whether a bulk-flush threshold (every 1,000 completed requests) has been crossed
        /// and, if so, writes all fully-completed groups to disk and releases their memory.
        /// </summary>
        private static void MaybeBulkFlush(
            ref int completedRequests,
            ref int lastBulkFlushAt,
            Dictionary<DayGroupKey, List<Request>> groupToRequests,
            ConcurrentDictionary<DayGroupKey, ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>>
                downloadedDataByGroup,
            ConcurrentDictionary<DayGroupKey, ConcurrentBag<Symbol>> processedSymbolsByGroup,
            Dictionary<TickType, LeanDataWriter> writers,
            string dataDirectory,
            Resolution resolution,
            IDataCacheProvider diskDataCacheProvider,
            int flushInterval = 1000
            )
        {
            // Snap current values to avoid race-induced double-flush for the same threshold
            int current = completedRequests;
            int currentThreshold = (current / flushInterval) * flushInterval;

            if (currentThreshold <= lastBulkFlushAt || currentThreshold == 0)
            {
                return;
            }

            // Use Interlocked.CompareExchange so only one thread performs the flush per threshold
            if (Interlocked.CompareExchange(ref lastBulkFlushAt, currentThreshold, currentThreshold - flushInterval) !=
                currentThreshold - flushInterval)
            {
                return; // another thread already took responsibility for this threshold
            }

            Log.Trace(
                $"PolygonDownloader: Bulk-flush triggered at {currentThreshold} completed requests. Flushing completed groups...");

            foreach (var kvp in groupToRequests)
            {
                var groupKey = kvp.Key;
                if (!downloadedDataByGroup.ContainsKey(groupKey))
                {
                    continue; // already flushed by CheckAndWriteGroup
                }

                // Race-safe: only flush if we can still remove it (another thread may beat us)
                if (!downloadedDataByGroup.TryRemove(groupKey, out var groupBag))
                {
                    continue;
                }

                // Put a fresh bag back immediately so ongoing downloads can still add to this group
                downloadedDataByGroup.GetOrAdd(groupKey,
                    _ => new ConcurrentBag<Tuple<Symbol, IEnumerable<BaseData>>>());

                var writer = writers[groupKey.TickType];

                var groupData = groupBag.Where(t => t.Item2.Any()).Select(t => t.Item2).ToList();
                if (groupData.Any())
                {
                    writer.Write(groupData.Select(d => d.OrderBy(x => x.Time)));
                }

                if (processedSymbolsByGroup.TryRemove(groupKey, out var symbolsBag))
                {
                    // Put a fresh bag back for future symbols in this group
                    processedSymbolsByGroup.GetOrAdd(groupKey, _ => new ConcurrentBag<Symbol>());

                    var groupSymbols = symbolsBag.Distinct().ToHashSet();
                    LeanData.WriteEmptyFileIfNotExists(dataDirectory, diskDataCacheProvider,
                        new[] { groupKey.Date }, groupSymbols, resolution, groupKey.TickType);
                }

                Log.Trace($"PolygonDownloader: Bulk-flush wrote group {groupKey}.");
            }
        }
    }
}
