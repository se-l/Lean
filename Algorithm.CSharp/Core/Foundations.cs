using System;
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

namespace QuantConnect.Algorithm.CSharp.Core
{

    public partial class Foundations : QCAlgorithm
    {
        public Resolution resolution;
        public Dictionary<Symbol, QuoteBarConsolidator> QuoteBarConsolidators = new();
        public Dictionary<Symbol, TradeBarConsolidator> TradeBarConsolidators = new();
        public List<OrderEvent> OrderEvents = new();
        public Dictionary<Symbol, List<OrderTicket>> orderTickets = new();
        public OrderType orderType;
        public HashSet<string> optionTicker;
        public HashSet<string> liquidateTicker;
        public HashSet<string> ticker;
        public HashSet<Symbol> equities = new();
        public HashSet<Symbol> options = new();  // Canonical symbols
        public MMWindow mmWindow;
        public Symbol symbolSubscribed;
        public Dictionary<Symbol, SecurityCache> PriceCache = new();
        public SecurityExchangeHours SecurityExchangeHours;

        public Dictionary<Symbol, IVQuoteIndicator> IVBids = new();
        public Dictionary<Symbol, IVQuoteIndicator> IVAsks = new();
        public Dictionary<Symbol, IVSurfaceRelativeStrike> IVSurfaceRelativeStrikeBid = new();
        public Dictionary<Symbol, IVSurfaceRelativeStrike> IVSurfaceRelativeStrikeAsk = new();
        public Dictionary<int, UtilityOrder> OrderTicket2UtilityOrder = new();

        // Begin Used by ImpliedVolaExporter - To be moved over there....
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVBid = new();
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVAsk = new();
        public Dictionary<Symbol, IVTrade> IVTrades = new();
        public Dictionary<Symbol, RollingIVIndicator<IVQuote>> RollingIVTrade = new();
        public Dictionary<Symbol, PutCallRatioIndicator> PutCallRatios = new();
        public Dictionary<Symbol, UnderlyingMovedX> UnderlyingMovedX = new();
        public Dictionary<Symbol, IntradayIVDirectionIndicator> IntradayIVDirectionIndicators = new();
        public Dictionary<Symbol, AtmIVIndicator> AtmIVIndicators = new();
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
        public Dictionary<string, EarningsAnnouncement[]> EarningBySymbol;
        public AMarketMakeOptionsAlgorithmConfig Cfg;

        public Dictionary<Symbol, RiskDiscount> DeltaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> GammaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> EventDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> AbsoluteDiscounts = new();

        public Dictionary<Symbol, RiskProfile> RiskProfiles = new();
        public Dictionary<Symbol, UtilityWriter> UtilityWriters = new();
        public Dictionary<Symbol, OrderEventWriter> OrderEventWriters = new();
        public RealizedPLExplainWriter RealizedPLExplainWriter;
        public Dictionary<int, Quote<Option>> Quotes = new();
        public Dictionary<int, double> OrderIdIV = new();

        public Dictionary<Symbol, LimitIfTouchedOrderInternal> LimitIfTouchedOrderInternals = new();

        public HashSet<Symbol> embargoedSymbols = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
        public HashSet<OrderStatus> orderCanceledOrPending = new() { OrderStatus.CancelPending, OrderStatus.Canceled };
        public HashSet<OrderStatus> orderFilledCanceledInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid };
        public HashSet<OrderStatus> orderSubmittedPartialFilledUpdated = new() { OrderStatus.PartiallyFilled, OrderStatus.Submitted, OrderStatus.UpdateSubmitted };
        public HashSet<SecurityType> securityTypeOptionEquity = new() { SecurityType.Equity, SecurityType.Option };
        public record MMWindow(TimeSpan Start, TimeSpan End);
        Func<Option, decimal> IntrinsicValue;
        public Dictionary<Symbol, DateTime> SignalsLastRun = new();
        public Dictionary<Symbol, bool> IsSignalsRunning = new();
        public readonly ConcurrentQueue<Signal> _signalQueue = new();
        public int ocaGroupId;

        public delegate void HandleRealizedPLExplainEvent(object sender, PLExplain plExplain);
        public event HandleRealizedPLExplainEvent RealizedPLExplainEvent;

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

        public double MidIVEWMA(Symbol symbol, double defaultSpread = 0.005)
        {
            double bidIV = IVSurfaceRelativeStrikeBid[symbol.Underlying].IV(symbol) ?? 0;
            double askIV = IVSurfaceRelativeStrikeAsk[symbol.Underlying].IV(symbol) ?? 0;

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

        public double ForwardIV(Symbol symbol, double defaultSpread = 0.005)
        {
            return MidIVEWMA(symbol);
        }

        public void AlertLateOrderRequests()
        {
            var lateCancelRequests = Transactions.CancelRequestsUnprocessed.Where(r => Time - r.Time > TimeSpan.FromSeconds(15));
            var lateSubmitRequests = Transactions.SubmitRequestsUnprocessed.Where(r => Time - r.Time > TimeSpan.FromMinutes(5));

            if (lateCancelRequests.Any())
            {
                Log($"{Time} AlertLateOrderRequests. Late CancelRequests: {string.Join(", ", lateCancelRequests.Select(r => r.OrderId))}");
                DiscordClient.Send($"AlertLateOrderRequests. Late CancelRequests: {string.Join(", ", lateCancelRequests.Select(r => r.OrderId))}", DiscordChannel.Emergencies, LiveMode);
            }
            if (lateSubmitRequests.Any())
            {
                Log($"{Time} AlertLateSubmitRequests. Late SubmitRequests: {string.Join(", ", lateSubmitRequests.Select(r => r.OrderId))}");
                DiscordClient.Send($"AlertLateSubmitRequests. Late SubmitRequests: {string.Join(", ", lateSubmitRequests.Select(r => r.OrderId))}", DiscordChannel.Emergencies, LiveMode);
            }
        }
        protected void ConsumeSignal()
        {
            lock (_signalQueue)
            {
                if (_signalQueue.IsEmpty) return;
                if (Transactions.CancelRequestsUnprocessed.Any() || Transactions.SubmitRequestsUnprocessed.Count() >= Cfg.MinSubmitRequestsUnprocessedBlockingSubmit)
                {
                    Log($"{Time} ConsumeSignal. WAITING with signal submission: Queue Length: {_signalQueue.Count()}, " +
                        $"CancelRequestsUnprocessed #: {Transactions.CancelRequestsUnprocessed.Count()}: {string.Join(", ", Transactions.CancelRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"SubmitRequestsUnprocessed #: {Transactions.SubmitRequestsUnprocessed.Count()}: {string.Join(", ", Transactions.SubmitRequestsUnprocessed.Select(r => r.OrderId))}, " +
                        $"UpdateRequestsUnprocessed #: {Transactions.UpdateRequestsUnprocessed.Count()}");
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

        public OrderStatus OcaGroupStatus(string ocaGroup)
        {
            return Transactions.OcaGroupStatus.TryGetValue(ocaGroup, out OrderStatus status) ? status : OrderStatus.None;
        }
        public void SubmitSignal(Signal signal)
        {
            // Order desired tickets
            bool valExists = orderTickets.TryGetValue(signal.Symbol, out List<OrderTicket> tickets);
            if (valExists && tickets.Any() && !orderCanceledOrPending.Contains(OcaGroupStatus(signal.OcaGroup)))
            {
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

        public decimal MidPrice(Symbol symbol)
        {
            var security = Securities[symbol];
            return (security.AskPrice + security.BidPrice) / 2;
        }
        static decimal Strike(Order o) => o.Symbol.ID.StrikePrice;
        Order NewEquityExerciseOrder(OptionExerciseOrder o) => new EquityExerciseOrder(o, new OrderFillData(o.Time, MidPrice(o.Symbol.Underlying), MidPrice(o.Symbol.Underlying), MidPrice(o.Symbol.Underlying), MidPrice(o.Symbol.Underlying), MidPrice(o.Symbol.Underlying), MidPrice(o.Symbol.Underlying)))
        {
            Status = OrderStatus.Filled
        };

        public void UpdatePositionLifeCycle(OrderEvent orderEvent)
        {
            var trades = WrapToTrade(orderEvent);
            ApplyToPosition(trades);
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
                    Positions.Remove(trade.Symbol);
                    RemoveSecurity(trade.Symbol);
                    return;
                }

                if (!Positions.ContainsKey(trade.Symbol))
                {
                    Positions[trade.Symbol] = new(null, trade, this);
                }
                else
                {
                    Positions[trade.Symbol] = new(Positions[trade.Symbol], trade, this);
                    RealizedPLExplainEvent?.Invoke(this, Positions[trade.Symbol].RealizedPLExplain(trade));
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

        /// <summary>
        /// Signals. Securities where we assume risk. Not necessarily same as positions or subscriptions.
        /// Should Handle Sizing??????
        /// </summary>
        public List<Signal> GetDesiredOrders(Symbol underlying)
        {
            var scopedSecurities = Securities.Values.Where(s => s.Type == SecurityType.Option && Underlying(s.Symbol) == underlying);
            Dictionary<(Symbol, int), string> ocaGroupByUnderlyingDelta = new()
            {
                [(underlying, -1)] = NewOcaGroupId(),
                [(underlying, 0)] = NewOcaGroupId(),
                [(underlying, +1)] = NewOcaGroupId()
            };

            List<Signal> signals = new();
            foreach (Security sec in scopedSecurities)
            {
                if (
                    sec.IsTradable
                    && (
                            (
                            // to be review with Gamma hedging. Selling option at ultra-high, near-expiry IVs with great gamma hedge could be extra profitable.
                            // DEBUG(sec.Symbol.Value == "DELL  230915C00067500" || sec.Symbol.Value == "DELL  230908C00073000") &&
                            (sec.Symbol.ID.Date - Time.Date).Days > 1  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                            && !sec.Symbol.IsCanonical()
                            && sec.BidPrice != 0
                            && sec.AskPrice != 0

                            // price is not stale. Bit ineffiient here. May rather have an indicator somewhere.
                            //&& PriceCache.ContainsKey(sec.Symbol)
                            //&& PriceCache[sec.Symbol].GetData().EndTime > Time - TimeSpan.FromMinutes(5)

                            && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                            && sec.Symbol.ID.StrikePrice >= MidPrice(sec.Symbol.Underlying) * (Cfg.scopeContractStrikeOverUnderlyingMinSignal)
                            && sec.Symbol.ID.StrikePrice <= MidPrice(sec.Symbol.Underlying) * (Cfg.scopeContractStrikeOverUnderlyingMaxSignal)
                            && (
                                ((Option)sec).GetPayOff(MidPrice(sec.Symbol.Underlying)) < Cfg.scopeContractMoneynessITM * MidPrice(sec.Symbol.Underlying)
                                || (orderTickets.ContainsKey(sec.Symbol) && orderTickets[sec.Symbol].Count > 0 && ((Option)sec).GetPayOff(MidPrice(sec.Symbol.Underlying)) < (Cfg.scopeContractMoneynessITM + 0.05m) * MidPrice(sec.Symbol.Underlying))
                            )
                            && !liquidateTicker.Contains(sec.Symbol.Underlying.Value)  // No new orders, Function oppositeOrder & hedger handle slow liquidation at decent prices.
                            && IVSurfaceRelativeStrikeBid[Underlying(sec.Symbol)].IsReady(sec.Symbol)
                            && IVSurfaceRelativeStrikeAsk[Underlying(sec.Symbol)].IsReady(sec.Symbol)
                            //&& symbol.ID.StrikePrice > 0.05m != 0m;  // Beware of those 5 Cent options. Illiquid, but decent high-sigma underlying move protection.
                            // Embargo
                            && !(
                                embargoedSymbols.Contains(sec.Symbol)
                            //|| (((Option)sec).Symbol.ID.Date - Time.Date).Days <= 2  // Options too close to expiration. This is not enough. Imminent Gamma squeeze risk. Get out fast.
                            )
                        )
                        ||
                        (
                            !(sec.Symbol.ID.Date <= Time.Date)
                            && Portfolio[sec.Symbol].Quantity != 0  // Need to exit eventually
                        )
                    )
                )
                {
                    // BuySell Distinction is insufficient. Scenario: We are delta short, gamma long. Would only want to buy/sell options reducing both, unless the utility is calculated better to compare weight 
                    // beneficial risk and detrimental risk against each other. That's what the RiskDiscounts are for.

                    Option option = (Option)sec;
                    Symbol symbol = sec.Symbol;
                    UtilityOrder utilBuy = new(this, option, SignalQuantity(symbol, OrderDirection.Buy));
                    UtilityOrder utilSell = new(this, option, SignalQuantity(symbol, OrderDirection.Sell));

                    double delta = OptionContractWrap.E(this, option, Time.Date).Delta();
                    string ocaGroupId = ocaGroupByUnderlyingDelta[(option.Underlying.Symbol, Math.Sign(delta))];

                    // Utility from Risk and Profit are not normed and cannot be compared directly. Risk is not in USD. UtilProfitVega can change very frequently whenever market IV whipsaws around the EWMA.
                    if (utilSell.Utility >= 0 && utilSell.Utility >= utilBuy.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Sell, utilSell, ocaGroupId));
                    }
                    else if (utilBuy.Utility >= 0 && utilBuy.Utility > utilSell.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Buy, utilBuy, ocaGroupId));
                    }
                    else
                    {
                        // Save to disk somehow
                        //Log($"None of the utils are positive\n{utilBuy}\n{utilSell}\n.");
                    }
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
                    Log($"{Time} HandleDesiredOrders. Canceling {ticketsToCancel.Count} tickets.");
                    ticketsToCancel.ForEach(t => Cancel(t));
                }

                // Sort the signals. Risk reducing first, then risk accepting. Can be done based on their UtilMargin. The larger the safer.
                var signalByUnderlying = group.OrderByDescending(g => g.UtilityOrder.UtilityMargin).ToList();
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
                IsSignalsRunning[underlying] = true;  // if refactored as task, more elegant? Just run 1 task at a time...
                HandleDesiredOrders(GetDesiredOrders(underlying));
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

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null, decimal margin = 0m)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return midPriceUnderlying > 0
                && symbol.ID.Date > Time + TimeSpan.FromDays(Cfg.scopeContractMinDTE)
                && symbol.ID.Date < Time + TimeSpan.FromDays(Cfg.scopeContractMaxDTE)
                && symbol.ID.OptionStyle == OptionStyle.American
                && symbol.ID.StrikePrice >= midPriceUnderlying * (Cfg.scopeContractStrikeOverUnderlyingMin - margin)
                && symbol.ID.StrikePrice <= midPriceUnderlying * (Cfg.scopeContractStrikeOverUnderlyingMax + margin)
                && IsLiquid(symbol, Cfg.scopeContractIsLiquidDays, Resolution.Daily)
                ;
        }

        public void RemoveUniverseSecurity(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                    (
                    Securities[symbol].IsTradable
                    && !ContractInScope(symbol, margin: Cfg.scopeContractStrikeOverUnderlyingMargin)
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
            // Tradable
            if (!security.IsTradable)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"security {security} not marked tradeable. Should not be sent as signal. Not trading..." } });
                return false;
            }

            // Timing
            if (IsWarmingUp ||
                (Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End && symbol.SecurityType == SecurityType.Option)  // Delta hedging with Equity anytime.
                )
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"Not time to trade yet." } });
                return false;
            }

            // Only 1 ticket per Symbol & Side
            if (orderTickets.TryGetValue(symbol, out var tickets))
            {
                foreach (var ticket in tickets.Where(t => t.Status != OrderStatus.Canceled))
                {
                    // Assigning fairly negative utility to this inventory increase.
                    if (ticket.Quantity * quantity >= 0)
                    {
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an order ticket with same sign: OrderId={ticket.OrderId}. For now only want 1 order. Not processing" } });
                        return false;
                    }

                    if (ticket.Quantity * quantity <= 0)
                    {
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order: OrderId: {ticket.OrderId}. Not processing..." } });
                        return false;
                    }
                }
            }

            //if (symbol.SecurityType == SecurityType.Option && Portfolio[symbol].Quantity * quantity > 0)
            //{
            //    QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an options position with same sign Quantity={Portfolio[symbol].Quantity}. Not processing...\"" } });
            //    return false;
            //}

            if (symbol.SecurityType == SecurityType.Option && !ContractInScope(symbol) && Portfolio[symbol].Quantity == 0)
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

            return true;
        }

        public void StoreOrderTicket(OrderTicket orderTicket, Quote<Option>? quote = null, UtilityOrder? utilityOrder = null)
        {
            (orderTickets.TryGetValue(orderTicket.Symbol, out List<OrderTicket> tickets)
                    ? tickets  // if the key exists, use its value
            : orderTickets[orderTicket.Symbol] = new List<OrderTicket>()) // create a new list if the key doesn't exist
                .Add(orderTicket);
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
                Log($"{Time} CancelOrderTicketIfUnprocessed: {ticket.Symbol} Status: {ticket.Status} remained Unprocessed for {sec} sec after new submission. Canceling lean id {ticket.OrderId}");
                Cancel(ticket);
            }
            else if (ticket?.CancelRequest == null && $"{ticket.Status}" == $"{OrderStatus.New}" && $"{ticket.SubmitRequest.Status}" == $"{OrderRequestStatus.Error}")
            {
                // SubmitRequest.Status is initialized with error.
                // true even if log prints false, therefore evaluating as string now..
                // SubmitRequest.Status=Processed. 15. Remains in bad submission state after 52 seconds. Canceling.
                Log($"{Time} CancelOrderTicketIfUnprocessed: OrderTicket {ticket}. {ticket.Symbol} {ticket.Quantity} {ticket.Status} SubmitRequest.Status={ticket.SubmitRequest.Status}. {ticket.SubmitRequest.OrderId}. Remains in bad submission state after {sec} seconds. Canceling.");
                Cancel(ticket);
            };
        }
        public void OrderEquity(Symbol symbol, decimal quantity, decimal limitPrice, string tag = "", OrderType orderType = OrderType.Market)
        {
            if (!IsOrderValid(symbol, quantity)) { return; }

            OrderTicket orderTicket = orderType switch
            {
                OrderType.Limit => LimitOrder(symbol, quantity, RoundTick(limitPrice, TickSize(symbol)), tag),
                OrderType.Market => MarketOrder(symbol, (int)quantity, tag: tag, asynchronous: LiveMode),
                _ => throw new NotImplementedException($"OrderType {orderType} not implemented")
            };
            LogOrderTicket(orderTicket);
            StoreOrderTicket(orderTicket);
        }
        public OrderTicket? OrderOptionContract(Signal signal, OrderType orderType = OrderType.Limit, string tag = "")
        {
            Option contract = Securities[signal.Symbol] as Option;
            decimal quantity = SignalQuantity(signal.Symbol, signal.OrderDirection);

            if (!IsOrderValid(contract.Symbol, quantity)) { return null; }

            Quote<Option> quote = GetQuote(new QuoteRequest<Option>(contract, quantity));
            decimal limitPrice = quote.Price;
            if (limitPrice == 0)
            {
                Log($"No price quoted for {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading...");
                return null;
            }
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));
            if (limitPrice == 0 || limitPrice < TickSize(contract.Symbol) || limitPrice == null)
            {
                Log($"Invalid price: {limitPrice}. {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading... TickSize: {TickSize(contract.Symbol)}");
                return null;
            }

            OrderTicket orderTicket;

            switch (orderType)
            {
                case OrderType.PeggedToStock:
                    if (quote.IVPrice == 0) return null;
                    var ocw = OptionContractWrap.E(this, contract, Time.Date);
                    decimal delta = (decimal)Math.Abs(ocw.Delta(quote.IVPrice));
                    decimal gamma = (decimal)ocw.Gamma(quote.IVPrice);
                    var midPriceUnderlying = MidPrice(contract.Underlying.Symbol);
                    var offset = delta * Cfg.PeggedToStockDeltaRangeOffsetFactor / gamma;
                    var underlyingRangeLow = midPriceUnderlying - offset;
                    var underlyingRangeHigh = midPriceUnderlying + offset;
                    orderTicket = PeggedToStockOrder(contract.Symbol, quantity, delta * 100m, limitPrice, midPriceUnderlying, underlyingRangeLow, underlyingRangeHigh, tag);
                    OrderIdIV[orderTicket.OrderId] = quote.IVPrice;
                    break;
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
                UpdateLimitOrderOption(Securities[symbol] as Option);
            }
            else if (symbol.SecurityType == SecurityType.Equity)
            {
                UpdateLimitOrderEquity(Securities[symbol] as Equity);
            }
        }
        public void UpdatePeggedOrderOption(Option option)
        {
            Symbol symbol = option.Symbol;
            foreach (OrderTicket ticket in orderTickets[symbol].ToList())
            {
                if (orderSubmittedPartialFilledUpdated.Contains(ticket.Status) && ticket.OrderType == OrderType.PeggedToStock && ticket.CancelRequest == null)
                {
                    decimal tickSize = TickSize(symbol);
                    double ticketIV = OrderIdIV.TryGetValue(ticket.OrderId, out ticketIV) ? ticketIV : 0;

                    Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, SignalQuantity(symbol, Num2Direction(ticket.Quantity))));

                    if (Math.Abs((decimal)quote.IVPrice - (decimal)ticketIV) < Cfg.MinimumIVOffsetBeforeUpdatingPeggedOptionOrder) { return; }

                    decimal quoteStartingPrice = quote.Price;

                    if (quoteStartingPrice == 0 || quote.Quantity == 0 || quote.IVPrice == 0)
                    {
                        Log($"{Time}: UpdateLimitPriceContract. Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. IVPrice={quote.IVPrice} Not trading...");
                        Cancel(ticket);
                        return;
                    }
                    quoteStartingPrice = RoundTick(quoteStartingPrice, tickSize);
                    decimal ticketStartingPrice = ticket.Get(OrderField.StartingPrice);

                    if (Math.Abs(quoteStartingPrice - ticketStartingPrice) >= tickSize && quoteStartingPrice >= tickSize)
                    {
                        if (quoteStartingPrice < tickSize)
                        {
                            Log($"{Time}: CANCEL TICKET Symbol{symbol}: QuoteStartingPrice too small: {quoteStartingPrice}");
                            Cancel(ticket);
                        }
                        else
                        {
                            var tag = $"{Time}: UPDATE TICKET Symbol {symbol} Price: From: {ticketStartingPrice} To: {quoteStartingPrice}";
                            var ocw = OptionContractWrap.E(this, option, Time.Date);
                            decimal delta = (decimal)Math.Abs(ocw.Delta(quote.IVPrice));
                            decimal gamma = (decimal)ocw.Gamma(quote.IVPrice);
                            var midPriceUnderlying = MidPrice(option.Underlying.Symbol);
                            var offset = delta * Cfg.PeggedToStockDeltaRangeOffsetFactor / gamma;
                            var underlyingRangeLow = midPriceUnderlying - offset;
                            var underlyingRangeHigh = midPriceUnderlying + offset;
                            var response = ticket.UpdatePeggedToStockOrder(
                                delta * 100m,
                                quoteStartingPrice,
                                midPriceUnderlying,
                                underlyingRangeLow,
                                underlyingRangeHigh,
                                tag
                                );
                            OrderIdIV[ticket.OrderId] = quote.IVPrice;
                            if (Cfg.LogOrderUpdates || LiveMode)
                            {
                                Log($"{tag}, Response: {response}");
                            }
                            Quotes[ticket.OrderId] = quote;
                        }
                    }

                    // Quantity - low overhead. SignalQuantity needs risk metrics that are also fetched for getting a price and cached.
                    if (ticket.Quantity != quote.Quantity && Math.Abs(ticket.Quantity - quote.Quantity) >= 2)
                    {
                        var tag = $"{Time}: UPDATE TICKET Symbol {symbol} Quantity: From: {ticket.Quantity} To: {quote.Quantity}";
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
        public void UpdateLimitOrderOption(Option option)
        {
            Symbol symbol = option.Symbol;
            foreach (OrderTicket ticket in orderTickets[symbol].ToList())
            {
                if (orderSubmittedPartialFilledUpdated.Contains(ticket.Status) && ticket.OrderType == OrderType.Limit && ticket.CancelRequest == null)
                {
                    decimal tickSize = TickSize(symbol);
                    decimal limitPrice = ticket.Get(OrderField.LimitPrice);

                    Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, SignalQuantity(symbol, Num2Direction(ticket.Quantity))));

                    decimal idealLimitPrice = quote.Price;

                    if (idealLimitPrice == 0 || quote.Quantity == 0)
                    {
                        Log($"{Time}: UpdateLimitPriceContract. Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. Not trading...");
                        Cancel(ticket);
                        return;
                    }
                    idealLimitPrice = RoundTick(idealLimitPrice, tickSize);

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
                                Log($"{tag}, Response: {response}");
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
        /// <summary>
        /// Observed in LiveMode that Order PriceUpdates are realiably fast while cancelations can take a long time leading to unwanted fills. 
        /// Therefore, first updating price and quantity into safe as possible zone followed by cancelation.
        /// </summary>
        /// <returns></returns>
        public OrderResponse? CancelOld(OrderTicket ticket, string tag = "")
        {
            if (orderCanceledOrPending.Contains(ticket.Status)) return null;

            OrderDirection orderDirection = Num2Direction(ticket.Quantity);

            // SecurityType securityType = ticket.Symbol.SecurityType;
            // Symbol underlying = Underlying(ticket.Symbol);
            // Equity underlyingSec = (Equity)Securities[underlying];

            //decimal newPrice = (securityType, orderDirection) switch
            //{
            //    (SecurityType.Equity, OrderDirection.Buy) => ticket.Get(OrderField.LimitPrice) * 0.95m,
            //    (SecurityType.Equity, OrderDirection.Sell) => ticket.Get(OrderField.LimitPrice) * 1.05m,
            //    (SecurityType.Option, OrderDirection.Buy) => ticket.Get(OrderField.LimitPrice) + underlyingSec.Price * 0.05m, // * Delta
            //    (SecurityType.Option, OrderDirection.Sell) => ticket.Get(OrderField.LimitPrice) * 1.05m,
            //    _ => throw new NotImplementedException($"OrderDirection {orderDirection} not implemented")
            //};
            if (ticket.OrderType == OrderType.Limit)
            {
                decimal newPrice = (orderDirection) switch
                {
                    OrderDirection.Buy => ticket.Get(OrderField.LimitPrice) * 0.95m,
                    OrderDirection.Sell => ticket.Get(OrderField.LimitPrice) * 1.05m,
                    _ => throw new NotImplementedException($"OrderDirection {orderDirection} not implemented")
                };
                ticket.UpdateLimitPrice(RoundTick(newPrice, TickSize(ticket.Symbol)), "Update Order before canceling");
            }
            if (ticket.Quantity != Math.Sign(ticket.Quantity))
            {
                ticket.UpdateQuantity(Math.Sign(ticket.Quantity), "Update Quantity before Canceling");
            }

            Log($"{Time} Cancel: {ticket.Symbol} OrderId={ticket.OrderId} Status={ticket.Status} {ticket}");
            var response = ticket.Cancel(tag);
            OrderEventWriters[Underlying(ticket.Symbol)].Write(ticket);
            return response;
        }

        public OrderResponse? Cancel(OrderTicket ticket, string tag = "")
        {
            if (orderCanceledOrPending.Contains(ticket.Status)) return null;

            Log($"{Time} Cancel: {ticket.Symbol} OrderId={ticket.OrderId} Status={ticket.Status} {ticket}");
            var response = ticket.Cancel(tag);
            OrderEventWriters[Underlying(ticket.Symbol)].Write(ticket);
            return response;
        }

        public decimal EquityHedgeQuantity(Symbol underlying)
        {
            decimal quantity;
            decimal deltaTotal = PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal);
            decimal deltaIVdSTotal = PfRisk.RiskByUnderlying(underlying, Metric.DeltaIVdSTotal);  // MV
            Log($"{Time} EquityHedgeQuantity: DeltaTotal={deltaTotal}, deltaIVdSTotal={deltaIVdSTotal} (not used)");
            quantity = -deltaTotal;

            // subtract pending Market order fills
            List<OrderTicket> tickets = orderTickets.TryGetValue(underlying, out tickets) ? tickets : new List<OrderTicket>();
            if (tickets.Any())
            {
                var marketOrders = tickets.Where(t => t.OrderType == OrderType.Market).ToList();
                decimal orderedQuantityMarket = marketOrders.Sum(t => t.Quantity);
                quantity -= orderedQuantityMarket;
                if (orderedQuantityMarket != 0)
                {
                    Log($"{Time} EquityHedgeQuantity: Market Orders present for {underlying} {orderedQuantityMarket} OrderId={string.Join(", ", marketOrders.Select(t => t.OrderId))}.");
                }
            }
            return Math.Round(quantity, 0);
        }

        public void UpdateLimitOrderEquity(Equity equity, decimal? quantity = null)
        {
            int cnt = 0;

            foreach (var ticket in orderTickets[equity.Symbol].ToList().Where(t => t.OrderType == OrderType.Limit && orderSubmittedPartialFilledUpdated.Contains(t.Status) && t.CancelRequest == null))
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
                orderType = GetEquityHedgeOrderType(equity);
                if (ticket.OrderType == OrderType.Limit && orderType == OrderType.Market)
                {
                    Cancel(ticket);
                    Log($"{Time} UpdateLimitOrderEquity: OrderType changed from Limit to Market");
                    OrderEquity(equity.Symbol, (decimal)quantity, ticketPrice, "UpdateLimitOrderEquity", orderType);
                    return;
                }

                // Currently, existing tickets cannot turn into LimitIfTouched. They'd rather be canceled.
                //if (orderType == OrderType.LimitIfTouched)
                //{
                //    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", $"Cancel Order Ticket. Turned into LimitIfTouched" }, { "f", $"UpdateLimitOrderEquity" } });
                //    Cancel(ticket);
                //    continue;
                //}

                decimal idealLimitPrice = GetEquityHedgePrice(equity, orderType, ticket);
                idealLimitPrice = RoundTick(idealLimitPrice, ts);
                if (idealLimitPrice != ticketPrice && idealLimitPrice > 0)
                {
                    var tag = $"{Time}: {ticket.Symbol} Price not good {ticketPrice}: Changing to ideal limit price: {idealLimitPrice}";
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

        public decimal SignalQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absQuantity;
            /// Want to avoid minimum fee payment of 1 USD/stock trade, hence looking to hit a delta that causes at least an equity fee of 1 USD during hedding and minimizes an absolute delta increase.
            /// So the target delta is +/-200.
            /// For more expensive stocks, wouldn't want to increase equity position too quickly, hence not exceed 5k long position. configurable
            //decimal midPrice = MidPrice(Underlying(symbol));

            var absFeeMinimizingDelta = QuantityExceedingMinimumBrokerageFee(symbol); // Make the hedge worthwile

            decimal maxOptionOrderQuantity = Cfg.MaxOptionOrderQuantity.TryGetValue(Underlying(symbol).Value, out maxOptionOrderQuantity) ? maxOptionOrderQuantity : Cfg.MaxOptionOrderQuantity[CfgDefault];

            // Find the delta that would cause an equity position of long max 5k if filled. No restriction for shorting
            decimal targetMaxEquityPositionUSD = Cfg.TargetMaxEquityPositionUSD.TryGetValue(Underlying(symbol).Value, out targetMaxEquityPositionUSD) ? targetMaxEquityPositionUSD : Cfg.TargetMaxEquityPositionUSD[CfgDefault];
            decimal signalQuantityFraction = Cfg.SignalQuantityFraction.TryGetValue(Underlying(symbol).Value, out signalQuantityFraction) ? signalQuantityFraction : Cfg.SignalQuantityFraction[CfgDefault];


            var currentDelta = PfRisk.RiskByUnderlying(symbol.Underlying, Metric.DeltaTotal);
            var deltaPerUnit = PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], Metric.DeltaTotal);

            if (deltaPerUnit == 0) // ZeroDivisionError
            {
                absQuantity = maxOptionOrderQuantity;
            }
            else if (deltaPerUnit * currentDelta > 0) // same direction. Increase risk up to ~200 more. Don't exceed ~5k long position.
            {
                var absFeeMinimizingQuantity = Math.Abs((absFeeMinimizingDelta - Math.Abs(currentDelta)) / deltaPerUnit);

                var absMaxLongPosRespectingDelta = targetMaxEquityPositionUSD / Securities[symbol.Underlying].Price;
                var absMaxLongPosRespectingQuantity = Math.Abs((absMaxLongPosRespectingDelta - Math.Abs(currentDelta)) / deltaPerUnit);

                absQuantity = Math.Min(absFeeMinimizingQuantity, absMaxLongPosRespectingQuantity);
            }
            else // opposite direction. Risk reducing / reversing. Aim for delta reversal, but not not to max 5k.
            {
                var absFeeMinimizingQuantity = Math.Abs((
                    Math.Abs(currentDelta) +  // To zero Risk
                    absFeeMinimizingDelta)  // Reversing Delta Risk to worthwhile ~200
                    / deltaPerUnit);

                var absMaxLongPosRespectingDelta = Math.Abs(currentDelta) + targetMaxEquityPositionUSD / Securities[symbol.Underlying].Price;
                var absMaxLongPosRespectingQuantity = Math.Abs(absMaxLongPosRespectingDelta / deltaPerUnit);

                absQuantity = Math.Min(absFeeMinimizingQuantity, absMaxLongPosRespectingQuantity);
            }

            absQuantity /= signalQuantityFraction;

            decimal maxQuantityByMarginConstraints = 9999;// SignalQuantityMaxByMargin(symbol, orderDirection);
            absQuantity = Math.Min(absQuantity, maxQuantityByMarginConstraints);
            absQuantity = Math.Min(absQuantity, maxOptionOrderQuantity);
            absQuantity = Math.Round(Math.Max(absQuantity, 1), 0);

            return DIRECTION2NUM[orderDirection] * absQuantity;
        }

        /// <summary>
        /// If max margin per underlying has been exceeded, return quantity that would reduce margin, not overshoot, e.g, from position: -2 end up with +8.
        /// To be refactored once portfolio risk based margining has been improved.
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="orderDirection"></param>
        /// <returns></returns>
        //public decimal SignalQuantityMaxByMargin(Symbol symbol, OrderDirection orderDirection)
        //{
        //    Symbol underlying = Underlying(symbol);
        //    decimal targetMaxMarginUsed = Cfg.TargetMaxMarginUsed.TryGetValue(underlying.Value, out targetMaxMarginUsed) ? targetMaxMarginUsed : Cfg.TargetMaxMarginUsed[CfgDefault];
        //    decimal maxOptionOrderQuantity = Cfg.MaxOptionOrderQuantity.TryGetValue(Underlying(symbol).Value, out maxOptionOrderQuantity) ? maxOptionOrderQuantity : Cfg.MaxOptionOrderQuantity[CfgDefault];

        //    decimal marginUsed = PfRisk.RiskByUnderlying(underlying, Metric.MarginUsed);
        //    decimal addedMarginIfFilled = PfRisk.RiskIfFilled(symbol, DIRECTION2NUM[orderDirection], Metric.MarginUsed);

        //    if (addedMarginIfFilled == 0) // ZeroDivisionError
        //    {
        //        return maxOptionOrderQuantity;
        //    }
        //    else if (marginUsed < targetMaxMarginUsed)
        //    {
        //        return targetMaxMarginUsed / addedMarginIfFilled;
        //    }
        //    else
        //    {
        //        if (Positions.TryGetValue(symbol, out Position position))
        //        {
        //            // Not Yet able to do what if - hence checking here if position is reduces or increased. No groups logic, no portfolio margin logic. Presumably Reg T like.
        //            addedMarginIfFilled = position.Quantity * DIRECTION2NUM[orderDirection] > 0 ? addedMarginIfFilled : -addedMarginIfFilled;
        //        }
        //        double negIfIsMarginIncreasing = -Math.Sign(addedMarginIfFilled);

        //        if (addedMarginIfFilled > 0)  // Increase already exceeded margin
        //        {
        //            // If margin is increasing, we want to reduce it. Hence, we want to sell.
        //            return 1;
        //        }
        //        else
        //        {
        //            return Math.Abs(position.Quantity);
        //        }
        //    }
        //}

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
                symbol.ID.Symbol.Contains(VolatilityBar)
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
                    var periods = Periods(security.Resolution, days: 5);
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
        public void InternalAudit()
        {
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
            Log($"Brokerage meesage received - {messageEvent.ToString()}");
        }
    }
}
