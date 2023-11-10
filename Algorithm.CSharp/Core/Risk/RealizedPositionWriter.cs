using System.IO;
using System.Linq;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class RealizedPositionWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        public RealizedPositionWriter(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, "RealizedPositions.csv");
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
        public List<string> CsvHeader(Position position) => _header ??= ObjectsToHeaderNames(position).OrderBy(x => x).ToList();
        public string CsvRow(Position position) => ToCsv(new[] { position }, _header, skipHeader: true);
        public void Write(Position position)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", CsvHeader(position)));
                _headerWritten = true;
            }
            _writer.Write(CsvRow(position));
        }
    }
}
