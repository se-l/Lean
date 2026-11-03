/*
 * R2SurfaceStore — reads the precomputed SSVI surfaces the Julia side writes.
 *
 * ONE ROW PER (event, recipe) in `r2.market.iv_surface_event`, keyed `(underlying, surface_id,
 * ts_end)`. A backtest asks for one recipe (`surface_id` from configuration) and one underlying, and
 * gets the session's rows in event order; the engine stamps each point with its AVAILABILITY time,
 * so the algorithm cannot see a surface before its fit finished.
 *
 * WHY A SEPARATE READER, not R2IvReader: the IV reader serves per-contract IV from `features`,
 * consolidated to a bar resolution and keyed by an option ticker. This table is keyed by
 * UNDERLYING and carries one wide JSON cell per event — 200 rows a session, no consolidation, no
 * ticker resolution. Sharing the reader would mean sharing none of its logic.
 *
 * The table is small and unpartitioned, so a day is one statement; there is no day-scoped symbol
 * index to consult and no `HoldsDay` check to make (that machinery exists for the tick tables).
 */

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using QuantConnect.Configuration;
using QuantConnect.Data;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads `market.iv_surface_event` and resolves which recipe a run wants.
    /// </summary>
    public static class R2SurfaceStore
    {
        /// <summary>The table the surfaces live in.</summary>
        public const string Table = "iv_surface_event";

        /// <summary>Configuration key naming the calibration recipe (e.g. `ivs_bc8da5d1d357`).</summary>
        public const string SurfaceIdKey = "surface-id";

        private static readonly Regex IdPattern = new("^ivs_[0-9a-f]{12}$", RegexOptions.Compiled);

        /// <summary>True when a subscription or history request is asking for a surface.</summary>
        public static bool IsRequested(Type dataType) => dataType == typeof(SurfaceSSVI);

        /// <summary>
        /// The configured recipe. Throws rather than guessing: a run that asks for surfaces without
        /// naming one would otherwise silently deliver a different product than the backtest it is
        /// meant to reproduce (the id IS the identity — config changes mint a new id).
        /// </summary>
        public static string ConfiguredSurfaceId()
        {
            var id = Config.Get(SurfaceIdKey, string.Empty);
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new InvalidOperationException(
                    $"R2SurfaceStore: a SurfaceSSVI subscription needs \"{SurfaceIdKey}\" in configuration " +
                    "(e.g. \"surface-id\": \"ivs_3869a150a87f\").");
            }
            id = id.Trim();
            if (!IdPattern.IsMatch(id))
            {
                throw new InvalidOperationException(
                    $"R2SurfaceStore: \"{SurfaceIdKey}\" = '{id}' is not a surface id (ivs_ + 12 hex).");
            }
            return id;
        }
    }

    /// <summary>
    /// Reads one session's surfaces for one underlying and recipe.
    /// </summary>
    public sealed class R2SurfaceReader
    {
        private readonly R2LocalCatalog _catalog;

        public R2SurfaceReader(R2LocalCatalog catalog)
        {
            _catalog = catalog;
        }

        /// <summary>
        /// The session's surface points, event order. A day with no rows is a legitimate answer
        /// (a holiday, or a session the loader has not covered) — it yields nothing.
        /// </summary>
        /// <param name="symbol">The underlying.</param>
        /// <param name="surfaceId">The recipe, already validated.</param>
        /// <param name="dayEt">The session date, New York.</param>
        public IEnumerable<BaseData> Read(Symbol symbol, string surfaceId, DateTime dayEt)
        {
            var lo = dayEt.Date;
            var hi = lo.AddDays(1);
            var sql =
                "SELECT ts_end, ts_available, spot, env_hash, cnt_quotes, surface, calibration_meta " +
                $"FROM {R2SurfaceStore.Table} " +
                $"WHERE underlying = '{symbol.Value}' AND surface_id = '{surfaceId}' " +
                $"AND ts_end >= TIMESTAMP '{lo:yyyy-MM-dd} 00:00:00' " +
                $"AND ts_end < TIMESTAMP '{hi:yyyy-MM-dd} 00:00:00' " +
                "AND surface IS NOT NULL ORDER BY ts_end";

            foreach (var row in _catalog.Rows(sql))
            {
                // A short row means a NULL cell the filter did not catch; skip it rather than
                // let a parse exception kill the session.
                if (row.Length < 6 || string.IsNullOrEmpty(row[5]))
                {
                    continue;
                }
                yield return SurfaceSSVI.FromRow(row, symbol, surfaceId);
            }
        }
    }
}
