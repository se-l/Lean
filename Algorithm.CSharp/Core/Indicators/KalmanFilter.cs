using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class KalmanRecord
    {
        public DateTime Expiry { get; set; }
        public string Right { get; set; }
        public decimal Strike { get; set; }
        public decimal Spot { get; set; }
        public double MoneynessFwdLn { get; set; }
        public double IVMean { get; set; }
        public double STD { get; set; }
        public double AlphaBid { get; set; }
        public double AlphaAsk { get; set; }
        public double KalmanBidIV { get; set; }
        public double KalmanAskIV { get; set; }
        public decimal KalmanBidPrice { get; set; }
        public decimal KalmanAskPrice { get; set; }
        public decimal BidPrice { get; set; }
        public decimal AskPrice { get; set; }
        public double MidIV { get; set; }
        
        public KalmanRecord(DateTime expiry, string right, decimal strike, decimal spot, double moneyness, double ivMean, double std, double alphaBid, double alphaAsk, double kalmanBidIV, double kalmanAskIV, decimal kalmanBidPrice, decimal kalmanAskPrice, decimal bidPrice, decimal askPrice, double midIV)
        {
            Expiry = expiry;
            Right = right;
            Strike = strike;
            Spot = spot;
            MoneynessFwdLn = moneyness;
            IVMean = ivMean;
            STD = std;
            AlphaBid = alphaBid;
            AlphaAsk = alphaAsk;
            KalmanBidIV = kalmanBidIV;
            KalmanAskIV = kalmanAskIV;
            KalmanBidPrice = kalmanBidPrice;
            KalmanAskPrice = kalmanAskPrice;            
            BidPrice = bidPrice;
            AskPrice = askPrice;
            MidIV = midIV;
        }

        public string ToCsv()
        {
            return $"{Expiry.ToString(DtFmtISO, CultureInfo.InvariantCulture)},{Right},{Strike},{Spot},{MoneynessFwdLn},{IVMean},{STD},{AlphaBid},{AlphaAsk},{KalmanBidIV},{KalmanAskIV},{KalmanBidPrice},{KalmanAskPrice},{BidPrice},{AskPrice},{MidIV}";
        }

        public static string ToCsvHeader()
        {
            return "Expiry,Right,Strike,Spot,MoneynessFwdLn,IVMean,STD,AlphaBid,AlphaAsk,KalmanBidIV,KalmanAskIV,KalmanBidPrice,KalmanAskPrice,BidPrice,AskPrice,MidIV";
        }
    }
    public class KalmanFilter : IDisposable
    {
        private readonly Foundations _algo;
        public Symbol Underlying {  get; private set; }
        public DateTime Expiry { get; private set; }
        public OptionRight Right { get; private set; }
        public int StateDim { get; private set; }
        private Vector<double> x1;
        private Matrix<double> P1;
        private Matrix<double> P;
        private Vector<double> x;
        private Matrix<double> K;
        private Matrix<double> Q;
        private Matrix<double> I;
        private Matrix<double> H;
        private readonly Dictionary<Option, double> ivs = new();
        private readonly Dictionary<Option, double> ivsFit = new();
        private readonly int RollingErrorWindow = 30;
        private readonly Dictionary<Option, Queue<double>> rollingSquaredResiduals = new();
        private readonly MatrixBuilder<double> Mat = Matrix<double>.Build;
        private readonly VectorBuilder<double> Vec = Vector<double>.Build;
        private readonly Normal norm = new();
        private readonly double alphaBid;
        private readonly double alphaAsk;
        private Tuple<double, double> scopedMoneynessFitting = Tuple.Create( 0.8, 1.2 );

        // CSV writer
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;
        private readonly List<KalmanRecord> _kalmanRecords = new();

        /// <summary>
        /// No predict step, hence no matrices for that
        /// No process noise Q.
        /// </summary>
        /// <param name="x"></param> State vector
        /// <param name="p"></param> Covariance matrix
        public KalmanFilter(Foundations algo, Symbol underlying, DateTime expiry, OptionRight right, Vector<double> x, Matrix<double> P)
        {
            _algo = algo;
            Underlying = underlying;
            Expiry = expiry;
            Right = right;
            StateDim = x.Count;
            this.x = x;
            this.P = P;
            Q = I = Matrix<double>.Build.DenseDiagonal(StateDim, StateDim, 1);
            alphaBid = _algo.Cfg.KalmanAlphaBid.TryGetValue(underlying, out alphaBid) ? alphaBid : _algo.Cfg.KalmanAlphaBid[CfgDefault];
            alphaAsk = _algo.Cfg.KalmanAlphaAsk.TryGetValue(underlying, out alphaAsk) ? alphaAsk : _algo.Cfg.KalmanAlphaAsk[CfgDefault];
            List<double> scopedMoneynessFittingLst = _algo.Cfg.KalmanScopedMoneyness.TryGetValue(underlying, out scopedMoneynessFittingLst) ? scopedMoneynessFittingLst : _algo.Cfg.KalmanScopedMoneyness[CfgDefault];
            scopedMoneynessFitting = Tuple.Create(scopedMoneynessFittingLst[0], scopedMoneynessFittingLst[1]);

            string rightStr = Right == OptionRight.Call ? "call" : "put";
            _path = Path.Combine(Globals.PathAnalytics, "Kalman", Underlying.Value, $"{expiry.ToString(DtFmtISO, CultureInfo.InvariantCulture)}-{rightStr}.csv");            
            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            _writer = new StreamWriter(_path, true);
        }

        /// <summary>
        /// Compute the Kalman gain
        /// Update observation matrix H with new moneyness params
        /// Update x with measurement and residual
        /// Update p.
        /// </summary>
        /// <param name="z"></param>
        /// <returns></returns>
        public Vector<double> Update(Vector<double> z)
        {
            // Predict step
            x1 = x;  // F is identity. No trajectory prediction
            P1 = P + Q;  // No process noise

            // Update
            var R = Matrix<double>.Build.DenseDiagonal(z.Count, z.Count, 1);  // Measurement Covariance
            var S = (H * P1 * H.Transpose() + R).Inverse();
            K = P1 * H.Transpose() * S;

            var residual = z - H * x1;
            x += K * residual;

            P = P1 - K * H * P1;

            return residual;
        }

        private bool ScopedForFitting(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time);
            var moneyness = ocw.MoneynessFwd();
            return option.Expiry == Expiry && 
                option.Right == Right && 
                moneyness > scopedMoneynessFitting.Item1 && moneyness < scopedMoneynessFitting.Item2;
        }

        /// <summary>
        /// Contracts that have not been filled in delta spot % period, get dropped off the ivs dictionary. Can add more conditions, like
        /// must have been updated within last 2 hours...
        /// </summary>
        /// <param name="option"></param>
        /// <param name="underlying"></param>
        /// <param name="iv"></param>
        public void UpdateObservation(Option option, double iv)
        {
            double residual;
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time);
            ivs[option] = iv;
            if (ScopedForFitting(option))
            {
                var orderedIVsFit = ivsFit.OrderBy(kvp => kvp.Key.StrikePrice);
                ivsFit[option] = iv;
                IEnumerable<double> moneynessLn = orderedIVsFit.Select(kvp => OptionContractWrap.E(_algo, kvp.Key, _algo.Time).MoneynessFwdLn());
                double[][] m = moneynessLn.Select(m => new double[] { 1, m, Math.Pow(m, 2), Math.Pow(m, 3) }).ToArray();
                H = Mat.DenseOfRowArrays(m);
                Vector<double> residuals = Update(Vec.DenseOfEnumerable(orderedIVsFit.Select(kvp => kvp.Value)));
                residual = residuals[ivsFit.Keys.ToList().IndexOf(option)];
            } 
            else
            {
                if (ivsFit.ContainsKey(option))
                    ivsFit.Remove(option);
                residual = KalmanMeanIV(option) - iv;
            }
            EnqueueResidual(option, residual);

            _kalmanRecords.Add(GetKalmanRecord(option));
        }

        private void EnqueueResidual(Option option, double residual)
        {
            double squaredResidual = Math.Pow(residual, 2);
            if (!rollingSquaredResiduals.ContainsKey(option))
                rollingSquaredResiduals[option] = new Queue<double>();
            rollingSquaredResiduals[option].Enqueue(squaredResidual);
            if (rollingSquaredResiduals[option].Count > RollingErrorWindow)
                rollingSquaredResiduals[option].Dequeue();
        }

        public double KalmanMeanIV(Option option)
        {
            double moneyness = OptionContractWrap.E(_algo, option, _algo.Time).MoneynessFwdLn();
            Vector<double> polynomial = Vec.DenseOfArray(new double[] { 1, moneyness, Math.Pow(moneyness, 2), Math.Pow(moneyness, 3) });
            return x.DotProduct(polynomial);
        }

        private double STD(Option option)
        {
            if (!rollingSquaredResiduals.ContainsKey(option))
                return 0;
            return Math.Sqrt(rollingSquaredResiduals[option].Sum() / rollingSquaredResiduals[option].Count);
        }

        public double Bound(Option option, double? alpha = null)
        {
            return STD(option) * norm.InverseCumulativeDistribution(alpha ?? alphaBid);
        }

        public double KalmanBidIV(Option option)
        {
            return KalmanMeanIV(option) - Bound(option);
        }
        public double KalmanAskIV(Option option)
        {
            return KalmanMeanIV(option) + Bound(option);
        }
        public decimal KalmanBidPrice(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time);
            return (decimal)ocw.NPV(KalmanBidIV(option), null);
        }
        public decimal KalmanAskPrice(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time);
            return (decimal)ocw.NPV(KalmanAskIV(option), null);
        }

        public KalmanRecord GetKalmanRecord(Option option)
        {
            double meanKalmanIV = KalmanMeanIV(option);
            return new KalmanRecord(
                option.Expiry,
                option.Right.ToString(),
                option.StrikePrice,
                _algo.MidPrice(Underlying),
                OptionContractWrap.E(_algo, option, _algo.Time).MoneynessFwd(),
                meanKalmanIV,
                STD(option),
                alphaBid,
                alphaAsk,
                KalmanBidIV(option),
                KalmanAskIV(option),
                KalmanBidPrice(option),
                KalmanAskPrice(option),
                option.BidPrice,
                option.AskPrice,
                _algo.MidIV(option.Symbol)
        );
        }

        public void WriteCsvRows()
        {
            var csv = new StringBuilder();
            
            if (!_headerWritten)
            {
                _writer.WriteLine(KalmanRecord.ToCsvHeader());
                _headerWritten = true;
            }
            _kalmanRecords.ForEach(kr => _writer.WriteLine(kr.ToCsv()));
            _kalmanRecords.Clear();
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
