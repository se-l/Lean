using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Trino.Ado.Server;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Warehouse access through an always-on Trino coordinator (HTTP), replacing the duckdb child
    /// process when <c>cloudflare-r2.engine=trino</c>.
    ///
    /// The readers were written against DuckDB and emit DuckDB-dialect SQL; the two functions that
    /// do not exist in Trino are translated here, so one SQL builder serves both engines:
    /// <c>make_timestamp_ns(x)</c> -> <c>from_unixtime_nanos(x)</c> and
    /// <c>regexp_matches(a,b)</c> -> <c>regexp_like(a,b)</c>.
    ///
    /// One connection per process, serialised by a lock: the Trino ADO objects are not thread-safe
    /// while the plugin's readers are called from several threads. Rows are materialised as
    /// invariant strings, the same contract the duckdb CSV path provided.
    /// </summary>
    public sealed class TrinoSession : IDisposable
    {
        private static readonly Regex NanosFn = new Regex(@"make_timestamp_ns\(", RegexOptions.Compiled);
        private static readonly Regex RegexFn = new Regex(@"regexp_matches\(", RegexOptions.Compiled);

        private readonly TrinoConnectionProperties _properties;
        private readonly object _gate = new object();
        private TrinoConnection _connection;

        /// <summary>Catalog the plugin reads (e.g. r2).</summary>
        public string Catalog { get; }

        /// <summary>Schema the plugin reads (e.g. market).</summary>
        public string Schema { get; }

        public TrinoSession(string url, string catalog, string schema, string user, TimeSpan timeout)
        {
            Catalog = catalog;
            Schema = schema;
            _properties = new TrinoConnectionProperties
            {
                Server = new Uri(url),
                Catalog = catalog,
                Schema = schema,
                User = string.IsNullOrEmpty(user) ? "lean" : user,
                Source = "fino-r2-plugin",
                // The warehouse columns are naive UTC TIMESTAMP(9); pinning the session timezone
                // keeps from_unixtime_nanos comparisons neutral on any host.
                TimeZone = "UTC",
                Timeout = timeout,
            };
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
                var connection = Connection();
                using var command = new TrinoCommand(connection, translated);
                using var reader = command.ExecuteReader();
                var rows = new List<string[]>();
                while (reader.Read())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = Format(reader.GetValue(i));
                    }
                    rows.Add(row);
                }
                return rows;
            }
        }

        private TrinoConnection Connection()
        {
            if (_connection != null && _connection.State == ConnectionState.Open)
            {
                return _connection;
            }
            _connection?.Dispose();
            _connection = new TrinoConnection(_properties);
            _connection.Open();
            return _connection;
        }

        private static string Translate(string sql)
        {
            var text = NanosFn.Replace(sql, "from_unixtime_nanos(");
            text = RegexFn.Replace(text, "regexp_like(");
            return text.TrimEnd().TrimEnd(';');
        }

        private static string Format(object value)
        {
            switch (value)
            {
                case null:
                case DBNull _:
                    return string.Empty;
                case DateTime dt:
                    return dt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
                case bool b:
                    return b ? "true" : "false";
                case IFormattable f:
                    return f.ToString(null, CultureInfo.InvariantCulture);
                default:
                    return value.ToString() ?? string.Empty;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _connection?.Dispose();
                _connection = null;
            }
        }
    }
}
