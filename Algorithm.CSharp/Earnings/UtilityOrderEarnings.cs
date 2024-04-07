using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Securities.Option;
using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    public class UtilityOrderEarnings : UtilityOrderBase
    {
        public UtilityOrderEarnings(Foundations algo, Option option, decimal quantity, decimal? price = null)
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
                UtilityTargetHoldings +
                // IntradayVolatilityRisk + Needs more research and calibration
                UtilityCapitalCostPerDay +
                UtilityTransactionCosts +  // Very approximate, but small at the moment
                UtilityGamma +
                UtilityEquityPosition;
        }

        protected new HashSet<string> _utilitiesToLog = new() {
            "UtilityTargetHoldings",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition",
            "UtilityGamma",
            "UtilityTransactionCosts"
            };

        protected double? _utilityTargetHoldings;
        public virtual double UtilityTargetHoldings { get => _utilityTargetHoldings ??= GetUtilityTargetHoldings(); }

        protected double GetUtilityTargetHoldings()
        {
            double utility = 100;
            decimal targetQuantity = _algo.TargetHoldings.TryGetValue(Symbol.Value, out targetQuantity) ? targetQuantity : 0;
            decimal orderQuantity = targetQuantity - _algo.Portfolio[Symbol].Quantity;
            //double hour = Math.Max(1, _algo.Time.Hour - 9 / 2);

            if (orderQuantity == 0 || Quantity * orderQuantity < 0) return -2000;

            return utility;
        }
    }
}
