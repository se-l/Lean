using System;
using QuantConnect.Indicators;
using QuantConnect.Securities.Equity;
using System.Collections.Generic;
using MathNet.Numerics.Statistics;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    /// <summary>
    /// Subscribes to EOD ATM IV from IV Surfaces. Unlike the surface, stores those to create long-term history.
    /// </summary>
    public class AtmIVIndicator : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        private readonly Foundations _algo;
        private readonly QuoteSide _side;
        public QuoteSide Side { get => _side; }
        public Equity Underlying { get; }
        public Symbol Symbol { get => Underlying.Symbol; }
        public int WarmUpPeriod => _window;

        private Dictionary<DateTime, double> AtmIVsBid;
        private Dictionary<DateTime, double> AtmIVsAsk;        
        public double Current => (AtmIVsBid.Values.Where(x => x != 0).Mean() + AtmIVsAsk.Values.Where(x => x != 0).Mean()) / 2;
        private readonly int _window;
        public override bool IsReady => AtmIVsAsk.Values.Count >= _window;

        public AtmIVIndicator(Foundations algo, Equity underlying ) : base($"AtmIVIndicator {underlying.Symbol}")
        {
            _algo = algo;
            _window = _algo.Cfg.AtmIVIndicatorWindow.TryGetValue(underlying.Symbol, out _window) ? _window : _algo.Cfg.AtmIVIndicatorWindow[CfgDefault];
        }

        public void Update(DateTime date, double iv, QuoteSide side)
        {
            switch (side)
            {
                case QuoteSide.Bid:
                    AtmIVsBid[date.Date] = iv;
                    break;
                case QuoteSide.Ask:
                    AtmIVsAsk[date.Date] = iv;
                    break;
                default:
                    throw new NotImplementedException();
            }

            RemoveOldDates();
        }

        public void RemoveOldDates()
        {
            foreach (var date in AtmIVsBid.Keys)
            {
                if (date < _algo.Time.Date.AddDays(-_window))
                {
                    AtmIVsBid.Remove(date);
                }
            }
            foreach (var date in AtmIVsAsk.Keys)
            {
                if (date < _algo.Time.Date.AddDays(-_window))
                {
                    AtmIVsAsk.Remove(date);
                }
            }
        }
        protected override decimal ComputeNextValue(IndicatorDataPoint input) => 0;
    }
}
