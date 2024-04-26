using QuantConnect.Securities.Option;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class UtilityOrderMarketMaking : UtilityOrderBase
    {
        public UtilityOrderMarketMaking(Foundations algo, Option option, decimal quantity, decimal? price = null)
        {            
            _algo = algo;
            _option = option;
            Quantity = quantity;
            _price = price;
            IVPrice = IV(_price);
            Time = _algo.Time;
            OrderDirection = Num2Direction(Quantity);
            _regimes = _algo.ActiveRegimes.TryGetValue(Underlying, out _regimes) ? _regimes : new HashSet<Regime>();

            // Calling Utility to snap the risk => cached for future use.
            _ = Utility;
            _algo.UtilityWriters[Underlying].Write(this);
        }

        public override double Utility
        {
            get =>
            // PV based
            //UtilityProfitSpread +
            UtilityManualOrderInstructions +
            UtlityVegaMispricedIVUntilExpiry +  // IV at expiry incorrectly assumed to be RV.                
            UtilityDontSellBodyBuyWings +

            //UtilityTheta +  // double counting any value that's already included in the vega utils?
            // IntradayVolatilityRisk + Needs more research and calibration
            UtilityCapitalCostPerDay +
            UtilityTransactionCosts +  // Very approximate, but small at the moment

            // ES based to be refactored
            UtilityRiskExpiry +  // Coz Theta impact somehow too small. Should make dependent on how much OTM. Just use delta. An optimization given there are many other terms to choose from.                
            UtilityGamma +
            UtilityDontLongLowDelta +
            UtilityEquityPosition +  // essentially there is a hedging inefficieny. Whatever net delta I hold after options, can multiply by such a factor.

            // Utils need to naturally flow into hedging scenarios. E.g.: short 10 call => short puts to arrive at 0 delta. Or short IV a very low gamma => calendar spread

            // Not yet refactored
            //UtilityMargin +
            UtilityExpiringNetDelta + 
            UtilityInventory;
            //UtilityVanna;  // Call vs Put & Buy vs Sell.
        }
    }
}
