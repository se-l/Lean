using QuantConnect.Securities.Equity;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public record Record(DateTime Ts, DateTime Expiry, OptionRight Right, decimal S, double Theta, double Rho, double Psi);
    public class KalmanFilterSSVIWriter : IDisposable
    {
        private readonly Foundations _algo;
        public Equity Equity { get; }
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;

        public KalmanFilterSSVIWriter(Foundations algo, KalmanFilter<SSVIParamsDictionary> kalmanFilter)
        {
            _algo = algo;
            Equity = kalmanFilter.Equity;
            _path = Path.Combine(Globals.PathAnalytics, Equity.Symbol.Value, "KalmanFilterSSVI.csv");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Globals.PathAnalytics, Equity.Symbol.Value)));
            _writer = new StreamWriter(_path);
            kalmanFilter.OnUpdate += RecordUpdatedKalmanParams;
        }

        public void RecordUpdatedKalmanParams(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            var ts = _algo.Time;
            var s = _algo.MidPrice(Equity.Symbol);
            List<Record> records = new List<Record>();
            foreach (var kvp in e.State)
            {
                var key = kvp.Key;
                var value = kvp.Value;
                records.Add(new Record(ts, key.Item1, key.Item2, s, value.Theta, value.Rho, value.Psi));
            }
            WriteCsvRows(records);
        }

        public static string ToCsvHeader()
        {
            return string.Join(",", typeof(Record).GetProperties().Select(p => p.Name));
        }

        public static string ToCsv(Record record)
        {
            return $"{record.Ts.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture)},{record.Expiry.ToString(DtFmtISO, CultureInfo.InvariantCulture)},{record.Right},{record.S},{record.Theta},{record.Rho},{record.Psi}";
        }

        public void WriteCsvRows(IEnumerable<Record> records)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(ToCsvHeader());
                _headerWritten = true;
            }
            records.DoForEach(r => _writer.WriteLine(ToCsv(r)));
            //_algo.Log($"{_algo.Time} KalmanFilterSSVIWriter: {Equity.Symbol.Value} Wrote {records.Count()} records to {_path}");
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
