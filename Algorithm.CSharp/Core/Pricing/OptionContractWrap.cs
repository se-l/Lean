using QLNet;
using System;
using System.Collections;
using System.Collections.Generic;
using static Accord.Math.FourierTransform;
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
        public int Version { get; internal set; }
        public Symbol UnderlyingSymbol { get; }
        public Func<decimal?, decimal?, double, double> IV;

        private readonly Foundations algo;
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
        private double riskAversion = 1;
        private double proportionalTransactionCost = 0.001;  // review refine
        private List<Date> dividendExDates;
        private List<double> dividendAmounts;

        /// <summary>
        /// Cached constructor
        /// </summary>
        private static Func<DateTime, Securities.Option.Option, int, string> genCacheKey = (date, contract, version) => $"{contract.Symbol}{date}{version}";

        public Func<decimal?, double?, GreeksPlus> Greeks;

        private (decimal, decimal, double) GenCacheKeyIV(decimal? spotPriceContract, decimal? spotPriceUnderlying, double accuracy= 0.001)
        {
            return (
                spotPriceContract ?? algo.MidPrice(Contract.Symbol),
                spotPriceUnderlying ?? algo.MidPrice(UnderlyingSymbol),
                accuracy
                );
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="contract"></param>
        /// <param name="algo"></param>
        private OptionContractWrap(Foundations algo, Securities.Option.Option contract, int version = 1, DateTime? calculationDate = null)
        {
            this.algo = algo;
            Version = version;
            //algo.Log($"{algo.Time}: OptionContractWrap.constructor called. {contract.Symbol}");
            Contract = contract;
            UnderlyingSymbol = contract.Underlying.Symbol;
            IV = Cache<(decimal, decimal, double), decimal?, decimal?, double, double>(GetIVEngine, GenCacheKeyIV);  // Using fast Analytical BSM for IV

            Greeks = Cache(GreeksP,
                (decimal? spotPrice, double? hv) => {
                    SetSpotQuotePriceUnderlying(spotPrice);
                    SetHistoricalVolatility(hv);
                    return (spotQuote.value(), hvQuote.value());
                }
            );
            
            calendar = new UnitedStates(UnitedStates.Market.NYSE);
            //dayCounter = new Business252(calendar); // extremely slow
            dayCounter = new Actual365Fixed();
            maturityDate = new Date(contract.Expiry.Day, contract.Expiry.Month, contract.Expiry.Year);
            this.calculationDate = calculationDate ?? new Date(algo.Time.Day, algo.Time.Month, algo.Time.Year);
            this.calculationDate = Date.Min(this.calculationDate, maturityDate);
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

        public static OptionContractWrap E(Foundations algo, Securities.Option.Option contract, int version = 1, DateTime? calculationDate = null)
        {
            string singletonKey = genCacheKey(calculationDate ?? algo.Time.Date, contract, version);
            if (!instances.ContainsKey(singletonKey))
            {
                lock (lockObject)
                {
                    //instances[singletonKey] = constructorCached(algo, contract);
                    instances[singletonKey] = new OptionContractWrap(algo, contract, version, calculationDate ?? algo.Time.Date);
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
            var quote = spotPrice ?? algo.MidPrice(UnderlyingSymbol);            
            spotQuote ??= new SimpleQuote((double)quote);
            if ((double)spotQuote.value() != (double)quote)
            {
                spotQuote.setValue((double)quote);
            }
        }

        public decimal HistoricalVolatility()
        {
            return algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
        }

        private void SetHistoricalVolatility(double? hv = null)
        {
            hv ??= (double)HistoricalVolatility();
            hvQuote ??= new SimpleQuote(hv);
            if (hvQuote.value() != hv)
            {
                hvQuote.setValue(hv);
            }
            //algo.Log($"{algo.Time} {Contract.Symbol} HV: {algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility}");
        }

        public double? AnalyticalIVToPrice(decimal? spotPrice = null, double? hv = null)
        {
            SetEvaluationDateToCalcDate();
            if (hv == 0) { return null; }
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(hv);
            try
            {
                return euOption.NPV();
            }
            catch (Exception e)
            {
                algo.Error($"Unable to derive Fair price {e}. Most likely due to unreasonable volatiliy: {hvQuote.value()}.");
                return null;
            }
        }

        public void SetEvaluationDateToCalcDate()
        {
            // There is considerable performance overhead on setEvaluationDate raising some event within QLNet, therefore only calling if necessary.
            if (Settings.evaluationDate() != calculationDate)
            {
                Settings.setEvaluationDate(calculationDate);
            }
        }

        public double GetIVEngine(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.001)
        {
            SetEvaluationDateToCalcDate();
            // The results here between exported IV and Algorighm are inconsistent. Algorithm seems too extreme in both upper and lower region. Something's off. Debug
            double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
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
            double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
            double spotUnderlyingQuote0 = spotQuote.value();
            double hvQuote0 = hvQuote.value();
            spotQuote.setValue((double)(spotPriceUnderlying ?? algo.MidPrice(Contract.Underlying.Symbol)));

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

        public GreeksPlus GreeksP(decimal? spotPrice = null, double? hv = null)
        {
            // parameterize that change. Depending on metric, perturbance is different. 1-10BP for price, 100 BP for HV and rho, 1 day for time, 
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(hv);         
            return new GreeksPlus(this);
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
            
        public double Delta()
        {
            double delta;
            SetEvaluationDateToCalcDate();
            try
            {
                delta = amOption.delta();
            }
            catch (Exception e)
            {
                algo.Error($"OptionContractWrap.Delta. Attempting FD. {e}");
                delta = FiniteDifferenceApprox(spotQuote, amOption, 0.01, "NPV");
            }
            return delta;
        }

        public decimal Delta100Bp()
        {
            return (decimal)Delta() * algo.MidPrice(UnderlyingSymbol) * 100 * BP;
        }

        /// <summary>
        /// Minimum Variance Delta
        /// </summary>
        public double MVDelta()
        {
            SetEvaluationDateToCalcDate();
            //Settings.setEvaluationDate(algo.Time.Date);
            //https://www.researchgate.net/publication/226498536
            //https://drive.google.com/drive/folders/10g-QYf17V5pEQEJ5aeNu4RGbtm4tJse3
            // The slope of the curve of IV vs strike price. In paper about 0.05 +/- 0.01
            return Delta() + Vega() * DIVdP();
            
        }

        public double KappaZM(double sigma)
        {
            double ttm = TimeToMaturity();
            
            return 4.76 * Math.Pow(proportionalTransactionCost, 0.78) / 
                Math.Pow(ttm, 0.02) * 
                Math.Pow(Math.Exp(-riskFreeRate * ttm) / sigma, 0.25) * 
                Math.Pow(riskAversion * Math.Pow((double)algo.MidPrice(UnderlyingSymbol), 2) * Math.Abs(Gamma()), 0.15);
        }

        public double DeltaZM(int? direction)
        {
            SetEvaluationDateToCalcDate();
            if (direction == null)
            {
                throw new Exception("DeltaZM needs a direction");
            }
            var hv0 = hvQuote.value();
            // ZM -Zakamulin
            double sigma_mod = Math.Pow(Math.Pow(hv0, 2) * (1.0 + KappaZM(hv0) * Math.Sign(direction ?? 0)), 0.5);

            hvQuote.setValue(sigma_mod);
            double delta = Delta();
            hvQuote.setValue(hv0);
            return delta;
        }

        public double H0ZM()
        {
            // not adjusted volatility. Implied, historical or forecasted.
            return proportionalTransactionCost / (riskAversion * (double)algo.MidPrice(UnderlyingSymbol) * Math.Pow(hvQuote.value(), 2) * TimeToMaturity());
        }

        public double HwZM()
        {
            return 1.12 * Math.Pow(proportionalTransactionCost, 0.31) * 
                Math.Pow(TimeToMaturity(), 0.05) * 
                Math.Pow(Math.Exp(-riskFreeRate * TimeToMaturity()) / hvQuote.value(), 0.25) * 
                Math.Pow((Math.Abs(Gamma()) / riskAversion), 0.5);
        }

        public double BandZMLower(int direction)
        {
            double offset = H0ZM() + HwZM();
            return DeltaZM(direction) - offset;
        }

        public double BandZMUpper(int direction)
        {
            double offset = H0ZM() + HwZM();
            return DeltaZM(direction) + offset;
        }

        public double Theta(VanillaOption? option = null)
        {
            SetEvaluationDateToCalcDate();
            //var theta = amOption.theta();
            //return  FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "theta", 1, "forward");
            try
            {
                return (option ?? euOption).thetaPerDay();  // Different by 0.1 % from FD approach only. Likely much faster though 
            }
            catch (Exception e)
            {
                algo.Error($"OptionContractWrap.Theta. Attempting FD. {e}");
                return FiniteDifferenceApproxTime(calculationDate, (option ?? amOption), "NPV", 1, "forward");
            }
        }

        public double Gamma()
        {
            SetEvaluationDateToCalcDate();
            // dPdP = gamma = finite_difference_approx(spot_quote, am_option, 0.01, 'delta');
            if (hvQuote.value() == 0)
            {
                algo.Error($"OptionContractWrap.Gamma: hvQuote.value() == 0, returning 0 gamma.");
                return 0;
            }
            try
            {
                return amOption.gamma();
            }
            catch  (Exception e)
            {
                algo.Error($"OptionContractWrap.Gamma. HV: {hvQuote.value()} Attempting FD {e}");
                return FDApprox2ndDerivative(spotQuote, amOption, 0.01, "NPV");
            }
            
        }

        public decimal Gamma100Bp()
        {
            return (decimal)(0.5 * Gamma() * Math.Pow((double)algo.MidPrice(UnderlyingSymbol) * 100 * (double)BP, 2));
        }

        public double Vega()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "NPV") / 100;
        }

        public double DTdP()
        {
            return FiniteDifferenceApprox(spotQuote, euOption, 0.01, "thetaPerDay");
        }
        public double DVegadP()  // Vanna
        {
            return FiniteDifferenceApprox(spotQuote, euOption, 0.01, "vega", d1perturbance: hvQuote) / 100;
        }
        public double DGdP()
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.01, "gamma");
        }

        public double DeltaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "delta");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "delta");
        }

        public double ThetaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, euOption, "thetaPerDay");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "thetaPerDay");
        }
        public double VegaDecay()
       
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "vega") / 100;
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "vega") / 100;
        }
        public double GammaDecay()
       
        {
            return FiniteDifferenceApproxTime(calculationDate, amOption, "gamma");
            //return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "gamma");
        }
        public double Rho()

        {
            return FiniteDifferenceApprox(riskFreeRateQuote, amOption, 0.01, "NPV");
        }

        public double DPdIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "delta") / 100;
        }
        public double DTdIV()
        {
            return FiniteDifferenceApprox(hvQuote, euOption, 0.01, "thetaPerDay") / 100;
        }
        public double DVegadIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "vega", d1perturbance: hvQuote) / Math.Pow(100,2);
        }
        public double DGdIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "gamma") / 100;
        }
        public double DIVdP()
        {
            // Used to calculate MV Minimum Variance Delta.
            return FiniteDifferenceApprox(spotQuote, amOption, 0.01, "IV");
        }

        public double NPV(bool? resetCalcDate = true)
        {
            if (resetCalcDate == true)
                SetEvaluationDateToCalcDate();
            return amOption.NPV();
        }

        public int DaysToExpiration()
        {
            return calendar.businessDaysBetween(calculationDate, maturityDate);
        }

        public int DaysToExpiration(DateTime dt)
        {
            return calendar.businessDaysBetween(new Date(dt.Day, dt.Month, dt.Year), maturityDate);
        }

        public double TimeToMaturity()
        {
            return calendar.businessDaysBetween(calculationDate, maturityDate) / 252.0;
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
            if (q0 == 0)            {
                // Percentage perturbance will result in NaN / 0 division error, hence using absolute value
                quote.setValue(d_pct);
            }
            else
            {
                quote.setValue(q0 * (1 + d_pct));
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
                pPlus = FiniteDifferenceApprox(d1perturbance, option, d_pct); // VEGA
            }
            else if (derive == "thetaPerDay")
            {
                //pPlus = amOption.thetaPerDay();
                pPlus = FiniteDifferenceApproxTime(calculationDate, amOption, "NPV", 1, "forward");
                //pPlus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pPlus = option.NPV();
            }
            else if (derive == "IV")
            {
                pPlus = GetIVEngine(spotPriceUnderlying: (decimal)quote.value());
            }
            else
            {
                var methodInfo = option.GetType().GetMethod(derive);
                pPlus = (double)methodInfo.Invoke(option, new object[] { });
            }

            PerturbQuote(quote, q0, -d_pct);

            if (derive == "vega" && d1perturbance != null)
            {
                pMinus = FiniteDifferenceApprox(d1perturbance, option, d_pct); // VEGA
            }
            else if (derive == "thetaPerDay")
            {
                // Works for Vinalla not Dividend
                pMinus = FiniteDifferenceApproxTime(calculationDate, amOption, "NPV", 1, "forward");
                //pMinus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, spotQuote, hvQuote, riskFreeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pMinus = option.NPV();
            }
            else if (derive == "IV")
            {
                pMinus = GetIVEngine(spotPriceUnderlying: (decimal)quote.value());
            }
            else
            {
                var methodInfo = option.GetType().GetMethod(derive);
                pMinus = (double)methodInfo.Invoke(option, new object[] { });
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
                var methodInfo = option.GetType().GetMethod(derive);
                var p0 = (double)methodInfo.Invoke(option, new object[] { });
                var stepSize = q0 == 0 ? d_pct : q0 * d_pct;
                result = ((double)pPlus - (double)p0) / stepSize;
            }
            else if (pMinus != null || method=="backward")
            {
                var methodInfo = option.GetType().GetMethod(derive);
                var p0 = (double)methodInfo.Invoke(option, new object[] { });
                var stepSize = q0 == 0 ? d_pct : q0 * d_pct;
                result = ((double)p0 - (double)pMinus) / stepSize;
            }
            else
            {
                algo.Error($"FiniteDifferenceApprox failed. pPlus {pPlus}, pMinus {pMinus}, method {method}");
                result = 0;
            }
            if (double.IsNaN(result) || double.IsInfinity(result))
            {
                algo.Log($"FiniteDifferenceApprox. Warning: Result is NaN or Infinity. {result}. Defaulting to 0");
                result = 0;
            }
            return result;
        }

        public double FiniteDifferenceApproxTime(Date calculationDate, VanillaOption option, string derive = "NPV", int nDays = 1, string method = "forward")
        {
            var values = new List<double>();

            if (calculationDate >= maturityDate)
            {
                algo.Log($"Option {Contract} matured. Find a way to avoid running calculations on this. Returns Greek value: 0.");
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
            return algo.MidPrice(Contract.Symbol) - Contract.GetPayOff(algo.MidPrice(UnderlyingSymbol));
        }
    }
}
