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
using QuantConnect.Orders;
using System.Linq;
//using QuantConnect.ToolBox.IQFeed.IQ;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp
{
    public partial class SnapBrokerageAccount : Foundations
    {
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            UniverseSettings.Resolution = resolution = Resolution.Minute;
            SetStartDate(2023, 5, 17);
            SetEndDate(2023, 5, 18);
            SetCash(100000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            if (LiveMode)
            {
                //SetOptionChainProvider(new IQOptionChainProvider());
            }
            int volatilitySpan = 10;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPrices), volatilitySpan));

            AssignCachedFunctions();

            SetWarmUp((int)(volatilitySpan * 1.5), Resolution.Daily);
            spy = AddEquity("SPY", Resolution.Daily).Symbol;
        }

        public override void OnData(Slice slice)
        {
        }

        public void LogToDisk()
        {
            var pfRisk = PortfolioRisk.E(this);
            ExportToCsv(pfRisk.Positions, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_positions_{Time:yyyyMMdd}.csv"));
            ExportToCsv(Transactions.GetOrders(x => true).ToList(), Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_orders_{Time:yyyyMMdd}.csv"));

            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFees: {Portfolio.TotalFees}");
            Log($"TotalNetProfit: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfit: {Portfolio.TotalUnrealizedProfit}");
            Log($"TotalPortfolioValue: {Portfolio.TotalPortfolioValue}");
        }

        public override void OnWarmupFinished()
        {
            IEnumerable<OrderTicket> openOrderTickets = Transactions.GetOpenOrderTickets();
            IEnumerable<OrderTicket> allOrderTickets = Transactions.GetOrderTickets();
            Log($"Adding Open Transactions to open OrderTickets: {openOrderTickets.Count()}");
            Log($"Adding Open Transactions to all OrderTickets: {allOrderTickets.Count()}");

            foreach (OrderTicket ticket in openOrderTickets)
            {
                if (!orderTickets.ContainsKey(ticket.Symbol)) {
                    orderTickets[ticket.Symbol] = new List<OrderTicket>();
                }
                orderTickets[ticket.Symbol].Add(ticket);
            }

            PopulateOptionChains();

            Log($"Transactions Orders Count: {Transactions.GetOrders().Count()}");
            Log($"Transactions Open Orders Count: {Transactions.GetOpenOrders().Count}");

            LogRisk();

            LogToDisk();

            throw new Exception("OnWarmupFinished executed. Stopping Account Snap.");
        }
    }
}
