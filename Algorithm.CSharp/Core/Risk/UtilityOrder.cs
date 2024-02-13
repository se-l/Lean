using System;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using System.Collections.Generic;
using Accord.Math;
using QuantConnect.Securities;
using Fasterflect;
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
    public class UtilityOrder
    {
        // Constructor
        private readonly Foundations _algo;
        private readonly Option _option;
        private readonly decimal? _price;
        public decimal Quantity { get; internal set; }
        public double IVPrice { get; internal set; }
        public Symbol Symbol { get => _option.Symbol; }
        private Security _securityUnderlying;
        private Security SecurityUnderlying => _securityUnderlying ??= _algo.Securities[Underlying];
        public Symbol Underlying { get => Underlying(Symbol); }
        public DateTime Time { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        private decimal Multiplier { get => _option.ContractMultiplier; }
        //public double Utility { get => UtilityProfit + UtilityRisk; }
        private readonly HashSet<Regime> _regimes;
        public UtilityOrder(Foundations algo, Option option, decimal quantity, decimal? price = null)
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

        /// <summary>
        /// Vega / IV0 -> IV1: Skew, Term Structure, Level. EWMA each bin and connecting lead to an EWMA Surface. Assumption: IV returns to that, hence below util. How soon it returns, unclear.
        /// IV != HV: profit: (RV - IV) * 0.5*dS^2 or (RV - IV) * Vega.
        /// Theta: Daily theta. Could be offset by vega during rising IVs, eg, before earnings.
        /// 
        /// </summary>
        public double Utility { get =>
                // PV based
                //UtilityProfitSpread +
                UtilityManualOrderInstructions +
                UtlityVegaMispricedIVUntilExpiry +  // IV at expiry incorrectly assumed to be RV.
                UtilityVegaIV2AH +
                UtilityDontSellBodyBuyWings +

                UtilityTheta +  // double counting any value that's already included in the vega utils?
                // IntradayVolatilityRisk + Needs more research and calibration
                UtilityCapitalCostPerDay +
                UtilityTransactionCosts +  // Very approximate, but small at the moment

                // To be PV refactored
                UtilityEventUpcoming +

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
        //public double UtilityProfit { get => 0; }
        //public double UtilityRisk { get => 0; }
        private readonly HashSet<string> _utilitiesToLog = new() {
            "UtilityManualOrderInstructions", "UtlityVegaMispricedIVUntilExpiry", "UtilityVegaIV2AH", "UtilityTheta", "UtilityInventory", "UtilityRiskExpiry",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition", 
            "UtilityGamma", "UtilityDontSellBodyBuyWings",
            "UtilityDontLongLowDelta", "UtilityMargin", "UtilityEventUpcoming",
            "UtilityVannaRisk", "UtilityTransactionCosts" // Currently not in Utility
            };

        private double? _utilityProfitSpread;
        public double UtilityProfitSpread { get => _utilityProfitSpread ??= GetUtilityProfitSpread(); }
        private double? _utilityManualOrderInstructions;
        public double UtilityManualOrderInstructions { get => _utilityManualOrderInstructions ??= GetUtilityManualOrderInstructions(); }
        //private double? _utilityVegaIV2Ewma;
        //public double UtilityVegaIV2Ewma { get => _utilityVegaIV2Ewma ??= GetUtilityVegaIV2Ewma(); }
        private double? _utilityVegaIV2AH;
        public double UtilityVegaIV2AH { get => _utilityVegaIV2AH ??= GetUtilityVegaIV2AndreasenHuge(); }
        private double? _utlityVegaMispricedIVUntilExpiry;
        public double UtlityVegaMispricedIVUntilExpiry { get => _utlityVegaMispricedIVUntilExpiry ??= GetUtlityVegaMispricedIVUntilExpiry(); }

        private double? _intradayVolatilityRisk;
        public double IntradayVolatilityRisk { get => _intradayVolatilityRisk ??= GetIntradayVolatilityRisk(); }

        private double? _utilityInventory;
        public double UtilityInventory { get => _utilityInventory ??= GetUtilityInventory(); }
        private double? _utilityExpiringNetDelta;
        public double UtilityExpiringNetDelta { get => _utilityExpiringNetDelta ??= GetUtilityExpiringNetDelta(); }
        private double? _utilityRiskExpiry;
        public double UtilityRiskExpiry { get => _utilityRiskExpiry ??= GetUtilityRiskExpiry(); }
        private double? _utitlityTheta;
        public double UtilityTheta { get => _utitlityTheta ??= GetUtilityTheta(ThetaDte()); }

        private double? _utitlityCapitalCostPerDay;
        public double UtilityCapitalCostPerDay { get => _utitlityCapitalCostPerDay ??= GetUtilityCapitalCostPerDay(); }
        private double? _utilityEquityPosition;
        public double UtilityEquityPosition { get => _utilityEquityPosition ??= GetUtilityEquityPosition(); }
        private double? _utitlityTransactionCosts;
        public double UtilityTransactionCosts { get => _utitlityTransactionCosts ??= GetUtilityTransactionCosts(); }
        private double? _utilityDontSellBodyBuyWings;
        public double UtilityDontSellBodyBuyWings { get => _utilityDontSellBodyBuyWings ??= GetUtilityDontSellBodyBuyWings(); }
        private double? _utitlityGamma;
        public double UtilityGamma { get => _utitlityGamma ??= GetUtilityGamma(); }
        
        private double? _utitlityEventUpcoming;
        public double UtilityEventUpcoming { get => _utitlityEventUpcoming ??= GetUtilityEventUpcoming(); }

        private double? _utitlityVannaRisk;
        public double UtilityVannaRisk { get => _utitlityVannaRisk ??= GetUtilityVannaRisk(); }
        private double? _utitlityDontLongLowDelta;
        public double UtilityDontLongLowDelta { get => _utitlityDontLongLowDelta ??= GetUtilityDontLongLowDelta(); }

        private double? _utitlityMargin;
        public double UtilityMargin { get => _utitlityMargin ??= GetUtilityMargin(); }

        /// <summary>
        /// Don't really want to increase inventory. Hard to Quantity. Attach price tag of 50...
        /// </summary>
        private double GetUtilityInventory()
        {
            //Portfolio[symbol].Quantity * quantity
            double quantityPosition = (double)_algo.Portfolio[Symbol].Quantity;
            return quantityPosition * (double)Quantity > 0 ? -50 * Math.Abs(quantityPosition) : 0;
        }

        private double GetUtilityExpiringNetDelta()
        {
            double util;
            double b = 0.05;
            double c = 0.001;
            Tuple<double, double> thresholds = new(-150, 150);

            var expiries = _algo.Positions.Values.Where(p => p.UnderlyingSymbol == Underlying && p.Quantity != 0 && p.SecurityType == SecurityType.Option).Select(p => p.Symbol.ID.Date).Where(dt => (dt - _algo.Time.Date).Days < 7);
            if (!expiries.Any())
            {
                return 0;
            }
            DateTime nextExpiry = expiries.Min();
            if (Symbol.ID.Date > nextExpiry)
            {
                return 0;
            }
            double dte = (nextExpiry - _algo.Time).Days;
            double dte_damping = Math.Max(0, 1 - dte * (3/7));

            var filter = new Func<IEnumerable<Position>, IEnumerable<Position>>(positions => positions.Where(p => p.UnderlyingSymbol == Underlying && p.Quantity != 0 && p.SecurityType == SecurityType.Option && p.Symbol.ID.Date == nextExpiry));
            double orderDelta = OCW.Delta(IV(_price)) * (double)Quantity * 100;
            double deltaPfTotal0 = (double)(_algo.PfRisk.RiskByUnderlying(Symbol, _algo.HedgeMetric(Underlying), filter: filter));
            double deltaPfTotal1 = orderDelta + deltaPfTotal0;

            if (
                (deltaPfTotal0 > thresholds.Item1 && deltaPfTotal0 < thresholds.Item2) &&
                (deltaPfTotal1 > thresholds.Item1 && deltaPfTotal1 < thresholds.Item2)
            )
            {
                util = 0;
            }
                
            else
            {
                double pfUtil0 = b * deltaPfTotal0 + c * Math.Pow(deltaPfTotal0, 2);
                double pfUtil1 = b * deltaPfTotal1 + c * Math.Pow(deltaPfTotal1, 2);
                double deltaPfUtil = pfUtil1 - pfUtil0;
                util = -deltaPfUtil;
            }
            return util * dte_damping;
        }

        /// <summary>
        /// Selling. Pos Util. Buying. Neg Util. 
        /// </summary>
        private double GetUtilityTheta(int dDTE = -1)
        {
            return OCW.Theta(IVPrice) * (double)(Quantity * Multiplier) * dDTE;
        }

        /// <summary>
        /// Long stock: opportunity cost / interest. Shorting stock: pay shorting fee, get capital to work with... Not applicable to options
        /// </summary>
        private decimal CapitalCostPerDay(decimal quantity)
        {
            // Should I reward shorting here?
            return quantity > 0 ? 
                    -quantity * SecurityUnderlying.Price * _algo.Cfg.DiscountRateMarket / 365 : 
                     quantity * SecurityUnderlying.Price * _algo.Cfg.EquityShortingRate / 365;
        }

        /// <summary>
        /// Reducing OptionsOnlyDelta is good. Util up! Delta exposure costs hedings transaction cost and bound capital. Needs Expontential profile.
        /// Shorting gives me capital, much better than having to buy the underlying stock for heding purposes.
        /// 
        /// Capital cost: 1/365 * 4% * C + -1 * delta * 100 * S * (4% buying stocks and 9-4% borrowing stocks). Check I get the 4% interest when borrowing cash.
        /// 
        /// </summary>
        private double GetUtilityCapitalCostPerDay()
        {
            decimal deltaOrder = _algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);

            return (double)CapitalCostPerDay(deltaOrder);
        }
        /// <summary>
        /// Related to MarginUtil, which only kicks in at higher equity positions. Better unify both!
        /// Objectives: - Incentivize trades that minimize margin requirements.
        ///             - Reduce equity position as it invites hedging error.
        /// 
        /// Scenario where a higher absolute total delta reduces margin requirements: 
        /// 
        /// // What's the PV of this Delta increase ??? Proportional to hedging error and transaction costs. Helps reduce margin requirements.
        // Instantaneous hedging cost.
        // Increase/Decrease in subsequent hedging costs, a function of fwdVola, pfGamma, pfDelta
        /// </summary>
        /// <returns></returns>
        private double GetUtilityEquityPosition()
        {
            double util;
            double b = 0.1;
            double c = 0.005;

            decimal targetMaxEquityPositionUSD = _algo.Cfg.TargetMaxEquityPositionUSD.TryGetValue(Underlying.Value, out targetMaxEquityPositionUSD) ? targetMaxEquityPositionUSD : _algo.Cfg.TargetMaxEquityPositionUSD[CfgDefault];
            double targetAbsMaxEquityQuantity = (double)Math.Abs((targetMaxEquityPositionUSD / SecurityUnderlying.Price));

            decimal deltaPfTotal = _algo.DeltaMV(Symbol);
            double optionDelta = (double)(-_algo.Securities[Underlying].Holdings.Quantity + deltaPfTotal);
            double orderDelta = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, _algo.HedgeMetric(Underlying));

            var whatIfOptionDelta = optionDelta + orderDelta;

            if (Math.Abs(optionDelta) < targetAbsMaxEquityQuantity && Math.Abs(whatIfOptionDelta) < targetAbsMaxEquityQuantity)
            {
                util = 0;
            }
            else
            {
                double scaleByOrder = optionDelta == 0 ? 1 : Math.Abs(orderDelta / optionDelta);

                double absCurrentPfUtil = Math.Abs(b * optionDelta + c * Math.Pow(optionDelta, 2));
                double absWhatIfUtil = Math.Abs(b * whatIfOptionDelta + c * Math.Pow(whatIfOptionDelta, 2));
                double absPfUtil = Math.Abs(absWhatIfUtil - absCurrentPfUtil);
                util = -Math.Sign(optionDelta) * Math.Sign(orderDelta) * absPfUtil * scaleByOrder;
            }

            // CalendarSpread. Not hedging risk with front, but back months. To be refactored getting target expiries, rather than hard coding day range.
            if ((_regimes.Contains(Regime.SellEventCalendarHedge) && _option.Expiry < EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && util > 0) ||
                (_regimes.Contains(Regime.SellEventCalendarHedge) && _option.Expiry >= EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && Quantity < 0 && util > 0)
                )
            {
                util = 0;
            }
            return util;
        }
        /// <summary>
        /// Essentially an overall stress risk provile util. Stressing dS by +/- 15%. Becoming hugely influencial once algo excceeds a used margin threshold.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityMargin()
        {
            // Bad bug here. Presumable caching related. Does not return consistently the same results across multiple backtests.
            return 0;
            decimal initialMargin;
            decimal targetMarginAsFractionOfNLV = _algo.Cfg.TargetMarginAsFractionOfNLV.TryGetValue(Underlying.Value, out targetMarginAsFractionOfNLV) ? targetMarginAsFractionOfNLV : _algo.Cfg.TargetMarginAsFractionOfNLV[CfgDefault];
            double marginUtilScaleFactor = _algo.Cfg.MarginUtilScaleFactor.TryGetValue(Underlying.Value, out marginUtilScaleFactor) ? marginUtilScaleFactor : _algo.Cfg.MarginUtilScaleFactor[CfgDefault];

            initialMargin = _algo.InitialMargin();
            if (initialMargin == 0)
            {
                return 0;  // No Position whatsover. Algo start.
            }

            //decimal excessLiquidity = _algo.Portfolio.MarginMetrics.ExcessLiquidity;
            decimal marginExcessTarget = Math.Abs(Math.Min(0, _algo.Portfolio.TotalPortfolioValue * targetMarginAsFractionOfNLV - initialMargin));

            // Order Level - IB also offers WhatIf calcs for margin added, but would not want to rely on slow forth n back...
            // Ignores the fact that simply a lot positions cause higher margin, need to stop increasing them eventually.
            double stressedPnl = (double)_algo.RiskProfiles[Underlying].WhatIfMarginAdd(Symbol, Quantity);
            // positive is good, positive pnl, good utility

            double utilMargin = stressedPnl * marginUtilScaleFactor +
                                Math.Sign(stressedPnl) * Math.Min(Math.Pow((double)marginExcessTarget, 2), 1_000_000);  // Quadratic reward/punishment, once margin target has been exceeded

            if (marginExcessTarget > 0)
            {
                string noNewPositionTag = "";
                if (_algo.Portfolio[Symbol].Quantity == 0)
                {
                    noNewPositionTag = $"No new positions when margin target exceeeded. ";
                    utilMargin += -10000;
                }
                _algo.Log($"GetUtilityMargin: {Symbol} {Quantity}. {noNewPositionTag}initialMargin={initialMargin} Exceeded by marginExcessTarget={marginExcessTarget}. utilMargin={utilMargin} based on stressedPnl={stressedPnl}");
            }
            return utilMargin;
        }

        /// <summary>
        /// The instantaneous cost + any following hedging costs.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityTransactionCosts()
        {
            static decimal transactionCost(decimal x) => 1 * Math.Abs(x);  // refactor to transaction fee estimator.
            static double transactionHedgeCost(double x) => 0.05 * Math.Abs(x);  // refactor to transaction fee estimator.

            decimal deltaOrder = _algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);

            return -(double)transactionCost(Quantity) - transactionHedgeCost((double)deltaOrder);
        }

        private DateTime EventDate => _algo.EventDate(Underlying);
        private DateTime? _expiryEventImpacted;
        private DateTime ExpiryEventImpacted => _expiryEventImpacted ??= _algo.ExpiryEventImpacted(Symbol);

       
        /// <summary>
        /// Ramp up the factor from 1 to 10 with ten being 1 day before Event Date until EventEnd.
        /// </summary>
        /// <returns></returns>
        public double GetUtilityEventUpcomingUrgency()
        {
            int daysTillEvent = Math.Max(1, (EventDate - _algo.Time.Date).Days);
            int upcomingEventCalendarSpreadStartDaysPrior = _algo.Cfg.UpcomingEventCalendarSpreadStartDaysPrior.TryGetValue(Underlying, out upcomingEventCalendarSpreadStartDaysPrior) ? upcomingEventCalendarSpreadStartDaysPrior : 1;
            return Math.Min(10, Math.Max(1, 10 * (upcomingEventCalendarSpreadStartDaysPrior / daysTillEvent)));
        }

        /// <summary>
        /// Replicate short front month position in cheap IV back month
        /// have the same number of contracts in the back month as in the front month. Grouped by put vs calls.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityEventUpcoming()
        {
            //return 0;  // CalendarSpreadQuantityToBuyBackMonth is not functional and needs more analysis first.
            double _util = 0;

            if (_regimes.Contains(Regime.SellEventCalendarHedge))
            {
                if (_option.Expiry < EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) || OrderDirection == OrderDirection.Sell) 
                {   
                    if (OrderDirection == OrderDirection.Sell)
                    {
                        decimal absDeltaFrontMonthCallsTotal = Math.Abs(_algo.PfRisk.RiskByUnderlying(Symbol.Underlying, _algo.HedgeMetric(Symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => _algo.IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Call)));
                        decimal absDeltaFrontMonthPutsTotal = Math.Abs(_algo.PfRisk.RiskByUnderlying(Symbol.Underlying, _algo.HedgeMetric(Symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => _algo.IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Put)));

                        // For the calendar spread, would want an approximately equal delta between short puts and short calls. Therefore, selling wichever right has a significantly lower abs delta.
                        bool needToSellMore = (_option.Right) switch
                        {
                            OptionRight.Call => absDeltaFrontMonthCallsTotal < 0.9m * absDeltaFrontMonthPutsTotal,
                            OptionRight.Put => absDeltaFrontMonthPutsTotal < 0.9m * absDeltaFrontMonthCallsTotal,
                        };
                        double delta = OCW.Delta(IVPrice);

                        if (needToSellMore && Math.Abs(delta) < 0.3)
                        {
                            _algo.Log($"{_algo.Time} GetUtilityEventUpcoming: Need to Sell more {_option.Right}. absDeltaFrontMonthCallsTotal={absDeltaFrontMonthCallsTotal}, absDeltaFrontMonthPutsTotal={absDeltaFrontMonthPutsTotal}");
                            return 500;
                        }
                    }                    
                
                    return -500;
                };

                decimal quantityToBuy = _algo.CalendarSpreadQuantityToBuyBackMonth(Symbol, OrderDirection.Buy);

                // Buy back month hedging above sell
                double urgencyFactor = GetUtilityEventUpcomingUrgency();
                if (quantityToBuy > 0)
                {
                    _util = 100;
                }
                return _util * urgencyFactor;
            }
            return 0;
        }

        /// <summary>
        /// Instead of specificly coding up an event util... this could automatically follow from vega util and risk reduction utils. But they usually dont have StressedPnl calcs. 
        /// Therefore, rather need a need risk aversion parameter. Aversion increases towards events. If simple as such, wouldn't sell the contract and enter calendar spread. Hence this here codes up a 2-legged strategy.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityEventUpcomingRiskBased()
        {
            decimal stressedPnlPf;
            decimal meanStressPnlPf = 0;
            decimal stressedPnlPos;
            decimal netStressReduction = 0;
            double _util = 0;

            int dDays = (EventDate - _algo.Time.Date).Days <= 0 ? 1 : (EventDate - _algo.Time.Date).Days;

            double urgencyFactorA = 0;
            double urgencyFactorB = 0.1;
            double urgencyFactorMin = 1;
            double urgencyFactorMax = 10;
            double urgencyFactor = Math.Min(urgencyFactorMax, Math.Max(urgencyFactorA + urgencyFactorB * dDays, urgencyFactorMin));

            HashSet<Regime> regimes = _algo.ActiveRegimes.TryGetValue(Underlying, out regimes) ? regimes : new HashSet<Regime>();
            // Very high utility for becoming gamma neutral/positive. But horizontally. Front month, gamma/IV short. Back month, gamma/IV long.
            // Both, downward and upward event needs hedging.
            List<decimal> riskProfilePctChanges = new() { -20, -10, -5, 5, 10, 20 };
            HashSet<Metric> metricsDs = new() { Metric.Delta, Metric.Gamma, Metric.Speed };
            var trade = new Trade(_algo, Symbol, Quantity, _algo.MidPrice(Symbol));
            Position position = new(_algo, trade);
            List<Position>? pfPositions = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == Symbol.Underlying && x.Quantity != 0 && x.SecurityType == SecurityType.Option).ToList();

            if (regimes.Contains(Regime.SellEventCalendarHedge))
            {
                // 2 rewards: front month: selling IV, back month: hedging to zero or positive dS risk.
                foreach (decimal pctChange in riskProfilePctChanges)
                {
                    stressedPnlPf = _algo.RiskProfiles[Underlying].StressedPnlPositions(pfPositions, dSPct: (double)pctChange, metricsDs: metricsDs);
                    meanStressPnlPf += stressedPnlPf;
                    stressedPnlPos = _algo.RiskProfiles[Underlying].StressedPnlPositions(position, dSPct: (double)pctChange, metricsDs: metricsDs);
                    if (stressedPnlPf < 0 && stressedPnlPos > 0)
                    {
                        netStressReduction += stressedPnlPos;
                    }                    
                }
                meanStressPnlPf /= riskProfilePctChanges.Count;

                if (_option.Expiry < ExpiryEventImpacted) return 0;

                // Sell the high IV. refactor to actually selling extraordinarily high IV contracts, dont select by date...
                if (_option.Expiry < ExpiryEventImpacted.AddDays(60) && OrderDirection == OrderDirection.Sell && meanStressPnlPf > 0)  // At this point, only sell IV if risk is reduced.
                {
                    _util = UtilityVegaIV2AH;
                }

                // This should be fairly negative in losing vega util...
                if (_option.Expiry < ExpiryEventImpacted.AddDays(60) && OrderDirection == OrderDirection.Buy) return 0;

                // Buy back month hedging above sell
                if (_option.Expiry >= ExpiryEventImpacted.AddDays(60))
                {
                    _util = (double)netStressReduction;
                    foreach (decimal pctChange in riskProfilePctChanges)
                    {
                        stressedPnlPf = _algo.RiskProfiles[Underlying].StressedPnlPositions(pfPositions, dSPct: (double)pctChange, metricsDs: metricsDs);
                        meanStressPnlPf += stressedPnlPf;
                        stressedPnlPos = _algo.RiskProfiles[Underlying].StressedPnlPositions(position, dSPct: (double)pctChange, metricsDs: metricsDs);
                        if (stressedPnlPf < 0 && stressedPnlPos > 0)
                        {
                            netStressReduction += stressedPnlPos;
                        }
                    }
                }

                return _util * urgencyFactor;
            }

            if (regimes.Contains(Regime.BuyEvent))
            {
                if (_option.Expiry < ExpiryEventImpacted) return 0;
                if (_option.Expiry < ExpiryEventImpacted.AddDays(14) && OrderDirection == OrderDirection.Sell) return -500;
                if (_option.Expiry < ExpiryEventImpacted.AddDays(14) && OrderDirection == OrderDirection.Buy) return 500;
            }
            return 0;
        }


        /// <summary>
        /// 0.5 gamma * dS**2 == theta * dT for delta hedged option.
        /// Gain: Given realized vola, estimate distribution of dS before trailing_stop(vola) kicks in. Calibrate numerically and BT.
        /// Risk: Reducing Exposure increases Util. Pos Gamma exposure yields gamma scalping profits. Neg loses it. Offset by theta. 
        /// </summary>
        /// <returns></returns>
        private double GetUtilityGamma()
        {
            double riskReductionIncentive;
            double b = 0.05;
            double c = 0.003;
            Tuple<double, double> gammaTargetZeroRange = new (-75, 75);

            HashSet<Regime> regimes = _algo.ActiveRegimes.TryGetValue(Underlying, out regimes) ? regimes : new HashSet<Regime>();
            bool wantPosGamma = regimes.Contains(Regime.SellEventCalendarHedge);

            // neg gamma: risky; pos gamma: scalping gains (not a risk, hence not fitting here)
            // Changed, treating also as risk, because hedging error too large at the moment. gamma scalping didnt quite turn into profts.
            var pfGammaTotal0 = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.GammaTotal);
            // The more negative the total gamma, the may want to reduce it. 

            var gammaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.GammaTotal, IVPrice);

            double expDailyDS = (double)_algo.MidPrice(Underlying) * (double)ExpectedMeanRealizedVolatility(Underlying) / Math.Sqrt(252); // That's better todays' vola! short term ATM vola.

            if (wantPosGamma)
            {
                return 0.5 * Math.Max(0, gammaOrder) * Math.Pow(expDailyDS, 2);
            }
            else
            {
                double pfGammaTotal1 = pfGammaTotal0 + gammaOrder;
                var utilOrder = 0.5 * gammaOrder * Math.Pow(expDailyDS, 2);
                double gammaRiskThresholdLow = gammaTargetZeroRange.Item1 - gammaTargetZeroRange.Item2;
                double gammaRiskThresholdHigh = gammaTargetZeroRange.Item1 + gammaTargetZeroRange.Item2;
                if (
                    (pfGammaTotal0 > gammaRiskThresholdLow && (pfGammaTotal1) > gammaRiskThresholdLow) &&
                    (pfGammaTotal0 < gammaRiskThresholdHigh && (pfGammaTotal1) < gammaRiskThresholdHigh)
                    )
                {
                    riskReductionIncentive = 0;
                }
                else
                {
                    double riskUtil0 = b * pfGammaTotal0 + c * Math.Pow(pfGammaTotal0, 2);
                    double riskUtil1 = b * pfGammaTotal1 + c * Math.Pow(pfGammaTotal1, 2);
                    riskReductionIncentive = -(riskUtil1 - riskUtil0);
                }                
                return utilOrder + riskReductionIncentive;                           
            }
        }

        private double GetUtilityDontSellBodyBuyWings()
        {
            if (UtilityGamma > 200 || UtilityMargin > 200) return 0;  // Means wanna hedge gamma and wings might be what's needed. Or want to hedge scenario risk or large moves.

            double delta = Math.Abs(OCW.Delta(_algo.MidIV(Symbol)));  // MidIV - avoids entering/non entering clause depending on IVPrice
            if (
                (delta < 0.3 || delta > 0.7) && Quantity > 0  // Buying Wings
            )
            {
                return -50;
            }
            else if (
                (delta < 0.7 || delta > 0.3) && Quantity < 0  // Selling Bodys
            )
            {
                return -50;
            }
            return 0;
        }

        /// <summary>
        /// Vanna relies on dIV. To get the daily utility, would need IV forecasts. This is here is just the risk controller, keeping total Vanna low.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityVannaRisk()
        {
            var totalVanna = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.Vanna100BpUSDTotal);
            var vannaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.Vanna100BpUSDTotal);
            bool isAbsVannaIncreasing = vannaOrder * totalVanna > 0;
            return isAbsVannaIncreasing ? -Math.Abs(vannaOrder) : Math.Abs(vannaOrder);
        }

        /// <summary>
        /// VegaSkew
        /// </summary>
        private double GetUtilityVanna()
        {
            return 0;
        }
        /// <summary>
        /// 0.05 USD kinda options. Dont long'em
        /// </summary>
        /// <returns></returns>
        private double GetUtilityDontLongLowDelta()
        {
            if ( UtilityMargin > 200) return 0;  // May want to hedge scenario risk or large moves.

            double delta = OCW.Delta((double)IVPrice);
            return delta < 0.1 && Quantity > 0 ? -100 * (double)Quantity : 0;
        }

        /// <summary>
        /// Dont buy stuff about to expire. But that should be quantified. A risk is underlying moving after market close.
        /// To be fined. THere's a util on theta
        /// </summary>
        private double GetUtilityRiskExpiry()
        {
            return OrderDirection == OrderDirection.Buy && (_option.Symbol.ID.Date - _algo.Time.Date).Days <= 5 ? -(double)(Quantity * Multiplier) : 0;
        }

        /// <summary>
        /// Sell AM, Buy PM.
        /// </summary>
        private double GetIntradayVolatilityRisk()
        {
            if (_intradayVolatilityRisk != null) return (double)_intradayVolatilityRisk;

            if (_algo.IntradayIVDirectionIndicators[_option.Underlying.Symbol].Direction().Length > 1) return 0;

            double iv = IVPrice;
            double intraDayIVSlope = _algo.IntradayIVDirectionIndicators[_option.Underlying.Symbol].IntraDayIVSlope;
            double fractionOfDayRemaining = 1 - _algo.IntradayIVDirectionIndicators[_option.Underlying.Symbol].FractionOfDay(_algo.Time);
            return OCW.Vega(iv) * intraDayIVSlope * fractionOfDayRemaining * (double)(Quantity * _option.ContractMultiplier);
        }

        /// <summary>
        /// Spread Discount will be applied reducing this.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityProfitSpread()
        {
            decimal spread = (_option.AskPrice - _option.BidPrice) / 2;
            return Math.Abs((double)(Quantity * Multiplier * spread));
        }
        private double GetUtilityManualOrderInstructions()
        {
            if (!_algo.ManualOrderInstructionBySymbol.ContainsKey(Symbol.Value) || !_algo.Cfg.ExecuteManualOrderInstructions) return 0;

            ManualOrderInstruction manualOrderInstruction = _algo.ManualOrderInstructionBySymbol[Symbol.Value];
            decimal orderQuantity = manualOrderInstruction.TargetQuantity - _algo.Portfolio[Symbol].Quantity;
            if (orderQuantity > 0 && OrderDirection == OrderDirection.Buy) return 1000;
            else if (orderQuantity < 0 && OrderDirection == OrderDirection.Sell) return -1000;
            else if (orderQuantity == 0 || _algo.Portfolio[Symbol].Quantity == 0) return -1000;
            else return 0;
        }

        private OptionContractWrap? _ocw = null;
        private OptionContractWrap OCW
        {
            get {
                if (_ocw == null)
                {
                    _ocw = OptionContractWrap.E(_algo, _option, _algo.Time.Date);
                    _ocw.SetIndependents(_algo.MidPrice(Underlying), _price ?? _algo.MidPrice(Symbol));
                }
                return _ocw;
            }            
        }

        /// <summary>
        /// This is incomplete
        /// Research founds there is a tendency for IVs to revert to the IV interpolated by AndreasenHuge. May want to refine this quoting the expected IV adjustment, a sort of mean, couple pct.
        /// Research also found this to be working across a large sample size on average only, hence quote the EXPECTED IV reduction.
        /// /// </summary>
        private double GetUtilityVegaIV2AndreasenHuge()
        {
            double vol0 = IVPrice;
            if (vol0 == 0) 
            { 
                return 0; 
            }
            double? ahIV = _algo.IVSurfaceAndreasenHuge[(Underlying, _option.Symbol.ID.OptionRight)].IV(Symbol);
            if (ahIV == null) 
            {
                return 0; 
            }
            double fv = ((double)ahIV - vol0) * OCW.Vega(vol0) * (double)(Quantity * _option.ContractMultiplier);
            return (double)_algo.DiscountedValue((decimal)fv, 1.0 / 365.0);  // Expecting intraday reversion to expected IV levels
        }


        /// <summary>
        /// Ignoring moving S, hence ignoring skew.surface ATM IV is slightly decreasing over lifetime. Skew closer to maturity will get steeper.
        /// Earning money from vega only when IV is significantly above forecasted IV. Not trying to capture theta here...
        /// 
        /// Easier thinking in Delta/IV Currado Su surface. Less skew, more linear...
        /// So would a slow moving IV measured in DTE to have an anchor, essentially looking to forecast IV, not HV.
        /// 
        /// Combines Skew, surface and term structure.
        /// /// </summary>
        private double GetUtilityVegaIV2Ewma()
        {
            double midIV = _algo.MidIV(Symbol);            
            if (midIV == 0) { return 0; }
            double midIVEwma = _algo.MidIVEWMA(Symbol);
            // Favors selling skewed wings.
            double fv = (midIVEwma - midIV) * OCW.Vega(midIV) * (double)(Quantity * _option.ContractMultiplier);
            return (double)_algo.DiscountedValue((decimal)fv, 1.0/365.0);  // Expecting intraday reversion to expected IV levels
        }


        /// <summary>
        /// Difference between (RV - IV) * Vega
        /// Vega - try get from AH surface, rather than somewhat unstable IV surface.
        /// For earnings announcement, assume an increase of 2% in IV over current level up to 2 days before announcement. Then drop back to expected realized and start selling high IV.
        /// </summary>
        /// <returns></returns>
        private double GetUtlityVegaMispricedIVUntilExpiry()
        {
            Symbol underlying = Symbol.Underlying;
            double vol0 = IVPrice;
            double vol1;

            _algo.EarningsBySymbol.TryGetValue(underlying.Value, out EarningsAnnouncement[] earningsAnnouncements);
            if (earningsAnnouncements.Length > 0 && earningsAnnouncements.Where(x => (x.Date - _algo.Time.Date).Days < 45 && (x.Date - _algo.Time.Date).Days > 2).Any())
            {
                // FIXME To be calibrated, modeled. Need Fwd IV / IV Term structure.
                vol1 = vol0 + 0.02;
            }
            else
            {
                vol1 = (double)ExpectedMeanRealizedVolatility(underlying);
            }            
            
            double fv = (vol1 - vol0) * OCW.Vega(vol0) * (double)(Quantity * _option.ContractMultiplier);
            return (double)_algo.DiscountedValue((decimal)fv, _option);
        }

        /// <summary>
        /// Simplified model. Consider changing to ATM, based on whether hedging error is reduced by that.
        /// </summary>
        private decimal ExpectedMeanRealizedVolatility(Symbol underlying, DateTime? until=null)
        {
            return (decimal)_algo.AtmIV(underlying);  // Hedging Error was larger with ATM, hence assuming not matching realized vola better...
            //return _algo.Securities[underlying].VolatilityModel.Volatility;  // Led to drastic losses in Nov 22nd 2023. To be investigated. Need proper expected realized vola.
        }

        /// <summary>
        /// Before announcment, 1) buy IV. 2) The AM Sell IV and PM buy IV must outweigh this utility at least during the rise up.
        /// On Announcement day, sell IV or other to be developed strategies (calendar spread).
        /// </summary>
        private double GetUtilityEarningsAnnouncment()
        {
            double utility = 0;

            bool any = _algo.EarningsBySymbol.TryGetValue(_option.Underlying.Symbol.Value, out EarningsAnnouncement[] earningsAnnouncements);
            if (!any || earningsAnnouncements.Length == 0) return 0;

            if (!_algo.Cfg.EarningsAnnouncementUtilityMinDTE.TryGetValue(Underlying.Value, out int minDTE))
            {
                minDTE = _algo.Cfg.EarningsAnnouncementUtilityMinDTE[CfgDefault];
            }

            var nextAnnouncement = earningsAnnouncements.Where(earningsAnnouncements => earningsAnnouncements.Date >= _algo.Time.Date && (earningsAnnouncements.Date - _algo.Time.Date).Days >= minDTE).OrderBy(x => x.Date).FirstOrDefault(defaultValue: null);
            if (nextAnnouncement == null) return 0;

            // Impact after announcement. Implied move goes into gamma delta risk. -ImpliedMove * Vega into IV risk.
            var expiry = _algo.IVSurfaceRelativeStrikeAsk[Underlying].MinExpiry();
            if (expiry == null) return 0;
            int dte = ((DateTime)expiry - _algo.Time.Date).Days;
            double longTermAtm = _algo.AtmIVIndicators[Underlying].Current;
            double impliedMove = _algo.ImpliedMove(Underlying);
            
            // Only consider earnings strategy if market is also elevated, hence prices in increased vola.
            if (!_algo.Cfg.EarningsAnnouncementUtilityMinAtmIVElevation.TryGetValue(Underlying.Value, out double minAtmIVElevation))
            {
                minAtmIVElevation = _algo.Cfg.EarningsAnnouncementUtilityMinAtmIVElevation[CfgDefault];
            }
            if (impliedMove <= minAtmIVElevation * longTermAtm) return 0;

            // Missing directional component. Could use Put/Call ratio & open interest. non-contrary: DELL, contrary: PFE. To be measured.
            // For uni-directional; Want 0 Delta/Vega, but high positive gamma/volga.
            var riskDs = DSMetrics.Select(m => _algo.PfRisk.RiskByUnderlying(Underlying, m, null, null, impliedMove)).Sum();
            // Simplified, presuming perfect negative returns and IV correlation. To be measured.
            var riskDIV = DSMetrics.Select(m => _algo.PfRisk.RiskByUnderlying(Underlying, m, null, null, -impliedMove)).Sum();
            double risk = (double)(riskDs + riskDIV);

            // Discount the utility with 80% on day of announcement. 20% to be distributed across days until then getting increasingly ready. 100 % on actual day.
            if (dte == 0)
            {
                // Go flat. Flag up any exposure as negative.
                utility = (double)(-Math.Abs(riskDs) - Math.Abs(riskDIV));
            } 
            else
            {
                // Long IV, so pick long gamma risk only now. unless AM (different util).
                utility = 0.2 * (double)riskDs / dte;
            }
            _algo.Log($"UtilityOrder.GetUtilityEarningsAnnouncment: impliedMove={impliedMove}, utility={utility}, riskDs={riskDs}, riskDIV={riskDIV}");
            return utility;
        }


        /// <summary>
        /// Theta over the weekend likely less than 3 days. It's already priced in on Friday.
        /// </summary>
        /// <returns></returns>
        private int ThetaDte()
        {
            return _algo.Time.Date.DayOfWeek == DayOfWeek.Friday ? 2 : 1;
        }
        private decimal MidPriceUnderlying { get { return _algo.MidPrice(Underlying); } }
        private double IV(decimal? price = null)
        {
            //return _algo.MidIV(Symbol);
            return (price ?? 0) == 0 ? _algo.MidIV(Symbol) : OCW.IV(_price, MidPriceUnderlying, 0.001);
        }

        public override string ToString()
        {
            var str = $"UTILITYORDER: {Symbol} {OrderDirection} {Quantity} ";
            // Iterate over utility names building a string that contains the attribute whenever its value is non-zero
            foreach (var name in _utilitiesToLog)
            {
                double value = (double)this.GetPropertyValue(name);
                if (value != 0)
                {
                    str += $", {name}={value}";
                }
            }
            return str;
        }
    }
}
