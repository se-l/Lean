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
using System.Linq;
using System.Diagnostics;
using QuantConnect.Data;
using QuantConnect.Util;
using QuantConnect.Logging;
using System.Threading.Tasks;
using IQFeed.CSharpApiClient;
using QuantConnect.Securities;
using System.Collections.Generic;
using QuantConnect.Configuration;
using QuantConnect.ToolBox.IQFeed;
using IQFeed.CSharpApiClient.Lookup;
using IQFeed.CSharpApiClient.Lookup.Chains;
using IQFeed.CSharpApiClient.Lookup.Chains.Equities;

namespace QuantConnect.ToolBox.IQFeedDownloader
{
    /// <summary>
    /// IQFeed Downloader Toolbox Project For LEAN Algorithmic Trading Engine.
    /// </summary>
    public static class IQFeedDownloaderProgram
    {
        private const int NumberOfClients = 8;

        /// <summary>
        /// Primary entry point to the program. This program only supports EQUITY for now.
        /// </summary>
        public static void IQFeedDownloader(IList<string> tickers, IList<string> resolutions, DateTime fromDate, DateTime toDate, IList<string> tickTypes, string securityType = "Equity")
        {
            IEnumerable<BaseData> data;

            if (!resolutions.Any() || tickers.IsNullOrEmpty())
            {
                Console.WriteLine("IQFeedDownloader ERROR: '--tickers=' or '--resolution=' parameter is missing");
                Console.WriteLine("--tickers=SPY,AAPL");
                Console.WriteLine("--resolutions=Tick/Second/Minute/Hour/Daily");
                Environment.Exit(1);
            }
            try
            {
                // Load settings from command line
                IEnumerable<Resolution> _resolutions = resolutions.Select(resolution => (Resolution)Enum.Parse(typeof(Resolution), resolution));
                IEnumerable<TickType> _tickTypes = tickTypes.Select(tickType => (TickType)Enum.Parse(typeof(TickType), tickType));
                SecurityType _securityType = (SecurityType)Enum.Parse(typeof(SecurityType), securityType);
                var startDate = fromDate.ConvertToUtc(TimeZones.NewYork);
                var endDate = toDate.ConvertToUtc(TimeZones.NewYork);
                endDate = endDate.AddDays(1).AddMilliseconds(-1);

                // Load settings from config.json
                var dataDirectory = Config.Get("data-folder", "../../../Data");
                var userName = Config.Get("iqfeed-username", "username");
                var password = Config.Get("iqfeed-password", "password");
                var productName = Config.Get("iqfeed-productName", "productname");
                var productVersion = Config.Get("iqfeed-version", "productversion");

                // Create an instance of the downloader
                const string market = Market.USA;

                // Connect to IQFeed
                if (!string.IsNullOrEmpty(productName))
                {
                    IQFeedLauncher.Start(userName, password, productName, productVersion);
                }
                else
                {
                    Log.Trace("No IQFeed product name provided. Assuming IQFeed is already running.");
                }
                var lookupClient = LookupClientFactory.CreateNew(NumberOfClients);
                lookupClient.Connect();

                // Create IQFeed downloader instance
                var universeProvider = new IQFeedDataQueueUniverseProvider();
                var historyProvider = new IQFeedFileHistoryProvider(lookupClient, universeProvider, MarketHoursDatabase.FromDataFolder());
                var downloader = new IQFeedDataDownloader(historyProvider);
                var quoteDownloader = new IQFeedDataDownloader(historyProvider);

                var symbols = Enumerable.Empty<Symbol>();

                switch (_securityType)
                {
                    case SecurityType.Option:
                        // Resolve option tickers
                        foreach (string ticker in tickers)
                        {
                            Symbol option = Symbol.Create(ticker, SecurityType.Option, market);
                            IEnumerable<EquityOption> optionChain = historyProvider.GetIndexEquityOptionChain(option, startDate, endDate);
                            foreach (var optionContract in optionChain)
                            {
                                OptionRight optionRight = optionContract.Side == OptionSide.Call ? OptionRight.Call : OptionRight.Put;
                                // Defaulting to American style in abscence of definition in EquityOption type.
                                var optionContractSymbol = Symbol.CreateOption(option.Underlying, market, OptionStyle.American, optionRight, (decimal)optionContract.StrikePrice, optionContract.Expiration);
                                symbols = symbols.Append(optionContractSymbol);
                            }
                        }
                        break;
                    case SecurityType.Equity:
                        symbols = symbols.Concat(tickers.Select(t => Symbol.Create(t, SecurityType.Equity, market)));
                        break;
                    default:
                        throw new NotImplementedException($"Security type {securityType} is not supported.");
                }

                var requests = _resolutions.SelectMany(r => symbols.Select(s => new { Symbol = s, Resolution = r })).ToList();

                var sw = Stopwatch.StartNew();
                foreach (TickType _tickType in _tickTypes)
                {
                    Parallel.ForEach(requests, new ParallelOptions { MaxDegreeOfParallelism = NumberOfClients }, request =>
                    {
                        var writer = new LeanDataWriter(request.Resolution, request.Symbol, dataDirectory, _tickType);
                        data = quoteDownloader.Get(new DataDownloaderGetParameters(request.Symbol, request.Resolution, startDate, endDate, _tickType));
                        writer.Write(data);

                        // Caution TickType.Quote:
                        // Still as of 2023-05-11, IQFeed still does only provide quote ticks when a trade happened. For options, that's especially limiting given most contract don't actually trade througout the day
                        // while implied volatility is derived from a contracts bid/ask prices.
                        // https://forums.iqfeed.net/index.cfm?page=topic&topicID=5679
                    });
                }                
                sw.Stop();

                Log.Trace($"IQFeedDownloader: Completed successfully in {sw.Elapsed}!");
            }
            catch (Exception err)
            {
                Log.Error(err);
            }
        }
    }
}
