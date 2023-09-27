using System;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class PLExplain
    {
        /// <summary>
        /// Consider removing PL_ prefix from all properties
        /// </summary>
        public double PL_DeltaFillMid { get; }  // Bid/Ask difference to midpoint. Positive if we earned the spread.
        public decimal PL_Fee { get; }
        public double PL_DeltaIVdS { get; }
        public double PL_Delta { get; }  // dS
        public double PL_Gamma { get; }  // dS2
        public double PL_DeltaDecay { get; }  // dSdT
        public double PL_Theta { get; }  // dT
        public double PL_ThetaDecay { get; }  // dT2
        public double PL_Vega { get; }  // dIV
        public double PL_Vanna { get; }  // dSdIV / dIVdS (change of Delta with IV / change of Vega with underlying price)
        public double PL_VegaDecay { get; }  // dIVdT
        public double PL_Volga { get; }  // dIV2, Vomma / Volga
        public double PL_Rho { get; }  // dR ; sensitivity to interest rate
        public double PL_dS3 { get; }  // dS3
        public double PL_GammaDecay { get; }  // dS2dT
        public double PL_dGammaDIV { get; }  // dS2dIV
        public double PL_Total { get; }  // total PnL

        /// <summary>
        /// PLExplain
        /// </summary>
        public PLExplain(GreeksPlus g, double dS = 1.0, double dT = 1, double dIV = 0, double dR = 0, double position = 0, decimal deltaMid = 0, decimal tradeQuantity = 0, decimal fee=0, decimal? premiumOnExpiry = null, double iVdS = 0)
        {
            // Greeks:
            // First first-order derivative of option price with respect to dS, dT, dIV
            // Then below the first-order derivative's respective second-order derivate in same order - dS, dT, dIV
            // Then third-order derivatives, Gamma only for now.

            if (g == null) { return; }
            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
            // https://medium.com/hypervolatility/option-greeks-and-hedging-strategies-14101169604e  //Δ Option Value ≈ Delta * ΔS + ½ Gamma * ΔS + 1/6 Speed * ΔS

            // Non-Greek PL
            PL_DeltaFillMid = (double)(tradeQuantity * deltaMid);
            PL_Fee = fee;
            PL_DeltaIVdS = position * g.Vega * iVdS * dS;
            // Greek PL

            // Delta
            PL_Delta = position * g.Delta * dS;  // dS
            PL_Gamma = position * 0.5 * g.Gamma * Math.Pow(dS, 2);  // dS2
            PL_DeltaDecay = position * g.DeltaDecay * dS * dT;  // dSdT, Charm.
            PL_Vanna = position * g.DSdIV * dIV * dS;  // dSdIV, Vanna, ( dSdIV == dIVdS ) - https://optionstradingiq.com/vanna-greek/

            // Theta
            PL_Theta = position * g.Theta * dT + (double)(premiumOnExpiry ?? 0);  // dT, that's theta per day. very small at high dte. Does thetaDecay keep up?
            // dTdS,Charm included with DeltaDecay above
            PL_ThetaDecay = position * 0.5 * g.ThetaDecay * Math.Pow(dT, 2);  // dT2.
            // dTdIV - included with dIVdT below

            // Vega
            PL_Vega = position * g.Vega * dIV;  // dIV
            // Vanna - dIVdS, already above as dSdIV  // dIVdS
            PL_VegaDecay = position * g.VegaDecay * dIV * dT;  // dIVdT, Veta
            PL_Volga = position * 0.5 * g.DIV2 * Math.Pow(dIV, 2);  // dIV2 - Vomma

            // Rho - no change simulated as of now.
            PL_Rho = position * g.Rho * dR;

            // Greeks - 3rd order
            PL_dS3 = position * (1.0 / 6.0) * g.DS3 * Math.Pow(dS, 3);  // dS3, Speed. Change of Gamma with Underlying Price.
            PL_GammaDecay = position * 0.5 * g.GammaDecay * Math.Pow(dS, 2) * dT;  // dS2dT, Color ; knife edge
            PL_dGammaDIV = position * 0.5 * g.DS2dIV * dIV * Math.Pow(dS, 2);  // dS2dIV, Zomma

            // Total
            PL_Total = 
                (double)PL_DeltaFillMid + (double)PL_Fee + // Non-Greek
                PL_Delta + PL_Gamma + PL_DeltaDecay + PL_Vanna + PL_dS3 + PL_GammaDecay + PL_dGammaDIV + // dS
                PL_Theta + PL_ThetaDecay + // dT
                PL_Vega  + PL_VegaDecay + PL_Volga + // dIV
                PL_Rho;  // dR
        }
    }
}
