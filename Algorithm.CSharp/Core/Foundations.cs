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
using Fasterflect;

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
        //public Utility Utility = new();
        public Dictionary<int, Quote<Option>> Quotes = new();

        public Dictionary<Symbol, LimitIfTouchedOrderInternal> LimitIfTouchedOrderInternals = new();

        public HashSet<Symbol> embargoedSymbols = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
        public HashSet<OrderStatus> orderCanceledOrPending = new() { OrderStatus.CancelPending, OrderStatus.Canceled };
        public HashSet<OrderStatus> orderFilledCanceledInvalid = new() { OrderStatus.Filled, OrderStatus.Canceled, OrderStatus.Invalid, OrderStatus.CancelPending };
        public HashSet<SecurityType> securityTypeOptionEquity = new() { SecurityType.Equity, SecurityType.Option };
        public record MMWindow(TimeSpan Start, TimeSpan End);
        Func<Option, decimal> IntrinsicValue;

        // LogFileHandler


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

        public decimal CastGracefully(double originalValue)
        {
            if (originalValue >= (double)decimal.MinValue && originalValue <= (double)decimal.MaxValue)
            {
                // The original value is within the valid range for a decimal
                return (decimal)originalValue;
            }
            else
            {
                // The original value is out of the valid range
                return 0; // Round to zero
            }
        }

        public decimal MidPrice(Symbol symbol)
        {
            var security = Securities[symbol];
            return (security.AskPrice + security.BidPrice) / 2;
        }
        static decimal Strike(Order o) => o.Symbol.ID.StrikePrice;
        Order NewEquityExerciseOrder(OptionExerciseOrder o) => new EquityExerciseOrder(o, new OrderFillData(o.Time, Strike(o), Strike(o), Strike(o), Strike(o), Strike(o), Strike(o)))
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
            if (symbol.SecurityType == SecurityType.Option && symbol.ID.Date <= Time.Date && orderEvent.IsInTheMoney)  // Assignment or Exercise Option Leg
            {
                Log($"WrapToTrade. Option OptionExersiseOrder - Option Leg - IsInTheMoney: {symbol}. {orderEvent.OrderId}.");
                OptionExerciseOrder optionExerciseOrder = (OptionExerciseOrder)Transactions.GetOrderById(orderEvent.OrderId);
                Trade tradeOptionExercise = new(this, orderEvent, optionExerciseOrder);
                newTrades.Add(tradeOptionExercise);
            }
            else if (symbol.SecurityType == SecurityType.Option && symbol.ID.Date <= Time.Date)  // Expired OTM
            {
                Log($"WrapToTrade. Option Expired OTM: {symbol}. {Portfolio[symbol].Quantity}");
                newTrades.Add(new(this, orderEvent, -Positions[symbol].Quantity));
            }
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
        /// Refactor into position. This here has the risk of double-counting trades. Need to not apply when order id equial to trade0.ID.
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
        public List<Signal> GetDesiredOrders()  // Takes 25% CPU time. 10% each for utilBuy and 10% utilSell.
        {
            List<Signal> signals = new();
            foreach (Security sec in Securities.Values)
            {
                if (
                    sec.IsTradable
                    && sec.Type == SecurityType.Option
                    && (
                            (                    
                            // to be review with Gamma hedging. Selling option at ultra-high, near-expiry IVs with great gamma hedge could be extra profitable.
                            (sec.Symbol.ID.Date - Time.Date).Days > 1  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                            && !sec.Symbol.IsCanonical()
                            && sec.BidPrice != 0
                            && sec.AskPrice != 0
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
                    
                    // Utility from Risk and Profit are not normed and cannot be compared directly. Risk is not in USD. UtilProfitVega can change very frequently whenever market IV whipsaws around the EWMA.
                    if (utilSell.Utility >= 0 && utilSell.Utility >= utilBuy.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Sell, utilSell));
                    }
                    else if (utilBuy.Utility >= 0 && utilBuy.Utility > utilSell.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Buy, utilBuy));
                    }
                    else
                    {
                        // Save to disk somehow
                        //Log($"None of the utils are positive\n{utilBuy}\n{utilSell}\n.");
                    }
                }
            }
            Log($"{Time}, topic=SIGNALS, " +
                    $"#Symbols={signals.Select(s => s.Symbol).Distinct().Count()}, " +
                    $"#Signals={signals.Count}, " +
                    $"#BuyCalls={signals.Where(s => s.OrderDirection == OrderDirection.Buy && s.Symbol.ID.OptionRight == OptionRight.Call).Count()}, " +
                    $"#SellCalls={signals.Where(s => s.OrderDirection == OrderDirection.Sell && s.Symbol.ID.OptionRight == OptionRight.Call).Count()}, " +
                    $"#BuyPuts={signals.Where(s => s.OrderDirection == OrderDirection.Buy && s.Symbol.ID.OptionRight == OptionRight.Put).Count()}, " +
                    $"#SellPuts={signals.Where(s => s.OrderDirection == OrderDirection.Sell && s.Symbol.ID.OptionRight == OptionRight.Put).Count()}");
            return signals;
        }

        /// <summary>
        /// Cancels undesired orders, places desired orders.
        /// </summary>
        public void HandleDesiredOrders(IEnumerable<Signal> signals)
        {
            foreach (var group in signals.GroupBy(s => s.Symbol.Underlying))
            {
                // Cancel any undesired option ticket.
                var underlying = group.Key;
                var symbolDirectionToOrder = group.Select(s => (s.Symbol, s.OrderDirection)).ToList();
                var ticketsToCancel = orderTickets.
                    Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).
                    SelectMany(kvp => kvp.Value).
                    Where(t => !symbolDirectionToOrder.Contains((t.Symbol, Num2Direction(t.Quantity)))
                            && !orderCanceledOrPending.Contains(t.Status)).
                    ToList();
                if (ticketsToCancel.Any())
                {
                    Log($"{Time} HandleDesiredOrders. Canceling {ticketsToCancel.Count} tickets.");
                    ticketsToCancel.ForEach(t => t.Cancel());
                }

                // Order desired tickets
                foreach (Signal signal in group)
                {
                    bool valExists = orderTickets.TryGetValue(signal.Symbol, out List<OrderTicket> tickets);
                    if (valExists && tickets.Any())
                    {                        
                        // Either already have a ticket. No problem / ok.

                        // Or cancelation pending. In this case. register a callback to order the desired ticket, once canceled, comes as orderEvent.
                        // EventDriven : On Cancelation, place opposite direction order if any in Signals.
                        // TBCoded
                        continue;                        
                    }
                    OrderOptionContract(signal, OrderType.Limit);
                    //Log($"{Time}, topic={signal.UtilityOrder}");
                }
            }
        }

        public void CancelOpenOptionTickets()
        {
            if (IsWarmingUp)
            {
                return;
            }
            foreach (var tickets in orderTickets.Values)
            {
                foreach (var t in tickets.Where(t => t.Status != OrderStatus.Invalid && t.Symbol.SecurityType == SecurityType.Option).ToList())
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "action", $"CancelOpenTickets. Canceling {t.Symbol}. EndOfDay" } });
                    t.Cancel();
                }
            }
        }

        public void LogRiskSchedule()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            LogPositions();
            LogRisk();
            LogPnL();
        }

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null, decimal margin=0m)
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
            if (IsWarmingUp || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End)
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
                        QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order. Not processing..." } });
                        return false;
                    }
                }
            }

            if (symbol.SecurityType == SecurityType.Option && Portfolio[symbol].Quantity * quantity > 0)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION.IsOrderValid" }, { "msg", $"{symbol}. Already have an options position with same sign Quantity={Portfolio[symbol].Quantity}. Not processing...\"" } });
                return false;
            }

            if (symbol.SecurityType == SecurityType.Option && !ContractInScope(symbol) && Portfolio[symbol].Quantity == 0)
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"contract {symbol} is not in scope. Not trading..." } });
                RemoveUniverseSecurity(Securities[symbol]);
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
        }
        public void OrderEquity(Symbol symbol, decimal quantity, decimal limitPrice, string tag = "", OrderType orderType = OrderType.Market)
        {
            if (!IsOrderValid(symbol, quantity)) { return; }

            OrderTicket orderTicket = orderType switch
            {
                OrderType.Limit =>  LimitOrder(symbol, quantity, RoundTick(limitPrice, TickSize(symbol)), tag),
                OrderType.Market => MarketOrder(symbol, (int)quantity, tag: tag, asynchronous: LiveMode),
                _ => throw new NotImplementedException($"OrderType {orderType} not implemented")
            };            
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
            if (limitPrice < TickSize(contract.Symbol))
            {
                Log($"Invalid price: {limitPrice}. {signal.OrderDirection} {Math.Abs(quantity)} {contract.Symbol}. Not trading... TickSize: {TickSize(contract.Symbol)}");
                return null;
            }

            tag = LogOptionOrder(contract, quantity, limitPrice, orderType) + tag;

            OrderTicket orderTicket = orderType switch
            {
                OrderType.Limit => LimitOrder(contract.Symbol, quantity, limitPrice, tag),
                OrderType.Market => MarketOrder(contract.Symbol, (int)quantity, tag: tag, asynchronous: LiveMode),
                _ => throw new NotImplementedException($"OrderType {orderType} not implemented")
            };
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
        public void UpdateLimitOrderOption(Option option)
        {
            Symbol symbol = option.Symbol;
            foreach (var ticket in orderTickets[symbol])
            {
                if (ticket.Status == OrderStatus.Submitted || ticket.Status == OrderStatus.PartiallyFilled || ticket.Status == OrderStatus.UpdateSubmitted)
                {
                    decimal tickSize = TickSize(symbol);
                    decimal limitPrice = ticket.Get(OrderField.LimitPrice);
                    
                    Quote<Option> quote = GetQuote(new QuoteRequest<Option>(option, SignalQuantity(symbol, Num2Direction(ticket.Quantity))));

                    decimal idealLimitPrice = quote.Price;

                    if (idealLimitPrice == 0 || quote.Quantity == 0)
                    {
                        Log($"{Time}: UpdateLimitPriceContract. Received 0 price or quantity for submitted order. Canceling {symbol}. Quote: {quote}. Not trading...");
                        ticket.Cancel();
                        return;
                    }
                    idealLimitPrice = RoundTick(idealLimitPrice, tickSize);

                    // Price
                    if (Math.Abs(idealLimitPrice - limitPrice) >= tickSize && idealLimitPrice >= tickSize)
                    {
                        if (idealLimitPrice < tickSize)
                        {
                            Log($"{Time}: CANCEL LIMIT Symbol{symbol}: Price too small: {limitPrice}");
                            ticket.Cancel();
                        }
                        else
                        {
                            var tag = $"{Time}: UPDATE LIMIT Symbol {symbol} Price: From: {limitPrice} To: {idealLimitPrice}";
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
                        var tag = $"{Time}: UPDATE LIMIT Symbol {symbol} Quantity: From: {ticket.Quantity} To: {quote.Quantity}";
                        var response = ticket.UpdateQuantity(quote.Quantity, tag);
                        if (Cfg.LogOrderUpdates || LiveMode)
                        {
                            Log($"{tag}, Response: {response}");
                        }
                        Quotes[ticket.OrderId] = quote;
                    }
                }
                else if (ticket.Status == OrderStatus.CancelPending) { }
                else
                {
                    Log($"{Time} UpdateLimitPriceContract {option} ticket={ticket}, OrderStatus={ticket.Status} - Should not run this function for this ticket. Cleanup orderTickets.");
                }
            }
        }

        public decimal EquityHedgeQuantity(Symbol underlying) => -PfRisk.RiskByUnderlying(underlying, Metric.DeltaTotal);

        public void UpdateLimitOrderEquity(Equity equity, decimal? quantity = null)
        {
            int cnt = 0;

            foreach (var ticket in orderTickets[equity.Symbol].Where(t => t.OrderType == OrderType.Limit))
            {
                if (ticket.Status == OrderStatus.Submitted || ticket.Status == OrderStatus.PartiallyFilled || ticket.Status == OrderStatus.UpdateSubmitted)
                {
                    if (cnt > 1)
                    {
                        Log($"{Time}: CANCEL LIMIT Symbol{equity.Symbol}: Too many orders");
                        ticket.Cancel();
                        continue;
                    }
                    cnt++;

                    quantity ??= Math.Round(EquityHedgeQuantity(equity.Symbol));

                    decimal ts = TickSize(ticket.Symbol);
                    decimal ticketPrice = ticket.Get(OrderField.LimitPrice);
                    (decimal idealLimitPrice, orderType) = GetEquityHedgeLimitOrderPrice(equity, ticket);

                    // Currently, existing tickets cannot turn into LimitIfTouched. They'd rather be canceled.
                    //if (orderType == OrderType.LimitIfTouched)
                    //{
                    //    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", $"Cancel Order Ticket. Turned into LimitIfTouched" }, { "f", $"UpdateLimitOrderEquity" } });
                    //    ticket.Cancel();
                    //    continue;
                    //}

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

            absQuantity = Math.Round(Math.Max(absQuantity, 1), 0);

            return DIRECTION2NUM[orderDirection] * Math.Min(maxOptionOrderQuantity, absQuantity);
        }

        private static readonly HashSet<OrderStatus> skipOrderStatus = new() { OrderStatus.Canceled, OrderStatus.Filled, OrderStatus.Invalid, OrderStatus.CancelPending };

        public void CancelGammaHedgeBeyondScope()
        {
            var tickets = orderTickets.Values.SelectMany(x => x).Where(t => !skipOrderStatus.Contains(t.Status)).ToList();  // ToList() -> Avoids concurrent modification error
            var ticketToCancel = new List<OrderTicket>();
            foreach (var ticketsByUnderlying in tickets.GroupBy(t => Underlying(t.Symbol)))
            {
                OrderTicket tg = ticketsByUnderlying.First();
                foreach (var t in ticketsByUnderlying)
                {
                    if (t.Quantity > 0 && Portfolio[t.Symbol].Quantity == 0 && t.Symbol.SecurityType == SecurityType.Option && t.Symbol.ID.Date <= Time + TimeSpan.FromDays(30) )  // Currently a gamma hedge. Needs more specific way of identifying these orders.
                    {
                        ticketToCancel.Add(t);
                    }
                }
            }
            ticketToCancel.ForEach(t => t.Cancel());

        }
        public void InitializePositionsFromPortfolio()
        {
            // Adding existing positions to algo state.
            foreach (var holding in Portfolio.Values.Where(x => securityTypeOptionEquity.Contains(x.Type)))
            {
                Log($"Initialized Position {holding.Symbol} with Holding: {holding}");
                Positions[holding.Symbol] = new(this, holding);
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
    }
}
