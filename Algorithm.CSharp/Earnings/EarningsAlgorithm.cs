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

using MathNet.Numerics.LinearAlgebra;
using Newtonsoft.Json;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Indicators;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

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
        private bool ExecuteScheduledTargetPortfolioFetch = true;
        private readonly Dictionary<Symbol, IEnumerable<MarketDataSnapByUnderlying>> MarketDataSnaps = new();
        // private readonly Dictionary<Symbol, bool> FetchingTargetPortfolio = new();

        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            CfgAlgo = JsonConvert.DeserializeObject<EarningsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            CfgAlgo.OverrideWithEnvironmentVariables<EarningsAlgorithmConfig>();
            Cfg.OverrideWith(CfgAlgo); // Override with config
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));

            wsClient = new(this);
            Log($"{Time} Connecting to ws://{CfgAlgo.WsHost}:{CfgAlgo.WsPort}/ws ...");
            wsClient.ConnectAsync($"ws://{CfgAlgo.WsHost}:{CfgAlgo.WsPort}/ws").Wait();

            UtilityOrderFactory utilityOrderFactory = new (typeof(UtilityOrderEarnings));
            InitializeAlgo(utilityOrderFactory);

            // After release date. Reset the target holdings
            foreach (string s in Cfg.Ticker)
            {
                Symbol symbol = Securities[s].Symbol;
                DateTime nextReleaseDate = NextReleaseDate(symbol, StartDate);
                SetTargetHoldingsToZeroAfterEarnings(symbol);
                Schedule.On(DateRules.EveryDay(symbol), TimeRules.At(1, 0),
                    () => SetTargetHoldingsToZeroAfterEarnings(
                        symbol)); // Not just at midnight, but on algo start and better keep this, dont overwrite dict.
            }

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), FetchTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), SetTargetHoldingsFromTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), SetPricingStrategies);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(5)), LogDifferenceTargetHoldingsOrderTickets);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), FetchTargetPortfolios); // Only runs if marketRegime is matching. refactor
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), FetchPfRiskScenarios); // Only runs if marketRegime is matching. refactor
            ScheduleRegularRequestStressTestDs(startTime: new TimeSpan(0, 15, 50, 0), endTime: new TimeSpan(0, 16, 10, 0));

            foreach (string tickerStr in ticker)
            {
                Equity equity = Securities[tickerStr] as Equity;
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) =>
                    RequestSSVICalibration((Equity)Securities[e]);

                // Schedules Events only if a earnings release is in preparation
                var releaseDate = NextReleaseDate(equity.Symbol, StartDate);
                if (IsPreparingEarningsRelease(equity.Symbol))
                {
                    UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent +=
                        (object sender, Symbol underlying) =>
                        {
                            Log(
                                $"{Time} UnderlyingMovedX: {underlying} 0.5% event fired: FetchTargetPortfolios({underlying})");
                            RequestTargetPortfolios(underlying);
                        };
                    // UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += SnapMarketData;

                    Schedule.On(DateRules.On(releaseDate), TimeRules.At(new TimeSpan(0, 23, 0, 0)), () =>
                    {
                        Log($"{Time} Clearing MarginalWeightedDNLV, TargetPortfolios and TargetHoldings");
                        MarginalUtility.Clear();
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

            MarketRegimesChangeEventHandler += (object sender, MarketRegimesChangeEventArgs e) => RequestPfRiskScenarios(e.Symbol);
        }

        public override void OnWarmupFinished()
        {
            base.OnWarmupFinished();
            equities.DoForEach(symbol => RequestKalmanInit(ToEquity(symbol)));

            Cfg.Ticker.DoForEach(s => SetTargetHoldingsToZeroAfterEarnings(Securities[s].Symbol));

            // This is especially for algo restarts mid day. Wouldn't want TargetHoldings to be set to default 0, and closing positions meant to be held until earnings release.
            Cfg.Ticker
                .Select(s => Securities[s].Symbol)
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

            Log(
                $"SetTargetHoldingsToPortfolio: TargetHoldings: {string.Join(", ", TargetHoldings.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
        }

        public void SnapMarketData(object sender, Symbol underlying)
        {
            if (!IsMyMarketOpen(underlying)) return;

            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (snap == null) return;
            if (string.IsNullOrEmpty(snap.Ts)) return;

            MarketDataSnaps[underlying] =
                MarketDataSnaps.TryGetValue(underlying, out IEnumerable<MarketDataSnapByUnderlying> snaps)
                    ? snaps.Append(snap)
                    : new List<MarketDataSnapByUnderlying> { snap };
        }

        private MarketDataSnapByUnderlying GetMarketDataSnapByUnderlying(Symbol underlying, bool excludeStaleData = true)
        {
            Dictionary<string, OptionQuote> mapSymbolQuote = new();
            DateTime mostRecentPriceUpdate = DateTime.MinValue;

            foreach (Security option in Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option
                         && kvp.Key.Underlying == underlying
                         && (!excludeStaleData || !IsPriceStale(kvp.Key))
                     ).Select(kvp => kvp.Value))
            {
                // this seems to have failed. IsPriceStale(symbol)
                SecurityCache cache = Securities[option.Symbol].Cache;
                DateTime lastUpdated = cache.LastQuoteBarUpdate > cache.LastOHLCUpdate
                    ? cache.LastQuoteBarUpdate
                    : cache.LastOHLCUpdate;
                mostRecentPriceUpdate = mostRecentPriceUpdate > lastUpdated ? mostRecentPriceUpdate : lastUpdated;

                OptionQuote quote = new() { Bid = (float)option.BidPrice, Ask = (float)option.AskPrice };
                mapSymbolQuote[option.Symbol.Value] = quote;
            }

            MarketDataSnapByUnderlying marketDataSnap = new();

            // Return empty snap if no option data is included
            if (mapSymbolQuote.Count == 0)
            {
                Log(
                    $"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data because zero quotes fetched. Not subscribed to any options?");
            }
            else if (excludeStaleData && (Time - mostRecentPriceUpdate) > TimeSpan.FromMinutes(30))
            {
                Log(
                    $"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data snap because prices are stale. mostRecentPriceUpdate: {mostRecentPriceUpdate}");
            }
            else
            {
                // Ensure current holdings are included
                foreach (var kvp in Portfolio.Where(kvp =>
                             kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option &&
                             kvp.Key.Underlying == underlying))
                {
                    string sym = kvp.Key.Value;
                    if (!mapSymbolQuote.ContainsKey(kvp.Key.Value))
                    {
                        OptionQuote quote = new()
                            { Bid = (float)Securities[sym].BidPrice, Ask = (float)Securities[sym].AskPrice };
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
                double fillIv = kvp.Value;
                double bidIv = IvBids[option.Symbol].IVBidAsk.IV;
                double askIv = IvAsks[option.Symbol].IVBidAsk.IV;

                if ((bidIv > fillIv * (1 + tolerance) || askIv < fillIv * (1 - tolerance)))
                {
                    Log(
                        $"{Time} ReloadTargetPortfolioOnUnattainableFillIV: {option}, bidIV={bidIv}, askIV={askIv}, presumedFillIV={fillIv}. Refetching target portfolios.");
                    RequestTargetPortfolios(underlying);
                    return;
                }
            }
        }

        public override void OnData(Slice slice)
        {
            base.OnData(slice);

            if (IsWarmingUp) return;

            // Run task queue
            UpdateSweepRatios();
        }

        private void UpdateSweepRatios()
        {
            foreach (string s in Cfg.Ticker)
            {
                Equity equity = ToEquity(Securities[s].Symbol);
                if (RiskScenarioHandler.ShouldUpdateSweepOrders(equity))
                {
                    Log($"{Time} UpdateSweepRatios(): Schedule RunSignals {equity.Symbol}");
                    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(1)),
                        () => RunSignals(equity.Symbol));
                }
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null) return;
            OnOrderEventDelayed(orderEvent);
        }


        /// <summary>
        /// 2 Assumptions: Cannot quote on both sides of the spread and only 1 ticket per Symbol.
        /// </summary>
        /// <param name="orderEvent"></param>
        private void HandleSpreadBuffers(OrderEvent orderEvent)
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

        /// <summary>
        /// Became a tad too complicated. refactor into more fine-grained event handlers...
        /// </summary>
        /// <param name="orderEvent"></param>
        private void OnOrderEventDelayed(OrderEvent orderEvent)
        {
            ConsumeSignal();
            OrderEvents.Add(orderEvent);

            (OrderEventWriters.TryGetValue(Underlying(orderEvent.Symbol), out OrderEventWriter writer)
                    ? writer
                    : OrderEventWriters[orderEvent.Symbol] =
                        new(this, (Equity)Securities[Underlying(orderEvent.Symbol)]))
                .Write(orderEvent);

            lock (orderTickets)
            {
                if (orderTickets.ContainsKey(orderEvent.Symbol))
                {
                    orderTickets[orderEvent.Symbol].RemoveAll(t => orderFilledCanceledInvalid.Contains(t.Status));
                }
            }

            Symbol underlying = Underlying(orderEvent.Symbol);

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

                Publish(new TradeEventArgs(trades));

                LogOnEventOrderFill(orderEvent);

                InternalAudit(orderEvent);
                
                TaskHandler.Add("RunSignals", new Task(() => RunSignals(orderEvent.Symbol)));
                TaskHandler.Add("RequestStressTestDs", new Task(() =>
                {
                    SnapPositions();
                    RequestStressTestDs(underlying);
                }));


                // Earnings algo specific. On Option fills, want to rerun the target portfolio
                if (orderEvent.Symbol.SecurityType == SecurityType.Option)
                {
                    TaskHandler.Add("OnOptionFill", new Task(() =>
                    {
                        LastDeltaAcrossDsOptionsOnly.Remove(Underlying(orderEvent.Symbol));

                        ClearTargetPortfolios(underlying);
                        ClearTargetHoldings(underlying);
                        SetTargetHoldingsFromTargetPortfolios(underlying); // After earnings
                        CancelOrdersNotAlignedWithTargetPortfolio();
                        RequestTargetPortfolios(underlying); // Before earnings
                        RequestPfRiskScenarios(underlying);
                    }));
                }
            }
            // if canceled and pre-earnings release before market , enqueue pfrisk fetch 
            else if (
                (orderEvent.Status is OrderStatus.Canceled or OrderStatus.Invalid)
                && ActiveRegimes.TryGetValue(ToEquity(Underlying(orderEvent.Symbol)), out HashSet<MarketRegime> regimes) && regimes.Contains(MarketRegime.PreEarningsReleaseBeforeMarketClose)
            )
            {
                LogOrderEvent(orderEvent);
                TaskHandler.Add("RequestPfRiskScenarios", new Task(() =>
                {
                    LastDeltaAcrossDsOptionsOnly.Remove(Underlying(orderEvent.Symbol));
                    RequestPfRiskScenarios(underlying);
                }));
            }

            HandleSpreadBuffers(orderEvent);
        }

        private void LogDifferenceTargetHoldingsOrderTickets()
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
                    IUtilityOrder util = UtilityOrderFactory.Create(this, option,
                        SignalQuantity(symbol, Num2Direction(quantityToOrder)), MidPrice(option.Symbol));
                    Core.Holding holding = new(symbol, Math.Sign(quantityToOrder));
                    double marginalUtility =
                        MarginalUtility.TryGetValue(holding, out marginalUtility) ? marginalUtility : 0;
                    Error(
                        $"{Time} No order ticket present for {symbol}. Remaining Quantity to fill: {quantityToOrder}. Marginal utility: {marginalUtility:0.00}, Util: {util:0.00} UUtil: {util.Utility:0.00} UEquity: {util.UtilityEquityPosition:0.00} UGamma: {util.UtilityGamma:0.00}");
                }
            }
        }

        private static string Portfolio2String(TargetPortfolio portfolio)
        {
            return portfolio == null ? "" : string.Join(", ", portfolio.Holdings.Values.Select(v => $"{v.Symbol}: {v.Quantity}"));
        }

        private void SetTargetHoldingsFromTargetPortfolios()
        {
            equities.DoForEach(SetTargetHoldingsFromTargetPortfolios);
        }

        private void SetTargetHoldingsFromTargetPortfolios(Symbol underlying)
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
                Log(
                    $"{Time} {underlying} WeightedAvgDNLV: {portfolio.ResultStressTestDs.WeightedDnlv:0.00}, Obj: {portfolio.Objective:0.00}, TargetPortfolio: {Portfolio2String(portfolio)}");
                foreach (var holding in portfolio.Holdings.Values.Where(h => SymbolCache.TryGetSymbol(h.Symbol, out _)))
                {
                    Symbol option = Securities[holding.Symbol].Symbol;
                    decimal quantity = (int)holding.Quantity;

                    // Delaying adding highly liquid options to target portfolio until last trading session before release to allow room for spot moves.
                    Option security = (Option)Securities[option];
                    bool skipToday = IsPresumablyLiquid(security) && Time.Date < nextReleaseDate && quantity < 0;
                    if (skipToday)
                        Log(
                            $"{Time} {underlying} Skipping {option} from target portfolio because liquid and today is not release day.");

                    quantity = (skipToday) ? 0 : quantity;

                    if (TargetHoldings.TryGetValue(option, out decimal currentQuantity))
                    {
                        // To be removed if reduction of abs position comes in.
                        if (currentQuantity * quantity < -0.5m)
                        {
                            Error(
                                $"{Time} TargetPortfolio quantities have oppposite sides. {option} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity} Fix API to not send contradicting instructions");
                            // Simulate this whole pf first, ensure it's valid.
                            break;
                        }

                        TargetHoldings[option] = quantity < 0
                            ? Math.Min(currentQuantity, quantity)
                            : Math.Max(currentQuantity, quantity);
                    }
                    else
                    {
                        TargetHoldings[option] = quantity;
                    }
                }
            }

            Log(
                $"{Time} SetTargetHoldingsFromTargetPortfolios: Underlying={underlying}, TargetHoldings={string.Join(", ",  TargetHoldings.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
        }

        private IEnumerable<TargetPortfolio> TargetPortfoliosWithoutOppositeQuantities(
            IEnumerable<TargetPortfolio> portfolios
            )
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
                                Error(
                                    $"{Time} TargetPortfolio quantities have oppposite sides. {symbol} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity}. Removing Portfolio: {Portfolio2String(portfolio)}");
                                introducesOppositeQuantities = true;
                                break;
                            }

                            simulatedTargetHoldings[symbol] = quantity < 0
                                ? Math.Min(currentQuantity, quantity)
                                : Math.Max(currentQuantity, quantity);
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

        private record TradeUtility(
            Symbol Underlying,
            Symbol Symbol,
            decimal Quantity,
            double Utility,
            double IvEnter
            );

        /// <summary>
        /// SetRiskScenario as a handler for sweeping across all target holdings respective equiUtilityBoundary.
        /// </summary>
        private void SetEquiUtilitySweeperFromTargetPortfolios(List<TargetPortfolio> TargetPortfolios)
        {
            // For each entry, pick the <symbol, quantity> and maximum utility.
            List<TradeUtility> tradeUtilities = new();

            foreach (TargetPortfolio targetPortfolio in TargetPortfolios)
            {
                foreach (var kvp in targetPortfolio.Holdings)
                {
                    Symbol symbol = Securities[kvp.Key].Symbol;
                    Symbol underlying = Underlying(symbol);
                    decimal quantity = (int)kvp.Value.Quantity;
                    double utility = CollectionExtensions.GetValueOrDefault(targetPortfolio.ResultStressTestDs.MarginalUtilityByHolding, kvp.Key, 0);
                    double entryIv = CollectionExtensions.GetValueOrDefault(targetPortfolio.Ivs, symbol.Value, 0);
                    tradeUtilities.Add(new TradeUtility(underlying, symbol, quantity, utility, entryIv));
                }
            }

            // Group by underlying and symbol, then select the record with the highest utility
            tradeUtilities = tradeUtilities
                .GroupBy(tu => (tu.Symbol))
                .Select(g => g.OrderByDescending(tu => tu.Utility).First())
                .ToList();

            // Create risk scenarios for each symbol
            // and set the holdings hedge to that symbol and quantity.
            List<PfRiskScenario> riskScenarios = new();

            foreach (TradeUtility tradeUtility in tradeUtilities)
            {
                PfRiskScenario scenario = new()
                {
                    Underlying = tradeUtility.Underlying,
                    HoldingsHedge =
                    {
                        [tradeUtility.Symbol.Value] = new Core.IO.Holding()
                        {
                            Symbol = tradeUtility.Symbol, Quantity = (float)tradeUtility.Quantity,
                            SecurityType = Core.IO.SecurityType.Option
                        }
                    },
                    DPL = tradeUtility.Utility,
                    Score = tradeUtility.Utility,
                    DPLEqHedged = tradeUtility.Utility,
                    IvEnter = { [tradeUtility.Symbol.Value] = tradeUtility.IvEnter },
                    HoldingsCurrent = { }
                };
                riskScenarios.Add(scenario);
            }

            RiskScenarioHandler.SetScenarios(riskScenarios.ToArray());
        }

        private void OnResponseTargetPortfolios(object sender, TargetPortfoliosEventArgs e)
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

            if (!IsMyMarketOpen(e.ResponseTargetPortfolios.Underlying))
                return; // Avoids Running Signals after warmup has finished and a test fetch is scheduled.

            var targetPfs = TargetPortfoliosWithoutOppositeQuantities(e.ResponseTargetPortfolios.TargetPortfolios).ToArray();
            Log(
                $"{Time} OnTargetPortfolios: {targetPfs.Length} portfolios received. IsLastTransmission: {e.ResponseTargetPortfolios.IsLastTransmission}");
            if (!targetPfs.Any())
            {
                if (e.ResponseTargetPortfolios.IsLastTransmission && !ExecuteScheduledTargetPortfolioFetch)
                {
                    Log(
                        $"{Time} OnTargetPortfolios: Last transmission contained zero portfolios. Scheduling retry in 5min");
                    ExecuteScheduledTargetPortfolioFetch = true;
                    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(5)),
                        ScheduledTargetPortfolioFetch);
                }

                return;
            }

            ;

            Symbol underlying = Securities[targetPfs.First().Underlying].Symbol;

            foreach (string underlyingStr in targetPfs.Select((p) => p.Underlying).Distinct())
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
                //pf.Ivs.DoForEach(kvp =>
                //{
                //    if (!SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol))
                //    {
                //        var item = AddData<VolatilityQuoteBar>(symbol, resolution: Resolution.Second, fillForward: false);
                //        item.IsTradable = false;

                //        // This line requests quite a bit of past data. Minute and second resolution for a whole month into past.
                //        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false, extendedMarketHours: true);

                //        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                //    }
                //});

                Log(
                    $"{Time} OnTargetPortfolios, Presumed Fill IVs:\n{string.Join("\n", pf.Ivs.Select(kvp => $"{kvp.Key}: {kvp.Value:0.000}"))}");
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

                foreach (var kvp in
                         pf.ResultStressTestDs
                             .MarginalUtilityByHolding) // Includes dNLV and a utility for going delta neutral (should split)
                {
                    if (SymbolCache.TryGetSymbol(kvp.Key, out Symbol symbol))
                    {
                        Core.Holding holding = new(symbol, Math.Sign(pf.ResultStressTestDs.Holdings[symbol].Quantity));
                        MarginalUtility[holding] = kvp.Value;

                        string comment = kvp.Value < 0 ? "Negative Utility -> Revert" : "";
                        Log(
                            $"{Time} MarginalUtility: Symbol={holding.Symbol}, Quantity={holding.Quantity}, Utility={kvp.Value:0.00}, Comment={comment}");
                    }
                }
            }

            // This overrides the risk scenario set to achieve delta neutral. Dont want that when PricingStategy is going delta neutral.
            foreach (var kvp in TargetPortfolios)
            {
                TargetPortfolios[kvp.Key] = kvp.Value.Where(IsTargetPortfolioCompatibleWithHoldings).ToList();
                SetEquiUtilitySweeperFromTargetPortfolios(TargetPortfolios[kvp.Key]);
            }

            SetTargetHoldingsFromTargetPortfolios();

            Log($"{Time} UpdateSweepRatios(): Schedule RunSignals {underlying}");
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(1)),
                () => RunSignals(underlying));
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
            string dsString = string.Join(",\n",
                e.ResultStressTestDs.DsDnlv.OrderBy(kvp => kvp.Key).Select(kvp =>
                    $"dS: {double.Parse(kvp.Key, CultureInfo.InvariantCulture):0.00}:{kvp.Value:0.00} USD"));
            
            float holdingUnderlyingQuantity =
                e.ResultStressTestDs.Holdings.TryGetValue(e.ResultStressTestDs.Underlying, out Core.IO.Holding holdingUnderlying) ? holdingUnderlying.Quantity : 0;
            
            LastDeltaAcrossDsOptionsOnly[underlying] = e.ResultStressTestDs.DeltaTotalAcrossDs - holdingUnderlyingQuantity;
            decimal deltaTotalOptions = PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal) - Portfolio[underlying].Quantity;
            
            Log($"{Time} OnResultStressTestDs: {e.ResultStressTestDs.Underlying} @ {e.ResultStressTestDs.Ts}, " +
                $"Pf Quantity {e.ResultStressTestDs.Underlying}: {holdingUnderlyingQuantity}, " +
                $"DeltaTotalAcrossDs: {e.ResultStressTestDs.DeltaTotalAcrossDs:0.0}, " +
                $"DeltaTotal OptionsOnly Algo: {deltaTotalOptions:0.0}\n" +
                $"DeltaTotal StressTest: {e.ResultStressTestDs.DeltaTotal:0.0}\n" +
                $"{underlying} S: {MidPrice(Securities[underlying].Symbol)}, " +
                $"{dsString}"
            );
        }

        public void OnCmdCancelOID(object sender, CmdCancelOID cmdCancelOID)
        {
            if (cmdCancelOID == null)
            {
                Log($"{Time} OnCmdCancelOID: cmdCancelOID is null. Ignoring.");
                return;
            }

            OrderTicket ticket = orderTickets.Values.SelectMany(tickets => tickets)
                .FirstOrDefault(t => t.OrderId == cmdCancelOID.Oid);
            Cancel(ticket, $"CmdCancelOID: {cmdCancelOID}");
        }

        private static Func<Vector<double>, SSVIParamsDictionary> VecToSsviParamsDictionary(
            SSVIParamsDictionary ssviParamsDictionary
            )
        {
            return (Vector<double> vec) =>
            {
                SSVIParamsDictionary result = new();
                List<DateTime> sortedKeys = ssviParamsDictionary.GetSortedKeys();
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

        private void OnResponseKalmanInit(object sender, ResponseKalmanInitEventArgs e)
        {
            wsClient.ReleaseThread();
            Equity equity = (Equity)Securities[e.ResponseKalmanInit.Request.Underlying];
            if (e.ResponseKalmanInit.InitState.Count > 0)
            {
                if (!KalmanFiltersSSVI.ContainsKey(equity))
                {
                    SSVIParamsDictionary ssviParamsDct = new(e.ResponseKalmanInit.InitState.ToArray());
                    Matrix<double> init_covariance =
                        Matrix<double>.Build.DenseOfRowArrays(
                            e.ResponseKalmanInit.InitCovariance.Select(row => row.Values.ToArray()));

                    var vecToSsviParamsDictionary = VecToSsviParamsDictionary(ssviParamsDct);
                    KalmanFiltersSSVI[equity] = new KalmanFilter<SSVIParamsDictionary>(this, equity,
                        ssviParamsDct.ToVector(), init_covariance, vecToSsviParamsDictionary);
                    KalmanFiltersSSVI[equity].OnUpdate += IvSurfaceSsviMid[equity].SetModelParams;
                    KalmanFilterSSVIWriters[equity] = new(this, KalmanFiltersSSVI[equity]);
                    PositionWriters[equity] = new(this);
                    IvSurfaceSsviMid[equity].SetModelParams(KalmanFiltersSSVI[equity].GetSSVIParams());
                    KalmanFiltersSSVI[equity].OnUpdate += OnSSVISurfaceUpdated;
                    Log($"{Time} OnResponseKalmanInit: {equity} Kalman filter initialized.");

                    // Don't call TestFetchTargetPortfolios directly because this function runs in the WS.ReceiveLoop. Wouldn't wanna block that thread.
                    //Schedule.On(DateRules.Today, TimeRules.At(Time.AddMinutes(1).TimeOfDay), () => TestFetchTargetPortfolios(equity.Symbol));
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

        internal void OnCmdCfgOverride(object sender, CmdCfgOverrideEventArgs e)
        {
            try
            {
                string cfgName = e.CmdCfgOverride.CfgName;
                //object cfg = type.GetMember(cfgName, BindingFlags.NonPublic | BindingFlags.Instance)[0];
                //this.GetPropertyValue("CfgAlgo", BindingFlags.NonPublic)
                object cfg = cfgName == "CfgAlgo" ? CfgAlgo : Cfg;

                string cfgKey = e.CmdCfgOverride.Key;
                string cfgValue = e.CmdCfgOverride.Value;

                PropertyInfo attr = cfg.GetType().GetProperty(cfgKey);

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
                    else if (attr.PropertyType.GenericTypeArguments.Length > 0 &&
                             attr.PropertyType?.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        object convertedValue = JsonConvert.DeserializeObject(cfgValue, attr.PropertyType);
                        //convertedValue = Convert.ChangeType(convertedValue, attr.PropertyType);
                        attr.SetValue(cfg, convertedValue);
                    }
                    else
                    {
                        object convertedValue = Convert.ChangeType(cfgValue, attr.PropertyType);
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

        private void OnWSConnected(object sender, object obj)
        {
            if (!IsWarmingUp && !IsMyMarketOpen(symbolSubscribed))
            {
                Log($"{Time} OnWSConnected: Fetching target portfolios.");
                FetchTargetPortfolios();
            }
        }

        private void RequestKalmanInit(Equity underlying)
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
            OptionContractWrap ocw = OptionContractWrap.E(this, option, Time.Date);
            double absDelta = Math.Abs(ocw.Delta(MidIV(option.Symbol)));
            return ocw.DaysToExpiration() / 365 < 0.1 && absDelta < 0.8 && absDelta > 0.2;
        }

        internal bool IsTargetPortfolioCompatibleWithHoldings(TargetPortfolio portfolio)
        {
            Symbol underlying = Securities[portfolio.Underlying].Symbol;
            var options = Portfolio
                .Where(kvp =>
                    kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Quantity != 0 &&
                    kvp.Key.Underlying == underlying).Select(kvp => kvp.Key).ToList();

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

        private DateTime SubtractBusinessDays(DateTime dt, int days)
        {
            DateTime startIteratingFrom = dt.AddDays(-(days + 7));
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder()
                .GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            int i = 0;
            foreach (DateTime day in QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, startIteratingFrom, dt)
                         .Reverse())
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

        private static bool IsValidMarketDataSnap(MarketDataSnapByUnderlying snap)
        {
            return
                snap != null &&
                !string.IsNullOrEmpty(snap.Ts) &&
                snap.OptionQuotes.Count > 0;
        }

        private Dictionary<string, Core.IO.Holding> PortfolioOptionHoldings(Symbol underlying)
        {
            var holdings = Portfolio
                .Where(kvp =>
                    kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option &&
                    kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => new Core.IO.Holding()
                {
                    Symbol = kvp.Key.Value,
                    Quantity = (float)kvp.Value.Quantity,
                    SecurityType = SecurityType2SecurityTypePb(kvp.Value.Type)
                });
            return holdings;
        }

        public Dictionary<string, Core.IO.Holding> PortfolioUnderlyingHoldings(Symbol underlying)
        {
            //var holdings = Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => new Core.IO.Holding()
            var holdings = Portfolio.Where(kvp => kvp.Value.Quantity != 0 && Underlying(kvp.Key) == underlying)
                .ToDictionary(kvp => kvp.Key.Value, kvp => new Core.IO.Holding()
                {
                    Symbol = kvp.Key.Value,
                    Quantity = (float)kvp.Value.Quantity,
                    SecurityType = SecurityType2SecurityTypePb(kvp.Value.Type)
                });
            return holdings;
        }

        private void RequestTargetPortfolios(Symbol underlying)
        {
            // ToDo: Need a new class. History of MarketDataSnaps every x% change + latest one when requested. history for skew calculation... 

            HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(underlying), out regimes)
                ? regimes
                : new HashSet<MarketRegime>();
            if (IsWarmingUp || !regimes.Contains(MarketRegime.PreEarningsRelease) ||
                !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying) ||
                !IsPastEarningsEntryStartTime(underlying)) return;

            Equity equity = ToEquity(underlying);

            if (!KalmanFiltersSSVI.ContainsKey(equity)) return;

            // Build request
            //int request_n_contracts = CfgAlgo.RequestTargetPfNContracts.TryGetValue(underlying.Value, out request_n_contracts) ? request_n_contracts : CfgAlgo.RequestTargetPfNContracts[CfgDefault];
            int requestNContracts =
                RequestContractsHandlers.TryGetValue(underlying, out RequestContractsHandler handler)
                    ? handler.GetContractsRequested()
                    : 0;

            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
                NContracts = requestNContracts,
            };
            requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));

            HashSet<DateTime> scopedSlices = Securities.Values
                .Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k)
                .Select(o => o.Expiry).ToHashSet();
            requestTargetPortfolios.Params.AddRange(IVSSSVIParamsToPb(underlying,
                KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            requestTargetPortfolios.ScopedSymbols.AddRange(Securities.Keys
                .Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

            // var startTime = SubtractBusinessDays(Time, 1);

            MarketDataSnapByUnderlying snap = GetMarketDataSnapByUnderlying(underlying);
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
                Log(
                    $"{Time} FetchTargetPortfolios: {underlying}. No valid snap found. Not fetching target portfolios. Scheduling next try in 1min");
                Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)),
                    () => RequestTargetPortfolios(underlying));
            }
        }

        private void RequestPfRiskScenarios(Symbol underlying)
        {
            HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(underlying), out regimes)
                ? regimes
                : new HashSet<MarketRegime>();
            if (IsWarmingUp || !regimes.Contains(MarketRegime.PreEarningsReleaseBeforeMarketClose) ||
                !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying)) return;

            Equity equity = ToEquity(underlying);

            // if LastDeltaAcrossDsOptionsOnly from riskService and locally calculatedd delta total is lower than 5, skip requesting risk scenarios.
            decimal deltaOptions =
                PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal) - Portfolio[underlying].Quantity;
            // double deltaAcrossDs LastDeltaAcrossDsOptionsOnly.TryGetValue(underlying, out double deltaAcrossDs);
            if (Math.Abs(deltaOptions) < 10)
            {
                Log($"{Time} RequestPfRiskScenarios: {underlying} Delta across Ds is too low ({deltaOptions:0.00}). Not requesting risk scenarios.");
                return;
            }

            RequestPfRiskScenarios request = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying
            };
            request.Holdings.Add(PortfolioOptionHoldings(underlying));
            HashSet<DateTime> scopedSlices = Securities.Values
                .Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k)
                .Select(o => o.Expiry).ToHashSet();
            request.Params.AddRange(IVSSSVIParamsToPb(underlying,
                KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            request.ScopedSymbols.AddRange(Securities.Keys
                .Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

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
                Log(
                    $"{Time} RequestPfRiskScenarios: {underlying}. No valid snap found. Not fetching target portfolios. Scheduling next try in 1min");
                Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)),
                    () => RequestPfRiskScenarios(underlying));
            }
        }

        public void TestFetchTargetPortfolios(Symbol underlying)
        {
            if (!IsPreparingEarningsRelease(underlying)) return;

            // Build request
            //int request_n_contracts = CfgAlgo.RequestTargetPfNContracts.TryGetValue(underlying.Value, out request_n_contracts) ? request_n_contracts : CfgAlgo.RequestTargetPfNContracts[CfgDefault];
            int requestNContracts =
                RequestContractsHandlers.TryGetValue(underlying, out RequestContractsHandler handler)
                    ? handler.GetContractsRequested()
                    : 0;
            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
                NContracts = requestNContracts
            };
            requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));

            Equity equity = ToEquity(underlying);
            HashSet<DateTime> scopedSlices = Securities.Values
                .Where(k => k.Type == SecurityType.Option && Underlying(k.Symbol) == underlying).Select(k => (Option)k)
                .Select(o => o.Expiry).ToHashSet();
            requestTargetPortfolios.Params.AddRange(IVSSSVIParamsToPb(underlying,
                KalmanFiltersSSVI[equity].GetSSVIParams().Where(kvp => scopedSlices.Contains(kvp.Key)).ToDictionary()));
            requestTargetPortfolios.ScopedSymbols.AddRange(Securities.Keys
                .Where(k => k.SecurityType == SecurityType.Option && Underlying(k) == underlying).Select(k => k.Value));

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
                Log(
                    $"{Time} TestFetchTargetPortfolios: {underlying}. No valid snap found. Not fetching target portfolios. Expecting to be populated with warmup data.");
            }
        }

        private void RequestStressTestDs()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(s => RequestStressTestDs(Securities[s].Symbol));
        }

        private void ScheduleRegularRequestStressTestDs(TimeSpan startTime, TimeSpan endTime, int intervalSeconds = 60)
        {
            TimeSpan currentTime = startTime;
            while (currentTime <= endTime)
            {
                Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(currentTime), RequestStressTestDs);
                currentTime = currentTime.Add(new TimeSpan(0, 0, intervalSeconds));
            }
        }

        private void RequestStressTestDs(Symbol underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying)) return;

            Log($"{Time} RequestStressTestDs: {underlying}");

            // Build request
            RequestStressTestDs requestStressTestDs = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
            };
            requestStressTestDs.Params.AddRange(IVSSSVIParamsToPb(underlying,
                KalmanFiltersSSVI[ToEquity(underlying)].GetSSVIParams()));
            requestStressTestDs.Holdings.Add(PortfolioUnderlyingHoldings(underlying));

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

        private MarketDataHistory GetMarketDataHistory(Equity underlying, DateTime start, DateTime end)
        {
            // string _start = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);
            // string _end = end.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);

            MarketDataHistory history = new()
            {
                Underlying = underlying.Symbol.Value,
                TsStart = start.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                TsEnd = end.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
            };

            Dictionary<string, Quotes> quotesMap = new();
            lock (MarketDataQuotes)
            {
                foreach (var kvp in MarketDataQuotes.Where(kvp =>
                             Underlying(kvp.Key) == underlying.Symbol && kvp.Key.SecurityType == SecurityType.Option))
                {
                    Quotes quotes = new()
                    {
                        Symbol = kvp.Key.Value,
                        SecurityType = SecurityType2SecurityTypePb(kvp.Key.SecurityType),
                    };
                    // Bad costly processing. Remove someday... Dont send price data here and rather have the service fetch it from a db.
                    quotes.Quotes_.Add(kvp.Value.Where(v =>
                        v != null &&
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) >= start &&
                        DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) <= end
                    ).ToArray());
                    quotesMap[kvp.Key.Value] = quotes;
                }
            }

            history.Quotes.Add(quotesMap);

            Dictionary<string, Core.IO.Trades> tradesMap = new();
            lock (MarketDataTrades)
            {
                foreach (var kvp in MarketDataTrades.Where(kvp =>
                             Underlying(kvp.Key) == underlying.Symbol && kvp.Key.SecurityType == SecurityType.Option))
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

        private void RequestSSVICalibration(Equity underlying)
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
            RequestSSVICalibration requestSsviCalibration = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying.Symbol.Value,
                MarketDataHistory = history
            };

            if (!LiveMode)
            {
                wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                _ = wsClient.SendMessageAsync(requestSsviCalibration);
                wsClient.WaitThread();
            }
            else
            {
                _ = wsClient.SendMessageAsync(requestSsviCalibration);
            }
        }

        private void OnResponseSSVICalibration(object source, ResponseSSVICalibrationEventArgs e)
        {
            wsClient.ReleaseThread();

            lock (MarketDataQuotes)
            {
                foreach (Symbol key in MarketDataQuotes.Keys)
                {
                    // Keep the last 30 mins of elements.
                    // MarketDataQuotes[key] = MarketDataQuotes[key].Where(v => DateTime.ParseExact(v.Ts, DatetTmeFmtProto, CultureInfo.InvariantCulture) >= Time - Ma).ToList();
                    MarketDataQuotes[key].Clear();
                }
            }

            lock (MarketDataTrades)
            {
                foreach (Symbol key in MarketDataTrades.Keys)
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

        private void OnSSVISurfaceUpdated(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            if (!ActiveRegimes.TryGetValue(e.Equity, out var regimes) || !regimes.Contains(MarketRegime.PostEarningsRelease)) return;
            
            Log($"{Time} OnSSVISurfaceUpdated(): ...");
                
            List<PfRiskScenario> scenarios = new();
            // Only need scenarios for the targetHoldings
            foreach ((Symbol symbol, decimal targetQ) in TargetHoldings.Where(kvp => kvp.Key.SecurityType == SecurityType.Option))
            {
                decimal orderQ = targetQ - Portfolio[symbol].Quantity;
                if (orderQ == 0) continue;
                    
                Option option = Securities[symbol] as Option;
                double midIv = IvSurfaceSsviMid[e.Equity].IV(option);
                if (!IVSpreadSMA.TryGetValue(symbol, out SimpleMovingAverage value))
                {
                    continue;
                }

                float ivSpread = (float)value.Current.Value;
                float entryIvSsvi = (float)midIv - Math.Sign(orderQ) * ivSpread / 2;
                decimal midPriceUnderlying = MidPrice(e.Equity.Symbol);
                double npvEntry = OptionContractWrap.E(this, option, Time.Date).NPV(entryIvSsvi, midPriceUnderlying);
                double currentMid = (double)MidPrice(option.Symbol);
                        
                PfRiskScenario scenario = new()
                {
                    Underlying = e.Equity.Symbol.Value,
                    DPL = 0,
                    Score = 100*(currentMid - npvEntry) * Math.Sign(orderQ),  // Half the spread in USD
                    DPLEqHedged = 0,
                };
                scenario.HoldingsHedge.Add(symbol.Value, new Core.IO.Holding
                {
                    Symbol = symbol.Value,
                    Quantity = (float)orderQ,
                    SecurityType = Core.IO.SecurityType.Option
                });
                scenario.IvEnter.Add(symbol.Value, entryIvSsvi);
                scenarios.Add(scenario);
            }
                
            RiskScenarioHandler.SetScenarios(scenarios.ToArray());
        }

        private void OnResponsePfRiskScenarios(object source, ResponsePfRiskScenariosEventArgs e)
        {
            // When the risk scenario was requested, likely not delta neutral hedged. In order to filter for scenarios
            // that reduce the final absolute equity position, need to filter for those where requested equity position + hedge equity quantity is lower than current deltaTotalOptions.
            wsClient.ReleaseThread();
            string underlyingStr = e.ResponsePfRiskScenarios.Underlying;
            Symbol underlying = Securities[underlyingStr].Symbol;
            float deltaTotalOptions = (float)(PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal) -
                Portfolio[underlying].Quantity);
            float eodDeltaToHedge = LastDeltaAcrossDsOptionsOnly.TryGetValue(underlying, out double v) ? (float)v : deltaTotalOptions;
            Core.IO.Holding h;
            List<PfRiskScenario> validScenarios = new();
            foreach (var scenario in e.ResponsePfRiskScenarios.PfRiskScenarios)
            {
                float holdingsCurrentQty = scenario.HoldingsCurrent.TryGetValue(underlyingStr, out h) ? h.Quantity : 0;
                float holdingsHedgeQty = scenario.HoldingsHedge.TryGetValue(underlyingStr, out h) ? h.Quantity : 0;
                float netUnderlyingPosition = holdingsCurrentQty + holdingsHedgeQty;

                if (Math.Abs(netUnderlyingPosition) < Math.Abs(eodDeltaToHedge) && Math.Abs(eodDeltaToHedge) > 5 && scenario.Score > 0)
                {
                    validScenarios.Add(scenario);
                }
            }
            // Only the first 10 scnearios with highest score
            validScenarios = validScenarios.OrderByDescending(s => s.Score).Take(10).ToList();

            RiskScenarioHandler.SetScenarios(validScenarios.ToArray());
            Log($"{Time} OnResponsePfRiskScenarios: updated {validScenarios.Count} scenarios.");

            // Log for all scenarios the option, direction and utility. Log each option only once. That is, if already picked in previous scenario, skip it. Combine all into a single log message.
            HashSet<string> loggedOptions = new();
            List<string> logMessages = new();
            foreach (var scenario in validScenarios)
            {
                float holdingsCurrentQty = scenario.HoldingsCurrent.TryGetValue(underlyingStr, out h) ? h.Quantity : 0;
                float holdingsHedgeQty = scenario.HoldingsHedge.TryGetValue(underlyingStr, out h) ? h.Quantity : 0;

                foreach (var kvp in scenario.HoldingsHedge)
                {
                    if (scenario.Score < 0 || kvp.Value.Quantity == 0 || !loggedOptions.Add(kvp.Key)) continue;
                    logMessages.Add($"{kvp.Key}, " +
                        $"Direction={Num2Direction(kvp.Value.Quantity)}, " +
                        $"Utility={scenario.Score:0.00}, " +
                        $"QPfRequest={holdingsCurrentQty:0.0}, " +
                        $"QPfHedge={holdingsHedgeQty:0.0}"
                    );
                }
            }

            Log($"{Time} OnResponsePfRiskScenarios: {underlyingStr}, eodDeltaToHedge={eodDeltaToHedge}, deltaTotalOptions={deltaTotalOptions:0.00}, positionUnderlying={Portfolio[underlying].Quantity}\n{string.Join("\n", logMessages)}");

            TaskHandler.Add("RunSignals", new Task(() => RunSignals(underlying)));
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
            List<int> hourMin = CfgAlgo.EarningsEntryStartTime.TryGetValue(underlying.Value, out hourMin)
                ? hourMin
                : CfgAlgo.EarningsEntryStartTime[CfgDefault];
            return Time.TimeOfDay >= new TimeSpan(hourMin[0], hourMin[1], 0);
        }

        private void FetchTargetPortfolios()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(s => RequestTargetPortfolios(Securities[s].Symbol));
        }

        private void FetchPfRiskScenarios()
        {
            Cfg.Ticker.DoForEach(s => RequestPfRiskScenarios(Securities[s].Symbol));
        }

        private void SetTargetHoldingsToZeroAfterEarnings(Symbol underlying)
        {
            if (IsAfterEarningsRelease(underlying))
            {
                string pfBeforeStr = string.Join(", ",
                    TargetHoldings.Where(h => Underlying(h.Key) == underlying).Select(kvp => $"{kvp.Key}={kvp.Value}"));
                foreach (var kvp in Portfolio.Where(kvp =>
                             kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying))
                {
                    TargetHoldings[kvp.Key] = 0;
                }

                string pfAfterStr = string.Join(", ",
                    TargetHoldings.Where(h => Underlying(h.Key) == underlying).Select(kvp => $"{kvp.Key}={kvp.Value}"));
                Log($"{Time} SetTargetHoldingsToZeroAfterEarnings: {underlying} {pfBeforeStr} -> {pfAfterStr}");
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
