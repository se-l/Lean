using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Statistics;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        // Cached Methods
        /// <summary>Symbols with a hedge submission between the ticket check and the order call.</summary>
        private readonly HashSet<Symbol> hedgeSubmitting = new();

        public VoidArg1Function<Symbol> HedgeOptionWithUnderlying;
        public Func<Symbol, int, Resolution, IEnumerable<TradeBar>> HistoryWrap;
        public Func<Symbol, decimal> TickSize;
        public Func<Symbol, double> AtmIVCached;
        public Func<Symbol, bool> IsDeltaHedgeInProgress;

        public void AssignCachedFunctions()
        {
            //HedgeWithIndex = Cache(GetHedgeWithIndex, () => Time, maxKeys: 1);
            HedgeOptionWithUnderlying = Cache(GetHedgeOptionWithUnderlying, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), Underlying(symbol)));
            HistoryWrap = Cache(GetHistoryWrap, (Symbol symbol, int window, Resolution resolution) => (Time.Date, symbol, window, resolution));  // not correct for resolution < daily
            TickSize = Cache(GetTickSize, (Symbol symbol) => symbol, maxKeys: 1);
            AtmIVCached = Cache(GetAtmIV, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), symbol));
            IsDeltaHedgeInProgress = Cache(GetIsDeltaHedgeInProgress, (Symbol symbol) => (Time.Trim(TimeSpan.TicksPerSecond), symbol));
        }
        public double AtmIV(Symbol symbol) => AtmIVCached(symbol);
        /// <summary>
        /// Ask IV strongly slopes up close to expiration (1-3 days), therefore rendering midIV not a good indicator. Would wanna use contracts expiring later. This will lead to a
        /// jump in AtmIV when referenced contracts are switched. How to make it smooth?
        /// </summary>
        public double GetAtmIV(Symbol symbol)
        {
            return IvSurfaceSsviRisk.TryGetValue(ToEquity(Underlying(symbol)), out IIVSurface ivs) ? ivs.AtmIv() : 0;
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

        private bool GetIsDeltaHedgeInProgress(Symbol underlying)
        {
            return orderTickets.ContainsKey(underlying) && orderTickets[underlying].Any(t => orderSubmittedPartialFilledUpdated.Contains(t.Status));
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
                decimal quantity = -(decimal)LastDeltaAcrossDsOptionsOnly[underlying] - Portfolio[underlying].Quantity;
                Log($"{Time} {underlying} GetHedgeOptionWithUnderlying with LastDeltaAcrossDsOptionsOnly: {LastDeltaAcrossDsOptionsOnly[underlying]}, quantity: {quantity}, Position: {Portfolio[underlying].Quantity}");
                if (Math.Abs(quantity) > 1)
                {
                    ExecuteHedge(underlying, quantity);
                }
                return;
            }
            
            decimal deltaTotal = PfRisk.RiskByUnderlying(underlying, HedgeMetric(underlying));
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
            
            return LastDeltaAcrossDsOptionsOnly.ContainsKey(underlying)
                && Time.TimeOfDay  >= hedgeToAcrossDs
                && nextReleaseDate == Time.Date;
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
        /// <summary>
        /// The schedules matching `underlying` right now, classified by their Direction attribute.
        ///
        /// A schedule matches when its underlying key (ticker, falling back to `_`), its
        /// MarketRegime (one of the active regimes) and its [Start, End] window all fit the current
        /// cycle. Direction decides what it means:
        ///   "Hold"      — restrictive: do not hedge while this window is in force
        ///   "Buy"/"Sell"— permissive: hedge in that direction, priced with EquityHedgeMode
        /// No matching schedule at all means neither style applies and hedging proceeds normally.
        /// </summary>
        private (HedgeScheduleCfg Hold, HedgeScheduleCfg Buy, HedgeScheduleCfg Sell) MatchHedgeSchedules(Symbol underlying)
        {
            if (Cfg.HedgeSchedules == null || Cfg.HedgeSchedules.Count == 0) return (null, null, null);

            Symbol equitySymbol = underlying.SecurityType == SecurityType.Equity ? underlying : Underlying(underlying);
            if (!Securities.ContainsKey(equitySymbol)) return (null, null, null);

            string key = equitySymbol.Value;
            if (!Cfg.HedgeSchedules.TryGetValue(key, out List<HedgeScheduleCfg> schedules) || schedules == null)
            {
                if (!Cfg.HedgeSchedules.TryGetValue(CfgDefault, out schedules) || schedules == null) return (null, null, null);
            }

            // COMPUTE the regime, do not read the dictionary. ActiveRegimes is refreshed by a 1-minute
            // schedule and only while IsMyMarketOpen is true, so at 09:30:00 it still holds the PRE-OPEN
            // set: a "Hold" window starting at the open matched nothing and the algo hedged into it.
            HashSet<MarketRegime> regimes;
            try
            {
                regimes = GetMarketRegimes(equitySymbol);
            }
            catch
            {
                regimes = ActiveRegimes.TryGetValue((Equity)Securities[equitySymbol], out HashSet<MarketRegime> active)
                    ? active
                    : new HashSet<MarketRegime>();
            }

            TimeSpan now = Time.TimeOfDay;
            HedgeScheduleCfg hold = null, buy = null, sell = null;
            foreach (HedgeScheduleCfg schedule in schedules)
            {
                if (!Enum.TryParse(schedule.MarketRegime, out MarketRegime regime) || !regimes.Contains(regime)) continue;
                TimeSpan scheduleStart = AlgoConfig.GetTimeSpan(schedule.Start);
                TimeSpan scheduleEnd = AlgoConfig.GetTimeSpan(schedule.End);
                if (now < scheduleStart || now > scheduleEnd) continue;

                switch ((schedule.Direction ?? string.Empty).Trim().ToUpperInvariant())
                {
                    case "HOLD": hold ??= schedule; break;
                    case "BUY": buy ??= schedule; break;
                    case "SELL": sell ??= schedule; break;
                }
            }
            return (hold, buy, sell);
        }

        /// <summary>True when a matching schedule says "Hold" — hedging is suppressed for the window.</summary>
        public bool IsHedgeOnHold(Symbol underlying) => MatchHedgeSchedules(underlying).Hold != null;

        /// <summary>The permissive Buy/Sell schedule for this direction, or null when none matches.</summary>
        public HedgeScheduleCfg GetActiveHedgeSchedule(Symbol underlying, OrderDirection direction)
        {
            (HedgeScheduleCfg _, HedgeScheduleCfg buy, HedgeScheduleCfg sell) = MatchHedgeSchedules(underlying);
            return direction == OrderDirection.Buy ? buy : sell;
        }

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
            if (!HasSsviSurface(symbol))
            {
                // Greeks need the surface. Without it the delta that produced `quantity` is the equity
                // leg plus zeros — hedging on that is what put a wrong hedge out on the first second of
                // live, before the first calibration arrived.
                Log($"{Time} ExecuteHedge: Not hedging {equity} because no SSVI surface is present yet (requested quantity={quantity}).");
                return;
            }

            // Hedge schedules: a matching "Hold" schedule suppresses hedging for its window (that is
            // what keeps the algo out of the market for the first minutes after a release). Buy/Sell
            // schedules are permissive and only set the pricing mode; when nothing matches, hedging
            // proceeds as before.
            OrderDirection hedgeDirection = quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;
            if (IsHedgeOnHold(equity.Symbol))
            {
                Log($"{Time} ExecuteHedge: {equity} qty={quantity} ({hedgeDirection}) skipped — HedgeSchedule Direction=Hold.");
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

                // ANY non-terminal ticket blocks a new hedge. The status filter used to require
                // New/Submitted/Partial/UpdateSubmitted, but a ticket returned by LimitOrder() in LIVE
                // sits in OrderStatus.None until the brokerage transaction handler acknowledges it —
                // and that window is exactly how a second hedge order went out a second later: the
                // first ticket existed, was stored, and still did not count as "live".
                liveTickets = tickets
                    .Where(t => t != null && orderTypeMarketLimit.Contains(t.OrderType) && !orderFilledCanceledInvalid.Contains(t.Status))
                    .ToList();
                isLiveTickets = liveTickets.Any();

                if (liveTickets.Where(t => t.OrderType == OrderType.Limit).Any() && orderType == OrderType.Market)
                {
                    Error($"{Time} ExecuteHedge: {equity}. A market order hedge was requested despite existing limit order tickets. Shouldn't happen, rather make the existing limit orders aggressive.");
                }
            }

            if (!isLiveTickets)
            {
                // The ticket list is read, then order submission happens: without this lease two
                // triggers interleaved on different threads (fill handler + scheduled hedge) both saw an
                // empty list and both submitted.
                lock (hedgeSubmitting)
                {
                    if (!hedgeSubmitting.Add(symbol))
                    {
                        Log($"{Time} ExecuteHedge: {equity} qty={quantity} skipped — another hedge submission for this symbol is in flight.");
                        return;
                    }
                }

                try
                {
                    OrderType _orderType = orderType ?? GetEquityHedgeOrderType(equity);
                    price = GetEquityHedgePrice(equity, _orderType, quantity);

                    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "New OrderEquity" }, 
                        { "f", $"ExecuteHedge" }, { "Symbol", symbol}, { "riskDeltaTotal", quantity.ToString() }, 
                        { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });
                    OrderEquity(symbol, quantity, price, _orderType);
                }
                finally
                {
                    lock (hedgeSubmitting)
                    {
                        hedgeSubmitting.Remove(symbol);
                    }
                }
            }
            else
            {
                string msg = $"{Time} ExecuteHedge: Not hedging {equity} because quantity={quantity}, isLiveTickets={isLiveTickets}, " +
                             $"pending=[{string.Join(", ", liveTickets.Select(t => $"id={t.OrderId} status={t.Status} qty={t.Quantity}"))}].";
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
            // Hedges are ALWAYS limit orders. A tight spread used to become a market order; instead the
            // limit price is placed at the touch (ask to buy, bid to sell) in GetEquityHedgePrice, which
            // pays the same spread but keeps the order cancellable and its price visible.
            return OrderType.Limit;
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
            OrderDirection direction = quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;

            bool isPriceMovingAway = (ticket != null && ticket.UpdateRequests.Count > Cfg.LimitOrderUpdateBeforeMarketOrderConversion);
            // A spread tight enough that we would previously have crossed it with a market order is paid
            // with a limit AT the touch instead — same fill odds, no market order in the book.
            bool isTinySpread = Spread(equity) <= Cfg.MaxSpreadForMarketOrderHedging;
            int modeInt = Cfg.EquityHedgeMode.TryGetValue(equity.Symbol.Value, out modeInt) ? modeInt : Cfg.EquityHedgeMode[CfgDefault];
            // An active hedge schedule dictates the mode for its window (byte in the config, the same
            // 0/1/2 encoding as EquityHedgeMode); otherwise the per-underlying setting applies.
            HedgeScheduleCfg activeSchedule = GetActiveHedgeSchedule(equity.Symbol, direction);
            if (activeSchedule != null) modeInt = activeSchedule.EquityHedgeMode;
            int maxMode = Enum.GetValues(typeof(EquityHedgeMode)).Length - 1;
            modeInt = Math.Min(Math.Max(modeInt, 0), maxMode);
            EquityHedgeMode mode = (EquityHedgeMode)Enum.GetValues(typeof(EquityHedgeMode)).GetValue(modeInt);
            mode = (isPriceMovingAway || isTinySpread) ? EquityHedgeMode.Agressive : mode;

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

        private IEnumerable<TradeBar> GetHistoryWrap(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<TradeBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private decimal GetTickSize(Symbol symbol)
        {
            //Log($"GetTickSize called {symbol}");
            var sec = Securities[symbol];
            return sec.SymbolProperties.MinimumPriceVariation;
        }
    }
}
