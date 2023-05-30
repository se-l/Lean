using System;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class PLExplain
    {
        /// <summary>
        /// Consider removing PL_ prefix from all properties
        /// </summary>
        public double PL_Delta { get; }  // dP ; sensitivity to underlying price
        public double PL_Gamma { get; }  // dP2
        public double PL_DeltaDecay { get; }  // dPdT
        public double PL_dPdIV { get; }  // dPdIV
        public double PL_dGdP { get; }  // dP3
        public double PL_GammaDecay { get; }  // dP2dT
        public double PL_dGdIV { get; }  // dP2dIV
        public double PL_Theta { get; }  // dT ; sensitivity to time
        public double PL_dTdP { get; }  // dTdP
        public double PL_ThetaDecay { get; }  // dT2
        public double PL_dTdIV { get; }  // dTdIV
        public double PL_Vega { get; }  // dIV ; sensitivity to volatility
        public double PL_dIVdP { get; }  // dIVdP ; vanna
        public double PL_VegaDecay { get; }  // dIVdT
        public double PL_dIV2 { get; }  // dIV2 ; vomma
        public double PL_Rho { get; }  // dR ; sensitivity to interest rate
        public double PL_Total { get; }  // total PnL

        public PLExplain(GreeksPlus g, double dP = 1.0, double dT = 0, double dIV = 0, double dR = 0)
        {
            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
            PL_Delta = g.Delta * dP;
            PL_Gamma = 0.5 * g.Gamma * Math.Pow(dP, 2);
            PL_DeltaDecay = g.DeltaDecay * dT * dP;
            PL_dPdIV = g.DPdIV * dP * dIV;
            PL_dGdP = 0.5 * g.DGdP * Math.Pow(dP, 3);
            PL_GammaDecay = 0.5 * g.GammaDecay * Math.Pow(dP, 2);
            PL_dGdIV = 0.5 * g.DGdIV * Math.Pow(dP, 2) * dIV;
            PL_Theta = g.Theta * dT;
            PL_dTdP = g.DTdP * dT * dP;
            PL_ThetaDecay = 0.5 * g.ThetaDecay * Math.Pow(dT, 2);
            PL_dTdIV = g.DTdIV * dT * dIV;
            PL_Vega = g.Vega * dIV;
            PL_dIVdP = g.DIVdP * dIV * dP;
            PL_VegaDecay = g.VegaDecay * dIV * dT;
            PL_dIV2 = 0.5 * g.DIV2 * Math.Pow(dIV, 2);
            PL_Rho = g.Rho * dR;
            PL_Total = PL_Delta + PL_Gamma + PL_DeltaDecay + PL_dPdIV + PL_dGdP + PL_GammaDecay + PL_dGdIV + PL_Theta + PL_dTdP + 
                PL_ThetaDecay + PL_dTdIV + PL_Vega + PL_dIVdP + PL_VegaDecay + PL_dIV2 + PL_Rho;
        }
    }
}
