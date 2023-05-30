using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Data;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;

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
            kwargs.Add("ts", Time.ToString());
            string tag = Humanize(kwargs);
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

        public string LogOrderEvent(OrderEvent order_event)
        {
            if (order_event.Status == OrderStatus.UpdateSubmitted)
            {
                return null;
            }
            Security security = Securities[order_event.Symbol];
            string order_status_nm = order_event.Status.ToString();
            string order_direction_nm = order_event.Direction.ToString();
            string security_type_nm = security.Type.ToString();
            string symbol = order_event.Symbol.ToString();
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "ORDER EVENT" },
                { "OrderDirection", order_direction_nm },
                { "OrderStatus", order_status_nm },
                { "SecurityType", security_type_nm },
                { "Symbol", symbol },
                { "FillQuantity", order_event.FillQuantity.ToString() },
                { "LimitPrice", order_event.LimitPrice.ToString() },
                { "FillPrice", order_event.FillPrice.ToString() },
                { "Fee", order_event.OrderFee.ToString() },
                { "BestBid", security.BidPrice.ToString() },
                { "BestAsk", security.AskPrice.ToString() }
            });
            Log(tag);
            return tag;
        }

        public string LogRisk()
        {
            var d1 = new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "RISK" },
            };
            d1 = d1.ToDictionary(x => x.Key, x => x.Value.ToString());
            var d2 = PortfolioRisk.E(this).ToDict().ToDictionary(x => x.Key, x => x.Value.ToString());
            string tag = Humanize(d1.Union(d2));
            Log(tag);
            return tag;
        }

        public string LogPL(Symbol symbol, decimal? price = null, decimal? holdings = null, decimal? portfolio = null, decimal? unrealized = null, decimal? realized = null, decimal? fees = null)
        {
            string tag = Humanize(new Dictionary<string, string>
            {
                { "ts", Time.ToString() },
                { "topic", "PL" },
                { "symbol", symbol.ToString() },
                { "Price", price.ToString() },
                { "Holdings", holdings.ToString() },
                { "Portfolio", portfolio.ToString() },
                { "Unrealized", unrealized.ToString() },
                { "Realized", realized.ToString() },
                { "Fees", fees.ToString() }
            });
            Log(tag);
            return tag;
        }
    }
}
