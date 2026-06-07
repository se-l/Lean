using QuantConnect.Algorithm.CSharp.Core.Events;
using System.IO;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class TradeWriter : Disposable
    {
        private readonly string _path;
        private bool _headerWritten;
        private readonly StreamWriter _writer;
        private static readonly string[] Header = new[]
        {
            "Symbol", "UnderlyingSymbol", "SecurityType", "OptionRight", "Expiry", "StrikePrice", "Quantity", "PriceFillAvg", "Fee", "FirstFillTime"
        };

        public TradeWriter(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, "RealizedTrades.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true)
            {
                AutoFlush = true
            };
        }

        public void Write(Trade trade)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(string.Join(",", Header));
                _headerWritten = true;
            }
            _writer.WriteLine(string.Join(",", new[]
            {
                trade.Symbol?.Value ?? "",
                trade.UnderlyingSymbol?.Value ?? "",
                trade.SecurityType.ToString(),
                trade.OptionRight?.ToString() ?? "",
                trade.Expiry?.ToString("yyyy-MM-dd") ?? "",
                trade.StrikePrice?.ToString() ?? "",
                trade.Quantity.ToString(),
                trade.PriceFillAvg.ToString(),
                trade.Fee.ToString(),
                trade.FirstFillTime.ToString("o")
            }));
        }

        public void OnTradeEvent(object sender, TradeEventArgs e)
        {
            if (e.Trades == null)
            {
                return;
            }
            foreach (Trade trade in e.Trades)
            {
                Write(trade);
            }
        }
    }
}
