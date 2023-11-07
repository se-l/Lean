using System.IO;
using System.Linq;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    // The CSV part is quite messy. Improve
    public class RealizedPLExplainWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        public RealizedPLExplainWriter(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, "RealizedPLExplain.csv");
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
        public List<string> CsvHeader(PLExplain plExplain) => _header ??= ObjectsToHeaderNames(plExplain).OrderBy(x => x).ToList();
        public string CsvRow(PLExplain plExplain) => ToCsv(new[] { plExplain }, _header, skipHeader: true);
        public void Write(PLExplain plExplain)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", CsvHeader(plExplain)));
                _headerWritten = true;
            }
            _writer.Write(CsvRow(plExplain));
        }
    }
}
