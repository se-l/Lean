using QuantConnect.Logging;
using QuantConnect.Securities.Equity;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    /// <summary>One SSVI tenor slice of a Kalman update: the schema emitted as metrics.</summary>
    public record Record(DateTime Ts, DateTime Expiry, decimal S, double Theta, double Rho, double Psi);

    /// <summary>
    /// Emits the Kalman-filter SSVI state as OTLP metric gauges instead of appending to
    /// KalmanFilterSSVI.csv. One data point per tenor per update, dimensioned by underlying and
    /// expiry, so a run can be sliced by tenor in the backend rather than parsed from a CSV.
    ///
    /// Injection is asynchronous (OpenTelemetryLogHandler.Metric enqueues; the export thread does the
    /// I/O), so a Kalman update on the algorithm thread never blocks on the collector.
    /// </summary>
    public class KalmanFilterSSVIWriter : IDisposable
    {
        private const string Prefix = "lean.kalman.";

        private readonly Foundations _algo;
        public Equity Equity { get; }

        public KalmanFilterSSVIWriter(Foundations algo, KalmanFilter<SSVIParamsDictionary> kalmanFilter)
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
            Equity = kalmanFilter.Equity;
            kalmanFilter.OnUpdate += RecordUpdatedKalmanParams;
        }

        public void RecordUpdatedKalmanParams(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            var ts = _algo.Time;
            var s = _algo.MidPrice(Equity.Symbol);
            var records = e.State
                .Select(kvp => new Record(ts, kvp.Key, s, kvp.Value.Theta, kvp.Value.Rho, kvp.Value.Psi))
                .ToList();
            WriteMetrics(records);
        }

        public void WriteMetrics(IEnumerable<Record> records)
        {
            if (records == null)
            {
                return;
            }

            foreach (Record record in records)
            {
                var attributes = new[]
                {
                    new KeyValuePair<string, object>("lean.underlying", Equity.Symbol.Value),
                    new KeyValuePair<string, object>("expiry", record.Expiry.ToString("yyyy-MM-dd")),
                };

                OpenTelemetryLogHandler.Metric(Prefix + "theta", record.Theta, attributes);
                OpenTelemetryLogHandler.Metric(Prefix + "rho", record.Rho, attributes);
                OpenTelemetryLogHandler.Metric(Prefix + "psi", record.Psi, attributes);
                OpenTelemetryLogHandler.Metric(Prefix + "spot", (double)record.S, attributes);
            }
        }

        public void Dispose()
        {
            // No file handle: metrics are already handed to the asynchronous OTLP queue.
        }
    }
}
