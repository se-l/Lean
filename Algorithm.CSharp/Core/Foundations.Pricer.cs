using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations: QCAlgorithm
    {
        private Dictionary<Option, decimal?> fairOptionPrices = new();
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

        public void TimeValue() { }
        public decimal? GetFairOptionPrice(Option contract)
        {
            return (decimal)OptionContractWrap.E(this, contract).PriceFair();
        }

        /// <summary>
        /// Due to smoothing out of IV, the implied price is often different from the respected bid/ask, therefore no need to further price better.
        /// </summary>
        private decimal MarketMarkup(Symbol symbol, OrderDirection orderDirection, decimal price)
        {
            return 0m;
            //decimal ownSizeOnLevel = QuantityLimitOrdersOnPriceLevel(symbol, price);            
            //decimal markUp = ownSizeOnLevel == 0 ? DIRECTION2NUM[orderDirection] * TickSize(symbol) : 0m;
            //decimal spread = Securities[symbol].AskPrice - Securities[symbol].BidPrice;
            //if (markUp <= spread / 2m)
            //{
            //    return markUp;
            //} 
            //else
            //{
            //    return 0m;
            //}
        }

        /// <summary>
        /// Looking to offer a better price for contracts that reduce portfolio risk.
        /// 1) If contract is in my inventory - try get rid of it earning the spread!
        /// </summary>
        private decimal PricePortfolioRisk(OptionContractWrap ocw, OrderDirection orderDirection)
        { 
            Symbol symbol = ocw.Contract.Symbol;
            decimal price = 0m;
            double iVSpread = RollingIVBid[symbol].Current - RollingIVAsk[symbol].Current;
            decimal bidPrice = Securities[symbol].BidPrice;
            decimal askPrice = Securities[symbol].BidPrice;
            decimal priceSpread = askPrice - bidPrice;

            if (Position(symbol) != 0)
            {
                // sell at 75% of IV spread.
                double spreadFactor = orderDirection switch
                {
                    OrderDirection.Buy => 0.25,
                    OrderDirection.Sell => 0.75,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
                double iv = RollingIVBid[symbol].Current + iVSpread * spreadFactor;
                // These are smoothed and eventually forecasted IV values. May want to use those as starting point and improve with market.

                decimal? smoothedIVPrice = (decimal?)ocw.PriceFair(hv: iv);
                price = orderDirection switch
                {
                    OrderDirection.Buy => Math.Min(bidPrice + (decimal)spreadFactor * priceSpread, smoothedIVPrice ?? askPrice),
                    OrderDirection.Sell => Math.Max(bidPrice + (decimal)spreadFactor * priceSpread, smoothedIVPrice ?? bidPrice),
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }
            return price;
        }

        public decimal Position(Symbol symbol)
        {
            return Portfolio.ContainsKey(symbol) ? Portfolio[symbol].Quantity : 0m;
        }

        public decimal PriceOptionPfRiskAdjusted(Option option, PortfolioRisk pfRisk, OrderDirection orderDirection)
        {
            // Move to pricing...
            // Prices options based on portfolio risk, intrinsic value, time value, IV/market bid ask, HV forecasts, dividends, liquidity of contract, interest rate.
            //
            Symbol symbol = option.Symbol;
            decimal price;
            decimal bidPrice = Securities[symbol].BidPrice;
            decimal askPrice = Securities[symbol].AskPrice;
            decimal priceSpread = askPrice - bidPrice;
            decimal position = Position(symbol);
            decimal markupToMarket;
            decimal pricePfRisk = 0m;
            decimal? priceImplied;
            OptionContractWrap ocw = OptionContractWrap.E(this, option);

            double bidIV = RollingIVBid.ContainsKey(symbol) ? RollingIVBid[symbol].Current : 0;
            double askIV = RollingIVAsk.ContainsKey(symbol) ? RollingIVAsk[symbol].Current : 0;
            double iVSpread = RollingIVAsk[symbol].Current - RollingIVBid[symbol].Current;

            //decimal? priceFair = fairOptionPrices.GetValueOrDefault(option, GetFairOptionPrice(option));            

            if (position != 0)
            {
                // Already in Market. Turnover inventory!
                pricePfRisk = PricePortfolioRisk(ocw, orderDirection: orderDirection);
                price = pricePfRisk;
            }
            else
            {
                // No position yet. Patiently wait for good entry. Transact on right side of bid ask spread, stay market maker.
                var spreadFactor = orderDirection switch
                {
                    OrderDirection.Buy => 0.2,
                    OrderDirection.Sell => 0.8,
                    OrderDirection.Hold => 0,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };

                priceImplied = (decimal?)ocw.PriceFair(hv: bidIV + iVSpread * spreadFactor);
                if (priceImplied == 0) { return 0; }
                //decimal marketMarkup = MarketMarkup(symbol, orderDirection, priceImplied);

                price = orderDirection switch
                {
                    OrderDirection.Buy => Math.Min(bidPrice + (decimal)spreadFactor * priceSpread, priceImplied ?? bidPrice),
                    OrderDirection.Sell => Math.Max(bidPrice + (decimal)spreadFactor * priceSpread, priceImplied ?? askPrice),
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }
            decimal tickSize = TickSize(symbol);
            decimal deltaNBBO = orderDirection switch
            {
                OrderDirection.Buy => option.AskPrice - price,
                OrderDirection.Sell => price - option.BidPrice,
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
            };

            QuickLog(new Dictionary<string, string>() { 
                { "topic", "PRICING" }, 
                { "Function", "PriceOptionPfRiskAdjusted"},
                { "Symbol", $"{symbol}" },
                { "Direction", $"{orderDirection}" },
                { "IntrinsicValue", $"{ ocw.IntrinsicValue() }" },
                { "ExtrinsicValue", $"{ocw.ExtrinsicValue() }" },
                //{ "FairPrice", $"{RoundTick(priceFair ?? 0, tickSize/10m)}" },
                //{ "PriceImplied", $"{RoundTick(priceImplied, tickSize/10m)}" },
                //{ "MarketMarkup", $"{marketMarkup}" },
                { "PricePortfolioRisk", $"{pricePfRisk}" },
                { "Price", $"{RoundTick(price, tickSize/10m)}" },
                { "DeltaNBBO", $"{RoundTick(deltaNBBO, tickSize/10m)}" },
                { "BidPrice", $"{option.BidPrice}" },
                { "AskPrice", $"{option.AskPrice}" }
                });
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
