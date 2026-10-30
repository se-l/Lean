using NetMQ;
using NetMQ.Sockets;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Thread-safe, pipelined DEALER client for the Julia pricing service (PricerZMQ).
    ///
    /// One socket — one TCP connection — serves every thread in the process. Each request
    /// carries a 64-bit id in its own ZMQ frame, which the server echoes, so replies may
    /// arrive in any order and a slow solve no longer blocks the calls queued behind it.
    /// The previous REQ/REP client held a process-wide lock across send+receive, so the
    /// algorithm thread waited on one solve at a time (~17 ms each for IV).
    ///
    /// Frames (all multi-byte values little-endian):
    ///   request   [id:8][payload]
    ///   reply     [id:8][0x02 + Float32 | 0x03 + UTF-8 error]
    /// The server echoes the leading frames it received, so a legacy REQ client sending
    /// [""][payload] keeps working against the same port during a mixed deployment.
    ///
    /// Concurrency rules:
    ///   * at most MaxInFlight requests outstanding (the window);
    ///   * the two-frame send is atomic (a lock never held across the wait);
    ///   * exactly one thread receives — the reader started in the constructor.
    /// </summary>
    public sealed class JuliaPricingClient : IDisposable
    {
        private readonly DealerSocket _socket;
        private readonly string _endpoint;
        private static readonly Action<string> _log = Console.WriteLine;

        // NetMQ sockets are SINGLE-THREADED. Not "send under a lock" — one thread total. The
        // receive thread plus worker threads calling Send is exactly the pattern that produced
        // `NullReferenceException at SocketBase.ProcessCommands` on 2026-10-30 and killed the run:
        // a send-side lock does not exclude the receiver.
        //
        // So all socket I/O happens on _ioThread: it drains the outbound queue (sends) and polls
        // for replies. Callers only enqueue and await their TaskCompletionSource.
        private readonly ConcurrentQueue<Outbound> _outbound = new();
        private static readonly TimeSpan IoPollInterval = TimeSpan.FromMilliseconds(1);

        // id -> waiting caller. Ids are a monotonic counter and are never reused, so a
        // reply that arrives after its caller timed out can only miss this map.
        private readonly ConcurrentDictionary<ulong, TaskCompletionSource<byte[]>> _pending = new();

        // Process-wide admission control: bounds requests IN FLIGHT across every consumer, so a
        // task can fan out freely without overfilling the pricer's queue. Shared by reference when
        // a budget is injected (signals/risk/quote paths), otherwise owned by this client.
        private readonly PricerBudget _budget;
        private readonly bool _ownsBudget;
        private readonly Thread _ioThread;
        private volatile bool _disposed;
        private long _nextId;
        private long _lateReplies;
        private long _timeouts;
        private long _malformed;

        private readonly struct Outbound
        {
            public readonly ulong Id;
            public readonly byte[] IdBytes;
            public readonly byte[] Payload;
            public readonly TaskCompletionSource<byte[]> Completion;

            public Outbound(ulong id, byte[] payload, TaskCompletionSource<byte[]> completion)
            {
                Id = id;
                IdBytes = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(IdBytes, id);
                Payload = payload;
                Completion = completion;
            }
        }

        /// <summary>
        /// Deadline for one request, from window slot to reply. ZMQ gives no deadline of
        /// its own: with nothing listening (observed 2026-10-25 — the pricer moved to 8103
        /// while the algorithm config still said 8102) the old client waited forever and
        /// Lean's 20-minute Isolator killed the timestep with no log line. 30 s turns that
        /// into one readable TimeoutException naming the endpoint.
        /// </summary>
        private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

        public enum Command : byte
        {
            Price      = 0x00,
            IV         = 0x01,
            Delta      = 0x02,
            Vega       = 0x03,
            Theta      = 0x04,
            Gamma      = 0x05,
            Speed      = 0x06,
            GammaDecay = 0x07,
            GammaVol   = 0x08,
            ThetaDecay = 0x09,
            VegaDecay  = 0x0A,
            Vanna      = 0x0B,
            Volga      = 0x0C,
            MnyFwd     = 0x0D
        }

        /// <param name="maxInFlight">
        /// Requests allowed in flight at once when this client owns its budget. 0 (default) uses
        /// <see cref="PricerBudget.DefaultCapacity"/> — the pricer's worker count, which is this
        /// machine's CPU count while both run on the same host. More than that only queues inside
        /// the server and inflates measured latency.
        /// </param>
        /// <param name="budget">
        /// Optional shared budget. Pass one when several consumers (signal passes, risk, quote
        /// indicator) must share a single in-flight cap; the client then does not own it.
        /// </param>
        public JuliaPricingClient(string endpoint, int maxInFlight = 0, PricerBudget budget = null)
        {
            _endpoint = endpoint;
            _budget = budget ?? new PricerBudget(maxInFlight > 0 ? maxInFlight : PricerBudget.DefaultCapacity);
            _ownsBudget = budget == null;

            _socket = new DealerSocket();
            _socket.Options.Linger = TimeSpan.Zero;   // don't block Dispose on queued frames
            _socket.Connect(endpoint);

            _ioThread = new Thread(IoLoop)
            {
                IsBackground = true,
                Name = "JuliaPricing-io-" + endpoint
            };
            _ioThread.Start();
        }

        // ── Socket I/O — this thread is the ONLY user of _socket ────────────

        private void IoLoop()
        {
            var msg = new NetMQMessage();
            while (!_disposed)
            {
                DrainOutbound();

                try
                {
                    if (!_socket.TryReceiveMultipartMessage(IoPollInterval, ref msg, 2))
                    {
                        continue;
                    }

                    Dispatch(msg);
                    // Drain everything already queued before going back to the send side.
                    while (!_disposed && _socket.TryReceiveMultipartMessage(TimeSpan.Zero, ref msg, 2))
                    {
                        Dispatch(msg);
                    }
                }
                catch (Exception ex)
                {
                    if (_disposed) return;
                    Interlocked.Increment(ref _malformed);
                    _log($"[JuliaPricing] receive failed on {_endpoint}: {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(10);
                }
            }
        }

        /// <summary>Sends every queued request. Runs on the I/O thread, never concurrently.</summary>
        private void DrainOutbound()
        {
            while (_outbound.TryDequeue(out Outbound item))
            {
                try
                {
                    _socket.SendMoreFrame(item.IdBytes);
                    _socket.SendFrame(item.Payload);
                }
                catch (Exception ex)
                {
                    // A failed send is that caller's failure, not the process's.
                    if (_pending.TryRemove(item.Id, out _))
                    {
                        item.Completion.TrySetException(
                            new InvalidOperationException($"Julia pricing: send failed on {_endpoint}: {ex.Message}", ex));
                    }
                    if (_disposed) return;
                }
            }
        }

        private void Dispatch(NetMQMessage msg)
        {
            if (msg.FrameCount != 2)
            {
                Interlocked.Increment(ref _malformed);
                return;
            }

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(msg[0].Buffer);
            // TryReceiveMultipartMessage CloneData()s every frame, so this array is owned by
            // the message and stays valid after the next receive.
            byte[] reply = msg[1].Buffer;

            if (_pending.TryRemove(id, out var tcs))
            {
                tcs.TrySetResult(reply);
            }
            else
            {
                Interlocked.Increment(ref _lateReplies);
            }
        }

        // ── Call path ───────────────────────────────────────────────────────

        private async Task<byte[]> CallAsync(byte[] payload, TimeSpan timeout, Command command, string ticker,
            PricerPriority priority)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(JuliaPricingClient));

            IDisposable lease;
            try
            {
                lease = await _budget.AcquireAsync(priority, timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref _timeouts);
                _log($"[JuliaPricing] BUDGET WAIT exceeded {timeout.TotalSeconds:0}s on {_endpoint} " +
                     $"for {command} {ticker} — capacity={_budget.Capacity} in-flight={_budget.InFlight} " +
                     $"waiting={_budget.Waiting}; the pricer is not draining.");
                throw;
            }

            ulong id = (ulong)Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            try
            {
                // Hand the two frames to the I/O thread. It is the socket's only user, so a
                // request can never be spliced into another's frames or race the receiver.
                _outbound.Enqueue(new Outbound(id, payload, tcs));

                return await tcs.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Interlocked.Increment(ref _timeouts);
                _log($"[JuliaPricing] TIMEOUT after {timeout.TotalSeconds:0}s on {_endpoint} " +
                     $"for {command} {ticker} (id={id}) — the pricer is not answering " +
                     "(wrong PricerPort or a wedged server).");
                // Unlike the old REQ socket, a timed-out DEALER is still usable: no reset,
                // no reconnect, no identity churn. The reply, if it ever comes, is dropped
                // by id and counted in _lateReplies.
                throw;
            }
            finally
            {
                _pending.TryRemove(id, out _);
                lease.Dispose();
            }
        }

        /// <summary>
        /// Same call as <see cref="IV"/>/<see cref="Price"/>/..., but awaitable: several
        /// contracts can be priced in parallel without parking a thread per request. The
        /// window bounds how many are in flight; the pricer serves them concurrently.
        /// </summary>
        public Task<double> SendAsync(
            Command command,
            bool isCall,
            double s,
            double k,
            double t,
            double sigma,
            double price,
            long calcTimeUtc,
            string ticker,
            string yieldCurve = "YC",
            double dSpot = 0.0,
            double dSigma = 0.0,
            double dTime = 0.0,
            int timeSteps = 0,
            int spaceSteps = 0,
            PricerPriority priority = PricerPriority.Normal)
        {
            if (command == Command.Price && double.IsNaN(sigma)) return Task.FromResult(double.NaN);
            if (command == Command.IV && double.IsNaN(price)) return Task.FromResult(double.NaN);

            byte[] payload = BuildPayload(command, isCall, s, k, t, sigma, price, calcTimeUtc,
                ticker, yieldCurve, dSpot, dSigma, dTime, timeSteps, spaceSteps);
            return SendCoreAsync(payload, command, ticker, priority);
        }

        private async Task<double> SendCoreAsync(byte[] payload, Command command, string ticker,
            PricerPriority priority)
        {
            var sw = Stopwatch.StartNew();
            byte[] reply = await CallAsync(payload, CallTimeout, command, ticker, priority).ConfigureAwait(false);
            double result = ParseReply(reply);
            RecordCall(command, ticker, sw.Elapsed.TotalMilliseconds);
            return result;
        }

        /// <summary>Blocking wrapper around <see cref="SendAsync"/> — the historical API.</summary>
        private double Send(
            Command command,
            bool isCall,
            double s,
            double k,
            double t,
            double sigma,
            double price,
            long calcTimeUtc,
            string ticker,
            string yieldCurve = "YC",
            double dSpot = 0.0,
            double dSigma = 0.0,
            double dTime = 0.0,
            int timeSteps = 0,
            int spaceSteps = 0)
        {
            return SendAsync(command, isCall, s, k, t, sigma, price, calcTimeUtc, ticker,
                    yieldCurve, dSpot, dSigma, dTime, timeSteps, spaceSteps)
                .GetAwaiter().GetResult();
        }

        // ── Wire encode / decode ────────────────────────────────────────────

        private static byte[] BuildPayload(
            Command command,
            bool isCall,
            double s,
            double k,
            double t,
            double sigma,
            double price,
            long calcTimeUtc,
            string ticker,
            string yieldCurve,
            double dSpot,
            double dSigma,
            double dTime,
            int timeSteps,
            int spaceSteps)
        {
            byte[] tickerBytes = Encoding.UTF8.GetBytes(ticker);
            byte[] yieldCurveBytes = Encoding.UTF8.GetBytes(yieldCurve);

            // cmd(1) + is_call(1) + S(8)+K(8)+T(8)+sigma(8)+price(8) + calc_time_utc(8)
            //          + len_ticker(4)+ticker + len_yc(4)+yc
            //          + d_spot(8)+d_sigma(8)+d_time(8) + time_steps(4)+space_steps(4)
            int byteCount = 1 + 1
                + 5 * sizeof(double)
                + sizeof(long)
                + sizeof(int) + tickerBytes.Length
                + sizeof(int) + yieldCurveBytes.Length
                + 3 * sizeof(double)
                + 2 * sizeof(int);

            byte[] buf = new byte[byteCount];
            int pos = 0;

            buf[pos++] = (byte)command;
            buf[pos++] = isCall ? (byte)0x01 : (byte)0x00;

            void WriteDouble(double v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(double);
            }

            void WriteLong(long v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(long);
            }

            void WriteInt(int v)
            {
                MemoryMarshal.Write(buf.AsSpan(pos), ref v);
                pos += sizeof(int);
            }

            void WriteBytes(byte[] b)
            {
                WriteInt(b.Length);
                b.CopyTo(buf, pos);
                pos += b.Length;
            }

            WriteDouble(s);
            WriteDouble(k);
            WriteDouble(t);
            WriteDouble(sigma);
            WriteDouble(price);
            WriteLong(calcTimeUtc);
            WriteBytes(tickerBytes);
            WriteBytes(yieldCurveBytes);
            WriteDouble(dSpot);
            WriteDouble(dSigma);
            WriteDouble(dTime);
            WriteInt(timeSteps);
            WriteInt(spaceSteps);

            return buf;
        }

        private static double ParseReply(byte[] reply)
        {
            if (reply.Length == 0)
            {
                throw new InvalidOperationException("Julia pricing: empty reply");
            }

            byte type = reply[0];
            switch (type)
            {
                case 0x02:
                    if (reply.Length < 1 + sizeof(float))
                    {
                        throw new InvalidOperationException(
                            $"Julia pricing: short success reply ({reply.Length} bytes)");
                    }
                    return MemoryMarshal.Read<float>(reply.AsSpan(1));

                case 0x03:
                    string errorMessage = reply.Length > 1
                        ? Encoding.UTF8.GetString(reply, 1, reply.Length - 1)
                        : "(no message)";
                    throw new InvalidOperationException($"Julia pricing error: {errorMessage}");

                default:
                    throw new InvalidOperationException($"Julia pricing: unexpected type {type}");
            }
        }

        // ── Call statistics (cross-thread, cumulative) ──────────────────────
        private static readonly object _statsLock = new object();
        private static readonly long[] _statsCalls = new long[16];
        private static readonly double[] _statsTotalMs = new double[16];
        private static readonly double[] _statsMaxMs = new double[16];
        private static readonly long[] _lastCalls = new long[16];
        private static readonly double[] _lastTotalMs = new double[16];
        private static readonly double[] _intervalMaxMs = new double[16];
        private static long _lastSnapshotTicks;
        public static Action<string> Logger;
        public static double SlowCallThresholdMs = 50;

        /// <summary>The in-flight admission budget this client draws from (shared when injected).</summary>
        public PricerBudget Budget => _budget;

        /// <summary>Late replies, timeouts and malformed frames since process start.</summary>
        public (long lateReplies, long timeouts, long malformed) TransportSnapshot()
            => (Interlocked.Read(ref _lateReplies),
                Interlocked.Read(ref _timeouts),
                Interlocked.Read(ref _malformed));

        private static void RecordCall(Command command, string ticker, double elapsedMs)
        {
            int cmdIdx = (int)command;
            lock (_statsLock)
            {
                _statsCalls[cmdIdx]++;
                _statsTotalMs[cmdIdx] += elapsedMs;
                if (elapsedMs > _statsMaxMs[cmdIdx])
                {
                    _statsMaxMs[cmdIdx] = elapsedMs;
                }
                if (elapsedMs > _intervalMaxMs[cmdIdx])
                {
                    _intervalMaxMs[cmdIdx] = elapsedMs;
                }
            }
            if (Logger != null && elapsedMs >= SlowCallThresholdMs)
            {
                Logger($"JuliaPricing: cmd={command}, ticker={ticker}, ms={elapsedMs:0.0}");
            }
        }

        public static (long calls, double totalMs) Snapshot()
        {
            lock (_statsLock)
            {
                long calls = 0;
                double totalMs = 0;
                for (int i = 0; i < _statsCalls.Length; i++)
                {
                    calls += _statsCalls[i];
                    totalMs += _statsTotalMs[i];
                }
                return (calls, totalMs);
            }
        }

        public static (long calls, double totalMs) Delta(long callsBefore, double totalMsBefore)
        {
            var (calls, totalMs) = Snapshot();
            return (calls - callsBefore, totalMs - totalMsBefore);
        }

        public static void ResetStats()
        {
            lock (_statsLock)
            {
                for (int i = 0; i < _statsCalls.Length; i++)
                {
                    _statsCalls[i] = 0;
                    _statsTotalMs[i] = 0;
                    _statsMaxMs[i] = 0;
                }
            }
        }

        public static string StatsSnapshot()
        {
            lock (_statsLock)
            {
                var parts = new List<string>();
                for (int i = 0; i < _statsCalls.Length; i++)
                {
                    if (_statsCalls[i] == 0) continue;
                    double avg = _statsTotalMs[i] / _statsCalls[i];
                    parts.Add($"{(Command)i}:#{_statsCalls[i]},avg={avg:0.0}ms,max={_statsMaxMs[i]:0.0}ms,total={_statsTotalMs[i]:0}ms");
                }
                return string.Join("; ", parts);
            }
        }

        /// <summary>
        /// Per-command breakdown for the interval SINCE THE LAST CALL, with the run total in
        /// brackets: <c>IV:#123(8195) avg=17.5(19.1)ms max=193(7293)ms tot=2.15(156.71)s</c>.
        ///
        /// The cumulative form read as a per-report delta and was actively misleading: two
        /// identical lines looked like "nothing is being priced" while the run totals never
        /// distinguished an idle interval from one served entirely by the cache. A silent
        /// interval now shows #0 explicitly, and the interval's own average/max/total are the
        /// numbers to judge a slowdown by.
        /// </summary>
        public static string StatsSinceLast()
        {
            lock (_statsLock)
            {
                var now = Environment.TickCount64;
                var since = _lastSnapshotTicks == 0 ? 0d : (now - _lastSnapshotTicks) / 1000d;
                _lastSnapshotTicks = now;

                var parts = new List<string>();
                for (int i = 0; i < _statsCalls.Length; i++)
                {
                    var dCalls = _statsCalls[i] - _lastCalls[i];
                    var dTotal = _statsTotalMs[i] - _lastTotalMs[i];
                    var intervalMax = _intervalMaxMs[i];
                    _lastCalls[i] = _statsCalls[i];
                    _lastTotalMs[i] = _statsTotalMs[i];
                    _intervalMaxMs[i] = 0;
                    if (dCalls == 0 && _statsCalls[i] == 0) continue;
                    var avg = dCalls > 0 ? dTotal / dCalls : 0d;
                    var runAvg = _statsCalls[i] > 0 ? _statsTotalMs[i] / _statsCalls[i] : 0d;
                    parts.Add($"{(Command)i}:#{dCalls}({_statsCalls[i]}) " +
                              $"avg={avg:0.0}({runAvg:0.0})ms max={intervalMax:0.0}({_statsMaxMs[i]:0.0})ms " +
                              $"tot={dTotal / 1000d:0.00}({_statsTotalMs[i] / 1000d:0.00})s");
                }
                return $"[{since:0.0}s since last] " +
                       (parts.Count > 0 ? string.Join("; ", parts) : "no pricing calls");
            }
        }

        // ── Core greeks ──────────────────────────────────────────────────────────

        public double Price(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Price, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <param name="price">Market price of the option (used to back out IV).</param>
        public double IV(bool isCall, double s, double k, double t,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(price) ? double.NaN : Send(Command.IV, isCall, s, k, t, sigma: 0.0, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Delta(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Delta, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Vega(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Vega, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Theta(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Theta, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        public double Gamma(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Gamma, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        // ── Higher-order greeks ───────────────────────────────────────────────────

        /// <summary>dGamma/dS — third derivative of price w.r.t. spot.</summary>
        public double Speed(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Speed, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dGamma/dt — decay of gamma over time.</summary>
        public double GammaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.GammaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dGamma/dSigma — sensitivity of gamma to volatility.</summary>
        public double GammaVol(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.GammaVol, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dTheta/dt — decay of theta over time.</summary>
        public double ThetaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.ThetaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dVega/dt — decay of vega over time (Veta).</summary>
        public double VegaDecay(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.VegaDecay, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dDelta/dSigma — sensitivity of delta to volatility.</summary>
        public double Vanna(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Vanna, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);

        /// <summary>dVega/dSigma — sensitivity of vega to volatility (Vomma).</summary>
        public double Volga(bool isCall, double s, double k, double t, double sigma,
            double price, long calcTimeUtc, string ticker, string yieldCurve = "YC",
            double dSpot = 0.0, double dSigma = 0.0, double dTime = 0.0,
            int timeSteps = 0, int spaceSteps = 0)
            => double.IsNaN(sigma) ? double.NaN : Send(Command.Volga, isCall, s, k, t, sigma, price, calcTimeUtc, ticker, yieldCurve,
                    dSpot, dSigma, dTime, timeSteps, spaceSteps);


        /// <summary>Moneyness forward.</summary>
        public double MnyFwd(double s, double k, double t, long calcTimeUtc, string ticker, string yieldCurve = "YC")
            => Send(Command.MnyFwd, false, s, k, t, 0, 0, calcTimeUtc, ticker, yieldCurve);



        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            foreach (var kv in _pending)
            {
                kv.Value.TrySetException(new ObjectDisposedException(nameof(JuliaPricingClient)));
            }
            _pending.Clear();

            try { _ioThread.Join(500); } catch { /* shutting down */ }
            _socket.Dispose();
            if (_ownsBudget) _budget.Dispose();
        }
    }
}
