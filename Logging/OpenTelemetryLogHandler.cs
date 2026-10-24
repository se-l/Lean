/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

// CA1031: every entry point here is a best-effort telemetry sink — an export, a settings lookup or
// a shutdown failure must degrade to a dropped log record, never to an exception in the caller.
// CA1810: the identity fields cannot all be initialised at their declaration sites —
// host.name recovery depends on host.ip, and container detection returns two values.
#pragma warning disable CA1810
#pragma warning disable CA1031

namespace QuantConnect.Logging
{
    /// <summary>
    /// Ships Lean log lines to a host-local OpenTelemetry Collector over OTLP/HTTP (JSON).
    /// </summary>
    /// <remarks>
    /// Topology: this handler only ever talks to the collector on the same host
    /// (<c>http://127.0.0.1:4318/v1/logs</c> by default). The collector owns the fragile part —
    /// retries, the persistent spool, and the hop to the remote backend (SigNoz) — so an
    /// unreachable backend can only cost delayed or dropped telemetry, never a stalled Lean run.
    ///
    /// Every record carries the OTLP <b>resource</b> attributes <c>service.name</c> and
    /// <c>host.name</c>, mirroring the Fino.jl logger, so records from the backtester, the
    /// toolbox, the optimizer and several machines can be filtered apart downstream.
    ///
    /// The handler is opt-in: <see cref="TryCreate"/> returns null unless an OTLP endpoint is
    /// configured, so an unconfigured Lean behaves exactly as before. Configuration is read from
    /// the environment (the Logging assembly cannot reference QuantConnect.Configuration without a
    /// circular project reference):
    ///
    ///   OTEL_ENDPOINT / OTEL_EXPORTER_OTLP_LOGS_ENDPOINT / OTEL_EXPORTER_OTLP_ENDPOINT
    ///       collector URL, e.g. http://127.0.0.1:4318 (the /v1/logs path is appended when absent).
    ///       Port 4318 is the OTLP/HTTP listener; 4317 is gRPC and will not accept these posts.
    ///   OTEL_ENABLED         true/1/yes to enable with the default endpoint, false/0/no to disable.
    ///   OTEL_SERVICE_NAME   service.name (default "lean").
    ///   LEAN_HOSTNAME             host.name override (default: the machine name).
    ///   OTEL_RESOURCE_ATTRIBUTES  standard k=v,k=v pairs, added to the resource.
    /// </remarks>
    public class OpenTelemetryLogHandler : ILogHandler
    {
        private const string DefaultEndpoint = "http://127.0.0.1:4318/v1/logs";

        // Same shape as the Fino.jl sink: bounded drop gate, batched export, exponential backoff.
        private const int QueueMax = 10_000;
        private const int BatchMax = 512;
        private const int MaxMessageChars = 64 * 1024;
        private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ExportTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan RetryWait = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(120);

        // OTLP numeric severities. Lean's Trace is the normal informational channel; the text keeps
        // Lean's own vocabulary (TRACE/DEBUG/ERROR) while the number stays OTel-standard.
        private const int SeverityDebug = 5;
        private const int SeverityInfo = 9;
        private const int SeverityError = 17;

        private static readonly HttpClient Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        private static readonly long UnixEpochTicks = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        private static readonly string ServiceName =
            FirstNonEmpty(GetSetting("OTEL_SERVICE_NAME")) ?? "lean";

        // Identity: resolved once. Mirrors Fino.jl's src/shared/Log.jl so both stacks are filters
        // in SigNoz with the same attribute names.
        private static readonly bool IsContainer;
        private static readonly string ContainerId;
        private static readonly string ContainerName =
            FirstNonEmpty(GetSetting("CONTAINER_NAME"), GetSetting("LEAN_CONTAINER_NAME")) ?? "";
        private static readonly string HostIp = DetectHostIp();
        private static readonly string HostName = ResolveHostName();

        private static readonly KeyValuePair<string, string>[] ExtraResourceAttributes =
            ParseResourceAttributes(GetSetting("OTEL_RESOURCE_ATTRIBUTES"));

        /// <summary>
        /// Static constructor: identity needs to run in declaration order (host.ip before host.name,
        /// since name recovery uses the address), which inline field initialisers cannot express.
        /// </summary>
        static OpenTelemetryLogHandler()
        {
            IsContainer = DetectContainer(out var containerId);
            ContainerId = containerId;
        }

        // Last constructed handler, flushed from ProcessExit: the Launcher never disposes the
        // global Log.LogHandler, so without this the final flush window would be lost on exit.
        private static int _exitHookInstalled;
        private static OpenTelemetryLogHandler _live;

        private readonly ConcurrentQueue<Entry> _queue = new ConcurrentQueue<Entry>();
        private readonly Uri _endpoint;
        private readonly Thread _worker;
        private readonly ManualResetEventSlim _stop = new ManualResetEventSlim(false);

        private int _queued;
        private int _dropped;
        private int _droppedReported;
        private int _disposed;
        private int _failCount;
        private long _backoffUntilTicks;
        private bool _down;

        private readonly struct Entry
        {
            public readonly long TimeUnixNano;
            public readonly int SeverityNumber;
            public readonly string SeverityText;
            public readonly string Message;

            public Entry(long timeUnixNano, int severityNumber, string severityText, string message)
            {
                TimeUnixNano = timeUnixNano;
                SeverityNumber = severityNumber;
                SeverityText = severityText;
                Message = message;
            }
        }

        /// <summary>
        /// True when an OTLP endpoint is configured (and not explicitly disabled).
        /// </summary>
        public static bool IsEnabled => ResolveEndpoint() != null;

        /// <summary>
        /// The resolved OTLP/HTTP endpoint, or null when the handler is not configured.
        /// </summary>
        public static string Endpoint => ResolveEndpoint()?.ToString();

        /// <summary>
        /// Creates the handler when OTLP shipping is configured, otherwise returns null so callers
        /// can leave their handler composition untouched.
        /// </summary>
        public static ILogHandler TryCreate()
        {
            var endpoint = ResolveEndpoint();
            if (endpoint == null)
            {
                return null;
            }

            try
            {
                return new OpenTelemetryLogHandler(endpoint);
            }
            catch (Exception err)
            {
                // Telemetry must never be the reason a Lean process fails to start.
                Console.Error.WriteLine("[Lean.OTel] handler creation failed, continuing without OTLP: " + err.Message);
                return null;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenTelemetryLogHandler"/> class using the
        /// endpoint resolved from the environment.
        /// </summary>
        public OpenTelemetryLogHandler()
            : this(ResolveEndpoint() ?? new Uri(DefaultEndpoint))
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OpenTelemetryLogHandler"/> class.
        /// </summary>
        /// <param name="endpoint">The OTLP/HTTP logs endpoint (the collector's /v1/logs route).</param>
        public OpenTelemetryLogHandler(Uri endpoint)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));

            _worker = new Thread(Worker)
            {
                IsBackground = true,
                Name = "Lean-OTLP-Exporter"
            };

            InstallExitHook();
            Volatile.Write(ref _live, this);
            _worker.Start();
        }

        /// <summary>
        /// Write error message to log
        /// </summary>
        public void Error(string text)
        {
            Enqueue(SeverityError, "ERROR", text);
        }

        /// <summary>
        /// Write debug message to log
        /// </summary>
        public void Debug(string text)
        {
            Enqueue(SeverityDebug, "DEBUG", text);
        }

        /// <summary>
        /// Write trace message to log
        /// </summary>
        public void Trace(string text)
        {
            Enqueue(SeverityInfo, "TRACE", text);
        }

        /// <summary>
        /// Flushes whatever is queued, then stops the exporter thread. Idempotent.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Flushes whatever is queued, then stops the exporter thread.
        /// </summary>
        /// <param name="disposing">True when called from <see cref="Dispose()"/></param>
        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            if (ReferenceEquals(Volatile.Read(ref _live), this))
            {
                Volatile.Write(ref _live, null);
            }

            try
            {
                _stop.Set();
                if (disposing && _worker.IsAlive && !_worker.Join(DisposeTimeout))
                {
                    Console.Error.WriteLine("[Lean.OTel] exporter thread did not stop in time; abandoning it");
                }
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] shutdown failed: " + err.Message);
            }

            // Best effort: ship the tail without letting a wedged peer hold up process exit.
            try
            {
                Export(Collect(BatchMax * 8), DisposeTimeout, force: true);
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] final flush failed: " + err.Message);
            }

            _stop.Dispose();
        }

        private static void InstallExitHook()
        {
            if (Interlocked.Exchange(ref _exitHookInstalled, 1) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
            {
                try
                {
                    Volatile.Read(ref _live)?.Dispose();
                }
                catch
                {
                    // process is going down; nothing useful left to do
                }
            };
        }

        private void Enqueue(int severityNumber, string severityText, string text)
        {
            if (text == null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (Interlocked.Increment(ref _queued) > QueueMax)
            {
                Interlocked.Decrement(ref _queued);
                Interlocked.Increment(ref _dropped);
                return;
            }

            if (text.Length > MaxMessageChars)
            {
                text = string.Concat(text.AsSpan(0, MaxMessageChars), "…[truncated]");
            }

            var now = DateTime.UtcNow;
            _queue.Enqueue(new Entry((now.Ticks - UnixEpochTicks) * 100, severityNumber, severityText, text));
        }

        private List<Entry> Collect(int max)
        {
            var batch = new List<Entry>(Math.Min(max, 256));
            while (batch.Count < max && _queue.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _queued);
                batch.Add(entry);
            }
            return batch;
        }

        private void Worker()
        {
            while (!_stop.Wait(FlushInterval))
            {
                try
                {
                    var batch = Collect(BatchMax);
                    if (batch.Count > 0)
                    {
                        Export(batch, ExportTimeout, force: false);
                    }
                    else
                    {
                        ReportDrops();
                    }
                }
                catch (Exception err)
                {
                    // A broken export path must never take the producer down with it.
                    Console.Error.WriteLine("[Lean.OTel] export loop error: " + err.Message);
                }
            }
        }

        private void ReportDrops()
        {
            var dropped = Volatile.Read(ref _dropped);
            var reported = Interlocked.Exchange(ref _droppedReported, dropped);
            if (dropped > reported)
            {
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[Lean.OTel] dropped {0} log record(s): queue saturated at {1}", dropped - reported, QueueMax));
            }
        }

        private bool Export(List<Entry> batch, TimeSpan timeout, bool force)
        {
            if (batch.Count == 0)
            {
                return true;
            }

            var now = DateTime.UtcNow.Ticks;
            if (!force && now < Interlocked.Read(ref _backoffUntilTicks))
            {
                // Backend in a cooldown window: drop instead of hammering an unreachable peer.
                Interlocked.Add(ref _dropped, batch.Count);
                return false;
            }

            try
            {
                var payload = BuildPayload(batch);
                using (var cts = new CancellationTokenSource(timeout))
                using (var content = new ByteArrayContent(payload))
                {
                    content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                    using (var response = Http.PostAsync(_endpoint, content, cts.Token).GetAwaiter().GetResult())
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            OnFailure("collector returned " + (int)response.StatusCode);
                            return false;
                        }
                    }
                }

                Interlocked.Exchange(ref _failCount, 0);
                Interlocked.Exchange(ref _backoffUntilTicks, 0);
                if (_down)
                {
                    _down = false;
                    Console.Error.WriteLine("[Lean.OTel] collector reachable again at " + _endpoint);
                }
                ReportDrops();
                return true;
            }
            catch (Exception err)
            {
                OnFailure(err.Message);
                return false;
            }
        }

        private void OnFailure(string reason)
        {
            var fails = Interlocked.Increment(ref _failCount);
            var exponent = Math.Min(fails - 1, 6);
            var wait = Math.Min(RetryWait.TotalSeconds * Math.Pow(2, exponent), MaxRetryWait.TotalSeconds);
            Interlocked.Exchange(ref _backoffUntilTicks, DateTime.UtcNow.AddSeconds(wait).Ticks);

            if (!_down)
            {
                _down = true;
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "[Lean.OTel] OTLP/HTTP backend unreachable at {0} ({1}); backing off, telemetry is best-effort",
                    _endpoint, reason));
            }
        }

        private static byte[] BuildPayload(List<Entry> batch)
        {
            var buffer = new ArrayBufferWriter<byte>(1024);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("resourceLogs");
                writer.WriteStartArray();
                writer.WriteStartObject();

                writer.WritePropertyName("resource");
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartArray();
                WriteStringAttribute(writer, "service.name", ServiceName);
                WriteStringAttribute(writer, "host.name", HostName);
                WriteStringAttribute(writer, "host.ip", HostIp);
                WriteStringAttribute(writer, "container.id", ContainerId);
                WriteStringAttribute(writer, "container.name", ContainerName);
                WriteIntAttribute(writer, "process.pid", Environment.ProcessId);
                foreach (var pair in ExtraResourceAttributes)
                {
                    WriteStringAttribute(writer, pair.Key, pair.Value);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WritePropertyName("scopeLogs");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WritePropertyName("scope");
                writer.WriteStartObject();
                writer.WriteString("name", "QuantConnect.Logging");
                writer.WriteEndObject();

                writer.WritePropertyName("logRecords");
                writer.WriteStartArray();
                foreach (var entry in batch)
                {
                    writer.WriteStartObject();
                    writer.WriteString("timeUnixNano", entry.TimeUnixNano.ToString(CultureInfo.InvariantCulture));
                    writer.WriteString("observedTimeUnixNano", entry.TimeUnixNano.ToString(CultureInfo.InvariantCulture));
                    writer.WriteNumber("severityNumber", entry.SeverityNumber);
                    writer.WriteString("severityText", entry.SeverityText);
                    writer.WritePropertyName("body");
                    writer.WriteStartObject();
                    writer.WriteString("stringValue", entry.Message);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();

                writer.WriteEndObject();
                writer.WriteEndArray();

                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return buffer.WrittenSpan.ToArray();
        }

        private static void WriteStringAttribute(Utf8JsonWriter writer, string key, string value)
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WritePropertyName("value");
            writer.WriteStartObject();
            writer.WriteString("stringValue", value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static void WriteIntAttribute(Utf8JsonWriter writer, string key, long value)
        {
            writer.WriteStartObject();
            writer.WriteString("key", key);
            writer.WritePropertyName("value");
            writer.WriteStartObject();
            writer.WriteNumber("intValue", value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static Uri ResolveEndpoint()
        {
            var enabledSetting = GetSetting("OTEL_ENABLED");
            var enabled = ParseBool(enabledSetting);

            var raw = FirstNonEmpty(
                GetSetting("OTEL_ENDPOINT"),
                GetSetting("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT"),
                GetSetting("OTEL_EXPORTER_OTLP_ENDPOINT"));

            if (raw == null)
            {
                return enabled == true ? new Uri(DefaultEndpoint) : null;
            }

            if (enabled == false)
            {
                return null;
            }

            var endpoint = NormalizeEndpoint(raw);
            if (endpoint.Port == 4317)
            {
                Console.Error.WriteLine("[Lean.OTel] port 4317 is the collector's gRPC listener; OTLP/HTTP posts go to 4318 — " +
                                        "expect failures until the endpoint is corrected");
            }
            return endpoint;
        }

        private static Uri NormalizeEndpoint(string raw)
        {
            var text = raw.Trim();
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                text = "http://" + text;
            }

            var builder = new UriBuilder(new Uri(text, UriKind.Absolute));
            if (builder.Uri.IsDefaultPort)
            {
                builder.Port = 4318;
            }

            if (string.IsNullOrEmpty(builder.Path) || builder.Path == "/")
            {
                builder.Path = "/v1/logs";
            }
            return builder.Uri;
        }

        private static KeyValuePair<string, string>[] ParseResourceAttributes(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Array.Empty<KeyValuePair<string, string>>();
            }

            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var part in raw.Split(','))
            {
                var split = part.IndexOf('=', StringComparison.Ordinal);
                if (split <= 0 || split == part.Length - 1)
                {
                    continue;
                }

                var key = part.Substring(0, split).Trim();
                var value = part.Substring(split + 1).Trim();
                if (key.Length > 0 && value.Length > 0)
                {
                    pairs.Add(new KeyValuePair<string, string>(key, value));
                }
            }
            return pairs.ToArray();
        }

        private static string SafeMachineName()
        {
            try
            {
                return string.IsNullOrEmpty(Environment.MachineName) ? "unknown" : Environment.MachineName;
            }
            catch
            {
                return "unknown";
            }
        }

        /// <summary>
        /// Resolve <c>host.name</c>: <c>LEAN_HOSTNAME</c> wins, then the machine name, and inside a
        /// container the machine name is recovered from the network identity because Docker defaults
        /// the UTS hostname to the container's short id (which shows up downstream as a bare hash).
        /// </summary>
        private static string ResolveHostName()
        {
            var overrideValue = FirstNonEmpty(GetSetting("LEAN_HOSTNAME"));
            if (overrideValue != null)
            {
                return overrideValue;
            }

            var machine = SafeMachineName();
            if (!IsContainer && !LooksLikeContainerId(machine))
            {
                return machine;
            }

            var recovered = HostFromNetwork(machine);
            return recovered ?? machine;
        }

        /// <summary>
        /// Detect a container from runtime markers — never from the hostname's shape, because a bare
        /// machine may legitimately be named like a hash.
        /// </summary>
        /// <param name="containerId">The full container id, or "" when not in a container.</param>
        private static bool DetectContainer(out string containerId)
        {
            containerId = "";
            try
            {
                var marker = File.Exists("/.dockerenv") || File.Exists("/run/.containerenv");
                var envContainer = !string.IsNullOrWhiteSpace(GetSetting("CONTAINER"));
                var pidNamespace = false;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var selfNs = LinkTarget("/proc/self/ns/pid");
                    var initNs = LinkTarget("/proc/1/ns/pid");
                    pidNamespace = !string.IsNullOrEmpty(selfNs) && !string.IsNullOrEmpty(initNs) && selfNs != initNs;
                }

                if (!marker && !envContainer && !pidNamespace)
                {
                    return false;
                }

                containerId = FullContainerId();
                return true;
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] container detection failed: " + err.Message);
                return false;
            }
        }

        private static string LinkTarget(string path)
        {
            var info = new FileInfo(path);
            return info.Exists || Directory.Exists(path) ? info.LinkTarget ?? "" : "";
        }

        /// <summary>
        /// The full container id, or "" outside a container. Docker sets no env var for its own id,
        /// but renders the cgroup path as <c>/&lt;scope&gt;/docker-&lt;full id&gt;.scope</c>; the id is
        /// also present in cgroup v1 controller paths. Falls back to the UTS hostname when that is a
        /// container id (the short id).
        /// </summary>
        private static string FullContainerId()
        {
            try
            {
                if (File.Exists("/proc/self/cgroup"))
                {
                    var body = File.ReadAllText("/proc/self/cgroup");
                    var match = Regex.Match(body, @"(?:docker|containerd|crio|libpod)-([0-9a-f]{64})");
                    if (match.Success)
                    {
                        return match.Groups[1].Value;
                    }
                }
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] container id lookup failed: " + err.Message);
            }

            var machine = SafeMachineName();
            return LooksLikeContainerId(machine) ? machine : "";
        }

        /// <summary>
        /// Docker/containerd name containers after the first 12 hex chars of their id; k8s pod names
        /// are not hex and pass through untouched.
        /// </summary>
        private static bool LooksLikeContainerId(string value)
        {
            return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, "^[0-9a-fA-F]{12,64}$");
        }

        /// <summary>
        /// Recover the machine name for a containerized process whose UTS hostname is only a container
        /// id: PTR of the primary interface (the host itself under host networking), then of the
        /// default-route gateway. Best effort — a docker bridge container resolves through the host's
        /// upstream DNS and usually has no PTR for the gateway; the collector then stamps host.name.
        /// </summary>
        private static string HostFromNetwork(string containerId)
        {
            var candidates = new List<string> { HostIp };
            var gateway = DefaultGatewayIpv4();
            if (!string.IsNullOrEmpty(gateway))
            {
                candidates.Add(gateway);
            }

            foreach (var ip in candidates)
            {
                var name = ReverseDns(ip);
                if (string.IsNullOrEmpty(name) || name == containerId || LooksLikeContainerId(name))
                {
                    continue;
                }
                return name;
            }
            return null;
        }

        /// <summary>
        /// First non-loopback IPv4 address across the machine's interfaces, or "" — emitted as
        /// <c>host.ip</c> so a record can be tied to an address even when the name is ambiguous.
        /// </summary>
        private static string DetectHostIp()
        {
            try
            {
                // Take the first non-loopback IPv4 address across the machine's interfaces.
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    {
                        continue;
                    }

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                            !IPAddress.IsLoopback(addr.Address))
                        {
                            return addr.Address.ToString();
                        }
                    }
                }
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] host.ip lookup failed: " + err.Message);
            }
            return "";
        }

        private static string ReverseDns(string ip)
        {
            if (string.IsNullOrEmpty(ip))
            {
                return null;
            }

            try
            {
                var entry = Dns.GetHostEntry(IPAddress.Parse(ip));
                var name = entry.HostName?.TrimEnd('.');
                if (string.IsNullOrEmpty(name) || name == ip || name.StartsWith("_", StringComparison.Ordinal) ||
                    name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// IPv4 default gateway from /proc/net/route (Iface Destination Gateway Flags); the kernel
        /// stores addresses as little-endian hex ("FE00A8C0" → 192.168.0.254).
        /// </summary>
        private static string DefaultGatewayIpv4()
        {
            try
            {
                if (!File.Exists("/proc/net/route"))
                {
                    return "";
                }

                foreach (var line in File.ReadAllLines("/proc/net/route").Skip(1))
                {
                    var f = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length < 4 || f[1] != "00000000")
                    {
                        continue; // Iface Destination Gateway Flags — Destination 0 = default
                    }

                    if (!int.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var flags) ||
                        (flags & 0x2) == 0)
                    {
                        continue; // RTF_GATEWAY not set
                    }

                    return RouteHexToIpv4(f[2]);
                }
            }
            catch (Exception err)
            {
                Console.Error.WriteLine("[Lean.OTel] default gateway lookup failed: " + err.Message);
            }
            return "";
        }

        private static string RouteHexToIpv4(string hex)
        {
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
            {
                return "";
            }
            return string.Format(CultureInfo.InvariantCulture, "{0}.{1}.{2}.{3}",
                n & 0xff, (n >> 8) & 0xff, (n >> 16) & 0xff, (n >> 24) & 0xff);
        }

        private static string GetSetting(string key)
        {
            try
            {
                var value = Environment.GetEnvironmentVariable(key);
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
            return null;
        }

        private static bool? ParseBool(string raw)
        {
            if (raw == null)
            {
                return null;
            }

            switch (raw.Trim().ToUpperInvariant())
            {
                case "1":
                case "TRUE":
                case "YES":
                case "ON":
                    return true;
                case "0":
                case "FALSE":
                case "NO":
                case "OFF":
                    return false;
                default:
                    return null;
            }
        }
    }
}
