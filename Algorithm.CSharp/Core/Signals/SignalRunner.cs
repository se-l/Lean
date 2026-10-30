using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// One option captured ON THE ALGORITHM THREAD: the security reference plus every decision the
    /// utility needs from live state (side gates, quantities, the prices used as its input).
    ///
    /// This is what makes an off-thread pass safe. The worker may read numbers (prices, IVs) and
    /// build utilities, but it must never enumerate <c>Securities</c>, evaluate a regime gate, or
    /// scan order tickets — those are the calls that race with <c>OnData</c> adding contracts.
    /// </summary>
    public readonly struct ScopedOption
    {
        public readonly Option Option;
        public readonly Symbol Symbol;
        public readonly bool SellAllowed;
        public readonly bool BuyAllowed;
        public readonly decimal QuantitySell;
        public readonly decimal QuantityBuy;
        public readonly decimal AskPrice;
        public readonly decimal BidPrice;

        public ScopedOption(Option option, bool sellAllowed, bool buyAllowed,
                            decimal quantitySell, decimal quantityBuy)
        {
            Option = option;
            Symbol = option.Symbol;
            SellAllowed = sellAllowed;
            BuyAllowed = buyAllowed;
            QuantitySell = quantitySell;
            QuantityBuy = quantityBuy;
            AskPrice = option.AskPrice;
            BidPrice = option.BidPrice;
        }
    }

    /// <summary>
    /// The product of one pass: signals computed against a captured scope, plus what the main
    /// thread needs to log, measure and decide whether to apply them.
    /// </summary>
    public sealed class SignalBatch
    {
        public Symbol Underlying;
        public List<Signal> Signals = new();
        public DateTime CapturedAt;     // algorithm time when the scope was taken
        public long CapturedTicks;      // Stopwatch timestamp at capture (staleness clock)
        public double ComputeMs;
        public double SerialMs;         // sum of per-option times = what a serial loop would have cost
        public int OptionCount;
        public int Parallelism;
        public int Errors;              // contracts whose utility threw (dropped, pass continues)
        public string ErrorSample;      // first few messages, for the apply-time log
        public Exception Error;

        public double AgeMs => (Stopwatch.GetTimestamp() - CapturedTicks) * 1000.0 / Stopwatch.Frequency;
        public double Speedup => SerialMs / Math.Max(1.0, ComputeMs);
    }

    /// <summary>
    /// Runs signal passes off the algorithm thread, one worker per underlying, at most one pass
    /// in flight per underlying.
    ///
    /// Lean has no coroutine model: the algorithm thread never awaits. It captures the scope,
    /// launches, and drains completed batches from <see cref="OnData"/> — the worker is a plain
    /// background thread and the only shared state is a ConcurrentQueue plus the gate.
    ///
    /// The gate is released by the APPLY step, not by the worker: computing twice is harmless,
    /// applying twice can double-submit.
    /// </summary>
    public sealed class SignalRunner : IDisposable
    {
        private sealed class Worker
        {
            public readonly Symbol Underlying;
            public readonly BlockingCollection<Func<SignalBatch>> Jobs =
                new(new ConcurrentQueue<Func<SignalBatch>>(), 4);

            public Worker(Symbol underlying) => Underlying = underlying;
        }

        private readonly ConcurrentDictionary<Symbol, byte> _running = new();
        private readonly ConcurrentDictionary<Symbol, Worker> _workers = new();
        private readonly ConcurrentQueue<SignalBatch> _completed = new();
        private long _skippedRunning;
        private int _disposed;

        /// <summary>Gate hits: a launch was refused because a pass is already in flight.</summary>
        public long SkippedRunning => Interlocked.Read(ref _skippedRunning);

        public bool IsRunning(Symbol underlying) => _running.ContainsKey(underlying);

        /// <summary>True when this call took the per-underlying slot.</summary>
        public bool TryAcquire(Symbol underlying) => _running.TryAdd(underlying, 0) == true;

        /// <summary>Releases the slot; called by the apply step (or on a failed launch).</summary>
        public void Release(Symbol underlying) => _running.TryRemove(underlying, out _);

        internal void CountSkipped() => Interlocked.Increment(ref _skippedRunning);

        /// <summary>Hands a pass to the underlying's worker. Never blocks the caller.</summary>
        public bool Launch(Symbol underlying, Func<SignalBatch> compute)
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var worker = _workers.GetOrAdd(underlying, CreateWorker);
            try
            {
                worker.Jobs.Add(compute);
                return true;
            }
            catch (InvalidOperationException)
            {
                // Completed (shutdown race). The caller releases the gate it took for this launch.
                return false;
            }
        }

        public bool TryDequeueCompleted(out SignalBatch batch) => _completed.TryDequeue(out batch);

        public int QueueDepth => _completed.Count;

        private Worker CreateWorker(Symbol underlying)
        {
            var worker = new Worker(underlying);
            var thread = new Thread(() => Loop(worker))
            {
                IsBackground = true,
                Name = "signals-" + underlying.Value
            };
            thread.Start();
            return worker;
        }

        private void Loop(Worker worker)
        {
            try
            {
                foreach (var compute in worker.Jobs.GetConsumingEnumerable())
                {
                    SignalBatch batch;
                    try
                    {
                        batch = compute();
                    }
                    catch (Exception ex)
                    {
                        // A failed pass must never take the process down; it is reported at apply.
                        batch = new SignalBatch
                        {
                            Underlying = worker.Underlying,
                            CapturedTicks = Stopwatch.GetTimestamp(),
                            Error = ex
                        };
                    }
                    _completed.Enqueue(batch);
                }
            }
            catch (Exception)
            {
                // Completing/shutting down.
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var worker in _workers.Values)
            {
                try { worker.Jobs.CompleteAdding(); } catch { }
            }
        }
    }
}
