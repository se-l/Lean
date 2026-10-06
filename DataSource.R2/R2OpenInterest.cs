/*
 * R2OpenInterest — the answer to every open-interest request: zero.
 *
 * THE WAREHOUSE HOLDS NO OPEN INTEREST. The twenty tables cover trade/quote ticks and bars; there is
 * no OI table and nothing derived from one. This class exists so that absence is handled in ONE
 * place, deliberately, instead of surfacing as an unmapped-subscription error deep in a run.
 *
 * HOW LEAN ASKS FOR OI — the three paths that reach this plugin:
 *
 *   1. An explicit subscription: AddData<OpenInterest>(symbol, resolution). The config carries
 *      TickType.OpenInterest and Type == typeof(OpenInterest) (LeanData.GetDataType maps tick type
 *      -> data type), so it arrives at R2IcebergDataFeed.CreateSubscription.
 *   2. A history request: History<OpenInterest>(symbol, ...) — HistoryRequest with
 *      DataType == typeof(OpenInterest) and TickType == TickType.OpenInterest, which arrives at
 *      R2IcebergHistoryProvider.GetHistory. The same request is what feeds
 *      security.Cache.GetData<OpenInterest>() and the contract.OI values in Lean's own OI
 *      regression algorithm.
 *   3. Implicitly, every option/future security: SecurityCache.OpenInterest and Security.OpenInterest
 *      are long-valued and default to 0, so a security whose OI is never delivered already reads 0 —
 *      no synthesized data is needed for the property itself.
 *
 * WHY RETURN 0 INSTEAD OF NOTHING. An empty series is indistinguishable from "the loader has not
 * got there yet", so a consumer cannot tell a genuine absence from a data gap. A zero-valued point
 * says exactly one thing — "this warehouse does not carry OI, and the value is 0" — and keeps
 * History<OpenInterest>() shaped like a series rather than vanishing. The value is deliberately not
 * presented as measured data: it is a constant, and this file is the only place that produces it.
 *
 * CADENCE AND TIMESTAMPS mirror Lean's own OI conventions rather than inventing one:
 *
 *   - OI is a once-a-day figure. SubscriptionRequest and BaseDataRequest both special-case
 *     TickType.OpenInterest — "open interest data comes in once a day before market open, make the
 *     subscription start from midnight and use always open exchange" — so a per-session point is the
 *     right grain even when the subscription asks for a finer resolution.
 *   - The timestamp is midnight exchange time of the session's date, which is how Lean's own OI
 *     files are written (daily rows at 00:00; see the index-option OI zip) and consistent with the
 *     midnight subscription start above.
 *   - Weekends are skipped: they carry no sessions and the loader does no discovery for them either.
 *
 * If the warehouse ever does load OI, this class is the single thing to delete — the handlers branch
 * on it before any table routing, so removing the branch restores normal serving with no other edit.
 */

using System;
using System.Collections.Generic;

using QuantConnect;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Synthesizes the zero-valued open-interest series the warehouse cannot serve.
    /// </summary>
    public static class R2OpenInterest
    {
        /// <summary>
        /// True when a subscription or history request is asking for open interest — the one data
        /// kind this plugin answers without touching a table.
        /// </summary>
        public static bool IsRequested(TickType tickType) => tickType == TickType.OpenInterest;

        /// <summary>One zero-valued OI point for a session, stamped at midnight exchange time.</summary>
        public static OpenInterest Zero(Symbol symbol, DateTime sessionEt) =>
            new OpenInterest(sessionEt.Date, symbol, 0m);

        /// <summary>
        /// Zero-valued OI points for every session in an exchange-time window, inclusive of both ends.
        /// </summary>
        public static IEnumerable<OpenInterest> ZeroSeries(Symbol symbol, DateTime startEt, DateTime endEt)
        {
            for (var day = startEt.Date; day <= endEt.Date; day = day.AddDays(1))
            {
                if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                {
                    continue;
                }
                yield return Zero(symbol, day);
            }
        }
    }
}
