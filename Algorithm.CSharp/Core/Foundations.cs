using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Accord.Math;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using Newtonsoft.Json;
using QuantConnect.Configuration;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public Resolution resolution;
        public double minBeta;
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

        public TickCounter TickCounterFilter;
        
        public PortfolioRisk pfRisk;
        public bool OnWarmupFinishedCalled = false;
        public decimal TotalPortfolioValueSinceStart = 0m;
        public Dictionary<int, OrderFillData> orderFillDataTN1 = new();

        public string pathRiskRecords;
        public StreamWriter fileHandleRiskRecords;
        public Dictionary<(Symbol, string), StreamWriter> fileHandlesIVSurface = new();
        // Refactor this into the an object. Ideally inferred from object attributes.
        public readonly List<string> riskRecordsHeader = new() { "Time", "Symbol",
            "DeltaTotal", "Delta100BpUSDTotal", "Delta100BpUSDOptionsTotal", "GammaTotal", "Gamma100BpUSDTotal",
            "VegaTotal", "ThetaTotal", "PositionUSD", "PositionUnderlying", "PositionUnderlyingUSD", "PositionOptions", "PositionOptionsUSD", "PnL", "MidPriceUnderlying", };

        public EarningsAnnouncement[] EarningsAnnouncements;
        
        public int OptionOrderQuantityDflt = JsonConvert.DeserializeObject<int>(Config.Get("OptionOrderQuantityDflt"));
        public decimal delta100BpTotalUpperBandStopSelling = JsonConvert.DeserializeObject<decimal>(Config.Get("delta100BpTotalUpperBandStopSelling"));
        public decimal delta100BpTotalLowerBandStopSelling = JsonConvert.DeserializeObject<decimal>(Config.Get("delta100BpTotalLowerBandStopSelling"));
        public decimal scopeContractStrikeOverUnderlyingMax = JsonConvert.DeserializeObject<decimal>(Config.Get("scopeContractStrikeOverUnderlyingMax"));
        public decimal scopeContractStrikeOverUnderlyingMin = JsonConvert.DeserializeObject<decimal>(Config.Get("scopeContractStrikeOverUnderlyingMin"));
        public decimal scopeContractStrikeOverUnderlyingMargin = JsonConvert.DeserializeObject<decimal>(Config.Get("scopeContractStrikeOverUnderlyingMargin"));
        public int scopeContractMinDTE = JsonConvert.DeserializeObject<int>(Config.Get("scopeContractMinDTE"));
        public int scopeContractMaxDTE = JsonConvert.DeserializeObject<int>(Config.Get("scopeContractMaxDTE"));
        public int scopeContractIsLiquidDays = JsonConvert.DeserializeObject<int>(Config.Get("scopeContractIsLiquidDays"));

        public decimal GammaUpperStopBuying = JsonConvert.DeserializeObject<decimal>(Config.Get("GammaUpperStopBuying"));
        public decimal GammaLowerStopSelling = JsonConvert.DeserializeObject<decimal>(Config.Get("GammaLowerStopSelling"));
        public decimal GammaUpperContinuousHedge = JsonConvert.DeserializeObject<decimal>(Config.Get("GammaUpperContinuousHedge"));
        public decimal GammaLowerContinuousHedge = JsonConvert.DeserializeObject<decimal>(Config.Get("GammaLowerContinuousHedge"));
        public Dictionary<Symbol, RiskDiscount> DeltaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> GammaDiscounts = new();
        public Dictionary<Symbol, RiskDiscount> EventDiscounts = new();

        public HashSet<OrderStatus> orderStatusFilled = new() { OrderStatus.Filled, OrderStatus.PartiallyFilled };
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
                //&& !(sec.Symbol.Value.StartsWith("HPE") && (new List<DateTime>() { new DateTime(2023, 5, 30).Date, new DateTime(2023, 6, 1) }).Contains(Time.Date))
                && sec.Type == SecurityType.Option
                // to be review with Gamma hedging. Selling option at ultra-high, near-expiry IVs with great gamma hedge could be extra profitable.
                && (sec.Symbol.ID.Date - Time.Date).Days > 2  //  Currently unable to handle the unpredictable underlying dynamics in between option epiration and ITM assignment.
                && (sec.Symbol.ID.Date <= Time.Date + TimeSpan.FromDays(30))  //  Sell front month. Hedge with back month.
                && !sec.Symbol.IsCanonical()
                && sec.BidPrice != 0
                && sec.AskPrice != 0
                && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                && RollingIVStrikeBid[sec.Symbol.Underlying].IsReady
                && RollingIVStrikeAsk[sec.Symbol.Underlying].IsReady
                && !liquidateTicker.Contains(sec.Symbol.Underlying.Value)  // No new orders, Function oppositeOrder & hedger handle slow liquidation at decent prices.
                )
                {
                    Symbol symbol = sec.Symbol;

                    var permittedOrderDirectionsFromVolatility = PermittedOrderDirectionsFromVolatility(symbol);
                    var permittedOrderDirectionsFromPortfolio = PermittedOrderDirectionsFromPortfolio(symbol);
                    var permittedOrderDirections = permittedOrderDirectionsFromVolatility.Intersect(permittedOrderDirectionsFromPortfolio);

                    // Only vega short!
                    if (permittedOrderDirections.Contains(OrderDirection.Sell))
                    {
                        //if (RollingIVAsk[symbol].EWMASlow > RollingIVAsk[symbol].EWMA)
                        //{
                        //    continue;
                        //}
                        signals.Add(new Signal(symbol, OrderDirection.Sell));
                    }
                    if (permittedOrderDirections.Contains(OrderDirection.Buy))
                    {
                        //if (RollingIVBid[symbol].EWMASlow < RollingIVBid[symbol].EWMA * 1.2)
                        //{
                        //    continue;
                        //}
                        signals.Add(new Signal(symbol, OrderDirection.Buy));
                    }
                }
            }

            // Opposite orders
            var nonZeroPositions = Portfolio.Values.Where(x => x.Invested && x.Type == SecurityType.Option);
            foreach (var position in nonZeroPositions)
            {
                //if (position.Symbol.Value.StartsWith("HPE") && (new List<DateTime>() { new DateTime(2023, 5, 30).Date, new DateTime(2023, 6, 1) }).Contains(Time.Date))
                //{
                //    continue;
                //}
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
                // Not feasible for fast market making. Need to turn over captital. Only stop selling when delta is beyond some threshold..
                decimal dPfDeltaIf = pfRisk.RiskAddedIfFilled(contract.Symbol, DIRECTION2NUM[signal.OrderDirection], Metric.Delta100BpUSDTotal);
                decimal pfRiskIfFilled = dPfDeltaIf + pfRisk.DerivativesRiskByUnderlying(contract.Symbol, Metric.Delta100BpUSDTotal);
                if (pfRiskIfFilled < delta100BpTotalLowerBandStopSelling || pfRiskIfFilled > delta100BpTotalUpperBandStopSelling)
                //if (dPfDeltaIf * derivativesRiskByUnderlying > 0)  // same sign, risk would grow. dont signal.
                // rather want o manage this via pricing logic. risk increasing trades are progressively priced worse...
                // reason for keeping this is IBs restriction to trade on both sides of limit order... Cannot hedge with this 
                {
                    continue;
                }

                // if both sell and buy is allow given current risk profile, prefer selling ovre buying for near maturities.

                // At the moment, dont trade contracts without Bid or Ask
                if (contract.BidPrice == 0 || contract.AskPrice == 0)
                {
                    continue;
                }

                // Only 1 deal per contract. At the moment. IB wouldnt allow opposite orders. Preferring Sells, hence those comes first after the delta check.
                // Without any position. Buy comes first here and sell gets rejected. That's bad. Skews delta towards Buy.
                if (signals_out.Any(x => x.Symbol == signal.Symbol))
                {
                    continue;
                }

                // Respect trade regime embargo around earnings announcements. No new trade signals. Reducing risk still goes on in other functions!
                if (EarningsAnnouncements.Where(ea => ea.Symbol == contract.Symbol && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any())
                {
                    continue;
                }

                var permittedOrderDirectionsFromGamma = PermittedOrderDirectionsFromGamma(signal.Symbol);
                if (!permittedOrderDirectionsFromGamma.Contains(signal.OrderDirection))
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

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null, decimal margin=0m)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return midPriceUnderlying > 0
                && symbol.ID.Date > Time + TimeSpan.FromDays(scopeContractMinDTE)
                && symbol.ID.Date < Time + TimeSpan.FromDays(scopeContractMaxDTE)
                && symbol.ID.OptionStyle == OptionStyle.American
                && symbol.ID.StrikePrice >= midPriceUnderlying * (scopeContractStrikeOverUnderlyingMin - margin)
                && symbol.ID.StrikePrice <= midPriceUnderlying * (scopeContractStrikeOverUnderlyingMax + margin)
                && IsLiquid(symbol, scopeContractIsLiquidDays, Resolution.Daily)
                ;
                //&& symbol.ID.StrikePrice % 0.05m != 0m;  // This condition is somewhat strange here. Revise and move elsewhere. Beware of not buying those 5 Cent options. Should have been previously filtered out. Yet another check
        }

        public void RemoveUniverseSecurity(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                    (
                    Securities[symbol].IsTradable
                    && !ContractInScope(symbol, margin: scopeContractStrikeOverUnderlyingMargin)
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
            if (orderTickets.ContainsKey(symbol))
            {
                foreach (var ticket in orderTickets[symbol])
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
        public HashSet<OrderDirection> PermittedOrderDirectionsFromGamma(Symbol symbol)
        {
            decimal total100BpGamma = pfRisk.RiskByUnderlying(Underlying(symbol), Metric.Gamma100BpUSDTotal); // Called way too often...

            decimal lowerBand = pfRisk.RiskBandByUnderlying(symbol, Metric.GammaLowerStopSelling);
            decimal upperBand = pfRisk.RiskBandByUnderlying(symbol, Metric.GammaUpperStopBuying);

            if (total100BpGamma > upperBand)
            {
                return new HashSet<OrderDirection>() { OrderDirection.Sell };
            }
            if (total100BpGamma < lowerBand)
            {
                return new HashSet<OrderDirection>() { OrderDirection.Buy };
            }

            return new HashSet<OrderDirection>() { OrderDirection.Buy, OrderDirection.Sell };
        }

        /// <summary>
        /// Overridden by flipping ticket. Need to compare ATM IVs with ATM IVs, not cross expiry or cross strikes...
        /// </summary>
        public HashSet<OrderDirection> PermittedOrderDirectionsFromVolatility(Symbol symbol)
        {
            return new HashSet<OrderDirection>() { OrderDirection.Sell };
            var permittedOrderDirections = new HashSet<OrderDirection>();
            if (symbol.SecurityType != SecurityType.Option)
            {
                return permittedOrderDirections;
            }
            
            var underlying = Underlying(symbol);

            //var bounds = SymbolBounds.TryGetValue(Tuple.Create(underlying.Value.ToLower(), symbol.ID.Date), out var bound) ? bound : null;
            //if (bounds == null)
            //{
            //    return permittedOrderDirections;
            //}

            //var lowerS1bound = bounds.LowerBound(Time);
            //var upperS1bound = bounds.UpperBound(Time);
            //var iv = bounds.ImpliedVolatility(Time);
            
            //if (lowerS1bound == 0 || upperS1bound == 0)
            //{
            //    return permittedOrderDirections;
            //}

            //// OK vega long!

            ////if ((RollingIVBid[symbol].LongMean + band) > RollingIVAsk[symbol].Current)
            //if (upperS1bound > iv)
            //{
            //    permittedOrderDirections.Add(OrderDirection.Buy);
            //}
            
            //// OK vega short!
            ////if ((RollingIVAsk[symbol].LongMean - band) < RollingIVAsk[symbol].Current)
            //if (lowerS1bound < iv)
            //{
            //    permittedOrderDirections.Add(OrderDirection.Sell);
            //}
            //return permittedOrderDirections;
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
            return spread * Math.Max(Math.Min(0, pfRisk.Delta100BpUSDTotal / 500), 1) * 0.5m * (pfRisk.Delta100BpUSDTotal - pfDeltaUSDIf) / pfRisk.Delta100BpUSDTotal;
        }

        public decimal SignalQuantity(Symbol symbol, OrderDirection orderDirection, decimal basePrice = 15.0m)
        {
            /// Hacky way to get quantities, risk, PnL of different underlyings on approximately the same live. HPE costing ~15 would typically have a quantity ~7 times higher than AKAM: ~100
            decimal midPrice = MidPrice(Underlying(symbol));
            return DIRECTION2NUM[orderDirection] * Math.Max(Math.Round(OptionOrderQuantityDflt * basePrice / midPrice), 1);
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
                decimal quantity = SignalQuantity(signal.Symbol, signal.OrderDirection);
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

        /// <summary>
        /// Objectives:
        /// 1. Dont cancel tickets that reduce an absolute position. Need to flip.
        /// 2. Cancel any open tickets that would increase the risk by Underlying, except for flipping tickets...
        /// 3. This function is oddly portfoliowide....
        /// </summary>
        public void CancelRiskIncreasingOrderTickets(RiskLimitType riskLimitType)
        {
            decimal riskAddedIfFilled;
            decimal derivativesRiskByUnderlying;
            
            var tickets = orderTickets.Values.SelectMany(x => x).Where(t => !skipOrderStatus.Contains(t.Status)).ToList();  // ToList() -> Avoids concurrent modification error
            var ticketToCancel = new List<OrderTicket>();
            foreach (var ticketsByUnderlying in tickets.GroupBy(t => Underlying(t.Symbol)))
            {
                OrderTicket tg = ticketsByUnderlying.First();
                derivativesRiskByUnderlying = riskLimitType switch 
                {
                    RiskLimitType.Delta => pfRisk.DerivativesRiskByUnderlying(tg.Symbol, Metric.DeltaTotal),
                    RiskLimitType.Gamma => pfRisk.DerivativesRiskByUnderlying(tg.Symbol, Metric.Gamma100BpUSDTotal),
                    _ => throw new NotImplementedException(riskLimitType.ToString()),
                };
                if (  // Only cancel when risk limit are so high, that no buy/sell signals follow.
                    riskLimitType == RiskLimitType.Gamma && derivativesRiskByUnderlying < 0 && !(derivativesRiskByUnderlying < pfRisk.RiskBandByUnderlying(tg.Symbol, Metric.GammaLowerStopSelling))
                    || riskLimitType == RiskLimitType.Gamma && derivativesRiskByUnderlying > 0 && !(derivativesRiskByUnderlying > pfRisk.RiskBandByUnderlying(tg.Symbol, Metric.GammaUpperStopBuying))
                    )
                {
                    continue;
                }

                foreach (var t in ticketsByUnderlying)
                {
                    switch (riskLimitType)
                    {
                        case RiskLimitType.Delta:
                            riskAddedIfFilled = pfRisk.RiskAddedIfFilled(t.Symbol, t.Quantity, Metric.Delta100BpUSDTotal);
                            decimal pfRiskIfFilled = riskAddedIfFilled + pfRisk.DerivativesRiskByUnderlying(t.Symbol, Metric.Delta100BpUSDTotal);
                            if (!IsFlippingTicket(t) && (pfRiskIfFilled < delta100BpTotalLowerBandStopSelling || pfRiskIfFilled > delta100BpTotalUpperBandStopSelling))
                            {
                                QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "msg", $"CancelRiskIncreasingOrderTickets {t.Symbol}, riskLimitType={riskLimitType}, riskAddedIfFilled={riskAddedIfFilled}, derivativesRiskByUnderlying={derivativesRiskByUnderlying}" } });
                                ticketToCancel.Add(t);
                            }
                            break;
                        case RiskLimitType.Gamma:
                            riskAddedIfFilled = pfRisk.RiskAddedIfFilled(t.Symbol, t.Quantity, Metric.Gamma100BpUSDTotal);
                            if (riskAddedIfFilled * derivativesRiskByUnderlying > 0)  // same sign; non-null
                            {
                                QuickLog(new Dictionary<string, string>() { { "topic", "CANCEL" }, { "msg", $"CancelRiskIncreasingOrderTickets {t.Symbol}, riskLimitType={riskLimitType}, riskAddedIfFilled={riskAddedIfFilled}, derivativesRiskByUnderlying={derivativesRiskByUnderlying}" } });
                                ticketToCancel.Add(t);
                            }
                            break;
                    }
                }
            }
            ticketToCancel.ForEach(t => t.Cancel());
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
            //if (symbol.Value.StartsWith("HPE") && (new List<DateTime>() { new DateTime(2023, 5, 30).Date, new DateTime(2023, 6, 1) }).Contains(Time.Date))
            //{
            //    return;
            //}
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

            if (!PermittedOrderDirectionsFromGamma(symbol).Contains(NUM2DIRECTION[Math.Sign(-position)])) { 
                return; 
            }

            // IV Very High - Dont buy amymore
            //if (position < 0 && RollingIVBid[symbol].EWMASlow < RollingIVBid[symbol].EWMA * 1.2)
            //{
            //    Log($"{Time} - IV very high. Not buying anymore.");
            //    return;
            //}
            //if (position > 0 && RollingIVAsk[symbol].EWMASlow * 0.8 > RollingIVAsk[symbol].EWMA * 1.2)
            //{
            //    Log($"{Time} - IV very low. Not selling anymore.");
            //    return;
            //}

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
