using System;
using System.IO;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Equity;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public record SSVIParamsRecord(double Theta, double Rho, double Psi);
    public class IVSurfaceSSVI : IIVSurface, IDisposable
    {
        private readonly Foundations _algo;
        public Equity Underlying { get; }
        public QuoteSide? Side { get; }
        public SSVIParamsDictionary ModelParams { get; internal set; }
        public bool IsCalibrated { get; internal set; }

        public IVSurfaceSSVI(Foundations algo, Equity underlying, QuoteSide? side = null, bool createFile = false)
        {
            // createFile is retained for source compatibility; the surface no longer writes files —
            // see WriteMetrics().
            _algo = algo;
            Underlying = underlying;
            Side = side;
        }

        public void SetModelParams(SSVIParamsDictionary modelParams)
        {
            ModelParams = modelParams;
            _algo.Log($"{_algo.Time} IVSurfaceSSVI.SetModelParams: Updated!");
            IsCalibrated = true;
        }

        public void SetModelParams(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            SetModelParams(e.State);
        }

        public bool HasParams(DateTime tenor, OptionRight right)
        {
            return ModelParams != null && ModelParams.ContainsKey(tenor);
        }

        public bool HasParams(Option option)
        {
            return ModelParams != null && ModelParams.ContainsKey(option.Expiry);
        }

        public double IV(Option option)
        {
            DateTime calcDate = _algo.Time.Date;
            return IV(OptionContractWrap.E(_algo, option, calcDate).MoneynessFwdLn(), option.Expiry, option.Right, calcDate);
        }

        public double IV(double k, DateTime tenor, OptionRight right, DateTime calcDate)
        {
            if (!IsCalibrated) throw new InvalidOperationException($"IVSurface is not calibrated: Underlying={Underlying}");
            if (!HasParams(tenor, right)) throw new InvalidOperationException($"IVSurface does not have parameters for this tenor={tenor}, right={right}, Underlying={Underlying}");

            SSVIParamsRecord mParams = ModelParams[tenor];
            return SsviIV(k, mParams, ToTenor(tenor, calcDate));
        }

        public decimal BidPrice(Option option)
        {
            throw new NotImplementedException();
        }

        public decimal AskPrice(Option option)
        {
            throw new NotImplementedException();
        }

        public double AtmIv()
        {
            // Use tenor from params keys that is at least 7 days away. Only calibrated records count:
            // an empty tenor set or a null record used to throw (Min over an empty sequence, or a
            // NullReferenceException inside SsviIV) out of a utility evaluation, killing the run from a
            // scheduled event. Now it is "no ATM IV", which propagates as NaN and is filtered by the
            // callers' IsFinite guards.
            if (ModelParams == null)
            {
                // The surface has never been calibrated (e.g. the KalmanInit/SSVI response timed out).
                return double.NaN;
            }

            DateTime[] tenors = ModelParams
                .Where(kvp => kvp.Value != null && kvp.Key > _algo.Time.Date.AddDays(7))
                .Select(kvp => kvp.Key)
                .ToArray();
            if (tenors.Length == 0)
            {
                return double.NaN;
            }

            DateTime tenor = tenors.Min();
            return (IV(0, tenor, OptionRight.Call, _algo.Time.Date) + IV(0, tenor, OptionRight.Put, _algo.Time.Date)) / 2;

        }
        
        /// <summary>
        /// Implied total variance surface
        /// eSSVI(K, T) = 1/2 (θ(T) + ρ(T)ψ(T)k + sqrt((ψ(T) k + θ(T) ρ(T))**2 + θ(T)**2 * (1 − ρ(T)**2))
        /// </summary>
        public double SsviIV(double k, SSVIParamsRecord mParams, double tenor)
        {
            return Math.Sqrt(SsviTotalVariance(k, mParams) / tenor);
        }

        /// <summary>
        /// Implied total variance surface
        /// eSSVI(K, T) = 1/2 (θ(T) + ρ(T)ψ(T)k + sqrt((ψ(T) k + θ(T) ρ(T))**2 + θ(T)**2 * (1 − ρ(T)**2))
        /// 0.5 * (theta + rho * psi * k + ((psi * k + theta * rho) ** 2 + theta ** 2 * (1 - rho ** 2)) ** (1 / 2))
        /// </summary>
        public double SsviTotalVariance(double k, SSVIParamsRecord p)
        {
            return 0.5 * (p.Theta + p.Rho * p.Psi * k + Math.Sqrt(
                    Math.Pow(p.Psi * k + p.Theta * p.Rho, 2) + 
                    Math.Pow(p.Theta, 2) * (1 - Math.Pow(p.Rho, 2))
                    )
                );
        }

        public double? IVdS(Symbol symbol)
        {
            return null;
        }

        public double SkewStrike()
        {
            return 0;
        }

        /// <summary>
        /// The SSVI parameters as OTLP gauges: one data point per tenor, dimensioned by underlying
        /// and expiry. Replaces the per-side IVSurface CSV dump.
        /// </summary>
        public void WriteMetrics()
        {
            if (ModelParams == null)
            {
                return;
            }

            foreach (var kvp in ModelParams)
            {
                var attributes = new[]
                {
                    new System.Collections.Generic.KeyValuePair<string, object>("lean.underlying", Underlying.Symbol.Value),
                    new System.Collections.Generic.KeyValuePair<string, object>("expiry", kvp.Key.ToString("yyyy-MM-dd")),
                    new System.Collections.Generic.KeyValuePair<string, object>("side", Side?.ToString() ?? "mid"),
                };
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.ivsurface.theta", kvp.Value.Theta, attributes);
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.ivsurface.rho", kvp.Value.Rho, attributes);
                QuantConnect.Logging.OpenTelemetryLogHandler.Metric("lean.ivsurface.psi", kvp.Value.Psi, attributes);
            }
        }

        public void Dispose()
        {
            // No file handle and no unmanaged state: metrics are queued for the OTLP exporter.
        }
    }
}
