using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// A snap shot of current positions, their risk at pnl up to now. Excludes closed positions, hence the final pnl of this file is not the same as the final pnl of the algorithm.
    /// </summary>
    public class RiskRecord
    {
        private readonly Foundations _algo;
        private readonly Equity _equity;
        private readonly PortfolioRisk _pfRisk;
        private readonly IEnumerable<SecurityHolding> _optionHoldings;
        private readonly List<PLExplain> _plExplains;
        public string Time => _algo.Time.ToStringInvariant("yyyy-MM-dd HH:mm:ss");
        public Symbol Symbol => _equity.Symbol;

        public decimal DeltaTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal);
        public decimal Delta100BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal);
        public decimal Delta100BpUSDOptionsTotal => Delta100BpUSDTotal - PositionUnderlying;
        public decimal GammaTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
        public decimal Gamma100BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.Gamma100BpUSDTotal);
        public decimal Gamma500BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.Gamma500BpUSDTotal);
        public decimal VegaTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.VegaTotal);
        public decimal VannaTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.VannaTotal);
        public decimal Vanna100BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.Vanna100BpUSDTotal);
        public decimal ThetaTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.ThetaTotal);

        public decimal PositionUSD => _pfRisk.RiskByUnderlying(Symbol, Metric.EquityDeltaTotal);
        public decimal PositionUnderlying => _algo.Securities[Symbol].Holdings.Quantity;
        public decimal PositionUnderlyingUSD => _algo.Securities[Symbol].Holdings.HoldingsValue;
        public decimal PositionOptions => _optionHoldings.Select(h => h.Quantity).Sum();
        public decimal PositionOptionsUSD => _optionHoldings.Select(h => h.HoldingsValue).Sum();

        public double PL_DeltaFillMid => (double)_plExplains.Sum(x => x.PL_DeltaFillMid);
        public decimal PL_Fee => _plExplains.Sum(x => x.PL_Fee);
        public double PL_DeltaIVdS => _plExplains.Sum(x => x.PL_DeltaIVdS);
        public double PL_Delta => _plExplains.Sum(x => x.PL_Delta);
        public double PL_Gamma => _plExplains.Sum(x => x.PL_Gamma);
        public double PL_DeltaDecay => _plExplains.Sum(x => x.PL_DeltaDecay);
        public double PL_dS3 => _plExplains.Sum(x => x.PL_dS3);
        public double PL_GammaDecay => _plExplains.Sum(x => x.PL_GammaDecay);
        public double PL_dGammaDIV => _plExplains.Sum(x => x.PL_dGammaDIV);
        public double PL_Theta => _plExplains.Sum(x => x.PL_Theta);
        public double PL_ThetaDecay => _plExplains.Sum(x => x.PL_ThetaDecay);
        public double PL_Vega => _plExplains.Sum(x => x.PL_Vega);
        public double PL_Vanna => _plExplains.Sum(x => x.PL_Vanna);
        public double PL_VegaDecay => _plExplains.Sum(x => x.PL_VegaDecay);
        public double PL_Volga => _plExplains.Sum(x => x.PL_Volga);
        public double PL_Total => _plExplains.Sum(x => x.PL_Total);

        public decimal MidPriceUnderlying => _algo.MidPrice(Symbol);
        public decimal HistoricalVolatility => _algo.Securities[Symbol].VolatilityModel.Volatility;
        public double AtmIV => _algo.PfRisk.AtmIV(Symbol);
        public double AtmIVEWMA => _algo.PfRisk.AtmIVEWMA(Symbol);
        public double? SkewStrikeBid => _algo.IVSurfaceRelativeStrikeBid[Symbol].SkewStrike();
        public double? SkewStrikeAsk => _algo.IVSurfaceRelativeStrikeAsk[Symbol].SkewStrike();
        public decimal PosWeightedIV => _pfRisk.RiskByUnderlying(Symbol, Metric.PosWeightedIV);
        public decimal DeltaIVdSTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.DeltaIVdSTotal);
        public decimal DeltaIVdS100BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.DeltaIVdS100BpUSDTotal);
        public decimal PnL => _algo.Portfolio.TotalPortfolioValue - _algo.TotalPortfolioValueSinceStart;
        //public decimal PnL => _algo.Positions.Values.Where(p => p.UnderlyingSymbol == Symbol).Sum(p => p.PL);
        public decimal TotalMarginUsed => _algo.Portfolio.TotalMarginUsed;
        public decimal MarginRemaining => _algo.Portfolio.MarginRemaining;
        public RiskRecord(Foundations algo, PortfolioRisk pfRisk, Equity equity)
        {
            _algo = algo;
            _pfRisk = pfRisk;
            _equity = equity;
            _optionHoldings = _algo.Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == Symbol).Select(kvp => kvp.Value.Holdings);
            // Include unrealized Positions (Quantity != 0) and closed positions (Trade1 != null)
            _plExplains = Position.AllLifeCycles(_algo).Where(p => p.UnderlyingSymbol == Symbol).Select(p => p.PLExplain).ToList();
            //_plExplains = _algo.Positions.Values.Where(p => p.Quantity != 0 && p.UnderlyingSymbol == Symbol).Select(p => p.PLExplain.Update(new PositionSnap(_algo, p.Symbol))).ToList();
            //_plExplains.AddRange(_algo.PositionsRealized.Values.SelectMany(l => l).Select(p => p.PLExplain).ToList());

            if (DeltaTotal * Delta100BpUSDTotal < 0)
            {
                _algo.Error($"{_algo.Time} - RiskRecord: DeltaTotal and Delta100BpUSDTotal have different signs. Caching wrong? DeltaTotal={DeltaTotal}, Delta100BpUSDTotal={Delta100BpUSDTotal}." +
                    $"Recalc Delta={_pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal, skipCache: true)}, Recalc Delta100={_pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal, skipCache: true)}");
            }
        }
    }

    public class RiskRecorder : Disposable
    {
        private readonly string _path;
        public readonly List<string> riskRecordsHeader = typeof(RiskRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(prop => prop.Name).ToList();
        public RiskRecorder(Foundations algo)
        {
            _algo = algo;
            _path = Path.Combine(Globals.PathAnalytics, "RiskRecords.csv");
            
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
    }
}
