using QuantConnect.Algorithm.CSharp.Core.Risk;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class FoundationsConfig : AlgoConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> Ticker { get; set; }
        public HashSet<string> LiquidateTicker { get; set; }
        public Dictionary<string, bool> SkipRunSignals { get; set; }
        public bool ExecuteManualOrderInstructions { get; set; }
        public Dictionary<string, decimal> BacktestingHoldings { get; set; }
        public int VolatilityPeriodDays { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMax { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMaxSignal { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMin { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMinSignal { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMargin { get; set; }
        public decimal ScopeContractMoneynessITM { get; set; }
        public int ScopeContractMinDTE { get; set; }
        public int ScopeContractMaxDTE { get; set; }
        public int ScopeContractIsLiquidDays { get; set; }
        public double ZMRiskAversion { get; set; }
        public double ZMProportionalTransactionCost { get; set; }
        public Dictionary<string, DiscountParams> DiscountParams { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalLong { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalShort { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdIntercept { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdGammaFactor { get; set; }
        public Dictionary<string, double> IVSurfaceRelativeStrikeAlpha { get; set; }
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
        // Pegged Orders not in use because IB limits number of simultaneous pegged orders.
        //public decimal PeggedToStockDeltaRangeOffsetFactor { get => 0.05; set; } -> 0.05
        //public decimal MinimumIVOffsetBeforeUpdatingPeggedOptionOrder { get; set; }  -> 0.003
        public Dictionary<string, int> HedgingMode { get; set; }
        public Dictionary<string, bool> UpcomingEventLongIV { get; set; }
        public Dictionary<string, int> UpcomingEventCalendarSpreadStartDaysPrior { get; set; }
        public int CalendarSpreadPeriodDays { get; set; }
        public decimal EquityShortingRate { get; set; }
        public decimal DiscountRatePortfolioCAGR { get; set; }
        public decimal DiscountRateMarket { get; set; }
        public Dictionary<string, double> MinUtility { get; set; }
        public Dictionary<string, decimal> CorrelationSpotVolatility { get; set; }
        public Dictionary<string, decimal> VolatilityOfVolatility { get; set; }
        public Dictionary<string, double> SlopeNeg { get; set; }
        public Dictionary<string, double> SlopePos { get; set; }
        public Dictionary<string, double> ZeroSDUtil { get; set; }
        public Dictionary<string, double> UtilBidTaperer { get; set; }
        public Dictionary<string, decimal> MaxSpreadDiscount { get; set; }
        public Dictionary<string, Dictionary<string, List<double>>> DeltaAdjustmentParameters { get; set; }
        public int LimitOrderUpdateBeforeMarketOrderConversion { get; set; }
        public decimal MaxSpreadForMarketOrderHedging { get; set; }
        public Dictionary<string, List<List<double>>> SweepLongSchedule { get; set; }
        public Dictionary<string, List<List<double>>> SweepShortSchedule { get; set; }
        public Dictionary<string, List<double>> KalmanScopedMoneyness { get; set; }
        public Dictionary<string, double> KalmanAlphaBid { get; set; }
        public Dictionary<string, double> KalmanAlphaAsk { get; set; }
        public Dictionary<string, int> PrepareEarningsPeriodDays { get; set; }
    }
}
