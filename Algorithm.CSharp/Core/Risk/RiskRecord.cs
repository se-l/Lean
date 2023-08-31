using QuantConnect.Securities.Equity;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class RiskRecord
    {
        public DateTime Time { get; internal set; }
        public Symbol Symbol { get; internal set; }

        public decimal DeltaTotal { get; internal set; }
        public decimal Delta100BpUSDTotal { get; internal set; }
        public decimal Delta100BpUSDOptionsTotal { get; internal set; }
        public decimal GammaTotal { get; internal set; }
        public decimal Gamma100BpUSDTotal { get; internal set; }
        public decimal Gamma500BpUSDTotal { get; internal set; }
        public decimal VegaTotal { get; internal set; }
        public decimal ThetaTotal { get; internal set; }

        public decimal PositionUSD { get; internal set; }
        public decimal PositionUnderlying { get; internal set; }
        public decimal PositionUnderlyingUSD { get; internal set; }
        public decimal PositionOptions { get; internal set; }
        public decimal PositionOptionsUSD { get; internal set; }

        public decimal PnL { get; internal set; }
        public decimal MidPriceUnderlying { get; internal set; }
        public RiskRecord(Foundations algo, PortfolioRisk pfRisk, Equity equity)
        {
            Time = algo.Time;
            Symbol = equity.Symbol;
            PositionUnderlying = algo.Securities[Symbol].Holdings.Quantity;
            PositionUnderlyingUSD = algo.Securities[Symbol].Holdings.HoldingsValue;

            DeltaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal);
            Delta100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal);
            Delta100BpUSDOptionsTotal = Delta100BpUSDTotal - PositionUnderlying;
            GammaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            Gamma100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma100BpUSDTotal);
            Gamma500BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma500BpUSDTotal);
            VegaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.VegaTotal);
            ThetaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.ThetaTotal);

            var optionHoldings = algo.Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == Symbol).Select(kvp => kvp.Value.Holdings);
            PositionOptions = optionHoldings.Select(h => h.Quantity).Sum();
            PositionOptionsUSD = optionHoldings.Select(h => h.HoldingsValue).Sum();

            PnL = TradesCumulative.Cumulative(algo).Where(t => t.UnderlyingSymbol == Symbol).Select(t => t.PL).Sum();
            MidPriceUnderlying = algo.MidPrice(Symbol);
        }
    }

    public class RiskRecorder : IDisposable
    {
        private readonly Foundations _algo;
        private readonly StreamWriter _writer;
        private readonly string _path;
        public readonly List<string> riskRecordsHeader = new() { "Time", "Symbol",
            "DeltaTotal", "Delta100BpUSDTotal", "Delta100BpUSDOptionsTotal", "GammaTotal", "Gamma100BpUSDTotal","Gamma500BpUSDTotal",
            "VegaTotal", "ThetaTotal", "PositionUSD", "PositionUnderlying", "PositionUnderlyingUSD", "PositionOptions", "PositionOptionsUSD", "PnL", "MidPriceUnderlying", };
        public RiskRecorder(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Directory.GetCurrentDirectory(), $"{_algo.Name}_risk_records_{_algo.EndDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new(_path, true)
            {
                AutoFlush = true
            };
            _writer.WriteLine(string.Join(",", riskRecordsHeader));
        }

        public void Record(string ticker)
        {
            List<RiskRecord> riskRecords = new() { new RiskRecord(_algo, _algo.pfRisk, (Equity)_algo.Securities[ticker]) };
            string csv = ToCsv(riskRecords, riskRecordsHeader, skipHeader: true);
            _writer.Write(csv);
        }
        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
}
