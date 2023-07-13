using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;
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
            return (decimal)OptionContractWrap.E(this, contract).AnalyticalIVToPrice();
        }

        public decimal Position(Symbol symbol)
        {
            return Portfolio.ContainsKey(symbol) ? Portfolio[symbol].Quantity : 0m;
        }

        /// <summary>
        /// Consider making spreadfactor a function of the forecasted volatility (long mean) and current volatility (short mean). all implied. Then hold until vega earned.
        /// </summary>
        public decimal PriceOptionPfRiskAdjusted(Option option, OrderDirection orderDirection)
        {
            // Move to pricing...
            // Prices options based on portfolio risk, intrinsic value, time value, IV/market bid ask, HV forecasts, dividends, liquidity of contract, interest rate.
            //
            Symbol symbol = option.Symbol;
            decimal price;
            decimal bidPrice = Securities[symbol].BidPrice;
            decimal askPrice = Securities[symbol].AskPrice;

            // Need to avoid impacting myself... otherwise race to bottom pricing wise....
            if (QuantityLimitOrdersOnPriceLevel(symbol, bidPrice) - Securities[symbol].BidSize == 0){
                bidPrice -= TickSize(symbol);
            }
            if (QuantityLimitOrdersOnPriceLevel(symbol, askPrice) - Securities[symbol].AskSize == 0)
            {
                askPrice += TickSize(symbol);
            }

            decimal priceSpread = askPrice - bidPrice;
            decimal position = Position(symbol);
            decimal? smoothedIVPrice;
            double spreadFactor;
            OptionContractWrap ocw = OptionContractWrap.E(this, option);

            if (!RollingIVBid.ContainsKey(symbol) || !RollingIVAsk.ContainsKey(symbol))
            {
                Error($"Missing IV indicator for {symbol}. Expected to have been filled in securityInitializer");
                return 0;
            }

            double bidIV = RollingIVBid[symbol].GetCurrentExOutlier();
            double askIV = RollingIVAsk[symbol].GetCurrentExOutlier();
            double iVSpread = askIV - bidIV;

            if (position != 0)  // Already in Market. Turnover inventory!
            {
                /// Looking to offer a better price for contracts that reduce portfolio risk.
                /// 1) If contract is in my inventory - try get rid of it earning the spread!
                spreadFactor = orderDirection switch
                {
                    OrderDirection.Buy => 0.2,
                    OrderDirection.Sell => 0.8,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }
            else
            {
                // No position yet. Patiently wait for good entry. Transact on right side of bid ask spread, stay market maker.
                
                spreadFactor = orderDirection switch
                {
                    OrderDirection.Buy => 0.15,
                    OrderDirection.Sell => 0.85,
                    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
                };
            }

            //double bidIVLong = RollingIVBid[symbol].LongMean;
            //double askIVLong = RollingIVAsk[symbol].LongMean;
            //double iVSpreadLong = askIV - bidIV;
            //var longMeanIVPrice = orderDirection switch
            //{
            //    OrderDirection.Buy => (decimal?)ocw.AnalyticalIVToPrice(hv: bidIVLong + iVSpreadLong * spreadFactor),
            //    OrderDirection.Sell => (decimal?)ocw.AnalyticalIVToPrice(hv: askIVLong - iVSpreadLong * (1 - spreadFactor)),
            //    _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            //};

            smoothedIVPrice = orderDirection switch
            {
                OrderDirection.Buy => (decimal?)ocw.AnalyticalIVToPrice(hv: bidIV + iVSpread * spreadFactor),
                OrderDirection.Sell => (decimal?)ocw.AnalyticalIVToPrice(hv: askIV - iVSpread * (1 - spreadFactor)),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}")
            };

            if (smoothedIVPrice == 0) { return 0; }

            //price = (decimal)smoothedIVPrice;
            price = orderDirection switch
            {
                OrderDirection.Buy => Math.Min(bidPrice + (decimal)spreadFactor * priceSpread, smoothedIVPrice ?? bidPrice),
                OrderDirection.Sell => Math.Max(askPrice - (1 - (decimal)spreadFactor) * priceSpread, smoothedIVPrice ?? askPrice),
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {orderDirection}"),
            };

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
