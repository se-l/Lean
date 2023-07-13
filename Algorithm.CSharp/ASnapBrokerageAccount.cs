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
using QuantConnect.ToolBox.IQFeed.IQ;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;

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

            if (LiveMode)
            {
                SetOptionChainProvider(new IQOptionChainProvider());
            }
            int volatilitySpan = 30;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPrices), volatilitySpan));

            AssignCachedFunctions();

            spy = AddEquity("SPY", Resolution.Daily).Symbol;
            pfRisk = PortfolioRisk.E(this);

            securityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, spy, SecurityType.Equity);
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(securityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(2).First();
            Log($"WarmUp TimeSpan: {timeSpan}");
            SetWarmUp(timeSpan);
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
        }

        public override void OnWarmupFinished()
        {
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

            pfRisk.ResetPositions();

            PopulateOptionChains();

            LogRisk();
            LogPnL();

            foreach (var indicator in RollingIVBid.Values)
            {
                if (!indicator.IsReadyLongMean)
                {
                    Log($"RollingIVBid {indicator.Symbol} Bid not ready at startup. Samples {indicator.Samples}");
                }
            }
            foreach (var indicator in RollingIVAsk.Values)
            {
                if (!indicator.IsReadyLongMean)
                {
                    Log($"RollingIVAsk {indicator.Symbol} Ask not ready at startup. Samples {indicator.Samples}");
                }
            }

            LogToDisk();
        }
    }
}