using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        internal void SetPricingStrategies()
        {
            if (IsWarmingUp) return;
            Cfg.Ticker.DoForEach(s => SetPricingStrategy(s));
        }

        internal void SetPricingStrategy(Symbol underlying)
        {
            PricingStrategy[underlying] = GetPricingStrategy(underlying);
        }

        private Func<QuoteRequest<Option>, decimal> GetPricingStrategy(Symbol underlying)
        {
            // Set the pricing strategy based on the market regime
            HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(underlying), out regimes) ? regimes : new HashSet<MarketRegime>();

            if (regimes.Contains(MarketRegime.PreEarningsRelease)) { return GetPricePreEarningsReleasePricerKalman; }
            if (regimes.Contains(MarketRegime.PreEarningsReleaseBeforeMarketClose)) { return GetPricePreEarningsReleaseBeforeMarketClose; }
            if (regimes.Contains(MarketRegime.PostEarningsRelease)) { return GetPricePostEarningsReleasePricer; }
            return GetPriceNoTraderPricer;
        }

        private static decimal GetPriceNoTraderPricer(QuoteRequest<Option> qr)
        {
            return 0;
        }

        private decimal GetPricePreEarningsReleasePricerKalman(QuoteRequest<Option> qr)
        {
            if (qr == null || IsUtilityGtMin(qr)) return 0;

            // The sweep curve is already a PRICE: the live touch at ratio 0, walking towards the target as
            // the active schedule's duration elapses (RiskScenarioHandler.ComputeSweepCurve). No IV round
            // trip here any more - inverting an IV that never priced back to the touch is what threw the
            // quote into the middle of the spread on the first tick.
            var sweep = RiskScenarioHandler.SweepCurve(qr.Option, qr.OrderDirection);
            decimal priceSweep = sweep?.Price ?? 0;
            double sweepTargetIv = sweep?.TargetIv ?? 0;
            double sweepRatio = sweep?.Ratio ?? 0;
            decimal sweepEntry = sweep?.EntryPrice ?? 0;
            decimal sweepTargetPrice = sweep?.TargetPrice ?? 0;
            if (priceSweep <= 0)
            {
                // No risk scenario, no price.
                return 0;
            }

            decimal kfPrice = GetKalmanQuote(qr) ?? 0;
            decimal priceModel = TakeAggressivePrice(qr.OrderDirection, kfPrice, priceSweep);

            // The sweep curve IS the quote policy: at ratio 0 it rests on the passive touch (ask for a
            // sell, bid for a buy) and walks into the spread as the schedule's duration elapses. It
            // must not be collapsed onto the model price - TakeAggressivePrice(kfPrice, priceSweep)
            // picks the MORE AGGRESSIVE of the two, and the model mid is always more aggressive than
            // the touch, so the first order crossed half the spread instead of resting at the touch.
            // The model price stays as the fallback for when there is no sweep at all.
            decimal price = priceSweep != 0 ? priceSweep : kfPrice;

            decimal bid = qr.Option.BidPrice;
            decimal ask = qr.Option.AskPrice;
            decimal spread = ask - bid;

            decimal aggressiveCrossRatio = CrossRatio(qr.OrderDirection, priceModel, bid, ask);

            if (price == 0)
            {
                Log($"{Time} GetPricePreEarningsReleasePricerKalman(): symbol={qr.Option.Symbol.Value}, direction={qr.OrderDirection}, no sweep and no model price - not quoting. bid={bid}, ask={ask}");
                return 0;
            }

            if (IsPricerOverridePricesWithPresumedIvFillDefensively(qr.Underlying.Value))
            {
                price = TakeDefensivePrice(qr.OrderDirection, PriceModelPresumedFill(qr) ?? price, price);
            }

            decimal priceBeforeBboCap = price;
            price = ClampInsideSpread(qr, price);
            price = LimitPriceToBBO(qr, price);

            decimal finalCrossRatio = CrossRatio(qr.OrderDirection, price, bid, ask);

            // The sweep schedule is the rate control for a sweep-priced quote; BufferPriceCrossingSpread
            // is only the fallback rate limiter for a MODEL-priced one. Applying both double-counts the
            // walk - the buffer moves 5% of the spread every 15s (a full spread in ~5 minutes), far
            // faster than a 30-minute sweep - and its NBBO-mid cap then blocks the sweep from ever
            // reaching the worse execution price.
            bool bufferApplied = priceSweep == 0;

            Log($"{Time} GetPricePreEarningsReleasePricerKalman(): symbol={qr.Option.Symbol.Value}, direction={qr.OrderDirection}, utility={qr.UtilityOrder.Utility:0.00}, sweepTargetIV={sweepTargetIv:0.000}, sweepRatio={sweepRatio:0.000}, sweepEntry={sweepEntry:0.0000}, sweepTarget={sweepTargetPrice:0.0000}, sweepPrice={priceSweep:0.0000}, kfPrice={kfPrice:0.0000}, aggressivePrice={priceModel:0.0000}, priceSource={(priceSweep != 0 ? "sweep" : "model")}, bufferApplied={bufferApplied}, preBboPrice={priceBeforeBboCap:0.0000}, finalPrice={price:0.0000}, bid={bid:0.0000}, ask={ask:0.0000}, spread={spread:0.0000}, aggressiveCrossRatio={aggressiveCrossRatio:0.000}, finalCrossRatio={finalCrossRatio:0.000}");
            if (bufferApplied)
            {
                price = BufferPriceCrossingSpread(qr, price);
                price = LimitPriceToBBO(qr, price);
            }
            // price = LimitMaxSpreadDiscount(qr, price);

            return price;
        }

        /// <summary>
        /// Relies on pfRiskScenario to provide a ranking of options
        /// </summary>
        private decimal GetPricePreEarningsReleaseBeforeMarketClose(QuoteRequest<Option> qr)
        {
            // Check if sweep is already underway
            var sweep = RiskScenarioHandler.SweepCurve(qr.Option, qr.OrderDirection);
            decimal price = sweep?.Price ?? 0;
            if (price <= 0)
            {
                // No risk scenario, no price.
                return 0;
            }
            price = ClampInsideSpread(qr, price);
            price = LimitPriceToBBO(qr, price);

            return price;
        }


        /// <summary>
        /// Post-release quoting. The sweep schedule in FoundationsConfig has PostEarningsRelease
        /// entries (Sell 09:45-11:30, Buy 11:30-14:30, Sell 14:00-16:00), so the same policy as the
        /// pre-release Kalman pricer applies: the schedule drives the limit from the passive touch
        /// inward over its Duration, BufferPriceCrossingSpread rate-limits how fast the quote is
        /// allowed to walk, and the Kalman model price is only the fallback when no sweep exists.
        ///
        /// This path used to (a) collapse the sweep onto the model mid with TakeAggressivePrice,
        /// (b) leave the spread buffer commented out, and (c) log nothing at all - which is why a
        /// null sweep silently quoted at the model mid and looked like "the sweeper never ran".
        /// </summary>
        private decimal GetPricePostEarningsReleasePricer(QuoteRequest<Option> qr)
        {
            if (qr == null) return 0;

            var sweep = RiskScenarioHandler.SweepCurve(qr.Option, qr.OrderDirection);
            decimal priceSweep = sweep?.Price ?? 0;
            double sweepTargetIv = sweep?.TargetIv ?? 0;
            double sweepRatio = sweep?.Ratio ?? 0;
            decimal sweepEntry = sweep?.EntryPrice ?? 0;
            decimal sweepTargetPrice = sweep?.TargetPrice ?? 0;

            // Convert IV to a price
            decimal kfPrice = GetKalmanQuote(qr) ?? 0;
            decimal priceModel = TakeAggressivePrice(qr.OrderDirection, priceSweep, kfPrice);

            // Sweep first, model only as the fallback - see GetPricePreEarningsReleasePricerKalman.
            decimal price = priceSweep != 0 ? priceSweep : kfPrice;

            decimal bid = qr.Option.BidPrice;
            decimal ask = qr.Option.AskPrice;
            decimal spread = ask - bid;

            if (price == 0)
            {
                Log($"{Time} GetPricePostEarningsReleasePricer(): symbol={qr.Option.Symbol.Value}, direction={qr.OrderDirection}, no sweep and no model price - not quoting. bid={bid}, ask={ask}");
                return 0;
            }

            decimal priceBeforeBuffer = price;
            price = ClampInsideSpread(qr, price);
            price = LimitPriceToBBO(qr, price);

            // Sweep-priced quotes are paced by the schedule; the spread buffer only paces a model-priced
            // fallback (see GetPricePreEarningsReleasePricerKalman).
            bool bufferApplied = priceSweep == 0;

            Log($"{Time} GetPricePostEarningsReleasePricer(): symbol={qr.Option.Symbol.Value}, direction={qr.OrderDirection}, utility={qr.UtilityOrder.Utility:0.00}, sweepTargetIV={sweepTargetIv:0.000}, sweepRatio={sweepRatio:0.000}, sweepEntry={sweepEntry:0.0000}, sweepTarget={sweepTargetPrice:0.0000}, sweepPrice={priceSweep:0.0000}, kfPrice={kfPrice:0.0000}, modelPrice={priceModel:0.0000}, priceSource={(priceSweep != 0 ? "sweep" : "model")}, bufferApplied={bufferApplied}, preBufferPrice={priceBeforeBuffer:0.0000}, priceInSpread={price:0.0000}, sweepCrossRatio={CrossRatio(qr.OrderDirection, priceSweep, bid, ask):0.000}, modelCrossRatio={CrossRatio(qr.OrderDirection, priceModel, bid, ask):0.000}, bid={bid:0.0000}, ask={ask:0.0000}, spread={spread:0.0000}");

            if (bufferApplied)
            {
                price = BufferPriceCrossingSpread(qr, price);
                price = LimitPriceToBBO(qr, price);
            }

            return price;
        }

        private TimeSpan TimeStartKfBeforeRelease(string underlying)
        {
            try
            {
                return AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfBeforeRelease, underlying));
            }
            catch (Exception e)
            {
                Error($"{Time} GetQuote: {e.Message}");
                Log(Environment.StackTrace);
                return new TimeSpan(0, 23, 0, 0);
            }
        }

        private decimal? PriceModelPresumedFill(QuoteRequest<Option> qr)
        {
            return PresumedFillIV.ContainsKey(qr.Option) ? new PresumedFillMetrics(qr, this).DiscountedPrice : null;
        }

        /// <summary>
        /// Least aggressive of the given prices (a buy at the lowest, a sell at the highest).
        /// Returns 0 when there is nothing to price with: Min()/Max() over an empty sequence throws,
        /// and "no price" is a condition the callers must handle (do not quote), not a crash.
        /// </summary>
        private static decimal TakeDefensivePrice(OrderDirection direction, params decimal[] prices)
        {
            decimal[] okPrices = prices.Where(p => p != 0).ToArray();
            if (okPrices.Length == 0)
            {
                return 0;
            }
            return direction switch
            {
                OrderDirection.Buy => okPrices.Min(),
                OrderDirection.Sell => okPrices.Max(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }

        /// <summary>
        /// Most aggressive of the given prices (a buy at the highest, a sell at the lowest).
        /// Returns 0 when there is nothing to price with - see TakeDefensivePrice.
        /// </summary>
        private static decimal TakeAggressivePrice(OrderDirection direction, params decimal[] prices)
        {
            decimal[] okPrices = prices.Where(p => p != 0).ToArray();
            if (okPrices.Length == 0)
            {
                return 0;
            }
            return direction switch
            {
                OrderDirection.Buy => okPrices.Max(),
                OrderDirection.Sell => okPrices.Min(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }

        /// <summary>
        /// Clamps a quote into the live NBBO. A sweep IV taken from the scenario surface can price a
        /// contract outside the touch - above the ask for a sell, below the bid for a buy - and a
        /// limit there is not "passive", it is off-market and never fills. The passive edge of every
        /// quote is the touch; LimitPriceToBBO then guards the aggressive edge.
        /// </summary>
        private static decimal ClampInsideSpread(QuoteRequest<Option> qr, decimal price)
        {
            decimal bid = qr.Option.BidPrice;
            decimal ask = qr.Option.AskPrice;
            if (bid <= 0 || ask <= bid)
            {
                return price;
            }
            return Math.Min(Math.Max(price, bid), ask);
        }

        /// <summary>
        /// Fraction of the spread the quote has crossed, measured from the passive touch:
        /// 0 = resting on the ask (sell) / bid (buy), 1 = at the opposite touch, &gt;1 = out of the
        /// book. May be negative when a price sits outside the touch on the passive side; logged raw
        /// on purpose.
        /// </summary>
        private static decimal CrossRatio(OrderDirection direction, decimal price, decimal bid, decimal ask)
        {
            decimal spread = ask - bid;
            if (spread <= 0 || price == 0)
            {
                return 0;
            }
            return direction switch
            {
                OrderDirection.Buy => (price - bid) / spread,
                OrderDirection.Sell => (ask - price) / spread,
                _ => 0
            };
        }
        /// <summary>
        /// // Defensive: IV Model price override
        /// Somewhat temporary and to be refactored. Limit the price to the presumedFillIV coming from the model - a discount dependent on the utility.
        /// Essentially, both KalmanFilter price and this PresumedIV-utility based price must be good enough to offer competitive quotes.
        /// </summary>
        private bool IsPricerOverridePricesWithPresumedIvFillDefensively(string underlying)
        {
            return Cfg.PricerOverridePricesWithPresumedIvFillDefensively.TryGetValue(underlying, out bool pricerOverridePricesWithPresumedIvFillDefensively) ? pricerOverridePricesWithPresumedIvFillDefensively : Cfg.PricerOverridePricesWithPresumedIvFillDefensively[CfgDefault];
        }

        private bool IsUtilityGtMin(QuoteRequest<Option> qr)
        {
            double minUtility = Cfg.MinUtility.TryGetValue(qr.Underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
            if (qr.UtilityOrder.Utility < minUtility)
            {
                Log($"{Time} GetQuote: UtilityHigh not anymore greater minUtil => Quoting Price 0. utilityOrderHigh={qr.UtilityOrder.Utility}. utilityOrderLowCrossSpread={qr.UtilityOrder.Utility}. QuoteRequest Util: {qr.UtilityOrder.Utility}");
            }
            return qr.UtilityOrder.Utility < minUtility;
        }

        private decimal BufferPriceCrossingSpread(QuoteRequest<Option> qr, decimal price)
        {
            // Shouldn't just go by ticket. Imagine it's cancelled and first new submission is crossing much of the spread. Would wanna buffer that too!
            if (Cfg.BufferIntraSpreadQuotes && SpreadBuffers[qr.OrderDirection].TryGetValue(qr.Symbol, out SpreadBuffer sp))
            {
                price = sp.BufferIntraSpreadQuote(price);
            }
            else if (Cfg.BufferIntraSpreadQuotes)
            {
                Error($"{Time} GetQuote: BufferIntraSpreadQuotes is true, but no SpreadBuffer found for {qr.Symbol}. Expected to be instantiated in SecurityInitializer.");
            }
            return price;
        }

        /// <summary>
        /// // Don't hit deep order book wasting money.
        /// </summary>
        private static decimal LimitPriceToBBO(QuoteRequest<Option> qr, decimal price)
        {
            return qr.OrderDirection switch
            {
                OrderDirection.Buy => Math.Min(price, qr.Option.AskPrice),
                OrderDirection.Sell => Math.Max(price, qr.Option.BidPrice),
                OrderDirection.Hold => throw new ArgumentException($"Unsupported order direction {qr.OrderDirection}"),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
        }

        private decimal? GetKalmanQuote(QuoteRequest<Option> qr)
        {
            if (!IvSurfaceSsviMid.TryGetValue((Equity)Securities[qr.Underlying], out IIVSurface ivs))
            {
                Log($"{Time} GetKalmanQuote(): {qr.Underlying} No KalmanFilter found for {qr.Underlying}");
                return null;
            }

            if (!ivs.IsCalibrated || !ivs.HasParams(qr.Option)) return null;

            double modelIv = ivs.IV(qr.Option);
            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            double kfPriceMid = qr.OrderDirection switch
            {
                OrderDirection.Buy => ocw.NPV(modelIv, null),
                OrderDirection.Sell => ocw.NPV(modelIv, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            //kfPriceWSpread = qr.OrderDirection switch
            //{
            //    OrderDirection.Buy => (decimal)ocw.NPV(modelIV - ((double)ivMeanSpread) / 2, null),
            //    OrderDirection.Sell => (decimal)ocw.NPV(modelIV + ((double)ivMeanSpread) / 2, null),
            //    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            //};
            double kfPriceWSpread = qr.OrderDirection switch
            {
                OrderDirection.Buy => ocw.NPV(modelIv, null),
                OrderDirection.Sell => ocw.NPV(modelIv, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            Log($"{Time} GetKalmanQuote(): direction={qr.OrderDirection} option={qr.Option}, kfPriceMid={kfPriceMid:0.0000}, kfModelIV={modelIv:0.00}, kfPriceWSpread={kfPriceWSpread:0.0000}, bidPrice={qr.Option.BidPrice}, bidIV={IvBids[qr.Option.Symbol].IVBidAsk.IV:0.00}, askPrice={qr.Option.AskPrice}, askIV={IvAsks[qr.Option.Symbol].IVBidAsk.IV:0.00}, spot={MidPrice(qr.Option.Underlying.Symbol)}");
            return (decimal?)kfPriceWSpread;
        }


        internal Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            
            decimal price = PricingStrategy[qr.Underlying](qr);

            // Defensive rounding and adjustments
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection == OrderDirection.Sell);
            double ivPrice = OptionContractWrap.E(this, qr.Option, Time.Date).IV(price, MidPrice(qr.Symbol.Underlying), 0.001);
            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, qr.UtilityOrder, null, 0);
        }
    }
}
