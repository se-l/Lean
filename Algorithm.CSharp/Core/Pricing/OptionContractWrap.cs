using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public sealed class OptionContractWrap
    {
        public Securities.Option.Option Contract { get; }

        private Symbol UnderlyingSymbol { get; }
        public Func<decimal?, decimal?, double, double> IV { get; }
        private Func<double, decimal, decimal, double> DeltaCached { get; }
        private Func<double, decimal, decimal, double> GammaCached { get; }
        private Func<double, decimal, decimal, double> ThetaCached { get; }
        private Func<double, decimal, decimal, double> VegaCached { get; }
        private Func<double, decimal, decimal, double> SpeedCached { get; }
        private Func<double, decimal, decimal, double> GammaDecayCached { get; }
        private Func<double, decimal, decimal, double> GammaVolCached { get; }
        private Func<double, decimal, decimal, double> ThetaDecayCached { get; }
        private Func<double, decimal, decimal, double> VegaDecayCached { get; }
        private Func<double, decimal, decimal, double> VannaCached { get; }
        private Func<double, decimal, decimal, double> VolgaCached { get; }

        private double Tenor { get; set; }
        private const int Multiplier = 100;
        public const double Accuracy = 0.001;

        private readonly Foundations _algo;
        private static readonly Dictionary<(Symbol, DateTime), OptionContractWrap> instances = new();

        private readonly DateTime calculationDate;
        private readonly double k;
        private readonly bool isCall;

        private OptionContractWrap(Foundations algo, Securities.Option.Option contract, DateTime calculationDate)
        {
            _algo = algo;
            Contract = contract;
            UnderlyingSymbol = contract.Underlying.Symbol;

            IV            = Cache((decimal? p, decimal? s, double accuracy) => JuliaIV(p, s),        (p, s, accuracy) => (p, s, accuracy));
            DeltaCached    = Cache((double iv, decimal s, decimal p) => JuliaDelta(iv, s, p),         (iv, s, p) => (iv, s, p));
            GammaCached    = Cache((double iv, decimal s, decimal p) => JuliaGamma(iv, s, p),         (iv, s, p) => (iv, s, p));
            ThetaCached    = Cache((double iv, decimal s, decimal p) => JuliaTheta(iv, s, p),         (iv, s, p) => (iv, s, p));
            VegaCached     = Cache((double iv, decimal s, decimal p) => JuliaVega(iv, s, p),          (iv, s, p) => (iv, s, p));
            SpeedCached    = Cache((double iv, decimal s, decimal p) => JuliaSpeed(iv, s, p),         (iv, s, p) => (iv, s, p));
            GammaDecayCached = Cache((double iv, decimal s, decimal p) => JuliaGammaDecay(iv, s, p), (iv, s, p) => (iv, s, p));
            GammaVolCached   = Cache((double iv, decimal s, decimal p) => JuliaGammaVol(iv, s, p),   (iv, s, p) => (iv, s, p));
            ThetaDecayCached = Cache((double iv, decimal s, decimal p) => JuliaThetaDecay(iv, s, p), (iv, s, p) => (iv, s, p));
            VegaDecayCached  = Cache((double iv, decimal s, decimal p) => JuliaVegaDecay(iv, s, p),  (iv, s, p) => (iv, s, p));
            VannaCached    = Cache((double iv, decimal s, decimal p) => JuliaVanna(iv, s, p),         (iv, s, p) => (iv, s, p));
            VolgaCached    = Cache((double iv, decimal s, decimal p) => JuliaVolga(iv, s, p),         (iv, s, p) => (iv, s, p));

            this.calculationDate = calculationDate.TimeOfDay.TotalSeconds == 0
                ? calculationDate.Date.AddHours(9.5)
                : calculationDate;
            Tenor  = ToTenor(contract.Expiry, this.calculationDate);
            k      = (double)contract.StrikePrice;
            isCall = contract.Right == OptionRight.Call;
        }

        public static OptionContractWrap E(Foundations algo, Securities.Option.Option contract, DateTime calculationDate)
        {
            DateTime _calculationDate = calculationDate.TimeOfDay != TimeSpan.Zero
                ? calculationDate.Date.Add(new TimeSpan(0, 9, 30, 0))
                : calculationDate;

            (Symbol, DateTime) singletonKey = (contract.Symbol, _calculationDate.Date);
            if (!instances.ContainsKey(singletonKey))
            {
                if (_calculationDate != calculationDate)
                    algo.Log($"OptionContractWrap.E: Overrode calculationDate {calculationDate} with {_calculationDate} to get non-zero greeks on expiration date.");
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
                        count++;
                    }
                }
            }
            return count;
        }

        // ── Convenience accessors ────────────────────────────────────────────────

        public decimal HistoricalVolatility() =>
            _algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;

        public int DaysToExpiration() => (Contract.Expiry - _algo.Time).Days;

        public decimal ExtrinsicValue() =>
            _algo.MidPrice(Contract.Symbol) - Contract.GetPayOff(_algo.MidPrice(UnderlyingSymbol));

        public double MoneynessFwd(decimal? s = null)
        {
            return _algo.julia.MnyFwd((double)(s ?? _algo.MidPrice(UnderlyingSymbol)), k, T(), GetCalcTimeUtc(), UnderlyingSymbol.Value);
        }

        public double MoneynessFwdLn() => Math.Log(MoneynessFwd());

        // ── Julia call helpers ───────────────────────────────────────────────────

        private long GetCalcTimeUtc() =>
            new DateTimeOffset(calculationDate, TimeSpan.Zero).ToUnixTimeSeconds();

        private double S() => (double)_algo.MidPrice(UnderlyingSymbol);
        private double P() => (double)_algo.MidPrice(Contract.Symbol);
        private double T() => ToTenor(Contract.Expiry, _algo.Time);

        // ── Public Julia wrappers ────────────────────────────────────────────────

        private static bool InvalidInputs(double? p, double? s) =>
            (p.HasValue && p.Value <= 0) || (s.HasValue && s.Value <= 0);
        
        private static bool InvalidInputs(double? p, double? s, double? sigma = null) =>
            (p.HasValue && p.Value <= 0) ||
            (s.HasValue && s.Value <= 0) ||
            (sigma.HasValue && !double.IsFinite(sigma.Value));

        private double JuliaPrice(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Price(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaIV(decimal? p = null, decimal? s = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s) ? double.NaN : _algo.julia.IV(isCall, _s, k, T(), _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaDelta(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Delta(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaGamma(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Gamma(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaTheta(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Theta(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaVega(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Vega(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaSpeed(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Speed(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaGammaDecay(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.GammaDecay(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaGammaVol(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.GammaVol(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaThetaDecay(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.ThetaDecay(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaVegaDecay(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.VegaDecay(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaVanna(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Vanna(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        private double JuliaVolga(double sigma, decimal? s = null, decimal? p = null, string yieldCurve = "YC")
        {
            double _s = (double)(s ?? _algo.MidPrice(UnderlyingSymbol));
            double _p = (double)(p ?? _algo.MidPrice(Contract.Symbol));
            return InvalidInputs(_p, _s, sigma) ? double.NaN : _algo.julia.Volga(isCall, _s, k, T(), sigma, _p, GetCalcTimeUtc(), UnderlyingSymbol.Value, yieldCurve);
        }

        // ── SetIndependents — kept for callers that pre-warm spot before a greek call ─

        /// <summary>
        /// Optional pre-warming; spot override is honoured by passing s explicitly to
        /// Julia wrappers. Volatility override is passed through via the cached greek calls.
        /// </summary>
        public void SetIndependents(decimal? spotUnderlyingPrice = null, decimal? spotPrice = null, double? volatility = null)
        {
            // When a spot override is provided we store it so DeltaXBpUSD etc. can pick it up.
            if (spotUnderlyingPrice != null) _overrideSpot = spotUnderlyingPrice;
            if (spotPrice != null)           _overrideContractPrice = spotPrice;

            // If a volatility is not given, back-solve it from the option price.
            if (volatility != null)
            {
                _overrideVol = volatility;
            }
            else if (spotPrice != null)
            {
                _overrideVol = IV(spotPrice, spotUnderlyingPrice, Accuracy);
            }
            else
            {
                _overrideVol = null;
            }
        }

        // Overrides set by SetIndependents; null means "use live market prices".
        private decimal? _overrideSpot;
        private decimal? _overrideContractPrice;
        private double?  _overrideVol;

        private decimal EffectiveSpot(decimal? s)            => s ?? _overrideSpot ?? _algo.MidPrice(UnderlyingSymbol);
        private decimal EffectiveContractPrice(decimal? p)   => p ?? _overrideContractPrice ?? _algo.MidPrice(Contract.Symbol);

        // ── First-order greeks ───────────────────────────────────────────────────

        public double Delta(double volatility, decimal spot) =>
            DeltaCached(volatility, spot, EffectiveContractPrice(null));

        public double Delta(double volatility) =>
            DeltaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double DeltaXBpUSD(double volatility, decimal dS = 100) =>
            Delta(volatility) * (double)(EffectiveSpot(null) * dS * BP) * Multiplier;

        public double Gamma(double volatility, decimal spot) =>
            GammaCached(volatility, spot, EffectiveContractPrice(null));

        public double Gamma(double volatility) =>
            GammaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double Theta(double volatility, decimal spot) =>
            ThetaCached(volatility, spot, EffectiveContractPrice(null));

        public double Theta(double volatility) =>
            ThetaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double Vega(double volatility) =>
            VegaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        // ── Higher-order greeks ──────────────────────────────────────────────────

        /// <summary>dGamma/dS (Speed)</summary>
        public double DS3(double volatility) =>
            SpeedCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double Speed(double volatility) => DS3(volatility);

        /// <summary>dGamma/dt</summary>
        public double GammaDecay(double volatility) =>
            GammaDecayCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        /// <summary>dGamma/dSigma</summary>
        public double DS2dIV(double volatility) =>
            GammaVolCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        /// <summary>dTheta/dt</summary>
        public double ThetaDecay(double volatility) =>
            ThetaDecayCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        /// <summary>dVega/dt (Veta)</summary>
        public double VegaDecay(double volatility) =>
            VegaDecayCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        /// <summary>dDelta/dSigma (Vanna)</summary>
        public double DDeltadIV(double volatility) =>
            VannaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double Vanna(double volatility) => DDeltadIV(volatility);

        /// <summary>dVega/dSigma (Volga / Vomma)</summary>
        public double DIV2(double volatility) =>
            VolgaCached(volatility, EffectiveSpot(null), EffectiveContractPrice(null));

        public double Volga(double volatility) => DIV2(volatility);

        // ── Misc ─────────────────────────────────────────────────────────────────

        public double Rho() => 0; // not yet implemented in Julia server

        /// <summary>Theoretical option price at a given IV.</summary>
        public double NPV(double iv, decimal? s = null) =>
            JuliaPrice(iv, EffectiveSpot(s), EffectiveContractPrice(null));

        /// <summary>
        /// IVdS: how much IV changes per unit move in the underlying.
        /// Approximated as a central finite difference of JuliaIV w.r.t. spot.
        /// </summary>
        public double IVdS(double volatility)
        {
            decimal s0   = EffectiveSpot(null);
            decimal p0   = EffectiveContractPrice(null);
            double  dS   = (double)s0 * 0.01;
            if (dS == 0) return 0;

            double ivPlus  = JuliaIV(p0, s0 + (decimal)dS);
            double ivMinus = JuliaIV(p0, s0 - (decimal)dS);
            return (ivPlus - ivMinus) / (2 * dS);
        }

        /// <summary>Vega component of Minimum Variance Delta.</summary>
        public double MVVega(double iv) => Vega(iv) * IVdS(iv);
    }
}
