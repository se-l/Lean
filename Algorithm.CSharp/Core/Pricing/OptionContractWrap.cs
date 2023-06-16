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
    }
    public class OptionContractWrap
    {
        ///<summary>
        /// Singleton class for caching contract attributes and calculating Greeks
        /// </summary>
        ///
        public Securities.Option.Option Contract { get; }
        public Symbol UnderlyingSymbol { get; }
        public Func<decimal?, decimal?, double, double?> IV;

        private readonly Foundations algo;
        private static readonly Dictionary<string, OptionContractWrap> instances = new();
        private static readonly object lockObject = new();

        private readonly DayCounter dayCounter;
        private readonly Calendar calendar;        
        private readonly Date calculationDate;
        private readonly Date settlementDate;
        private readonly Date maturityDate;
        private readonly double strikePrice;
        private SimpleQuote dividendRateQuote;
        private readonly Option.Type optionType;
        private SimpleQuote spotQuote;
        private SimpleQuote riskFeeRateQuote;
        private SimpleQuote hvQuote;
        private PlainVanillaPayoff payoff;
        private AmericanExercise amExercise;
        private EuropeanExercise euExercise;
        private VanillaOption amOption;
        private VanillaOption euOption;
        private BlackScholesMertonProcess bsmProcess;
        private double riskFreeRate = 0.0433;

        /// <summary>
        /// Cached constructor
        /// </summary>
        private static Func<Foundations, Securities.Option.Option, string> genCacheKey = (algo, contract) => $"{contract.Symbol}{algo.Time.Date}";

        public Func<decimal?, double?, GreeksPlus> Greeks;

        private (decimal, decimal, double) GenCacheKeyIV(decimal? spotPriceContract, decimal? spotPriceUnderlying, double accuracy= 0.1)
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
        private OptionContractWrap(Foundations algo, Securities.Option.Option contract)
        {
            this.algo = algo;
            //algo.Log($"{algo.Time}: OptionContractWrap.constructor called. {contract.Symbol}");
            Contract = contract;
            UnderlyingSymbol = contract.Underlying.Symbol;
            IV = Cache<(decimal, decimal, double), decimal?, decimal?, double, double?>(GetIVEngine, GenCacheKeyIV);  // Using fast Analytical BSM for IV

            Greeks = Cache(GreeksP,
                (decimal? spotPrice, double? hv) => {
                    SetSpotQuotePriceUnderlying(spotPrice);
                    SetHistoricalVolatility(hv);
                    return (spotQuote.value(), hvQuote.value());
                }
            );
            
            dayCounter = new Actual365Fixed();
            calendar = new UnitedStates();            
            calculationDate = new Date(algo.Time.Day, algo.Time.Month, algo.Time.Year);
            settlementDate = calculationDate;
            maturityDate = new Date(contract.Expiry.Day, contract.Expiry.Month, contract.Expiry.Year);
            strikePrice = (double)contract.StrikePrice;
            optionType = contract.Right == OptionRight.Call ? Option.Type.Call : Option.Type.Put;

            SetSpotQuotePriceUnderlying();
            SetHistoricalVolatility();
            riskFeeRateQuote = new SimpleQuote(riskFreeRate);
            dividendRateQuote = new SimpleQuote(0.0);

            payoff = new PlainVanillaPayoff(optionType, (double)strikePrice);
            amExercise = new AmericanExercise((Date)settlementDate, maturityDate);
            euExercise = new EuropeanExercise(maturityDate);

            bsmProcess = GetBsm(calculationDate, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), new Handle<Quote>(riskFeeRateQuote));
            amOption = EnginedOption(new VanillaOption(payoff, amExercise), bsmProcess, optionPricingModel: OptionPricingModel.CoxRossRubinstein);
            euOption = EnginedOption(new VanillaOption(payoff, euExercise), bsmProcess, optionPricingModel: OptionPricingModel.AnalyticEuropeanEngine);
        }

        public static OptionContractWrap E(Foundations algo, Securities.Option.Option contract)
        {
            string singletonKey = genCacheKey(algo, contract);
            if (!instances.ContainsKey(singletonKey))
            {
                lock (lockObject)
                {
                    //instances[singletonKey] = constructorCached(algo, contract);
                    instances[singletonKey] = new OptionContractWrap(algo, contract);
                }
            }
            return instances[singletonKey];
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

        private void SetHistoricalVolatility(double? hv = null)
        {
            hv ??= (double)algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
            hvQuote ??= new SimpleQuote(hv);
            if (hvQuote.value() != hv)
            {
                hvQuote.setValue(hv);
            }
            //algo.Log($"{algo.Time} {Contract.Symbol} HV: {algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility}");
        }

        public double? PriceFair(decimal? spotPrice = null, double? hv = null, Date calculationDate = null)
        {
            if (hv == 0) { return null; }
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(hv);
            
            try
            {
                return amOption.NPV();
            }
            catch (Exception e)
            {
                algo.Log($"Unable to derive Fair price {e}. Most likely due to unreasonable volatiliy: {hvQuote.value()}.");
                return null;
            }
        }

        public double? GetIVEngine(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.1)
        {
            double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
            SetSpotQuotePriceUnderlying(spotPriceUnderlying);
            try
            {
                return euOption.impliedVolatility(_spotPriceContract, bsmProcess, accuracy: accuracy);
            }
            catch
            {
                return null;
            }
        }

        //public double GuessInitialImpliedVolatility(decimal? spotPriceContract = null)
        //{
        //    /// By default, it uses the formula from Brenner and Subrahmanyam (1988) as the initial guess for implied volatility. PS2πT−−−√
        //    /// where P is the Option contract price, S is the underlying price, and T is the time until Option expiration.
        //    double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
        //    return (_spotPriceContract / strikePrice) * Math.Sqrt(2*Math.PI / (double)Contract.TimeUntilExpiry.TotalDays);
        //}

        public double? GetIVNewtonRaphson(decimal? spotPriceContract = null, decimal? spotPriceUnderlying = null, double accuracy = 0.1)
        {
            int maxIterations = 100;
            double _spotPriceContract = (double)(spotPriceContract ?? algo.MidPrice(Contract.Symbol));
            spotQuote.setValue((double)spotPriceUnderlying);

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

            // If the method did not converge to a solution, return null
            return null;
        }

        public GreeksPlus GreeksP(decimal? spotPrice = null, double? hv = null)
        {
            // parameterize that change. Depending on metric, perturbance is different. 1-10BP for price, 100 BP for HV and rho, 1 day for time, 
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(hv);         
            return new GreeksPlus(this, hvQuote.value());
        }

        public double Delta()
        {
            return amOption.delta();
            // delta = finite_difference_approx(spot_quote, am_option, 0.01, 'NPV') ; print(delta) ; print(am_option.delta())
        }

        public double Theta()
        {
            //var theta = amOption.theta();  // WRONG
            //amOption.thetaPerDay()
            //var theta = -FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "NPV", 1, "forward");
            return euOption.thetaPerDay();  // Different by 0.1 % from FD approach only. Likely much faster though 
        }

        public double Gamma()
        {
            // dPdP = gamma = finite_difference_approx(spot_quote, am_option, 0.01, 'delta');
            return amOption.gamma();
        }

        public double Vega()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "NPV") / 100;
        }

        public double DTdP()
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.001, "theta");
        }
        public double DIVdP()
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.001, "vega", d1perturbance: hvQuote) / 100;
        }
        public double DGdP()
        {
            return FiniteDifferenceApprox(spotQuote, amOption, 0.001, "gamma");
        }

        public double DeltaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "delta", 1, "forward");
        }

        public double ThetaDecay()
        {
            return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "theta", 1, "forward");
        }
        public double VegaDecay()
       
        {
            return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "vega", 1, "forward");
        }
        public double GammaDecay()
       
        {
            return FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "gamma", 1, "forward");
        }
        public double Rho()
       
        {
            return FiniteDifferenceApprox(riskFeeRateQuote, amOption, 0.01, "NPV"); // amOption.rho();  // Errors - Need FD likely.
        }

        public double DPdIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "delta");
        }
        public double DTdIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "theta");
        }
        public double DIV2()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "vega", d1perturbance: hvQuote) / 100;
        }
        public double DGdIV()
        {
            return FiniteDifferenceApprox(hvQuote, amOption, 0.01, "gamma");
        }

        public double TheoreticalPrice()
        {
            return amOption.NPV();
        }

        public BlackScholesMertonProcess GetBsm(Date calculationDate, Handle<Quote> spotQuote, Handle<Quote> hvQuote, Handle<Quote> rfQuote, double dividendRate = 0)
        {
            var dividendRateQuote = new SimpleQuote(dividendRate);
            var flatTs = new Handle<YieldTermStructure>(new FlatForward(calculationDate, rfQuote, dayCounter));
            var dividendYield = new Handle<YieldTermStructure>(new FlatForward(calculationDate, dividendRateQuote, dayCounter));
            var flatVolTs = new Handle<BlackVolTermStructure>(new BlackConstantVol(calculationDate, calendar, hvQuote, dayCounter));
            return new BlackScholesMertonProcess(spotQuote, dividendYield, flatTs, flatVolTs);
        }

        public VanillaOption EnginedOption(VanillaOption option, BlackScholesMertonProcess bsmProcess, int steps = 100, OptionPricingModel optionPricingModel = OptionPricingModel.CoxRossRubinstein)
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

        public double FiniteDifferenceApprox(SimpleQuote quote, VanillaOption option, double d_pct = 0.01, string derive = "NPV", SimpleQuote d1perturbance = null, string method = "central")
        {
            // f'(x) ≈ (f(x+h) - f(x-h)) / (2h); h: step size;
            double pPlus;
            double pMinus;

            var q0 = quote.value();
            quote.setValue(q0 * (1 + d_pct));
            if (derive == "vega" && d1perturbance != null)
            {
                pPlus = FiniteDifferenceApprox(d1perturbance, option, d_pct); // VEGA
            }
            else if (derive == "theta")
            {
                pPlus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pPlus = option.NPV();
            }
            else
            {
                var methodInfo = option.GetType().GetMethod(derive);
                pPlus = (double)methodInfo.Invoke(option, new object[] { });
            }

            quote.setValue(q0 * (1 - d_pct));
            if (derive == "vega" && d1perturbance != null)
            {
                pMinus = FiniteDifferenceApprox(d1perturbance, option, d_pct); // VEGA
            }
            else if (derive == "theta")
            {
                pMinus = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "NPV", 1, "forward");
            }
            else if (derive == "NPV")
            {
                pMinus = option.NPV();
            }
            else
            {
                var methodInfo = option.GetType().GetMethod(derive);
                pMinus = (double)methodInfo.Invoke(option, new object[] { });
            }
            quote.setValue(q0);

            if (method == "central")
            {
                // placeholder for left / right
                return (pPlus - pMinus) / (2 * q0 * d_pct);
            } else
            {
                return (pPlus - pMinus) / (2 * q0 * d_pct);
            }
        }

        public double FiniteDifferenceApproxTime(Date calculationDate, Option.Type optionType, double strikePrice, Date maturityDate, SimpleQuote spotQuote, SimpleQuote hvQuote, SimpleQuote rfQuote, string derive = "NPV", int nDays = 1, string method = "forward")
        {
            var values = new List<double>();
            for (var dt = calculationDate; dt <= calculationDate + nDays; dt++)
            {
                // Fix moving this by business date. Dont divide by Sat / Sun. Use the USA calendar ideally.
                var payoff = new PlainVanillaPayoff(optionType, strikePrice);
                var amExercise = new AmericanExercise(dt, maturityDate);
                var bsmProcess = GetBsm(dt, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), new Handle<Quote>(rfQuote));
                var optionDt = EnginedOption(new VanillaOption(payoff, amExercise), bsmProcess);
                if (derive == "vega")
                {
                    values.Add(FiniteDifferenceApprox(hvQuote, optionDt, 0.01)); // VEGA
                }
                else if (derive == "NPV")
                {
                    values.Add(optionDt.NPV());
                }
                else
                {
                    var methodInfo = optionDt.GetType().GetMethod(derive);
                    values.Add((double)methodInfo.Invoke(optionDt, new object[] { }));
                }
            }
            return (values[0] - values[values.Count - 1]) / nDays;
        }
        public decimal IntrinsicValue()
        {
            return (Contract.StrikePrice - algo.MidPrice(UnderlyingSymbol)) * OptionRight2Int[Contract.Right];
        }

        public decimal ExtrinsicValue()
        {
            return algo.MidPrice(Contract.Symbol) - IntrinsicValue();
        }
    }
}
