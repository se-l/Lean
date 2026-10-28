using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Warehouse access through an always-on Trino coordinator (HTTP), replacing the duckdb child
    /// process when <c>cloudflare-r2.engine=trino</c>.
    ///
    /// Speaks the Trino HTTP statement protocol (<c>POST /v1/statement</c>, follow <c>nextUri</c>)
    /// directly instead of using Trino.Ado: that client is built against System.Text.Json 8, and a
    /// net6.0 host (Lean) has already loaded the framework's System.Text.Json 6 into the default
    /// load context, so any assembly placed beside the plugin that requests 8.0.0.0 fails with
    /// FileLoad (0x80131621 / 0x80131040).
    ///
    /// Pages are decoded with <see cref="Utf8JsonReader"/> — a ref struct over the response bytes
    /// that allocates nothing per token. Newtonsoft's JObject/JValue tree was allocating ~12 JValue
    /// objects per row (~89M for a 7.4M-row chain-day, several GB of short-lived garbage); the
    /// reader code below allocates only one symbol string per row, and nothing for the numbers.
    ///
    /// Rows are handed to the caller as a REUSED <see cref="JsonCell"/> buffer: consume the row in
    /// the callback, never store the array.
    ///
    /// The readers were written against DuckDB and emit DuckDB-dialect SQL; the functions that do
    /// not exist in Trino are translated here, so one SQL builder serves both engines:
    /// <c>make_timestamp_ns(x)</c> -> <c>from_unixtime_nanos(x)</c>, <c>regexp_matches(a,b)</c> ->
    /// <c>regexp_like(a,b)</c>, <c>epoch_ns(x)</c> -> <c>round(to_unixtime(x)*1e9)</c>, and
    /// <c>arg_max/arg_min</c> -> <c>max_by/min_by</c>.
    ///
    /// One HttpClient per process, serialised by a lock: queries are issued from several reader
    /// threads.
    /// </summary>
    public sealed class TrinoSession : IDisposable
    {
        private static readonly Regex NanosFn = new Regex(@"make_timestamp_ns\(", RegexOptions.Compiled);
        private static readonly Regex RegexFn = new Regex(@"regexp_matches\(", RegexOptions.Compiled);
        private static readonly Regex EpochNsFn = new Regex(@"epoch_ns\(([^()]+)\)", RegexOptions.Compiled);
        private static readonly Regex ArgMaxFn = new Regex(@"\barg_max\(", RegexOptions.Compiled);
        private static readonly Regex ArgMinFn = new Regex(@"\barg_min\(", RegexOptions.Compiled);
        private static readonly Regex LongFraction = new Regex(
            @"^(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}\.\d{7})\d{2}$", RegexOptions.Compiled);

        private readonly object _gate = new object();
        private readonly HttpClient _http;
        private readonly string _statementUrl;
        private bool _disposed;
        private byte[] _page = new byte[2 << 20];

        /// <summary>Catalog the plugin reads (e.g. r2).</summary>
        public string Catalog { get; }

        /// <summary>Schema the plugin reads (e.g. market).</summary>
        public string Schema { get; }

        public TrinoSession(string url, string catalog, string schema, string user, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("trino-url is required when engine=trino", nameof(url));
            }

            Catalog = catalog;
            Schema = schema;
            _statementUrl = url.TrimEnd('/') + "/v1/statement";
            _http = new HttpClient
            {
                Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromMinutes(10) : timeout
            };
            var userName = string.IsNullOrEmpty(user) ? "lean" : user;
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Trino-User", userName);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Trino-Catalog", catalog);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Trino-Schema", schema);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Trino-Source", "fino-r2-plugin");
            // The warehouse columns are naive UTC TIMESTAMP(9); pinning the session timezone keeps
            // from_unixtime_nanos comparisons neutral on any host.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Trino-Time-Zone", "UTC");
        }

        /// <summary>
        /// Runs DuckDB-dialect SQL and invokes <paramref name="onRow"/> for every row, in order. The
        /// cells array is REUSED between rows: parse it immediately, do not retain it. One page is
        /// held at a time (typically 0.5-1.5 MB), so a 7.4M-row result streams at constant memory.
        /// </summary>
        public void Execute(string sql, Action<JsonCell[], int> onRow)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return;
            }
            if (onRow == null)
            {
                throw new ArgumentNullException(nameof(onRow));
            }

            var translated = Translate(sql);
            var cells = new JsonCell[64];
            lock (_gate)
            {
                ThrowIfDisposed();
                var nextUri = (string)null;
                using (var content = new StringContent(translated, Encoding.UTF8, "text/plain"))
                using (var response = _http.PostAsync(_statementUrl, content).GetAwaiter().GetResult())
                {
                    nextUri = ScanPage(response, ref cells, onRow);
                }

                while (!string.IsNullOrEmpty(nextUri))
                {
                    using var response = _http.GetAsync(nextUri).GetAwaiter().GetResult();
                    nextUri = ScanPage(response, ref cells, onRow);
                }
            }
        }

        /// <summary>
        /// Materialises a result as invariant strings — the duckdb contract, used by the preflight
        /// counts and the IV reader. Not for the big batch reads (those go through Execute).
        /// </summary>
        public List<string[]> Query(string sql)
        {
            var rows = new List<string[]>();
            Execute(sql, (cells, count) =>
            {
                var row = new string[count];
                for (var i = 0; i < count; i++)
                {
                    row[i] = R2CellParser.Text(cells[i]);
                }
                rows.Add(row);
            });
            return rows;
        }

        /// <summary>
        /// Decodes one page, invoking <paramref name="onRow"/> per row; returns the next page URI.
        /// The page bytes are read into ONE reused buffer: a fresh 1.5 MB byte[] per page landed on
        /// the LOH ~1,300 times for a chain-day, which was the last visible source of garbage.
        /// </summary>
        private string ScanPage(HttpResponseMessage response, ref JsonCell[] cells,
            Action<JsonCell[], int> onRow)
        {
            var length = ReadIntoPage(response);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Trino HTTP {(int)response.StatusCode} {response.ReasonPhrase}: " +
                    Trim(Encoding.UTF8.GetString(_page, 0, Math.Min(length, 400))));
            }
            var bytes = new ReadOnlySpan<byte>(_page, 0, length);

            var nextUri = (string)null;
            string error = null;
            var reader = new Utf8JsonReader(bytes,
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("nextUri"))
                {
                    reader.Read();
                    nextUri = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                }
                else if (reader.ValueTextEquals("error"))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                    {
                        error = ReadErrorMessage(ref reader);
                    }
                }
                else if (reader.ValueTextEquals("data"))
                {
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartArray)
                    {
                        continue;
                    }
                    // Rows: [ [cell, cell, ...], ... ]
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            continue;
                        }
                        var count = 0;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            Fill(ref reader, ref cells, ref count);
                        }
                        onRow(cells, count);
                    }
                }
            }

            if (error != null)
            {
                throw new InvalidOperationException("Trino: " + error);
            }
            return nextUri;
        }

        /// <summary>Reads the response body into the reused page buffer; returns its length.</summary>
        private int ReadIntoPage(HttpResponseMessage response)
        {
            using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            var length = 0;
            while (true)
            {
                if (length == _page.Length)
                {
                    Array.Resize(ref _page, _page.Length * 2);
                }
                var read = stream.Read(_page, length, _page.Length - length);
                if (read <= 0)
                {
                    return length;
                }
                length += read;
            }
        }

        /// <summary>Reads one cell into the reused buffer.</summary>
        private static void Fill(ref Utf8JsonReader reader, ref JsonCell[] cells, ref int count)
        {
            if (count == cells.Length)
            {
                Array.Resize(ref cells, cells.Length * 2);
            }

            ref var cell = ref cells[count];
            cell.Text = null;
            switch (reader.TokenType)
            {
                case JsonTokenType.Null:
                    cell.Kind = JsonCell.Null;
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    cell.Kind = JsonCell.Boolean;
                    cell.Bool = reader.TokenType == JsonTokenType.True;
                    break;
                case JsonTokenType.String:
                    cell.Kind = JsonCell.String;
                    cell.Text = Normalize(reader.GetString());
                    break;
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out var integer))
                    {
                        cell.Kind = JsonCell.Integer;
                        cell.Long = integer;
                    }
                    else
                    {
                        cell.Kind = JsonCell.Float;
                        cell.Double = reader.GetDouble();
                    }
                    break;
                case JsonTokenType.StartArray:
                case JsonTokenType.StartObject:
                    // Nested value (e.g. parquet `conditions`): skipped, exposed as null.
                    SkipComposite(ref reader);
                    cell.Kind = JsonCell.Null;
                    break;
                default:
                    cell.Kind = JsonCell.Null;
                    break;
            }
            count++;
        }

        /// <summary>Consumes a nested array/object, leaving the reader on its closing token.</summary>
        private static void SkipComposite(ref Utf8JsonReader reader)
        {
            var depth = 0;
            do
            {
                if (reader.TokenType == JsonTokenType.StartArray || reader.TokenType == JsonTokenType.StartObject)
                {
                    depth++;
                }
                else if (reader.TokenType == JsonTokenType.EndArray || reader.TokenType == JsonTokenType.EndObject)
                {
                    depth--;
                }
            }
            while (depth > 0 && reader.Read());
        }

        /// <summary>Extracts the "message" of the statement protocol's error object.</summary>
        private static string ReadErrorMessage(ref Utf8JsonReader reader)
        {
            var depth = 0;
            string message = null;
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (depth == 0)
                        {
                            return message ?? "query failed";
                        }
                        depth--;
                        break;
                    case JsonTokenType.PropertyName:
                        if (depth == 0 && reader.ValueTextEquals("message"))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                message = reader.GetString();
                            }
                        }
                        break;
                }
            }
            return message ?? "query failed";
        }

        /// <summary>Trino renders TIMESTAMP(9) with 9 fractional digits; .NET DateTime parses at most 7.</summary>
        private static string Normalize(string text)
        {
            return text != null && LongFraction.IsMatch(text) ? LongFraction.Replace(text, "$1") : text;
        }

        private static string Translate(string sql)
        {
            var text = NanosFn.Replace(sql, "from_unixtime_nanos(");
            text = RegexFn.Replace(text, "regexp_like(");
            // DuckDB epoch_ns(ts) -> integer nanoseconds since the Unix epoch. Trino 484 has no
            // nanosecond date_diff/extract for TIMESTAMP; to_unixtime() is a double whose ulp at
            // ~1.8e9 s is ~0.4 us, which is finer than .NET's 100 ns DateTime tick in practice.
            text = EpochNsFn.Replace(text,
                "cast(round(to_unixtime($1) * 1000000000) as bigint)");
            // DuckDB arg_max(value, key) / arg_min(value, key) are Trino's max_by / min_by (same order).
            text = ArgMaxFn.Replace(text, "max_by(");
            text = ArgMinFn.Replace(text, "min_by(");
            return text.TrimEnd().TrimEnd(';');
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }
            return text.Length <= 400 ? text : text.Substring(0, 400) + "...";
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(TrinoSession));
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _http.Dispose();
            }
        }
    }
}
