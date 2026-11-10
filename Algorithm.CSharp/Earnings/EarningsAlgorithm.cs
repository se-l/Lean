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
using QuantConnect.Logging;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Configuration;
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
using System.Threading.Tasks;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    public class EarningsAlgorithm : Foundations
    {
        private EarningsAlgorithmConfig CfgAlgo;
        private readonly string CfgAlgoName = "EarningsAlgorithmConfig.json";
        private RabbitMQClient rMQClient;
        private SurfaceStreamClient _surfaceStreamClient;
        private readonly Dictionary<(Symbol Symbol, string SurfaceId), DateTime> _lastSurfaceStreamTs = new();
        private readonly object _surfaceStreamGate = new();
        private bool ExecuteScheduledTargetPortfolioFetch = true;
        // private readonly Dictionary<Symbol, bool> FetchingTargetPortfolio = new();
        // Local pricer-cache plane agent (spec: Fino.jl/docs/pricer-cache-dotnet-agent-spec.md):
        // serves IVs from the MQ-fed greek book, falls back to the ZMQ JuliaPricer on miss.
        public PricerCacheClient PricerCache;

        /// <summary>
        /// Telemetry run id: `yyyyMMdd-HHmmss` (UTC trigger time) - underlyings - start date - 6 hex.
        /// The stamp leads so a descending sort puts the most recent run on top; the hash keeps two
        /// runs started in the same second distinct.
        /// </summary>
        private string BuildRunId()
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string underlyings = string.Join(",", Cfg.Ticker.Where(t => !string.IsNullOrWhiteSpace(t)));
            // Cfg.StartDate is the configured backtest start (CfgAlgo overrides Cfg); QCAlgorithm's
            // StartDate is still the engine default at this point. Live runs have no window.
            DateTime configuredStart = Cfg.StartDate;
            string start = configuredStart.Year < 1990 ? "live" : configuredStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string salt = Guid.NewGuid().ToString("N").Substring(0, 6);
            return $"{stamp}-{underlyings}-{start}-{salt}";
        }

        public override bool HasActiveRequests() => rMQClient?.HasActiveRequests() ?? false;

        public override bool BrokerConnected => rMQClient?.IsConnected ?? false;

        public override bool HasPendingDecisionRequest() => rMQClient?.HasPendingDecisionRequest() ?? false;
        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            CfgAlgo = JsonConvert.DeserializeObject<EarningsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            CfgAlgo.OverrideWithEnvironmentVariables<EarningsAlgorithmConfig>();
            Cfg.OverrideWith(CfgAlgo); // Override with config
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));

            // Run identity for telemetry. Shape: <utc trigger stamp>-<underlyings>-<start date>-<6 hex>.
            // Lexicographically descending therefore lists the newest run first, and the run id is a
            // single filter in the backend (it is exported as the lean.run_id resource attribute).
            string runId = BuildRunId();
            OpenTelemetryLogHandler.SetRunId(runId, LiveMode ? "live" : "backtest");
            Log($"{Time} EarningsAlgorithm.Initialize: telemetry run id {runId}");

            string pricerUri = CfgAlgo.PricerProtocol.ToLower() == "ipc" 
                ? $"ipc://{CfgAlgo.PricerHost}.{CfgAlgo.PricerPort}"
                : $"{CfgAlgo.PricerProtocol}://{CfgAlgo.PricerHost}:{CfgAlgo.PricerPort}";
            julia = new JuliaPricingClient(pricerUri, CfgAlgo.PricerConcurrency);
            JuliaPricingClient.Logger = msg => Log(msg);
            PricerBudget.MetricsSink = OpenTelemetryLogHandler.Metric;
            Log($"Connecting to JuliaPricer: {pricerUri} ... (in-flight budget={julia.Budget.Capacity}" +
                $"{(CfgAlgo.PricerConcurrency > 0 ? " from PricerConcurrency" : " = CPU count")})");

            rMQClient = new(this);
            rMQClient.ConnectAsync(CfgAlgo.MQHost, CfgAlgo.MQPort, CfgAlgo.MQUser, CfgAlgo.MQPass, CfgAlgo.MQVirtualHost);
            Log($"Connecting to RabbitMQ: {CfgAlgo.MQHost}:{CfgAlgo.MQPort} VHost: {CfgAlgo.MQVirtualHost} ...");

            // Live config deltas: its own connection (the client above owns the catch-all reply
            // queue). Fails open — a broker hiccup must never block Initialize.
            ConfigUpdates = new ConfigUpdateChannel(this);
            ConfigUpdates.Start(CfgAlgo.MQHost, CfgAlgo.MQPort, CfgAlgo.MQUser, CfgAlgo.MQPass, CfgAlgo.MQVirtualHost);

            if (CfgAlgo.PricerCacheEnabled)
            {
                // The pricer-cache plane is mode-agnostic: it runs in backtests too (that is how
                // it gets tested). The env bundle rides inside every request and the env hash
                // encodes (ticker, calc date), so historical sim requests train the feeder with
                // per-date books — no cross-date contamination. Warmup publishes (train) without
                // serving; serving starts at warmup completion (see OptionContractWrap).
                // Never let a broker hiccup kill Initialize: Start() fails open into
                // ZMQ-fallback-only mode on its own.
                PricerCacheEnabled = false;
                PricerCache = new PricerCacheClient(this, CfgAlgo.MQHost, CfgAlgo.MQPort,
                    CfgAlgo.PricerCacheVHost ?? CfgAlgo.MQVirtualHost, CfgAlgo.MQUser, CfgAlgo.MQPass);
                PricerCache.Start();
                Log($"PricerCacheClient: starting pricer-cache agent (liveMode={LiveMode}).");
            }

            UtilityOrderFactory utilityOrderFactory = new (typeof(UtilityOrderEarnings));
            InitializeAlgo(utilityOrderFactory);
            SetStartDate(CfgAlgo.StartDate);
            SetEndDate(CfgAlgo.EndDate);

            // After release date. Reset the target holdings
            foreach (string s in Cfg.Ticker)
            {
                Symbol symbol = Securities[s].Symbol;
                SetTargetHoldingsToZeroAfterEarnings(symbol);
                Schedule.On(DateRules.EveryDay(symbol), TimeRules.At(1, 0),
                    () => SetTargetHoldingsToZeroAfterEarnings(
                        symbol)); // Not just at midnight, but on algo start and better keep this, dont overwrite dict.
            }

            Schedule.On("FetchTargetPortfolios", DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), FetchTargetPortfolios);
            Schedule.On("SetTargetHoldingsFromTargetPortfolios", DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, minutesAfterOpen: 4), SetTargetHoldingsFromTargetPortfolios);
            Schedule.On("SetPricingStrategies", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), SetPricingStrategies);
            Schedule.On("LogDifferenceTargetHoldingsOrderTickets", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(5)), LogDifferenceTargetHoldingsOrderTickets);
            Schedule.On("FetchTargetPortfolios", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), FetchTargetPortfolios); // Only runs if marketRegime is matching. refactor
            Schedule.On("FetchPfRiskScenarios", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), FetchPfRiskScenarios); // Only runs if marketRegime is matching. refactor
            ScheduleRegularRequestStressTestDs(startTime: new TimeSpan(0, 15, 50, 0), endTime: new TimeSpan(0, 16, 10, 0));

            foreach (string tickerStr in ticker)
            {
                Equity equity = Securities[tickerStr] as Equity;
                if (UseSurfaceStream)
                {
                    // LIVE PUSH: the surface producer publishes both products to surface.<UNDERLYING>
                    // on the config's own event grid. No request is sent from here; the subscription
                    // is started once, after the ticker loop.
                    Log($"{Time} InitializeAlgo: live surfaces from MQ stream " +
                        $"(execution={ExecutionSurfaceId}, risk={RiskSurfaceId}); no calibration requests.");
                }
                else if (SurfaceFromRabbitMq)
                {
                    // MQ BACKTEST ONLY: a 0.5% move asks the calc-engine for a fresh calibration.
                    // This used to be wired only when LiveMode, so a backtest with
                    // surface-mode=rabbitmq never sent a single request.
                    UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += (sender, e) => RequestSSVICalibration((Equity)Securities[e]);

                    // A quiet tape sends nothing, which makes the MQ path untestable in a backtest.
                    // A fixed cadence is opt-in: 0 disables it.
                    int calibrationIntervalMinutes = Config.GetInt("ssvi-calibration-interval-minutes", 0);
                    if (calibrationIntervalMinutes > 0)
                    {
                        Schedule.On($"RequestSsviCalibration-{equity.Symbol.Value}", DateRules.EveryDay(equity.Symbol),
                            TimeRules.Every(TimeSpan.FromMinutes(calibrationIntervalMinutes)),
                            () => RequestSSVICalibration(equity));
                        Log($"{Time} InitializeAlgo: SSVI calibration requested every {calibrationIntervalMinutes} min for {equity.Symbol} (plus 0.5% moves).");
                    }
                }
                else
                {
                    Log($"{Time} Backtest: surfaces arrive as slice data (surface-mode={SurfaceMode}); no MQ calibration requests.");
                }

                // Schedules Events only if a earnings release is in preparation
                var releaseDate = NextReleaseDate(equity.Symbol, StartDate);
                if (IsPreparingEarningsRelease(equity.Symbol))
                {
                    UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent +=
                        (object sender, Symbol underlying) =>
                        {
                            Log($"{Time} UnderlyingMovedX: {underlying} 0.5% event fired: FetchTargetPortfolios({underlying})");
                            RequestTargetPortfolios(underlying);
                        };

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

            rMQClient.EventHandlerConnected += OnWSConnected;
            rMQClient.RegisterEventHandler<TargetPortfoliosEventArgs>(ChannelPb.TargetPortfolio, OnResponseTargetPortfolios);
            rMQClient.RegisterEventHandler<ResultStressTestDsEventArgs>(ChannelPb.StressTestDs, OnResultStressTestDs);
            rMQClient.RegisterEventHandler<CmdCfgOverrideEventArgs>(ChannelPb.CmdCfgOverride, OnCmdCfgOverride);
            rMQClient.RegisterEventHandler<ResponseSSVICalibrationEventArgs>(ChannelPb.RequestSsviCalibration, OnResponseSSVICalibration);
            rMQClient.RegisterEventHandler<ResponsePfRiskScenariosEventArgs>(ChannelPb.RequestPfRiskScenarios, OnResponsePfRiskScenarios);

            if (UseSurfaceStream)
            {
                StartSurfaceStream(ticker);
            }

            MarketRegimesChangeEventHandler += (object sender, MarketRegimesChangeEventArgs e) => RequestPfRiskScenarios(e.Symbol);
            
            AfterInitializeAlgo();
        }

        protected override void AfterInitializeAlgo()
        {
            base.AfterInitializeAlgo();

            // Repeating schedules are skipped during warm-up; run them once here, before trading starts.
            SetPricingStrategies();
            FetchTargetPortfolios();
            FetchPfRiskScenarios();
            LogDifferenceTargetHoldingsOrderTickets();

            Cfg.Ticker.DoForEach(s => SetTargetHoldingsToZeroAfterEarnings(Securities[s].Symbol));

            // This is especially for algo restarts mid day. Wouldn't want TargetHoldings to be set to default 0, and closing positions meant to be held until earnings release.
            Cfg.Ticker
                .Select(s => Securities[s].Symbol)
                .Where(underlying => IsPreparingEarningsRelease(underlying))
                .DoForEach(underlying => RequestTargetPortfolios(underlying, force:true));

            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(2)), FetchTargetPortfolios);
        }

        private MarketDataSnapByUnderlyingPb GetMarketDataSnapUnderlyingOnly(Symbol underlying, bool excludeStaleData = true)
        {
            MarketDataSnapByUnderlyingPb marketDataSnap = new();
            
            marketDataSnap.Underlying = underlying;
            marketDataSnap.Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture);

            // Mid first, last trade as the fallback: the stress test is surface-driven and consumes this
            // one number as s0, so a zero here silently kills the request on the mind side.
            decimal spot = MidPrice(underlying);
            if (spot <= 0)
            {
                spot = Securities[underlying].Price;
            }
            marketDataSnap.UnderlyingPrice = (float)spot;

            return marketDataSnap;
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
            if (!Cfg.Ticker.Contains(Underlying(orderEvent.Symbol).Value))
            {
                Log($"{Time} OnOrderEvent(): OrderEvent {orderEvent}. Not in Ticker for this job. Ignoring...");
                return;
            }
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

                // Only an OPTION fill invalidates the option scenario set - its holdings, entry IVs and
                // utilities were computed against the option book. An EQUITY hedge fill used to wipe
                // BestOrder and the sweep clock as well, and the delta hedge fills constantly, so every
                // option quote after a hedge fill lost its sweep and silently fell back to the model
                // price (visible as "no sweep IV ... bestOrder=none" right after the hedge fill).
                if (orderEvent.Symbol.SecurityType == SecurityType.Option)
                {
                    RiskScenarioHandler.ClearScenarios(ToEquity(Underlying(orderEvent.Symbol)));
                }

                var trades = WrapToTrade(orderEvent);
                ApplyToPosition(trades);

                PfRisk.CheckHandleDeltaRiskExceedingBand(orderEvent.Symbol);
                RiskProfiles[Underlying(orderEvent.Symbol)].Update();

                // Not urgent
                LogOrderEvent(orderEvent);

                Publish(new TradeEventArgs(trades));

                LogOnEventOrderFill(orderEvent);

                InternalAudit(orderEvent);
                
                LogPositions();
                
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

                        ClearTargetHoldings(underlying);
                        ReduceTargetPortfolios(underlying, orderEvent.Symbol, orderEvent.FillQuantity);
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

            // A terminal ticket ends that contract's sweep clock: the next submission for the same
            // contract freezes fresh anchors from the then-current book and surface.
            if (orderFilledCanceledInvalid.Contains(orderEvent.Status))
            {
                RiskScenarioHandler.ClearSweep(orderEvent.Symbol);
            }

            HandleSpreadBuffers(orderEvent);
        }

        private void LogDifferenceTargetHoldingsOrderTickets()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            foreach (var kvp in TargetHoldings)
            {
              try
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
                    double marginalUtility = MarginalUtility.TryGetValue(holding, out marginalUtility) ? marginalUtility : 0;
                    Error($"{Time} No order ticket present for {symbol}. Remaining Quantity to fill: {quantityToOrder}. Marginal utility: {marginalUtility:0.00}, Util: {util:0.00} UUtil: {util.Utility:0.00} UEquity: {util.UtilityEquityPosition:0.00} UGamma: {util.UtilityGamma:0.00}");
                }
              }
              catch (Exception error)
              {
                  // Diagnostic-only event: a failing utility must not end the run.
                  Error($"{Time} LogDifferenceTargetHoldingsOrderTickets: {kvp.Key} skipped — {error.Message}");
              }
            }
        }

        private static string Portfolio2String(TargetPortfolioPb portfolio)
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
            if (
                !IsPreparingEarningsRelease(underlying)
                || !TargetPortfolios.ContainsKey(underlying)
            )
            {
                return;
            }

            var nextReleaseDate = NextReleaseDate(underlying);

            foreach (TargetPortfolioPb portfolio in TargetPortfolios[underlying].ToList())
            {
                Log(
                    $"{Time} {underlying} WeightedAvgDNLV: {portfolio.ResultStressTestDs.WeightedDnlv:0.00}, Obj: {portfolio.Objective:0.00}, TargetPortfolio: {Portfolio2String(portfolio)}");
                foreach (var holding in portfolio.Holdings.Values)
                {
                    // Resolved WITHOUT requiring a subscription: TargetHoldings is what the subscription
                    // sync diffs against, so a first-time contract must land here before it is carried.
                    Symbol option = ResolveOptionContract(underlying, holding.Symbol);
                    if (option == null || option.SecurityType != SecurityType.Option)
                    {
                        Log($"{Time} SetTargetHoldingsFromTargetPortfolios: {underlying} cannot resolve '{holding.Symbol}' from SymbolCache or the session chain. Skipping.");
                        continue;
                    }
                    decimal quantity = (int)holding.Quantity;

                    // Delaying adding highly liquid options to target portfolio until last trading session before release to allow room for spot moves.
                    // Liquidity is only knowable for a carried contract; one we have not subscribed yet is not delayed.
                    bool skipToday = Securities.TryGetValue(option, out Security optionSecurity) &&
                                     optionSecurity is Option security &&
                                     IsPresumablyLiquid(security) && Time.Date < nextReleaseDate && quantity < 0;
                    if (skipToday)
                        Log($"{Time} {underlying} Skipping {option} from target portfolio because liquid and today is not release day.");

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

        /// <summary>
        /// Resolve every contract string a target-portfolio response carries into a Symbol and leave it
        /// in the cache, before any filter or state update reads one. Nothing is subscribed here:
        /// TargetHoldings (holdings ∪ target != 0) decides that, and SyncOptionSubscriptions does the
        /// AddOptionContract for the contracts we can act on.
        /// </summary>
        private void PrimeContractSymbols(IEnumerable<TargetPortfolioPb> portfolios, Symbol underlying)
        {
            int resolved = 0;
            int unresolved = 0;
            foreach (TargetPortfolioPb portfolio in portfolios)
            {
                foreach (var holding in portfolio.Holdings.Values)
                {
                    if (ResolveOptionContract(underlying, holding.Symbol) == null)
                    {
                        unresolved++;
                    }
                    else
                    {
                        resolved++;
                    }
                }
            }
            Log($"{Time} PrimeContractSymbols: {underlying} {resolved} contract symbol(s) cached from the response ({unresolved} unresolved).");
        }

        private IEnumerable<TargetPortfolioPb> TargetPortfoliosWithoutOppositeQuantities(
            IEnumerable<TargetPortfolioPb> portfolios,
            Symbol underlying
            )
        {
            List<TargetPortfolioPb> result = new();
            Dictionary<Symbol, decimal> simulatedTargetHoldings = new();
            foreach (TargetPortfolioPb portfolio in portfolios.ToList())
            {
                if (portfolio.Objective <= 0)
                {
                    continue;
                }

                bool introducesOppositeQuantities = false;
                foreach (var holding in portfolio.Holdings.Values)
                {
                    Symbol symbol = ResolveOptionContract(underlying, holding.Symbol);
                    if (symbol != null)
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
        private void SetEquiUtilitySweeperFromTargetPortfolios(List<TargetPortfolioPb> TargetPortfolios, Symbol underlying)
        {
            // For each entry, pick the <symbol, quantity> and maximum utility.
            List<TradeUtility> tradeUtilities = new();

            foreach (TargetPortfolioPb targetPortfolio in TargetPortfolios)
            {
                foreach (var kvp in targetPortfolio.Holdings)
                {
                    Symbol symbol = ResolveOptionContract(underlying, kvp.Key);
                    if (symbol == null || !Securities.ContainsKey(symbol))
                    {
                        Log($"{Time} SetEquiUtilitySweeperFromTargetPortfolios: Holding symbol '{kvp.Key}' not subscribed. Skipping.");
                        continue;
                    }
                    decimal quantity = (int)kvp.Value.Quantity;
                    double utility = CollectionExtensions.GetValueOrDefault(targetPortfolio.ResultStressTestDs.MarginalUtilityByHolding, kvp.Key, 0);
                    // The IV map is keyed by the wire string, which need not match Symbol.Value spacing.
                    double entryIv = CollectionExtensions.GetValueOrDefault(targetPortfolio.Ivs, symbol.Value,
                        CollectionExtensions.GetValueOrDefault(targetPortfolio.Ivs, kvp.Key, 0));
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
            List<PfRiskScenarioPb> riskScenarios = new();

            foreach (TradeUtility tradeUtility in tradeUtilities)
            {
                PfRiskScenarioPb scenario = new()
                {
                    Underlying = tradeUtility.Underlying,
                    HoldingsHedge =
                    {
                        [tradeUtility.Symbol.Value] = new HoldingPb()
                        {
                            Symbol = tradeUtility.Symbol, Quantity = (float)tradeUtility.Quantity,
                            SecurityType = SecurityTypePb.Option
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
            if (e.ResponseTargetPortfolios == null)
            {
                Log($"{Time} OnTargetPortfolios: ResponseTargetPortfolios is null. Ignore.");
                return;
            }

            string underlyingTicker = e.ResponseTargetPortfolios.Underlying;
            if (string.IsNullOrEmpty(underlyingTicker) || !Securities.ContainsKey(underlyingTicker))
            {
                Log($"{Time} OnTargetPortfolios: Underlying '{underlyingTicker}' not subscribed. Ignore.");
                return;
            }

            if (!IsMyMarketOpen(underlyingTicker))
            {
                Log($"{Time} OnTargetPortfolios: Underlying '{underlyingTicker}'. Market closed. Ignore.");
                return; // Avoids Running Signals after warmup has finished and a test fetch is scheduled.
            }

            Symbol underlying = Securities[underlyingTicker].Symbol;

            // Every contract this response names goes into the Symbol cache BEFORE any filter reads it.
            // The response is strings only, and an unresolved string is what sent the lookup into
            // SymbolCache's SID-parse fallback (see ResolveOptionContract) — that path both spams an
            // ERROR per contract and, for ~4% of values, throws NullReferenceException and takes the
            // whole response down with it.
            PrimeContractSymbols(e.ResponseTargetPortfolios.TargetPortfolios, underlying);

            var targetPfs = TargetPortfoliosWithoutOppositeQuantities(e.ResponseTargetPortfolios.TargetPortfolios, underlying).ToArray();
            Log($"{Time} OnTargetPortfolios: {targetPfs.Length} portfolios received. IsLastTransmission: {e.ResponseTargetPortfolios.IsLastTransmission}");
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

            foreach (string pfUnderlying in targetPfs.Select((p) => p.Underlying).Distinct())
            {
                if (string.IsNullOrEmpty(pfUnderlying) || !Securities.ContainsKey(pfUnderlying))
                {
                    Log($"{Time} OnTargetPortfolios: Underlying '{pfUnderlying}' not subscribed. Skipping.");
                    continue;
                }
                TargetPortfolios[Securities[pfUnderlying].Symbol] = new();
            }

            bool loggedBestPresumedIv = false;
            bool loggedBestMarginalUtil = false;
            
            foreach (TargetPortfolioPb pf in targetPfs.OrderBy(pf => pf.Objective).Reverse())
            {
                if (!IsTargetPortfolioCompatibleWithHoldings(pf))
                {
                    continue;
                }

                Symbol pfSymbol = string.IsNullOrEmpty(pf.Underlying) || !Securities.ContainsKey(pf.Underlying)
                    ? underlying
                    : Securities[pf.Underlying].Symbol;

                if (!loggedBestPresumedIv)
                {
                    Log($"{Time} OnTargetPortfolios, Presumed Fill IVs:\n{string.Join("\n", pf.Ivs.Select(kvp => $"{kvp.Key}: {kvp.Value:0.000}"))}");
                    loggedBestPresumedIv = true;
                }
                
                pf.Ivs.DoForEach(kvp =>
                {
                    Symbol symbol = ResolveOptionContract(pfSymbol, kvp.Key);
                    if (symbol != null && Securities.ContainsKey(symbol))
                    {
                        if (Securities[symbol].Type != SecurityType.Option) return;
                        Option option = (Option)Securities[symbol];
                        PresumedFillIV[option] = kvp.Value;
                    }
                });

                if (string.IsNullOrEmpty(pf.Underlying) || !Securities.ContainsKey(pf.Underlying))
                {
                    Log($"{Time} OnTargetPortfolios: Underlying '{pf.Underlying}' not subscribed. Skipping portfolio: {Portfolio2String(pf)}");
                    continue;
                }
                underlying = pfSymbol;
                if (TargetPortfolios.TryGetValue(underlying, out List<TargetPortfolioPb> list))
                {
                    list.Add(pf);
                }
                else
                {
                    TargetPortfolios[underlying] = new List<TargetPortfolioPb> { pf };
                }

                foreach (var kvp in pf.ResultStressTestDs.MarginalUtilityByHolding) // Includes dNLV and a utility for going delta neutral (should split)
                {
                    Symbol symbol = ResolveOptionContract(underlying, kvp.Key);
                    if (symbol != null &&
                        pf.ResultStressTestDs.Holdings.TryGetValue(kvp.Key, out HoldingPb holdingPb))
                    {
                        // MarginalUtility can be for either adding or removing a symbol from the portfolio. Marginal is not just for adding. Done this way as utility is non-linear. Direction is chosen based on TargetPortfolio.
                        Core.Holding holding = new(symbol, Math.Sign(holdingPb.Quantity));
                        MarginalUtility[holding] = kvp.Value;
                        if (!loggedBestMarginalUtil)
                        {
                            Log($"{Time} MarginalUtility for Quantity={holding.Quantity}, Symbol={holding.Symbol}, Val={kvp.Value:0.00}, Quantity={holding.Quantity}");
                            loggedBestMarginalUtil = true;
                        }
                    }
                }
            }

            // This overrides the risk scenario set to achieve delta neutral. Dont want that when PricingStategy is going delta neutral.
            foreach (var kvp in TargetPortfolios)
            {
                TargetPortfolios[kvp.Key] = kvp.Value
                    .Where(IsTargetPortfolioCompatibleWithHoldings)
                    // Best objective first, and only the best 20: the response carries hundreds of
                    // candidate portfolios and every one of them is carried through the utility and
                    // stress-test paths. The set is already ranked by objective (maxObjective), so
                    // this keeps the actionable head and drops the tail.
                    .OrderByDescending(pf => pf.Objective)
                    .Take(Math.Max(1, Cfg.MaxTargetPortfolios))
                    .ToList();
                SetEquiUtilitySweeperFromTargetPortfolios(TargetPortfolios[kvp.Key], kvp.Key);
            }

            SetTargetHoldingsFromTargetPortfolios();

            // Subscriptions follow the decision state, so this runs AFTER TargetHoldings was rebuilt:
            // holdings ∪ {target != 0}, diffed against what is already subscribed.
            SyncOptionSubscriptions(underlying, "target portfolios");

            // The sweeper was built before these subscriptions existed (it runs in the trim loop above),
            // so every first-time contract was skipped. Rebuild it now that they are carried.
            if (TargetPortfolios.TryGetValue(underlying, out List<TargetPortfolioPb> sweptPortfolios))
            {
                SetEquiUtilitySweeperFromTargetPortfolios(sweptPortfolios, underlying);
            }

            Log($"{Time} UpdateSweepRatios(): Schedule RunSignals {underlying}");
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(1)),
                () => RunSignals(underlying));
        }

        public void OnResultStressTestDs(object sender, ResultStressTestDsEventArgs e)
        {
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
            string dsString = string.Join("\n",
                e.ResultStressTestDs.DsDnlv.OrderBy(kvp => kvp.Key).Select(kvp =>
                    $"{double.Parse(kvp.Key, CultureInfo.InvariantCulture):+0.00;-0.00}:{kvp.Value:0.0}USD"));

            float holdingUnderlyingQuantity =
                e.ResultStressTestDs.Holdings.TryGetValue(e.ResultStressTestDs.Underlying, out HoldingPb holdingUnderlying) ? holdingUnderlying.Quantity : 0;

            double deltaAcrossDs = e.ResultStressTestDs.DeltaTotalAcrossDs;   // weighted across the dS curve
            double deltaSpot = e.ResultStressTestDs.DeltaTotal;               // tangent to the current spot

            LastDeltaAcrossDsOptionsOnly[underlying] = e.ResultStressTestDs.DeltaTotalAcrossDs - holdingUnderlyingQuantity;
            decimal deltaTotalOptions = PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal) - Portfolio[underlying].Quantity;

            // Hedge that would flatten the across-Ds measure (linear in delta: one share moves it by 1).
            int hedgeQuantity = (int)Math.Round(-deltaAcrossDs, MidpointRounding.AwayFromZero);
            double residualAcrossDs = deltaAcrossDs + hedgeQuantity;
            double deltaSpotAfter = deltaSpot + hedgeQuantity;
            string action = hedgeQuantity == 0
                ? "HOLD"
                : $"{(hedgeQuantity > 0 ? "BUY" : "SELL")} {Math.Abs(hedgeQuantity)} {underlying.Value}";
            decimal spot = MidPrice(Securities[underlying].Symbol);

            Log(
                $"{Time} STRESS {e.ResultStressTestDs.Underlying} @ {e.ResultStressTestDs.Ts}  S={spot:0.00}  " +
                $"underlyingPos={holdingUnderlyingQuantity:0}\n" +
                $"  RISK NOW     delta_spot={deltaSpot:0.0} (tangent to S)   " +
                $"delta_across_ds={deltaAcrossDs:0.0} (weighted across dS)   " +
                $"delta_options_only_algo={deltaTotalOptions:0.0}\n" +
                $"  AFTER HEDGE  {action} {Math.Abs(hedgeQuantity)} {underlying.Value} at market → " +
                $"delta_across_ds={residualAcrossDs:0.0}   delta_spot={deltaSpotAfter:0.0}\n" +
                $"  ACTION       {(hedgeQuantity == 0 ? "nothing to do — across-Ds delta is already ~0" : $"{action} to bring the across-Ds stress test to ~0 (residual {residualAcrossDs:0.0})")}\n" +
                $"  DS CURVE     \n{dsString}");
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

        /// <summary>
        /// Builds the state-vector -> params mapping for a GIVEN tenor list.
        /// </summary>
        internal static Func<IReadOnlyList<DateTime>, Func<Vector<double>, SSVIParamsDictionary>> VecToSsviParamsDictionary()
        {
            return (IReadOnlyList<DateTime> tenors) =>
            {
                return (Vector<double> vec) =>
                {
                    SSVIParamsDictionary result = new();
                    for (int i = 0; i < tenors.Count; i++)
                    {
                        result[tenors[i]] = new SSVIParamsRecord(vec[i * 3 + 0], vec[i * 3 + 1], vec[i * 3 + 2]);
                    }

                    return result;
                };
            };
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

        public void OnCmdFetchTargetPortfolio(object sender, CmdFetchTargetPortfolio cmdFetchTargetPortfolio)
        {
            if (cmdFetchTargetPortfolio == null)
            {
                Log($"{Time} OnCmdFetchTargetPortfolio: cmdFetchTargetPortfolio is null. Ignoring.");
                return;
            }

            if (Securities.TryGetValue(cmdFetchTargetPortfolio.Symbol, out Security security))
            {
                RequestTargetPortfolios(Underlying(security.Symbol), force:true);
            }
        }

        internal bool IsPresumablyLiquid(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(this, option, Time.Date);
            double absDelta = Math.Abs(ocw.Delta(MidIV(option.Symbol)));
            return ocw.DaysToExpiration() / 365 < 0.1 && absDelta < 0.8 && absDelta > 0.2;
        }

        /// <summary>
        /// Target Portfolio calculator received holdings H, but decided to kick one out. Wouldnt want to reject it all, rather set that to zero.
        /// Far reason, setting to true for now, hence switching off any removal.
        /// </summary>
        /// <param name="portfolio"></param>
        /// <returns></returns>
        internal bool IsTargetPortfolioCompatibleWithHoldings(TargetPortfolioPb portfolio)
        {
            // A portfolio is still reachable from the current book when, for EVERY option it holds,
            // the algo's position is either already equal (nothing left to trade) or on the same side
            // and smaller in absolute size (the remaining quantity is the order). If the algo holds an
            // option the portfolio does not list, the portfolio does not describe the book any more.
            //
            // Portfolios are keyed by symbol string in the response; resolve them through the session
            // chain (see ResolveOptionContract) and keep a whitespace-insensitive text map so a
            // padded/OSI variant cannot silently drop every holding.
            if (string.IsNullOrEmpty(portfolio.Underlying) || !Securities.ContainsKey(portfolio.Underlying))
            {
                return false;
            }
            Symbol underlying = Securities[portfolio.Underlying].Symbol;

            var bySymbol = new Dictionary<Symbol, float>();
            var byText = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var holding in portfolio.Holdings.Values)
            {
                byText[NormalizeContractValue(holding.Symbol)] = holding.Quantity;
                Symbol resolved = ResolveOptionContract(underlying, holding.Symbol);
                if (resolved != null)
                {
                    bySymbol[resolved] = holding.Quantity;
                }
            }

            var currentOptions = Portfolio
                .Where(kvp => kvp.Key.SecurityType == SecurityType.Option
                              && kvp.Value.Quantity != 0
                              && kvp.Key.Underlying == underlying)
                .ToList();

            foreach (var kvp in currentOptions)
            {
                var option = kvp.Key;
                if (!bySymbol.TryGetValue(option, out var targetQuantity) &&
                    !byText.TryGetValue(NormalizeContractValue(option.Value), out targetQuantity))
                {
                    return false;
                }

                var algoQuantity = (float)kvp.Value.Quantity;
                if (targetQuantity == algoQuantity)
                {
                    continue;   // already executed on this leg
                }
                if (Math.Sign(targetQuantity) != Math.Sign(algoQuantity))
                {
                    return false;
                }
                if (Math.Abs(targetQuantity) < Math.Abs(algoQuantity))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Keeps only the cached target portfolios still reachable from the current holdings, then
        /// re-derives everything that depends on the set (utility sweeper, target holdings). Used
        /// after a fill or a cancellation, where the previous code cleared the whole cache and left
        /// the algorithm unquoted until a fresh solve arrived.
        /// </summary>
        public void ReduceTargetPortfolios(Symbol underlying, Symbol fillSymbol = null, decimal fillQuantity = 0)
        {
            if (!TargetPortfolios.TryGetValue(underlying, out List<TargetPortfolioPb> cached) || cached.Count == 0)
            {
                return;
            }

            var kept = cached.Where(IsTargetPortfolioCompatibleWithHoldings).ToList();
            TargetPortfolios[underlying] = kept;

            // SetEquiUtilitySweeperFromTargetPortfolios(kept);
            // SetTargetHoldingsFromTargetPortfolios(underlying);

            // Anything a live ticket is resting on that no surviving portfolio wants has to go:
            // a fill narrows the target set, and an order for a dropped contract would otherwise sit
            // there forever (CancelOrdersNotAlignedWithTargetPortfolio only looks at TargetHoldings,
            // which no longer contains it).
            var heldBySurvivors = new HashSet<Symbol>();
            foreach (var portfolio in kept)
            {
                foreach (var holding in portfolio.Holdings.Values)
                {
                    Symbol held = ResolveOptionContract(underlying, holding.Symbol);
                    if (held != null)
                    {
                        heldBySurvivors.Add(held);
                    }
                }
            }

            var orphans = new List<(Symbol Symbol, OrderTicket Ticket)>();
            foreach (var kvp in orderTickets)
            {
                if (kvp.Key.SecurityType != SecurityType.Option || kvp.Key.Underlying != underlying)
                {
                    continue;
                }
                foreach (var ticket in kvp.Value)
                {
                    if (IsLiveTicket(ticket) && !heldBySurvivors.Contains(kvp.Key))
                    {
                        orphans.Add((kvp.Key, ticket));
                    }
                }
            }

            foreach (var (orphanSymbol, ticket) in orphans)
            {
                Cancel(ticket, $"ReduceTargetPortfolios: {orphanSymbol} not in any surviving target portfolio");
            }

            var fillText = fillSymbol == null ? string.Empty : $" after fill {fillSymbol} qty={fillQuantity}";
            Log($"{Time} ReduceTargetPortfolios({underlying}){fillText}: {cached.Count} -> {kept.Count} targets reachable, " +
                $"{orphans.Count} orphan ticket(s) cancelled.");
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

        private static bool IsValidMarketDataSnap(MarketDataSnapByUnderlyingPb snap)
        {
            return
                snap != null &&
                !string.IsNullOrEmpty(snap.Ts) &&
                snap.OptionQuotes.Count > 0;
        }

        private Dictionary<string, HoldingPb> PortfolioOptionHoldings(Symbol underlying)
        {
            var holdings = Portfolio
                .Where(kvp =>
                    kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option &&
                    kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => new HoldingPb()
                {
                    Symbol = kvp.Key.Value,
                    Quantity = (float)kvp.Value.Quantity,
                    SecurityType = SecurityType2SecurityTypePb(kvp.Value.Type)
                });
            return holdings;
        }

        public Dictionary<string, HoldingPb> PortfolioUnderlyingHoldings(Symbol underlying)
        {
            //var holdings = Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => new Core.IO.Holding()
            var holdings = Portfolio.Where(kvp => kvp.Value.Quantity != 0 && Underlying(kvp.Key) == underlying)
                .ToDictionary(kvp => kvp.Key.Value, kvp => new HoldingPb()
                {
                    Symbol = kvp.Key.Value,
                    Quantity = (float)kvp.Value.Quantity,
                    SecurityType = SecurityType2SecurityTypePb(kvp.Value.Type)
                });
            return holdings;
        }

        /// <summary>
        /// Option contracts held for this underlying, as an absolute contract count — the same unit
        /// RequestContractsHandler reports for the target portfolio.
        /// </summary>
        private int HeldOptionContracts(Symbol underlying) =>
            Portfolio
                .Where(kvp => kvp.Key.SecurityType == SecurityType.Option && Underlying(kvp.Key) == underlying)
                .Sum(kvp => (int)Math.Abs(kvp.Value.Quantity));

        private void RequestTargetPortfolios(Symbol underlying, bool force = false)
        { 
            try
            {
                HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(underlying), out regimes)
                    ? regimes
                    : new HashSet<MarketRegime>();

                // Contracts the solver would be asked for, needed by the gate below.
                int requestNContracts =
                    RequestContractsHandlers.TryGetValue(underlying, out RequestContractsHandler contractsHandler)
                        ? contractsHandler.GetContractsRequested()
                        : 0;
                int heldContracts = HeldOptionContracts(underlying);

                if (!force)
                {
                    // Each of these terms used to return silently. With 50 UnderlyingMovedX triggers
                    // and not a single request in the log there was no way to tell which one blocked,
                    // so name the reason on the way out.
                    string skip = null;
                    if (IsWarmingUp) skip = "warming up";
                    else if (!regimes.Contains(MarketRegime.PreEarningsRelease))
                        skip = $"regime=[{string.Join(",", regimes)}] has no PreEarningsRelease";
                    else if (!IsMyMarketOpen(symbolSubscribed)) skip = "market closed";
                    else if (!IsPreparingEarningsRelease(underlying))
                        skip = $"release={NextReleaseDate(underlying):yyyy-MM-dd} is outside the preparation window";
                    else if (!IsPastEarningsEntryStartTime(underlying)) skip = "before the earnings entry start time";
                    else if (requestNContracts == heldContracts)
                        skip = $"nothing to solve — the request would ask for {requestNContracts} contracts and the book already holds " +
                               $"{heldContracts}; the solver cannot produce a different portfolio from the same size";
                    if (skip != null)
                    {
                        Log($"{Time} RequestTargetPortfolios: {underlying} skipped — {skip}. force=False");
                        return;
                    }
                }

                Equity equity = ToEquity(underlying);

                if (!IvSurfaceSsviExecution.ContainsKey(equity) && !force)
                {
                    Log($"{Time} RequestTargetPortfolios: {underlying} skipped — SSVI not initialized yet. force=False");
                    return;
                }

                // Build request (requestNContracts was resolved above, before the gate).

                RequestTargetPortfoliosPb requestTargetPortfolios = new()
                {
                    Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                    Underlying = underlying,
                    NContracts = requestNContracts,
                    AlgoMode = LiveMode ? AlgoModePb.Live : AlgoModePb.Backtest
                };
                requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));
                
                SSVIParamsDictionary surfaceParams = CurrentSurfaceParams(equity);
                if (surfaceParams != null && surfaceParams.Count > 0)
                {
                    requestTargetPortfolios.Params.AddRange(IVSSSVIParamsToPb(underlying, surfaceParams.ToDictionary()));
                }
                else
                {
                    Log($"{Time} FetchTargetPortfolios: {underlying}, force={force}. No valid SSVI parameters found. Not fetching target portfolios. Scheduling next try in 1min");
                    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)), () => RequestTargetPortfolios(underlying));
                    return;
                }

                Log($"{Time} RequestTargetPortfolios: {underlying} sending {requestTargetPortfolios.Params.Count} surface " +
                    $"params, force={force}.");
                _ = rMQClient.SendMessageAsync(requestTargetPortfolios);
            }
            catch (Exception e)
            {
                Error($"{Time} RequestTargetPortfolios: {underlying}. {e.Message}.");
            }
        }

        /// <summary>
        /// The SSVI state consumers are using right now
        /// </summary>
        private SSVIParamsDictionary CurrentSurfaceParams(Equity equity)
        {
            // The EXECUTION surface: this is the surface0 Mind prices the book off. The risk surface exists
            // to damp Lean-side greeks; it is not the optimisation input.
            if (IvSurfaceSsviExecution.TryGetValue(equity, out IIVSurface surface) && surface is IVSurfaceSSVI ssvi)
            {
                return ssvi.ModelParams;
            }
            return null;
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

            RequestPfRiskScenariosPb request = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying
            };
            request.Holdings.Add(PortfolioOptionHoldings(underlying));
            
            // A regime change can fire before the IVSurface exists for this underlying, and indexing the dictionary threw
            SSVIParamsDictionary riskParams = CurrentSurfaceParams(equity);
            if (riskParams == null || riskParams.Count == 0)
            {
                Log($"{Time} RequestPfRiskScenarios: no SSVI state for {underlying} yet — not requesting risk scenarios.");
                return;
            }
            request.Params.AddRange(IVSSSVIParamsToPb(underlying, riskParams.ToDictionary()));

            var snap = GetMarketDataSnapUnderlyingOnly(underlying);
            request.MarketDataSnaps.Add(snap);

            // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
            Log($"{Time} RequestPfRiskScenarios: {underlying}");

            _ = rMQClient.SendMessageAsync(request);
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
                Schedule.On("RequestStressTestDs", DateRules.EveryDay(symbolSubscribed), TimeRules.At(currentTime), RequestStressTestDs);
                currentTime = currentTime.Add(new TimeSpan(0, 0, intervalSeconds));
            }
        }

        private void RequestStressTestDs(Symbol underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed) || !IsPreparingEarningsRelease(underlying)) return;

            // The SSVI filter is built lazily once calibration params arrive; until then there is
            // nothing to send (this is what threw KeyNotFound for a contract with no filter yet).
            var equity = ToEquity(underlying);
            if (CurrentSurfaceParams(equity) is null) return;

            Log($"{Time} RequestStressTestDs: {underlying}");

            // Build request
            RequestStressTestDsPb requestStressTestDs = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying,
                AlgoMode = LiveMode ? AlgoModePb.Live : AlgoModePb.Backtest
            };
            requestStressTestDs.Params.AddRange(IVSSSVIParamsToPb(underlying,
                CurrentSurfaceParams(equity)));
            requestStressTestDs.Holdings.Add(PortfolioUnderlyingHoldings(underlying));

            // Stress Test only uses SSVI surface and underlying spot price.
            var snap = GetMarketDataSnapUnderlyingOnly(underlying);
            requestStressTestDs.MarketDataSnaps.Add(snap);
            _ = rMQClient.SendMessageAsync(requestStressTestDs);
        }

        /// <summary>
        /// Start the live surface stream consumer. One stream per underlying; both the execution and
        /// the risk surface id arrive on it and are routed by id.
        /// </summary>
        private void StartSurfaceStream(IEnumerable<string> underlyings)
        {
            var options = new SurfaceStreamOptions
            {
                Host = CfgAlgo.MQHost,
                Port = CfgAlgo.MQPort,
                User = CfgAlgo.MQUser,
                Pass = CfgAlgo.MQPass,
                VirtualHost = CfgAlgo.MQVirtualHost,
                Exchange = Config.Get("surface-stream-exchange", "surfaces"),
                QueuePrefix = Config.Get("surface-stream-prefix", "surface."),
                Offset = Config.Get("surface-stream-offset", "first"),
                Prefetch = (ushort)Math.Max(1, Config.GetInt("surface-stream-prefetch", 200)),
            };
            _surfaceStreamClient = new SurfaceStreamClient(options, msg => Log(msg));
            _ = _surfaceStreamClient.StartAsync(underlyings, OnSurfaceStreamEvent);
        }

        /// <summary>
        /// One live surface event from the stream. `surface_id` selects the destination: the execution
        /// id prices resting orders, the risk id feeds greeks/hedging. Replayed overlaps after a
        /// reconnect are dropped by (symbol, id, ts_snap).
        /// </summary>
        private void OnSurfaceStreamEvent(SurfaceEventPb e)
        {
            if (string.IsNullOrEmpty(e.Underlying) ||
                !Securities.TryGetValue(e.Underlying, out Security security) ||
                security is not Equity equity)
            {
                return;
            }

            if (string.Equals(e.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                LogOncePerDay($"SurfaceStreamFailed:{e.Underlying}:{e.SurfaceId}",
                    $"{Time} OnSurfaceStreamEvent: {e.Underlying} {e.SurfaceId} status=failed; ignored.");
                return;
            }

            bool forExecution = SurfaceIdMatches(e.SurfaceId, ExecutionSurfaceId);
            bool forRisk = SurfaceIdMatches(e.SurfaceId, RiskSurfaceId);
            if (!forExecution && !forRisk)
            {
                LogOncePerDay($"SurfaceStreamUnexpected:{e.SurfaceId}",
                    $"{Time} OnSurfaceStreamEvent: {e.Underlying} {e.SurfaceId} ignored — neither " +
                    $"the execution ({ExecutionSurfaceId}) nor the risk ({RiskSurfaceId}) surface id.");
                return;
            }

            if (!DateTime.TryParseExact(e.TsSnap, DatetTmeFmtProto, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out DateTime ts))
            {
                return;
            }

            var key = (equity.Symbol, e.SurfaceId);
            lock (_surfaceStreamGate)
            {
                if (_lastSurfaceStreamTs.TryGetValue(key, out DateTime last) && ts <= last)
                {
                    return;
                }
                _lastSurfaceStreamTs[key] = ts;
            }

            SSVIParamsDictionary parameters = SurfaceStreamClient.ToParams(e);
            if (parameters.Count == 0)
            {
                return;
            }

            ApplySurface(equity, parameters, $"stream {e.SurfaceId}", raw: e.Kind == "raw",
                execution: forExecution, risk: forRisk);
            Log($"{Time} OnSurfaceStreamEvent: {equity} {e.SurfaceId} {e.Kind} {e.Status} " +
                $"snap={e.TsSnap} tenors={parameters.Count} spot={e.Spot} -> " +
                $"{(forExecution ? "execution" : "")}{(forExecution && forRisk ? "+" : "")}{(forRisk ? "risk" : "")}");
        }

        /// <summary>
        /// Only used in LiveMode - Backtests receive surfaces through OnData from warehouse.
        /// </summary>
        /// <param name="underlying"></param>
        private void RequestSSVICalibration(Equity underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            
            // Build request
            RequestSSVICalibrationPb requestSsviCalibration = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Underlying = underlying.Symbol.Value,
                AlgoMode = LiveMode ? AlgoModePb.Live : AlgoModePb.Backtest
            };

            _ = rMQClient.SendMessageAsync(requestSsviCalibration);

            LastSSVICalibrationRequestTime[underlying.Symbol] = Time;
        }

        private void OnResponseSSVICalibration(object source, ResponseSSVICalibrationEventArgs e)
        {
            if (UseSurfaceStream)
            {
                return;   // live push path: no calibration responses are requested
            }

            Equity underlying = (Equity)Securities[e.ResponseSSVICalibration.Request.Underlying];
            SSVIParamsDictionary responseParams = new(e.ResponseSSVICalibration.Params.ToArray());
            if (responseParams.Count == 0)
            {
                Log($"{Time} OnResponseSSVICalibration: {underlying} empty calibration. Ignoring.");
                return;
            }

            // One entry point with the warehouse rows: the calibration is the product.
            // A calibration response carries ONE surface and no id, so it is the EXECUTION product. Mirror
            // it into the risk store only while both roles share an id — with a distinct risk-surface-id
            // the risk state must come from its own feed, never from the execution stream.
            bool alsoRisk = !RiskSurfaceIsDistinct;
            ApplySurface(underlying, responseParams, "ssvi calibration", raw: true, execution: true, risk: alsoRisk);
            if (!alsoRisk)
            {
                LogOncePerDay($"RiskSurfaceSourceMissing:{underlying.Symbol.Value}",
                    $"{Time} OnResponseSSVICalibration: {underlying} risk surface ({RiskSurfaceId}) has no live source yet — " +
                    $"only the execution surface ({ExecutionSurfaceId}) is being updated from calibration responses.");
            }
        }

        protected override void OnSurfaceUpdated(object sender, IVSurfaceSSVIOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            if (!ActiveRegimes.TryGetValue(e.Equity, out var regimes)) return;
            // if (!ActiveRegimes.TryGetValue(e.Equity, out var regimes) || !regimes.Contains(MarketRegime.PostEarningsRelease)) return;
            
            Log($"{Time} OnSSVISurfaceUpdated(): ...");
                
            List<PfRiskScenarioPb> scenarios = new();
            // Only need scenarios for the targetHoldings
            foreach ((Symbol symbol, decimal targetQ) in TargetHoldings.Where(kvp => kvp.Key.SecurityType == SecurityType.Option))
            {
                decimal orderQ = targetQ - Portfolio[symbol].Quantity;
                if (orderQ == 0) continue;
                    
                Option option = Securities[symbol] as Option;
                double midIv = IvSurfaceSsviExecution[e.Equity].IV(option);
                float ivSpread = (float)(IvAsk(symbol) - IvBid(symbol));
                float entryIvSsvi = (float)midIv - Math.Sign(orderQ) * ivSpread / 2;
                decimal midPriceUnderlying = MidPrice(e.Equity.Symbol);
                double npvEntry = OptionContractWrap.E(this, option, Time.Date).NPV(entryIvSsvi, midPriceUnderlying);
                double currentMid = (double)MidPrice(option.Symbol);
                        
                PfRiskScenarioPb scenario = new()
                {
                    Underlying = e.Equity.Symbol.Value,
                    DPL = 0,
                    Score = 100*(currentMid - npvEntry) * Math.Sign(orderQ),  // Half the spread in USD
                    DPLEqHedged = 0,
                };
                scenario.HoldingsHedge.Add(symbol.Value, new HoldingPb
                {
                    Symbol = symbol.Value,
                    Quantity = (float)orderQ,
                    SecurityType = SecurityTypePb.Option
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

            string underlyingStr = e.ResponsePfRiskScenarios.Underlying;
            Symbol underlying = Securities[underlyingStr].Symbol;
            float deltaTotalOptions = (float)(PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal) -
                Portfolio[underlying].Quantity);
            float eodDeltaToHedge = LastDeltaAcrossDsOptionsOnly.TryGetValue(underlying, out double v) ? (float)v : deltaTotalOptions;
            HoldingPb h;
            List<PfRiskScenarioPb> validScenarios = new();
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
            if (!CfgAlgo.EarningsEntryStartTime.TryGetValue(underlying.Value, out List<int> hourMin))
            {
                if (!CfgAlgo.EarningsEntryStartTime.TryGetValue(CfgDefault, out hourMin))
                {
                    Error($"{Time} IsPastEarningsEntryStartTime: No entry for '{underlying.Value}' or default '{CfgDefault}'");
                    return false; // or true, depending on desired default behavior
                }
            }
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
            // Scheduled every 15 minutes; skipped during warm-up (see OnWarmupFinished, which calls it once).
            if (IsWarmingUp) return;
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
                SyncOptionSubscriptions(underlying, "SetTargetHoldingsToZeroAfterEarnings");
            }
        }

        public override void OnEndOfAlgorithm()
        {
            base.OnEndOfAlgorithm();
            _surfaceStreamClient?.Dispose();
            rMQClient.Dispose();
        }
    }
}
