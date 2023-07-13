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
            SetEndDate(2023, 7, 7);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            int volatilitySpan = 30;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPrices), volatilitySpan));

            AssignCachedFunctions();

            // Subscriptions
            spy = AddEquity("SPY", resolution).Symbol;
            hedgeTicker = new List<string> { "SPY" };
            //optionTicker = new List<string> { "HPE", "IPG", "AKAM", "AOS", "MO", "FL", "AES", "LNT", "A", "ALL", "ARE", "ZBRA", "APD", "ALLE", "ZTS", "ZBH", "PFE" };
            optionTicker = new List<string> { "AES" };
            ticker = optionTicker.Concat(hedgeTicker).ToList();

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: Resolution.Second, fillForward: false);

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
            if (IsWarmingUp) return;

            foreach (KeyValuePair<Symbol, QuoteBar> kvp in data.QuoteBars)
            {
                Symbol symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVBids[symbol].Update(kvp.Value);
                    IVAsks[symbol].Update(kvp.Value);
                    RollingIVBid[symbol].Update(IVBids[symbol].Current);
                    RollingIVAsk[symbol].Update(IVAsks[symbol].Current);
                }
            }
        }

        /// <summary>
        /// For a given Symbol, warmup Underlying MidPrices and Option Bid/Ask to calculate implied volatility primarily. Add other indicators where necessary. 
        /// Required for scoping new options as part of the dynamic universe.
        /// Consider moving this into the SecurityInitializer.
        /// </summary>
        public void WarmUpSecurities(ICollection<Security> securities)
        {
            QuoteBar quoteBar;
            Symbol symbol;
            Dictionary<Symbol, decimal> underlyingMidPrice = new();

            var history = History<QuoteBar>(securities.Select(sec => sec.Symbol), 60 * 7 * 5, Resolution.Minute, fillForward: false);

            foreach (DataDictionary<QuoteBar> data in history)
            {
                foreach (KeyValuePair<Symbol, QuoteBar> kvp in data)
                {
                    symbol = kvp.Key;
                    quoteBar = kvp.Value;
                    if (quoteBar.Symbol.SecurityType == SecurityType.Equity)
                    {
                        underlyingMidPrice[symbol] = (quoteBar.Bid.Close + quoteBar.Ask.Close) / 2;
                    }
                    else if (quoteBar.Symbol.SecurityType == SecurityType.Option)
                    {
                        if (underlyingMidPrice.TryGetValue(quoteBar.Symbol.Underlying, out decimal underlyingMidPriceValue))
                        {
                            IVBids[symbol].Update(quoteBar, underlyingMidPriceValue);
                            IVAsks[symbol].Update(quoteBar, underlyingMidPriceValue);
                            RollingIVBid[symbol].Update(IVBids[symbol].Current);
                            RollingIVAsk[symbol].Update(IVAsks[symbol].Current);
                        }
                    }
                }
            };
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
                        var optionContract = AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
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
                var filePath = LeanData.GenerateZipFilePath(dataDirectory, symbol, Time, option.Resolution, TickType.Quote);
                filePath = filePath.ToString();
                filePath = filePath.Replace("quote", "iv");
                if (!File.Exists(filePath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(filePath));
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

                string csv = ToCsv(outerJoin);
                // remove first header line of csv
                if (!string.IsNullOrEmpty(csv)) 
                {
                    csv = csv.Substring(csv.IndexOf(Environment.NewLine) + Environment.NewLine.Length);
                }                

                string underlying = symbol.ID.Underlying.Symbol.ToString();
                string entryName = $"{Time:yyyyMMdd}_{underlying}_{resolution}_iv_american_{symbol.ID.OptionRight}_{symbol.ID.StrikePrice * 10000m}_{symbol.ID.Date:yyyyMMdd}.csv".ToLower();
                using (streamWriter = new ZipStreamWriter(filePath, entryName, overwrite:true))
                {
                    streamWriter.WriteLine(csv);
                }
                RollingIVBid[symbol].Reset();
                RollingIVAsk[symbol].Reset();

            }
        }
    }
}
