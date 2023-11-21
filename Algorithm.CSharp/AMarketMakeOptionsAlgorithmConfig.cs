using QuantConnect.Algorithm.CSharp.Core.Risk;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    public class AMarketMakeOptionsAlgorithmConfig : AlgoConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> Ticker { get; set; }
        public HashSet<string> LiquidateTicker { get; set; }
        public Dictionary<string, bool> SkipRunSignals {  get; set; }
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
        public decimal RiskLimitGammaScalpDeltaTotalLong { get; set; }
        public decimal RiskLimitGammaScalpDeltaTotalShort { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalLong { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalShort { get; set; }
        public Dictionary<string, decimal> RiskLimitHedgeDeltaTotalLong { get; set; }
        public Dictionary<string, decimal> RiskLimitHedgeDeltaTotalShort { get; set; }
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
        public Dictionary<string, bool> GammaScalpingEnabled { get; set; }
        public Dictionary<string, decimal> TrailingHedgePct { get; set; }
        public Dictionary<string, decimal> MaxOptionOrderQuantity { get; set; }
        public Dictionary<string, decimal> TargetMaxEquityPositionUSD { get; set; }
        public Dictionary<string, decimal> SignalQuantityFraction { get; set; }
        public Dictionary<string, decimal> TargetMarginAsFractionOfNLV { get; set; }
        public Dictionary<string, double> MarginUtilScaleFactor { get; set; }
        public int MinSubmitRequestsUnprocessedBlockingSubmit { get; set; }
        public int MinCancelRequestsUnprocessedBlockingSubmit { get; set; }
        public decimal PeggedToStockDeltaRangeOffsetFactor { get; set; }
        public decimal MinimumIVOffsetBeforeUpdatingPeggedOptionOrder { get; set; }
        public Dictionary<string, int> HedgingMode { get; set; }
        public Dictionary<string, bool> UpcomingEventLongIV { get; set; }
        public Dictionary<string, int> UpcomingEventCalendarSpreadStartDaysPrior { get; set; }
        public bool SetBacktestingHoldings { get; set; }
    }
}
