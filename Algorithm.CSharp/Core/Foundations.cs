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
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Indicators;
using MathNet.Numerics.LinearAlgebra.Factorization;
using QuantConnect.Algorithm.CSharp.Core.Utils;

namespace QuantConnect.Algorithm.CSharp.Core
{

    public partial class Foundations : QCAlgorithm
    {
        public SecurityInitializerMine securityInitializer;
        public Resolution resolution;
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
        public SecurityExchangeHours SecurityExchangeHours;
        private DateTime endOfDay;

        public Dictionary<Symbol, IVQuoteIndicator> IVBids = new();
        public Dictionary<Symbol, IVQuoteIndicator> IVAsks = new();
        public Dictionary<Equity, IIVSurface> IVSurfaceSSVIMid = new();
        
        public Dictionary<int, IUtilityOrder> OrderTicket2UtilityOrder = new();
        public RiskScenerioHandler RiskScenarioHandler;

        // Begin Used by ImpliedVolaExporter - To be moved over there....
        // public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVBid = new();
        // public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVAsk = new();
        // public Dictionary<Symbol, IVTrade> IVTrades = new();
        // public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVTrade = new();
        public Dictionary<Symbol, PutCallRatioIndicator> PutCallRatios = new();
        public Dictionary<(Symbol, decimal), UnderlyingMovedX> UnderlyingMovedX = new();
        //public Dictionary<Symbol, ConsecutiveTicksTrend> ConsecutiveTicksTrend = new();
        public Dictionary<Symbol, IntradayIVDirectionIndicator> IntradayIVDirectionIndicators = new();
        //public Dictionary<Symbol, AtmIVIndicator> AtmIVIndicators = new();
        public Dictionary<Symbol, HashSet<Regime>> ActiveRegimes = new();
        public ConcurrentDictionary<Symbol, List<PositionSnap>> PositionSnaps = new();
        public ConcurrentDictionary<Symbol, List<Quote>> MarketDataQuotes = new();
        public ConcurrentDictionary<Symbol, List<Core.IO.Trade>> MarketDataTrades = new ();
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
        public HashSet<OrderStatus> orderFilledCanceledCancelPendingInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.CancelPending, OrderStatus.Invalid };
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
        public Dictionary<Symbol, bool> IsSignalsRunning = new();
        public readonly ConcurrentQueue<Signal> _signalQueue = new();
        public int ocaGroupId;
        public int SignalQuantityDflt = 9999;
        public Dictionary<Symbol, decimal> TargetHoldings = new();
        protected IUtilityOrderFactory UtilityOrderFactory;
        public Dictionary<Symbol, double> LastDeltaAcrossDs = new();
        public readonly ConcurrentDictionary<Symbol, double> MarginalWeightedDNLV = new();
        public ConcurrentDictionary<Symbol, ConcurrentDictionary<OrderDirection, Sweep>> SweepState = new();
        public Dictionary<OrderDirection, ConcurrentDictionary<Symbol, SpreadBuffer>> SpreadBuffers = new() { { OrderDirection.Buy, new() }, {  OrderDirection.Sell, new() } };
        public Dictionary<Equity, KalmanFilter<SSVIParamsDictionary>> KalmanFiltersSSVI = new();
        public Dictionary<Equity, KalmanFilterSSVIWriter> KalmanFilterSSVIWriters = new();
        public ConcurrentDictionary<Option, double> PresumedFillIV = new();
        public Dictionary<Symbol, DateTime> CurrentMarketOpen = new();
        public Dictionary<Symbol, DateTime> NextMarketClose = new();
        public Dictionary<Symbol, RequestContractsHandler> RequestContractsHandlers = new();
        public Dictionary<Symbol, SimpleMovingAverage> IVSpreadSMA = new();
        public Dictionary<Symbol, IPricingStrategy> PricingStrategy = new();
        

        public DateTime TimeWarmupFinished = DateTime.MaxValue;

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public void InitializeAlgo(IUtilityOrderFactory utilityOrderFactory)
        {
            UtilityOrderFactory = utilityOrderFactory;
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(1_000_000);
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

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);

            AssignCachedFunctions();
            RiskScenarioHandler = new(this);


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

                //if (optionTicker.Contains(ticker))
                //{
                //    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                //    options.Add(option);
                //    var subscribedSymbols = AddOptionIfScoped(option);
                //    subscriptions += subscribedSymbols.Count;
                //}
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

            RealizedPositionWriter = new(this);

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 120), ExerciseOnExpiryDate);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(TimeSpan.FromMinutes(0)), ClearNextMarketOpenClose);

            // Before EOD - stop trading & overnight hedge
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(mmWindow.End), CancelOpenOptionTickets);  // Stop MM
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed, Cfg.MinutesBeforeMarketCloseHedgeDeltaFlat), HedgeDeltaFlat);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.BeforeMarketClose(symbolSubscribed), OnMarketClose);  // just some logging & cache clearing

            // Logging events
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), LogRiskSchedule);
            //Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(15)), ExportRiskRecords);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(60)), ExportPutCallRatios);

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed), SetTradingRegime);

            // WARMUP
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(Cfg.WarmUpDays + 1).First();
            Log($"WarmUp TimeSpan: {timeSpan} starting on {StartDate - timeSpan}");
            SetWarmUp(timeSpan);

            // Logging
            RiskRecorder = new(this);

            // Wiring up events
            NewBidAskEventHandler += OnNewBidAskEventUpdateLimitPrices;
            NewBidAskEventHandler += OnNewBidAskEventCheckRiskLimits;
            NewBidAskEventHandler += AppendQuoteToMarketsDataSnap;
            NewBidAskEventHandler += OnNewBidAskEventUpdateIVSpread;
            
            RiskLimitExceededEventHandler += OnRiskLimitExceededEventHedge;

            // For backtesting purposes: Test risk profile moves or compare BT to Live
            SetBacktestingHoldings();
        }

        public void OnNewBidAskEventUpdateIVSpread(object sender, NewBidAskEventArgs newBidAsk)
        {
            Symbol symbol = newBidAsk.Symbol;
            if (symbol.SecurityType == SecurityType.Option)
            {
                double spread = IVAsks[symbol].IVBidAsk.IV - IVBids[symbol].IVBidAsk.IV;

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
            Quote quote = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Symbol = e.Symbol.Value,
                SecurityType = SecurityType2SecurityTypePb(security.Type),
                Bid = (float)security.BidPrice,
                Ask = (float)security.AskPrice,
                PriceUnderlying = (float)(security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : 0),
            };
            MarketDataQuotes[e.Symbol].Add(quote);
        }

        public void AppendTradeToMarketsDataSnap(object sender, NewTradeEventArgs e)
        {
            Security security = Securities[e.Symbol];
            IO.Trade trade = new()
            {
                Ts = Time.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                Symbol = e.Symbol.Value,
                SecurityType = SecurityType2SecurityTypePb(security.Type),
                Price = (float)security.Price,
                PriceUnderlying = (float)(security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : 0),
            };
            MarketDataTrades[e.Symbol].Add(trade);
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

        /// <summary>
        /// The algorithm manager calls events in the following order:
        /// Scheduled Events
        /// Consolidation event handlers
        /// OnData event handler
        /// </summary>
        public override void OnData(Slice slice)
        {
            if (IsWarmingUp) return;

            foreach (Symbol symbol in slice.QuoteBars.Keys)
            {
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

            foreach (Symbol underlying in equities)
            {
                if (SignalsLastRun[underlying] < Time - TimeSpan.FromMinutes(30)) RunSignals(underlying);
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
                if (Securities.ContainsKey(symbol) && Securities[symbol].IsTradable) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                
                var historyUnderlying = HistoryWrap(symbolUnderlying, Cfg.MinHistoryDaysUnderlyingForScoping, Resolution.Daily).ToList();
                bool optionInSSVIParams = OptionInSSVIParams(symbol);
                if (historyUnderlying.Any() || optionInSSVIParams)
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (optionInSSVIParams || ContractScopedForSubscription(symbol, lastClose, Cfg.ScopeContractStrikeOverUnderlyingMargin))
                    {
                        var item = AddData<VolatilityQuoteBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        item.IsTradable = false;

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

        public bool OptionInSSVIParams(Symbol option)
        {
            Equity equity = (Equity)Securities[option.ID.Underlying.Symbol];
            return KalmanFiltersSSVI.ContainsKey(equity) && KalmanFiltersSSVI[equity].GetSSVIParams().ContainsKey((option.ID.Date, option.ID.OptionRight));
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

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
            //ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Globals.PathAnalytics, "PositionLifeCycle.csv"));
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            //ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Globals.PathAnalytics, "PositionLifeCycle.csv"));
            //RiskRecorder.Dispose();
            IVSurfaceSSVIMid.Values.DoForEach(s => s.Dispose());
            KalmanFilterSSVIWriters.Values.DoForEach(w => w.Dispose());
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

            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(IVSurfaceRelativeStrike.Status.Smoothings)));
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(IVSurfaceRelativeStrike.Status.Smoothings)));

            OnWarmupFinishedCalled = true;  // bad. remove flag setting design
        }
        /// <summary>
        /// Dump portfolio risk metrics by underlying to csv for outside plotting
        /// </summary>
        public void ExportRiskRecords()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            optionTicker.DoForEach(ticker => RiskRecorder.Record(ticker));
        }

        public void SnapPositions()
        {
            Positions.Values.Where(p => p.Quantity != 0).DoForEach(p => Snap(p.Symbol));
        }

        public void ExportIVSurface()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            IVSurfaceSSVIMid.Values.DoForEach(s => s.WriteCsvRows());
            //IVSurfaceSSVIBid.Values.Union(IVSurfaceSSVIAsk.Values).DoForEach(s => s.WriteCsvRows());
        }
        public void ExportPutCallRatios()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;
            PutCallRatios.Where(kvp => kvp.Key.SecurityType == SecurityType.Equity).DoForEach(kvp => kvp.Value.Export());
        }

        public void HedgeDeltaFlat()
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            foreach (string ticker in ticker)
            {
                Equity equity = (Equity)Securities[ticker];
                Log($"{Time} HedgeDeltaFlat: {equity.Symbol}");
                HedgeOptionWithUnderlying(equity.Symbol);
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

        public double MidIVSSVI(Symbol symbol, double defaultSpread = 0.005)
        {
            if (symbol.SecurityType != SecurityType.Option) return 0;

            Option option = (Option)Securities[symbol];
            Equity equity = (Equity)option.Underlying;
            return IVSurfaceSSVIMid[equity].IV(option);
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

        public Equity ToEquity(Symbol underlying)
        {
            return (Equity)Securities[underlying];
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
                        $"CancelRequestsUnprocessed: Count={Transactions.CancelRequestsUnprocessed.Count()}, LeanID={string.Join(", ", Transactions.CancelRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"SubmitRequestsUnprocessed: Count={Transactions.SubmitRequestsUnprocessed.Count()}, LeanID={string.Join(", ", Transactions.SubmitRequestsUnprocessed.Select(r => r.OrderId))}, " +
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
            Equity equity = (Equity)o.Underlying;
            Symbol underlying = equity.Symbol;

            MarketRegime regime = GetMarketRegime(underlying);
            if (regime == MarketRegime.NoTrade) return false;

            return OptionIsTradeable(o)
                ||
                (
                    regime == MarketRegime.PostEarningsRelease &&
                    !(o.Symbol.ID.Date <= Time.Date)
                    && Portfolio[o.Symbol].Quantity != 0  // Need to exit eventually
                )
                || ManualOrderInstructionBySymbol.ContainsKey(o.Symbol.Value)
                || (beforeEarnings.Contains(regime) && TargetHoldings.ContainsKey(o.Symbol))
                || (regime == MarketRegime.PreEarningsReleaseBeforeMarketClose && RiskScenarioHandler.TradeableOptions.ContainsKey(equity) && RiskScenarioHandler.TradeableOptions[equity].Contains(o));
        }

        /// <summary>
        /// Signals. Securities where we assume risk. Not necessarily same as positions or subscriptions.
        /// </summary>
        public List<Signal> GetDesiredOrders(Symbol underlying)
        {
            Equity equity = ToEquity(underlying);
            if (!IVSurfaceSSVIMid.ContainsKey(equity))
            {
                Log($"{Time} GetDesiredOrders: {underlying} IVSurfaceSSVIMid not ready.");
                return new();
            }
            var scopedOptions = Securities.Values
                .Where(s => s.Type == SecurityType.Option)
                .Select(o => (Option)o)
                .Where(o =>
                    o.Underlying.Symbol == underlying && 
                    ContractScopedForNewPosition(o) &&
                    IVSurfaceSSVIMid[equity].HasParams(o)
            );

            List<Signal> signals = new();
            foreach (Security sec in scopedOptions)
            {
                // BuySell Distinction is insufficient. Scenario: We are delta short, gamma long. Would only want to buy/sell options reducing both, unless the utility is calculated better to compare weight 
                // beneficial risk and detrimental risk against each other. That's what the RiskDiscounts are for.

                Option option = (Option)sec;
                Symbol symbol = sec.Symbol;

                IUtilityOrder utilBuy = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, OrderDirection.Buy), option.BidPrice);
                IUtilityOrder utilSell = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, OrderDirection.Sell), option.AskPrice);

                double minUtility = Cfg.MinUtility.TryGetValue(underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
                // Utility from Risk and Profit are not normed and cannot be compared directly. Risk is not in USD. UtilProfitVega can change very frequently whenever market IV whipsaws around the EWMA.
                if (utilSell.Utility >= minUtility && 
                    utilSell.Utility >= utilBuy.Utility &&
                    IfFilledDecreasesEquityHedgeOrderSize(symbol, OrderDirection.Sell)
                    )
                {
                    signals.Add(new Signal(symbol, OrderDirection.Sell, utilSell));
                }
                else if (utilBuy.Utility >= minUtility && 
                    utilBuy.Utility > utilSell.Utility &&
                    IfFilledDecreasesEquityHedgeOrderSize(symbol, OrderDirection.Buy)
                    )
                {
                    signals.Add(new Signal(symbol, OrderDirection.Buy, utilBuy));
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
                decimal spot = MidPrice(ticketUnderlying);

                if (ocaGroupByUnderlyingDelta.ContainsKey((ticketUnderlying, 1)) || ocaGroupByUnderlyingDelta.ContainsKey((ticketUnderlying, -1)))
                {
                    continue;
                }                
                double delta = OptionContractWrap.E(this, (Option)Securities[t.Symbol], Time.Date).Delta(MidIV(t.Symbol), spot);
                var key = (t.Symbol.Underlying, Math.Sign(delta));
                ocaGroupByUnderlyingDelta[key] = t.OcaGroup;
            }
            // Now for every signal, if (underlying, sing(delta)) is found in dict, assign its OCA group to the signal
            foreach (Signal s in signals)
            {
                decimal spot = MidPrice(Underlying(s.Symbol));
                double delta = OptionContractWrap.E(this, (Option)Securities[s.Symbol], Time.Date).Delta(MidIV(s.Symbol), spot);
                var key = (Underlying(s.Symbol), Math.Sign(delta));
                if (!ocaGroupByUnderlyingDelta.ContainsKey(key))
                {
                       ocaGroupByUnderlyingDelta[key] = NewOcaGroupId();
                }    
                s.AssignOcaGroup(ocaGroupByUnderlyingDelta[key]);                
            }

            return signals;
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

                List<OrderTicket> liveTickets = orderTickets.ToList().
                    Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).
                    SelectMany(kvp => kvp.Value).
                    Where(t => !orderFilledCanceledCancelPendingInvalid.Contains(t.Status)).
                    ToList();

                var ticketsToCancel = liveTickets.Where(t => !symbolDirectionToOrder.Contains((t.Symbol, Num2Direction(t.Quantity)))).ToList();
                if (ticketsToCancel.Any())
                {
                    var orderIDs = string.Join(", ", ticketsToCancel.Select(t => t.OrderId).ToList());
                    string tag = $"HandleDesiredOrders. Canceling {ticketsToCancel.Count} tickets: {orderIDs}";
                    ticketsToCancel.ForEach(t => Cancel(t, tag));
                }
                List<OrderTicket> remainingLiveTickets = liveTickets.Where(t => !ticketsToCancel.Contains(t)).ToList();

                // Sort the signals. Risk reducing first, then risk accepting. Can be done based on their UtilMargin. The larger the safer.
                IEnumerable<Signal> signalByUnderlying = group.OrderByDescending(g => g.UtilityOrder.UtilityMargin);

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
        /// Event driven: On MarketOpen ok, OnFill ok. On underlying moves 0.1% ok. at least every x 5mins. Every call restarts the timer.ok.
        /// </summary>
        public void RunSignals(Symbol symbol)
        {
            Symbol underlying = Underlying(symbol);
            
            if (IsSignalsRunning[underlying] ||
                IsWarmingUp ||
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
            };

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
                double delta = direction * OptionContractWrap.E(this, (Option)Securities[symbol], Time.Date).Delta(MidIV(symbol), spot);

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
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an order ticket with same sign: LeanID={ticket.OrderId}. Status: {ticket.Status}. For now only want 1 order. Not processing" } });
                        return false;
                    }

                    if (ticket.Quantity * quantity <= 0)
                    {
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order: LeanID={ticket.OrderId}. Status: {ticket.Status} Not processing..." } });
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
            };
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
                Log($"{Time}: UpdateLimitOrderOption. Not trading {option.Underlying}. Not in ticker.");
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
                HedgingMode.ImpliedVolatilitySSVI => MidIVSSVI(symbol),
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
        /// <summary>
        /// To be deprecated. Delete.
        /// </summary>
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
