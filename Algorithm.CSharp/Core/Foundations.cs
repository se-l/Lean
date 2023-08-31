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
using QuantConnect.Algorithm.CSharp.Core.Pricing;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public Resolution resolution;
        public List<OrderEvent> OrderEvents = new();
        public Dictionary<Symbol, List<OrderTicket>> orderTickets = new();
        public OrderType orderType;
        public List<string> hedgeTicker;
        public List<string> optionTicker;
        public List<string> liquidateTicker;
        public List<string> ticker;
        public List<Symbol> equities = new();
        public List<Symbol> options = new();  // Canonical symbols
        public Dictionary<Symbol, HashSet<Option>> optionChains = new();
        public MMWindow mmWindow;
        public Symbol equity1;
        public Dictionary<Symbol, SecurityCache> PriceCache = new();
        public SecurityExchangeHours securityExchangeHours;

        public Dictionary<Symbol, IVBid> IVBids = new();
        public Dictionary<Symbol, IVAsk> IVAsks = new();
        public Dictionary<Symbol, RollingIVIndicator<IVBidAsk>> RollingIVBid = new();
        public Dictionary<Symbol, RollingIVIndicator<IVBidAsk>> RollingIVAsk = new();
        public Dictionary<Symbol, RollingIVSurfaceRelativeStrike<IVBidAsk>> RollingIVStrikeBid = new();
        public Dictionary<Symbol, RollingIVSurfaceRelativeStrike<IVBidAsk>> RollingIVStrikeAsk = new();
        public Dictionary<Symbol, IVTrade> IVTrades = new();
        public Dictionary<Symbol, RollingIVIndicator<IVBidAsk>> RollingIVTrade = new();
        public RiskRecorder RiskRecorder;

        public TickCounter TickCounterFilter;
        
        public PortfolioRisk pfRisk;
        public bool OnWarmupFinishedCalled = false;
        public decimal TotalPortfolioValueSinceStart = 0m;
        public Dictionary<int, OrderFillData> orderFillDataTN1 = new();
        public EarningsAnnouncement[] EarningsAnnouncements;
        public AMarketMakeOptionsAlgorithmConfig Cfg;

        public Dictionary<Symbol, RiskDiscount> DeltaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> GammaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> EventDiscounts = new();
        public Dictionary<Symbol, RiskPnLProfile> RiskPnLProfiles = new();
        public Utility Utility = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
        public HashSet<OrderStatus> orderCanceledOrPending = new() { OrderStatus.CancelPending, OrderStatus.Canceled };
        public record MMWindow(TimeSpan Start, TimeSpan End);

        public int Periods(Resolution? thisResolution = null, int days = 5)
        {
            return (thisResolution ?? resolution) switch
            {
                Resolution.Daily => days,
                Resolution.Hour => (int)(days * 24),
                Resolution.Minute => (int)(days * 24 * 60),
                Resolution.Second => (int)(days * 24 * 60 * 60),
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
                orderFillDataTN1[order.Id] = symbol.SecurityType switch
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

        public bool IsEventNewBidAsk(Symbol symbol)
        {
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
        public List<Signal> GetDesiredOrders()
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
                        (sec.Symbol.ID.Date - Time.Date).Days > 2  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                        && !sec.Symbol.IsCanonical()
                        && sec.BidPrice != 0
                        && sec.AskPrice != 0
                        && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                        && sec.Symbol.ID.StrikePrice >= MidPrice(sec.Symbol.Underlying) * (Cfg.scopeContractStrikeOverUnderlyingMin)
                        && sec.Symbol.ID.StrikePrice <= MidPrice(sec.Symbol.Underlying) * (Cfg.scopeContractStrikeOverUnderlyingMax)
                        && RollingIVStrikeBid[sec.Symbol.Underlying].IsReady
                        && RollingIVStrikeAsk[sec.Symbol.Underlying].IsReady
                        && !liquidateTicker.Contains(sec.Symbol.Underlying.Value)  // No new orders, Function oppositeOrder & hedger handle slow liquidation at decent prices.
                        ) 
                        || 
                        (
                        Portfolio[sec.Symbol].Quantity != 0  // Need to exit eventually
                        )
                    )
                )
                {
                    Option option = (Option)sec;
                    Symbol symbol = sec.Symbol;
                    UtilityOrder utilBuy = new(this, option, 1m);
                    UtilityOrder utilSell = new(this, option, -1m);
                    
                    // Utility from Risk and Profit are not normed and cannot be compared directly. Risk is not in USD. UtilProfitVega can change very frequently whenever market IV whipsaws around the EWMA.
                    if (utilSell.Utility >= 0 && utilSell.Utility >= utilBuy.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Sell, utilSell));
                    }
                    else if (utilBuy.Utility >= 0 && utilBuy.Utility > utilSell.Utility)
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Buy, utilBuy));
                    }                    
                }
            }
            return signals;
        }

        /// <summary>
        /// Cancels undesired orders, places desired orders.
        /// </summary>
        /// <param name="signals"></param>
        /// <returns></returns>
        public void HandleDesiredOrders(IEnumerable<Signal> signals)
        {
            foreach (var group in signals.GroupBy(s => s.Symbol.Underlying))
            {
                var underlying = group.Key;
                var symbolsToTicket = group.Select(s => s.Symbol).ToList();
                foreach (Signal signal in group)
                {
                    if (orderTickets.TryGetValue(signal.Symbol, out List<OrderTicket> tickets))
                    {
                        if (tickets.Any())
                        {
                            // Cancel undesired orders without placing new ones right now, awaiting cancelation. If existing order is ok, continues to next symbol.
                            foreach (OrderTicket ticket in tickets)
                            {
                                // There is an order. If desired direction ok, otherwise place.
                                if (Num2Direction(ticket.Quantity) != signal.OrderDirection)
                                {
                                    if (!orderCanceledOrPending.Contains(ticket.Status))
                                    {
                                        ticket.Cancel();
                                    }
                                    // Ticket goes into CancelPending status. Cannot yet place new until cancelation is confirmed

                                    // Below code needs to go into an event-driven handler, placing the new order once cancelation is confirmed.
                                    // OrderOptionContract((Option)Securities[signal.Symbol], SignalQuantity(signal.Symbol, signal.OrderDirection), OrderType.Limit);
                                }
                                // Good. Already having a ticket for this symbol and direction.
                            }
                        }
                        else
                        {
                            // No tickets for this symbol currently.
                            OrderOptionContract((Option)Securities[signal.Symbol], SignalQuantity(signal.Symbol, signal.OrderDirection), OrderType.Limit);
                        }
                    }
                    else 
                    {
                        // First time orders are placed on this symbol
                        OrderOptionContract((Option)Securities[signal.Symbol], SignalQuantity(signal.Symbol, signal.OrderDirection), OrderType.Limit);
                    }
                }
                // Also need to cancel any other option order not listed in the signals.
                var toCancel = orderTickets.Keys.Where(s => s.Underlying == underlying && !symbolsToTicket.Contains(s) && orderTickets[s].Any()).ToList();
                toCancel.ForEach(s =>
                {
                    // Refactor using Many, chain(*args) to create single list and remove foreach.
                    foreach (var t in orderTickets[s].Where(t => !orderCanceledOrPending.Contains(t.Status)).ToList()) {
                        t.Cancel(); 
                    }
                });
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
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;
            
            LogRisk();
            LogPnL();
        }

        public void PopulateOptionChains()
        {
            if (IsWarmingUp) return;

            foreach (Symbol symbol in Securities.Keys)
            {
                if (symbol.SecurityType == SecurityType.Option)
                {
                    Option option = (Option)Securities[symbol];
                    Symbol underlying = option.Underlying.Symbol;
                    HashSet<Option> optionChain = optionChains.GetValueOrDefault(underlying, new HashSet<Option>());
                    optionChain.Add(option);
                    optionChains[underlying] = optionChain;
                }
            }
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
                //&& symbol.ID.StrikePrice % 0.05m != 0m;  // This condition is somewhat strange here. Revise and move elsewhere. Beware of not buying those 5 Cent options. Should have been previously filtered out. Yet another check
        }

        public void RemoveUniverseSecurity(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                    (
                    Securities[symbol].IsTradable
                    && !ContractInScope(symbol, margin: Cfg.scopeContractStrikeOverUnderlyingMargin)
                    && Portfolio[symbol].Quantity == 0
                    && Securities[symbol].IsTradable
                    )
                    || security.IsDelisted
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

        public void StoreOrderTicket(OrderTicket orderTicket)
        {
            (orderTickets.TryGetValue(orderTicket.Symbol, out List<OrderTicket> tickets)
                    ? tickets  // if the key exists, use its value
            : orderTickets[orderTicket.Symbol] = new List<OrderTicket>()) // create a new list if the key doesn't exist
                .Add(orderTicket);
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

        public void OrderOptionContract(Option contract, decimal quantity, OrderType orderType = OrderType.Limit, string tag = "")
        {
            if (!IsOrderValid(contract.Symbol, quantity)) { return; }

            decimal limitPrice = PriceOptionPfRiskAdjusted(contract, quantity);
            if (limitPrice == 0)
            {
                Log($"No price for {contract.Symbol}. Not trading...");
                return;
            }
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));

            tag = LogContract(contract, NUM2DIRECTION[Math.Sign(quantity)], limitPrice, orderType) + tag;

            OrderTicket orderTicket = orderType switch
            {
                OrderType.Limit => LimitOrder(contract.Symbol, quantity, limitPrice, tag),
                OrderType.Market => MarketOrder(contract.Symbol, (int)quantity, tag: tag, asynchronous: LiveMode),
                _ => throw new NotImplementedException($"OrderType {orderType} not implemented")
            };
            StoreOrderTicket(orderTicket);
        }
        public void UpdateLimitPrice(Symbol symbol)
        {
            if (!orderTickets.ContainsKey(symbol))
            {
                return;
            }
            if (symbol.SecurityType == SecurityType.Option && !symbol.IsCanonical())
            {
                UpdateLimitPriceContract(Securities[symbol] as Option);
            }
            else if (symbol.SecurityType == SecurityType.Equity)
            {
                UpdateLimitOrderEquity(Securities[symbol] as Equity);
            }
        }
        public void UpdateLimitPriceContract(Option contract)
        {
            foreach (var ticket in orderTickets[contract.Symbol])
            {
                if (ticket.Status == OrderStatus.Submitted || ticket.Status == OrderStatus.PartiallyFilled || ticket.Status == OrderStatus.UpdateSubmitted)
                {
                    var symbol = ticket.Symbol;
                    var tick_size_ = TickSize(contract.Symbol);
                    var limit_price = ticket.Get(OrderField.LimitPrice);
                    var ideal_limit_price = PriceOptionPfRiskAdjusted(contract, ticket.Quantity);
                    ideal_limit_price = RoundTick(ideal_limit_price, tick_size_);

                    // Reduce Order Ticket Updates - Only update when more change is more than 1 tick size. Commented as it premature optimization.
                    //if (ticket.UpdateRequests.Count > 0 && ticket.UpdateRequests[ticket.UpdateRequests.Count - 1].LimitPrice == ideal_limit_price)
                    //{
                    //    continue;
                    //}


                    //var orderDirection = NUM2DIRECTION[Math.Sign(ticket.Quantity)];
                    //if (!IsFlippingTicket(ticket) && !PermittedOrderDirectionsFromVolatility(contract.Symbol, IVLongShortBandCancel).Contains(orderDirection))
                    ////else if (!PermittedOrderDirectionsFromVolatility(contract.Symbol, IVLongShortBandCancel).Contains(orderDirection))                        
                    //{
                    //    Log($"{Time}: CANCEL LIMIT Symbol{contract.Symbol}: ideal_limit_price = 0. Presumably IV too far gone...");
                    //    ticket.Cancel();
                    //}

                    if (Math.Abs(ideal_limit_price - limit_price) >= tick_size_ && ideal_limit_price >= tick_size_)
                    {
                        if (ideal_limit_price < tick_size_)
                        {
                            Log($"{Time}: CANCEL LIMIT Symbol{contract.Symbol}: Price too small: {limit_price}");
                            ticket.Cancel();
                        }
                        else
                        {
                            var tag = $"{Time}: UPDATE LIMIT Symbol{contract.Symbol}: From: {limit_price} To: {ideal_limit_price}";
                            var response = ticket.UpdateLimitPrice(ideal_limit_price, tag);
                            if (LiveMode)
                            {
                                Log($"{tag}, Response: {response}");
                            }
                        }
                    }
                }
                else if (ticket.Status == OrderStatus.CancelPending) { }
                else
                {
                    Log($"{Time} UpdateLimitPriceContract {contract} ticket={ticket}, OrderStatus={ticket.Status} - Should not run this function for this ticket. Cleanup orderTickets.");
                }
            }
        }

        public void UpdateLimitOrderEquity(Equity equity, decimal? newQuantity = 0, decimal? newLimitPrice = null)
        {
            int cnt = 0;
            foreach (var ticket in orderTickets[equity.Symbol])
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

                    decimal ts = TickSize(ticket.Symbol);
                    var limit_price = ticket.Get(OrderField.LimitPrice);
                    decimal ideal_limit_price = newLimitPrice ?? GetEquityHedgeLimitOrderPrice(-ticket.Quantity, equity);
                    if (RoundTick(ideal_limit_price, ts) != RoundTick(limit_price, ts) && ideal_limit_price > 0)
                    {
                        var tag = $"{Time}: {ticket.Symbol} Price not good {limit_price}: Changing to ideal limit price: {ideal_limit_price}";
                        var response = ticket.UpdateLimitPrice(ideal_limit_price, tag);
                        Log($"{tag}, Response: {response}");
                    }
                    if (newQuantity != 0 && newQuantity != ticket.Quantity)
                    {
                        var tag = $"{Time}: {ticket.Symbol} Quantity not good {ticket.Quantity}: Changing to ideal quantity: {newQuantity}";
                        var response = ticket.UpdateQuantity(newQuantity.Value, tag);
                        Log($"{tag}, Response: {response}");
                    }
                }
            }
        }
        public decimal RiskSpreadAdjustment(decimal spread, PortfolioRisk pfRisk, decimal pfDeltaUSDIf)
        {
            return spread * Math.Max(Math.Min(0, pfRisk.Delta100BpUSDTotal / 500), 1) * 0.5m * (pfRisk.Delta100BpUSDTotal - pfDeltaUSDIf) / pfRisk.Delta100BpUSDTotal;
        }

        public decimal QuantityExceedingMinimumBrokerageFee(Symbol symbol)
        {
            return 1 / 0.005m;  // 200. To be extended. max is 1% of trade value I think. 
        }

        public decimal SignalQuantity(Symbol symbol, OrderDirection orderDirection)
        {
            decimal absQuantity;
            /// Want to avoid minimum fee payment of 1 USD/stock trade, hence looking to hit a delta that causes at least an equity fee of 1 USD during hedding and minimizes an absolute delta increase.
            /// So the target delta is +/-200.
            /// Hacky way to get quantities, risk, PnL of different underlyings on approximately the same live. HPE costing ~15 would typically have a quantity ~7 times higher than AKAM: ~100
            decimal midPrice = MidPrice(Underlying(symbol));

            var absTargetDelta = QuantityExceedingMinimumBrokerageFee(symbol); // Make the hedge worthwile
            var currentDelta = pfRisk.RiskByUnderlying(symbol.Underlying, Metric.DeltaTotal);
            var deltaPerUnit = pfRisk.RiskAddedIfFilled(symbol, DIRECTION2NUM[orderDirection] * 1, Metric.DeltaTotal);
            if (deltaPerUnit * currentDelta > 0) // same direction. Increase risk up to 200 more.
            {
                absQuantity = Math.Abs((absTargetDelta - Math.Abs(currentDelta)) / deltaPerUnit);
            }
            else // opposite direction and zero.
            {
                absQuantity = Math.Abs(absTargetDelta / deltaPerUnit);
            }

            absQuantity = Math.Round(Math.Max(absQuantity, 1), 0);

            // Limit Portfolio Quantity to configurable 20.

            return DIRECTION2NUM[orderDirection] * Math.Min(10, absQuantity);
        }

        //public void HedgePortfolioRiskIs()
        //{
        //    /    todo: This needs to go through central risk and other trade processing function. Dont circumvent.
        //    /    This event should be triggered when
        //    /    - the absolute risk changes significantly requiring updates to ticket prices or new hedges.
        //    /    - Many small changes will result in a big net delta change, then olso hedge...
        //    /   Simplifying assumption: There is at most one contract per option contract
        //    /   //    # Missing the usual Signal -> Risk check here. Suddenly placing orders for illiquid option.
        //    foreach (var tickets in orderTickets.Values)
        //    {
        //        foreach (var t in tickets)
        //        {
        //            decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(t.Symbol, t.Quantity);
        //             0 to be replace with HedgeBand target, once DPfDeltaFilled returns a decent number
        //            if (dPfDeltaIf * pfRisk.DeltaSPYUnhedged100BpUSD > 0)
        //            {
        //                OrderDirection orderDirection = NUM2DIRECTION[Math.Sign(t.Quantity)];
        //                decimal newPrice = PriceOptionPfRiskAdjusted((Option)Securities[t.Symbol], pfRisk, orderDirection);
        //                newPrice = RoundTick(newPrice, TickSize(t.Symbol));
        //                if (newPrice == 0)
        //                {
        //                    Debug($"Failed to get price for {t.Symbol} while hedging portfolio looking to update existing tickets..");
        //                    continue;
        //                }
        //                if (t.UpdateRequests?.Count > 0 && t.UpdateRequests.Last()?.LimitPrice == newPrice)
        //                {
        //                    continue;
        //                }
        //                        tag = f'Update Ticket: {ticket}. Set Price: {new_price} from originally: {limit_price}'
        //                        algo.Log(humanize(ts=
        //                        , topic="HEDGE MORE AGGRESSIVELY", symbol=str(ticket.Symbol), current_price=limit_price, new_price=new_price))
        //                t.UpdateLimitPrice(newPrice);
        //            }
        //        }
        //    }
        //}

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

        ///// <summary>
        ///// Objectives:
        ///// 1. Dont cancel tickets that reduce an absolute position. Need to flip.
        ///// 2. Cancel any open tickets that would increase the risk by Underlying, except for flipping tickets...
        ///// 3. This function is oddly portfoliowide....
        ///// </summary>
        //public void CancelRiskIncreasingOrderTickets(RiskLimitType riskLimitType)
        //{
        //    decimal riskAddedIfFilled;
        //    decimal derivativesRiskByUnderlying;
            
        //    var tickets = orderTickets.Values.SelectMany(x => x).Where(t => !skipOrderStatus.Contains(t.Status)).ToList();  // ToList() -> Avoids concurrent modification error
        //    var ticketToCancel = new List<OrderTicket>();
        //    foreach (var ticketsByUnderlying in tickets.GroupBy(t => Underlying(t.Symbol)))
        //    {
        //        OrderTicket tg = ticketsByUnderlying.First();
        //        derivativesRiskByUnderlying = riskLimitType switch 
        //        {
        //            RiskLimitType.Delta => pfRisk.DerivativesRiskByUnderlying(tg.Symbol, Metric.Delta100BpUSDTotal),
        //            RiskLimitType.Gamma => pfRisk.DerivativesRiskByUnderlying(tg.Symbol, Metric.Gamma100BpUSDTotal),
        //            _ => throw new NotImplementedException(riskLimitType.ToString()),
        //        };
        //        //if (  // Only cancel when risk limit are so high, that no buy/sell signals follow.
        //        //    riskLimitType == RiskLimitType.Gamma && derivativesRiskByUnderlying < 0 && !(derivativesRiskByUnderlying < pfRisk.RiskBandByUnderlying(tg.Symbol, Metric.GammaLowerStopSelling))
        //        //    || riskLimitType == RiskLimitType.Gamma && derivativesRiskByUnderlying > 0 && !(derivativesRiskByUnderlying > pfRisk.RiskBandByUnderlying(tg.Symbol, Metric.GammaUpperStopBuying))
        //        //    )
        //        //{
        //        //    continue;
        //        //}

        //        foreach (var t in ticketsByUnderlying)
        //        {
        //            switch (riskLimitType)
        //            {
        //                case RiskLimitType.Delta:
        //                    riskAddedIfFilled = pfRisk.RiskAddedIfFilled(t.Symbol, t.Quantity, Metric.Delta100BpUSDTotal);
        //                    var currentRisk = pfRisk.DerivativesRiskByUnderlying(t.Symbol, Metric.Delta100BpUSDTotal);
        //                    if (!IsFlippingTicket(t) && (riskAddedIfFilled * currentRisk > 0 ))
        //                    {
        //                        QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "msg", $"CancelRiskIncreasingOrderTickets {t.Symbol}, riskLimitType={riskLimitType}, riskAddedIfFilled={riskAddedIfFilled}, derivativesRiskByUnderlying={derivativesRiskByUnderlying}" } });
        //                        ticketToCancel.Add(t);
        //                    }
        //                    break;
        //                //case RiskLimitType.Gamma:
        //                //    riskAddedIfFilled = pfRisk.RiskAddedIfFilled(t.Symbol, t.Quantity, Metric.Gamma100BpUSDTotal);
        //                //    if (riskAddedIfFilled * derivativesRiskByUnderlying > 0)  // same sign; non-null
        //                //    {
        //                //        QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "msg", $"CancelRiskIncreasingOrderTickets {t.Symbol}, riskLimitType={riskLimitType}, riskAddedIfFilled={riskAddedIfFilled}, derivativesRiskByUnderlying={derivativesRiskByUnderlying}" } });
        //                //        ticketToCancel.Add(t);
        //                //    }
        //                //    break;
        //            }
        //        }
        //    }
        //    ticketToCancel.ForEach(t => t.Cancel());
        //}

        //public bool IsFlippingTicket(OrderTicket ticket)
        //{
        //    Symbol symbol = ticket.Symbol;
        //    if (symbol.ID.SecurityType != SecurityType.Option) { return false; }

        //    var position = Portfolio[symbol].Quantity;
        //    if (position == 0) { return false; }

        //    return ticket.Quantity * position < 0;
        //}

        /// <summary>
        /// Good for flipping. Bad for risk reduction. But flipping is more important...
        /// </summary>
        //public void OrderOppositeOrder(Symbol symbol)
        //{
        //    //if (symbol.Value.StartsWith("HPE") && (new List<DateTime>() { new DateTime(2023, 5, 30).Date, new DateTime(2023, 6, 1) }).Contains(Time.Date))
        //    //{
        //    //    return;
        //    //}
        //    if (symbol.ID.SecurityType != SecurityType.Option) { return; }

        //    var position = Portfolio[symbol].Quantity;
        //    if (position == 0) { return; }

        //    if (orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets))
        //    {
        //        if (tickets.Sum(t => t.Quantity) * position < 0) 
        //        {
        //            // Already having liquidating tickets
        //            return;
        //        }
        //    }

        //    //if (!PermittedOrderDirectionsFromGamma(symbol).Contains(NUM2DIRECTION[Math.Sign(-position)])) { 
        //    //    return; 
        //    //}

        //    // IV Very High - Dont buy amymore
        //    //if (position < 0 && RollingIVBid[symbol].EWMASlow < RollingIVBid[symbol].EWMA * 1.2)
        //    //{
        //    //    Log($"{Time} - IV very high. Not buying anymore.");
        //    //    return;
        //    //}
        //    //if (position > 0 && RollingIVAsk[symbol].EWMASlow * 0.8 > RollingIVAsk[symbol].EWMA * 1.2)
        //    //{
        //    //    Log($"{Time} - IV very low. Not selling anymore.");
        //    //    return;
        //    //}

        //    // If here in function. No liquidating tickets.
        //    Log($"{Time} - Ordering order opposite to {symbol}");
        //    OrderOptionContract((Option)Securities[symbol], -position, OrderType.Limit);
        //}

        //public void OrderOppositeOrders()
        //{
        //    // Get all Securities with non-zero position. Would not get through as it's not yet time to trade....
        //    var nonZeroPositions = Portfolio.Values.Where(x => x.Invested && x.Type == SecurityType.Option);
        //    foreach (var position in nonZeroPositions)
        //    {
        //        OrderOppositeOrder(position.Symbol);
        //    }
        //}

        public IEnumerable<Symbol> SymbolsATM(Symbol symbol)
        {
            if (symbol.ID.SecurityType != SecurityType.Option) return new List<Symbol> { };

            Option option = (Option)Securities[symbol];

            if (!optionChains.ContainsKey(symbol.Underlying)) { PopulateOptionChains(); }
            if (optionChains.TryGetValue(symbol.Underlying, out HashSet<Option> optionContracts))
            {
                var priceUnderlying = MidPrice(option.Underlying.Symbol);

                // get the contracts whose strike prices are next above and below the underlying price
                //var strikeAbove = optionContracts.Where(c => c.StrikePrice >= priceUnderlying).Min(c => c.StrikePrice) ?? priceUnderlying;
                var strikeAbove = optionContracts.Where(c => c.StrikePrice >= priceUnderlying).Select(c => c.StrikePrice).DefaultIfEmpty(priceUnderlying).Min();
                var strikeBelow = optionContracts.Where(c => c.StrikePrice < priceUnderlying).Select(c => c.StrikePrice).DefaultIfEmpty(priceUnderlying).Max();  // may not have matching contracts in Securities. Illiquid requires refactor.

                return optionContracts.Where(c => c.StrikePrice == strikeAbove || c.StrikePrice == strikeBelow).Select(c => c.Symbol);
            }
            else
            {
                return new List<Symbol> { };
            }
        }

        //public double IVAtm(Symbol symbol)
        //{
        //    // refactor to use ATM symbols of canoical option derived from symbol argument.
        //    var symbolsAtm = SymbolsATM(symbol);
        //    if (!symbolsAtm.Any()) return 0;

        //    double bidIV = symbolsAtm.Select(sym => RollingIVBid[sym].Current.IV).Average();
        //    double askIV = symbolsAtm.Select(sym => RollingIVAsk[sym].Current.IV).Average();
        //    double midIV = (bidIV + askIV) / 2;
        //    return midIV;
        //}

        //public void EmitNewFairOptionPrices(Symbol symbol)
        //{
        //    if (symbol.SecurityType != SecurityType.Equity)
        //    {
        //        return;
        //    }
        //    foreach (Symbol sym in orderTickets.Keys.Where(sym => sym.SecurityType == SecurityType.Option && sym.ID.Underlying.Symbol == symbol))
        //    {
        //        if (!orderTickets[sym].Any()) continue;
        //        var option = (Option)Securities[sym];
        //        decimal? price = PriceOptionPfRiskAdjusted(option, OrderDirection.Buy);
        //        if (price != null && price != fairOptionPrices.GetValueOrDefault(option, null))
        //        {
        //            fairOptionPrices[option] = price;
        //            PublishEvent(new EventNewFairOptionPrice(option.Symbol, (decimal)price));
        //        }
        //    }
        //}
    }
}
