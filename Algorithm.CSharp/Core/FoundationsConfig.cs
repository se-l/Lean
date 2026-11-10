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
        public bool DontHedge { get; set; }

        /// <summary>
        /// Force the off-thread signal runner in a backtest. Off by default: a backtest applies the
        /// batch on a later slice, so order timing shifts and the run is no longer deterministic.
        /// Live always runs async regardless of this flag.
        /// </summary>
        public bool SignalsAsync { get; set; } = false;
        public bool ExecuteManualOrderInstructions { get; set; }
        public Dictionary<string, decimal> BacktestingHoldings { get; set; }
        public int VolatilityPeriodDays { get; set; }

        /// <summary>
        /// How many target portfolios per underlying are kept after a response: the best by
        /// objective (maxObjective), the rest dropped. Live-tunable over the config channel.
        /// </summary>
        public int MaxTargetPortfolios { get; set; } = 10;
        public decimal ScopeContractStrikeOverUnderlyingMax { get; set; }
        public decimal ScopeContractStrikeOverUnderlyingMin { get; set; }
        public int ScopeContractMinDTE { get; set; }
        public int ScopeContractMaxDTE { get; set; }
        public Dictionary<string, DiscountParams> DiscountParams { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalLong { get; set; }
        public decimal RiskLimitEODDelta100BpUSDTotalShort { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdIntercept { get; set; }
        public Dictionary<string, decimal> TotalDeltaHedgeThresholdGammaFactor { get; set; }
        public bool LogOrderUpdates { get; set; }
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
        public Dictionary<string, List<HedgeScheduleCfg>> HedgeSchedules { get; set; }
        public Dictionary<string, int> PrepareEarningsPeriodDays { get; set; }
        public bool BacktestNearRealtimePacingEnabled { get; set; }
        public Dictionary<string, double> MaxDelta100BpUSDSignalQuantity { get; set; }
        public Dictionary<string, bool> PricerOverridePricesWithPresumedIvFillDefensively { get; set; }
        public Dictionary<string, Dictionary<string, string>> TimeRangeReduceAbsDeltaPosition { get; set; }
        public Dictionary<string, string> TimeStartKfBeforeRelease { get; set; }
        public Dictionary<string, string> EarningsUtilityTargetHoldingsAfterReleaseStartTimeBuy { get; set; }
        public Dictionary<string, string> EarningsUtilityTargetHoldingsAfterReleaseStartTimeSell { get; set; }
        public double BufferIntraSpreadRatioSpreadThreshold {  get; set; }
        public int BufferIntraSpreadWaitingPeriodSeconds {  get; set; }
        public bool BufferIntraSpreadQuotes {  get; set; }
        
        public int RequestContractsSchedulesBacktestNMaximum;
        public Dictionary<string, List<RequestContractsScheduleCfg>> RequestContractsSchedules;
        public int MinutesBeforeMarketCloseHedgeDeltaFlat { get; set; }
        public int MinutesAfterOpenMMWindowStarts { get; set; }
        public int MinutesBeforeCloseMMWindowEnds { get; set; }
        public int MinutesBeforeCloseHedgeToAcrossDs { get; set; }
        public int MinutesBeforeCloseIsPreEarningsReleaseEOD { get; set; }
        public Dictionary<string, int> EquityHedgeMode { get; set; }

        /// <summary>
        /// Emit every utility component as an OTLP gauge. Replaces the per-underlying
        /// UtilityOrder.csv dump; switch off if the metric volume is too high.
        /// </summary>
        public bool OtelUtilityMetrics { get; set; } = true;
        /// <summary>
        /// One pricer round trip for ALL greeks of a node (Command.Greeks) instead of one per greek.
        /// Off falls back to the historical per-greek calls (useful against an old pricer image).
        /// </summary>
        public bool PricerBatchGreeks { get; set; } = true;

        /// <summary>
        /// Smile term in the hedge delta: "surface" adds vega × dσ/dS from the RISK surface to the
        /// sticky-strike delta; "none" keeps the historical sticky-strike-only hedge.
        /// </summary>
        public string DeltaHedgeSkewTerm { get; set; } = "surface";

        /// <summary>
        /// Multiplier on the smile term. 1 = the surface's own dσ/dS. The fitted surface is parameterised
        /// in k = ln(K/F), so its spot derivative is the sticky-moneyness slope; the empirical spot/vol
        /// relation (leverage) can carry the opposite sign. Use -1 / a smaller magnitude to calibrate the
        /// term against hedge PnL without a rebuild.
        /// </summary>
        public decimal DeltaHedgeSkewScale { get; set; } = 1.0m;

        /// <summary>Utility orders are reused per (contract, quantity, spot bucket, iv bucket, portfolio version).</summary>
        public bool UtilityCacheEnabled { get; set; } = true;

        /// <summary>Spot grid width for the utility cache, in basis points (5 = 0.05%).</summary>
        public double UtilityCacheSpotBucketBps { get; set; } = 5.0;

        /// <summary>IV grid width for the utility cache in vol points (0.01 = 1 vol point).</summary>
        public double UtilityCacheIvBucket { get; set; } = 0.01;

        /// <summary>Entries kept before the oldest half is dropped.</summary>
        public int UtilityCacheMaxKeys { get; set; } = 20000;

        public int MaxDesiredOrders { get; set; }

        /// <summary>
        /// Maximum age of a computed signal batch at apply time. LIVE measures wall time (a stall
        /// of N ms is N ms of market, even if the data frontier never advanced); BACKTEST measures
        /// simulation time, so a slow or paced machine does not silently drop signals. A batch past
        /// this age is dropped, never applied.
        /// </summary>
        public int MaxSignalBatchAgeMs { get; set; } = 5_000;
    }
}
