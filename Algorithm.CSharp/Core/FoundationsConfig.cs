using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

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
        public decimal ScopeContractStrikeOverUnderlyingMin { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMargin { get; set; }
        public int ScopeContractMinDTE { get; set; }
        public int ScopeContractMaxDTE { get; set; }
        public int ScopeContractIsLiquidDays { get; set; }
        public Dictionary<string, DiscountParams> DiscountParams { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalLong { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalShort { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdIntercept { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdGammaFactor { get; set; }
        public int WarmUpDays { get; set; }
        public bool LogOrderUpdates { get; set; }
        public bool SkipWarmUpSecurity { get; set; }
        public Dictionary<string, List<TargetRisk>> PutCallRatioTargetRisks { get; set; }
        public Dictionary<string, double> EOD2SODATMIVJumpThreshold { get; set; }
        public Dictionary<string, double[]> IntradayIVSlopeTrendingRange { get; set; }
        public Dictionary<string, bool> GammaScalpingEnabled { get; set; }
        public Dictionary<string, decimal> TrailingHedgePct { get; set; }
        public Dictionary<string, decimal> MaxOptionOrderQuantity { get; set; }
        public Dictionary<string, decimal> TargetMaxEquityPositionUSD { get; set; }
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
        public decimal EquityShortingRate { get; set; }
        public decimal DiscountRatePortfolioCAGR { get; set; }
        public decimal DiscountRateMarket { get; set; }
        public Dictionary<string, double> MinUtility { get; set; }
        public Dictionary<string, decimal> CorrelationSpotVolatility { get; set; }
        public Dictionary<string, decimal> VolatilityOfVolatility { get; set; }
        public Dictionary<string, Dictionary<string, List<double>>> DeltaAdjustmentParameters { get; set; }
        public int LimitOrderUpdateBeforeMarketOrderConversion { get; set; }
        public decimal MaxSpreadForMarketOrderHedging { get; set; }
        public Dictionary<string, List<SweepScheduleCfg>> SweepSchedules { get; set; }
        public Dictionary<string, double> SweepWorstIVUtilityBump { get; set; }
        public Dictionary<string, List<double>> KalmanScopedMoneyness { get; set; }
        public Dictionary<string, double> KalmanAlphaBid { get; set; }
        public Dictionary<string, double> KalmanAlphaAsk { get; set; }
        public Dictionary<string, int> PrepareEarningsPeriodDays { get; set; }
        public int BacktestingBrokerageLatency {  get; set; }
        public int MinHistoryDaysUnderlyingForScoping { get; set; }
        public Dictionary<string, double> MaxDelta100BpUSDSignalQuantity { get; set; }
        public bool UseKalmanFilterBeforeEarningsRelease { get; set; }
        public Dictionary<string, bool> PricerOverridePricesWithPresumedIvFillDefensively { get; set; }
        public Dictionary<string, Dictionary<string, string>> TimeRangeReduceAbsDeltaPosition { get; set; }
        public Dictionary<string, string> TimeStartKfBeforeRelease { get; set; }
        public Dictionary<string, string> EarningsUtilityTargetHoldingsAfterReleaseStartTimeBuy { get; set; }
        public Dictionary<string, string> EarningsUtilityTargetHoldingsAfterReleaseStartTimeSell { get; set; }
        public double BufferIntraSpreadRatioSpreadThreshold {  get; set; }
        public int BufferIntraSpreadWaitingPeriodSeconds {  get; set; }
        public bool BufferIntraSpreadQuotes {  get; set; }
        
        public Dictionary<string, List<RequestContractsScheduleCfg>> RequestContractsSchedules;
        public Dictionary<string, int> KalmanMinUpdatesToReady { get; set; }
        public int MinutesBeforeMarketCloseHedgeDeltaFlat { get; set; }
        public int MinutesAfterOpenMMWindowStarts { get; set; }
        public int MinutesBeforeCloseMMWindowEnds { get; set; }
        public int MinutesBeforeCloseHedgeToAcrossDs { get; set; }
        public int MinutesBeforeCloseIsPreEarningsReleaseEOD { get; set; }
        public Dictionary<string, int> EquityHedgeMode { get; set; }
        public Dictionary<string, int> IVSpreadSMAPeriod { get; set; }
    }
}
