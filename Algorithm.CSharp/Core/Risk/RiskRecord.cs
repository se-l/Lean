using QuantConnect.Algoalgorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Equity;
using System;
using System.Collections.Generic;
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
        public decimal DeltaImpliedTotal { get; internal set; }
        public decimal Delta100BpUSDTotal { get; internal set; }
        public decimal DeltaImplied100BpUSDTotal { get; internal set; }
        public decimal Delta100BpUSDOptionsTotal { get; internal set; }
        public decimal GammaTotal { get; internal set; }
        public decimal GammaImpliedTotal { get; internal set; }
        public decimal Gamma100BpUSDTotal { get; internal set; }
        public decimal GammaImplied100BpUSDTotal { get; internal set; }
        public decimal Gamma500BpUSDTotal { get; internal set; }
        public decimal GammaImplied500BpUSDTotal { get; internal set; }
        public decimal VegaTotal { get; internal set; }
        public decimal ThetaTotal { get; internal set; }

        public decimal PositionUSD { get; internal set; }
        public decimal PositionUnderlying { get; internal set; }
        public decimal PositionUnderlyingUSD { get; internal set; }
        public decimal PositionOptions { get; internal set; }
        public decimal PositionOptionsUSD { get; internal set; }
        public decimal HistoricalVolatility { get; internal set; }
        public double AtmIvOtm { get; internal set; }
        public double AtmIvOtmEWMA { get; internal set; }
        public decimal PnL { get; internal set; }
        public decimal MidPriceUnderlying { get; internal set; }

        public double PL_DeltaFillMid { get; internal set; }  // Bid/Ask difference to midpoint. Positive if we earned the spread.
        public double PL_Delta { get; internal set;}  // dP ; sensitivity to underlying price
        public double PL_Gamma { get; internal set;}  // dP2
        public double PL_DeltaDecay { get; internal set;}  // dPdT
        public double PL_dGammaDP { get; internal set;}  // dP3
        public double PL_GammaDecay { get; internal set;}  // dP2dT
        public double PL_dGammaDIV { get; internal set;}  // dP2dIV
        public double PL_Theta { get; internal set;}  // dT ; sensitivity to time
        public double PL_dTdP { get; internal set;}  // dTdP
        public double PL_ThetaDecay { get; internal set;}  // dT2
        public double PL_dTdIV { get; internal set;}  // dTdIV
        public double PL_Vega { get; internal set;}  // dIV ; sensitivity to volatility
        public double PL_dDeltaDIV { get; internal set;}  // vanna
        public double PL_VegaDecay { get; internal set;}  // dIVdT
        public double PL_dVegadIV { get; internal set;}  // vomma
        public double PL_Rho { get; internal set;}  // dR ; sensitivity to interest rate
        public decimal PL_Fee { get; internal set;}
        public double PL_Total { get; internal set;}  // total PnL

        public RiskRecord(Foundations algo, PortfolioRisk pfRisk, Equity equity)
        {
            Time = algo.Time;
            Symbol = equity.Symbol;
            PositionUnderlying = algo.Securities[Symbol].Holdings.Quantity;
            PositionUnderlyingUSD = algo.Securities[Symbol].Holdings.HoldingsValue;

            DeltaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal);
            DeltaImpliedTotal = 0; // pfRisk.RiskByUnderlying(Symbol, Metric.DeltaImpliedTotal);
            Delta100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal);
            DeltaImplied100BpUSDTotal = 0;// pfRisk.RiskByUnderlying(Symbol, Metric.DeltaImplied100BpUSDTotal);
            Delta100BpUSDOptionsTotal = Delta100BpUSDTotal - PositionUnderlying;
            GammaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            Gamma100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma100BpUSDTotal);
            GammaImplied100BpUSDTotal = 0;// pfRisk.RiskByUnderlying(Symbol, Metric.GammaImplied100BpUSDTotal);
            Gamma500BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma500BpUSDTotal);
            GammaImplied500BpUSDTotal = 0; // pfRisk.RiskByUnderlying(Symbol, Metric.GammaImplied500BpUSDTotal);
            VegaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.VegaTotal);
            ThetaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.ThetaTotal);

            var optionHoldings = algo.Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == Symbol).Select(kvp => kvp.Value.Holdings);
            PositionOptions = optionHoldings.Select(h => h.Quantity).Sum();
            PositionOptionsUSD = optionHoldings.Select(h => h.HoldingsValue).Sum();

            PnL = Position.AllLifeCycles(algo).Where(p => p.UnderlyingSymbol == Symbol).Sum(p => p.PL);
            IEnumerable<PLExplain> plExplains = Position.AllLifeCycles(algo).Where(p => p.UnderlyingSymbol == Symbol).Select(p => p.PLExplain);

            if (DeltaTotal * Delta100BpUSDTotal < 0)
            {
                algo.Error($"{algo.Time} - RiskRecord: DeltaTotal and Delta100BpUSDTotal have different signs. Caching wrong? DeltaTotal={DeltaTotal}, Delta100BpUSDTotal={Delta100BpUSDTotal}." +
                    $"Recalc Delta={pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal, skipCache: true)}, Recalc Delta100={pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal, skipCache: true)}");
            }

            PL_DeltaFillMid = plExplains.Sum(x => x.PL_DeltaFillMid);
            PL_Delta = plExplains.Sum(x => x.PL_Delta);
            PL_Gamma = plExplains.Sum(x => x.PL_Gamma);
            PL_DeltaDecay = plExplains.Sum(x => x.PL_DeltaDecay);
            PL_dGammaDP = plExplains.Sum(x => x.PL_dGammaDP);
            PL_GammaDecay = plExplains.Sum(x => x.PL_GammaDecay);
            PL_dGammaDIV = plExplains.Sum(x => x.PL_dGammaDIV);
            PL_Theta = plExplains.Sum(x => x.PL_Theta);
            PL_dTdP = plExplains.Sum(x => x.PL_dTdP);
            PL_ThetaDecay = plExplains.Sum(x => x.PL_ThetaDecay);
            PL_dTdIV = plExplains.Sum(x => x.PL_dTdIV);
            PL_Vega = plExplains.Sum(x => x.PL_Vega);
            PL_dDeltaDIV = plExplains.Sum(x => x.PL_dDeltaDIV);
            PL_VegaDecay = plExplains.Sum(x => x.PL_VegaDecay);
            PL_dVegadIV = plExplains.Sum(x => x.PL_dVegadIV);
            PL_Fee = plExplains.Sum(x => x.PL_Fee);
            PL_Total = plExplains.Sum(x => x.PL_Total);

            MidPriceUnderlying = algo.MidPrice(Symbol);
            HistoricalVolatility = algo.Securities[equity.Symbol].VolatilityModel.Volatility;
            AtmIvOtm = algo.PfRisk.AtmIV(equity.Symbol);
            AtmIvOtmEWMA = algo.PfRisk.AtmIVEWMA(equity.Symbol);            
        }
    }

    public class RiskRecorder : IDisposable
    {
        private readonly Foundations _algo;
        private readonly StreamWriter _writer;
        private readonly string _path;
        public readonly List<string> riskRecordsHeader = new() { "Time", "Symbol",
            "DeltaTotal", "DeltaImpliedTotal", "Delta100BpUSDTotal", "DeltaImplied100BpUSDTotal", "Delta100BpUSDOptionsTotal", "GammaTotal", "GammaImpliedTotal", "Gamma100BpUSDTotal", "GammaImplied100BpUSDTotal", "Gamma500BpUSDTotal", "GammaImplied500BpUSDTotal",
            "VegaTotal", "ThetaTotal", "PositionUSD", "PositionUnderlying", "PositionUnderlyingUSD", "PositionOptions", "PositionOptionsUSD", "MidPriceUnderlying", "HistoricalVolatility", "AtmIvOtm", "AtmIvOtmEWMA",
            "PnL", "PL_DeltaFillMid", "PL_Delta", "PL_Gamma", "PL_DeltaDecay", "PL_dGammaDP", "PL_GammaDecay", "PL_dGammaDIV", "PL_Theta", "PL_dTdP", "PL_ThetaDecay", "PL_dTdIV", "PL_Vega", "PL_dDeltaDIV", "PL_VegaDecay", "PL_dVegadIV", "PL_Fee", "PL_Total"
        };
        public RiskRecorder(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "RiskRecords.csv");
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
            List<RiskRecord> riskRecords = new() { new RiskRecord(_algo, _algo.PfRisk, (Equity)_algo.Securities[ticker]) };
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
