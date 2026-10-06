/*
 * Route-1 tick reader: ticks for one symbol/session straight out of the local rclone mount of the
 * R2 warehouse — Iceberg metadata first, then the daily Parquet data files, all with
 * allow_moved_paths := true (see R2LocalCatalog for the recipe and rationale).
 *
 * Tables are NOT chosen here. The caller passes the R2Table it resolved through R2Warehouse, so
 * this reader serves any of the four tick tables (equity/option x trade/quote) with one code path:
 * the column list follows the table's kind, and the option tables' extra `sequence_number` (BIGINT)
 * says nothing to Lean but is part of the total order.
 *
 * There is NO cache and NO materialization step: the mount IS the "present locally, otherwise
 * fetch" layer. rclone serves cached bytes and transparently fetches anything missing, so a
 * backtest reuses whatever the mount already holds and only pays the network for genuinely
 * missing files.
 *
 * Session grain: the scan filter is the UTC day of sip_timestamp — the warehouse partitions on
 * day(sip_timestamp). For the sessions loaded so far this is exactly one ET session per UTC day
 * (sessions run 04:00-20:00 ET = 08:00-24:00Z in EDT), so an ET session date and its UTC day
 * coincide.
 *
 * The reader never throws for empty days: no rows is a legitimate result. Whether a symbol-day
 * SHOULD have rows is the loader's story, not this class's.
 *
 * Columns follow Fino's Catalog.SCHEMAS. Prices arrive in basis points (integer, 1/10000 dollar)
 * and are converted to dollars by R2Warehouse — the one place the unit is stripped on this side.
 * Times are emitted in EXCHANGE time (ET); the UTC-nanosecond conversion is also R2Warehouse's.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

using QuantConnect;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads ticks through the local rclone mirror and emits <see cref="Tick"/> objects in
    /// exchange time.
    /// </summary>
    public sealed class R2TickReader
    {
        // Equity trade/quote carry participant_timestamp and conditions; the option tables have no
        // participant_timestamp at all (the vendor's REST payload does not) and a BIGINT sequence
        // number. Only the fields Lean consumes are selected.
        private const string TradeColumns = "epoch_ns(sip_timestamp), price, size, exchange_id";
        private const string QuoteColumns =
            "epoch_ns(sip_timestamp), bid_price, bid_size, bid_exchange_id, ask_price, ask_size, ask_exchange_id";

        private readonly R2LocalCatalog _catalog;

        /// <param name="catalog">Route-1 catalog helper (mount root, table roots, duckdb plumbing).</param>
        public R2TickReader(R2LocalCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>
        /// The warehouse ticker for a Lean symbol, validated against the tick table that will serve
        /// it. Null when the warehouse does not carry the symbol (e.g. a benchmark that was never
        /// loaded). Throws on transport/catalog failure — a resolution failure must not read as
        /// "not loaded".
        /// </summary>
        /// <param name="symbol">Lean symbol.</param>
        /// <param name="table">Tick table the subscription will read, used for the lookup.</param>
        public string ResolveSymbol(Symbol symbol, R2Table table)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }

            string vendorTicker;
            switch (symbol.SecurityType)
            {
                case SecurityType.Equity:
                    // The ticker must match the warehouse `symbol` verbatim (vendor form, e.g. CRWD).
                    vendorTicker = symbol.Value.ToUpperInvariant();
                    break;
                case SecurityType.Option:
                    // Warehouse form of Lean's OSI value, e.g. CRWD  261002P00262500 ->
                    // O:CRWD261002P00262500. Contracts the warehouse never loaded resolve to null.
                    vendorTicker = R2Warehouse.VendorOptionTicker(symbol);
                    break;
                default:
                    return null;
            }

            return _catalog.ResolveSymbol(vendorTicker, table.Name);
        }

        /// <summary>Trade ticks for one symbol and one UTC day, ordered by (sip_timestamp, sequence_number).</summary>
        public IEnumerable<Tick> ReadTrades(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireTick(table, TickType.Trade);
            return Read(symbol, ticker, dayUtc, table, quotes: false);
        }

        /// <summary>Quote ticks for one symbol and one UTC day, ordered by (sip_timestamp, sequence_number).</summary>
        public IEnumerable<Tick> ReadQuotes(Symbol symbol, string ticker, R2Table table, DateTime dayUtc)
        {
            RequireTick(table, TickType.Quote);
            return Read(symbol, ticker, dayUtc, table, quotes: true);
        }

        private IEnumerable<Tick> Read(Symbol symbol, string ticker, DateTime dayUtc, R2Table table, bool quotes)
        {
            if (!_catalog.HoldsDay(table.Name, dayUtc) || !_catalog.HoldsSymbol(table.Name, dayUtc, ticker))
            {
                yield break;
            }

            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            // The day, read once: a materialised day answers every later question about it from memory.
            var source = _catalog.DaySource(table.Name, dayUtc);
            var columns = quotes ? QuoteColumns : TradeColumns;

            // The day predicate is a half-open range on sip_timestamp itself (not a cast of it), so
            // Iceberg manifest stats and Parquet row-group stats can both prune with it.
            var sql =
                _catalog.ScanPreamble() +
                $"SELECT {columns} FROM {source} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' " +
                $"AND sip_timestamp >= make_timestamp_ns({lo}) AND sip_timestamp < make_timestamp_ns({hi}) " +
                "ORDER BY sip_timestamp, sequence_number;";

            foreach (var row in _catalog.Rows(sql))
            {
                if (row.Length < (quotes ? 7 : 4))
                {
                    continue;
                }
                if (!R2Warehouse.TryLong(row[0], out var ns))
                {
                    continue;
                }

                var time = R2Warehouse.ToExchangeTime(ns);

                if (quotes)
                {
                    var bidSize = decimal.Parse(row[2], CultureInfo.InvariantCulture);
                    var bid = R2Warehouse.FromBasisPoints(long.Parse(row[1], CultureInfo.InvariantCulture));
                    var askSize = decimal.Parse(row[5], CultureInfo.InvariantCulture);
                    var ask = R2Warehouse.FromBasisPoints(long.Parse(row[4], CultureInfo.InvariantCulture));
                    var exchange = ExchangeName(row[3]);

                    // NOTE the argument order of this ctor: (saleCondition, exchange, bidSize,
                    // bidPrice, askSize, askPrice).
                    yield return new Tick(time, symbol, string.Empty, exchange, bidSize, bid, askSize, ask);
                }
                else
                {
                    var price = R2Warehouse.FromBasisPoints(long.Parse(row[1], CultureInfo.InvariantCulture));
                    var size = decimal.Parse(row[2], CultureInfo.InvariantCulture);
                    var exchange = ExchangeName(row[3]);

                    // NOTE the argument order from Tick's ctor: quantity BEFORE price. Passing them
                    // the other way round compiles (both are decimal) and silently transposes
                    // volume and price.
                    yield return new Tick(time, symbol, string.Empty, exchange, size, price);
                }
            }
        }

        private static void RequireTick(R2Table table, TickType expected)
        {
            if (table == null)
            {
                throw new ArgumentNullException(nameof(table));
            }
            if (table.IsBar)
            {
                throw new ArgumentException(
                    $"R2TickReader: {table.Name} is a bar table — read it with R2BarReader.", nameof(table));
            }
            if (table.TickType != expected)
            {
                throw new ArgumentException(
                    $"R2TickReader: {table.Name} is not a {expected} tick table.", nameof(table));
            }
        }

        /// <summary>
        /// Vendor exchange id -> Lean exchange string. PLACEHOLDER for the spike: the real table is a
        /// P1 item, and a wrong exchange string affects fill modelling, not correctness of the read.
        /// </summary>
        private static string ExchangeName(string exchangeId)
        {
            switch (exchangeId)
            {
                case "4": return "XNAS";
                case "1": return "XNYS";
                case "8": return "ARCX";
                default: return "NASD";
            }
        }
    }
}
