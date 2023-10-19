using System.IO;
using System.Collections.Generic;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using System.Text;
using QuantConnect.Securities.Option;
using QuantConnect.Securities;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using System.Globalization;
using System.Linq;
using System;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class OrderEventWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        private List<string> _header = new() { "ts", "OrderId", "BrokerId", "OrderDirection", "OrderStatus", "SecurityType", "Underlying", "Symbol", "Quantity", "FillQuantity", 
            "LimitPrice", "FillPrice", "Fee", "PriceUnderlying", "BestBid", "BestAsk", "Delta2Mid", "OrderType", 
            "SubmitRequest.Status", "SubmitRequest.Time",
            "CancelRequest.Status", "CancelRequest.Time",
            "LastUpdateRequest.Status", "LastUpdateRequest.Time",
            "Tag",
        };
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

        public string CsvRow(OrderTicket orderTicket)
        {
            if (orderTicket == null) return "";

            Symbol symbol = orderTicket.Symbol;
            Security security = _algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);
            Order? order = _algo.Transactions.GetOrderById(orderTicket.OrderId);  // If existing tickets, might not have an order, only ticket in cache
            string order_status_nm = orderTicket.Status.ToString();
            string order_direction_nm = Num2Direction(orderTicket.Quantity).ToString();
            string security_type_nm = security.Type.ToString();
            decimal midPrice = _algo.MidPrice(symbol);
            decimal priceUnderlying = security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : security.Price;
            decimal limitPrice = orderTicket.OrderType == OrderType.Limit ? orderTicket.Get(OrderField.LimitPrice) : 0;
            decimal fillPrice = orderTicket.AverageFillPrice;
            decimal delta2Mid = fillPrice != 0 ? (orderTicket.Quantity > 0 ? midPrice - fillPrice : fillPrice - midPrice) : (orderTicket.Quantity > 0 ? midPrice - limitPrice : limitPrice - midPrice);

            var row = new StringBuilder();

            try
            {
                row.AppendJoin(",", new[] {
                    _algo.Time.ToString(CultureInfo.InvariantCulture),
                    orderTicket.OrderId.ToString(CultureInfo.InvariantCulture),
                    string.Join(",", order?.BrokerId ?? new List<string>()),
                    order_direction_nm.ToString(),
                    order_status_nm.ToString(),
                    security_type_nm.ToString(),
                    underlying.Value,
                    symbol.ToString(),
                    orderTicket.Quantity.ToString(CultureInfo.InvariantCulture),
                    orderTicket.QuantityFilled.ToString(CultureInfo.InvariantCulture),
                    limitPrice.ToString(CultureInfo.InvariantCulture),
                    fillPrice.ToString(CultureInfo.InvariantCulture),
                    order?.OrderFillData?.Fee.ToString(CultureInfo.InvariantCulture) ?? "",
                    priceUnderlying.ToString(CultureInfo.InvariantCulture),
                    security.BidPrice.ToString(CultureInfo.InvariantCulture),
                    security.AskPrice.ToString(CultureInfo.InvariantCulture),
                    delta2Mid.ToString(CultureInfo.InvariantCulture),
                    orderTicket.OrderType.ToString(),
                    orderTicket.SubmitRequest.Status.ToString(),
                    orderTicket.SubmitRequest.Time.ToString(CultureInfo.InvariantCulture),
                    orderTicket.CancelRequest?.Status.ToString(CultureInfo.InvariantCulture),
                    orderTicket.CancelRequest?.Time.ToString(CultureInfo.InvariantCulture),
                    orderTicket.UpdateRequests.Any() ? orderTicket.UpdateRequests[orderTicket.UpdateRequests.Count - 1].Status.ToString(CultureInfo.InvariantCulture) : "",
                    orderTicket.UpdateRequests.Any() ? orderTicket.UpdateRequests[orderTicket.UpdateRequests.Count - 1].Time.ToString(CultureInfo.InvariantCulture) : "",
                });
            }
            catch (Exception e)
            {
                _algo.Error($"OrderEventWriter.CsvRow: {e.Message}");
                return "";
            }
            return row.ToString();
        }
        public void Write(OrderEvent orderEvent)
        {
            if (!IsValidWriter()) return;
            OrderTicket orderTicket = _algo.Transactions.GetOrderTicket(orderEvent.OrderId);
            if (orderTicket == null)
            {
                _algo.Log($"{_algo.Time} OrderEventWriter.Write No orderTicket found for OrderId: {orderEvent.OrderId}");
                return;
            }
            _writer.WriteLine(CsvRow(orderTicket));
        }

        public void Write(OrderTicket orderTicket)
        {
            if (!IsValidWriter()) return;
            _writer.WriteLine(CsvRow(orderTicket));
        }

        private bool IsValidWriter()
        {
            if (_writer == null)
            {
                _algo.Log($"OrderEventWriter.CheckWriter(): _writer is null.");
                return false;
            }
            else if (_writer.BaseStream == null)
            {
                _algo.Log($"OrderEventWriter.CheckWriter(): _writer is closed.");
                return false;
            }
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", _header));
                _headerWritten = true;
            }
            return true;
        }
    }
}




