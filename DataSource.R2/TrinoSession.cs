using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    /// FileLoad (0x80131621 / 0x80131040). Raw HTTP keeps the plugin dependency-free: Newtonsoft.Json
    /// is already in Lean's dependency graph.
    ///
    /// The readers were written against DuckDB and emit DuckDB-dialect SQL; the two functions that
    /// do not exist in Trino are translated here, so one SQL builder serves both engines:
    /// <c>make_timestamp_ns(x)</c> -> <c>from_unixtime_nanos(x)</c> and
    /// <c>regexp_matches(a,b)</c> -> <c>regexp_like(a,b)</c>.
    ///
    /// One HttpClient per process, serialised by a lock: queries are issued from several reader
    /// threads. Rows are materialised as invariant strings, the same contract the duckdb CSV path
    /// provided (null -> empty string; timestamps truncated to .NET's 7 fractional digits).
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

        /// <summary>Runs DuckDB-dialect SQL on Trino and returns the rows as invariant strings.</summary>
        public IEnumerable<string[]> Query(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return Array.Empty<string[]>();
            }

            var translated = Translate(sql);
            lock (_gate)
            {
                ThrowIfDisposed();
                var rows = new List<string[]>();
                string nextUri;
                using (var content = new StringContent(translated, Encoding.UTF8, "text/plain"))
                using (var response = _http.PostAsync(_statementUrl, content).GetAwaiter().GetResult())
                {
                    nextUri = Consume(response, rows);
                }

                while (!string.IsNullOrEmpty(nextUri))
                {
                    using var response = _http.GetAsync(nextUri).GetAwaiter().GetResult();
                    nextUri = Consume(response, rows);
                }

                return rows;
            }
        }

        /// <summary>Reads one statement-protocol page into <paramref name="rows"/>; returns the next page URI.</summary>
        private static string Consume(HttpResponseMessage response, List<string[]> rows)
        {
            var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Trino HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Trim(body)}");
            }

            var page = JObject.Parse(body);
            if (page["error"] is JObject error)
            {
                throw new InvalidOperationException(
                    "Trino: " + (error.Value<string>("message") ?? error.ToString(Formatting.None)));
            }

            if (page["data"] is JArray data)
            {
                foreach (var row in data)
                {
                    if (row is JArray cells)
                    {
                        rows.Add(ToRow(cells));
                    }
                }
            }

            return page.Value<string>("nextUri");
        }

        private static string[] ToRow(JArray cells)
        {
            var row = new string[cells.Count];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Format(cells[i]);
            }
            return row;
        }

        private static string Format(JToken cell)
        {
            switch (cell.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return string.Empty;
                case JTokenType.String:
                    return Normalize(cell.Value<string>());
                case JTokenType.Boolean:
                    return cell.Value<bool>() ? "true" : "false";
                case JTokenType.Integer:
                case JTokenType.Float:
                    var value = ((JValue)cell).Value;
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                default:
                    // arrays / row structures (e.g. parquet `conditions`): compact JSON is what the
                    // duckdb CSV path effectively produced for such columns.
                    return cell.ToString(Formatting.None);
            }
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
