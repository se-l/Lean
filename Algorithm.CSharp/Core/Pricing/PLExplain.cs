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

        public PLExplain(GreeksPlus g, double dP = 1.0, double dT = 1, double dIV = 0, double dR = 0, double position = 0)
        {
            if (g == null) { return; }
            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
            PL_Delta = position * g.Delta * dP;
            PL_Gamma = position * 0.5 * g.Gamma * Math.Pow(dP, 2);
            PL_DeltaDecay = position * g.DeltaDecay * dT * dP;
            PL_dPdIV = position * g.DPdIV * dP * dIV;
            PL_dGdP = position * (1.0 /6.0) * g.DGdP * Math.Pow(dP, 3);
            PL_GammaDecay = position * 0.5 * g.GammaDecay * Math.Pow(dP, 2);
            PL_dGdIV = position * 0.5 * g.DGdIV * Math.Pow(dP, 2) * dIV;
            PL_Theta = position * g.Theta * dT;
            PL_dTdP = position * g.DTdP * dT * dP;
            PL_ThetaDecay = position * 0.5 * g.ThetaDecay * Math.Pow(dT, 2);
            PL_dTdIV = position * g.DTdIV * dT * dIV;
            PL_Vega = position * g.Vega * dIV;
            PL_dIVdP = position * g.DIVdP * dIV * dP;
            PL_VegaDecay = position * g.VegaDecay * dIV * dT;
            PL_dIV2 = position * 0.5 * g.DIV2 * Math.Pow(dIV, 2);
            PL_Rho = position * g.Rho * dR;
            PL_Total = PL_Delta + PL_Gamma + PL_DeltaDecay + PL_dPdIV + PL_dGdP + PL_GammaDecay + PL_dGdIV + PL_Theta + PL_dTdP + 
                PL_ThetaDecay + PL_dTdIV + PL_Vega + PL_dIVdP + PL_VegaDecay + PL_dIV2 + PL_Rho;
        }
    }
}
