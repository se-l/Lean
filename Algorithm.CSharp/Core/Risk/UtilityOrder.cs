using System;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using System.Collections.Generic;
using Accord.Math;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using Fasterflect;
using QuantConnect.Securities;

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
        public Symbol Underlying { get => Underlying(Symbol); }
        public DateTime Time { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        private decimal Multiplier { get => Option.ContractMultiplier; }
        //public double Utility { get => UtilityProfit + UtilityRisk; }
        public double Utility { get =>
                UtilityProfitSpread +  // That's always positive. Not good if option universe is to be split in 4 quadrants. Call/Put Buy/Sell, where only 1 is good for portfolio at a t 
                UtilityVega2HV +
                UtilityTheta +
                IntradayVolatilityRisk +
                UtilityEarningsAnnouncment +
                UtilityInventory +
                UtilityRiskExpiry +  // Coz Theta impact somehow too small. Should make dependent on how much OTM. Just use delta. An optimization given there are many other terms to choose from.
                UtilityCapitalCostPerDay +
                UtilityEquityPosition +
                UtilityGamma +
                UtilityLowDelta +
                UtilityMargin;
                //UtilityVanna;  // Call vs Put & Buy vs Sell.                
        }
        //public double UtilityProfit { get => 0; }
        //public double UtilityRisk { get => 0; }
        private readonly HashSet<string> _utilitiesToLog = new() {
            "UtilityVega2HV", "UtilityTheta", "IntradayVolatilityRisk", "UtilityEarningsAnnouncment", "UtilityInventory", "UtilityRiskExpiry",
            "UtilityCapitalCostPerDay", "UtilityEquityPosition", "UtilityGamma", "UtilityLowDelta", "UtilityMargin",
            "UtilityVanna", "UtilityTransactionCosts" // Currently not in Utility
            };

        private double? _utilityProfitSpread;
        public double UtilityProfitSpread { get => _utilityProfitSpread ??= GetUtilityProfitSpread(); }
        private double? _utilityVega2IVEwma;
        public double UtilityVega2IVEwma { get => _utilityVega2IVEwma ??= GetUtilityVega2IVEwma(); }

        private double? _utilityVega2HV;
        public double UtilityVega2HV { get => _utilityVega2HV ??= GetUtilityVega2HV(); }

        private double? _intradayVolatilityRisk;
        public double IntradayVolatilityRisk { get => _intradayVolatilityRisk ??= GetIntradayVolatilityRisk(); }

        private double? _utilityInventory;
        public double UtilityInventory { get => _utilityInventory ??= GetUtilityInventory(); }
        private double? _utilityRiskExpiry;
        public double UtilityRiskExpiry { get => _utilityRiskExpiry ??= GetUtilityRiskExpiry(); }

        private double? _utitlityEarningsAnnouncment;
        public double UtilityEarningsAnnouncment { get => _utitlityEarningsAnnouncment ??= GetUtilityEarningsAnnouncment(); }
        private double? _utitlityTheta;
        public double UtilityTheta { get => _utitlityTheta ??= GetUtilityTheta(ThetaDte()); }

        private double? _utitlityCapitalCostPerDay;
        public double UtilityCapitalCostPerDay { get => _utitlityCapitalCostPerDay ??= GetUtilityCapitalCostPerDay(); }
        private double? _utilityEquityPosition;
        public double UtilityEquityPosition { get => _utilityEquityPosition ??= GetUtilityEquityPosition(); }
        private double? _utitlityTransactionCosts;
        public double UtilityTransactionCosts { get => _utitlityTransactionCosts ??= GetUtilityTransactionCosts(); }
        private double? _utitlityGamma;
        public double UtilityGamma { get => _utitlityGamma ??= GetUtilityGamma(); }

        private double? _utitlityVanna;
        public double UtilityVanna { get => _utitlityVanna ??= GetUtilityVanna(); }
        private double? _utitlityLowDelta;
        public double UtilityLowDelta { get => _utitlityLowDelta ??= GetUtilityLowDelta(); }

        private double? _utitlityMargin;
        public double UtilityMargin { get => _utitlityMargin ??= GetUtilityMargin(); }

        public UtilityOrder(Foundations algo, Option option, decimal quantity)
        {
            _algo = algo;
            Option = option;
            Quantity = quantity;
            Time = _algo.Time;
            OrderDirection = Num2Direction(Quantity);

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
            return OptionContractWrap.E(_algo, Option, _algo.Time.Date).Theta() * (double)(Quantity * Multiplier) * dDTE;
        }

        /// <summary>
        /// Punish buying stocks, reward shorting. Assuming CAGR of 30% and cost of shorting of 10%.
        /// </summary>
        private decimal CapitalCostPerDay(decimal quantity)
        {
            return quantity > 0 ? -quantity * SecurityUnderlying.Price * 0.3m / 365 : quantity * SecurityUnderlying.Price * 0.20m / 365;
        }

        /// <summary>
        /// Reducing OptionsOnlyDelta is good. Util up! Delta exposure costs hedings transaction cost and bound capital. Needs Expontential profile.
        /// Shorting gives me capital, much better than having to buy the underlying stock for heding purposes.
        /// </summary>
        private double GetUtilityCapitalCostPerDay()
        {
            decimal deltaOrder = _algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);

            return (double)CapitalCostPerDay(deltaOrder);
        }
        private double GetUtilityEquityPosition()
        {
            double totalEquityPosUtil;

            decimal targetMaxEquityPositionUSD = _algo.Cfg.TargetMaxEquityPositionUSD.TryGetValue(Underlying.Value, out targetMaxEquityPositionUSD) ? targetMaxEquityPositionUSD : _algo.Cfg.TargetMaxEquityPositionUSD[CfgDefault];
            double targetMaxEquityDelta = (double)(targetMaxEquityPositionUSD / SecurityUnderlying.Price);

            double totalDerivativesDelta = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.DeltaTotal);
            double orderDelta = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);
            double cumDelta = totalDerivativesDelta + orderDelta;
            double cumEquityDelta = -cumDelta;

            int negIfRiskAbsDeltaIncreasing = -Math.Sign(orderDelta * totalDerivativesDelta);
            double scaleOrder = totalDerivativesDelta != 0 ? Math.Abs(orderDelta / totalDerivativesDelta) : 1;
            double scaleRelativeToOthertilities = 1.0 / 10;

            totalEquityPosUtil = Math.Abs(Math.Abs(cumEquityDelta) + Math.Pow(Math.Max(0, Math.Abs(cumEquityDelta) - targetMaxEquityDelta), 2));
            //if (cumEquityDelta > 0)
            //{
            //    // Long position. Exponential punishment / reward. Starts becoming exponential after targetMaxEquityDelta.
            //    totalEquityPosUtil = Math.Abs(cumEquityDelta + Math.Pow(Math.Max(0, cumEquityDelta - targetMaxEquityDelta), 2));
            //}
            //else
            //{
            //    // Short equity position. Linear is enough.
            //    totalEquityPosUtil = Math.Abs(Math.Abs(cumEquityDelta) + Math.Pow(Math.Max(0, Math.Abs(cumEquityDelta) - targetMaxEquityDelta), 2));
            //    //totalEquityPosUtil = Math.Abs(cumEquityDelta);
            //}
            //_algo.Log($"{_algo.Time} UTIL={totalEquityPosUtil * scaleOrder * scaleRelativeToOthertilities * negIfRiskAbsDeltaIncreasing}, totalEquityPosUtil={totalEquityPosUtil}, orderDelta={orderDelta}, totalDerivativesDelta={totalDerivativesDelta}, scaleOrder={scaleOrder}");
            return totalEquityPosUtil * scaleOrder * scaleRelativeToOthertilities * negIfRiskAbsDeltaIncreasing;
        }

        private double GetUtilityTransactionCosts()
        {
            static decimal transactionCost(decimal x) => 1 * Math.Abs(x);  // refactor to transaction fee estimator.
            static double transactionHedgeCost(double x) => 0.05 * Math.Abs(x);  // refactor to transaction fee estimator.

            decimal deltaOrder = _algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.DeltaTotal);

            return -(double)transactionCost(Quantity) - transactionHedgeCost((double)deltaOrder);
        }

        /// <summary>
        /// Reducing Exposure increases Util. Pos Gamma exposure yields gamma scalping profits. Neg loses it. Is offset by theta. Would need a way to quantity gamma scalping profits.
        /// </summary>
        /// <returns></returns>
        private double GetUtilityGamma()
        {
            var totalGamma = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.GammaTotal);
            var gammaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.GammaTotal);
            bool isAbsGammaIncreasing = gammaOrder * totalGamma > 0;
            return isAbsGammaIncreasing ? -Math.Abs(gammaOrder) : Math.Abs(gammaOrder);
        }
        
        private double GetUtilityVanna()
        {
            var totalVanna = (double)_algo.PfRisk.DerivativesRiskByUnderlying(Underlying, Metric.Vanna100BpUSDTotal);
            var vannaOrder = (double)_algo.PfRisk.RiskIfFilled(Symbol, Quantity, Metric.Vanna100BpUSDTotal);
            bool isAbsVannaIncreasing = vannaOrder * totalVanna > 0;
            return isAbsVannaIncreasing ? -Math.Abs(vannaOrder) : Math.Abs(vannaOrder);
        }
        /// <summary>
        /// 0.05 USD kinda options. Dont long'em
        /// </summary>
        /// <returns></returns>
        private double GetUtilityLowDelta()
        {
            double delta = OptionContractWrap.E(_algo, Option, _algo.Time.Date).Delta();
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

            double intraDayIVSlope = _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].IntraDayIVSlope;
            double fractionOfDayRemaining = 1 - _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].FractionOfDay(_algo.Time);
            return OptionContractWrap.E(_algo, Option, _algo.Time.Date).Vega() * intraDayIVSlope * fractionOfDayRemaining * (double)(Quantity * Option.ContractMultiplier);
        }

        private double GetUtilityProfitSpread()
        {
            decimal spread = (Option.AskPrice - Option.BidPrice) / 2;
            return Math.Abs((double)(Quantity * Multiplier * spread));
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
            //decimal excessLiquidity = _algo.Portfolio.MarginMetrics.ExcessLiquidity;
            decimal marginExcessTarget = Math.Max(0, initialMargin - _algo.Portfolio.TotalPortfolioValue * targetMarginAsFractionOfNLV);

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
                //_algo.Log($"GetUtilityMargin: {Symbol} {Quantity}. {noNewPositionTag}initialMargin={initialMargin} Exceeded by marginExcessTarget={marginExcessTarget}. utilMargin={utilMargin} based on stressedPnl={stressedPnl}");
            }
            return utilMargin;
        }
        /// <summary>
        /// ( Realized Vola - Implied Vola ) * Vega.
        /// Here Realized Vola is the EWMA of IV.
        /// Preliminary assumption for vola: constant.
        /// Needs to converted into a daily expected util/PV.
        /// </summary>
        private double GetUtilityVega2IVEwma()
        {
            Symbol underlying = Symbol.Underlying;
            
            double fwdVol = OrderDirection switch
            {
                OrderDirection.Buy => _algo.IVSurfaceRelativeStrikeBid.ContainsKey(underlying) ? _algo.IVSurfaceRelativeStrikeBid[underlying].IV(Symbol) : 0,
                OrderDirection.Sell => _algo.IVSurfaceRelativeStrikeAsk.ContainsKey(underlying) ? _algo.IVSurfaceRelativeStrikeAsk[underlying].IV(Symbol) : 0,
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {OrderDirection}")
            } ?? 0;
            if (fwdVol == 0) { return 0; }

            return ExpectedVegaUtil(underlying, fwdVol);
        }

        /// <summary>
        /// ( Realized Vola - Implied Vola ) * Vega.
        /// Here Realized Vola is Historical Vola.
        /// Preliminary assumption for vola: constant.
        /// </summary>
        private double GetUtilityVega2HV()
        {
            Symbol underlying = Symbol.Underlying;
            
            double fwdVol = (double)ExpectedRealizedVolatility(underlying);
            if (fwdVol == 0) { return 0; }

            return ExpectedVegaUtil(underlying, fwdVol);
        }

        private double ExpectedVegaUtil(Symbol underlying, double fwdVol)
        {
            double iv = OrderDirection switch
            {
                OrderDirection.Buy => _algo.IVBids[Symbol].IVBidAsk.IV,
                OrderDirection.Sell => _algo.IVAsks[Symbol].IVBidAsk.IV,
                _ => 0
            };
            if (iv == 0) { return 0; }

            var ocw = OptionContractWrap.E(_algo, Option, _algo.Time.Date);
            ocw.SetIndependents(_algo.MidPrice(underlying), _algo.MidPrice(Symbol), fwdVol);
            double vega = ocw.Vega();

            double util = (fwdVol - iv) * vega * (double)(Quantity * Option.ContractMultiplier);
            int dte = Symbol.ID.Date.Subtract(_algo.Time.Date).Days;
            return util / dte;
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

            bool any = _algo.EarningBySymbol.TryGetValue(Option.Underlying.Symbol.Value, out EarningsAnnouncement[] earningsAnnouncements);
            if (!any || earningsAnnouncements.Length == 0) return 0;

            if (!_algo.Cfg.EarningsAnnouncementUtilityMinDTE.TryGetValue(Underlying.Value, out int minDTE))
            {
                minDTE = _algo.Cfg.EarningsAnnouncementUtilityMinDTE[CfgDefault];
            }

            var nextAnnouncement = earningsAnnouncements.Where(earningsAnnouncements => earningsAnnouncements.Date >= _algo.Time.Date && (earningsAnnouncements.Date - _algo.Time.Date).Days >= minDTE).OrderBy(x => x.Date).FirstOrDefault(defaultValue: null);
            if (nextAnnouncement == null) return 0;

            // Impact after announcement. Implied move goes into gamma delta risk. -ImpliedMove * Vega into IV risk.
            int dte = (_algo.IVSurfaceRelativeStrikeAsk[Underlying].MinExpiry() - _algo.Time.Date).Days;
            double currentAtm = _algo.PfRisk.AtmIV(Underlying);
            double longTermAtm = _algo.AtmIVIndicators[Underlying].Current;
            double impliedMove = currentAtm * Math.Sqrt(dte) / Math.Sqrt(365);  
            
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
