/*
 * R2BarReader — pre-aggregated bars for one symbol/session out of the R2 warehouse, through
 * R2LocalCatalog, which reads every table through the Trino coordinator (see R2LocalCatalog.cs).
 *
 * Sibling of R2TickReader and deliberately the same shape: Iceberg metadata first, then the Parquet
 * data files, day-bounded predicate on the time column so manifest and row-group stats can prune.
 * The only differences are the table set (sixteen bar tables instead of four tick tables), the
 * column list, and the fact that a bar row is already a bar — nothing here consolidates, buckets or
 * re-samples. The WAREHOUSE produced these bars from the ticks; this reader's whole job is to hand
 * them to Lean with the right units.
 *
 * Rows are held as the typed structs in R2Rows.cs, never as strings: a chain-day of
 * option_quote_bar_second is 7.4M rows, and string rows cost ~550 bytes each against 48 for the
 * struct. Parsing happens once while the batch cache is filled; a read is a field access.
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
using System.Diagnostics;

using QuantConnect;
using QuantConnect.Data.Market;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads pre-aggregated trade/quote bars through R2LocalCatalog's route and emits
    /// <see cref="TradeBar"/>/<see cref="QuoteBar"/> objects in exchange time.
    /// </summary>
    public sealed class R2BarReader
    {
        // Column lists mirror Fino's Catalog.bar_columns: trade bars carry OHLC + Σ size, quote bars
        // carry the bid and ask OHLC plus the CLOSING quote's sizes (Lean's LastBidSize/LastAskSize).
        // Schema 2026-10-19: closing sizes are `bid_size_close` / `ask_size_close` (was bid_size/ask_size).
        private const string TradeColumns = "epoch_ns(ts), open, high, low, close, volume";
        private const string QuoteColumns =
            "epoch_ns(ts), bid_open, bid_high, bid_low, bid_close, bid_size_close, " +
            "ask_open, ask_high, ask_low, ask_close, ask_size_close";

        private readonly R2LocalCatalog _catalog;

        /// <param name="catalog">Catalog helper (scan source and the in-process batch caches).</param>
        public R2BarReader(R2LocalCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>Trade bars for one symbol and one UTC day, ordered by bucket start.</summary>
        public IEnumerable<TradeBar> ReadTradeBars(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireBar(table, TickType.Trade);
            var period = table.Resolution.ToTimeSpan();

            foreach (var row in RowsTyped<TradeBarRow>(table, ticker, dayUtc, TradeColumns, R2RowParser.TryTradeBar,
                R2CellParser.TryTradeBar, "trade"))
            {
                yield return new TradeBar(
                    R2Warehouse.ToExchangeTime(row.Ts),
                    symbol,
                    R2Warehouse.FromBasisPoints(row.Open),
                    R2Warehouse.FromBasisPoints(row.High),
                    R2Warehouse.FromBasisPoints(row.Low),
                    R2Warehouse.FromBasisPoints(row.Close),
                    row.Volume,
                    period);
            }
        }

        /// <summary>Quote bars for one symbol and one UTC day, ordered by bucket start.</summary>
        public IEnumerable<QuoteBar> ReadQuoteBars(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireBar(table, TickType.Quote);
            var period = table.Resolution.ToTimeSpan();

            foreach (var row in RowsTyped<QuoteBarRow>(table, ticker, dayUtc, QuoteColumns, R2RowParser.TryQuoteBar,
                R2CellParser.TryQuoteBar, "quote"))
            {
                // NOTE the QuoteBar ctor: (time, symbol, bidBar, lastBidSize, askBar, lastAskSize,
                // period). Sizes come from the closing quote of the bucket, not a sum over it.
                yield return new QuoteBar(
                    R2Warehouse.ToExchangeTime(row.Ts),
                    symbol,
                    new Bar(
                        R2Warehouse.FromBasisPoints(row.BidOpen),
                        R2Warehouse.FromBasisPoints(row.BidHigh),
                        R2Warehouse.FromBasisPoints(row.BidLow),
                        R2Warehouse.FromBasisPoints(row.BidClose)),
                    row.BidSizeClose,
                    new Bar(
                        R2Warehouse.FromBasisPoints(row.AskOpen),
                        R2Warehouse.FromBasisPoints(row.AskHigh),
                        R2Warehouse.FromBasisPoints(row.AskLow),
                        R2Warehouse.FromBasisPoints(row.AskClose)),
                    row.AskSizeClose,
                    period);
            }
        }

        /// <summary>
        /// One day of a bar table, typed. The chain batch answers options (the first contract pays for
        /// the underlying's day, every other contract is a memory lookup); equities and oversize
        /// chain-days go through the per-contract cache, so a repeated question never scans twice.
        ///
        /// The day predicate sits on `ts` itself (not a cast of it) so Iceberg manifest stats and
        /// Parquet row-group stats can both prune with it.
        /// </summary>
        private IEnumerable<TRow> RowsTyped<TRow>(R2Table table, string ticker, DateTime dayUtc, string columns,
            RowParser<TRow> parse, CellRowParser<TRow> cellParse, string variant)
        {
            if (!_catalog.HoldsDay(table.Name, dayUtc) || !_catalog.HoldsSymbol(table.Name, dayUtc, ticker))
            {
                return Array.Empty<TRow>();
            }

            // One read serves the whole chain: the first contract's question loads the underlying's
            // day (all its contracts) into one flat array, every other contract is a slice lookup.
            // Null for equities and on a failed batch, which keeps the per-symbol path below.
            var batch = _catalog.RowsByUnderlying(table.Name, dayUtc, ticker, columns, parse, orderBy: "2",
                cellParse: cellParse);
            if (batch.HasValue)
            {
                var slice = batch.Value.Rows(ticker);
                return slice.Count == 0 ? (IEnumerable<TRow>)Array.Empty<TRow>() : slice;
            }

            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            var source = _catalog.DaySource(table.Name, dayUtc);
            var sql =
                _catalog.ScanPreamble() +
                $"SELECT {columns} FROM {source} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' " +
                $"AND ts >= make_timestamp_ns({lo}) AND ts < make_timestamp_ns({hi}) " +
                "ORDER BY ts;";

            // A slow read names its source: a materialised day (never on this transport) or a
            // coordinator scan. Without that split we could not tell why a cache change did nothing.
            // RowsForSymbol keeps the fallback answer in process as well: a repeated question about
            // the same contract-day (a second resolution, a warmup re-read) never scans twice.
            var readWatch = Stopwatch.StartNew();
            var materialised = _catalog.RowsForSymbol(table.Name, dayUtc, ticker, variant,
                () => _catalog.Rows(sql), parse, out var cached);
            var readMs = readWatch.ElapsedMilliseconds;
            if (readMs >= 50 && !cached)
            {
                Log.Trace($"R2LocalCatalog: read {table.Name} {ticker} {dayUtc:yyyy-MM-dd}: " +
                          $"{materialised.Count} rows in {readMs} ms " +
                          $"({(_catalog.DayIsMaterialised(table.Name, dayUtc) ? "temp table" : "trino scan")})");
            }
            return materialised;
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
