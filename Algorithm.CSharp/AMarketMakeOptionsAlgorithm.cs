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
using QuantConnect.Configuration;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// 
    /// </summary>
    public partial class AMarketMakeOptionsAlgorithm : Foundations
    {
        private DateTime endOfDay;
        DiskDataCacheProvider _diskDataCacheProvider = new();
        Dictionary<(Resolution, Symbol, TickType), LeanDataWriter> writers = new();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            // Configurable Settings
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(2023, 9, 29);
            SetEndDate(2023, 10, 2);
            //SetStartDate(2023, 10, 5);
            //SetEndDate(2023, 10, 5);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 1;
            Cfg = JsonConvert.DeserializeObject<AMarketMakeOptionsAlgorithmConfig>(File.ReadAllText("AMarketMakeOptionsAlgorithmConfig.json"));
            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText("EarningsAnnouncements.json"));
            DividendSchedule = JsonConvert.DeserializeObject<Dictionary<string, DividendMine[]>>(File.ReadAllText("DividendSchedule.json"));
            EarningBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            mmWindow = new MMWindow(new TimeSpan(9, 31, 00), new TimeSpan(16, 0, 0) - ScheduledEvent.SecurityEndOfDayDelta);  // 2mins before EOD EOD market close events fire
            orderType = OrderType.Limit;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays));

            AssignCachedFunctions();

            // Subscriptions
            optionTicker = Cfg.OptionTicker;
            ticker = optionTicker;
            symbolSubscribed = null;// AddEquity(optionTicker.First(), resolution).Symbol;
            liquidateTicker = Cfg.LiquidateTicker;

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: resolution, fillForward: false);
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
                UtilityWriters[equity.Symbol] = new UtilityWriter(equity);
                OrderEventWriters[equity.Symbol] = new OrderEventWriter(this, equity);
            }

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(mmWindow.End), CancelOpenOptionTickets);  // Leaves EOD Equity hedges.
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(30)), LogRiskSchedule);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(mmWindow.Start), RunSignals);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), RunSignals); // not event driven, bad. Essentially
            //Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), SnapPositions);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportRiskRecords);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportIVSurface);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), ExportPutCallRatios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed, 3), HedgeDeltaFlat); // Meeds to fill within a minute, otherwise canceled. Refactor to turn to MarketOrder then.
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed), OnMarketClose);
            // Turn Limit Equity into EOD before market close...

            // WARMUP
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(Cfg.WarmUpDays + 1).First();
            Log($"WarmUp TimeSpan: {timeSpan} starting on {StartDate - timeSpan}");
            SetWarmUp(timeSpan);

            // Logging
            RiskRecorder = new(this);
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
                    PublishEvent(new EventNewBidAsk(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }
            SubmitLimitIfTouchedOrder();
            RecordMarketData(slice);
        }

        /// <summary>
        /// Currently only used for gamma scalping.
        /// </summary>
        public void SubmitLimitIfTouchedOrder()
        {
            LimitIfTouchedOrderInternals.DoForEach(kvp =>
            {
                Symbol symbol = kvp.Key;
                OrderDirection direction = kvp.Value.Quantity < 0 ? OrderDirection.Sell : OrderDirection.Buy;
                if (
                    (direction == OrderDirection.Buy && Securities[symbol].Price >= kvp.Value.LimitPrice) ||
                    (direction == OrderDirection.Sell && Securities[symbol].Price <= kvp.Value.LimitPrice)
                )
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "LimitIfTouchedOrder triggered" }, { "f", $"GetHedgeOptionWithUnderlying" },
                                    { "Symbol", symbol}, { "OrderQuantity",  kvp.Value.Quantity.ToString() }, {"Price", kvp.Value.LimitPrice.ToString() } });
                    OrderEquity(symbol, kvp.Value.Quantity, kvp.Value.LimitPrice, "LimitIfTouchedOrder triggered");
                    LimitIfTouchedOrderInternals.Remove(symbol);
                }                 
            });
        }

        public void RecordMarketData(Slice slice)
        {
            // Move into consolidator events. Not driving any biz logic.
            // Record data for restarts and next day comparison with history. Avoid conflict with Paper on same instance by running this for live mode only.
            if (LiveMode && Config.Get("ib-trading-mode") == "live")
            {
                foreach (KeyValuePair<Symbol, QuoteBar> kvp in slice.QuoteBars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Quote), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, Config.Get("data-folder"), TickType.Quote, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
                    writer.Write(dataW);
                }

                foreach (KeyValuePair<Symbol, TradeBar> kvp in slice.Bars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Trade), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, Config.Get("data-folder"), TickType.Trade, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
                    writer.Write(dataW);
                }
            }
        }
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            OrderEvents.Add(orderEvent);
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                LogOrderEvent(orderEvent);
            }
            OrderEventWriters[Underlying(orderEvent.Symbol)].Write(orderEvent);

            foreach (var tickets in orderTickets.Values)
            {
                tickets.RemoveAll(t => orderFilledCanceledInvalid.Contains(t.Status));
            }
            PublishEvent(orderEvent);
        }

        public override void OnAssignmentOrderEvent(OrderEvent assignmentEvent)
        {
            Log($"OnAssignmentOrderEvent: {assignmentEvent}");
        }

        /// <summary>
        /// Event driven: On MarketOpen, OnFill, OnRiskProfileChanges / Thresholds crossed
        /// </summary>
        public void RunSignals()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed) || Time.TimeOfDay <= mmWindow.Start || Time.TimeOfDay >= mmWindow.End) return;
            if (!OnWarmupFinishedCalled)
            {
                OnWarmupFinished();
            }

            var signals = GetDesiredOrders();
            HandleDesiredOrders(signals);
        }

        public List<Symbol> AddOptionIfScoped(Symbol option)
        {
            //if (!IsMarketOpen(hedgeTicker[0])) return new List<Symbol>();

            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            List<Symbol> subscribedSymbol = new();
            foreach (var symbol in contractSymbols)
            {
                if ( Securities.ContainsKey(symbol) && Securities[symbol].IsTradable ) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                // Todo: move the period parameter to a config
                var historyUnderlying = HistoryWrap(symbolUnderlying, 7, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractInScope(symbol, lastClose))
                    {
                        var item = AddData<VolatilityBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        item.IsTradable = false;

                        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
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
            LogPortfolioHighLevel();
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "PositionLifeCycle.csv"));
            //ExportToCsv(Positions.Values.Where(p => p.Trade0.Quantity != 0), Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "PositionLifeCycle.csv"));
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "PositionLifeCycle.csv"));
            RiskRecorder.Dispose();
            IVSurfaceRelativeStrikeBid.Values.DoForEach(s => s.Dispose());
            IVSurfaceRelativeStrikeAsk.Values.DoForEach(s => s.Dispose());
            RiskProfiles.Values.DoForEach(s => s.Dispose());
            UtilityWriters.Values.DoForEach(s => s.Dispose());
            OrderEventWriters.Values.DoForEach(s => s.Dispose());
            PutCallRatios.Values.DoForEach(s => s.Dispose());
            _diskDataCacheProvider.DisposeSafely();
        }

        public void OnMarketOpen()
        {
            if (IsWarmingUp) { return; }

            embargoedSymbols = Securities.Keys.Where(s => EarningsAnnouncements.Where(ea => ea.Symbol == s.Underlying && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()).ToHashSet();

            CancelGammaHedgeBeyondScope();

            // Trigger events
            foreach (Security security in Securities.Values.Where(s => s.Type == SecurityType.Equity))  // because risk is hedged by underlying
            {
                PublishEvent(new EventNewBidAsk(security.Symbol));
                PfRisk.IsRiskLimitExceededZMBands(security.Symbol);
            }

            LogRisk();
            LogPnL();
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

            InitializePositionsFromPortfolio();

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
            Positions.Values.Where(p => p.Quantity != 0).DoForEach(p => p.Snap());
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

            LimitIfTouchedOrderInternals.Clear();
            foreach (string ticker in optionTicker)
            {
                Equity equity = (Equity)Securities[ticker];
                Log($"HedgeDeltaFlat EOD: {equity.Symbol}");
                HedgeOptionWithUnderlying(equity.Symbol);
            }
        }

        public void OnMarketClose()
        {
            optionTicker.DoForEach(ticker => IVSurfaceRelativeStrikeBid[ticker].OnEODATM());
            optionTicker.DoForEach(ticker => IVSurfaceRelativeStrikeAsk[ticker].OnEODATM());
        }
    }
}
