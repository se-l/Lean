/*
 * R2IvReader — the volatility data types, served out of the warehouse's `features` table.
 *
 * WHY THIS READER EXISTS AT ALL: the twenty tick/bar tables are grain-aligned with a subscription
 * (one table per tick kind and resolution), so their reader hands rows over untouched. `features`
 * is not: it holds the IV conversion at ONE native grain — one row per (symbol, ET second) per
 * flavour, iv_bid/iv_ask from that second's first quote tick, iv_trade from its first trade tick
 * (see Fino's Catalog.jl). A subscription can ask for any resolution, so this is the one place in
 * the plugin that CONSOLIDATES, and it consolidates in SQL, using the same recipe the loader uses
 * for its own bar tables (Fino's Resampler): bucket the ET wall clock, then hand back the bucket
 * start as a UTC epoch-ns instant. That is why the buckets line up with the warehouse's bars.
 *
 * Flavours are two disjoint row sets, selected rather than merged: a quote row carries
 * iv_bid/iv_ask, a trade row carries iv_trade. A subscription asks for exactly one of the two
 * types, so it sees exactly one flavour.
 *
 * Columns: ONLY the IV columns are read (iv_bid, iv_ask, iv_trade). The table's price columns are
 * not part of this reader's contract — every price bar on VolatilityQuoteBar
 * (PriceBid/PriceAsk/UnderlyingPrice) and VolatilityTradeBar's Price/UnderlyingMidPrice is ZERO,
 * never null: the fork's own initializer dereferences them unconditionally. The IVs are decimals
 * (0.2516 = 25.16%) and are passed through as they are, NOT scaled. A bucket with no observation on
 * a side is an all-zero bar too — his `== 0` check is the "empty row" sentinel, so nothing here is
 * ever null.
 *
 * OHLC: for a consolidated bucket, open/high/low/close come from the ordered aggregates the loader
 * uses for its own bars (arg_min/arg_max by ts for open/close, min/max for low/high). The trade
 * flavour's IV is a single decimal on that type, so it carries the bucket's last trade IV.
 *
 * Sparse by construction: a bucket with nothing to say emits no row, and a symbol the warehouse
 * does not carry emits nothing. Neither is an error.
 */

using System;
using System.Collections.Generic;
using System.Globalization;

using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Consolidates the warehouse's per-second IV features into
    /// <see cref="VolatilityQuoteBar"/>/<see cref="VolatilityTradeBar"/> objects at the resolution a
    /// subscription asks for, in exchange time.
    /// </summary>
    public sealed class R2IvReader
    {
        // One output row per bucket. A consolidated bucket aggregates; at tick resolution the row's
        // own value goes into every OHLC slot instead, so one parse path per flavour covers both.
        private const string QuoteColumns =
            "{0} AS bucket, " +
            "arg_min(iv_bid, ts) FILTER (WHERE iv_bid IS NOT NULL) AS bid_open, max(iv_bid) AS bid_high, " +
            "min(iv_bid) AS bid_low, arg_max(iv_bid, ts) FILTER (WHERE iv_bid IS NOT NULL) AS bid_close, " +
            "arg_min(iv_ask, ts) FILTER (WHERE iv_ask IS NOT NULL) AS ask_open, max(iv_ask) AS ask_high, " +
            "min(iv_ask) AS ask_low, arg_max(iv_ask, ts) FILTER (WHERE iv_ask IS NOT NULL) AS ask_close";

        private const string QuoteRowColumns =
            "{0} AS bucket, iv_bid AS bid_open, iv_bid AS bid_high, iv_bid AS bid_low, iv_bid AS bid_close, " +
            "iv_ask AS ask_open, iv_ask AS ask_high, iv_ask AS ask_low, iv_ask AS ask_close";

        private const string TradeColumns =
            "{0} AS bucket, arg_max(iv_trade, ts) FILTER (WHERE iv_trade IS NOT NULL) AS iv";

        private const string TradeRowColumns = "{0} AS bucket, iv_trade AS iv";

        // The loader's Resampler._bucket_expr, spelled out: bucket the ET wall clock, then return
        // the bucket start as the UTC instant ToExchangeTime expects.
        private const string BucketFromTs =
            "epoch_ns((time_bucket({0}, (ts::TIMESTAMP AT TIME ZONE 'UTC') AT TIME ZONE 'America/New_York') " +
            "AT TIME ZONE 'America/New_York') AT TIME ZONE 'UTC')";

        private readonly R2LocalCatalog _catalog;

        /// <param name="catalog">Route-1 catalog helper (mount root, table roots, duckdb plumbing).</param>
        public R2IvReader(R2LocalCatalog catalog)
        {
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        }

        /// <summary>
        /// One session of one volatility flavour, consolidated to <paramref name="resolution"/> and
        /// stamped in exchange time, ordered by bucket start.
        /// </summary>
        public IEnumerable<BaseData> Read(Symbol symbol, string ticker, R2IvTable table, Resolution resolution,
            DateTime dayUtc)
        {
            return table.IsQuote
                ? (IEnumerable<BaseData>)ReadQuote(symbol, ticker, table, resolution, dayUtc)
                : ReadTrade(symbol, ticker, table, resolution, dayUtc);
        }

        private IEnumerable<VolatilityQuoteBar> ReadQuote(Symbol symbol, string ticker, R2IvTable table,
            Resolution resolution, DateTime dayUtc)
        {
            var period = resolution.ToTimeSpan();
            var columns = BuildColumns(resolution, QuoteColumns, QuoteRowColumns);

            foreach (var row in Rows(table, ticker, dayUtc, resolution, columns, QuotePredicate))
            {
                if (row.Length < 9 || !R2Warehouse.TryLong(row[0], out var ns))
                {
                    continue;
                }

                var time = R2Warehouse.ToExchangeTime(ns);
                // The three price bars carry ZERO rather than null. The features table's price
                // columns are not read (being dropped upstream), and SecurityCache in this fork
                // dereferences PriceBid/PriceAsk unconditionally on a volatility bar — a null there
                // is an NRE in the engine, not a missing value. Zeros pass his `!= 0` guards, so the
                // security's bid/ask price simply stays untouched.
                yield return new VolatilityQuoteBar(
                    time,
                    symbol,
                    IvBar(row[1], row[2], row[3], row[4]),
                    IvBar(row[5], row[6], row[7], row[8]),
                    NoPriceBar(),
                    NoPriceBar(),
                    NoPriceBar(),
                    period);
            }
        }

        private IEnumerable<VolatilityTradeBar> ReadTrade(Symbol symbol, string ticker, R2IvTable table,
            Resolution resolution, DateTime dayUtc)
        {
            var period = resolution.ToTimeSpan();
            var columns = BuildColumns(resolution, TradeColumns, TradeRowColumns);

            foreach (var row in Rows(table, ticker, dayUtc, resolution, columns, TradePredicate))
            {
                if (row.Length < 2 || !R2Warehouse.TryLong(row[0], out var ns))
                {
                    continue;
                }

                if (!TryDecimal(row[1], out var iv))
                {
                    // No trade IV in this bucket: the warehouse has nothing to say here.
                    continue;
                }

                var time = R2Warehouse.ToExchangeTime(ns);

                // Price and UnderlyingMidPrice stay zero: the features table's price columns are
                // not read (they are being dropped upstream).
                var bar = new VolatilityTradeBar(time, symbol, 0m, 0m, iv) { Period = period };

                // VolatilityTradeBar declares `Time` and `Symbol` FIELDS, which shadow BaseData's
                // properties: his EndTime reads the fields, the engine's slicing reads the
                // properties. Both are set so they cannot disagree.
                ((BaseData)bar).Time = time;
                ((BaseData)bar).Symbol = symbol;

                // His type's constructor leaves DataType at its default; Slice routes volatility
                // bars on exactly this value, so it is set here.
                bar.DataType = MarketDataType.VolatilityBar;

                yield return bar;
            }
        }

        /// <summary>
        /// The bucket projection: the ET bucket start for a consolidated resolution, the tick's own
        /// stamp when rows are taken as they are (tick resolution has no bucket).
        /// </summary>
        private static string BuildColumns(Resolution resolution, string consolidated, string raw)
        {
            var interval = R2Warehouse.BucketInterval(resolution);
            return interval == null
                ? string.Format(CultureInfo.InvariantCulture, raw, "epoch_ns(ts)")
                : string.Format(CultureInfo.InvariantCulture, consolidated,
                    string.Format(CultureInfo.InvariantCulture, BucketFromTs, interval));
        }

        private const string QuotePredicate = "AND (iv_bid IS NOT NULL OR iv_ask IS NOT NULL)";
        private const string TradePredicate = "AND iv_trade IS NOT NULL";

        /// <summary>
        /// One day of the features table. The day predicate sits on `ts` itself so Iceberg manifest
        /// stats and Parquet row-group stats can prune with it, exactly as the other two readers do.
        /// </summary>
        private IEnumerable<string[]> Rows(R2IvTable table, string ticker, DateTime dayUtc, Resolution resolution,
            string columns, string flavourPredicate)
        {
            // Days the warehouse does not hold are answered from a local directory listing: no duckdb
            // process, no catalog, no network. Warmup lookbacks run over weeks while a freshly loaded
            // table holds a handful of days, so most of these calls have nothing to read.
            if (!_catalog.HoldsDay(table.Name, dayUtc) || !_catalog.HoldsSymbol(table.Name, dayUtc, ticker))
            {
                return Array.Empty<string[]>();
            }

            R2Warehouse.UtcDayBoundsNs(dayUtc, out var lo, out var hi);
            // The day, read once: a materialised day answers every later question about it from memory.
            var source = _catalog.DaySource(table.Name, dayUtc);

            // Tick resolution reads the rows as they are; every other resolution consolidates. In
            // both cases the projection is the same shape (see BuildColumns), so one parse path
            // per flavour serves both.
            var grouping = R2Warehouse.BucketInterval(resolution) == null ? string.Empty : "GROUP BY 1 ";

            var sql =
                _catalog.ScanPreamble() +
                $"SELECT {columns} FROM {source} " +
                $"WHERE symbol = '{ticker.Replace("'", "''")}' " +
                $"AND ts >= make_timestamp_ns({lo}) AND ts < make_timestamp_ns({hi}) " +
                $"{flavourPredicate} {grouping}ORDER BY 1;";

            return _catalog.Rows(sql);
        }

        /// <summary>
        /// IV OHLC -> a Bar. A bucket with no observation on that side is an all-zero Bar, never
        /// null: the fork's own initializer dereferences Bid/Ask/UnderlyingPrice/PriceBid
        /// unconditionally and uses `== 0` as its "empty row" sentinel (SecurityInitializerMine.cs),
        /// so a null here is a NullReferenceException inside his warmup rather than a missing value.
        /// </summary>
        private static Bar IvBar(string open, string high, string low, string close)
        {
            TryDecimal(open, out var o);
            TryDecimal(high, out var h);
            TryDecimal(low, out var l);
            TryDecimal(close, out var c);

            if (o == 0m && h == 0m && l == 0m && c == 0m)
            {
                // The warehouse floors a solved IV at 1e-4, so 0 here can only mean "no observation".
                return new Bar(0m, 0m, 0m, 0m);
            }

            if (o == 0m) o = c != 0m ? c : h;
            if (h == 0m) h = o;
            if (l == 0m) l = o;
            if (c == 0m) c = o;
            return new Bar(o, h, l, c);
        }

        /// <summary>An all-zero price bar: the features table's price columns are not read.</summary>
        private static Bar NoPriceBar() => new Bar(0m, 0m, 0m, 0m);

        /// <summary>Parses a duckdb CSV field; false for NULL/empty or anything unparseable.</summary>
        private static bool TryDecimal(string text, out decimal value) =>
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
