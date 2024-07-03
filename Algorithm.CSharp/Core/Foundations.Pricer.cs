using Accord.Statistics.Running;
using Fasterflect;
using MathNet.Numerics.LinearAlgebra.Factorization;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System;
using static QLNet.Callability;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        /// <summary>
        /// Sudden quote jumps, leading to price spread variation can mess up my discounting expressed in a factor*priceSpread. Therefore, taking min/max of IVSurface derived and market surface. IV surface can get too wide, tight too at times, pending fix.
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        internal decimal PriceSpread(OptionContractWrap ocw)
        {
            decimal iVSurfaceSpread;
            var option = ocw.Contract;
            decimal marketQuoteSpread = option.AskPrice - option.BidPrice;

            double? bidIV = IVSurfaceRelativeStrikeBid[option.Symbol.Underlying].IV(option.Symbol);
            double? askIV = IVSurfaceRelativeStrikeAsk[option.Symbol.Underlying].IV(option.Symbol);

            // bad nesting. refactor sometime
            if (askIV == null)
            {
                Error($"PriceSpread: Unexpected askIV={askIV}. Defaulting to 0 ivSurfaceSpread.");
                Log(Environment.StackTrace);
                iVSurfaceSpread = 0;
            }
            else
            {
                decimal? iVSurfaceBidPrice = (bidIV ?? 0) == 0 ? IntrinsicValue(option) : (decimal?)ocw.AnalyticalIVToPrice((double)bidIV);
                decimal? iVSurfaceAskPrice = (askIV ?? 0) == 0 ? IntrinsicValue(option) : (decimal?)ocw.AnalyticalIVToPrice((double)askIV);

                if (iVSurfaceAskPrice == null || iVSurfaceBidPrice == null)
                {
                    Error($"PriceSpread: Unexpected null ivSurfacePrice: iVSurfaceBidPrice={iVSurfaceBidPrice}, iVSurfaceAskPrice ={iVSurfaceAskPrice}, bidIV={bidIV}, askIV={askIV}. Defaulting to 0 ivSurfaceSpread.\n{Environment.StackTrace}");
                    iVSurfaceSpread = 0;
                }
                else
                {
                    iVSurfaceSpread = (decimal)iVSurfaceAskPrice - (decimal)iVSurfaceBidPrice;
                }
            }

            // At the moment, preferring tight over wide spreads as it minimized chance of giving outsized discounts. However, to be revisited.
            return Math.Min(marketQuoteSpread, iVSurfaceSpread);
        }

        /// <summary>
        /// Override in respective Strategy.
        /// def D(uD1, uD0, b= .001):
        ///    if uD1 > uD0:
        ///        return np.nan
        ///    dUdD = uD1 - uD0
        ///    d0 = sd(uD0)
        ///    return d0 + b* d0 * dUdD
        /// 
        /// </summary>
        public decimal SpreadDiscount(Symbol underlying, IUtilityOrder utilityOrderHigh, IUtilityOrder utilityOrderLow)
        {
            double utilLow = utilityOrderLow.Utility;
            double utilHigh = utilityOrderHigh.Utility;

            double discountUtilHigh;
            if (utilLow > utilHigh)
            {
                if (utilLow > utilHigh * 1.05)
                {
                    Error($"SpreadDiscount: utilLow={utilLow} > utilHigh={utilHigh}. Swapping for now. Investigate.");
                }
                (utilLow, utilHigh) = (utilHigh, utilLow);
            }
            double dUdD = utilLow - utilHigh;

            double zeroSDUtil = Cfg.ZeroSDUtil.TryGetValue(underlying, out zeroSDUtil) ? zeroSDUtil : Cfg.ZeroSDUtil[CfgDefault];
            double slopeNeg = Cfg.SlopeNeg.TryGetValue(underlying, out slopeNeg) ? slopeNeg : Cfg.SlopeNeg[CfgDefault];
            double slopePos = Cfg.SlopeNeg.TryGetValue(underlying, out slopePos) ? slopePos : Cfg.SlopePos[CfgDefault];
            double utilBidTaperer = Cfg.UtilBidTaperer.TryGetValue(underlying, out utilBidTaperer) ? utilBidTaperer : Cfg.UtilBidTaperer[CfgDefault];

            if (utilHigh >= zeroSDUtil)
            {
                discountUtilHigh = 2 / (1 + Math.Exp(slopePos * (utilHigh - zeroSDUtil))) - 1;
            }
            else
            {
                discountUtilHigh = 2 / (1 + Math.Exp(slopeNeg * (utilHigh - zeroSDUtil))) - 1;
            }

            decimal maxSpreadDiscount = Cfg.MaxSpreadDiscount.TryGetValue(underlying, out maxSpreadDiscount) ? maxSpreadDiscount : Cfg.MaxSpreadDiscount[CfgDefault];
            return Math.Min(maxSpreadDiscount, ToDecimal(discountUtilHigh + utilBidTaperer * discountUtilHigh * dUdD));
        }

        public decimal? GetKalmanQuote(QuoteRequest<Option> qr)
        {
            decimal kfPrice;

            if (!KalmanFilters.TryGetValue((qr.Underlying, qr.Option.Expiry, qr.Option.Right), out KalmanFilter kf))
            {
                Log($"GetQuote: No KalmanFilter found for {qr.Underlying}, {qr.Option.Expiry}, {qr.Option.Right}");
                return null;
            }

            double meanIV = kf.KalmanMeanIV(qr.Option);
            double bidIV = IVBids[qr.Option.Symbol].IVBidAsk.IV;
            double askIV = IVAsks[qr.Option.Symbol].IVBidAsk.IV;
            kfPrice = qr.OrderDirection switch
            {
                OrderDirection.Buy => kf.KalmanBidPrice(qr.Option),
                OrderDirection.Sell => kf.KalmanAskPrice(qr.Option),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            Log($"GetKalmanQuote: direction={qr.OrderDirection} option={qr.Option}, kfPrice={kfPrice}, bid={qr.Option.BidPrice}, ask={qr.Option.AskPrice}, kfMeanIV={meanIV}, bidIV={bidIV}, askIV={askIV}");
            return kfPrice;
        }

        /// <summary>
        /// Need to unify a bunch of concepts that flow into this. Currently, somewhat of a majority vote.
        /// </summary>
        /// <param name="qr"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        internal Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            IUtilityOrder utilityOrderCrossSpread;
            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            decimal priceSpread = PriceSpread(ocw);
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;

            if (!IVSurfaceRelativeStrikeBid.ContainsKey(qr.Underlying) || !IVSurfaceRelativeStrikeAsk.ContainsKey(qr.Underlying))
            {
                Error($"GetQuote: Missing IV indicator for {qr.Symbol}. Expected to have been filled in securityInitializer. Quoting 0.");
                Log(Environment.StackTrace);
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, qr.UtilityOrder, null);
            }

            // Since the signal, markets might have moved. Update the quote request's utility order.
            switch (qr.OrderDirection)
                {                 
                case OrderDirection.Buy:
                    qr.UtilityOrder = UtilityOrderFactory.Create(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
                    utilityOrderCrossSpread = UtilityOrderFactory.Create(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
                    break;
                case OrderDirection.Sell:
                    qr.UtilityOrder = UtilityOrderFactory.Create(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
                    utilityOrderCrossSpread = UtilityOrderFactory.Create(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
                    break;
                default:
                    throw new ArgumentException($"GetQuote: Unknown order direction {qr.OrderDirection}");
            }

            double minUtility = Cfg.MinUtility.TryGetValue(qr.Underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
            if (qr.UtilityOrder.Utility < minUtility)
            {
                Log($"GetQuote: UtilityHigh not anymore greater minUtil => Quoting Price 0. utilityOrderHigh={qr.UtilityOrder.Utility}. utilityOrderLowCrossSpread={utilityOrderCrossSpread.Utility}. QuoteRequest Util: {qr.UtilityOrder.Utility}");
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, qr.UtilityOrder, null);
            }

            //// Only for debugging purposes. Uncomment if debugging utilLow > utilHigh.
            //if (utilityOrderCrossSpread.Utility > qr.UtilityOrder.Utility)
            //{
            //    UtilityOrder utilOrder;
            //    UtilityOrder utilityOrderCrossSpread2;
            //    switch (qr.OrderDirection)
            //    {
            //        case OrderDirection.Buy:
            //            utilOrder = new(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
            //            utilityOrderCrossSpread2 = new(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
            //            break;
            //        case OrderDirection.Sell:
            //            utilOrder = new(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
            //            utilityOrderCrossSpread2 = new(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
            //            break;
            //        default:
            //            throw new ArgumentException($"GetQuote: Unknown order direction {qr.OrderDirection}");
            //    }
            //}
            RiskDiscount discountAbsolute = AbsoluteDiscounts[qr.Option.Underlying.Symbol];

            decimal spreadDiscount;
            decimal price;

            if (SweepState[qr.Symbol][qr.OrderDirection].IsSweepScheduled())
            {
                // Aggressive
                spreadDiscount = SpreadDiscountSweep(qr.UtilityOrder);
            }
            else
            {
                //Log($"GetQuote: Not sweeping. Using SweepDiscount. qr.Symbol={qr.Symbol}, qr.OrderDirection={qr.OrderDirection}");
                spreadDiscount = SpreadDiscount(qr.Underlying, qr.UtilityOrder, utilityOrderCrossSpread);
            }

            decimal priceSpreadDiscounted = qr.OrderDirection switch
            {
                OrderDirection.Buy => qr.Option.BidPrice + spreadDiscount * marketPriceSpread + (decimal)discountAbsolute.X0,
                OrderDirection.Sell => qr.Option.AskPrice - spreadDiscount * marketPriceSpread - (decimal)discountAbsolute.X0,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            bool isPreparingEarningsRelease = PreparingEarningsRelease(qr.Underlying);
            bool isAfterEarningsRelease = IsAfterEarningsRelease(qr.Underlying);

            // messy. Refactor this into some utility functions returning null on error, handle null.
            TimeSpan timeStartKfBeforeRelease;
            TimeSpan timeStartKfAfterRelease;
            try
            {
                timeStartKfBeforeRelease = AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfBeforeRelease, qr.Underlying.Value));
                timeStartKfAfterRelease = AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfAfterRelease, qr.Underlying.Value));
            }
            catch (Exception e)
            {
                Error($"GetQuote: {e.Message}");
                Log(Environment.StackTrace);
                timeStartKfBeforeRelease = new TimeSpan(0, 23, 0, 0);
                timeStartKfAfterRelease = new TimeSpan(0, 23, 0, 0);
            }
            

            bool useKfBeforeRelease = 
                isPreparingEarningsRelease
                && Cfg.UseKalmanFilterBeforeEarningsRelease
                && Time.TimeOfDay >= timeStartKfBeforeRelease;
            bool useKfAfterRelease = 
                isAfterEarningsRelease
                && Cfg.UseKalmanFilterAfterEarningsRelease
                && Time.TimeOfDay >= timeStartKfAfterRelease;

            if (useKfBeforeRelease | useKfAfterRelease)
            {
                // Purpose of KalmanFilter is to avoid following very aggressive quotes, that is NBBO * SweepDiscount. Easy.

                // Not yet implemented:
                //      Purpose of KalmanFilter is to quote more aggressively when opportunity is good. Comparing it to sweeper which will eventually get to KalmanFilter price.
                //      Tricky because we'd want to sweep towards that more aggressive price and not waste money jumping to it.

                decimal kfPrice = GetKalmanQuote(qr) ?? priceSpreadDiscounted;

                // Defensive
                price = qr.OrderDirection switch
                {
                    OrderDirection.Buy => Math.Min(kfPrice, priceSpreadDiscounted),
                    OrderDirection.Sell => Math.Max(kfPrice, priceSpreadDiscounted),
                    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
                };
            }
            else // Not using KalmanFilter
            {
                price = priceSpreadDiscounted;
            }

            // Defensive: IV Model price override
            // Somewhat temporary and to be refactored. Limit the price to the presumedFillIV coming from the model - a discount dependent on the utility.
            // Essentially, both KalmanFilter price and this PresumedIV-utility based price must be good enough to offer competitive quotes.
            if (Cfg.UseKalmanFilterBeforeEarningsRelease 
                && isPreparingEarningsRelease
                && PresumedFillIV.ContainsKey(qr.Option)
                )
            {
                PresumedFillMetrics presumedFill = new(qr, this);
                decimal overridePrice = presumedFill.DiscountedPrice;

                switch (qr.OrderDirection)
                {
                    case OrderDirection.Buy:                        
                        if (overridePrice < price)  // Defensive. (price can be higher than presumedFillPrice due to sweep discounting.
                        {
                            Log($"GetQuote: Defensively overriding Quote Price {price} with {overridePrice}. modelPresumedPrice={presumedFill.PresumedFillPrice}, ModelIV={PresumedFillIV[qr.Option]}, priceDiscount={presumedFill.Discount}");
                        }
                        price = Math.Min(overridePrice, price);
                        break;
                    case OrderDirection.Sell:                        
                        if (overridePrice > price)  // Defensive. (price can be higher than presumedFillPrice due to sweep discounting.
                        {
                            Log($"GetQuote: Defensively overriding Quote Price {price} with {overridePrice}. modelPresumedPrice={presumedFill.PresumedFillPrice}, ModelIV={PresumedFillIV[qr.Option]}, priceDiscount={presumedFill.Discount}");
                        }
                        price = Math.Max(overridePrice, price);
                        break;
                    default:
                        throw new ArgumentException($"Unknown order direction {qr.OrderDirection}");
                }
            }

            // Don't hit deep order book wasting money.
            price = qr.OrderDirection switch
            {
                OrderDirection.Buy => Math.Min(price, qr.Option.AskPrice),
                OrderDirection.Sell => Math.Max(price, qr.Option.BidPrice),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            // For tight spreads, a tickSize difference of, e.g., 0.01 can make a signiicant difference in terms of IV spread. Therefore, the price is rounded defensively away from midPrice.
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection == OrderDirection.Sell);  // Can go against sweep
            double ivPrice = (double)ocw.IV(price, MidPrice(qr.Symbol.Underlying), 0.001);
 
            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, qr.UtilityOrder, utilityOrderCrossSpread, spreadDiscount);
        }

        public decimal SpreadDiscountSweep(IUtilityOrder utilityOrder)
        {
            Symbol symbol = utilityOrder.Symbol;

            decimal bid = Securities[symbol].BidPrice;
            decimal ask = Securities[symbol].AskPrice;
            decimal spread = ask - bid;

            decimal sweepRatio = SweepState[symbol][utilityOrder.OrderDirection].SweepRatio;

            double utilPV = utilityOrder.UtilityPV;  // That'll be okayish, directly comparable with spreads to pay.
            decimal maxAcceptableSpreadRatio = spread <= 0 || utilPV == 0 ? sweepRatio : ToDecimal((utilPV / 2)) / (100 * spread);

            var res = Math.Min(sweepRatio, maxAcceptableSpreadRatio);
            res = Math.Min(res, 1.001m);

            Log($"{Time} SpreadDiscountSweep: {symbol} res={res} sweepRatio={sweepRatio}, maxAcceptableSpreadRatio={maxAcceptableSpreadRatio}, " +
                $"spread={spread} utilPV={utilPV}, utilEquityPosition={utilityOrder.UtilityEquityPosition}. bid={bid}, ask={ask}");

            return res;
        }
    }
}
