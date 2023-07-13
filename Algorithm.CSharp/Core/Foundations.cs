using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class Bounds
    {
        private List<Tuple<DateTime, double>> _lowerBounds;
        private List<Tuple<DateTime, double>> _upperBounds;
        private List<Tuple<DateTime, double>> _impliedVolatility;
        private double _lastLowerBound;
        private double _lastUpperBound;
        private double _lastImpliedVolatility;

        public Bounds(Dictionary<DateTime, double> lowerBounds, Dictionary<DateTime, double> upperBounds, Dictionary<DateTime, double> impliedVolatility)
        {
            _lowerBounds = lowerBounds.Select(pair => Tuple.Create(pair.Key, pair.Value)).ToList();
            _upperBounds = upperBounds.Select(pair => Tuple.Create(pair.Key, pair.Value)).ToList();
            _impliedVolatility = impliedVolatility.Select(pair => Tuple.Create(pair.Key, pair.Value)).ToList();
        }
        public double UpperBound(DateTime dt)
        {
            int i;
            for (i = 0; i < _upperBounds.Count; i++)
            {
                var kvp = _upperBounds[i];
                if (kvp.Item1 < dt)
                {
                    _lastUpperBound = kvp.Item2;
                }
                else
                {
                    break;
                }
            }
            _upperBounds.RemoveRange(0, i);
            return _lastUpperBound;
        }

        public double LowerBound(DateTime dt)
        {
            int i;
            for (i = 0; i < _lowerBounds.Count; i++)
            {
                var kvp = _lowerBounds[i];
                if (kvp.Item1 < dt)
                {
                    _lastLowerBound = kvp.Item2;
                }
                else
                {
                    break;
                }
            }
            _lowerBounds.RemoveRange(0, i);
            return _lastLowerBound;
        }

        public double ImpliedVolatility(DateTime dt)
        {
            int i;
            for (i = 0; i < _impliedVolatility.Count; i++)
            {
                var kvp = _impliedVolatility[i];
                if (kvp.Item1 < dt)
                {
                    _lastImpliedVolatility = kvp.Item2;
                }
                else
                {
                    break;
                }
            }
            _impliedVolatility.RemoveRange(0, i);
            return _lastImpliedVolatility;
        }
    }
    public partial class Foundations : QCAlgorithm
    {
        public Resolution resolution;
        public double minBeta;
        public List<OrderEvent> OrderEvents = new();
        public Dictionary<Symbol, List<OrderTicket>> orderTickets = new();
        public OrderType orderType;
        public List<string> hedgeTicker;
        public List<string> optionTicker;
        public List<string> ticker;
        public List<Symbol> equities = new();
        public List<Symbol> options = new();  // Canonical symbols
        public Dictionary<Symbol, HashSet<Option>> optionChains = new();
        public LinkedList<PortfolioRisk> pfRisks;
        public MMWindow mmWindow;
        public Symbol spy;
        public Dictionary<Symbol, SecurityCache> PriceCache = new();
        public SecurityExchangeHours securityExchangeHours;

        public Dictionary<Symbol, IVBid> IVBids = new();
        public Dictionary<Symbol, IVAsk> IVAsks = new();
        public Dictionary<Symbol, RollingIVIndicator<IVBidAsk>> RollingIVBid = new();
        public Dictionary<Symbol, RollingIVIndicator<IVBidAsk>> RollingIVAsk = new();

        public double IVLongShortBand = 10.005;
        public double IVLongShortBandCancel = 10.01;
        //public double IVLongShortBand = 10.0;

        public TickCounter TickCounterFilter;
        public TickCounter TickCounterOnData;
        public PortfolioRisk pfRisk;
        public bool OnWarmupFinishedCalled = false;
        public decimal TotalPortfolioValueSinceStart = 0m;
        public Dictionary<int, OrderFillData> orderFillDataTN1 = new();
        public Dictionary<OrderDirection, double> hedgingVolatilityBias = new() { { OrderDirection.Buy, 0.01 }, { OrderDirection.Sell, -0.01 } };
        public Dictionary<string, Dictionary<DateTime, List<List<Dictionary<DateTime, double>>>>> IVBounds;
        public Dictionary<Tuple<string, DateTime>, Bounds> SymbolBounds = new();

        public record MMWindow(TimeSpan Start, TimeSpan End);

        public decimal MidPrice(Symbol symbol)
        {
            var security = Securities[symbol];
            return (security.AskPrice + security.BidPrice) / 2;
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

        public List<Signal> GetSignals()
        {
            List<Signal> signals = new List<Signal>();
            foreach (Security sec in Securities.Values)
            {
                if (
                sec.IsTradable
                && sec.Type == SecurityType.Option
                && !sec.Symbol.IsCanonical()
                && sec.BidPrice != 0
                && sec.AskPrice != 0
                && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                && RollingIVBid[sec.Symbol].IsReady
                && RollingIVAsk[sec.Symbol].IsReady
                )
                {
                    Symbol symbol = sec.Symbol;

                    var permittedOrderDirectionsFromVolatility = PermittedOrderDirectionsFromVolatility(symbol, IVLongShortBand);
                    var permittedOrderDirectionsFromPortfolio = PermittedOrderDirectionsFromPortfolio(symbol);
                    var permittedOrderDirections = permittedOrderDirectionsFromVolatility.Intersect(permittedOrderDirectionsFromPortfolio);
                    if (permittedOrderDirections.Contains(OrderDirection.Buy))
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Buy));
                    }
                    // Only vega short!
                    if (permittedOrderDirections.Contains(OrderDirection.Sell))
                    {
                        signals.Add(new Signal(symbol, OrderDirection.Sell));
                    }
                }
            }

            var nonZeroPositions = Portfolio.Values.Where(x => x.Invested && x.Type == SecurityType.Option);
            foreach (var position in nonZeroPositions)
            {
                var direction = NUM2DIRECTION[-Math.Sign(position.Quantity)];
                var signal = new Signal(position.Symbol, direction);
                if (!signals.Contains(signal))
                {
                    signals.Add(signal);
                }
            }

            // Order the signals to be alternatingly buy and sell as long as both remain in list
            return signals;
        }

        /// <summary>
        /// 2 objectives:
        /// 1. Only 1 deal per contract. At the moment. IB wouldnt allow opposite orders.
        /// 2. Flip positions. Have a buy/sell order for every position we are holding.
        /// </summary>
        /// <param name="signals"></param>
        /// <returns></returns>
        public List<Signal> FilterSignalByRisk(List<Signal> signals)
        {

            List<Signal> signals_out = new List<Signal>();
            foreach (var signal in signals)
            {
                Option contract = Securities[signal.Symbol] as Option;
                int order_direction_sign = DIRECTION2NUM[signal.OrderDirection];

                // Only 1 deal per contract. At the moment. IB wouldnt allow opposite orders. Existing one would get cancelled in other functions.
                // Bad direction needs to be cancelled first...
                if (orderTickets.ContainsKey(contract.Symbol) && orderTickets[contract.Symbol].Any())
                {
                    continue;
                }

                // Exclude contracts that would increase absolute risk. Means we're already long/short and let the liquidating ticket through.
                decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(contract.Symbol, DIRECTION2NUM[signal.OrderDirection]);
                decimal derivativesRiskByUnderlying = pfRisk.DerivativesRiskByUnderlying(contract.Symbol, Metric.DeltaTotal);
                if (dPfDeltaIf * derivativesRiskByUnderlying > 0)  // same sign, risk would grow. dont signal.
                {
                    // Seems to fail at startup...
                    continue;
                }

                // At the moment, dont trade contracts without Bid or Ask
                if (contract.BidPrice == 0 || contract.AskPrice == 0)
                {
                    continue;
                }

                // Only 1 deal per contract. At the moment. IB wouldnt allow opposite orders.
                // Without any position. Buy comes first here and sell gets rejected. That's bad. Skews delta towards Buy.
                if (signals_out.Any(x => x.Symbol == signal.Symbol))
                {
                    continue;
                }

                signals_out.Add(signal);
            }
            return signals_out;
        }

        public void CancelOpenTickets()
        {
            if (IsWarmingUp)
            {
                return;
            }
            foreach (var tickets in orderTickets.Values)
            {
                foreach (var t in tickets.Where(t => t.Status != OrderStatus.Invalid))
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

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return midPriceUnderlying > 0
                && symbol.ID.Date > Time + TimeSpan.FromDays(10)
                && symbol.ID.Date < Time + TimeSpan.FromDays(90)
                && symbol.ID.OptionStyle == OptionStyle.American
                && symbol.ID.StrikePrice >= midPriceUnderlying * 0.80m
                && symbol.ID.StrikePrice <= midPriceUnderlying * 1.2m
                && IsLiquid(symbol, 5, Resolution.Daily)
                ;
                //&& symbol.ID.StrikePrice % 0.05m != 0m;  // This condition is somewhat strange here. Revise and move elsewhere. Beware of not buying those 5 Cent options. Should have been previously filtered out. Yet another check
        }

        public void RemoveUniverseSecurity(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                    !ContractInScope(symbol)
                    && Portfolio[symbol].Quantity == 0
                    && Securities[symbol].IsTradable
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
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"security {security} not marked tradeable. Should not be sent as signal. Not trading..." } });
                return false;
            }

            // Timing
            if (IsWarmingUp || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End)
            {
                // Risk of opening jumps....
                Debug("No time to trade...");
                return false;
            }

            // Only 1 ticket per Symbol & Side
            if (orderTickets.ContainsKey(symbol))
            {
                foreach (var ticket in orderTickets[symbol])
                {
                    if (ticket.Quantity * quantity >= 0)
                    {
                        Debug($"{Time} topic=BAD ORDER. IsOrderValid. {symbol}. Already have an order ticket with same sign. For now only want 1 order. Not processing...");
                        return false;
                    }

                    if (ticket.Quantity * quantity <= 0)
                    {
                        Debug($"{Time} topic=BAD ORDER. IsOrderValid. {symbol}. IB does not allow opposite-side simultaneous order. Not processing...");
                        return false;
                    }
                }
            }

            if (Portfolio[symbol].Quantity * quantity > 0)
            {
                Debug($"{Time} topic=BAD ORDER. IsOrderValid. {symbol}. Already have a position with same sign. Not processing...");
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

        public void OrderOptionContract(Option contract, decimal quantity, OrderType orderType = OrderType.Limit)
        {
            if (!IsOrderValid(contract.Symbol, quantity)) { return; }

            OrderDirection orderDirection = NUM2DIRECTION[Math.Sign(quantity)];

            decimal limitPrice = PriceOptionPfRiskAdjusted(contract, orderDirection);
            if (limitPrice == 0)
            {
                Log($"No price for {contract.Symbol}. Not trading...");
                return;
            }
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));

            string tag = LogContract(contract, orderDirection, limitPrice, orderType);

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
                    var orderDirection = NUM2DIRECTION[Math.Sign(ticket.Quantity)];
                    var ideal_limit_price = PriceOptionPfRiskAdjusted(contract, orderDirection);
                    ideal_limit_price = RoundTick(ideal_limit_price, tick_size_);
                    if (ticket.UpdateRequests.Count > 0 && ticket.UpdateRequests?.Last().LimitPrice == ideal_limit_price)
                    {
                        continue;
                    }
                    else if (!IsFlippingTicket(ticket) && !PermittedOrderDirectionsFromVolatility(contract.Symbol, IVLongShortBandCancel).Contains(orderDirection))
                    //else if (!PermittedOrderDirectionsFromVolatility(contract.Symbol, IVLongShortBandCancel).Contains(orderDirection))                        
                    {
                        Log($"{Time}: CANCEL LIMIT Symbol{contract.Symbol}: ideal_limit_price = 0. Presumably IV too far gone...");
                        ticket.Cancel();
                    }
                    else if (Math.Abs(ideal_limit_price - limit_price) > tick_size_ && ideal_limit_price >= tick_size_)
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
                            Log($"{tag}, Response: {response}");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Overridden by flipping ticket. Need to compare ATM IVs with ATM IVs, not cross expiry or cross strikes...
        /// </summary>
        public HashSet<OrderDirection> PermittedOrderDirectionsFromVolatility(Symbol symbol, double band)
        {
            return new HashSet<OrderDirection>() { OrderDirection.Sell };
            var permittedOrderDirections = new HashSet<OrderDirection>();
            if (symbol.SecurityType != SecurityType.Option)
            {
                return permittedOrderDirections;
            }
            
            var underlying = Underlying(symbol);

            var bounds = SymbolBounds.TryGetValue(Tuple.Create(underlying.Value.ToLower(), symbol.ID.Date), out var bound) ? bound : null;
            if (bounds == null)
            {
                return permittedOrderDirections;
            }

            var lowerS1bound = bounds.LowerBound(Time);
            var upperS1bound = bounds.UpperBound(Time);
            var iv = bounds.ImpliedVolatility(Time);
            
            if (lowerS1bound == 0 || upperS1bound == 0)
            {
                return permittedOrderDirections;
            }

            // OK vega long!

            //if ((RollingIVBid[symbol].LongMean + band) > RollingIVAsk[symbol].Current)
            if (upperS1bound > iv)
            {
                permittedOrderDirections.Add(OrderDirection.Buy);
            }
            
            // OK vega short!
            //if ((RollingIVAsk[symbol].LongMean - band) < RollingIVAsk[symbol].Current)
            if (lowerS1bound < iv)
            {
                permittedOrderDirections.Add(OrderDirection.Sell);
            }
            return permittedOrderDirections;
        }

        /// <summary>
        /// Overridden by flipping ticket
        /// </summary>
        public HashSet<OrderDirection> PermittedOrderDirectionsFromPortfolio(Symbol symbol)
        {
            var permittedOrderDirections = new HashSet<OrderDirection>();

            if (Portfolio[symbol].Quantity <= 0)
            {
                permittedOrderDirections.Add(OrderDirection.Buy);
            }

            if (Portfolio[symbol].Quantity >= 0)
            {
                permittedOrderDirections.Add(OrderDirection.Sell);
            }
            return permittedOrderDirections;
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
            return spread * Math.Max(Math.Min(0, pfRisk.Delta100BpUSD / 500), 1) * 0.5m * (pfRisk.Delta100BpUSD - pfDeltaUSDIf) / pfRisk.Delta100BpUSD;
        }

        public void HandleSignals(List<Signal> signals)
        {
            // This event should be fired whenever
            //- an opportunity exists, ie, available slots in order book levels where I can take risk: This is usually not the case, hence can save performance by check and
            //turning into event.
            //- assumed risk is within risk limits
            //- there is enough margin to increase risk
            //- time to trade
            //    # Now it says, go trade, check for opportunities and time consuming scan begins...
            foreach (var signal in signals)
            {
                Option contract = Securities[signal.Symbol] as Option;
                decimal quantity = DIRECTION2NUM[signal.OrderDirection];
                OrderOptionContract(contract, quantity, OrderType.Limit);
            }
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
        //                        algo.Log(humanize(ts=algo.Time, topic="HEDGE MORE AGGRESSIVELY", symbol=str(ticket.Symbol), current_price=limit_price, new_price=new_price))
        //                t.UpdateLimitPrice(newPrice);
        //            }
        //        }
        //    }
        //}
        /// <summary>
        /// Objectives:
        /// 1. Dont cancel tickets that reduce an absolute position. Need to flip.
        /// 2. Cancel any open tickets that would increase the risk by Underlying, except for flipping tickets...
        /// 3. This function is oddly portfoliowide....
        /// </summary>
        public void CancelRiskIncreasingOrderTickets()
        {
            var skipOrderStatus = new HashSet<OrderStatus>() { OrderStatus.Canceled, OrderStatus.Filled, OrderStatus.Invalid, OrderStatus.CancelPending };
            // Make this more efficient. Group risk by underlying
            var tickets = orderTickets.Values.SelectMany(x => x).Where(t => !skipOrderStatus.Contains(t.Status));
            foreach (var t in tickets)
            {
                decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(t.Symbol, t.Quantity);
                decimal derivativesRiskByUnderlying = pfRisk.DerivativesRiskByUnderlying(t.Symbol, Metric.DeltaTotal);  // Needs to exclude actual underlying as it's just a hedge. 
                // 0 to be replace with HedgeBand target, once DPfDeltaFilled returns a decent number
                // if (dPfDeltaIf * pfRisk.DeltaSPYUnhedged100BpUSD > 0)
                if (!IsFlippingTicket(t) && dPfDeltaIf * derivativesRiskByUnderlying > 0 || dPfDeltaIf == 0)  // same sign; non-null
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "msg", $"CancelRiskIncreasingOrderTickets. {t.Symbol} dPfDeltaIf={dPfDeltaIf}, derivativesRiskByUnderlying={derivativesRiskByUnderlying}" } });
                    t.Cancel();
                }
            }
        }

        public bool IsFlippingTicket(OrderTicket ticket)
        {
            Symbol symbol = ticket.Symbol;
            if (symbol.ID.SecurityType != SecurityType.Option) { return false; }

            var position = Portfolio[symbol].Quantity;
            if (position == 0) { return false; }

            return ticket.Quantity * position < 0;
        }

        /// <summary>
        /// Good for flipping. Bad for risk reduction. But flipping is more important...
        /// </summary>
        public void OrderOppositeOrder(Symbol symbol)
        {
            if (symbol.ID.SecurityType != SecurityType.Option) { return; }

            var position = Portfolio[symbol].Quantity;
            if (position == 0) { return; }

            if (orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets))
            {
                if (tickets.Sum(t => t.Quantity) * position < 0) 
                {
                    // Already having liquidating tickets
                    return;
                }
            }

            // If here in function. No liquidating tickets.
            Log($"{Time} - Ordering order opposite to {symbol}");
            OrderOptionContract((Option)Securities[symbol], -position, OrderType.Limit);
        }

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

        public double IVAtm(Symbol symbol)
        {
            // refactor to use ATM symbols of canoical option derived from symbol argument.
            var symbolsAtm = SymbolsATM(symbol);
            if (!symbolsAtm.Any()) return 0;

            double bidIV = symbolsAtm.Select(sym => RollingIVBid[sym].GetCurrentExOutlier()).Average();
            double askIV = symbolsAtm.Select(sym => RollingIVAsk[sym].GetCurrentExOutlier()).Average();
            double midIV = (bidIV + askIV) / 2;
            return midIV;
        }

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
