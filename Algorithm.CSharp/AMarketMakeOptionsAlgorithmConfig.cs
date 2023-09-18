using QuantConnect.Algorithm.CSharp.Core.Risk;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    public class AMarketMakeOptionsAlgorithmConfig
    {
        public HashSet<string> HedgeTicker { get; set; }
        public HashSet<string> OptionTicker { get; set; }
        public HashSet<string> LiquidateTicker { get; set; }
        public int OptionOrderQuantityDflt { get; set; }
        public int VolatilityPeriodDays { get; set; }
        public decimal scopeContractStrikeOverUnderlyingMax { get; set; }
        public decimal scopeContractStrikeOverUnderlyingMaxSignal { get; set; }
        public decimal scopeContractStrikeOverUnderlyingMin { get; set; }
        public decimal scopeContractStrikeOverUnderlyingMinSignal { get; set; }
        public decimal scopeContractStrikeOverUnderlyingMargin { get; set; }
        public decimal scopeContractMoneynessITM { get; set; }
        public int scopeContractMinDTE { get; set; }
        public int scopeContractMaxDTE { get; set; }
        public int scopeContractIsLiquidDays { get; set; }
        public double ZMRiskAversion { get; set; }
        public double ZMProportionalTransactionCost { get; set; }
        public Dictionary<string, DiscountParams> DiscountParams { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalLong { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalShort { get; set; }
        public Dictionary<string, double> IVSurfaceRelativeStrikeAlpha { get; set; }
        public double SurfaceVerticalResetThreshold { get; set; }
        public int WarmUpDays { get; set; }
        public bool LogOrderUpdates { get; set; }
    }
}
