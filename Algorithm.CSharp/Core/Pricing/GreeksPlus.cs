
namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class GreeksPlus
    {
        public OptionContractWrap OCW;
        // Besides HV, all meant to be sensitivities to the underlying price, P, volatility, IV, time, T, and interest rate, R. 
        // No quantity, price besides typical (delta, dPUnderlying=1; vega dHV=100 Bp; theta dTime = 1 day)
        // dIV: dV2 / dIVdT (Vega changes towards maturity) ; d2V / dIV2 (Vanna) ; d2V / dIVdP (Vega changes with Delta)
        // d2V / dPdIV (Delta changes with IV / Color)
        // dP: dV2 / dPdT (Delta decay / Charm) ; d2V / dP2 (Gamma) ; d2V / dPdIV (Delta changes with IV / Color)
        // probably the more expensive calculation. Given not used for hedging, only calc on request, like EOD position end PF Risk.
        public double HV { get => (double)(OCW?.HistoricalVolatility() ?? 0); }  // historical volatility
        //public double IVAnalytical { get => OCW?.IV(null, null, 0.001) ?? 0; }
        //public double IVNR { get => OCW?.GetIVNewtonRaphson() ?? 0; }

        // First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
        public double Delta { get => OCW?.Delta() ?? 1; }  // dP ; sensitivity to underlying price}
        public decimal Delta100Bp { get => OCW?.Delta100Bp() ?? 0 ; }  // incorrect for equity. Symbol not available...
        public double DeltaZM(int? direction) {  // Adjusted Delta
            return OCW?.DeltaZM(direction) ?? 1;
        }
        public double BandZMLower(int direction)
        {  // Adjusted Delta
            return OCW?.BandZMLower(direction) ?? 1;
        }
        public double BandZMUpper(int direction)
        {  // Adjusted Delta
            return OCW?.BandZMUpper(direction) ?? 1;
        }
        public double Gamma { get => OCW?.Gamma() ?? 0; }  // dP2
        public decimal Gamma100Bp { get => OCW?.Gamma100Bp() ?? 0; }  // dP2

        // Second order derivatives using finite difference
        public double DeltaDecay { get => OCW?.DeltaDecay() ?? 0; }  // dPdT
        public double DPdIV { get => OCW?.DPdIV() ?? 0; }  // dPdIV
        public double DGdP { get => OCW?.DGdP() ?? 0; }  // dP3
        public double GammaDecay { get => OCW?.GammaDecay() ?? 0; }  // dP2dT
        public double DGdIV { get => OCW?.DGdIV() ?? 0; }  // dP2dIV
        public double Theta { get => OCW?.Theta() ?? 0; }  // dT ; sensitivity to time
        public double DTdP { get => OCW?.DTdP() ?? 0; }  // dTdP
        public double ThetaDecay { get => OCW?.ThetaDecay() ?? 0; }  // dT2
        public double DTdIV { get => OCW?.DTdIV() ?? 0; }  // dTdIV
        public double Vega { get => OCW?.Vega() ?? 0; }  // dIV ; sensitivity to volatility
        public double DVegadP { get => OCW?.DVegadP() ?? 0; }  // dVegadP ; vanna
        public double VegaDecay { get => OCW?.VegaDecay() ?? 0; }  // dIVdT
        public double DVegadIV { get => OCW?.DVegadIV() ?? 0; }  // vomma
        public double Rho { get => OCW?.Rho() ?? 0; }  // dR ; sensitivity to interest rate
        public double NPV { get => OCW?.NPV() ?? 0; }  // theoretical price

        //Delta100BpUSD = Positions.Sum(x => x.PfDelta100BpUSD);
        //Gamma100BpUSD = Positions.Sum(x => x.PfGamma100BpUSD);
        //ThetaUSD = Positions.Sum(x => x.PfThetaUSD);
        //Vega100BpUSD

        public GreeksPlus(OptionContractWrap? ocw = null)
        {
            OCW = ocw;
        }
    }
}
