using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.LinearAlgebra;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// RAW surface handling: one observation in, filtered surface out.
    ///
    /// DEFAULT: the incoming surface IS the product. It is installed as-is into
    /// `IvSurfaceSsviMid` and every consumer reads it — no filter, no smoothing, backtest and live
    /// identical because both receive the same finished calibrations (filtering happens upstream).
    ///
    /// OPT-IN (`Cfg.UseKalmanSurfaces`): a row whose fit records `kind = raw` is treated as an
    /// observation, filtered here, and only the filtered state reaches consumers. The code below is
    /// kept for that path; it is not wired by default because it has not been validated against the
    /// upstream product yet.
    /// </summary>
    public partial class Foundations
    {
        /// <summary>Hook for subclasses that react to every applied surface (scenario publishing).</summary>
        protected virtual void OnSurfaceUpdated(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
        }

        /// <summary>
        /// Install one surface for consumers: the single entry point for slice rows, the history seed
        /// and live calibration responses.
        ///
        /// Default path: the parameters are the product — install and notify. Opt-in path
        /// (`Cfg.UseKalmanSurfaces`) with a raw row: filter first; the surface is then filled by the
        /// filter's own OnUpdate subscription.
        /// </summary>
        public void ApplySurface(Equity equity, SSVIParamsDictionary parameters, string source, bool raw)
        {
            if (equity == null || parameters == null || parameters.Count == 0)
            {
                return;
            }
            if (!IvSurfaceSsviMid.TryGetValue(equity, out IIVSurface surface))
            {
                Log($"{Time} ApplySurface: {equity.Symbol} has no IV surface object yet — dropped ({source}).");
                return;
            }

            if (Cfg.UseKalmanSurfaces && raw)
            {
                ApplyRawSsviObservation(equity, parameters, source);
                return;
            }

            surface.SetModelParams(parameters);

            // Downstream (scenario publishing) hangs off one event per applied surface, no matter
            // which path filled it.
            OnSurfaceUpdated(surface, new KalmanOnUpdateEventArgs<SSVIParamsDictionary>(parameters, equity));
        }

        /// <summary>
        /// The Kalman filter for this underlying, created from its FIRST observation if it does not
        /// exist yet. Live used to depend on Mind answering a KalmanInit request at warm-up end; without
        /// warm-up that request never left, so the filter stayed null and every calibration was dropped.
        /// Seeding from the first observation removes that ordering dependency entirely.
        /// </summary>
        public KalmanFilter<SSVIParamsDictionary> EnsureKalmanFilter(Equity equity, SSVIParamsDictionary seed, string source)
        {
            if (equity == null || seed == null || seed.Count == 0)
            {
                return null;
            }
            if (KalmanFiltersSSVI.TryGetValue(equity, out KalmanFilter<SSVIParamsDictionary> existing))
            {
                return existing;
            }
            if (!IvSurfaceSsviMid.TryGetValue(equity, out IIVSurface surface))
            {
                Log($"{Time} EnsureKalmanFilter: {equity.Symbol} has no IV surface object yet — cannot filter ({source}).");
                return null;
            }

            List<DateTime> tenors = seed.GetSortedKeys();
            Vector<double> x = seed.ToVector();
            int dim = tenors.Count * 3;
            var filter = new KalmanFilter<SSVIParamsDictionary>(this, equity, tenors, x,
                Matrix<double>.Build.Dense(dim, dim), Earnings.EarningsAlgorithm.VecToSsviParamsDictionary());

            // Prior = one observation's worth of confidence per parameter, the same block Grow gives a
            // newly listed tenor. The zero matrix the constructor was given above is only a placeholder.
            filter.Reset(tenors, x, filter.NewPriorCovariance(tenors.Count));

            filter.OnUpdate += surface.SetModelParams;      // consumers read the FILTERED state
            filter.OnUpdate += OnSurfaceUpdated;
            KalmanFilterSSVIWriters[equity] = new(this, filter);
            if (!PositionWriters.ContainsKey(equity))
            {
                PositionWriters[equity] = new(this);
            }
            surface.SetModelParams(filter.GetSSVIParams());
            KalmanFiltersSSVI[equity] = filter;

            Log($"{Time} EnsureKalmanFilter: {equity.Symbol} filter created from first observation ({source}): " +
                $"{tenors.Count} tenors, {filter.StateDim} dims.");
            return filter;
        }

        /// <summary>
        /// Feed one RAW surface as the observation and let the filter publish the filtered state.
        /// Tenors the filter does not carry yet grow the state (a newly listed expiry), exactly as the
        /// live calibration path does.
        /// </summary>
        public void ApplyRawSsviObservation(Equity equity, SSVIParamsDictionary observation, string source)
        {
            if (equity == null || observation == null || observation.Count == 0)
            {
                return;
            }

            KalmanFilter<SSVIParamsDictionary> filter = EnsureKalmanFilter(equity, observation, source);
            if (filter == null)
            {
                return;
            }

            int tenorsBefore = filter.Tenors.Count;
            List<(DateTime Tenor, double Theta, double Rho, double Psi)> additions = observation
                .Where(kvp => kvp.Value != null && !filter.Tenors.Contains(kvp.Key))
                .Select(kvp => (kvp.Key, kvp.Value.Theta, kvp.Value.Rho, kvp.Value.Psi))
                .ToList();
            bool grew = additions.Count > 0 && filter.Grow(additions);

            filter.Update(observation.ToVector(filter.Tenors), Time);

            Log($"{Time} ApplyRawSsviObservation: {equity.Symbol} ({source}) Kalman update: observed " +
                $"{filter.LastObservations}/{filter.StateDim} dims, tenors {tenorsBefore}{(grew ? $"->{filter.Tenors.Count}" : string.Empty)}, " +
                $"|z|/sigma med={filter.MedianStandardizedInnovation:0.0} p90={filter.P90StandardizedInnovation:0.0} " +
                $"max={filter.MaxStandardizedInnovation:0.0} @{filter.WorstInnovationParam}");
        }

        /// <summary>
        /// True when the SSVI surface for `symbol` carries parameters, i.e. option greeks are measurable.
        ///
        /// Before the first surface/calibration arrives the option deltas are not information — the
        /// pricer has no IV to work from — so a delta computed in that window is the EQUITY position's
        /// delta plus zeros, and a hedge sized off it is a guess. That is what fired on the first second
        /// of live: a hedge taken before any surface existed, sized off an equity-only delta.
        /// </summary>
        public bool HasSsviSurface(Symbol symbol)
        {
            if (symbol == null) return false;
            if (symbol.SecurityType != SecurityType.Equity && symbol.SecurityType != SecurityType.Option) return false;
            Symbol underlying = symbol.SecurityType == SecurityType.Option ? symbol.Underlying : symbol;
            if (!Securities.TryGetValue(underlying, out Security security) || security is not Equity equity) return false;

            if (IvSurfaceSsviMid.TryGetValue(equity, out IIVSurface surface) &&
                surface is IVSurfaceSSVI ssvi && ssvi.IsCalibrated && ssvi.ModelParams is { Count: > 0 })
            {
                return true;
            }
            return KalmanFiltersSSVI.TryGetValue(equity, out KalmanFilter<SSVIParamsDictionary> filter) &&
                   filter.GetSSVIParams() is { Count: > 0 };
        }
    }
}
