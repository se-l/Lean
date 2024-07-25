using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
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
                Log($"{Time} GetKalmanQuote(): {qr.Underlying} No KalmanFilter found for {qr.Underlying}, {qr.Option.Expiry}, {qr.Option.Right}");
                return null;
            }
            if (!kf.IsReady())
            {
                Log($"{Time} GetKalmanQuote(): {qr.Underlying} KalmanFilter is not ready for {qr.Underlying}, {qr.Option.Expiry}, {qr.Option.Right}, #Updates={kf.NUpdated}");
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
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;

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
                // Aggressive. Problem: Sweepratio goes up, while order never matches that ratio because overriden further below...
                // Further bad: It sweeps the price spread instead of IV. Impacted by others quoting aggressively quickly.
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
                // Implemented a generic sweeper. Would want to backtest if the defensive override with presumedModelPrice can be switched off without losing a lot of money.

                decimal kfPrice = GetKalmanQuote(qr) ?? priceSpreadDiscounted;

                // Aggressive Max Buy / Min Sell - protected by BufferIntraSpreadQuote.
                // Aggressive Sweeper run in last hour on release day and fairly frequently the days after release.
                price = qr.OrderDirection switch
                {
                    OrderDirection.Buy => Math.Max(kfPrice, priceSpreadDiscounted),
                    OrderDirection.Sell => Math.Min(kfPrice, priceSpreadDiscounted),
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
            bool pricerOverridePricesWithPresumedIVFillDefensively = Cfg.PricerOverridePricesWithPresumedIVFillDefensively.TryGetValue(qr.Underlying.Value, out pricerOverridePricesWithPresumedIVFillDefensively) ? pricerOverridePricesWithPresumedIVFillDefensively : Cfg.PricerOverridePricesWithPresumedIVFillDefensively[CfgDefault];
            if (pricerOverridePricesWithPresumedIVFillDefensively
                && Cfg.UseKalmanFilterBeforeEarningsRelease 
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

            // Shouldn't just go by ticket. Imagine it's cancelled and first new submission is crossing much of the spread. Would wanna buffer that too!
            if (Cfg.BufferIntraSpreadQuotes && SpreadBuffers[qr.OrderDirection].TryGetValue(qr.Symbol, out SpreadBuffer sp))
            {
                price = sp.BufferIntraSpreadQuote(price);
            }
            else if (Cfg.BufferIntraSpreadQuotes)
            {
                Error($"GetQuote: BufferIntraSpreadQuotes is true, but no SpreadBuffer found for {qr.Symbol}. Expected to be instantiated in SecurityInitializer.");
            }

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
            decimal spreadDiscountSweepMinSpreadRatio = Cfg.SpreadDiscountSweepMinSpreadRatio.TryGetValue(utilityOrder.Underlying.Value, out spreadDiscountSweepMinSpreadRatio) ? spreadDiscountSweepMinSpreadRatio : Cfg.SpreadDiscountSweepMinSpreadRatio[CfgDefault];
            res = Math.Min(res, spreadDiscountSweepMinSpreadRatio);

            Log($"{Time} SpreadDiscountSweep: {symbol} res={res} sweepRatio={sweepRatio}, maxAcceptableSpreadRatio={maxAcceptableSpreadRatio}, " +
                $"spread={spread} utilPV={utilPV}, utilEquityPosition={utilityOrder.UtilityEquityPosition}. bid={bid}, ask={ask}");

            return res;
        }
    }
}
