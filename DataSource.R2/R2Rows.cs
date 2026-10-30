/*
 * R2Rows — the in-process representation of warehouse rows.
 *
 * The readers used to hold every row as string[]: 11 strings per row, ~40 bytes each once the
 * object header and UTF-16 payload are counted, so a chain-day of option_quote_bar_second
 * (7.4M rows) cost ~5 GB of managed heap. These structs hold the same values in the warehouse's
 * own small types — basis-point prices are int32 (int32 covers $214k), sizes/volume int32/int64,
 * timestamps int64 nanoseconds — which is 48 bytes per quote bar instead of ~550.
 *
 * Parsing happens ONCE at ingest, not per read: the batch cache stores typed rows, and the
 * readers consume fields instead of indexing strings and re-parsing decimals. Malformed rows are
 * rejected by the Try* parsers exactly where the string path used to skip them.
 */

using System;
using System.Globalization;

using Newtonsoft.Json.Linq;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>Trade bar: ts (ns), OHLC basis points, volume.</summary>
    public readonly struct TradeBarRow
    {
        public readonly long Ts;
        public readonly int Open;
        public readonly int High;
        public readonly int Low;
        public readonly int Close;
        public readonly long Volume;

        public TradeBarRow(long ts, int open, int high, int low, int close, long volume)
        {
            Ts = ts;
            Open = open;
            High = high;
            Low = low;
            Close = close;
            Volume = volume;
        }
    }

    /// <summary>Quote bar: ts (ns), bid/ask OHLC basis points and closing sizes.</summary>
    public readonly struct QuoteBarRow
    {
        public readonly long Ts;
        public readonly int BidOpen;
        public readonly int BidHigh;
        public readonly int BidLow;
        public readonly int BidClose;
        public readonly int BidSizeClose;
        public readonly int AskOpen;
        public readonly int AskHigh;
        public readonly int AskLow;
        public readonly int AskClose;
        public readonly int AskSizeClose;

        public QuoteBarRow(long ts, int bidOpen, int bidHigh, int bidLow, int bidClose, int bidSizeClose,
            int askOpen, int askHigh, int askLow, int askClose, int askSizeClose)
        {
            Ts = ts;
            BidOpen = bidOpen;
            BidHigh = bidHigh;
            BidLow = bidLow;
            BidClose = bidClose;
            BidSizeClose = bidSizeClose;
            AskOpen = askOpen;
            AskHigh = askHigh;
            AskLow = askLow;
            AskClose = askClose;
            AskSizeClose = askSizeClose;
        }
    }

    /// <summary>Trade tick: ts (ns), price basis points, size, exchange id.</summary>
    public readonly struct TradeTickRow
    {
        public readonly long Ts;
        public readonly int Price;
        public readonly int Size;
        public readonly int ExchangeId;

        public TradeTickRow(long ts, int price, int size, int exchangeId)
        {
            Ts = ts;
            Price = price;
            Size = size;
            ExchangeId = exchangeId;
        }
    }

    /// <summary>Quote tick: ts (ns), bid/ask price basis points, sizes and exchange ids.</summary>
    public readonly struct QuoteTickRow
    {
        public readonly long Ts;
        public readonly int BidPrice;
        public readonly int BidSize;
        public readonly int BidExchangeId;
        public readonly int AskPrice;
        public readonly int AskSize;
        public readonly int AskExchangeId;

        public QuoteTickRow(long ts, int bidPrice, int bidSize, int bidExchangeId,
            int askPrice, int askSize, int askExchangeId)
        {
            Ts = ts;
            BidPrice = bidPrice;
            BidSize = bidSize;
            BidExchangeId = bidExchangeId;
            AskPrice = askPrice;
            AskSize = askSize;
            AskExchangeId = askExchangeId;
        }
    }

    /// <summary>Parses one projected row (symbol column stripped) into its typed form.</summary>
    public delegate bool RowParser<TRow>(string[] row, out TRow value);

    /// <summary>
    /// Parses one statement-protocol row straight from the JSON page: row[0] is the symbol (the
    /// projection always selects it first in the batch path), rows[1..] are the typed columns.
    /// This is what avoids the 11 strings per row that ToRow used to allocate.
    /// </summary>
    public delegate bool SymbolRowParser<TRow>(JArray row, out string symbol, out TRow value);

    /// <summary>Column-order parsers for the projections the readers issue.</summary>
    public static class R2RowParser
    {
        // TradeColumns = "epoch_ns(ts), open, high, low, close, volume"
        public static bool TryTradeBar(string[] row, out TradeBarRow value)
        {
            value = default;
            if (row.Length < 6 ||
                !long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ||
                !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var open) ||
                !int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var high) ||
                !int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var low) ||
                !int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var close) ||
                !long.TryParse(row[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume))
            {
                return false;
            }

            value = new TradeBarRow(ts, open, high, low, close, volume);
            return true;
        }

        // QuoteColumns = "epoch_ns(ts), bid_open, bid_high, bid_low, bid_close, bid_size_close,
        //                  ask_open, ask_high, ask_low, ask_close, ask_size_close"
        public static bool TryQuoteBar(string[] row, out QuoteBarRow value)
        {
            value = default;
            if (row.Length < 11 ||
                !long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ||
                !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bo) ||
                !int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bh) ||
                !int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bl) ||
                !int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bc) ||
                !int.TryParse(row[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bs) ||
                !int.TryParse(row[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ao) ||
                !int.TryParse(row[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ah) ||
                !int.TryParse(row[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out var al) ||
                !int.TryParse(row[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ac) ||
                !int.TryParse(row[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out var asz))
            {
                return false;
            }

            value = new QuoteBarRow(ts, bo, bh, bl, bc, bs, ao, ah, al, ac, asz);
            return true;
        }

        // TradeColumns = "epoch_ns(sip_timestamp), price, size, exchange_id"
        public static bool TryTradeTick(string[] row, out TradeTickRow value)
        {
            value = default;
            if (row.Length < 4 ||
                !long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ||
                !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var price) ||
                !int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ||
                !int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var exchange))
            {
                return false;
            }

            value = new TradeTickRow(ts, price, size, exchange);
            return true;
        }

        // QuoteColumns = "epoch_ns(sip_timestamp), bid_price, bid_size, bid_exchange_id,
        //                  ask_price, ask_size, ask_exchange_id"
        public static bool TryQuoteTick(string[] row, out QuoteTickRow value)
        {
            value = default;
            if (row.Length < 7 ||
                !long.TryParse(row[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts) ||
                !int.TryParse(row[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ||
                !int.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bs) ||
                !int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bx) ||
                !int.TryParse(row[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ap) ||
                !int.TryParse(row[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var az) ||
                !int.TryParse(row[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ax))
            {
                return false;
            }

            value = new QuoteTickRow(ts, bp, bs, bx, ap, az, ax);
            return true;
        }

        // ---- direct-from-JSON parsers (batch path) --------------------------------------------
        // The statement protocol delivers numbers as JSON integers, so no string is materialised:
        // JValue already holds a long/int and Value<T>() is a boxed read. Strings are accepted as a
        // fallback (some Trino types render as strings) so a projection change cannot silently zero
        // a column.

        public static bool TryTradeBarJson(JArray row, out string symbol, out TradeBarRow value)
        {
            symbol = null;
            value = default;
            if (row.Count < 7)
            {
                return false;
            }
            symbol = row[0].Value<string>();
            if (string.IsNullOrEmpty(symbol) ||
                !TryLong(row[1], out var ts) ||
                !TryInt(row[2], out var open) ||
                !TryInt(row[3], out var high) ||
                !TryInt(row[4], out var low) ||
                !TryInt(row[5], out var close) ||
                !TryLong(row[6], out var volume))
            {
                return false;
            }

            value = new TradeBarRow(ts, open, high, low, close, volume);
            return true;
        }

        public static bool TryQuoteBarJson(JArray row, out string symbol, out QuoteBarRow value)
        {
            symbol = null;
            value = default;
            if (row.Count < 12)
            {
                return false;
            }
            symbol = row[0].Value<string>();
            if (string.IsNullOrEmpty(symbol) ||
                !TryLong(row[1], out var ts) ||
                !TryInt(row[2], out var bo) ||
                !TryInt(row[3], out var bh) ||
                !TryInt(row[4], out var bl) ||
                !TryInt(row[5], out var bc) ||
                !TryInt(row[6], out var bs) ||
                !TryInt(row[7], out var ao) ||
                !TryInt(row[8], out var ah) ||
                !TryInt(row[9], out var al) ||
                !TryInt(row[10], out var ac) ||
                !TryInt(row[11], out var asz))
            {
                return false;
            }

            value = new QuoteBarRow(ts, bo, bh, bl, bc, bs, ao, ah, al, ac, asz);
            return true;
        }

        public static bool TryTradeTickJson(JArray row, out string symbol, out TradeTickRow value)
        {
            symbol = null;
            value = default;
            if (row.Count < 5)
            {
                return false;
            }
            symbol = row[0].Value<string>();
            if (string.IsNullOrEmpty(symbol) ||
                !TryLong(row[1], out var ts) ||
                !TryInt(row[2], out var price) ||
                !TryInt(row[3], out var size) ||
                !TryInt(row[4], out var exchange))
            {
                return false;
            }

            value = new TradeTickRow(ts, price, size, exchange);
            return true;
        }

        public static bool TryQuoteTickJson(JArray row, out string symbol, out QuoteTickRow value)
        {
            symbol = null;
            value = default;
            if (row.Count < 8)
            {
                return false;
            }
            symbol = row[0].Value<string>();
            if (string.IsNullOrEmpty(symbol) ||
                !TryLong(row[1], out var ts) ||
                !TryInt(row[2], out var bp) ||
                !TryInt(row[3], out var bs) ||
                !TryInt(row[4], out var bx) ||
                !TryInt(row[5], out var ap) ||
                !TryInt(row[6], out var az) ||
                !TryInt(row[7], out var ax))
            {
                return false;
            }

            value = new QuoteTickRow(ts, bp, bs, bx, ap, az, ax);
            return true;
        }

        /// <summary>Materialises the cells after <paramref name="offset"/> as a string[] (legacy JArray path).</summary>
        public static string[] ToStringRow(JArray row, int offset)
        {
            var cells = new string[row.Count - offset];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = TokenToString(row[offset + i]);
            }
            return cells;
        }

        private static string TokenToString(JToken cell)
        {
            switch (cell.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return string.Empty;
                case JTokenType.String:
                    return cell.Value<string>();
                case JTokenType.Boolean:
                    return cell.Value<bool>() ? "true" : "false";
                case JTokenType.Integer:
                case JTokenType.Float:
                    return Convert.ToString(((JValue)cell).Value, CultureInfo.InvariantCulture) ?? string.Empty;
                default:
                    return cell.ToString(Newtonsoft.Json.Formatting.None);
            }
        }

        private static bool TryLong(JToken token, out long value)
        {
            value = 0;
            switch (token?.Type)
            {
                case null:
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return false;
                case JTokenType.Integer:
                    value = token.Value<long>();
                    return true;
                case JTokenType.Float:
                    value = (long)token.Value<double>();
                    return true;
                default:
                    return long.TryParse(token.Value<string>(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value);
            }
        }

        private static bool TryInt(JToken token, out int value)
        {
            value = 0;
            switch (token?.Type)
            {
                case null:
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return false;
                case JTokenType.Integer:
                    var wide = token.Value<long>();
                    if (wide < int.MinValue || wide > int.MaxValue)
                    {
                        return false;
                    }
                    value = (int)wide;
                    return true;
                case JTokenType.Float:
                    value = (int)token.Value<double>();
                    return true;
                default:
                    return int.TryParse(token.Value<string>(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value);
            }
        }
    }

    /// <summary>
    /// One decoded statement-protocol cell. A reused buffer of these replaces both the earlier
    /// string[] row and Newtonsoft's per-cell JValue objects — the latter was ~89M short-lived
    /// objects for a 7.4M-row chain-day (12 cells each) and the reason peak RSS stayed at GBs while
    /// the retained rows were a few hundred MB.
    /// </summary>
    public struct JsonCell
    {
        public const byte Null = 0;
        public const byte Integer = 1;
        public const byte Float = 2;
        public const byte String = 3;
        public const byte Boolean = 4;

        public byte Kind;
        public long Long;
        public double Double;
        public string Text;
        public bool Bool;
    }

    /// <summary>Parses one row of decoded cells: cells[0] is the symbol, the projection follows.</summary>
    public delegate bool CellRowParser<TRow>(JsonCell[] cells, int count, out string symbol, out TRow value);

    /// <summary>Column-order parsers over <see cref="JsonCell"/> rows.</summary>
    public static class R2CellParser
    {
        public static bool TryTradeBar(JsonCell[] cells, int count, out string symbol, out TradeBarRow value)
        {
            symbol = Symbol(cells, count);
            value = default;
            if (symbol == null ||
                !TryLong(cells[1], out var ts) ||
                !TryInt(cells[2], out var open) ||
                !TryInt(cells[3], out var high) ||
                !TryInt(cells[4], out var low) ||
                !TryInt(cells[5], out var close) ||
                !TryLong(cells[6], out var volume))
            {
                return false;
            }

            value = new TradeBarRow(ts, open, high, low, close, volume);
            return true;
        }

        public static bool TryQuoteBar(JsonCell[] cells, int count, out string symbol, out QuoteBarRow value)
        {
            symbol = Symbol(cells, count);
            value = default;
            if (symbol == null || count < 12 ||
                !TryLong(cells[1], out var ts) ||
                !TryInt(cells[2], out var bo) ||
                !TryInt(cells[3], out var bh) ||
                !TryInt(cells[4], out var bl) ||
                !TryInt(cells[5], out var bc) ||
                !TryInt(cells[6], out var bs) ||
                !TryInt(cells[7], out var ao) ||
                !TryInt(cells[8], out var ah) ||
                !TryInt(cells[9], out var al) ||
                !TryInt(cells[10], out var ac) ||
                !TryInt(cells[11], out var asz))
            {
                return false;
            }

            value = new QuoteBarRow(ts, bo, bh, bl, bc, bs, ao, ah, al, ac, asz);
            return true;
        }

        public static bool TryTradeTick(JsonCell[] cells, int count, out string symbol, out TradeTickRow value)
        {
            symbol = Symbol(cells, count);
            value = default;
            if (symbol == null || count < 5 ||
                !TryLong(cells[1], out var ts) ||
                !TryInt(cells[2], out var price) ||
                !TryInt(cells[3], out var size) ||
                !TryInt(cells[4], out var exchange))
            {
                return false;
            }

            value = new TradeTickRow(ts, price, size, exchange);
            return true;
        }

        public static bool TryQuoteTick(JsonCell[] cells, int count, out string symbol, out QuoteTickRow value)
        {
            symbol = Symbol(cells, count);
            value = default;
            if (symbol == null || count < 8 ||
                !TryLong(cells[1], out var ts) ||
                !TryInt(cells[2], out var bp) ||
                !TryInt(cells[3], out var bs) ||
                !TryInt(cells[4], out var bx) ||
                !TryInt(cells[5], out var ap) ||
                !TryInt(cells[6], out var az) ||
                !TryInt(cells[7], out var ax))
            {
                return false;
            }

            value = new QuoteTickRow(ts, bp, bs, bx, ap, az, ax);
            return true;
        }

        /// <summary>Cell -> invariant text, for callers that still need string rows (IV reader).</summary>
        public static string Text(in JsonCell cell)
        {
            switch (cell.Kind)
            {
                case JsonCell.Null:
                    return string.Empty;
                case JsonCell.String:
                    return cell.Text ?? string.Empty;
                case JsonCell.Boolean:
                    return cell.Bool ? "true" : "false";
                case JsonCell.Integer:
                    return cell.Long.ToString(CultureInfo.InvariantCulture);
                case JsonCell.Float:
                    return cell.Double.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return string.Empty;
            }
        }

        private static string Symbol(JsonCell[] cells, int count)
        {
            return count > 0 && cells[0].Kind == JsonCell.String && !string.IsNullOrEmpty(cells[0].Text)
                ? cells[0].Text
                : null;
        }

        private static bool TryLong(in JsonCell cell, out long value)
        {
            value = 0;
            switch (cell.Kind)
            {
                case JsonCell.Integer:
                    value = cell.Long;
                    return true;
                case JsonCell.Float:
                    value = (long)cell.Double;
                    return true;
                case JsonCell.String:
                    return long.TryParse(cell.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                default:
                    return false;
            }
        }

        private static bool TryInt(in JsonCell cell, out int value)
        {
            value = 0;
            switch (cell.Kind)
            {
                case JsonCell.Integer:
                    if (cell.Long < int.MinValue || cell.Long > int.MaxValue)
                    {
                        return false;
                    }
                    value = (int)cell.Long;
                    return true;
                case JsonCell.Float:
                    value = (int)cell.Double;
                    return true;
                case JsonCell.String:
                    return int.TryParse(cell.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
                default:
                    return false;
            }
        }
    }
}
