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
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

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
            //SetStartDate(2023, 9, 8);
            //SetEndDate(2023, 9, 8);
            SetStartDate(2023, 9, 14);
            SetEndDate(2023, 9, 14);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            int volatilityPeriodDays = 5;

            SetSecurityInitializer(new SecurityInitializerIVExporter(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPrices), volatilityPeriodDays));

            AssignCachedFunctions();

            // Subscriptions
            optionTicker = new() { "HPE", "IPG", "AKAM", "AOS", "MO", "FL", "AES", "LNT", "PFE", "A", "ALL", "ARE", "ZBRA", "APD", "ALLE", "ZTS", "ZBH" };
            optionTicker = new() { "HPE", "IPG", "AKAM", "PFE" };
            //optionTicker = new() { "PFE" };
            ticker = optionTicker;
            symbolSubscribed = AddEquity(optionTicker.First(), resolution).Symbol;

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

            PfRisk = PortfolioRisk.E(this);

            // Scheduled functions
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(new TimeSpan(1, 0, 0)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(new TimeSpan(16, 0, 0)), WriteIV);

            //SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, equity1, SecurityType.Equity);
            //var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-5), StartDate).TakeLast(1).First();
            //Log($"WarmUp TimeSpan: {timeSpan}");
            //SetWarmUp(timeSpan);
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
                    RollingIVBid[symbol].Update(IVBids[symbol].IVBidAsk);
                    RollingIVAsk[symbol].Update(IVAsks[symbol].IVBidAsk);
                }
            }

            foreach (KeyValuePair<Symbol, TradeBar> kvp in data.Bars)
            {
                symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVTrades[symbol].Update(kvp.Value);
                    IVTrades[symbol].SetDelta();
                    RollingIVTrade[symbol].Update(IVTrades[symbol].Current);
                }
            }
        }

        public bool ContractInScope(Symbol symbol)
        {
            return symbol.ID.Date > Time.Date && symbol.ID.OptionStyle == OptionStyle.American;
        }

        public int AddOptionIfScoped(Symbol option)
        {
            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            foreach (var symbol in contractSymbols)
            {
                if (Securities.ContainsKey(symbol) && Securities[symbol].IsTradable) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                if (ContractInScope(symbol))
                {
                    var optionContract = AddOptionContract(symbol, resolution: resolution, fillForward: false);
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                    susbcriptions++;
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $" Not scoped {symbol}." } });
                }
            }
            return susbcriptions;
        }
        public void UpdateUniverseSubscriptions()
        {
            // Add options that have moved into scope
            options.DoForEach(s => AddOptionIfScoped(s));
        }

        public static IEnumerable<TResult> FullOuterJoin<TA, TB, TKey, TResult>(
            IEnumerable<TA> a,
            IEnumerable<TB> b,
            Func<TA, TKey> selectKeyA,
            Func<TB, TKey> selectKeyB,
            Func<TA, TB, TKey, TResult> projection,
            TA defaultA = default,
            TB defaultB = default,
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
                if (security.Type != SecurityType.Option || security.IsDelisted) continue;
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
                        AskIV = askItem?.IV,
                        BidDelta = bidItem?.Delta,
                        AskDelta = askItem?.Delta
                    });

                string csv = ToCsv(outerJoin, new List<string>() { "Time", "UnderlyingMidPrice", "BidPrice", "BidIV", "AskPrice", "AskIV", "BidDelta", "AskDelta" });
                // remove first header line of csv
                if (!string.IsNullOrEmpty(csv)) 
                {
                    csv = csv.Substring(csv.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                }                

                string underlying = symbol.ID.Underlying.Symbol.ToString();
                string entryName = $"{Time:yyyyMMdd}_{underlying}_{resolution}_iv_quote_american_{symbol.ID.OptionRight}_{Math.Round(symbol.ID.StrikePrice * 10000m)}_{symbol.ID.Date:yyyyMMdd}.csv".ToLowerInvariant();
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
                    t.IV,
                    t.Delta
                });
                csv = ToCsv(csvLines, new List<string>() { "Time", "UnderlyingMidPrice", "Price", "IV", "Delta" });
                // remove first header line of csv
                if (!string.IsNullOrEmpty(csv))
                {
                    csv = csv.Substring(csv.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                }

                entryName = $"{Time:yyyyMMdd}_{underlying}_{resolution}_iv_trade_american_{symbol.ID.OptionRight}_{Math.Round(symbol.ID.StrikePrice * 10000m)}_{symbol.ID.Date:yyyyMMdd}.csv".ToLowerInvariant();
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
