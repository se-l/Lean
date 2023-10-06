using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    // The CSV part is quite messy. Improve
    public class UtilityWriter : IDisposable
    {
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;
        public UtilityWriter(Equity equity)
        {
            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", equity.Symbol.Value, "UtilityOrder.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true);
        }
        private List<string>? _header;
        public List<string> CsvHeader(UtilityOrder utilityOrder) => _header ??= ObjectsToHeaderNames(utilityOrder).OrderBy(x => x).ToList();
        public string CsvRow(UtilityOrder utilityOrder) => ToCsv(new[] { utilityOrder }, _header, skipHeader: true);
        public void Write(UtilityOrder utility)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", CsvHeader(utility)));
                _headerWritten = true;
            }
            _writer.Write(CsvRow(utility));
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
