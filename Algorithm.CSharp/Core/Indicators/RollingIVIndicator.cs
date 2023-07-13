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
        public double Current { get => GetCurrent(); }
        public double LongMean { get => totalIV / Window.Count; }
        public T Last { get; protected set; }

        public readonly List<T> Window = new();
        private IEnumerable<T> WindowExOutliers;
        private readonly int _sizeCurrent = 100;
        public bool IsReady => Window.Count >= 1;// _sizeCurrent;
        public bool IsReadyLongMean => Window.Count >= 1;// _size;
        public int WarmUpPeriod => _size;
        public int Samples { get; internal set; }

        private readonly int _size;
        private readonly double _threshold;
        private double currentTotalIV { get; set; }
        private double totalIV { get; set; }

        public RollingIVIndicator(int size, Symbol symbol, double threshold = 0.15)
        {
            Symbol = symbol;
            _size = size;
            _threshold = threshold;
        }

        private double GetCurrent()
        {
            return Window.Last().IV;
        }

        public double GetCurrentExOutlier()
        {
            if (IsReady)
            {
                double meanBidIV = currentTotalIV / _sizeCurrent;
                WindowExOutliers = Window.TakeLast(_sizeCurrent).Where(x =>
                    x.IV != 0 &&
                    Math.Abs(x.IV - meanBidIV) / meanBidIV < _threshold
                );
                if (WindowExOutliers.Any())
                {
                    // To be revised. EWMA. zero Call/Put arbitrage => more samples.                   
                    return WindowExOutliers.Select(x => x.IV).Average();
                }
            }
            return Last.IV;
        }

        public void Update(T item)
        {
            if (item == null || item.Time <= Last?.Time || item.IV == 0) return;
            Add(item);
        }

        public bool Add(T item)
        {
            // Sampling events, delta IV of > 1%
            if (Math.Abs(item.IV - (Last?.IV ?? 0)) < 0.001) return false;

            if (Samples >= _sizeCurrent)
            {
                currentTotalIV -= Window.First().IV;
            }

            if (Samples >= _size)
            {
                totalIV -= Window.First().IV;
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
    }
}
