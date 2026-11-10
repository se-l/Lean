using System;
using System.Collections.Concurrent;
using System.Threading;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations
    {
        /// <summary>
        /// Key of a reusable utility order. The utility of one order depends on the contract, its signed
        /// quantity, the market price it would trade at, the surface IV and the portfolio state — nothing
        /// else. Bucketing the two continuous inputs and signing the portfolio is what turns "rebuild on
        /// every quote" (the cost that put 95% of Delta calls in `Other`) into a dictionary hit.
        /// </summary>
        public sealed record UtilityCacheKey(Symbol Symbol, int Quantity, long SpotBucket, long IvBucket,
            long PortfolioSignature, long TimeBucket, long PriceBucket);

        private readonly ConcurrentDictionary<UtilityCacheKey, IUtilityOrder> _utilityCache = new();
        private long _utilityCacheHits;
        private long _utilityCacheMisses;

        /// <summary>
        /// Create (or reuse) the utility order for one contract/quantity/price.
        ///
        /// Everything downstream — quote pricing, target-holding sweeps, the risk checks — asks for the
        /// same few hundred (contract, price) combinations over and over while spot barely moves. The
        /// utility order is expensive (greeks, portfolio risk, capital cost), so it is cached against a
        /// spot grid (`Cfg.UtilityCacheSpotBucketBps`), an IV grid (`Cfg.UtilityCacheIvBucket`), the
        /// option price in ticks, a portfolio signature (positions + target holdings + marginal utilities)
        /// and a one-minute time bucket.
        /// </summary>
        public IUtilityOrder CreateUtility(Option option, decimal quantity, decimal? price = null)
        {
            if (!Cfg.UtilityCacheEnabled || option == null || quantity == 0)
            {
                return UtilityOrderFactory.Create(this, option, quantity, price);
            }

            Symbol symbol = option.Symbol;
            decimal spot = MidPrice(option.Underlying.Symbol);
            double iv = MidIVSsvi(symbol);
            if (spot <= 0 || !double.IsFinite(iv) || iv <= 0)
            {
                // No usable node: do not cache on a degenerate key.
                return UtilityOrderFactory.Create(this, option, quantity, price);
            }

            double width = Math.Max(Cfg.UtilityCacheSpotBucketBps, 0.1) * 1e-4;
            long spotBucket = (long)Math.Floor(Math.Log((double)spot) / Math.Log(1.0 + width));
            long ivBucket = (long)Math.Floor(iv / Math.Max(Cfg.UtilityCacheIvBucket, 1e-4));
            long priceBucket = price.HasValue
                ? (long)Math.Round(price.Value / Math.Max(TickSize(symbol), 0.0001m))
                : 0;

            var key = new UtilityCacheKey(symbol, (int)quantity, spotBucket, ivBucket,
                UtilityPortfolioSignature(option.Underlying.Symbol),
                Time.Ticks / TimeSpan.TicksPerMinute, priceBucket);

            if (_utilityCache.TryGetValue(key, out IUtilityOrder cached))
            {
                Interlocked.Increment(ref _utilityCacheHits);
                return cached;
            }

            IUtilityOrder created = UtilityOrderFactory.Create(this, option, quantity, price);
            if (created == null)
            {
                return null;
            }

            Interlocked.Increment(ref _utilityCacheMisses);
            if (_utilityCache.Count >= Math.Max(1000, Cfg.UtilityCacheMaxKeys))
            {
                _utilityCache.Clear();
            }
            _utilityCache[key] = created;
            return created;
        }

        /// <summary>
        /// Cheap signature of the state a utility depends on besides price/IV: the underlying's own
        /// positions, the target holdings for it, and whether the marginal-utility map changed. O(open
        /// positions), which is tens of entries — dwarfed by the greek fan-out it guards.
        /// </summary>
        private long UtilityPortfolioSignature(Symbol underlying)
        {
            long hash = 17;
            foreach (var kvp in Portfolio)
            {
                if (kvp.Value.Quantity == 0) continue;
                if (Underlying(kvp.Key) != underlying) continue;
                hash = hash * 31 + kvp.Key.GetHashCode();
                hash = hash * 31 + (long)kvp.Value.Quantity;
            }
            foreach (var kvp in TargetHoldings)
            {
                if (Underlying(kvp.Key) != underlying) continue;
                hash = hash * 31 + kvp.Key.GetHashCode();
                hash = hash * 31 + (long)kvp.Value;
            }
            hash = hash * 31 + MarginalUtility.Count;
            return hash;
        }

        /// <summary>Hit/miss counters for the last interval, formatted for the signal-batch log.</summary>
        public string UtilityCacheStatsSinceLast()
        {
            long hits = Interlocked.Exchange(ref _utilityCacheHits, 0);
            long misses = Interlocked.Exchange(ref _utilityCacheMisses, 0);
            double total = hits + misses;
            return $"hit={hits} miss={misses} hitRate={(total > 0 ? hits / total : 0):0.0%} keys={_utilityCache.Count}";
        }
    }
}
