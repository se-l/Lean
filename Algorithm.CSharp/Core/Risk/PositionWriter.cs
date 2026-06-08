using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public record PositionRecord(
        DateTime Ts1,
        Symbol Symbol,
        SecurityType SecurityType,
        OptionRight? Right,
        Symbol UnderlyingSymbol,
        OptionRight? OptionRight,
        DateTime? Expiry,
        decimal? StrikePrice,
        int Multiplier,
        decimal Quantity,
        decimal Mid1,
        decimal Bid1,
        decimal Ask1,
        decimal Mid1Underlying,
        decimal Bid1Underlying,
        decimal Ask1Underlying,
        decimal UnrealizedProfit,
        decimal Spread1,
        double IVBid1,
        double IVAsk1,
        double IVMid1,
        decimal ValueMid,
        decimal ValueWorst,
        decimal DeltaTotal,
        decimal GammaTotal,
        decimal VegaTotal,
        decimal ThetaTotal,
        decimal PnL,
        bool IsITM1,
        bool IsExercised
    );

    public class PositionWriter : IDisposable
    {
        private readonly Foundations _algo;
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;

        public PositionWriter(Foundations algo, string fileName = "Positions.csv")
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, fileName);
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _writer = new StreamWriter(_path);
        }

        public static string ToCsvHeader()
        {
            return string.Join(",", typeof(PositionRecord).GetProperties().Select(p => p.Name));
        }

        public static string ToCsv(PositionRecord record)
        {
            return string.Join(",", new object[]
            {
                record.Ts1.ToString(DatetTmeFmtProto, CultureInfo.InvariantCulture),
                record.Symbol,
                record.SecurityType,
                record.Right,
                record.UnderlyingSymbol,
                record.OptionRight,
                record.Expiry?.ToString(DtFmtISO, CultureInfo.InvariantCulture),
                record.StrikePrice,
                record.Multiplier,
                record.Quantity,
                record.Mid1,
                record.Bid1,
                record.Ask1,
                record.Mid1Underlying,
                record.Bid1Underlying,
                record.Ask1Underlying,
                record.UnrealizedProfit,
                record.Spread1,
                record.IVBid1,
                record.IVAsk1,
                record.IVMid1,
                record.ValueMid,
                record.ValueWorst,
                record.DeltaTotal,
                record.GammaTotal,
                record.VegaTotal,
                record.ThetaTotal,
                record.PnL,
                record.IsITM1,
                record.IsExercised
            });
        }

        public void WriteCsvRows(IEnumerable<Position> positions)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(ToCsvHeader());
                _headerWritten = true;
            }
            foreach (var pos in positions)
            {
                var rec = new PositionRecord(
                    pos.Ts1,
                    pos.Symbol,
                    pos.SecurityType,
                    pos.Right,
                    pos.UnderlyingSymbol,
                    pos.OptionRight,
                    pos.Expiry,
                    pos.StrikePrice,
                    pos.Multiplier,
                    pos.Quantity,
                    pos.Mid1,
                    pos.Bid1,
                    pos.Ask1,
                    pos.Mid1Underlying,
                    pos.Bid1Underlying,
                    pos.Ask1Underlying,
                    pos.UnrealizedProfit,
                    pos.Spread1,
                    pos.IVBid1,
                    pos.IVAsk1,
                    pos.IVMid1,
                    pos.ValueMid,
                    pos.ValueWorst,
                    pos.DeltaTotal(),
                    pos.GammaTotal(),
                    pos.VegaTotal(),
                    pos.ThetaTotal(),
                    pos.PnL,
                    pos.IsITM1,
                    pos.IsExercised
                );
                _writer.WriteLine(ToCsv(rec));
            }
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}

