/*
 * The Lean plugin's warehouse reader, with the transport behind it configurable per class of
 * request — see R2Route.cs for the R2_METADATA_ROUTE / R2_DATA_ROUTE / R2_READ_ROUTE toggles.
 * What follows are the route-1 (catalog) decisions this class was built on; the cloudflare route
 * deliberately steps around the ones that live on this host.
 *
 * Fino's Catalog.local_table_root / Catalog.scan_local do the same on the Julia side; this class
 * mirrors them so the plugin can serve Lean data from the local mirror. The catalog stays
 * canonical; the mount is the byte layer (rclone serves cached files and fetches missing ones).
 *
 * Deliberate decisions, mirroring the Julia original:
 *
 * - The table root is resolved from the CATALOG (name -> uuid directory: storage paths never carry
 *   the table name) and is re-resolved on every call. A drop-then-load REPLACES a table with a new
 *   uuid directory, so a cached root would quietly keep reading the replaced table's files. The
 *   cloudflare metadata route makes that literal: every call asks the remote catalog and the root
 *   caches below are never consulted. The catalog route keeps them, because the alternative
 *   measured 10.3 s per resolve.
 *   `iceberg_snapshots('r2.market.<table>')` is the SQL route on this DuckDB build; the
 *   `<table>$snapshots` metadata-table syntax does NOT exist for catalog-attached tables here.
 *
 * - allow_moved_paths := true + unsafe_enable_version_guessing = true is the route-1 recipe:
 *   Iceberg metadata stores ABSOLUTE s3:// URIs, so the scanner must be told the files were
 *   relocated; version guessing supplies the version because a catalog-written table has no
 *   version-hint file on disk. The cloudflare data route needs neither: its scans run against the
 *   attached catalog, whose manifests carry the real URIs and the current snapshot.
 *
 * - SQL is fed to duckdb on STDIN, never argv, so the catalog token cannot leak into `ps`.
 *   duckdb stderr is captured and REDACTED with the token before it is ever logged or thrown:
 *   error output can echo the offending statement, and the attach statement contains the token.
 *
 * - This duckdb build emits a bare `true` row into CSV stdout from `CREATE OR REPLACE SECRET`
 *   (a boolean statement result, verified on v1.5.5). Run() drops bare true/false rows and any
 *   line starting with '-'; statement results are not data.
 *
 * - Transient failures are retried, briefly: the REST catalog answering 5xx/429 and connection
 *   errors cost a fresh duckdb process and a 1 s/2 s backoff, not the live deployment (a 502 from
 *   catalog.cloudflarestorage.com used to kill algorithm initialization mid-warmup). Auth and
 *   "not found" fail on the first attempt — no amount of retrying turns a bad token into a success.
 *
 * - The mount's freshness is maintained by the r2-events service (vfs/forget + vfs/refresh on
 *   every object event), not by rclone itself. That is the first line of defence; the second is
 *   here, active only on error: a 404 on a mount-served catalog-metadata file
 *   (__r2_data_catalog/&lt;uuid&gt;/metadata/...) is a STALE VIEW, not missing data — the reader's cached
 *   snapshot was expired by a writer-side commit while the mount still served it. Recovery
 *   (RecoverFromStaleView) refreshes the VFS view over the RC socket, forgets the memoised root
 *   and re-resolves the head from the REST catalog, then reruns — bounded, idempotent. A 404 on
 *   a direct s3:// read is NOT this: no cache sits in front of it, so a live snapshot referencing
 *   a missing file is an integrity breach (R2-STALE-VIEW-UNRESOLVED alert). Classification in
 *   R2StaleView, RC calls in R2RcloneRC.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

using Newtonsoft.Json.Linq;

using QuantConnect.Configuration;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads warehouse tables — through the local rclone mirror (route 1: allow_moved_paths) or
    /// straight from CloudFlare, per the R2Routing toggles the instance is built with.
    ///
    /// One instance serves BOTH handlers: the engine instantiates the feed and the history provider
    /// independently (MEF), but they read the same tables, so they share one duckdb process, one day
    /// cache and one root cache — a day materialised for warmup is reused by the feed's stream of
    /// that session. Production code takes <see cref="Shared"/>; explicitly constructed instances
    /// are test seams.
    /// </summary>
    public sealed class R2LocalCatalog
    {
        /// <summary>
        /// The shared instance, created from the configuration on first use. A configuration failure
        /// (a Required setting missing) is remembered by the Lazy and rethrown on every access, the
        /// same as a constructor throw in the unshared shape.
        /// </summary>
        private static readonly Lazy<R2LocalCatalog> SharedInstance =
            new Lazy<R2LocalCatalog>(() => new R2LocalCatalog());

        /// <summary>The process-wide catalog both handlers read through.</summary>
        public static R2LocalCatalog Shared => SharedInstance.Value;

        private readonly string _mountRoot;
        private readonly string _duckdbPath;

        /// <summary>Trino transport; null unless `cloudflare-r2.engine=trino`.</summary>
        private readonly TrinoSession _trino;
        private readonly string _warehouse;
        private readonly string _catalogUri;
        private readonly string _s3Endpoint;
        private readonly string _catalogToken;
        private readonly string _s3AccessKey;
        private readonly string _s3SecretKey;
        private readonly string _rootCachePath;
        private readonly R2Routing _routing;

        /// <summary>
        /// Deadline for one duckdb statement (a day materialisation, a catalog lookup). duckdb
        /// gives its own S3/catalog reads no client-side deadline, so a connection that dies
        /// mid-scan — observed as a CLOSE-WAIT socket to catalog.cloudflarestorage.com with unread
        /// bytes — leaves the engine waiting forever. Past this deadline the process is killed and
        /// the scan is retried on a fresh one; "timed out" is in IsTransient for exactly that.
        /// </summary>
        private readonly TimeSpan _scanTimeout;

        /// <summary>
        /// How many times a transient duckdb failure — the catalog answering 5xx/429, a connection
        /// error — is retried (see <see cref="RunOnce"/>). The default rides out a Cloudflare blip
        /// with a 1 s then 2 s backoff without meaningfully delaying warmup.
        /// </summary>
        private readonly int _catalogRetries;

        /// <summary>The one duckdb process this catalog talks to (null when it could not be started).</summary>
        private R2DuckSession _session;

        /// <summary>
        /// The session in which an ATTACH to the remote iceberg catalog has already succeeded. duckdb
        /// is not idempotent here (see AttachSql), and the marker has to be tied to the process, not
        /// to this object: the same catalog outlives session restarts and the per-scan fallback.
        /// </summary>
        private R2DuckSession _attachedSession;

        /// <summary>Days materialised in that session, with the symbols each one holds.</summary>
        private readonly Dictionary<string, CachedDay> _days = new Dictionary<string, CachedDay>(StringComparer.Ordinal);

        /// <summary>
        /// Per-(table, day) symbol sets (see <see cref="SymbolsFor"/>): the batching that turns a
        /// chain subscription's per-contract symbol checks from one table scan each into a hash
        /// lookup. Separate from the day cache because it answers presence only — no rows.
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

        /// <summary>
        /// Chain-batch counters, read by the history provider to label each request as
        /// served-from-memory or paid-for: a request that hits is cheap by construction, so a slow
        /// log line with a hit delta points at Lean's per-request plumbing, not the warehouse.
        /// </summary>
        public int BatchHits;
        public int BatchMisses;
        private readonly object _daysGate = new object();
        private readonly int _cacheMaxEntries;
        private readonly long _cacheMaxRows;
        private readonly long _batchMaxRows;
        private readonly long _batchCacheMaxRows;
        private readonly long _symbolCacheMaxRows;
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
        /// The snapshot id each table's root was resolved from, so stale-view recovery can tell
        /// "the head moved, rerun against the new snapshot" from "the head stands, this is mount
        /// listing lag". Filled by ResolveRootFromCatalog, dropped with the root on ForgetRoots.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _heads = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// The stale-view attempt budget and backoff (tighter than the catalog route's 1 s/2 s —
        /// the defect here is a local cache, not a Cloudflare blip). The query is fully
        /// re-runnable by contract, so each attempt starts from scratch on a fresh process.
        /// </summary>
        private const int StaleViewAttempts = 3;
        private static readonly TimeSpan[] StaleViewBackoff =
        {
            TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)
        };

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
        /// Matches the status code duckdb reports for a catalog request answered non-200:
        /// "returned a non-200 status code (BadGateway_502), with reason: Bad Gateway".
        /// </summary>
        private static readonly Regex Non200Status = new Regex(
            "returned a non-200 status code \\((?:[A-Za-z]+_)?(?<code>[0-9]{3})\\)", RegexOptions.Compiled);

/// <summary>
        /// Reads every setting from the `cloudflare-r2` section of the Lean configuration.
        /// Production code uses <see cref="Shared"/>; the arguments are test seams.
        /// </summary>
        public R2LocalCatalog(
            string mountRoot = null,
            string duckdbPath = null,
            string warehouse = null,
            string catalogUri = null,
            string s3Endpoint = null,
            R2Routing routes = null)
        {
            // Engine selection: "duckdb" (default) keeps the mount + child-process path; "trino"
            // reads the same warehouse through the always-on Trino coordinator over HTTP - no
            // mount, no duckdb binary, no local metadata mirroring. Readers and SQL builders are
            // shared; only the transport changes.
            // Default engine is trino (the always-on coordinator); duckdb stays opt-in only.
            // Host runs reach the coordinator on loopback; containers override trino-url with
            // http://host.docker.internal:18080.
            var engine = Setting("engine", "trino");
            var trinoUrl = Setting("trino-url", "http://127.0.0.1:18080");
            if (!string.IsNullOrEmpty(trinoUrl) &&
                string.Equals(engine, "trino", StringComparison.OrdinalIgnoreCase))
            {
                var trinoCatalog = Setting("trino-catalog", "r2");
                var trinoSchema = Setting("trino-schema", "market");
                _mountRoot = mountRoot ?? string.Empty;
                _duckdbPath = duckdbPath ?? string.Empty;
                _warehouse = warehouse ?? string.Empty;
                _catalogUri = catalogUri ?? string.Empty;
                _s3Endpoint = s3Endpoint ?? string.Empty;
                _routing = routes ?? R2Routing.FromConfiguration();
                _catalogToken = Setting("catalog-token");
                _s3AccessKey = Setting("s3-access-key");
                _s3SecretKey = Setting("s3-secret-access-key");
                _rootCachePath = Setting("roots-cache", DefaultRootCachePath);
                _cacheMaxEntries = Setting("cache-max-entries", 64);
                _catalogRetries = Setting("catalog-retries", 2);
                _scanTimeout = TimeSpan.FromSeconds(Setting("trino-timeout-s", 600));
                _cacheMaxRows = Setting("cache-max-rows", 30_000_000L);
                // Per chain-day budget. Measured: option_quote_bar_day ~4.6k rows, but
                // option_quote_bar_second 7,369,009 rows for one underlying-day; the batch is
                // gated by a pre-flight count(*) and abandoned above this budget.
                _batchMaxRows = Setting("batch-max-rows", 12_000_000L);
                _batchCacheMaxRows = Setting("batch-cache-max-rows", 12_000_000L);
                _symbolCacheMaxRows = Setting("symbol-cache-max-rows", 8_000_000L);
                _trino = new TrinoSession(
                    trinoUrl,
                    trinoCatalog,
                    trinoSchema,
                    Setting("trino-user", "seb"),
                    _scanTimeout);
                Log.Trace($"R2LocalCatalog: engine=trino, coordinator {trinoUrl}, " +
                          $"catalog {trinoCatalog}.{trinoSchema}");
                return;
            }

            _mountRoot = Required(mountRoot, "mount-root");
            _duckdbPath = Required(duckdbPath, "duckdb-path");
            _warehouse = Required(warehouse, "warehouse");
            _catalogUri = Required(catalogUri, "catalog-uri");
            _s3Endpoint = Required(s3Endpoint, "s3-endpoint");

            // Which transport serves what: metadata, data files, and what a read may keep
            // in-process. Defaults to the catalog route everywhere when nothing is configured.
            _routing = routes ?? R2Routing.FromConfiguration();

            // Credentials are optional here and validated where they are used: the catalog token is
            // required only when a table root has to be resolved from the catalog, and the S3 pair
            // only when a scan meets a deletion vector — or when the cloudflare data route makes a
            // table scan itself, which always goes over that endpoint (ScanPreamble).
            _catalogToken = Setting("catalog-token");
            _s3AccessKey = Setting("s3-access-key");
            _s3SecretKey = Setting("s3-secret-access-key");

            _rootCachePath = Setting("roots-cache", DefaultRootCachePath);
            _cacheMaxEntries = Setting("cache-max-entries", 64);
            _catalogRetries = Setting("catalog-retries", 2);
            _scanTimeout = TimeSpan.FromSeconds(Setting("scan-timeout-s", 180));

            // The working set is the days the warehouse holds, not a number of days: a single
            // features day is ~458k rows and an option_quote_bar_second day ~445k, so a handful of
            // tables over the held days runs to nine figures of rows quickly. The budget has to
            // cover that working set or the cache evicts a day it is about to be asked about again
            // (measured: 8M rows evicted mid-warmup and every evicted day was re-read). DuckDB
            // spills temp tables past its own memory limit, so a generous row budget is safer than
            // a tight one that guarantees thrash.
            _cacheMaxRows = Setting("cache-max-rows", 30_000_000L);
            _batchMaxRows = Setting("batch-max-rows", 12_000_000L);
            _batchCacheMaxRows = Setting("batch-cache-max-rows", 12_000_000L);
            _symbolCacheMaxRows = Setting("symbol-cache-max-rows", 8_000_000L);

            // One duckdb process for the life of the catalog: the preamble (SET/LOAD iceberg) is
            // applied once here rather than before every scan. A session that cannot start leaves
            // the process-per-scan path in place.
            try
            {
                _session = new R2DuckSession(_duckdbPath, ScanPreamble(), Redact, _scanTimeout);

                if (_routing.Data == R2Route.CloudFlare)
                {
                    // Every data-file scan on this route is `iceberg_scan('r2.market.<table>')`
                    // against the attached Iceberg REST catalog: attach once, here, so no scan ever
                    // pays for it and no caller has to think about it. A failed attach kills the
                    // session (bail-on-error), and the catch below puts the catalog on the
                    // per-scan path, which attaches inside its one-shot process instead.
                    AttachSession(_session);
                }
            }
            catch (R2SessionException error)
            {
                Log.Trace("R2LocalCatalog: no persistent duckdb session, one process per scan: " + error.Message);
                _session = null;
                _attachedSession = null;
            }
        }

        /// <summary>The routes this instance serves with, as configured.</summary>
        public R2Routing Routing => _routing;

        /// <summary>
        /// The routes, spelled out with what each one does here — the one line a run log needs to
        /// answer "where did this read come from".
        /// </summary>
        public string DescribeRoutes() =>
            $"routes: metadata {RouteWord(_routing.Metadata, "resolved from the catalog on every call", "memoised roots + on-disk cache")}, " +
            $"data {RouteWord(_routing.Data, "read straight from CloudFlare through the attached catalog", $"read through the rclone mount ({_mountRoot})")}, " +
            $"read {RouteWord(_routing.Read, "per request, nothing materialised", "day cache materialised in-process")}";

        private static string RouteWord(R2Route route, string cloudflare, string catalog) =>
            route == R2Route.CloudFlare ? $"cloudflare ({cloudflare})" : $"catalog ({catalog})";

        /// <summary>
        /// Reads a setting from the `cloudflare-r2` section, tolerating both shapes Lean configs use:
        /// a nested object (<c>"cloudflare-r2": { "mount-root": ... }</c>, resolved as a JSONPath) or a
        /// flat dotted key (<c>"cloudflare-r2.mount-root": ...</c>, reachable only through bracket
        /// syntax because Config.Get hands the string to JToken.SelectToken, where '.' separates
        /// path segments).
        /// </summary>
        internal static string Setting(string name)
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
        ///
        /// The cloudflare metadata route takes neither shortcut: every call puts the question to
        /// the remote catalog and nothing local is read or written. The memo's whole business case
        /// is not paying the round trip — on a route that has promised to ask the source every
        /// time, the memo would be a lie about freshness.
        /// </summary>
        public string TableRoot(string table)
        {
            RequireTable(table);

            if (_routing.Metadata == R2Route.CloudFlare)
            {
                return ResolveRootFromCatalog(table);
            }

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
        /// a process spawn plus a 10 s catalog round trip to answer "no rows" — the catalog route's
        /// cheap answer; the cloudflare metadata route defers the question to the scan itself.
        ///
        /// Answers true whenever the listing cannot be read or understood: a listing problem must
        /// never turn into silently missing data.
        /// </summary>
        public bool HoldsDay(string table, DateTime dayUtc)
        {
            RequireTable(table);

            if (_trino != null)
            {
                // Nothing to list locally; the scan's manifest stats answer it (a missing day is
                // "no rows", not an error) - the same contract as the cloudflare metadata route.
                return true;
            }

            if (_routing.Metadata == R2Route.CloudFlare)
            {
                // Nothing local to list on this route — and "cannot tell" must stay an attempt,
                // never a silence: the scan itself answers whether the warehouse holds the day
                // (its manifest stats prune the partitions), and for a day the warehouse does not
                // carry that answer is "no rows", not an error.
                return true;
            }

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
            // ensureAttach: the catalog must be attached for that query — on the live session or,
            // on the per-scan path, inside the one-shot process it spawns. Run owns the attach
            // bookkeeping; by the time rows are read the session that served them holds it.
            string location = null;
            foreach (var row in Run(RootSnapshotSql(table), ensureAttach: true))
            {
                // The manifest path is the only s3:// row this statement can produce; anything
                // else (stray statement-result rows) must not be mistaken for it. The snapshot id
                // rides along: stale-view recovery compares it against the re-resolved head.
                if (row.Length > 1 && row[1].StartsWith("s3://", StringComparison.Ordinal))
                {
                    _heads[table] = row[0];
                    location = row[1];
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

        private static string RootSnapshotSql(string table) =>
            "SELECT snapshot_id, manifest_list FROM iceberg_snapshots('r2.market." + table + "') " +
            "ORDER BY timestamp_ms DESC LIMIT 1;";

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
            // seeding, history), and each check used to re-attach the remote catalog and scan the
            // tick table over the network. The cloudflare metadata route does not memoise — its
            // whole identity is that every question is put to the source.
            var key = table + "\n" + ticker;
            if (_routing.Metadata == R2Route.Catalog && _symbols.TryGetValue(key, out var known))
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
                    if (_routing.Metadata == R2Route.Catalog)
                    {
                        _symbols[key] = ticker;
                    }
                    return ticker;
                }
            }

            // Prefetched (session, underlying) batch: on the cloudflare metadata route the
            // per-contract check below costs a ~2.5 s catalog round trip EACH; the ledger-sourced
            // batch answers for all contracts of the underlying across every option table in one
            // query. Only consulted when a prefetch exists for this ticker's underlying.
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

            var sql = ScanPreamble() +
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
            if (_routing.Metadata == R2Route.Catalog)
            {
                _symbols[key] = found;
            }
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

                if (!HoldsDay(table, dayUtc))
                {
                    var none = new R2Batch<TRow>(Array.Empty<TRow>(),
                        new Dictionary<string, (int Offset, int Count)>(StringComparer.Ordinal));
                    StoreBatch(key, none.FlatArray, none.Slices, 0);
                    return none;
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
                    foreach (var row in Rows(ScanPreamble() +
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

                    var sql = ScanPreamble() +
                              $"SELECT symbol, {columns} FROM {source} WHERE {where} " +
                              $"{grouping}ORDER BY symbol, {orderBy};";

                    if (_trino != null && cellParse != null)
                    {
                        // Straight from the page's JSON into the struct: no string[] and no JValue
                        // objects per row, only the reused cell buffer.
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
                _evictions++;
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
                _evictions++;
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
                if (_days.TryGetValue(key, out var cached))
                {
                    cached.LastUsed = ++_dayClock;
                    return cached.Symbols;
                }
                if (_daySymbols.TryGetValue(key, out var known))
                {
                    return known;
                }
            }

            try
            {
                var time = TimeColumn(table);
                R2Warehouse.UtcDayBoundsNs(dayUtc.Date, out var lo, out var hi);
                var sql = ScanPreamble() +
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
                    if (_daySymbols.Count >= 256)
                    {
                        // Bounded: the days a run touches are few, but a long backtest walks many.
                        _daySymbols.Clear();
                    }
                    _daySymbols[key] = symbols;
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
        /// Rows of <paramref name="table"/> read from the route's source, as string arrays.
        /// </summary>
        public IEnumerable<string[]> Scan(string table, string columns = "*", string where = null)
        {
            RequireTable(table);
            return Run(BuildScanSql(table, columns, where), _routing.Data == R2Route.CloudFlare);
        }

        /// <summary>SQL of the scan, for callers that build on it (e.g. adding ORDER BY).</summary>
        public string ScanSql(string table, string columns = "*", string where = null)
        {
            RequireTable(table);
            return BuildScanSql(table, columns, where);
        }

        /// <summary>
        /// Runs arbitrary SQL through the stdin/CSV plumbing (same contract as Scan). Used by the
        /// readers, which need their own WHERE/ORDER BY around a scan of a known table. On the
        /// cloudflare data route the FROM is the attached catalog's table, so the ATTACH is ensured
        /// exactly here, the one place every row-level read passes through.
        /// </summary>
        /// <summary>
        /// True when this (table, day) is held in memory as a materialised temp table — the caller
        /// uses it to say whether a slow read was a temp-table scan or an Iceberg scan.
        /// </summary>
        public bool DayIsMaterialised(string table, DateTime dayUtc)
        {
            lock (_daysGate)
            {
                return _days.ContainsKey(DayKey(table, dayUtc));
            }
        }

        public IEnumerable<string[]> Rows(string sql) =>
            _trino != null ? _trino.Query(sql) : Run(sql, _routing.Data == R2Route.CloudFlare);

        /// <summary>
        /// Rows of a CATALOG-SIDE table (dim/registry/ledger: option_contract_live,
        /// loaded_symbol_sessions, …) that exists only in the attached warehouse catalog — never a
        /// mount-routable data table, so the ATTACH is ensured unconditionally.
        /// </summary>
        public IEnumerable<string[]> CatalogRows(string sql) =>
            _trino != null ? _trino.Query(sql) : Run(sql, ensureAttach: true);

        /// <summary>
        /// The distinct vendor symbols a table holds for ONE underlying on ONE day, read through the
        /// attached catalog (cloudflare data route) or the local scan (catalog route). Diagnostic /
        /// ad-hoc use — the per-contract fast path goes through the ledger-sourced
        /// R2SessionPrefetch instead. Empty = the warehouse holds nothing here (a real answer).
        /// </summary>
        public IReadOnlyList<string> DistinctSymbols(string table, DateTime dayUtc, string underlying)
        {
            RequireTable(table);
            var und = underlying.Replace("'", "''");
            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            var timeColumn = table.Contains("_tick") ? "sip_timestamp" : "ts";
            var sql =
                ScanPreamble() +
                $"SELECT DISTINCT symbol FROM {DataFrom(table)} " +
                $"WHERE {timeColumn} >= make_timestamp_ns({lo}) AND {timeColumn} < make_timestamp_ns({hi}) " +
                $"AND symbol LIKE 'O:{und}%';";
            var result = new List<string>();
            foreach (var row in Run(sql, _routing.Data == R2Route.CloudFlare))
            {
                if (row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                {
                    result.Add(row[0]);
                }
            }
            return result;
        }

        private string BuildScanSql(string table, string columns, string where)
        {
            var sql = ScanPreamble() + $"SELECT {columns} FROM {DataFrom(table)}";
            if (!string.IsNullOrEmpty(where))
            {
                // The WHERE is assembled by internal callers from validated identifiers and
                // literals (ticker strings, epoch-nanosecond bounds); it is never user input.
                sql += $" WHERE {where}";
            }
            return sql + ";";
        }

        /// <summary>
        /// The FROM clause for scanning <paramref name="table"/>, per the data route: the table's
        /// local root under the rclone mount (catalog), or the catalog-attached table whose
        /// manifests and data files duckdb reads straight from CloudFlare over the S3 endpoint
        /// (cloudflare). Nothing in the readers is aware of which one applies.
        /// </summary>
        private string DataFrom(string table)
        {
            RequireTable(table);

            if (_trino != null)
            {
                return $"{_trino.Catalog}.{_trino.Schema}.{table}";
            }

            if (_routing.Data == R2Route.CloudFlare)
            {
                return $"iceberg_scan('r2.market.{table}')";
            }

            var root = TableRoot(table).Replace("'", "''");
            return $"iceberg_scan('{root}', allow_moved_paths := true)";
        }

        /// <summary>
        /// SQL preamble for scans.
        ///
        /// Catalog data route: unsafe_enable_version_guessing — a catalog-written table has no
        /// version-hint file on disk, so the scanner must pick the latest metadata version itself.
        ///
        /// The s3 client settings exist for TWO cases. The deliberate one: Iceberg v3 deletion
        /// vectors — duckdb's allow_moved_paths rewrites data-file paths to the local root, but
        /// delete files (*-deletes.puffin) are still opened through their original s3:// URI
        /// (v1.5.5 behaviour, verified), so without these settings a scan of a table carrying a
        /// deletion vector dies with "Could not resolve hostname ... trade.s3.auto.amazonaws.com".
        /// With them, the tiny delete file is fetched from the R2 S3 endpoint while every data
        /// file and all metadata still come from the local mount. And the enforced one: the
        /// cloudflare data route — its scans are catalog-attached iceberg_scan over absolute s3://
        /// URIs, so httpfs MUST be configured for the R2 endpoint or every read dies on the
        /// hostname error; the route therefore refuses to run without the credentials rather than
        /// surfacing duckdb's message.
        /// </summary>
        public string ScanPreamble()
        {
            if (_trino != null)
            {
                // Trino resolves the catalog itself; there is no SET/ATTACH preamble to send.
                return string.Empty;
            }

            if (_routing.Data == R2Route.CloudFlare)
            {
                if (string.IsNullOrEmpty(_s3AccessKey) || string.IsNullOrEmpty(_s3SecretKey))
                {
                    throw new InvalidOperationException(
                        "R2LocalCatalog: the cloudflare data route reads data files through the " +
                        "R2 S3 endpoint, so 'cloudflare-r2.s3-access-key' and " +
                        "'cloudflare-r2.s3-secret-access-key' are required in the configuration " +
                        "(no local mount exists for it to fall back to).");
                }
                return S3SettingsSql();
            }

            var sql = "SET unsafe_enable_version_guessing = true; ";
            if (!string.IsNullOrEmpty(_s3AccessKey) && !string.IsNullOrEmpty(_s3SecretKey))
            {
                sql += S3SettingsSql();
            }
            return sql;
        }

        private string S3SettingsSql() =>
            "SET s3_region = 'auto'; " +
            $"SET s3_endpoint = '{_s3Endpoint.Replace("'", "''")}'; SET s3_url_style = 'path'; " +
            $"SET s3_access_key_id = '{_s3AccessKey.Replace("'", "''")}'; " +
            $"SET s3_secret_access_key = '{_s3SecretKey.Replace("'", "''")}'; ";

        private static void RequireTable(string table)
        {
            if (table == null || !TableName.IsMatch(table))
            {
                throw new ArgumentException($"R2LocalCatalog: invalid table name '{table}'.", nameof(table));
            }
        }

        /// <summary>
        /// ATTACH statement for the remote catalog, or empty when the live session already has it
        /// attached.
        ///
        /// duckdb's ATTACH is not idempotent: a second `ATTACH ... AS r2` in the same process fails
        /// with "database with name \"r2\" already exists", and a failed statement is fatal to the
        /// session (bail on error). With a cold table-root cache several tables resolve in a row, so
        /// without this the second table's resolve would throw the session away and every later scan
        /// would pay a process spawn.
        /// </summary>
        private string AttachSql()
        {
            return CatalogAttached ? string.Empty : AttachSqlForNewProcess();
        }

        /// <summary>
        /// The full ATTACH block, unconditionally — for a process known to carry no attach yet,
        /// such as the one-shot processes of stale-view recovery and head re-resolution (their
        /// liveness cannot be read off CatalogAttached, which is keyed on the live session).
        /// </summary>
        private string AttachSqlForNewProcess()
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

        /// <summary>
        /// True when the live session is the one an ATTACH already succeeded in. Keyed on the session
        /// instance, so a recreated session — or the per-scan path, where there is no session at all —
        /// attaches again.
        /// </summary>
        private bool CatalogAttached => _session != null && ReferenceEquals(_attachedSession, _session);

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
        ///
        /// <paramref name="ensureAttach"/>: the SQL speaks to the attached warehouse catalog
        /// (iceberg_snapshots for root resolution, iceberg_scan('r2.market.<table>') on the
        /// cloudflare data route). The ATTACH is applied exactly here, before the SQL, so no caller
        /// has to know whether the live session already carries it or a fresh one-shot process has
        /// to be brought up with it.
        /// </summary>
        private IEnumerable<string[]> Run(string sql, bool ensureAttach)
        {
            var session = _session;
            if (session != null && !session.IsAlive)
            {
                // bail-on-error kills the session on a failed statement, and that failure has already
                // been thrown to the caller. Keep answering, one process per scan, rather than
                // letting later calls read empty results.
                Log.Trace("R2LocalCatalog: duckdb session is gone; one process per scan from here on.");
                _session = null;
                _attachedSession = null;
                session = null;
            }

            if (session != null)
            {
                try
                {
                    if (ensureAttach && !CatalogAttached)
                    {
                        AttachSession(session);
                    }
                    return session.Query(sql);
                }
                catch (R2SessionException error) when (R2StaleView.Matches(sql, error.Message, _mountRoot))
                {
                    // A mount-served metadata file 404ed: the reader's cached view is stale, the
                    // bucket is not. Never retried blind — the cached resolution would 404
                    // forever; the recovery invalidates the view, re-resolves the head and reruns.
                    return RecoverFromStaleView(sql, ensureAttach, error.Message);
                }
                catch (R2SessionException error) when (IsTransient(error.Message))
                {
                    // The failed statement has already killed the session (bail-on-error). A
                    // transient failure is retried below on the one-shot path, whose fresh process
                    // re-attaches from scratch and carries its own retry budget.
                    Log.Trace("R2LocalCatalog: transient duckdb failure; answering from a fresh process: " +
                              error.Message);
                    _session = null;
                    _attachedSession = null;
                }
            }

            var attach = ensureAttach && !CatalogAttached ? AttachSql() : string.Empty;
            return RunOnce(attach + sql);
        }

        /// <summary>
        /// Applies the catalog ATTACH to <paramref name="session"/> once and remembers it. A failed
        /// statement has already killed the session (bail-on-error), so the marker is only set for
        /// statements that actually ran — the same invariant the old in-line assignment kept.
        /// </summary>
        private void AttachSession(R2DuckSession session)
        {
            session.Query(AttachSql());
            _attachedSession = session;
        }

        /// <summary>
        /// Stale-view recovery, the second line of defence (r2-events' vfs/refresh on object
        /// events is the first; this runs only after a read already failed). Bounded and
        /// idempotent — every step is safe to repeat, and the budget is 3 attempts on a
        /// 500 ms/2 s/5 s backoff, a fresh process per attempt:
        ///
        /// 1. vfs/refresh the table's catalog subtree over the RC socket (drop the VFS/dir cache
        ///    entries the bucket no longer has);
        /// 2. forget the memoised root, head and days of the table (they pin the expired snapshot);
        /// 3. re-resolve the head from the REST catalog — one-shot duckdb with a fresh ATTACH, so
        ///    the FUSE mount is bypassed entirely — and log whether it moved;
        /// 4. rerun the statement. A moved head is a valid, point-in-time-consistent superset
        ///    (append-only ticks/bars); an unmoved head is mount listing lag and gets one retry.
        ///
        /// Exhaustion alerts (R2-STALE-VIEW-UNRESOLVED) and rethrows: a live snapshot's file must
        /// never be missing, and an error must never be read as "no rows".
        /// </summary>
        private IEnumerable<string[]> RecoverFromStaleView(string sql, bool ensureAttach, string failedMessage)
        {
            var table = TableFor(sql);
            var dir = MountRelativeDir(sql);

            for (var attempt = 1; attempt <= StaleViewAttempts; attempt++)
            {
                Thread.Sleep(StaleViewBackoff[attempt - 1]);
                if (dir != null)
                {
                    R2RcloneRC.RefreshDirectory(dir);
                }
                if (table != null)
                {
                    ForgetRoots(table);
                    CompareHead(table);
                }

                try
                {
                    return RunStaleViewAttempt(sql, ensureAttach);
                }
                catch (Exception error) when (R2StaleView.Matches(sql, error.Message, _mountRoot))
                {
                    if (attempt < StaleViewAttempts)
                    {
                        Log.Trace($"R2LocalCatalog: stale-view attempt {attempt} of {StaleViewAttempts} " +
                                  $"failed again: {error.Message}");
                        continue;
                    }
                    Log.Error($"R2-STALE-VIEW-UNRESOLVED: a live snapshot's file is still unreadable " +
                              $"after {StaleViewAttempts} refresh/re-resolve attempts" +
                              (table == null ? string.Empty : $" for {table}") +
                              "; the bucket and the reader now disagree: " + error.Message);
                    throw new R2SessionException(
                        $"R2LocalCatalog: stale catalog view did not recover in {StaleViewAttempts} " +
                        $"attempts: {error.Message}");
                }
            }

            // Unreachable: the loop either returns or throws.
            throw new R2SessionException("R2LocalCatalog: stale catalog view recovery exited its loop.");
        }

        /// <summary>
        /// One rerun of a stale-view recovery: the persistent session is restarted in place when
        /// there is one (the point of "restart the duckdb process between attempts"), otherwise
        /// the one-shot process serves the attempt. The killed process took its temp tables with
        /// it, so the day cache is discarded around the restart.
        /// </summary>
        private IEnumerable<string[]> RunStaleViewAttempt(string sql, bool ensureAttach)
        {
            if (_session != null)
            {
                _session.Restart();
                _attachedSession = null;
                DiscardSessionDays();
                if (ensureAttach)
                {
                    AttachSession(_session);
                }
                return _session.Query(sql);
            }

            var attach = ensureAttach ? AttachSqlForNewProcess() : string.Empty;
            return RunOnceCore(attach + sql);
        }

        /// <summary>
        /// Re-resolves the table's head from the authoritative source — a one-shot duckdb with a
        /// fresh ATTACH to the REST catalog; nothing here touches the mount — and records how it
        /// moved against the snapshot the failed scan was resolved from.
        /// </summary>
        private void CompareHead(string table)
        {
            try
            {
                var head = TryResolveHead(table);
                if (head == null)
                {
                    Log.Trace($"R2LocalCatalog: could not re-resolve the head of {table}; rerunning anyway.");
                    return;
                }
                if (_heads.TryGetValue(table, out var previous) && previous != head)
                {
                    Log.Trace($"R2LocalCatalog: {table} head moved {previous} -> {head}; " +
                              "rerunning against the new snapshot.");
                }
                else
                {
                    Log.Trace($"R2LocalCatalog: {table} head unchanged ({head}); " +
                              "treating the failure as mount listing lag.");
                }
                _heads[table] = head;
            }
            catch (Exception error)
            {
                // The re-resolve is diagnosis, not a gate: the bounded rerun below answers
                // regardless, and its failure surfaces on its own.
                Log.Trace($"R2LocalCatalog: head comparison for {table} failed: {error.Message}");
            }
        }

        /// <summary>The current snapshot id of <paramref name="table"/>, or null when unavailable.</summary>
        private string TryResolveHead(string table)
        {
            foreach (var row in RunOnceCore(AttachSqlForNewProcess() + RootSnapshotSql(table)))
            {
                if (row.Length > 1 && row[1].StartsWith("s3://", StringComparison.Ordinal))
                {
                    return row[0];
                }
            }
            return null;
        }

        /// <summary>
        /// The warehouse table a failing statement was about, when the statement names it: the
        /// attached-catalog spelling carries r2.market.&lt;table&gt;, a mount-rooted scan does not —
        /// for those the memoised roots give the reverse lookup (the scan root IS the memo value).
        /// </summary>
        private string TableFor(string sql)
        {
            var named = Regex.Match(sql, @"r2\.market\.([a-z_][a-z0-9_]*)");
            if (named.Success)
            {
                return named.Groups[1].Value;
            }
            foreach (var entry in _roots)
            {
                if (!string.IsNullOrEmpty(entry.Value) &&
                    sql.Contains(entry.Value, StringComparison.Ordinal))
                {
                    return entry.Key;
                }
            }
            return null;
        }

        /// <summary>
        /// The mount-relative catalog subtree a failing statement reads, for vfs/refresh — e.g.
        /// <c>__r2_data_catalog/&lt;uuid&gt;</c>; null when the statement names no catalog subtree.
        /// </summary>
        private static string MountRelativeDir(string sql)
        {
            var match = Regex.Match(sql, @"__r2_data_catalog[^'"";\s]*");
            return match.Success ? match.Value.TrimEnd('/') : null;
        }

        /// <summary>
        /// The killed process took its temp tables with it; the day cache must not go on answering
        /// from names that no longer exist anywhere.
        /// </summary>
        private void DiscardSessionDays()
        {
            lock (_daysGate)
            {
                _days.Clear();
                _touched.Clear();
                _cachedRows = 0;
            }
        }

        /// <summary>
        /// The FROM-clause source for one day of a table. The first question about a day materialises
        /// it into the session (one read of that day's data file); every later question about the same
        /// day — other contracts, other resolutions, other windows — is answered from memory. Days too
        /// large to hold are served by the Iceberg scan as before.
        ///
        /// The read route decides whether any of that happens: cloudflare means nothing is ever
        /// materialised and every question is put to the day's source as-is.
        /// </summary>
        public string DaySource(string table, DateTime dayUtc)
        {
            RequireTable(table);
            var from = DataFrom(table);

            if (_trino != null)
            {
                // No local materialisation on the Trino path: the engine caches metadata and data
                // blocks itself, and a temp-table copy would only duplicate them.
                return from;
            }

            if (_routing.Read == R2Route.CloudFlare)
            {
                return from;
            }

            var session = _session;
            if (session == null || !session.IsAlive || _cacheMaxEntries <= 0)
            {
                return from;
            }

            var key = DayKey(table, dayUtc);
            var materialise = System.Diagnostics.Stopwatch.StartNew();
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
                    return from;
                }
                _touched.Remove(key);

                _cacheMisses++;
                Log.Trace($"R2LocalCatalog: day cache: materialising {key} " +
                          $"(route read {_routing.Read}; bounded by scan-timeout {_scanTimeout.TotalSeconds:0}s).");
                var built = TryMaterialiseDay(session, table, dayUtc, from, key);
                if (built == null)
                {
                    return from;
                }
                // Slow-materialise probe: anything over 5 s is a stall (FUSE, lock, pipe), not work.
                if (materialise.ElapsedMilliseconds > 5000)
                {
                    Log.Trace($"R2LocalCatalog: SLOW materialise {key} took {materialise.ElapsedMilliseconds} ms " +
                              $"({built.Rows} rows) — investigate mount/session health if this recurs.");
                }
                return built.TableName;
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

        /// <summary>
        /// Forgets ONE table's memoised root, resolved head, symbols and cached days, and drops
        /// its entry from the on-disk root cache — stale-view recovery's step 2. The memoised
        /// values pin the expired snapshot's uuid directory; without this, the rerun would be
        /// handed the same dead root by <see cref="TableRoot"/> and 404 forever.
        /// </summary>
        public void ForgetRoots(string table)
        {
            RequireTable(table);
            _roots.TryRemove(table, out _);
            _heads.TryRemove(table, out _);
            _symbols.Clear();
            ForgetPersistedRoot(table);
            lock (_daysGate)
            {
                // Keyed on the day key (table + day), not the temp-table name: a table whose name
                // is a prefix of another's must not capture its days.
                var victims = _days.Values
                    .Where(day => day.Key.StartsWith(table + "\n", StringComparison.Ordinal))
                    .ToList();
                foreach (var day in victims)
                {
                    try
                    {
                        _session?.Query($"DROP TABLE IF EXISTS {day.TableName};");
                    }
                    catch (R2SessionException)
                    {
                        // A session that already failed lost its temp tables with the process.
                    }
                    _days.Remove(day.Key);
                    _cachedRows -= day.Rows;
                }
            }
        }

        /// <summary>Removes one table's entry from the on-disk root cache; a loss there only costs a catalog round trip.</summary>
        private void ForgetPersistedRoot(string table)
        {
            try
            {
                var map = ReadRootCache();
                if (map.Remove(table))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_rootCachePath));
                    File.WriteAllLines(_rootCachePath, map.Select(entry => entry.Key + "\t" + entry.Value).ToArray());
                }
            }
            catch
            {
                // An unwritable cache is self-healing: the next resolve overwrites it.
            }
        }

        /// <summary>
        /// Releases the session (and with it every cached day). On the shared instance this kills
        /// the session for every consumer, so it is for tests and process shutdown only — the
        /// duckdb child already exits with the engine through its own ProcessExit hook.
        /// </summary>
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
        /// treated as an empty day. <paramref name="from"/> is the scan the day is read from — local
        /// root or catalog-attached table, per the data route.
        /// </summary>
        private CachedDay TryMaterialiseDay(R2DuckSession session, string table, DateTime dayUtc, string from, string key)
        {
            var day = (dayUtc.Date - DateTime.UnixEpoch).Days;
            var time = TimeColumn(table);
            R2Warehouse.UtcDayBoundsNs(dayUtc.Date, out var lo, out var hi);
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
        /// "no data". A transient failure — the remote catalog answering 5xx/429, a connection
        /// error — is retried with a short backoff, each attempt a fresh process re-attaching from
        /// scratch: a Cloudflare blip must not be able to kill a live deployment mid-warmup. The
        /// whole result is read before it is handed over, so a retried attempt can never present a
        /// partial answer, and auth or "not found" failures cost no retry at all.
        /// </summary>
        private IEnumerable<string[]> RunOnce(string sql)
        {
            var attempts = 1 + Math.Max(0, _catalogRetries);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return RunOnceCore(sql);
                }
                catch (InvalidOperationException error) when (R2StaleView.Matches(sql, error.Message, _mountRoot))
                {
                    // Same defect as on the session path, different transport: the one-shot
                    // process read the stale view through the mount. Recovery reruns through the
                    // same one-shot plumbing (sql already carries the attach prefix).
                    return RecoverFromStaleView(sql, ensureAttach: false, error.Message);
                }
                catch (InvalidOperationException error) when (attempt < attempts && IsTransient(error.Message))
                {
                    var delay = TimeSpan.FromSeconds(attempt);
                    Log.Trace($"R2LocalCatalog: transient duckdb failure (attempt {attempt} of {attempts}), " +
                              $"retrying in {delay.TotalSeconds:0.#}s: {error.Message}");
                    Thread.Sleep(delay);
                }
            }
        }

        private List<string[]> RunOnceCore(string sql)
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

            // stdout is pumped into a queue so the read carries a deadline: a blocking ReadLine
            // cannot be abandoned, and the whole point here is that a dead catalog connection must
            // not be able to hold a caller forever (see _scanTimeout).
            var stdout = process.StandardOutput;
            var lines = new BlockingCollection<string>(new ConcurrentQueue<string>());
            var pump = new Thread(() =>
            {
                try
                {
                    string line;
                    while ((line = stdout.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }
                catch
                {
                    // The process went away; CompleteAdding below is the signal.
                }
                finally
                {
                    try
                    {
                        lines.CompleteAdding();
                    }
                    catch
                    {
                        // Already completed.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "r2-duckdb-scan-stdout"
            };
            pump.Start();

            var rows = new List<string[]>();
            var startedAt = DateTime.UtcNow;
            var lastBeat = startedAt;
            while (true)
            {
                if (lines.TryTake(out var line, 500))
                {
                    if (IsDataLine(line))
                    {
                        rows.Add(line.Split(','));
                    }
                    continue;
                }

                if (lines.IsCompleted)
                {
                    break;
                }

                var elapsed = DateTime.UtcNow - startedAt;
                if (elapsed >= _scanTimeout)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch
                    {
                        // Already gone.
                    }
                    throw new InvalidOperationException(
                        $"R2LocalCatalog: duckdb scan timed out after {elapsed.TotalSeconds:0}s " +
                        $"({rows.Count} rows read); the process was killed.");
                }
                if (DateTime.UtcNow - lastBeat >= TimeSpan.FromSeconds(10))
                {
                    Log.Trace($"R2LocalCatalog: duckdb scan still running " +
                              $"({elapsed.TotalSeconds:0}s, {rows.Count} rows, pid {process.Id}) — bounded by " +
                              $"scan-timeout {_scanTimeout.TotalSeconds:0}s.");
                    lastBeat = DateTime.UtcNow;
                }
            }

            process.WaitForExit();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"R2LocalCatalog: duckdb exited {process.ExitCode}: {Redact(stderr).Trim()}");
            }
            return rows;
        }

        /// <summary>
        /// True when a duckdb failure is the remote endpoint having a moment rather than a
        /// configuration problem: the REST catalog's server-side codes (5xx, 429) and connection
        /// errors. 401/403/404 are not retried — no amount of retrying turns a bad token or a
        /// missing table into a success.
        /// </summary>
        private static bool IsTransient(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            var match = Non200Status.Match(message);
            if (match.Success)
            {
                var code = int.Parse(match.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return code == 429 || code >= 500;
            }

            message = message.ToLowerInvariant();
            return message.Contains("could not connect") || message.Contains("connection refused") ||
                   message.Contains("connection reset") || message.Contains("timed out") ||
                   message.Contains("timeout") || message.Contains("temporarily unavailable");
        }
    }
}
