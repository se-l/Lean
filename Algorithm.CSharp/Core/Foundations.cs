using Newtonsoft.Json;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Utils;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using Trade = QuantConnect.Algorithm.CSharp.Core.Risk.Trade;

namespace QuantConnect.Algorithm.CSharp.Core
{

    public partial class Foundations : QCAlgorithm
    {
        public SecurityInitializerMine securityInitializer;
        public JuliaPricingClient julia;
        internal Resolution resolution;

        /// <summary>
        /// Daily TradeBar series fetched by the seeder (GetLastKnownPricesTradeOrQuote), keyed by
        /// symbol so SecurityInitializerMine's fill model can reuse them for the mean daily volume
        /// instead of issuing a second daily request. Consumed (removed) by the initializer.
        /// </summary>
        public Dictionary<Symbol, List<TradeBar>> SeedDailyTradeBars = new();

        public Dictionary<Symbol, QuoteBarConsolidator> QuoteBarConsolidators = new();
        public Dictionary<Symbol, TradeBarConsolidator> TradeBarConsolidators = new();
        public List<OrderEvent> OrderEvents = new();
        public ConcurrentDictionary<Symbol, List<OrderTicket>> orderTickets = new();
        public HashSet<string> optionTicker;
        public HashSet<string> liquidateTicker;
        public HashSet<string> ticker;
        public HashSet<Symbol> equities = new();
        public HashSet<Symbol> options = new();  // Canonical symbols
        public MMWindow mmWindow;
        public Symbol symbolSubscribed;
        public Dictionary<Symbol, SecurityCache> PriceCache = new();

        public virtual bool HasActiveRequests() => false;

        /// <summary>True when the pricer-cache plane runs (set by the algorithm's Initialize).
        /// The IV indicator reads this to decide whether a cache lookup precedes the feed IV.</summary>
        public bool PricerCacheEnabled;
        public SecurityExchangeHours SecurityExchangeHours;
        private DateTime endOfDay;

        internal Dictionary<Symbol, IVQuoteIndicator> IvBids = new();
        internal Dictionary<Symbol, IVQuoteIndicator> IvAsks = new();
        public Dictionary<Equity, IIVSurface> IvSurfaceSsviMid = new();

        public Dictionary<int, IUtilityOrder> OrderTicket2UtilityOrder = new();
        public RiskScenarioHandler RiskScenarioHandler;
        
        public Dictionary<(Symbol, decimal), UnderlyingMovedX> UnderlyingMovedX = new();
        public Dictionary<Equity, HashSet<MarketRegime>> ActiveRegimes = new();
        public ConcurrentDictionary<Symbol, List<PositionSnap>> PositionSnaps = new();
        public ConcurrentDictionary<Symbol, List<QuotePb>> MarketDataQuotes = new();
        public ConcurrentDictionary<Symbol, List<Core.IO.TradePb>> MarketDataTrades = new();
        public ConcurrentDictionary<Symbol, DateTime> LastSSVICalibrationRequestTime = new();
        // End

        public RiskRecorder RiskRecorder;
        public TickCounter TickCounterFilter;  // Not in use
        public PortfolioRisk PfRisk;
        public bool OnWarmupFinishedCalled = false;
        public decimal TotalPortfolioValueSinceStart = 0m;
        public Dictionary<int, OrderFillData> OrderFillDataTN1 = new();
        public Dictionary<Symbol, List<Trade>> Trades = new();
        public Dictionary<Symbol, Position> Positions = new();
        public EarningsAnnouncement[] EarningsAnnouncements;
        public Dictionary<string, double> DividendYield;
        public Dictionary<string, ManualOrderInstruction> ManualOrderInstructionBySymbol;
        public Dictionary<string, EarningsAnnouncement[]> EarningsBySymbol;
        public FoundationsConfig Cfg;

        /// <summary>
        /// Live configuration deltas (RabbitMQ "config.#"). Started in Initialize; applied on the
        /// algorithm thread at the top of OnData, so settings never change mid-cycle.
        /// </summary>
        public ConfigUpdateChannel ConfigUpdates;
        public string FoundationsConfigFileName = "FoundationsConfig.json";

        public Dictionary<Symbol, RiskDiscount> AbsoluteDiscounts = new();

        public Dictionary<Symbol, RiskProfile> RiskProfiles = new();
        public Dictionary<Symbol, GammaScalper> GammaScalpers = new();
        public Dictionary<Symbol, OrderEventWriter> OrderEventWriters = new();
        public TradeWriter TradeWriter;
        public Dictionary<int, Quote<Option>> Quotes = new();
        public Dictionary<int, double> OrderIdIv = new();
        public ConcurrentDictionary<Symbol, List<Position>> PositionsRealized = new();

        public HashSet<Symbol> embargoedSymbols = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
        public HashSet<OrderStatus> orderCanceledOrPending = new() { OrderStatus.CancelPending, OrderStatus.Canceled };
        public HashSet<OrderStatus> orderFilledCanceledInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid };
        public HashSet<OrderStatus> orderFilledCanceledCancelPendingInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.CancelPending, OrderStatus.Invalid };

        /// <summary>Per-symbol cap on retained OrderTickets (see <see cref="PruneOrderTickets"/>).</summary>
        private const int MaxTicketsPerSymbol = 2_000;
        public HashSet<OrderStatus> orderPartialFilledCanceledPendingInvalid = new() { OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.CancelPending, OrderStatus.Canceled, OrderStatus.Invalid };
        public HashSet<OrderStatus> orderSubmittedPartialFilledUpdated = new() { OrderStatus.PartiallyFilled, OrderStatus.Submitted, OrderStatus.UpdateSubmitted };
        public HashSet<OrderStatus> orderNewSubmittedPartialFilledUpdated = new() { OrderStatus.New, OrderStatus.PartiallyFilled, OrderStatus.Submitted, OrderStatus.UpdateSubmitted };
        public HashSet<OrderStatus> orderNewSubmittedUpdated = new() { OrderStatus.New, OrderStatus.Submitted, OrderStatus.UpdateSubmitted };
        public HashSet<SecurityType> securityTypeOptionEquity = new() { SecurityType.Equity, SecurityType.Option };
        public HashSet<OrderType> orderTypeMarketLimit = new() { OrderType.Market, OrderType.Limit };

        public HashSet<MarketRegime> beforeEarnings = new() { MarketRegime.PreEarningsReleaseBeforeMarketClose, MarketRegime.PreEarningsRelease };
        public record MMWindow(TimeSpan Start, TimeSpan End);
        Func<Option, decimal> IntrinsicValue;
        public Dictionary<Symbol, DateTime> SignalsLastRun = new();
        /// <summary>Per-underlying single-flight gate + worker threads for off-thread signal passes.</summary>
        public readonly SignalRunner SignalRunner = new();
        /// <summary>Backtests run the pass inline (deterministic); live runs it off-thread.</summary>
        public bool AsyncSignals = false;
        public readonly ConcurrentQueue<Signal> _signalQueue = new();
        public int ocaGroupId;
        public int SignalQuantityDflt = 9999;
        public ConcurrentDictionary<Symbol, decimal> TargetHoldings = new();
        protected readonly ConcurrentDictionary<Symbol, List<TargetPortfolioPb>> TargetPortfolios = new();
        protected IUtilityOrderFactory UtilityOrderFactory;
        public Dictionary<Symbol, double> LastDeltaAcrossDsOptionsOnly = new();
        public readonly ConcurrentDictionary<Holding, double> MarginalUtility = new();
        //public ConcurrentDictionary<Symbol, ConcurrentDictionary<OrderDirection, Sweep>> SweepState = new();
        public Dictionary<OrderDirection, ConcurrentDictionary<Symbol, SpreadBuffer>> SpreadBuffers = new() { { OrderDirection.Buy, new() }, { OrderDirection.Sell, new() } };
        public Dictionary<Equity, KalmanFilter<SSVIParamsDictionary>> KalmanFiltersSSVI = new();
        public Dictionary<Equity, KalmanFilterSSVIWriter> KalmanFilterSSVIWriters = new();
        public Dictionary<Equity, PositionWriter> PositionWriters = new();
        public ConcurrentDictionary<Option, double> PresumedFillIV = new();
        public Dictionary<Symbol, DateTime> CurrentMarketOpen = new();
        public Dictionary<Symbol, DateTime> NextMarketClose = new();
        public Dictionary<Symbol, RequestContractsHandler> RequestContractsHandlers = new();
        public Dictionary<Symbol, SimpleMovingAverage> IVSpreadSMA = new();
        public Dictionary<Symbol, Func<QuoteRequest<Option>, decimal>> PricingStrategy = new();
        
        public TaskHandler TaskHandler = new();

        // Main-thread watchdog. Every queued task runs synchronously inside OnData, so a slow task
        // is a slow slice — and a stalled slice starves the signal drain, which then looks like a
        // stale batch. Both numbers are logged so a stall can be attributed rather than guessed at.
        public double OnDataGapWarnMs = 2_000;
        public double TaskWarnMs = 250;
        private long _lastOnDataTicks;
        private double _lastOnDataMs;

        private void ReportTask(string name, double ms)
        {
            if (ms < TaskWarnMs) return;
            Log($"{Time} SlowTask: {name} took {ms:0}ms on the algorithm thread.");
            QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.algo.task_ms", ms,
                new[] { new KeyValuePair<string, object>("task", name) });
        }
        private DateTime _lastOnDataLagWarningUtc = DateTime.MinValue;
        
        public DateTime TimeWarmupFinished = DateTime.MaxValue;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public void InitializeAlgo(IUtilityOrderFactory utilityOrderFactory)
        {
            UtilityOrderFactory = utilityOrderFactory;
            // Backtests run the pass inline so fills stay deterministic; live runs it off-thread and
            // applies the result on a later slice. Cfg.SignalsAsync can force async in a backtest.
            AsyncSignals = LiveMode || Cfg.SignalsAsync;
            TaskHandler.Timed = ReportTask;
            IvSolveAsync = LiveMode;
            StartIvSolver();
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(1_000_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 10;
            Portfolio.MarginCallModel = MarginCallModel.Null;

            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText(Path.Combine(Globals.DataFolder, "symbol-properties", "EarningsAnnouncements.json")));

            // To be handled with API.Essentially get in realtime positions out of algo and ingest orders in realtime
            ManualOrderInstructionBySymbol = JsonConvert.DeserializeObject<ManualOrderInstruction[]>(File.ReadAllText("ManualOrderInstructions.json")).GroupBy(x => x.Symbol).ToDictionary(g => g.Key, g => g.First());
            EarningsBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);

            AssignCachedFunctions();
            RiskScenarioHandler = new(this);

            // Option chain from the R2 warehouse — registry (option_contract_live) ∩ loaded ledger
            // (loaded_symbol_sessions) for the requested date, never the QC zip/map-file layout
            // (the trade mount carries no chain files). BacktestingSetupHandler installed the
            // zip-based provider before Initialize; swap it here, before the subscription loop's
            // AddOptionIfScoped resolves any chain. The provider caches per (underlying, date).
            // Fail loudly rather than silently continuing on the zip provider: without the R2
            // assembly in the catalog, every chain request would come back empty and the run
            // would "succeed" without options.
            var r2ChainProvider = Composer.Instance.GetExportedValueByTypeName<QuantConnect.Interfaces.IOptionChainProvider>(
                "QuantConnect.Lean.DataSource.R2.R2OptionChainProvider");
            if (r2ChainProvider == null)
            {
                throw new InvalidOperationException(
                    "Foundations: R2OptionChainProvider not found in the composition catalog — " +
                    "the QuantConnect.Lean.DataSource.R2 assembly is missing from the deployment.");
            }
            SetOptionChainProvider(r2ChainProvider);


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
                }
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) => RunSignals(e);
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) => SnapPositions();
                //UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += RiskProfiles[equity.Symbol].OnDS;
            }
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // Needs refactoring to handle early closes and trading across days and reference an updated config.
            mmWindow = new MMWindow(
                GetCurrentMarketOpen(symbolSubscribed).TimeOfDay + TimeSpan.FromMinutes(Cfg.MinutesAfterOpenMMWindowStarts),
                GetNextMarketClose(symbolSubscribed).TimeOfDay - TimeSpan.FromMinutes(Cfg.MinutesBeforeCloseMMWindowEnds)
            );

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On("OnMarketOpen", DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), OnMarketOpen);
            Schedule.On("UpdateUniverseSubscriptions", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On("ExerciseOnExpiryDate", DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 120), ExerciseOnExpiryDate);
            Schedule.On("ClearNextMarketOpenClose", DateRules.EveryDay(symbolSubscribed), TimeRules.At(TimeSpan.FromMinutes(0)), ClearNextMarketOpenClose);
            Schedule.On("SetActiveMarketRegimes", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), SetActiveMarketRegimes);  // make event driven. which event?

            // Before EOD - stop trading & overnight hedge
            Schedule.On("CancelOpenOptionTickets", DateRules.EveryDay(symbolSubscribed), TimeRules.At(mmWindow.End), CancelOpenOptionTickets);  // Stop MM
            Schedule.On("HedgeDeltaFlat", DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed, Cfg.MinutesBeforeMarketCloseHedgeDeltaFlat), HedgeDeltaFlat);
            Schedule.On("OnMarketClose", DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed), OnMarketClose);  // just some logging & cache clearing

            // Logging events
            Schedule.On("LogRiskSchedule", DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), LogRiskSchedule);
            // Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportRiskRecords);

            // WARMUP
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(Cfg.WarmUpDays + 1).First();
            Log($"WarmUp TimeSpan: {timeSpan} starting on {StartDate - timeSpan}");
            SetWarmUp(timeSpan);

            // Logging
            RiskRecorder = new(this);
            TradeWriter = new(this);
            // Stack Overflow
            TradeEventHandler += TradeWriter.OnTradeEvent;
            TradeEventHandler += RiskRecorder.OnTradeEvent;

            // Wiring up events
            NewBidAskEventHandler += OnNewBidAskEventUpdateLimitPrices;
            NewBidAskEventHandler += OnNewBidAskEventCheckRiskLimits;
            NewBidAskEventHandler += AppendQuoteToMarketsDataSnap;
            NewBidAskEventHandler += OnNewBidAskEventUpdateIVSpread;

            RiskLimitExceededEventHandler += OnRiskLimitExceededEventHedge;

            MarketRegimesChangeEventHandler += OnMarketRegimesChange;

            // For backtesting purposes: Test risk profile moves or compare BT to Live
            SetBacktestingHoldings();
        }

        public void ClearTargetPortfolios(Symbol underlying)
        {
            Log($"{Time} ClearTargetPortfolios(): {underlying}");
            
            TargetPortfolios.TryRemove(underlying, out _);
        }

        /// <summary>
        /// Expect triggers cancelation of all dependent orders...
        /// </summary>
        public void ClearTargetHoldings(Symbol underlying)
        {
            lock (TargetHoldings)
            {
                Log($"{Time} ClearTargetHoldings(): {underlying}");
                foreach (Symbol symbol in TargetHoldings.Keys.Where(k => Underlying(k) == underlying).ToList())
                {
                    TargetHoldings.TryRemove(symbol, out _);
                }
            }
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
                OrderTicket ticket = tickets.First();
                if (TargetHoldings.TryGetValue(symbol, out decimal targetQuantity))
                {
                    decimal quantityIfFilled = ticket.Quantity + Portfolio[symbol].Quantity;
                    bool shouldCancel = (targetQuantity < 0 ? quantityIfFilled < targetQuantity : quantityIfFilled > targetQuantity) || ticket.Quantity * targetQuantity < 0;
                    if (shouldCancel)
                    {
                        string tag = $"CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, TicketQuantity={ticket.Quantity}, PortfolioQuantity={Portfolio[symbol].Quantity}, TargetQ={targetQuantity}";
                        Cancel(ticket, tag);
                    }
                }
            }
        }

        public void OnNewBidAskEventUpdateIVSpread(object sender, NewBidAskEventArgs newBidAsk)
        {
            // With the async solver the IVs for this quote do not exist yet; the spread is updated
            // in DrainSolvedIvs, on the same thread, right after they are applied.
            if (IvSolveAsync) return;
            UpdateIvSpread(newBidAsk.Symbol);
        }

        public void UpdateIvSpread(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option) return;
            // Both sides are registered together, but the async apply can arrive for one side only.
            if (!IvBids.ContainsKey(symbol) || !IvAsks.ContainsKey(symbol)) return;
            {
                double spread = IvAsks[symbol].IVBidAsk.IV - IvBids[symbol].IVBidAsk.IV;

                if (!IVSpreadSMA.ContainsKey(symbol))
                {
                    int ivSpreadSMAPeriod = Cfg.IVSpreadSMAPeriod.TryGetValue(symbol, out int period) ? period : Cfg.IVSpreadSMAPeriod[CfgDefault];
                    IVSpreadSMA[symbol] = new SimpleMovingAverage(ivSpreadSMAPeriod);
                }
                IVSpreadSMA[symbol].Update(new IndicatorDataPoint(Time, (decimal)spread));
            }
        }

        public void AppendQuoteToMarketsDataSnap(object sender, NewBidAskEventArgs e)
        {
            Security security = Securities[e.Symbol];
            Symbol underlying = security.Type == SecurityType.Option ? ((Option)security).Underlying.Symbol : security.Symbol;
            if (!Cfg.Ticker.Contains(underlying.Value))
            {
                return;
            }

            QuotePb quote = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Symbol = e.Symbol.Value,
                SecurityType = SecurityType2SecurityTypePb(security.Type),
                Bid = (float)security.BidPrice,
                Ask = (float)security.AskPrice,
                PriceUnderlying = (float)(security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : 0),
            };
            if (MarketDataQuotes.TryGetValue(e.Symbol, out List<QuotePb> quotes))
            {
                quotes.Add(quote);
            }
        }

        public void AppendTradeToMarketsDataSnap(object sender, NewTradeEventArgs e)
        {
            Security security = Securities[e.Symbol];
            Symbol underlying = security.Type == SecurityType.Option ? ((Option)security).Underlying.Symbol : security.Symbol;
            if (!Cfg.Ticker.Contains(underlying.Value))
            {
                return;
            }

            TradePb trade = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Symbol = e.Symbol.Value,
                SecurityType = SecurityType2SecurityTypePb(security.Type),
                Price = (float)security.Price,
                PriceUnderlying = (float)(security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : 0),
            };
            if (MarketDataTrades.TryGetValue(e.Symbol, out List<TradePb> trades))
            {
                trades.Add(trade);
            }
        }

        public DateTime GetCurrentMarketOpen(Symbol symbol)
        {
            var security = Securities[symbol];
            var exchangeHours = MarketHoursDatabase.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType).ExchangeHours;
            DateTime nextMarketOpen = exchangeHours.GetNextMarketOpen(Time, false);
            return Time.Date == nextMarketOpen.Date && Time.TimeOfDay < nextMarketOpen.TimeOfDay ? nextMarketOpen : exchangeHours.GetNextMarketOpen(Time.Subtract(TimeSpan.FromDays(1)), false);
        }
        public DateTime GetNextMarketClose(Symbol symbol)
        {
            var security = Securities[symbol];
            var exchangeHours = MarketHoursDatabase.GetEntry(symbol.ID.Market, symbol, symbol.SecurityType).ExchangeHours;
            return exchangeHours.GetNextMarketClose(Time, false);
            //return exchangeHours.GetNextMarketClose(Time, security.IsExtendedMarketHours);
        }

        public bool IsMyMarketOpen(Symbol symbol)
        {
            if (!NextMarketClose.TryGetValue(symbol, out DateTime nextMarketClose))
            {
                NextMarketClose[symbol] = nextMarketClose = GetNextMarketClose(symbol);
            }
            if (!CurrentMarketOpen.TryGetValue(symbol, out DateTime currentMarketOpen))
            {
                CurrentMarketOpen[symbol] = currentMarketOpen = GetCurrentMarketOpen(symbol);
            }
            return Time >= currentMarketOpen && Time < nextMarketClose;
        }

        public void ClearNextMarketOpenClose()
        {
            CurrentMarketOpen.Clear();
            NextMarketClose.Clear();
            Log($"{Time} ClearNextMarketOpenClose");
        }
        
        void WarnIfProcessedMarketDataIsLagging(Slice slice)
        {
            if (!LiveMode) return;

            var lag = DateTime.UtcNow - slice.UtcTime;
            if (lag <= TimeSpan.FromSeconds(5)) return;

            if (_lastOnDataLagWarningUtc != DateTime.MinValue && DateTime.UtcNow - _lastOnDataLagWarningUtc < TimeSpan.FromSeconds(30)) return;
            _lastOnDataLagWarningUtc = DateTime.UtcNow;

            QuickLog(new Dictionary<string, string>
            {
                { "topic", "DATA" },
                { "msg", $"Processed market data is lagging by {lag.TotalSeconds:F1}s. SliceTime={slice.Time:O}, SliceUtcTime={slice.UtcTime:O}, NowUtc={DateTime.UtcNow:O}" }
            });
        }

        /// <summary>
        /// The algorithm manager calls events in the following order:
        /// Scheduled Events
        /// Consolidation event handlers
        /// OnData event handler
        /// </summary>
        public override void OnData(Slice slice)
        {
            long entry = Stopwatch.GetTimestamp();
            if (_lastOnDataTicks != 0)
            {
                double gapMs = (entry - _lastOnDataTicks) * 1000.0 / Stopwatch.Frequency;
                if (gapMs >= OnDataGapWarnMs)
                {
                    Log($"{Time} OnData gap {gapMs:0}ms since previous entry (previous body {_lastOnDataMs:0}ms, " +
                        $"slice={slice.Time:HH:mm:ss.fff}, dataLag={(DateTime.UtcNow - slice.UtcTime).TotalSeconds:0.0}s).");
                }
            }
            _lastOnDataTicks = entry;

            try
            {
                OnDataCore(slice);
            }
            finally
            {
                _lastOnDataMs = (Stopwatch.GetTimestamp() - entry) * 1000.0 / Stopwatch.Frequency;
                if (_lastOnDataMs >= OnDataGapWarnMs)
                {
                    Log($"{Time} OnData took {_lastOnDataMs:0}ms on the algorithm thread " +
                        $"(slice={slice.Time:HH:mm:ss.fff}).");
                }
            }
        }

        private void OnDataCore(Slice slice)
        {
            // Live config deltas land here, on the algorithm thread: a settings object read by this
            // cycle never changes underneath it. Applied during warm-up too — a knob changed before
            // the session opens should be in force at the open.
            ConfigUpdates?.ApplyPending();

            if (IsWarmingUp) return;
            PaceBacktestNearRealtime();
            // Feed pre-calculated IVs from disk into IvBid/Ask indicators (backtest only),
            // bypassing the Julia pricer call that would otherwise be triggered by the consolidator.
            if (!LiveMode)
            {
                foreach (var (key, volBar) in slice.Get<VolatilityQuoteBar>())
                {
                    Symbol optionSymbol = key.Underlying; // VolatilityQuoteBar's underlying is the option symbol

                    if (volBar.Bid.Close == 0 && volBar.Ask.Close == 0) continue;
                    if (!IvBids.ContainsKey(optionSymbol) || !IvAsks.ContainsKey(optionSymbol)) continue;

                    if (volBar.Bid.Close > 0)
                    {
                        // iv_mid rides on the same bar/timestamp as the sides' IVs, so both side
                        // indicators carry it in their IVQuote (TryGetFeedIVMid reads it from there).
                        IvBids[optionSymbol].Update(volBar.EndTime, (double)volBar.Bid.Close, (double)volBar.Mid.Close);
                    }
                    if (volBar.Ask.Close > 0)
                    {
                        IvAsks[optionSymbol].Update(volBar.EndTime, (double)volBar.Ask.Close, (double)volBar.Mid.Close);
                    }
                }
            }
            
            WarnIfProcessedMarketDataIsLagging(slice);

            foreach (Symbol symbol in slice.QuoteBars.Keys)
            {
                if (!Cfg.Ticker.Contains(Underlying(symbol).Value))
                {
                    continue;
                }
                if (IsEventNewQuote(symbol)) // also called in Consolidator. Should cache result at timestamp, update PriceCache and read here from cache.
                {
                    Publish(new NewBidAskEventArgs(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }

            foreach (Symbol symbol in slice.Bars.Keys)
            {
                AppendTradeToMarketsDataSnap(this, new NewTradeEventArgs(symbol));
            }

            PfRisk.ResetCache();

            // Quote IVs inverted on the worker during the last slice: apply before risk/signals so
            // the whole slice sees one consistent set.
            DrainSolvedIvs();

            foreach (Symbol underlying in equities)
            {
                if (SignalsLastRun[underlying] < Time - TimeSpan.FromMinutes(5)) RunSignals(underlying);
            }

            // Results of any off-thread signal pass, applied here on the algorithm thread.
            DrainCompletedSignals();
            TaskHandler.RunTasks();
        }

        public void PaceBacktestNearRealtime()
        {
            if (!LiveMode && Cfg.BacktestNearRealtimePacingEnabled && HasActiveRequests())
            {
                // Block until all outstanding calc-engine requests have been answered. Delivery of
                // each response at sentAtAlgoTime + elapsedMs is handled by the scheduler, so no
                // wall-clock alignment is needed here.
                var pacingSinceUtc = DateTime.UtcNow;
                var lastLogUtc = pacingSinceUtc;
                Log($"Pacing backtest near realtime: waiting for active calc-engine requests to complete.");
                while (HasActiveRequests())
                {
                    System.Threading.Thread.Sleep(100);
                    if ((DateTime.UtcNow - lastLogUtc).TotalSeconds >= 5)
                    {
                        lastLogUtc = DateTime.UtcNow;
                        Log($"Pacing backtest near realtime: still waiting, elapsed {(DateTime.UtcNow - pacingSinceUtc).TotalSeconds:F0}s");
                    }
                }
            }
        }

        public void CancelOptionTicketsWithSameDeltaSign(Symbol symbol)
        {
            decimal spot = MidPrice(Underlying(symbol));
            if (symbol.SecurityType != SecurityType.Option) return;

            double deltaSign = OptionContractWrap.E(this, (Option)Securities[symbol], Time.Date).Delta(MidIV(symbol), spot);

            var tickets = orderTickets.Values.SelectMany(t => t).Where(t => Underlying(t.Symbol) == Underlying(symbol) && t.SecurityType == SecurityType.Option).ToList();
            foreach (OrderTicket t in tickets)
            {
                if (OptionContractWrap.E(this, (Option)Securities[t.Symbol], Time.Date).Delta(MidIV(t.Symbol), spot) * deltaSign > 0)
                {
                    Cancel(t, "DeltaSign");
                }
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
            string tag = $"Canceling OcaGroup: {ocaGroup}";
            tickets.DoForEach(t => Cancel(t, tag));
        }
        /// <summary>
        /// Exercise options to reduce delta if non-RTH
        /// </summary>
        /// <param name="assignmentEvent"></param>
        public override void OnAssignmentOrderEvent(OrderEvent assignmentEvent)
        {
            Log($"OnAssignmentOrderEvent: {assignmentEvent}");
            if (Time.Date == assignmentEvent.Symbol.ID.Date)
            {
                Symbol underlying = Underlying(assignmentEvent.Symbol);
                // Delta Total. ensure it's after security has been removed
                decimal deltaPfTotal = PfRisk.RiskByUnderlying(underlying, HedgeMetric(underlying));

                var itmPositions = Positions.Values.Where(p => p.Quantity > 0 && p.IsITM1).OrderBy(p => p.Expiry);
                foreach (Position pos in itmPositions)
                {
                    decimal deltaPos = pos.DeltaTotal() / pos.Quantity;
                    decimal ifFilledDeltaPfTotal = deltaPfTotal + deltaPos;

                    if (Math.Abs(ifFilledDeltaPfTotal) < Math.Abs(deltaPfTotal) && Math.Sign(ifFilledDeltaPfTotal) == Math.Sign(deltaPfTotal))
                    {
                        // Exercising this position would not bring us to zero delta
                        decimal quantity = Math.Min((int)pos.Quantity, Math.Floor(Math.Abs(deltaPfTotal / deltaPos)));
                        deltaPfTotal += deltaPos * quantity;
                        ExerciseOption(pos.Symbol, (int)quantity);
                    }
                }
            }
        }

        /// <summary>
        /// On Expiration, there may be a risky mismatch of assignable and exersizable option position leading to potentially large
        /// equity positions to be accumlated overnight, leading to undesirable gap risk. That is why here, positions that are exercisable
        /// in excess are exercised during market hours, ie, when this function is scheduled to run.
        /// </summary>
        public void ExerciseOnExpiryDate()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            List<Position> shortPositions = Positions.Values.Where(p => p.Quantity < 0 && p.SecurityType == SecurityType.Option && p.Expiry == Time.Date).ToList();
            List<Position> longPositions = Positions.Values.Where(p => p.Quantity > 0 && p.SecurityType == SecurityType.Option && p.Expiry == Time.Date).ToList();

            decimal cumDeltaAssignable = shortPositions.Sum(p => p.DeltaTotal());

            List<Position> posToExercise = new();
            foreach (Position pos in longPositions.OrderBy(p => p.Delta()).Reverse())
            {
                decimal deltaPos = pos.DeltaTotal() / pos.Quantity;
                decimal ifFilledCumDeltaAssignable = cumDeltaAssignable + deltaPos;
                if (Math.Abs(ifFilledCumDeltaAssignable) < Math.Abs(cumDeltaAssignable) && Math.Sign(ifFilledCumDeltaAssignable) == Math.Sign(cumDeltaAssignable))
                {
                    // Exercising this position would not bring us to zero delta
                    decimal quantity = Math.Min((int)pos.Quantity, Math.Floor(Math.Abs(cumDeltaAssignable / deltaPos)));
                    cumDeltaAssignable += deltaPos * quantity;
                }
                else
                {
                    posToExercise.Add(pos);
                }
            }
            posToExercise.DoForEach(p => ExerciseOption(p.Symbol, (int)p.Quantity));
        }

        public List<Symbol> AddOptionIfScoped(Symbol optionSymbol)
        {
            var contractSymbols = OptionChainProvider.GetOptionContractList(optionSymbol, Time);
            List<Symbol> subscribedSymbols = new();
            foreach (var symbol in contractSymbols)
            {
                if (Securities.ContainsKey(symbol)) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;

                var historyUnderlying = HistoryWrap(symbolUnderlying, Cfg.MinHistoryDaysUnderlyingForScoping, Resolution.Daily).ToList();
                bool optionInSsviParams = OptionInSsviParams(symbol);
                if (historyUnderlying.Any() || optionInSsviParams)
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (optionInSsviParams || ContractScopedForSubscription(symbol, lastClose, Cfg.ScopeContractStrikeOverUnderlyingMargin))
                    {
                        // var item = AddData<VolatilityQuoteBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        // item.IsTradable = false;

                        // This line requests quite a bit of past data. Minute and second resolution for a whole month into past.
                        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false, extendedMarketHours: true);

                        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                        subscribedSymbols.Add(symbol);
                    }
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"No history for {symbolUnderlying}. Not subscribing to its options." } });
                }
            }
            return subscribedSymbols;
        }

        public bool OptionInSsviParams(Symbol option)
        {
            Equity equity = (Equity)Securities[option.ID.Underlying.Symbol];
            return KalmanFiltersSSVI.ContainsKey(equity) && KalmanFiltersSSVI[equity].GetSSVIParams().ContainsKey(option.ID.Date);
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            var universeWatch = Stopwatch.StartNew();
            var securitiesBefore = Securities.Count;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                RemoveUniverseSecurity(sec);
            });

            // Add options that have moved into scope — one chain at a time. This is the phase that
            // runs at the day boundary and re-subscribes every chain: it can take minutes, and the
            // overnight gap used to be silent while it ran, so each chain reports progress with a
            // percentage and an ETA.
            var chains = options.ToList();
            var total = chains.Count;
            var lastReport = TimeSpan.Zero;
            for (var i = 0; i < total; i++)
            {
                AddOptionIfScoped(chains[i]);

                var done = i + 1;
                if (done == 1 || done == total || done % Math.Max(1, total / 20) == 0 ||
                    universeWatch.Elapsed - lastReport >= TimeSpan.FromSeconds(10))
                {
                    var frac = (double)done / total;
                    var eta = frac > 0 ? universeWatch.Elapsed.TotalSeconds * (1 - frac) / frac : 0;
                    Log($"Universe update: {done}/{total} chains ({frac:P1}), " +
                        $"securities {securitiesBefore} -> {Securities.Count}, " +
                        $"elapsed {universeWatch.Elapsed.TotalSeconds:0.0}s, eta {eta:0}s");
                    lastReport = universeWatch.Elapsed;
                }
            }

            Log($"Universe update: done in {universeWatch.Elapsed.TotalSeconds:0.0}s; " +
                $"securities {securitiesBefore} -> {Securities.Count}.");
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay) { return; }
            SnapPositions();
            LogPortfolioHighLevel();
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            StopIvSolver();
            OnEndOfDay();
            PositionWriters.Keys.DoForEach(RiskRecorder.Record);
            RiskRecorder.Dispose();
            IvSurfaceSsviMid.Values.DoForEach(s => s.Dispose());
            PositionWriters.Values.DoForEach(w => w.Dispose());
            KalmanFilterSSVIWriters.Values.DoForEach(w => w.Dispose());
            RiskProfiles.Values.DoForEach(s => s.Dispose());
            OrderEventWriters.Values.DoForEach(s => s.Dispose());
            TradeWriter.Dispose();
        }

        public void OnMarketOpen()
        {
            if (IsWarmingUp) { return; }

            // New day => Securities may have fallen into scope for trading embargo.
            // embargoedSymbols = Securities.Keys.Where(s => EarningsAnnouncements.Where(ea => ea.Symbol == s.Underlying && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()).ToHashSet();

            // Trigger events
            Securities.Values.Where(s => s.Type == SecurityType.Equity).DoForEach(s => Publish(new NewBidAskEventArgs(s.Symbol)));

            LogRisk();
            LogPnL();
            LogPositions();
        }

        public override void OnWarmupFinished()
        {
            TimeWarmupFinished = Time;
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

            // The repeating schedules do nothing while warming up (the clock sweeps the span and would fire
            // them hundreds of times for nothing), so run them once here: before trading, right after warm-up.
            UpdateUniverseSubscriptions();
            SetActiveMarketRegimes();
            LogRiskSchedule();

            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(IVSurfaceRelativeStrike.Status.Smoothings)));
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(IVSurfaceRelativeStrike.Status.Smoothings)));

            OnWarmupFinishedCalled = true;  // bad. remove flag setting design
        }

        public void SnapPositions()
        {
            if (IsWarmingUp ||
                Time.TimeOfDay <= mmWindow.Start ||
                Time.TimeOfDay >= mmWindow.End
               )
            {
                return;
            }
            
            Positions.Values.Where(p => p.Quantity != 0 && Cfg.Ticker.Contains(Underlying(p.Symbol).Value)).DoForEach(p => Snap(p.Symbol));
            foreach (PositionWriter writer in PositionWriters.Values)
            {
                writer.WriteCsvRows(Positions.Values.Where(p => p.Quantity != 0 && Cfg.Ticker.Contains(Underlying(p.Symbol).Value)));
            }
        }

        public void ExportIVSurface()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            IvSurfaceSsviMid.Values.DoForEach(s => s.WriteCsvRows());
            //IVSurfaceSSVIBid.Values.Union(IVSurfaceSSVIAsk.Values).DoForEach(s => s.WriteCsvRows());
        }

        public void HedgeDeltaFlat()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            foreach (Symbol symbol in ticker.Select(tickerStr => Securities[tickerStr].Symbol))
            {
                Log($"{Time} HedgeDeltaFlat: {symbol}");
                HedgeOptionWithUnderlying(symbol);
            }
        }

        public void OnMarketClose()
        {
            //optionTicker.DoForEach(ticker => IVSurfaceSSVIBid[ticker].OnEODATM());
            //optionTicker.DoForEach(ticker => IVSurfaceSSVIAsk[ticker].OnEODATM());

            Log($"{Time} OptionContractWrap.ClearCache: Removed {OptionContractWrap.ClearCache(Time - TimeSpan.FromDays(3))} instances."); ;
        }
        /// <summary>
        /// Set Holdings in Backtesting to compare a live trading day with a backtesting day
        /// Read Live Holdings from file or pass in arguments.
        /// For best comparison with IB, use market midnight closing prices. Best approximation: T-1 closing prices.
        /// </summary>
        public void SetBacktestingHoldings()
        {
            if (LiveMode || !Cfg.BacktestingHoldings.Any()) return;

            decimal averagePrice = 0m;
            foreach ((string ticker, decimal quantity) in Cfg.BacktestingHoldings.Select(h => (h.Key, h.Value)))
            {
                try
                {
                    if (!Securities.Keys.Select(s => s.Value).Contains(ticker))
                    {
                        string underlyingTicker = ticker.Split(' ')[0];
                        if (ticker.Split(' ').Length > 1)
                        {
                            var optionSymbol = QuantConnect.Symbol.CreateCanonicalOption(underlyingTicker, Market.USA, $"?{underlyingTicker}");
                            var contractSymbols = OptionChainProvider.GetOptionContractList(optionSymbol, Time);
                            contractSymbols.Where(s => s.Value == ticker).DoForEach(contractSymbol => AddOptionContract(contractSymbol, Resolution.Second, fillForward: false, extendedMarketHours: true));
                        }
                        else if (!Securities.Keys.Contains(underlyingTicker))
                        {
                            AddEquity(underlyingTicker, resolution, Market.USA, fillForward: false, extendedMarketHours: true);
                        }
                    }

                    Log($"{Time} SetBacktestingHoldings: Symbol={ticker}, Quantity={quantity}, AvgPrice={averagePrice}");
                    Securities[ticker].Holdings.SetHoldings(averagePrice == 0 ? Securities[ticker].Price : averagePrice, quantity);
                    TotalPortfolioValueSinceStart += Securities[ticker].Holdings.HoldingsValue;
                }
                catch (Exception e)
                {
                    Log($"{Time} SetBacktestingHoldings: {ticker} {e.Message}");
                    throw e;
                }
            }
        }
        public void AddSignals(IEnumerable<Signal> signals)
        {
            lock (_signalQueue)
            {
                // Append, do not Clear: ConsumeSignal can bail early while submissions are still
                // unprocessed (`:1027`), and a later batch wiping the queue would silently drop
                // signals. Per-underlying async batches make that reachable (B applies while A's
                // signals are still queued).
                signals.DoForEach(s => _signalQueue.Enqueue(s));
                ConsumeSignal();
            }
        }

        /// <summary>
        /// Feed-first IV lookup: the R2 iceberg feed publishes pre-computed bid/ask IVs — with
        /// iv_mid riding along on the same timestamp (see <see cref="TryGetFeedIVMid"/>) — covering
        /// nearly every second of the trading day, so consumers read them instead of round-tripping
        /// the pricer. Returns false when no current feed IV exists; the caller then falls back to
        /// the pricer solver.
        /// </summary>
        public bool TryGetFeedIV(Symbol optionSymbol, QuoteSide side, out double iv, double maxAgeSeconds = 5)
        {
            iv = 0;
            var indicators = side == QuoteSide.Bid ? IvBids : IvAsks;
            if (!indicators.TryGetValue(optionSymbol, out IVQuoteIndicator indicator) ||
                indicator.IVBidAsk.IV <= 0 ||
                !double.IsFinite(indicator.IVBidAsk.IV))
            {
                return false;
            }
            if (Time - indicator.IVBidAsk.Time > TimeSpan.FromSeconds(maxAgeSeconds))
            {
                return false;
            }
            iv = indicator.IVBidAsk.IV;
            return true;
        }

        /// <summary>
        /// Feed-first mid IV lookup: the R2 iceberg feed publishes iv_mid — the IV of the option's
        /// mid price, not half of the bid/ask IVs, therefore the more stable estimate — alongside
        /// iv_bid/iv_ask on the same timestamp, and both side indicators carry it in their IVQuote.
        /// Returns false when no current feed mid IV exists; the caller then falls back to averaging
        /// the sides or the pricer solver.
        /// </summary>
        public bool TryGetFeedIVMid(Symbol optionSymbol, out double ivMid, double maxAgeSeconds = 5)
        {
            if ((IvBids.TryGetValue(optionSymbol, out IVQuoteIndicator indicator) && TryGetFeedIVMid(indicator, out ivMid, maxAgeSeconds)) ||
                (IvAsks.TryGetValue(optionSymbol, out indicator) && TryGetFeedIVMid(indicator, out ivMid, maxAgeSeconds)))
            {
                return true;
            }
            ivMid = 0;
            return false;
        }

        private bool TryGetFeedIVMid(IVQuoteIndicator indicator, out double ivMid, double maxAgeSeconds)
        {
            IVQuote quote = indicator.IVBidAsk;
            if (quote.IVMid <= 0 || !double.IsFinite(quote.IVMid) ||
                Time - quote.Time > TimeSpan.FromSeconds(maxAgeSeconds))
            {
                ivMid = 0;
                return false;
            }
            ivMid = quote.IVMid;
            return true;
        }

        private DateTime _midIvSolverFallbackLog = DateTime.MinValue;

        /// <summary>
        /// Mid IV is bad - Always convert from MidPrice
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="defaultSpread"></param>
        /// <returns></returns>
        public double MidIV(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option) return double.NaN;
            // iv_mid is the IV of the mid price, not the average of the bid/ask IVs, and therefore
            // the more stable feed estimate: prefer it before averaging the sides.
            if (TryGetFeedIVMid(symbol, out double midIv))
            {
                return midIv;
            }
            // Days written before the warehouse carried iv_mid (the column reads NULL there): the
            // sides' average is the best feed estimate available and beats a pricer round trip.
            if (TryGetFeedIV(symbol, QuoteSide.Bid, out double bidIv) && TryGetFeedIV(symbol, QuoteSide.Ask, out double askIv))
            {
                return (bidIv + askIv) / 2;
            }
            if (Time - _midIvSolverFallbackLog > TimeSpan.FromMinutes(1))
            {
                _midIvSolverFallbackLog = Time;
                Log($"{Time} MidIV: No current feed IV for {symbol}; falling back to pricer solver.");
            }
            decimal mid = MidPrice(symbol);
            return OptionContractWrap.E(this, (Option)Securities[symbol], Time.Date).IV(mid, MidPrice(Underlying(symbol)), 0.0001);
        }

        public double MidIVSsvi(Symbol symbol, double defaultSpread = 0.005)
        {
            if (symbol.SecurityType != SecurityType.Option) return 0;

            Option option = (Option)Securities[symbol];
            Equity equity = (Equity)option.Underlying;
            if (!IvSurfaceSsviMid.TryGetValue(equity, out IIVSurface ivSurface) || ivSurface is not IVSurfaceSSVI ssviSurface || !ssviSurface.IsCalibrated || !ssviSurface.HasParams(option))
            {
                return MidIV(symbol);
            }

            return ssviSurface.IV(option);
        }

        public Equity ToEquity(Symbol underlying) => (Equity)Securities[underlying];
        public Equity ToEquity(Option o) => (Equity)o.Underlying;
        
        public void AlertLateOrderRequests()
        {
            var lateCancelRequests = Transactions.CancelRequestsUnprocessed.Where(r => Time - r.Time > TimeSpan.FromSeconds(15));
            var lateSubmitRequests = Transactions.SubmitRequestsUnprocessed.Where(r => Time - r.Time > TimeSpan.FromMinutes(5));

            if (lateCancelRequests.Any())
            {
                Log($"{Time} AlertLateCancelRequests. Late CancelRequests: {string.Join(", ", lateCancelRequests.Select(r => r.OrderId))}");
                DiscordClient.Send($"AlertLateCancelRequests. Late CancelRequests: {string.Join(", ", lateCancelRequests.Select(r => r.OrderId))}", DiscordChannel.Emergencies, LiveMode);
            }
            if (lateSubmitRequests.Any())
            {
                Log($"{Time} AlertLateSubmitRequests. Late SubmitRequests: {string.Join(", ", lateSubmitRequests.Select(r => r.OrderId))}");
                DiscordClient.Send($"AlertLateSubmitRequests. Late SubmitRequests: {string.Join(", ", lateSubmitRequests.Select(r => r.OrderId))}", DiscordChannel.Emergencies, LiveMode);
            }
        }
        public decimal DiscountedValue(decimal cashFlow, Option option, decimal? discountRate = null)
        {
            return DiscountedValue(cashFlow, Time.Date, option.Expiry.Date, discountRate);
        }
        public decimal DiscountedValue(decimal cashFlow, DateTime presentDate, DateTime futureDate, decimal? discountRate = null)
        {
            return DiscountedValue(cashFlow, (futureDate - presentDate).TotalDays / 365, discountRate);
        }
        public decimal DiscountedValue(decimal cashFlow, double years, decimal? discountRate = null)
        {
            decimal _discountRate = discountRate ?? Cfg.DiscountRatePortfolioCAGR;
            // Calculate the discount factor for the entire period (discrete compounding)
            // decimal discountFactor = (decimal)Math.Pow(1 + (double)discountRate / 365, days);

            // Calculate the discount factor using continuous compounding
            decimal discountFactor = (decimal)Math.Exp(-(double)_discountRate * years);

            // Calculate the present value of the cash flow
            return cashFlow * discountFactor;
        }
        protected void ConsumeSignal()
        {
            lock (_signalQueue)
            {
                if (_signalQueue.IsEmpty) return;
                if (Transactions.CancelRequestsUnprocessed.Any() || Transactions.SubmitRequestsUnprocessed.Count() >= Cfg.MinSubmitRequestsUnprocessedBlockingSubmit)
                {
                    if (LiveMode)
                    {
                        Log($"{Time} ConsumeSignal. WAITING with signal submission: Queue Length: {_signalQueue.Count()}, " +
                        $"CancelRequestsUnprocessed: Count={Transactions.CancelRequestsUnprocessed.Count()}, LeanID={string.Join(", ", Transactions.CancelRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"SubmitRequestsUnprocessed: Count={Transactions.SubmitRequestsUnprocessed.Count()}, LeanID={string.Join(", ", Transactions.SubmitRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"UpdateRequestsUnprocessed: Count={Transactions.UpdateRequestsUnprocessed.Count()}");
                    }
                    AlertLateOrderRequests();
                    return;
                }

                if (_signalQueue.TryDequeue(out Signal signal))
                {
                    var sw = Stopwatch.StartNew();
                    SubmitSignal(signal);
                    Log($"{Time} ConsumeSignal. SubmitSignal {signal?.Symbol} took {sw.ElapsedMilliseconds}ms.");
                    ConsumeSignal();
                }
            }
        }
        public PositionSnap Snap(Symbol symbol)
        {
            PositionSnap lastSnap = new(this, symbol);
            if (!PositionSnaps.ContainsKey(symbol))
            {
                PositionSnaps[symbol] = new();
            }
            PositionSnaps[symbol].Add(lastSnap);
            return lastSnap;
        }
        public PositionSnap LastSnap(Symbol symbol)
        {
            return PositionSnaps.TryGetValue(symbol, out List<PositionSnap> snaps) ? snaps.Last() : Snap(symbol);
        }
        public OrderStatus OcaGroupStatus(string ocaGroup)
        {
            if (string.IsNullOrEmpty(ocaGroup))
            {
                return OrderStatus.None;
            }
            else
            {
                return Transactions.OcaGroupStatus.TryGetValue(ocaGroup, out OrderStatus status) ? status : OrderStatus.None;
            }
        }
        /// <summary>
        /// A ticket is live only if its own status is open AND its OCA group is still open.
        ///
        /// Lean learns ticket status only from the broker's order events, and IB cancels OCA
        /// siblings server-side when one leg fills — those events can be missed for a given order,
        /// leaving its ticket "Submitted" forever. The group status is updated by whichever event
        /// does arrive, so it is the reliable half: once a group is Filled/Canceled, every ticket
        /// still carrying it is dead, whatever its own status says.
        /// </summary>
        private protected bool IsLiveTicket(OrderTicket ticket) =>
            !orderFilledCanceledCancelPendingInvalid.Contains(ticket.Status)
            && !orderFilledCanceledCancelPendingInvalid.Contains(OcaGroupStatus(ticket.OcaGroup));

        public void SubmitSignal(Signal signal)
        {
            if (signal == null)
            {
                Log($"{Time} SubmitSignal: signal is null");
                return;
            }
            // Order desired tickets
            bool anyTickets = orderTickets.TryGetValue(signal?.Symbol, out List<OrderTicket> tickets) && tickets.Any(IsLiveTicket);
            if (anyTickets)
            {
                Log($"{Time} SubmitSignal: Not submitting signal={signal}, symbol={signal.Symbol} because ticketsCnt={tickets.Count}, tickets={string.Join(",", tickets.Where(IsLiveTicket))}, OcaGroupStatus={OcaGroupStatus(signal.OcaGroup)}");
                // Already have a ticket. No problem / ok.
                return;
            }

            // A closed OCA group is not a reason to drop the signal: the id is dead for the run, so
            // the signal inherits a veto that never lifts. Give it a fresh group and place the order.
            // Only a genuinely closed group is reassigned — a partially filled group is alive and
            // keeps its id, so new orders still join it.
            var inheritedStatus = OcaGroupStatus(signal.OcaGroup);
            if (orderFilledCanceledCancelPendingInvalid.Contains(inheritedStatus))
            {
                var staleGroup = signal.OcaGroup;
                signal.AssignOcaGroup(NewOcaGroupId());
                Log($"{Time} SubmitSignal: signal={signal}, symbol={signal.Symbol} inherited closed " +
                    $"OcaGroup={staleGroup} (status={inheritedStatus}); reassigned {signal.OcaGroup}.");
            }

            OrderOptionContract(signal, OrderType.Limit);
        }

        public int Periods(Resolution? thisResolution = null, int days = 5)
        {
            return (thisResolution ?? resolution) switch
            {
                Resolution.Daily => days,
                Resolution.Hour => (days * 24),
                Resolution.Minute => (days * 24 * 60),
                Resolution.Second => (days * 24 * 60 * 60),
                _ => 1,
            };
        }

        public decimal Spread(Symbol symbol) => Spread(Securities[symbol]);

        public decimal Spread(Security security)
        {
            return security.AskPrice - security.BidPrice;
        }

        public decimal MidPrice(Symbol symbol)
        {
            var security = Securities[symbol];
            return (security.AskPrice + security.BidPrice) / 2;
        }

        public decimal KeepSreadPrice(Symbol symbol, OrderDirection direction)
        {
            var security = Securities[symbol];
            return direction == OrderDirection.Buy ? security.BidPrice : security.AskPrice;
        }
        public decimal CrossSreadPrice(Symbol symbol, OrderDirection direction)
        {
            var security = Securities[symbol];
            return direction == OrderDirection.Buy ? security.AskPrice : security.BidPrice;
        }

        static decimal Strike(Order o) => o.Symbol.ID.StrikePrice;

        Order NewEquityExerciseOrder(OptionExerciseOrder o)
        {
            // Get last trade for this symbol. Hacky. To avoid getting a PnL from this, but rather just modifying the quantity of an existing equity position, setting all prices to trade0.Mid0Underlying => PnLDelta 0.
            Positions.TryGetValue(Underlying(o.Symbol), out Position currentPosition);

            decimal fillPrice = currentPosition == null ? MidPrice(o.Symbol.Underlying) : currentPosition.Trade0.Mid0Underlying;
            var localTime = o.Time.ConvertFromUtc(Securities[o.Symbol].Exchange.TimeZone);
            var order = new EquityExerciseOrder(o, new OrderFillData(localTime, fillPrice, fillPrice, fillPrice, fillPrice, fillPrice, fillPrice)) // using Time instead of o.Time avoiding UTC conversion.
            {
                Status = OrderStatus.Filled,
            };
            return order;
        }
        public List<Trade> WrapToTrade(OrderEvent orderEvent)
        {
            // Apply to internal Positions and add a simulated trade setting the position quantity to zero, snapping data.

            List<Trade> newTrades = new();
            Symbol symbol = orderEvent.Symbol;

            //if (orderEvent.IsAssignment)
            //{
            //    Log($"WrapToTrade. IsAssignment: {symbol}. {orderEvent.OrderId}.");
            //    OptionExerciseOrder optionExerciseOrder = (OptionExerciseOrder)Transactions.GetOrderById(orderEvent.OrderId);
            //    Trade tradeOptionExercise = new(this, optionExerciseOrder, orderEvent);
            //    var equityExerciseOrder = NewEquityExerciseOrder(optionExerciseOrder);
            //    Trade equityExerciseTrade = new(this, equityExerciseOrder, orderEvent);
            //    newTrades.Add(tradeOptionExercise);
            //    newTrades.Add(equityExerciseTrade);
            //}
            if (symbol.SecurityType == SecurityType.Option && symbol.ID.Date <= Time.Date)  // Assignment or Exercise Option Leg or OTM Expiry
            {
                Log($"WrapToTrade. Option OptionExersiseOrder - Option Leg - IsInTheMoney={orderEvent.IsInTheMoney}: {symbol}. {orderEvent.OrderId}.");
                //OptionExerciseOrder optionExerciseOrder = (OptionExerciseOrder)Transactions.GetOrderById(orderEvent.OrderId);
                Trade tradeOptionExercise = new(this, orderEvent, orderEvent.IsInTheMoney ? orderEvent.FillQuantity : -Positions[symbol].Quantity, orderEvent.IsInTheMoney);
                newTrades.Add(tradeOptionExercise);
            }
            //else if (symbol.SecurityType == SecurityType.Option && symbol.ID.Date <= Time.Date)  // Expired OTM
            //{
            //    Log($"WrapToTrade. Option Expired OTM: {symbol}. {Portfolio[symbol].Quantity}");
            //    newTrades.Add(new(this, orderEvent, -Positions[symbol].Quantity));
            //}
            else if (symbol.SecurityType == SecurityType.Equity && Transactions.GetOrderById(orderEvent.OrderId).SecurityType == SecurityType.Option)  // Assignment Or Exercise Equity Leg
            {
                Log($"WrapToTrade. Equity OptionExersiseOrder - Equity Leg.");
                OptionExerciseOrder optionExerciseOrder = (OptionExerciseOrder)Transactions.GetOrderById(orderEvent.OrderId);
                var equityExerciseOrder = NewEquityExerciseOrder(optionExerciseOrder);
                Trade equityExerciseTrade = new(this, orderEvent, equityExerciseOrder);
                newTrades.Add(equityExerciseTrade);
            }
            else
            {
                //Log($"WrapToTrade. Neither expired not Assigned.");
                newTrades.Add(new(this, orderEvent, Transactions.GetOrderById(orderEvent.OrderId)));
            }

            foreach (Trade trade in newTrades)
            {
                if (!Trades.ContainsKey(trade.Symbol))
                {
                    Trades[trade.Symbol] = new();
                }
                Log($"Adding OrderEvent: {orderEvent.OrderId} -> Trade");
                Trades[orderEvent.Symbol].Add(trade);
            }
            return newTrades;
        }
        /// <summary>
        /// Refactor into position. This here has the risk of double-counting trades. Need to not apply when order id equal to trade0.ID.
        /// </summary>
        /// <param name="trade"></param>
        public void ApplyToPosition(List<Trade> trades)
        {
            foreach (var trade in trades)
            {
                if (trade.SecurityType == SecurityType.Option && trade.Expiry <= Time.Date)
                {
                    // Positions.Remove(trade.Symbol);
                    RemoveSecurity(trade.Symbol);
                    return;
                }

                if (!Positions.ContainsKey(trade.Symbol))
                {
                    // Brand new position                    
                    Positions[trade.Symbol] = new(null, trade, this, null, Securities[trade.Symbol].Holdings.Quantity);
                }
                else
                {
                    // Trade modifies / operates on existing position.
                    Positions[trade.Symbol] = new(Positions[trade.Symbol], trade, this, null, Securities[trade.Symbol].Holdings.Quantity);
                }
            }
        }
        public void UpdateOrderFillData(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                var symbol = orderEvent.Symbol;
                var security = Securities[symbol];
                var order = Transactions.GetOrderById(orderEvent.OrderId);
                if (!PriceCache.ContainsKey(symbol))
                {
                    PriceCache[symbol] = Securities[symbol].Cache.Clone();
                }
                //orderEvent.OrderFee
                OrderFillDataTN1[order.Id] = symbol.SecurityType switch
                {
                    SecurityType.Option => new OrderFillData(
                        orderEvent.UtcTime, PriceCache[symbol].BidPrice, PriceCache[symbol].AskPrice, PriceCache[symbol].Price,
                        ((Option)security).Underlying.Cache.BidPrice,
                        ((Option)security).Underlying.Cache.AskPrice,
                        ((Option)security).Underlying.Cache.Price,
                        orderEvent.OrderFee
                        ),
                    _ => new OrderFillData(Time, PriceCache[symbol].BidPrice, PriceCache[symbol].AskPrice, PriceCache[symbol].Price, fee: orderEvent.OrderFee) // Time is off.
                };
            }
        }

        public bool IsEventNewQuote(Symbol symbol)
        {
            // called in Consolidator AND OnData. Should cache result at timestamp, update PriceCache and read here from cache.
            if (!PriceCache.TryGetValue(symbol, out SecurityCache cache))
            {
                return false;
            }
            var security = Securities[symbol];
            return cache.BidPrice != security.BidPrice ||
                cache.AskPrice != security.AskPrice;
        }

        public bool OptionIsTradeable(Option o)
        {
            return o.IsTradable && (
                    // to be review with Gamma hedging. Selling option at ultra-high, near-expiry IVs with great gamma hedge could be extra profitable.
                    (o.Symbol.ID.Date - Time.Date).Days > 1  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                    && !o.Symbol.IsCanonical()
                    && o.BidPrice != 0
                    && o.AskPrice != 0

                    // price is not stale. Bit inefficient here. May rather have an indicator somewhere.
                    //&& PriceCache.ContainsKey(o.Symbol)
                    //&& PriceCache[o.Symbol].GetData().EndTime > Time - TimeSpan.FromMinutes(5)

                    //&& IsLiquid(o.Symbol, 5, Resolution.Daily)
                    //&& o.Symbol.ID.StrikePrice >= MidPrice(o.Symbol.Underlying) * (Cfg.ScopeContractStrikeOverUnderlyingMinSignal)
                    //&& o.Symbol.ID.StrikePrice <= MidPrice(o.Symbol.Underlying) * (Cfg.ScopeContractStrikeOverUnderlyingMaxSignal)
                    //&& (
                    //    ((Option)o).GetPayOff(MidPrice(o.Symbol.Underlying)) < Cfg.ScopeContractMoneynessITM * MidPrice(o.Symbol.Underlying) || (
                    //        orderTickets.ContainsKey(o.Symbol) &&
                    //        orderTickets[o.Symbol].Count > 0 &&
                    //        ((Option)o).GetPayOff(MidPrice(o.Symbol.Underlying)) < (Cfg.ScopeContractMoneynessITM + 0.05m) * MidPrice(o.Symbol.Underlying)
                    //    )
                    //)
                    && !liquidateTicker.Contains(o.Symbol.Underlying.Value)  // No new orders, Function oppositeOrder & hedger handle slow liquidation at decent prices.
                                                                             //&& IVSurfaceRelativeStrikeBid[Underlying(o.Symbol)].IsReady(o.Symbol)
                                                                             //&& IVSurfaceRelativeStrikeAsk[Underlying(o.Symbol)].IsReady(o.Symbol)
                                                                             //&& symbol.ID.StrikePrice > 0.05m != 0m;  // Beware of those 5 Cent options. Illiquid, but decent high-sigma underlying move protection.
                );
        }


        public bool ContractScopedForNewPosition(Option o)
        {
            Equity equity = ToEquity(o);

            HashSet<MarketRegime> regimes = ActiveRegimes[equity];
            if (!regimes.Any()) return false;

            return OptionIsTradeable(o)
                && (  // Exiting after earnings
                    regimes.Contains(MarketRegime.PostEarningsRelease) &&
                    !(o.Symbol.ID.Date <= Time.Date)
                    && Portfolio[o.Symbol].Quantity != 0  // Need to exit eventually
                )
                || ManualOrderInstructionBySymbol.ContainsKey(o.Symbol.Value)  // from config
                || (beforeEarnings.Intersect(regimes).Any() && TargetHoldings.ContainsKey(o.Symbol))
                || (regimes.Contains(MarketRegime.PreEarningsReleaseBeforeMarketClose) && RiskScenarioHandler.TradableOptions.ContainsKey(equity) && RiskScenarioHandler.TradableOptions[equity].Contains(o));
        }

        public void SetActiveMarketRegimes()
        {
            if (IsWarmingUp) return;

            foreach (var underlying in equities)
            {
                if (IsMyMarketOpen(underlying))
                {
                    var regimes = GetMarketRegimes(underlying);
                    Equity equity = ToEquity(underlying);
                    if ((ActiveRegimes.TryGetValue(equity, out HashSet<MarketRegime> activeRegimes) && !activeRegimes.SetEquals(regimes)) || activeRegimes == null)
                    {
                        ActiveRegimes[equity] = regimes;
                        MarketRegimesChangeEventHandler?.Invoke(this, new MarketRegimesChangeEventArgs(underlying, regimes));
                    }
                    ActiveRegimes[equity] = regimes;
                }
            }
        }

        public HashSet<MarketRegime> GetMarketRegimes(Symbol underlying)
        {
            // Towards end of day, go delta neutral wit options only. Reduce abs. equity position. A regime, not just exterior but still..
            // bool isReducingAbsEquityPosition = IsReducingAbsEquityPosition(qr.Underlying);
            HashSet<MarketRegime> regimes = new();

            if (IsPreparingEarningsRelease(underlying))
            {
                if (TimeToMarketClose(underlying).TotalMinutes < Cfg.MinutesBeforeCloseIsPreEarningsReleaseEOD)
                {
                    regimes.Add(MarketRegime.PreEarningsReleaseBeforeMarketClose);
                }
                else if (Cfg.UseKalmanFilterBeforeEarningsRelease && Time.TimeOfDay >= TimeStartKfBeforeRelease(underlying.Value))
                {
                    regimes.Add(MarketRegime.PreEarningsRelease);
                }
                else
                {
                    regimes.Add(MarketRegime.PreEarningsRelease);
                }
            }
            else if (IsAfterEarningsRelease(underlying))
            {
                regimes.Add(MarketRegime.PostEarningsRelease);
            }

            bool upcomingEventLongIV = Cfg.UpcomingEventLongIV.TryGetValue(underlying, out upcomingEventLongIV) ? upcomingEventLongIV : Cfg.UpcomingEventLongIV[CfgDefault];
            int upcomingEventCalendarSpreadStartDaysPrior = Cfg.UpcomingEventCalendarSpreadStartDaysPrior.TryGetValue(underlying, out upcomingEventCalendarSpreadStartDaysPrior) ? upcomingEventCalendarSpreadStartDaysPrior : Cfg.UpcomingEventCalendarSpreadStartDaysPrior[CfgDefault];
            foreach (var announcement in EarningsBySymbol[underlying].OrderBy(a => a.Date))
            {
                if (Time.Date > announcement.Date) continue;
                if (upcomingEventLongIV && Time.Date >= announcement.Date - TimeSpan.FromDays(20) && Time.Date < announcement.Date - TimeSpan.FromDays(3))
                {
                    //Log($"{Time} SetTradingRegime {underlying}: {Regime.BuyEvent}. announcement.Date: {announcement.Date}");
                    regimes.Add(MarketRegime.BuyEvent);
                }
                if (Time.Date >= announcement.Date - TimeSpan.FromDays(upcomingEventCalendarSpreadStartDaysPrior) && Time.Date <= announcement.Date)
                {
                    //Log($"{Time} SetTradingRegime {underlying}: {Regime.SellEventCalendarHedge}. announcement.Date: {announcement.Date}");
                    regimes.Add(MarketRegime.SellEventCalendarHedge);
                }
                break;
            }

            return regimes;
        }

        /// <summary>
        /// Signals. Securities where we assume risk. Not necessarily same as positions or subscriptions.
        ///
        /// Synchronous path: capture the scope on THIS thread, compute inline, return the signals.
        /// <see cref="RunSignals"/> uses the same three steps so the compute step can move off-thread.
        /// </summary>
        public List<Signal> GetDesiredOrders(Symbol underlying)
        {
            var scope = CaptureScope(underlying);
            if (scope == null) return new();

            var batch = ComputeSignals(underlying, scope, MinUtility(underlying), Time);
            LogSignalBatch(batch);
            return batch.Signals;
        }

        private double MinUtility(Symbol underlying) =>
            Cfg.MinUtility.TryGetValue(underlying.Value, out double configured) ? configured : Cfg.MinUtility[CfgDefault];

        /// <summary>
        /// Snapshot of the option scope, taken ON THE ALGORITHM THREAD. Everything the utility loop
        /// needs from live state is resolved here — the scoped set, the side gates, the quantities
        /// and the prices — so the worker never enumerates Securities, evaluates a regime gate or
        /// scans order tickets while OnData may be adding contracts.
        /// </summary>
        private List<ScopedOption> CaptureScope(Symbol underlying)
        {
            Equity equity = ToEquity(underlying);
            if (!IvSurfaceSsviMid.ContainsKey(equity))
            {
                Log($"{Time} GetDesiredOrders: {underlying} IVSurfaceSSVIMid not ready.");
                return null;
            }

            var scope = new List<ScopedOption>();
            foreach (Security security in Securities.Values)
            {
                if (security.Type != SecurityType.Option) continue;
                var option = (Option)security;
                if (option.Underlying.Symbol != underlying) continue;
                // && IvSurfaceSsviMid[equity].HasParams(option)
                if (!ContractScopedForNewPosition(option)) continue;

                // Pick the side BEFORE building the utilities. Constructing one costs its whole
                // greek vector; the delta-hedge gate already knows which direction reduces risk (a
                // sign test on delta against the portfolio's), so when a hedge is in progress only
                // one side is built instead of both — halving the loop's pricer work.
                bool sellAllowed = IfFilledDecreasesEquityHedgeOrderSize(option.Symbol, OrderDirection.Sell);
                bool buyAllowed = IfFilledDecreasesEquityHedgeOrderSize(option.Symbol, OrderDirection.Buy);
                if (!sellAllowed && !buyAllowed) continue;

                scope.Add(new ScopedOption(option, sellAllowed, buyAllowed,
                    sellAllowed ? SignalQuantity(option.Symbol, OrderDirection.Sell) : 0m,
                    buyAllowed ? SignalQuantity(option.Symbol, OrderDirection.Buy) : 0m));
            }
            return scope;
        }

        /// <summary>
        /// The utility loop. Safe off the algorithm thread: it reads the captured scope plus numeric
        /// market data, and fans out per contract — each option's IV→greeks chain is serial, but
        /// contracts are independent, so K workers keep K requests in flight against the pricer.
        /// </summary>
        private SignalBatch ComputeSignals(Symbol underlying, List<ScopedOption> scope, double minUtility,
            DateTime capturedAt)
        {
            var batch = new SignalBatch
            {
                Underlying = underlying,
                CapturedTicks = Stopwatch.GetTimestamp(),
                CapturedUtc = DateTime.UtcNow,
                CapturedAt = capturedAt,
                OptionCount = scope.Count,
            };

            // Bounded by the admission budget — offering more than the pricer's worker count only
            // deepens its queue. Two pool threads are left free because each worker blocks on its
            // reply (sync GetResult) and the TCS continuations need a thread to complete on.
            int parallelism = Math.Max(1, Math.Min(
                Math.Min(scope.Count, julia?.Budget.Capacity ?? Environment.ProcessorCount),
                Math.Max(2, Environment.ProcessorCount - 2)));
            batch.Parallelism = parallelism;

            var outcome = new Signal[scope.Count];
            long soloTicks = 0;
            int optionErrors = 0;
            var errorSamples = new System.Collections.Concurrent.ConcurrentQueue<string>();

            var sw = Stopwatch.StartNew();
            System.Threading.Tasks.Parallel.For(0, scope.Count,
                new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = parallelism },
                i =>
                {
                    // The caller tag is thread-static, so it must be set ON the worker thread;
                    // a Scope around the whole loop would tag only the calling thread.
                    using (PricingCallSite.Scope("UtilityLoop"))
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        try
                        {
                            outcome[i] = Evaluate(scope[i], minUtility);
                        }
                        catch (Exception ex)
                        {
                            // ONE contract's pricing failure must not abort the pass, and must
                            // never reach the caller: Parallel.For aggregates and rethrows, which
                            // killed a backtest on 2026-10-30 when a socket send failed.
                            System.Threading.Interlocked.Increment(ref optionErrors);
                            if (errorSamples.Count < 3) errorSamples.Enqueue($"{scope[i].Symbol}: {ex.Message}");
                        }
                        System.Threading.Interlocked.Add(ref soloTicks, Stopwatch.GetTimestamp() - t0);
                    }
                });
            sw.Stop();

            batch.ComputeMs = sw.Elapsed.TotalMilliseconds;
            batch.SerialMs = soloTicks * 1000.0 / Stopwatch.Frequency;
            batch.Errors = optionErrors;
            batch.ErrorSample = errorSamples.Count == 0 ? null : string.Join("; ", errorSamples);
            for (int i = 0; i < outcome.Length; i++)
            {
                if (outcome[i] != null) batch.Signals.Add(outcome[i]);
            }
            return batch;
        }

        /// <summary>One contract's two utilities. Reads the captured scope only — no live gates.</summary>
        private Signal Evaluate(ScopedOption scoped, double minUtility)
        {
            // BuySell Distinction is insufficient. Scenario: We are delta short, gamma long. Would only want to buy/sell options reducing both, unless the utility is calculated better to compare weight 
            // beneficial risk and detrimental risk against each other. That's what the RiskDiscounts are for.
            IUtilityOrder utilSell = scoped.SellAllowed
                ? UtilityOrderFactory.Create(this, scoped.Option, scoped.QuantitySell, scoped.AskPrice)
                : null;
            IUtilityOrder utilBuy = scoped.BuyAllowed
                ? UtilityOrderFactory.Create(this, scoped.Option, scoped.QuantityBuy, scoped.BidPrice)
                : null;

            if (utilSell != null && utilSell.Utility >= minUtility &&
                (utilBuy == null || utilSell.Utility >= utilBuy.Utility))
            {
                return new Signal(scoped.Symbol, OrderDirection.Sell, utilSell);
            }
            if (utilBuy != null && utilBuy.Utility >= minUtility &&
                (utilSell == null || utilBuy.Utility > utilSell.Utility))
            {
                return new Signal(scoped.Symbol, OrderDirection.Buy, utilBuy);
            }
            return null;
        }

        /// <summary>
        /// All per-pass logging, on the algorithm thread. The three StatsSinceLast calls reset their
        /// baselines while formatting, so they must never run on a worker (two passes would split
        /// one interval between them) — that is why this is separate from ComputeSignals.
        /// </summary>
        private void LogSignalBatch(SignalBatch batch)
        {
            Symbol underlying = batch.Underlying;
            double avgOptionMs = batch.OptionCount > 0 ? batch.SerialMs / batch.OptionCount : 0;
            var attributes = new[]
            {
                new KeyValuePair<string, object>("underlying", underlying.Value),
                new KeyValuePair<string, object>("parallelism", (double)batch.Parallelism),
                new KeyValuePair<string, object>("options", (double)batch.OptionCount),
            };
            // Total wall time AND per-option cost: the pair is what shows whether the fan-out
            // actually overlapped work (serial-equivalent >> loop means it did).
            QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.utility.loop_ms", batch.ComputeMs, attributes);
            QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.utility.option_ms", avgOptionMs, attributes);
            QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.utility.loop_serial_ms", batch.SerialMs, attributes);
            Log($"{batch.CapturedAt} GetDesiredOrders: {underlying}. Utility loop over {batch.OptionCount} options took {batch.ComputeMs:0}ms " +
                $"(avg={avgOptionMs:0.0}ms/option, serial-equivalent={batch.SerialMs:0}ms, parallelism={batch.Parallelism}, " +
                $"speedup={batch.Speedup:0.0}x).");

            Log($"{Time} GetDesiredOrders: {underlying}. JuliaPricing since last report (run totals in brackets): {JuliaPricingClient.StatsSinceLast()}");

            // The cache-plane counterpart: per-greek hit/miss of the pricer cache (Iv/Delta/Theta/
            // Vega/Volga), to be read next to the JuliaPricing round-trip stats above — hits here
            // are round trips avoided; misses (incl. sigma mismatches) are what got published to
            // the feeder for warming.
            var pricerCache = (this as Earnings.EarningsAlgorithm)?.PricerCache;
            if (pricerCache != null)
            {
                Log($"{Time} GetDesiredOrders: {underlying}. PricerCache since last report (run totals in brackets): {pricerCache.StatsByKindSinceLast()}");
                Log($"{Time} GetDesiredOrders: {underlying}. Pricing by caller since last report (run totals in brackets): {PricingCallSite.StatsSinceLast()}");
            }

            decimal targetMarginAsFractionOfNLV = Cfg.TargetMarginAsFractionOfNLV.TryGetValue(underlying.Value, out targetMarginAsFractionOfNLV) ? targetMarginAsFractionOfNLV : Cfg.TargetMarginAsFractionOfNLV[CfgDefault];
            decimal marginExcessTarget = Math.Max(0, InitialMargin() - Portfolio.TotalPortfolioValue * targetMarginAsFractionOfNLV);
            if (marginExcessTarget > 0)
            {
                Log($"{Time} GetDesiredOrders: {underlying} initialMargin={InitialMargin()} exceeded by marginExcessTarget={marginExcessTarget}.");
            }

            var filteredSignals = batch.Signals.Where(s => s.Symbol.Underlying == underlying).ToList();
            Log($"{Time}, topic=SIGNALS, " +
                $"#Underlying={underlying}, " +
                $"#Symbols={filteredSignals.Select(s => s.Symbol).Distinct().Count()}, " +
                $"#Signals={filteredSignals.Count}, " +
                $"#BuyCalls={filteredSignals.Where(s => s.OrderDirection == OrderDirection.Buy && s.Symbol.ID.OptionRight == OptionRight.Call).Count()}, " +
                $"#SellCalls={filteredSignals.Where(s => s.OrderDirection == OrderDirection.Sell && s.Symbol.ID.OptionRight == OptionRight.Call).Count()}, " +
                $"#BuyPuts={filteredSignals.Where(s => s.OrderDirection == OrderDirection.Buy && s.Symbol.ID.OptionRight == OptionRight.Put).Count()}, " +
                $"#SellPuts={filteredSignals.Where(s => s.OrderDirection == OrderDirection.Sell && s.Symbol.ID.OptionRight == OptionRight.Put).Count()}");

            Log($"{Time}, topic=# UNPROCESSED, " +
                    $"# SubmitRequests={Transactions.SubmitRequestsUnprocessed.Count()}, " +
                    $"# UpdateRequests={Transactions.UpdateRequestsUnprocessed.Count()}, " +
                    $"# CancelRequests={Transactions.CancelRequestsUnprocessed.Count()}");
        }

        public double IV(Option option, decimal? price = null)
        {
            return (price ?? 0) == 0 ? MidIV(option.Symbol) : OptionContractWrap.E(this, option, Time.Date).IV(price, MidPrice(option.Underlying.Symbol), 0.001);
        }
        public decimal InitialMargin()
        {
            if (LiveMode)
            {
                return Portfolio.MarginMetrics.FullInitMarginReq;
            }
            else
            {
                return Portfolio.TotalMarginUsed / 5;  // IB's Portfolio Margining requires a much lower margin.
            }
        }

        /// <summary>
        /// May rather need to become a manager class.Instead of checking all orderTickets, pick from sets of active OCA groups.
        /// </summary>
        /// <param name="signals"></param>
        /// <param name="ticketsWithOCAGroupsToReuse"></param>
        /// <returns></returns>
        public IEnumerable<Signal> AssignOCAGroups(IEnumerable<Signal> signals, List<OrderTicket> ticketsWithOCAGroupsToReuse)
        {
            if (!signals.Any()) return signals;

            Dictionary<(Symbol, int), string> ocaGroupByUnderlyingDelta = new();

            // Use the remaining tickets as source for OCA groups.                
            foreach (OrderTicket t in ticketsWithOCAGroupsToReuse)
            {
                Symbol ticketUnderlying = Underlying(t.Symbol);

                // Never adopt a ticket whose group is closed, and never adopt a dead ticket: once
                // Lean marks a group Canceled/Filled the id is dead for the rest of the run, so
                // seeding from a ticket that still carries it poisons every later signal — one
                // stuck ticket blocked BA for a whole session with "because OcaGroupStatus=Canceled".
                // A live group (including a partially filled one) still donates its id, which is the
                // whole point of the OCA: new orders on the same underlying and delta sign join it.
                if (!IsLiveTicket(t))
                {
                    continue;
                }

                decimal spot = MidPrice(ticketUnderlying);

                if (ocaGroupByUnderlyingDelta.ContainsKey((ticketUnderlying, 1)) || ocaGroupByUnderlyingDelta.ContainsKey((ticketUnderlying, -1)))
                {
                    continue;
                }
                double delta = OptionContractWrap.E(this, (Option)Securities[t.Symbol], Time.Date).Delta(MidIV(t.Symbol), spot);
                if (!double.IsFinite(delta)) continue;
                var key = (t.Symbol.Underlying, Math.Sign(delta));
                ocaGroupByUnderlyingDelta[key] = t.OcaGroup;
            }
            // Now for every signal, if (underlying, sing(delta)) is found in dict, assign its OCA group to the signal
            foreach (Signal s in signals)
            {
                decimal spot = MidPrice(Underlying(s.Symbol));
                double delta = OptionContractWrap.E(this, (Option)Securities[s.Symbol], Time.Date).Delta(MidIV(s.Symbol), spot);
                int deltaSign = double.IsFinite(delta) ? Math.Sign(delta) : 0;
                var key = (Underlying(s.Symbol), deltaSign);
                if (!ocaGroupByUnderlyingDelta.ContainsKey(key))
                {
                    ocaGroupByUnderlyingDelta[key] = NewOcaGroupId();
                }
                s.AssignOcaGroup(ocaGroupByUnderlyingDelta[key]);
            }

            return signals;
        }

        /// <summary>
        /// Bounds the per-symbol order-ticket lists.
        ///
        /// Terminal tickets are history (the order-event writers persist them), but `orderTickets`
        /// was never pruned: a multi-hour run accumulated every ticket it ever submitted — 7.8 GB
        /// RSS measured on 2026-10-21 — and the cancel tag built from the still-live ones grew with
        /// it until StreamWriter.WriteLine threw OverflowException. Keep the newest
        /// <see cref="MaxTicketsPerSymbol"/> tickets per symbol, always keeping live ones.
        /// </summary>
        private void PruneOrderTickets()
        {
            lock (orderTickets)
            {
                foreach (var kvp in orderTickets)
                {
                    var list = kvp.Value;
                    if (list.Count <= MaxTicketsPerSymbol) continue;
                    var live = list.Where(t => !orderFilledCanceledCancelPendingInvalid.Contains(t.Status)).ToList();
                    var terminalRoom = Math.Max(0, MaxTicketsPerSymbol - live.Count);
                    var terminal = list
                        .Where(t => orderFilledCanceledCancelPendingInvalid.Contains(t.Status))
                        .OrderByDescending(t => t.Time)
                        .Take(terminalRoom);
                    var kept = live.Concat(terminal).OrderBy(t => t.Time).ToList();
                    list.Clear();
                    list.AddRange(kept);
                }
            }
        }

        /// <summary>
        /// Cancels undesired orders, places desired orders. In a separate thread, because would only want to place new orders, once all cancelations have been confirmed and order placement will be done in batches to not have tickets dangling in processing/unprocessed state.
        /// </summary>
        public void HandleDesiredOrders(IEnumerable<Signal> signals)
        {
            PruneOrderTickets();
            foreach (var group in signals.GroupBy(s => s.Symbol.Underlying))
            {
                // Cancel any undesired option ticket.
                var underlying = group.Key;
                var symbolDirectionToOrder = group.Select(s => (s.Symbol, s.OrderDirection)).ToList();

                List<OrderTicket> liveTickets = orderTickets.ToList().
                    Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).
                    SelectMany(kvp => kvp.Value).
                    Where(IsLiveTicket).
                    ToList();

                var ticketsToCancel = liveTickets.Where(t => !symbolDirectionToOrder.Contains((t.Symbol, Num2Direction(t.Quantity)))).ToList();
                if (ticketsToCancel.Any())
                {
                    // The tag goes into a log line: cap the id list. Building it from every live
                    // ticket made the line grow with the run until StreamWriter.WriteLine threw
                    // OverflowException (2026-10-21, a multi-hour run with an unpruned ticket list).
                    const int maxIdsInTag = 20;
                    var ids = ticketsToCancel.Select(t => t.OrderId).ToList();
                    var orderIDs = string.Join(", ", ids.Take(maxIdsInTag));
                    if (ids.Count > maxIdsInTag)
                    {
                        orderIDs += $", …(+{ids.Count - maxIdsInTag} more)";
                    }
                    string tag = $"HandleDesiredOrders. Canceling {ticketsToCancel.Count} tickets: {orderIDs}";
                    ticketsToCancel.ForEach(t => Cancel(t, tag));
                }
                List<OrderTicket> remainingLiveTickets = liveTickets.Where(t => !ticketsToCancel.Contains(t)).ToList();

                // Sort the signals. Risk reducing first, then risk accepting. Can be done based on their UtilMargin. The larger the safer.
                IEnumerable<Signal> signalByUnderlying = group.OrderByDescending(g => g.UtilityOrder.UtilityMargin);
                // Pick only the n best orders because othrewise too risky
                signalByUnderlying = signalByUnderlying.Take(Cfg.MaxDesiredOrders);

                // Ignore signal where there is already an active order ticket.
                IEnumerable<Symbol> remainingLiveSymbols = liveTickets.Select(t => t.Symbol);
                signalByUnderlying = signalByUnderlying.Where(s => !remainingLiveSymbols.Contains(s.Symbol));

                signalByUnderlying = AssignOCAGroups(signalByUnderlying, remainingLiveTickets);

                AddSignals(signalByUnderlying);
            }
        }

        public string NewOcaGroupId()
        {
            return $"oco-{Time:yyMMddHHmmss}-{ocaGroupId++}";
        }
        public void RunSignals()
        {
            var underlyings = Securities.Values.Where(s => s.Type == SecurityType.Option).Select(s => s.Symbol.Underlying).Distinct().ToList();
            foreach (Symbol underlying in underlyings)
            {
                RunSignals(underlying);
            }
        }

        /// <summary>
        /// Event driven: On MarketOpen ok, OnFill ok. On underlying moves 0.1% ok. at least every x 5mins.
        ///
        /// Launcher only. It validates cheap state, captures the option scope ON THIS THREAD and
        /// either runs the pass inline (backtest) or hands it to the underlying's worker (live).
        /// Order submission happens in <see cref="ApplySignals"/>, on the algorithm thread.
        /// </summary>
        public void RunSignals(Symbol symbol)
        {
            Symbol underlying = Underlying(symbol);

            if (IsWarmingUp ||
                !IsMyMarketOpen(underlying) ||
                Time.TimeOfDay <= mmWindow.Start ||
                Time.TimeOfDay >= mmWindow.End ||
                !Cfg.Ticker.Contains(underlying.Value)
                )
            {
                return;
            }
            // This block should not be necessary...
            if (!OnWarmupFinishedCalled)
            {
                OnWarmupFinished();
            }
            if (Time - TimeWarmupFinished < TimeSpan.FromMinutes(1))
            {
                Log($"{Time} RunSignals. Waiting a minute since warmup before restarting to trade. TimeWarmupFinished={TimeWarmupFinished}");
                return;
            }

            bool skipRunSignals = Cfg.SkipRunSignals.TryGetValue(underlying.Value, out skipRunSignals) ? skipRunSignals : Cfg.SkipRunSignals[CfgDefault];
            if (skipRunSignals) return;

            // Per-underlying single-flight gate. A second request while a pass is in flight is
            // dropped with a log — same scope, and its result would only duplicate submissions.
            if (!SignalRunner.TryAcquire(underlying))
            {
                SignalRunner.CountSkipped();
                Log($"{Time} RunSignals. underlying={underlying}. A signal pass is already running — skipping this request.");
                return;
            }

            List<ScopedOption> scope;
            DateTime capturedAt = Time;
            try
            {
                Log($"{Time} RunSignals. underlying={underlying}");
                scope = CaptureScope(underlying);   // MAIN THREAD — see ScopedOption
            }
            catch (Exception ex)
            {
                SignalRunner.Release(underlying);
                Error($"RunSignals({underlying}): scope capture failed: {ex.Message}");
                return;
            }

            if (scope == null || scope.Count == 0)
            {
                SignalsLastRun[underlying] = Time;
                SignalRunner.Release(underlying);
                return;
            }

            double minUtility;
            try
            {
                minUtility = MinUtility(underlying);
            }
            catch (Exception ex)
            {
                SignalRunner.Release(underlying);
                Error($"RunSignals({underlying}): config lookup failed: {ex.Message}");
                return;
            }

            if (!AsyncSignals)
            {
                // Backtest: identical code path, inline — deterministic, still fanned out per contract.
                // Release in finally: ComputeSignals sits outside ApplySignals' own try/catch, so a
                // throw there would otherwise leave the single-flight gate acquired forever and every
                // later pass for this underlying would be skipped.
                try
                {
                    ApplySignals(ComputeSignals(underlying, scope, minUtility, capturedAt));
                }
                finally
                {
                    SignalRunner.Release(underlying);
                }
                return;
            }

            if (!SignalRunner.Launch(underlying, () => ComputeSignals(underlying, scope, minUtility, capturedAt)))
            {
                SignalRunner.Release(underlying);
            }
        }

        /// <summary>
        /// Applies one completed pass on the ALGORITHM THREAD: re-validate what can have changed
        /// since the scope was captured, then submit. Order submission, OCA assignment and ticket
        /// bookkeeping all live below this point and must never run on a worker.
        /// </summary>
        public void ApplySignals(SignalBatch batch)
        {
            Symbol underlying = batch.Underlying;
            try
            {
                if (batch.Error != null)
                {
                    Error($"RunSignals({underlying}): background pass failed: {batch.Error.Message}");
                    return;
                }

                LogSignalBatch(batch);

                var keep = new List<Signal>(batch.Signals.Count);
                int stale = 0;
                foreach (Signal signal in batch.Signals)
                {
                    if (SignalStillValid(signal, batch)) keep.Add(signal);
                    else stale++;
                }

                double ageMs = batch.StalenessMs(LiveMode, Time);
                double wallMs = batch.WallAgeMs;
                var attributes = new[] { new KeyValuePair<string, object>("underlying", underlying.Value) };
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.signals.pass_ms", batch.ComputeMs, attributes);
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.signals.apply_delay_ms", ageMs, attributes);
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.signals.apply_wall_ms", wallMs, attributes);
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.signals.stale", stale, attributes);
                Log($"{Time} ApplySignals. underlying={underlying}. #Signals={batch.Signals.Count} #Applied={keep.Count} " +
                    $"#Stale={stale} age={ageMs:0}ms wall={wallMs:0}ms at={batch.CapturedUtc:HH:mm:ss.fff} compute={batch.ComputeMs:0}ms speedup={batch.Speedup:0.0}x" +
                    (batch.Errors > 0 ? $" #Errors={batch.Errors} ({batch.ErrorSample})" : ""));

                if (keep.Count > 0) HandleDesiredOrders(keep);
                SignalsLastRun[underlying] = Time;
            }
            finally
            {
                SignalRunner.Release(underlying);
            }
        }

        /// <summary>Runs every completed pass on the algorithm thread. Call once per slice.</summary>
        public void DrainCompletedSignals()
        {
            while (SignalRunner.TryDequeueCompleted(out SignalBatch batch))
            {
                ApplySignals(batch);
            }
        }

        /// <summary>
        /// Everything a signal must still satisfy at APPLY time. Prices and quantities are NOT
        /// re-checked here: OrderOptionContract recomputes the quantity from live state and clamps
        /// the limit to the live NBBO. What can genuinely differ is the decision — side gate, scope
        /// membership and age.
        /// </summary>
        private bool SignalStillValid(Signal signal, SignalBatch batch)
        {
            Symbol underlying = Underlying(signal.Symbol);
            if (IsWarmingUp || !IsMyMarketOpen(underlying)) return false;
            if (!Cfg.Ticker.Contains(underlying.Value)) return false;
            double ageMs = batch.StalenessMs(LiveMode, Time);
            if (ageMs > Cfg.MaxSignalBatchAgeMs)
            {
                Log($"{Time} SignalStillValid: dropping {signal.Symbol} — batch age {ageMs:0}ms " +
                    $"(wall {batch.WallAgeMs:0}ms, sim {batch.SimAgeMs(Time):0}ms, live={LiveMode}) > {Cfg.MaxSignalBatchAgeMs}ms.");
                return false;
            }
            if (!IfFilledDecreasesEquityHedgeOrderSize(signal.Symbol, signal.OrderDirection)) return false;
            if (Securities.ContainsKey(signal.Symbol) && Securities[signal.Symbol] is Option option &&
                !ContractScopedForNewPosition(option)) return false;
            return true;
        }

        /// <summary>
        /// Cancels OCA order groups and respective tickets
        /// </summary>
        public void CancelOpenOptionTickets()
        {
            if (IsWarmingUp) return;

            List<OrderTicket> tickets;
            lock (orderTickets)
            {
                tickets = orderTickets.SelectMany(t => t.Value).ToList();
            }

            foreach (OrderTicket t in tickets.Where(t => t.Status != OrderStatus.Invalid && t.Symbol.SecurityType == SecurityType.Option))
            {
                string tag = QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "action", $"CancelOpenTickets. Canceling {t.Symbol} OCAGroup/Type: {t.OcaGroup}/{t.OcaType}. EndOfDay" } });
                Cancel(t, tag);
            }
        }

        public void LogRiskSchedule()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            LogPositions();
            LogRisk();
            LogPnL();
            LogOrderTickets();
            Log($"{Time} LogRiskSchedule. IsMarketOpen(symbolSubscribed)={IsMyMarketOpen(symbolSubscribed)}, symbolSubscribed={symbolSubscribed}");
        }

        /// <summary>
        /// False for options that would increase the abs portfolio delta WHILE an equity order (hedge) is live.
        /// Defaults to true if no equity hedge is ongoing.
        /// </summary>
        /// <returns></returns>
        public bool IfFilledDecreasesEquityHedgeOrderSize(Symbol symbol, OrderDirection orderDirection)
        {
            if (symbol.SecurityType != SecurityType.Option) return false;

            Symbol underlying = Underlying(symbol);
            bool isDeltaHedgeInProgress = IsDeltaHedgeInProgress(underlying);
            if (isDeltaHedgeInProgress)
            {
                decimal spot = MidPrice(underlying);
                int direction = DIRECTION2NUM[orderDirection];
                double iv = MidIV(symbol);
                if (!double.IsFinite(iv))
                {
                    Log($"{Time} IfFilledDecreasesEquityHedgeOrderSize: {symbol} MidIV is NaN/Inf. Defaulting to true.");
                    return true;
                }
                double delta = direction * OptionContractWrap.E(this, (Option)Securities[symbol], Time.Date).Delta(iv, spot);
                if (!double.IsFinite(delta))
                {
                    Log($"{Time} IfFilledDecreasesEquityHedgeOrderSize: {symbol} Delta is NaN/Inf. Defaulting to true.");
                    return true;
                }

                double pfDeltaTotal = (double)DeltaMV(underlying);

                return Math.Sign(delta) != Math.Sign(pfDeltaTotal);
            }
            else
            {
                return true;
            }
        }
        public bool ContractScopedForSubscription(Symbol symbol, decimal? priceUnderlying = null, decimal margin = 0m)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return (midPriceUnderlying > 0
                && symbol.ID.Date > Time + TimeSpan.FromDays(Cfg.ScopeContractMinDTE)
                && symbol.ID.Date < Time + TimeSpan.FromDays(Cfg.ScopeContractMaxDTE)
                && symbol.ID.OptionStyle == OptionStyle.American
                && symbol.ID.StrikePrice >= midPriceUnderlying * (Cfg.ScopeContractStrikeOverUnderlyingMin - margin)
                && symbol.ID.StrikePrice <= midPriceUnderlying * (Cfg.ScopeContractStrikeOverUnderlyingMax + margin)
                //When live need to start fetching data. Unlike during backtesting cannot just subscribe and fetch past data from disk.
                //&& IsLiquid(symbol, Cfg.ScopeContractIsLiquidDays, Resolution.Daily)
                )
                ||
                (Portfolio.ContainsKey(symbol) && Portfolio[symbol].Quantity != 0)
                || ManualOrderInstructionBySymbol.ContainsKey(symbol.Value)
                || TargetHoldings.ContainsKey(symbol);
        }

        public void RemoveUniverseSecurity(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                    (
                    Securities[symbol].IsTradable
                    && !ContractScopedForSubscription(symbol, null, Cfg.ScopeContractStrikeOverUnderlyingMargin)
                    && Portfolio[symbol].Quantity == 0
                    )
                //|| security.IsDelisted
                )
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Removing {symbol}. Descoped." } });
                RemoveSecurity(symbol);  // Open Transaction will be canceled
            }
        }

        /// <summary>
        /// Last Mile Checks
        /// </summary>
        public bool IsOrderValid(Symbol symbol, decimal quantity)
        {
            var security = Securities[symbol];
            if (quantity < 1 && quantity > -1)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"Submitted Quantity zero. {symbol}. quantity={quantity} Stack Trace: {Environment.StackTrace}" } });
                return false;
            }
            ;
            // Tradable
            if (!security.IsTradable)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"security {security} not marked tradeable. Should not be sent as signal. Not trading..." } });
                return false;
            }

            // Timing
            if (IsWarmingUp ||
                ((Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) && symbol.SecurityType == SecurityType.Option)  // Delta hedging with Equity anytime.
                )
            {
                QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"Not time to trade yet." } });
                return false;
            }

            // Only 1 ticket per Symbol & Side
            if (orderTickets.TryGetValue(symbol, out var tickets))
            {
                foreach (var ticket in tickets.Where(t => !orderFilledCanceledInvalid.Contains(t.Status)))
                {
                    // Assigning fairly negative utility to this inventory increase.
                    if (ticket.Quantity * quantity >= 0)
                    {
                        QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an order ticket with same sign: LeanID={ticket.OrderId}. Status: {ticket.Status}. For now only want 1 order. Not processing" } });
                        return false;
                    }

                    if (ticket.Quantity * quantity <= 0)
                    {
                        QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order: LeanID={ticket.OrderId}. Status: {ticket.Status} Not processing..." } });
                        return false;
                    }
                }
            }

            //if (symbol.SecurityType == SecurityType.Option && Portfolio[symbol].Quantity * quantity > 0)
            //{
            //    QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an options position with same sign Quantity={Portfolio[symbol].Quantity}. Not processing...\"" } });
            //    return false;
            //}

            if (symbol.SecurityType == SecurityType.Option && !ContractScopedForSubscription(symbol) && Portfolio[symbol].Quantity == 0)
            {
                QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION" }, { "msg", $"contract {symbol} is not in scope. Not trading..." } });
                RemoveUniverseSecurity(Securities[symbol]);
                return false;
            }

            if (Transactions.CancelRequestsUnprocessed.Count() >= Cfg.MinCancelRequestsUnprocessedBlockingSubmit && symbol.SecurityType != SecurityType.Equity)
            {
                QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION" }, { "msg", $"CancelRequests awaiting processing {Transactions.CancelRequestsUnprocessed.Count()}. Not submitting..." } });
                return false;
            }

            if (Transactions.SubmitRequestsUnprocessed.Count() >= Cfg.MinSubmitRequestsUnprocessedBlockingSubmit && symbol.SecurityType != SecurityType.Equity)
            {
                QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION" }, { "msg", $"SubmitRequest awaiting processing {Transactions.SubmitRequestsUnprocessed.Count()}. Not submitting..." } });
                return false;
            }

            // Protect against ordering on stale data. Especially dangerous when restarting the algo.
            if (IsPriceStale(symbol))
            {
                var cache = Securities[symbol].Cache;
                var lastUpdated = cache.LastQuoteBarUpdate > cache.LastOHLCUpdate ? cache.LastQuoteBarUpdate : cache.LastOHLCUpdate;
                QuickLog(new Dictionary<string, string> { { "topic", "EXECUTION" }, { "msg", $"Price is stale. Not trading... Symbol={symbol}, LastUpdated={lastUpdated:O}" } });
                return false;
            }

            return true;
        }

        public bool IsPriceStale(Symbol symbol, TimeSpan? timeSpan = null)
        {
            var cache = Securities[symbol].Cache;
            var lastUpdated = cache.LastQuoteBarUpdate > cache.LastOHLCUpdate ? cache.LastQuoteBarUpdate : cache.LastOHLCUpdate;
            return (Time - lastUpdated) > (timeSpan ?? TimeSpan.FromMinutes(15));
        }

        public void StoreOrderTicket(OrderTicket orderTicket, Quote<Option>? quote = null, IUtilityOrder? utilityOrder = null)
        {
            if (orderTicket == null) return;

            lock (orderTickets)
            {
                if (!orderTickets.ContainsKey(orderTicket.Symbol))
                {
                    orderTickets[orderTicket.Symbol] = new List<OrderTicket>();
                }
                ;
                orderTickets[orderTicket.Symbol].Add(orderTicket);
            }
            ;

            // Start the equity's sweep clock on the first option ticket it gets (see
            // RiskScenarioHandler.NoteOptionTicket - it used to be written from a pricing getter, so
            // the clock depended on which contract asked for a price first).
            RiskScenarioHandler.NoteOptionTicket(orderTicket);

            if (quote != null)
            {
                Quotes[orderTicket.OrderId] = quote;
            }
            if (utilityOrder != null)
            {
                OrderTicket2UtilityOrder[orderTicket.OrderId] = utilityOrder;
            }            

            // Occasionally limit orders dont get processed resulting in losses due to missing hedging. Also, these eventually hit a timeout set to 15min by QC resulting in runtime error.
            // Therefore, checking frequently whether a ticket has been process - orderTicket.SubmitRequest.Status;
            // Expecting orderStatus to be at least Submitted. If not, cancel and allow algo to resubmit.

            // Expecting very fast turnaround time for equity orders. Options order are ok to take longer as they are not used to hedge currently.

            int timeout = 10;
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(timeout)), () => CancelOrderTicketIfUnprocessed(orderTicket.OrderId, timeout));

            OrderEventWriters[Underlying(orderTicket.Symbol)].Write(orderTicket);
        }
        public void CancelOrderTicketIfUnprocessed(int orderId, int sec)
        {
            OrderTicket ticket = Transactions.GetOrderTicket(orderId);
            if (ticket?.CancelRequest == null && ticket.SubmitRequest.Status == OrderRequestStatus.Unprocessed)
            {
                // This was not encountered.
                string tag = $"CancelOrderTicketIfUnprocessed: {ticket.Symbol} Status: {ticket.Status} remained Unprocessed for {sec} sec after new submission. Canceling LeanID={ticket.OrderId}";
                Cancel(ticket, tag);
            }
            else if (ticket?.CancelRequest == null && $"{ticket.Status}" == $"{OrderStatus.New}" && $"{ticket.SubmitRequest.Status}" == $"{OrderRequestStatus.Error}")
            {
                // SubmitRequest.Status is initialized with error. true even if log prints false, therefore evaluating as string now..
                // SubmitRequest.Status=Processed. 15. Remains in bad submission state after 52 seconds. Canceling.
                string tag = $"CancelOrderTicketIfUnprocessed: Remains in bad submission state after {sec} seconds. Sybmol={ticket.Symbol}, OrderTicket={ticket}, LeanId={ticket.OrderId}, Quantity={ticket.Quantity}, Ticket.Status={ticket.Status}, Ticket.SubmitRequest.Status={ticket.SubmitRequest.Status}.";
                Cancel(ticket, tag);
            }
            ;
        }
        public void OrderEquity(Symbol symbol, decimal quantity, decimal limitPrice, OrderType orderType, string tag = "")
        {
            if (!IsOrderValid(symbol, quantity)) { return; }

            OrderTicket orderTicket = orderType switch
            {
                OrderType.Limit => LimitOrder(symbol, quantity, RoundTick(limitPrice, TickSize(symbol)), tag),
                OrderType.Market => MarketOrder(symbol, quantity, tag: tag, asynchronous: LiveMode),
                _ => throw new NotImplementedException($"OrderType {orderType} not implemented")
            };
            LogOrderTicket(orderTicket);
            StoreOrderTicket(orderTicket);
        }
        public OrderTicket? OrderOptionContract(Signal signal, OrderType orderType = OrderType.Limit, string tag = "")
        {
            Option contract = Securities[signal.Symbol] as Option;
            decimal quantity = SignalQuantity(signal.Symbol, signal.OrderDirection);
            if (Math.Round(quantity, 0) == 0) return null;

            if (!IsOrderValid(contract.Symbol, quantity)) { return null; }

            Quote<Option> quote = GetQuote(new QuoteRequest<Option>(contract, quantity, signal.UtilityOrder));
            decimal limitPrice = quote.Price;
            if (limitPrice == 0)
            {
                Log($"{Time} OrderOptionContract(): No price quoted for {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading...");
                return null;
            }
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));
            if (limitPrice == 0 || limitPrice < TickSize(contract.Symbol))
            {
                Log($"{Time} OrderOptionContract(): Invalid price: {limitPrice}. {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading... TickSize: {TickSize(contract.Symbol)}");
                return null;
            }

            // limit to NBBO
            limitPrice = quantity < 0 ? Math.Max(limitPrice, contract.BidPrice) : Math.Min(limitPrice, contract.AskPrice);

            OrderTicket orderTicket;

            switch (orderType)
            {
                //// Not in use due to IB limiting number of simultaneous pegged orders.
                //case OrderType.PeggedToStock:

                //if (quote.IVPrice == 0) return null;
                //var ocw = OptionContractWrap.E(this, contract, Time.Date);
                //decimal delta = (decimal)Math.Abs(ocw.Delta(quote.IVPrice));
                //decimal gamma = (decimal)ocw.Gamma(quote.IVPrice);
                //var midPriceUnderlying = MidPrice(contract.Underlying.Symbol);
                //var offset = delta * Cfg.PeggedToStockDeltaRangeOffsetFactor / gamma;
                //var underlyingRangeLow = midPriceUnderlying - offset;
                //var underlyingRangeHigh = midPriceUnderlying + offset;
                //orderTicket = PeggedToStockOrder(contract.Symbol, quantity, delta * 100m, limitPrice, midPriceUnderlying, underlyingRangeLow, underlyingRangeHigh, tag);
                //OrderIdIV[orderTicket.OrderId] = quote.IVPrice;
                //break;
                case OrderType.Limit:
                    orderTicket = LimitOrder(contract.Symbol, quantity, limitPrice, tag, ocaGroup: signal.OcaGroup, ocaType: signal.OcaType);
                    break;

                case OrderType.Market:
                    orderTicket = MarketOrder(contract.Symbol, (int)quantity, tag: tag, asynchronous: LiveMode);
                    break;

                default:
                    throw new NotImplementedException($"OrderType {orderType} not implemented");
            }

            LogOrderTicket(orderTicket);
            StoreOrderTicket(orderTicket, quote, signal.UtilityOrder);
            return orderTicket;
        }
        public void UpdateLimitPrice(Symbol symbol)
        {
            if (!orderTickets.ContainsKey(symbol))
            {
                return;
            }
            if (symbol.SecurityType == SecurityType.Option && !symbol.IsCanonical())
            {
                UpdateLimitOrder(Securities[symbol] as Option);
            }
            else if (symbol.SecurityType == SecurityType.Equity)
            {
                UpdateLimitOrder(Securities[symbol] as Equity);
            }
        }

        /// <summary>
        /// Cache this ...
        /// </summary>
        /// <param name="underlying"></param>
        /// <param name="dt"></param>
        /// <returns></returns>
        public DateTime NextReleaseDate(Symbol underlying, DateTime? dt = null)
        {
            var dfltReleaseDate = new DateTime(2000, 1, 1);
            if (EarningsBySymbol.TryGetValue(underlying, out var earningsBySymbol))
            {
                var eas = earningsBySymbol.Where(ea => ea.Date >= (dt ?? Time.Date));
                return eas.Any() ? eas.Select(ea => ea.Date).Min() : dfltReleaseDate;
            }
            return dfltReleaseDate;
        }
        public DateTime PreviouReleaseDate(Symbol underlying, DateTime? dt = null)
        {
            var dfltReleaseDate = new DateTime(2000, 1, 1);
            if (EarningsBySymbol.TryGetValue(underlying, out var earningsBySymbol))
            {
                var eas = earningsBySymbol.Where(ea => ea.Date < (dt ?? Time.Date));
                return eas.Any() ? eas.Select(ea => ea.Date).Max() : dfltReleaseDate;
            }
            return dfltReleaseDate;
        }
        public void UpdateLimitOrder(Option option)
        {
            if (!Cfg.Ticker.Contains(option.Underlying.ToString()))
            {
                Log($"{Time}: UpdateLimitOrder(option). Not trading {option.Underlying}. Not in ticker.");
                return;
            }
            Symbol symbol = option.Symbol;
            foreach (OrderTicket ticket in orderTickets[symbol].ToList())
            {
                if (orderSubmittedPartialFilledUpdated.Contains(ticket.Status) && ticket.OrderType == OrderType.Limit &&
                    (ticket.CancelRequest == null || ticket.CancelRequest.Status == OrderRequestStatus.Error))
                {
                    decimal tickSize = TickSize(symbol);
                    decimal limitPrice = ticket.Get(OrderField.LimitPrice);

                    decimal orderQuantity = SignalQuantity(symbol, Num2Direction(ticket.Quantity));
                    if (orderQuantity == 0)
                    {
                        Cancel(ticket, tag: $"orderQuantity={orderQuantity}");
                        return;
                    }
                    IUtilityOrder utilityOrder = UtilityOrderFactory.Create(this, option, orderQuantity);
                    Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, orderQuantity, utilityOrder));

                    decimal idealLimitPrice = quote.Price;

                    if (idealLimitPrice == 0 || quote.Quantity == 0)
                    {
                        string tag = $"UpdateLimitOrderOption: Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. Not trading...";
                        Cancel(ticket, tag);
                        return;
                    }
                    idealLimitPrice = RoundTick(idealLimitPrice, tickSize);

                    // Dont undercut one's own order. I would recursively undercutting my own order paying 100% of spread.
                    // This does not sit well with sweep logic. Need to update own order referencing the original spread.
                    //if (
                    //    (Num2Direction(orderQuantity) == OrderDirection.Buy  && idealLimitPrice >= limitPrice && limitPrice >= option.BidPrice ) ||
                    //    (Num2Direction(orderQuantity) == OrderDirection.Sell && idealLimitPrice <= limitPrice && limitPrice <= option.BidPrice )
                    //    )
                    //{
                    //    Log($"{Time}: UpdateLimitOrderOption. Not updating to avoid undercutting own order recursively. Symbol{symbol}: idealLimitPrice={idealLimitPrice}, limitPrice={limitPrice}");
                    //    return;
                    //}

                    // Price
                    if (idealLimitPrice >= tickSize && idealLimitPrice != limitPrice)
                    {
                        // refactor this into a function logging attributes of an order ticket or extension of orderticket.
                        var tag = $"{Time}: UPDATE LIMIT Price Symbol={symbol}, Quantity={ticket.Quantity}, LeanId={ticket.OrderId}, OcaGroup/Type={ticket.OcaGroup}/{ticket.OcaType}, currentLimitPrice={limitPrice}, newLimitPrice={idealLimitPrice}, Bid={option.BidPrice}, Ask={option.AskPrice}";
                        var response = ticket.UpdateLimitPrice(idealLimitPrice, tag);
                        if (Cfg.LogOrderUpdates || LiveMode)
                        {
                            Log($"{tag}, Response: {response}, IsProcessed: {response.IsProcessed}");
                        }
                        Quotes[ticket.OrderId] = quote;
                    }
                    else if (idealLimitPrice < tickSize)
                    {
                        Log($"{Time}: UpdateLimitOrderOption: Price too small Not updating. Symbol={symbol}, limitPrice={limitPrice}, tickSize={tickSize}");
                        //Cancel(ticket);
                    }

                    // Quantity - low overhead. SignalQuantity needs risk metrics that are also fetched for getting a price and cached.
                    if (ticket.Quantity != quote.Quantity && Math.Abs(ticket.Quantity - quote.Quantity) >= 2)
                    {
                        var tag = $"{Time}: UPDATE LIMIT Quantity Symbol {symbol}, PrevQuantity={ticket.Quantity}, NewQuantity={quote.Quantity}, LeanID={ticket.OrderId}, OcaGroup/Type={ticket.OcaGroup}/{ticket.OcaType}";
                        var response = ticket.UpdateQuantity(quote.Quantity, tag);
                        if (Cfg.LogOrderUpdates || LiveMode)
                        {
                            Log($"{tag}, Response: {response}");
                        }
                        Quotes[ticket.OrderId] = quote;
                    }
                }
                else if (ticket.Status == OrderStatus.CancelPending) { }
                //else
                //{
                //    Log($"{Time} UpdateLimitPriceContract {option} ticket={ticket}, OrderStatus={ticket.Status} - Should not run this function for this ticket. Cleanup orderTickets.");
                //}
                // Log($"{Time} UpdateLimitPriceContract {option} ticket={ticket}, OrderStatus={ticket.Status}. No action taken.");
            }
        }

        public OrderResponse? Cancel(OrderTicket ticket, string tag = "")
        {
            if (orderCanceledOrPending.Contains(ticket.Status)) return null;

            Log($"{Time} Cancel: {ticket.Symbol}, LeanID={ticket.OrderId}, Status={ticket.Status} {ticket}, CancelTag={tag}");
            var response = ticket.Cancel(tag);
            OrderEventWriters[Underlying(ticket.Symbol)].Write(ticket);
            return response;
        }

        public double HedgeVolatility(Symbol symbol)
        {
            return GetHedgingMode(symbol) switch
            {
                HedgingMode.FwdRealizedVolatility => (double)Securities[Underlying(symbol)].VolatilityModel.Volatility,
                HedgingMode.HistoricalVolatility => (double)Securities[Underlying(symbol)].VolatilityModel.Volatility,
                HedgingMode.ImpliedVolatility => MidIV(symbol),
                HedgingMode.ImpliedVolatilitySSVI => MidIVSsvi(symbol),
                _ => throw new NotImplementedException($"HedgingMode {GetHedgingMode(symbol)} not implemented"),
            };
        }

        public Metric HedgeMetric(Symbol symbol)
        {
            return GetHedgingMode(symbol) switch
            {
                HedgingMode.FwdRealizedVolatility or HedgingMode.HistoricalVolatility => Metric.DeltaTotal,
                HedgingMode.ImpliedVolatility => Metric.DeltaImpliedTotal,
                HedgingMode.ImpliedVolatilitySSVI => Metric.DeltaImpliedSSVITotal,
                _ => Metric.DeltaTotal,
            };
        }

        public Func<Symbol, double> FuncVolatility(VolatilityType volatilityType)
        {
            return volatilityType switch
            {
                VolatilityType.HVHedge => (symbol) => (double)Securities[Underlying(symbol)].VolatilityModel.Volatility,
                VolatilityType.IVMid => (symbol) => MidIV(symbol),
                VolatilityType.IVBid => (symbol) => IvBids[symbol].IVBidAsk.IV,
                VolatilityType.IVAsk => (symbol) => IvAsks[symbol].IVBidAsk.IV,
                _ => throw new NotImplementedException($"VolatilityType {volatilityType} not implemented")
            };
        }

        public decimal EquityHedgeQuantity(Symbol underlying)
        {
            decimal quantity;
            decimal deltaTotal = PfRisk.RiskByUnderlying(underlying, HedgeMetric(underlying));

            //decimal deltaIVdSTotal = 0;// PfRisk.RiskByUnderlying(underlying, Metric.DeltaIVdSTotal, HedgeVolatility(underlying));  // MV
            quantity = -deltaTotal;

            // subtract pending Market order fills
            List<OrderTicket> tickets = orderTickets.TryGetValue(underlying, out tickets) ? tickets : new List<OrderTicket>();
            if (tickets.Any())
            {
                var marketOrders = tickets.Where(t => t.OrderType == OrderType.Market && orderSubmittedPartialFilledUpdated.Contains(t.Status)).ToList();
                decimal orderedQuantityMarket = marketOrders.Sum(t => t.Quantity);
                quantity -= orderedQuantityMarket;
                if (orderedQuantityMarket != 0)
                {
                    Log($"{Time} EquityHedgeQuantity: Market Orders present for {underlying} {orderedQuantityMarket} LeanID={string.Join(", ", marketOrders.Select(t => t.OrderId))}.");
                }
                Log($"{Time} EquityHedgeQuantity: DeltaTotal={deltaTotal}");//, deltaIVdSTotal={deltaIVdSTotal} (not used)");
            }

            return Math.Round(quantity, 0);
        }

        public void UpdateLimitOrder(Equity equity)
        {
            if (!Cfg.Ticker.Contains(equity.Symbol.Value))
            {
                Log($"{Time} UpdateLimitOrderEquity: {equity.Symbol} not in ticker. Not trading...");
                return;
            }

            decimal idealLimitPrice;
            int cnt = 0;

            foreach (var ticket in orderTickets[equity.Symbol].ToList().Where(t =>
                    t.OrderType == OrderType.Limit &&
                    orderSubmittedPartialFilledUpdated.Contains(t.Status) &&
                    (t.CancelRequest == null || t.CancelRequest.Status == OrderRequestStatus.Error)
                    )
                )
            {
                if (cnt > 1)
                {
                    string tag = $"UpdateLimitOrderEquity: Reason=Too many orders";
                    Cancel(ticket, tag);
                    continue;
                }
                cnt++;

                decimal quantity = Math.Round(EquityHedgeQuantity(equity.Symbol));

                decimal ts = TickSize(ticket.Symbol);
                decimal ticketPrice = ticket.Get(OrderField.LimitPrice);

                idealLimitPrice = GetEquityHedgePrice(equity, ticket.OrderType, quantity, ticket);
                idealLimitPrice = RoundTick(idealLimitPrice, ts);
                if (idealLimitPrice != ticketPrice && idealLimitPrice > 0)
                {
                    var tag = $"{Time}: {ticket.Symbol} Price not good {ticketPrice}: Changing to ideal limit price: {idealLimitPrice}. Bid={equity.BidPrice}, Ask={equity.AskPrice}";
                    var response = ticket.UpdateLimitPrice(idealLimitPrice, tag);
                    Log($"{tag}, Response: {response}");
                }
                if (quantity != ticket.Quantity)
                {
                    var tag = $"{Time}: {ticket.Symbol} Quantity not good {ticket.Quantity}: Changing to ideal quantity: {quantity}";
                    var response = ticket.UpdateQuantity(quantity, tag);
                    Log($"{tag}, Response: {response}");
                }
            }
        }

        /// <summary>
        /// Fee. 0.005 USD per share. Minimum 1 USD per trade. Max 1% of trade value. Objective of this function is return the maximum quantity that would still be worth paying the minimum fee of 1 USD of 1% of trade value.
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public decimal QuantityExceedingMinimumBrokerageFee(Symbol symbol)
        {
            return 1 / 0.005m; // Get as many shares as fee/stock allows up to min Fee of 1 USD
            //return Math.Min(
            //    1 / 0.005m, // Get as many shares as possible for 1 USD
            //    // Q * Securities[Underlying(symbol)].Price * 0.01m // Max at 1% of trade value. 
            //    );  
        }

        /// <summary>
        /// To be refactored. Should utitlized Quantconnect's existing system. But it's significantly off for options...
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="quantity"></param>
        /// <returns></returns>
        public decimal TransactionCosts(Symbol symbol, decimal quantity)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Equity => Math.Abs(Math.Max(quantity * 0.005m, 1.05m)),
                SecurityType.Option => Math.Abs(quantity * 0.65m),
            };
        }

        public decimal MaxGammaRespectingQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absQuantity = 9999;
            HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(Underlying(symbol)), out regimes) ? regimes : new();
            if (regimes.Contains(MarketRegime.SellEventCalendarHedge))
            {
                var totalGamma = PfRisk.RiskByUnderlying(symbol.Underlying, Metric.GammaTotal);
                var gammaOrder = (double)PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], Metric.GammaTotal);
                if (gammaOrder < 0)
                {
                    absQuantity = Math.Floor((decimal)Math.Abs((double)totalGamma / (double)gammaOrder));
                }
                ;
            }
            return absQuantity;
        }

        public decimal MaxLongRespectingDeltaQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absMaxLongPosRespectingQuantity;
            /// Want to avoid minimum fee payment of 1 USD/stock trade, hence looking to hit a delta that causes at least an equity fee of 1 USD during hedding and minimizes an absolute delta increase.
            /// So the target delta is +/-200.
            /// For more expensive stocks, wouldn't want to increase equity position too quickly, hence not exceed 5k long position. configurable

            // Find the delta that would cause an equity position of long max 5k if filled. No restriction for shorting
            decimal targetMaxEquityPositionUSD = Cfg.TargetMaxEquityPositionUSD.TryGetValue(Underlying(symbol).Value, out targetMaxEquityPositionUSD) ? targetMaxEquityPositionUSD : Cfg.TargetMaxEquityPositionUSD[CfgDefault];

            var currentDelta = PfRisk.RiskByUnderlying(symbol.Underlying, HedgeMetric(Underlying(symbol)));
            var deltaPerUnit = PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], HedgeMetric(Underlying(symbol)));

            if (deltaPerUnit == 0) // ZeroDivisionError
            {
                absMaxLongPosRespectingQuantity = SignalQuantityDflt;
            }
            else if (deltaPerUnit * currentDelta > 0) // same direction. Increase risk up to ~200 more. Don't exceed ~5k long position.
            {
                var absMaxLongPosRespectingDelta = targetMaxEquityPositionUSD / Securities[symbol.Underlying].Price;
                absMaxLongPosRespectingQuantity = Math.Abs((absMaxLongPosRespectingDelta - Math.Abs(currentDelta)) / deltaPerUnit);
            }
            else // opposite direction. Risk reducing / reversing. Aim for delta reversal, but not not to max 5k.
            {
                var absMaxLongPosRespectingDelta = Math.Abs(currentDelta) + targetMaxEquityPositionUSD / Securities[symbol.Underlying].Price;
                absMaxLongPosRespectingQuantity = Math.Abs(absMaxLongPosRespectingDelta / deltaPerUnit);
            }
            return absMaxLongPosRespectingQuantity;
        }

        public decimal AbsMaxFeeMinimizingQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absFeeMinimizingQuantity = 9999;
            var currentDelta = PfRisk.RiskByUnderlying(symbol.Underlying, HedgeMetric(Underlying(symbol)));
            var deltaPerUnit = PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], HedgeMetric(Underlying(symbol)));
            var absFeeMinimizingDelta = QuantityExceedingMinimumBrokerageFee(symbol); // Make the hedge worthwile

            if (deltaPerUnit == 0) // ZeroDivisionError
            {

            }
            else if (deltaPerUnit * currentDelta > 0) // same direction. Increase risk up to ~200 more. Don't exceed ~5k long position.
            {
                absFeeMinimizingQuantity = Math.Abs((absFeeMinimizingDelta - Math.Abs(currentDelta)) / deltaPerUnit);
            }
            else // opposite direction. Risk reducing / reversing. Aim for delta reversal, but not not to max 5k.
            {
                absFeeMinimizingQuantity = Math.Abs((
                    Math.Abs(currentDelta) +  // To zero Risk
                    absFeeMinimizingDelta)  // Reversing Delta Risk to worthwhile ~200
                    / deltaPerUnit);
            }
            return absFeeMinimizingQuantity;
        }

        /// <summary>
        /// TBD
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="orderDirection"></param>
        /// <returns></returns>
        public decimal MaxQuantityByMarginConstraints(Symbol symbol, OrderDirection orderDirection) => 9999;

        /// <summary>
        /// USD utility of trading `SignalQuantity` of this contract at `price` - one full utility
        /// evaluation (a greek vector across DS buckets), so callers must cache per bar. The sweep uses
        /// it once per ticket when it freezes its anchors, and once per quote for the frontier contract.
        /// </summary>
        private readonly Dictionary<Symbol, DateTime> _lastUtilityAtErrorLog = new();

        public double UtilityAt(Option option, OrderDirection direction, decimal price)
        {
            if (option == null || direction == OrderDirection.Hold) return 0;

            decimal quantity = SignalQuantity(option.Symbol, direction);
            if (quantity == 0) return 0;

            try
            {
                IUtilityOrder utilityOrder = UtilityOrderFactory.Create(this, option, quantity, price);
                if (utilityOrder == null) return 0;

                double utility = utilityOrder.Utility;
                return double.IsFinite(utility) ? utility : 0;
            }
            catch (Exception error)
            {
                // A utility that cannot be evaluated (surface not calibrated yet, a greek the pricer
                // cannot produce) must not kill the run from a scheduled event or a pricing call: it
                // means "no utility budget", which leaves the sweep resting on its anchor.
                if (_lastUtilityAtErrorLog.Count > 5000) _lastUtilityAtErrorLog.Clear();
                if (!_lastUtilityAtErrorLog.TryGetValue(option.Symbol, out DateTime last) || Time - last > TimeSpan.FromMinutes(1))
                {
                    _lastUtilityAtErrorLog[option.Symbol] = Time;
                    Log($"{Time} UtilityAt(): utility unavailable for {option.Symbol.Value} {direction} at {price} ({error.GetType().Name}: {error.Message}) - treated as 0.");
                }
                return 0;
            }
        }

        /// <summary>
        /// Function is buggy and out of date pretty much. Only return -1/+1 at the moment
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="orderDirection"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public decimal SignalQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            if (orderDirection == OrderDirection.Hold) { throw new ArgumentException("OrderDirection.Hold not allowed in SignalQuantity."); }

            decimal absQuantity;
            decimal maxOptionOrderQuantity = Math.Abs(Cfg.MaxOptionOrderQuantity.TryGetValue(Underlying(symbol).Value, out maxOptionOrderQuantity) ? maxOptionOrderQuantity : Cfg.MaxOptionOrderQuantity[CfgDefault]);
            // Move this into the UtilityOrder class. Let that class determine the best quantity.

            //decimal signalQuantityFraction = Cfg.SignalQuantityFraction.TryGetValue(Underlying(symbol).Value, out signalQuantityFraction) ? signalQuantityFraction : Cfg.SignalQuantityFraction[CfgDefault];
            //absQuantity /= signalQuantityFraction;
            /// Want to avoid minimum fee payment of 1 USD/stock trade, hence looking to hit a delta that causes at least an equity fee of 1 USD during hedding and minimizes an absolute delta increase.
            /// So the target delta is +/-200.
            /// For more expensive stocks, wouldn't want to increase equity position too quickly, hence not exceed 5k long position. configurable
            /// 
            if (ManualOrderInstructionBySymbol.ContainsKey(symbol.Value) && Cfg.ExecuteManualOrderInstructions)
            {
                ManualOrderInstruction manualOrderInstruction = ManualOrderInstructionBySymbol[symbol.Value];
                return manualOrderInstruction.TargetQuantity - Portfolio[symbol].Quantity;
            }
            else if (TargetHoldings.ContainsKey(symbol))
            {
                absQuantity = Math.Abs(QuantityToTargetHolding(symbol));
                // respect delta. Only up to 100 deltaUSD per 1% of stock price.
                var ocw = OptionContractWrap.E(this, (Option)Securities[symbol], Time.Date);
                double delta1PcUsd = ocw.DeltaXBpUSD(MidIV(symbol), 100);  // Ideally replace 100BP (1% move) with a underlying volatility derived metric
                double maxDeltaUsd = Cfg.MaxDelta100BpUSDSignalQuantity.TryGetValue(Underlying(symbol), out maxDeltaUsd) ? maxDeltaUsd : Cfg.MaxDelta100BpUSDSignalQuantity[CfgDefault];
                decimal maxDeltaQuantity = ToDecimal(Math.Floor(Math.Abs(maxDeltaUsd / delta1PcUsd)));

                absQuantity = Math.Min(absQuantity, maxDeltaQuantity);
            }
            else
            {
                absQuantity = new HashSet<decimal>() {
                    maxOptionOrderQuantity,
                    AbsMaxFeeMinimizingQuantity(symbol, orderDirection),  // This is not just fee minimizing, but putting a threshold on the equity position. That should be left to a risk based margin reducing model, eg, only increase margin in steps of 0.5k.
                    MaxQuantityByMarginConstraints(symbol, orderDirection),
                    MaxGammaRespectingQuantity(symbol, orderDirection),
                    MaxLongRespectingDeltaQuantity(symbol, orderDirection)
                }.Min();
            }

            absQuantity = Math.Round(Math.Min(Math.Max(absQuantity, 1), maxOptionOrderQuantity), 0);
            if (absQuantity == 0)
            {
                Log($"{Time} SignalQuantity is 0, not trading: {symbol} absquantity={absQuantity}, maxOptionOrderQuantity={maxOptionOrderQuantity}, QuantityToTargetHolding={QuantityToTargetHolding(symbol)}");
            }

            return DIRECTION2NUM[orderDirection] * absQuantity;
        }

        private static readonly HashSet<OrderStatus> skipOrderStatus = new() { OrderStatus.Canceled, OrderStatus.Filled, OrderStatus.Invalid, OrderStatus.CancelPending };

        public void InitializePositionsFromPortfolioHoldings()
        {
            // Setting internal positions from algo state. takes a min+ in debug
            Positions = new Dictionary<Symbol, Position>();
            foreach (var holding in Portfolio.Values.Where(x => securityTypeOptionEquity.Contains(x.Type)))
            {
                Log($"Initialized Position {holding.Symbol} with Holding: {holding}");
                // refactor: make this event driven. Publish a trade -> Trades and Positions are updated.
                Positions[holding.Symbol] = new(this, holding);
            }
        }
        public void InitializeTradesFromPortfolioHoldings()
        {
            foreach (var holding in Portfolio.Values.Where(x => securityTypeOptionEquity.Contains(x.Type) && x.Quantity != 0))
            {
                Log($"Added Trade from Holding {holding.Symbol} with Holding: {holding}");
                // refactor: make this event driven. Publish a trade -> Trades and Positions are updated.
                Trades[holding.Symbol] = new()
                {
                    new(this, holding)
                };
            }
        }
        public IEnumerable<BaseData> GetLastKnownPricesTradeOrQuote(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                symbol.ID.Symbol.Contains(Statics.Volatility)
                || !HistoryRequestValid(symbol)
                || HistoryProvider == null
                )
            {
                return Enumerable.Empty<BaseData>();
            }

            // Daily bars, irrespective of the subscription resolution: a day is one small row set
            // per warehouse table (cheap to fetch; the R2 handler's day cache answers repeats from
            // memory) and a daily close seeds the price for any resolution the contract runs at.
            // The retry does not change resolution either, it only looks further back.
            var result = new Dictionary<TickType, BaseData>();
            var dailyTradeBars = new Dictionary<DateTime, TradeBar>();
            Func<int, bool> requestData = period =>
            {
                var historyRequests = CreateBarCountHistoryRequests(new[] { symbol }, period, Resolution.Daily)
                    .Select(request =>
                    {
                        // force no fill forward behavior
                        request.FillForwardResolution = null;
                        return request;
                    })
                    // request only those tick types we didn't get the data we wanted
                    .Where(request => !result.ContainsKey(request.TickType))
                    .ToList();
                if (historyRequests.Count == 0)
                {
                    // this shouldn't happen but just in case
                    Error($"GetLastKnownPricesTradeOrQuote(): no history request was created for symbol {symbol} at {Time}");
                    return true;
                }
                foreach (var slice in History(historyRequests))
                {
                    for (var i = 0; i < historyRequests.Count; i++)
                    {
                        var historyRequest = historyRequests[i];
                        var data = slice.Get(historyRequest.DataType);
                        if (data.ContainsKey(symbol))
                        {
                            // keep the last data point per tick type
                            var point = (BaseData)data[symbol];
                            result[historyRequest.TickType] = point;

                            // remember the whole daily trade series for the fill model's volume average
                            if (point is TradeBar tradeBar)
                            {
                                dailyTradeBars[tradeBar.Time] = tradeBar;
                            }
                        }
                    }
                }
                // true when all history requests tick types have a data point
                return historyRequests.All(request => result.ContainsKey(request.TickType));
            };

            // Last 7 tradeable days first; an illiquid contract retries further back, same resolution.
            if (!requestData(1))
            {
                Log($"GetLastKnownPricesTradeOrQuote(): {symbol} no daily trade or quote point in the last 30 tradeable days.");
            }

            // Hand the daily trade series to the initializer's fill model (mean daily volume): one
            // fetch serves both the seed price and the volume average.
            if (dailyTradeBars.Count > 0)
            {
                SeedDailyTradeBars[symbol] = dailyTradeBars.Values.OrderBy(bar => bar.Time).ToList();
            }

            // return the data ordered by time ascending
            return result.Values.OrderBy(data => data.Time);
        }
        /// <summary>
        /// Reconcile QC Position with AMM Algo Position object
        /// </summary>
        public void InternalAudit(OrderEvent? orderEvent = null)
        {
            if (orderEvent?.IsAssignment == true)
            {
                Log($"{Time} InternalAudit: OrderEvent.IsAssignment. Not running InternalAudit as 2 sequential orderEvents adjust option as well as equity position.");
                return;
            }
            var qcPositions = Portfolio.Where(x => x.Value.Quantity != 0).ToDictionary(x => x.Key.ToString(), x => x.Value.Quantity.ToString(CultureInfo.InvariantCulture));
            var algoPositions = Positions.Where(x => x.Value.Quantity != 0).ToDictionary(x => x.Key.ToString(), x => x.Value.Quantity.ToString(CultureInfo.InvariantCulture));

            if (!qcPositions.OrderBy(x => x.Key).SequenceEqual(algoPositions.OrderBy(x => x.Key)))
            {
                Error($"{Time} Portfolio and Positions mismatch!\n" +
                    $"QC POSITIONS: {Humanize(qcPositions)}\n" +
                    $"ALGO POSTIONS: {Humanize(algoPositions)}");
                InitializePositionsFromPortfolioHoldings();
            }
        }

        /// <summary>
        /// Brokerage message event handler. This method is called for all types of brokerage messages.
        /// </summary>
        public override void OnBrokerageMessage(BrokerageMessageEvent messageEvent)
        {
            Log($"Brokerage meesage received - {messageEvent}");
        }

        public HedgingMode GetHedgingMode(Symbol symbol)
        {
            return HedgingModeMap[Cfg.HedgingMode.TryGetValue(Underlying(symbol).Value, out int hedgeMode) ? hedgeMode : Cfg.HedgingMode[CfgDefault]];
        }

        public TimeSpan TimeToMarketClose(Symbol underlying)
        {
            DateTime nextMarketClose = NextMarketClose.TryGetValue(underlying, out nextMarketClose) ? nextMarketClose : GetNextMarketClose(underlying);
            return nextMarketClose.TimeOfDay - Time.TimeOfDay;
        }

        /// <summary>
        /// Release date is referred to as last trading session before earnings release, so adding a day.
        /// </summary>
        /// <param name="underlying"></param>
        /// <param name="days"></param>
        /// <returns></returns>
        public bool IsAfterEarningsRelease(Symbol underlying, int days = 30)
        {
            DateTime prevReleaseDate = PreviouReleaseDate(underlying);
            return Time.Date > prevReleaseDate.Date && Time.Date <= (prevReleaseDate + TimeSpan.FromDays(days)).Date;
        }

        public bool IsReleaseDate(Symbol underlying)
        {
            DateTime releaseDate = NextReleaseDate(underlying);
            return Time.Date == releaseDate.Date;
        }

        public bool IsPreparingEarningsRelease(Symbol underlying)
        {
            DateTime releaseDate = NextReleaseDate(underlying);
            int prepDays = Cfg.PrepareEarningsPeriodDays.TryGetValue(underlying, out prepDays) ? prepDays : Cfg.PrepareEarningsPeriodDays[CfgDefault] - 1;
            prepDays = Math.Max(prepDays, 0);
            return Time.Date >= releaseDate - TimeSpan.FromDays(prepDays) && Time.Date <= releaseDate;
        }
        public bool IsReducingAbsEquityPosition(Symbol underlying)
        {
            TimeRange timeRange = AlgoConfig.GetTimeRange(AlgoConfig.GetEntry(Cfg.TimeRangeReduceAbsDeltaPosition, Underlying(underlying).Value));
            return IsReleaseDate(underlying) && timeRange.Start <= Time.TimeOfDay && Time.TimeOfDay <= timeRange.End;
        }
        public decimal QuantityToTargetHolding(Symbol symbol)
        {
            if (TargetHoldings.TryGetValue(symbol, out decimal targetQuantity))
            {
                return targetQuantity - Portfolio[symbol].Quantity;
            }
            return 0;
        }

        public OrderTicket? GetOrderTicket(Symbol symbol, OrderType orderType)
        {
            return orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets) ? tickets.FirstOrDefault(t => t.OrderType == orderType) : null;
        }
    }
}
