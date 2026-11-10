using System;
using System.IO;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Equity;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVSurfaceSSVIOnUpdateEventArgs<T> : EventArgs
    {
        public T State { get; }
        public Equity Equity { get; }
        public IVSurfaceSSVIOnUpdateEventArgs(T state, Equity equity)
        {
            State = state;
            Equity = equity;
        }
    }
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

        public void SetModelParams(object sender, IVSurfaceSSVIOnUpdateEventArgs<SSVIParamsDictionary> e)
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
                // The surface has never been calibrated (e.g. the SSVI response timed out).
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

        /// <summary>
        /// dσ/dS along the fitted surface, per $1 of spot — the term a sticky-strike delta omits.
        ///
        ///   w(k)   = ½(θ + ρψk + √((ψk+θρ)² + θ²(1-ρ²))),   k = ln(K/F)
        ///   dw/dk  = ½ψ(ρ + (ψk+θρ)/√((ψk+θρ)² + θ²(1-ρ²)))
        ///   dσ/dk  = dw/dk / (2σT)
        ///   dσ/dS  = dσ/dk · dk/dS,   dk/dS = −1/S (the forward scales with spot)
        ///
        /// Returns null when there is no surface for this contract (nothing to differentiate);
        /// returns 0 for a degenerate fit. Non-finite inputs are rejected, but extreme-but-finite
        /// IVs (0 or 5) are processed rather than special-cased.
        /// </summary>
        public double? IVdS(Symbol symbol)
        {
            if (ModelParams == null || symbol == null) return null;
            if (!_algo.Securities.TryGetValue(symbol, out Security security) || security is not Option option) return null;
            if (!ModelParams.TryGetValue(option.Expiry, out SSVIParamsRecord p) || p == null) return null;

            double spot = (double)_algo.MidPrice(option.Underlying.Symbol);
            double tenor = ToTenor(option.Expiry, _algo.Time.Date);
            if (!double.IsFinite(spot) || spot <= 0 || !double.IsFinite(tenor) || tenor <= 0) return null;

            double k = OptionContractWrap.E(_algo, option, _algo.Time.Date).MoneynessFwdLn();
            if (!double.IsFinite(k)) return null;

            double w = SsviTotalVariance(k, p);
            if (!double.IsFinite(w) || w <= 0) return 0.0;

            double sigma = Math.Sqrt(w / tenor);
            if (!double.IsFinite(sigma) || sigma <= 0) return 0.0;

            double a = p.Psi * k + p.Theta * p.Rho;
            double b = Math.Sqrt(a * a + p.Theta * p.Theta * (1 - p.Rho * p.Rho));
            if (!double.IsFinite(b) || b <= 0) return 0.0;

            double dwdk = 0.5 * p.Psi * (p.Rho + a / b);
            double dsigmadk = dwdk / (2 * sigma * tenor);
            double ivds = -dsigmadk / spot;
            return double.IsFinite(ivds) ? ivds : 0.0;
        }

        /// <summary>
        /// ATM skew dσ/dk (k = ln(K/F)) of the nearest calibrated tenor beyond a week: the number that
        /// says how much vol moves per unit moneyness, independent of the spot scaling in IVdS.
        /// </summary>
        public double SkewStrike()
        {
            if (ModelParams == null) return 0;
            DateTime[] tenors = ModelParams
                .Where(kvp => kvp.Value != null && kvp.Key > _algo.Time.Date.AddDays(7))
                .Select(kvp => kvp.Key)
                .OrderBy(d => d)
                .ToArray();
            if (tenors.Length == 0) return 0;

            SSVIParamsRecord p = ModelParams[tenors[0]];
            double tenor = ToTenor(tenors[0], _algo.Time.Date);
            if (!double.IsFinite(tenor) || tenor <= 0) return 0;

            double w0 = SsviTotalVariance(0, p);
            if (!double.IsFinite(w0) || w0 <= 0) return 0;
            double sigma = Math.Sqrt(w0 / tenor);
            if (!double.IsFinite(sigma) || sigma <= 0) return 0;

            double a = p.Theta * p.Rho;
            double b = Math.Sqrt(a * a + p.Theta * p.Theta * (1 - p.Rho * p.Rho));
            if (!double.IsFinite(b) || b <= 0) return 0;

            double dwdk = 0.5 * p.Psi * (p.Rho + a / b);
            double dsigmadk = dwdk / (2 * sigma * tenor);
            return double.IsFinite(dsigmadk) ? dsigmadk : 0;
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
