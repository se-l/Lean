using System;
using System.Linq;
using QuantConnect.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class RollingWindowIVBidAskIndicator : IIndicatorWarmUpPeriodProvider
    {
        public IVBidAsk Current { get; protected set; }
        public IVBidAsk Last { get; protected set; }
        public Symbol Symbol { get; }
        public RollingWindow<IVBidAsk> Window { get; }
        public bool IsReady => Window.IsReady;
        public int WarmUpPeriod => _size;

        private readonly int _size;
        private readonly decimal _threshold;

        public RollingWindowIVBidAskIndicator(int size, Symbol symbol, decimal threshold = 0.05m)
        {
            Symbol = symbol;
            Window = new RollingWindow<IVBidAsk>(size);
            _size = size;
            _threshold = threshold;
        }

        public IVBidAsk Update(IVBidAsk iVBidAsk)
        {
            if (iVBidAsk == null) return null;

            Window.Add(iVBidAsk);
            if (Window.IsReady)
            {
                decimal meanBidIV = (decimal)Window.Select(x => x.BidIV).Average();
                decimal meanAskIV = (decimal)Window.Select(x => x.AskIV).Average();
                var exOutliers = Window.Where(x =>
                    x.BidIV != 0 && 
                    x.AskIV != 0 && 
                    Math.Abs((decimal)x.BidIV - meanBidIV) / meanBidIV < _threshold &&
                    Math.Abs((decimal)x.AskIV - meanAskIV) / meanAskIV < _threshold
                );
                if (exOutliers.Any())
                {
                    Last = exOutliers.Last();
                    // To be revised. EWMA. zero Call/Put arbitrage => more samples.
                    Current = new IVBidAsk(Last.Time, 0, 0, 0, exOutliers.Select(x => x.BidIV).Average(), exOutliers.Select(x => x.AskIV).Average());
                }
                return Current;
            }
            else 
            { 
                return null; 
            }
        }

        public void Reset()
        {
            Window.Reset();
        }
    }
}
