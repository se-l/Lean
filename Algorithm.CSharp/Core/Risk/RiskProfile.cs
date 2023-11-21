using Accord.Math;
using QuantConnect.Securities.Equity;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class RiskProfile : Disposable
    {
        // CSV export append each time on call. Implement Dispose here. Initialized in Initialize() method.
        // Given not using the CSVExport method, no need to implement each metric as attribute. Underlying Pct change just a parameter.        
        public readonly Equity Equity;
        public Symbol Symbol { get => Equity.Symbol; }
        private bool _headerWritten;
        private List<string> _header;
        private readonly string _path;

        //public Dictionary<double, decimal> PnLProfile = Enumerable.Range(-10, 21).Select(i => (double)i).ToDictionary(i => i, i => 0m);
        private readonly HashSet<Metric> dfltMetricsDIV = new() { Metric.Vega, Metric.Vanna, Metric.Volga };
        private readonly HashSet<Metric> dfltMetricsDs = new() {Metric.Delta, Metric.Gamma, Metric.DeltaIVdS };//, Metric.Speed
        //private HashSet<Metric> metrics = metricsDs.Union(metricsDIV).ToList();
        private Dictionary<Metric, Func<IEnumerable<Position>, double, decimal>> Metric2Function = new()
        {
            { Metric.Delta, (positions, pctChange) => positions.Sum(p => p.DeltaXBpUSDTotal(pctChange * 100)) },
            { Metric.Gamma, (positions, pctChange) => positions.Sum(p => p.GammaXBpUSDTotal(pctChange * 100)) },
            { Metric.Speed, (positions, pctChange) => positions.Sum(p => p.SpeedXBpUSDTotal(pctChange * 100)) },
            { Metric.DeltaIVdS, (positions, pctChange) => positions.Sum(p => p.DeltaIVdSXBpUSDTotal((decimal)pctChange * 100)) },

            { Metric.Vega, (positions, pctChange) => positions.Sum(p => p.VegaXBpUSDTotal(pctChange * 100)) },
            { Metric.Volga, (positions, pctChange) => positions.Sum(p => p.VolgaXBpUSDTotal(pctChange * 100)) },
            { Metric.Vanna, (positions, pctChange) => positions.Sum(p => p.VannaXBpUSDTotal((decimal)pctChange * 100)) },
        };
        private DateTime timeLastStressed;
        private List<Position> position0Cache;
        private decimal stressDsPlus0Cache;
        private decimal stressDsMinus0Cache;
        private Dictionary<Tuple<Metric, decimal, decimal>, decimal> CachedMetric2F = new();

        public RiskProfile(Foundations algo, Equity equity)
        {
            _algo = algo;
            Equity = equity;

            _path = Path.Combine(Globals.PathAnalytics, Symbol.Value, "RiskProfile.csv");
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new(_path, false)
            {
                AutoFlush = true
            };
        }

        public bool WriteHeader()
        {
            if (_headerWritten) { return true; }

            var positions = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol).ToList();
            if (positions.Select(p => p.SecurityType).Contains(SecurityType.Option) && positions.Select(p => p.SecurityType).Contains(SecurityType.Equity))
            {
                _header = positions.SelectMany(x => ObjectsToHeaderNames(x)).Distinct().OrderBy(x => x).ToList();
                _writer.WriteLine(string.Join(",", _header));
                _headerWritten = true;
            }
            return _headerWritten;
        }

        public void Update()
        {
            if (!WriteHeader()) { return; }

            var positions = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol && x.Quantity != 0);
            if (positions.Any())
            {
                _writer.Write(ToCsv(positions, _header, skipHeader: true));
            }
            CachedMetric2F.Clear();
        }

        /// <summary>
        /// Rough estimate of the marginal contribution of a trade to the overall margin reqirement. Put differently, how much risk is added by the trade.
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="quantity"></param>
        /// <returns></returns>
        public decimal WhatIfMarginAdd(Symbol symbol, decimal quantity, double dSPct = 5, double dIVPct = 0)
        {
            Trade trade = new(_algo, symbol, quantity, _algo.MidPrice(symbol));
            Position position = new(_algo, trade);

            // Mathmatically - only single trade contribution matters.
            if (_algo.Time != timeLastStressed)
            {
                position0Cache = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol && x.Quantity != 0).ToList();
                stressDsPlus0Cache = Math.Min(StressedPnlPositions(position0Cache, 5), 0);
                stressDsMinus0Cache = Math.Min(StressedPnlPositions(position0Cache, -5), 0);
                timeLastStressed = _algo.Time;
            }

            //var positions1 = position0Cache.Append(position).ToList();
            //_algo.Log($"Checking {symbol} {quantity} {positions1.Count} positions.");
            List<Position> positions1 = new() { position };
            decimal stressDsPlus1 = Math.Min(stressDsPlus0Cache + StressedPnlPositions(positions1, dSPct, dIVPct), 0);
            decimal stressDsMinus1 = Math.Min(stressDsMinus0Cache + StressedPnlPositions(positions1, -dSPct, dIVPct), 0);
            //decimal stressDIVPlus1 = StressTest(positions1, 0, 0.15);
            //decimal stressDIVMinus1 = StressTest(positions1, 0, -0.15);
            // _algo.Log($"{_algo.Time} WhatIfMarginAdd: {symbol} {quantity} stressDsPlus1={stressDsPlus1}, stressDsMinus1={stressDsMinus1}");
            //return stressDsPlus1 + stressDsMinus1; // + stressDIVPlus1 + stressDIVMinus1;

            // Only considering negative risk reduction.
            return stressDsPlus1 + stressDsMinus1 - (stressDsPlus0Cache + stressDsMinus0Cache);
        }

        public decimal StressedPnlPositions(List<Position>? positions = null, double dSPct = 0, double dIVPct = 0, IEnumerable<Metric>? metricsDs = null, IEnumerable<Metric>? metricsDIV = null, DateTime? evalDate = null)
        {
            decimal pnlStressed = 0;
            List<Position> _positions = positions ?? _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol && x.Quantity != 0).ToList();
            
            foreach (Metric metric in metricsDs ?? dfltMetricsDs)
            {
                pnlStressed += dSPct == 0 ? 0 : CachedMetric2Function(metric, _positions, dSPct);
            }
            foreach (Metric metric in metricsDIV ?? dfltMetricsDIV)
            {
                pnlStressed += dIVPct == 0 ? 0 : CachedMetric2Function(metric, _positions, dIVPct);
            }

            return pnlStressed;
        }
        public decimal StressedPnlPositions(Position positions, double dSPct = 0, double dIVPct = 0, IEnumerable<Metric>? metricsDs = null, IEnumerable<Metric>? metricsDIV = null, DateTime? evalDate = null)
        {
            return StressedPnlPositions(new List<Position>() { positions }, dSPct, dIVPct, metricsDs, metricsDIV, evalDate);
        }

        public void OnDS(object? sender, Symbol symbol) => Update();

        public decimal PositionsQuantity(IEnumerable<Position> positions) => positions.Sum(p => p.Quantity);

        public decimal CachedMetric2Function(Metric metric, IEnumerable<Position> positions, double dX)
        {
            var key = Tuple.Create(metric, PositionsQuantity(positions), (decimal)dX);
            if (!CachedMetric2F.ContainsKey(key))
            {
                CachedMetric2F[key] = Metric2Function[metric](positions, dX);
            }
            return CachedMetric2F[key];
        }
    }
}
