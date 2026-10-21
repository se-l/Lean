using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Who asked for the price.
    ///
    /// The pricer round-trip stats say WHAT was calculated (Delta:#…, IV:#…) and the cache stats
    /// say whether the book served it, but neither says which algorithm path drove the call. With
    /// ~8k IV solves per bar the difference matters: the utility loop builds two utilities per
    /// scoped option, the quote indicator solves one IV per contract per quote bar, and risk /
    /// order logging add more. This thread-static tag lets <see cref="OptionContractWrap"/> count
    /// every IV/greek solve by caller; <see cref="StatsSinceLast"/> prints the interval delta with
    /// the run total in brackets, next to the other two lines.
    ///
    /// Cost: one lock + dictionary bump per solve (~10k/bar), negligible against the solve itself.
    /// </summary>
    public static class PricingCallSite
    {
        [ThreadStatic] private static string _phase;

        private static readonly object Gate = new();
        private static readonly Dictionary<string, Dictionary<string, long>> Counts = new();
        private static readonly Dictionary<string, Dictionary<string, long>> Last = new();

        /// <summary>Current caller tag; untagged work reports as <c>Other</c>.</summary>
        public static string Phase => _phase ?? "Other";

        /// <summary>Tags every solve on this thread until the returned scope is disposed.</summary>
        public static IDisposable Scope(string phase)
        {
            var previous = _phase;
            _phase = phase;
            return new Restore(previous);
        }

        /// <summary>Counts one solve of <paramref name="kind"/> for the current caller tag.</summary>
        public static void Count(string kind)
        {
            var phase = Phase;
            lock (Gate)
            {
                if (!Counts.TryGetValue(phase, out var map))
                {
                    Counts[phase] = map = new Dictionary<string, long>();
                }
                map.TryGetValue(kind, out var n);
                map[kind] = n + 1;
            }
        }

        /// <summary>
        /// <c>UtilityLoop IV:#123(8195) Delta:#40(1639) | IVQuote IV:#80(…) | Other …</c> —
        /// interval delta first, run total in brackets, per caller and per kind.
        /// </summary>
        public static string StatsSinceLast()
        {
            lock (Gate)
            {
                var parts = new List<string>();
                foreach (var phaseEntry in Counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!Last.TryGetValue(phaseEntry.Key, out var previous))
                    {
                        Last[phaseEntry.Key] = previous = new Dictionary<string, long>();
                    }

                    var bits = new List<string>();
                    foreach (var kindEntry in phaseEntry.Value.OrderBy(k => k.Key, StringComparer.Ordinal))
                    {
                        previous.TryGetValue(kindEntry.Key, out var before);
                        var delta = kindEntry.Value - before;
                        if (delta != 0 || kindEntry.Value != 0)
                        {
                            bits.Add($"{kindEntry.Key}:#{delta}({kindEntry.Value})");
                        }
                    }
                    foreach (var kindEntry in phaseEntry.Value)
                    {
                        previous[kindEntry.Key] = kindEntry.Value;
                    }
                    if (bits.Count > 0)
                    {
                        parts.Add($"{phaseEntry.Key} {string.Join(" ", bits)}");
                    }
                }
                return parts.Count > 0
                    ? string.Join(" | ", parts)
                    : "no pricing calls in this interval";
            }
        }

        /// <summary>Restores the previous tag when the scope ends (nesting-safe).</summary>
        private sealed class Restore : IDisposable
        {
            private readonly string _previous;

            public Restore(string previous) => _previous = previous;

            public void Dispose() => _phase = _previous;
        }
    }
}
