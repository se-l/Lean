using System;
using System.Collections.Concurrent;
using NodaTime;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Configuration;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    /// <summary>
    /// Limit-fill model priced off the contract's vega instead of its traded volume.
    ///
    /// pFill = 1 - exp(-vega * VegaWeight * spreadTerm)
    ///
    /// The immediate fill (the base class) stays the first stage; this only assigns the
    /// additional fills a quote-driven probability. Vega replaces both the volume term and the
    /// liquidity weight of FillModelVolumeWeighted: an option's hedging demand, not its traded
    /// volume, is what gets a resting order filled.
    ///
    /// Vega is computed lazily, only when a limit fill is actually evaluated, from the CURRENT bar
    /// (the option's live quote through OptionContractWrap) and then cached against that bar's
    /// timestamp: repeated fill checks inside one bar reuse it, the next bar recomputes. A contract
    /// with no vega had no quote to price it — that is a no-fill (pFill = 0), never a silent default.
    /// </summary>
    public class FillModelVega : FillModelMine
    {
        private readonly Foundations _algo;
        private readonly Random _random;
        private readonly double _vegaWeight;
        private readonly double _pFillSpreadExp;
        private readonly ConcurrentDictionary<(Symbol Symbol, DateTime BarTime), double> _vegaByBar = new();

        public FillModelVega(Foundations algo,
            double vegaWeight = 0.1,
            double pFillSpreadExp = 3.0,
            int? randomSeed = null)
        {
            _algo = algo ?? throw new ArgumentNullException(nameof(algo));
            _vegaWeight = Math.Max(0d, vegaWeight);
            _pFillSpreadExp = pFillSpreadExp;
            _random = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
        }

        /// <summary>
        /// Config-driven construction: <c>fill-model-vega-weight</c> (default 1.0) and
        /// <c>fill-model-vega-spread-exp</c> (default 3.0). Separate from the constructor so the
        /// call is not ambiguous with the explicit-weights overload.
        /// </summary>
        public static FillModelVega FromConfig(Foundations algo, int? randomSeed = null) =>
            new (algo,
                Config.GetDouble("fill-model-vega-weight", 0.1),
                Config.GetDouble("fill-model-vega-spread-exp", 3.0),
                randomSeed);

        protected override OrderEvent InternalLimitFill(Security asset, Order order, decimal limitPrice, decimal quantity)
        {
            OrderEvent fill = base.InternalLimitFill(asset, order, limitPrice, quantity);
            if (fill.Status == OrderStatus.Filled)
            {
                return fill;
            }

            decimal spread = asset.AskPrice - asset.BidPrice;
            if (spread <= 0)
            {
                return fill;
            }

            var rawSpreadCrossedPc = (double)(order.Direction == OrderDirection.Buy
                ? (limitPrice - asset.BidPrice) / spread
                : (asset.AskPrice - limitPrice) / spread);
            double spreadCrossedPc = Math.Clamp(rawSpreadCrossedPc, 0d, 1d);
            if (spreadCrossedPc <= 0)
            {
                return fill;
            }

            double vega = CurrentBarVega(asset);
            if (!(vega > 0d))
            {
                // No vega for this bar (no quote to price it) -> no probabilistic fill. Deliberately
                // not a default weight: a contract that cannot be priced should not trade.
                return fill;
            }

            double spreadTerm = Math.Pow(spreadCrossedPc, Math.Max(0d, _pFillSpreadExp));
            double lambda = vega * _vegaWeight * spreadTerm;
            double pFill = 1d - Math.Exp(-Math.Max(0d, lambda));

            if (_random.NextDouble() < pFill)
            {
                DateTime localTime = fill.UtcTime.ConvertTo(DateTimeZone.Utc, asset.Exchange.TimeZone);
                Logging.Log.Trace($"{localTime} FillModelVega: Filled quantity={quantity}, symbol={order.Symbol}, " +
                    $"bid={asset.BidPrice}, fillPrice={limitPrice}, ask={asset.AskPrice}, " +
                    $"spreadCrossedPc={spreadCrossedPc:0.00}, pFill={pFill:0.0000}, " +
                    $"spreadTerm={spreadTerm:0.000}, vega={vega:0.0000}, weight={_vegaWeight:0.000}");

                fill.Status = OrderStatus.Filled;
                fill.FillPrice = limitPrice;
                fill.FillQuantity = quantity;
            }

            return fill;
        }

        /// <summary>
        /// Vega for the current bar, computed once per (contract, bar) and cached.
        ///
        /// Lazy on purpose: nothing is built at subscription time. The first limit fill evaluated in
        /// a bar pays for the vega from that bar's live quote (OptionContractWrap, the same wrap the
        /// rest of the algorithm prices with); every later fill check in the same bar reuses it.
        /// </summary>
        private double CurrentBarVega(Security asset)
        {
            var barTime = asset.LocalTime;
            var key = (asset.Symbol, barTime);
            if (_vegaByBar.TryGetValue(key, out var cached))
            {
                return cached;
            }

            double vega = 0d;
            try
            {
                if (asset is Option option)
                {
                    var wrap = OptionContractWrap.E(_algo, option, _algo.Time.Date);
                    var iv = _algo.MidIV(asset.Symbol);
                    vega = wrap.Vega(iv);
                    if (!double.IsFinite(vega))
                    {
                        vega = 0d;
                    }
                }
            }
            catch (Exception error)
            {
                // A contract that cannot be priced is a no-fill; one line per contract per bar.
                Logging.Log.Trace($"{barTime:yyyy-MM-dd HH:mm:ss} FillModelVega: no vega for {asset.Symbol} " +
                                  $"({error.GetType().Name}: {error.Message}) — no probabilistic fill this bar.");
                vega = 0d;
            }

            PruneOldBars(barTime);
            _vegaByBar[key] = vega;
            return vega;
        }

        /// <summary>Keeps the per-bar cache from growing across days: drop anything older than a day.</summary>
        private void PruneOldBars(DateTime barTime)
        {
            if (_vegaByBar.Count < 50_000)
            {
                return;
            }

            var cutoff = barTime.AddDays(-1);
            foreach (var entry in _vegaByBar)
            {
                if (entry.Key.BarTime < cutoff)
                {
                    _vegaByBar.TryRemove(entry.Key, out _);
                }
            }
        }
    }
}
