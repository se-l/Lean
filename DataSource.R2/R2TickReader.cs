/*
 * Tick reader: ticks for one symbol/session out of the R2 warehouse, through R2LocalCatalog — one
 * statement per read, served by the Trino coordinator, which resolves the Iceberg manifests and the
 * Parquet data files itself (see R2LocalCatalog.cs).
 *
 * Tables are NOT chosen here. The caller passes the R2Table it resolved through R2Warehouse, so
 * this reader serves any of the four tick tables (equity/option x trade/quote) with one code path:
 * the column list follows the table's kind, and the option tables' extra `sequence_number` (BIGINT)
 * says nothing to Lean but is part of the total order.
 *
 * Rows are held as the typed structs in R2Rows.cs: a tick row is 24-40 bytes instead of ~300 for
 * string[], which matters because an option chain-day is millions of rows.
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

using QuantConnect;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Reads ticks through R2LocalCatalog's route and emits <see cref="Tick"/> objects in
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

        /// <param name="catalog">Catalog helper (scan source and the in-process batch caches).</param>
        public R2TickReader(R2LocalCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>
        /// The warehouse ticker for <paramref name="symbol"/>, or null when the table does not carry
        /// it. <paramref name="dayUtc"/> lets the catalog answer from its per-(table, day) symbol
        /// index — one query for the whole chain instead of one table scan per contract.
        /// </summary>
        public string ResolveSymbol(Symbol symbol, R2Table table, DateTime? dayUtc = null)
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
                case SecurityType.IndexOption:
                    // Warehouse form of Lean's OSI value, e.g. CRWD  261002P00262500 ->
                    // O:CRWD261002P00262500. Returned WITHOUT a registry/scan check: existence is
                    // answered by the (table, day, underlying) chain batch, which is cached and
                    // already read for the rest of the chain. The old per-contract
                    // `SELECT symbol ... LIMIT 1` re-scanned the table for every contract the day
                    // index missed — thousands of scans per warmup for contracts that simply have no
                    // rows that day (the chain registry carries more contracts than the bar tables).
                    return R2Warehouse.VendorOptionTicker(symbol);
                default:
                    return null;
            }

            return _catalog.ResolveSymbol(vendorTicker, table.Name, dayUtc);
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

            if (quotes)
            {
                foreach (var row in ReadQuotesTyped(table, ticker, dayUtc))
                {
                    // NOTE the argument order of this ctor: (saleCondition, exchange, bidSize,
                    // bidPrice, askSize, askPrice).
                    yield return new Tick(
                        R2Warehouse.ToExchangeTime(row.Ts), symbol, string.Empty, ExchangeName(row.BidExchangeId),
                        row.BidSize, R2Warehouse.FromBasisPoints(row.BidPrice),
                        row.AskSize, R2Warehouse.FromBasisPoints(row.AskPrice));
                }
                yield break;
            }

            foreach (var row in ReadTradesTyped(table, ticker, dayUtc))
            {
                // NOTE the argument order from Tick's ctor: quantity BEFORE price. Passing them
                // the other way round compiles (both are decimal) and silently transposes
                // volume and price.
                yield return new Tick(
                    R2Warehouse.ToExchangeTime(row.Ts), symbol, string.Empty, ExchangeName(row.ExchangeId),
                    row.Size, R2Warehouse.FromBasisPoints(row.Price));
            }
        }

        /// <summary>
        /// One day of a tick table, typed. Chain batch first (one contract's question loads the whole
        /// underlying's day, every other contract is a memory lookup); per-contract cache on the
        /// fallback path. `sequence_number` stays in the ORDER BY without being selected.
        /// </summary>
        private IEnumerable<TradeTickRow> ReadTradesTyped(R2Table table, string ticker, DateTime dayUtc)
        {
            return RowsTyped<TradeTickRow>(table, ticker, dayUtc, TradeColumns, R2RowParser.TryTradeTick,
                R2CellParser.TryTradeTick, "trade");
        }

        private IEnumerable<QuoteTickRow> ReadQuotesTyped(R2Table table, string ticker, DateTime dayUtc)
        {
            return RowsTyped<QuoteTickRow>(table, ticker, dayUtc, QuoteColumns, R2RowParser.TryQuoteTick,
                R2CellParser.TryQuoteTick, "quote");
        }

        private IEnumerable<TRow> RowsTyped<TRow>(R2Table table, string ticker, DateTime dayUtc, string columns,
            RowParser<TRow> parse, CellRowParser<TRow> cellParse, string variant)
        {
            var batch = _catalog.RowsByUnderlying(table.Name, dayUtc, ticker, columns, parse,
                orderBy: "2, sequence_number", cellParse: cellParse);
            if (batch.HasValue)
            {
                var slice = batch.Value.Rows(ticker);
                return slice.Count == 0 ? (IEnumerable<TRow>)Array.Empty<TRow>() : slice;
            }

            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            var source = _catalog.DaySource(table.Name, dayUtc);
            // The day predicate is a half-open range on sip_timestamp itself (not a cast of it), so
            // Iceberg manifest stats and Parquet row-group stats can both prune with it.
            var sql =
                _catalog.ScanPreamble() +
                $"SELECT {columns} FROM {source} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' " +
                $"AND sip_timestamp >= make_timestamp_ns({lo}) AND sip_timestamp < make_timestamp_ns({hi}) " +
                "ORDER BY sip_timestamp, sequence_number;";
            return _catalog.RowsForSymbol(table.Name, dayUtc, ticker, variant, () => _catalog.Rows(sql), parse,
                out _);
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
        private static string ExchangeName(int exchangeId)
        {
            switch (exchangeId)
            {
                case 4: return "XNAS";
                case 1: return "XNYS";
                case 8: return "ARCX";
                default: return "NASD";
            }
        }
    }
}
