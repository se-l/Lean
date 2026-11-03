using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Logging;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// Emits every realized trade as OTLP metric gauges instead of appending to RealizedTrades.csv.
    ///
    /// Injection is asynchronous (OpenTelemetryLogHandler.Metric enqueues onto a bounded queue with
    /// its own export thread), so a fill on the algorithm thread never waits on the collector. The
    /// class keeps its <see cref="Disposable"/> base and the OnTradeEvent signature, so the existing
    /// subscription and disposal wiring is unchanged.
    /// </summary>
    public class TradeWriter : Disposable
    {
        private const string Prefix = "lean.trade.";

        public TradeWriter(Foundations algo)
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
        }

        public void Write(Trade trade)
        {
            if (trade == null)
            {
                return;
            }

            var attributes = new[]
            {
                new KeyValuePair<string, object>("lean.symbol", trade.Symbol?.Value ?? ""),
                new KeyValuePair<string, object>("lean.underlying", trade.UnderlyingSymbol?.Value ?? ""),
                new KeyValuePair<string, object>("security_type", trade.SecurityType.ToString()),
                new KeyValuePair<string, object>("right", trade.OptionRight?.ToString() ?? ""),
                new KeyValuePair<string, object>("expiry", trade.Expiry?.ToString("yyyy-MM-dd") ?? ""),
                new KeyValuePair<string, object>("strike", (double)(trade.StrikePrice ?? 0m)),
            };

            OpenTelemetryLogHandler.Metric(Prefix + "quantity", (double)trade.Quantity, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "fill_price", (double)trade.PriceFillAvg, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "fee", (double)trade.Fee, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "notional", (double)(trade.Quantity * trade.PriceFillAvg), attributes);
        }

        public void OnTradeEvent(object sender, TradeEventArgs e)
        {
            if (e.Trades == null)
            {
                return;
            }
            foreach (Trade trade in e.Trades)
            {
                Write(trade);
            }
        }
    }
}
