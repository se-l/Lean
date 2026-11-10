using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Configuration;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// RAW surface handling: one observation in, filtered surface out.
    ///
    /// DEFAULT: the incoming surface IS the product. It is installed as-is into
    /// `IvSurfaceSsviExecution` and every consumer reads it — no filter, no smoothing, backtest and live
    /// identical because both receive the same finished calibrations (filtering happens upstream).
    ///
    /// OPT-IN (`Cfg.UseSurfaces`): a row whose fit records `kind = raw` is treated as an
    /// observation, filtered here, and only the filtered state reaches consumers. The code below is
    /// kept for that path; it is not wired by default because it has not been validated against the
    /// upstream product yet.
    /// </summary>
    public partial class Foundations
    {
        /// <summary>Execution surface recipe (`surface-id`): what prices resting limit orders.</summary>
        public string ExecutionSurfaceId => (Config.Get("surface-id", "") ?? string.Empty).Trim();

        /// <summary>
        /// Risk surface recipe (`risk-surface-id`): what feeds greeks, risk metrics and the delta hedge.
        /// Defaults to the execution id, so a run that only sets `surface-id` uses one surface everywhere.
        /// </summary>
        public string RiskSurfaceId
        {
            get
            {
                string risk = (Config.Get("risk-surface-id", "") ?? string.Empty).Trim();
                return risk.Length > 0 ? risk : ExecutionSurfaceId;
            }
        }

        /// <summary>True when the two roles point at different products.</summary>
        public bool RiskSurfaceIsDistinct =>
            !string.Equals(RiskSurfaceId, ExecutionSurfaceId, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// True when LIVE surfaces arrive from the RabbitMQ stream (push) instead of the calibration
        /// request/response (pull). Backtest never uses this: it reads the warehouse.
        /// </summary>
        public bool UseSurfaceStream =>
            SurfaceFromRabbitMq && LiveMode && Config.GetBool("surface-stream-enabled", false);

        /// <summary>True when `rowId` is the surface id configured for a role.</summary>
        public bool SurfaceIdMatches(string rowId, string roleId) =>
            !string.IsNullOrEmpty(roleId) && !string.IsNullOrEmpty(rowId) &&
            string.Equals(rowId.Trim(), roleId.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Hook for subclasses that react to every applied surface (scenario publishing).</summary>
        protected virtual void OnSurfaceUpdated(object sender, IVSurfaceSSVIOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
        }

        /// <summary>
        /// Install one surface for consumers: the single entry point for slice rows, the history seed
        /// and live calibration responses.
        /// </summary>
        public void ApplySurface(Equity equity, SSVIParamsDictionary parameters, string source, bool raw,
                                 bool execution = true, bool risk = true)
        {
            if (equity == null || parameters == null || parameters.Count == 0)
            {
                return;
            }

            IIVSurface updated = null;
            if (execution && IvSurfaceSsviExecution.TryGetValue(equity, out IIVSurface executionSurface))
            {
                executionSurface.SetModelParams(parameters);
                updated = executionSurface;
            }
            if (risk && IvSurfaceSsviRisk.TryGetValue(equity, out IIVSurface riskSurface))
            {
                riskSurface.SetModelParams(parameters);
                updated ??= riskSurface;
            }
            if (updated == null)
            {
                Log($"{Time} ApplySurface: {equity.Symbol} has no IV surface object yet — dropped ({source}).");
                return;
            }

            // Downstream (scenario publishing) hangs off one event per applied surface, no matter which
            // path filled it. Fired ONCE per row: the two stores hold identical params until the risk id
            // differs, and a double event would publish the scenario twice.
            OnSurfaceUpdated(updated, new IVSurfaceSSVIOnUpdateEventArgs<SSVIParamsDictionary>(parameters, equity));
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

            if (!IvSurfaceSsviRisk.TryGetValue(equity, out IIVSurface surface) ||
                surface is not IVSurfaceSSVI ssvi || !ssvi.IsCalibrated || ssvi.ModelParams is not { Count: > 0 })
            {
                return false;
            }

            // Params must COVER the book, not merely exist. A missing expiry makes MidIVSsvi fall back
            // to MidIV() — the option's own quotes — which is the dependency the surface is meant to
            // remove: with no quote that IV is 0/NaN and the net delta silently drops the option leg.
            List<DateTime> missing = null;
            foreach (Security held in Securities.Values)
            {
                if (held.Type != SecurityType.Option || held.Symbol.Underlying != underlying) continue;
                if (Portfolio[held.Symbol].Quantity == 0) continue;
                if (ssvi.ModelParams.ContainsKey(held.Symbol.ID.Date)) continue;
                (missing ??= new List<DateTime>()).Add(held.Symbol.ID.Date);
            }
            if (missing == null)
            {
                return true;
            }

            missing.Sort();
            // Key on the GAP as well as the underlying: a different missing expiry set is new information
            // and should be reported even if an earlier gap was already logged today.
            LogOncePerDay($"SsviSurfaceCoverage:{underlying.Value}:{missing[0]:yyyyMMdd}-{missing[^1]:yyyyMMdd}-{missing.Count}",
                $"{Time} HasSsviSurface: {underlying} surface carries {ssvi.ModelParams.Count} tenors but no params for " +
                $"{string.Join(", ", missing.Distinct().Select(d => d.ToString("yyyy-MM-dd")))} — held contracts on those " +
                $"expiries would fall back to quoted IV. Not hedging on this surface.");
            return false;
        }

    }
}
