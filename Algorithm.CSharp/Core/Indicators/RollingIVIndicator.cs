using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Indicators;


namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class RollingIVIndicator<T> : IIndicatorWarmUpPeriodProvider
        where T : IIVBidAsk
    {
        public Symbol Symbol { get; }
        public T Current { get => GetCurrent(); }
        public double LongMean { get => totalIV / Window.Count; }
        public T Last { get; protected set; }

        public readonly List<T> Window = new();
        private IEnumerable<T> WindowExOutliers;
        private readonly int _sizeCurrent = 100;
        //private readonly TimeSpan windowSize = TimeSpan.FromDays(3);
        public bool IsReady => Window.Count >= 1;// _sizeCurrent;
        public bool IsReadyLongMean => Window.Count >= 1;// _size;
        public int WarmUpPeriod => _size;
        public int Samples { get; internal set; }

        private readonly int _size;
        private readonly double _threshold;
        private double currentTotalIV { get; set; }
        private double totalIV { get; set; }

        public double EWMA;
        public double EWMAPrevious;

        public double EWMASlow;
        public double EWMASlowPrevious;
        // Now also need how many ZScores current IV is off from EWMA.
        public double ZScore;

        //public int halfLife = 2 * 6.5 * 60 * 60;
        // Count in events. HalfLife of 1,000 IV changes.
        // private double alpha = Math.Exp(Math.Log((0.5) / 1000));
        private double alpha = 0.005;
        private double gamma = 0.0001;
        private double eps;

        private double alphaSlow = 0.001;
        private double gammaSlow = 0.0;
        // Refactor to TimeDelta of 3 days to relate to Jupyter plots. Future, refactor to event driven, so not by timedelta
        // So need a never ending window with warm up! Warmup currently in Minute??
        // May want to adjust alpha by counting how many samples were recorded in last 2 days.
        //private double alpha = 0.05;

        public RollingIVIndicator(int size, Symbol symbol, double threshold = 0.15)
        {
            Symbol = symbol;
            _size = size;
            _threshold = threshold;
        }

        private T? GetCurrent()
        {
            return Window.Any() ? Window.Last() : default(T);
        }

        public double GetCurrentExOutlier(int nLast = 100)
        {
            if (IsReady)
            {
                double meanBidIV = currentTotalIV / _sizeCurrent;
                WindowExOutliers = Window.TakeLast(nLast).Where(x =>
                    x.IV != null && x.IV != 0 &&
                    Math.Abs((double)x.IV - meanBidIV) / meanBidIV < _threshold
                ); ;
                if (WindowExOutliers.Any())
                {
                    return WindowExOutliers.Select(x => x.IV).Average();
                }
            }
            return Last.IV;
        }

        public void Update(T item)
        {
            if (item == null || item.Time <= Last?.Time) return;
            Add(item);
            //UpdateEWMA(item, EWMA, EWMAPrevious, alpha, gamma);
            UpdateEWMA(item);
            UpdateEWMASlow(item);
        }

        public bool Add(T item)
        {
            // Sampling events, delta IV of > 1%
            //if (Math.Abs((double)item.IV - (Last?.IV ?? 0)) < 0.001) return false;

            if (Samples >= _sizeCurrent)
            {
                currentTotalIV -= (double)Window.First().IV;
            }

            if (Samples >= _size)
            {
                totalIV -= (double)Window.First().IV;
                Window.RemoveAt(0);
            }
            Last = item;
            Window.Add(item);
            currentTotalIV += item.IV;
            totalIV += item.IV;
            Samples += 1;
            return true;
        }

        public void Reset()
        {
            Window.Clear();
            Samples = 0;
            currentTotalIV = 0;
            totalIV = 0;
        }

        private void UpdateEWMA(T item)
        {
            if (item.IV > 0)  // Consider calculating with negative interest rate to avoid 0 IV shocks.
            {
                EWMAPrevious = EWMA == 0 ? item.IV : EWMA;
                EWMA = alpha * item.IV + (1 - alpha) * EWMAPrevious;
                eps = Math.Abs(item.IV - EWMA);
                alpha = (1 - gamma) * alpha + gamma * (eps / (eps + EWMAPrevious));
            }
        }

        private void UpdateEWMASlow(T item)
        {
            if (item.IV > 0)  // Consider calculating with negative interest rate to avoid 0 IV shocks.
            {
                EWMASlowPrevious = EWMA == 0 ? item.IV : EWMASlow;
                EWMASlow = alphaSlow * item.IV + (1 - alphaSlow) * EWMASlowPrevious;
                eps = Math.Abs(item.IV - EWMASlow);
                alphaSlow = (1 - gammaSlow) * alphaSlow + gammaSlow * (eps / (eps + EWMASlowPrevious));
            }
        }

        //private void UpdateEWMA(T item, double ewma, double ewmaPrevious, double alpha, double gamma)
        //{
        //    if (item.IV > 0)  // Consider calculating with negative interest rate to avoid 0 IV shocks.
        //    {
        //        ewmaPrevious = ewma == 0 ? item.IV : ewma;
        //        ewma = alpha * item.IV + (1 - alpha) * ewmaPrevious;
        //        eps = Math.Abs(item.IV - ewma);
        //        alpha = (1 - gamma) * alpha + gamma * (eps / (eps + ewmaPrevious));
        //    }
        //}
    }
}
