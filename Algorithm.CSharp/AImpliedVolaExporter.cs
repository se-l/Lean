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
using System.Collections.Generic;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Securities;
using System.Linq;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Data.Market;
using QuantConnect.Scheduling;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class AImpliedVolaExporter : Foundations
    {
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            // Configurable Settings
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(2023, 5, 5);
            SetEndDate(2023, 7, 28);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            int volatilityPeriodDays = 5;

            SetSecurityInitializer(new SecurityInitializerIVExporter(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), volatilityPeriodDays));

            AssignCachedFunctions();

            // Subscriptions
            spy = AddEquity("SPY", resolution).Symbol;
            hedgeTicker = new List<string> { "SPY" };
            optionTicker = new List<string> { "HPE", "IPG", "AKAM", "AOS", "MO", "FL", "AES", "LNT", "A", "ALL", "ARE", "ZBRA", "APD", "ALLE", "ZTS", "ZBH", "PFE" };
            optionTicker = new List<string> { "HPE" };
            ticker = optionTicker.Concat(hedgeTicker).ToList();


            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: resolution, fillForward: false);

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    options.Add(option);
                    subscriptions += AddOptionIfScoped(option);
                }
            }

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            pfRisk = PortfolioRisk.E(this);

            // Scheduled functions
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(new TimeSpan(1, 0, 0)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(new TimeSpan(16, 0, 0)), WriteIV);

            securityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, spy, SecurityType.Equity);
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(securityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(1).First();
            Log($"WarmUp TimeSpan: {timeSpan}");
            SetWarmUp(timeSpan);
        }
        public override void OnData(Slice data)
        {
            // Add another instance of IVAsk Bid Windows trade trade only and save to disk under iv_deals. Trades better be stored in Tick data, not second data!!!!
            // Generate Tick  iv trades.
            if (IsWarmingUp) return;
            Symbol symbol;

            foreach (KeyValuePair<Symbol, QuoteBar> kvp in data.QuoteBars)
            {
                symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVBids[symbol].Update(kvp.Value);
                    IVAsks[symbol].Update(kvp.Value);
                    RollingIVBid[symbol].Update(IVBids[symbol].Current);
                    RollingIVAsk[symbol].Update(IVAsks[symbol].Current);
                }
            }

            foreach (KeyValuePair<Symbol, TradeBar> kvp in data.Bars)
            {
                symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVTrades[symbol].Update(kvp.Value);
                    RollingIVTrade[symbol].Update(IVTrades[symbol].Current);
                }
            }
        }

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return midPriceUnderlying > 0
                //&& symbol.ID.Date > Time + TimeSpan.FromDays(0)
                && symbol.ID.OptionStyle == OptionStyle.American
                //&& symbol.ID.StrikePrice >= midPriceUnderlying * 0.9m
                //&& symbol.ID.StrikePrice <= midPriceUnderlying * 1.1m
                ;
        }

        public IEnumerable<BaseData> GetLastKnownPricesTradeOrQuote(Security security)
        {
            Symbol symbol = security.Symbol;
            if (!HistoryRequestValid(symbol) || HistoryProvider == null)
            {
                return Enumerable.Empty<BaseData>();
            }

            var result = new Dictionary<TickType, BaseData>();
            Resolution? resolution = null;
            Func<int, bool> requestData = period =>
            {
                var historyRequests = CreateBarCountHistoryRequests(new[] { symbol }, period)
                    .Select(request =>
                    {
                        // For speed and memory usage, use Resolution.Minute as the minimum resolution
                        request.Resolution = (Resolution)Math.Max((int)Resolution.Minute, (int)request.Resolution);
                        // force no fill forward behavior
                        request.FillForwardResolution = null;

                        resolution = request.Resolution;
                        return request;
                    })
                    // request only those tick types we didn't get the data we wanted
                    .Where(request => !result.ContainsKey(request.TickType))
                    .ToList();
                foreach (var slice in History(historyRequests))
                {
                    for (var i = 0; i < historyRequests.Count; i++)
                    {
                        var historyRequest = historyRequests[i];
                        var data = slice.Get(historyRequest.DataType);
                        if (data.ContainsKey(symbol))
                        {
                            // keep the last data point per tick type
                            result[historyRequest.TickType] = (BaseData)data[symbol];
                        }
                    }
                }
                // true when all history requests tick types have a data point
                return historyRequests.All(request => result.ContainsKey(request.TickType));
            };

            if (!requestData(5))
            {
                if (resolution.HasValue)
                {
                    // If the first attempt to get the last know price returns null, it maybe the case of an illiquid security.
                    // Use Quote data to return MidPrice
                    var periods = Periods(security.Resolution, days: 5);
                    requestData(periods);
                }
                else
                {
                    // this shouldn't happen but just in case
                    Error($"QCAlgorithm.GetLastKnownPrices(): no history request was created for symbol {symbol} at {Time}");
                }
            }
            // return the data ordered by time ascending
            return result.Values.OrderBy(data => data.Time);
        }

        public int AddOptionIfScoped(Symbol option)
        {
            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            foreach (var symbol in contractSymbols)
            {
                if (Securities.ContainsKey(symbol) && Securities[symbol].IsTradable) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                var historyUnderlying = HistoryWrap(symbolUnderlying, 30, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractInScope(symbol, lastClose))
                    {
                        var optionContract = AddOptionContract(symbol, resolution: resolution, fillForward: false);
                        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                        susbcriptions++;
                    }
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"No history for {symbolUnderlying}. Not subscribing to its options." } });
                }
            }
            return susbcriptions;
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec => {
                RemoveUniverseSecurity(sec);
            });

            // Add options that have moved into scope
            options.ForEach(s => AddOptionIfScoped(s));

            PopulateOptionChains();
        }

        public static IEnumerable<TResult> FullOuterJoin<TA, TB, TKey, TResult>(
            IEnumerable<TA> a,
            IEnumerable<TB> b,
            Func<TA, TKey> selectKeyA,
            Func<TB, TKey> selectKeyB,
            Func<TA, TB, TKey, TResult> projection,
            TA defaultA = default(TA),
            TB defaultB = default(TB),
            IEqualityComparer<TKey> cmp = null
            )
        {
            cmp = cmp ?? EqualityComparer<TKey>.Default;
            var alookup = a.ToLookup(selectKeyA, cmp);
            var blookup = b.ToLookup(selectKeyB, cmp);

            var keys = alookup.Select(p => p.Key).Union(blookup.Select(p => p.Key), cmp);

            return
                from key in keys
                from aItem in alookup[key].DefaultIfEmpty(defaultA)
                from bItem in blookup[key].DefaultIfEmpty(defaultB)
                select projection(aItem, bItem, key);
        }

        public void WriteIV()
        {
            ZipStreamWriter streamWriter;

            foreach (var security in Securities.Values)
            {
                if (security.Type != SecurityType.Option) continue;
                Option option = security as Option;
                Symbol symbol = option.Symbol;
                
                var dataDirectory = @"C:\repos\trade\data";
                var filePath = LeanData.GenerateZipFilePath(dataDirectory, symbol, Time, option.Resolution, TickType.Quote).ToString();
                string filePathQuote = filePath.Replace("quote", "iv_quote");
                if (!File.Exists(filePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePathQuote));
                }

                var bid = RollingIVBid[symbol].Window.Where(o => o.Time.Date == Time.Date);
                var ask = RollingIVAsk[symbol].Window.Where(o => o.Time.Date == Time.Date);
                // ['time', 'mid_price_underlying', 'bid_price', 'bid_iv', 'ask_price', 'ask_iv']
                var outerJoin = FullOuterJoin(ask, bid,
                    askItem => askItem.Time,
                    bidItem => bidItem.Time,
                    (askItem, bidItem, time) => new
                    {
                        Time = (int)(time.TimeOfDay.TotalSeconds * 1000),
                        UnderlyingMidPrice = bidItem?.UnderlyingMidPrice ?? askItem.UnderlyingMidPrice,
                        BidPrice = bidItem?.Price,
                        BidIV = bidItem?.IV,
                        AskPrice = askItem?.Price,
                        AskIV = askItem?.IV
                        
                    });

                string csv = ToCsv(outerJoin, new List<string>() { "Time", "UnderlyingMidPrice", "BidPrice", "BidIV", "AskPrice", "AskIV" });
                // remove first header line of csv
                if (!string.IsNullOrEmpty(csv)) 
                {
                    csv = csv.Substring(csv.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                }                

                string underlying = symbol.ID.Underlying.Symbol.ToString();
                string entryName = $"{Time:yyyyMMdd}_{underlying}_{resolution}_iv_quote_american_{symbol.ID.OptionRight}_{symbol.ID.StrikePrice * 10000m}_{symbol.ID.Date:yyyyMMdd}.csv".ToLower();
                using (streamWriter = new ZipStreamWriter(filePathQuote, entryName, overwrite:true))
                {
                    streamWriter.WriteLine(csv);
                }
                RollingIVBid[symbol].Reset();
                RollingIVAsk[symbol].Reset();


                string filePathTrade = filePath.Replace("quote", "iv_trade");
                var trade = RollingIVTrade[symbol].Window.Where(o => o.Time.Date == Time.Date);
                var csvLines = trade.Select(t => new
                {
                    Time = (int)(t.Time.TimeOfDay.TotalSeconds * 1000),
                    t.UnderlyingMidPrice,
                    t.Price,
                    t.IV
                });
                csv = ToCsv(csvLines, new List<string>() { "Time", "UnderlyingMidPrice", "Price", "IV" });
                // remove first header line of csv
                if (!string.IsNullOrEmpty(csv))
                {
                    csv = csv.Substring(csv.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                }

                entryName = $"{Time:yyyyMMdd}_{underlying}_{resolution}_iv_trade_american_{symbol.ID.OptionRight}_{symbol.ID.StrikePrice * 10000m}_{symbol.ID.Date:yyyyMMdd}.csv".ToLower();
                using (streamWriter = new ZipStreamWriter(filePathTrade, entryName, overwrite: true))
                {
                    streamWriter.WriteLine(csv);
                }
                RollingIVTrade[symbol].Reset();
            }
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp)
            {
                return;
            }

            Log($"Time: {Time}");
        }
    }
}
