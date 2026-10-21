/*
 * PricerEnvProvider — resolves the price-of-cash bundle (`env`) the pricer plane requires
 * with every request, replicating PricerZMQ.enrich_request (PricerZMQ.jl:203-227) exactly:
 *
 *   rates:   get_last_zero_curve(calc_date, ticker, -14) — the calibrated per-ticker curve
 *            ({DataFolder}/alternative/interest-rate/usa/daily/{TICKER}-{yyyymmdd}.csv,
 *            Version >= 2) within the 14-day walk-back, else the generic treasury curve
 *            (bill rates ∪ bootstrapped par yields), walking back up to 180 days.
 *            Reuses MoneynessForward's loader — the same bytes the ZMQ pricer path already
 *            reconciles against, so a divergent curve would have failed the mny-fwd probe.
 *   divs:    {DataFolder}/equity/usa/dividends/{TICKER}.csv through today plus the forward-
 *            fill forecast (DividendManager.jl get_dividends + forecast_dividends), with
 *            tᵢ = ((ex_date − calc_date).days + 1) / 365  (DividendManager.jl:762).
 *
 * Caching contract (the byte-identical rule): the bundle for one (ticker, calc date) is
 * resolved ONCE and serialized ONCE into a JSON object fragment; every request message for
 * that (ticker, date) embeds the SAME bytes. The Julia side content-hashes the arrays —
 * any re-resolution with different formatting would split one env into two hash clouds.
 * The fragment is refreshed after one day so a recalibrated curve starts a new hash on a
 * new calc date, which is exactly the intended behavior (the hash encodes ticker + date).
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public static class PricerEnvProvider
    {
        private static readonly ConcurrentDictionary<(string ticker, DateTime date), byte[]> Fragments = new();

        /// <summary>JSON object fragment for "env": {…} — rt/rc (rates), dt/da (divs), Float32.</summary>
        public static byte[] EnvFragment(string ticker, DateTime calcDate)
        {
            var key = (ticker.ToUpperInvariant(), calcDate);
            return Fragments.GetOrAdd(key, _ => BuildFragment(ticker, calcDate));
        }

        private static byte[] BuildFragment(string ticker, DateTime calcDate)
        {
            var upper = ticker.ToUpperInvariant();

            // Rates: the MoneynessForward loader IS the reconciled replica of the curve path —
            // the same bytes the ZMQ pricer path already reconciles against, so a divergent
            // curve would have failed the mny-fwd probe. Dividends are rebuilt below through
            // the same forecast logic DividendManager.jl applies (the pricer's schedule, not
            // the CSV-only list).
            var (times, rates) = MoneynessForward.CurveForEnv(upper, calcDate);

            // Dividends: replicate get_dividend_amount_times(ticker, calc_date)
            // = get_dividends(ticker, calc_date, Date(2030,1,1)) then tᵢ = ((ex − calc).days + 1)/365.
            var divs = new DividendManager().GetDividends(upper, calcDate, new DateTime(2030, 1, 1));
            var amounts = new List<float>();
            var divTimes = new List<float>();
            foreach (var d in divs.OrderBy(d => d.ExDate))
            {
                var tDiv = (float)((d.ExDate.Date - calcDate).Days + 1) / 365f;
                if (tDiv <= 0f) continue;   // ex-date before calc date has no time leg
                amounts.Add((float)d.Amount);
                divTimes.Add(tDiv);
            }

            var sb = new StringBuilder(256);
            sb.Append("{\"rt\":[");
            AppendFloats(sb, times);
            sb.Append("],\"rc\":[");
            AppendFloats(sb, rates);
            sb.Append("],\"da\":[");
            AppendFloats(sb, amounts);
            sb.Append("],\"dt\":[");
            AppendFloats(sb, divTimes);
            sb.Append("]}");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static void AppendFloats(StringBuilder sb, IEnumerable<float> values)
        {
            var first = true;
            foreach (var v in values)
            {
                if (!first) sb.Append(',');
                first = false;
                // "R" round-trips the float32 exactly and is locale-invariant; the Julia side
                // parses to Float32, so byte-identical senders produce identical arrays.
                sb.Append(v.ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }
}
