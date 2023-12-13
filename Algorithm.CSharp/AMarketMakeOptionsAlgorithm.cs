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
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Orders;
using QuantConnect.Data.Market;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Scheduling;
using QuantConnect.Securities.Equity;
using Newtonsoft.Json;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Option;
using QuantConnect.Data.UniverseSelection;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// 
    /// </summary>
    public partial class AMarketMakeOptionsAlgorithm : Foundations
    {
        private DateTime endOfDay;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<AMarketMakeOptionsAlgorithmConfig>(File.ReadAllText("AMarketMakeOptionsAlgorithmConfig.json"));
            Cfg.OverrideWithEnvironmentVariables<AMarketMakeOptionsAlgorithmConfig>();
            File.Copy("./AMarketMakeOptionsAlgorithmConfig.json", Path.Combine(Globals.PathAnalytics, "AMarketMakeOptionsAlgorithmConfig.json"));

            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(30_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 10;

            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText("EarningsAnnouncements.json"));
            DividendSchedule = JsonConvert.DeserializeObject<Dictionary<string, DividendMine[]>>(File.ReadAllText("DividendSchedule.json"));

            // To be handled with API.Essentially get in realtime positions out of algo and ingest orders in realtime
            ManualOrderInstructionBySymbol = JsonConvert.DeserializeObject<ManualOrderInstruction[]>(File.ReadAllText("ManualOrderInstructions.json")).GroupBy(x => x.Symbol).ToDictionary(g => g.Key, g => g.First());
            EarningsBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            mmWindow = new MMWindow(new TimeSpan(9, 31, 00), new TimeSpan(16, 0, 0) - ScheduledEvent.SecurityEndOfDayDelta - TimeSpan.FromMinutes(5));  // 10mins before EOD market close events fire

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);;

            AssignCachedFunctions();

            // Subscriptions
            optionTicker = Cfg.Ticker;
            ticker = optionTicker;
            symbolSubscribed = null;
            liquidateTicker = Cfg.LiquidateTicker;

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: resolution, Market.USA, fillForward: false, extendedMarketHours: true);
                symbolSubscribed ??= equity.Symbol;

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    options.Add(option);
                    var subscribedSymbols = AddOptionIfScoped(option);
                    subscriptions += subscribedSymbols.Count;

                    foreach (string t in optionTicker)
                    {
                        DeltaDiscounts[equity.Symbol] = new RiskDiscount(this, Cfg, equity.Symbol, Metric.Delta100BpUSDTotal);
                        GammaDiscounts[equity.Symbol] = new RiskDiscount(this, Cfg, equity.Symbol, Metric.Gamma100BpUSDTotal);
                        EventDiscounts[equity.Symbol] = new RiskDiscount(this, Cfg, equity.Symbol, Metric.Events);
                        AbsoluteDiscounts[equity.Symbol] = new RiskDiscount(this, Cfg, equity.Symbol, Metric.Absolute);
                    }
                }
                RiskProfiles[equity.Symbol] = new RiskProfile(this, equity);
                UtilityWriters[equity.Symbol] = new UtilityWriter(this, equity);
                OrderEventWriters[equity.Symbol] = new OrderEventWriter(this, equity);
                UnderlyingMovedX[equity.Symbol].UnderlyingMovedXEvent += (object sender, Symbol e) => RunSignals(e);
                UnderlyingMovedX[equity.Symbol].UnderlyingMovedXEvent += (object sender, Symbol e) => SnapPositions();
                UnderlyingMovedX[equity.Symbol].UnderlyingMovedXEvent += RiskProfiles[equity.Symbol].OnDS;
            }
            RealizedPositionWriter = new(this);

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);

            // Before EOD - stop trading & overnight hedge
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(mmWindow.End), CancelOpenOptionTickets);  // Stop MM
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed, 5), HedgeDeltaFlat);  // Equity delta neutral hedge
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed), OnMarketClose);  // just some logging & cache clearing

            // Logging events
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), LogRiskSchedule);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportRiskRecords);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportIVSurface);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), ExportPutCallRatios);

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), SetTradingRegime);

            // WARMUP
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(Cfg.WarmUpDays + 1).First();
            // Add a day if live
            timeSpan += LiveMode ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            Log($"WarmUp TimeSpan: {timeSpan} starting on {StartDate - timeSpan}");
            SetWarmUp(timeSpan);

            // Logging
            RiskRecorder = new(this);

            // Wiring up events
            NewBidAskEventHandler += OnNewBidAskEventUpdateLimitPrices;
            NewBidAskEventHandler += OnNewBidAskEventCheckRiskLimits;
            RiskLimitExceededEventHandler += OnRiskLimitExceededEventHedge;

            // For backtesting purposes: Test risk profile moves or compare BT to Live
            SetBacktestingHoldings();
        }

        /// <summary>
        /// The algorithm manager calls events in the following order:
        /// Scheduled Events
        /// Consolidation event handlers
        /// OnData event handler
        /// </summary>
        public override void OnData(Slice slice)
        {
            foreach (Symbol symbol in slice.QuoteBars.Keys)
            {
                if (symbol.SecurityType == SecurityType.Equity)
                {
                    IVSurfaceRelativeStrikeBid[symbol].ScheduleUpdate();
                    IVSurfaceRelativeStrikeAsk[symbol].ScheduleUpdate();
                }
            }

            equities.DoForEach(underlying => IVSurfaceRelativeStrikeBid[underlying].ProcessUpdateFlag());
            equities.DoForEach(underlying => IVSurfaceRelativeStrikeAsk[underlying].ProcessUpdateFlag());

            if (IsWarmingUp) return;

            foreach (Symbol symbol in slice.QuoteBars.Keys)
            {
                if (IsEventNewQuote(symbol)) // also called in Consolidator. Should cache result at timestamp, update PriceCache and read here from cache.
                {
                    Publish(new NewBidAskEventArgs(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }
            PfRisk.ResetCache();

            foreach (Symbol underlying in equities)
            {
                if (SignalsLastRun[underlying] < Time - TimeSpan.FromMinutes(30)) RunSignals(underlying);
            }
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

        public void CancelOcaGroup(OrderEvent orderEvent)
        {
            Order order = Transactions.GetOrderById(orderEvent.OrderId);
            if (order.OcaGroup != null)
            {
                CancelOcaGroup(order.OcaGroup);
            }
        }

        public void CancelOcaGroup(string ocaGroup)
        {
            var tickets = orderTickets.Values.SelectMany(t => t).Where(t => t.OcaGroup == ocaGroup).ToList();
            Log($"{Time} Canceling OcaGroup: {ocaGroup}.");
            tickets.DoForEach(t => Cancel(t));
        }

        public override void OnAssignmentOrderEvent(OrderEvent assignmentEvent)
        {
            Log($"OnAssignmentOrderEvent: {assignmentEvent}");
        }

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            changes.AddedSecurities.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                securityInitializer.RegisterIndicators((Option)sec);
            });
        }

        public List<Symbol> AddOptionIfScoped(Symbol optionSymbol)
        {
            //if (!IsMarketOpen(hedgeTicker[0])) return new List<Symbol>();

            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(optionSymbol, Time);
            List<Symbol> subscribedSymbol = new();
            foreach (var symbol in contractSymbols)
            {
                if (Securities.ContainsKey(symbol) && Securities[symbol].IsTradable) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                // Todo: move the period parameter to a config
                var historyUnderlying = HistoryWrap(symbolUnderlying, 7, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractScopedForSubscription(symbol, lastClose, Cfg.scopeContractStrikeOverUnderlyingMargin))
                    {
                        var item = AddData<VolatilityBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        item.IsTradable = false;

                        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false, extendedMarketHours: true);
                        //securityInitializer.RegisterIndicators(option);

                        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                        subscribedSymbol.Add(symbol);
                    }
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"No history for {symbolUnderlying}. Not subscribing to its options." } });
                }
            }
            return subscribedSymbol;
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                RemoveUniverseSecurity(sec);
            });

            // Add options that have moved into scope
            options.DoForEach(s => AddOptionIfScoped(s));
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay) { return; }
            SnapPositions();
            LogPortfolioHighLevel();
            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Globals.PathAnalytics, "PositionLifeCycle.csv"));
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Globals.PathAnalytics, "PositionLifeCycle.csv"));
            RiskRecorder.Dispose();
            IVSurfaceRelativeStrikeBid.Values.DoForEach(s => s.Dispose());
            IVSurfaceRelativeStrikeAsk.Values.DoForEach(s => s.Dispose());
            RiskProfiles.Values.DoForEach(s => s.Dispose());
            UtilityWriters.Values.DoForEach(s => s.Dispose());
            OrderEventWriters.Values.DoForEach(s => s.Dispose());
            PutCallRatios.Values.DoForEach(s => s.Dispose());
            RealizedPositionWriter.Dispose();
        }

        public void OnMarketOpen()
        {
            if (IsWarmingUp) { return; }

            // New day => Securities may have fallen into scope for trading embargo.
            embargoedSymbols = Securities.Keys.Where(s => EarningsAnnouncements.Where(ea => ea.Symbol == s.Underlying && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()).ToHashSet();

            // Trigger events
            Securities.Values.Where(s => s.Type == SecurityType.Equity).DoForEach(s => Publish(new NewBidAskEventArgs(s.Symbol)));

            LogRisk();
            LogPnL();
            LogPositions();
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

            InitializePositionsFromPortfolioHoldings();
            InitializeTradesFromPortfolioHoldings();

            TotalPortfolioValueSinceStart = Portfolio.TotalPortfolioValue;

            LogRisk();
            LogPnL();

            OnMarketOpen();

            equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));

            OnWarmupFinishedCalled = true;
        }
        /// <summary>
        /// Dump portfolio risk metrics by underlying to csv for outside plotting
        /// </summary>
        public void ExportRiskRecords()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            optionTicker.DoForEach(ticker => RiskRecorder.Record(ticker));
        }

        public void SnapPositions()
        {
            Positions.Values.Where(p => p.Quantity != 0).DoForEach(p => Snap(p.Symbol));
        }

        public void ExportIVSurface()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            IVSurfaceRelativeStrikeBid.Values.Union(IVSurfaceRelativeStrikeAsk.Values).DoForEach(s => s.WriteCsvRows());
        }
        public void ExportPutCallRatios()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            PutCallRatios.Where(kvp => kvp.Key.SecurityType == SecurityType.Equity).DoForEach(kvp => kvp.Value.Export());
        }

        public void HedgeDeltaFlat()
        {
            if (!IsMarketOpen(symbolSubscribed)) return;

            foreach (string ticker in ticker)
            {
                Equity equity = (Equity)Securities[ticker];
                Log($"{Time} HedgeDeltaFlat: {equity.Symbol}");
                HedgeOptionWithUnderlying(equity.Symbol);
            }
        }

        public void OnMarketClose()
        {
            optionTicker.DoForEach(ticker => IVSurfaceRelativeStrikeBid[ticker].OnEODATM());
            optionTicker.DoForEach(ticker => IVSurfaceRelativeStrikeAsk[ticker].OnEODATM());

            Log($"{Time} OptionContractWrap.ClearCache: Removed {OptionContractWrap.ClearCache(Time - TimeSpan.FromDays(3))} instances."); ;
        }
        /// <summary>
        /// Set Holdings in Backtesting to compare a live trading day with a backtesting day
        /// Read Live Holdings from file or pass in arguments.
        /// For best comparison with IB, use market midnight closing prices. Best approximation: T-1 closing prices.
        /// </summary>
        public void SetBacktestingHoldings()
        {
            if (LiveMode || !Cfg.SetBacktestingHoldings) return;
            foreach ((string ticker, decimal quantity, decimal averagePrice) in FetchHoldings())
            {
                try
                {
                    if (!Securities.Keys.Select(s => s.Value).Contains(ticker))
                    {
                        string underlyingTicker = ticker.Split(' ')[0];
                        var optionSymbol = QuantConnect.Symbol.CreateCanonicalOption(underlyingTicker, Market.USA, $"?{underlyingTicker}");
                        var contractSymbols = OptionChainProvider.GetOptionContractList(optionSymbol, Time);
                        contractSymbols.Where(s => s.Value == ticker).DoForEach(contractSymbol => AddOptionContract(contractSymbol, Resolution.Second, fillForward: false, extendedMarketHours: true));
                    }
                    
                    Log($"{Time} SetBacktestingHoldings: Symbol={ticker}, Quantity={quantity}, AvgPrice={averagePrice}");
                    Securities[ticker].Holdings.SetHoldings(averagePrice == 0 ? Securities[ticker].Price : averagePrice, quantity);
                    TotalPortfolioValueSinceStart += Securities[ticker].Holdings.HoldingsValue;                   
                }
                catch (Exception e)
                {
                    Log($"{Time} SetBacktestingHoldings: {ticker} {e.Message}");
                }
                
            }
            //InitializePositionsFromPortfolioHoldings();
        }
        public List<(string, decimal, decimal)> FetchHoldings()
        {
            // Read from file: Symbol, Quantity, AveragePrice
            string pathBacktestingHoldings = Path.Combine(".", Cfg.BacktestingHoldingsFn);
            // read CSV from above path. The CSV contains 2 columns: Symbol, Quantity
            if (File.Exists(pathBacktestingHoldings))
            {
                return File.ReadAllLines(pathBacktestingHoldings).Skip(1).Select(l => l.Split(',')).Select(a => (a[0], decimal.Parse(a[1]), decimal.Parse(a[2]))).ToList();
            }
            return new List<(string, decimal, decimal)>();
        }

        public void SetTradingRegime()
        {
            // Events - earnings. Future, auto-detect events.
            foreach (Symbol underlying in equities)
            {
                ActiveRegimes[underlying] = new();
                bool upcomingEventLongIV = Cfg.UpcomingEventLongIV.TryGetValue(underlying, out upcomingEventLongIV) ? upcomingEventLongIV : Cfg.UpcomingEventLongIV[CfgDefault];
                int upcomingEventCalendarSpreadStartDaysPrior = Cfg.UpcomingEventCalendarSpreadStartDaysPrior.TryGetValue(underlying, out upcomingEventCalendarSpreadStartDaysPrior) ? upcomingEventCalendarSpreadStartDaysPrior : Cfg.UpcomingEventCalendarSpreadStartDaysPrior[CfgDefault];
                foreach (var announcement in EarningsBySymbol[underlying].OrderBy(a => a.Date))
                {
                    if (Time.Date > announcement.Date) continue;
                    if (upcomingEventLongIV && Time.Date >= announcement.Date - TimeSpan.FromDays(20) && Time.Date < announcement.Date - TimeSpan.FromDays(3))
                    {
                        Log($"{Time} SetTradingRegime {underlying}: {Regime.BuyEvent}. announcement.Date: {announcement.Date}");
                        ActiveRegimes[underlying].Add(Regime.BuyEvent);
                    }
                    if (Time.Date >= announcement.Date - TimeSpan.FromDays(upcomingEventCalendarSpreadStartDaysPrior) && Time.Date <= announcement.Date)
                    {
                        Log($"{Time} SetTradingRegime {underlying}: {Regime.SellEventCalendarHedge}. announcement.Date: {announcement.Date}");
                        ActiveRegimes[underlying].Add(Regime.SellEventCalendarHedge);
                    }
                    break;
                }
            }
        }
    }
}
