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

using System.IO;
using QuantConnect.Algorithm.CSharp.Core;
using Newtonsoft.Json;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.MarketMaking
{
    /// <summary>
    /// 
    /// </summary>
    public partial class MarketMakeOptionsAlgorithm : Foundations
    {
        private MarketMakeOptionsAlgorithmConfig CfgAlgo;
        private readonly string CfgAlgoName = "MarketMakeOptionsAlgorithmConfig.json";

        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            CfgAlgo = JsonConvert.DeserializeObject<MarketMakeOptionsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            Cfg.OverrideWith(CfgAlgo);  // Override with config
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));
            var utilityOrderFactory = new UtilityOrderFactory(typeof(UtilityOrderMarketMaking));
            InitializeAlgo(utilityOrderFactory);
        }
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            ConsumeSignal();
            OrderEvents.Add(orderEvent);
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                LogOrderEvent(orderEvent);
            }
            (OrderEventWriters.TryGetValue(Underlying(orderEvent.Symbol), out OrderEventWriter writer) ? writer : OrderEventWriters[orderEvent.Symbol] = new(this, (Equity)Securities[Underlying(orderEvent.Symbol)])).Write(orderEvent);

            lock (orderTickets)
            {
                foreach (var tickets in orderTickets.Values)
                {
                    tickets.RemoveAll(t => orderFilledCanceledInvalid.Contains(t.Status));
                }
            }
            if (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            {
                UpdateOrderFillData(orderEvent);

                CancelOcaGroup(orderEvent);

                var trades = WrapToTrade(orderEvent);
                ApplyToPosition(trades);
                Publish(new TradeEventArgs(trades));  // Continues asynchronously. Sure that's wanted?

                LogOnEventOrderFill(orderEvent);

                RunSignals(orderEvent.Symbol);

                PfRisk.CheckHandleDeltaRiskExceedingBand(orderEvent.Symbol);
                RiskProfiles[Underlying(orderEvent.Symbol)].Update();

                InternalAudit(orderEvent);
                SnapPositions();
            }
        }
    }
}
