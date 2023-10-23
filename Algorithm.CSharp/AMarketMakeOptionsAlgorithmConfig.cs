using QuantConnect.Algorithm.CSharp.Core.Risk;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp
{
    public class AMarketMakeOptionsAlgorithmConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> HedgeTicker { get; set; }
        public HashSet<string> OptionTicker { get; set; }
        public HashSet<string> LiquidateTicker { get; set; }
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
        public bool SkipWarmUpSecurity { get; set; }
        public Dictionary<string, double> MinZMOffset { get; set; }
        public Dictionary<string, decimal> MaxZMOffset { get; set; }
        public int PutCallRatioWarmUpDays { get; set; }
        public Dictionary<string, List<TargetRisk>> PutCallRatioTargetRisks { get; set; }
        public Dictionary<string, double> EOD2SODATMIVJumpThreshold { get; set; }
        public Dictionary<string, double[]> IntradayIVSlopeTrendingRange { get; set; }
        public Dictionary<string, int> DaysBeforeConsideringEarningsAnnouncement { get; set; }
        public Dictionary<string, int> AtmIVIndicatorWindow { get; set; }
        public Dictionary<string, double> EarningsAnnouncementUtilityMinAtmIVElevation { get; set; }
        public Dictionary<string, int> EarningsAnnouncementUtilityMinDTE { get; set; }
        public Dictionary<string, decimal> TrailingHedgePct { get; set; }
        public Dictionary<string, decimal> MaxOptionOrderQuantity { get; set; }
        public Dictionary<string, decimal> TargetMaxEquityPositionUSD { get; set; }
        public Dictionary<string, decimal> SignalQuantityFraction { get; set; }
        public Dictionary<string, decimal> TargetMarginAsFractionOfNLV { get; set; }
        public Dictionary<string, double> MarginUtilScaleFactor { get; set; }
        public int MinSubmitRequestsUnprocessedBlockingSubmit { get; set; }
        public int MinCancelRequestsUnprocessedBlockingSubmit { get; set; }

        public AMarketMakeOptionsAlgorithmConfig OverrideWithEnvironmentVariables()
        {
            // Loop over all getter attribuetes
            foreach (var attr in typeof(AMarketMakeOptionsAlgorithmConfig).GetProperties())
            {
                // Get the value of the environment variable
                var envValue = Environment.GetEnvironmentVariable(attr.Name);
                if (envValue != null)
                {
                    // Convert the value to the correct type
                    if (attr.PropertyType == typeof(HashSet<string>) || attr.PropertyType == typeof(List<string>))
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        var type = attr.PropertyType;
                        var convertValue = type.GetConstructor(new[] { typeof(string) });
                        // Set the value of the property
                        attr.SetValue(this, convertedValue);
                        // Log it
                        Console.WriteLine($"AMarketMakeOptionsAlgorithmConfig: Overriding {attr.Name} with {convertedValue}");
                    } else
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        // Set the value of the property
                        attr.SetValue(this, convertedValue);
                        // Log it
                        Console.WriteLine($"AMarketMakeOptionsAlgorithmConfig: Overriding {attr.Name} with {convertedValue}");
                    }                    
                }
            }
            return this;
        }
    }
}
