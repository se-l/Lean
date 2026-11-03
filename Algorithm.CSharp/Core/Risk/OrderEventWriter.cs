using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Globalization;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// Emits order-ticket state as OTLP metric gauges instead of appending to OrderEvents.csv.
    ///
    /// Injection is asynchronous (OpenTelemetryLogHandler.Metric enqueues onto a bounded queue with
    /// its own export thread), so a fill or a submit on the algorithm thread never waits on the
    /// collector. Identity travels as attributes (symbol, side, status, order type, OCA group, tag);
    /// everything numeric travels as its own gauge, and the timing cells are expressed as ages in
    /// algorithm-clock milliseconds so a run reads as a time series without any CSV join.
    /// </summary>
    public class OrderEventWriter : Disposable
    {
        private const string Prefix = "lean.order.";

        public OrderEventWriter(Foundations algo, Equity equity)
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
        }

        public void Write(OrderEvent orderEvent)
        {
            OrderTicket orderTicket = _algo.Transactions.GetOrderTicket(orderEvent.OrderId);
            if (orderTicket == null)
            {
                _algo.Log($"{_algo.Time} OrderEventWriter.Write No orderTicket found for OrderId: {orderEvent.OrderId}");
                return;
            }
            Write(orderTicket, orderEvent);
        }

        public void Write(OrderTicket orderTicket) => Write(orderTicket, null);

        private void Write(OrderTicket orderTicket, OrderEvent orderEvent)
        {
            if (orderTicket == null)
            {
                return;
            }

            Symbol symbol = orderTicket.Symbol;
            if (!_algo.Securities.ContainsKey(symbol))
            {
                // Not subscribed (yet): the state is not meaningful, and the CSV version skipped it too.
                return;
            }

            try
            {
                Security security = _algo.Securities[symbol];
                Symbol underlying = Underlying(symbol);
                Order order = _algo.Transactions.GetOrderById(orderTicket.OrderId);

                decimal midPrice = _algo.MidPrice(symbol);
                decimal priceUnderlying = security.Type == SecurityType.Option ? ((Option)security).Underlying.Price : security.Price;
                decimal limitPrice = orderTicket.OrderType == OrderType.Limit ? orderTicket.Get(OrderField.LimitPrice) : 0m;
                decimal fillPrice = orderTicket.AverageFillPrice;
                decimal delta2Mid = fillPrice != 0
                    ? (orderTicket.Quantity > 0 ? midPrice - fillPrice : fillPrice - midPrice)
                    : (orderTicket.Quantity > 0 ? midPrice - limitPrice : limitPrice - midPrice);

                OrderDirection direction = Num2Direction(orderTicket.Quantity);
                var attributes = new[]
                {
                    new KeyValuePair<string, object>("lean.symbol", symbol.Value),
                    new KeyValuePair<string, object>("lean.underlying", underlying.Value),
                    new KeyValuePair<string, object>("side", direction.ToString()),
                    new KeyValuePair<string, object>("status", orderTicket.Status.ToString()),
                    new KeyValuePair<string, object>("order_type", orderTicket.OrderType.ToString()),
                    new KeyValuePair<string, object>("security_type", security.Type.ToString()),
                    new KeyValuePair<string, object>("order_id", orderTicket.OrderId),
                    new KeyValuePair<string, object>("oca_group", orderTicket.OcaGroup ?? ""),
                    new KeyValuePair<string, object>("tag", orderTicket.Tag ?? ""),
                };

                Emit("quantity", (double)orderTicket.Quantity, attributes);
                Emit("quantity_filled", (double)orderTicket.QuantityFilled, attributes);
                Emit("limit_price", (double)limitPrice, attributes);
                Emit("fill_price", (double)fillPrice, attributes);
                Emit("delta_to_mid", (double)delta2Mid, attributes);
                Emit("best_bid", (double)security.BidPrice, attributes);
                Emit("best_ask", (double)security.AskPrice, attributes);
                Emit("price_underlying", (double)priceUnderlying, attributes);
                Emit("sign", Math.Sign(orderTicket.Quantity), attributes);

                if (order?.OrderFillData != null)
                {
                    Emit("fee", (double)order.OrderFillData.Fee, attributes);
                }
                if (orderEvent != null)
                {
                    Emit("event_fill_quantity", (double)orderEvent.FillQuantity, attributes);
                    Emit("event_fill_price", (double)orderEvent.FillPrice, attributes);
                    Emit("event_is_assignment", orderEvent.IsAssignment ? 1d : 0d, attributes);
                }
                if (_algo.OrderTicket2UtilityOrder.TryGetValue(orderTicket.OrderId, out IUtilityOrder utilityOrder))
                {
                    Emit("utility", utilityOrder.Utility, attributes);
                }

                // Timing as ages on the algorithm clock: submit -> last update -> now.
                double nowMs = AlgoMs(_algo.Time);
                double submittedMs = AlgoMs(orderTicket.SubmitRequest?.Time.ConvertFromUtc(_algo.TimeZone) ?? default);
                double lastUpdateMs = AlgoMs(orderTicket.Time.ConvertFromUtc(_algo.TimeZone));
                if (submittedMs > 0)
                {
                    Emit("age_ms", nowMs - submittedMs, attributes);
                    Emit("time_to_last_update_ms", lastUpdateMs - submittedMs, attributes);
                }
                if (orderTicket.CancelRequest != null)
                {
                    double cancelMs = AlgoMs(orderTicket.CancelRequest.Time.ConvertFromUtc(_algo.TimeZone));
                    if (cancelMs > 0 && submittedMs > 0) Emit("cancel_latency_ms", cancelMs - submittedMs, attributes);
                }
            }
            catch (Exception error)
            {
                _algo.Error($"{_algo.Time} OrderEventWriter.Write: {orderTicket.Symbol} — {error.Message}");
            }
        }

        private static double AlgoMs(DateTime local) =>
            local == default ? 0d : local.Ticks / (double)TimeSpan.TicksPerMillisecond;

        private static void Emit(string name, double value, KeyValuePair<string, object>[] attributes)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return;
            }
            OpenTelemetryLogHandler.Metric(Prefix + name, value, attributes);
        }
    }
}
