/*
 * R2BarReader — pre-aggregated bars for one symbol/session straight out of the local rclone mount
 * of the R2 warehouse (route 1: allow_moved_paths; see R2LocalCatalog).
 *
 * Sibling of R2TickReader and deliberately the same shape: Iceberg metadata first, then the Parquet
 * data files, day-bounded predicate on the time column so manifest and row-group stats can prune.
 * The only differences are the table set (sixteen bar tables instead of four tick tables), the
 * column list, and the fact that a bar row is already a bar — nothing here consolidates, buckets or
 * re-samples. The WAREHOUSE produced these bars from the ticks; this reader's whole job is to hand
 * them to Lean with the right units.
 *
 * Units and time: prices arrive as integer basis points and `ts` as UTC nanoseconds with the bucket
 * START as its value. Both conversions live in R2Warehouse and happen exactly here.
 *
 * Sparse by construction: a bucket that held no ticks emits no row, and a session the warehouse does
 * not carry for that symbol emits nothing at all. Neither is an error and neither is logged as one.
 * Retry/fill policy upstream of this reader (which is where "should this session have bars?" is
 * answerable) is out of scope by design.
 *
 * What the reader does NOT do, on purpose:
 * - It does not filter by exchange hours. A bar's own bucket semantics (extended hours or not) are
 *   the producer's declared decision, recorded when the bars were built, and re-deciding it here
 *   would silently contradict the stored data.
 * - It does not fabricate a period. The bar period comes from the table's resolution, so a minute
 *   bar is a minute bar and a daily bar is a day.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

using QuantConnect;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads pre-aggregated trade/quote bars through the local rclone mirror and emits
    /// <see cref="TradeBar"/>/<see cref="QuoteBar"/> objects in exchange time.
    /// </summary>
    public sealed class R2BarReader
    {
        // Column lists mirror Fino's Catalog.bar_columns: trade bars carry OHLC + Σ size, quote bars
        // carry the bid and ask OHLC plus the CLOSING quote's sizes (Lean's LastBidSize/LastAskSize).
        private const string TradeColumns = "epoch_ns(ts), open, high, low, close, volume";
        private const string QuoteColumns =
            "epoch_ns(ts), bid_open, bid_high, bid_low, bid_close, bid_size, " +
            "ask_open, ask_high, ask_low, ask_close, ask_size";

        private readonly R2LocalCatalog _catalog;

        /// <param name="catalog">Route-1 catalog helper (mount root, table roots, duckdb plumbing).</param>
        public R2BarReader(R2LocalCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>Trade bars for one symbol and one UTC day, ordered by bucket start.</summary>
        public IEnumerable<TradeBar> ReadTradeBars(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireBar(table, TickType.Trade);
            var period = table.Resolution.ToTimeSpan();

            foreach (var row in Rows(table, ticker, dayUtc, TradeColumns))
            {
                if (row.Length < 6 || !R2Warehouse.TryLong(row[0], out var ns))
                {
                    continue;
                }

                yield return new TradeBar(
                    R2Warehouse.ToExchangeTime(ns),
                    symbol,
                    R2Warehouse.FromBasisPoints(long.Parse(row[1], CultureInfo.InvariantCulture)),
                    R2Warehouse.FromBasisPoints(long.Parse(row[2], CultureInfo.InvariantCulture)),
                    R2Warehouse.FromBasisPoints(long.Parse(row[3], CultureInfo.InvariantCulture)),
                    R2Warehouse.FromBasisPoints(long.Parse(row[4], CultureInfo.InvariantCulture)),
                    decimal.Parse(row[5], CultureInfo.InvariantCulture),
                    period);
            }
        }

        /// <summary>Quote bars for one symbol and one UTC day, ordered by bucket start.</summary>
        public IEnumerable<QuoteBar> ReadQuoteBars(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireBar(table, TickType.Quote);
            var period = table.Resolution.ToTimeSpan();

            foreach (var row in Rows(table, ticker, dayUtc, QuoteColumns))
            {
                if (row.Length < 11 || !R2Warehouse.TryLong(row[0], out var ns))
                {
                    continue;
                }

                // NOTE the QuoteBar ctor: (time, symbol, bidBar, lastBidSize, askBar, lastAskSize,
                // period). Sizes come from the closing quote of the bucket, not a sum over it.
                yield return new QuoteBar(
                    R2Warehouse.ToExchangeTime(ns),
                    symbol,
                    new Bar(
                        R2Warehouse.FromBasisPoints(long.Parse(row[1], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[2], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[3], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[4], CultureInfo.InvariantCulture))),
                    decimal.Parse(row[5], CultureInfo.InvariantCulture),
                    new Bar(
                        R2Warehouse.FromBasisPoints(long.Parse(row[6], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[7], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[8], CultureInfo.InvariantCulture)),
                        R2Warehouse.FromBasisPoints(long.Parse(row[9], CultureInfo.InvariantCulture))),
                    decimal.Parse(row[10], CultureInfo.InvariantCulture),
                    period);
            }
        }

        /// <summary>
        /// One day of a bar table. The day predicate sits on `ts` itself (not a cast of it) so
        /// Iceberg manifest stats and Parquet row-group stats can both prune with it — the same
        /// reason R2TickReader predicates on sip_timestamp.
        /// </summary>
        private IEnumerable<string[]> Rows(R2Table table, string ticker, DateTime dayUtc, string columns)
        {
            if (!_catalog.HoldsDay(table.Name, dayUtc) || !_catalog.HoldsSymbol(table.Name, dayUtc, ticker))
            {
                return Array.Empty<string[]>();
            }

            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            // The day, read once: a materialised day answers every later question about it from memory.
            var source = _catalog.DaySource(table.Name, dayUtc);

            var sql =
                _catalog.ScanPreamble() +
                $"SELECT {columns} FROM {source} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' " +
                $"AND ts >= make_timestamp_ns({lo}) AND ts < make_timestamp_ns({hi}) " +
                "ORDER BY ts;";

            return _catalog.Rows(sql);
        }

        private static void RequireBar(R2Table table, TickType expected)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (!table.IsBar)
            {
                throw new ArgumentException(
                    $"R2BarReader: {table.Name} is a tick table — read it with R2TickReader.", nameof(table));
            }
            if (table.TickType != expected)
            {
                throw new ArgumentException(
                    $"R2BarReader: {table.Name} is not a {expected} bar table.", nameof(table));
            }
        }
    }
}
