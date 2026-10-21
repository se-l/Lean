/*
 * MoneynessForward — C# port of the Julia pricer's forward-moneyness path, so the SSVI surface's
 * k = log(K/F) is computed in-process instead of via a ZMQ round trip per contract per call.
 *
 * The Julia side being replicated exactly is `get_mny_fwd` in Fino.jl/src/pricing/PricerZMQ.jl
 * (NOT src/shared/moneyness.jl — the RPC handler is what produced the numbers this replaces):
 *
 *     P(0,T)  = exp(-z(T)·T)                zero rate z(T): LINEAR interp on (times, rates),
 *                                           flat outside the endpoints (PricerZMQ.jl:236-248)
 *     PV(Dᵢ)  = Dᵢ · exp(-z(tᵢ)·tᵢ)         for ex-dates tᵢ ∈ (0, T] (PricerZMQ.jl:270-276)
 *     F       = (S − Σ PV(Dᵢ)) / P(0,T)     (PricerZMQ.jl:278)
 *     mny     = K / F                       (PricerZMQ.jl:281; NaN guards :264, :268, :279)
 *
 * Enrichment replicates enrich_request (PricerZMQ.jl:201-225):
 *   curve  = calibrated per-ticker curve for the calc date when present with Version >= 2
 *            ({DataFolder}/alternative/interest-rate/usa/daily/{TICKER}-{yyyymmdd}.csv),
 *            else the generic treasury curve for that date — bill rates ∪ bootstrapped par
 *            yields — falling back to the most recent earlier date with data (YieldCurve.jl).
 *   divs   = {DataFolder}/equity/usa/dividends/{TICKER}.csv cash amounts, plus the same
 *            forward-fill forecast (frequency from the last 12 rows, 5% annual growth,
 *            horizon min(end, today+2y); DividendManager.jl:586-616), with
 *            tᵢ = ((ex_date − calc_date).days + 1) / 365 (DividendManager.jl:762).
 *   NO_DIVIDENDS tickers (DividendManager.jl:14-16) resolve to an empty schedule.
 *
 * All numeric work is float32 like the Julia handler, because bit-exact reconciliation against
 * the RPC is the acceptance bar.
 *
 * Reconciliation: the first MnyFwd call per (ticker, calc date) also routes ONE probe through
 * the pricer and compares; a mismatch beyond tolerance logs a loud error and flips the day to
 * RPC-only (fail open — never silently wrong k).
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using QuantConnect.Logging;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>In-process replica of the Julia pricer's forward moneyness (PricerZMQ.get_mny_fwd).</summary>
    public static class MoneynessForward
    {
        private const int CalibratedRatesVersion = 2;

        /// <summary>DividendManager.jl:14-16 — tickers resolved to an empty dividend schedule.</summary>
        private static readonly HashSet<string> NoDividends = new(StringComparer.OrdinalIgnoreCase)
        {
            "PANW", "ONON", "XPO", "PATH", "CDNS", "BIDU", "CBRE",
            "CRWD", "DLTR", "DOCU", "KMX", "LI", "MDB", "MRNA",
            "NEOG", "NFLX", "ON", "PDD", "PLTR", "SNOW", "WDAY", "ZK", "ESLT"
        };

        private static readonly ConcurrentDictionary<string, Curve> CurveCache = new();
        private static readonly ConcurrentDictionary<(string ticker, DateTime date), List<Div>> DivCache = new();

        private sealed class Curve
        {
            public float[] Times = Array.Empty<float>();
            public float[] Rates = Array.Empty<float>();
            public int Version;
        }

        private sealed class Div
        {
            public DateTime ExDate;
            public float Amount;
        }

        /// <summary>Per (ticker, calc date): whether the local replica is trusted, for the fail-open flip.</summary>
        private static readonly ConcurrentDictionary<(string ticker, DateTime date), bool> Trusted = new();

        /// <summary>Days a curve (or the dividend set) is reused before re-reading the CSVs.</summary>
        private static readonly TimeSpan CurveRefresh = TimeSpan.FromDays(1);

        /// <summary>
        /// K / F(0,T), replicating get_mny_fwd. <paramref name="reconcile"/> (probe contract):
        /// when set, the first call for (ticker, date) ALSO runs the RPC and compares.
        /// </summary>
        public static double MnyFwd(
            string ticker, double s, double k, double t, long calcTimeUtc,
            Func<double> rpcProbe = null)
        {
            if (t <= 0 || s <= 0 || k <= 0) return double.NaN;

            var calcDate = UnixToUtcDate(calcTimeUtc);
            var key = (ticker.ToUpperInvariant(), calcDate);

            var (times, rates, divs) = Enrichment(ticker, calcDate);
            var local = (double)MnyFwdCore((float)s, (float)k, (float)t, times, rates, divs, calcDate);

            if (rpcProbe == null || Trusted.TryGetValue(key, out var trusted) && trusted)
            {
                return local;
            }

            // First use for this (ticker, date): reconcile once, then commit the day.
            var remote = rpcProbe();
            const double tolerance = 1e-4;   // relative; both sides are float32 pipelines
            var ok = double.IsNaN(local) && double.IsNaN(remote)
                     || (!double.IsNaN(local) && !double.IsNaN(remote)
                         && Math.Abs(local - remote) <= tolerance * Math.Max(1.0, Math.Abs(remote)));
            Trusted[key] = ok;
            if (!ok)
            {
                Log.Error($"MoneynessForward: RECONCILIATION FAILED {ticker} {calcDate:yyyy-MM-dd}: " +
                          $"local={local:0.00000000} rpc={remote:0.00000000} — this day uses RPC only.");
                // Fail open: report the RPC value for THIS call; Trusted=false keeps future
                // calls on the RPC until the next calc date re-probes.
                return remote;
            }
            Log.Trace($"MoneynessForward: reconciled {ticker} {calcDate:yyyy-MM-dd}: local={local:0.00000000} rpc={remote:0.00000000} (ok)");
            return local;
        }

        /// <summary>True while the replica is trusted for this (ticker, date); probes skip the RPC when true.</summary>
        public static bool IsTrusted(string ticker, DateTime calcDate) =>
            Trusted.TryGetValue((ticker.ToUpperInvariant(), calcDate), out var trusted) && trusted;

        /// <summary>log-moneyness — the form the SSVI surface consumes.</summary>
        public static double MnyFwdLn(string ticker, double s, double k, double t, long calcTimeUtc)
        {
            var mny = MnyFwd(ticker, s, k, t, calcTimeUtc);
            return double.IsNaN(mny) ? mny : Math.Log(mny);
        }

        // ────────────────────────────────────────────────────────────────────────
        // Enrichment (curve + dividends), cached per (ticker, calc date)
        // ────────────────────────────────────────────────────────────────────────

        private static (float[] times, float[] rates, List<Div> divs) Enrichment(string ticker, DateTime calcDate)
        {
            var upper = ticker.ToUpperInvariant();
            var key = (ticker: upper, calcDate);

            var divs = DivCache.GetOrAdd(key, _ => LoadDividends(upper, calcDate));
            var curve = CurveCache.GetOrAdd($"curve:{upper}:{calcDate:yyyyMMdd}", _ => LoadCurve(upper, calcDate));
            return (curve.Times, curve.Rates, divs);
        }

        /// <summary>
        /// Internal access for PricerEnvProvider — the SAME curve resolution the reconciled
        /// mny-fwd path uses (calibrated walk-back, generic treasury fallback), so the env
        /// bundle cannot disagree with the ZMQ pricer's own enrichment for this (ticker, date).
        /// </summary>
        internal static (float[] times, float[] rates) CurveForEnv(string ticker, DateTime calcDate)
        {
            var upper = ticker.ToUpperInvariant();
            var curve = CurveCache.GetOrAdd($"curve:{upper}:{calcDate:yyyyMMdd}", _ => LoadCurve(upper, calcDate));
            return (curve.Times, curve.Rates);
        }

        /// <summary>get_last_zero_curve: calibrated per-ticker curve within the -14d window, else generic treasury.</summary>
        private static Curve LoadCurve(string ticker, DateTime calcDate)
        {
            // Calibrated walk-back: get_last_zero_curve(delta_days=-14) probes the window
            // [calc_date-14 .. calc_date] for a calibrated curve before falling back.
            for (var offset = 0; offset <= 14; offset++)
            {
                var day = calcDate.AddDays(-offset);
                var calibrated = CalibratedPath(ticker, day);
                if (File.Exists(calibrated))
                {
                    var parsed = ParseCalibrated(calibrated);
                    if (parsed != null && parsed.Version >= CalibratedRatesVersion)
                    {
                        return parsed;
                    }
                }
            }

            // Generic treasury: exact date first, then walk back up to 180 days
            // (_get_latest_available_curve, YieldCurve.jl:278).
            for (var offset = 0; offset <= 180; offset++)
            {
                var day = calcDate.AddDays(-offset);
                var curve = BuildTreasuryCurve(day);
                if (curve != null) return curve;
            }
            Log.Error($"MoneynessForward: no zero curve found for {ticker} {calcDate:yyyy-MM-dd} (±180d) — moneyness will be NaN.");
            return new Curve();
        }

        private static string CalibratedPath(string ticker, DateTime calcDate) =>
            Path.Combine(Globals.DataFolder, "alternative", "interest-rate", "usa", "daily",
                $"{ticker.ToUpperInvariant()}-{calcDate:yyyyMMdd}.csv");

        private static Curve ParseCalibrated(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length < 2) return null;
                var version = 0;
                var header = lines[0].Split(',');
                var versionCol = Array.IndexOf(header, "Version");
                var tenorCol = Array.IndexOf(header, "Tenor");
                var rateCol = Array.IndexOf(header, "Rate");
                if (versionCol >= 0 && lines.Length > 1)
                {
                    var first = lines[1].Split(',');
                    int.TryParse(first[versionCol], out version);
                }
                var times = new List<float>();
                var rates = new List<float>();
                for (var i = 1; i < lines.Length; i++)
                {
                    var parts = lines[i].Split(',');
                    if (parts.Length <= Math.Max(tenorCol, rateCol)) continue;
                    if (!TryFloat(parts[tenorCol], out var time) || !TryFloat(parts[rateCol], out var rate)) continue;
                    times.Add(time);
                    rates.Add(rate);
                }
                return new Curve { Times = times.ToArray(), Rates = rates.ToArray(), Version = version };
            }
            catch (Exception e)
            {
                Log.Error($"MoneynessForward: failed to parse calibrated curve {path}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// The generic treasury curve for one date: bill rates ∪ par yields bootstrapped to zeros
        /// (_initialize!/_bootstrap, YieldCurve.jl:136-198). Returns null when the date has no data.
        /// </summary>
        private static Curve BuildTreasuryCurve(DateTime day)
        {
            var root = Path.Combine(Globals.DataFolder, "alternative", "interest-rate", "usa");
            var rates = new SortedDictionary<float, float>();

            // Bills: CE bid columns 3,5,7,9,11,13,15 → tenors 4/52..52/52 (YieldCurve.jl:82-84).
            var billPath = Path.Combine(root, "daily-treasury-bill-rates", $"{day.Year}.csv");
            var billTenors = new[] { 4f / 52, 6f / 52, 8f / 52, 13f / 52, 17f / 52, 26f / 52, 52f / 52 };
            var billCols = new[] { 2, 4, 6, 8, 10, 12, 14 };   // 0-based
            foreach (var (date, row) in ReadRows(billPath))
            {
                if (date != day) continue;
                for (var i = 0; i < billCols.Length; i++)
                {
                    if (billCols[i] < row.Length && TryFloat(row[billCols[i]], out var v))
                    {
                        rates[billTenors[i]] = v / 100f;
                    }
                }
            }
            var billMax = rates.Count > 0 ? rates.Keys.Last() : 0f;

            // Par yields: fixed tenor grid, bootstrapped > 1y (YieldCurve.jl:108-131, 163-198).
            var parPath = Path.Combine(root, "daily-treasury-par-yield-curve-rates", $"{day.Year}.csv");
            var parTenors = new[] { 1f / 12, 1.5f / 12, 2f / 12, 3f / 12, 4f / 12, 6f / 12, 1f, 2f, 3f, 5f, 7f, 10f, 20f, 30f };
            foreach (var (date, row) in ReadRows(parPath))
            {
                if (date != day) continue;
                var parPairs = new List<(float t, float c)>();
                for (var i = 0; i < parTenors.Length; i++)
                {
                    var col = i + 1;   // par files: col 0 = date, then one column per tenor
                    if (col < row.Length && TryFloat(row[col], out var v)) parPairs.Add((parTenors[i], v / 100f));
                }
                if (parPairs.Count == 0) continue;
                var zeros = Bootstrap(parPairs);
                foreach (var (t, z) in zeros)
                {
                    if (t > billMax) rates[t] = z;   // bills take precedence up to bill_max
                }
            }

            if (rates.Count == 0) return null;
            return new Curve { Times = rates.Keys.ToArray(), Rates = rates.Values.ToArray() };
        }

        /// <summary>Yields (date, fields) for non-empty non-header rows; date parsed m/d/yyyy.</summary>
        private static IEnumerable<(DateTime date, string[] fields)> ReadRows(string path)
        {
            if (!File.Exists(path)) yield break;
            var lines = File.ReadAllLines(path);
            for (var i = 1; i < lines.Length; i++)   // skip header
            {
                var parts = lines[i].Split(',');
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0])) continue;
                if (!DateTime.TryParseExact(parts[0].Trim(), "M/d/yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date)) continue;
                yield return (date, parts);
            }
        }

        /// <summary>Semi-annual par→zero bootstrap, float32 — YieldCurve.jl:163-198.</summary>
        private static List<(float t, float z)> Bootstrap(List<(float t, float c)> parPairs)
        {
            var zeros = new SortedDictionary<float, float>();
            foreach (var (t, c) in parPairs.OrderBy(p => p.t))
            {
                if (t <= 1f)
                {
                    zeros[t] = c;
                    continue;
                }
                var coupon = c / 2f;
                var pvCoupons = 0f;
                var numPayments = (int)MathF.Round(t * 2f);
                for (var i = 1; i <= numPayments - 1; i++)
                {
                    var paymentTime = (float)i * 0.5f;
                    var zi = InterpSorted(zeros, paymentTime);
                    pvCoupons += coupon / (float)Math.Pow(1f + zi / 2f, 2f * paymentTime);
                }
                var remaining = 1f - pvCoupons;
                if (remaining <= 0f)
                {
                    zeros[t] = c;
                    continue;
                }
                zeros[t] = 2f * ((float)Math.Pow((1f + coupon) / remaining, 1f / (2f * t)) - 1f);
            }
            return zeros.Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        private static float InterpSorted(SortedDictionary<float, float> map, float t)
        {
            if (map.Count == 0) return 0f;
            if (t <= map.First().Key) return map.First().Value;
            if (t >= map.Last().Key) return map.Last().Value;
            float[] keys = map.Keys.ToArray();
            for (var i = 0; i < keys.Length - 1; i++)
            {
                if (keys[i] <= t && t <= keys[i + 1])
                {
                    var r1 = map[keys[i]];
                    var r2 = map[keys[i + 1]];
                    return r1 + (r2 - r1) * (t - keys[i]) / (keys[i + 1] - keys[i]);
                }
            }
            return map.Last().Value;
        }

        /// <summary>
        /// Dividend schedule Julia resolves: cached CSV rows through `today`, plus the forward-fill
        /// forecast to the horizon (DividendManager.jl get_dividends:661-666 + forecast_dividends).
        /// </summary>
        private static List<Div> LoadDividends(string ticker, DateTime calcDate)
        {
            if (NoDividends.Contains(ticker)) return new List<Div>();

            var path = Path.Combine(Globals.DataFolder, "equity", "usa", "dividends", $"{ticker.ToUpperInvariant()}.csv");
            var history = new List<Div>();
            if (File.Exists(path))
            {
                try
                {
                    foreach (var line in File.ReadLines(path).Skip(1))
                    {
                        var parts = line.Split(',');
                        if (parts.Length < 2) continue;
                        if (!DateTime.TryParseExact(parts[0].Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
                                DateTimeStyles.None, out var exDate)) continue;
                        if (!TryFloat(parts[1], out var amount)) continue;
                        history.Add(new Div { ExDate = exDate, Amount = amount });
                    }
                }
                catch (Exception e)
                {
                    Log.Error($"MoneynessForward: failed to read dividends {path}: {e.Message}");
                }
            }
            else
            {
                Log.Trace($"MoneynessForward: no dividend file for {ticker} — treating as no dividends.");
                return new List<Div>();
            }

            var today = DateTime.Today;
            var lastEx = history.Where(d => d.ExDate <= today).OrderBy(d => d.ExDate).LastOrDefault();
            if (lastEx == null) return new List<Div>();

            var months = InferFrequencyMonths(history.Where(d => d.ExDate <= today).OrderBy(d => d.ExDate).ToList());
            var forecast = new List<Div>();
            var next = AddMonths(lastEx.ExDate, months);
            var horizon = today.AddDays(365.25 * 2);
            while (next <= horizon)
            {
                var years = (float)(next - lastEx.ExDate).TotalDays / 365.25f;
                forecast.Add(new Div { ExDate = next, Amount = lastEx.Amount * (float)Math.Pow(1.05, years) });
                next = AddMonths(next, months);
            }

            var all = history.Concat(forecast)
                .Where(d => d.ExDate > calcDate && d.ExDate <= calcDate.AddDays(730))   // relevant window only
                .OrderBy(d => d.ExDate)
                .ToList();
            return all;
        }

        /// <summary>DividendManager.jl infer_frequency_months: median gap snapped to {1,2,3,4,6,12} months.</summary>
        private static int InferFrequencyMonths(List<Div> sorted)
        {
            if (sorted.Count < 2) return 3;
            var deltas = new List<double>();
            for (var i = 1; i < sorted.Count; i++) deltas.Add((sorted[i].ExDate - sorted[i - 1].ExDate).TotalDays);
            if (deltas.Count == 0) return 3;
            var median = deltas.OrderBy(d => d).ElementAt(deltas.Count / 2);
            var candidates = new Dictionary<int, double> { { 1, 30 }, { 2, 60 }, { 3, 91 }, { 4, 121 }, { 6, 182 }, { 12, 365 } };
            return candidates.OrderBy(c => Math.Abs(c.Value - median)).First().Key;
        }

        private static DateTime AddMonths(DateTime d, int months)
        {
            try { return d.AddMonths(months); }
            catch { return d.AddDays(months * 30); }
        }

        // ────────────────────────────────────────────────────────────────────────
        // The numeric core — float32, 1:1 with PricerZMQ.get_mny_fwd
        // ────────────────────────────────────────────────────────────────────────

        private static float MnyFwdCore(float s, float k, float t, float[] times, float[] rates,
            List<Div> divs, DateTime calcDate)
        {
            var zT = InterpZero(times, rates, t);
            var p0t = (float)Math.Exp(-zT * t);
            if (p0t <= 0f) return float.NaN;

            var pvDivs = 0f;
            if (divs != null)
            {
                foreach (var d in divs)
                {
                    // DividendManager.jl:762 — ((ex − calc).days + 1) / 365
                    var tDiv = (float)((d.ExDate.Date - calcDate).Days + 1) / 365f;
                    if (!(0f < tDiv && tDiv <= t)) continue;
                    var zDiv = InterpZero(times, rates, tDiv);
                    pvDivs += d.Amount * (float)Math.Exp(-zDiv * tDiv);
                }
            }

            var fwd = (s - pvDivs) / p0t;
            return fwd <= 0f ? float.NaN : k / fwd;
        }

        /// <summary>Linear zero-rate interp with flat ends — PricerZMQ._interp_zero (:236-248).</summary>
        private static float InterpZero(float[] times, float[] rates, float t)
        {
            if (times.Length == 0) return 0f;
            if (t <= times[0]) return rates[0];
            if (t >= times[^1]) return rates[^1];
            for (var i = 0; i < times.Length - 1; i++)
            {
                if (times[i] <= t && t <= times[i + 1])
                {
                    return rates[i] + (rates[i + 1] - rates[i]) * (t - times[i]) / (times[i + 1] - times[i]);
                }
            }
            return rates[^1];
        }

        private static DateTime UnixToUtcDate(long seconds) =>
            DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.Date;

        private static bool TryFloat(string text, out float value) =>
            float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
