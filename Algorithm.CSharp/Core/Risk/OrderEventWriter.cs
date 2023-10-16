using System.IO;
using System.Collections.Generic;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using System.Text;
using QuantConnect.Securities.Option;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    // The CSV part is quite messy. Improve
    public class OrderEventWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        private List<string> _header = new() { "ts", "ID", "OrderDirection", "OrderStatus", "SecurityType", "Symbol", "Quantity", "FillQuantity", "LimitPrice", "FillPrice", "Fee", "PriceUnderlying", "BestBid", "BestAsk", "Delta2Mid" };
        public OrderEventWriter(Foundations algo, Equity equity)
        {
            _algo = algo;
            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", equity.Symbol.Value, "OrderEvents.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true);
        }
        public string CsvRow(OrderEvent orderEvent)
        {
            Symbol symbol = orderEvent.Symbol;
            Security security = _algo.Securities[symbol];
            // Symbol underlying = Underlying(symbol);
            string order_status_nm = orderEvent.Status.ToString();
            string order_direction_nm = orderEvent.Direction.ToString();
            string security_type_nm = security.Type.ToString();
            decimal midPrice = _algo.MidPrice(symbol);
            decimal priceUnderlying = security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : security.Price;

            var row = new StringBuilder();

            row.AppendJoin(",", new[] { 
                _algo.Time.ToString(),
                orderEvent.OrderId.ToString(),
                order_direction_nm.ToString(),
                order_status_nm.ToString(),
                security_type_nm.ToString(),
                symbol.ToString(),
                orderEvent.Quantity.ToString(),
                orderEvent.FillQuantity.ToString(),
                orderEvent.LimitPrice.ToString(),
                orderEvent.FillPrice.ToString(),
                orderEvent.OrderFee.ToString(),
                priceUnderlying.ToString(),
                security.BidPrice.ToString(),
                security.AskPrice.ToString(),
                (orderEvent.Quantity > 0 ? midPrice - orderEvent.LimitPrice : orderEvent.LimitPrice - midPrice).ToString()
            });
            //row.Append("IVPrice", orderEvent.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(_algo, (Option)security, _algo.Time.Date).IV(orderEvent.Status == OrderStatus.Filled ? orderEvent.FillPrice : orderEvent.LimitPrice, _algo.MidPrice(underlying), 0.001), 3).ToString() : "" );
            //row.Append("IVBid", orderEvent.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(_algo, (Option)security, _algo.Time.Date).IV(security.BidPrice, _algo.MidPrice(underlying), 0.001), 3).ToString() : "" );
            //row.Append("IVAsk", orderEvent.Symbol.SecurityType == SecurityType.Option ? Math.Round(OptionContractWrap.E(_algo, (Option)security, _algo.Time.Date).IV(security.AskPrice, _algo.MidPrice(underlying), 0.001), 3).ToString() : "" );
            //row.Append("IVBidEWMA", orderEvent.Symbol.SecurityType == SecurityType.Option ? _algo.IVSurfaceRelativeStrikeBid[underlying].IV(symbol).ToString() : "" );
            //row.Append("IVAskEWMA", orderEvent.Symbol.SecurityType == SecurityType.Option ? _algo.IVSurfaceRelativeStrikeAsk[underlying].IV(symbol).ToString() : "");

            return row.ToString();
        }
        public void Write(OrderEvent orderEvent)
        {
            if (_writer == null)
            {
                _algo.Log($"OrderEventWriter.Write(): _writer is null.");
                return;
            }
            else if (_writer.BaseStream == null)
            {
                _algo.Log($"OrderEventWriter.Write(): _writer is closed.");
                return;
            }

            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", _header));
                _headerWritten = true;
            }
            _writer.WriteLine(CsvRow(orderEvent));
        }
    }
}




