using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// Band by Security (Option Contract), Underlying, Portfolio
    /// </summary>
    public class HedgeBand
    {
        public decimal DeltaLongUSD { get; } = 1_000;
        public decimal DeltaShortUSD { get; } = -200;
        public decimal DeltaTargetUSD { get { return GetDeltaTargetUSD(); } }
        //public decimal GammaLongUSD { get; } = 1_000;
        //public decimal GammaShortUSD { get; } = 1_000;
        //public decimal ThetaLongUSD { get; } = 1_000;
        //public decimal ThetaShortUSD { get; } = 1_000;
        //public decimal VegaLongUSD { get; } = 1_000;
        //public decimal VegaShortUSD { get; } = 1_000;

        public HedgeBand() {}

        private decimal GetDeltaTargetUSD() { return (DeltaLongUSD + DeltaShortUSD) / 2; }
    }
}
