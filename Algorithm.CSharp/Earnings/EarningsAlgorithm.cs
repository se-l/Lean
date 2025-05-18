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
using System.Linq;
using System;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Securities;
using QuantConnect.Algorithm.CSharp.Core.IO;
using System.Globalization;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Equity;
using System.Threading;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data;
using MathNet.Numerics.LinearAlgebra;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using System.Collections.Concurrent;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    /// <summary>
    /// 
    /// </summary>
    public partial class EarningsAlgorithm : Foundations
    {
        private EarningsAlgorithmConfig CfgAlgo;
        private readonly string CfgAlgoName = "EarningsAlgorithmConfig.json";
        private WsClient wsClient;
        private readonly Dictionary<Symbol, List<TargetPortfolio>> TargetPortfolios = new();
        private bool ExecuteScheduledTargetPortfolioFetch = true;
        private readonly Dictionary<Symbol, IEnumerable<MarketDataSnapByUnderlying>> MarketDataSnaps = new();
        private readonly Dictionary<Symbol, bool> FetchingTargetPortfolio = new();

        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            CfgAlgo = JsonConvert.DeserializeObject<EarningsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            CfgAlgo.OverrideWithEnvironmentVariables<EarningsAlgorithmConfig>();
            Cfg.OverrideWith(CfgAlgo);  // Override with config
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));

            wsClient = new(this);            
            Log($"{Time} Connecting to ws://{CfgAlgo.WsHost}:{CfgAlgo.WsPort}/ws ...");
            wsClient.ConnectAsync($"ws://{CfgAlgo.WsHost}:{CfgAlgo.WsPort}/ws").Wait();

            var utilityOrderFactory = new UtilityOrderFactory(typeof(UtilityOrderEarnings));
            InitializeAlgo(utilityOrderFactory);

            // After release date. Reset the target holdings
            foreach (string ticker in Cfg.Ticker)
            {
                Symbol symbol = Securities[ticker].Symbol;
                DateTime nextReleaseDate = NextReleaseDate(symbol, StartDate);
                SetTargetHoldingsToZeroAfterEarnings(symbol);
                Schedule.On(DateRules.EveryDay(symbol), TimeRules.At(1, 0), () => SetTargetHoldingsToZeroAfterEarnings(symbol));  // Not just at midnight, but on algo start and better keep this, dont overwrite dict.
            }

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), FetchTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), SetTargetHoldingsFromTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed, minutesBeforeClose: 30), FetchPfRiskScenarios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), SetPricingStrategies);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(5)), LogDifferenceTargetHoldingsOrderTickets);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), FetchTargetPortfolios);
            ScheduleRegularRequestStressTestDs(startTime: new TimeSpan(0, 15, 50, 0), endTime: new TimeSpan(0, 16, 10, 0));

            foreach (string ticker in ticker)
            {
                var equity = Securities[ticker] as Equity;
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) => RequestSSVICalibration((Equity)Securities[e]);

                // Schedules Events only if a earnings release is in preparation
                var releaseDate = NextReleaseDate(equity.Symbol, StartDate);
                if (IsPreparingEarningsRelease(equity.Symbol))
                {
                    UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += (object sender, Symbol underlying) =>
                    {
                        Log($"{Time} UnderlyingMovedX: {underlying} 0.5% event fired: FetchTargetPortfolios({underlying})");
                        RequestTargetPortfolios(underlying);
                    };
                    // UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += SnapMarketData;

                    Schedule.On(DateRules.On(releaseDate), TimeRules.At(new TimeSpan(0, 23, 0, 0)), () => {
                        Log($"{Time} Clearing MarginalWeightedDNLV, TargetPortfolios and TargetHoldings");
                        MarginalWeightedDNLV.Clear();
                        ClearTargetPortfolios(equity.Symbol);
                        ClearTargetHoldings(equity.Symbol);
                    });
                    //Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(3)), () => ReloadTargetPortfolioOnUnattainableFillIV(equity.Symbol));
                }
            }

            wsClient.EventHandlerWSConnected += OnWSConnected;
            wsClient.RegisterEventHandler<TargetPortfoliosEventArgs>(Core.IO.Channel.TargetPortfolio, OnResponseTargetPortfolios);
            wsClient.RegisterEventHandler<ResultStressTestDsEventArgs>(Core.IO.Channel.StressTestDs, OnResultStressTestDs);
            wsClient.RegisterEventHandler<ResponseKalmanInitEventArgs>(Core.IO.Channel.KalmanInit, OnResponseKalmanInit);
            wsClient.RegisterEventHandler<CmdCfgOverrideEventArgs>(Core.IO.Channel.CmdCfgOverride, OnCmdCfgOverride);
            wsClient.RegisterEventHandler<ResponseSSVICalibrationEventArgs>(Core.IO.Channel.RequestSsviCalibration, OnResponseSSVICalibration);
            wsClient.RegisterEventHandler<ResponsePfRiskScenariosEventArgs>(Core.IO.Channel.RequestPfRiskScenarios, OnResponsePfRiskScenarios);

        }

        public override void OnWarmupFinished()
        {
            base.OnWarmupFinished();
            equities.DoForEach(symbol => RequestKalmanInit(ToEquity(symbol)));

            Cfg.Ticker.DoForEach(ticker => SetTargetHoldingsToZeroAfterEarnings(Securities[ticker].Symbol));

            // This is especially for algo restarts mid day. Wouldn't want TargetHoldings to be set to default 0, and closing positions meant to be held until earnings release.
            Cfg.Ticker
                .Select(ticker => Securities[ticker].Symbol)
                .Where(underlying => IsPreparingEarningsRelease(underlying))
                .DoForEach(underlying => RequestTargetPortfolios(underlying));

            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(2)), FetchTargetPortfolios);
        }

        public void SetTargetHoldingsToPortfolio(Symbol underlying)
        {
            foreach (var kvp in Portfolio.Where(kvp => kvp.Key.SecurityType == SecurityType.Option))
            {
                TargetHoldings[kvp.Key] = kvp.Value.Quantity;
            }
            Log($"SetTargetHoldingsToPortfolio: TargetHoldings: {string.Join(", ", TargetHoldings.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
        }

        public void SnapMarketData(object sender, Symbol underlying)
        {
            if (!IsMyMarketOpen(underlying)) return;

            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (snap == null) return;
            if (string.IsNullOrEmpty(snap.Ts)) return;

            MarketDataSnaps[underlying] = MarketDataSnaps.TryGetValue(underlying, out IEnumerable<MarketDataSnapByUnderlying> snaps) ? snaps.Append(snap) : new List<MarketDataSnapByUnderlying> { snap };
        }

        public MarketDataSnapByUnderlying GetMarketDataSnapByUnderlying(Symbol underlying, bool excludeStaleData = true)
        {
            Dictionary<string, OptionQuote> mapSymbolQuote = new();
            DateTime mostRecentPriceUpdate = DateTime.MinValue;

            foreach (var option in Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option 
            && kvp.Key.Underlying == underlying 
            && (!excludeStaleData || !IsPriceStale(kvp.Key))
            ).Select(kvp => kvp.Value))
            {
                // this seems to have failed. IsPriceStale(symbol)
                var cache = Securities[option.Symbol].Cache;
                var lastUpdated = cache.LastQuoteBarUpdate > cache.LastOHLCUpdate ? cache.LastQuoteBarUpdate : cache.LastOHLCUpdate;
                mostRecentPriceUpdate = mostRecentPriceUpdate > lastUpdated ? mostRecentPriceUpdate : lastUpdated;
                
                OptionQuote quote = new() { Bid = (float)option.BidPrice, Ask = (float)option.AskPrice };
                mapSymbolQuote[option.Symbol.Value] = quote;
            }

            MarketDataSnapByUnderlying marketDataSnap = new();

            // Return empty snap if no option data is included
            if (mapSymbolQuote.Count == 0)
            {
                Log($"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data because zero quotes fetched. Not subscribed to any options?");                
            }
            else if (excludeStaleData && (Time - mostRecentPriceUpdate) > TimeSpan.FromMinutes(30))
            {
                Log($"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data snap because prices are stale. mostRecentPriceUpdate: {mostRecentPriceUpdate}");
            }
            else
            {
                // Ensure current holdings are included
                foreach (var kvp in Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying))
                {
                    string sym = kvp.Key.Value;
                    if (!mapSymbolQuote.ContainsKey(kvp.Key.Value))
                    {
                        OptionQuote quote = new() { Bid = (float)Securities[sym].BidPrice, Ask = (float)Securities[sym].AskPrice };
                        mapSymbolQuote[kvp.Key.Value] = quote;
                    }
                }
                marketDataSnap.Underlying = underlying;
                marketDataSnap.Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);
                marketDataSnap.UnderlyingPrice = (float)Securities[underlying].Price;                
                marketDataSnap.OptionQuotes.Add(mapSymbolQuote);
            }

            return marketDataSnap;
        }

        public void ReloadTargetPortfolioOnUnattainableFillIV(Symbol underlying)
        {
            if (!IsMyMarketOpen(underlying)) return;
            double tolerance = 0.01;

            foreach (var kvp in PresumedFillIV.Where(kvp => kvp.Key.Underlying.Symbol == underlying).ToDictionary())
            {
                Option option = kvp.Key;
                double fillIV = kvp.Value;
                double bidIV = IVBids[option.Symbol].IVBidAsk.IV;
                double askIV = IVAsks[option.Symbol].IVBidAsk.IV;

                if ((bidIV > fillIV * (1 + tolerance) || askIV < fillIV * (1 - tolerance)))
                {
                    Log($"{Time} ReloadTargetPortfolioOnUnattainableFillIV: {option}, bidIV={bidIV}, askIV={askIV}, presumedFillIV={fillIV}. Refetching target portfolios.");
                    RequestTargetPortfolios(underlying);
                    return;
                }
            }
        }

        public override void OnData(Slice slice)
        {
            base.OnData(slice);

            if (IsWarmingUp) return;

            UpdateSweepRatios();
        }

        public void UpdateSweepRatios()
        {
            foreach (var kvp in SweepState)
            {
                if (!orderTickets.ContainsKey(kvp.Key)) continue;

                OrderTicket t = orderTickets[kvp.Key].Any() ? orderTickets[kvp.Key].First() : null;
                if (t != null && kvp.Value[Num2Direction(t.Quantity)].IsSweepScheduled())
                {
                    kvp.Value[Num2Direction(t.Quantity)].UpdateSweepRatio(t);
                }
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null) return;
            OnOrderEventDelayed(orderEvent);

            // The QC system already changes the state of the order ticket without any delay. This lead to excess hedging when risk was updated a second later only.

            //if (LiveMode)
            //{
            //    OnOrderEventDelayed(orderEvent);
            //}
            //else
            //{
            //    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(Cfg.BacktestingBrokerageLatency)), () => OnOrderEventDelayed(orderEvent));
            //}            
        }

        public void HandleSweepStateOnOrderEvent(OrderEvent orderEvent)
        {
            // Purpose. Due to frequent cancelation of an order ticket, resume sweeping at the previous timer level without resetting. Only when an order was filled, sweeper is reset to get the chance of getting a better price. Definitely room for improvement.
            // refactor into dedicated function. Avoid sweeping through all tickets.
            if (!orderFilledCanceledInvalid.Contains(orderEvent.Status)) return;

            if (SweepState.TryGetValue(orderEvent.Symbol, out ConcurrentDictionary<OrderDirection, Sweep> tmp))
            {
                var direction = Num2Direction(orderEvent.Quantity);
                if (tmp.TryGetValue(direction, out Sweep sweep))
                {
                    if (orderEvent.Status is OrderStatus.Filled)
                    {
                        sweep.StopSweep();
                    }
                    else
                    {
                        sweep.PauseSweep();
                    }
                }
            }         
        }
        /// <summary>
        /// 2 Assumptions: Cannot quote on both sides of the spread and only 1 ticket per Symbol.
        /// </summary>
        /// <param name="orderEvent"></param>
        public void HandleSpreadBuffers(OrderEvent orderEvent)
        {
            OrderTicket ticket = orderEvent.Ticket;
            Symbol symbol = ticket.Symbol;
            if (symbol.SecurityType != SecurityType.Option) return;


            OrderDirection direction = Num2Direction(ticket.Quantity);
            var buffers = SpreadBuffers[direction];

            // Instantiate if missing
            if (!buffers.TryGetValue(symbol, out SpreadBuffer spreadBuffer))
            {
                spreadBuffer = new(this, (Option)Securities[symbol], direction);
                buffers[symbol] = spreadBuffer;
            }

            spreadBuffer.Update(ticket);
        }

        public void OnOrderEventDelayed(OrderEvent orderEvent)
        {
            ConsumeSignal();
            OrderEvents.Add(orderEvent);

            (OrderEventWriters.TryGetValue(Underlying(orderEvent.Symbol), out OrderEventWriter writer) ? writer : OrderEventWriters[orderEvent.Symbol] = new(this, (Equity)Securities[Underlying(orderEvent.Symbol)])).Write(orderEvent);

            lock (orderTickets)
            {
                if (orderTickets.ContainsKey(orderEvent.Symbol))
                {
                    orderTickets[orderEvent.Symbol].RemoveAll(t => orderFilledCanceledInvalid.Contains(t.Status));
                }
            }

            HandleSweepStateOnOrderEvent(orderEvent);

            if (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            {
                UpdateOrderFillData(orderEvent);

                // Urgent first. Cancel other tickets. Hedge delta.
                CancelOcaGroup(orderEvent);
                CancelOptionTicketsWithSameDeltaSign(orderEvent.Symbol);
                RiskScenarioHandler.ClearScenarios(ToEquity(Underlying(orderEvent.Symbol)));

                var trades = WrapToTrade(orderEvent);
                ApplyToPosition(trades);

                PfRisk.CheckHandleDeltaRiskExceedingBand(orderEvent.Symbol);
                RiskProfiles[Underlying(orderEvent.Symbol)].Update();

                // Not urgent
                LogOrderEvent(orderEvent);

                Publish(new TradeEventArgs(trades));  // Continues asynchronously. Sure that's wanted?

                LogOnEventOrderFill(orderEvent);

                RunSignals(orderEvent.Symbol);

                InternalAudit(orderEvent);
                SnapPositions();

                // Earnings algo specific. On Option fills, want to rerun the target portfolio
                if (orderEvent.Symbol.SecurityType == SecurityType.Option)
                {
                    LastDeltaAcrossDs.Remove(Underlying(orderEvent.Symbol));

                    Symbol underlying = orderEvent.Symbol.Underlying;
                    ClearTargetPortfolios(underlying);
                    ClearTargetHoldings(underlying);
                    SetTargetHoldingsFromTargetPortfolios(underlying);  // After earnings
                    CancelOrdersNotAlignedWithTargetPortfolio();
                    RequestTargetPortfolios(Underlying(orderEvent.Symbol));  // Before earnings
                    RequestStressTestDs(Underlying(orderEvent.Symbol));
                }
            }
            HandleSpreadBuffers(orderEvent);
        }

        public void LogDifferenceTargetHoldingsOrderTickets()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            foreach (var kvp in TargetHoldings)
            {
                Symbol symbol = kvp.Key;
                decimal quantityToOrder = kvp.Value - Portfolio[symbol].Quantity;
                if (quantityToOrder == 0) continue;
                if (!orderTickets.TryGetValue(symbol, out List<OrderTicket> _))
                {
                    var option = (Option)Securities[symbol];
                    IUtilityOrder util = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, Num2Direction(quantityToOrder)), MidPrice(option.Symbol));
                    double marginalObjective = MarginalWeightedDNLV.TryGetValue(symbol, out marginalObjective) ? marginalObjective : 0;
                    Error($"{Time} No order ticket present for {symbol}. Remaining Quantity to fill: {quantityToOrder}. Marginal objective: {marginalObjective}, Util: {util} UUtil: {util.Utility} UEquity: {util.UtilityEquityPosition} UGamma: {util.UtilityGamma}");
                }
            }
        }

        public static string Portfolio2String(TargetPortfolio portfolio)
        {
            if (portfolio == null) return "";
            return string.Join(", ", portfolio.Holdings.Values.Select(v => $"{v.Symbol}:{v.Quantity}"));
        }

        /// <summary>
        /// Expect triggers cancelation of all dependent orders...
        /// </summary>
        public void ClearTargetHoldings(Symbol underlying)
        {
            lock (TargetHoldings)
            {
                foreach (Symbol symbol in TargetHoldings.Keys.Where(k => Underlying(k) == underlying).ToList())
                {
                    TargetHoldings.Remove(symbol);
                }
            }
        }

        public void ClearTargetPortfolios(Symbol underlying)
        {
            TargetPortfolios.Remove(underlying);
        }

        public void SetTargetHoldingsFromTargetPortfolios(Symbol underlying)
        {
            // These 2 conditions are weird. Funtion names says set it equal to target, but then it's not done. Rather move these conditions up the stack or rename the function.
            if (IsAfterEarningsRelease(underlying))
            {
                SetTargetHoldingsToZeroAfterEarnings(underlying);
                return;
            }
            else if (
                !IsPreparingEarningsRelease(underlying)
                || !TargetPortfolios.ContainsKey(underlying)
                )
            {
                return;
            }

            var nextReleaseDate = NextReleaseDate(underlying);

            foreach (TargetPortfolio portfolio in TargetPortfolios[underlying].ToList())
            {
                Log($"{Time} {underlying} WeightedAvgDNLV: {portfolio.ResultStressTestDs.WeightedDnlv}, Obj: {portfolio.Objective}, TargetPortfolio: {Portfolio2String(portfolio)}");
                foreach (var holding in portfolio.Holdings.Values.Where(h => SymbolCache.TryGetSymbol(h.Symbol, out _)))
                {
                    Symbol option = Securities[holding.Symbol].Symbol;
                    decimal quantity = (int)holding.Quantity;

                    // Delaying adding highly liquid options to target portfolio until last trading session before release to allow room for spot moves.
                    Option security = (Option)Securities[option];
                    bool skipToday = IsPresumablyLiquid(security) && Time.Date < nextReleaseDate && quantity < 0;
                    if (skipToday) Log($"{Time} {underlying} Skipping {option} from target portfolio because liquid and today is not release day.");

                    quantity = (skipToday) ? 0 : quantity;

                    if (TargetHoldings.TryGetValue(option, out decimal currentQuantity))
                    {
                        // To be removed if reduction of abs position comes in.
                        if (currentQuantity * quantity < -0.5m)
                        {
                            Error($"{Time} TargetPortfolio quantities have oppposite sides. {option} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity} Fix API to not send contradicting instructions");
                            // Simulate this whole pf first, ensure it's valid.
                            break;
                        }
                        TargetHoldings[option] = quantity < 0 ? Math.Min(currentQuantity, quantity) : Math.Max(currentQuantity, quantity);
                    }
                    else
                    {
                        TargetHoldings[option] = quantity;
                    }
                }
            }
            Log($"{Time} SetTargetHoldingsFromTargetPortfolios: Underlying={underlying}, TargetHoldings={string.Join(", ", TargetHoldings.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
        }

        public IEnumerable<TargetPortfolio> TargetPortfoliosWithoutOppositeQuantities(IEnumerable<TargetPortfolio> portfolios)
        {
            List<TargetPortfolio> result = new();
            Dictionary<Symbol, decimal> simulatedTargetHoldings = new();
            foreach (TargetPortfolio portfolio in portfolios.ToList())
            {
                if (portfolio.Objective <= 0)
                {
                    continue;
                }
                bool introducesOppositeQuantities = false;
                foreach (var holding in portfolio.Holdings.Values)
                {
                    if (SymbolCache.TryGetSymbol(holding.Symbol, out Symbol symbol))
                    {
                        decimal quantity = (int)holding.Quantity;

                        if (simulatedTargetHoldings.TryGetValue(symbol, out decimal currentQuantity))
                        {
                            if (currentQuantity * quantity < -0.5m)
                            {
                                Error($"{Time} TargetPortfolio quantities have oppposite sides. {symbol} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity}. Removing Portfolio: {Portfolio2String(portfolio)}");
                                introducesOppositeQuantities = true;
                                break;
                            }
                            simulatedTargetHoldings[symbol] = quantity < 0 ? Math.Min(currentQuantity, quantity) : Math.Max(currentQuantity, quantity);
                        }
                    }
                }
                if (!introducesOppositeQuantities)
                {
                    result.Add(portfolio);
                }                
            }
            return result;
        }
        
        public void SetTargetHoldingsFromTargetPortfolios()
        {
            TargetPortfolios.DoForEach(kvp => SetTargetHoldingsFromTargetPortfolios(kvp.Key));
        }

        public void OnResponseTargetPortfolios(object sender, TargetPortfoliosEventArgs e)
        {
            if (e.ResponseTargetPortfolios.IsLastTransmission)
            {
                wsClient.ReleaseThread();
            }

            if (e.ResponseTargetPortfolios == null)
            {
                Log($"{Time} OnTargetPortfolios: ResponseTargetPortfolios is null. Ignoring.");
                return;
            }
            if (!IsMyMarketOpen(e.ResponseTargetPortfolios.Underlying)) return;  // Avoids Running Signals after warmup has finished and a test fetch is scheduled.

            var targetPfs = TargetPortfoliosWithoutOppositeQuantities(e.ResponseTargetPortfolios.TargetPortfolios);
            Log($"{Time} OnTargetPortfolios: {targetPfs.Count()} portfolios received. IsLastTransmission: {e.ResponseTargetPortfolios.IsLastTransmission}");
            if (!targetPfs.Any()) {
                if (e.ResponseTargetPortfolios.IsLastTransmission && !ExecuteScheduledTargetPortfolioFetch)
                {
                    Log($"{Time} OnTargetPortfolios: Last transmission contained zero portfolios. Scheduling retry in 5min");
                    ExecuteScheduledTargetPortfolioFetch = true;
                    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(5)), ScheduledTargetPortfolioFetch);
                }
                return;
            };

            Symbol underlying = Securities[targetPfs.First().Underlying].Symbol;

            foreach (var underlyingStr in targetPfs.Select((p) => p.Underlying).Distinct())
            {
                underlying = Securities[targetPfs.First().Underlying].Symbol;
                TargetPortfolios[underlying] = new();
            }

            foreach (TargetPortfolio pf in targetPfs.OrderBy(pf => pf.Objective).Reverse())
            {
                if (!IsTargetPortfolioCompatibleWithHoldings(pf))
                {
                    continue;
                }
                //pf.Ivs.DoForEach(kvp => {
                //    if (!SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol)) {
                //        var item = AddData<VolatilityQuoteBar>(symbol, resolution: Resolution.Second, fillForward: false);
                //        item.IsTradable = false;

                //        // This line requests quite a bit of past data. Minute and second resolution for a whole month into past.
                //        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false, extendedMarketHours: true);

                //        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                //    }
                //});

                Log($"{Time} OnTargetPortfolios, Presumed Fill IVs: {pf.Ivs}");
                pf.Ivs.DoForEach(kvp =>
                {
                    if (SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol))
                    {
                        Option option = (Option)Securities[symbol];
                        PresumedFillIV[option] = kvp.Value;
                    }
                });

                underlying = Securities[pf.Underlying].Symbol;
                if (TargetPortfolios.TryGetValue(underlying, out List<TargetPortfolio> list))
                {
                    list.Add(pf);
                }
                else
                {
                    TargetPortfolios[underlying] = new List<TargetPortfolio> { pf };
                }

                foreach (var kvp in pf.ResultStressTestDs.MarginalScaledObjectiveByHolding)
                {
                    if (SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol))
                    {
                        MarginalWeightedDNLV[symbol] = kvp.Value;
                    }
                }
                LogMarginalWeightedDNLV(pf.ResultStressTestDs.MarginalScaledObjectiveByHolding);
            }

            foreach (var kvp in TargetPortfolios)
            {
                TargetPortfolios[kvp.Key] = kvp.Value.Where(p => IsTargetPortfolioCompatibleWithHoldings(p)).ToList();
            }
            SetTargetHoldingsFromTargetPortfolios();

            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(1)), () => RunSignals(underlying));
        }

        private void LogMarginalWeightedDNLV(Google.Protobuf.Collections.MapField<string, double> marginalScaledObjectiveByHolding)
        {
            foreach (var kvp in marginalScaledObjectiveByHolding)
            {
                if (SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol))
                {
                    decimal qHolding = Securities[symbol].Holdings.Quantity;
                    decimal qTargetOne = TargetHoldings.TryGetValue(Securities[kvp.Key].Symbol, out decimal q) ? q : 0;
                    qTargetOne = Math.Sign(qTargetOne) * Math.Min(Math.Abs(qTargetOne), 1);
                    Log($"{Time} MarginalWeightedDNLV {kvp.Key}: {kvp.Value}. TargetDirection={qTargetOne}, Product: {kvp.Value * (double)qTargetOne}, Holdings: {qHolding}");

                    if ((decimal)kvp.Value * q < 0)
                    {
                        Error($"{Time} MarginalWeightedDNLV {kvp.Key}: {kvp.Value}. Negative marginal objective. Revert this position.");
                    }
                }
            }
        }

        public void OnResultStressTestDs(object sender, ResultStressTestDsEventArgs e)
        {
            wsClient.ReleaseThread();
            if (e.ResultStressTestDs == null)
            {
                Log($"{Time} OnResultStressTestDs: resultStressTestDs is null. Ignoring.");
                return;
            }
            if (!Securities.ContainsKey(e.ResultStressTestDs.Underlying))
            {
                Log($"{Time} OnResultStressTestDs: {e.ResultStressTestDs.Underlying} not subscribed. Ignoring.");
                return;
            }
            Symbol underlying = Securities[e.ResultStressTestDs.Underlying].Symbol;
            LastDeltaAcrossDs[underlying] = e.ResultStressTestDs.DeltaTotalAcrossDs;
            string dsString = string.Join(",\n", e.ResultStressTestDs.DsDnlv.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key[..Math.Min(4, kvp.Key.Length)]}:{kvp.Value}"));
            Log($"{Time} OnResultStressTestDs assumes estimated fill scenario: {e.ResultStressTestDs.Underlying} @ {e.ResultStressTestDs.Ts}, DeltaTotalAcrossDs: {e.ResultStressTestDs.DeltaTotalAcrossDs}, DeltaTotal: {e.ResultStressTestDs.DeltaTotal}\n{dsString}");
        }

        public void OnCmdCancelOID(object sender, CmdCancelOID cmdCancelOID)
        {
            if (cmdCancelOID == null)
            {
                Log($"{Time} OnCmdCancelOID: cmdCancelOID is null. Ignoring.");
                return;
            }
            
            OrderTicket ticket = orderTickets.Values.SelectMany(tickets => tickets).FirstOrDefault(t => t.OrderId == cmdCancelOID.Oid);
            Cancel(ticket, $"CmdCancelOID: {cmdCancelOID}");
        }

        public static Func<Vector<double>, SSVIParamsDictionary> VecToSSVIParamsDictionary(SSVIParamsDictionary ssviParamsDictionary)
        {
            return (Vector<double> vec) =>
            {
                SSVIParamsDictionary result = new();
                List<(DateTime, OptionRight)> sortedKeys = ssviParamsDictionary.GetSortedKeys();
                for (int i = 0; i < sortedKeys.Count; i++)
                {
                    double theta = vec[i * 3 + 0];
                    double psi = vec[i * 3 + 1];
                    double vega = vec[i * 3 + 2];
                    
                    result[sortedKeys[i]] = new SSVIParamsRecord(theta, psi, vega);
                }
                return result;
            };
        }
        
        public void OnResponseKalmanInit(object sender, ResponseKalmanInitEventArgs e)
        {
            wsClient.ReleaseThread();
            Equity equity = (Equity)Securities[e.ResponseKalmanInit.Request.Underlying];
            if (e.ResponseKalmanInit.InitState.Count > 0)
            {
                if (!KalmanFiltersSSVI.ContainsKey(equity))
                {
                    SSVIParamsDictionary ssviParamsDct = new(e.ResponseKalmanInit.InitState.ToArray());
                    Matrix<double> init_covariance = Matrix<double>.Build.DenseOfRowArrays(e.ResponseKalmanInit.InitCovariance.Select(row => row.Values.ToArray()));

                    var vecToSSVIParamsDictionary = VecToSSVIParamsDictionary(ssviParamsDct);
                    KalmanFiltersSSVI[equity] = new KalmanFilter<SSVIParamsDictionary>(this, equity, ssviParamsDct.ToVector(), init_covariance, vecToSSVIParamsDictionary);
                    KalmanFiltersSSVI[equity].OnUpdate += IVSurfaceSSVIMid[equity].SetModelParams;
                    KalmanFilterSSVIWriters[equity] = new(this, KalmanFiltersSSVI[equity]);
                    IVSurfaceSSVIMid[equity].SetModelParams(KalmanFiltersSSVI[equity].GetSSVIParams());

                    Log($"{Time} OnResponseKalmanInit: {equity} Kalman filter initialized.");

                    // Don't call TestFetchTargetPortfolios directly because this function runs in the WS.ReceiveLoop. Wouldn't wanna block that thread.
                    Schedule.On(DateRules.Today, TimeRules.At(Time.AddMinutes(1).TimeOfDay), () => TestFetchTargetPortfolios(equity.Symbol));
                }
                else
                {
                    Log($"{Time} OnResponseKalmanInit: {equity} Kalman filter already exists. Ignoring.");
                }
            }
            else
            {
                Log($"{Time} OnResponseKalmanInit: {equity} No init state received. Ignoring.");
            }
        }
        public void OnCmdCfgOverride(object sender, CmdCfgOverrideEventArgs e)
        {
            try
            {
                string cfgName = e.CmdCfgOverride.CfgName;
                Type type = this.GetType();
                //object cfg = type.GetMember(cfgName, BindingFlags.NonPublic | BindingFlags.Instance)[0];
                //this.GetPropertyValue("CfgAlgo", BindingFlags.NonPublic)
                object cfg = cfgName == "CfgAlgo" ? CfgAlgo : Cfg;

                string cfgKey = e.CmdCfgOverride.Key;
                string cfgValue = e.CmdCfgOverride.Value;

                var attr = cfg.GetType().GetProperty(cfgKey);

                if (attr != null)
                {
                    if (attr.PropertyType == typeof(List<string>))
                    {
                        List<string> convertedValue = cfgValue.Split(",").ToList();
                        attr.SetValue(cfg, convertedValue);
                    }
                    else if (attr.PropertyType == typeof(HashSet<string>))
                    {
                        HashSet<string> convertedValue = cfgValue.Split(",").ToHashSet();
                        attr.SetValue(cfg, convertedValue);
                    }
                    else if (attr.PropertyType.GenericTypeArguments.Length > 0 && attr.PropertyType?.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        var convertedValue = JsonConvert.DeserializeObject(cfgValue, attr.PropertyType);
                        //convertedValue = Convert.ChangeType(convertedValue, attr.PropertyType);
                        attr.SetValue(cfg, convertedValue);
                    }
                    else
                    {
                        var convertedValue = Convert.ChangeType(cfgValue, attr.PropertyType);
                        attr.SetValue(cfg, convertedValue);
                    }

                    Log($"{Time} OnCmdCfgOverride: {cfgName}.{cfgKey} set to {cfgValue}");
                }
                else
                {
                    Error($"{Time} OnCmdCfgOverride: {cfgName}.{cfgKey} not found. Ignoring.");
                }
            }
            catch (Exception ex)
            {
                Error($"{Time} OnCmdCfgOverride: {ex.Message}");
            }            
        }

        public void OnWSConnected(object sender, object obj)
        {
            if (!IsWarmingUp && !IsMyMarketOpen(symbolSubscribed))
            {
                Log($"{Time} OnWSConnected: Fetching target portfolios.");
                FetchTargetPortfolios();
            }
        }

        public void RequestKalmanInit(Equity underlying)
        {
            DateTime start = SubtractBusinessDays(Time.Date, 1);
            RequestKalmanInit requestKalmanInit = new()
            {
                Underlying = underlying.Symbol.Value,
                DateFitStart = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                DateFitEnd = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
            };
            
            if (!LiveMode)
            {
                wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                _ = wsClient.SendMessageAsync(requestKalmanInit);
                Log($"{Time} RequestKalmanInit: BLOCKING THREAD until response received. Backtesting only");
                wsClient.WaitThread();
            }
            else
            {
                _ = wsClient.SendMessageAsync(requestKalmanInit);
            }
        }

        public void OnCmdFetchTargetPortfolio(object sender, CmdFetchTargetPortfolio cmdFetchTargetPortfolio)
        {
            if (cmdFetchTargetPortfolio == null)
            {
                Log($"{Time} OnCmdFetchTargetPortfolio: cmdFetchTargetPortfolio is null. Ignoring.");
                return;
            }
            if (Securities.TryGetValue(cmdFetchTargetPortfolio.Symbol, out Security security))
            {
                RequestTargetPortfolios(Underlying(security.Symbol));
            }            
        }

        internal bool IsPresumablyLiquid(Option option)
        {
            var ocw = OptionContractWrap.E(this, option, Time.Date);
            var absDelta = Math.Abs(ocw.Delta(MidIV(option.Symbol)));
            return ocw.DaysToExpiration() / 365 < 0.1 && absDelta < 0.8 && absDelta > 0.2 ;
        }

        public void CancelOrdersNotAlignedWithTargetPortfolio()
        {
            foreach (var kvp in orderTickets.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Any()))
            {
                Symbol symbol = kvp.Key;
                var tickets = kvp.Value;
                if (tickets.Count > 1)
                {
                    string tag = $"CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, Count={tickets.Count}";
                    tickets.DoForEach(ticket => Cancel(ticket, tag));
                }
                var ticket = tickets.First();
                if (TargetHoldings.TryGetValue(symbol, out decimal targetQuantity))
                {
                    var quantityIfFilled = ticket.Quantity + Portfolio[symbol].Quantity;
                    bool shouldCancel = (targetQuantity < 0 ? quantityIfFilled < targetQuantity : quantityIfFilled > targetQuantity) || ticket.Quantity * targetQuantity < 0;
                    if (shouldCancel)
                    {
                        string tag = $"CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, TicketQuantity={ticket.Quantity}, PortfolioQuantity={Portfolio[symbol].Quantity}, TargetQ={targetQuantity}";
                        Cancel(ticket, tag);
                    }
                }
            }
        }

        internal bool IsTargetPortfolioCompatibleWithHoldings(TargetPortfolio portfolio)
        {
            Symbol underlying = Securities[portfolio.Underlying].Symbol;
            var options = Portfolio.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Quantity != 0 && kvp.Key.Underlying == underlying).Select(kvp => kvp.Key).ToList();

            foreach (var option in options)
            {
                var holdings = portfolio.Holdings.Values.Where(h => h.Symbol == option.Value).FirstOrDefault();
                if (holdings == null)
                {
                    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, No Quantity in Target Portfolio");
                    return false;
                }
                //var algoQuantity = Portfolio[option].Quantity;
                //if (holdings.Quantity * (float)algoQuantity < 0)
                //{
                //    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, holdings.Quantity={holdings.Quantity}, algoQuantity={algoQuantity}");
                //    return false;
                //}
                //if (Math.Abs((float)algoQuantity) > Math.Abs((float)holdings.Quantity))
                //{
                //    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, AlgoQuantity={algoQuantity}, PortfolioQuantity={holdings.Quantity}");
                //    return false;
                //}
            }
            return true;
        }

        public DateTime SubtractBusinessDays(DateTime dt, int days)
        {
            var startIteratingFrom = dt.AddDays(-(days+7));
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            int i = 0;
            foreach (var day in QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, startIteratingFrom, dt).Reverse())
            {
                if (i == days)
                {
                    return day;
                }
                i++;
            }
            Error($"SubtractBusinessDays: Could not find {days} trading days before {dt}");
            return (dt - TimeSpan.FromDays(days)) + dt.TimeOfDay;
        }

        public static bool IsValidMarketDataSnap(MarketDataSnapByUnderlying snap)
        {
            return
                snap != null &&
                !string.IsNullOrEmpty(snap.Ts) &&
                snap.OptionQuotes.Count > 0;
        }

        public Dictionary<string, Core.IO.Holding> PortfolioOptionHoldings(Symbol underlying)
        {
            var holdings = Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => new Core.IO.Holding()
            {
                Symbol = kvp.Key.Value,
                Quantity = (float)kvp.Value.Quantity,
                SecurityType = SecurityType2SecurityTypePb(kvp.Value.Type)
            });
            return holdings;
        }

        public void RequestTargetPortfolios(Symbol underlying)
        {
            // ToDo: Need a new class. History of MarketDataSnaps every x% change + latest one when requested. history for skew calculation... 

            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying) || !IsPastEarningsEntryStartTime(underlying)) return;

            Equity equity = ToEquity(underlying);

            if (!KalmanFiltersSSVI.ContainsKey(equity)) return;

            // Build request
            //int request_n_contracts = CfgAlgo.RequestTargetPfNContracts.TryGetValue(underlying.Value, out request_n_contracts) ? request_n_contracts : CfgAlgo.RequestTargetPfNContracts[CfgDefault];
            int request_n_contracts = RequestContractsHandlers.TryGetValue(underlying, out RequestContractsHandler handler) ? handler.GetContractsRequested() : 0;

            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
                NContracts = request_n_contracts,
            };
            requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));

            HashSet<(DateTime, OptionRight)> scopedSlices = Securities.Values.Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k).Select(o => (o.Expiry, o.Right)).ToHashSet();
            requestTargetPortfolios.Params.AddRange(IVSSSVIParamsToPb(underlying, KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            requestTargetPortfolios.ScopedSymbols.AddRange(Securities.Keys.Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

            var startTime = SubtractBusinessDays(Time, 1);

            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (IsValidMarketDataSnap(snap))
            {
                requestTargetPortfolios.MarketDataSnaps.Add(snap);

                // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
                Log($"{Time} FetchTargetPortfolios: {underlying}");

                if (!LiveMode)
                {
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                    Log($"{Time} RequestTargetPortfolios: BLOCKING THREAD until response received. Backtesting only");
                    wsClient.WaitThread();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                }
            }
            else
            {
                Log($"{Time} FetchTargetPortfolios: {underlying}. No valid snap found. Not fetching target portfolios. Scheduling next try in 1min");
                Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)), () => RequestTargetPortfolios(underlying));
            }
        }

        public void RequestPfRiskScenarios(Symbol underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying) || !IsPastEarningsEntryStartTime(underlying)) return;

            Equity equity = ToEquity(underlying);

            RequestPfRiskScenarios request = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying
            };
            request.Holdings.Add(PortfolioOptionHoldings(underlying));
            HashSet<(DateTime, OptionRight)> scopedSlices = Securities.Values.Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k).Select(o => (o.Expiry, o.Right)).ToHashSet();
            request.Params.AddRange(IVSSSVIParamsToPb(underlying, KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            request.ScopedSymbols.AddRange(Securities.Keys.Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (IsValidMarketDataSnap(snap))
            {
                request.MarketDataSnaps.Add(snap);

                // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
                Log($"{Time} RequestPfRiskScenarios: {underlying}");

                if (!LiveMode)
                {
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(request);
                    Log($"{Time} RequestPfRiskScenarios: BLOCKING THREAD until response received. Backtesting only");
                    wsClient.WaitThread();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(request);
                }
            }
            else
            {
                Log($"{Time} RequestPfRiskScenarios: {underlying}. No valid snap found. Not fetching target portfolios. Scheduling next try in 1min");
                Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)), () => RequestPfRiskScenarios(underlying));
            }
        }

        public void TestFetchTargetPortfolios(Symbol underlying)
        {
            if (!IsPreparingEarningsRelease(underlying)) return;

            // Build request
            //int request_n_contracts = CfgAlgo.RequestTargetPfNContracts.TryGetValue(underlying.Value, out request_n_contracts) ? request_n_contracts : CfgAlgo.RequestTargetPfNContracts[CfgDefault];
            int request_n_contracts = RequestContractsHandlers.TryGetValue(underlying, out RequestContractsHandler handler) ? handler.GetContractsRequested() : 0;
            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
                NContracts = request_n_contracts
            };
            requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));

            Equity equity = ToEquity(underlying);
            HashSet<(DateTime, OptionRight)> scopedSlices = Securities.Values.Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k).Select(o => (o.Expiry, o.Right)).ToHashSet();
            requestTargetPortfolios.Params.AddRange(IVSSSVIParamsToPb(underlying, KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            requestTargetPortfolios.ScopedSymbols.AddRange(Securities.Keys.Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

            var startTime = SubtractBusinessDays(Time, 1);

            var snap = GetMarketDataSnapByUnderlying(underlying, false);
            if (IsValidMarketDataSnap(snap))
            {
                requestTargetPortfolios.MarketDataSnaps.Add(snap);

                // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
                Log($"{Time} TestFetchTargetPortfolios: {underlying}.");

                if (!LiveMode)
                {
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                    wsClient.WaitThread();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                }
            }
            else
            {
                Log($"{Time} TestFetchTargetPortfolios: {underlying}. No valid snap found. Not fetching target portfolios. Expecting to be populated with warmup data.");
            }
        }
        public void RequestStressTestDs()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(ticker => RequestStressTestDs(Securities[ticker].Symbol));
        }

        public void ScheduleRegularRequestStressTestDs(TimeSpan startTime, TimeSpan endTime, int intervalSeconds = 60)
        {
            TimeSpan currentTime = startTime;
            while (currentTime <= endTime)
            {
                Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(currentTime), RequestStressTestDs);
                currentTime = currentTime.Add(new TimeSpan(0, 0, intervalSeconds));
            }
        }

        public void RequestStressTestDs(Symbol underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying)) return;

            Log($"{Time} RequestStressTestDs: {underlying}");

            // Build request
            RequestStressTestDs requestStressTestDs = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
            };
            requestStressTestDs.Params.AddRange(IVSSSVIParamsToPb(underlying, KalmanFiltersSSVI[ToEquity(underlying)].GetSSVIParams()));
            requestStressTestDs.Holdings.Add(PortfolioOptionHoldings(underlying));

            // var startTime = SubtractBusinessDays(Time, 1);
            // Historical snaps are used to calculate a smoothened skew. Latest snap's prices is used to calculate the current target portfolio.
            // requestStressTestDs.MarketDataSnaps.Add(MarketDataSnaps[underlying].Where(s => IsValidMarketDataSnap(s) && DateTimeOffset.Parse(s.Ts, CultureInfo.InvariantCulture) >= startTime));
            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (IsValidMarketDataSnap(snap))
            {
                requestStressTestDs.MarketDataSnaps.Add(snap);
                if (!LiveMode)
                {
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(requestStressTestDs);
                    wsClient.WaitThread();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(requestStressTestDs);
                }
            }
            else
            {
                Error($"{Time} RequestStressTestDs: {underlying}. No valid snap found. Not fetching stress test.");
            }
        }

        public MarketDataHistory GetMarketDataHistory(Equity underlying, DateTime start, DateTime end)
        {
            string _start = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);
            string _end = end.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);

            MarketDataHistory history = new()
            {
                Underlying = underlying.Symbol.Value,
                TsStart = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                TsEnd = end.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
            };

            Dictionary<string, Quotes> quotesMap = new();
            lock(MarketDataQuotes)
            {
                foreach (var kvp in MarketDataQuotes.Where(kvp => Underlying(kvp.Key) == underlying.Symbol && kvp.Key.SecurityType == SecurityType.Option))
                {
                    Quotes quotes = new()
                    {
                        Symbol = kvp.Key.Value,
                        SecurityType = SecurityType2SecurityTypePb(kvp.Key.SecurityType),
                    };
                    // Bad costly processing. Remove someday... Dont send price data here and rather have the service fetch it from a db.
                    quotes.Quotes_.Add(kvp.Value.Where(v =>
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) >= start &&
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) <= end
                        ).ToArray());
                    quotesMap[kvp.Key.Value] = quotes;
                }
            }            
            history.Quotes.Add(quotesMap);

            Dictionary<string, Core.IO.Trades> tradesMap = new();
            lock(MarketDataTrades)
            {
                foreach (var kvp in MarketDataTrades.Where(kvp => Underlying(kvp.Key) == underlying.Symbol && kvp.Key.SecurityType == SecurityType.Option))
                {
                    Trades trades = new()
                    {
                        Symbol = kvp.Key.Value,
                        SecurityType = SecurityType2SecurityTypePb(kvp.Key.SecurityType),
                    };
                    trades.Trades_.Add(kvp.Value.Where(v =>
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) >= start &&
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) <= end
                        ).ToArray());
                    tradesMap[kvp.Key.Value] = trades;
                }
            }
            history.Trades.Add(tradesMap);

            return history;
        }

        public void RequestSSVICalibration(Equity underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            Log($"{Time} RequestSSVICalibration: {underlying}");
            MarketDataHistory history;
            try
            {
                history = GetMarketDataHistory(underlying, SubtractBusinessDays(Time, 1), Time);
            }
            catch (Exception e)
            {
                Error($"{Time} RequestSSVICalibration: {underlying}. {e.Message}");
                return;
            }

            // Build request
            RequestSSVICalibration requestSSVICalibration = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying.Symbol.Value,
                MarketDataHistory = history
            };

            if (!LiveMode)
            {
                wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                _ = wsClient.SendMessageAsync(requestSSVICalibration);
                wsClient.WaitThread();
            }
            else
            {
                _ = wsClient.SendMessageAsync(requestSSVICalibration);
            }
        }

        public void OnResponseSSVICalibration(object source, ResponseSSVICalibrationEventArgs e)
        {
            wsClient.ReleaseThread();

            lock (MarketDataQuotes)
            {
                foreach (var key in MarketDataQuotes.Keys)
                {
                    // Keep the last 30 mins of elements.
                    // MarketDataQuotes[key] = MarketDataQuotes[key].Where(v => DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) >= Time - Ma).ToList();
                    MarketDataQuotes[key].Clear();
                }
            }
            lock (MarketDataTrades)
            {
                foreach (var key in MarketDataTrades.Keys)
                {
                    MarketDataTrades[key].Clear();
                }
            }

            Equity underlying = (Equity)Securities[e.ResponseSSVICalibration.Request.Underlying];
            SSVIParamsDictionary currentStateParams = KalmanFiltersSSVI[underlying].GetSSVIParams();
            SSVIParamsDictionary responseParams = new(e.ResponseSSVICalibration.Params.ToArray());
            KalmanFiltersSSVI[underlying].Update(currentStateParams.Update(responseParams).ToVector());
            Log($"{Time} OnResponseSSVICalibration: {underlying} Kalman state updated.");
        }

        public void OnResponsePfRiskScenarios(object source, ResponsePfRiskScenariosEventArgs e)
        {
            wsClient.ReleaseThread();
            RiskScenarioHandler.SetScenarios(e.ResponsePfRiskScenarios.PfRiskScenarios.ToArray());  
            Log($"{Time} OnResponsePfRiskScenarios: updated.");
        }

        public void ScheduledTargetPortfolioFetch()
        {
            if (ExecuteScheduledTargetPortfolioFetch)
            {
                FetchTargetPortfolios();
            }
        }

        public bool IsPastEarningsEntryStartTime(Symbol underlying)
        {
            List<int> hourMin = CfgAlgo.EarningsEntryStartTime.TryGetValue(underlying.Value, out hourMin) ? hourMin : CfgAlgo.EarningsEntryStartTime[CfgDefault];
            return Time.TimeOfDay >= new TimeSpan(hourMin[0], hourMin[1], 0);
        }

        public void FetchTargetPortfolios()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(ticker => RequestTargetPortfolios(Securities[ticker].Symbol));
        }

        public void FetchPfRiskScenarios()
        {
            Cfg.Ticker.DoForEach(ticker => RequestPfRiskScenarios(Securities[ticker].Symbol));
        }

        public void SetTargetHoldingsToZeroAfterEarnings(Symbol underlying)
        {
            if (IsAfterEarningsRelease(underlying))
            {
                string pf_before_str = string.Join(", ", TargetHoldings.Where(h => Underlying(h.Key) == underlying).Select(kvp => $"{kvp.Key}={kvp.Value}"));
                foreach (var kvp in Portfolio.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying))
                {
                    TargetHoldings[kvp.Key] = 0;
                }
                string pf_after_str = string.Join(", ", TargetHoldings.Where(h => Underlying(h.Key) == underlying).Select(kvp => $"{kvp.Key}={kvp.Value}"));
                Log($"{Time} SetTargetHoldingsToZeroAfterEarnings: {underlying} {pf_before_str} -> {pf_after_str}");
            }            
        }

        public override void OnEndOfAlgorithm()
        {
            base.OnEndOfAlgorithm();
            wsClient.StopHealthCheck();
            wsClient.Dispose();
        }
    }
}
