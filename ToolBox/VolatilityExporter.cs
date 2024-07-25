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
using System.IO;
using System.Linq;
using QuantConnect.Securities;
using QuantConnect.Configuration;
using Newtonsoft.Json;
using QuantConnect.Data.Market;
using QuantConnect.Data;
using QuantConnect.Util;
using System.Collections.Generic;
using QuantConnect.Logging;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Data.Auxiliary;
using QLNet;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using QuantConnect.Algorithm.CSharp.Core;

namespace QuantConnect.ToolBox
{
    class VolatilityTradeBar
    {
        public DateTime Time;
        public DateTime EndTime;
        public Symbol Symbol;
        public decimal UnderlyingMidPrice;
        public decimal Price;
        public decimal IV;
        public decimal? Delta;

        public VolatilityTradeBar(DateTime time, Symbol symbol, decimal underlyingMidPrice, decimal price, decimal iv, decimal? delta = null)
        {
            Time = time;
            EndTime = time + TimeSpan.FromSeconds(1);
            Symbol = symbol;
            UnderlyingMidPrice = underlyingMidPrice;
            Price = price;
            IV = iv;
            Delta = delta;
        }

        public string ToIVTradeString()
        {
            var secondsSinceMidnight = EndTime.TimeOfDay.TotalSeconds * 1_000;  // + 1 as it becomes bar closing time
            return $"{secondsSinceMidnight},{UnderlyingMidPrice},{Price},{IV},{Delta}";
        }
    }
    /// <summary>
    /// Base tool for pulling data from a remote source and updating existing csv file.
    /// </summary>
    public class VolatilityExporter
    {
        public FoundationsConfig Cfg;
        public double accuracy = 1e-4;
        public bool extendedHours = true;
        public bool isInternalFeed = true;
        public bool fillForward = false;

        /// <summary>
        /// Update existing symbol properties database
        /// </summary>
        public void Run(List<string> tickers, DateTime startDate, DateTime endDate, Resolution resolution = Resolution.Second, int nThreads = 16)
        {
            Log.Trace($"Running VolatilityExporter... tickers={string.Join(",", tickers)}, startDate={startDate}, endDate={endDate}, resolution={resolution}, nThreads={nThreads}");
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText("FoundationsConfig.json"));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            var marketHoursDatabase = MarketHoursDatabase.FromDataFolder();
            string dataDirectory = Config.Get("data-folder");

            var dataProvider = Composer.Instance.GetExportedValueByTypeName<IDataProvider>(Config.Get("data-provider", "DefaultDataProvider"));
            var mapFileProvider = new LocalDiskMapFileProvider();
            mapFileProvider.Initialize(dataProvider);
            //var _mapFileProvider = new LocalDiskMapFileProvider();
            IDataCacheProvider _dataCacheProvider = new ZipDataCacheProvider(dataProvider, isDataEphemeral: false);

            var OptionChainProvider = new CachingOptionChainProvider(new BacktestingOptionChainProvider(_dataCacheProvider, mapFileProvider));
            Dictionary<Symbol, VanillaOption> euOptions = new();
            Dictionary<Symbol, BlackScholesMertonProcess> bsmProcesses = new();
            Dictionary<Symbol, SimpleQuote> spotQuote = new();
            Dictionary<Symbol, Handle<Quote>> spotQuoteHandle = new();
            var riskFreeRate = new SimpleQuote((double)Cfg.DiscountRateMarket);
            var riskFreeRateHandle = new Handle<Quote>(riskFreeRate);
            var dayCounter = new Actual365Fixed();
            var calendar = new UnitedStates(UnitedStates.Market.NYSE);

            foreach (string ticker in tickers)
            {
                Symbol underlying = Symbol.Create(ticker, SecurityType.Equity, Market.USA);
                double dividendYield = Cfg.DividendYield.TryGetValue(underlying, out dividendYield) ? dividendYield : Cfg.DividendYield["_"];
                var dividendYieldQuote = new SimpleQuote(dividendYield);
                var dividendYieldQuoteHandle = new Handle<Quote>(dividendYieldQuote);

                //SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
                foreach (DateTime dateTime in Time.EachTradeableDay(marketHoursDatabase.GetExchangeHours(Market.USA, ticker, SecurityType.Equity), startDate, endDate))
                {
                    Date calculationDate = new(dateTime.Day, dateTime.Month, dateTime.Year);
                    SetEvaluationDateToCalcDate(calculationDate);

                    var marketHoursDbEntry = marketHoursDatabase.GetEntry(Market.USA, underlying, SecurityType.Equity);
                    var dataTimeZone = marketHoursDbEntry.DataTimeZone;
                    var exchangeTimeZone = marketHoursDbEntry.ExchangeHours.TimeZone;

                    // Fetching equity data
                    var subscriptionDataConfigQuoteBar = new SubscriptionDataConfig(typeof(QuoteBar), underlying, resolution, dataTimeZone, exchangeTimeZone, fillForward, extendedHours, isInternalFeed, isFilteredSubscription: false);
                    var leanDataReaderQuotes = new LeanDataReader(subscriptionDataConfigQuoteBar, underlying, resolution, dateTime, dataDirectory).Parse();

                    var canonicalOptionSymbol = Symbol.CreateCanonicalOption(underlying, Market.USA, $"?{underlying}");
                    var optionSymbols = OptionChainProvider.GetOptionContractList(canonicalOptionSymbol, dateTime);

                    // Quotes

                    ConcurrentDictionary<Symbol, List<VolatilityBar>> IVQuotes = new();
                    foreach (var optionSymbol in optionSymbols)
                    {
                        var rateTSHandle = new Handle<YieldTermStructure>(new FlatForward(dateTime, riskFreeRateHandle, dayCounter));
                        var dividendTSHandle = new Handle<YieldTermStructure>(new FlatForward(dateTime, dividendYieldQuoteHandle, dayCounter));
                        var volatilityTSHandle = new Handle<BlackVolTermStructure>(new BlackConstantVol(calculationDate, calendar, 0, dayCounter));

                        IVQuotes[optionSymbol] = new List<VolatilityBar>();
                        spotQuote[optionSymbol] = new SimpleQuote(0);
                        spotQuoteHandle[optionSymbol] = new Handle<Quote>(spotQuote[optionSymbol]);
                        bsmProcesses[optionSymbol] = GetBsmProcess(spotQuoteHandle[optionSymbol], rateTSHandle, dividendTSHandle, volatilityTSHandle);
                        euOptions[optionSymbol] = CreateEuOption(optionSymbol, bsmProcesses[optionSymbol]);
                    }                    

                    Parallel.ForEach(optionSymbols, new ParallelOptions { MaxDegreeOfParallelism = nThreads }, optionSymbol =>
                    {
                        IEnumerator<BaseData> underlyingQuoteBarsEnumerator = leanDataReaderQuotes.AsEnumerable().GetEnumerator();
                        underlyingQuoteBarsEnumerator.MoveNext();  // Initialize the enumerator

                        var subscriptionDataConfigOptionQuotes = new SubscriptionDataConfig(typeof(QuoteBar), optionSymbol, resolution, dataTimeZone, exchangeTimeZone, fillForward, extendedHours, isInternalFeed, isFilteredSubscription: false);
                        var leanDataReaderOptionQuotes = new LeanDataReader(subscriptionDataConfigOptionQuotes, optionSymbol, resolution, dateTime, dataDirectory);

                        leanDataReaderOptionQuotes.Parse().DoForEach(baseData =>
                        {
                            var quoteBar = baseData as QuoteBar;
                            var equityQuoteBar = CurrentUnderlyingQuoteBar(underlyingQuoteBarsEnumerator, quoteBar.Time);
                            //double spotMidOpen = (double)(equityQuoteBar.Bid.Open + equityQuoteBar.Ask.Open) / 2;
                            double spotMidClose = (double)(equityQuoteBar.Bid.Close + equityQuoteBar.Ask.Close) / 2;

                            //SetQuote(spotQuote[optionSymbol], spotMidOpen);
                            //var bidIvOpen = (decimal)IV(euOptions[optionSymbol], (double)quoteBar.Bid.Open, bsmProcesses[optionSymbol]);
                            //var askIvOpen = (decimal)IV(euOptions[optionSymbol], (double)quoteBar.Ask.Open, bsmProcesses[optionSymbol]);

                            SetQuote(spotQuote[optionSymbol], spotMidClose);
                            var bidIvClose = (decimal)IV(euOptions[optionSymbol], (double)(quoteBar?.Bid?.Close ?? 0), bsmProcesses[optionSymbol]);
                            var askIvClose = (decimal)IV(euOptions[optionSymbol], (double)(quoteBar?.Ask?.Close ?? 0), bsmProcesses[optionSymbol]);

                            //var bidIvHigh = Math.Max(bidIvOpen, bidIvClose);
                            //var bidIvLow = Math.Min(bidIvOpen, bidIvClose);
                            //var askIvHigh = Math.Max(askIvOpen, askIvClose);
                            //var askIvLow = Math.Min(askIvOpen, askIvClose);

                            //var bidIVbar = new Bar(bidIvOpen, bidIvHigh, bidIvLow, bidIvClose);
                            //var askIVbar = new Bar(askIvOpen, askIvHigh, askIvLow, askIvClose);
                            var bidIVbar = new Bar(bidIvClose, bidIvClose, bidIvClose, bidIvClose);
                            var askIVbar = new Bar(askIvClose, askIvClose, askIvClose, askIvClose);

                            IVQuotes[optionSymbol].Add(new VolatilityBar(quoteBar.Time, optionSymbol,
                            bidIVbar, askIVbar,
                            quoteBar.Bid, quoteBar.Ask,
                            equityQuoteBar
                            ));                      
                        });
                    });
                    WriteIV(IVQuotes, dateTime, resolution);
                    IVQuotes.Clear();

                    // Trades
                    ConcurrentDictionary<Symbol, List<VolatilityTradeBar>> IVTrades = new();
                    foreach (var optionSymbol in optionSymbols)
                    {
                        IVTrades[optionSymbol] = new List<VolatilityTradeBar>();
                    }

                    Parallel.ForEach(optionSymbols, new ParallelOptions { MaxDegreeOfParallelism = nThreads }, optionSymbol =>
                    {
                        IEnumerator<BaseData> underlyingQuoteBarsEnumerator = leanDataReaderQuotes.AsEnumerable().GetEnumerator();
                        underlyingQuoteBarsEnumerator.MoveNext();  // Initialize the enumerator

                        var subscriptionDataConfigOption = new SubscriptionDataConfig(typeof(TradeBar), optionSymbol, resolution, dataTimeZone, exchangeTimeZone, fillForward, extendedHours, isInternalFeed, isFilteredSubscription: false);
                        var leanDataReaderOption = new LeanDataReader(subscriptionDataConfigOption, optionSymbol, resolution, dateTime, dataDirectory);

                        leanDataReaderOption.Parse().DoForEach(baseData =>
                        {
                            var tradeBar = baseData as TradeBar;
                            var equityQuoteBar = CurrentUnderlyingQuoteBar(underlyingQuoteBarsEnumerator, tradeBar.Time);
                            decimal spotMidClose = (equityQuoteBar.Bid.Close + equityQuoteBar.Ask.Close) / 2;

                            SetQuote(spotQuote[optionSymbol], (double)spotMidClose);
                            var ivClose = (decimal)IV(euOptions[optionSymbol], (double)tradeBar.Close, bsmProcesses[optionSymbol]);

                            IVTrades[optionSymbol].Add(new VolatilityTradeBar(tradeBar.Time, optionSymbol, spotMidClose, tradeBar.Close, ivClose));
                        });
                    });
                    WriteIV(IVTrades, dateTime, resolution);
                    IVTrades.Clear();

                    bsmProcesses.Clear();
                    euOptions.Clear();
                    spotQuote.Clear();
                    spotQuoteHandle.Clear();
                }
            }
        }

        public static QuoteBar CurrentUnderlyingQuoteBar(IEnumerator<BaseData> enumerator, DateTime now)
        {
            if (enumerator.Current.Time >= now)
            {
                return enumerator.Current as QuoteBar;
            }
            else
            {
                while (enumerator.MoveNext())
                {
                    if (enumerator.Current.Time >= now)
                    {
                        return enumerator.Current as QuoteBar;
                    }
                }
                return null;
            }
        }

        public BlackScholesMertonProcess GetBsmProcess(Handle<Quote> spotQuoteHandle, Handle<YieldTermStructure> rateTSHandle, Handle<YieldTermStructure> dividendTSHandle, Handle<BlackVolTermStructure> volatilityTSHandle)
        {
            return new BlackScholesMertonProcess(spotQuoteHandle, dividendTSHandle, rateTSHandle, volatilityTSHandle);
        }

        public VanillaOption CreateEuOption(Symbol optionSymbol, BlackScholesMertonProcess bsmProcess)
        {
            var maturityDate = new Date(optionSymbol.ID.Date.Day, optionSymbol.ID.Date.Month, optionSymbol.ID.Date.Year);
            var euExercise = new EuropeanExercise(maturityDate);
            var optionType = optionSymbol.ID.OptionRight == OptionRight.Call ? Option.Type.Call : Option.Type.Put;
            var payoff = new PlainVanillaPayoff(optionType, (double)optionSymbol.ID.StrikePrice);
            VanillaOption euOption = new(payoff, euExercise);
            var engine = new AnalyticEuropeanEngine(bsmProcess);
            euOption.setPricingEngine(engine);
            return euOption;
        }

        public void SetEvaluationDateToCalcDate(Date calculationDate)
        {
            // There is considerable performance overhead on setEvaluationDate raising some event within QLNet, therefore only calling if necessary.
            if (Settings.evaluationDate() != calculationDate)
            {
                Settings.setEvaluationDate(calculationDate);
            }
        }

        private void SetQuote(SimpleQuote spotQuote, double quote)
        {
            if (spotQuote.value() != quote)
            {
                spotQuote.setValue(quote);
            }
        }

        public double IV(VanillaOption option, double price, BlackScholesMertonProcess bsmProcess)
        {
            try
            {
                return option.impliedVolatility(price, bsmProcess, accuracy: accuracy);
            }
            catch (Exception e)
            {
                return 0;
                //Log.Trace(e);
            }            
        }

        private static void WriteIV(ConcurrentDictionary<Symbol, List<VolatilityBar>> IVDct, DateTime date, Resolution resolution, string tick_type = "quote")
        {
            if (IVDct.IsEmpty) return;

            var dataDirectory = Config.Get("data-folder");
            Symbol optionSymbol = IVDct.Keys.First();
            Symbol underlying = optionSymbol.Underlying;
            var filePath = LeanData.GenerateZipFilePath(dataDirectory, optionSymbol, date, resolution, TickType.Quote).ToString();
            filePath = filePath.Replace(tick_type, $"iv_{tick_type}");
            if (!File.Exists(filePath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath));
            }

            Dictionary<string, ZipArchive> archives = new();
            if (!archives.ContainsKey(filePath))
            {
                archives[filePath] = ZipFile.Open(filePath, ZipArchiveMode.Update);
            }

            foreach ( var item in IVDct.Keys )
            {
                var symbol = item;
                var bars = IVDct[item];

                var csv = new StringBuilder();
                foreach (var bar in bars)
                {
                    csv.AppendLine(bar.ToIVQuoteString());
                }

                string entryName = $"{date:yyyyMMdd}_{underlying.Value}_{resolution}_iv_{tick_type}_american_{symbol.ID.OptionRight}_{Math.Round(symbol.ID.StrikePrice * 10000m)}_{symbol.ID.Date:yyyyMMdd}.csv".ToLowerInvariant();
                var entry = archives[filePath].GetEntry(entryName) ?? archives[filePath].CreateEntry(entryName);
                var writer = new StreamWriter(entry.Open());
                writer.WriteLine(csv);
                writer.Flush();
                writer.Close();
                writer.Dispose();
            }
            Log.Trace($"Saved IV Quote files for {underlying.Value} on {date}");
            archives.DoForEach(kvp => kvp.Value.Dispose());
        }

        private static void WriteIV(ConcurrentDictionary<Symbol, List<VolatilityTradeBar>> IVDct, DateTime date, Resolution resolution, string tick_type = "trade")
        {
            if (IVDct.IsEmpty) return;

            var dataDirectory = Config.Get("data-folder");
            Symbol optionSymbol = IVDct.Keys.First();
            Symbol underlying = optionSymbol.Underlying;
            var filePath = LeanData.GenerateZipFilePath(dataDirectory, optionSymbol, date, resolution, TickType.Trade).ToString();
            filePath = filePath.Replace(tick_type, $"iv_{tick_type}");
            if (!File.Exists(filePath))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filePath));
            }

            Dictionary<string, ZipArchive> archives = new();
            if (!archives.ContainsKey(filePath))
            {
                archives[filePath] = ZipFile.Open(filePath, ZipArchiveMode.Update);
            }

            foreach (var item in IVDct.Keys)
            {
                var symbol = item;
                var bars = IVDct[item];

                var csv = new StringBuilder();
                foreach (var bar in bars)
                {
                    csv.AppendLine(bar.ToIVTradeString());       
                }
                string entryName = $"{date:yyyyMMdd}_{underlying.Value}_{resolution}_iv_{tick_type}_american_{symbol.ID.OptionRight}_{Math.Round(symbol.ID.StrikePrice * 10000m)}_{symbol.ID.Date:yyyyMMdd}.csv".ToLowerInvariant();
                var entry = archives[filePath].GetEntry(entryName) ?? archives[filePath].CreateEntry(entryName);
                var writer = new StreamWriter(entry.Open());
                writer.WriteLine(csv);
                writer.Flush();
                writer.Close();
                writer.Dispose();
            }
            Log.Trace($"Saved IV Trade files for {underlying.Value} on {date}");
            archives.DoForEach(kvp => kvp.Value.Dispose());
        }
    }
}
