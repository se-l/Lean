using System;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class ManualOrderInstruction
    {
        public string Symbol { get; set; }
        public decimal TargetQuantity { get; set; }
        public double? Utility { get; set; }
        public double? Volatility { get; set; }
        public TimeSpan[][]? TimeToTrade { get; set; }
    }
}
