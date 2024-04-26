using System;
using System.IO;
using System.Linq;
using Accord.Math;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Data.Consolidators;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data;
using Trade = QuantConnect.Algorithm.CSharp.Core.Risk.Trade;
using QuantConnect.Util;
using QuantConnect.Brokerages;
using System.Globalization;
using System.Collections.Concurrent;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using Newtonsoft.Json;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Scheduling;

namespace QuantConnect.Algorithm.CSharp.Core
{

    public partial class Foundations : QCAlgorithm
    {
        public SecurityInitializerMine securityInitializer;
        public Resolution resolution;
        public Dictionary<Symbol, QuoteBarConsolidator> QuoteBarConsolidators = new();
        public Dictionary<Symbol, TradeBarConsolidator> TradeBarConsolidators = new();
        public List<OrderEvent> OrderEvents = new();
        public Dictionary<Symbol, List<OrderTicket>> orderTickets = new();
        public HashSet<string> optionTicker;
        public HashSet<string> liquidateTicker;
        public HashSet<string> ticker;
        public HashSet<Symbol> equities = new();
        public HashSet<Symbol> options = new();  // Canonical symbols
        public MMWindow mmWindow;
        public Symbol symbolSubscribed;
        public Dictionary<Symbol, SecurityCache> PriceCache = new();
        public SecurityExchangeHours SecurityExchangeHours;
        private DateTime endOfDay;

        public Dictionary<Symbol, IVQuoteIndicator> IVBids = new();
        public Dictionary<Symbol, IVQuoteIndicator> IVAsks = new();
        public Dictionary<Symbol, IVSurfaceRelativeStrike> IVSurfaceRelativeStrikeBid = new();
        public Dictionary<Symbol, IVSurfaceRelativeStrike> IVSurfaceRelativeStrikeAsk = new();
        //public Dictionary<(Symbol, OptionRight), IVSurfaceAndreasenHuge> IVSurfaceAndreasenHuge = new();
        public Dictionary<int, IUtilityOrder> OrderTicket2UtilityOrder = new();

        // Begin Used by ImpliedVolaExporter - To be moved over there....
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVBid = new();
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVAsk = new();
        public Dictionary<Symbol, IVTrade> IVTrades = new();
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVTrade = new();
        public Dictionary<Symbol, PutCallRatioIndicator> PutCallRatios = new();
        public Dictionary<(Symbol, decimal), UnderlyingMovedX> UnderlyingMovedX = new();
        public Dictionary<Symbol, ConsecutiveTicksTrend> ConsecutiveTicksTrend = new();
        public Dictionary<Symbol, IntradayIVDirectionIndicator> IntradayIVDirectionIndicators = new();
        public Dictionary<Symbol, AtmIVIndicator> AtmIVIndicators = new();
        public Dictionary<Symbol, HashSet<Regime>> ActiveRegimes = new();
        public ConcurrentDictionary<Symbol, List<PositionSnap>> PositionSnaps = new();
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
        public Dictionary<string, DividendMine[]> DividendSchedule;
        public Dictionary<string, double> DividendYield;
        public Dictionary<string, ManualOrderInstruction> ManualOrderInstructionBySymbol;
        public Dictionary<string, EarningsAnnouncement[]> EarningsBySymbol;
        public FoundationsConfig Cfg;
        public string FoundationsConfigFileName = "FoundationsConfig.json";

        public Dictionary<Symbol, RiskDiscount> AbsoluteDiscounts = new();

        public Dictionary<Symbol, RiskProfile> RiskProfiles = new();
        public Dictionary<Symbol, GammaScalper> GammaScalpers = new();
        public Dictionary<Symbol, UtilityWriter> UtilityWriters = new();
        public Dictionary<Symbol, OrderEventWriter> OrderEventWriters = new();
        public RealizedPositionWriter RealizedPositionWriter;
        public Dictionary<int, Quote<Option>> Quotes = new();
        public Dictionary<int, double> OrderIdIV = new();
        public ConcurrentDictionary<Symbol, List<Position>> PositionsRealized = new();

        public HashSet<Symbol> embargoedSymbols = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
        public HashSet<OrderStatus> orderCanceledOrPending = new() { OrderStatus.CancelPending, OrderStatus.Canceled };
        public HashSet<OrderStatus> orderFilledCanceledInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid };
        public HashSet<OrderStatus> orderPartialFilledCanceledPendingInvalid = new() { OrderStatus.PartiallyFilled, OrderStatus.Filled, OrderStatus.CancelPending, OrderStatus.Canceled, OrderStatus.Invalid };
        public HashSet<OrderStatus> orderSubmittedPartialFilledUpdated = new() { OrderStatus.PartiallyFilled, OrderStatus.Submitted, OrderStatus.UpdateSubmitted };
        public HashSet<SecurityType> securityTypeOptionEquity = new() { SecurityType.Equity, SecurityType.Option };
        public HashSet<OrderType> orderTypeMarketLimit = new() { OrderType.Market, OrderType.Limit };
        public record MMWindow(TimeSpan Start, TimeSpan End);
        Func<Option, decimal> IntrinsicValue;
        public Dictionary<Symbol, DateTime> SignalsLastRun = new();
        public Dictionary<Symbol, bool> IsSignalsRunning = new();
        public readonly ConcurrentQueue<Signal> _signalQueue = new();
        public int ocaGroupId;
        public int SignalQuantityDflt = 9999;
        public Dictionary<string, decimal> TargetHoldings = new();
        protected IUtilityOrderFactory UtilityOrderFactory;
        public Dictionary<Symbol, double> LastDeltaAcrossDs = new();
        public readonly ConcurrentDictionary<Symbol, double> MarginalWeightedDNLV = new();
        public ConcurrentDictionary<Symbol, ConcurrentDictionary<OrderDirection, Sweep>> SweepState = new();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public void InitializeAlgo(IUtilityOrderFactory utilityOrderFactory)
        {
            UtilityOrderFactory = utilityOrderFactory;
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 10;
            Portfolio.MarginCallModel = MarginCallModel.Null;

            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText(Path.Combine(Globals.DataFolder, "symbol-properties", "EarningsAnnouncements.json")));
            DividendYield = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(Path.Combine(Globals.DataFolder, "symbol-properties", "DividendYields.json")));
            DividendSchedule = JsonConvert.DeserializeObject<Dictionary<string, DividendMine[]>>(File.ReadAllText("DividendSchedule.json"));

            // To be handled with API.Essentially get in realtime positions out of algo and ingest orders in realtime
            ManualOrderInstructionBySymbol = JsonConvert.DeserializeObject<ManualOrderInstruction[]>(File.ReadAllText("ManualOrderInstructions.json")).GroupBy(x => x.Symbol).ToDictionary(g => g.Key, g => g.First());
            EarningsBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            mmWindow = new MMWindow(new TimeSpan(9, 31, 00), new TimeSpan(16, 0, 0) - ScheduledEvent.SecurityEndOfDayDelta - TimeSpan.FromMinutes(5));  // 10mins before EOD market close events fire

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);

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
                }
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) => RunSignals(e);
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += (sender, e) => SnapPositions();
                UnderlyingMovedX[(equity.Symbol, 0.002m)].UnderlyingMovedXEvent += RiskProfiles[equity.Symbol].OnDS;
            }
            RealizedPositionWriter = new(this);

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 120), ExerciseOnExpiryDate);

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
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

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

        public override void OnSecuritiesChanged(SecurityChanges changes)
        {
            changes.AddedSecurities.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                securityInitializer.RegisterIndicators((Option)sec);
            });
            //changes.RemovedSecurities.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            //{
            //    Option option = (Option)sec;
            //    IVSurfaceAndreasenHuge[(option.Symbol.Underlying, option.Right)].UnRegisterSymbol(option);
            //});
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
                    if (ContractScopedForSubscription(symbol, lastClose, Cfg.ScopeContractStrikeOverUnderlyingMargin))
                    {
                        var item = AddData<VolatilityBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        item.IsTradable = false;

                        // This line requests quite a bit of past data. Minute and second resolution for a whole month into past.
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
            // embargoedSymbols = Securities.Keys.Where(s => EarningsAnnouncements.Where(ea => ea.Symbol == s.Underlying && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()).ToHashSet();

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
            //IVSurfaceAndreasenHuge.Values.DoForEach(s => s.WriteCsvRows());
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
                _signalQueue.Clear(); // Move out
                signals.DoForEach(s => _signalQueue.Enqueue(s));
                ConsumeSignal();
            }
        }

        public double MidIV(Symbol symbol, double defaultSpread = 0.005)
        {
            if (symbol.SecurityType != SecurityType.Option) return 0;

            double bidIV = IVBids[symbol].IVBidAsk.IV;
            double askIV = IVAsks[symbol].IVBidAsk.IV;
            return InterpolateMidIVIfAnyZero(bidIV, askIV, defaultSpread);
        }

        //public double IVAH(Symbol symbol)
        //{
        //    if (symbol.SecurityType != SecurityType.Option) return 0;

        //    IVSurfaceAndreasenHuge ivSurfaceAndreasenHuge = IVSurfaceAndreasenHuge[(Underlying(symbol), symbol.ID.OptionRight)];
        //    return ivSurfaceAndreasenHuge.IV(symbol) ?? 0;
        //}

        public double MidIVEWMA(Symbol symbol, double defaultSpread = 0.005)
        {
            double bidIV = IVSurfaceRelativeStrikeBid[symbol.Underlying].IV(symbol) ?? 0;
            double askIV = IVSurfaceRelativeStrikeAsk[symbol.Underlying].IV(symbol) ?? 0;
            return InterpolateMidIVIfAnyZero(bidIV, askIV, defaultSpread);
        }

        public double ForwardIV(Symbol symbol, double defaultSpread = 0.005)
        {
            return MidIVEWMA(symbol);
        }
        public double InterpolateMidIVIfAnyZero(double bidIV, double askIV, double defaultSpread = 0.005)
        {
            if (bidIV == 0 && askIV == 0)
            {
                return 0;
            }
            else if (bidIV == 0)
            {
                return askIV - defaultSpread / 2;
            }
            else if (askIV == 0)
            {
                return bidIV + defaultSpread / 2;
            }
            else
            {
                return (bidIV + askIV) / 2;
            }
        }

        public double AtmIVEWMA(Symbol symbol, double defaultSpread = 0.005)
        {
            return InterpolateMidIVIfAnyZero(
                IVSurfaceRelativeStrikeBid[Underlying(symbol)].AtmIvEwma(),
                IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIvEwma(), 
                defaultSpread
                );
        }

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
                        $"CancelRequestsUnprocessed: Count={Transactions.CancelRequestsUnprocessed.Count()}, OrderId={string.Join(", ", Transactions.CancelRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"SubmitRequestsUnprocessed: Count={Transactions.SubmitRequestsUnprocessed.Count()}, OrderId={string.Join(", ", Transactions.SubmitRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"UpdateRequestsUnprocessed: Count={Transactions.UpdateRequestsUnprocessed.Count()}");
                    }                    
                    AlertLateOrderRequests();
                    return;
                }

                if (_signalQueue.TryDequeue(out Signal signal))
                {
                    SubmitSignal(signal);
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
        public void SubmitSignal(Signal signal)
        {
            if (signal == null)
            {
                Log($"{Time} SubmitSignal: signal is null");
                return;
            }
            // Order desired tickets
            bool anyTickets = orderTickets.TryGetValue(signal?.Symbol, out List<OrderTicket> tickets) && tickets.Any();
            if (anyTickets || orderPartialFilledCanceledPendingInvalid.Contains(OcaGroupStatus(signal.OcaGroup)))
            {
                if (anyTickets)
                {
                    Log($"{Time} SubmitSignal: Not submitting signal={signal}, symbol={signal.Symbol} because ticketsCnt={tickets.Count}, tickets={string.Join(",", tickets)}, OcaGroupStatus={OcaGroupStatus(signal.OcaGroup)}");
                }
                else
                {
                    Log($"{Time} SubmitSignal: Not submitting signal={signal}, symbol={signal.Symbol} because OcaGroupStatus={OcaGroupStatus(signal.OcaGroup)}");
                }
                // Either already have a ticket. No problem / ok.

                // Or cancelation pending. In this case. register a callback to order the desired ticket, once canceled, comes as orderEvent.
                // EventDriven : On Cancelation, place opposite direction order if any in Signals.
                // TBCoded

                // Or OCA group has already been canceled
                return;
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
            // Get last trade for this symbol. Hacky. To avoid getting a PnL from this, but rather just modifying the quantity of an existing equity position, setting all prices to trade0.Mid0Underlying => PL_Delta 0.
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
                    //Positions.Remove(trade.Symbol);
                    RemoveSecurity(trade.Symbol);
                    return;
                }

                if (!Positions.ContainsKey(trade.Symbol))
                {
                    // Brand new position                    
                    Positions[trade.Symbol] = new(null, trade, this);                    
                }
                else
                {
                    // Trade modifies / operates on existing position.
                    Positions[trade.Symbol] = new(Positions[trade.Symbol], trade, this);
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
            if (!PriceCache.ContainsKey(symbol))
            {
                return false;
            }
            return PriceCache[symbol].BidPrice != Securities[symbol].BidPrice ||
                Securities[symbol].AskPrice != Securities[symbol].AskPrice;
        }
        public bool ContractScopedForNewPosition(Security security)
        {
            var sec = security;
            return sec.IsTradable && 
                (
                    (
                    // to be review with Gamma hedging. Selling option at ultra-high, near-expiry IVs with great gamma hedge could be extra profitable.
                    (sec.Symbol.ID.Date - Time.Date).Days > 1  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                    && !sec.Symbol.IsCanonical()
                    && sec.BidPrice != 0
                    && sec.AskPrice != 0

                    // price is not stale. Bit inefficient here. May rather have an indicator somewhere.
                    //&& PriceCache.ContainsKey(sec.Symbol)
                    //&& PriceCache[sec.Symbol].GetData().EndTime > Time - TimeSpan.FromMinutes(5)

                    && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                    && sec.Symbol.ID.StrikePrice >= MidPrice(sec.Symbol.Underlying) * (Cfg.ScopeContractStrikeOverUnderlyingMinSignal)
                    && sec.Symbol.ID.StrikePrice <= MidPrice(sec.Symbol.Underlying) * (Cfg.ScopeContractStrikeOverUnderlyingMaxSignal)
                    && (
                        ((Option)sec).GetPayOff(MidPrice(sec.Symbol.Underlying)) < Cfg.ScopeContractMoneynessITM * MidPrice(sec.Symbol.Underlying) || (
                            orderTickets.ContainsKey(sec.Symbol) &&
                            orderTickets[sec.Symbol].Count > 0 &&
                            ((Option)sec).GetPayOff(MidPrice(sec.Symbol.Underlying)) < (Cfg.ScopeContractMoneynessITM + 0.05m) * MidPrice(sec.Symbol.Underlying)
                        )
                    )
                    && !liquidateTicker.Contains(sec.Symbol.Underlying.Value)  // No new orders, Function oppositeOrder & hedger handle slow liquidation at decent prices.
                    && IVSurfaceRelativeStrikeBid[Underlying(sec.Symbol)].IsReady(sec.Symbol)
                    && IVSurfaceRelativeStrikeAsk[Underlying(sec.Symbol)].IsReady(sec.Symbol)
                //&& symbol.ID.StrikePrice > 0.05m != 0m;  // Beware of those 5 Cent options. Illiquid, but decent high-sigma underlying move protection.
                )
                ||
                (
                    !(sec.Symbol.ID.Date <= Time.Date)
                    && Portfolio[sec.Symbol].Quantity != 0  // Need to exit eventually
                )
                || ManualOrderInstructionBySymbol.ContainsKey(sec.Symbol.Value)
                || TargetHoldings.ContainsKey(sec.Symbol.Value)
            );
        }

        /// <summary>
        /// Signals. Securities where we assume risk. Not necessarily same as positions or subscriptions.
        /// </summary>
        public List<Signal> GetDesiredOrders(Symbol underlying)
        {
            var scopedOptions = Securities.Values.Where(s => 
                s.Type == SecurityType.Option && 
                Underlying(s.Symbol) == underlying && 
                ContractScopedForNewPosition(s)
            );
            Dictionary<(Symbol, int), string> ocaGroupByUnderlyingDelta = new()
            {
                [(underlying, -1)] = NewOcaGroupId(),
                [(underlying, 0)] = NewOcaGroupId(),
                [(underlying, +1)] = NewOcaGroupId()
            };

            List<Signal> signals = new();
            foreach (Security sec in scopedOptions)
            {
                // BuySell Distinction is insufficient. Scenario: We are delta short, gamma long. Would only want to buy/sell options reducing both, unless the utility is calculated better to compare weight 
                // beneficial risk and detrimental risk against each other. That's what the RiskDiscounts are for.

                Option option = (Option)sec;
                Symbol symbol = sec.Symbol;

                IUtilityOrder utilBuy = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, OrderDirection.Buy), option.BidPrice);
                IUtilityOrder utilSell = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, OrderDirection.Sell), option.AskPrice);

                double delta = OptionContractWrap.E(this, option, Time.Date).Delta(MidIV(symbol));
                string ocaGroupId = ocaGroupByUnderlyingDelta[(option.Underlying.Symbol, Math.Sign(delta))];

                double minUtility = Cfg.MinUtility.TryGetValue(underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
                // Utility from Risk and Profit are not normed and cannot be compared directly. Risk is not in USD. UtilProfitVega can change very frequently whenever market IV whipsaws around the EWMA.
                if (utilSell.Utility >= minUtility && utilSell.Utility >= utilBuy.Utility)
                {
                    signals.Add(new Signal(symbol, OrderDirection.Sell, utilSell, ocaGroupId));
                }
                else if (utilBuy.Utility >= minUtility && utilBuy.Utility > utilSell.Utility)
                {
                    signals.Add(new Signal(symbol, OrderDirection.Buy, utilBuy, ocaGroupId));
                }
            }

            decimal targetMarginAsFractionOfNLV = Cfg.TargetMarginAsFractionOfNLV.TryGetValue(underlying.Value, out targetMarginAsFractionOfNLV) ? targetMarginAsFractionOfNLV : Cfg.TargetMarginAsFractionOfNLV[CfgDefault];
            decimal marginExcessTarget = Math.Max(0, InitialMargin() - Portfolio.TotalPortfolioValue * targetMarginAsFractionOfNLV);
            if (marginExcessTarget > 0)
            {
                Log($"{Time} GetDesiredOrders: {underlying} initialMargin={InitialMargin()} exceeded by marginExcessTarget={marginExcessTarget}.");
            }

            var filteredSignals = signals.Where(s => s.Symbol.Underlying == underlying).ToList();
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
            return signals;
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
        /// Cancels undesired orders, places desired orders. In a separate thread, because would only want to place new orders, once all cancelations have been confirmed and order placement will be done in batches to not have tickets dangling in processing/unprocessed state.
        /// </summary>
        public void HandleDesiredOrders(IEnumerable<Signal> signals)
        {
            foreach (var group in signals.GroupBy(s => s.Symbol.Underlying))
            {
                // Cancel any undesired option ticket.
                var underlying = group.Key;
                var symbolDirectionToOrder = group.Select(s => (s.Symbol, s.OrderDirection)).ToList();
                var ticketsToCancel = orderTickets.ToList().
                    Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).
                    SelectMany(kvp => kvp.Value).
                    Where(t => !symbolDirectionToOrder.Contains((t.Symbol, Num2Direction(t.Quantity)))
                            && !orderCanceledOrPending.Contains(t.Status)).
                    ToList();
                if (ticketsToCancel.Any())
                {
                    var orderIDs = string.Join(", ", ticketsToCancel.Select(t => t.OrderId).ToList());
                    Log($"{Time} HandleDesiredOrders. Canceling {ticketsToCancel.Count} tickets: {orderIDs}");
                    ticketsToCancel.ForEach(t => Cancel(t));
                }

                // Sort the signals. Risk reducing first, then risk accepting. Can be done based on their UtilMargin. The larger the safer.
                var signalByUnderlying = group.OrderByDescending(g => g.UtilityOrder.UtilityMargin).ToList();

                // ignore signal where there is already an active limit order
                var activeLimitOrders = Transactions.GetOpenOrders().Where(o => o.Type == OrderType.Limit && o.Symbol.SecurityType == SecurityType.Option && o.Symbol.Underlying == underlying).ToList();
                signalByUnderlying = signalByUnderlying.Where(s => !activeLimitOrders.Any(o => o.Symbol == s.Symbol)).ToList();
                AddSignals(signalByUnderlying);
            }
        }

        public string NewOcaGroupId()
        {
            string ocaGroupNm = $"oco-{Time:yyMMddHHmmss}-{ocaGroupId++}";
            return ocaGroupNm;
        }

        /// <summary>
        /// Event driven: On MarketOpen ok, OnFill ok. On underlying moves 0.1% ok. at least every x 5mins. Every call restarts the timer.ok.
        /// </summary>
        public void RunSignals(Symbol? symbol = null)
        {
            var underlyings = symbol == null ? Securities.Values.Where(s => s.Type == SecurityType.Option).Select(s => s.Symbol.Underlying).Distinct().ToList() : new List<Symbol>() { Underlying(symbol) };
            foreach (Symbol underlying in underlyings)
            {
                if (IsSignalsRunning[underlying] || IsWarmingUp || !IsMarketOpen(underlying) || Time.TimeOfDay <= mmWindow.Start || Time.TimeOfDay >= mmWindow.End) continue;
                if (!OnWarmupFinishedCalled)
                {
                    OnWarmupFinished();
                }
                Log($"{Time} RunSignals. underlying={underlying}");
                IsSignalsRunning[underlying] = true;  // if refactored as task, more elegant? Just run 1 task at a time...
                bool skipRunSignals = Cfg.SkipRunSignals.TryGetValue(underlying.Value, out skipRunSignals) ? skipRunSignals : Cfg.SkipRunSignals[CfgDefault];
                if (!skipRunSignals)
                {
                    HandleDesiredOrders(GetDesiredOrders(underlying));
                }                
                IsSignalsRunning[underlying] = false;
                SignalsLastRun[underlying] = Time;
            }
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
                QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "action", $"CancelOpenTickets. Canceling {t.Symbol} OCAGroup/Type: {t.OcaGroup}/{t.OcaType}. EndOfDay" } });
                Cancel(t);
            }
        }

        public void LogRiskSchedule()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            LogPositions();
            LogRisk();
            LogPnL();
            LogOrderTickets();
            Log($"{Time} LogRiskSchedule. IsMarketOpen(symbolSubscribed)={IsMarketOpen(symbolSubscribed)}, symbolSubscribed={symbolSubscribed}");
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
                && IsLiquid(symbol, Cfg.ScopeContractIsLiquidDays, Resolution.Daily)
                ) 
                || 
                (Portfolio.ContainsKey(symbol) && Portfolio[symbol].Quantity != 0)
                || ManualOrderInstructionBySymbol.ContainsKey(symbol.Value)
                || TargetHoldings.ContainsKey(symbol.Value
                );
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
            };
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
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"Not time to trade yet." } });
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
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an order ticket with same sign: OrderId={ticket.OrderId}. Status: {ticket.Status}. For now only want 1 order. Not processing" } });
                        return false;
                    }

                    if (ticket.Quantity * quantity <= 0)
                    {
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order: OrderId: {ticket.OrderId}. Status: {ticket.Status} Not processing..." } });
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
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"contract {symbol} is not in scope. Not trading..." } });
                RemoveUniverseSecurity(Securities[symbol]);
                return false;
            }

            if (Transactions.CancelRequestsUnprocessed.Count() >= Cfg.MinCancelRequestsUnprocessedBlockingSubmit && symbol.SecurityType != SecurityType.Equity)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"CancelRequests awaiting processing {Transactions.CancelRequestsUnprocessed.Count()}. Not submitting..." } });
                return false;
            }

            if (Transactions.SubmitRequestsUnprocessed.Count() >= Cfg.MinSubmitRequestsUnprocessedBlockingSubmit && symbol.SecurityType != SecurityType.Equity)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"SubmitRequest awaiting processing {Transactions.SubmitRequestsUnprocessed.Count()}. Not submitting..." } });
                return false;
            }

            // Protect against ordering on stale data. Especially dangerous when restarting the algo.
            if (IsPriceStale(symbol))
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"Price is stale. Not trading..." } });
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
                };
                orderTickets[orderTicket.Symbol].Add(orderTicket);
            };

            if (quote != null)
            {
                Quotes[orderTicket.OrderId] = quote;
            }
            OrderTicket2UtilityOrder[orderTicket.OrderId] = utilityOrder;

            // Occasionally limit orders dont get processed timely eventually hitting a timeout set to 15min by QC resulting in runtime error.
            // Therefore, checking frequently whether a ticket has been process - orderTicket.SubmitRequest.Status;
            // Expecting orderStatus to be at least Submitted. If not, cancel and allow algo to resubmit.

            // Something bad with this closure. If so, would also be bad during backtesting...
            int timeout = 30;
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(timeout)), () => CancelOrderTicketIfUnprocessed(orderTicket.OrderId, timeout));

            OrderEventWriters[Underlying(orderTicket.Symbol)].Write(orderTicket);
        }
        public void CancelOrderTicketIfUnprocessed(int orderId, int sec)
        {
            OrderTicket ticket = Transactions.GetOrderTicket(orderId);
            if (ticket?.CancelRequest == null && ticket.SubmitRequest.Status == OrderRequestStatus.Unprocessed)
            {
                Log($"{Time} CancelOrderTicketIfUnprocessed: {ticket.Symbol} Status: {ticket.Status} remained Unprocessed for {sec} sec after new submission. Canceling LeanId: {ticket.OrderId}");
                Cancel(ticket);
            }
            else if (ticket?.CancelRequest == null && $"{ticket.Status}" == $"{OrderStatus.New}" && $"{ticket.SubmitRequest.Status}" == $"{OrderRequestStatus.Error}")
            {
                // SubmitRequest.Status is initialized with error.
                // true even if log prints false, therefore evaluating as string now..
                // SubmitRequest.Status=Processed. 15. Remains in bad submission state after 52 seconds. Canceling.
                Log($"{Time} CancelOrderTicketIfUnprocessed: OrderTicket {ticket}. LeanId: {ticket.OrderId} {ticket.Symbol} {ticket.Quantity} {ticket.Status} SubmitRequest.Status={ticket.SubmitRequest.Status}. {ticket.SubmitRequest.OrderId}. Remains in bad submission state after {sec} seconds. Canceling.");
                Cancel(ticket);
            };
        }
        public void OrderEquity(Symbol symbol, decimal quantity, decimal limitPrice, string tag = "", OrderType orderType = OrderType.Market)
        {
            // Cancel any tickets ordering the opposite quantity


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
            if (Math.Round(quantity ,0) == 0) return null;

            if (!IsOrderValid(contract.Symbol, quantity)) { return null; }

            Quote<Option> quote = GetQuote(new QuoteRequest<Option>(contract, quantity, signal.UtilityOrder));
            decimal limitPrice = quote.Price;
            if (limitPrice == 0)
            {
                Log($"No price quoted for {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading...");
                return null;
            }
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));
            if (limitPrice == 0 || limitPrice < TickSize(contract.Symbol))
            {
                Log($"Invalid price: {limitPrice}. {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading... TickSize: {TickSize(contract.Symbol)}");
                return null;
            }

            limitPrice = Math.Min(limitPrice, contract.AskPrice);
            limitPrice = Math.Max(limitPrice, contract.BidPrice);

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

            SweepState[contract.Symbol][Num2Direction(orderTicket.Quantity)].ContinueSweep();

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
                UpdateLimitOrderOption(Securities[symbol] as Option);
            }
            else if (symbol.SecurityType == SecurityType.Equity)
            {
                UpdateLimitOrderEquity(Securities[symbol] as Equity);
            }
        }
        ///// <summary>
        ///// Not in use due to IB limiting number of simultaneous pegged orders.
        ///// </summary>
        ///// <param name="option"></param>
        //public void UpdatePeggedOrderOption(Option option)
        //{
        //    Symbol symbol = option.Symbol;
        //    foreach (OrderTicket ticket in orderTickets[symbol].ToList())
        //    {
        //        if (orderSubmittedPartialFilledUpdated.Contains(ticket.Status) && ticket.OrderType == OrderType.PeggedToStock && ticket.CancelRequest == null)
        //        {
        //            decimal tickSize = TickSize(symbol);
        //            double ticketIV = OrderIdIV.TryGetValue(ticket.OrderId, out ticketIV) ? ticketIV : 0;

        //            decimal orderQuantity = SignalQuantity(symbol, Num2Direction(ticket.Quantity));
        //            UtilityOrder utilityOrder = new(this, option, orderQuantity);
        //            Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, orderQuantity, utilityOrder));

        //            if (Math.Abs((decimal)quote.IVPrice - (decimal)ticketIV) < Cfg.MinimumIVOffsetBeforeUpdatingPeggedOptionOrder) { return; }

        //            decimal quoteStartingPrice = quote.Price;

        //            if (quoteStartingPrice == 0 || quote.Quantity == 0 || quote.IVPrice == 0)
        //            {
        //                Log($"{Time}: UpdateLimitPriceContract. Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. IVPrice={quote.IVPrice} Not trading...");
        //                Cancel(ticket);
        //                return;
        //            }
        //            quoteStartingPrice = RoundTick(quoteStartingPrice, tickSize);
        //            decimal ticketStartingPrice = ticket.Get(OrderField.StartingPrice);

        //            if (Math.Abs(quoteStartingPrice - ticketStartingPrice) >= tickSize && quoteStartingPrice >= tickSize)
        //            {
        //                if (quoteStartingPrice < tickSize)
        //                {
        //                    Log($"{Time}: CANCEL TICKET Symbol{symbol}: QuoteStartingPrice too small: {quoteStartingPrice}");
        //                    Cancel(ticket);
        //                }
        //                else
        //                {
        //                    var tag = $"{Time}: UPDATE TICKET Symbol {symbol} Price: From: {ticketStartingPrice} To: {quoteStartingPrice}";
        //                    var ocw = OptionContractWrap.E(this, option, Time.Date);
        //                    decimal delta = (decimal)Math.Abs(ocw.Delta(quote.IVPrice));
        //                    decimal gamma = (decimal)ocw.Gamma(quote.IVPrice);
        //                    var midPriceUnderlying = MidPrice(option.Underlying.Symbol);
        //                    var offset = delta * Cfg.PeggedToStockDeltaRangeOffsetFactor / gamma;
        //                    var underlyingRangeLow = midPriceUnderlying - offset;
        //                    var underlyingRangeHigh = midPriceUnderlying + offset;
        //                    var response = ticket.UpdatePeggedToStockOrder(
        //                        delta * 100m,
        //                        quoteStartingPrice,
        //                        midPriceUnderlying,
        //                        underlyingRangeLow,
        //                        underlyingRangeHigh,
        //                        tag
        //                        );
        //                    OrderIdIV[ticket.OrderId] = quote.IVPrice;
        //                    if (Cfg.LogOrderUpdates || LiveMode)
        //                    {
        //                        Log($"{tag}, Response: {response}");
        //                    }
        //                    Quotes[ticket.OrderId] = quote;
        //                }
        //            }

        //            // Quantity - low overhead. SignalQuantity needs risk metrics that are also fetched for getting a price and cached.
        //            if (ticket.Quantity != quote.Quantity && Math.Abs(ticket.Quantity - quote.Quantity) >= 2)
        //            {
        //                var tag = $"{Time}: UPDATE TICKET Symbol {symbol} Quantity: From: {ticket.Quantity} To: {quote.Quantity}";
        //                var response = ticket.UpdateQuantity(quote.Quantity, tag);
        //                if (Cfg.LogOrderUpdates || LiveMode)
        //                {
        //                    Log($"{tag}, Response: {response}");
        //                }
        //                Quotes[ticket.OrderId] = quote;
        //            }
        //        }
        //        else if (ticket.Status == OrderStatus.CancelPending) { }
        //        //else
        //        //{
        //        //    Log($"{Time} UpdateLimitPriceContract {option} ticket={ticket}, OrderStatus={ticket.Status} - Should not run this function for this ticket. Cleanup orderTickets.");
        //        //}
        //    }
        //}
        public DateTime NextReleaseDate(Symbol underlying, DateTime? dt=null)
        {
            var dfltReleaseDate = new DateTime(2000, 1, 1);
            if (EarningsBySymbol.TryGetValue(underlying, out var earningsBySymbol))
            {
                var eas = earningsBySymbol.Where(ea => ea.Date >= (dt ?? Time.Date));
                return eas.Any() ? eas.Select(ea => ea.Date).Min() : dfltReleaseDate;
            }
            return dfltReleaseDate;
        }
        public void UpdateLimitOrderOption(Option option)
        {
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
                        Cancel(ticket);
                        return;
                    }
                    IUtilityOrder utilityOrder = UtilityOrderFactory.Create(this, option, orderQuantity);
                    Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, orderQuantity, utilityOrder));

                    decimal idealLimitPrice = quote.Price;

                    if (idealLimitPrice == 0 || quote.Quantity == 0)
                    {
                        Log($"{Time}: UpdateLimitPriceContract. Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. Not trading...");
                        Cancel(ticket);
                        return;
                    }
                    idealLimitPrice = RoundTick(idealLimitPrice, tickSize);

                    // Dont undercut one's own order. I would recursively undercutting my own order paying 100% of spread.
                    if (
                        (Num2Direction(orderQuantity) == OrderDirection.Buy  && idealLimitPrice >= limitPrice && limitPrice >= option.BidPrice ) ||
                        (Num2Direction(orderQuantity) == OrderDirection.Sell && idealLimitPrice <= limitPrice && limitPrice <= option.BidPrice )
                        )
                    {
                        return;
                    }

                    // Price
                    if (Math.Abs(idealLimitPrice - limitPrice) >= tickSize && idealLimitPrice >= tickSize)
                    {
                        if (idealLimitPrice < tickSize)
                        {
                            Log($"{Time}: CANCEL LIMIT Symbol{symbol}: Price too small: {limitPrice}");
                            Cancel(ticket);
                        }
                        else
                        {
                            var tag = $"{Time}: UPDATE LIMIT Symbol {symbol}, OrderId: {ticket.OrderId}, OcaGroup/Type: {ticket.OcaGroup}/{ticket.OcaType}, Price: From: {limitPrice} To: {idealLimitPrice}";
                            var response = ticket.UpdateLimitPrice(idealLimitPrice, tag);
                            if (Cfg.LogOrderUpdates || LiveMode)
                            {
                                Log($"{tag}, Response: {response}, IsProcessed: {response.IsProcessed}");
                            }
                            Quotes[ticket.OrderId] = quote;
                        }
                    }

                    // Quantity - low overhead. SignalQuantity needs risk metrics that are also fetched for getting a price and cached.
                    if (ticket.Quantity != quote.Quantity && Math.Abs(ticket.Quantity - quote.Quantity) >= 2)
                    {
                        var tag = $"{Time}: UPDATE LIMIT Symbol {symbol}, OrderId: {ticket.OrderId}, OcaGroup/Type: {ticket.OcaGroup}/{ticket.OcaType}, Quantity: From: {ticket.Quantity} To: {quote.Quantity}";
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
            }
        }

        public OrderResponse? Cancel(OrderTicket ticket, string tag = "")
        {
            if (orderCanceledOrPending.Contains(ticket.Status)) return null;

            Log($"{Time} Cancel: {ticket.Symbol} OrderId={ticket.OrderId} Status={ticket.Status} {ticket}");
            var response = ticket.Cancel(tag);
            OrderEventWriters[Underlying(ticket.Symbol)].Write(ticket);
            return response;
        }

        public double HedgeVolatility(Symbol symbol)
        {
            switch (GetHedgingMode(symbol))
            {
                case HedgingMode.FwdRealizedVolatility:
                    return (double)Securities[Underlying(symbol)].VolatilityModel.Volatility;
                case HedgingMode.HistoricalVolatility:
                    return (double)Securities[Underlying(symbol)].VolatilityModel.Volatility;
                case HedgingMode.ImpliedVolatility:
                    return MidIV(symbol);
                case HedgingMode.ImpliedVolatilityAtm:
                    return AtmIV(symbol);
                case HedgingMode.ImpliedVolatilityEWMA:
                    return MidIVEWMA(symbol);
                default:
                    throw new NotImplementedException($"HedgingMode {GetHedgingMode(symbol)} not implemented");
            }
        }

        public Metric HedgeMetric(Symbol symbol)
        {
            switch (GetHedgingMode(symbol))
            {
                case HedgingMode.Zakamulin:
                case HedgingMode.FwdRealizedVolatility:
                case HedgingMode.HistoricalVolatility:
                    return Metric.DeltaTotal;
                case HedgingMode.ImpliedVolatility:
                    return Metric.DeltaImpliedTotal;
                case HedgingMode.ImpliedVolatilityAtm:
                    return Metric.DeltaImpliedAtmTotal;
                case HedgingMode.ImpliedVolatilityEWMA:
                    return Metric.DeltaImpliedEWMATotal;
                default:
                    return Metric.DeltaTotal;
            }
        }

        public Func<Symbol, double> FuncVolatility(VolatilityType volatilityType)
        {
            return volatilityType switch
            {
                VolatilityType.HVHedge => (symbol) => (double)Securities[Underlying(symbol)].VolatilityModel.Volatility,
                VolatilityType.IVMid => (symbol) => MidIV(symbol),
                VolatilityType.IVATM => (symbol) => AtmIV(symbol),
                VolatilityType.IVBid => (symbol) => IVBids[symbol].IVBidAsk.IV,
                VolatilityType.IVAsk => (symbol) => IVAsks[symbol].IVBidAsk.IV,
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
                    Log($"{Time} EquityHedgeQuantity: Market Orders present for {underlying} {orderedQuantityMarket} OrderId={string.Join(", ", marketOrders.Select(t => t.OrderId))}.");
                }
                Log($"{Time} EquityHedgeQuantity: DeltaTotal={deltaTotal}");//, deltaIVdSTotal={deltaIVdSTotal} (not used)");
            }

            return Math.Round(quantity, 0);
        }

        public void UpdateLimitOrderEquity(Equity equity, decimal? quantity = null)
        {
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
                    Log($"{Time}: CANCEL LIMIT Symbol{equity.Symbol}: Too many orders");
                    Cancel(ticket);
                    continue;
                }
                cnt++;

                quantity ??= Math.Round(EquityHedgeQuantity(equity.Symbol));

                decimal ts = TickSize(ticket.Symbol);
                decimal ticketPrice = ticket.Get(OrderField.LimitPrice);
                var orderType = GetEquityHedgeOrderType(equity, ticket);
                if (ticket.OrderType == OrderType.Limit && orderType == OrderType.Market)
                {
                    Log($"{Time} UpdateLimitOrderEquity: Requested OrderType: Market for live limit order. Turning limit into aggressive limit.");
                }
                idealLimitPrice = GetEquityHedgePrice(equity, orderType, quantity ?? 0, ticket);
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
                    var response = ticket.UpdateQuantity(quantity.Value, tag);
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
            HashSet<Regime> regimes = ActiveRegimes.TryGetValue(Underlying(symbol), out regimes) ? regimes : new HashSet<Regime>();
            if (regimes.Contains(Regime.SellEventCalendarHedge))
            {
                var totalGamma = PfRisk.RiskByUnderlying(symbol.Underlying, Metric.GammaTotal);
                var gammaOrder = (double)PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], Metric.GammaTotal);
                if (gammaOrder < 0)
                {
                    absQuantity = Math.Floor((decimal)Math.Abs((double)totalGamma / (double)gammaOrder));
                };
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

        public bool IsFrontMonthPosition(Position x) =>
                x.SecurityType == SecurityType.Option &&
                x.UnderlyingSymbol == x.Symbol.Underlying &&
                x.Quantity != 0 &&                
                IsElevatedIVExpiry(x.Symbol);

        public bool IsElevatedIVExpiry(Symbol symbol)
        {
            return symbol.ID.Date >= EventDate(symbol) && symbol.ID.Date < ExpiryEventImpacted(symbol).AddDays(Cfg.CalendarSpreadPeriodDays);
        }

        public decimal MaxRegimeRelatedQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            //return SignalQuantityDflt;  // CalendarSpreadQuantityToBuyBackMonth is not functional and needs more analysis first.
            HashSet<Regime> regimes = ActiveRegimes.TryGetValue(Underlying(symbol), out regimes) ? regimes : new HashSet<Regime>();
            if (regimes.Contains(Regime.SellEventCalendarHedge))
            {
                if (IsElevatedIVExpiry(symbol) || orderDirection == OrderDirection.Sell)
                {
                    decimal absDeltaFrontMonthCallsTotal = Math.Abs(PfRisk.RiskByUnderlying(symbol.Underlying, HedgeMetric(symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Call)));
                    decimal absDeltaFrontMonthPutsTotal = Math.Abs(PfRisk.RiskByUnderlying(symbol.Underlying, HedgeMetric(symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Put)));

                    decimal deltaSymbol = PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], HedgeMetric(symbol.Underlying));
                    return Math.Abs(absDeltaFrontMonthCallsTotal - absDeltaFrontMonthPutsTotal) / deltaSymbol;

                }
                else if (symbol.ID.Date.AddDays(60) > EventDate(symbol))
                {
                    return Math.Abs(CalendarSpreadQuantityToBuyBackMonth(symbol, orderDirection));
                }                
            }
            return SignalQuantityDflt;
        }
        // cache me
        public DateTime EventDate(Symbol symbol)
        {
            return DateTime.MaxValue;
            Symbol underlying = Underlying(symbol);
            if (!EarningsBySymbol.ContainsKey(underlying.Value) || !EarningsBySymbol[underlying.Value].Any()) return default(DateTime);

            return EarningsBySymbol[underlying.Value].Where(earningsAnnouncement => earningsAnnouncement.Date >= Time.Date).OrderBy(x => x.Date).FirstOrDefault().Date;
        }

        // cache me
        public DateTime ExpiryEventImpacted(Symbol symbol) => IVSurfaceRelativeStrikeAsk[Underlying(symbol)].Expiries().Where(expiry => expiry > EventDate(symbol)).OrderBy(expiry => expiry).FirstOrDefault();

        public IEnumerable<Position> ElevatedIVFrontMonthPositionsFilter(Symbol symbol)
        {
            Symbol underlying = Underlying(symbol);
            return Positions.Values.Where(x => 
                x.UnderlyingSymbol == symbol.Underlying && 
                x.Quantity != 0 && 
                x.SecurityType == SecurityType.Option &&
                x.Symbol.ID.Date >= EventDate(symbol) &&
                x.Symbol.ID.Date < ExpiryEventImpacted(symbol).AddDays(Cfg.CalendarSpreadPeriodDays)
            );
        }
        public decimal CalendarSpreadQuantityToBuyBackMonth(Symbol symbol, OrderDirection orderDirection)
        {
            double impliedMove = ImpliedMove(symbol);
            impliedMove = symbol.ID.OptionRight == OptionRight.Call ? impliedMove : -impliedMove;

            var optionRight = symbol.ID.OptionRight;
            var relevantPositions = Positions.Values.Where(x => x.UnderlyingSymbol == symbol.Underlying && x.Quantity != 0 && x.SecurityType == SecurityType.Option && x.Symbol.ID.OptionRight == optionRight).ToList();

            // front month position to replicate back month.
            Dictionary<Symbol, decimal> symbolFrontMonthQuantity = new();
            foreach (var position in relevantPositions.Where(x =>
                x.Symbol.ID.Date >= EventDate(symbol) &&
                x.Symbol.ID.Date < ExpiryEventImpacted(symbol).AddDays(Cfg.CalendarSpreadPeriodDays)// refactor to splitting by elevated IV, not days
                ))
            {
                symbolFrontMonthQuantity[position.Symbol] = position.Quantity;
            }
            decimal quantityFrontMonth = symbolFrontMonthQuantity.Sum(kvp => kvp.Value);

            // Back month existing offsetting positions
            Dictionary<Symbol, decimal> symbolQuantityBackMonth = new();
            foreach (var position in relevantPositions.Where(x =>
                x.Symbol.ID.Date > EventDate(symbol).AddDays(60)
                ))
            {
                symbolQuantityBackMonth[position.Symbol] = position.Quantity;
            }
            decimal quantityBackMonth = symbolQuantityBackMonth.Sum(kvp => kvp.Value);

            // Group by strike
            Dictionary<decimal, decimal> strikeQuantityBackMonth = new();
            foreach (var kvp in symbolQuantityBackMonth)
            {
                strikeQuantityBackMonth[kvp.Key.ID.StrikePrice] = strikeQuantityBackMonth.TryGetValue(kvp.Key.ID.StrikePrice, out decimal strikeQuantityBackMonthValue) ? strikeQuantityBackMonthValue + kvp.Value : kvp.Value;
            }

            // Net them to check whether this particular strike needs to be bought.
            decimal quantityBackMonthToBuy = -quantityBackMonth - quantityFrontMonth;

            // Add to this the loss of extrinsic value of the back month position after the implied move.            
            // Hedging with multiple back month expiries, each with different IVs and sensitivities to the underlying price.
            Option option = (Option)Securities[symbol];
            var ocw = OptionContractWrap.E(this, option, Time.Date);
            double iv0 = MidIV(symbol);
            ocw.SetIndependents(MidPrice(symbol.Underlying), MidPrice(symbol), iv0);
            double p0 = ocw.NPV(); // Fix that this is calculated for today, instead of EventDate
            ocw.SetIndependents(MidPrice(symbol.Underlying) + (decimal)impliedMove, MidPrice(symbol), iv0);
            double p1 = ocw.NPV();
            decimal extrinsicValueLossPerContract = (decimal)(p1 - p0 - impliedMove);
            decimal intrinsicValueGainPerContract = (decimal)impliedMove;
            decimal additionalQuantity = Math.Max(Math.Abs(quantityFrontMonth * extrinsicValueLossPerContract / intrinsicValueGainPerContract), 1);
            additionalQuantity = Math.Min(additionalQuantity, Math.Abs(quantityFrontMonth * 0.5m));
            quantityBackMonthToBuy += additionalQuantity;

            // avoid buying low delta options
            var ocwStrike = OptionContractWrap.E(this, option, Time.Date);
            double delta = ocwStrike.Delta(iv0);
            if (Math.Abs(delta) < 0.2)
            {
                return 0;
            }

            decimal currentCalendarHedgeRatio = quantityFrontMonth == 0 ? 0 : Math.Round(100 * quantityBackMonth / quantityFrontMonth, 0);
            decimal targetCalendarHedgeRatio = quantityFrontMonth == 0? 0 : Math.Round((100 * (Math.Abs(quantityFrontMonth) + additionalQuantity) / Math.Abs(quantityFrontMonth)), 0);
            Log($"{Time} CalendarSpreadQuantityToBuyBackMonth: {symbol}, quantityBackMonthToBuy={quantityBackMonthToBuy}, " +
                $"current/target calendarHedgeRatio={-currentCalendarHedgeRatio}% / {targetCalendarHedgeRatio}%, " +
                $"quantityFrontMonth={quantityFrontMonth}, quantityBackMonth={quantityBackMonth}, impliedMove={impliedMove}, additionalQuantity={additionalQuantity}");
            return DIRECTION2NUM[orderDirection] * quantityBackMonthToBuy > 0 ? quantityBackMonthToBuy : 0;
        }

        public double CalendarSpreadExpectedVegaProfit(Symbol symbol)
        {
            var optionRight = symbol.ID.OptionRight;
            var relevantPositions = Positions.Values.Where(x => x.UnderlyingSymbol == symbol.Underlying && x.Quantity != 0 && x.SecurityType == SecurityType.Option && x.Symbol.ID.OptionRight == optionRight).ToList();

            Dictionary<Symbol, decimal> symbolFrontMonthQuantity = new();
            foreach (var position in relevantPositions.Where(x =>
                x.Symbol.ID.Date >= EventDate(symbol) &&
                x.Symbol.ID.Date < ExpiryEventImpacted(symbol).AddDays(Cfg.CalendarSpreadPeriodDays)// refactor to splitting by elevated IV, not days
                ))
            {
                symbolFrontMonthQuantity[position.Symbol] = position.Quantity;
            }
            decimal quantityFrontMonth = symbolFrontMonthQuantity.Sum(kvp => kvp.Value);

            double expectedIV = (double)Securities[symbol.Underlying].VolatilityModel.Volatility;
            double expectedVegaGain = 0;
            foreach (var frontMonthSymbol in symbolFrontMonthQuantity.Keys)
            {
                double midIV = MidIV(frontMonthSymbol);
                if (midIV == 0) { continue; }
                Option option = (Option)Securities[frontMonthSymbol];
                var ocw = OptionContractWrap.E(this, option, Time.Date);
                ocw.SetIndependents(MidPrice(frontMonthSymbol.Underlying), MidPrice(frontMonthSymbol), midIV);
                double vega = ocw.Vega(midIV);

                // Favors selling skewed wings.
                expectedVegaGain += (expectedIV - midIV) * vega * (double)(quantityFrontMonth * option.ContractMultiplier);
            }
            return expectedVegaGain;
        }

        /// <summary>
        /// The implied move for an earnings release date has a dte of 1. For a period of days where high movement is expected, need to refactor this. Not planned so far.
        /// Stock impacting events also happen on weekdays, suggests 365 as denominator (theta calculating argument). But, stocks dont move on weekends...
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public double ImpliedMove(Symbol symbol)
        {
            int dte = 1;  // (IVSurfaceRelativeStrikeAsk[Underlying(symbol)].MinExpiry() - Time.Date).Days;
            if (dte < 0) return 0;
            double currentAtm = AtmIV(Underlying(symbol));
            return (double)MidPrice(Underlying(symbol)) * currentAtm * Math.Sqrt(dte) / Math.Sqrt(256);
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

        public decimal SignalQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absQuantity;
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

            if (TargetHoldings.ContainsKey(symbol))
            {
                absQuantity = Math.Abs(TargetHoldings[symbol] - Portfolio[symbol].Quantity);
            }
            else
            {
                decimal maxOptionOrderQuantity = Cfg.MaxOptionOrderQuantity.TryGetValue(Underlying(symbol).Value, out maxOptionOrderQuantity) ? maxOptionOrderQuantity : Cfg.MaxOptionOrderQuantity[CfgDefault];

                absQuantity = new HashSet<decimal>() {
                    maxOptionOrderQuantity,
                    AbsMaxFeeMinimizingQuantity(symbol, orderDirection),  // This is not just fee minimizing, but putting a threshold on the equity position. That should be left to a risk based margin reducing model, eg, only increase margin in steps of 0.5k.
                    MaxQuantityByMarginConstraints(symbol, orderDirection),
                    MaxGammaRespectingQuantity(symbol, orderDirection),
                    MaxLongRespectingDeltaQuantity(symbol, orderDirection)
                }.Min();
            }

            absQuantity = Math.Round(Math.Min(absQuantity, 1), 0);
            //if(absQuantity == 0)
            //{
            //    Log($"{Time} SignalQuantity: {symbol} absQuantity=0. Not trading.");
            //}

            return DIRECTION2NUM[orderDirection] * absQuantity;
        }

        private static readonly HashSet<OrderStatus> skipOrderStatus = new() { OrderStatus.Canceled, OrderStatus.Filled, OrderStatus.Invalid, OrderStatus.CancelPending };

        public void InitializePositionsFromPortfolioHoldings()
        {
            // Setting internal positions from algo state.
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
                symbol.ID.Symbol.Contains(Statics.VolatilityBar)
                || !HistoryRequestValid(symbol)
                || HistoryProvider == null
                )
            {
                return Enumerable.Empty<BaseData>();
            }

            var result = new Dictionary<TickType, BaseData>();
            Resolution? resolution = null;
            Func<int, bool> requestData = period =>
            {
                var historyRequests = CreateBarCountHistoryRequests(new[] { symbol }, period)
                    .Select(request =>
                    {
                        // For speed and memory usage, use Resolution.Minute as the minimum resolution
                        request.Resolution = (Resolution)Math.Max((int)Resolution.Minute, (int)request.Resolution);
                        // force no fill forward behavior
                        request.FillForwardResolution = null;

                        resolution = request.Resolution;
                        return request;
                    })
                    // request only those tick types we didn't get the data we wanted
                    .Where(request => !result.ContainsKey(request.TickType))
                    .ToList();
                foreach (var slice in History(historyRequests))
                {
                    for (var i = 0; i < historyRequests.Count; i++)
                    {
                        var historyRequest = historyRequests[i];
                        var data = slice.Get(historyRequest.DataType);
                        if (data.ContainsKey(symbol))
                        {
                            // keep the last data point per tick type
                            result[historyRequest.TickType] = (BaseData)data[symbol];
                        }
                    }
                }
                // true when all history requests tick types have a data point
                return historyRequests.All(request => result.ContainsKey(request.TickType));
            };

            if (!requestData(Periods(Resolution.Minute, days: 1)))
            {
                if (resolution.HasValue)
                {
                    // If the first attempt to get the last know price returns null, it maybe the case of an illiquid security.
                    // Use Quote data to return MidPrice
                    var periods = Periods(Resolution.Minute, days: 5);
                    requestData(periods);
                }
                else
                {
                    // this shouldn't happen but just in case
                    Error($"QCAlgorithm.GetLastKnownPrices(): no history request was created for symbol {symbol} at {Time}");
                    Log(Environment.StackTrace);
                }
            }
            // return the data ordered by time ascending
            return result.Values.OrderBy(data => data.Time);
        }
        /// <summary>
        /// Reconcile QC Position with AMM Algo Position object
        /// </summary>
        public void InternalAudit(OrderEvent? orderEvent=null)
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
