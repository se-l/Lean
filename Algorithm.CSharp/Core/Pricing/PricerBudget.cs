using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Who is asking for a pricer slot. Every call site uses <see cref="Normal"/> today, so the
    /// budget admits strictly FIFO; the enum exists so admission can become priority-aware later
    /// without touching a single caller.
    /// </summary>
    public enum PricerPriority
    {
        Normal = 0,
        Batch = 1,
        Background = 2
    }

    /// <summary>
    /// Process-wide admission control for the ZMQ pricer.
    ///
    /// It bounds requests IN FLIGHT, not tasks. One task may offer as many requests as it likes —
    /// the budget is the only thing keeping the total at the pricer's worker count. That is what
    /// keeps the Julia-side queue shallow, so a request arriving during a batch waits ~one solve
    /// instead of `queue_depth / workers` solves.
    ///
    /// Capacity: <see cref="DefaultCapacity"/> is this machine's CPU count, which equals the
    /// pricer's `julia -t auto` worker count when both run on the same host. Set
    /// <c>PricerConcurrency</c> explicitly when the pricer is a container with its own -t, or on
    /// another host. Measured on the dev container (32 threads, IV, closed loop):
    /// <code>
    ///   W:    4     8     16    32    48    64
    ///   /s:   216   374   603   892   1265  1434      (p50 flat ~14 ms through W=32)
    /// </code>
    /// Underfeeding is the expensive mistake — a cap of 5 idles ~27 of 32 workers. Overfeeding
    /// only adds queue depth; past ~64 the single-socket client, not the pricer, is the ceiling.
    ///
    /// Priority is recorded per class and emitted as a metric, but admission is FIFO while every
    /// caller passes <see cref="PricerPriority.Normal"/>. When priorities become real, the waiter
    /// list replaces the semaphore behind <see cref="AcquireAsync"/> — same API, no caller changes.
    /// </summary>
    public sealed class PricerBudget : IDisposable
    {
        private const int PriorityCount = 3;

        /// <summary>See the type remarks: the pricer's worker count, approximated by CPUs here.</summary>
        public static int DefaultCapacity => Math.Max(1, Environment.ProcessorCount);

        /// <summary>
        /// Sink for the budget's gauges, wired once by the algorithm
        /// (<c>PricerBudget.MetricsSink = OpenTelemetryLogHandler.Metric</c>). Kept as a delegate —
        /// same pattern as <see cref="JuliaPricingClient.Logger"/> — so this type has no dependency
        /// on the logging assembly and stays unit-testable.
        /// </summary>
        public static Action<string, double, KeyValuePair<string, object>[]> MetricsSink;

        public int Capacity { get; }

        public int InFlight => Volatile.Read(ref _inFlight);
        public int Waiting => Volatile.Read(ref _waiting);
        public long Acquired => Interlocked.Read(ref _acquired);
        public long Rejected => Interlocked.Read(ref _rejected);

        private readonly SemaphoreSlim _slots;
        private readonly object _metricsGate = new object();
        private readonly long[] _acquiredByPriority = new long[PriorityCount];
        private readonly long[] _waitTicksWindow = new long[PriorityCount];
        private readonly long[] _waitCountWindow = new long[PriorityCount];

        private int _inFlight;
        private int _waiting;
        private int _maxInFlightWindow;
        private long _acquired;
        private long _rejected;
        private long _lastMetricsTicks;
        private int _disposed;

        public PricerBudget(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            _slots = new SemaphoreSlim(capacity, capacity);
        }

        /// <summary>
        /// Takes one slot. Throws <see cref="TimeoutException"/> when no slot frees up within
        /// <paramref name="timeout"/> — the caller is then responsible for surfacing that as a
        /// pricing failure rather than blocking the algorithm thread forever.
        /// </summary>
        public async Task<IDisposable> AcquireAsync(PricerPriority priority, TimeSpan timeout)
        {
            if (Volatile.Read(ref _disposed) != 0) throw new ObjectDisposedException(nameof(PricerBudget));

            int cls = (int)priority;
            Interlocked.Increment(ref _waiting);
            long t0 = Stopwatch.GetTimestamp();

            bool acquired;
            try
            {
                acquired = await _slots.WaitAsync(timeout).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiting);
            }

            long waited = Stopwatch.GetTimestamp() - t0;
            if (!acquired)
            {
                Interlocked.Increment(ref _rejected);
                EmitMetricsIfDue();
                throw new TimeoutException(
                    $"PricerBudget: no request slot within {timeout.TotalSeconds:0}s " +
                    $"(capacity={Capacity}, in-flight={InFlight}, waiting={Waiting})");
            }

            Interlocked.Add(ref _waitTicksWindow[cls], waited);
            Interlocked.Increment(ref _waitCountWindow[cls]);
            Interlocked.Increment(ref _acquiredByPriority[cls]);
            Interlocked.Increment(ref _acquired);

            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlightWindow)) &&
                   Interlocked.CompareExchange(ref _maxInFlightWindow, now, seen) != seen)
            {
            }

            EmitMetricsIfDue();
            return new Lease(this);
        }

        /// <summary>Aggregates for logs / tests (the OTel emission happens inside AcquireAsync).</summary>
        public (int capacity, int inFlight, int waiting, long acquired, long rejected) Snapshot()
            => (Capacity, InFlight, Waiting, Acquired, Rejected);

        private void Release()
        {
            Interlocked.Decrement(ref _inFlight);
            try { _slots.Release(); } catch (ObjectDisposedException) { /* shutting down */ }
            EmitMetricsIfDue();
        }

        /// <summary>
        /// At most one emission per second, from whichever acquire/release happens to cross the
        /// boundary. No timer thread, and it cannot drift with the caller's rate.
        /// </summary>
        private void EmitMetricsIfDue()
        {
            long now = Stopwatch.GetTimestamp();
            long last = Volatile.Read(ref _lastMetricsTicks);
            if (now - last < Stopwatch.Frequency) return;
            if (Interlocked.CompareExchange(ref _lastMetricsTicks, now, last) != last) return;

            long[] waitTicks = new long[PriorityCount];
            long[] waitCount = new long[PriorityCount];
            long[] acquired = new long[PriorityCount];
            int maxInFlight;
            long rejected;
            int inFlight, waiting;

            lock (_metricsGate)
            {
                for (int i = 0; i < PriorityCount; i++)
                {
                    waitTicks[i] = _waitTicksWindow[i];
                    waitCount[i] = _waitCountWindow[i];
                    acquired[i] = _acquiredByPriority[i];
                    _waitTicksWindow[i] = 0;
                    _waitCountWindow[i] = 0;
                }
                maxInFlight = _maxInFlightWindow;
                _maxInFlightWindow = 0;
                rejected = _rejected;
                inFlight = _inFlight;
                waiting = _waiting;
            }

            Emit("lean.pricer.budget.capacity", Capacity, null);
            Emit("lean.pricer.budget.inflight", inFlight, null);
            Emit("lean.pricer.budget.waiting", waiting, null);
            Emit("lean.pricer.budget.max_inflight", maxInFlight, null);
            Emit("lean.pricer.budget.rejected", rejected, null);

            for (int i = 0; i < PriorityCount; i++)
            {
                string cls = ((PricerPriority)i).ToString();
                var attributes = new[] { new KeyValuePair<string, object>("priority", cls) };
                double avgWaitMs = waitCount[i] == 0
                    ? 0
                    : waitTicks[i] * 1000.0 / Stopwatch.Frequency / waitCount[i];

                Emit("lean.pricer.budget.wait_ms", avgWaitMs, attributes);
                Emit("lean.pricer.budget.acquired", acquired[i], attributes);
            }
        }

        private static void Emit(string name, double value, KeyValuePair<string, object>[] attributes)
            => MetricsSink?.Invoke(name, value, attributes);

        /// <summary>
        /// Marks the budget closed. The semaphore itself is deliberately NOT disposed: in-flight
        /// callers still release it, and a queued waiter may be waking up — disposing it turns
        /// shutdown into a stream of ObjectDisposedException from continuations. It holds no
        /// unmanaged WaitHandle unless AvailableWaitHandle is touched, which this type never does.
        /// </summary>
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

        private sealed class Lease : IDisposable
        {
            private PricerBudget _budget;

            public Lease(PricerBudget budget) => _budget = budget;

            public void Dispose()
            {
                var budget = Interlocked.Exchange(ref _budget, null);
                budget?.Release();
            }
        }
    }
}
