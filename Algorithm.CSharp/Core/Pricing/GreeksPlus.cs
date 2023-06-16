
namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class GreeksPlus
    {
        private OptionContractWrap ocw;
        // Besides HV, all meant to be sensitivities to the underlying price, P, volatility, IV, time, T, and interest rate, R. 
        // No quantity, price besides typical (delta, dPUnderlying=1; vega dHV=100 Bp; theta dTime = 1 day)
        // dIV: dV2 / dIVdT (Vega changes towards maturity) ; d2V / dIV2 (Vanna) ; d2V / dIVdP (Vega changes with Delta)
        // d2V / dPdIV (Delta changes with IV / Color)
        // dP: dV2 / dPdT (Delta decay / Charm) ; d2V / dP2 (Gamma) ; d2V / dPdIV (Delta changes with IV / Color)
        // probably the more expensive calculation. Given not used for hedging, only calc on request, like EOD position end PF Risk.
        public double HV { get; }  // historical volatility

        // First order derivatives: dV / dt (Theta) ; dV / dP (Delta) ; dV / dIV (Vega)
        public double Delta { get => ocw?.Delta() ?? 1; }  // dP ; sensitivity to underlying price
        public double Gamma { get => ocw?.Gamma() ?? 0; }  // dP2

        // Second order derivatives using finite difference
        public double DeltaDecay { get => ocw?.DeltaDecay() ?? 0; }  // dPdT
        public double DPdIV { get => ocw?.DPdIV() ?? 0; }  // dPdIV
        public double DGdP { get => ocw?.DGdP() ?? 0; }  // dP3
        public double GammaDecay { get => ocw?.GammaDecay() ?? 0; }  // dP2dT
        public double DGdIV { get => ocw?.DGdIV() ?? 0; }  // dP2dIV
        public double Theta { get => ocw?.Theta() ?? 0; }  // dT ; sensitivity to time
        public double DTdP { get => ocw?.DTdP() ?? 0; }  // dTdP
        public double ThetaDecay { get => ocw?.ThetaDecay() ?? 0; }  // dT2
        public double DTdIV { get => ocw?.DTdIV() ?? 0; }  // dTdIV
        public double Vega { get => ocw?.Vega() ?? 0; }  // dIV ; sensitivity to volatility
        public double DIVdP { get => ocw?.DIVdP() ?? 0; }  // dIVdP ; vanna
        public double VegaDecay { get => ocw?.VegaDecay() ?? 0; }  // dIVdT
        public double DIV2 { get => ocw?.DIV2() ?? 0; }  // dIV2 ; vomma
        public double Rho { get => ocw?.Rho() ?? 0; }  // dR ; sensitivity to interest rate
        public double TheoreticalPrice { get => ocw?.TheoreticalPrice() ?? 0; }  // theoretical price

        //Delta100BpUSD = Positions.Sum(x => x.PfDelta100BpUSD);
        //Gamma100BpUSD = Positions.Sum(x => x.PfGamma100BpUSD);
        //ThetaUSD = Positions.Sum(x => x.PfThetaUSD);
        //Vega100BpUSD

        public GreeksPlus(OptionContractWrap? ocw = null, double hv = 0)
        {
            this.ocw = ocw;
            HV = hv;
        }
    }
}
