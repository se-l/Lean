using System;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class PLExplain
    {
        /// <summary>
        /// Consider removing PL_ prefix from all properties
        /// </summary>
        public double PL_DeltaFillMid { get; }  // Bid/Ask difference to midpoint. Positive if we earned the spread.
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
        public double PL_dVegadP { get; }  // vanna
        public double PL_VegaDecay { get; }  // dIVdT
        public double PL_dVegadIV { get; }  // vomma
        public double PL_Rho { get; }  // dR ; sensitivity to interest rate
        public decimal PL_Fee { get; }  //
        public double PL_Total { get; }  // total PnL

        /// <summary>
        /// 
        /// </summary>
        /// <param name="g"></param>GreeksPlus
        /// <param name="dP"></param>Delta Underlying Price
        /// <param name="dT"></param>Delta Time. Simplified to 1 day.
        /// <param name="dIV"></param>Delta Implied IV. Analytical used.
        /// <param name="position"></param>Quantity of contracts
        /// <param name="deltaMid0"></param>We didnt get filled at Mid exactly, slightly off.
        public PLExplain(GreeksPlus g, double dP = 1.0, double dT = 1, double dIV = 0, double dR = 0, double position = 0, decimal deltaMid = 0, decimal fee=0)
        {
            if (g == null) { return; }
            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
            // https://medium.com/hypervolatility/option-greeks-and-hedging-strategies-14101169604e  //Δ Option Value ≈ Delta * ΔS + ½ Gamma * ΔS + 1/6 Speed * ΔS
            double dIVPct = dIV * 100;

            PL_DeltaFillMid = position * (double)deltaMid;

            PL_Delta = position * g.Delta * dP;
            PL_Vega = position * g.Vega * dIVPct;
            PL_Theta = position * g.Theta * dT;
            PL_Rho = position * g.Rho * dR;

            PL_Gamma = Math.Sign(dP) * position * 0.5 * g.Gamma * Math.Pow(dP, 2);
            PL_dVegadIV = Math.Sign(dIVPct) * position * 0.5 * g.DVegadIV * Math.Pow(dIVPct, 2);  // Vomma
            PL_ThetaDecay = Math.Sign(dT) * position * 0.5 * g.ThetaDecay * Math.Pow(dT, 2);

            PL_dPdIV = position * g.DPdIV * dIV * dP;

            PL_DeltaDecay = position * g.DeltaDecay * dP * dT;
            PL_VegaDecay = position * g.VegaDecay * dIVPct * dT;   
            PL_GammaDecay = Math.Sign(dP) * position * 0.5 * g.GammaDecay * Math.Pow(dP, 2) * dT;  // Color

            PL_dTdP = position * g.DTdP * dP * dT;
            PL_dTdIV = position * g.DTdIV * dIV * dT;
            
            PL_dVegadP = position * g.DVegadP * dIVPct * dP;

            PL_dGdP = position * (1.0 / 6.0) * g.DGdP * Math.Pow(dP, 3);
            PL_dGdIV = Math.Sign(dP) * position * 0.5 * g.DGdIV * dIV * Math.Pow(dP, 2);

            PL_Fee = fee;

            PL_Total = (double)PL_DeltaFillMid + PL_Delta + PL_Gamma + PL_DeltaDecay + PL_dPdIV + PL_dGdP + PL_GammaDecay + PL_dGdIV + PL_Theta + PL_dTdP + 
                PL_ThetaDecay + PL_dTdIV + PL_Vega + PL_dVegadP + PL_VegaDecay + PL_dVegadIV + PL_Rho + (double)PL_Fee;
        }
    }
}
