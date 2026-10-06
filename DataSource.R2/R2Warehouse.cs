/*
 * R2Warehouse — the warehouse's naming, unit and time conventions in one place.
 *
 * The catalog holds TWENTY tables, one per (asset class, tick kind, resolution) — the scheme
 * Fino's Catalog.jl declares and the loader fills:
 *
 *     {equity,option}_{trade,quote}_tick                             4 tick tables
 *     {equity,option}_{trade,quote}_bar_{second,minute,hour,day}    16 bar tables
 *
 * Both handlers (R2IcebergDataFeed and R2IcebergHistoryProvider) route through THIS file, so a
 * subscription's (SecurityType, TickType, Resolution) is translated to a table name in exactly one
 * place. Adding a resolution or an asset class is a change here, not in every reader.
 *
 * Conventions that are easy to get wrong, all enforced here:
 *
 * - PRICES ARE INTEGER BASIS POINTS (1/10000 dollar) in every tick and bar table. The unit is
 *   stripped exactly once, at the boundary where a Lean object is built (FromBasisPoints).
 * - TIME IS UTC NANOSECONDS on ticks (`sip_timestamp`) and on bars (`ts`). Lean wants exchange
 *   time on BaseData.Time, so the conversion happens at that same boundary (ToExchangeTime).
 * - A BAR's `ts` is the START of its bucket: the 09:30 minute bar carries 09:30:00.000000000.
 *   Session interpretation stays in the consumer; nothing here re-buckets anything.
 * - Bars are SPARSE: a bucket that held no ticks has no row. An empty read is a legitimate
 *   result, not an error, and this class never dresses it up as one.
 *
 * The option ticker mapping is the other half of the routing: Lean's option Symbol.Value is the OSI
 * string (6-char space-padded root + yymmdd + C|P + strike*1000), while the warehouse stores the
 * vendor form (`O:` + root unpadded + yymmdd + C|P + strike*1000). VendorOptionTicker is the single
 * conversion, verified against the loaded contracts (e.g. CRWD 2026-10-02 262.5P ->
 * O:CRWD261002P00262500).
 *
 * A twenty-first table breaks the (tick kind, resolution) scheme: `features` holds the derived IV
 * conversion, one row per (symbol, ET second) per flavour, at ONE native granularity. A
 * VolatilityQuoteBar/VolatilityTradeBar subscription therefore routes by DATA TYPE rather than by
 * tick kind and resolution (TryResolveIvTable), and its rows are consolidated to the requested
 * resolution instead of being read as bars (R2IvReader, bucket intervals below).
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using QuantConnect;
using QuantConnect.Data.Market;   // VolatilityQuoteBar / VolatilityTradeBar live here

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>The asset class axis of the warehouse naming scheme.</summary>
    public enum R2Asset
    {
        Equity,
        Option
    }

    /// <summary>
    /// One warehouse table, plus the subscription shape it serves.
    /// </summary>
    public sealed class R2Table
    {
        public R2Table(string name, R2Asset asset, TickType tickType, Resolution resolution)
        {
            Name = name;
            Asset = asset;
            TickType = tickType;
            Resolution = resolution;
        }

        /// <summary>Warehouse table name, e.g. <c>option_quote_bar_minute</c>.</summary>
        public string Name { get; }

        public R2Asset Asset { get; }

        public TickType TickType { get; }

        /// <summary><see cref="Resolution.Tick"/> for a tick table, otherwise the bar resolution.</summary>
        public Resolution Resolution { get; }

        /// <summary>False for the four tick tables, true for the sixteen bar tables.</summary>
        public bool IsBar => Resolution != Resolution.Tick;

        public bool IsQuote => TickType == TickType.Quote;

        public override string ToString() =>
            $"{Name} ({Asset.ToString().ToLowerInvariant()}, {TickType}, {Resolution})";
    }

    /// <summary>
    /// The features table, seen as the source of one volatility flavour. Not one of the twenty: it
    /// is neither a tick nor a pre-aggregated bar table, so its rows are consolidated to the
    /// subscription's resolution by R2IvReader rather than read as bars off the shelf.
    /// </summary>
    public sealed class R2IvTable
    {
        public R2IvTable(string name, bool isQuote)
        {
            Name = name;
            IsQuote = isQuote;
        }

        /// <summary>Table name, <c>features</c> today.</summary>
        public string Name { get; }

        /// <summary>True for a VolatilityQuoteBar (iv_bid/iv_ask), false for a VolatilityTradeBar (iv_trade).</summary>
        public bool IsQuote { get; }

        public string QuoteOrTrade => IsQuote ? "quote" : "trade";

        public override string ToString() => $"{Name} ({QuoteOrTrade} IV)";
    }

    /// <summary>
    /// Table naming, unit conversion and time conversion for the R2 warehouse.
    /// </summary>
    public static class R2Warehouse
    {
        private const string BarPrefix = "bar";

        /// <summary>Every table the scheme defines, in warehouse order — used for diagnostics.</summary>
        public static readonly IReadOnlyList<string> AllTables = BuildAllTables();

        /// <summary>
        /// The table serving a subscription shape. False with a message naming the reason and the
        /// full known set — an unmapped subscription must fail loudly, never return silence.
        /// </summary>
        public static bool TryResolveTable(
            SecurityType securityType,
            TickType tickType,
            Resolution resolution,
            out R2Table table,
            out string error)
        {
            table = null;
            error = null;

            R2Asset asset;
            switch (securityType)
            {
                case SecurityType.Equity:
                    asset = R2Asset.Equity;
                    break;
                case SecurityType.Option:
                    asset = R2Asset.Option;
                    break;
                default:
                    error = $"security type {securityType} has no warehouse table " +
                            "(the scheme covers equity and option only)";
                    return false;
            }

            if (tickType == TickType.OpenInterest)
            {
                // Not an error the callers should ever surface: both handlers serve a zero series
                // for OI before they route. Reaching here means a new caller skipped that branch.
                error = "open interest has no warehouse table (see R2OpenInterest: callers must serve " +
                        "zeros before routing)";
                return false;
            }

            if (tickType != TickType.Trade && tickType != TickType.Quote)
            {
                error = $"tick type {tickType} has no warehouse table (trade and quote only)";
                return false;
            }

            string name;
            if (resolution == Resolution.Tick)
            {
                name = TickTable(asset, tickType);
            }
            else
            {
                string suffix;
                switch (resolution)
                {
                    case Resolution.Second: suffix = "second"; break;
                    case Resolution.Minute: suffix = "minute"; break;
                    case Resolution.Hour: suffix = "hour"; break;
                    case Resolution.Daily: suffix = "day"; break;
                    default:
                        error = $"resolution {resolution} has no warehouse bar table " +
                                "(tick, second, minute, hour and daily only)";
                        return false;
                }
                name = BarTable(asset, tickType, suffix);
            }

            table = new R2Table(name, asset, tickType, resolution);
            return true;
        }

        /// <summary>(asset, tick kind) -> tick table, e.g. (equity, trade) -> equity_trade_tick.</summary>
        public static string TickTable(R2Asset asset, TickType tickType) =>
            $"{AssetName(asset)}_{KindName(tickType)}_tick";

        /// <summary>(asset, tick kind, suffix) -> bar table, e.g. (option, quote, minute).</summary>
        public static string BarTable(R2Asset asset, TickType tickType, string suffix) =>
            $"{AssetName(asset)}_{KindName(tickType)}_{BarPrefix}_{suffix}";

        public static string AssetName(R2Asset asset) =>
            asset == R2Asset.Equity ? "equity" : "option";

        public static string KindName(TickType tickType) =>
            tickType == TickType.Quote ? "quote" : "trade";

        private static IReadOnlyList<string> BuildAllTables()
        {
            var names = new List<string>();
            foreach (var asset in new[] { R2Asset.Equity, R2Asset.Option })
            {
                foreach (var kind in new[] { TickType.Trade, TickType.Quote })
                {
                    names.Add(TickTable(asset, kind));
                    foreach (var suffix in new[] { "second", "minute", "hour", "day" })
                    {
                        names.Add(BarTable(asset, kind, suffix));
                    }
                }
            }
            return names;
        }

        // ── volatility (IV) ──────────────────────────────────────────────────────

        /// <summary>
        /// The one derived feature table. Currently the IV conversion: one row per (symbol, ET
        /// second) per tick flavour, `iv_bid`/`iv_ask` from the second's first quote tick and
        /// `iv_trade` from its first trade tick. The IVs are decimals (0.2516 = 25.16%), NOT basis
        /// points, and only those three columns are read — the table's price columns are not part
        /// of the plugin's contract.
        /// </summary>
        public const string FeaturesTable = "features";

        /// <summary>
        /// Bucket interval for a resolution, spelled the way the warehouse's own resampler spells
        /// it (Fino's Resampler.BUCKET_SQL). Null for resolutions whose rows are taken as they are
        /// stored rather than consolidated.
        /// </summary>
        public static string BucketInterval(Resolution resolution)
        {
            switch (resolution)
            {
                case Resolution.Second: return "INTERVAL 1 SECOND";
                case Resolution.Minute: return "INTERVAL 1 MINUTE";
                case Resolution.Hour: return "INTERVAL 1 HOUR";
                case Resolution.Daily: return "INTERVAL 1 DAY";
                default: return null;
            }
        }

        /// <summary>
        /// The route for a volatility subscription: the features table, plus which of the two
        /// flavours (and therefore which column set) serves it.
        /// </summary>
        public static bool TryResolveIvTable(Type dataType, out R2IvTable table)
        {
            table = null;
            if (dataType == typeof(VolatilityQuoteBar))
            {
                table = new R2IvTable(FeaturesTable, isQuote: true);
                return true;
            }
            if (dataType == typeof(VolatilityTradeBar))
            {
                table = new R2IvTable(FeaturesTable, isQuote: false);
                return true;
            }
            return false;
        }

        /// <summary>
        /// The option contract a volatility subscription hangs off. Lean's feed side carries a
        /// SecurityType.Base symbol whose Underlying is the contract; a history request carries the
        /// contract itself. Both reduce to the contract here.
        /// </summary>
        public static Symbol IvUnderlying(Symbol symbol) => symbol?.Underlying ?? symbol;

        // ── units ────────────────────────────────────────────────────────────────

        /// <summary>Warehouse basis points (1/10000 dollar) -> dollars, the one place the unit dies.</summary>
        public static decimal FromBasisPoints(long basisPoints) => basisPoints / 10000m;

        /// <summary>Warehouse epoch nanoseconds -> exchange (ET) wall time.</summary>
        public static DateTime ToExchangeTime(long epochNs) =>
            DateTime.UnixEpoch.AddTicks(epochNs / 100L).ConvertFromUtc(TimeZones.NewYork);

        /// <summary>
        /// Half-open UTC-day bounds in epoch nanoseconds. The tick tables partition on
        /// day(sip_timestamp) and the bars on day(ts), so this is the pushdown predicate for both.
        /// </summary>
        public static void UtcDayBoundsNs(DateTime dayUtc, out long lo, out long hi)
        {
            lo = (dayUtc.Date - DateTime.UnixEpoch).Ticks * 100L;   // .NET ticks are 100 ns
            hi = lo + 86_400L * 1_000_000_000L;
        }

        /// <summary>
        /// Lean option Symbol -> the warehouse's vendor ticker. Null for non-option symbols.
        ///
        /// Lean: <c>CRWD  261002P00262500</c> (OSI, root space-padded to 6) ->
        /// warehouse: <c>O:CRWD261002P00262500</c>. The padding is only ever in the root, so
        /// removing spaces cannot touch the date, right or strike fields.
        /// </summary>
        public static string VendorOptionTicker(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option && symbol.SecurityType != SecurityType.IndexOption)
            {
                return null;
            }
            return "O:" + symbol.Value.Replace(" ", string.Empty);
        }

        /// <summary>Parses a warehouse integer column, tolerating the decimal form duckdb may print.</summary>
        public static bool TryLong(string text, out long value) =>
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
