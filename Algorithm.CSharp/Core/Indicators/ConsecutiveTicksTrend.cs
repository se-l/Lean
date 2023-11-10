using System;
using QuantConnect.Indicators;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class ConsecutiveTicksTrend : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        public Symbol Symbol { get; }
        public Equity Equity { get; internal set; }
        public event EventHandler<ConsecutiveTicksTrend> TickTrendEventHandler;
        private decimal _prevPrice;
        public decimal NConsecutiveTicks { get => _nConsecutiveTicks; }
        private decimal _nConsecutiveTicks;
        private int _ticksDirection;
        public decimal Value;
        public int WarmUpPeriod => 0;
        public override bool IsReady => true;

        /// <summary>
        /// Just emits how many times MidPrice changes up vs it moved down. Reset whenever direction changed. So momentum like indicator..
        /// </summary>
        /// <param name="algo"></param>
        /// <param name="window"></param>
        public ConsecutiveTicksTrend(Equity equity) : base($"ConsecutiveTicksTrend Indicator {equity.Symbol}")
        {
            Symbol = equity.Symbol;
        }
        // input is expected to be midprice
        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (input == 0 || input == null) return Value;

            decimal midPrice = input; // _algo.MidPrice(Symbol);
            if (midPrice == _prevPrice) return _nConsecutiveTicks;

            int tickDirection = midPrice > _prevPrice ? 1 : -1;
            if (tickDirection == _ticksDirection) 
            {
                _nConsecutiveTicks ++;
            }
            else
            {
                _ticksDirection = tickDirection;
                _nConsecutiveTicks = 1;
            }

            TickTrendEventHandler?.Invoke(this, this);
            Value = tickDirection * _nConsecutiveTicks;
            return Value;
        }
    }
}
