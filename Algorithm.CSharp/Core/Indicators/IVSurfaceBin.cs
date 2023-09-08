using System;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class Bin
    {
        public readonly QuoteSide Side;
        public readonly ushort Value;
        public readonly bool IsOTM;
        public readonly DateTime Expiry;
        public readonly double Alpha;  // 1: No Smoothing, 0: No Update. First IV.
        public readonly TimeSpan SamplingPeriod;
        // For Adaptive EWMA
        // private double gamma = 0.0001;

        public double? IV;
        public double? IVEWMA;
        private double? _IVEWMAPrevious;

        public double? Slope;
        public double? SlopeEWMA;
        private double? _slopeEWMAPrevious;

        public DateTime Time;
        private DateTime PreviousTime;
        public double? Epsilon { get => IV - IVEWMA; }

        private uint _samples;
        private uint _smoothings;
        public uint Samples { get => _samples; }
        public uint Smoothings { get => _smoothings; }

        public Bin(QuoteSide side, ushort value, DateTime expiry, bool isOTM, double alpha = 1.0, TimeSpan? samplingPeriod = null)
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
            _samples += 1;

            IVEWMA = Alpha * iv + (1 - Alpha) * (_IVEWMAPrevious ?? iv);
            SlopeEWMA = Alpha * iv + (1 - Alpha) * (_slopeEWMAPrevious ?? slope);
            
            if (UpdatePreviousTime(time) || _IVEWMAPrevious == null)
            {
                _IVEWMAPrevious = IVEWMA;
                _slopeEWMAPrevious = Slope;
                _smoothings += 1;
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

        public void ResetEWMA()
        {
            IVEWMA = _IVEWMAPrevious = IV;
            SlopeEWMA = _slopeEWMAPrevious = Slope;
        }

        public string Id()
        {
            string otm = IsOTM ? "OTM" : "ITM";
            return $"{Side} {otm} {Expiry.ToString("yyMMdd", CultureInfo.InvariantCulture)} {Value}";
        }
    }
}
