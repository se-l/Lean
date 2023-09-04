using System;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class Bin
    {
        public readonly QuoteSide Side;
        public readonly decimal Value;
        public readonly bool IsOTM;
        public readonly DateTime Expiry;
        public readonly double Alpha;  // 1: No Smoothing, 0: No Update. First IV.
        public readonly TimeSpan SamplingPeriod;
        // For Adaptive EWMA
        // private double gamma = 0.0001;
        // private double eps;

        public double? IV;
        public double? IVEWMA;
        private double? _IVEWMAPrevious;

        public double? Slope;
        public double? SlopeEWMA;
        private double? _slopeEWMAPrevious;

        public DateTime Time;
        private DateTime PreviousTime;

        public Bin(QuoteSide side, decimal value, DateTime expiry, bool isOTM, double alpha = 1.0, TimeSpan? samplingPeriod = null)
        {
            Side = side;
            Value = value;
            Expiry = expiry;
            IsOTM = isOTM;
            Alpha = alpha;
            SamplingPeriod = samplingPeriod ?? TimeSpan.FromMinutes(5);
        }

        public void Update(DateTime time, double iv, double slope)
        {
            if (time <= Time || iv == 0) { return; }

            IV = iv;
            Time = time;
            Slope = slope;

            IVEWMA = Alpha * iv + (1 - Alpha) * (_IVEWMAPrevious ?? iv);
            SlopeEWMA = Alpha * iv + (1 - Alpha) * (_slopeEWMAPrevious ?? slope);
            
            if (UpdatePreviousTime(time) || _IVEWMAPrevious == null)
            {
                _IVEWMAPrevious = IVEWMA;
                _slopeEWMAPrevious = Slope;
            }
        }

        private bool UpdatePreviousTime(DateTime time)
        {
            // Too frequent EWMA would turn signal noisier, hence keep only save latest ewma to previousEWMA every x Periods.
            if (PreviousTime + SamplingPeriod < time)
            {
                PreviousTime = time;
                return true;
            }
            return false;
        }
    }
}
