using System;
using System.Collections.Generic;
using QuantConnect.Logging;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// Schema of one position snapshot. Kept as a record so the metric key set has a single
    /// definition: the writer below turns each instance into OTLP gauges, which replaces the
    /// Positions.csv dump.
    /// </summary>
    public record PositionRecord(
        DateTime Ts1,
        Symbol Symbol,
        SecurityType SecurityType,
        OptionRight? Right,
        Symbol UnderlyingSymbol,
        OptionRight? OptionRight,
        DateTime? Expiry,
        decimal? StrikePrice,
        int Multiplier,
        decimal Quantity,
        decimal Mid1,
        decimal Bid1,
        decimal Ask1,
        decimal Mid1Underlying,
        decimal Bid1Underlying,
        decimal Ask1Underlying,
        decimal UnrealizedProfit,
        decimal Spread1,
        double IVBid1,
        double IVAsk1,
        double IVMid1,
        decimal ValueMid,
        decimal ValueWorst,
        decimal DeltaTotal,
        decimal GammaTotal,
        decimal VegaTotal,
        decimal ThetaTotal,
        decimal PnL,
        bool IsITM1,
        bool IsExercised
    );

    /// <summary>
    /// Emits every open position as OTLP metric gauges instead of appending to Positions.csv.
    ///
    /// Injection is asynchronous: OpenTelemetryLogHandler.Metric only enqueues onto a bounded queue
    /// with its own export thread, so the algorithm thread never waits on the collector. The writer
    /// holds no file handle and needs no flush; Dispose stays so the dictionary disposal call sites
    /// keep compiling.
    /// </summary>
    public class PositionWriter : IDisposable
    {
        private const string Prefix = "lean.position.";

        private readonly Foundations _algo;

        public PositionWriter(Foundations algo, string fileName = "Positions.csv")
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
        }

        /// <summary>Snapshot every position in <paramref name="positions"/> as metrics.</summary>
        public void WriteMetrics(IEnumerable<Position> positions)
        {
            if (positions == null)
            {
                return;
            }

            foreach (Position pos in positions)
            {
                Emit(new PositionRecord(
                    pos.Ts1,
                    pos.Symbol,
                    pos.SecurityType,
                    pos.Right,
                    pos.UnderlyingSymbol,
                    pos.OptionRight,
                    pos.Expiry,
                    pos.StrikePrice,
                    pos.Multiplier,
                    pos.Quantity,
                    pos.Mid1,
                    pos.Bid1,
                    pos.Ask1,
                    pos.Mid1Underlying,
                    pos.Bid1Underlying,
                    pos.Ask1Underlying,
                    pos.UnrealizedProfit,
                    pos.Spread1,
                    pos.IVBid1,
                    pos.IVAsk1,
                    pos.IVMid1,
                    pos.ValueMid,
                    pos.ValueWorst,
                    pos.DeltaTotal(),
                    pos.GammaTotal(),
                    pos.VegaTotal(),
                    pos.ThetaTotal(),
                    pos.PnL,
                    pos.IsITM1,
                    pos.IsExercised));
            }
        }

        private static void Emit(PositionRecord r)
        {
            var attributes = new[]
            {
                new KeyValuePair<string, object>("lean.underlying", r.UnderlyingSymbol.Value),
                new KeyValuePair<string, object>("lean.symbol", r.Symbol.Value),
                new KeyValuePair<string, object>("security_type", r.SecurityType.ToString()),
                new KeyValuePair<string, object>("right", r.OptionRight?.ToString() ?? "Equity"),
                new KeyValuePair<string, object>("expiry", r.Expiry?.ToString("yyyy-MM-dd") ?? ""),
                new KeyValuePair<string, object>("strike", (double)(r.StrikePrice ?? 0m)),
                new KeyValuePair<string, object>("is_itm", r.IsITM1),
                new KeyValuePair<string, object>("is_exercised", r.IsExercised),
            };

            OpenTelemetryLogHandler.Metric(Prefix + "quantity", (double)r.Quantity, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "mid", (double)r.Mid1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "bid", (double)r.Bid1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "ask", (double)r.Ask1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "spread", (double)r.Spread1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "iv_bid", r.IVBid1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "iv_ask", r.IVAsk1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "iv_mid", r.IVMid1, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "delta_total", (double)r.DeltaTotal, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "gamma_total", (double)r.GammaTotal, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "vega_total", (double)r.VegaTotal, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "theta_total", (double)r.ThetaTotal, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "value_mid", (double)r.ValueMid, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "value_worst", (double)r.ValueWorst, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "unrealized_pnl", (double)r.UnrealizedProfit, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "pnl", (double)r.PnL, attributes);
            OpenTelemetryLogHandler.Metric(Prefix + "mid_underlying", (double)r.Mid1Underlying, attributes);
        }

        public void Dispose()
        {
            // No file handle: metrics are already handed to the asynchronous OTLP queue.
        }
    }
}
