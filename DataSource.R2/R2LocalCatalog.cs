/*
 * Route-1 local reads for the Lean plugin: read warehouse tables through the rclone mount
 * instead of the R2 catalog.
 *
 * Fino's Catalog.local_table_root / Catalog.scan_local do the same on the Julia side; this class
 * mirrors them so the plugin can serve Lean data from the local mirror. The catalog stays
 * canonical; the mount is the byte layer (rclone serves cached files and fetches missing ones).
 *
 * Deliberate decisions, mirroring the Julia original:
 *
 * - The table root is resolved from the CATALOG (name -> uuid directory: storage paths never carry
 *   the table name) and is re-resolved on every call. A drop-then-load REPLACES a table with a new
 *   uuid directory, so a cached root would quietly keep reading the replaced table's files.
 *   `iceberg_snapshots('r2.market.<table>')` is the SQL route on this DuckDB build; the
 *   `<table>$snapshots` metadata-table syntax does NOT exist for catalog-attached tables here.
 *
 * - allow_moved_paths := true + unsafe_enable_version_guessing = true is the route-1 recipe:
 *   Iceberg metadata stores ABSOLUTE s3:// URIs, so the scanner must be told the files were
 *   relocated; version guessing supplies the version because a catalog-written table has no
 *   version-hint file on disk.
 *
 * - SQL is fed to duckdb on STDIN, never argv, so the catalog token cannot leak into `ps`.
 *   duckdb stderr is captured and REDACTED with the token before it is ever logged or thrown:
 *   error output can echo the offending statement, and the attach statement contains the token.
 *
 * - This duckdb build emits a bare `true` row into CSV stdout from `CREATE OR REPLACE SECRET`
 *   (a boolean statement result, verified on v1.5.5). Run() drops bare true/false rows and any
 *   line starting with '-'; statement results are not data.
 *
 * - The mount's freshness is maintained by the r2-events service (vfs/forget + vfs/refresh on
 *   every object event), not by rclone itself.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads warehouse tables through the local rclone mirror (route 1: allow_moved_paths).
    /// </summary>
    public sealed class R2LocalCatalog
    {
        private readonly string _mountRoot;
        private readonly string _duckdbPath;
        private readonly string _warehouse;
        private readonly string _catalogUri;
        private readonly string _s3Endpoint;
        private readonly string _catalogToken;
        private readonly string _s3AccessKey;
        private readonly string _s3SecretKey;
        private readonly string _rootCachePath;

        /// <summary>The one duckdb process this catalog talks to (null when it could not be started).</summary>
        private R2DuckSession _session;

        /// <summary>Days materialised in that session, with the symbols each one holds.</summary>
        private readonly Dictionary<string, CachedDay> _days = new Dictionary<string, CachedDay>(StringComparer.Ordinal);
        private readonly object _daysGate = new object();
        private readonly int _cacheMaxEntries;
        private readonly long _cacheMaxRows;
        private long _cachedRows;
        private long _dayClock;
        private long _cacheHits;
        private long _cacheMisses;
        private long _evictions;

        /// <summary>
        /// Days that have been asked about once but not yet read into memory. The first question about
        /// a day is answered by the Iceberg scan (milliseconds); the read is only paid when the day is
        /// asked about again, which is what separates "warmup walks 60 lookback days, once each" from
        /// "the session being simulated asks 1,000 questions about one day".
        /// </summary>
        private readonly Dictionary<string, int> _touched = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// One day of one table, materialised once per process and answered from memory afterwards.
        /// A history call is one day of one table, and warmup asks the same day over and over (one
        /// question per contract, per resolution), so the read is done once and the questions are
        /// answered from memory — including "this day does not carry that symbol", which costs nothing.
        /// </summary>
        private sealed class CachedDay
        {
            public string Key;
            public string TableName;
            public HashSet<string> Symbols;
            public long Rows;
            public long LastUsed;
        }

        /// <summary>
        /// Table roots and resolved symbols, memoised for the life of the process. Both used to be
        /// answered by the REMOTE catalog on every call: AttachSql() + a catalog query measured
        /// 10.3 s against 0.39 s for the local scan it precedes, and the symbol check re-attached
        /// (plus scanned the tick table) once per contract.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _roots = new ConcurrentDictionary<string, string>();
        private readonly ConcurrentDictionary<string, string> _symbols = new ConcurrentDictionary<string, string>();
        private readonly object _resolveLock = new object();

        /// <summary>
        /// On-disk copy of the table-root map, so a cold start does not pay a catalog round trip per
        /// table. Path comes from cloudflare-r2.roots-cache; a drop-then-load does replace a table's
        /// uuid directory, which is what ForgetRoots() is for (a read through a stale root fails
        /// loudly rather than quietly).
        /// </summary>
        private static string DefaultRootCachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache", "r2-plugin", "table-roots.tsv");

        /// <summary>
        /// The configuration path every setting below hangs off. Lean's Config.Get resolves a
        /// JSONPath against the selected environment and the config root, and PREFERS an environment
        /// variable of exactly this name, so `cloudflare-r2.catalog-token` may be supplied in the
        /// environment instead of the file.
        /// </summary>
        private const string Prefix = "cloudflare-r2.";

        // Table names are internal identifiers; this keeps them out of SQL injection territory.
        private static readonly Regex TableName = new Regex("^[a-z_][a-z0-9_]*$", RegexOptions.Compiled);

        /// <summary>
        /// Reads every setting from the <c>cloudflare-r2</c> section of the Lean configuration.
        /// The arguments are test seams: pass nothing in production.
        /// </summary>
        public R2LocalCatalog(
            string mountRoot = null,
            string duckdbPath = null,
            string warehouse = null,
            string catalogUri = null,
            string s3Endpoint = null)
        {
            _mountRoot = Required(mountRoot, "mount-root");
            _duckdbPath = Required(duckdbPath, "duckdb-path");
            _warehouse = Required(warehouse, "warehouse");
            _catalogUri = Required(catalogUri, "catalog-uri");
            _s3Endpoint = Required(s3Endpoint, "s3-endpoint");

            // Credentials are optional here and validated where they are used: the catalog token is
            // required only when a table root has to be resolved from the catalog, and the S3 pair
            // only when a scan meets a deletion vector.
            _catalogToken = Setting("catalog-token");
            _s3AccessKey = Setting("s3-access-key");
            _s3SecretKey = Setting("s3-secret-access-key");

            _rootCachePath = Setting("roots-cache", DefaultRootCachePath);
            _cacheMaxEntries = Setting("cache-max-entries", 64);

            // The working set is the days the warehouse holds, not a number of days: a single
            // features day is ~458k rows and an option_quote_bar_second day ~445k, so a handful of
            // tables over the held days runs to nine figures of rows quickly. The budget has to
            // cover that working set or the cache evicts a day it is about to be asked about again
            // (measured: 8M rows evicted mid-warmup and every evicted day was re-read). DuckDB
            // spills temp tables past its own memory limit, so a generous row budget is safer than
            // a tight one that guarantees thrash.
            _cacheMaxRows = Setting("cache-max-rows", 30_000_000L);

            // One duckdb process for the life of the catalog: the preamble (SET/LOAD iceberg) is
            // applied once here rather than before every scan. A session that cannot start leaves
            // the process-per-scan path in place.
            try
            {
                _session = new R2DuckSession(_duckdbPath, ScanPreamble(), Redact);
            }
            catch (R2SessionException error)
            {
                Log.Trace("R2LocalCatalog: no persistent duckdb session, one process per scan: " + error.Message);
                _session = null;
            }
        }

        /// <summary>
        /// Reads a setting from the `cloudflare-r2` section, tolerating both shapes Lean configs use:
        /// a nested object (<c>"cloudflare-r2": { "mount-root": ... }</c>, resolved as a JSONPath) or a
        /// flat dotted key (<c>"cloudflare-r2.mount-root": ...</c>, reachable only through bracket
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

            // ['cloudflare-r2.mount-root'] addresses a single property whose name contains dots.
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

        private static string Required(string explicitValue, string name)
        {
            var value = string.IsNullOrEmpty(explicitValue) ? Setting(name) : explicitValue;
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"R2LocalCatalog: '{Prefix}{name}' is missing from the configuration.");
            }
            return value;
        }

        /// <summary>Root of the rclone mount; safe to log.</summary>
        public string MountRoot => _mountRoot;

        /// <summary>
        /// Local directory holding a table's Iceberg files, under the rclone mount.
        ///
        /// Memoised: resolving it means attaching the REMOTE Iceberg catalog and asking for the
        /// snapshot, which measured 10.3 s per call on this host against ~0.4 s for the local scan
        /// that follows it. Paying that per scan is what made a warmup die on Lean's five-minute
        /// Isolator budget. The disk cache means a cold process does not pay it once per table
        /// either; a drop-then-load that replaces the uuid directory is handled by ForgetRoots().
        /// </summary>
        public string TableRoot(string table)
        {
            RequireTable(table);
            if (_roots.TryGetValue(table, out var known) && Usable(known))
            {
                return known;
            }

            lock (_resolveLock)
            {
                if (_roots.TryGetValue(table, out known) && Usable(known))
                {
                    return known;
                }

                if (ReadRootCache().TryGetValue(table, out var persisted) && Usable(persisted))
                {
                    _roots[table] = persisted;
                    return persisted;
                }

                var resolved = ResolveRootFromCatalog(table);
                _roots[table] = resolved;
                WriteRootCache(table, resolved);
                return resolved;
            }
        }

        /// <summary>
        /// True when <paramref name="table"/> has a partition directory for the date of
        /// <paramref name="dayUtc"/>. The loader partitions `data/` by `day_ts_2` = days since
        /// 1970-01-01, so "does the warehouse hold this day" is a local directory listing of the
        /// mount: no duckdb process, no catalog, no network. Warmup lookbacks over a table holding a
        /// handful of loaded days are mostly days that are NOT held, and each of those used to cost
        /// a process spawn plus a 10 s catalog round trip to answer "no rows".
        ///
        /// Answers true whenever the listing cannot be read or understood: a listing problem must
        /// never turn into silently missing data.
        /// </summary>
        public bool HoldsDay(string table, DateTime dayUtc)
        {
            RequireTable(table);
            var wanted = (dayUtc.Date - DateTime.UnixEpoch).Days;
            try
            {
                var data = Path.Combine(TableRoot(table), "data");
                if (!Directory.Exists(data))
                {
                    return true;
                }

                var parsed = 0;
                foreach (var directory in Directory.EnumerateDirectories(data, "day_ts_2=*"))
                {
                    var name = Path.GetFileName(directory).Substring("day_ts_2=".Length);
                    if (!int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var day))
                    {
                        continue;
                    }
                    parsed++;
                    if (day == wanted)
                    {
                        return true;
                    }
                }
                return parsed == 0;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>A table root is usable when the mount actually resolves it to a metadata dir.</summary>
        private static bool Usable(string root) =>
            !string.IsNullOrEmpty(root) && Directory.Exists(root) &&
            Directory.Exists(Path.Combine(root, "metadata"));

        /// <summary>The name -> uuid resolution: the one step that needs the remote catalog.</summary>
        private string ResolveRootFromCatalog(string table)
        {
            var sql = AttachSql() +
                $"SELECT manifest_list FROM iceberg_snapshots('r2.market.{table}') " +
                "ORDER BY timestamp_ms DESC LIMIT 1;";

            string location = null;
            foreach (var row in Run(sql))
            {
                // The manifest path is the only s3:// row this statement can produce; anything
                // else (stray statement-result rows) must not be mistaken for it.
                if (row.Length > 0 && row[0].StartsWith("s3://", StringComparison.Ordinal))
                {
                    location = row[0];
                    break;
                }
            }
            if (location == null)
            {
                throw new InvalidOperationException($"R2LocalCatalog: no snapshot for {table}.");
            }

            // s3://trade/<key>/metadata/snap-<uuid>.avro  ->  <key>
            var key = Regex.Replace(location, "^s3://[^/]+/", string.Empty);
            key = Regex.Replace(key, "/metadata/.*$", string.Empty);
            return Path.Combine(_mountRoot, key);
        }

        private Dictionary<string, string> ReadRootCache()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(_rootCachePath))
                {
                    foreach (var line in File.ReadAllLines(_rootCachePath))
                    {
                        var tab = line.IndexOf('\t');
                        if (tab > 0)
                        {
                            map[line.Substring(0, tab)] = line.Substring(tab + 1);
                        }
                    }
                }
            }
            catch
            {
                // An unreadable cache is a cold cache, never an error.
            }
            return map;
        }

        private void WriteRootCache(string table, string root)
        {
            try
            {
                var map = ReadRootCache();
                map[table] = root;
                Directory.CreateDirectory(Path.GetDirectoryName(_rootCachePath));
                File.WriteAllLines(_rootCachePath, map.Select(entry => entry.Key + "\t" + entry.Value).ToArray());
            }
            catch
            {
                // Losing the cache only costs a catalog round trip next time.
            }
        }

        /// <summary>
        /// The warehouse ticker for a vendor ticker, validated against the tick table that will
        /// serve it — the authoritative "does the warehouse carry this symbol" list for that
        /// dataset; dim_symbol is the contract REGISTRY and lags reality. Null when the warehouse
        /// does not carry it.
        /// </summary>
        /// <param name="ticker">Vendor ticker as stored (equity: <c>CRWD</c>; option: <c>O:CRWD261002P00262500</c>).</param>
        /// <param name="table">The tick table to validate against, e.g. <c>option_trade_tick</c>.</param>
        public string ResolveSymbol(string ticker, string table = "equity_trade_tick")
        {
            if (string.IsNullOrEmpty(ticker))
            {
                return null;
            }
            RequireTable(table);

            // The empty string marks "the warehouse does not carry it", so a negative answer is
            // memoised too: Lean resolves the same contract several times per warmup (subscription,
            // seeding, history), and each check used to re-attach the remote catalog and scan the
            // tick table over the network.
            var key = table + "\n" + ticker;
            if (_symbols.TryGetValue(key, out var known))
            {
                return known.Length == 0 ? null : known;
            }

            // Read through the local table root, like every other scan (no catalog attach here).
            var root = TableRoot(table).Replace("'", "''");
            var sql = ScanPreamble() +
                $"SELECT symbol FROM iceberg_scan('{root}', allow_moved_paths := true) " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' LIMIT 1;";
            var found = string.Empty;
            foreach (var row in Run(sql))
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
        /// Rows of <paramref name="table"/> read from the local mirror, as string arrays.
        /// </summary>
        public IEnumerable<string[]> Scan(string table, string columns = "*", string where = null)
        {
            RequireTable(table);
            return Run(BuildScanSql(table, columns, where));
        }

        /// <summary>SQL of the route-1 scan, for callers that build on it (e.g. adding ORDER BY).</summary>
        public string ScanSql(string table, string columns = "*", string where = null)
        {
            RequireTable(table);
            return BuildScanSql(table, columns, where);
        }

        /// <summary>
        /// Runs arbitrary SQL through the stdin/CSV plumbing (same contract as Scan). Used by the
        /// tick reader, which needs its own WHERE/ORDER BY around a route-1 scan of a known table.
        /// </summary>
        public IEnumerable<string[]> Rows(string sql) => Run(sql);

        private string BuildScanSql(string table, string columns, string where)
        {
            var root = TableRoot(table).Replace("'", "''");
            var sql = ScanPreamble() +
                $"SELECT {columns} FROM iceberg_scan('{root}', allow_moved_paths := true)";
            if (!string.IsNullOrEmpty(where))
            {
                // The WHERE is assembled by internal callers from validated identifiers and
                // literals (ticker strings, epoch-nanosecond bounds); it is never user input.
                sql += $" WHERE {where}";
            }
            return sql + ";";
        }

        /// <summary>
        /// SQL preamble for route-1 scans.
        ///
        /// unsafe_enable_version_guessing: a catalog-written table has no version-hint file on
        /// disk, so the scanner must pick the latest metadata version itself.
        ///
        /// The s3 client settings exist for ONE case: Iceberg v3 deletion vectors. duckdb's
        /// allow_moved_paths rewrites data-file paths to the local root, but delete files
        /// (*-deletes.puffin) are still opened through their original s3:// URI (v1.5.5
        /// behaviour, verified), so without these settings a scan of a table carrying a deletion
        /// vector dies with "Could not resolve hostname ... trade.s3.auto.amazonaws.com". With
        /// them, the tiny delete file is fetched from the R2 S3 endpoint while every data file
        /// and all metadata still come from the local mount.
        /// </summary>
        public string ScanPreamble()
        {
            var sql = "SET unsafe_enable_version_guessing = true; ";
            if (!string.IsNullOrEmpty(_s3AccessKey) && !string.IsNullOrEmpty(_s3SecretKey))
            {
                sql +=
                    "SET s3_region = 'auto'; " +
                    $"SET s3_endpoint = '{_s3Endpoint.Replace("'", "''")}'; SET s3_url_style = 'path'; " +
                    $"SET s3_access_key_id = '{_s3AccessKey.Replace("'", "''")}'; " +
                    $"SET s3_secret_access_key = '{_s3SecretKey.Replace("'", "''")}'; ";
            }
            return sql;
        }

        private static void RequireTable(string table)
        {
            if (table == null || !TableName.IsMatch(table))
            {
                throw new ArgumentException($"R2LocalCatalog: invalid table name '{table}'.", nameof(table));
            }
        }

        private string AttachSql()
        {
            if (string.IsNullOrEmpty(_catalogToken))
            {
                throw new InvalidOperationException(
                    $"R2LocalCatalog: '{Prefix}catalog-token' is missing from the configuration " +
                    "and a table root has to be resolved from the catalog.");
            }
            var tok = _catalogToken.Replace("'", "''");
            return "INSTALL iceberg; LOAD iceberg; " +
                   $"CREATE OR REPLACE SECRET r2_cat (TYPE ICEBERG, TOKEN '{tok}'); " +
                   $"ATTACH '{_warehouse}' AS r2 (TYPE ICEBERG, ENDPOINT '{_catalogUri}'); ";
        }

        /// <summary>Removes every credential (catalog token, S3 keys) from text that will be logged or thrown.</summary>
        private string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }
            foreach (var secret in new[] { _catalogToken, _s3AccessKey, _s3SecretKey })
            {
                if (!string.IsNullOrEmpty(secret))
                {
                    text = text.Replace(secret, "***");
                }
            }
            return text;
        }

        /// <summary>A CSV line that carries data (statement results and duckdb chatter are not data).</summary>
        private static bool IsDataLine(string line)
        {
            // `-- Loading resources ...` style chatter, and the bare `true`/`false` rows this
            // duckdb build prints for boolean statements such as CREATE OR REPLACE SECRET.
            return line.Length > 0 && !line.StartsWith("-") && line != "true" && line != "false";
        }

        /// <summary>
        /// Runs SQL through the persistent session, falling back to a process per scan when no
        /// session is available. A failed query throws (from the session or from the process exit
        /// code) — an empty result always means the query ran and matched nothing.
        /// </summary>
        private IEnumerable<string[]> Run(string sql)
        {
            var session = _session;
            if (session != null && session.IsAlive)
            {
                return session.Query(sql);
            }
            if (session != null)
            {
                // bail-on-error kills the session on a failed statement, and that failure has already
                // been thrown to the caller. Keep answering, one process per scan, rather than
                // letting later calls read empty results.
                Log.Trace("R2LocalCatalog: duckdb session is gone; one process per scan from here on.");
                _session = null;
            }
            return RunOnce(sql);
        }

        /// <summary>
        /// The FROM-clause source for one day of a table. The first question about a day materialises
        /// it into the session (one read of that day's data file); every later question about the same
        /// day — other contracts, other resolutions, other windows — is answered from memory. Days too
        /// large to hold are served by the Iceberg scan as before.
        /// </summary>
        public string DaySource(string table, DateTime dayUtc)
        {
            RequireTable(table);
            var root = TableRoot(table).Replace("'", "''");
            var scan = $"iceberg_scan('{root}', allow_moved_paths := true)";

            var session = _session;
            if (session == null || !session.IsAlive || _cacheMaxEntries <= 0)
            {
                return scan;
            }

            var key = DayKey(table, dayUtc);
            lock (_daysGate)
            {
                if (_days.TryGetValue(key, out var cached))
                {
                    cached.LastUsed = ++_dayClock;
                    _cacheHits++;
                    return cached.TableName;
                }

                // First look at this day: the scan answers it, and the day is remembered. Reading a
                // day into memory costs ~40 ms against ~3 ms for a scan, so paying it for a day that
                // is never asked about again is a loss — measured as 754 evictions against 813 reads.
                if (!_touched.ContainsKey(key))
                {
                    _touched[key] = 1;
                    return scan;
                }
                _touched.Remove(key);

                _cacheMisses++;
                var built = TryMaterialiseDay(session, table, dayUtc, root, key);
                return built?.TableName ?? scan;
            }
        }

        /// <summary>
        /// What the day cache has done so far: entries, rows, questions answered from memory, reads
        /// paid to the warehouse, and evictions. Exposed for diagnostics — "is the cache working" and
        /// "is it evicting too early" should be answerable without reading the log closely.
        /// </summary>
        public string CacheReport()
        {
            lock (_daysGate)
            {
                return $"day cache: {_days.Count} days, {_cachedRows} rows, " +
                       $"{_cacheHits} hits, {_cacheMisses} reads, {_evictions} evictions";
            }
        }

        /// <summary>
        /// False only when a day already materialised is known not to carry the symbol, which answers
        /// "nothing to read" without a query. Unknown (no cached day yet) answers true, so the caller
        /// runs its scan and the day gets materialised.
        /// </summary>
        public bool HoldsSymbol(string table, DateTime dayUtc, string ticker)
        {
            if (string.IsNullOrEmpty(ticker))
            {
                return false;
            }
            lock (_daysGate)
            {
                return !_days.TryGetValue(DayKey(table, dayUtc), out var cached) ||
                       cached.Symbols.Contains(ticker);
            }
        }

        /// <summary>
        /// Drops the memoised table roots, resolved symbols and cached days — for a table that was
        /// replaced on the warehouse side (a drop-then-load gives it a new uuid directory). A read
        /// through a stale root fails loudly, but forgetting first keeps a rerun clean.
        /// </summary>
        public void ForgetRoots()
        {
            _roots.Clear();
            _symbols.Clear();
            lock (_daysGate)
            {
                foreach (var day in _days.Values)
                {
                    try
                    {
                        _session?.Query($"DROP TABLE IF EXISTS {day.TableName};");
                    }
                    catch (R2SessionException)
                    {
                        // A session that already failed lost its temp tables with the process.
                    }
                }
                _days.Clear();
                _touched.Clear();
                _cachedRows = 0;
            }
        }

        /// <summary>Releases the session (and with it every cached day).</summary>
        public void Dispose()
        {
            lock (_daysGate)
            {
                _days.Clear();
                _cachedRows = 0;
            }
            _session?.Dispose();
            _session = null;
        }

        private static string DayKey(string table, DateTime dayUtc) =>
            table + "\n" + (dayUtc.Date - DateTime.UnixEpoch).Days;

        /// <summary>Ticks carry sip_timestamp; every other table is bucketed by ts.</summary>
        private static string TimeColumn(string table) =>
            table.EndsWith("_tick", StringComparison.Ordinal) ? "sip_timestamp" : "ts";

        /// <summary>
        /// Reads one day of one table into a temp table and indexes the symbols it holds. Returns null
        /// (leaving the caller on the Iceberg scan) when the day is too large to hold or when duckdb
        /// refuses; a refused read is a failed statement, so it throws rather than being silently
        /// treated as an empty day.
        /// </summary>
        private CachedDay TryMaterialiseDay(R2DuckSession session, string table, DateTime dayUtc, string root, string key)
        {
            var day = (dayUtc.Date - DateTime.UnixEpoch).Days;
            var time = TimeColumn(table);
            R2Warehouse.UtcDayBoundsNs(dayUtc.Date, out var lo, out var hi);
            var from = $"iceberg_scan('{root}', allow_moved_paths := true)";
            var window = $"WHERE {time} >= make_timestamp_ns({lo}) AND {time} < make_timestamp_ns({hi})";

            var rows = 0L;
            foreach (var row in session.Query($"SELECT count(*) FROM {from} {window};"))
            {
                if (row.Length > 0)
                {
                    long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows);
                }
            }

            if (rows > _cacheMaxRows)
            {
                Log.Trace($"R2LocalCatalog: {table} {dayUtc:yyyy-MM-dd} holds {rows} rows, " +
                          $"over the {_cacheMaxRows} row cache budget; scanning it per request.");
                return null;
            }

            // A readable name: day_ts_2 is a count of days since 1970-01-01, which is what the
            // warehouse partitions by, but as a temp-table suffix it reads like a typo (20726 is
            // 2026-09-30, not July).
            var name = $"r2_day_{table}_{dayUtc.Date:yyyyMMdd}";
            session.Query($"CREATE OR REPLACE TEMP TABLE {name} AS SELECT * FROM {from} {window};");

            var symbols = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in session.Query($"SELECT DISTINCT symbol FROM {name};"))
            {
                if (row.Length > 0 && row[0].Length > 0)
                {
                    symbols.Add(row[0]);
                }
            }

            var entry = new CachedDay
            {
                Key = key,
                TableName = name,
                Symbols = symbols,
                Rows = rows,
                LastUsed = ++_dayClock
            };
            _days[key] = entry;
            _cachedRows += rows;
            Log.Trace($"R2LocalCatalog: cached {table} {dayUtc:yyyy-MM-dd} as {name} " +
                      $"({rows} rows, {symbols.Count} symbols)");
            EvictDays(session);
            if (_cacheMisses % 100 == 0)
            {
                Log.Trace($"R2LocalCatalog: {CacheReport()}");
            }
            return entry;
        }

        /// <summary>
        /// Drops least-recently-used days until the entry and row budgets are met.
        ///
        /// The most recent eight accesses are never evicted. A warmup walks a table day by day and
        /// asks a dozen questions about each one, so evicting strictly by recency would drop the day
        /// that is being worked on — the cache would thrash and every question would pay the read
        /// again. Keeping a small protected window costs a few entries and removes that entirely;
        /// if everything cached is that recent, the budgets are simply exceeded rather than thrashed.
        /// </summary>
        private void EvictDays(R2DuckSession session)
        {
            const int protectedRecent = 8;
            while ((_days.Count > _cacheMaxEntries || _cachedRows > _cacheMaxRows) && _days.Count > 1)
            {
                var cutoff = _dayClock - protectedRecent;
                CachedDay victim = null;
                foreach (var candidate in _days.Values)
                {
                    if (candidate.LastUsed <= cutoff && (victim == null || candidate.LastUsed < victim.LastUsed))
                    {
                        victim = candidate;
                    }
                }
                if (victim == null)
                {
                    return;
                }

                _days.Remove(victim.Key);
                _cachedRows -= victim.Rows;
                _evictions++;
                try
                {
                    session.Query($"DROP TABLE IF EXISTS {victim.TableName};");
                    Log.Trace($"R2LocalCatalog: evicted {victim.TableName} from the day cache " +
                              $"({_days.Count} days, {_cachedRows} rows left)");
                }
                catch (R2SessionException error)
                {
                    Log.Trace($"R2LocalCatalog: could not drop {victim.TableName}: {error.Message}");
                    return;
                }
            }
        }

        /// <summary>
        /// Runs duckdb in CSV mode with the SQL written to stdin (never argv — the token is in it).
        /// Non-zero exit throws with the (redacted) stderr, so "no rows" can never masquerade as
        /// "no data".
        /// </summary>
        private IEnumerable<string[]> RunOnce(string sql)
        {
            var psi = new ProcessStartInfo(_duckdbPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-csv");
            psi.ArgumentList.Add("-noheader");

            using var process = Process.Start(psi);
            process.StandardInput.Write(sql);
            process.StandardInput.Close();

            // Read stderr concurrently (drained, so the child can never block on a full pipe).
            var stderrTask = process.StandardError.ReadToEndAsync();

            using (var reader = process.StandardOutput)
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (IsDataLine(line))
                    {
                        yield return line.Split(',');
                    }
                }
            }

            process.WaitForExit();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"R2LocalCatalog: duckdb exited {process.ExitCode}: {Redact(stderr).Trim()}");
            }
        }
    }
}
