using QuantConnect.Algorithm.CSharp.Core.Pricing;
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

        public double SpreadFactor(Option option, decimal position, OrderDirection orderDirection, decimal quantity)
        {
            double discount = 0;
            Symbol symbol = option.Symbol;

            double currentGammaRisk = (double)pfRisk.DerivativesRiskByUnderlying(symbol, Metric.Gamma100BpTotal);
            if (Math.Abs(currentGammaRisk) < 0.2) {
                discount = 0; 
            }
            else
            {
                double riskIfFilled = (double)pfRisk.RiskAddedIfFilled(symbol, quantity, Metric.Gamma100BpTotal);
                // the higher the absolute risk, the more discount towards reducing the risk.
                discount = Math.Max(-0.7, Math.Min(0.3, Math.Abs(currentGammaRisk) * (1 - (currentGammaRisk + riskIfFilled) / currentGammaRisk)));
                if (currentGammaRisk < -0.7 && !LiveMode)
                {
                    Log($"RiskSpreadDiscount. Gamma too low. Expect discount or increase {symbol} {quantity} CurrentRisk: {currentGammaRisk} riskIfFilled: {currentGammaRisk + riskIfFilled} - Discount: {discount}");  // too much discount might depress the IVBuy into 0 price.
                }
            }

            // Trade Embargo pricing. and avoid assignments
            if (
                //EarningsAnnouncements.Where(ea => ea.Symbol == option.Symbol && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()
                (option.Symbol.ID.Date - Time.Date).Days <= 2  // Options too close to expiration. This is not enough. Imminent Gamma squeeze risk. Get out fast.
                )
            {
                discount = 0.4;
            }
            // Gamma Hedge pricing. How does the pricer know this is a gamma hedge. Same calcs again???

            //if (position != 0)  // Already in Market. Turnover inventory! Get rid of this. Fairly rare, irrelevant starting conditions. Evetually alway gonna be in marketl...
            //{
            //    /// Looking to offer a better price for contracts that reduce portfolio risk.
            //    /// 1) If contract is in my inventory - try get rid of it earning the spread!
            //    discount = orderDirection switch
            //    {
            //        OrderDirection.Buy =>  0,
            //        OrderDirection.Sell => 0,
            //        _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
            //    };
            //}
            //else
            //{
            //    // No position yet. Patiently wait for good entry. Transact on right side of bid ask spread, stay market maker.
            //    // Selling Straddles till expiry strategy. Keeping it, only sell if very good exit.
            //    discount =  orderDirection switch
            //    {
            //        OrderDirection.Buy =>  0,
            //        OrderDirection.Sell => 0,
            //        _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
            //    };
            //}
            return discount;
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

            double spreadFactor = SpreadFactor(option, position, orderDirection, quantity);
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
