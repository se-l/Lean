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
        /// Sudden quote jumps, leading to price spread variation can mess up my discounting expressed in a factor*priceSpread. Therefore, taking min/max of IVSurface derived and market surface. IV surface can get too wide, tight too at times, pending fix.
        /// </summary>
        /// <param name="symbol"></param>
        /// <returns></returns>
        public decimal PriceSpread(OptionContractWrap ocw)
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
        /// def D(uD1, uD0, b= .001):
        ///    if uD1 > uD0:
        ///        return np.nan
        ///    dUdD = uD1 - uD0
        ///    d0 = sd(uD0)
        ///    return d0 + b* d0 * dUdD
        /// </summary>
        public decimal SpreadDiscount(Symbol underlying, double utilHigh, double utilLow)
        {
            double discountUtilHigh;
            if (utilLow > utilHigh)
            {
                Error($"SpreadDiscount: utilLow={utilLow} > utilHigh={utilHigh}. Swapping for now. Investigate.");
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

        /// <summary>
        /// Should not make price based on very unstable market data quotes. Need util(IV)
        /// </summary>
        /// <param name="qr"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentException"></exception>
        public Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            UtilityOrder utilityOrderCrossSpread;
            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            decimal priceSpread = PriceSpread(ocw);
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;

            if (!IVSurfaceRelativeStrikeBid.ContainsKey(qr.Underlying) || !IVSurfaceRelativeStrikeAsk.ContainsKey(qr.Underlying))
            {
                Error($"GetQuote: Missing IV indicator for {qr.Symbol}. Expected to have been filled in securityInitializer. Quoting 0.");
                Log(Environment.StackTrace);
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, null, qr.UtilityOrder, null);
            }

            // Since the signal, markets might have moved. Update the quote request's utility order.
            switch (qr.OrderDirection)
                {                 
                case OrderDirection.Buy:
                    qr.UtilityOrder = new(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
                    utilityOrderCrossSpread = new UtilityOrder(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
                    break;
                case OrderDirection.Sell:
                    qr.UtilityOrder = new(this, qr.Option, qr.Quantity, qr.Option.AskPrice);
                    utilityOrderCrossSpread = new UtilityOrder(this, qr.Option, qr.Quantity, qr.Option.BidPrice);
                    break;
                default:
                    throw new ArgumentException($"GetQuote: Unknown order direction {qr.OrderDirection}");
            }

            double minUtility = Cfg.MinUtility.TryGetValue(qr.Underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
            if (qr.UtilityOrder.Utility < minUtility)
            {
                Log($"GetQuote: UtilityHigh not anymore greater minUtil => Quoting Price 0. utilityOrderHigh={qr.UtilityOrder.Utility}. utilityOrderLowCrossSpread={utilityOrderCrossSpread.Utility}. QuoteRequest Util: {qr.UtilityOrder.Utility}");
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, null, qr.UtilityOrder, null);
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

            spreadDiscount = SpreadDiscount(qr.Underlying, qr.UtilityOrder.Utility, utilityOrderCrossSpread.Utility);
            price = qr.OrderDirection switch
            {
                OrderDirection.Buy => qr.Option.BidPrice + spreadDiscount * marketPriceSpread + (decimal)discountAbsolute.X0,
                OrderDirection.Sell => qr.Option.AskPrice - spreadDiscount * marketPriceSpread - (decimal)discountAbsolute.X0,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            // For tight spreads, a tickSize difference of, e.g., 0.01 can make a signiicant difference in terms of IV spread. Therefore, the price is rounded defensively away from midPrice.
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection != OrderDirection.Buy);
            double ivPrice = (double)ocw.IV(price, MidPrice(qr.Symbol.Underlying), 0.001);
            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, null, qr.UtilityOrder, utilityOrderCrossSpread, spreadDiscount);
        }
    }
}
