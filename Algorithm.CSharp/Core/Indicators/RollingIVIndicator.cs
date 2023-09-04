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
        public bool IsReady => Window.Count >= 1;// _sizeCurrent;
        public int WarmUpPeriod => _size;
        public int Samples { get; internal set; }

        private readonly int _size;

        public RollingIVIndicator(int size, Symbol symbol)
        {
            Symbol = symbol;
            _size = size;
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
            if (Samples >= _size)
            {
                Window.RemoveAt(0);
            }
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
