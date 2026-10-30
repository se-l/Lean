/*
 * The Lean plugin's warehouse reader: every statement goes to the always-on Trino coordinator
 * (see TrinoSession.cs for the HTTP statement protocol) and nothing is read locally.
 *
 * Fino's Catalog.jl does the same on the Julia side; the warehouse's catalog is canonical and the
 * coordinator owns the Iceberg metadata, the S3 credentials and the file reads. This class is the
 * single place the plugin turns a table name into SQL, and the single place it caches what the
 * readers ask for repeatedly.
 *
 * Deliberate decisions:
 *
 * - The FROM clause is `catalog.schema.table` for every table — data tables and catalog-side
 *   tables (registries, ledgers) alike. There is no ATTACH, no path and no local metadata root to
 *   resolve, so a drop-then-load cannot leave a stale path behind: the coordinator's catalog IS
 *   the answer.
 *
 * - The day window is a half-open predicate on the table's own time column, in epoch nanoseconds
 *   (`make_timestamp_ns`, translated to Trino's `from_unixtime_nanos` by TrinoSession). It sits on
 *   the column itself, never on a cast of it, so Iceberg manifest stats and Parquet row-group
 *   stats can both prune with it.
 *
 * - What is cached here is what the readers ask for over and over, and nothing else: the
 *   per-(table, day) symbol index (one DISTINCT symbol query answers every contract check of that
 *   day) and the per-(table, day, underlying) chain batches (the first contract's request loads the
 *   whole chain-day, every other contract is a slice lookup). Both are bounded by row and entry
 *   budgets and both are reported through BatchHits/BatchMisses.
 *
 * - A failed statement throws. An empty result always means the query ran and matched nothing, so
 *   "no data" can never be a transport failure in disguise.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads warehouse tables through the Trino coordinator, and caches the two things the readers
    /// ask for repeatedly: the per-(table, day) symbol index and the per-(table, day, underlying)
    /// chain batches.
    ///
    /// One instance serves BOTH handlers: the engine instantiates the feed and the history provider
    /// independently (MEF), but they read the same tables, so they share one coordinator connection
    /// and one set of caches — a chain-day read for warmup is reused by the feed's stream of that
    /// session. Production code takes <see cref="Shared"/>; the configuration is read in the
    /// constructor.
    /// </summary>
    public sealed class R2LocalCatalog
    {
        /// <summary>
        /// The shared instance, created from the configuration on first use. A configuration failure
        /// is remembered by the Lazy and rethrown on every access, the same as a constructor throw
        /// in the unshared shape.
        /// </summary>
        private static readonly Lazy<R2LocalCatalog> SharedInstance =
            new Lazy<R2LocalCatalog>(() => new R2LocalCatalog());

        /// <summary>The process-wide catalog both handlers read through.</summary>
        public static R2LocalCatalog Shared => SharedInstance.Value;

        /// <summary>Trino transport: the one path every statement takes.</summary>
        private readonly TrinoSession _trino;

        /// <summary>Coordinator URL, kept for the startup log and diagnostics.</summary>
        private readonly string _coordinator;

        /// <summary>
        /// Deadline for one statement (a day read, a catalog lookup). The coordinator gives a long
        /// scan no client-side deadline of its own, so this is what bounds a stuck read; it is also
        /// the TrinoSession HTTP timeout.
        /// </summary>
        private readonly TimeSpan _statementTimeout;

        /// <summary>
        /// Per-(table, day) symbol sets (see <see cref="SymbolsFor"/>): the batching that turns a
        /// chain subscription's per-contract symbol checks from one table scan each into a hash
        /// lookup. It answers presence only — no rows.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _daySymbols = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>
        /// Per-(table, day, underlying) row batches (see <see cref="RowsByUnderlying"/>): the first
        /// contract's request loads the whole chain's day, and every other contract of that
        /// (table, day, underlying) is answered from memory, keyed by contract symbol.
        /// Applies to every option table — bars and ticks — with a per-batch row budget.
        /// </summary>
        private sealed class BatchEntry
        {
            /// <summary>TRow[] — every row of the chain-day in one array (null when oversize).</summary>
            public Array Flat;
            /// <summary>Per-contract slices into <see cref="Flat"/>; null when oversize.</summary>
            public Dictionary<string, (int Offset, int Count)> Slices;
            public long RowCount;
            public long LastUsed;
        }

        /// <summary>One cached (table, day, symbol) read — the per-contract fallback path.</summary>
        private sealed class SymbolEntry
        {
            public object Rows;
            public long RowCount;
            public long LastUsed;
        }

        private readonly Dictionary<string, BatchEntry> _dayBatches = new Dictionary<string, BatchEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _batchGates = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, SymbolEntry> _symbolRows = new Dictionary<string, SymbolEntry>(StringComparer.Ordinal);
        private long _batchRows;
        private long _symbolRowCount;
        private long _symbolIndexRows;

        /// <summary>
        /// Chain-batch counters, read by the history provider to label each request as
        /// served-from-memory or paid-for: a request that hits is cheap by construction, so a slow
        /// log line with a hit delta points at Lean's per-request plumbing, not the warehouse.
        /// </summary>
        public int BatchHits;
        public int BatchMisses;
        private readonly object _daysGate = new object();
        private readonly int _cacheMaxEntries;
        private readonly long _symbolIndexMaxRows;
        private readonly long _batchMaxRows;
        private readonly long _batchCacheMaxRows;
        private readonly long _symbolCacheMaxRows;
        private long _dayClock;

        /// <summary>
        /// Resolved symbols, memoised for the life of the process. The symbol check is asked once per
        /// contract per table (subscription, seeding, history) and each miss used to cost a table
        /// scan; the empty string marks a negative answer, which is memoised too.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _symbols = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// The configuration path every setting below hangs off. Lean's Config.Get resolves a
        /// JSONPath against the selected environment and the config root, and PREFERS an environment
        /// variable of exactly this name, so `cloudflare-r2.trino-url` may be supplied in the
        /// environment instead of the file.
        /// </summary>
        private const string Prefix = "cloudflare-r2.";

        // Table names are internal identifiers; this keeps them out of SQL injection territory.
        private static readonly Regex TableName = new Regex("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>
        /// Reads every setting from the `cloudflare-r2` section of the Lean configuration. The
        /// coordinator is the only transport: there is no engine switch and no local fallback.
        /// </summary>
        public R2LocalCatalog()
        {
            // Host runs reach the coordinator on loopback; containers override trino-url with
            // http://host.docker.internal:18080.
            _coordinator = Setting("trino-url", "http://127.0.0.1:18080");
            var trinoCatalog = Setting("trino-catalog", "r2");
            var trinoSchema = Setting("trino-schema", "market");

            _cacheMaxEntries = Setting("cache-max-entries", 64);
            _statementTimeout = TimeSpan.FromSeconds(Setting("trino-timeout-s", 600));
            // Total symbols held across every cached (table, day) index — the row budget those
            // indexes used to share with the day cache.
            _symbolIndexMaxRows = Setting("cache-max-rows", 30_000_000L);
            // Per chain-day budget. Measured: option_quote_bar_day ~4.6k rows, but
            // option_quote_bar_second 7,369,009 rows for one underlying-day; the batch is gated by
            // a pre-flight count(*) and abandoned above this budget.
            _batchMaxRows = Setting("batch-max-rows", 12_000_000L);
            _batchCacheMaxRows = Setting("batch-cache-max-rows", 12_000_000L);
            _symbolCacheMaxRows = Setting("symbol-cache-max-rows", 8_000_000L);

            _trino = new TrinoSession(
                _coordinator,
                trinoCatalog,
                trinoSchema,
                Setting("trino-user", "seb"),
                _statementTimeout);

            Log.Trace($"R2LocalCatalog: engine=trino, coordinator {_coordinator}, " +
                      $"catalog {trinoCatalog}.{trinoSchema}");
        }

        /// <summary>
        /// The one route this catalog serves, spelled out for the startup log: which coordinator,
        /// which catalog.schema, and that every statement goes through it.
        /// </summary>
        public string DescribeRoutes() =>
            $"engine=trino: every statement goes to the coordinator at {_coordinator} " +
            $"(catalog {_trino.Catalog}.{_trino.Schema}, statement timeout {_statementTimeout.TotalSeconds:0}s)";

        /// <summary>
        /// Reads a setting from the `cloudflare-r2` section, tolerating both shapes Lean configs use:
        /// a nested object (<c>"cloudflare-r2": { "trino-url": ... }</c>, resolved as a JSONPath) or a
        /// flat dotted key (<c>"cloudflare-r2.trino-url": ...</c>, reachable only through bracket
        /// syntax because Config.Get hands the string to JToken.SelectToken, where '.' separates
        /// path segments).
        /// </summary>
        private static string Setting(string name)
        {
            var path = Prefix + name;
            var value = Config.Get(path, string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                return value.Trim();
            }

            // ['cloudflare-r2.trino-url'] addresses a single property whose name contains dots.
            value = Config.Get($"['{path}']", string.Empty);
            if (!string.IsNullOrEmpty(value))
            {
                return value.Trim();
            }
            return string.Empty;
        }

        private static string Setting(string name, string fallback)
        {
            var value = Setting(name);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        private static int Setting(string name, int fallback)
        {
            var value = Setting(name);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        private static long Setting(string name, long fallback)
        {
            var value = Setting(name);
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;
        }

        /// <summary>
        /// True when <paramref name="table"/> holds rows for <paramref name="dayUtc"/> — which this
        /// transport cannot answer without the scan, so it answers true and lets the coordinator's
        /// manifest stats prune. A day the warehouse does not carry is "no rows", not an error, and
        /// this keeps the readers' cheap early-out shaped the same as it always was.
        /// </summary>
        public bool HoldsDay(string table, DateTime dayUtc)
        {
            RequireTable(table);
            return true;
        }

        /// <summary>
        /// The warehouse ticker for a vendor ticker, validated against the table that will serve
        /// it — the authoritative "does the warehouse carry this symbol" list for that dataset;
        /// dim_symbol is the contract REGISTRY and lags reality. Null when the warehouse does not
        /// carry it.
        /// </summary>
        /// <param name="ticker">Vendor ticker as stored (equity: <c>CRWD</c>; option: <c>O:CRWD261002P00262500</c>).</param>
        /// <param name="table">The table to validate against, e.g. <c>equity_trade_tick</c>.</param>
        public string ResolveSymbol(string ticker, string table = "equity_trade_tick", DateTime? dayUtc = null)
        {
            if (string.IsNullOrEmpty(ticker))
            {
                return null;
            }
            RequireTable(table);

            // Option contracts are NOT existence-checked here. The chain registry (option_contract_live)
            // is a superset of the bar/tick tables, so thousands of contracts legitimately have no
            // rows for a given day/table; scanning per contract to prove that was the largest
            // remaining source of per-contract queries. Existence is answered by the
            // (table, day, underlying) chain batch — cached, already paid for by the rest of the
            // chain — and an absent contract simply reads as an empty slice.
            if (ticker.StartsWith("O:", StringComparison.Ordinal))
            {
                return ticker;
            }

            // The empty string marks "the warehouse does not carry it", so a negative answer is
            // memoised too: Lean resolves the same contract several times per warmup (subscription,
            // seeding, history).
            var key = table + "\n" + ticker;
            if (_symbols.TryGetValue(key, out var known))
            {
                return known.Length == 0 ? null : known;
            }

            // Day-scoped answer first: one DISTINCT-symbol query per (table, day) answers every
            // contract of that day, instead of the per-symbol scan below (~73 ms per contract
            // measured during a chain subscription: 1,636 contracts took 119 s). A positive answer
            // is authoritative for the run; a miss falls through to the per-symbol scan, because a
            // contract absent on the window's first day may still be carried later in it.
            if (dayUtc.HasValue)
            {
                var daySymbols = SymbolsFor(table, dayUtc.Value);
                if (daySymbols != null && daySymbols.Contains(ticker))
                {
                    _symbols[key] = ticker;
                    return ticker;
                }
            }

            // Prefetched (session, underlying) batch: the per-contract check below costs a catalog
            // round trip EACH, and the ledger-sourced batch answers for all contracts of the
            // underlying across every option table in one query. Only consulted when a prefetch
            // exists for this ticker's underlying.
            if (ticker.StartsWith("O:", StringComparison.Ordinal))
            {
                var root = UnderlyingOf(ticker);
                if (root != null)
                {
                    var carries = R2SessionPrefetch.CarriesSymbol(root, table, ticker);
                    if (carries.HasValue)
                    {
                        return carries.Value ? ticker : null;
                    }
                }
            }

            var sql =
                $"SELECT symbol FROM {DataFrom(table)} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' LIMIT 1;";
            var found = string.Empty;
            foreach (var row in Rows(sql))
            {
                if (row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                {
                    found = row[0];
                    break;
                }
            }
            _symbols[key] = found;
            return found.Length == 0 ? null : found;
        }

        /// <summary>
        /// String-row overload for callers that predate the typed caches — currently the IV reader,
        /// whose projection is consolidated in SQL and consumed as text.
        /// </summary>
        public R2Batch<string[]>? RowsByUnderlying(string table, DateTime dayUtc, string ticker, string columns,
            string predicate = "", string grouping = "", string orderBy = "1")
        {
            return RowsByUnderlying<string[]>(table, dayUtc, ticker, columns, IdentityParser, predicate, grouping,
                orderBy, IdentityCellParser);
        }

        private static bool IdentityParser(string[] row, out string[] value)
        {
            value = row;
            return true;
        }

        private static bool IdentityCellParser(JsonCell[] cells, int count, out string symbol, out string[] value)
        {
            symbol = count > 0 && cells[0].Kind == JsonCell.String ? cells[0].Text : null;
            value = new string[count > 0 ? count - 1 : 0];
            for (var i = 1; i < count; i++)
            {
                value[i - 1] = R2CellParser.Text(cells[i]);
            }
            return !string.IsNullOrEmpty(symbol);
        }

        /// <summary>
        /// One day of a table for EVERY contract of <paramref name="ticker"/>'s underlying.
        ///
        /// Stored as ONE flat array plus (offset, count) slices per contract: the batch SQL orders by
        /// symbol first, so a contract's rows are contiguous by construction. Per-contract
        /// List&lt;TRow&gt; growth doubled its capacity (~2x the payload in allocations) and every buffer
        /// past 85 KB landed on the LOH, which is collected only in gen2 and never compacted by
        /// default — that was the real reason a 7.4M-row chain-day peaked at several GB while holding
        /// ~350 MB of rows.
        ///
        /// The array length comes from a preflight `SELECT symbol, count(*) ... GROUP BY symbol`, so it
        /// is allocated exactly once. If the data shifts between the two queries the batch aborts and
        /// the caller keeps the per-contract path.
        /// </summary>
        public R2Batch<TRow>? RowsByUnderlying<TRow>(string table, DateTime dayUtc, string ticker,
            string columns, RowParser<TRow> parse, string predicate = "", string grouping = "", string orderBy = "1",
            CellRowParser<TRow> cellParse = null)
        {
            var root = ticker.StartsWith("O:", StringComparison.Ordinal) ? UnderlyingOf(ticker) : null;
            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            var day = dayUtc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var key = table + "\n" + day + "\n" + root;

            lock (_daysGate)
            {
                if (_dayBatches.TryGetValue(key, out var cached))
                {
                    cached.LastUsed = ++_dayClock;
                    BatchHits++;
                    // null slices = this chain-day exceeded the batch budget; the caller keeps the
                    // per-contract path and the negative entry stops repeated scans.
                    return Cached<TRow>(cached);
                }
            }

            // Single flight per (table, day, underlying): the contracts of one chain warm up
            // concurrently, and without this every thread would scan the same day before any of
            // them had a chance to store the batch.
            object gate;
            lock (_daysGate)
            {
                if (!_batchGates.TryGetValue(key, out gate))
                {
                    _batchGates[key] = gate = new object();
                }
            }

            lock (gate)
            {
                lock (_daysGate)
                {
                    if (_dayBatches.TryGetValue(key, out var raced))
                    {
                        raced.LastUsed = ++_dayClock;
                        BatchHits++;
                        return Cached<TRow>(raced);
                    }
                }

                try
                {
                    var time = TimeColumn(table);
                    R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
                    var source = DaySource(table, dayUtc);
                    var pattern = "^O:" + root.Replace("'", "''") + "[0-9]{6}[CP][0-9]{8}$";
                    // LIKE prunes row groups on the symbol stats; regexp_matches keeps the OSI shape
                    // exact (a root that prefixes another, BA vs BAC, would otherwise leak contracts).
                    var prefix = "O:" + root.Replace("'", "''");
                    var where =
                        $"symbol LIKE '{prefix}%' AND regexp_matches(symbol, '{pattern}') {predicate} " +
                        $"AND {time} >= make_timestamp_ns({lo}) AND {time} < make_timestamp_ns({hi})";

                    var started = Stopwatch.StartNew();
                    BatchMisses++;

                    // Exact per-contract counts: size the flat array once, no growth copies. Iceberg
                    // answers this from metadata in ~1 s even for a 7.4M-row chain-day (measured).
                    var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
                    long total = 0;
                    foreach (var row in Rows(
                                 $"SELECT symbol, count(*) FROM {source} WHERE {where} GROUP BY symbol;"))
                    {
                        if (row.Length < 2 || string.IsNullOrEmpty(row[0]) ||
                            !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                        {
                            continue;
                        }
                        capacities[row[0]] = n;
                        total += n;
                    }

                    if (total > _batchMaxRows)
                    {
                        Log.Trace($"R2LocalCatalog: chain batch {table} {day} {root} estimated " +
                                  $"{total} rows (> {_batchMaxRows}) — not cached, reads stay per contract.");
                        StoreBatch(key, null, null, 0);
                        return null;
                    }

                    if (total == 0)
                    {
                        var empty = new R2Batch<TRow>(Array.Empty<TRow>(),
                            new Dictionary<string, (int Offset, int Count)>(StringComparer.Ordinal));
                        StoreBatch(key, empty.FlatArray, empty.Slices, 0);
                        return empty;
                    }

                    var flat = new TRow[total];
                    var slices = new Dictionary<string, (int Offset, int Count)>(capacities.Count,
                        StringComparer.Ordinal);
                    var write = 0;
                    string current = null;
                    var start = 0;
                    var rejected = 0L;

                    void CloseContract()
                    {
                        if (current == null)
                        {
                            return;
                        }
                        if (slices.ContainsKey(current))
                        {
                            throw new InvalidOperationException($"rows for {current} are not contiguous");
                        }
                        slices[current] = (start, write - start);
                    }

                    void Add(string symbol, TRow value)
                    {
                        if (!string.Equals(symbol, current, StringComparison.Ordinal))
                        {
                            CloseContract();
                            current = symbol;
                            start = write;
                        }
                        if (write >= flat.Length)
                        {
                            throw new InvalidOperationException(
                                $"batch carries more rows than the preflight counted ({flat.Length})");
                        }
                        flat[write++] = value;
                        if (write % 1_000_000 == 0)
                        {
                            Log.Trace($"R2LocalCatalog: chain batch {table} {day} {root} progress " +
                                      $"{write} rows, managed {GC.GetTotalMemory(false) / (1024 * 1024)} MB, " +
                                      $"rss {Environment.WorkingSet / (1024 * 1024)} MB");
                        }
                    }

                    var sql =
                        $"SELECT symbol, {columns} FROM {source} WHERE {where} " +
                        $"{grouping}ORDER BY symbol, {orderBy};";

                    if (cellParse != null)
                    {
                        // Straight from the page's JSON into the struct: no string[] per row, only
                        // the reused cell buffer.
                        _trino.Execute(sql, (cells, count) =>
                        {
                            if (cellParse(cells, count, out var symbol, out var value))
                            {
                                Add(symbol, value);
                            }
                        });
                    }
                    else
                    {
                        foreach (var row in Rows(sql))
                        {
                            if (row.Length < 2 || string.IsNullOrEmpty(row[0]))
                            {
                                continue;
                            }
                            var rest = new string[row.Length - 1];
                            Array.Copy(row, 1, rest, 0, rest.Length);
                            if (!parse(rest, out var value))
                            {
                                rejected++;
                                continue;
                            }
                            Add(row[0], value);
                        }
                    }
                    CloseContract();

                    if (write != flat.Length)
                    {
                        Log.Trace($"R2LocalCatalog: chain batch {table} {day} {root} preflight counted " +
                                  $"{total} rows, read {write} — trimming.");
                        Array.Resize(ref flat, write);
                    }

                    if (write >= 1_000_000)
                    {
                        // Kill the page buffers and any parse garbage in one compacting collect, so
                        // RSS reflects the retained flat array rather than ingest churn.
                        System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                            System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                    }

                    StoreBatch(key, flat, slices, write);
                    Log.Trace($"R2LocalCatalog: chain batch {table} {day} {root}: " +
                              $"{slices.Count} symbols, {write} rows" +
                              $"{(rejected > 0 ? $" ({rejected} unparseable)" : string.Empty)} in " +
                              $"{started.ElapsedMilliseconds} ms ({_dayBatches.Count} chain-days cached, " +
                              $"{_batchRows} rows).");
                    return new R2Batch<TRow>(flat, slices);
                }
                catch (OutOfMemoryException)
                {
                    // Passed the estimate but still does not fit: remember it as oversize so the rest
                    // of the run reads per contract instead of retrying.
                    Log.Trace($"R2LocalCatalog: chain batch {table} {day} {root} ran out of memory — " +
                              "not cached, reads stay per contract.");
                    StoreBatch(key, null, null, 0);
                    GC.Collect();
                    return null;
                }
                catch (Exception error)
                {
                    // Deliberately not negative-cached: a transport failure must be retryable, and a
                    // count/data mismatch must be able to heal when the day's files settle.
                    Log.Error($"R2LocalCatalog: batched read of {table} {day} for {root} failed — " +
                              $"falling back to per-contract reads: {error.Message}");
                    return null;
                }
            }
        }

        private static R2Batch<TRow>? Cached<TRow>(BatchEntry entry)
        {
            return entry.Slices == null
                ? (R2Batch<TRow>?)null
                : new R2Batch<TRow>((TRow[])entry.Flat, entry.Slices);
        }

        /// <summary>
        /// One (table, day, symbol) read held in process, keyed by table/day/symbol/variant. This is
        /// what the per-contract fallback answers from: equities, chains larger than the batch
        /// budget, and any batch that failed. Without it a repeated question about the same
        /// contract-day would re-scan the warehouse.
        /// </summary>
        public List<TRow> RowsForSymbol<TRow>(string table, DateTime dayUtc, string ticker, string variant,
            Func<IEnumerable<string[]>> read, RowParser<TRow> parse, out bool cached)
        {
            var key = table + "\n" +
                      dayUtc.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "\n" +
                      ticker + "\n" + variant;

            lock (_daysGate)
            {
                if (_symbolRows.TryGetValue(key, out var hit))
                {
                    hit.LastUsed = ++_dayClock;
                    cached = true;
                    return (List<TRow>)hit.Rows;
                }
            }

            var rows = new List<TRow>();
            foreach (var raw in read())
            {
                if (parse(raw, out var value))
                {
                    rows.Add(value);
                }
            }

            lock (_daysGate)
            {
                if (_symbolRows.TryGetValue(key, out var raced))
                {
                    raced.LastUsed = ++_dayClock;
                    cached = true;
                    return (List<TRow>)raced.Rows;
                }
                _symbolRows[key] = new SymbolEntry { Rows = rows, RowCount = rows.Count, LastUsed = ++_dayClock };
                _symbolRowCount += rows.Count;
                EvictSymbolRowsLocked();
            }

            cached = false;
            return rows;
        }

        private void StoreBatch(string key, Array flat,
            Dictionary<string, (int Offset, int Count)> slices, long rowCount)
        {
            lock (_daysGate)
            {
                if (_dayBatches.TryGetValue(key, out var existing))
                {
                    _batchRows -= existing.RowCount;
                }
                _dayBatches[key] = new BatchEntry
                {
                    Flat = flat,
                    Slices = slices,
                    RowCount = rowCount,
                    LastUsed = ++_dayClock
                };
                _batchRows += rowCount;
                EvictBatchesLocked();
            }
        }

        /// <summary>LRU over chain-days on the shared entry/row budgets.</summary>
        private void EvictBatchesLocked()
        {
            const int protectedRecent = 8;
            while (_dayBatches.Count > _cacheMaxEntries || _batchRows > _batchCacheMaxRows)
            {
                var cutoff = _dayClock - protectedRecent;
                string victimKey = null;
                BatchEntry victim = null;
                foreach (var pair in _dayBatches)
                {
                    if (pair.Value.LastUsed <= cutoff && (victim == null || pair.Value.LastUsed < victim.LastUsed))
                    {
                        victim = pair.Value;
                        victimKey = pair.Key;
                    }
                }
                if (victim == null)
                {
                    return;
                }
                _dayBatches.Remove(victimKey);
                _batchRows -= victim.RowCount;
                Log.Trace($"R2LocalCatalog: evicted chain batch {victimKey.Replace('\n', '/')} " +
                          $"({_dayBatches.Count} chain-days, {_batchRows} rows left)");
            }
        }

        /// <summary>LRU over per-contract reads on the symbol-cache budget.</summary>
        private void EvictSymbolRowsLocked()
        {
            const int protectedRecent = 8;
            while (_symbolRows.Count > _cacheMaxEntries || _symbolRowCount > _symbolCacheMaxRows)
            {
                var cutoff = _dayClock - protectedRecent;
                string victimKey = null;
                SymbolEntry victim = null;
                foreach (var pair in _symbolRows)
                {
                    if (pair.Value.LastUsed <= cutoff && (victim == null || pair.Value.LastUsed < victim.LastUsed))
                    {
                        victim = pair.Value;
                        victimKey = pair.Key;
                    }
                }
                if (victim == null)
                {
                    return;
                }
                _symbolRows.Remove(victimKey);
                _symbolRowCount -= victim.RowCount;
            }
        }

        /// <summary>
        /// Every symbol one table holds on one UTC day, built with a single
        /// <c>SELECT DISTINCT symbol</c> over that day's window and memoised.
        ///
        /// This is the batching that makes a chain subscription O(1) per contract: Lean asks
        /// <see cref="ResolveSymbol"/> once per contract per table while subscribing (and again for
        /// history), and each answer used to be its own scan of the whole table. Null when the
        /// query could not answer (the caller then falls back to the per-symbol scan).
        /// </summary>
        public HashSet<string> SymbolsFor(string table, DateTime dayUtc)
        {
            RequireTable(table);
            var key = DayKey(table, dayUtc);
            lock (_daysGate)
            {
                if (_daySymbols.TryGetValue(key, out var known))
                {
                    return known;
                }
            }

            try
            {
                var time = TimeColumn(table);
                R2Warehouse.UtcDayBoundsNs(dayUtc.Date, out var lo, out var hi);
                var sql =
                    $"SELECT DISTINCT symbol FROM {DataFrom(table)} " +
                    $"WHERE {time} >= make_timestamp_ns({lo}) AND {time} < make_timestamp_ns({hi});";
                var symbols = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in Rows(sql))
                {
                    if (row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                    {
                        symbols.Add(row[0]);
                    }
                }
                lock (_daysGate)
                {
                    // Bounded twice over: a long backtest walks many days, so the index is dropped
                    // wholesale once it holds too many days or too many symbols.
                    if (_daySymbols.Count >= 256 || _symbolIndexRows > _symbolIndexMaxRows)
                    {
                        _daySymbols.Clear();
                        _symbolIndexRows = 0;
                    }
                    _daySymbols[key] = symbols;
                    _symbolIndexRows += symbols.Count;
                }
                return symbols;
            }
            catch (Exception error)
            {
                Log.Error($"R2LocalCatalog: symbol index for {table} {dayUtc:yyyy-MM-dd} failed — " +
                          $"falling back to per-symbol checks: {error.Message}");
                return null;
            }
        }

        /// <summary>
        /// OSI root: the leading alpha run of the vendor ticker's body (O:GE260619C00130000 -> GE).
        /// Cheap, deterministic; a wrong split merely misses the cache and falls back to the
        /// per-contract path.
        /// </summary>
        private static string UnderlyingOf(string ticker)
        {
            var body = ticker.Substring(2);
            var n = 0;
            while (n < body.Length && !char.IsDigit(body[n])) n++;
            return n == 0 ? null : body.Substring(0, n);
        }

        /// <summary>
        /// Always false: there are no TEMP TABLEs on this transport — the coordinator caches metadata
        /// and data blocks itself and nothing is materialised in this process. The member stays so a
        /// caller can label a read's source without branching on the transport.
        /// </summary>
        public bool DayIsMaterialised(string table, DateTime dayUtc) => false;

        /// <summary>
        /// Rows of arbitrary SQL, as invariant strings. The whole result is read before it is
        /// returned, so an exception always means the statement failed and never that a partial
        /// answer was passed off as a complete one.
        /// </summary>
        public IEnumerable<string[]> Rows(string sql) => _trino.Query(sql);

        /// <summary>
        /// Rows of a CATALOG-SIDE table (dim/registry/ledger: option_contract_live,
        /// loaded_symbol_sessions, …). Same transport as <see cref="Rows"/>; kept as a separate name
        /// because these tables live in the warehouse catalog rather than being data tables, and the
        /// distinction is worth seeing at the call site.
        /// </summary>
        public IEnumerable<string[]> CatalogRows(string sql) => _trino.Query(sql);

        /// <summary>
        /// The FROM clause for scanning <paramref name="table"/>: the coordinator's
        /// catalog.schema.table, whose manifests and data files Trino resolves and reads itself.
        /// Nothing in the readers is aware of where the bytes live.
        /// </summary>
        private string DataFrom(string table)
        {
            RequireTable(table);
            return $"{_trino.Catalog}.{_trino.Schema}.{table}";
        }

        /// <summary>
        /// SQL preamble for scans. Empty: the coordinator resolves the catalog, its credentials and
        /// the data files from its own configuration, so a statement carries no SET/LOAD/ATTACH
        /// ahead of it. The member stays because the readers concatenate it into the SQL they build.
        /// </summary>
        public string ScanPreamble() => string.Empty;

        private static void RequireTable(string table)
        {
            if (table == null || !TableName.IsMatch(table))
            {
                throw new ArgumentException($"R2LocalCatalog: invalid table name '{table}'.", nameof(table));
            }
        }

        /// <summary>
        /// The FROM-clause source for one day of a table. Always the coordinator's
        /// catalog.schema.table: Trino prunes the day predicate inside the scan and caches metadata
        /// and data blocks itself, so nothing is materialised here for a day to be worth naming.
        /// </summary>
        public string DaySource(string table, DateTime dayUtc)
        {
            RequireTable(table);
            return DataFrom(table);
        }

        /// <summary>
        /// True: nothing is materialised on this transport for a day to be known absent from, so the
        /// caller runs its scan and the coordinator's manifest stats prune. An empty ticker is the
        /// one negative answer. The member stays so the readers' early-out keeps its shape.
        /// </summary>
        public bool HoldsSymbol(string table, DateTime dayUtc, string ticker)
        {
            if (string.IsNullOrEmpty(ticker))
            {
                return false;
            }
            RequireTable(table);
            return true;
        }

        /// <summary>
        /// Releases the coordinator connection and every in-process cache. On the shared instance
        /// this affects every consumer, so it is for tests and process shutdown only.
        /// </summary>
        public void Dispose()
        {
            lock (_daysGate)
            {
                _daySymbols.Clear();
                _dayBatches.Clear();
                _symbolRows.Clear();
                _batchRows = 0;
                _symbolRowCount = 0;
                _symbolIndexRows = 0;
            }
            _trino.Dispose();
        }

        private static string DayKey(string table, DateTime dayUtc) =>
            table + "\n" + (dayUtc.Date - DateTime.UnixEpoch).Days;

        /// <summary>Ticks carry sip_timestamp; every other table is bucketed by ts.</summary>
        private static string TimeColumn(string table) =>
            table.EndsWith("_tick", StringComparison.Ordinal) ? "sip_timestamp" : "ts";
    }
}