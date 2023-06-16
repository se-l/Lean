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
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Orders;
using System.Linq;
using QuantConnect.ToolBox.IQFeed.IQ;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Events;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;
using QuantConnect.Data.Market;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Basic template algorithm simply initializes the date range and cash. This is a skeleton
    /// framework you can use for designing an algorithm.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class AMarketMakeOptionsAlgorithm : Foundations
    {
        private DateTime endOfDay;
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            // Configurable Settings
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(2023, 6, 15);
            SetEndDate(2023, 6, 15);
            SetCash(100000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            if (LiveMode)
            {
                SetOptionChainProvider(new IQOptionChainProvider());
            }

            mmWindow = new MMWindow(new TimeSpan(9, 30, 15), new TimeSpan(15, 58, 0));
            riskLimit = new RiskLimit(40, 100000, 100);
            int volatilitySpan = 30;
            orderType = OrderType.Limit;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPrices), volatilitySpan));

            AssignCachedFunctions();

            // Subscriptions
            spy = AddEquity("SPY", resolution).Symbol;
            hedgeTicker = new List<string> { "SPY" };
            optionTicker = new List<string> { "HPE", "IPG", "AKAM", "AOS", "A", "MO", "FL", "ALL", "ARE", "ZBRA", "AES", "APD", "ALLE", "LNT", "ZTS", "ZBH" };
            //optionTicker = new List<string> { "IPG", "MO", "FL", "ZBRA" };
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

            pfRisks = new LinkedList<PortfolioRisk>();

            // Scheduled functions
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), IsRiskBandExceeded);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.AfterMarketOpen(hedgeTicker[0]), PopulateOptionChains);  // and on SecurityChanges
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), LogRiskSchedule);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(1)), RunSignals);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(1)), UpdateRisk);
            //Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(1)), LogHealth);  // For tick subscriptions
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(mmWindow.End), CancelOpenTickets);

            SetWarmUp(TimeSpan.FromDays(1));

            TickCounterFilter = new TickCounter(this);
            TickCounterOnData = new TickCounter(this);
        }

        public override void OnData(Slice data)
        {
            foreach (KeyValuePair<Symbol, QuoteBar> kvp in data.QuoteBars)
            {
                Symbol symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVBids[symbol].Update(kvp.Value);
                    IVAsks[symbol].Update(kvp.Value);
                    RollingIVBid[symbol].Update(IVBids[symbol]);
                    RollingIVAsk[symbol].Update(IVAsks[symbol]);
                }
            }

            if (IsWarmingUp)return;

            foreach (Symbol symbol in data.QuoteBars.Keys)
            {
                if (symbol.SecurityType == SecurityType.Equity && IsEventNewBidAsk(symbol))
                {
                    PublishEvent(new EventNewBidAsk(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            OrderEvents.Add(orderEvent);
            LogOrderEvent(orderEvent);
            foreach (var tickets in orderTickets.Values)
            {
                tickets.RemoveAll(t => t.Status == OrderStatus.Filled || t.Status == OrderStatus.Canceled || t.Status == OrderStatus.Invalid);
            }
            PublishEvent(orderEvent);
        }

        public void RunSignals()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;

            var signals = GetSignals();
            if (signals.Any())
            {
                signals = FilterSignalByRisk(signals); // once anything is filled. this calcs a lot
                if (signals.Any())
                {
                    {
                        PublishEvent(new EventSignals(signals));
                    }
                }
            }
        }

        public int AddOptionIfScoped(Symbol option)
        {
            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            foreach (var symbol in contractSymbols)
            {
                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                var historyUnderlying = HistoryWrap(symbolUnderlying, 30, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractInScope(symbol, lastClose))
                    {
                        var optionContract = AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
                        susbcriptions++;
                    }
                }
                else
                {
                    Log($"No history for {symbolUnderlying}. Not subscribing to its options.");
                }
            }
            return susbcriptions;
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec => {
                if (
                    !ContractInScope(sec.Symbol)
                    && !Portfolio.ContainsKey(sec.Symbol)
                )
                {
                    Log($"{Time} Removing {sec.Symbol} from the the Universe. Descoped.");
                    orderTickets.GetValueOrDefault(sec.Symbol)?.DoForEach(t => t.Cancel());
                    RemoveSecurity(sec.Symbol);
                }
            });

            // Add options that have moved into scope
            options.DoForEach(s => AddOptionIfScoped(s));
        }

        public void UpdateRisk()
        {
            if (IsWarmingUp) return;

            var pfRisk = PortfolioRisk.E(this);
            pfRisks.AddFirst(pfRisk);
            if (pfRisks.Count > 2)
            {
                pfRisks.RemoveLast();
            }
            if (IsHighPortfolioRisk(pfRisks))
            {
                PublishEvent(new EventHighPortfolioRisk(pfRisk));
            }
        }

        //public void LogHealth()
        //{
        //    if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) { return; }
        //    var d1 = new Dictionary<string, string>
        //    {
        //        { "ts", Time.ToString() },
        //        { "topic", "HEALTH" },
        //        { "TickCountFilterSnap", TickCounterFilter.Snap().ToString() },
        //        { "TickCountFilterTotal", TickCounterFilter.Total.ToString() },
        //        { "TickCountOnDataSnap", TickCounterOnData.Snap().ToString() },
        //        { "TickCountOnDataTotal", TickCounterOnData.Total.ToString() },
        //    };
        //    d1 = d1.ToDictionary(x => x.Key, x => x.Value.ToString());
        //    var d2 = PortfolioRisk.E(this).ToDict().ToDictionary(x => x.Key, x => x.Value.ToString());
        //    string tag = Humanize(d1.Union(d2));
        //    Log(tag);
        //}

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay)
            {
                return;
            }
            var pfRisk = new PortfolioRisk(this, positions: PortfolioRisk.GetPositionsSince(this));
            LogRisk();

            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFees: {Portfolio.TotalFees}");
            Log($"TotalNetProfit: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfit: {Portfolio.TotalUnrealizedProfit}");

            Log($"TotalPortfolioValueMid: {pfRisk.PortfolioValue("Mid")}");
            Log($"TotalPortfolioValueClose: {pfRisk.PortfolioValue("Close")}");
            Log($"TotalPortfolioValueQC: {pfRisk.PortfolioValue("QC")}");
            Log($"TotalPortfolioValueWorst: {pfRisk.PortfolioValue("Worst")}");
            endOfDay = Time.Date;

            IEnumerable<Trade> trades = new List<Trade>();
            pfRisk.Positions.DoForEach(p => { trades = trades.Concat(p.Trades); } );
            ExportToCsv(trades, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_trades_{Time:yyyyMMdd}.csv"));

            //foreach (Symbol symbol_ in IVBidAsk.Keys)
            //{
            //    ExportToCsv(IVBidAsk[symbol_].Window, Path.Combine(Directory.GetCurrentDirectory(), "IV", $"{symbol_}_IV_{Time:yyyyMMdd}.csv"));
            //}
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            base.OnEndOfAlgorithm();
        }

        public override void OnWarmupFinished()
        {
            IEnumerable<OrderTicket> openTransactions = Transactions.GetOpenOrderTickets();
            Log($"Adding Open Transactions to OrderTickets: {openTransactions.Count()}");
            foreach (OrderTicket ticket in openTransactions)
            {
                if (!orderTickets.ContainsKey(ticket.Symbol)) {
                    orderTickets[ticket.Symbol] = new List<OrderTicket>();
                }
                orderTickets[ticket.Symbol].Add(ticket);
            }
            Log($"Transactions Open Orders Count: {Transactions.GetOpenOrders().Count}");

            //// Get order tickets that pass a filter
            var filteredOrderTickets = Transactions.GetOrderTickets();
            Log($"Transactions Filtered Orders Count: {filteredOrderTickets.Count()}");

            // Get all completed orders
            var completedOrders = Transactions.GetOrders();
            Log($"Transactions Completed Orders Count: {completedOrders.Count()}");
        }
    }
}
