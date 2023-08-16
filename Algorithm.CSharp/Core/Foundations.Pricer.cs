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

        public double SpreadFactor(Option option, decimal position, OrderDirection orderDirection)
        {
            // Trade Embargo pricing.
            if (
                EarningsAnnouncements.Where(ea => ea.Symbol == option.Symbol && Time.Date >= ea.EmbargoPrior && Time.Date <= ea.EmbargoPost).Any()
                || (option.Symbol.ID.Date - Time.Date).Days <= 2  // Options too close to expiration. This is not enough. Imminent Gamma squeeze risk. Get out fast.
                )
            {
                return 0.5;
            }
            // Gamma Hedge pricing. How does the pricer know this is a gamma hedge. Same calcs again???

            if (position != 0)  // Already in Market. Turnover inventory! Get rid of this. Fairly rare, irrelevant starting conditions. Evetually alway gonna be in marketl...
            {
                /// Looking to offer a better price for contracts that reduce portfolio risk.
                /// 1) If contract is in my inventory - try get rid of it earning the spread!
                return orderDirection switch
                {
                    OrderDirection.Buy =>  1,
                    OrderDirection.Sell => 1,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }
            else
            {
                // No position yet. Patiently wait for good entry. Transact on right side of bid ask spread, stay market maker.
                // Selling Straddles till expiry strategy. Keeping it, only sell if very good exit.
                return orderDirection switch
                {
                    OrderDirection.Buy =>  1,
                    OrderDirection.Sell => 1,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }
        }

        public decimal? SmoothedIVPrice(OptionContractWrap ocw, OrderDirection orderDirection, double spreadFactor)
        {
            // Bid IV is quote often 0, hence Price would come back as null.
            // The IV Spread gets becomes considerably larger for Bid IV 0, going from suddenly ~20% to 0%. Therefore a Mid IV of ~25% can quickly be ~15%.
            // Hence in the presence of 0, may want to price without any spreadFactor.
            Symbol symbol = ocw.Contract.Symbol;
            decimal? smoothedIVPrice;

            double bidIV = RollingIVBid[symbol].GetCurrentExOutlier(); // Using Current IV over ExOutlier improved cumulative annual return by ~2 USD per trade (~200 trades).
            double askIV = RollingIVAsk[symbol].GetCurrentExOutlier(); // ExOutlier is a moving average of the last 100IVs. Doesnt explain why I quoted Selling at less than 90% of bid ask spread.
            double iVSpread = (double)askIV - (double)bidIV;

            smoothedIVPrice = orderDirection switch
            {
                OrderDirection.Buy => (decimal?)ocw.AnalyticalIVToPrice(hv: bidIV + iVSpread * (1 - spreadFactor)),
                OrderDirection.Sell => (decimal?)ocw.AnalyticalIVToPrice(hv: askIV - iVSpread * (1 - spreadFactor)),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };
            return smoothedIVPrice;
        }

        public decimal? SmoothedIVEWMAPrice(OptionContractWrap ocw, OrderDirection orderDirection, double spreadFactor)
        {
            Symbol symbol = ocw.Contract.Symbol;
            decimal? smoothedIVPrice;
            double bidIV = RollingIVBid[symbol].EWMA; // Using Current IV over ExOutlier improved cumulative annual return by ~2 USD per trade (~200 trades).
            double askIV = RollingIVAsk[symbol].EWMA; // ExOutlier is a moving average of the last 100IVs. Doesnt explain why I quoted Selling at less than 90% of bid ask spread.
            double iVSpread = (double)askIV - (double)bidIV;
            // Log(symbol.ToString() + " " + bidIV.ToString() + " " + askIV.ToString() + " " + iVSpread.ToString() + " " + spreadFactor.ToString());
            smoothedIVPrice = orderDirection switch
            {
                OrderDirection.Buy => (decimal?)ocw.AnalyticalIVToPrice(hv: bidIV + iVSpread * (1 - spreadFactor)),
                OrderDirection.Sell => (decimal?)ocw.AnalyticalIVToPrice(hv: askIV - iVSpread * (1 - spreadFactor)),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };
            return smoothedIVPrice;
        }

        public double RiskSpreadDiscount(Symbol symbol, decimal quantity, double slope = 1)
        {
            double currentRisk = (double)pfRisk.DerivativesRiskByUnderlying(symbol, Metric.Gamma100BpTotal);
            if (Math.Abs(currentRisk) < 0.2) { return 0; }

            double riskIfFilled = (double)pfRisk.RiskAddedIfFilled(symbol, quantity, Metric.Gamma100BpTotal);
            // the higher the absolute risk, the more discount towards reducing the risk.
            double discount = Math.Max(-0.7, Math.Min(0.4, Math.Abs(currentRisk) * (1  - (currentRisk + riskIfFilled) / currentRisk)));
            if (currentRisk < -0.7)
            {
                Log($"RiskSpreadDiscount. {symbol} {quantity} CurrentRisk: {currentRisk} riskIfFilled: {currentRisk + riskIfFilled} - Discount: {discount}");  // too much discount might depress the IVBuy into 0 price.
            }
            return discount;
        }

        /// <summary>
        /// Consider making spreadfactor a function of the forecasted volatility (long mean) and current volatility (short mean). all implied. Then hold until vega earned.
        /// </summary>
        public decimal PriceOptionPfRiskAdjusted(Option option, decimal quantity, bool ewmaBased = true)
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
            decimal? smoothedIVPrice;
            decimal price;

            // Need to avoid impacting myself... otherwise race to bottom pricing wise....
            //if (QuantityLimitOrdersOnPriceLevel(symbol, bidPrice) - Securities[symbol].BidSize == 0){
            //    bidPrice -= TickSize(symbol);
            //}
            //if (QuantityLimitOrdersOnPriceLevel(symbol, askPrice) - Securities[symbol].AskSize == 0)
            //{
            //    askPrice += TickSize(symbol);
            //}

            decimal position = Position(symbol);
            var orderDirection = Num2Direction(quantity);

            double spreadFactor = SpreadFactor(option, position, orderDirection) - RiskSpreadDiscount(symbol, quantity);
            OptionContractWrap ocw = OptionContractWrap.E(this, option, 1);

            if (!RollingIVBid.ContainsKey(symbol) || !RollingIVAsk.ContainsKey(symbol))
            {
                Error($"Missing IV indicator for {symbol}. Expected to have been filled in securityInitializer");
                return 0;
            }

            if (ewmaBased)
            {
                // EWMA
                smoothedIVPrice = SmoothedIVEWMAPrice(ocw, orderDirection, spreadFactor);
                price = smoothedIVPrice ?? 0;
            }
            else
            {
                decimal bidPrice = Securities[symbol].BidPrice;
                decimal askPrice = Securities[symbol].AskPrice;
                decimal priceSpread = askPrice - bidPrice;
                // BBA -  Pricing at or worse than bid ask
                smoothedIVPrice = SmoothedIVPrice(ocw, orderDirection, spreadFactor);
                if (smoothedIVPrice == null) { return 0; }
                price = orderDirection switch
                {
                    OrderDirection.Buy => Math.Min(bidPrice + (decimal)spreadFactor * priceSpread, (decimal)smoothedIVPrice),
                    OrderDirection.Sell => Math.Max(askPrice - (1 - (decimal)spreadFactor) * priceSpread, (decimal)smoothedIVPrice),
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }

            //QuickLog(new Dictionary<string, string>() { 
            //    { "topic", "PRICING" }, 
            //    { "Function", "PriceOptionPfRiskAdjusted"},
            //    { "Symbol", $"{symbol}" },
            //    { "Direction", $"{orderDirection}" },
            //    { "IntrinsicValue", $"{ ocw.IntrinsicValue() }" },
            //    { "ExtrinsicValue", $"{ocw.ExtrinsicValue() }" },
            //    //{ "FairPrice", $"{RoundTick(priceFair ?? 0, tickSize/10m)}" },
            //    //{ "PriceImplied", $"{RoundTick(priceImplied, tickSize/10m)}" },
            //    //{ "MarketMarkup", $"{marketMarkup}" },
            //    { "PricePortfolioRisk", $"{pricePfRisk}" },
            //    { "Price", $"{RoundTick(price, tickSize/10m)}" },
            //    { "DeltaNBBO", $"{RoundTick(deltaNBBO, tickSize/10m)}" },
            //    { "BidPrice", $"{option.BidPrice}" },
            //    { "AskPrice", $"{option.AskPrice}" },
            //    { "iVSpread", $"{iVSpread}" }
            //    });
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
