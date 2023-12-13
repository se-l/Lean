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
using System.Collections.Generic;
using QuantConnect.Brokerages;
using QuantConnect.Securities;
using QuantConnect.Orders;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using Newtonsoft.Json;
using System.IO;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Util;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class ASnapBrokerageAccount : Foundations
    {
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            UniverseSettings.Resolution = resolution = Resolution.Second;
            //SetStartDate(2023, 5, 17);
            //SetEndDate(2023, 5, 18);
            //SetCash(100000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            Cfg = JsonConvert.DeserializeObject<AMarketMakeOptionsAlgorithmConfig>(File.ReadAllText("AMarketMakeOptionsAlgorithmConfig.json"));
            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText("EarningsAnnouncements.json"));
            DividendSchedule = JsonConvert.DeserializeObject<Dictionary<string, DividendMine[]>>(File.ReadAllText("DividendSchedule.json"));
            EarningsBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);

            AssignCachedFunctions();
            PfRisk = PortfolioRisk.E(this);

            //SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            //var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(2).First();
            //Log($"WarmUp TimeSpan: {timeSpan}");
            SetWarmUp(1);
        }

        public override void OnBrokerageMessage(BrokerageMessageEvent messageEvent)
        {
            base.OnBrokerageMessage(messageEvent);
        }

        public void LogToDisk()
        {
            //var pfRisk = PortfolioRisk.E(this);
            //ExportToCsv(pfRisk.Trades, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_trades_{Time:yyyyMMdd}.csv"));
            //ExportToCsv(Transactions.GetOrders(x => true).ToList(), Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_orders_{Time:yyyyMMdd}.csv"));

            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFees: {Portfolio.TotalFees}");
            Log($"TotalNetProfit: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfit: {Portfolio.TotalUnrealizedProfit}");
            Log($"TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
            Log($"EquityWithLoanValue: {Portfolio.MarginMetrics.EquityWithLoanValue}");
            Log($"InitMarginReq: {Portfolio.MarginMetrics.FullInitMarginReq}");
            Log($"FullMaintMarginReq: {Portfolio.MarginMetrics.FullMaintMarginReq}");
            Log($"QCMarginRemaining: {Portfolio.MarginRemaining}");
            Log($"QCTotalMarginUsed: {Portfolio.TotalMarginUsed}");
        }

        public override void OnWarmupFinished()
        {
            equities = Securities.Keys.Where(s => s.SecurityType == SecurityType.Equity).ToHashSet();
            IEnumerable<OrderTicket> openTransactions = Transactions.GetOpenOrderTickets();
            Log($"Adding Open Transactions to OrderTickets: {openTransactions.Count()}");
            foreach (OrderTicket ticket in openTransactions)
            {
                if (!orderTickets.ContainsKey(ticket.Symbol))
                {
                    orderTickets[ticket.Symbol] = new List<OrderTicket>();
                }
                orderTickets[ticket.Symbol].Add(ticket);
            }
            InitializePositionsFromPortfolioHoldings();
            InitializeTradesFromPortfolioHoldings();

            LogRisk();
            LogPnL();
            LogPositions();
            LogOrderTickets();
            LogToDisk();

            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Globals.PathAnalytics, "PositionLifeCycle.csv"));
            
            SetRunTimeError(new System.Exception("Account Snapped. Metrics logged and exported"));
        }
        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            changes.AddedSecurities.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                securityInitializer.RegisterIndicators((Option)sec);
            });
        }
    }
}
