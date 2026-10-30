/*
 * KalmanFilter — a per-parameter random-walk Kalman filter over SSVI surface parameters.
 *
 * STATE LAYOUT: three parameters per tenor, tenors ascending:
 *
 *     [theta_1, rho_1, psi_1, theta_2, rho_2, psi_2, ...]
 *
 * The filter OWNS the tenor list, because the tenor list is what the state vector means: when a
 * new expiry is listed the state must grow, and every index after the insertion point shifts.
 * A mapping function that captured a fixed key list would keep writing theta/rho/psi under the
 * old tenor after a growth — silently, and only for the tenors after the new one. So the mapping
 * is a FACTORY over the tenor list, rebuilt whenever the list changes.
 *
 * GROWTH SEMANTICS (matches Fino's research trainer, src/types/KalmanFilter.jl)
 *   - tenors never contract: a tenor that stops appearing keeps its slot (predict-only), so an
 *     expiry that goes quiet does not lose its history;
 *   - a tenor absent from one innovation is masked (NaN) and its state simply persists;
 *   - a NEW tenor is inserted at its sorted position, the existing covariance blocks are carried
 *     over by KEY (not by index), and the new 3x3 block is seeded from the first observation with
 *     a prior variance of `KalmanNewTenorPriorScale * R` (default 1.0 = "one observation's worth
 *     of confidence").
 *
 * WHY THE UPDATE WAS REWRITTEN (2026-10-30)
 *   The previous version assigned ONE identity matrix to five roles:
 *
 *       H = R = F = Q = I = Mat.DenseIdentity(StateDim);
 *
 *   which made the "filter" a fixed-gain EWMA: with Q = R = I the scalar steady state is
 *   P = (P+1)/(P+2) -> 0.618, so the gain converged to ~0.618 and every observation was weighted
 *   ~62 % regardless of tenor, parameter scale or fit quality. It also required a full
 *   observation vector, so the caller synthesised absent tenors by copying the current state
 *   into z — which "confirms" the state and shrinks P as if data had arrived — and any genuine
 *   NaN in z poisoned the state permanently.
 *
 *   Current behaviour: diagonal Q/R with per-parameter variances, NaN masking, Q scaled by
 *   elapsed time, Joseph-form covariance update, and LastInnovation / MaxStandardizedInnovation
 *   exposed as the oscillation gauge.
 */

using MathNet.Numerics.LinearAlgebra;
using QuantConnect.Securities.Equity;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class KalmanOnUpdateEventArgs<T> : EventArgs
    {
        public T State { get; }
        public Equity Equity { get; }
        public KalmanOnUpdateEventArgs(T state, Equity equity)
        {
            State = state;
            Equity = equity;
        }
    }

    public class KalmanFilter<T>// : IDisposable
    {
        private readonly Foundations _algo;
        public Equity Equity {  get; private set; }

        // EventHandlers
        public event EventHandler<KalmanOnUpdateEventArgs<T>> OnUpdate;
        public int StateDim { get; private set; }

        /// <summary>Tenor per 3-dimension block, ascending. The state vector's meaning.</summary>
        public IReadOnlyList<DateTime> Tenors => _tenors;
        private List<DateTime> _tenors;

        private Vector<double> x;   // state mean
        private Matrix<double> P;   // state covariance

        private Matrix<double> Q;   // process noise covariance (diagonal)
        private double[] _rVar;     // measurement noise variance, per dimension
        private double[] _qStd;     // per-parameter process std (theta, rho, psi)
        private double[] _rStd;     // per-parameter measurement std
        private DateTime _lastTs = default;

        private int _nUpdated;

        // Vector -> params, rebuilt from the tenor list whenever the tenor list changes.
        private readonly Func<IReadOnlyList<DateTime>, Func<Vector<double>, T>> _paramsFactory;
        private Func<Vector<double>, T> _vecToParams;

        /// <summary>
        /// (z - x1) for the last update, per dimension; NaN where no observation arrived.
        /// This is the oscillation gauge: its rolling magnitude says how much the incoming
        /// calibrations are moving relative to the filtered state.
        /// </summary>
        public Vector<double> LastInnovation { get; private set; }

        /// <summary>Number of dimensions actually observed in the last update.</summary>
        public int LastObservations { get; private set; }

        /// <summary>
        /// Largest |innovation| in units of the parameter's measurement std, over the observed
        /// dimensions of the last update. ~1 means "the snapshot moved by about one calibration
        /// error"; sustained values above ~3 mean the state is lagging the market, not smoothing it.
        /// </summary>
        public double MaxStandardizedInnovation { get; private set; }

        /// <summary>Median standardised innovation over the observed dimensions of the last update.</summary>
        public double MedianStandardizedInnovation { get; private set; }

        /// <summary>90th percentile standardised innovation over the observed dimensions.</summary>
        public double P90StandardizedInnovation { get; private set; }

        /// <summary>
        /// Worst-fitted dimension of the last update as "<tenor>:<parameter>", e.g. "2026-09-04:psi".
        /// The max alone cannot distinguish "one illiquid wing tenor" from "the whole surface moves
        /// faster than Q assumes"; this names the dimension so the two are separable in one run.
        /// </summary>
        public string WorstInnovationParam { get; private set; } = "-";

        /// <summary>Current state covariance (read-only use; do not mutate).</summary>
        public Matrix<double> StateCovariance => P;

        // Per-step process std and measurement std for (theta, rho, psi), in the parameters'
        // own units. theta is total variance (~1e-2), rho is a correlation (~+-0.5), psi is the
        // eSSVI smile slope (~0.5-2). Measurement std is the expected error of a single
        // snapshot calibration; process std is the expected drift between two calibrations at
        // the reference cadence.
        private static readonly double[] DefaultProcessStd     = { 2.0e-3, 2.0e-2, 5.0e-2 };
        private static readonly double[] DefaultMeasurementStd = { 8.0e-3, 6.0e-2, 1.2e-1 };

        // Cadence that DefaultProcessStd is calibrated at (the 10-minute capture timer).
        // Q is scaled by dt / this, so a faster feed does not get more weight per unit time.
        private const double ReferenceCadenceSeconds = 600.0;

        private static readonly string[] ParamNames = { "theta", "rho", "psi" };

        private readonly MatrixBuilder<double> Mat = Matrix<double>.Build;
        private readonly VectorBuilder<double> Vec = Vector<double>.Build;

        /// <summary>
        /// Random-walk Kalman filter over a flat SSVI parameter vector, whose dimension is
        /// `3 * tenors.Count` and may grow as new expiries are listed.
        /// </summary>
        /// <param name="algo">Algorithm (logging + config).</param>
        /// <param name="equity">Underlying this surface belongs to.</param>
        /// <param name="tenors">Tenor per 3-parameter block, ascending.</param>
        /// <param name="x">Initial state mean, [theta, rho, psi] per tenor.</param>
        /// <param name="P">Initial state covariance (e.g. Fino's EM-fitted InitCovariance).</param>
        /// <param name="paramsFactory">Given a tenor list, returns the state-vector -> params mapping.</param>
        /// <param name="processStd">Optional per-parameter process std override (keyed theta/rho/psi).</param>
        /// <param name="measurementStd">Optional per-parameter measurement std override.</param>
        public KalmanFilter(Foundations algo, Equity equity,
            IReadOnlyList<DateTime> tenors, Vector<double> x, Matrix<double> P,
            Func<IReadOnlyList<DateTime>, Func<Vector<double>, T>> paramsFactory,
            IReadOnlyDictionary<string, double> processStd = null,
            IReadOnlyDictionary<string, double> measurementStd = null)
        {
            _algo = algo;
            Equity = equity;
            _paramsFactory = paramsFactory ?? throw new ArgumentNullException(nameof(paramsFactory));

            _qStd = ResolveStd(processStd ?? _algo.Cfg.KalmanProcessStd, DefaultProcessStd, "KalmanProcessStd");
            _rStd = ResolveStd(measurementStd ?? _algo.Cfg.KalmanMeasurementStd, DefaultMeasurementStd, "KalmanMeasurementStd");

            SetState(tenors, x, P);
        }

        /// <summary>
        /// Install a new tenor set and state. Used by the constructor and by the daily reset.
        /// Rebuilds Q/R and the params mapping; clears the cadence clock and the update counter.
        /// </summary>
        /// <param name="tenors">New tenor list (ascending, unique).</param>
        /// <param name="newX">State mean, `3 * tenors.Count` long.</param>
        /// <param name="newP">State covariance, square of the same dimension.</param>
        public void Reset(IReadOnlyList<DateTime> tenors, Vector<double> newX, Matrix<double> newP)
        {
            SetState(tenors, newX, newP);
            _lastTs = default;
            _nUpdated = 0;
            LastInnovation = null;
            LastObservations = 0;
            MaxStandardizedInnovation = 0.0;
        }

        private void SetState(IReadOnlyList<DateTime> tenors, Vector<double> newX, Matrix<double> newP)
        {
            if (tenors == null || tenors.Count == 0)
            {
                throw new ArgumentException("KalmanFilter: at least one tenor is required.", nameof(tenors));
            }
            for (int i = 1; i < tenors.Count; i++)
            {
                if (tenors[i] <= tenors[i - 1])
                {
                    throw new ArgumentException(
                        $"KalmanFilter({Equity?.Symbol}): tenors must be ascending and unique; " +
                        $"{tenors[i - 1]:yyyy-MM-dd} >= {tenors[i]:yyyy-MM-dd}.", nameof(tenors));
                }
            }

            int dim = tenors.Count * 3;
            if (newX == null || newX.Count != dim)
            {
                throw new ArgumentException(
                    $"KalmanFilter({Equity?.Symbol}): state has {newX?.Count ?? 0} dimensions, " +
                    $"expected {dim} for {tenors.Count} tenors.", nameof(newX));
            }

            _tenors = tenors.ToList();
            StateDim = dim;
            x = newX;
            _vecToParams = _paramsFactory(_tenors);

            if (newP == null || newP.RowCount != dim || newP.ColumnCount != dim)
            {
                // Fino's init can arrive with a wrong shape, and a non-PSD P makes the gain
                // meaningless from the first update.
                _algo?.Error($"{_algo.Time} KalmanFilter({Equity?.Symbol}): init covariance is " +
                             $"{(newP == null ? "null" : $"{newP.RowCount}x{newP.ColumnCount}")}, expected " +
                             $"{dim}x{dim}. Using a flat diagonal.");
                P = Mat.DenseIdentity(dim) * (_rStd[0] * _rStd[0]);
            }
            else
            {
                P = newP;
            }

            RebuildNoise();
        }

        private void RebuildNoise()
        {
            Q = Mat.Dense(StateDim, StateDim);
            _rVar = new double[StateDim];
            for (int i = 0; i < StateDim; i++)
            {
                int p = i % ParamNames.Length;
                Q[i, i] = _qStd[p] * _qStd[p];
                _rVar[i] = _rStd[p] * _rStd[p];
            }
        }

        /// <summary>
        /// Grow the state to include `additions` (tenor, theta, rho, psi), keeping every existing
        /// entry at its own tenor. Existing covariance blocks are carried over BY KEY, so a tenor
        /// inserted in the middle cannot scramble the parameters that follow it.
        ///
        /// Returns false when no tenor is new (the common case: the chain did not change).
        /// </summary>
        public bool Grow(IReadOnlyList<(DateTime Tenor, double Theta, double Rho, double Psi)> additions)
        {
            if (additions == null || additions.Count == 0) return false;

            var seeds = additions
                .Where(a => !_tenors.Contains(a.Tenor))
                .GroupBy(a => a.Tenor)
                .ToDictionary(g => g.Key, g => g.Last());
            if (seeds.Count == 0) return false;

            var merged = _tenors.Concat(seeds.Keys).Distinct().OrderBy(t => t).ToList();
            int oldDim = StateDim;
            int newDim = merged.Count * 3;

            var pos = merged.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i * 3);
            var oldPos = _tenors.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i * 3);

            var newX = Vec.Dense(newDim, 0.0);
            var newP = Mat.Dense(newDim, newDim);

            // Carry the existing blocks: mean triples and full 3x3 covariance blocks (which keeps
            // the cross-tenor correlations, not just the variances).
            foreach (var (tOld, iOld) in oldPos)
            {
                int iNew = pos[tOld];
                for (int k = 0; k < 3; k++) newX[iNew + k] = x[iOld + k];
                foreach (var (tOld2, jOld) in oldPos)
                {
                    int jNew = pos[tOld2];
                    for (int r = 0; r < 3; r++)
                    {
                        for (int c = 0; c < 3; c++)
                        {
                            newP[iNew + r, jNew + c] = P[iOld + r, jOld + c];
                        }
                    }
                }
            }

            // Seed the new tenors: mean from the observation, variance = prior scale * R (block
            // diagonal, no cross-covariance with the existing tenors because none is estimated).
            double priorScale = (_algo?.Cfg?.KalmanNewTenorPriorScale) is double s && s > 0 ? s : 1.0;
            foreach (var (tNew, seed) in seeds)
            {
                int iNew = pos[tNew];
                newX[iNew + 0] = seed.Theta;
                newX[iNew + 1] = seed.Rho;
                newX[iNew + 2] = seed.Psi;
                for (int k = 0; k < 3; k++)
                {
                    double v = _rStd[k] * _rStd[k] * priorScale;
                    newP[iNew + k, iNew + k] = v;
                }
            }

            _algo?.Log($"{_algo.Time} KalmanFilter({Equity?.Symbol}): tenor set grew " +
                       $"{_tenors.Count} -> {merged.Count} ({oldDim} -> {newDim} dims): " +
                       $"{string.Join(",", seeds.Keys.OrderBy(t => t).Select(t => t.ToString("yyyy-MM-dd")))}");

            _tenors = merged;
            StateDim = newDim;
            x = newX;
            P = newP;
            _vecToParams = _paramsFactory(_tenors);
            RebuildNoise();
            return true;
        }

        private double[] ResolveStd(IReadOnlyDictionary<string, double> overrideStd, double[] defaults, string cfgName)
        {
            var outStd = (double[])defaults.Clone();
            if (overrideStd == null)
            {
                return outStd;
            }
            for (int p = 0; p < ParamNames.Length; p++)
            {
                if (overrideStd.TryGetValue(ParamNames[p], out double v) && v > 0 && double.IsFinite(v))
                {
                    outStd[p] = v;
                }
                else
                {
                    _algo?.Log($"{_algo.Time} KalmanFilter({Equity?.Symbol}): {cfgName} has no usable " +
                               $"'{ParamNames[p]}' entry; defaulting to {defaults[p]}.");
                }
            }
            return outStd;
        }

        /// <summary>
        /// Predict + update. `z` is the observation in state-vector order; NaN marks a dimension
        /// with no observation this step (a tenor the calibration did not return).
        /// </summary>
        public void Update(Vector<double> z) => Update(z, (DateTime?)null);

        /// <summary>
        /// Predict + update with an explicit observation timestamp. Q is scaled by the elapsed
        /// time since the previous update relative to <see cref="ReferenceCadenceSeconds"/>.
        /// </summary>
        public void Update(Vector<double> z, DateTime? ts)
        {
            if (z == null || z.Count != StateDim)
            {
                _algo?.Error($"{_algo.Time} KalmanFilter({Equity?.Symbol}): cannot update — observation " +
                             $"has {z?.Count ?? 0} dimensions, state has {StateDim}.");
                return;
            }

            // ── Process-noise scaling: random walk, variance proportional to elapsed time ──
            double dtScale = 1.0;
            if (ts.HasValue)
            {
                if (_lastTs != default)
                {
                    double dt = (ts.Value - _lastTs).TotalSeconds;
                    dtScale = Math.Max(dt, 1.0) / ReferenceCadenceSeconds;
                }
                _lastTs = ts.Value;
            }

            // ── Predict (F = identity: no trajectory model, the state persists) ──
            var x1 = x;
            var P1 = P + Q * dtScale;

            // ── Observation mask ──
            var obs = new List<int>(StateDim);
            for (int i = 0; i < StateDim; i++)
            {
                if (double.IsFinite(z[i])) obs.Add(i);
            }

            if (obs.Count == 0)
            {
                // Nothing to correct with: publish the prediction so the caller sees the
                // covariance grow rather than a silently frozen state.
                x = x1;
                P = Symmetrise(P1);
                LastInnovation = Vec.Dense(StateDim, double.NaN);
                LastObservations = 0;
                MaxStandardizedInnovation = 0.0;
                MedianStandardizedInnovation = 0.0;
                P90StandardizedInnovation = 0.0;
                WorstInnovationParam = "-";
                _nUpdated++;
                OnUpdate?.Invoke(this, new KalmanOnUpdateEventArgs<T>(_vecToParams(x), Equity));
                return;
            }

            int n = obs.Count;

            // S = P1[obs, obs] + R[obs, obs]   (R is diagonal by construction)
            var S = Mat.Dense(n, n);
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    S[a, b] = P1[obs[a], obs[b]] + (a == b ? _rVar[obs[a]] : 0.0);
                }
            }
            Matrix<double> SInv;
            try
            {
                SInv = S.Inverse();
            }
            catch (Exception ex)
            {
                _algo?.Error($"{_algo.Time} KalmanFilter({Equity?.Symbol}): innovation covariance is " +
                             $"singular ({ex.Message}); skipping this observation.");
                x = x1;
                P = Symmetrise(P1);
                LastInnovation = Vec.Dense(StateDim, double.NaN);
                LastObservations = 0;
                MaxStandardizedInnovation = 0.0;
                MedianStandardizedInnovation = 0.0;
                P90StandardizedInnovation = 0.0;
                WorstInnovationParam = "-";
                return;
            }

            // K = P1[:, obs] * S^-1   (StateDim x n)
            var K = Mat.Dense(StateDim, n);
            for (int r = 0; r < StateDim; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    double acc = 0.0;
                    for (int k = 0; k < n; k++) acc += P1[r, obs[k]] * SInv[k, c];
                    K[r, c] = acc;
                }
            }

            // Innovation: only observed dimensions are defined.
            var innov = Vec.Dense(StateDim, double.NaN);
            for (int k = 0; k < n; k++) innov[obs[k]] = z[obs[k]] - x1[obs[k]];

            var x2 = Vec.Dense(StateDim, 0.0);
            for (int r = 0; r < StateDim; r++)
            {
                double d = x1[r];
                for (int k = 0; k < n; k++) d += K[r, k] * innov[obs[k]];
                x2[r] = d;
            }

            // Joseph form: P2 = (I - K H) P1 (I - K H)' + K R K', with H the row selector.
            var IminusKH = Mat.DenseIdentity(StateDim);
            for (int k = 0; k < n; k++)
            {
                int c = obs[k];
                for (int r = 0; r < StateDim; r++) IminusKH[r, c] -= K[r, k];
            }
            var P2 = IminusKH * P1 * IminusKH.Transpose();
            for (int k = 0; k < n; k++)
            {
                var col = K.Column(k);
                P2 += _rVar[obs[k]] * col.OuterProduct(col);
            }

            x = x2;
            P = Symmetrise(P2);
            LastInnovation = innov;
            LastObservations = n;

            // Innovation statistics over the observed dimensions. The MAX is an order statistic over
            // ~48 dimensions, so it is usually one illiquid wing tenor; the median and p90 describe
            // the body of the distribution and the argmax names the offending dimension, which is
            // what separates "one broken tenor" from "Q is too small for the whole surface".
            var stdInnov = new List<double>(n);
            double maxStd = 0.0;
            int argMax = -1;
            for (int k = 0; k < n; k++)
            {
                int i = obs[k];
                double s = Math.Abs(innov[i]) / Math.Sqrt(_rVar[i]);
                stdInnov.Add(s);
                if (s > maxStd)
                {
                    maxStd = s;
                    argMax = i;
                }
            }
            stdInnov.Sort();
            MaxStandardizedInnovation = maxStd;
            MedianStandardizedInnovation = stdInnov.Count % 2 == 1
                ? stdInnov[stdInnov.Count / 2]
                : 0.5 * (stdInnov[stdInnov.Count / 2 - 1] + stdInnov[stdInnov.Count / 2]);
            P90StandardizedInnovation =
                stdInnov[Math.Min(stdInnov.Count - 1, (int)Math.Ceiling(0.9 * stdInnov.Count) - 1)];
            WorstInnovationParam = argMax >= 0
                ? $"{_tenors[argMax / 3]:yyyy-MM-dd}:{ParamNames[argMax % 3]}"
                : "-";
            _nUpdated++;

            OnUpdate?.Invoke(this, new KalmanOnUpdateEventArgs<T>(_vecToParams(x), Equity));
        }

        private static Matrix<double> Symmetrise(Matrix<double> m) => (m + m.Transpose()) * 0.5;

        public T GetSSVIParams()
        {
            return _vecToParams(x);
        }

        private int MinUpdatesToReady 
        {
            get {
                return _algo.Cfg.KalmanMinUpdatesToReady.TryGetValue(Equity.Symbol.Value, out int val) ? val : _algo.Cfg.KalmanMinUpdatesToReady[CfgDefault];
            }         
        }

        public bool IsReady()
        {
            return _nUpdated > MinUpdatesToReady;
        }
        public int NUpdated => _nUpdated;
    }
}
