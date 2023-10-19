using System;
using QuantConnect.Securities.Option;
using QuantConnect.Indicators;
using QuantConnect.Securities.Equity;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class UnderlyingMovedX : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        public Symbol Symbol { get; }
        public Symbol Underlying { get => Option == null ? Option.Underlying.Symbol : Symbol; }
        public Option? Option { get; internal set; }
        public Equity Equity { get; internal set; }
        public delegate void UnderlyingMovedXEventHandler(object sender, Symbol symbol);
        public event UnderlyingMovedXEventHandler UnderlyingMovedXEvent;
        public decimal ReferencePrice { get; internal set; }

        private decimal _changeToAlert;

        public int WarmUpPeriod => 0;
        public override bool IsReady => true;

        /// <summary>
        /// Emits event whenever underlying's return is x since inception or last event submitted.
        /// </summary>
        /// <param name="equity"></param>
        /// <param name="algo"></param>
        /// <param name="window"></param>
        public UnderlyingMovedX(Equity equity, decimal changeToAlert = 0.002m) : base($"UnderlyingMovedXBP {equity.Symbol}")
        {
            Symbol = equity.Symbol;
            Equity = equity;
            _changeToAlert = changeToAlert;
        }

        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            if (input.Value == 0) return 0;
            if (ReferencePrice == 0)
            {
                ReferencePrice = input.Value;
            }
            decimal r = input.Value / ReferencePrice;
            if (Math.Abs(r - 1)  > _changeToAlert)
            {
                //Console.Write($"{Symbol} UnderlyingMovedX Event invoked");
                UnderlyingMovedXEvent?.Invoke(this, Symbol);
                ReferencePrice = input.Value;
            }
            return r;
        }
    }
}
