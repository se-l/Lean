using QuantConnect.Algorithm.CSharp.Core.Events;
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
        private readonly List<PnLExplain> _plExplains;
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

        public double PnLDeltaFillMid => (double)_plExplains.Sum(x => x.PnLDeltaFillMid);
        public decimal PnLFee => _plExplains.Sum(x => x.PnLFee);
        public double PnLDeltaIVdS => _plExplains.Sum(x => x.PnLDeltaIVdS);
        public double PnLDelta => _plExplains.Sum(x => x.PnLDelta);
        public double PnLGamma => _plExplains.Sum(x => x.PnLGamma);
        public double PnLDeltaDecay => _plExplains.Sum(x => x.PnLDeltaDecay);
        public double PnLdS3 => _plExplains.Sum(x => x.PnLdS3);
        public double PnLGammaDecay => _plExplains.Sum(x => x.PnLGammaDecay);
        public double PnLdGammaDIV => _plExplains.Sum(x => x.PnLdGammaDIV);
        public double PnLTheta => _plExplains.Sum(x => x.PnLTheta);
        public double PnLThetaDecay => _plExplains.Sum(x => x.PnLThetaDecay);
        public double PnLVega => _plExplains.Sum(x => x.PnLVega);
        public double PnLVanna => _plExplains.Sum(x => x.PnLVanna);
        public double PnLVegaDecay => _plExplains.Sum(x => x.PnLVegaDecay);
        public double PnLVolga => _plExplains.Sum(x => x.PnLVolga);
        public double PnLTotal => _plExplains.Sum(x => x.PnLTotal);
        public int CntPnLExplains => _plExplains.Count;

        public decimal MidPriceUnderlying => _algo.MidPrice(Symbol);
        public decimal HistoricalVolatility => _algo.Securities[Symbol].VolatilityModel.Volatility;
        public double? SkewStrike => _algo.IvSurfaceSsviMid[_equity].SkewStrike();
        public decimal PosWeightedIV => _pfRisk.RiskByUnderlying(Symbol, Metric.PosWeightedIV);
        public decimal DeltaIVdSTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.DeltaIVdSTotal);
        public decimal DeltaIVdS100BpUSDTotal => _pfRisk.RiskByUnderlying(Symbol, Metric.DeltaIVdS100BpUSDTotal);
        public decimal PnL => _algo.Portfolio.TotalPortfolioValue - _algo.TotalPortfolioValueSinceStart;
        public decimal TotalMarginUsed => _algo.Portfolio.TotalMarginUsed;
        public decimal MarginRemaining => _algo.Portfolio.MarginRemaining;
        public RiskRecord(Foundations algo, PortfolioRisk pfRisk, Equity equity)
        {
            _algo = algo;
            _pfRisk = pfRisk;
            _equity = equity;
            _optionHoldings = _algo.Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == Symbol).Select(kvp => kvp.Value.Holdings);
            // Include unrealized Positions (Quantity != 0) and closed positions (Trade1 != null)
            _plExplains = Position.AllLifeCycles(_algo).Where(p => p.UnderlyingSymbol == Symbol).Select(p => p.PnLExplain).ToList();
            //_plExplains = _algo.Positions.Values.Where(p => p.Quantity != 0 && p.UnderlyingSymbol == Symbol).Select(p => p.PnLExplain.Update(new PositionSnap(_algo, p.Symbol))).ToList();
            //_plExplains.AddRange(_algo.PositionsRealized.Values.SelectMany(l => l).Select(p => p.PnLExplain).ToList());

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
        public void OnTradeEvent(object sender, TradeEventArgs e)
        {
            Record(Underlying(e.Trades.First().Symbol));
        }
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

        public void Record(Symbol symbol) => Record((Equity)_algo.Securities[symbol.Value]);

        public void Record(Equity equity)
        {
            List<RiskRecord> riskRecords = new() { new RiskRecord(_algo, _algo.PfRisk, equity) };
            string csv = ToCsv(riskRecords, riskRecordsHeader, skipHeader: true);
            _writer.Write(csv);
        }
    }
}
