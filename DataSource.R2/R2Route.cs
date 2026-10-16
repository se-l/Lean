/*
 * The three transport toggles behind the R2 plugin. Every request the plugin serves is either
 * answered through the local mirror (the rclone mount R2LocalCatalog reads, "the catalog" in
 * route-1 terms) or put to CloudFlare directly — and which of the two applies is a separate
 * decision for metadata, for data files and for the reads' in-process state:
 *
 *     R2_METADATA_ROUTE   table metadata: table-root resolution and day-holding checks.
 *         catalog         memoised roots + on-disk roots cache, the remote Iceberg catalog as
 *                         the fallback (today's route-1 behaviour).
 *         cloudflare      every resolve is put to the CloudFlare Iceberg REST catalog; no local
 *                         file is ever read or written, so a drop-then-load can never be read
 *                         through a stale root and nothing on this host has to exist at all.
 *
 *     R2_DATA_ROUTE       data files: the bytes of the daily Parquet parts.
 *         catalog         iceberg_scan over the table's local root under the mount.
 *         cloudflare      iceberg_scan('r2.market.<table>') over the attached catalog: duckdb
 *                         resolves the manifests and reads the bytes straight from the R2 S3
 *                         endpoint. No mount, no local path anywhere in the FROM clause.
 *
 *     R2_READ_ROUTE       read plumbing: what a read may use that is not the network.
 *         catalog         the day cache: a day asked about twice is materialised once into the
 *                         duckdb session and answered from memory afterwards.
 *         cloudflare      per request, every read goes to the source
 *
 * Accepted values for all three are `catalog` (alias: `local`) and `cloudflare`, case-
 * insensitive. Each is spelled three ways, in precedence order:
 *
 *     1. environment:        R2_METADATA_ROUTE / R2_DATA_ROUTE / R2_READ_ROUTE
 *     2. flat config key:    "cloudflare-r2.metadata_route" / "data_route" / "read_route"
 *                            (the dashed form "cloudflare-r2.metadata-route" works too)
 *     3. nested config:      "cloudflare-r2": { "metadata_route": ... }
 *
 * Lean's Config.Get prefers an environment variable of exactly the key's name. An unset route
 * stays on `catalog`, so configuring one route never silently moves the other two: production on
 * AWS sets all three to `cloudflare` and is free of any local referencing, while a development
 * box can mix (e.g. metadata resolved freshly from CloudFlare, bytes still read from the warm
 * local mount).
 */

using System;
using QuantConnect.Configuration;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>Which transport serves a class of requests.</summary>
    public enum R2Route
    {
        /// <summary>Served through the local mirror: the rclone mount, the on-disk caches and the day cache.</summary>
        Catalog,

        /// <summary>Served straight from CloudFlare: the attached Iceberg REST catalog and the R2 S3 endpoint — no local referencing.</summary>
        CloudFlare
    }

    /// <summary>
    /// The route for each class of request, read once from the configuration. Production code
    /// uses <see cref="FromConfiguration"/>; constructing one explicitly is a test seam.
    /// </summary>
    public sealed class R2Routing
    {
        public R2Routing(R2Route metadata, R2Route data, R2Route read)
        {
            Metadata = metadata;
            Data = data;
            Read = read;
        }

        /// <summary>Where table metadata (roots, day-holding) is answered.</summary>
        public R2Route Metadata { get; }

        /// <summary>Where data-file bytes are read.</summary>
        public R2Route Data { get; }

        /// <summary>What the read path may keep in-process between requests.</summary>
        public R2Route Read { get; }

        /// <summary>Reads the three routes: R2_* environment variables first, the
        /// cloudflare-r2.* settings second (both the dashed and the underscore spelling, the way
        /// the rest of the config's flat keys are written), the catalog route as the standing
        /// default.</summary>
        public static R2Routing FromConfiguration()
        {
            return new R2Routing(
                Parse("metadata", "R2_METADATA_ROUTE", "metadata-route"),
                Parse("data", "R2_DATA_ROUTE", "data-route"),
                Parse("read", "R2_READ_ROUTE", "read-route"));
        }

        /// <summary>
        /// Parses one route. <c>catalog</c>/<c>local</c> is the local mirror and stands when
        /// nothing is configured; <c>cloudflare</c> is the direct route; anything else refuses to
        /// start rather than being read as a default.
        /// </summary>
        private static R2Route Parse(string what, string envName, string configKey)
        {
            // Config.Get prefers an environment variable of exactly this name and otherwise looks
            // the name up in the config; the cloudflare-r2 keys are the file-spelled forms of the
            // same setting. Both spellings are accepted because the section mixes styles (it
            // carries both "s3_endpoint" and "s3-endpoint" keys).
            var value = Config.Get(envName, string.Empty);
            if (string.IsNullOrEmpty(value))
            {
                value = R2LocalCatalog.Setting(configKey);
            }
            if (string.IsNullOrEmpty(value))
            {
                value = R2LocalCatalog.Setting(configKey.Replace('-', '_'));
            }
            if (string.IsNullOrEmpty(value))
            {
                return R2Route.Catalog;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "catalog":
                case "local":
                    return R2Route.Catalog;
                case "cloudflare":
                    return R2Route.CloudFlare;
                default:
                    throw new InvalidOperationException(
                        $"R2Routing: the {what} route '{value}' ({envName}) is not a route. " +
                        "Accepted values: 'catalog' (the local rclone mirror; 'local' is the " +
                        "same thing) and 'cloudflare' (direct, no local referencing).");
            }
        }

        public override string ToString() => $"metadata={Metadata}, data={Data}, read={Read}";
    }
}