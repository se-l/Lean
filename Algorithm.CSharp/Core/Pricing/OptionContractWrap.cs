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

        private readonly Foundations _algo;
        private static readonly Dictionary<string, OptionContractWrap> instances = new();
        private static readonly object lockObject = new();

        private readonly DayCounter dayCounter;
        private readonly Calendar calendar;        
        private readonly Date calculationDate;
        private readonly Date settlementDate;
        private readonly Date maturityDate;
        private readonly double strikePrice;
        private readonly Option.Type optionType;
        private SimpleQuote spotQuote;
        private SimpleQuote riskFreeRateQuote;
        private Handle<Quote> riskFeeRateQuoteHandle;
        private SimpleQuote hvQuote;
        private Handle<Quote> hvQuoteHandle;
        private PlainVanillaPayoff payoff;
        private AmericanExercise amExercise;
        private EuropeanExercise euExercise;
        private VanillaOption amOption;
        private VanillaOption euOption;
        private BlackScholesMertonProcess bsmProcess;
        private BlackScholesProcess bsProcess;
        private double riskFreeRate = 0.0;  // IV; 0.0433
        private List<Date> dividendExDates;
        private List<double> dividendAmounts;

        /// <summary>
        /// Cached constructor
        /// </summary>
        private static Func<DateTime, Securities.Option.Option, string> genCacheKey = (date, contract) => $"{contract.Symbol}{date}";

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

            riskFreeRateQuote = new SimpleQuote(riskFreeRate);
            riskFeeRateQuoteHandle = new Handle<Quote>(riskFreeRateQuote);

            payoff = new PlainVanillaPayoff(optionType, strikePrice);
            amExercise = new AmericanExercise(settlementDate, maturityDate);
            euExercise = new EuropeanExercise(maturityDate);

            //dividendExDates = new List<Date>() { { new Date(13, 12, 2022) }, { new Date(16, 3, 2023) }, { new Date(14, 6, 2023) }, maturityDate };
            //dividendAmounts = new List<double>() { { 0.12 }, { 0.12 }, { 0.12 }, { 0.0 } };
            //dividendExDates = new List<Date>() { { new Date(13, 12, 2022) }, { calculationDate }, maturityDate, { new Date(16, 3, 2024) } };
            //dividendAmounts = new List<double>() { { 0.1 }, { 0.1 }, { 0.1 }, { 0.1 } };
            dividendExDates = new List<Date>() { };
            dividendAmounts = new List<double>() { };
            Settings.setEvaluationDate(algo.Time.Date);

            bsmProcess = GetBSMP(calculationDate, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), riskFeeRateQuoteHandle, new SimpleQuote(0));
            bsProcess = GetBSP(calculationDate, new Handle<Quote>(spotQuote), hvQuoteHandle, riskFeeRateQuoteHandle);
            amOption = SetEngine(new VanillaOption(payoff, amExercise), bsmProcess, optionPricingModel: OptionPricingModel.CoxRossRubinstein);
            euOption = SetEngine(new VanillaOption(payoff, euExercise), bsmProcess, optionPricingModel: OptionPricingModel.AnalyticEuropeanEngine);
            //amOption = SetEngine(new DividendVanillaOption(payoff, amExercise, dividendExDates, dividendAmounts), bsProcess, optionPricingModel: OptionPricingModel.FdBlackScholesVanillaEngine);
            //euOption = SetEngine(new DividendVanillaOption(payoff, euExercise, dividendExDates, dividendAmounts), bsProcess, optionPricingModel: OptionPricingModel.FdBlackScholesVanillaEngine);
        }

        public static OptionContractWrap E(Foundations algo, Securities.Option.Option contract, DateTime calculationDate)
        {
            string singletonKey = genCacheKey(calculationDate, contract);
            if (!instances.ContainsKey(singletonKey))
            {
                lock (lockObject)
                {
                    instances[singletonKey] = new OptionContractWrap(algo, contract, calculationDate);
                }
            }
            return instances[singletonKey];
        }

        public void SetIndependents(decimal? spotUnderlyingPrice = null, decimal? spotPrice = null, double? volatility = null)
        {
            //Settings.setEvaluationDate(algo.Time.Date);
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

        public void SetEvaluationDateToCalcDate()
        {
            // There is considerable performance overhead on setEvaluationDate raising some event within QLNet, therefore only calling if necessary.
            if (Settings.evaluationDate() != calculationDate)
            {
                //_algo.Log($"{_algo.Time} OptionContractWrap.SetEvaluationDateToCalcDate: Symbol={Contract.Symbol}, Settings.evaluationDate()={Settings.evaluationDate()}, calculationDate={calculationDate}");
                //_algo.Log(Environment.StackTrace);
                Settings.setEvaluationDate(calculationDate);
            }
        }

        public double GetIVEngine(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.001)
        {
            SetEvaluationDateToCalcDate();
            // The results here between exported IV and Algorighm are inconsistent. Algorithm seems too extreme in both upper and lower region. Something's off. Debug
            double _spotPriceContract = (double)(spotPriceContract ?? _algo.MidPrice(Contract.Symbol));
            SetSpotQuotePriceUnderlying(spotPriceUnderlying);
            //if (IntrinsicValue(spotPriceUnderlying) >= spotPriceContract) 
            //{ 
            //    return 0; 
            //}
            try
            {
                return euOption.impliedVolatility(_spotPriceContract, bsmProcess, accuracy: accuracy);
            }
            catch (Exception e)
            {
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
                    double vega = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "NPV");

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
            
        public double Delta(double? volatility = null)
        {
            double delta;
            SetEvaluationDateToCalcDate();
            SanityCheck(volatility);

            var hv = hvQuote.value();

            if (volatility != null && volatility != 0)  // For calculating ATM Implied Greeks
            {
                SetHistoricalVolatility((double)volatility);
            }

            try
            {
                delta = DeltaCached(hvQuote.value(), spotQuote.value());  // amOption.delta()
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Delta. {Contract.Symbol} volatilityArg={volatility}, hvQuote={hvQuote.value()}, spotQuote={spotQuote.value()} Attempting FD. {e}");
                delta = FiniteDifferenceApprox(spotQuote, amOption, 0.01, "NPV");
            }
            SetHistoricalVolatility(hv);
            return delta;
        }

        /// <summary>
        /// Vega component of Minimum Variance Delta
        /// </summary>
        public double MVVega()
        {
            SetEvaluationDateToCalcDate();
            //Settings.setEvaluationDate(algo.Time.Date);
            //https://www.researchgate.net/publication/226498536
            //https://drive.google.com/drive/folders/10g-QYf17V5pEQEJ5aeNu4RGbtm4tJse3
            // The slope of the curve of IV vs strike price. In paper about 0.05 +/- 0.01
            return Vega() * IVdS();
        }

        public double KappaZM(double sigma)
        {
            double ttm = TimeToMaturity();
            
            return 4.76 * Math.Pow(_algo.Cfg.ZMProportionalTransactionCost, 0.78) / 
                Math.Pow(ttm, 0.02) * 
                Math.Pow(Math.Exp(-riskFreeRate * ttm) / sigma, 0.25) * 
                Math.Pow(_algo.Cfg.ZMRiskAversion * Math.Pow((double)_algo.MidPrice(UnderlyingSymbol), 2) * Math.Abs(Gamma()), 0.15);
        }

        public double DeltaZM(int direction)
        {
            SetEvaluationDateToCalcDate();
            var hv0 = hvQuote.value();
            // ZM -Zakamulin
            hvQuote.setValue(VolatilityZM(direction));
            double delta = Delta();
            hvQuote.setValue(hv0);
            return delta; // + MVVega();
        }

        public double VolatilityZM(int direction)
        {
            double hv0;
            SetEvaluationDateToCalcDate();
            hv0 = (double)HistoricalVolatility();
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
                Math.Pow(Math.Exp(-riskFreeRate * TimeToMaturity()) / volatilityZM, 0.25) * 
                Math.Pow((Math.Abs(Gamma()) / _algo.Cfg.ZMRiskAversion), 0.5);
        }

        public double DeltaZMOffset(int direction)
        {
            double volatilityZM = VolatilityZM(direction);
            return H0ZM(volatilityZM) + HwZM(volatilityZM);
        }
        public double Gamma(double? volatility = null)
        {
            double gamma;
            SetEvaluationDateToCalcDate();
            SanityCheck(volatility);

            double hv0 = hvQuote.value();
            if (volatility != null)
            {
                SetHistoricalVolatility((double)volatility); // For calculating at, eg, ATM IV.
            }
            try
            {
                gamma = GammaCached(hvQuote.value(), spotQuote.value()); // amOption.gamma();
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Gamma. HV: {hvQuote.value()} Attempting FD {e}");
                gamma = FDApprox2ndDerivative(spotQuote, amOption, 0.01, "NPV");
            }
            SetHistoricalVolatility(hv0);
            return gamma;
        }
        public double DeltaDecay()  // Charm
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "delta");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "delta");
        }

        public double Theta(VanillaOption? option = null)
        {
            SetEvaluationDateToCalcDate();
            try
            {
                return (option ?? euOption).thetaPerDay();  // Different by 0.1 % from FD approach only. Likely much faster though. thetaPerDay() returns neg. values.
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Theta. Attempting FD. {e}");
                return FiniteDifferenceApproxTime(calculationDate, (option ?? amOption), "NPV", 1, "forward");
            }
        }

        public double ThetaTillExpiry(VanillaOption? option = null)
        {
            SetEvaluationDateToCalcDate();
            try
            {
                return (option ?? euOption).theta();
            }
            catch (Exception e)
            {
                _algo.Error($"OptionContractWrap.Theta. Returning 0. {e}");
                return 0;
            }
        }

        public void SanityCheck(double? volatility = null)
        {
            if (hvQuote.value() == 0 && volatility == null)
            {
                _algo.Error($"OptionContractWrap: Volatility set to 0. Resetting to HV.\n{Environment.StackTrace}");
                SetHistoricalVolatility();
            }
            else if (volatility == 0)
            {
                _algo.Error($"OptionContractWrap: Received 0 volatility. Potentially for implied calcs. {Contract}. Resetting to HV.\n{Environment.StackTrace}");
                SetHistoricalVolatility();
            }
        }
        public double DS3()  // Speed
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.05, "gamma");
        }
        public double Speed() => DS3();
        public double GammaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "gamma");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "gamma");
        }
        public double DS2dIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "gamma");
        }

        public double Vega()  // dIV - cache me
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "NPV");
        }    

        public double ThetaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, euOption, "thetaPerDay");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "thetaPerDay");
        }
        public double VegaDecay()  // Veta
       
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "vega");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "vega") / 100;
        }
        public double Rho()

        {
            return FiniteDifferenceApprox(riskFreeRateQuote, amOption, 0.01, "NPV");
        }

        public double DSdIV()  // Vanna, same as dSdIV
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "delta");
        }
        public double Vanna() => DSdIV();
        public double DIV2()  // Vomma / Volga
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.05, "vega", d1perturbance: hvQuote);
        }
        public double Volga() => DIV2();

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
        public double IVdS()  // How much IV changes with underlying price. That's not a BSM greek, not differentiating with respect to option price.
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.01, "IV");
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

        public double? Invoke(VanillaOption option, string invoke="NPV")
        {
            try
            {
                var methodInfo = option.GetType().GetMethod(invoke);
                return (double)methodInfo.Invoke(option, new object[] { });
            }
            catch (Exception e)
            {
                _algo.Error(e);
                return null;
            }
        }

        public double FiniteDifferenceApprox(SimpleQuote quote, VanillaOption option, double d_pct = 0.01, string derive = "NPV", SimpleQuote d1perturbance = null, string method = "central")
        {
            // f'(x) ≈ (f(x+h) - f(x-h)) / (2h); h: step size;
            double result;
            double? pPlus;
            double? pMinus;

            SetEvaluationDateToCalcDate();
            var q0 = quote.value();
            PerturbQuote(quote, q0, d_pct);
            
            if (derive == "vega" && d1perturbance != null)
            {
                pPlus = FiniteDifferenceApprox(d1perturbance, option, d_pct);
            }
            else if (derive == "thetaPerDay")
            {
                //pPlus = amOption.thetaPerDay();
                pPlus = FiniteDifferenceApproxTime(calculationDate, amOption, "NPV", 1, "forward");
                //pPlus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pPlus = NPV(option);
            }
            else if (derive == "IV")
            {
                // doesnt make much sense to perturb the underlying, and not perturb the spotOption but at least same absolute amount / parity.
                double deltaParitySpotContract = quote.value() - q0;
                pPlus = GetIVEngine(spotPriceContract: (decimal)(spotQuote.value() + deltaParitySpotContract), spotPriceUnderlying: (decimal)quote.value());
            }
            else
            {
                pPlus = Invoke(option, derive);
            }

            PerturbQuote(quote, q0, -d_pct);

            if (derive == "vega" && d1perturbance != null)
            {
                pMinus = FiniteDifferenceApprox(d1perturbance, option, d_pct);
            }
            else if (derive == "thetaPerDay")
            {
                // Works for Vinalla not Dividend
                pMinus = FiniteDifferenceApproxTime(calculationDate, amOption, "NPV", 1, "forward");
                //pMinus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pMinus = NPV(option);
            }
            else if (derive == "IV")
            {
                double deltaParitySpotContract = quote.value() - q0;
                pMinus = GetIVEngine(spotPriceContract: (decimal)(spotQuote.value() + deltaParitySpotContract), spotPriceUnderlying: (decimal)quote.value());
            }
            else
            {
                pMinus = Invoke(option, derive);
            }
            // Reset to starting value
            quote.setValue(q0);

            if (method == "central" && pPlus != null && pMinus != null)
            {
                // placeholder for left / right
                var stepSize = q0 == 0 ? 2 * d_pct : 2 * q0 * d_pct;
                result = ((double)pPlus - (double)pMinus) / stepSize;
            } 
            else if (pPlus != null || method=="forward")
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
            else if (pMinus != null || method=="backward")
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

        public double FiniteDifferenceApproxTime(Date calculationDate, VanillaOption option, string derive = "NPV", int nDays = 1, string method = "forward")
        {
            var values = new List<double>();

            if (calculationDate >= maturityDate)
            {
                _algo.Log($"Option {Contract} matured. Find a way to avoid running calculations on this. Returns Greek value: 0.");
                return 0;
            }

            foreach (var dt in new List<Date> { calculationDate, calendar.advance(calculationDate, nDays, TimeUnit.Days) })
            {
                if (dt >= maturityDate)
                {
                    values.Add(0);
                    continue;
                }
                Settings.setEvaluationDate(dt);
                // Fix moving this by business date. Dont divide by Sat / Sun. Use the USA calendar ideally.

                //if (derive == "thetaPerDay")
                //{
                //    var euExercise = new EuropeanExercise(maturityDate);
                //    optionDt = SetEngine(new VanillaOption(payoff, euExercise), bsmProcess, optionPricingModel: OptionPricingModel.AnalyticEuropeanEngine);
                //}
                //else
                //{
                //    var amExercise = new AmericanExercise(dt, maturityDate);
                //    optionDt = SetEngine(new VanillaOption(payoff, amExercise), bsmProcess, optionPricingModel: OptionPricingModel.CoxRossRubinstein);
                //}

                if (derive == "vega")
                {
                    values.Add(FiniteDifferenceApprox(hvQuote, option, 0.01)); // VEGA
                }
                else if (derive == "thetaPerDay")  // Here only used to measure thetaPerDay time decay
                {
                    values.Add(Theta(option));
                    //values.Add(FiniteDifferenceApproxTime(dt, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "theta", 1, "forward"));
                }
                else if (derive == "NPV")
                {
                    values.Add(option.NPV());
                }
                else
                {
                    var methodInfo = option.GetType().GetMethod(derive);
                    values.Add((double)methodInfo.Invoke(option, new object[] { }));
                }
            }
            SetEvaluationDateToCalcDate();
            return (values[values.Count - 1] - values[0]) / nDays;
        }

        //public double FiniteDifferenceApproxTime(Date calculationDate, Option.Type optionType, double strikePrice, SimpleQuote spotQuote, SimpleQuote hvQuote, SimpleQuote rfQuote, string derive = "NPV", int nDays = 1, string method = "forward")
        //{
        //    VanillaOption optionDt;
        //    var values = new List<double>();
        //    //calendar.businessDaysBetween(calculationDate, maturityDate);
        //    if (calculationDate >= maturityDate)
        //    {
        //        algo.Log($"Option {Contract} matured. Find a way to avoid running calculations on this. Returns Greek value: 0.");
        //        return 0;
        //    }

        //    foreach (var dt in new List<Date> { calculationDate, calendar.advance(calculationDate, nDays, TimeUnit.Days) })
        //    {
        //        if (dt >= maturityDate)
        //        {
        //            values.Add(0);
        //            continue;
        //        }
        //        Settings.setEvaluationDate(dt);
        //        // Fix moving this by business date. Dont divide by Sat / Sun. Use the USA calendar ideally.
        //        var payoff = new PlainVanillaPayoff(optionType, strikePrice);
        //        var bsmProcess = GetBSMP(dt, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), new Handle<Quote>(rfQuote), new SimpleQuote(0));

        //        if (derive == "thetaPerDay")
        //        {
        //            var euExercise = new EuropeanExercise(maturityDate);
        //            optionDt = SetEngine(new VanillaOption(payoff, euExercise), bsmProcess, optionPricingModel: OptionPricingModel.AnalyticEuropeanEngine);
        //        }
        //        else
        //        {
        //            var amExercise = new AmericanExercise(dt, maturityDate);
        //            optionDt = SetEngine(new VanillaOption(payoff, amExercise), bsmProcess, optionPricingModel: OptionPricingModel.CoxRossRubinstein);
        //        }

        //        if (derive == "vega")
        //        {
        //            values.Add(FiniteDifferenceApprox(hvQuote, optionDt, 0.01)); // VEGA
        //        }
        //        else if (derive == "thetaPerDay")  // Here only used to measure thetaPerDay time decay
        //        {
        //            values.Add(optionDt.thetaPerDay());
        //            //values.Add(FiniteDifferenceApproxTime(dt, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "theta", 1, "forward"));
        //        }
        //        else if (derive == "NPV")
        //        {
        //            values.Add(optionDt.NPV());
        //        }
        //        //else if (derive == "theta")
        //        //{
        //        //    if (dt >= maturityDate)
        //        //    {
        //        //        values.Add(0);
        //        //    }
        //        //    else
        //        //    {
        //        //        values.Add(optionDt.theta());
        //        //    }
        //        //}
        //        else
        //        {
        //            var methodInfo = optionDt.GetType().GetMethod(derive);
        //            values.Add((double)methodInfo.Invoke(optionDt, new object[] { }));
        //        }
        //    }
        //    SetEvaluationDateToCalcDate();
        //    return (values[values.Count - 1] - values[0]) / nDays;
        //}

        public decimal ExtrinsicValue()
        {
            return _algo.MidPrice(Contract.Symbol) - Contract.GetPayOff(_algo.MidPrice(UnderlyingSymbol));
        }
    }
}
