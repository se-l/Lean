
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    // Interface to get real-time greeks, as well as snapshots as of request time for different security types, option & equity.
    public class GreeksPlus
    {
        public Security Security { get; internal set; }
        public OptionContractWrap? OCW;
        private double? _hV;
        private double? _delta;
        private double? _gamma;
        private decimal? _gamma100Bp;
        private double? _mVVega;
        private double? _deltaDecay;
        private double? _dPdIV;
        private double? _dGdP;
        private double? _gammaDecay;
        private double? _dGammaDIV;
        private double? _theta;
        private double? _dTdP;
        private double? _thetaDecay;
        private double? _dTdIV;
        private double? _vega;
        private double? _dDeltaDIV;
        private double? _vegaDecay;
        private double? _dVegadIV;
        private double? _rho;
        private double? _nPV;
               
        public double HV { get => _hV ?? (double)OCW.HistoricalVolatility(); }

        // First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
        public double Delta { get => _delta ?? OCW.Delta(); }  // dP ; sensitivity to underlying price}
        public double Gamma { get => _gamma ?? OCW.Gamma(); }  // dP2
        public decimal Gamma100Bp { get => _gamma100Bp ?? OCW.GammaXBp(100); }  // dP2

        // Second order derivatives using finite difference
        public double MVVega { get => _mVVega ?? OCW.MVVega(); }
        public double DeltaDecay { get => _deltaDecay ?? OCW.DeltaDecay(); }  // dPdT
        public double DPdIV { get => _dPdIV ?? OCW.DDeltaDIV(); }  // dPdIV
        public double DGdP { get => _dGdP ?? OCW.DGdP(); }  // dP3
        public double GammaDecay { get => _gammaDecay ?? OCW.GammaDecay(); }  // dP2dT
        public double DGammaDIV { get => _dGammaDIV ?? OCW.DGammaDIV(); }  // dP2dIV
        public double Theta { get => _theta ?? OCW.Theta(); }  // dT ; sensitivity to time
        public double DTdP { get => _dTdP ?? OCW.DTdP(); }  // dTdP
        public double ThetaDecay { get => _thetaDecay ?? OCW.ThetaDecay(); }  // dT2
        public double DTdIV { get => _dTdIV ?? OCW.DTdIV(); }  // dTdIV
        public double Vega { get => _vega ?? OCW.Vega(); }  // dIV ; sensitivity to volatility
        public double DDeltaDIV { get => _dDeltaDIV ?? OCW.DDeltaDIV(); }  // dVegadP ; Vanna
        public double VegaDecay { get => _vegaDecay ?? OCW.VegaDecay(); }  // dIVdT
        public double DVegadIV { get => _dVegadIV ?? OCW.DVegadIV(); }  // vomma
        public double Rho { get => _rho ?? OCW.Rho(); }  // dR ; sensitivity to interest rate
        public double NPV { get => _nPV ?? OCW.NPV(); }  // theoretical price
        public double DeltaZM(int direction)
        {  // Adjusted Delta
            return OCW.DeltaZM(direction);
        }
        public double DeltaZMOffset(int direction)  // Zakamulin bands are made of option deltas only, hence no default 1 for equity.
        {  // Adjusted Delta
            return OCW.DeltaZMOffset(direction);  // Zakamulin bands are made of option deltas only, hence no default 1 for equity.
        }


        public GreeksPlus(OptionContractWrap ocw)
        {
            Security = ocw.Contract;
            OCW = ocw;
        }

        /// <summary>
        /// Equity GreeksPlus
        /// </summary>
        public GreeksPlus(Security security)
        {
            if (security.Type != SecurityType.Equity)
            {
                throw new System.NotImplementedException();
            }
            Security = security;
            _hV = (double)Security.VolatilityModel.Volatility;
            _delta = 1;
            _gamma = 0;
            _gamma100Bp = 0;
            _mVVega = 0;
            _deltaDecay = 0;
            _dPdIV = 0;
            _dGdP = 0;
            _gammaDecay = 0;
            _dGammaDIV = 0;
            _theta = 0;
            _dTdP = 0;
            _thetaDecay = 0;
            _dTdIV = 0;
            _vega = 0;
            _dDeltaDIV = 0;
            _vegaDecay = 0;
            _dVegadIV = 0;
            _rho = 0;
            _nPV = 0;
        }

        public GreeksPlus Snap()
        {
            _hV = HV;
            _delta = Delta;
            _gamma = Gamma;
            _gamma100Bp = Gamma100Bp;
            _mVVega = MVVega;
            _deltaDecay = DeltaDecay;
            _dPdIV = DPdIV;
            _dGdP = DGdP;
            _gammaDecay = GammaDecay;
            _dGammaDIV = DGammaDIV;
            _theta = Theta;
            _dTdP = DTdP;
            _thetaDecay = ThetaDecay;
            _dTdIV = DTdIV;
            _vega = Vega;
            _dDeltaDIV = DDeltaDIV;
            _vegaDecay = VegaDecay;
            _dVegadIV = DVegadIV;
            _rho = Rho;
            _nPV = NPV;
            return this;
        }
    }
}
