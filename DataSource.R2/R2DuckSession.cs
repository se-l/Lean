using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Raised when the session could not answer. Callers must never read this as "no rows": an empty
    /// result has to mean the query ran and matched nothing.
    /// </summary>
    internal sealed class R2SessionException : Exception
    {
        public R2SessionException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// One long-lived duckdb process per catalog, instead of a process per scan.
    ///
    /// Measured on this host, a scan cost 250-600 ms of spawn plus a fresh `LOAD iceberg`, against
    /// 3-4 ms for the query itself; the data is a 6.7 MB parquet file per day. This session starts
    /// the process once, applies the caller's preamble (the SET/LOAD statements) once, then answers
    /// queries over stdin/stdout, so a call costs the query.
    ///
    /// Framing: the SQL goes to stdin followed by a sentinel `SELECT`; every line printed before the
    /// sentinel is a row of the statements above it. `bail on` makes a failing statement kill the
    /// process, so stdout ends instead of quietly printing nothing — the error is then read off
    /// stderr and thrown, never swallowed.
    ///
    /// The SQL never reaches argv (the catalog token is in the preamble), and the child holds the
    /// read end of our stdin, so it exits when the engine does.
    /// </summary>
    internal sealed class R2DuckSession : IDisposable
    {
        private const string SentinelPrefix = "<<r2-session:";

        private readonly string _duckdbPath;
        private readonly string _preamble;
        private readonly Func<string, string> _redact;
        private readonly object _gate = new object();
        private readonly StringBuilder _stderr = new StringBuilder();
        private readonly Thread _pump;

        private Process _process;
        private StreamWriter _stdin;
        private StreamReader _stdout;
        private int _sequence;
        private bool _dead;
        private bool _disposed;

        /// <summary>
        /// Starts duckdb in list mode with bail-on-error, applies the preamble, and verifies the
        /// session answers before returning.
        /// </summary>
        /// <param name="duckdbPath">Path to the duckdb executable.</param>
        /// <param name="preamble">Statements applied once (SET/LOAD); may be empty.</param>
        /// <param name="redact">Removes credentials from any text that will be thrown.</param>
        public R2DuckSession(string duckdbPath, string preamble, Func<string, string> redact)
        {
            _duckdbPath = duckdbPath;
            _preamble = preamble ?? string.Empty;
            _redact = redact ?? (text => text);

            Start();
            _pump = new Thread(DrainStderr) { IsBackground = true, Name = "r2-duckdb-stderr" };
            _pump.Start();

            // A killed engine (tmux kill-session sends SIGHUP) never closes our stdin, so the child
            // would linger idle after its parent is gone; take it down with the process. SIGKILL
            // still leaves one behind, which is harmless (it holds no more than a few hundred MB
            // and exits as soon as anything writes to its dead stdout).
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Dispose();
        }

        /// <summary>False once the process is gone; the caller then rethrows instead of reading nothing.</summary>
        public bool IsAlive
        {
            get
            {
                lock (_gate)
                {
                    return !_dead && !_disposed && _process != null && !SafeHasExited(_process);
                }
            }
        }

        /// <summary>
        /// Runs <paramref name="sql"/> (any number of statements) and returns the rows they printed.
        /// Throws <see cref="R2SessionException"/> when duckdb reports an error or the process dies.
        /// </summary>
        public IReadOnlyList<string[]> Query(string sql)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    throw new R2SessionException("R2LocalCatalog: the duckdb session is disposed.");
                }
                if (_dead || _process == null || SafeHasExited(_process))
                {
                    throw new R2SessionException("R2LocalCatalog: the duckdb session is not running.");
                }

                var sentinel = SentinelPrefix + (++_sequence) + ">>";
                var statements = sql.TrimEnd();
                _stdin.Write(statements);
                if (!statements.EndsWith(";", StringComparison.Ordinal))
                {
                    _stdin.Write(';');
                }
                _stdin.Write('\n');
                _stdin.Write($"SELECT '{sentinel}';\n");
                _stdin.Flush();

                var rows = new List<string[]>();
                string line;
                while ((line = _stdout.ReadLine()) != null)
                {
                    if (line == sentinel)
                    {
                        return rows;
                    }
                    if (IsDataLine(line))
                    {
                        rows.Add(line.Split('|'));
                    }
                }

                // No sentinel: bail-on-error killed duckdb, so the statements above failed.
                Fail("duckdb exited while running a statement");
                return rows;
            }
        }

        /// <summary>Shuts the process down; the engine's own exit does the same through the closed stdin.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                try
                {
                    _stdin?.Close();
                }
                catch
                {
                    // The child is going away regardless.
                }
                try
                {
                    if (_process != null && !SafeHasExited(_process))
                    {
                        _process.Kill();
                    }
                }
                catch
                {
                    // Already gone.
                }
                _process?.Dispose();
                _dead = true;
            }
        }

        private void Start()
        {
            var psi = new ProcessStartInfo(_duckdbPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            // -init /dev/null: the operator's ~/.duckdbrc must not print or configure anything into
            // a session whose stdout is a framing protocol.
            psi.ArgumentList.Add("-init");
            psi.ArgumentList.Add("/dev/null");
            psi.ArgumentList.Add("-csv");
            psi.ArgumentList.Add("-noheader");

            try
            {
                _process = Process.Start(psi);
            }
            catch (Exception error)
            {
                throw new R2SessionException(
                    $"R2LocalCatalog: could not start {_duckdbPath}: {error.Message}");
            }
            if (_process == null)
            {
                throw new R2SessionException($"R2LocalCatalog: could not start {_duckdbPath}.");
            }

            _stdin = _process.StandardInput;
            _stdout = _process.StandardOutput;

            // list mode: pipe separated, no quoting or padding, so a row is a line split on '|' and
            // the only columns we read are timestamps, tickers and numbers.
            // bail on: a failing statement kills the process (see Query).
            _stdin.Write(".mode list\n.bail on\n");
            if (_preamble.Length > 0)
            {
                _stdin.Write(_preamble);
                _stdin.Write('\n');
            }
            _stdin.Flush();

            // The preamble itself can fail (a bad SET or a missing extension). Prove the session
            // answers before the caller relies on it.
            var probe = SentinelPrefix + "start>>";
            _stdin.Write($"SELECT '{probe}';\n");
            _stdin.Flush();
            string line;
            while ((line = _stdout.ReadLine()) != null)
            {
                if (line == probe)
                {
                    return;
                }
            }

            var detail = ReadStderrNow();
            _dead = true;
            throw new R2SessionException(
                $"R2LocalCatalog: duckdb session did not start: {detail}");
        }

        private void DrainStderr()
        {
            try
            {
                string line;
                while ((line = _process.StandardError.ReadLine()) != null)
                {
                    lock (_stderr)
                    {
                        _stderr.AppendLine(line);
                    }
                }
            }
            catch
            {
                // A torn-down pipe is not interesting.
            }
        }

        /// <summary>The error text of a dead session: the process is gone, so this cannot block.</summary>
        private string ReadStderrNow()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _process.WaitForExit(2000);
                }
                _pump?.Join(500);
            }
            catch
            {
                // Fall through to whatever was collected.
            }
            lock (_stderr)
            {
                return _redact(_stderr.ToString().Trim());
            }
        }

        /// <summary>Marks the session dead and throws with the (redacted) duckdb error.</summary>
        private void Fail(string what)
        {
            _dead = true;
            throw new R2SessionException($"R2LocalCatalog: {what}: {ReadStderrNow()}");
        }

        private static bool SafeHasExited(Process process)
        {
            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>A line that carries data (duckdb chatter and statement-result booleans are not data).</summary>
        private static bool IsDataLine(string line)
        {
            return line.Length > 0 && !line.StartsWith("-", StringComparison.Ordinal) &&
                   line != "true" && line != "false";
        }
    }
}
