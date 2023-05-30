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
using QuantConnect.Algorithm.CSharp.Core.Events;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;


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
            UniverseSettings.Resolution = resolution = Resolution.Minute;
            SetStartDate(2023, 5, 17);
            SetEndDate(2023, 5, 18);
            SetCash(100000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            if (LiveMode)
            {
                SetOptionChainProvider(new IQOptionChainProvider());
            }

            mmWindow = new MMWindow(new TimeSpan(9, 30, 0), new TimeSpan(15, 58, 0));
            minBeta = 0.5; // for paper trading. Should be rather 0.3 at least..
            riskLimit = new RiskLimit(40, 100000, 100);
            int volatilitySpan = 10;
            orderType = OrderType.Limit;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, new FuncSecuritySeeder(GetLastKnownPrices), volatilitySpan));

            AssignCachedFunctions();

            // Subscriptions
            spy = AddEquity("SPY", Resolution.Minute).Symbol;
            hedgeTicker = new List<string> { "SPY" };
            optionTicker = new List<string> { "HPE", "IPG", "AKAM", "AOS", "A", "MO", "FL", "ALL", "ARE", "ZBRA", "AES", "APD", "ALLE", "LNT", "ZTS", "ZBH" };
            optionTicker = new List<string> { "HPE", "AKAM", "FL" };
            ticker = optionTicker.Concat(hedgeTicker).ToList();

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: resolution);

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    options.Add(option);

                    var history = HistoryWrap(equity.Symbol, 30, Resolution.Daily).ToList();
                    if (history.Any())
                    {
                        var latest_close = history.Last().Close;
                        var contract_symbols = OptionChainProvider.GetOptionContractList(ticker, Time);
                        foreach (var symbol in contract_symbols)
                        {
                            if (Time + TimeSpan.FromDays(60) < symbol.ID.Date && symbol.ID.Date < Time + TimeSpan.FromDays(2 * 365) &&
                                symbol.ID.OptionStyle == OptionStyle.American &&
                                latest_close * (decimal)0.9 < symbol.ID.StrikePrice && symbol.ID.StrikePrice < latest_close * (decimal)1.1)
                            {
                                var optionContract = AddOptionContract(symbol, resolution: resolution);
                                subscriptions++;
                            }
                        }
                    }
                    else
                    {
                        Debug($"No history for {ticker}. Not subscribing to any relating options.");
                    }
                }
            }

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            simulated_missed_gain = 0;

            SetWarmUp((int)(volatilitySpan * 1.5), Resolution.Daily);
            //SetWarmUp(3, Resolution.Daily);
            SymbolSodPriceMid = new Dictionary<Symbol, decimal>();
            slices = new LinkedList<Slice>(); // bad design as some slices might not contain the data checked for later. Need Symbol specific ones...
            pfRisks = new LinkedList<PortfolioRisk>();

            // Scheduled functions
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), IsRiskBandExceeded);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.AfterMarketOpen(hedgeTicker[0]), PopulateOptionChains);  // and on SecurityChanges
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), LogRiskSchedule);
    }

        public override void OnData(Slice data)
        {
            slices.AddFirst(data);
            if (slices.Count > 2)
            {
                slices.RemoveLast();
            }
            if (IsWarmingUp)
            {
                return;
            }

            foreach (var symbol in Securities.Keys)
            {
                if (!SymbolSodPriceMid.ContainsKey(symbol))
                {
                    SymbolSodPriceMid[symbol] = MidPrice(symbol);
                }

                if (symbol.SecurityType == SecurityType.Equity && IsEventNewBidAsk(symbol))
                {
                    PublishEvent(new EventNewBidAsk(symbol));
                }
            }

            var pfRisk = PortfolioRisk.E(this); // Log the risk on every price change and order Event!
            pfRisks.AddFirst(pfRisk);
            if (pfRisks.Count > 2)
            {
                pfRisks.RemoveLast();
            }
            if (IsHighPortfolioRisk(pfRisks))
            {
                PublishEvent(new EventHighPortfolioRisk(pfRisk));
            }

            var signals = GetSignals();
            if (signals.Count() > 0) {
                signals = FilterSignalByRisk(signals); // once anything is filled. this calcs a lot
                if (signals.Count() > 0)
                {
                    {
                        PublishEvent(new EventSignals(signals));
                    }
                }
            }

            if (Time.TimeOfDay >= mmWindow.End)
            {
                CancelOpenTickets(); // maybe removing this to stay in limit order book. Review close-open jumps...
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

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay)
            {
                return;
            }
            var pfRisk = PortfolioRisk.E(this);
            var estimated_position_valuation_gain = pfRisk.Positions.Sum(p => Math.Abs(p.Multiplier * (p.Spread / 2) * p.Quantity));
            LogRisk();

            Log($"simulated_missed_gain: {simulated_missed_gain}");
            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFees: {Portfolio.TotalFees}");
            Log($"TotalNetProfit: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfit: {Portfolio.TotalUnrealizedProfit}");
            Log($"TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            var estimated_portfolio_value = Portfolio.TotalPortfolioValue + simulated_missed_gain + estimated_position_valuation_gain;
            Log($"estimated_position_valuation_gain: {estimated_position_valuation_gain}");
            Log($"EstimatedPortfolioValue: {estimated_portfolio_value}");
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            ExportToCsv(PortfolioRisk.E(this).Positions, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_positions_{Time:yyyyMMdd}.csv"));
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
        }
    }
}
