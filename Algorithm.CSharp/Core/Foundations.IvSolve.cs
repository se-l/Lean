using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// Off-thread IV inversion for live quote updates.
    ///
    /// Every option quote used to invert IV twice (bid and ask) ON the algorithm thread:
    /// `OnNewBidAskEventUpdateLimitPrices`. At ~50 solves/s and ~15 ms each that is ~750 ms of
    /// algorithm-thread time per wall second, and a quote burst blocks the slice outright — which
    /// is what starved the signal drain and made batches look seconds old.
    ///
    /// The inversion is a pure function of (quote, underlying mid, contract, date), so it moves to
    /// one worker. The main thread keeps everything that mutates state: indicator fields, the IV
    /// spread SMA, and limit prices.
    ///
    /// Only the PRICER fallback moves. The pricer-cache and feed-IV lookups stay on the main
    /// thread because they are memory reads/writes on shared maps, not round trips.
    /// </summary>
    public partial class Foundations
    {
        /// <summary>Solve quote IVs off the algorithm thread. Live only; backtest stays exact.</summary>
        public bool IvSolveAsync;

        private sealed class IvSolveJob
        {
            public Symbol Symbol;
            public DateTime Time;
            public decimal Bid, Ask, MidUnderlying;
            public Option Option;
        }

        private sealed class IvSolveResult
        {
            public Symbol Symbol;
            public DateTime Time;
            public decimal Bid, Ask, MidUnderlying;
            public double BidIV = double.NaN, AskIV = double.NaN;
        }

        // Latest quote per symbol wins: a burst of 20 quotes for one contract costs one solve pair
        // when the worker gets there, not 20. Symbols are independent, so the map is per contract.
        private readonly ConcurrentDictionary<Symbol, IvSolveJob> _ivPending = new();
        private readonly ConcurrentQueue<IvSolveResult> _ivDone = new();
        private readonly SemaphoreSlim _ivSignal = new(0);
        private Thread _ivThread;
        private volatile bool _ivStop;

        private void StartIvSolver()
        {
            if (!IvSolveAsync || _ivThread != null) return;
            _ivThread = new Thread(IvSolveLoop)
            {
                IsBackground = true,
                Name = "IvSolver"
            };
            _ivThread.Start();
        }

        private void StopIvSolver()
        {
            _ivStop = true;
            try { _ivSignal.Release(); } catch { }
            try { _ivThread?.Join(500); } catch { }
            _ivThread = null;
        }
        
        private void IvSolveLoop()
        {
            while (!_ivStop)
            {
                try { _ivSignal.Wait(50); } catch { return; }

                foreach (Symbol symbol in new List<Symbol>(_ivPending.Keys))
                {
                    if (!_ivPending.TryRemove(symbol, out IvSolveJob job)) continue;

                    var result = new IvSolveResult
                    {
                        Symbol = symbol,
                        Time = job.Time,
                        Bid = job.Bid,
                        Ask = job.Ask,
                        MidUnderlying = job.MidUnderlying
                    };

                    try
                    {
                        using (Pricing.PricingCallSite.Scope("IVQuoteAsync"))
                        {
                            var wrap = Pricing.OptionContractWrap.E(this, job.Option, job.Time.Date);
                            if (job.Bid != 0m) result.BidIV = wrap.IV(job.Bid, job.MidUnderlying, 0.001);
                            if (job.Ask != 0m) result.AskIV = wrap.IV(job.Ask, job.MidUnderlying, 0.001);
                        }
                    }
                    catch (Exception ex)
                    {
                        // A failed inversion must not kill the worker; the next quote retries.
                        Log($"{Time} IvSolveAsync: {symbol} failed: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    _ivDone.Enqueue(result);
                }
            }
        }
    }
}
