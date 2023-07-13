using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.IO;
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

        public string LogOrder(Security security, OrderType order_type, float quantity)
        {
            string security_type_nm = security.Type.ToString();
            string order_type_nm = order_type.ToString();
            OrderDirection order_direction = quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;
            string order_direction_nm = order_direction.ToString();
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "ORDER" },
                { "OrderDirection", order_direction_nm },
                { "OrderType", order_type_nm },
                { "SecurityType", security_type_nm },
                { "Symbol", security.Symbol.ToString() },
                { "Quantity", Math.Abs(quantity).ToString() },
                { "Price", security.Price.ToString() },
                { "BestBidPrice", security.BidPrice.ToString() },
                { "BestAskPrice", security.AskPrice.ToString() }
            });
            Log(tag);
            return tag;
        }

        public string LogContract(Option contract, OrderDirection orderDirection, decimal? limitPrice = null, OrderType orderType=OrderType.Limit)
        {
            decimal best_bid = contract.BidPrice;
            decimal best_ask = contract.AskPrice;
            string order_type_nm = orderType.ToString();
            string order_direction_nm = orderDirection.ToString();
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "CONTRACT" },
                { "OrderDirection", order_direction_nm },
                { "OrderType", order_type_nm },
                { "Symbol", contract.Symbol.ToString() },
                { "Price", limitPrice.ToString() },
                { "PriceUnderlying", contract.Underlying.Price.ToString() },
                { "BestBid", best_bid.ToString() },
                { "BestAsk", best_ask.ToString() }
            });
            if (contract.StrikePrice != null)
            {
                tag += ", ";
                tag += Humanize(new Dictionary<string, string>
                {
                    { "Strike", contract.StrikePrice.ToString() },
                    { "Expiry", contract.Expiry.ToString() },
                    { "Contract", contract.ToString() }
                });
            }
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

        public string LogOnEventNewBidAsk(EventNewBidAsk @event)
        {
            var security = Securities[@event.Symbol];
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "EventNewBidAsk" },
                { "symbol", @event.Symbol.ToString() },
                { "Bid", security.BidPrice.ToString() },
                { "Ask", security.AskPrice.ToString() },
                { "Mid", MidPrice(@event.Symbol).ToString() },
                { "Last", security.Price.ToString() },
            };
            string tag = Humanize(d1);
            Log(tag);
            return tag;
        }

        public void LogOnEventOrderFill(OrderEvent @event)
        {
            LogRisk(@event.Symbol);
            LogPnL(@event.Symbol);
            var trades = Transactions.GetOrders().Where(o => o.LastFillTime != null && o.Status != OrderStatus.Canceled).Select(order => new Trade(this, order));
            ExportToCsv(trades, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_trades_{Time:yyyyMMddHHmmss}.csv"));
        }

        public string LogOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent.Status == OrderStatus.UpdateSubmitted)
            {
                return null;
            }
            Security security = Securities[orderEvent.Symbol];
            string order_status_nm = orderEvent.Status.ToString();
            string order_direction_nm = orderEvent.Direction.ToString();
            string security_type_nm = security.Type.ToString();
            string symbol = orderEvent.Symbol.ToString();
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "ORDER EVENT" },
                { "ID", orderEvent.OrderId.ToString() },
                { "OrderDirection", order_direction_nm },
                { "OrderStatus", order_status_nm },
                { "SecurityType", security_type_nm },
                { "Symbol", symbol },
                { "Quantity", orderEvent.Quantity.ToString() },
                { "FillQuantity", orderEvent.FillQuantity.ToString() },
                { "LimitPrice", orderEvent.LimitPrice.ToString() },
                { "FillPrice", orderEvent.FillPrice.ToString() },
                { "Fee", orderEvent.OrderFee.ToString() },
                { "PriceUnderlying", orderEvent.Symbol.SecurityType == SecurityType.Option ? ((Option)Securities[orderEvent.Symbol]).Underlying.Price.ToString() : "" },
                { "BestBid", security.BidPrice.ToString() },
                { "BestAsk", security.AskPrice.ToString() },
                { "Delta2Mean", (orderEvent.Quantity > 0 ? MidPrice(symbol) - orderEvent.FillPrice : orderEvent.FillPrice - MidPrice(symbol)).ToString() }
            });
            Log(tag);
            return tag;
        }

        public string LogRisk(Symbol symbol = null)
        {
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "RISK" },
                { "Symbol", $"{symbol ?? "Portfolio"}" },
            };
            d1 = d1.ToDictionary(x => x.Key, x => x.Value.ToString());
            var d2 = pfRisk.ToDict(symbol).ToDictionary(x => x.Key, x => x.Value.ToString());
            string tag = Humanize(d1.Union(d2));
            Log(tag);
            return tag;
        }        

        public string LogPnL(Symbol symbol = null)
        {
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "PnL" },
                { "Symbol", $"{symbol ?? "Portfolio"}" },
                { "TotalPortfolioValueQC", Portfolio.TotalPortfolioValue.ToString() },
                { "TotalPortfolioValueQCSinceStart", (Portfolio.TotalPortfolioValue - TotalPortfolioValueSinceStart).ToString() },
                { "Cash",  Portfolio.Cash.ToString() },
                { "TotalFeesQC", Portfolio.TotalFees.ToString() },
                { "TotalNetProfitQC", Portfolio.TotalNetProfit.ToString() },
                { "TotalUnrealizedProfitQC", Portfolio.TotalUnrealizedProfit.ToString() },
        };
            string tag = Humanize(d1.Union(d1));
            Log(tag);
            return tag;
        }
    }
}
