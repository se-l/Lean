/*
 * The second line of defence for the local mirror. When a mount-served Iceberg metadata file
 * 404s, the bucket is consistent and the reader's cached view is not: the resolved snapshot was
 * expired by a writer-side commit while the rclone mount (and the plugin's own root memo) still
 * served the old one. This class is ONLY the classifier, consulted on a failed statement — never
 * on the happy path; the recovery itself lives in R2LocalCatalog.RecoverFromStaleView, and the
 * rclone RC client it calls lives in R2RcloneRC.
 *
 * Two 404s must never be confused:
 *
 * - a 404 read THROUGH THE MOUNT (the failing SQL carries the local
 *   <mount-root>/__r2_data_catalog/<uuid>/metadata/ path) is the stale view above — the mount's
 *   VFS/dir cache outlived the writer's expiry, and only rclone can manufacture a 404 the bucket
 *   itself would not give;
 *
 * - a 404 on a DIRECT s3:// read has no cache in front of it, so it means a live snapshot
 *   references a file the bucket genuinely lacks — an integrity breach, alerted, never retried.
 *   Same for the REST catalog's own "returned a non-200 status code (NotFound_404)" wording:
 *   that is a missing table/namespace, a configuration fault.
 */

using System;
using System.Linq;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Decides whether a failed duckdb statement is a "stale catalog view" — a mount-served
    /// metadata file 404ing because the reader's cached view outlived a writer-side commit —
    /// as opposed to a real missing object or a configuration fault. Purely a predicate: the
    /// caller owns what to do about a match.
    /// </summary>
    internal static class R2StaleView
    {
        /// <summary>
        /// The shapes the failure takes on its way up. rclone surfaces the object-store NoSuchKey
        /// as a SerializationError naming the status code; through a FUSE mount duckdb may also
        /// see it as a plain ENOENT.
        /// </summary>
        private static readonly string[] Markers404 =
        {
            "status code: 404",
            "SerializationError",
            "NoSuchKey",
            "No such file",
            "ENOENT"
        };

        /// <summary>
        /// True when <paramref name="message"/> carries a 404-class failure AND
        /// <paramref name="sql"/> read through the rclone mount's catalog subtree. A direct
        /// s3:// read or a REST-catalog 404 never matches — those are real faults, not staleness.
        /// </summary>
        public static bool Matches(string sql, string message, string mountRoot)
        {
            if (string.IsNullOrEmpty(sql) || string.IsNullOrEmpty(message) || string.IsNullOrEmpty(mountRoot))
            {
                return false;
            }

            // The REST catalog's non-200 wording ("returned a non-200 status code (NotFound_404)")
            // is a table- or namespace-level answer, not an object-store one.
            if (message.Contains("non-200 status code", StringComparison.Ordinal))
            {
                return false;
            }

            if (!Markers404.Any(marker => message.Contains(marker, StringComparison.Ordinal)))
            {
                return false;
            }

            // The scan SQL on the catalog data route embeds the local root
            // (<mount-root>/__r2_data_catalog/<uuid>); the cloudflare data route embeds neither
            // the mount root nor the uuid directory. This is the mount-vs-direct discriminator:
            // a stale view is only possible where a cache sits in front of the bytes.
            return sql.Contains("__r2_data_catalog", StringComparison.Ordinal) &&
                   sql.Contains(mountRoot, StringComparison.Ordinal);
        }
    }
}
