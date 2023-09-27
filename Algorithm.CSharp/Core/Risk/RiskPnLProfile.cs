using Accord.Math;
using QuantConnect.Algoalgorithm.CSharp.Core.Risk;
using QuantConnect.Securities.Equity;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class RiskPnLProfile: IDisposable
    {
        // CSV export append each time on call. Implement Dispose here. Initialized in Initialize() method.
        // Given not using the CSVExport method, no need to implement each metric as attribute. Underlying Pct change just a parameter.        
        private readonly Foundations _algo;
        public readonly Equity Equity;
        public Symbol Symbol { get => Equity.Symbol; }
        public Dictionary<double, decimal> PnLProfile = Enumerable.Range(-10, 21).Select(i => (double)i).ToDictionary(i => i, i => 0m);
        private bool _headerWritten;
        private List<string> _header;
        private readonly string _path;
        private readonly StreamWriter _writer;
        private List<Metric> metrics = new() { 
            Metric.Delta, Metric.Gamma, Metric.Speed, Metric.DeltaIVdS, // dS
            Metric.Vega, Metric.Vanna, Metric.Volga  // dIV
        };
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

        public RiskPnLProfile(Foundations algo, Equity equity)
        {
            _algo = algo;
            Equity = equity;

            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "RiskPnLProfile", $"{Symbol.Value}.csv");
            if (!File.Exists(_path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new(_path, false)
            {
                AutoFlush = true
            };
            //_header = new List<string> { "Time", "Symbol", "Metric" };
            //List<string> trailingHeader = PnLProfile.Keys.Sorted().Select(k => k.ToString()).ToList();
            //_header.AddRange(trailingHeader);
            //_writer.WriteLine(string.Join(",", _header));
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
        }

        //public void UpdateSums()
        //{
        //    // Update PnL profile
        //    var time = _algo.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        //    var midPrice = _algo.MidPrice(Symbol);
        //    var positions = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol);

        //    foreach (Metric metric in metrics)
        //    {
        //        foreach (double pctChange in PnLProfile.Keys)
        //        {
        //            decimal metricPnL = Metric2Function[metric](positions, pctChange);
        //            PnLProfile[pctChange] = metricPnL;
        //        }

        //        // Update CSV export
        //        string row = new StringBuilder()
        //            .Append($"{time},{Symbol.Value},{metric},")
        //            .Append(string.Join(",", PnLProfile.Keys.OrderBy(k => k).Select(k => PnLProfile[k])))
        //            .ToString();
        //        _writer.WriteLine(row);
        //    }   
        //}

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
