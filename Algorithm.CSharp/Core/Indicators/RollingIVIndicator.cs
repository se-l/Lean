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
        public double Current { get; protected set; } = 0;
        public T Last { get; protected set; }

        private readonly List<T> Window = new();
        public bool IsReady => Window.Count == _size;
        public int WarmUpPeriod => _size;

        private readonly int _size;
        private readonly double _threshold;
        private T _mostRecentlyRemoved;
        private double currentTotalIV { get; set; }
        private int _tail;

        public RollingIVIndicator(int size, Symbol symbol, double threshold = 0.15)
        {
            Symbol = symbol;
            _size = size;
            _threshold = threshold;
        }

        public double Update(T item)
        {
            if (item == null) return Current;

            currentTotalIV += item.IV;
            Add(item);
            if (IsReady)
            {
                currentTotalIV -= _mostRecentlyRemoved?.IV ?? 0;
                double meanBidIV = currentTotalIV / _size;
                var exOutliers = Window.Where(x =>
                    x.IV != 0 && 
                    Math.Abs(x.IV - meanBidIV) / meanBidIV < _threshold
                );
                if (exOutliers.Any())
                {
                    // To be revised. EWMA. zero Call/Put arbitrage => more samples.                   
                    Current = exOutliers.Select(x => x.IV).Average();
                }
            }
            return Current;
        }

        public void Add(T item)
        {
            if (Window.Count == _size)
            {
                // keep track of what's the last element
                // so we can reindex on this[ int ]
                _mostRecentlyRemoved = Window[_tail];
                Window[_tail] = item;
                _tail = (_tail + 1) % _size;
            }
            else
            {
                Window.Add(item);
            }
        }
    }
}
