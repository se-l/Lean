using MathNet.Numerics.LinearAlgebra.Factorization;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using static QuantConnect.Messages;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public QuoteDiscount DiscountMetric(QuoteRequest<Option> qr, RiskDiscount riskDiscount)
        {
            // Distinguish between urgent risk (high absolute risk including equity) and non-urgent risk (high abs. delta summing derivatives only).
            // In order to maintain good cash book, want to keep stocks low, but no need to give up much spread for that.

            double riskCurrent = (double)PfRisk.DerivativesRiskByUnderlying(qr.Symbol, riskDiscount.Metric);  // These 2 risk functions probably take 30% of CPU time in GetQuoteDiscounts.
            double riskIfFilled = riskCurrent + (double)PfRisk.RiskIfFilled(qr.Symbol, qr.Quantity, riskDiscount.Metric);
            //double riskBenefit = riskCurrent - riskIfFilled;
            // x0 is targetRisk in 100BpUnderlying Price Change

            // So the discount for a hypotheical trade that sets risk to target minus the trade discussed. That is meant to account for 
            // different discount in high-risk (more disc/punishment) vs low-risk (free tradin) regimes as well as risk reversals (eg -low-risk to +high-risk).
            double discountToZeroRisk = riskDiscount.Discount(riskCurrent - riskDiscount.TargetRisk);
            double discountToRiskIfFilled = riskDiscount.Discount(riskIfFilled - riskDiscount.TargetRisk);

            double discount = discountToZeroRisk - discountToRiskIfFilled;
            return new QuoteDiscount(
                riskDiscount.Metric,
                Math.Min(Math.Max(riskDiscount.CapMin, discount), riskDiscount.CapMax),
                riskCurrent,
                riskIfFilled,
                (riskIfFilled - riskDiscount.TargetRisk)
                );
        }

        public QuoteDiscount DiscountEvents(QuoteRequest<Option> qr)
        {
            double spreadFactor = 0;
            // Get out / Liquidate Discount (e.g. earnings release, rollover/assignments/expiry, etc.) to be refined.
            // Trade Embargo pricing. and avoid assignments

            RiskDiscount riskDiscount = EventDiscounts[qr.Option.Underlying.Symbol];

            // Below code is to avoid any kind of event, not well tested. Below the updated version handling this via a calender spread.
            //if (
            //    embargoedSymbols.Contains(option.Symbol)
            //    || (option.Symbol.ID.Date - Time.Date).Days <= 2  // Options too close to expiration. This is not enough. Imminent Gamma squeeze risk. Get out fast.
            //    )
            //{
            //    //double discount = riskDiscount.X0 + riskDiscount.X1 * deltaTargetRisk + riskDiscount.X2 * Math.Pow(deltaTargetRisk, 2);
            //    discount = riskDiscount.X0;
            //}

            // Increase spread factor with urgency, ie, days remaining until event.
            double utilityEventUpcoming = qr.UtilityOrder.UtilityEventUpcoming;
            if (utilityEventUpcoming > 0)
            {
                spreadFactor = riskDiscount.Discount(utilityEventUpcoming);
                //Log($"{Time} DiscountEvents: {qr.Symbol} utilityEventUpcoming={utilityEventUpcoming}, spreadFactor={spreadFactor}");
            }

            return new QuoteDiscount(
                riskDiscount.Metric,
                spreadFactor,
                0, 0, 0
                ); 
        }

        public IEnumerable<QuoteDiscount> GetQuoteDiscounts(QuoteRequest<Option> qr)  // 30% of CPU time here.
        {
            // Refactor to send this through as object, so it can be logged if actually filled.
            // For every metric that should influence the price, 3 params are configurable for a quadratic / progressive discount. 0,1,2.
            // The discounts are summed up and min/max capped (configurable).
            // targetRisk is absolute, while x0,x1 and x2 are relative to the precentage deviation off absoltue target risk.
            // Need to punish/incentivize how much a trade changes risk profile, but also how urgent it is....
            // Big negative delta while pf is pos, great, but only if whole portfolio is not worse off afterwards. Essentially need the area where nothing changes.

            QuoteDiscount discountDelta = DiscountMetric(qr, DeltaDiscounts[qr.Option.Underlying.Symbol]);
            QuoteDiscount discountGamma = DiscountMetric(qr, GammaDiscounts[qr.Option.Underlying.Symbol]);
            QuoteDiscount discountEvents = DiscountEvents(qr);

            return new List<QuoteDiscount>() { discountDelta, discountGamma, discountEvents };
        }

        public double? SurfaceIV(Symbol symbol, OrderDirection orderDirection)
        {
            return orderDirection switch
            {
                OrderDirection.Buy => IVSurfaceRelativeStrikeBid[symbol.Underlying].IV(symbol),
                OrderDirection.Sell => IVSurfaceRelativeStrikeAsk[symbol.Underlying].IV(symbol),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };
        }


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
        /// Derive a price from the IV surface.
        /// </summary>
        public double? IVStrikePrice(OptionContractWrap ocw, OrderDirection orderDirection)
        {
            double? iv = SurfaceIV(ocw.Contract.Symbol, orderDirection);

            return orderDirection switch
            {
                OrderDirection.Buy => (iv ?? 0) == 0 ? 0 : ocw.AnalyticalIVToPrice((double)iv),
                OrderDirection.Sell => (iv ?? 0) == 0 ? 0 : ocw.AnalyticalIVToPrice((double)iv),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };
        }

        public Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            // Intrinsice value
            // Extrinsic Time value:
            //      Volatility value Implied
            //      Volatility value Historical
            //      Volatility value Forecasted
            //      Directional Bias / Drift
            //
            // Interest rate
            // Dividend rate
            // Option Liquidity
            // Prices options based on portfolio risk (delta - just dont trade); gamma (higher price for bad gamma), extrinsic value (better price if much is captured), IV/market bid ask, HV forecasts, dividends, liquidity of contract, interest rate.

            // Risk reduction
            // Options with detrimental delta dont arrive here at the moment
            // Gamma: (gamma pf + gammaIfFilled) -> 0 best price -1/1 worse prices. 0: 0.5 of spread. intercepting spreadfactor 1 at (tune: 0.5 gamma). beyond which spreadfactor is larger than 1.
            // correction: form rewards trades, not where in portfolio we'end up, hence:   (gammaPfNow - gammaPfIfFilled) -> 0 best price -1/1 worse prices. 0: 0.5 of spread. intercepting spreadfactor 1 at (tune: 0.5 gamma). beyond which spreadfactor is larger than 1.

            // Profit maximization
            // For contracts where I expect higher volume, reduce the spread in order to maximise volume * spread. Usually spread covers hedging costs. So just for very liquid contracts, can offer better rates. Try for selected ones and plot! Fees more
            // relevant then. Might only work at NasdaqQM.
            decimal? smoothedIVPrice;
            decimal price;

            var quoteDiscounts = GetQuoteDiscounts(qr);
            decimal netSpreadFactor = (decimal)quoteDiscounts.Sum(qd => qd.SpreadFactor);

            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            decimal priceSpread = PriceSpread(ocw);
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;


            if (!IVSurfaceRelativeStrikeBid.ContainsKey(qr.Underlying) || !IVSurfaceRelativeStrikeAsk.ContainsKey(qr.Underlying))
            {
                Error($"GetQuote: Missing IV indicator for {qr.Symbol}. Expected to have been filled in securityInitializer. Quoting 0.");
                Log(Environment.StackTrace);
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, quoteDiscounts, qr.UtilityOrder);
            }

            double? bidIV = IVSurfaceRelativeStrikeBid[qr.Symbol.Underlying].IV(qr.Symbol);
            double? askIV = IVSurfaceRelativeStrikeAsk[qr.Symbol.Underlying].IV(qr.Symbol);
            if (bidIV > askIV)
            {
                Error($"GetQuote: IVSurface: BidIV > AskIV for {qr.Symbol.Underlying} {qr.Symbol.ID.Date} {qr.Symbol.ID.OptionRight} {qr.Symbol.ID.StrikePrice} {bidIV} {askIV}");
                //Log(Environment.StackTrace);
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, quoteDiscounts, qr.UtilityOrder);
            }

            smoothedIVPrice = (decimal?)IVStrikePrice(ocw, qr.OrderDirection);

            RiskDiscount discountAbsolute = AbsoluteDiscounts[qr.Option.Underlying.Symbol];
            smoothedIVPrice = (smoothedIVPrice ?? 0) + qr.OrderDirection switch
            {
                OrderDirection.Buy => (decimal)discountAbsolute.X0,
                OrderDirection.Sell => (decimal)-discountAbsolute.X0,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            if (smoothedIVPrice == null || smoothedIVPrice <= 0)
            {
                Error($"GetQuote: IVStrikePrice returned invalid price. smoothedIVPrice={smoothedIVPrice}. Quoting 0.");
                //Log(Environment.StackTrace);
                return new Quote<Option>(qr.Option, qr.Quantity, 0, 0, quoteDiscounts, qr.UtilityOrder);
            }

            smoothedIVPrice = qr.OrderDirection switch
            {
                OrderDirection.Buy => (decimal)smoothedIVPrice + priceSpread * netSpreadFactor,
                OrderDirection.Sell => (decimal)smoothedIVPrice - priceSpread * netSpreadFactor,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            // Quick hacky fix to incorporate a faster fill for calendar spread hedges
            if (qr.UtilityOrder.UtilityEventUpcoming > 0 && ActiveRegimes[qr.Underlying].Contains(Regime.SellEventCalendarHedge))
            {

                // THe 0.2 / 0.8 - to be refactored. They put lower and upper limit on discounting. At least to be moved to config ...
                price = qr.OrderDirection switch
                {
                    OrderDirection.Buy => Math.Min((decimal)smoothedIVPrice, qr.Option.BidPrice + 1.0m * marketPriceSpread),
                    OrderDirection.Sell => Math.Max((decimal)smoothedIVPrice, qr.Option.BidPrice + 0.0m * marketPriceSpread),
                    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
                };
            }
            else if (ManualOrderInstructionBySymbol.ContainsKey(qr.Symbol.Value) && Cfg.ExecuteManualOrderInstructions)
            {
                ManualOrderInstruction manualOrderInstruction = ManualOrderInstructionBySymbol[qr.Symbol.Value];
                price = qr.OrderDirection switch
                {
                    OrderDirection.Buy => (decimal)qr.Option.BidPrice + manualOrderInstruction.SpreadFactor * marketPriceSpread,
                    OrderDirection.Sell => (decimal)qr.Option.AskPrice - manualOrderInstruction.SpreadFactor * marketPriceSpread,
                    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
                };
                Log($"{Time} GetQuote.ManualOrderInstructionBySymbol: {qr.OrderDirection} {qr.Symbol} Quantity={qr.Quantity} Bid={qr.Option.BidPrice}, Quote={price}, Ask={qr.Option.AskPrice}");
            }
            else
            {
                // THe 0.2 / 0.8 - to be refactored. They put lower and upper limit on discounting. At least to be moved to config ...
                price = qr.OrderDirection switch
                {
                    OrderDirection.Buy => Math.Min((decimal)smoothedIVPrice, qr.Option.BidPrice + 0.2m * marketPriceSpread),
                    OrderDirection.Sell => Math.Max((decimal)smoothedIVPrice, qr.Option.BidPrice + 0.8m * marketPriceSpread),
                    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
                };
            }

            // For tight spreads, a tickSize difference of, e.g., 0.01 can make a signiicant difference in terms of IV spread. Therefore, the price is rounded defensively away from midPrice.
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection == OrderDirection.Buy ? false : true);
            double ivPrice = (double)ocw.IV(price, MidPrice(qr.Symbol.Underlying), 0.001);
            //if (true) //qr.Symbol.Value == "PFE   240119C00038000" && Time.TimeOfDay > new TimeSpan(0, 9, 49) & Time.TimeOfDay < new TimeSpan(0, 9, 50))
            //{
            //    Log($"option: {qr.Symbol}");
            //    Log($"priceSpread: {priceSpread}");
            //    Log($"Option.BidPrice : {qr.Option.BidPrice}");
            //    Log($"Option.AskPrice : {qr.Option.AskPrice}");
            //    Log($"bidIV: {bidIV}");
            //    Log($"askIV: {askIV}");
            //    Log($"smoothedIVPrice: {smoothedIVPrice}");
            //    Log($"price : {price}");
            //    Log($"priceRounded : {priceRounded}");
            //}
            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, quoteDiscounts, qr.UtilityOrder);
        }
    }
}
