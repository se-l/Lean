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
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class TestInteractiveBrokers : Foundations
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

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays));

            AssignCachedFunctions();
            PfRisk = PortfolioRisk.E(this);

            //SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            //var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(2).First();
            //Log($"WarmUp TimeSpan: {timeSpan}");
            SetWarmUp(0);            
        }

        public override void OnBrokerageMessage(BrokerageMessageEvent messageEvent)
        {
            base.OnBrokerageMessage(messageEvent);
        }

        public void OrderTest()
        {
            Symbol firstOption = Securities.Keys.First(s => s.SecurityType == SecurityType.Option && s.Value.StartsWith("DELL"));
            Symbol firstEq = Securities.Keys.First(s => s.SecurityType == SecurityType.Equity && s.Value.StartsWith("DELL"));
            Symbol secEq = Securities.Keys.First(s => s.SecurityType == SecurityType.Equity && s.Value.StartsWith("HPE"));
            Option option = (Option)Securities[firstOption];
            //PeggedToStockOrder(firstOption, 1, 50, option.Price, option.Underlying.Price, option.Underlying.Price - 1, option.Underlying.Price + 1);
            //LimitOrder(firstOption, 1, option.BidPrice, ocaGroup: "TestOCA88");
            //LimitOrder(firstOption, 1, option.BidPrice + 0.01m, ocaGroup: "TestOCA88");

            string ocaGroup = "TestOCA95";
            int ocaType = 3;
            LimitOrder(firstEq, ocaType, Securities[firstEq].BidPrice -0.01m, ocaGroup: ocaGroup);
            LimitOrder(secEq, ocaType, Securities[secEq].BidPrice - 0.01m, ocaGroup: ocaGroup);
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

            LogRisk();
            LogPnL();
            LogPositions();
            LogOrderTickets();
            LogToDisk();

            OrderTest();
        }
    }
}
