using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System.Collections.Generic;
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
            //      Volaatility value Forecasted
            //      Directional Bias / Drift
            //
            // Interest rate
            // Dividend rate
            // Option Liquidity
            return 0;
        }
        public decimal IntrinsicValue(OptionContractWrap ocw) {
            return (ocw.Contract.StrikePrice - ocw.Contract.Underlying.Price) * OptionRight2Int[ocw.Contract.Right];
        }
        public void TimeValue() { }
        public decimal? GetFairOptionPrice(Option contract)
        {
            return (decimal)OptionContractWrap.E(this, contract).PriceFair();
        }

        private decimal AdjustPriceForMarket(Symbol symbol, OrderDirection orderDirection, decimal price)
        {
            // Upper limit is mid price. Lower limit is fair price, meaning may not be on bid/ask... Paying more than market is up to risk adjustment.
            decimal midPrice = MidPrice(symbol);
            decimal spreadMarket2FairPrice;
            if (orderDirection == OrderDirection.Buy)
            {
                if (price > midPrice) { return midPrice ; }
                //else
                //{
                //    spreadMarket2FairPrice = (decimal)price - contract.BidPrice;
                //}                
            }
            else if (orderDirection == OrderDirection.Sell)
            {
                if (price < midPrice) { return midPrice; }
                //else
                //{
                //    spreadMarket2FairPrice = contract.AskPrice - (decimal)price;
                //}
                
            }
            return price;
            
        }

        public decimal PriceOptionPfRiskAdjusted(Option contract, PortfolioRisk pfRisk, OrderDirection orderDirection)
        {
            // Move to pricing...
            // Prices options based on portfolio risk, intrinsic value, time value, IV/market bid ask, HV forecasts, dividends, liquidity of contract, interest rate.

            decimal? fairPrice = fairOptionPrices.GetValueOrDefault(contract, GetFairOptionPrice(contract));
            if (fairPrice == null)
            {
                return 0;
            }
            decimal price = AdjustPriceForMarket(contract.Symbol, orderDirection, fairPrice ?? 0);
            
            // # Adjust theoretical price for portfolio risk
            double pfDeltaId = pfRisk.PfDeltaIfFilled(contract.Symbol, orderDirection);
            bool increasesPfDelta = pfDeltaId * pfRisk.Delta > 0;
            if (increasesPfDelta)
            {
                return price; // dont need this deal much
            }
            else
            {
                // Want this trade much. Currently going up to mid price. Todo, refine this based on how much risk we have. First develop risk model better...
                // spread_adjustment = self.risk_spread_adjustment(spread, pf_risk, pfDeltaId)
                decimal spreadAdjustment = 0;  // how much we want this trade in order to, e.g., offset risk
                price = MidPrice(contract.Symbol);
            }
            return price;
        }
    }
}
