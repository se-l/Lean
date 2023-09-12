using System;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class Bin
    {
        public readonly QuoteSide Side;
        public readonly ushort Value;
        public readonly DateTime Expiry;
        public readonly OptionRight OptionRight;
        public readonly double Alpha;  // 1: No Smoothing, 0: No Update. First IV.
        public readonly TimeSpan SamplingPeriod;
        // For Adaptive EWMA
        // private double gamma = 0.0001;

        public double? IV;
        public double? IVEWMA;
        private double? _IVEWMAPrevious;

        public DateTime Time;
        private DateTime PreviousTime;
        public double? Epsilon { get => IV - IVEWMA; }

        private uint _samples;
        private uint _smoothings;
        public uint Samples { get => _samples; }
        public uint Smoothings { get => _smoothings; }

        public Bin(QuoteSide side, ushort value, DateTime expiry, OptionRight optionRight, double alpha = 1.0, TimeSpan? samplingPeriod = null)
        {
            Side = side;
            Value = value;
            Expiry = expiry;
            OptionRight = optionRight;
            Alpha = alpha;
            SamplingPeriod = samplingPeriod ?? TimeSpan.FromMinutes(5);
        }

        public void Update(DateTime time, double iv)
        {
            if (time <= Time || iv == 0) { return; }

            IV = iv;
            Time = time;
            _samples += 1;

            IVEWMA = Alpha * iv + (1 - Alpha) * (_IVEWMAPrevious ?? iv);

            if (UpdatePreviousTime(time) || _IVEWMAPrevious == null)
            {
                _IVEWMAPrevious = IVEWMA;
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
        }

        public string Id()
        {
            return $"{Side} {OptionRight} {Expiry.ToString("yyMMdd", CultureInfo.InvariantCulture)} {Value}";
        }
    }
}
