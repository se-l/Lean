using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Statistics;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using QuantConnect.Securities;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        // Cached Methods
        public Func<Symbol, Symbol, int, Resolution, double> Beta;
        public Func<Symbol, Symbol, int, Resolution, double> Correlation;
        public Func<Symbol, int, Resolution, bool> IsLiquid;
        public VoidFunction HedgeWithIndex;
        public VoidArg1Function<Symbol> HedgeOptionWithUnderlying;
        public Func<Symbol, int, Resolution, IEnumerable<TradeBar>> HistoryWrap;
        public Func<Symbol, int, Resolution, IEnumerable<QuoteBar>> HistoryWrapQuote;
        public Func<Symbol, decimal> TickSize;
        public Func<decimal> PositionsTotal;
        public Func<int> PositionsN;
        public Func<Symbol, double> AtmIVCached;
        public Func<Symbol, bool> IsDeltaHedgeInProgress;

        public void AssignCachedFunctions()
        {
            Beta = Cache(GetBeta, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            Correlation = Cache(GetCorrelation, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            IsLiquid = Cache(GetIsLiquid, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            //HedgeWithIndex = Cache(GetHedgeWithIndex, () => Time, maxKeys: 1);
            HedgeOptionWithUnderlying = Cache(GetHedgeOptionWithUnderlying, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), Underlying(symbol)));
            HistoryWrap = Cache(GetHistoryWrap, (Symbol symbol, int window, Resolution resolution) => (Time.Date, symbol, window, resolution));  // not correct for resolution < daily
            HistoryWrapQuote = Cache(GetHistoryWrapQuote, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            TickSize = Cache(GetTickSize, (Symbol symbol) => symbol, maxKeys: 1);
            PositionsTotal = Cache(GetPositionsTotal, () => Time.Trim(TimeSpan.TicksPerSecond), maxKeys: 1);
            PositionsN = Cache(GetPositionsN, () => Time.Trim(TimeSpan.TicksPerSecond), maxKeys: 1);
            AtmIVCached = Cache(GetAtmIV, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), symbol));
            IsDeltaHedgeInProgress = Cache(GetIsDeltaHedgeInProgress, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), symbol));

            IntrinsicValue = (Option option) => option.GetIntrinsicValue(MidPrice(option.Underlying.Symbol));
        }
        public double AtmIV(Symbol symbol) => AtmIVCached(symbol);
        /// <summary>
        /// Ask IV strongly slopes up close to expiration (1-3 days), therefore rendering midIV not a good indicator. Would wanna use contracts expiring later. This will lead to a
        /// jump in AtmIV when referenced contracts are switched. How to make it smooth?
        /// </summary>
        public double GetAtmIV(Symbol symbol)
        {
            return IVSurfaceSSVIMid.TryGetValue(ToEquity(Underlying(symbol)), out IIVSurface ivs) ? ivs.AtmIv() : 0;
        }

        private double GetBeta(Symbol index, Symbol asset, int periods, Resolution resolution = Resolution.Daily)
        {
            // needs caching as multiple option contracts will run this method for the same underlying.
            var logReturnsAsset = LogReturns(HistoryWrap(asset, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            var logReturnsIndex = LogReturns(HistoryWrap(index, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            int minSize = Math.Min(logReturnsAsset.Length, logReturnsIndex.Length);

            if (minSize != periods - 1)  // Log returns removes 1.
            {
                Debug($"Beta() Error: The received periods are smaller than the requested periods, likely due to missing historical data for request. Returning 0 beta. {index}. {asset} {periods} {resolution}");
            }
            if (minSize == 0)
            {
                return 0;
            }
            return Covariance(logReturnsAsset, logReturnsIndex, periods) / logReturnsIndex.Variance(unbiased: true);
        }
        private double GetCorrelation(Symbol symbol1, Symbol symbol2, int periods = 20, Resolution resolution = Resolution.Daily)
        {
            var logReturnsSymbol1 = LogReturns(HistoryWrap(symbol1, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            var logReturnsSymbol2 = LogReturns(HistoryWrap(symbol2, periods, resolution).Select(tb => (double)tb.Close).ToArray());

            int minSize = Math.Min(logReturnsSymbol1.Length, logReturnsSymbol2.Length);

            if (minSize != periods - 1)  // Log returns removes 1.
            {
                Debug($"Error: The window size {minSize} is smaller than the requested periods, likely due to missing historical data for request {symbol1}. {symbol2} {periods} {resolution}");
            }

            if (minSize != 0)
            {
                logReturnsSymbol1 = logReturnsSymbol1.TakeLast(minSize).ToArray();
                logReturnsSymbol2 = logReturnsSymbol2.TakeLast(minSize).ToArray();
            }
            else // (minSize == 0)
            {
                return 0;
            }

            double corrPearson = MathNet.Numerics.Statistics.Correlation.Pearson(logReturnsSymbol1, logReturnsSymbol2);
            //double correlation = Covariance(logReturnsSymbol1, logReturnsSymbol2, periods) / (logReturnsSymbol1.Variance(unbiased: true) * logReturnsSymbol2.Variance(unbiased: true));

            Debug($"Correlation.Pearson({symbol1},{symbol2},{periods}: Pearson: {corrPearson}");
            return corrPearson;
        }

        private bool GetIsDeltaHedgeInProgress(Symbol underlying)
        {
            return orderTickets.ContainsKey(underlying) && orderTickets[underlying].Any(t => orderSubmittedPartialFilledUpdated.Contains(t.Status));
        }

        /// <summary>
        /// Adjust the target hedge risk by an amount corresponding to the put call ratio signal.
        /// </summary>
        /// <returns></returns>
        public decimal TargetRiskPutCallRatio(Symbol underlying)
        {
            if (Cfg.PutCallRatioTargetRisks.ContainsKey(underlying.Value))
            {
                foreach (TargetRisk targetRisk in Cfg.PutCallRatioTargetRisks[underlying.Value])
                {
                    if (targetRisk.RangeLower <= PutCallRatios[underlying].Ratio() && PutCallRatios[underlying].Ratio() <= targetRisk.RangeUpper)
                    {
                        return targetRisk.Target100BpUSD;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Closely related to GedHedgeWithIndex, but hedges with the underlying instead of the index.
        /// To avoid dynamic over hedging, best used rarely. For example once per fill only.
        /// To be refactored with a more generic hedging function searching for the best hedge given the current portfolio.
        /// </summary>
        private void GetHedgeOptionWithUnderlying(Symbol symbol)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            Symbol underlying = Underlying(symbol);

            // Special case scenario
            if (IsEODAcrossDsHedge(underlying))
            {
                decimal quantity = -(decimal)LastDeltaAcrossDs[underlying] - Portfolio[underlying].Quantity;
                Log($"{Time} {underlying} GetHedgeOptionWithUnderlying with DeltaTotalAcrossDs: {LastDeltaAcrossDs[underlying]}, quantity: {quantity}, Position: {Portfolio[underlying].Quantity}");
                if (Math.Abs(quantity) > 1)
                {
                    ExecuteHedge(underlying, quantity);
                }
                return;
            }

            decimal deltaTotal = DeltaMV(symbol);
            if (PfRisk.IsUnderlyingDeltaExceedingBand(symbol, deltaTotal))
            {
                ExecuteHedge(underlying, EquityHedgeQuantity(underlying));
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. underlying={underlying} Not hedging because deltaTotal={deltaTotal}.");
            }
        }

        public bool IsEODAcrossDsHedge(Symbol underlying)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return false;

            DateTime nextReleaseDate = NextReleaseDate(underlying);
            DateTime nextMarketClose = NextMarketClose.TryGetValue(underlying, out nextMarketClose) ? nextMarketClose : GetNextMarketClose(underlying);            
            TimeSpan hedgeToAcrossDs = nextMarketClose.TimeOfDay - TimeSpan.FromMinutes(Cfg.MinutesBeforeCloseHedgeToAcrossDs);
            
            return LastDeltaAcrossDs.ContainsKey(underlying)
                && Time.TimeOfDay  >= hedgeToAcrossDs
                && nextReleaseDate == Time.Date;
        }

        /// <summary>
        /// Closely related to GedHedgeWithIndex, but hedges with the underlying instead of the index.
        /// To avoid dynamic over hedging, best used rarely. For example once per fill only.
        /// To be refactored with a more generic hedging function searching for the best hedge given the current portfolio.
        /// </summary>
        private void GetHedgeOptionWithUnderlyingUSD(Symbol symbol)
        {
            if (IsWarmingUp || !IsMyMarketOpen(symbolSubscribed)) return;

            decimal riskDelta100BpUSD = 0;
            Symbol underlying = Underlying(symbol);

            decimal delta100BpUSDTotal = PfRisk.RiskByUnderlying(symbol, Metric.Delta100BpUSDTotal);
            riskDelta100BpUSD += delta100BpUSDTotal;
            decimal deltaIVdS100BpUSD = PfRisk.RiskByUnderlying(symbol, Metric.DeltaIVdS100BpUSDTotal);  // MV
            riskDelta100BpUSD += deltaIVdS100BpUSD;

            SecurityRiskLimit riskLimit = Securities[underlying].RiskLimit;

            if (riskDelta100BpUSD > riskLimit.Delta100BpLong || riskDelta100BpUSD < riskLimit.Delta100BpShort)
            {
                ExecuteHedge(underlying, EquityHedgeQuantity(underlying));
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. Not hedging because riskDelta100BpUSD={riskDelta100BpUSD}, delta100BpUSDTotal={delta100BpUSDTotal}, deltaIVdS100BpUSD={deltaIVdS100BpUSD} for symbol={symbol}.");
            }
        }

        public decimal DeltaMV(Symbol symbol)
        {
            decimal deltaMVTotal = 0;
            decimal deltaTotal = PfRisk.RiskByUnderlying(symbol, HedgeMetric(Underlying(symbol)));
            
            // decimal deltaIVdSTotal = PfRisk.RiskByUnderlying(symbol, Metric.DeltaIVdSTotal);  // MV

            deltaMVTotal += deltaTotal;
            // deltaMVTotal += deltaIVdSTotal;
            // Log($"{Time} DeltaMV {symbol}: deltaMVTotal={deltaMVTotal}, deltaTotal={deltaTotal}, deltaIVdSTotal={deltaIVdSTotal}");
            return deltaMVTotal;
        }
        /// <summary>
        /// Adjusting Heding Frequency by adjusting volatilty. Not making sense to me how adjust vola helps with hedging frequency, but can adjust the threhold...
        /// Vola Bias (Vola up -> All Deltas closer to 0.5 (C) / -0.5 (P))
        ///      Short Gamma + Trending    -> Hedge often (defensively)
        ///      Short Gamma + Range Bound -> Hedge less  (hedges are losers)
        ///      Long  Gamma + Trending    -> Hedge less  (let delta run)
        ///      Long  Gamma + Range Bound -> Hedge often (hedges are winners)
        /// </summary>
        /// <param name="symbol"></param>
        /// <param name="quantity"></param>
        /// <param name="orderType"></param>
        public void ExecuteHedge(Symbol symbol, decimal quantity, OrderType? orderType = null)
        {
            Equity equity = (Equity)Securities[symbol];

            if (!Cfg.Ticker.Contains(equity.ToString()))
            {
                Log($"{Time} ExecuteHedge: Not hedging because {equity} is not in Ticker: {string.Join(",", Cfg.Ticker)}");
                return;
            }
            if (quantity == 0)
            {
                Log($"{Time} ExecuteHedge: Not hedging {equity} because quantity={quantity}.");
                return;
            }

            decimal price;
            List<OrderTicket> liveTickets = new();
            bool isLiveTickets = false;

            if (orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets))
            {
                // Cancel any tickets ordering the opposite quantity
                tickets.Where(t => t.Quantity * quantity < 0).ToList().ForEach(t => Cancel(t, $"Opposite direction to requested hedge quantity"));

                if (orderType == OrderType.Market)
                {
                    tickets.Where(t => t.OrderType == OrderType.Limit).ToList().ForEach(t => Cancel(t, $"Requested a market order on {equity}"));
                }

                liveTickets = tickets.Where(t => orderTypeMarketLimit.Contains(t.OrderType) && orderNewSubmittedPartialFilledUpdated.Contains(t.Status)).ToList();
                isLiveTickets = liveTickets.Any();

                if (liveTickets.Where(t => t.OrderType == OrderType.Limit).Any() && orderType == OrderType.Market)
                {
                    Error($"{Time} ExecuteHedge: {equity}. A market order hedge was requested despite existing limit order tickets. Shouldn't happen, rather make the existing limit orders aggressive.");
                }
            }

            if (!isLiveTickets)
            {
                OrderType _orderType = orderType ?? GetEquityHedgeOrderType(equity);
                price = GetEquityHedgePrice(equity, _orderType, quantity);

                QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "New OrderEquity" }, { "f", $"ExecuteHedge" },
                            { "Symbol", symbol}, { "riskDeltaTotal", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });
                OrderEquity(symbol, quantity, price, _orderType);
            }
            else
            {
                string msg = $"{Time} ExecuteHedge: Not hedging {equity} because quantity={quantity}, isLiveTickets={isLiveTickets}, OrderId={string.Join(",", liveTickets.Select(t => t.OrderId))}.";
                Log(msg);
            }
        }

        /// <summary>
        /// Default order type for hedging is limit. If spread is tiny, use market order.
        /// </summary>
        /// <param name="equity"></param>
        /// <returns></returns>
        public OrderType GetEquityHedgeOrderType(Equity equity) 
        {
            bool isTinySpread = Spread(equity) <= Cfg.MaxSpreadForMarketOrderHedging;
            // bool isMarketAboutToClose = Time.TimeOfDay > Cfg.MarketCloseTime - Cfg.MarketCloseTimeBuffer;
            return isTinySpread ? OrderType.Market : OrderType.Limit;
        }

        enum EquityHedgeMode {
            Agressive,
            MidPrice,
            Passive,
        }

        /// <summary>
        /// Gamma long - trailing limit orders.
        /// Gamma short - hedge more tightly - midPrice Limit Orders.
        /// </summary>
        public decimal GetEquityHedgePrice(Equity equity, OrderType orderType, decimal quantity, OrderTicket? ticket = null)
        {
            decimal touchPrice;
            decimal price;
            OrderDirection direction = quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;

            bool isPriceMovingAway = (ticket != null && ticket.UpdateRequests.Count > Cfg.LimitOrderUpdateBeforeMarketOrderConversion);
            int modeInt = Cfg.EquityHedgeMode.TryGetValue(equity.Symbol.Value, out modeInt) ? modeInt : Cfg.EquityHedgeMode[CfgDefault];
            EquityHedgeMode mode = (EquityHedgeMode)Enum.GetValues(typeof(EquityHedgeMode)).GetValue(modeInt);
            mode = isPriceMovingAway ? EquityHedgeMode.Agressive : mode;

            switch (orderType)
            {
                case OrderType.Market:
                    return MidPrice(equity.Symbol);

                case OrderType.Limit:
                    return (mode, direction) switch
                    {
                        //// Aggressively limit order at worst price like a market order.
                        (EquityHedgeMode.Agressive, OrderDirection.Buy) => equity.AskPrice,
                        (EquityHedgeMode.Agressive, OrderDirection.Sell) => equity.BidPrice,

                        // Earn the spread.
                        (EquityHedgeMode.Passive, OrderDirection.Buy) => equity.BidPrice + 0.01m,
                        (EquityHedgeMode.Passive, OrderDirection.Sell) => equity.AskPrice - 0.01m,
                        _ => MidPrice(equity.Symbol)  // During Simulation, above is good. During real trading, high-delta options appear to be typically filled just before a small jump in the opposite direction. Bad.
                    };
                default:
                    return 0;
            }
        }

        private bool GetIsLiquid(Symbol contract, int window = 3, Resolution resolution = Resolution.Daily)
        {
            var trade_bars = HistoryWrap(contract, window, resolution).ToList();
            return trade_bars.Sum(bar => bar.Volume) > 0;
        }

        private IEnumerable<TradeBar> GetHistoryWrap(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<TradeBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<QuoteBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, DateTime start, DateTime end, Resolution resolution, bool? fillForward = false
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<QuoteBar>(symbol, start, end, resolution, fillForward: fillForward);
            //extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private decimal GetTickSize(Symbol symbol)
        {
            //Log($"GetTickSize called {symbol}");
            var sec = Securities[symbol];
            return sec.SymbolProperties.MinimumPriceVariation;
        }


        private decimal GetPositionsTotal()
        {
            return Portfolio.TotalHoldingsValue;
            //return Portfolio.Values.Where(s => s.Invested).Sum(s => s.HoldingsValue);
        }
        private int GetPositionsN()
        {
            // Filter out securities that are not invested
            return Portfolio.Values.Count(s => s.Invested);
        }
    }
}
