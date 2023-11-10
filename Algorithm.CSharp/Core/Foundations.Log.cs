using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations: QCAlgorithm
    {
        public string Humanize(Dictionary<string, string> kwargs)
        {
            return string.Join(", ", kwargs.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        public string Humanize(IEnumerable<KeyValuePair<string, string>> kwargs)
        {
            return string.Join(", ", kwargs.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        public string QuickLog(Dictionary<string, string> kwargs)
        {
            // Consider having class Log<Topic> for every topic where log fetched neccessary info. QuickLog(new Log<Topic>(necessary params would be enough to log...)) 
            string tag = Humanize(new Dictionary<string, string>() { { "ts", Time.ToString() } });
            tag += " " + Humanize(kwargs);
            Log(tag);
            return tag;
        }

        public string LogOrderTicket(OrderTicket ticket)
        {
            Security security = Securities[ticket.Symbol];
            string security_type_nm = ticket.Symbol.SecurityType.ToString();
            string order_type_nm = ticket.OrderType.ToString();
            OrderDirection order_direction = ticket.Quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;
            string order_direction_nm = order_direction.ToString();
            decimal ticketPrice = ticket.OrderType == OrderType.Limit ? ticket.Get(OrderField.LimitPrice) : ticket.AverageFillPrice;
            Security underlying = Securities[Underlying(ticket.Symbol)];

            string tag = Humanize(new Dictionary<string, string>()
            {
                { "ts", Time.ToString() },
                { "topic", "ORDERTICKET" },
                { "OrderDirection", order_direction_nm },
                { "OrderType", order_type_nm },
                { "SecurityType", security_type_nm },
                { "Symbol", ticket.Symbol.ToString() },
                { "Quantity", ticket.Quantity.ToString() },
                { "TicketPrice", ticketPrice.ToString() },
                { "Status", ticket.Status.ToString() },
                { "OCAGroup/Type", $"{ticket.OcaGroup}/{ticket.OcaType}" },
                { "BestBidPrice", security.BidPrice.ToString() },
                { "BestAskPrice", security.AskPrice.ToString() },
                { "PriceUnderlying", underlying.Price.ToString() },
            });
            Log(tag);
            return tag;
        }

        public string LogDividend(Slice data, Symbol sym)
        {
            var dividend = data.Dividends[sym];
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "DIVIDEND" },
                { "symbol", dividend.Symbol.ToString() },
                { "Distribution", dividend.Distribution.ToString() },
                { "PortfolioCash", Portfolio.Cash.ToString() },
                { "Price", Portfolio[sym].Price.ToString() }
            });
            Log(tag);
            return tag;
        }

        public string LogOnEventNewBidAsk(NewBidAskEventArgs e)
        {
            var security = Securities[e.Symbol];
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "EventNewBidAsk" },
                { "symbol", e.Symbol.ToString() },
                { "Bid", security.BidPrice.ToString() },
                { "Ask", security.AskPrice.ToString() },
                { "Mid", MidPrice(e.Symbol).ToString() },
                { "Last", security.Price.ToString() },
            };
            string tag = Humanize(d1);
            Log(tag);
            return tag;
        }

        public void LogOnEventOrderFill(OrderEvent e)
        {
            LogRisk(e.Symbol);
            LogPnL(e.Symbol);
        }

        public string LogOrderEvent(OrderEvent e)
        {
            if (e.Status == OrderStatus.UpdateSubmitted)
            {
                return null;
            }
            Security security = Securities[e.Symbol];
            string order_status_nm = e.Status.ToString();
            string order_direction_nm = e.Direction.ToString();
            string security_type_nm = security.Type.ToString();
            Symbol underlying = e.Symbol.SecurityType == SecurityType.Option ? ((Option)Securities[e.Symbol]).Underlying.Symbol : e.Symbol;
            string symbol = e.Symbol.ToString();
            Order order = Transactions.GetOrderById(e.OrderId);

            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "ORDER EVENT" },
                { "OrderId", e.OrderId.ToString() },
                { "OrderDirection", order_direction_nm },
                { "OrderStatus", order_status_nm },
                { "SecurityType", security_type_nm },
                { "Symbol", symbol },
                { "Quantity", e.Quantity.ToString() },
                { "OCAGroup/Type", $"{order.OcaGroup}/{order.OcaType}" },
                { "FillQuantity", e.FillQuantity.ToString() },
                { "LimitPrice", e.LimitPrice.ToString() },
                { "FillPrice", e.FillPrice.ToString() },
                { "Fee", e.OrderFee.ToString() },
                { "PriceUnderlying", e.Symbol.SecurityType == SecurityType.Option ? ((Option)Securities[e.Symbol]).Underlying.Price.ToString() : "" },
                { "BestBid", security.BidPrice.ToString() },
                { "BestAsk", security.AskPrice.ToString() },
                { "Delta2Mid", (e.Quantity > 0 ? MidPrice(symbol) - e.LimitPrice : e.LimitPrice - MidPrice(symbol)).ToString() },
                { "IVPrice", e.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(this, (Option)Securities[e.Symbol], Time.Date).IV(e.Status == OrderStatus.Filled ? e.FillPrice : e.LimitPrice, MidPrice(underlying), 0.001), 3).ToString() : "" },
                { "IVBid", e.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(this, (Option)Securities[e.Symbol], Time.Date).IV(Securities[e.Symbol].BidPrice, MidPrice(underlying), 0.001), 3).ToString() : "" },
                { "IVAsk", e.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(this, (Option)Securities[e.Symbol], Time.Date).IV(Securities[e.Symbol].AskPrice, MidPrice(underlying), 0.001), 3).ToString() : "" },
                { "IVBidEWMA", e.Symbol.SecurityType == SecurityType.Option ? IVSurfaceRelativeStrikeBid[underlying].IV(symbol).ToString() : "" },
                { "IVAskEWMA", e.Symbol.SecurityType == SecurityType.Option ? IVSurfaceRelativeStrikeAsk[underlying].IV(symbol).ToString() : "" },
            });
            Log(tag);

            if (orderStatusFilled.Contains(e.Status))
            {
                DiscordClient.Send(tag, DiscordChannel.Trades, LiveMode);
            }
            return tag;
        }

        public string LogRisk(Symbol symbol = null)
        {            
            var symbols = symbol == null ? equities : new HashSet<Symbol> { symbol };
            List<string> tagLines = new()
            {
                Humanize(new Dictionary<string, string> { { "ts", Time.ToString() }, { "topic", "RISK" } })
            };
            foreach (var _sym in symbols)
            {
                var d1 = new Dictionary<string, string>
            {
                { "Symbol", $"{_sym}" },
            };
                var d2 = PfRisk.ToDict(_sym).ToDictionary(x => x.Key, x => Math.Round(x.Value, 3).ToString());
                tagLines.Add(Humanize(d1.Union(d2)));
            }
            string tag = string.Join(", ", tagLines);
            Log(tag);

            DiscordClient.Send(tag, DiscordChannel.Status, LiveMode);
            return tag;
        }

        public string LogPositions()
        {
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "POSITIONS" },
            };
            var d2 = Positions.Where(x => x.Value.Quantity != 0).ToDictionary(x => x.Key.ToString(), x => x.Value.Quantity.ToString());
            string tag = Humanize(d1.Union(d2));
            Log(tag);

            DiscordClient.Send(tag, DiscordChannel.Status, LiveMode);
            return tag;
        }

        public string LogOrderTickets()
        {
            string tag = $"{Time} ORDERTICKETS" + 
            string.Join(" | ", orderTickets.
                    SelectMany(kvp => kvp.Value).ToList().
                    Select(x => $"{x.Symbol.Value}, Status: {x.Status}, SubmitRequest.Status: {x.SubmitRequest.Status}"));
            Log(tag);
            return tag;
        }

        public string LogPnL(Symbol symbol = null)
        {
            var posPnlRealized = PositionsRealized.Values.SelectMany(l => l).Sum(p => p.PL);
            var posPnLUnrealized = Positions.Values.Sum(p => p.PL);
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "PnL" },
                { "Symbol", $"{symbol ?? "Portfolio"}" },
                { "TotalPortfolioValueQC", Portfolio.TotalPortfolioValue.ToString() },
                { "PnLClose", (Portfolio.TotalPortfolioValue - TotalPortfolioValueSinceStart).ToString() },
                { "PnLRealizedPositions", posPnlRealized.ToString() },
                { "PnLUnrealizedPositions", posPnLUnrealized.ToString() },
                { "PnLRealized+UnrealizedPositions", (posPnlRealized + posPnLUnrealized).ToString() },
                { "Cash",  Portfolio.Cash.ToString() },
                { "TotalFeesQC", Portfolio.TotalFees.ToString() },
                { "TotalUnrealizedProfitQC", Portfolio.TotalUnrealizedProfit.ToString() },
        };
            string tag = Humanize(d1.Union(d1));
            Log(tag);
            DiscordClient.Send(tag, DiscordChannel.Status, LiveMode);
            return tag;
        }

        public void LogPortfolioHighLevel()
        {
            LogRisk();
            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFeesQC: {Portfolio.TotalFees}");
            Log($"RealizedProfitQC: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfitQC: {Portfolio.TotalUnrealizedProfit}");
            Log($"TotalPortfolioValueMid: {PfRisk.PortfolioValue("Mid")}");
            Log($"TotalPortfolioValueQC/Close: {PfRisk.PortfolioValue("QC")}");
            Log($"TotalPortfolioValueWorst: {PfRisk.PortfolioValue("Worst")}");
            Log($"TotalUnrealizedProfitMineExFees: {PfRisk.PortfolioValue("UnrealizedProfit")}");
            Log($"PnLClose: {Portfolio.TotalPortfolioValue - TotalPortfolioValueSinceStart}");
            Log($"PnlMidPerPosition: {PfRisk.PortfolioValue("AvgPositionPnLMid")}");
            Log($"PnlMidPerOptionAbsQuantity: {PfRisk.PortfolioValue("PnlMidPerOptionAbsQuantity")}");
        }

        public void LogTradeBar(Slice slice)
        {
            // Logging Fills
            foreach (KeyValuePair<Symbol, TradeBar> kvp in slice.Bars)
            {
                Symbol symbol = kvp.Key;
                if (symbol.ID.SecurityType == SecurityType.Option)
                {
                    Log($"{Time} OnData.FILL Detected: symbol: {symbol} Time: {kvp.Value.Time} Close: {kvp.Value.Close} Volume: {kvp.Value.Volume}" +
                        $"Best Bid: {Securities[symbol].BidPrice} Best Ask: {Securities[symbol].AskPrice}");
                }
            }
        }
    }
}
