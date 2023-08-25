using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public double GetLimitPrice()
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
            return 0;
        }
        public decimal Position(Symbol symbol)
        {
            return Portfolio.ContainsKey(symbol) ? Portfolio[symbol].Quantity : 0m;
        }
        public double Discount(RiskDiscount riskDiscount, double risk)
        {
            return riskDiscount.X0 + riskDiscount.X1 * Math.Abs(risk) + riskDiscount.X2 * Math.Pow(risk, 2);
        }
        public double DiscountMetric(Option option, decimal quantity, Metric metric)
        {
            Symbol symbol = option.Symbol;
            RiskDiscount riskDiscount = DeltaDiscounts[option.Underlying.Symbol];

            double riskCurrent = (double)pfRisk.DerivativesRiskByUnderlying(symbol, metric);
            double riskIfFilled = riskCurrent + (double)pfRisk.RiskAddedIfFilled(symbol, quantity, metric);

            // x0 is targetRisk in 100BpUnderlying Price Change

            // So the discount for a hypotheical trade that sets risk to target minus the trade discussed. That is meant to account for 
            // different discount in high-risk (more disc/punishment) vs low-risk (free tradin) regimes as well as risk reversals (eg -low-risk to +high-risk).
            double discountToZeroRisk = Discount(riskDiscount, (riskCurrent - riskDiscount.TargetRisk));
            double discountToRiskIfFilled = Discount(riskDiscount, (riskIfFilled - riskDiscount.TargetRisk));
            double discount = discountToZeroRisk - discountToRiskIfFilled;

            //double riskIfFilled = (double)pfRisk.RiskAddedIfFilled(symbol, quantity, Metric.Gamma100BpTotal);
            //// the higher the absolute risk, the more discount towards reducing the risk.
            //discount = Math.Max(-0.7, Math.Min(0.3, Math.Abs(currentGammaRisk) * (1 - (currentGammaRisk + riskIfFilled) / currentGammaRisk)));
            //if (currentGammaRisk < -0.7 && !LiveMode)
            //{
            //    Log($"RiskSpreadDiscount. Gamma too low. Expect discount or increase {symbol} {quantity} CurrentRisk: {currentGammaRisk} riskIfFilled: {currentGammaRisk + riskIfFilled} - Discount: {discount}");  // too much discount might depress the IVBuy into 0 price.
            //}

            return Math.Min(Math.Max(riskDiscount.CapMin, discount), riskDiscount.CapMax);
        }

        public double DiscountEvents(Option option, decimal quantity)
        {
            double discount = 0;
            // Get out / Liquidate Discount (e.g. earnings release, rollover/assignments/expiry, etc.) to be refined.
            // Trade Embargo pricing. and avoid assignments

            RiskDiscount riskDiscount = DeltaDiscounts[option.Underlying.Symbol];
            
            if (
                //EarningsAnnouncements.Where(ea => ea.Symbol == option.Symbol && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()
                (option.Symbol.ID.Date - Time.Date).Days <= 2  // Options too close to expiration. This is not enough. Imminent Gamma squeeze risk. Get out fast.
                )
            {
                //double discount = riskDiscount.X0 + riskDiscount.X1 * deltaTargetRisk + riskDiscount.X2 * Math.Pow(deltaTargetRisk, 2);
                discount = riskDiscount.X0;
            }
            return Math.Min(Math.Max(riskDiscount.CapMin, discount), riskDiscount.CapMax);
        }

        public double SpreadFactor(Option option, decimal position, decimal quantity)
        {
            /// For every metric that should influence the price, 3 params are configurable for a quadratic / progressive discount. 0,1,2.
            /// The discounts are summed up and min/max capped (configurable).
            /// targetRisk is absolute, while x0,x1 and x2 are relative to the precentage deviation off absoltue target risk.
            /// Need to punish/incentivize how much a trade changes risk profile, but also how urgent it is....
            /// Big negative delta while pf is pos, great, but only if whole portfolio is not worse off afterwards. Essentially need the area where nothing changes.

            double discountDelta = DiscountMetric(option, quantity, Metric.Delta100BpTotal);
            double discountGamma = DiscountMetric(option, quantity, Metric.Gamma100BpTotal);
            double discountEvents = DiscountEvents(option, quantity);

            return discountDelta + discountGamma + discountEvents;
        }

        public decimal? IVStrikePrice(OptionContractWrap ocw, OrderDirection orderDirection, double spreadFactor)
        {
            // Bid IV is quote often 0, hence Price would come back as null.
            // The IV Spread gets becomes considerably larger for Bid IV 0, going from suddenly ~20% to 0%. Therefore a Mid IV of ~25% can quickly be ~15%.
            // Hence in the presence of 0, may want to price without any spreadFactor.
            Symbol symbol = ocw.Contract.Symbol;
            decimal? smoothedIVPrice;

            double? bidIV = RollingIVStrikeBid[symbol.Underlying].IV(symbol); // Using Current IV over ExOutlier improved cumulative annual return by ~2 USD per trade (~200 trades).
            double? askIV = RollingIVStrikeAsk[symbol.Underlying].IV(symbol); // ExOutlier is a moving average of the last 100IVs. Doesnt explain why I quoted Selling at less than 90% of bid ask spread.
            double iVSpread = (askIV - bidIV) ?? 0.1 * (askIV ?? 0);  // presuinng default 10 % spread.

            // Issue here. Bid IV is often null, impacting selling due to missing IV Spread. I could default to a kind
            // of default spread. Whenever BidIV is null, it's very low. < ~20%.
            // FF?

            if (orderDirection == OrderDirection.Buy && bidIV == null) { return null; }
            if (orderDirection == OrderDirection.Sell && askIV == null) { return null; }

            smoothedIVPrice = orderDirection switch
            {
                OrderDirection.Buy => (decimal?)ocw.AnalyticalIVToPrice(hv: bidIV + iVSpread * spreadFactor), // not good given non-linearity between IV and price.
                OrderDirection.Sell => (decimal?)ocw.AnalyticalIVToPrice(hv: askIV - iVSpread * spreadFactor),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };
            return smoothedIVPrice;
        }


        /// <summary>
        /// Consider making spreadfactor a function of the forecasted volatility (long mean) and current volatility (short mean). all implied. Then hold until vega earned.
        /// </summary>
        public decimal PriceOptionPfRiskAdjusted(Option option, decimal quantity)
        {
            // Prices options based on portfolio risk (delta - just dont trade); gamma (higher price for bad gamma), extrinsic value (better price if much is captured), IV/market bid ask, HV forecasts, dividends, liquidity of contract, interest rate.

            // Risk reduction
            // Options with detrimental delta dont arrive here at the moment
            // Gamma: (gamma pf + gammaIfFilled) -> 0 best price -1/1 worse prices. 0: 0.5 of spread. intercepting spreadfactor 1 at (tune: 0.5 gamma). beyond which spreadfactor is larger than 1.
            // correction: form rewards trades, not where in portfolio we'end up, hence:   (gammaPfNow - gammaPfIfFilled) -> 0 best price -1/1 worse prices. 0: 0.5 of spread. intercepting spreadfactor 1 at (tune: 0.5 gamma). beyond which spreadfactor is larger than 1.

            // Profit maximization
            // For contracts where I expect higher volume, reduce the spread in order to maximise volume * spread. Usually spread covers hedging costs. So just for very liquid contracts, can offer better rates. Try for selected ones and plot! Fees more
            // relevant then. Might only work at NasdaqQM.
            
            Symbol symbol = option.Symbol;
            Symbol underlying = symbol.Underlying;
            decimal? smoothedIVPrice;
            decimal price;

            decimal position = Position(symbol);
            var orderDirection = Num2Direction(quantity);

            double spreadFactor = SpreadFactor(option, position, quantity);
            OptionContractWrap ocw = OptionContractWrap.E(this, option, 1);

            if (!RollingIVStrikeBid.ContainsKey(underlying) || !RollingIVStrikeAsk.ContainsKey(underlying))
            {
                Error($"Missing IV indicator for {symbol}. Expected to have been filled in securityInitializer");
                return 0;
            }

            smoothedIVPrice = IVStrikePrice(ocw, orderDirection, spreadFactor);
            if (smoothedIVPrice == null || smoothedIVPrice == 0)
            {
                price = 0;
            }
            else
            {
                price = orderDirection switch
                {
                    OrderDirection.Buy => Math.Min(smoothedIVPrice ?? 0, MidPrice(symbol)),
                    OrderDirection.Sell => Math.Max(smoothedIVPrice ?? 0, MidPrice(symbol)),
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
                };
            }     
            return price;
        }

        /// <summary>
        /// If we have greater then zero order quantity on this price level, may impact oneself recursively. For example, may not want to +/- tick the price.
        /// </summary>
        public decimal QuantityLimitOrdersOnPriceLevel(Symbol symbol, decimal quotePrice)
        {
            // As per IB cannot be short & long same contract, hence all orders are in same direction
            return orderTickets.ContainsKey(symbol) ? Math.Abs(orderTickets[symbol].Where(x => x.Get(OrderField.LimitPrice) == quotePrice).Select(x => x.Quantity).Sum()) : 0m;
        }
    }
}
