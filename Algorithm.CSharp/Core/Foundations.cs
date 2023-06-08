using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Statistics;
using QuantConnect.Algorithm.CSharp.Core.Events;
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
        public RiskLimit riskLimit;
        public Symbol spy;
        public HedgeBand HedgeBand = new();
        public Dictionary<Symbol, SecurityCache> PriceCache = new();
        public Dictionary<Symbol, IVBidAskIndicator> IVBidAsk = new();
        public Dictionary<Symbol, RollingWindowIVBidAskIndicator> RollingIVBidAsk = new();

        public record MMWindow(TimeSpan Start, TimeSpan End);
        public record RiskLimit(int? PositionsN = null, decimal? PositionsTotal = null, int? PositionSecurityTotal = null);

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

        public bool IsHighPortfolioRisk(LinkedList<PortfolioRisk> pfRisks)
        {
            // Either large change or constantly large risk.
            return Math.Abs(pfRisks.Last.Value.Delta100BpUSD - pfRisks.First.Value.Delta100BpUSD) / 2 > 500 || Math.Abs(pfRisks.Last.Value.Delta100BpUSD) > 1000;
        }

        public List<Signal> GetSignals()
        {
            List<Symbol> symbols = new List<Symbol>();
            foreach (Security sec in Securities.Values)
            {
                if (
                    sec.Type == SecurityType.Option 
                    && !sec.Symbol.IsCanonical() 
                    && sec.BidPrice != 0 
                    && sec.AskPrice != 0 
                    && IsLiquid(sec.Symbol, 5, Resolution.Daily)
                    && RollingIVBidAsk[sec.Symbol].IsReady
                    )
                {
                    symbols.Add(sec.Symbol);
                }
            }
            List<Signal> signals = new List<Signal>();
            foreach (Symbol symbol in symbols)
            {
                signals.Add(new Signal(symbol, OrderDirection.Buy));
            }
            foreach (Symbol symbol in symbols)
            {
                signals.Add(new Signal(symbol, OrderDirection.Sell));
            }
            return signals;
        }

        public List<Signal> FilterSignalByRisk(List<Signal> signals)
        {
            PortfolioRisk pfRisk = PortfolioRisk.E(this);
            bool exclude_non_invested = BreachedPositionLimit();
            List<Signal> signals_out = new List<Signal>();
            foreach (var signal in signals)
            {
                Option contract = Securities[signal.Symbol] as Option;
                int order_direction_sign = DIRECTION2NUM[signal.OrderDirection];
                if (orderTickets.ContainsKey(contract.Symbol))
                {
                    continue;
                }
                if (exclude_non_invested && contract.Holdings.Quantity * order_direction_sign < 0)  // Starting with a short position
                {
                    continue;
                }
                decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(contract.Symbol, DIRECTION2NUM[signal.OrderDirection]);
                if (dPfDeltaIf * pfRisk.DeltaSPYUnhedged100BpUSD > 0)
                {
                    continue;
                }
                // At the moment, dont trade contracts without Bid or Ask
                if (contract.BidPrice == 0 || contract.AskPrice == 0)
                {
                    continue;
                }
                // Dont accumulate a position in a particular security exceeding RiskLimit.PositionsSingleTotal
                if (Math.Abs(contract.Holdings.Quantity) * MidPrice(contract.Symbol) > riskLimit.PositionSecurityTotal)
                {
                    QuickLog(new Dictionary<string, string> { { "topic", "RISK FILTER" }, { "message", $"Position limit breached for {contract.Symbol}" } });
                    continue;
                }
                signals_out.Add(signal);
            }
            return signals_out;
        }

        public bool BreachedPositionLimit()
        {
            return PositionsN() >= riskLimit.PositionsN || PositionsTotal() >= riskLimit.PositionsTotal;
        }

        public void CancelOpenTickets()
        {
            if (IsWarmingUp)
            {
                return;
            }
            foreach (var tickets in orderTickets.Values)
            {
                foreach (var t in tickets)
                {
                    t.Cancel();
                }
            }
        }

        public void LogRiskSchedule()
        {
            if (IsWarmingUp) { return;  }
            if (IsMarketOpen(equities[0]))
            {
                LogRisk();
                LogPnL();
            }
        }

        public void PopulateOptionChains()
        {
            if (IsWarmingUp)
            {
                return;
            }
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

        public void IsRiskBandExceeded()
        {
            if (IsWarmingUp) { return; }
            PortfolioRisk pfRisk = PortfolioRisk.E(this);
            if (pfRisk.DeltaSPY100BpUSD > HedgeBand.DeltaLongUSD || pfRisk.DeltaSPY100BpUSD < HedgeBand.DeltaShortUSD)
            {
                PublishEvent(new EventRiskBandExceeded());
            }
        }

        public bool ContractInScope(Symbol symbol, decimal? priceUnderlying = null)
        {
            decimal midPriceUnderlying = priceUnderlying ?? MidPrice(symbol.ID.Underlying.Symbol);
            return midPriceUnderlying > 0
                && symbol.ID.Date > Time + TimeSpan.FromDays(60)
                && symbol.ID.OptionStyle == OptionStyle.American
                && symbol.ID.StrikePrice >= midPriceUnderlying * 0.9m
                && symbol.ID.StrikePrice <= midPriceUnderlying * 1.1m
                && IsLiquid(symbol, 5, Resolution.Daily);
                //&& symbol.ID.StrikePrice % 0.05m != 0m;  // This condition is somewhat strange here. Revise and move elsewhere. Beware of not buying those 5 Cent options. Should have been previously filtered out. Yet another check
        }

        public void OrderOptionContract(Option contract, decimal quantity, OrderType orderType = OrderType.Limit)
        {
            // Last Mile Checks
            // Timing
            if (mmWindow.End > Time.TimeOfDay && Time.TimeOfDay < mmWindow.Start)
            {
                Debug("No time to trade...");
                return;
            }
            // Only 1 ticket per Symbol & Side
            if (orderTickets.ContainsKey(contract.Symbol))
            {
                foreach (var ticket in orderTickets[contract.Symbol])
                {
                    if (ticket.Quantity * quantity >= 0)
                    {
                        Debug($"Already have an order ticket for {contract.Symbol} with same sign. Not processing...");
                        return;
                    }
                }
            }
            if (!ContractInScope(contract.Symbol))
            {
                QuickLog(new Dictionary<string, string>() { { "topic", "EXECUTION" }, { "msg", $"contract {contract.Symbol} is not in scope. Not trading..." } });
                return;
            }

            OrderDirection orderDirection = NUM2DIRECTION[Math.Sign(quantity)];

            decimal limitPrice = PriceOptionPfRiskAdjusted(contract, PortfolioRisk.E(this), orderDirection);
            if (limitPrice == 0)
            {
                Log($"No price for {contract.Symbol}. Not trading...");
                return;
            } 
            limitPrice = (decimal)limitPrice;
            limitPrice = RoundTick(limitPrice, TickSize(contract.Symbol));

            string tag = LogContract(contract, orderDirection, limitPrice, orderType);

            OrderTicket orderTicket = LimitOrder(contract.Symbol, quantity, limitPrice, tag);
            (orderTickets.TryGetValue(contract.Symbol, out List<OrderTicket> tickets)
                ? tickets  // if the key exists, use its value
                : orderTickets[contract.Symbol] = new List<OrderTicket>()) // create a new list if the key doesn't exist
            .Add(orderTicket);      
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
                UpdateLimitPriceEquity(Securities[symbol] as Equity);
            }
        }
        public void UpdateLimitPriceContract(Option contract)
        {
            foreach (var ticket in orderTickets[contract.Symbol])
            {
                if (ticket.Status == OrderStatus.Submitted || ticket.Status == OrderStatus.PartiallyFilled || ticket.Status == OrderStatus.UpdateSubmitted)
                {
                    var tick_size_ = TickSize(contract.Symbol);
                    var limit_price = ticket.Get(OrderField.LimitPrice);
                    var order_direction = ticket.Quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;
                    var ideal_limit_price = PriceOptionPfRiskAdjusted(contract, PortfolioRisk.E(this), order_direction);
                    ideal_limit_price = RoundTick(ideal_limit_price, tick_size_);
                    if (ticket.UpdateRequests.Count > 0 && ticket.UpdateRequests?.Last().LimitPrice == ideal_limit_price)
                    {
                        continue;
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
        public void UpdateLimitPriceEquity(Equity equity)
        {
            foreach (var ticket in orderTickets[equity.Symbol])
            {
                if (ticket.Status == OrderStatus.Submitted || ticket.Status == OrderStatus.PartiallyFilled || ticket.Status == OrderStatus.UpdateSubmitted)
                {
                    // bad bug. Canceled already, still went through and created market order... could ruin me 
                    continue;
                }
                //else if (LiveMode && ticket.UpdateRequests.Count > 1)  // Chasing the market. Risky. Market Order
                //{
                //    ticket.Cancel();
                //    MarketOrder(ticket.Symbol, ticket.Quantity, tag: ticket.Tag.Replace("Limit", "Market"));
                //}
                else
                {
                    decimal ts = TickSize(ticket.Symbol);
                    var limit_price = ticket.Get(OrderField.LimitPrice);
                    var ideal_limit_price = ticket.Quantity > 0 ? equity.BidPrice : equity.AskPrice;
                    if (RoundTick(ideal_limit_price, ts) != RoundTick(limit_price, ts) && ideal_limit_price > 0)
                    {
                        var tag = $"Price not good {limit_price}: Changing to ideal limit price: {ideal_limit_price}";
                        var response = ticket.UpdateLimitPrice(ideal_limit_price, tag);
                        Log($"{tag}, Response: {response}");
                    }
                }
            }
        }
        public decimal RiskSpreadAdjustment(decimal spread, PortfolioRisk pfRisk, decimal pfDeltaUSDIf)
        {
            return spread * Math.Max(Math.Min(0, pfRisk.Delta100BpUSD / 500), 1) * 0.5m * (pfRisk.Delta100BpUSD - pfDeltaUSDIf) / pfRisk.Delta100BpUSD;
        }

        // Please translate above Python code to C#. Thanks!
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
        public void HedgePortfolioRiskIs()
        {
            ///    todo: This needs to go through central risk and other trade processing function. Dont circumvent.
            ///    This event should be triggered when
            ///    - the absolute risk changes significantly requiring updates to ticket prices or new hedges.
            ///    - Many small changes will result in a big net delta change, then olso hedge...
            ///   Simplifying assumption: There is at most one contract per option contract
            ///   //    # Missing the usual Signal -> Risk check here. Suddenly placing orders for illiquid option.
            PortfolioRisk pfRisk = PortfolioRisk.E(this);
            foreach (var tickets in orderTickets.Values)
            {
                foreach (var t in tickets)
                {
                    decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(t.Symbol, t.Quantity);
                    // 0 to be replace with HedgeBand target, once DPfDeltaFilled returns a decent number
                    if (dPfDeltaIf * pfRisk.DeltaSPYUnhedged100BpUSD > 0)
                    {
                        OrderDirection orderDirection = NUM2DIRECTION[Math.Sign(t.Quantity)];
                        decimal newPrice = PriceOptionPfRiskAdjusted((Option)Securities[t.Symbol], pfRisk, orderDirection);
                        newPrice = RoundTick(newPrice, TickSize(t.Symbol));
                        if (newPrice == 0)
                        {
                            Debug($"Failed to get price for {t.Symbol} while hedging portfolio looking to update existing tickets..");
                            continue;
                        }
                        if (t.UpdateRequests?.Count > 0 && t.UpdateRequests.Last()?.LimitPrice == newPrice)
                        {
                            continue;
                        }
                        //        tag = f'Update Ticket: {ticket}. Set Price: {new_price} from originally: {limit_price}'
                        //        algo.Log(humanize(ts=algo.Time, topic="HEDGE MORE AGGRESSIVELY", symbol=str(ticket.Symbol), current_price=limit_price, new_price=new_price))
                        t.UpdateLimitPrice(newPrice);
                    }
                }
            }
        }
        public void CancelRiskIncreasingOrderTickets()
        {
            PortfolioRisk pfRisk = PortfolioRisk.E(this);
            foreach (var tickets in orderTickets.Values)
            {
                if (tickets.Count == 0)
                {
                    continue;
                }
                foreach (var t in tickets)
                {
                    decimal dPfDeltaIf = pfRisk.DPfDeltaIfFilled(t.Symbol, t.Quantity);
                    // 0 to be replace with HedgeBand target, once DPfDeltaFilled returns a decent number
                    if (dPfDeltaIf * pfRisk.DeltaSPYUnhedged100BpUSD > 0)
                    {
                        t.Cancel();
                    }
                }
            }
        }

        public void EmitNewFairOptionPrices(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Equity)
            {
                return;
            }            
            foreach (Option option in optionChains.GetValueOrDefault(symbol, new HashSet<Option> ()))
            {
                decimal? price = GetFairOptionPrice(option);                
                if (price != fairOptionPrices.GetValueOrDefault(option, null))
                {
                    fairOptionPrices[option] = price;
                    if (price != null)
                    {
                        PublishEvent(new EventNewFairOptionPrice(option.Symbol, (decimal)price));
                    }
                }
            }
        }
    }
}
