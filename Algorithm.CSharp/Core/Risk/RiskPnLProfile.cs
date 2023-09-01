using Accord.Math;
using QuantConnect.Securities.Equity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

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
        private readonly List<string> _header;
        private readonly string _path;
        private readonly StreamWriter _writer;

        public RiskPnLProfile(Foundations algo, Equity equity)
        {
            _algo = algo;
            Equity = equity;
            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "RiskPnLProfile", $"{Symbol.Value}.csv");
            if (File.Exists(_path))
            {
                //File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new(_path, false)
            {
                AutoFlush = true
            };
            _header = new List<string> { "Time", "Symbol" };
            List<string> trailingHeader = PnLProfile.Keys.Sorted().Select(k => k.ToString()).ToList();
            _header.AddRange(trailingHeader);
            _writer.WriteLine(string.Join(",", _header));
        }

        public void Update()
        {
            // Update PnL profile
            var midPrice = _algo.MidPrice(Symbol);
            var positions = _algo.pfRisk.Positions.Where(x => x.UnderlyingSymbol == Symbol);

            foreach (double pctChange in PnLProfile.Keys)
            {              
                var deltaPnL = positions.Sum(t => t.DeltaXBpUSDTotal(pctChange * 100));
                var gammaPnL = positions.Sum(t => t.GammaXBpUSDTotal(pctChange * 100));
                PnLProfile[pctChange] = deltaPnL + gammaPnL;                
            }

            // Update CSV export
            string row = new StringBuilder()
                .Append(_algo.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
                .Append(",")
                .Append(Symbol.Value)
                .Append(",")
                .Append(string.Join(",", PnLProfile.Keys.OrderBy(k => k).Select(k => PnLProfile[k])))
                .ToString();
            _writer.WriteLine(row);
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
