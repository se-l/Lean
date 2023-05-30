using QLNet;
using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class OptionContractWrap
    {
        ///<summary>
        /// Singleton class for caching contract attributes and calculating Greeks
        /// </summary>
        ///
        public Securities.Option.Option Contract { get; }
        public Symbol UnderlyingSymbol { get; }

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
        private VanillaOption amOption;
        private BlackScholesMertonProcess bsmProcess;
        private double riskFreeRate = 0.0433;

        /// <summary>
        /// Cached constructor
        /// </summary>
        private static Func<Foundations, Securities.Option.Option, string> genCacheKey = (algo, contract) => $"{contract.Symbol}{algo.Time.Date}";

        public Func<decimal?, double?, GreeksPlus> Greeks;

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

            Greeks = Cache(GreeksP,
                (decimal? spotPrice, double? hv) => {
                    SetSpotQuotePriceUnderlying(spotPrice);
                    SetHistoricalVolatility(hv);
                    return $"{Contract}{spotQuote.value()}{hvQuote.value()}";
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
            amOption = new VanillaOption(payoff, amExercise);
            bsmProcess = GetBsm(calculationDate, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), new Handle<Quote>(riskFeeRateQuote));
            amOption = EnginedOption(amOption, bsmProcess);
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
            spotQuote?.setValue((double)quote);
        }

        private void SetHistoricalVolatility(double? hv = null)
        {
            hv ??= (double)algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
            hvQuote ??= new SimpleQuote(hv);
            hvQuote?.setValue(hv);
            //algo.Log($"{algo.Time} {Contract.Symbol} HV: {algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility}");
        }

        public double? PriceFair(decimal? spotPrice = null, double? hv = null, Date calculationDate = null)
        {
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

        public double? IV(decimal? spotPriceContract = null)
        {
            decimal _spotPriceContract = spotPriceContract ?? algo.MidPrice(Contract.Symbol);
            return amOption.impliedVolatility((double)_spotPriceContract, bsmProcess);
        }

        public Tuple<double?, double?> IVBidAsk(decimal? bidPrice = null, decimal? askPrice = null)
        {
            double? bidIV;
            double? askIV;
            try
            {
                bidIV = Math.Max(amOption.impliedVolatility((double)(bidPrice ?? Contract.BidPrice), bsmProcess), 0.01);
            }
            catch (Exception e)
            {
                algo.Log($"bid iv error. Bid price is too low to derive IV: {e} - setting IV Bid to 0.001");
                bidIV = null;
            }
            try
            {
                askIV = Math.Max(amOption.impliedVolatility((double)(askPrice ?? Contract.AskPrice), bsmProcess), 0.01);
            }
            catch (Exception e)
            {
                algo.Log($"ask iv error. Ask price is too high to derive IV: {e} - setting IV Ask to None");
                askIV = null;
            }
            return Tuple.Create(bidIV, askIV);
        }

        public GreeksPlus GreeksP(decimal? spotPrice = null, double? hv = null)
        {
            // parameterize that change. Depending on metric, perturbance is different. 1-10BP for price, 100 BP for HV and rho, 1 day for time, 
            double dPdP, gamma;
            SetSpotQuotePriceUnderlying(spotPrice);
            SetHistoricalVolatility(hv);
            algo.Debug($"{algo.Time} - {Contract}.GreeksP(). spotPrice: {spotQuote.value()} and HV: {hvQuote.value()}.");

            // First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
            var delta = amOption.delta();
            // delta = finite_difference_approx(spot_quote, am_option, 0.01, 'NPV') ; print(delta) ; print(am_option.delta())
            //var theta = amOption.theta();  // WRONG
            var theta = -FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "NPV", 1, "forward");
            var vega = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "NPV") / 100;

            // Second order derivatives using finite difference
            dPdP = gamma = amOption.gamma();
            // gamma = finite_difference_approx(spot_quote, am_option, 0.01, 'delta');
            var dTdP = FiniteDifferenceApprox(spotQuote, amOption, 0.001, "theta");
            var dIVdP = FiniteDifferenceApprox(spotQuote, amOption, 0.001, "vega", d1perturbance: hvQuote) / 100;
            var dGdP = FiniteDifferenceApprox(spotQuote, amOption, 0.001, "gamma");

            // dIV: dV2 / dIVdT (Vega changes towards maturity) ; d2V / dIV2 (Vanna) ; d2V / dIVdP (Vega changes with Delta)
            // d2V / dPdIV (Delta changes with IV / Color)
            var dPdIV = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "delta");
            var dTdIV = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "theta");
            var dIV2 = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "vega", d1perturbance: hvQuote) / 100;
            var dGdIV = FiniteDifferenceApprox(hvQuote, amOption, 0.01, "gamma");

            // dP: dV2 / dPdT (Delta decay / Charm) ; d2V / dP2 (Gamma) ; d2V / dPdIV (Delta changes with IV / Color)
            // probably the more expensive calculation. Given not used for hedging, only calc on request, like EOD position end PF Risk.
            var deltaDecay = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "delta", 1, "forward");
            var thetaDecay = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "theta", 1, "forward");
            var vegaDecay = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "vega", 1, "forward");
            var gammaDecay = FiniteDifferenceApproxTime(calculationDate, optionType, strikePrice, maturityDate, spotQuote, hvQuote, riskFeeRateQuote, "gamma", 1, "forward");
            var rho = FiniteDifferenceApprox(riskFeeRateQuote, amOption, 0.01, "NPV"); // amOption.rho();  // Errors - Need FD likely.
            var theoPrice = amOption.NPV();

            return new GreeksPlus(hvQuote.value(), delta, gamma, deltaDecay, dPdIV, dGdP, gammaDecay, dGdIV, theta, dTdP, thetaDecay, dTdIV, vega, dIVdP, vegaDecay, dIV2, rho, theoPrice);

        }

        public BlackScholesMertonProcess GetBsm(Date calculationDate, Handle<Quote> spotQuote, Handle<Quote> hvQuote, Handle<Quote> rfQuote, double dividendRate = 0)
        {
            var dividendRateQuote = new SimpleQuote(dividendRate);
            var flatTs = new Handle<YieldTermStructure>(new FlatForward(calculationDate, rfQuote, dayCounter));
            var dividendYield = new Handle<YieldTermStructure>(new FlatForward(calculationDate, dividendRateQuote, dayCounter));
            var flatVolTs = new Handle<BlackVolTermStructure>(new BlackConstantVol(calculationDate, calendar, hvQuote, dayCounter));
            return new BlackScholesMertonProcess(spotQuote, dividendYield, flatTs, flatVolTs);
        }

        public VanillaOption EnginedOption(VanillaOption option, BlackScholesMertonProcess bsmProcess, int steps = 100)
        {
            var engine = new BinomialVanillaEngine<CoxRossRubinstein>(bsmProcess, steps);
            option.setPricingEngine(engine);
            return option;
        }

        public double FiniteDifferenceApprox(SimpleQuote quote, Option option, double d_pct = 0.01, string derive = "NPV", SimpleQuote d1perturbance = null, string method = "central")
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
                var option = new VanillaOption(payoff, amExercise);
                var bsmProcess = GetBsm(dt, new Handle<Quote>(spotQuote), new Handle<Quote>(hvQuote), new Handle<Quote>(rfQuote));
                var optionDt = EnginedOption(option, bsmProcess);
                if (derive == "vega")
                {
                    values.Add(FiniteDifferenceApprox(hvQuote, optionDt, 0.01)); // VEGA
                }
                else
                {
                    var methodInfo = option.GetType().GetMethod(derive);
                    values.Add((double)methodInfo.Invoke(optionDt, new object[] { }));
                }
            }
            return (values[0] - values[values.Count - 1]) / nDays;
        }
    }
}
