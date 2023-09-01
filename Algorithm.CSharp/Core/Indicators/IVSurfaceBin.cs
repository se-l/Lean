using System;
using System.Linq;
using QuantConnect.Securities.Option;
using Accord.Math;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    // All too imperative design. Refactor into getters. Correct Lazy updates on demand
    public class Bin<T> where T : IVBidAsk, IIVBidAsk
    {
        private readonly Foundations algo;
        public readonly RollingIVSurfaceRelativeStrike<T> Surface;
        public readonly string Side;
        public readonly bool IsOTM;
        public decimal Value;
        public double? IV;
        public double? EWMA;
        public double? EWMAPrevious;
        public decimal Strike;
        public DateTime Expiry;
        public DateTime LastUpdatedEWMA;
        public decimal MidPrice;
        public double Slope;
        private readonly double alpha = 0.005;
        private readonly TimeSpan samplingPeriod = TimeSpan.FromMinutes(5);

        private DateTime LastUpdatedEWMAPrevious;

        public Bin(Foundations algo, RollingIVSurfaceRelativeStrike<T> surface, string side, decimal value, double? iv, DateTime expiry, DateTime time, decimal midPrice, bool isOTM, double slope = 0)
        {
            this.algo = algo;
            Surface = surface;
            Side = side;
            Value = value;
            IV = iv;
            Expiry = expiry;
            LastUpdatedEWMA = time;
            MidPrice = midPrice;
            Slope = slope;
            IsOTM = isOTM;
        }
        public void Update(IVBidAsk item)
        {
            if (item.Time == DateTime.MinValue || item.Price == 0 || item.IV == 0)
            {
                return; 
            }

            EWMAPrevious = GetPreviousEWMA(item);
            EWMA = alpha * item.IV + (1 - alpha) * EWMAPrevious;
            // Adjust alpha if gamma > 0
            // eps = Math.Abs(item.IV - EWMA);
            // alpha = (1 - gamma) * alpha + gamma * (eps / (eps + EWMAPrevious));
            UpdateEWMAPrevious(item);
        }
        private double? GetPreviousEWMA(IVBidAsk item)
        {
            // Previous EWMA was calculated within sample Period. Expected to be most common scenario.
            if (LastUpdatedEWMAPrevious.TimeOfDay >= new TimeSpan(15, 55, 0) || LastUpdatedEWMAPrevious >= item.Time - samplingPeriod)
            {
                return EWMAPrevious;
            }
            // Initial EWMA
            else if (LastUpdatedEWMAPrevious == DateTime.MinValue)
            {
                return item.IV;
            }
            // EWMAPrevious could be too old in the past, rather interpolate recently updated strikes then.
            // Little issue. Neighbors are already EWMA smoothed. Would not want to 'double' smooth.
            else
            {
                double? interpolated = Surface.InterpolateNearestBins(this);
                if (interpolated != null)
                {
                    LastUpdatedEWMA = item.Time; // Approximation. Smallest timestamps of bins EWMA is source from.
                }
                return EWMAPrevious = interpolated ?? EWMAPrevious;
            }
        }
        private void UpdateEWMAPrevious(IVBidAsk item)
        {
            // Too frequent EWMA would turn signal noisier, hence keep only save latest ewma to previousEWMA every x Periods.
            if (LastUpdatedEWMAPrevious + samplingPeriod < item.Time)
            {
                EWMAPrevious = EWMA;
                LastUpdatedEWMAPrevious = item.Time;
            }
        }

        private decimal Bin2Strike(decimal bin)
        {
            return Math.Round(bin * MidPrice / 100, 0);
        }
        /// <summary>
        /// Fetch Bid / Ask / Calc IV and update EWMA... No interpolation here. Stack Overflow
        /// </summary>
        public bool Refresh()
        {
            decimal strike = Bin2Strike(Value);
            var contracts = algo.Securities.Values.Where(s =>
                s.Type == SecurityType.Option
                && s.Symbol.Underlying == Surface.Underlying
                && ((Option)s).Symbol.ID.Date == Expiry
                && ((Option)s).Symbol.ID.StrikePrice == strike
                && (IsOTM ? ((Option)s).GetPayOff(MidPrice) < 0 : ((Option)s).GetPayOff(MidPrice) > 0 )
            );
            if (contracts.Any())
            {
                var option = contracts.First();
                Symbol contract = option.Symbol;

                if (Side == "bid")
                {
                    Update(algo.IVBids[contract].Refresh()); // to be deleted with registrated event
                    return true;

                }
                else if (Side == "ask")
                {
                    Update(algo.IVAsks[contract].Refresh()); // to be deleted with registrated event
                    return true;
                }
                else
                {
                    throw new Exception("Invalid side");
                }
            }
            return false;
        }

        public bool IsReady
        {
            get
            {
                if (algo.IsWarmingUp)
                {
                    throw new NotSupportedException("IsReady during warm up is not supported currently.");
                }
                return LastUpdatedEWMA >= algo.Time - samplingPeriod;
            }
        }
    }
}
