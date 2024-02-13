using QLNet;
using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public enum OptionPricingModel
    {
        CoxRossRubinstein,
        AnalyticEuropeanEngine,
        FdBlackScholesVanillaEngine,
    }
    public class OptionContractWrap
    {
        ///<summary>
        /// Singleton class for caching contract attributes and calculating Greeks
        /// </summary>
        ///
        public Securities.Option.Option Contract { get; }
        public Symbol UnderlyingSymbol { get; }
        public Func<decimal?, decimal?, double, double> IV;
        public Func<double, double, double> DeltaCached;
        public Func<double, double, double> GammaCached;
        public Func<double, double, double> VegaCached;
        //public Func<SimpleQuote, double, double> VegaCached;

        private readonly Foundations _algo;
        private static readonly Dictionary<(Symbol, DateTime), OptionContractWrap> instances = new();

        private readonly DayCounter dayCounter;
        private readonly Calendar calendar;        
        private readonly Date calculationDate;
        private readonly Date settlementDate;
        private readonly Date maturityDate;
        private readonly double strikePrice;
        private readonly Option.Type optionType;
        private SimpleQuote spotQuote;
        private SimpleQuote riskFreeRateQuote;
        private Handle<Quote> riskFreeRateQuoteHandle;
        private SimpleQuote dividendYieldQuote;
        private Handle<Quote> dividendYieldQuoteHandle;
        private SimpleQuote hvQuote;
        private Handle<Quote> hvQuoteHandle;
        private PlainVanillaPayoff payoff;
        private AmericanExercise amExercise;
        private EuropeanExercise euExercise;
        private VanillaOption amOption;
        private VanillaOption euOption;
        private BlackScholesMertonProcess bsmProcess;
        
        //private BlackScholesProcess bsProcess;
        //private List<Date> dividendExDates;
        //private List<double> dividendAmounts;

        //private static Func<DateTime, Securities.Option.Option, (Symbol, DateTime)> genCacheKey = (date, contract) => (contract.Symbol, date);

        private (decimal, decimal, double) GenCacheKeyIV(decimal? spotPriceContract, decimal? spotPriceUnderlying, double accuracy= 0.001)
        {
            return (
                spotPriceContract ?? _algo.MidPrice(Contract.Symbol),
                spotPriceUnderlying ?? _algo.MidPrice(UnderlyingSymbol),
                accuracy
                );
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="contract"></param>
        /// <param name="algo"></param>
        private OptionContractWrap(Foundations algo, Securities.Option.Option contract, DateTime calculationDate)
        {
            _algo = algo;
            //algo.Log($"{algo.Time}: OptionContractWrap.constructor called. {contract.Symbol}");
            Contract = contract;
            UnderlyingSymbol = contract.Underlying.Symbol;
            IV = Cache<(decimal, decimal, double), decimal?, decimal?, double, double>(GetIVEngine, GenCacheKeyIV);  // Using fast Analytical BSM for IV
            DeltaCached = Cache((double hvQuote, double spotQuote) => amOption.delta(), (double hvQuote, double spotQuote) => (hvQuote, spotQuote));
            GammaCached = Cache((double hvQuote, double spotQuote) => amOption.gamma(), (double hvQuote, double spotQuote) => (hvQuote, spotQuote));
            VegaCached = Cache((double hvQuote, double spotQuote) => euOption.vega(), (double hvQuote, double spotQuote) => (hvQuote, spotQuote));
            //VegaCached = Cache((SimpleQuote hvQuote, double spotQuote) => FiniteDifferenceApprox(hvQuote, amOption, 0.01, Derive.NPV), (SimpleQuote hvQuote, double spotQuote) => (hvQuote.value(), spotQuote));

            calendar = new UnitedStates(UnitedStates.Market.NYSE);
            //dayCounter = new Business252(calendar); // extremely slow
            dayCounter = new Actual365Fixed();
            maturityDate = new Date(contract.Expiry.Day, contract.Expiry.Month, contract.Expiry.Year);
            this.calculationDate = calculationDate;
            this.calculationDate = Date.Min(this.calculationDate, maturityDate);///////////////////////
            settlementDate = this.calculationDate;
            strikePrice = (double)contract.StrikePrice;
            optionType = contract.Right == OptionRight.Call ? Option.Type.Call : Option.Type.Put;

            SetSpotQuotePriceUnderlying();
            SetHistoricalVolatility();
            hvQuoteHandle = new Handle<Quote>(hvQuote);

            riskFreeRateQuote = new SimpleQuote((double)_algo.Cfg.DiscountRateMarket);
            riskFreeRateQuoteHandle = new Handle<Quote>(riskFreeRateQuote);

            double dividendYield = _algo.Cfg.DividendYield.TryGetValue(UnderlyingSymbol.Value, out dividendYield) ? dividendYield : _algo.Cfg.DividendYield[CfgDefault];
            dividendYieldQuote = new SimpleQuote(dividendYield);
            dividendYieldQuoteHandle = new Handle<Quote>(dividendYieldQuote);

            payoff = new PlainVanillaPayoff(optionType, strikePrice);
            amExercise = new AmericanExercise(settlementDate, maturityDate);
            euExercise = new EuropeanExercise(maturityDate);

            //dividendExDates = new List<Date>() { { new Date(13, 12, 2022) }, { new Date(16, 3, 2023) }, { new Date(14, 6, 2023) }, maturityDate };
            //dividendAmounts = new List<double>() { { 0.12 }, { 0.12 }, { 0.12 }, { 0.0 } };
            //dividendExDates = new List<Date>() { { new Date(13, 12, 2022) }, { calculationDate }, maturityDate, { new Date(16, 3, 2024) } };
            //dividendAmounts = new List<double>() { { 0.1 }, { 0.1 }, { 0.1 }, { 0.1 } };
            //dividendExDates = new List<Date>() { };
            //dividendAmounts = new List<double>() { };
            Settings.setEvaluationDate(algo.Time.Date);

            bsmProcess = GetBSMP(calculationDate, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), riskFreeRateQuoteHandle, dividendYieldQuoteHandle);            
            amOption = SetEngine(new VanillaOption(payoff, amExercise), bsmProcess, optionPricingModel: OptionPricingModel.CoxRossRubinstein);
            euOption = SetEngine(new VanillaOption(payoff, euExercise), bsmProcess, optionPricingModel: OptionPricingModel.AnalyticEuropeanEngine);
            //bsProcess = GetBSP(calculationDate, new Handle<Quote>(spotQuote), hvQuoteHandle, riskFeeRateQuoteHandle);
            //amOption = SetEngine(new DividendVanillaOption(payoff, amExercise, dividendExDates, dividendAmounts), bsProcess, optionPricingModel: OptionPricingModel.FdBlackScholesVanillaEngine);
            //euOption = SetEngine(new DividendVanillaOption(payoff, euExercise, dividendExDates, dividendAmounts), bsProcess, optionPricingModel: OptionPricingModel.FdBlackScholesVanillaEngine);
        }

        public static OptionContractWrap E(Foundations algo, Securities.Option.Option contract, DateTime calculationDate)
        {
            DateTime _calculationDate = calculationDate == contract.Expiry ? calculationDate - new TimeSpan(1, 0, 0, 0) : calculationDate;
            (Symbol, DateTime) singletonKey = (contract.Symbol, _calculationDate);
            if (!instances.ContainsKey(singletonKey))
            {
                if (_calculationDate != calculationDate)
                {
                    algo.Log($"OptionContractWrap.E: Overrode calculationDate {calculationDate} with {_calculationDate} to get non-zero greeks on expiration date.");
                }
                lock (instances)
                {
                    instances[singletonKey] = new OptionContractWrap(algo, contract, _calculationDate);
                }
            }
            return instances[singletonKey];
        }

        public static int ClearCache(DateTime upToDate)
        {
            int count = 0;
            lock (instances)
            {
                foreach (var key in instances.Keys)
                {
                    if (key.Item2 < upToDate)
                    {
                        instances.Remove(key);
                        count ++;
                    }
                }
            }
            return count;
        }

        public void SetIndependents(decimal? spotUnderlyingPrice = null, decimal? spotPrice = null, double? volatility = null)
        {
            // Underlying Price
            if (spotUnderlyingPrice != null)
            {
                SetSpotQuotePriceUnderlying(spotUnderlyingPrice);
            }

            // Volatility
            if (volatility != null)
            {
                SetHistoricalVolatility(volatility);
            }
            else if (spotPrice != null)
            {
                //var iv = null;  // not converging currently GetIVNewtonRaphson(spotPrice, spotUnderlyingPrice ?? algo.MidPrice(UnderlyingSymbol), 0.001);  // Using Newton-Raphson for IV (slow)
                double iv = IV(spotPrice, spotUnderlyingPrice, 0.001);  // If NR didnt converge ( null ), use Analytical BSM for IV (fast)
                SetHistoricalVolatility(iv);
            }            
            else
            {
                SetHistoricalVolatility();
            }
            SetEvaluationDateToCalcDate();
        }

        private void SetSpotQuotePriceUnderlying(decimal? spotPrice=null)
        {
            var quote = spotPrice ?? _algo.MidPrice(UnderlyingSymbol);            
            spotQuote ??= new SimpleQuote((double)quote);
            if ((double)spotQuote.value() != (double)quote)
            {
                spotQuote.setValue((double)quote);
            }
        }

        public decimal HistoricalVolatility()
        {
            return _algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
        }

        private void SetHistoricalVolatility(double? hv = null)
        {
            hv = hv == null || hv == 0 ? (double)HistoricalVolatility() : hv;
            hvQuote ??= new SimpleQuote(hv);
            if (hvQuote.value() != hv)
            {
                hvQuote.setValue(hv);
            }
            //hvQuote.setValue(hv);
            //algo.Log($"{algo.Time} {Contract.Symbol} HV: {algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility}");
        }

        public double? AnalyticalIVToPrice(double volatility, decimal? spotPrice = null)
        {
            SetEvaluationDateToCalcDate();
            if (volatility == 0 || volatility == null)
            {
                _algo.Error($"OptionContractWrap.AnalyticalIVToPrice: Invalid argument volatility={volatility}. Returning null.");
                return null;
            }
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(volatility);
            try
            {
                return euOption.NPV();
            }
            catch (Exception e)
            {
                _algo.Error($"AnalyticalIVToPrice: Unable to derive Fair price {e}. Likely due to low hvQuote. hvQuote={hvQuote.value()}, spotQuote={spotQuote.value()}. Args: volatility={volatility}, spotPrice={spotPrice}.");
                _algo.Log(Environment.StackTrace);
                return null;
            }
        }

        public void SetEvaluationDateToCalcDate(Date? evaluationDate = null)
        {
            // There is considerable performance overhead on setEvaluationDate raising some event within QLNet, therefore only calling if necessary.
            if (Settings.evaluationDate() != (evaluationDate ?? calculationDate))
            {
                Settings.setEvaluationDate(evaluationDate ?? calculationDate);
            }
        }

        private double GetIVEngine(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.001)
        {
            SetEvaluationDateToCalcDate();
            // The results here between exported IV and Algorighm are inconsistent. Algorithm seems too extreme in both upper and lower region. Something's off. Debug
            double _spotPriceContract = (double)(spotPriceContract ?? _algo.MidPrice(Contract.Symbol));
            SetSpotQuotePriceUnderlying(spotPriceUnderlying);

            try
            {
                return euOption.impliedVolatility(_spotPriceContract, bsmProcess, accuracy: accuracy);
            }
            catch (Exception e)
            {
                // Happens a lot for very low bid prices
                //algo.Log($"OptionContractWrap.GetIVEngine {Contract} No IV {e}. _spotPriceContract {_spotPriceContract} spotPriceUnderlying: {spotQuote.value()} Intrinsic {IntrinsicValue(spotPriceUnderlying)} Most likely spotPrice is too low: {spotPriceContract}." +
                //    $"Consider using lastIV, historical IV, ATM IV, skipping calc, 0 IV, closest non-0 IV, opposite IV - lastSpread");
                return 0;
            }
        }

        //public double GuessInitialImpliedVolatility(decimal? spotPriceContract = null)
        //{
        //    /// By default, it uses the formula from Brenner and Subrahmanyam (1988) as the initial guess for implied volatility. PS2πT−−−√
        //    /// where P is the Option contract price, S is the underlying price, and T is the time until Option expiration.
        //    double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
        //    return (_spotPriceContract / strikePrice) * Math.Sqrt(2*Math.PI / (double)Contract.TimeUntilExpiry.TotalDays);
        //}

        public double? GetIVNewtonRaphson(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.001)
        {
            int maxIterations = 200;
            double _spotPriceContract = (double)(spotPriceContract ?? _algo.MidPrice(Contract.Symbol));
            double spotUnderlyingQuote0 = spotQuote.value();
            double hvQuote0 = hvQuote.value();
            spotQuote.setValue((double)(spotPriceUnderlying ?? _algo.MidPrice(Contract.Underlying.Symbol)));

            double initialGuess = 0.3; // Initial guess for the implied volatility
            double epsilon = accuracy;
            double currentGuess = initialGuess;
            int iteration = 0;

            while (iteration < maxIterations)
            {
                try
                {
                    hvQuote.setValue(Math.Min(Math.Max(currentGuess, 0), 4));
                    double optionPrice = amOption.NPV();
                    double vega = FiniteDifferenceApprox(hvQuote, amOption, 0.01, Derive.NPV);

                    double difference = optionPrice - _spotPriceContract;

                    if (Math.Abs(difference) < epsilon)
                    {
                        return currentGuess;
                    }

                    currentGuess = currentGuess - difference / vega;
                    iteration++;
                }
                catch
                {
                    return null;
                }
            }
            // reset to original value
            spotQuote.setValue(spotUnderlyingQuote0);
            hvQuote.setValue(hvQuote0);

            // If the method did not converge to a solution, return null
            return null;
        }

        public bool IsITM()
        {
            return Contract.Right switch
            {
                OptionRight.Call => spotQuote.value() > strikePrice,
                OptionRight.Put => spotQuote.value() < strikePrice,
                _ => throw new NotImplementedException(),
            };
        }
            
        public double Delta(double volatility)
        {
            double delta;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);
            if (hvQuote.value() == 0) return 0;

            try
            {
                delta = DeltaCached(hvQuote.value(), spotQuote.value());
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Delta. {Contract.Symbol} volatilityArg={volatility}, hvQuote={hvQuote.value()}, spotQuote={spotQuote.value()} Attempting FD. {e}");
                try
                {
                    delta = FiniteDifferenceApprox(spotQuote, amOption, 0.01, Derive.NPV);
                }
                catch (Exception e2)
                {
                    _algo.Error($"OptionContractWrap.Delta.FiniteDifferenceApprox. Returno 0 delta. {Contract.Symbol} volatilityArg={volatility}, hvQuote={hvQuote.value()}, spotQuote={spotQuote.value()} Attempting FD. {e2}");
                    delta = 0;
                }                
            }

            SetHistoricalVolatility(hv0);
            return delta;
        }

        /// <summary>
        /// Vega component of Minimum Variance Delta
        /// </summary>
        public double MVVega(double iv)
        {
            SetEvaluationDateToCalcDate();
            //Settings.setEvaluationDate(algo.Time.Date);
            //https://www.researchgate.net/publication/226498536
            //https://drive.google.com/drive/folders/10g-QYf17V5pEQEJ5aeNu4RGbtm4tJse3
            // The slope of the curve of IV vs strike price. In paper about 0.05 +/- 0.01
            return Vega(iv) * IVdS(iv);
        }

        public double KappaZM(double volatility)
        {
            double ttm = TimeToMaturity();
            
            return 4.76 * Math.Pow(_algo.Cfg.ZMProportionalTransactionCost, 0.78) / 
                Math.Pow(ttm, 0.02) * 
                Math.Pow(Math.Exp(-riskFreeRateQuote.value() * ttm) / volatility, 0.25) * 
                Math.Pow(_algo.Cfg.ZMRiskAversion * Math.Pow((double)_algo.MidPrice(UnderlyingSymbol), 2) * Math.Abs(Gamma(volatility)), 0.15);
        }

        /// <summary>
        /// Zakamulin (ZM) Delta
        /// </summary>
        /// <param name="direction"></param>
        /// <returns></returns>
        public double DeltaZM(int direction)
        {
            SetEvaluationDateToCalcDate();
            return Delta(VolatilityZM(direction));
        }

        public double VolatilityZM(int direction)
        {
            SetEvaluationDateToCalcDate();
            double hv0 = (double)HistoricalVolatility();
            return Math.Pow(Math.Pow(hv0, 2) * (1.0 + KappaZM(hv0) * Math.Sign(direction)), 0.5);
        }

        public double H0ZM(double volatilityZM)
        {
            // not adjusted volatility. Implied, historical or forecasted.
            return _algo.Cfg.ZMProportionalTransactionCost / (_algo.Cfg.ZMRiskAversion * (double)_algo.MidPrice(UnderlyingSymbol) * Math.Pow(volatilityZM, 2) * TimeToMaturity());
        }

        public double HwZM(double volatilityZM)
        {
            return 1.12 * Math.Pow(_algo.Cfg.ZMProportionalTransactionCost, 0.31) * 
                Math.Pow(TimeToMaturity(), 0.05) * 
                Math.Pow(Math.Exp(-riskFreeRateQuote.value() * TimeToMaturity()) / volatilityZM, 0.25) * 
                Math.Pow((Math.Abs(Gamma(volatilityZM)) / _algo.Cfg.ZMRiskAversion), 0.5);
        }

        public double DeltaZMOffset(int direction)
        {
            double volatilityZM = VolatilityZM(direction);
            return H0ZM(volatilityZM) + HwZM(volatilityZM);
        }
        public double Gamma(double volatility)
        {
            double gamma;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);
            if (hvQuote.value() == 0) return 0;

            try
            {
                gamma = GammaCached(hvQuote.value(), spotQuote.value()); // amOption.gamma();
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Gamma. HV: {hvQuote.value()} Attempting FD {e}");
                try
                {
                    gamma = FDApprox2ndDerivative(spotQuote, amOption, 0.01, "NPV");
                }
                catch (Exception e2)
                {
                    _algo.Error($"OptionContractWrap.Gamma.FDApprox2ndDerivative. Returning gamma=0 HV: {hvQuote.value()} Attempting FD {e2}");
                    gamma = 0;
                }
            }

            SetHistoricalVolatility(hv0);
            return gamma;
        }
        public double DeltaDecay(double volatility)  // Charm
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);
            double deltaDecay = FiniteDifferenceApproxTime(Derive.delta);
            SetHistoricalVolatility(hv0);
            return deltaDecay;
        }

        public double Theta(double volatility)
        {
            double theta;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            try
            {
                theta = hvQuote.value() == 0 ? 0 : euOption.thetaPerDay();  // Different by 0.1 % from FD approach only. Likely much faster though. thetaPerDay() returns neg. values.
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Theta. Attempting FD. {e}");
                theta = FiniteDifferenceApproxTime(Derive.NPV, 1, Method.forward);
            }
            SetHistoricalVolatility(hv0);
            return theta;
        }

        public double ThetaTillExpiry(double volatility)
        {
            double thetaTillExpiry;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            try
            {
                thetaTillExpiry = hvQuote.value() == 0 ? 0 : euOption.theta();
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.ThetaTillExpiry. Returning 0. {e}");
                thetaTillExpiry = 0;
            }

            SetHistoricalVolatility(hv0);
            return thetaTillExpiry;
        }

        public void SetSanityCheckVol(double? volatility = null)
        {
            if (volatility != null)
            {
                SetHistoricalVolatility((double)volatility); // For calculating at, eg, ATM IV.
                return;
            }
            //if (volatility != null && volatility != 0)
            //{
            //    SetHistoricalVolatility((double)volatility); // For calculating at, eg, ATM IV.
            //    return;
            //}
            //else if (volatility == 0)
            //{
            //    SetHistoricalVolatility();
            //    // FIX ME. Need to find a better way to handle this.
            //    _algo.Error($"OptionContractWrap: Received 0 volatility. Potentially for implied calcs. {Contract}. Resetting to HV: {hvQuote.value()}.\n{Environment.StackTrace}");
            //    //_algo.Error($"OptionContractWrap: Received 0 volatility. Potentially for implied calcs. {Contract}. Defaulting to 0.01");  // Resetting to HV: {hvQuote.value()}.\n{Environment.StackTrace}");
            //}

            if (hvQuote.value() == 0)
            {
                //SetHistoricalVolatility();
                _algo.Error($"OptionContractWrap: HV is zero. Neither argument, not HV is sensible. Wrong calculations ahead. HV: {hvQuote.value()}.\n{Environment.StackTrace}");
            }
        }

        public double DS3(double volatility)  // Speed
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);
            double dS3 = FiniteDifferenceApprox(spotQuote, amOption, 0.05, Derive.gamma);
            SetHistoricalVolatility(hv0);
            return dS3;
        }
        public double Speed(double volatility) => DS3(volatility);
        public double GammaDecay(double volatility)
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            double gammaDecay = hvQuote.value() == 0 ? 0 : FiniteDifferenceApproxTime(Derive.gamma);
            SetHistoricalVolatility(hv0);
            return gammaDecay;
        }
        public double DS2dIV(double volatility)
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            var dS2dIV = hvQuote.value() == 0 ? 0 : FiniteDifferenceApprox(hvQuote, amOption, 0.01, Derive.gamma);
            SetHistoricalVolatility(hv0);
            return dS2dIV;
        }

        public double Vega(double volatility)
        {
            double vega;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            try
            {
                //_algo.Log($"OptionContractWrap.Vega: impliedVolatility={impliedVolatility}, hvQuote={hvQuote.value()}, hv={HistoricalVolatility()}");
                vega = hvQuote.value() == 0 ? 0 : VegaCached(hvQuote.value(), spotQuote.value());
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Vega.FDApproxDerivative. Returning vega=0 HV: {hvQuote.value()} {e}");
                vega = 0;
            }

            SetHistoricalVolatility();
            return vega;
        }    

        public double ThetaDecay(double volatility)
        {
            double thetaTillExpiry;
            SetEvaluationDateToCalcDate();
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            double thetaDecay = FiniteDifferenceApproxTime(Derive.thetaPerDay);

            SetHistoricalVolatility(hv0);
            return thetaDecay;
        }
        public double VegaDecay(double volatility)  // Veta       
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            var dVegadT = hvQuote.value() == 0 ? 0: FiniteDifferenceApproxTime(Derive.vega);
            SetHistoricalVolatility(hv0);
            return dVegadT;
        }
        public double Rho()

        {
            return FiniteDifferenceApprox(riskFreeRateQuote, amOption, 0.01, Derive.NPV);
        }

        public double DDeltadIV(double volatility)  // Vanna, same as dSdIV
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            var dDeltadIV = hvQuote.value() == 0 ? 0 : FiniteDifferenceApprox(hvQuote, amOption, 0.01, Derive.delta);

            SetHistoricalVolatility(hv0);
            return dDeltadIV;
        }
        public double Vanna(double volatility) => DDeltadIV(volatility);
        public double DIV2(double volatility)  // Vomma / Volga
        {
            // The nested FD calc returns strange values. Large values for dIV > 0, very small values for dIV < 0.Across a single trade Volga vary by a magnitude of 10_000!
            // Change from above issue: using euOption and QlNet's vega() method instead of FD.
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            var greek = FiniteDifferenceApprox(hvQuote, euOption, 0.01, Derive.vega, d1perturbance: hvQuote);

            SetHistoricalVolatility(hv0);
            return greek;
        }
        public double Volga(double volatility) => DIV2(volatility);

        public double NPV(bool? resetCalcDate = true)
        {
            if (resetCalcDate == true)
                SetEvaluationDateToCalcDate();
            try
            {
                return amOption.NPV();
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.NPV. {Contract.Symbol} resetCalcDateArg={resetCalcDate}, calculationDate={calculationDate}, hvQuote={hvQuote.value()}, spotQuote={spotQuote.value()}. {e}");
                return 0;
            }
        }

        /// <summary>
        /// Calendar Days
        /// </summary>
        public int DaysToExpiration()
        {
            return maturityDate - calculationDate;
            //return calendar.businessDaysBetween(calculationDate, maturityDate);
        }

        /// <summary>
        /// Calendar Days
        /// </summary>
        public int DaysToExpiration(DateTime dt)
        {
            return maturityDate - new Date(dt.Day, dt.Month, dt.Year);
            //return calendar.businessDaysBetween(new Date(dt.Day, dt.Month, dt.Year), maturityDate);
        }
        /// <summary>
        /// Looks wrong. Review against paper. Stuck to ACTUAL Days calculation elsewhere as stock impacting events can also happen on weekends...
        /// </summary>
        /// <returns></returns>
        public double TimeToMaturity()
        {
            return (maturityDate - calculationDate) / 252.0;
            //return calendar.businessDaysBetween(calculationDate, maturityDate) / 252.0;
        }
        public double IVdS(double volatility)  // How much IV changes with underlying price. That's not a BSM greek, not differentiating with respect to option price.
        {
            double hv0 = hvQuote.value();
            SetSanityCheckVol(volatility);

            var dIVdS = hvQuote.value() == 0 ? 0 : FiniteDifferenceApprox(spotQuote, amOption, 0.01, Derive.IV);

            SetHistoricalVolatility(hv0);
            return dIVdS;
        }

        public BlackScholesMertonProcess GetBSMP(Date calculationDate, Handle<Quote> spotQuote, Handle<Quote> hvQuote, Handle<Quote> rfQuote, Quote dividendRateQuote)
        {
            var flatTs = new Handle<YieldTermStructure>(new FlatForward(calculationDate, rfQuote, dayCounter));
            var dividendYield = new Handle<YieldTermStructure>(new FlatForward(calculationDate, dividendRateQuote, dayCounter));
            var flatVolTs = new Handle<BlackVolTermStructure>(new BlackConstantVol(calculationDate, calendar, hvQuote, dayCounter));
            return new BlackScholesMertonProcess(spotQuote, dividendYield, flatTs, flatVolTs);
        }

        public BlackScholesProcess GetBSP(Date calculationDate, Handle<Quote> spotQuote, Handle<Quote> hvQuote, Handle<Quote> rfQuote)
        {
            var flatTs = new Handle<YieldTermStructure>(new FlatForward(calculationDate, rfQuote, dayCounter));
            var flatVolTs = new Handle<BlackVolTermStructure>(new BlackConstantVol(calculationDate, calendar, hvQuote, dayCounter));
            return new BlackScholesProcess(spotQuote, flatTs, flatVolTs);
        }

        public VanillaOption SetEngine(VanillaOption option, BlackScholesMertonProcess bsmProcess, int steps = 100, OptionPricingModel optionPricingModel = OptionPricingModel.CoxRossRubinstein)
        {
            OneAssetOption.Engine engine = optionPricingModel switch
            {
                OptionPricingModel.CoxRossRubinstein => new BinomialVanillaEngine<CoxRossRubinstein>(bsmProcess, steps),
                OptionPricingModel.AnalyticEuropeanEngine => new AnalyticEuropeanEngine(bsmProcess),
                _ => throw new ArgumentException("OptionPricingModel not supported"),
            };
            // var engine = new BinomialVanillaEngine<CoxRossRubinstein>(bsmProcess, steps);  // 59%; vega not provided
            // var engine = new FDAmericanEngine(bsmProcess, steps, steps - 1);   // 87 %; theta not provided
            // var engine = new BjerksundStenslandApproximationEngine(bsmProcess);  // delta not provided
            //var engine = new AnalyticEuropeanEngine(bsmProcess);  // only european options. 0% not showing up as hotpath/bottleneck anymore...
            // var engine = new BjerksundStenslandApproximationEngine(bsmProcess);  // delta not provided
            option.setPricingEngine(engine);
            return option;
        }

        public DividendVanillaOption SetEngine(DividendVanillaOption option, BlackScholesProcess bsProcess, int steps = 100, OptionPricingModel optionPricingModel = OptionPricingModel.FdBlackScholesVanillaEngine)
        {
            DividendVanillaOption.Engine engine = optionPricingModel switch
            {
                OptionPricingModel.FdBlackScholesVanillaEngine => new FdBlackScholesVanillaEngine(bsProcess, null),
                _ => throw new ArgumentException("OptionPricingModel not supported"),
            };
            option.setPricingEngine(engine);
            return option;
        }
        
        public double FDApprox2ndDerivative(SimpleQuote quote, VanillaOption option, double d_pct = 0.01, string derive = "NPV")
        {
            // f''(x) ≈ (f(x+h) -2f(x) + f(x-h)) / (h**2); h: step size;
            SetEvaluationDateToCalcDate();

            double pZero = option.NPV();

            double q0 = quote.value();
            quote.setValue(q0 * (1 + d_pct));
            double pPlus = option.NPV();

            quote.setValue(q0 * (1 - d_pct));
            double pMinus = option.NPV();

            quote.setValue(q0);
            return (pPlus - 2 * pZero + pMinus) / Math.Pow(q0 * d_pct, 2);
        }

        public void PerturbQuote(SimpleQuote quote, double q0, double d_pct = 0.01)
        {
            if (q0 == 0)            
            {
                // Percentage perturbance will result in NaN / 0 division error, hence using absolute value
                quote.setValue(d_pct);
            }
            else
            {
                quote.setValue(q0 * (1 + d_pct));
            }
        }

        public double? NPV(VanillaOption option)
        {
            try
            {
                return option.NPV();
            }
            catch (Exception e)
            {
                _algo.Error(e);
                return null;
            }
        }

        public double? Invoke(VanillaOption option, Derive invoke=Derive.NPV)
        {
            try
            {
                return invoke switch
                {
                    Derive.delta => option.delta(),
                    Derive.gamma => option.gamma(),
                    Derive.vega => option.vega(),
                    Derive.NPV => option.NPV(),
                    _ => throw new NotImplementedException(),
                };
                //var methodInfo = option.GetType().GetMethod($"{invoke}");
                //return (double)methodInfo.Invoke(option, new object[] { });
            }
            catch (Exception e)
            {
                _algo.Error(e);
                return null;
            }
        }

        public enum Derive
        {
            NPV,
            vega,
            delta,
            gamma,
            IV,
            thetaPerDay
        }

        public enum Method
        {
            central,
            forward,
            backward
        }
            

        public double FiniteDifferenceApprox(SimpleQuote quote, VanillaOption option, double d_pct = 0.01, Derive derive = Derive.NPV, SimpleQuote d1perturbance = null, Method method = Method.central)
        {
            // f'(x) ≈ (f(x+h) - f(x-h)) / (2h); h: step size;
            double result;
            double? pPlus;
            double? pMinus;

            SetEvaluationDateToCalcDate();
            var q0 = quote.value();
            PerturbQuote(quote, q0, d_pct);

            pPlus = derive switch
            {
                Derive.thetaPerDay => FiniteDifferenceApproxTime(Derive.NPV, 1, Method.forward),
                Derive.NPV => NPV(option),
                // doesnt make much sense to perturb the underlying, and not perturb the spotOption but at least same absolute amount / parity.
                Derive.IV => GetIVEngine(spotPriceContract: (decimal)(spotQuote.value() + (quote.value() - q0)), spotPriceUnderlying: (decimal)quote.value()),
                _ => Invoke(option, derive),
            };

            PerturbQuote(quote, q0, -d_pct);

            pMinus = derive switch
            {
                Derive.thetaPerDay => FiniteDifferenceApproxTime(Derive.NPV, 1, Method.forward),
                Derive.NPV => NPV(option),
                Derive.IV => GetIVEngine(spotPriceContract: (decimal)(spotQuote.value() - (quote.value() - q0)), spotPriceUnderlying: (decimal)quote.value()),
                _ => Invoke(option, derive),
            };

            // Reset to starting value
            quote.setValue(q0);

            if (method == Method.central && pPlus != null && pMinus != null)
            {
                // placeholder for left / right
                var stepSize = q0 == 0 ? 2 * d_pct : 2 * q0 * d_pct;
                result = ((double)pPlus - (double)pMinus) / stepSize;
            } 
            else if (pPlus != null || method==Method.forward)
            {
                var p0 = Invoke(option, derive);
                if (p0 == null)
                {
                    _algo.Error($"FiniteDifferenceApprox.forward.Invoke failed. p0 is null. Returning 0. {option}, {derive}, {d_pct}, {method}");
                    return 0;
                }
                var stepSize = q0 == 0 ? d_pct : q0 * d_pct;
                result = ((double)pPlus - (double)p0) / stepSize;
            }
            else if (pMinus != null || method==Method.backward)
            {
                var p0 = Invoke(option, derive);
                if (p0 == null)
                {
                    _algo.Error($"FiniteDifferenceApprox.backward.Invoke failed. p0 is null. Returning 0. {option}, {derive}, {d_pct}, {method}");
                    return 0;
                }
                var stepSize = q0 == 0 ? d_pct : q0 * d_pct;
                result = ((double)p0 - (double)pMinus) / stepSize;
            }
            else
            {
                _algo.Error($"FiniteDifferenceApprox failed. pPlus {pPlus}, pMinus {pMinus}, method {method}");
                result = 0;
            }
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                _algo.Log($"FiniteDifferenceApprox. Warning: Result is NaN or Infinity. {result}. Defaulting to 0");
                result = 0;
            }
            return result;
        }

        public double FiniteDifferenceApproxTime(Derive derive = Derive.NPV, int nDays = 1, Method method = Method.forward)
        {
            double hv = hvQuote.value();
            try
            {
                var values = new List<double>();

                if (calculationDate > maturityDate)
                {
                    _algo.Log($"Option {Contract} matured. Find a way to avoid running calculations on this. Returns Greek value: 0.");
                    return 0;
                }

                foreach (var dt in new List<Date> { calculationDate, calendar.advance(calculationDate, nDays, TimeUnit.Days) })
                {
                    if (dt > maturityDate)
                    {
                        values.Add(0);
                        continue;
                    }
                    Settings.setEvaluationDate(dt);
                    OptionContractWrap ocw1 = E(_algo, Contract, dt);
                    ocw1.SetHistoricalVolatility(hv);

                    values.Add(derive switch
                    {
                        //Derive.vega => ocw1.FiniteDifferenceApprox(ocw1.hvQuote, ocw1.amOption, 0.01),
                        Derive.vega => ocw1.Vega(hv),
                        Derive.gamma => ocw1.Gamma(hv),
                        Derive.delta => ocw1.Delta(hv),
                        Derive.thetaPerDay => ocw1.Theta(hv),
                        Derive.NPV => ocw1.NPV(),
                        _ => throw new NotImplementedException($"{_algo.Time} OptionContractWrap.FiniteDifferenceApproxTime: Symbol={Contract.Symbol}, Settings.evaluationDate()={Settings.evaluationDate()}, calculationDate={calculationDate}, derive={derive}, nDays={nDays}, method={method}"),
                    });
            }
                SetEvaluationDateToCalcDate();
                SetHistoricalVolatility(hv);
                return (values[values.Count - 1] - values[0]) / nDays;
            }
            catch (Exception e)
            {
                _algo.Log(e.ToString());
                return 0;
            }
        }

        public decimal ExtrinsicValue()
        {
            return _algo.MidPrice(Contract.Symbol) - Contract.GetPayOff(_algo.MidPrice(UnderlyingSymbol));
        }
    }
}
