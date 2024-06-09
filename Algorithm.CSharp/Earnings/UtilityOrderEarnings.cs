using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Securities.Option;
using QuantConnect.Orders;
using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    public class UtilityOrderEarnings : UtilityOrderBase
    {
        private readonly double UtilNo = -2000;
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

        public override double UtilityPV
        {
            get {
                _algo.MarginalWeightedDNLV.TryGetValue(Symbol, out double marginalUtil);
                if (marginalUtil != 0)
                {
                    return marginalUtil * Math.Sign(Quantity) + UtilityCapitalCostPerDay + UtilityTransactionCosts;
                }
                return 0;
            }
        }

        protected new HashSet<string> _utilitiesToLog = new() {
            "UtilityPV",
            "UtilityTargetHoldings",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition",
            "UtilityGamma",
            "UtilityTransactionCosts"
            };

        protected double? _utilityTargetHoldings;
        public virtual double UtilityTargetHoldings { get => _utilityTargetHoldings ??= GetUtilityTargetHoldings(); }

        /// <summary>
        /// Options of very high liquidity, lowest tenor, expiring same week should only be sold on release day, can buy day earlier.
        /// Too many if clauses. With an IV intraday forecast model, could simplify this. 
        /// </summary>
        /// <returns></returns>
        protected double GetUtilityTargetHoldings()
        {
            double utility = UtilNo;

            decimal orderQuantity = _algo.QuantityToTargetHolding(Symbol);
            if (orderQuantity == 0 || Quantity * orderQuantity < 0)
            {
                return UtilNo;
            }

            bool isAfterRelease = (_algo.PreviouReleaseDate(Underlying) + TimeSpan.FromDays(1)).Date == _algo.Time.Date;
            OptionContractWrap ocw = OptionContractWrap.E(_algo, _option, Time.Date);
            int dte = ocw.DaysToExpiration();
            double absDelta = Math.Abs(ocw.Delta(_algo.IV(_option)));

            // Before earnings release, utility is managed by the marginal util coming from estimator.
            if (_algo.PreparingEarningsRelease(Underlying))
            {
                double marginalUtility = _algo.MarginalWeightedDNLV.TryGetValue(Symbol, out marginalUtility) ? marginalUtility : 0;
                utility = marginalUtility * Math.Sign(Quantity);
            }
            // After release, sell any longs from SOD.
            else if (isAfterRelease 
                && OrderDirection == OrderDirection.Sell
                && _algo.Time.TimeOfDay > new TimeSpan(0, 9, 32, 0)
                && ((dte >= 7) || (dte < 7 && absDelta < 0.95))  // Dont sell deep ITM options, too much trouble adjusting the hedge. Just get let it exercise.
                )
            {
                utility = 200;
            }
            //After release, sell any longs only after noon when vola has dropped.
            else if (isAfterRelease 
                && OrderDirection == OrderDirection.Buy 
                && _algo.Time.TimeOfDay > new TimeSpan(0, 12, 0, 0)
                )
            {
                utility = 200;
            }

            return utility;
        }

        /// <summary>
        /// Related to MarginUtil, which only kicks in at higher equity positions. Better unify both!
        /// Objectives: - Incentivize trades that minimize margin requirements.
        ///             - Reduce equity position as it invites hedging error.
        /// 
        /// Scenario where a higher absolute total delta reduces margin requirements: 
        /// 
        /// What's the PV of this Delta increase ??? Proportional to hedging error and transaction costs. Helps reduce margin requirements.
        /// Instantaneous hedging cost.
        /// Increase/Decrease in subsequent hedging costs, a function of fwdVola, pfGamma, pfDelta
        /// </summary>
        /// <returns></returns>
        protected override double GetUtilityEquityPosition()
        {
            // Move these model parameters to a config file or with model specs.
            double util;
            double b = 0.01;
            double c = 0.005;

            decimal deltaPfTotal = _algo.LastDeltaAcrossDs.TryGetValue(Underlying, out double lastDeltaAcrossD) ? (decimal)lastDeltaAcrossD : _algo.DeltaMV(Symbol);

            double optionDelta = (double)(deltaPfTotal - _algo.Securities[Underlying].Holdings.Quantity);
            double orderDelta = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, _algo.HedgeMetric(Underlying));

            var whatIfOptionDelta = optionDelta + orderDelta;
            DateTime nextReleaseDate = _algo.NextReleaseDate(Underlying);

            // Need to become very strict on reducing abs deltaAcross within last 30min of release date.
            if (nextReleaseDate == _algo.Time.Date 
                && new TimeSpan(0, 16, 0, 0) - _algo.Time.TimeOfDay < TimeSpan.FromMinutes(15) 
                && Math.Abs(whatIfOptionDelta) > Math.Abs(optionDelta) && Math.Abs(whatIfOptionDelta) > 50
                )
            {
                util = UtilNo;
            }
            // More relaxed threshold beforehand
            else if (
                Math.Abs(whatIfOptionDelta) > Math.Abs(optionDelta) 
                && Math.Abs(whatIfOptionDelta) > 150
                )  // Refactor this back to a threshold considering volatility and underlying price. So a vola adjusted DeltaUSD.
            {
                util = UtilNo;
            }
            else
            {
                double scaleByOrder = optionDelta == 0 ? 1 : Math.Abs(orderDelta / optionDelta);

                double absCurrentPfUtil = Math.Abs(b * optionDelta + c * Math.Pow(optionDelta, 2));
                double absWhatIfUtil = Math.Abs(b * whatIfOptionDelta + c * Math.Pow(whatIfOptionDelta, 2));
                double absPfUtil = Math.Abs(absWhatIfUtil - absCurrentPfUtil);
                util = -Math.Sign(optionDelta) * Math.Sign(orderDelta) * absPfUtil * scaleByOrder;
            }
            return util;
        }
    }
}
