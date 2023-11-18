
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    // Interface to get real-time greeks, as well as snapshots as of request time for different security types, option & equity.
    public class GreeksPlus
    {
        private readonly Foundations _algo;
        public Security Security { get; internal set; }
        public OptionContractWrap? OCW;
        private double? _iV;
        private double? _hV;
        private double? _nPV;
        private double? _iVdS;
        private int? _dte;

        private double? _delta;
        private double? _gamma;
        private double? _deltaDecay;
        private double? _dS3;
        private double? _gammaDecay;
        private double? _dGammaDIV;
        private double? _theta;
        private double? _thetaTotal;
        private double? _thetaDecay;
        private double? _vega;
        private double? _dSdIV;
        private double? _vegaDecay;
        private double? _dIV2;
        private double? _rho;

        // Non-Greeks
        public double HV { get => _hV ?? (double)OCW.HistoricalVolatility(); }
        //public double IV { get => _iV ?? OCW.IV(_algo.MidPrice(Security.Symbol), _algo.MidPrice(Underlying(Security.Symbol)), 0.001); }
        public double IV
        {
            get
            {
                if (_iV != null && _iV != 0) return _iV ?? 0;

                _iV = _algo.MidIV(Security.Symbol);
                return _iV ?? 0;
            }
        }
        public double NPV { get => _nPV ?? OCW.NPV(); }  // theoretical price
        public double IVdS { get => _iVdS ?? OCW.IVdS(IV); }
        public int DTE { get => _dte ?? OCW.DaysToExpiration(); }

        // Greeks. 1st order followed by dS, dT, dIV. Then 3rd at end.
        // dS
        public double Delta { get => _delta ?? OCW.Delta(); }  // dP ; sensitivity to underlying price}
        public double Gamma { get => _gamma ?? OCW.Gamma(); }  // dP2
        public double DeltaDecay { get => _deltaDecay ?? OCW.DeltaDecay(); }  // dPdT
        public double DDeltadIV { get => _dSdIV ?? OCW.DDeltadIV(IV); }  // dVegadP ; Vanna
        public double Vanna { get => DDeltadIV; }
        // dT
        public double Theta { get => _theta ?? OCW.Theta(); }  // dT ; sensitivity to time
        public double ThetaTillExpiry { get => _thetaTotal ?? OCW.ThetaTillExpiry(); }
        public double ThetaDecay { get => _thetaDecay ?? OCW.ThetaDecay(); }  // dT2
        // dIV
        public double Vega { get => _vega ?? OCW.Vega(IV); }  // dIV ; sensitivity to volatility
        // Vanna - above in delta
        public double VegaDecay { get => _vegaDecay ?? OCW.VegaDecay(IV); }  // dIVdT
        public double DIV2 { get => _dIV2 ?? OCW.DIV2(IV); }  // Vomma / Volga
        // dR
        public double Rho { get => _rho ?? OCW.Rho(); }  // dR ; sensitivity to interest rate

        // 3rd order
        public double DS3 { get => _dS3 ?? OCW.DS3(); }  // dP3
        public double GammaDecay { get => _gammaDecay ?? OCW.GammaDecay(); }  // dP2dT
        public double DS2dIV { get => _dGammaDIV ?? OCW.DS2dIV(IV); }  // dP2dIV

        public double DeltaZM(int direction)
        {  // Adjusted Delta
            return OCW.DeltaZM(direction);
        }
        public double DeltaZMOffset(int direction)  // Zakamulin bands are made of option deltas only, hence no default 1 for equity.
        {  // Adjusted Delta
            return OCW.DeltaZMOffset(direction);  // Zakamulin bands are made of option deltas only, hence no default 1 for equity.
        }

        public void SetIV(double iV)
        {
            _iV = iV;
        }

        public GreeksPlus(Foundations algo, OptionContractWrap ocw)
        {
            _algo = algo;
            Security = ocw.Contract;
            OCW = ocw;
        }

        /// <summary>
        /// Equity GreeksPlus
        /// </summary>
        public GreeksPlus(Foundations algo, Security security)
        {
            if (security.Type != SecurityType.Equity)
            {
                throw new System.NotImplementedException();
            }
            _algo = algo;
            Security = security;
            _iV = 0;
            _hV = (double)Security.VolatilityModel.Volatility;
            _nPV = 0;
            _iVdS = 0;
            _dte = 0;

            _delta = 1;
            _gamma = 0;
            _deltaDecay = 0;
            _dSdIV = 0;

            _theta = 0;
            _thetaTotal = 0;
            _thetaDecay = 0;

            _vega = 0;
            _vegaDecay = 0;
            _dIV2 = 0;

            _rho = 0;

            _dS3 = 0;
            _gammaDecay = 0;
            _dGammaDIV = 0;
        }

        public GreeksPlus Snap()
        {
            if (Security.Type == SecurityType.Option && Security.Symbol.ID.Date <= _algo.Time)
            {
                SnapExpired();
                return this;
            }

            _hV = IV;
            _hV = HV;
            _nPV = NPV;
            _iVdS = IVdS;
            _dte = DTE;

            _delta = Delta;
            _gamma = Gamma;
            _deltaDecay = DeltaDecay;
            _dSdIV = DDeltadIV;

            _theta = Theta;
            _thetaTotal = ThetaTillExpiry;
            _thetaDecay = ThetaDecay;

            _vega = Vega;
            _vegaDecay = VegaDecay;
            _dIV2 = DIV2;

            _rho = Rho;

            _dS3 = DS3;
            _gammaDecay = GammaDecay;
            _dGammaDIV = DS2dIV;

            return this;
        }

        public void SnapExpired()
        {
            _iV = 0;
            _hV = HV;
            _nPV = 0;
            _iVdS = 0;
            _dte = 0;

            _delta = 0;
            _gamma = 0;
            _deltaDecay = 0;
            _dSdIV = 0;

            _theta = 0;
            _thetaTotal = 0;
            _thetaDecay = 0;

            _vega = 0;
            _vegaDecay = 0;
            _dIV2 = 0;

            _rho = 0;

            _dS3 = 0;
            _gammaDecay = 0;
            _dGammaDIV = 0;
        }
    }
}
