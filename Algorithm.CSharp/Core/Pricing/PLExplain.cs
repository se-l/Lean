using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Risk;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class PLExplain
    {
        private Position _position;

        private double positionQuantity;

        private decimal s0;
        private DateTime ts0;
        private double iv0;
        private double hv0;
        private double r0;
        private decimal iVdS0;
        private GreeksPlus g0;
        //private SnapsProcessed

        /// <summary>
        /// Consider removing PL_ prefix from all properties
        /// </summary>
        public decimal PL_DeltaFillMid { get; internal set; }  // Bid/Ask difference to midpoint. Positive if we earned the spread.
        public decimal PL_Fee { get; internal set; }
        public double PL_DeltaIVdS { get; internal set; }
        public double PL_Delta { get; internal set; }  // dS
        public double PL_Gamma { get; internal set; }  // dS2
        public double PL_DeltaDecay { get; internal set; }  // dSdT
        public double PL_Theta { get; internal set; }  // dT
        public double PL_ThetaDecay { get; internal set; }  // dT2
        public double PL_Vega { get; internal set; }  // dIV
        public double PL_Vanna { get; internal set; }  // dSdIV / dIVdS (change of Delta with IV / change of Vega with underlying price)
        public double PL_VegaDecay { get; internal set; }  // dIVdT
        public double PL_Volga { get; internal set; }  // dIV2, Vomma / Volga
        public double PL_Rho { get; internal set; }  // dR ; sensitivity to interest rate
        public double PL_dS3 { get; internal set; }  // dS3
        public double PL_GammaDecay { get; internal set; }  // dS2dT
        public double PL_dGammaDIV { get; internal set; }  // dS2dIV
        public double PL_Total { get; internal set; }  // total PnL
        public double Price1Theoretical { get; internal set; }
        public double Price1Deviation { get; internal set; }

        public PLExplain(Position position)
        {
            _position = position;

            positionQuantity = (double)(position.Quantity * position.Multiplier);  // Q over lifetime of position

            //s0 = position.Trade0.Mid0Underlying;
            ts0 = position.Trade0.Ts0;
            //iv0 = position.Trade0.IVMid0;
            //hv0 = position.Trade0.HistoricalVolatility;
            //r0 = 0;
            //iVdS0 = position.SurfaceIVdS;
            //g0 = position.Trade0.Greeks;

            decimal tradeQuantity = position.Trade0.Quantity * position.Multiplier;  // Trade fill Q at fill. Instant.
            var deltaMid = position.Trade0.Delta2MidFill;

            // Non-Greek PL
            PL_DeltaFillMid = Math.Abs(tradeQuantity) * deltaMid; // Gained on fill. Counted as unrealized until next trade changes the position. Only use Trade0, otherwise double counting....
            PL_Fee = position.Trade0.Fee;


            //if (g == null) { return; }
            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
            // https://medium.com/hypervolatility/option-greeks-and-hedging-strategies-14101169604e  //Δ Option Value ≈ Delta * ΔS + ½ Gamma * ΔS + 1/6 Speed * ΔS
        }

        private decimal DS(decimal s1, decimal s0) => s1 - s0;
        private double DT(DateTime ts1, DateTime ts0) => (ts1 - ts0).TotalSeconds / 86400;
        private double DIV(double iv1, double iv0) => (iv1 == 0 || iv0 == 0) ? 0 : iv1 - iv0;  // On expiration IV appears to be returned as zero. shouln't use that.
        private double DR(double r1, double r0) => r1 - r0;
        private decimal DIVdS(decimal iVdS1, decimal iVdS0) => iVdS1 - iVdS0;

        public PLExplain Update(List<PositionSnap> snaps)
        {
            PositionSnap snap0 = null;
            PositionSnap snap1 = null;

            var ts1 = _position.Trade1?.Ts0 ?? snaps.Last().Ts0;

            foreach (var snap in snaps.Where(s => s.Ts0 >= ts0 && s.Ts0 <= ts1))
            {
                if (snap0 == null)
                {                     
                    snap0 = snap;
                    continue;
                }
                g0 = snap0.Greeks;
                snap1 = snap;
                double dS = (double)DS(snap1.Mid0Underlying, snap0.Mid0Underlying);
                double dT = DT(snap1.Ts0, snap0.Ts0);
                double dIV = DIV(snap1.IVMid0, snap0.IVMid0);
                double dIVdS = (double)DIVdS(snap1.SurfaceIVdS, snap0.SurfaceIVdS);
                double dR = DR(0, 0);
                //double dS = (double)DS(_position.Trade1?.Mid0Underlying ?? snap.Mid0Underlying);
                //double dT = DT(_position.Trade1?.Ts0 ?? snap.Ts0);
                //double dIV = DIV(_position.Trade1?.IVMid0 ?? snap.IVMid0);
                //double dIVdS = (double)DIVdS(_position.Trade1?.SurfaceIVdS ?? snap.SurfaceIVdS);

                //s0 = snap.Mid0Underlying;
                //ts0 = snap.Ts0;
                //iv0 = snap.IVMid0;
                //iVdS0 = snap.SurfaceIVdS;

                // Dont double count Greeks. Snapping more often may only correct for higher order terms, given BSM is a complete model. Adjusted by IV.
                // double dDelta = snap.Greeks.Delta - g0.Delta;
                // double dVega = snap.Greeks.Vega - g0.Vega;
                // ...

                PL_DeltaIVdS += positionQuantity * g0.Vega * dIVdS * dS;

                // Greek PLs

                // Delta
                PL_Delta += positionQuantity * g0.Delta * dS;  // dS
                PL_Gamma += positionQuantity * 0.5 * g0.Gamma * Math.Pow(dS, 2);  // dS2
                PL_DeltaDecay += positionQuantity * g0.DeltaDecay * dS * dT;  // dSdT, Charm.
                PL_Vanna += positionQuantity * g0.DDeltadIV * dIV * dS;  // dSdIV, Vanna, ( dSdIV == dIVdS ) - https://optionstradingiq.com/vanna-greek/

                // Theta
                PL_Theta += positionQuantity * g0.Theta * dT;
                // dTdS,Charm included with DeltaDecay above
                PL_ThetaDecay += positionQuantity * 0.5 * g0.ThetaDecay * Math.Pow(dT, 2);  // dT2.
                                                                                           // dTdIV - included with dIVdT below

                // Vega
                PL_Vega += positionQuantity * g0.Vega * dIV;  // dIV
                                                             // Vanna - dIVdS, already above as dSdIV  // dIVdS
                PL_VegaDecay += positionQuantity * g0.VegaDecay * dIV * dT;  // dIVdT, Veta
                PL_Volga += positionQuantity * 0.5 * g0.DIV2 * Math.Pow(dIV, 2);  // dIV2 - Vomma


                // Greeks - 3rd order
                PL_dS3 += positionQuantity * (1.0 / 6.0) * g0.DS3 * Math.Pow(dS, 3);  // dS3, Speed. Change of Gamma with Underlying Price.
                PL_GammaDecay += positionQuantity * 0.5 * g0.GammaDecay * Math.Pow(dS, 2) * dT;  // dS2dT, Color ; knife edge
                PL_dGammaDIV += positionQuantity * 0.5 * g0.DS2dIV * dIV * Math.Pow(dS, 2);  // dS2dIV, Zomma

                // Rho - no change simulated as of now.
                PL_Rho = positionQuantity * g0.Rho * dR;

                Price1Theoretical = (double)s0 +
                    g0.Delta * dS +
                    0.5 * g0.Gamma * Math.Pow(dS, 2) +
                    g0.DeltaDecay * dS * dT +
                    1 / 6 * g0.DS3 * Math.Pow(dS, 3) +
                    0.5 * g0.GammaDecay * Math.Pow(dS, 2) * dT +

                    g0.DDeltadIV * dIV * dS +
                    0.5 * g0.DS2dIV * dIV * Math.Pow(dS, 2) +

                    g0.Theta * dT +
                    0.5 * g0.ThetaDecay * Math.Pow(dT, 2) +

                    g0.Vega * dIV +
                    g0.VegaDecay * dIV * dT +
                    0.5 * g0.DIV2 * Math.Pow(dIV, 2) +

                    g0.Rho * dR;
                Price1Deviation = Price1Theoretical / (double)_position.Mid1;

                snap0 = snap1;
            }
            // Total
            PL_Total =
                (double)PL_DeltaFillMid + (double)PL_Fee + // Non-Greek
                PL_Delta + PL_Gamma + PL_DeltaDecay + PL_Vanna + PL_dS3 + PL_GammaDecay + PL_dGammaDIV + // dS
                PL_Theta + PL_ThetaDecay + // dT
                PL_Vega + PL_VegaDecay + PL_Volga + // dIV
                PL_Rho;  // dR
            //if (_position.Security.Symbol.Value == "HPE   230915P00017000" && PL_VegaDecay > 50)
            if (_position.Security.Symbol.Value == "DELL" && (PL_Total - (double)_position.PL) > 1)
            {
                var a = 1;
            }
            return this;
        }
    }
}
