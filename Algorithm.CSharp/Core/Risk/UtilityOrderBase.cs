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
using QuantConnect.Util;

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
    public abstract class UtilityOrderBase : IUtilityOrder
    {
        // Constructor
        protected Foundations _algo;
        protected Option _option;
        protected decimal? _price;
        public decimal Quantity { get; internal set; }
        public double IVPrice { get; internal set; }
        public Symbol Symbol { get => _option.Symbol; }
        protected Security _securityUnderlying;
        protected Security SecurityUnderlying => _securityUnderlying ??= _algo.Securities[Underlying];
        public Symbol Underlying { get => Underlying(Symbol); }
        public DateTime Time { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        protected decimal Multiplier { get => _option.ContractMultiplier; }
        //public double Utility { get => UtilityProfit + UtilityRisk; }
        protected HashSet<Regime> _regimes;

        /// <summary>
        /// Vega / IV0 -> IV1: Skew, Term Structure, Level. EWMA each bin and connecting lead to an EWMA Surface. Assumption: IV returns to that, hence below util. How soon it returns, unclear.
        /// IV != HV: profit: (RV - IV) * 0.5*dS^2 or (RV - IV) * Vega.
        /// Theta: Daily theta. Could be offset by vega during rising IVs, eg, before earnings.
        /// 
        /// </summary>
        public abstract double Utility { get;  }
        
        //public virtual double UtilityProfit { get => 0; }
        //public virtual double UtilityRisk { get => 0; }
        protected HashSet<string> _utilitiesToLog = new() {
            "UtilityManualOrderInstructions", "UtlityVegaMispricedIVUntilExpiry", "UtilityTheta", "UtilityInventory", "UtilityRiskExpiry",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition", 
            "UtilityGamma", "UtilityDontSellBodyBuyWings",
            "UtilityDontLongLowDelta", "UtilityMargin",
            "UtilityVannaRisk", "UtilityTransactionCosts" // Currently not in Utility
            };

        protected double? _utilityProfitSpread;
        public virtual double UtilityProfitSpread { get => _utilityProfitSpread ??= GetUtilityProfitSpread(); }
        protected double? _utilityManualOrderInstructions;
        public virtual double UtilityManualOrderInstructions { get => _utilityManualOrderInstructions ??= GetUtilityManualOrderInstructions(); }
        //protected double? _utilityVegaIV2Ewma;
        //public virtual double UtilityVegaIV2Ewma { get => _utilityVegaIV2Ewma ??= GetUtilityVegaIV2Ewma(); }
        
        protected double? _utlityVegaMispricedIVUntilExpiry;
        public virtual double UtlityVegaMispricedIVUntilExpiry { get => _utlityVegaMispricedIVUntilExpiry ??= GetUtlityVegaMispricedIVUntilExpiry(); }

        protected double? _intradayVolatilityRisk;
        public virtual double IntradayVolatilityRisk { get => _intradayVolatilityRisk ??= GetIntradayVolatilityRisk(); }

        protected double? _utilityInventory;
        public virtual double UtilityInventory { get => _utilityInventory ??= GetUtilityInventory(); }
        protected double? _utilityExpiringNetDelta;
        public virtual double UtilityExpiringNetDelta { get => _utilityExpiringNetDelta ??= GetUtilityExpiringNetDelta(); }
        protected double? _utilityRiskExpiry;
        public virtual double UtilityRiskExpiry { get => _utilityRiskExpiry ??= GetUtilityRiskExpiry(); }
        protected double? _utitlityTheta;
        public virtual double UtilityTheta { get => _utitlityTheta ??= GetUtilityTheta(ThetaDte()); }

        protected double? _utitlityCapitalCostPerDay;
        public virtual double UtilityCapitalCostPerDay { get => _utitlityCapitalCostPerDay ??= GetUtilityCapitalCostPerDay(); }
        protected double? _utilityEquityPosition;
        public virtual double UtilityEquityPosition { get => _utilityEquityPosition ??= GetUtilityEquityPosition(); }
        protected double? _utitlityTransactionCosts;
        public virtual double UtilityTransactionCosts { get => _utitlityTransactionCosts ??= GetUtilityTransactionCosts(); }
        protected double? _utilityDontSellBodyBuyWings;
        public virtual double UtilityDontSellBodyBuyWings { get => _utilityDontSellBodyBuyWings ??= GetUtilityDontSellBodyBuyWings(); }
        protected double? _utitlityGamma;
        public virtual double UtilityGamma { get => _utitlityGamma ??= GetUtilityGamma(); }

        protected double? _utitlityVannaRisk;
        public virtual double UtilityVannaRisk { get => _utitlityVannaRisk ??= GetUtilityVannaRisk(); }
        protected double? _utitlityDontLongLowDelta;
        public virtual double UtilityDontLongLowDelta { get => _utitlityDontLongLowDelta ??= GetUtilityDontLongLowDelta(); }

        protected double? _utitlityMargin;
        public virtual double UtilityMargin { get => _utitlityMargin ??= GetUtilityMargin(); }

        /// <summary>
        /// Don't really want to increase inventory. Hard to Quantity. Attach price tag of 50...
        /// </summary>
        protected double GetUtilityInventory()
        {
            //Portfolio[symbol].Quantity * quantity
            double quantityPosition = (double)_algo.Portfolio[Symbol].Quantity;
            return quantityPosition * (double)Quantity > 0 ? -50 * Math.Abs(quantityPosition) : 0;
        }

        protected double GetUtilityExpiringNetDelta()
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
        protected double GetUtilityTheta(int dDTE = -1)
        {
            return OCW.Theta(IVPrice) * (double)(Quantity * Multiplier) * dDTE;
        }

        /// <summary>
        /// Long stock: opportunity cost / interest. Shorting stock: pay shorting fee, get capital to work with... Not applicable to options
        /// </summary>
        protected decimal CapitalCostPerDay(decimal quantity)
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
        protected double GetUtilityCapitalCostPerDay()
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
        protected double GetUtilityEquityPosition()
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
            //if ((_regimes.Contains(Regime.SellEventCalendarHedge) && _option.Expiry < EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && util > 0) ||
            //    (_regimes.Contains(Regime.SellEventCalendarHedge) && _option.Expiry >= EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && Quantity < 0 && util > 0)
            //    )
            //{
            //    util = 0;
            //}
            return util;
        }
        /// <summary>
        /// Essentially an overall stress risk provile util. Stressing dS by +/- 15%. Becoming hugely influencial once algo excceeds a used margin threshold.
        /// </summary>
        /// <returns></returns>
        protected double GetUtilityMargin()
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
        protected double GetUtilityTransactionCosts()
        {
            static decimal transactionCost(decimal x) => 1 * Math.Abs(x);  // refactor to transaction fee estimator.
            static double transactionHedgeCost(double x) => 0.05 * Math.Abs(x);  // refactor to transaction fee estimator.

            decimal deltaOrder = _algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);

            return -(double)transactionCost(Quantity) - transactionHedgeCost((double)deltaOrder);
        }

        protected DateTime EventDate => _algo.EventDate(Underlying);
        protected DateTime? _expiryEventImpacted;
        protected DateTime ExpiryEventImpacted => _expiryEventImpacted ??= _algo.ExpiryEventImpacted(Symbol);

        /// <summary>
        /// 0.5 gamma * dS**2 == theta * dT for delta hedged option.
        /// Gain: Given realized vola, estimate distribution of dS before trailing_stop(vola) kicks in. Calibrate numerically and BT.
        /// Risk: Reducing Exposure increases Util. Pos Gamma exposure yields gamma scalping profits. Neg loses it. Offset by theta. 
        /// </summary>
        /// <returns></returns>
        protected double GetUtilityGamma()
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

        protected double GetUtilityDontSellBodyBuyWings()
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
        protected double GetUtilityVannaRisk()
        {
            var totalVanna = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.Vanna100BpUSDTotal);
            var vannaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.Vanna100BpUSDTotal);
            bool isAbsVannaIncreasing = vannaOrder * totalVanna > 0;
            return isAbsVannaIncreasing ? -Math.Abs(vannaOrder) : Math.Abs(vannaOrder);
        }

        /// <summary>
        /// VegaSkew
        /// </summary>
        protected double GetUtilityVanna()
        {
            return 0;
        }
        /// <summary>
        /// 0.05 USD kinda options. Dont long'em
        /// </summary>
        /// <returns></returns>
        protected double GetUtilityDontLongLowDelta()
        {
            if ( UtilityMargin > 200) return 0;  // May want to hedge scenario risk or large moves.

            double delta = OCW.Delta((double)IVPrice);
            return delta < 0.1 && Quantity > 0 ? -100 * (double)Quantity : 0;
        }

        /// <summary>
        /// Dont buy stuff about to expire. But that should be quantified. A risk is underlying moving after market close.
        /// To be fined. THere's a util on theta
        /// </summary>
        protected double GetUtilityRiskExpiry()
        {
            return OrderDirection == OrderDirection.Buy && (_option.Symbol.ID.Date - _algo.Time.Date).Days <= 5 ? -(double)(Quantity * Multiplier) : 0;
        }

        /// <summary>
        /// Sell AM, Buy PM.
        /// </summary>
        protected double GetIntradayVolatilityRisk()
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
        protected double GetUtilityProfitSpread()
        {
            decimal spread = (_option.AskPrice - _option.BidPrice) / 2;
            return Math.Abs((double)(Quantity * Multiplier * spread));
        }
        protected double GetUtilityManualOrderInstructions()
        {
            if (
                !_algo.ManualOrderInstructionBySymbol.ContainsKey(Symbol.Value) ||
                !_algo.Cfg.ExecuteManualOrderInstructions
                )
            {
                return -2000;
            }
            double hour = Math.Max(1, _algo.Time.Hour - 9 / 2);

            ManualOrderInstruction manualOrderInstruction = _algo.ManualOrderInstructionBySymbol[Symbol.Value];

            // Respect Time to trade condition
            bool isTimeToTrade = !(manualOrderInstruction.TimeToTrade != null && manualOrderInstruction.TimeToTrade.Any());
            if (!isTimeToTrade)
            {
                manualOrderInstruction.TimeToTrade.DoForEach(timeToTrade =>
                {
                    if (timeToTrade[0] <= _algo.Time.TimeOfDay && _algo.Time.TimeOfDay <= timeToTrade[1])
                    {
                        isTimeToTrade = true;
                    }
                });
            }
            if (!isTimeToTrade) return -2000;


            // If util is not defined, defaults to zero => no discount.
            decimal orderQuantity = manualOrderInstruction.TargetQuantity - _algo.Portfolio[Symbol].Quantity;
            if (orderQuantity > 0 && OrderDirection == OrderDirection.Buy) {
                return manualOrderInstruction.Utility ?? 0 * hour;
            }
            else if (orderQuantity < 0 && OrderDirection == OrderDirection.Sell) return manualOrderInstruction.Utility ?? 0 * hour ;
            else if (orderQuantity == 0 || _algo.Portfolio[Symbol].Quantity == 0) return -2000;
            else return -2000;
        }

        protected OptionContractWrap? _ocw = null;
        protected OptionContractWrap OCW
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
        /// Ignoring moving S, hence ignoring skew.surface ATM IV is slightly decreasing over lifetime. Skew closer to maturity will get steeper.
        /// Earning money from vega only when IV is significantly above forecasted IV. Not trying to capture theta here...
        /// 
        /// Easier thinking in Delta/IV Currado Su surface. Less skew, more linear...
        /// So would a slow moving IV measured in DTE to have an anchor, essentially looking to forecast IV, not HV.
        /// 
        /// Combines Skew, surface and term structure.
        /// /// </summary>
        protected double GetUtilityVegaIV2Ewma()
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
        protected double GetUtlityVegaMispricedIVUntilExpiry()
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
        protected decimal ExpectedMeanRealizedVolatility(Symbol underlying, DateTime? until=null)
        {
            return (decimal)_algo.AtmIV(underlying);  // Hedging Error was larger with ATM, hence assuming not matching realized vola better...
            //return _algo.Securities[underlying].VolatilityModel.Volatility;  // Led to drastic losses in Nov 22nd 2023. To be investigated. Need proper expected realized vola.
        }

        /// <summary>
        /// Theta over the weekend likely less than 3 days. It's already priced in on Friday.
        /// </summary>
        /// <returns></returns>
        protected int ThetaDte()
        {
            return _algo.Time.Date.DayOfWeek == DayOfWeek.Friday ? 2 : 1;
        }
        protected decimal MidPriceUnderlying { get { return _algo.MidPrice(Underlying); } }
        protected double IV(decimal? price = null)
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
