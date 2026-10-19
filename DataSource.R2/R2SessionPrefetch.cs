/*
 * R2SessionPrefetch — one batched lookup per underlying that answers, locally, every per-contract
 * "does the warehouse carry this?" question the plugin asks while subscribing and warming up:
 *
 *   ResolveSymbol(table, ticker) — Lean asks it per contract, per table, several times
 *   (subscription, seeding, history); on the cloudflare metadata route each ask cost a
 *   ~2.5 s catalog round trip (the 10-minute Isolator kill on the AWS paper run).
 *
 * The data-presence source is the loaded_symbol_sessions LEDGER, not the data tables: the ledger
 * already answers "which contracts of this underlying were loaded, for which session, into which
 * table" in one place — so ONE query builds the whole (table -> carried contracts) map. The
 * ledger is keyed by session_date and lags the live session by design (the loader writes during/
 * after the session), so the walk-back takes the most recent session WITH rows within 14 days —
 * the same session the warmup will serve history from. The ledger covers every option tick and
 * bar table, so the prefetch answers for the bar tables the warmup history actually reads, not
 * just the tick tables.
 *
 * Fail-open: a failed or empty batch leaves callers on the per-contract path; a prefetch problem
 * must never become silently missing data.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    public static class R2SessionPrefetch
    {
        private sealed class Session
        {
            /// <summary>"<table>\n<vendorTicker>" -> carried. Populated from the ledger only.</summary>
            public HashSet<string> Carried = new(StringComparer.Ordinal);
            /// <summary>Calendar days the reference session sits behind the requested one (0 = same day).</summary>
            public int ReferenceSessionDaysBack = -1;
            public DateTime LoadedUtc;
        }

        private static readonly ConcurrentDictionary<string, Session> Sessions = new();
        private static readonly object LoadGate = new();
        private static TimeSpan _ttl = TimeSpan.FromMinutes(15);

        /// <summary>How long a prefetched session is trusted before a refresh is forced.</summary>
        public static TimeSpan Ttl
        {
            get => _ttl;
            set => _ttl = value;
        }

        /// <summary>
        /// Prefetch one underlying's session: walk the ledger back from <paramref name="sessionUtc"/>
        /// to the most recent session with loaded rows (max 14 calendar days) and memoise which
        /// (table, contract) pairs it carries. Cheap to call repeatedly within the TTL.
        /// </summary>
        public static void Ensure(string underlying, DateTime sessionUtc, R2LocalCatalog catalog)
        {
            var key = SessionKey(underlying);
            if (Sessions.TryGetValue(key, out var s) && DateTime.UtcNow - s.LoadedUtc < _ttl)
            {
                return;
            }

            lock (LoadGate)
            {
                if (Sessions.TryGetValue(key, out s) && DateTime.UtcNow - s.LoadedUtc < _ttl)
                {
                    return;
                }

                var sw = System.Diagnostics.Stopwatch.StartNew();
                var session = new Session();
                var und = underlying.Replace("'", "''");

                // ONE query over the ledger: the last 14 days for this underlying, every option
                // table. Grouped client-side by session_date, then walk back to the newest day
                // with rows — its (table, symbol) rows ARE the prefetch map.
                var sql =
                    "SELECT session_date, table_name, symbol FROM r2.market.loaded_symbol_sessions " +
                    $"WHERE scope = 'option' AND underlying = '{und}' " +
                    $"AND session_date >= DATE '{sessionUtc.AddDays(-14):yyyy-MM-dd}' " +
                    $"AND session_date <= DATE '{sessionUtc:yyyy-MM-dd}';";
                try
                {
                    var bySession = new Dictionary<DateTime, List<(string table, string symbol)>>();
                    foreach (var row in catalog.CatalogRows(sql))
                    {
                        if (row.Length < 3 || string.IsNullOrEmpty(row[2])) continue;
                        if (!DateTime.TryParse(row[0], out var day)) continue;
                        if (!bySession.TryGetValue(day, out var list))
                        {
                            bySession[day] = list = new List<(string, string)>();
                        }
                        list.Add((row[1], row[2]));
                    }

                    for (var back = 0; back <= 14; back++)
                    {
                        var day = sessionUtc.Date.AddDays(-back);
                        if (!bySession.TryGetValue(day, out var rows) || rows.Count == 0) continue;

                        foreach (var (table, symbol) in rows)
                        {
                            session.Carried.Add(table + "\n" + symbol);
                        }
                        session.ReferenceSessionDaysBack = back;
                        var tables = rows.Select(r => r.table).Distinct().Count();
                        Log.Trace($"R2SessionPrefetch: {underlying} reference session {day:yyyy-MM-dd} " +
                                  $"({back} day(s) back): {session.Carried.Count} (table, contract) pairs across " +
                                  $"{tables} tables in {sw.ElapsedMilliseconds} ms.");
                        break;
                    }

                    if (session.ReferenceSessionDaysBack < 0)
                    {
                        Log.Trace($"R2SessionPrefetch: {underlying} no ledger rows within 14 days of " +
                                  $"{sessionUtc:yyyy-MM-dd} ({sw.ElapsedMilliseconds} ms) — per-contract checks stay live.");
                    }
                }
                catch (Exception error)
                {
                    // Fail open: nothing prefetched; the per-contract path answers as before.
                    Log.Error($"R2SessionPrefetch: ledger batch for {underlying} failed after {sw.ElapsedMilliseconds} ms — " +
                              $"falling back to per-contract checks: {error.Message}");
                }

                Sessions[key] = session;
            }
        }

        /// <summary>Drop a prefetched session (loader wrote new data; next Ensure re-reads).</summary>
        public static void Forget(string underlying)
        {
            Sessions.TryRemove(SessionKey(underlying), out _);
        }

        /// <summary>
        /// Cached answer to ResolveSymbol; null = not prefetched / unknown — callers fall back to
        /// the per-contract path. True/False when the prefetched ledger session answers it: the
        /// ledger row set is the authority for "was this contract loaded into this table".
        /// </summary>
        public static bool? CarriesSymbol(string underlying, string table, string ticker)
        {
            if (!Sessions.TryGetValue(SessionKey(underlying), out var s) ||
                DateTime.UtcNow - s.LoadedUtc >= _ttl)
            {
                return null;
            }
            // A session with no ledger rows within the window is a real "nothing loaded" answer
            // only when the ledger query itself succeeded — ReferenceSessionDaysBack >= 0 marks a
            // completed (possibly empty) lookup; -1 means the lookup found nothing at all, which
            // after a successful query is still an authoritative empty. Both answer False.
            return s.ReferenceSessionDaysBack >= 0
                ? s.Carried.Contains(table + "\n" + ticker)
                : (bool?)null;
        }

        private static string SessionKey(string underlying) => underlying.ToUpperInvariant();
    }
}
