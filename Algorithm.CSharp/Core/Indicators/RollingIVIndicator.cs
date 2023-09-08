using System.Collections.Generic;
using System.Linq;
using QuantConnect.Indicators;


namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class RollingIVIndicator<T> : IIndicatorWarmUpPeriodProvider
        where T : IVQuote
    {
        public Symbol Symbol { get; }
        public T Current { get => GetCurrent(); }
        public T Last { get; protected set; }

        public readonly List<T> Window = new();
        public bool IsReady => Window.Count >= 1;
        public int WarmUpPeriod => 1;
        public int Samples { get; internal set; }

        public RollingIVIndicator(int size, Symbol symbol)
        {
            Symbol = symbol;
        }

        private T? GetCurrent()
        {
            return Window.Any() ? Window.Last() : default(T);
        }
        public void Update(T item)
        {
            if (item == null || item.Time <= Last?.Time) return;
            Add(item);
        }

        public bool Add(T item)
        {
            Last = item;
            Window.Add(item);
            Samples += 1;
            return true;
        }

        public void Reset()
        {
            Window.Clear();
            Samples = 0;
        }
    }
}
