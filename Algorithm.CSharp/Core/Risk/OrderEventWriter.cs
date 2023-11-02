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
using Accord.Math;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class OrderEventWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        private List<string> _header = new() { "Ts", "TimeMS", "OrderId", "BrokerId", "OrderDirection", "OrderStatus", "SecurityType", "Underlying", "Symbol", "Quantity", "FillQuantity", 
            "LimitPrice", "FillPrice", "Fee", "PriceUnderlying", "BestBid", "BestAsk", "Delta2Mid", "OrderType", 
            "SubmitRequest.Status", "SubmitRequest.Time", "SubmitRequest.TimeMS",
            "CancelRequest.Status", "CancelRequest.Time", "CancelRequest.TimeMS",
            "LastUpdateRequest.Status", "LastUpdateRequest.Time", "LastUpdateRequest.TimeMS",
            "Tag", "TimeOrderLastUpdated", "TimeOrderLastUpdatedMS",
            "Exchange", "OcaGroup", "OcaType"
        };
        public OrderEventWriter(Foundations algo, Equity equity)
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, equity.Symbol.Value, "OrderEvents.csv");
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
            DateTime timeOrderLastUpdated = orderTicket.Time.ConvertFromUtc(_algo.TimeZone);
            long timeOrderLastUpdatedMs = timeOrderLastUpdated.Ticks / TimeSpan.TicksPerMillisecond;

            var row = new StringBuilder();

            // refactor to try catch every single cell and append cell by cell to avoid missing out on entire rows
            (string?, Func<string>)[] headerFunc =
            {
                ("Ts", () => _algo.Time.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture) ),
                ("TimeMS", () => (_algo.Time.Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) ),
                ("OrderId", () => orderTicket.OrderId.ToString(CultureInfo.InvariantCulture) ),
                ("BrokerId", () => string.Join(",", order?.BrokerId ?? new List<string>()) ),
                ("OrderDirection", () => order_direction_nm.ToString() ),
                ("OrderStatus", () => order_status_nm.ToString() ),
                ("SecurityType", () => security_type_nm.ToString() ),
                ("Underlying", () => underlying.Value),
                ("Symbol", () => symbol.ToString() ),
                ("Quantity", () => orderTicket.Quantity.ToString(CultureInfo.InvariantCulture) ),
                ("FillQuantity", () => orderTicket.QuantityFilled.ToString(CultureInfo.InvariantCulture) ),
                ("LimitPrice", () => limitPrice.ToString(CultureInfo.InvariantCulture) ),
                ("FillPrice", () => fillPrice.ToString(CultureInfo.InvariantCulture) ),
                ("Fee", () => order?.OrderFillData?.Fee.ToString(CultureInfo.InvariantCulture) ?? ""),
                ("PriceUnderlying", () => priceUnderlying.ToString(CultureInfo.InvariantCulture) ),
                ("BestBid", () => security.BidPrice.ToString(CultureInfo.InvariantCulture) ),
                ("BestAsk", () => security.AskPrice.ToString(CultureInfo.InvariantCulture) ),
                ("Delta2Mid", () => delta2Mid.ToString(CultureInfo.InvariantCulture) ),
                ("OrderType", () => orderTicket.OrderType.ToString() ),
                ("SubmitRequestStatus", () => orderTicket.SubmitRequest.Status.ToString() ),
                ("SubmitRequestTime", () => orderTicket.SubmitRequest.Time.ConvertFromUtc(_algo.TimeZone).ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture) ),
                ("SubmitRequestTimeMS", () => (orderTicket.SubmitRequest.Time.ConvertFromUtc(_algo.TimeZone).Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) ),
                ("CancelRequestStatus", () => orderTicket.CancelRequest?.Status.ToString(CultureInfo.InvariantCulture) ),
                ("CancelRequestTime", () => orderTicket.CancelRequest?.Time.ConvertFromUtc(_algo.TimeZone).ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture) ),
                ("CancelRequestTimeMS", () => orderTicket.CancelRequest == null ? "" : (orderTicket.CancelRequest.Time.ConvertFromUtc(_algo.TimeZone).Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) ),
                ("LastUpdateRequestStatus", () => orderTicket.UpdateRequests.Any() ? orderTicket.UpdateRequests[orderTicket.UpdateRequests.Count - 1].Status.ToString(CultureInfo.InvariantCulture) : ""),
                ("LastUpdateRequestTime", () => orderTicket.UpdateRequests.Any() ? orderTicket.UpdateRequests[orderTicket.UpdateRequests.Count - 1].Time.ConvertFromUtc(_algo.TimeZone).ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture) : ""),
                ("LastUpdateRequestTimeMS", () => orderTicket.UpdateRequests.Any() ? (orderTicket.UpdateRequests[orderTicket.UpdateRequests.Count - 1].Time.ConvertFromUtc(_algo.TimeZone).Ticks / TimeSpan.TicksPerMillisecond).ToString(CultureInfo.InvariantCulture) : ""),
                ("Tag", () => orderTicket.Tag ),
                ("TimeOrderLastUpdated", () => timeOrderLastUpdated.ToString("yyyyMMdd HHmmss", CultureInfo.InvariantCulture)),
                ("TimeOrderLastUpdatedMS", () => timeOrderLastUpdatedMs.ToString(CultureInfo.InvariantCulture)),
                ("Exchange", () => order?.Exchange?.ToString()),
                ("OcaGroup", () => order?.OcaGroup),
                ("OcaType", () => order?.OcaType.ToString(CultureInfo.InvariantCulture)),
            };            

            foreach (var (col, func) in headerFunc)
            {
                try
                {
                   row.Append(func()?.Replace(",", "|") ?? "");
                }
                catch (Exception e)
                {
                    _algo.Error($"OrderEventWriter.CsvRow: {col} - {e.Message}");
                    row.Append("");
                }
                row.Append(",");
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




