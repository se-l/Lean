using System;
using QuantConnect.Orders;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// All Utilities in USD. Weighs easy-to-measure profit opportunities vs. uncertain Risk/Opportunities. Turning Risk into a USD estimate will be challenging and requires frequent review.
    /// Each public instance attribute is exported to CSV if exported. Methods wont be.
    /// Probably needs to be coupled more with Pricer to avoid unneccessary re-calculations.
    /// A utility to me translates into a price. But some opportunities/risk may me cheaper than others - that's for the pricer to optimize.
    /// 
    /// Refactor: need util as of fill price. Then can derive dU/dS and also dU/dIV. Also need util as of DTE to derive dU/dDTE.
    /// </summary>
    public interface IUtilityOrder
    {
        public decimal Quantity { get; }
        public double IVPrice { get; }
        public Symbol Symbol { get; }
        public Symbol Underlying { get => Underlying(Symbol); }
        public DateTime Time { get; }
        public OrderDirection OrderDirection { get; }

        /// <summary>
        /// Vega / IV0 -> IV1: Skew, Term Structure, Level. EWMA each bin and connecting lead to an EWMA Surface. Assumption: IV returns to that, hence below util. How soon it returns, unclear.
        /// IV != HV: profit: (RV - IV) * 0.5*dS^2 or (RV - IV) * Vega.
        /// Theta: Daily theta. Could be offset by vega during rising IVs, eg, before earnings.
        /// 
        /// </summary>
        public double Utility { get; }
        public double UtilityProfitSpread { get; }
        public double UtilityManualOrderInstructions { get; }
        //public double UtilityVegaIV2Ewma { get => _utilityVegaIV2Ewma ??= GetUtilityVegaIV2Ewma(); }
        public double UtlityVegaMispricedIVUntilExpiry { get; }
        public double IntradayVolatilityRisk { get; }
        public double UtilityInventory { get; }
        public double UtilityExpiringNetDelta { get; }
        public double UtilityRiskExpiry { get; }
        public double UtilityTheta { get; }
        public double UtilityCapitalCostPerDay { get; }
        public double UtilityEquityPosition { get; }
        public double UtilityTransactionCosts { get; }
        public double UtilityDontSellBodyBuyWings { get; }
        public double UtilityGamma { get; }
        public double UtilityVannaRisk { get; }
        public double UtilityDontLongLowDelta { get; }
        public double UtilityMargin { get; }
        public abstract string ToString();
    }
}
