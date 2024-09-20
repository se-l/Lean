using MathNet.Numerics.LinearAlgebra;
using QuantConnect.Securities.Equity;
using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class KalmanOnUpdateEventArgs<T> : EventArgs
    {
        public T State { get; set; }
        public KalmanOnUpdateEventArgs(T state)
        {
            State = state;
        }
    }

    public class KalmanFilter<T>// : IDisposable
    {
        private readonly Foundations _algo;
        public Equity Equity {  get; private set; }

        // EventHandlers
        public event EventHandler<KalmanOnUpdateEventArgs<T>> OnUpdate;
        public int StateDim { get; private set; }
        private Vector<double> x1;
        private Matrix<double> P1;
        private Matrix<double> P;  // State Covariance matrix
        private Vector<double> x;
        private Matrix<double> K;  // Kalman Gain

        private Matrix<double> F;  // Transition matrix
        private Matrix<double> Q;  // Transition Covariance matrix

        private Matrix<double> H;  // Observation matrix
        private Matrix<double> R;  // Observation Covariance

        private Matrix<double> I;  // Identity matrix

        private readonly MatrixBuilder<double> Mat = Matrix<double>.Build;
        private readonly VectorBuilder<double> Vec = Vector<double>.Build;

        private int _nUpdated;

        public Func<Vector<double>, T> VecToParams { get; private set; }

        /// <summary>
        /// No predict step, hence no matrices for that
        /// No process noise Q.
        /// </summary>
        /// <param name="x"></param> State vector
        /// <param name="p"></param> Covariance matrix
        public KalmanFilter(Foundations algo, Equity equity, Vector<double> x, Matrix<double> P, Func<Vector<double>, T> vecToParams)
        {
            _algo = algo;
            Equity = equity;
            StateDim = x.Count;
            this.x = x;
            this.P = P;
            VecToParams = vecToParams;
            H = R = F = Q = I = Mat.DenseIdentity(StateDim);

            //_path = Path.Combine(Globals.PathAnalytics, "Kalman", Underlying.Value, $"ssvi.csv");
            //Directory.CreateDirectory(Path.GetDirectoryName(_path));
            //_writer = new StreamWriter(_path, true);
        }

        /// <summary>
        /// Compute the Kalman gain
        /// Update x with measurement and residual
        /// Update p.
        /// </summary>
        /// <param name="z"></param>
        public void Update(Vector<double> z)
        {
            if (z.Count != H.RowCount)
            {
                _algo.Error($"{_algo.Time} Cannot update Kalman Filter. Invalid observation vector. Observation vector must have the same dimension as the observation matrix.");
            }
            // Predict step
            x1 = x;  // F is identity. No trajectory prediction
            P1 = P + Q;  // No process noise

            // Update
            var S = (H * P1 * H.Transpose() + R).Inverse();
            K = P1 * H.Transpose() * S;

            var residual = z - H * x1;
            x += K * residual;

            P = P1 - K * H * P1;

            _nUpdated++;

            OnUpdate?.Invoke(this, new KalmanOnUpdateEventArgs<T>(VecToParams(x)));
        }

        public T GetSSVIParams()
        {
            return VecToParams(x);
        }

        private int MinUpdatesToReady 
        {
            get {
                return _algo.Cfg.KalmanMinUpdatesToReady.TryGetValue(Equity.Symbol.Value, out int val) ? val : _algo.Cfg.KalmanMinUpdatesToReady[CfgDefault];
            }         
        }

        public bool IsReady()
        {
            return _nUpdated > MinUpdatesToReady;
        }
        public int NUpdated => _nUpdated;
    }
}
