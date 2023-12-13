using System;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class ManualOrderInstruction
    {
        public string Symbol { get; set; }
        public decimal TargetQuantity { get; set; }
        public decimal SpreadFactor { get; set; }
    }
}
