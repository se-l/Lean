using System;
using System.IO;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Equity;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public record SSVIParamsRecord(double Theta, double Rho, double Psi);
    public class IVSurfaceSSVI : IIVSurface, IDisposable
    {
        private readonly Foundations _algo;
        public Equity Underlying { get; }
        public QuoteSide? Side { get; }
        public SSVIParamsDictionary ModelParams { get; internal set; }
        public double Rho { get; internal set; }
        public double Psi { get; internal set; }
        public bool IsCalibrated { get; internal set; }

        private readonly OptionRight[] OptionRights = new[] { OptionRight.Call, OptionRight.Put };

        // CSV writer
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;

        public IVSurfaceSSVI(Foundations algo, Equity underlying, QuoteSide? side = null, bool createFile = false)
        {
            _algo = algo;
            Underlying = underlying;
            Side = side;

            if (createFile)
            {
                string _side = Side.ToString() ?? "mid";
                _path = Path.Combine(Globals.PathAnalytics, "IVSurface", Underlying.Symbol.Value, $"{_side}.csv");
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
        }

        public void SetModelParams(SSVIParamsDictionary modelParams)
        {
            ModelParams = modelParams;
            _algo.Log($"{_algo.Time} IVSurfaceSSVI.SetModelParams: Updated!");
            IsCalibrated = true;
        }

        public void SetModelParams(object sender, KalmanOnUpdateEventArgs<SSVIParamsDictionary> e)
        {
            SetModelParams(e.State);
        }

        public bool HasParams(DateTime tenor, OptionRight right)
        {
            return ModelParams.ContainsKey((tenor, right));
        }

        public bool HasParams(Option option)
        {
            return ModelParams.ContainsKey((option.Expiry, option.Right));
        }

        public double IV(Option option)
        {
            DateTime calcDate = _algo.Time.Date;
            return IV(OptionContractWrap.E(_algo, option, calcDate).MoneynessFwdLn(), option.Expiry, option.Right, calcDate);
        }

        public double IV(double k, DateTime tenor, OptionRight right, DateTime calcDate)
        {
            if (!IsCalibrated) throw new InvalidOperationException($"IVSurface is not calibrated: Underlying={Underlying}");
            if (!HasParams(tenor, right)) throw new InvalidOperationException($"IVSurface does not have parameters for this tenor={tenor}, right={right}, Underlying={Underlying}");

            SSVIParamsRecord mParams = ModelParams[(tenor, right)];
            return SsviIV(k, mParams, ToTenor(tenor, calcDate));
        }

        public decimal BidPrice(Option option)
        {
            throw new NotImplementedException();
        }

        public decimal AskPrice(Option option)
        {
            throw new NotImplementedException();
        }

        public double AtmIv()
        {
            // Use tenor from params keys that is at least 7 days away
            DateTime tenor = ModelParams.Keys.Where(k => k.Item1 > _algo.Time.Date.AddDays(7)).Min(k => k.Item1);
            return (IV(0, tenor, OptionRight.Call, _algo.Time.Date) + IV(0, tenor, OptionRight.Put, _algo.Time.Date)) / 2;

        }
        
        /// <summary>
        /// Implied total variance surface
        /// eSSVI(K, T) = 1/2 (θ(T) + ρ(T)ψ(T)k + sqrt((ψ(T) k + θ(T) ρ(T))**2 + θ(T)**2 * (1 − ρ(T)**2))
        /// </summary>
        public double SsviIV(double k, SSVIParamsRecord mParams, double tenor)
        {
            return Math.Sqrt(SsviTotalVariance(k, mParams) / tenor);
        }

        /// <summary>
        /// Implied total variance surface
        /// eSSVI(K, T) = 1/2 (θ(T) + ρ(T)ψ(T)k + sqrt((ψ(T) k + θ(T) ρ(T))**2 + θ(T)**2 * (1 − ρ(T)**2))
        /// 0.5 * (theta + rho * psi * k + ((psi * k + theta * rho) ** 2 + theta ** 2 * (1 - rho ** 2)) ** (1 / 2))
        /// </summary>
        public double SsviTotalVariance(double k, SSVIParamsRecord p)
        {
            return 0.5 * (p.Theta + p.Rho * p.Psi * k + Math.Sqrt(
                    Math.Pow(p.Psi * k + p.Theta * p.Rho, 2) + 
                    Math.Pow(p.Theta, 2) * (1 - Math.Pow(p.Rho, 2))
                    )
                );
        }

        public double? IVdS(Symbol symbol)
        {
            return null;
        }

        public double SkewStrike()
        {
            return 0;
        }

        public void WriteCsvRows()
        {
            //var csv = new StringBuilder();
            //var dict = ToDictionary(binGetter: (bin) => bin.IVEWMA);
            //if (!dict[OptionRight.Call].Keys.Any()) return;

            //if (!_headerWritten)
            //{
            //    _writer.Write(GetCsvHeader());
            //    _headerWritten = true;
            //}

            //List<decimal> sortedKeys = dict[OptionRight.Call][dict[OptionRight.Call].Keys.First()].Keys.Sorted().ToList();

            //// Smoothened IVs
            //foreach (OptionRight optionRight in OptionRights)
            //{
            //    foreach (var expiry in dict[optionRight].Keys)
            //    {
            //        string ts = _algo.Time.ToString(_dateTimeFmt, CultureInfo.InvariantCulture);
            //        string row = $"{ts},{optionRight},{expiry.ToString(_dateFmt, CultureInfo.InvariantCulture)},{_algo.MidPrice(Underlying)}," + string.Join(",", sortedKeys.Select(d => dict[optionRight][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
            //        csv.AppendLine(row);
            //    }
            //}

            //_writer.Write(csv.ToString());
        }

        public void Dispose()
        {
            if (_writer != null)
            {
                _writer.Flush();
                _writer.Close();
                _writer.Dispose();
            }
        }
    }
}
