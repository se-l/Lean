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
    /// </summary>
    public class UtilityOrder
    {
        // Constructor
        private readonly Foundations _algo;
        private Option Option { get; }
        public decimal Quantity { get; internal set; }
        public Symbol Symbol { get => Option.Symbol; }
        private Security _securityUnderlying;
        private Security SecurityUnderlying => _securityUnderlying ??= _algo.Securities[Underlying];
        private decimal HV => SecurityUnderlying.VolatilityModel.Volatility;
        public Symbol Underlying { get => Underlying(Symbol); }
        public DateTime Time { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        private decimal Multiplier { get => Option.ContractMultiplier; }
        //public double Utility { get => UtilityProfit + UtilityRisk; }
        private HashSet<Regime> Regimes;
        /// <summary>
        /// Vega / IV0 -> IV1: Skew, Term Structure, Level. EWMA each bin and connecting lead to an EWMA Surface. Assumption: IV returns to that, hence below util. How soon it returns, unclear.
        /// IV != HV: profit: (RV - IV) * 0.5*dS^2 or (RV - IV) * Vega.
        /// Theta: Daily theta. Could be offset by vega during rising IVs, eg, before earnings.
        /// 
        /// </summary>
        public double Utility { get =>
                // PV based
                //UtilityProfitSpread +  // That's always positive. Not good if option universe is to be split in 4 quadrants. Call/Put Buy/Sell, where only 1 is good for portfolio at a t 
                UtilityManualOrderInstructions +
                UtlityVegaMispricedIVUntilExpiry +  // IV at expiry incorrectly assumed to be RV.
                UtilityVegaIV2Ewma +
                UtilityTheta +  // double counting any value that's already included in the vega utils?
                IntradayVolatilityRisk +
                UtilityCapitalCostPerDay +
                UtilityTransactionCosts +  // Very approximate, but small at the moment

                // To be PV refactored
                UtilityEventUpcoming +

                // ES based to be refactored
                UtilityRiskExpiry +  // Coz Theta impact somehow too small. Should make dependent on how much OTM. Just use delta. An optimization given there are many other terms to choose from.                
                UtilityGammaRisk +
                UtilityDontLongLowDelta +
                UtilityEquityPosition +  // essentially there is a hedging inefficieny. Whatever net delta I hold after options, can multiply by such a factor.

                // Utils need to naturally flow into hedging scenarios. E.g.: short 10 call => short puts to arrive at 0 delta. Or short IV a very low gamma => calendar spread

                // Not yet refactored
                UtilityMargin +
                UtilityInventory;
            //UtilityVanna;  // Call vs Put & Buy vs Sell.                
        }
        //public double UtilityProfit { get => 0; }
        //public double UtilityRisk { get => 0; }
        private readonly HashSet<string> _utilitiesToLog = new() {
            "UtilityManualOrderInstructions", "UtlityVegaMispricedIVUntilExpiry", "UtilityVegaIV2Ewma", "UtilityTheta", "IntradayVolatilityRisk", "UtilityInventory", "UtilityRiskExpiry",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition", "UtilityGammaRisk", "UtilityDontLongLowDelta", "UtilityMargin", "UtilityEventUpcoming",
            "UtilityVannaRisk", "UtilityTransactionCosts" // Currently not in Utility
            };

        private double? _utilityProfitSpread;
        public double UtilityProfitSpread { get => _utilityProfitSpread ??= GetUtilityProfitSpread(); }
        private double? _utilityManualOrderInstructions;
        public double UtilityManualOrderInstructions { get => _utilityManualOrderInstructions ??= GetUtilityManualOrderInstructions(); }
        private double? _utilityVegaIV2Ewma;
        public double UtilityVegaIV2Ewma { get => _utilityVegaIV2Ewma ??= GetUtilityVegaIV2Ewma(); }
        private double? _utlityVegaMispricedIVUntilExpiry;
        public double UtlityVegaMispricedIVUntilExpiry { get => _utlityVegaMispricedIVUntilExpiry ??= GetUtlityVegaMispricedIVUntilExpiry(); }

        private double? _intradayVolatilityRisk;
        public double IntradayVolatilityRisk { get => _intradayVolatilityRisk ??= GetIntradayVolatilityRisk(); }

        private double? _utilityInventory;
        public double UtilityInventory { get => _utilityInventory ??= GetUtilityInventory(); }
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
        private double? _utitlityGammaRisk;
        public double UtilityGammaRisk { get => _utitlityGammaRisk ??= GetUtilityGammaRisk(); }
        private double? _utitlityEventUpcoming;
        public double UtilityEventUpcoming { get => _utitlityEventUpcoming ??= GetUtilityEventUpcoming(); }

        private double? _utitlityGammaScalping;
        public double UtilityGammaScalping { get => _utitlityGammaScalping ??= GetUtilityGammaScalping(); }

        private double? _utitlityVannaRisk;
        public double UtilityVannaRisk { get => _utitlityVannaRisk ??= GetUtilityVannaRisk(); }
        private double? _utitlityDontLongLowDelta;
        public double UtilityDontLongLowDelta { get => _utitlityDontLongLowDelta ??= GetUtilityDontLongLowDelta(); }

        private double? _utitlityMargin;
        public double UtilityMargin { get => _utitlityMargin ??= GetUtilityMargin(); }

        public UtilityOrder(Foundations algo, Option option, decimal quantity)
        {
            _algo = algo;
            Option = option;
            Quantity = quantity;
            Time = _algo.Time;
            OrderDirection = Num2Direction(Quantity);
            Regimes = _algo.ActiveRegimes.TryGetValue(Underlying, out Regimes) ? Regimes : new HashSet<Regime>();

            // Calling Utility to snap the risk => cached for future use.
            _ = Utility;
            algo.UtilityWriters[Underlying].Write(this);
        }

        /// <summary>
        /// Don't really want to increase inventory. Hard to Quantity. Attach price tag of 50...
        /// </summary>
        private double GetUtilityInventory()
        {
            //Portfolio[symbol].Quantity * quantity
            double quantityPosition = (double)_algo.Portfolio[Symbol].Quantity;
            return quantityPosition * (double)Quantity > 0 ? -50 * Math.Abs(quantityPosition) : 0;
        }

        /// <summary>
        /// Selling. Pos Util. Buying. Neg Util. 
        /// </summary>
        private double GetUtilityTheta(int dDTE = -1)
        {
            return OCW.Theta(_algo.MidIV(Symbol)) * (double)(Quantity * Multiplier) * dDTE;
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
        /// Related to MarginUtil (kicks in at higher equity positions). Better unify both!
        /// Objectives: 1) Incentivize trades that minimize margin requirements.
        /// 
        /// Scenario where a higher absolute total delta reduces margin requirements: 
        /// </summary>
        /// <returns></returns>
        private double GetUtilityEquityPosition()
        {
            double totalAbsEquityPosUtil;

            decimal targetMaxEquityPositionUSD = _algo.Cfg.TargetMaxEquityPositionUSD.TryGetValue(Underlying.Value, out targetMaxEquityPositionUSD) ? targetMaxEquityPositionUSD : _algo.Cfg.TargetMaxEquityPositionUSD[CfgDefault];
            double targetMaxEquityQuantity = (double)(targetMaxEquityPositionUSD / SecurityUnderlying.Price);

            double totalDerivativesDelta = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, _algo.HedgeMetric(Underlying));
            double orderDelta = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, _algo.HedgeMetric(Underlying));
            double cumDelta = totalDerivativesDelta + orderDelta;
            double cumEquityDelta = -cumDelta;

            int ifFilledDeltaTotalIncreases = Math.Sign(orderDelta * totalDerivativesDelta);

            // What's the PV of this Delta increase ???
            // Instantaneous hedging cost.
            // Increase/Decrease in subsequent hedging costs, a function of fwdVola, pfGamma, pfDelta

            double scaleOrder = totalDerivativesDelta != 0 ? Math.Abs(orderDelta / totalDerivativesDelta) : 1;
            double scaleRelativeToOthertilities = 1.0 / 10;

            totalAbsEquityPosUtil = Math.Abs(orderDelta) + Math.Pow(Math.Max(0, Math.Abs(cumEquityDelta) - targetMaxEquityQuantity), 2);
            //_algo.Log($"{_algo.Time} UTIL={totalEquityPosUtil * scaleOrder * scaleRelativeToOthertilities * negIfRiskAbsDeltaIncreasing}, totalEquityPosUtil={totalEquityPosUtil}, orderDelta={orderDelta}, totalDerivativesDelta={totalDerivativesDelta}, scaleOrder={scaleOrder}");
            double util = totalAbsEquityPosUtil * scaleOrder * scaleRelativeToOthertilities * -ifFilledDeltaTotalIncreases;

            // CalendarSpread. Not hedging risk with front, but back months. To be refactored getting target expiries, rather than hard coding day range.
            if ((Regimes.Contains(Regime.SellEventCalendarHedge) && Option.Expiry < EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && util > 0) ||
                (Regimes.Contains(Regime.SellEventCalendarHedge) && Option.Expiry >= EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) && Quantity < 0 && util > 0)
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
            decimal initialMargin;
            decimal targetMarginAsFractionOfNLV = _algo.Cfg.TargetMarginAsFractionOfNLV.TryGetValue(Underlying.Value, out targetMarginAsFractionOfNLV) ? targetMarginAsFractionOfNLV : _algo.Cfg.TargetMarginAsFractionOfNLV[CfgDefault];
            double marginUtilScaleFactor = _algo.Cfg.MarginUtilScaleFactor.TryGetValue(Underlying.Value, out marginUtilScaleFactor) ? marginUtilScaleFactor : _algo.Cfg.MarginUtilScaleFactor[CfgDefault];

            initialMargin = _algo.InitialMargin();
            if (initialMargin == 0) return 0;  // No Position whatsover. Algo start.

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
        /// Reducing Exposure increases Util. Pos Gamma exposure yields gamma scalping profits. Neg loses it. Is offset by theta. Would need a way to quantity gamma scalping profits.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityGammaRisk()
        {
            HashSet<Regime> regimes = _algo.ActiveRegimes.TryGetValue(Underlying, out regimes) ? regimes : new HashSet<Regime>();
            bool wantPosGamma = regimes.Contains(Regime.SellEventCalendarHedge);

            var totalGamma = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.GammaTotal);
            var gammaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.GammaTotal);
            bool isAbsGammaIncreasing = gammaOrder * totalGamma > 0;
            if (wantPosGamma)
            {
                return isAbsGammaIncreasing ? Math.Abs(gammaOrder) : -Math.Abs(gammaOrder);
            }
            else
            {
                return isAbsGammaIncreasing ? -Math.Abs(gammaOrder) : Math.Abs(gammaOrder);
            }
        }

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
            double _util = 0;

            if (Regimes.Contains(Regime.SellEventCalendarHedge))
            {
                if (Option.Expiry < EventDate.AddDays(_algo.Cfg.CalendarSpreadPeriodDays) || OrderDirection == OrderDirection.Sell) 
                {   
                    if (OrderDirection == OrderDirection.Sell)
                    {
                        decimal absDeltaFrontMonthCallsTotal = Math.Abs(_algo.PfRisk.RiskByUnderlying(Symbol.Underlying, _algo.HedgeMetric(Symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => _algo.IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Call)));
                        decimal absDeltaFrontMonthPutsTotal = Math.Abs(_algo.PfRisk.RiskByUnderlying(Symbol.Underlying, _algo.HedgeMetric(Symbol.Underlying), filter: (IEnumerable<Position> positions) => positions.Where(p => _algo.IsFrontMonthPosition(p) && p.OptionRight == OptionRight.Put)));

                        // For the calendar spread, would want an approximately equal delta between short puts and short calls. Therefore, selling wichever right has a significantly lower abs delta.
                        bool needToSellMore = (Option.Right) switch
                        {
                            OptionRight.Call => absDeltaFrontMonthCallsTotal < 0.9m * absDeltaFrontMonthPutsTotal,
                            OptionRight.Put => absDeltaFrontMonthPutsTotal < 0.9m * absDeltaFrontMonthCallsTotal,
                        };
                        double delta = OCW.Delta(_algo.MidIV(Symbol));

                        if (needToSellMore && Math.Abs(delta) < 0.3)
                        {
                            _algo.Log($"{_algo.Time} GetUtilityEventUpcoming: Need to Sell more {Option.Right}. absDeltaFrontMonthCallsTotal={absDeltaFrontMonthCallsTotal}, absDeltaFrontMonthPutsTotal={absDeltaFrontMonthPutsTotal}");
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

                if (Option.Expiry < ExpiryEventImpacted) return 0;

                // Sell the high IV. refactor to actually selling extraordinarily high IV contracts, dont select by date...
                if (Option.Expiry < ExpiryEventImpacted.AddDays(60) && OrderDirection == OrderDirection.Sell && meanStressPnlPf > 0)  // At this point, only sell IV if risk is reduced.
                {
                    _util = UtilityVegaIV2Ewma;
                }

                // This should be fairly negative in losing vega util...
                if (Option.Expiry < ExpiryEventImpacted.AddDays(60) && OrderDirection == OrderDirection.Buy) return 0;

                // Buy back month hedging above sell
                if (Option.Expiry >= ExpiryEventImpacted.AddDays(60))
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
                if (Option.Expiry < ExpiryEventImpacted) return 0;
                if (Option.Expiry < ExpiryEventImpacted.AddDays(14) && OrderDirection == OrderDirection.Sell) return -500;
                if (Option.Expiry < ExpiryEventImpacted.AddDays(14) && OrderDirection == OrderDirection.Buy) return 500;
            }
            return 0;
        }
        private double GetUtilityGammaScalping()
        {
            // Depends on whether gamma scalping is active and at which vola we hedge: Future realized! Also depends on hedging frequency as return is quadratic in dS.
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
            double delta = OCW.Delta((double)HV);
            return delta < 0.1 && Quantity > 0 ? -100 * (double)Quantity : 0;
        }

        /// <summary>
        /// Dont buy stuff about to expire. But that should be quantified. A risk is underlying moving after market close.
        /// To be fined. THere's a util on theta
        /// </summary>
        private double GetUtilityRiskExpiry()
        {
            return OrderDirection == OrderDirection.Buy && (Option.Symbol.ID.Date - _algo.Time.Date).Days <= 5 ? -(double)(Quantity * Multiplier) : 0;
        }

        /// <summary>
        /// Sell AM, Buy PM.
        /// </summary>
        private double GetIntradayVolatilityRisk()
        {
            if (_intradayVolatilityRisk != null) return (double)_intradayVolatilityRisk;

            if (_algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].Direction().Length > 1) return 0;

            double iv = (_algo.IVAsks[Symbol].IVBidAsk.IV + _algo.IVBids[Symbol].IVBidAsk.IV) / 2;
            double intraDayIVSlope = _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].IntraDayIVSlope;
            double fractionOfDayRemaining = 1 - _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].FractionOfDay(_algo.Time);
            return OCW.Vega(iv) * intraDayIVSlope * fractionOfDayRemaining * (double)(Quantity * Option.ContractMultiplier);
        }

        private double GetUtilityProfitSpread()
        {
            decimal spread = (Option.AskPrice - Option.BidPrice) / 2;
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
                    _ocw = OptionContractWrap.E(_algo, Option, _algo.Time.Date);
                    _ocw.SetIndependents(_algo.MidPrice(Underlying), _algo.MidPrice(Symbol));
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
        private double GetUtilityVegaIV2Ewma()
        {
            double midIV = _algo.MidIV(Symbol);            
            if (midIV == 0) { return 0; }
            double midIVEwma = _algo.MidIVEWMA(Symbol);
            // Favors selling skewed wings.
            double fv = (midIVEwma - midIV) * OCW.Vega(midIV) * (double)(Quantity * Option.ContractMultiplier);
            return (double)_algo.DiscountedValue((decimal)fv, 1.0/365.0);  // Expecting intraday reversion to expected IV levels
        }

        private double GetUtilityVegaSuspiciouslyLowIV()
        {
            Symbol underlying = Symbol.Underlying;
            double midIV = _algo.MidIV(Symbol);
            // Punish selling weirdly low IVs. To be investigated. saw once 16% where usually ~30%.
            double fwdVol = (double)ExpectedRealizedVolatility(underlying);
            if (midIV < fwdVol && Quantity < 0)
            {
                //_algo.Log($"GetUtilityVega2HV: Preventing selling low IV: {Symbol} {Quantity}. iv={iv} < fwdIV={fwdIV}. util={util} reduced by 1000");
                return -1000;
            };
            return 0;
        }

        /// <summary>
        /// Difference between (RV - IV) * Vega
        /// </summary>
        /// <returns></returns>
        private double GetUtlityVegaMispricedIVUntilExpiry()
        {
            Symbol underlying = Symbol.Underlying;
            double midIVFwdShortTerm = _algo.MidIVEWMA(Symbol);
            double fwdVola = (double)ExpectedRealizedVolatility(underlying);

            // Hacky presumption: IV is never lower than future realized volatility, therefore volatility is min(stdReturns, IV)
            double atmIV = _algo.AtmIV(Symbol);
            if (atmIV > 0 && atmIV < fwdVola)
            {
                fwdVola = atmIV;
            }

            double fv = (fwdVola - midIVFwdShortTerm) * OCW.Vega(midIVFwdShortTerm) * (double)(Quantity * Option.ContractMultiplier);
            return (double)_algo.DiscountedValue((decimal)fv, Option);
        }

        /// <summary>
        /// Simplified model.
        /// </summary>
        private decimal ExpectedRealizedVolatility(Symbol underlying)
        {
            return _algo.Securities[underlying].VolatilityModel.Volatility;
        }

        /// <summary>
        /// Before announcment, 1) buy IV. 2) The AM Sell IV and PM buy IV must outweigh this utility at least during the rise up.
        /// On Announcement day, sell IV or other to be developed strategies (calendar spread).
        /// </summary>
        private double GetUtilityEarningsAnnouncment()
        {
            double utility = 0;

            bool any = _algo.EarningsBySymbol.TryGetValue(Option.Underlying.Symbol.Value, out EarningsAnnouncement[] earningsAnnouncements);
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
            return _algo.Time.Date.DayOfWeek == DayOfWeek.Friday ? 3 : 1;
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
