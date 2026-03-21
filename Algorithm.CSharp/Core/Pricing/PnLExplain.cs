using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Risk;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class PnLExplain
    {
        private readonly Position _position;
        private readonly double positionQuantity;

        private readonly DateTime ts0;
        private GreeksPlus g0;

        /// <summary>
        /// Consider removing PL_ prefix from all properties
        /// </summary>
        public decimal PnLDeltaFillMid { get; internal set; }  // Bid/Ask difference to midpoint. Positive if we earned the spread.
        public decimal PnLFee { get; internal set; }
        public double PnLDeltaIVdS { get; internal set; }
        public double PnLDelta { get; internal set; }  // dS
        public double PnLGamma { get; internal set; }  // dS2
        public double PnLDeltaDecay { get; internal set; }  // dSdT
        public double PnLTheta { get; internal set; }  // dT
        public double PnLThetaDecay { get; internal set; }  // dT2
        public double PnLVega { get; internal set; }  // dIV
        public double PnLVanna { get; internal set; }  // dSdIV / dIVdS (change of Delta with IV / change of Vega with underlying price)
        public double PnLVegaDecay { get; internal set; }  // dIVdT
        public double PnLVolga { get; internal set; }  // dIV2, Vomma / Volga
        public double PnLRho { get; internal set; }  // dR ; sensitivity to interest rate
        public double PnLdS3 { get; internal set; }  // dS3
        public double PnLGammaDecay { get; internal set; }  // dS2dT
        public double PnLdGammaDIV { get; internal set; }  // dS2dIV
        public double PnLTotal { get; internal set; }  // total PnL
        public double PnLNetHedge { get; internal set; }  // total PnL minus PnLDelta
        public double PnLHedgingErrorDelta { get; internal set; }  // total PnL Total / PnL Delta - 1

        public PnLExplain(Position position)
        {
            _position = position;

            positionQuantity = (double)(position.Quantity * position.Multiplier);  // Q over lifetime of position
            ts0 = position.Trade0.Ts0;

            decimal tradeQuantity = position.Trade0.Quantity * position.Multiplier;  // Trade fill Q at fill. Instant.
            decimal deltaMid = position.Trade0.Delta2MidFill;

            // Non-Greek PnL
            PnLDeltaFillMid = Math.Abs(tradeQuantity) * deltaMid; // Gained on fill. Counted as unrealized until next trade changes the position. Only use Trade0, otherwise double counting....
            PnLFee = position.Trade0.Fee;

            //        # missed negative carry cost (interest payments). Not Greeks related though. Goes elsewhere.
            //        # Missing changes in Correlation leading to portfolio valuation differences.
        }

        private static decimal DS(decimal s1, decimal s0) => s1 - s0;
        private static double DT(DateTime ts1, DateTime ts0) => (ts1 - ts0).TotalSeconds / 86400;
        private static double DIV(double iv1, double iv0) => (iv1 == 0 || iv0 == 0) ? 0 : iv1 - iv0;  // On expiration IV appears to be returned as zero. shouln't use that.
        private static double DR(double r1, double r0) => r1 - r0;
        private static decimal DIVdS(decimal iVdS1, decimal iVdS0) => iVdS1 - iVdS0;

        public PnLExplain Update(List<PositionSnap> snaps)
        {
            PositionSnap snap0 = null;
            PositionSnap snap1 = null;
            double dS;
            double dT;
            double dIV;
            double dIVdS;
            double dR;

            DateTime ts1 = _position.Trade1?.Ts0 ?? snaps.Last().Ts0;

            // cannot integrate area under curve like here. The quadratic elements overshoot.
            foreach (var snap in snaps.Where(s => s.Ts0 >= ts0 && s.Ts0 <= ts1))
            {
                if (snap0 == null)
                {                     
                    snap0 = snap;
                    continue;
                }
                g0 = snap0.Greeks;
                snap1 = snap;
                dS = (double)DS(snap1.Mid0Underlying, snap0.Mid0Underlying);
                dT = DT(snap1.Ts0, snap0.Ts0);
                dIV = DIV(snap1.IVMid0, snap0.IVMid0);
                dIVdS = (double)DIVdS(snap1.SurfaceIVdS, snap0.SurfaceIVdS);
                dR = DR(0, 0);

                // Dont double count Greeks. Snapping more often may only correct for higher order terms, given BSM is a complete model, adjusted by IV.
                // double dDelta = snap.Greeks.Delta - g0.Delta;
                // double dVega = snap.Greeks.Vega - g0.Vega;
                // ...

                PnLDeltaIVdS = positionQuantity * g0.Vega * dIVdS * dS;
                PnLVanna = positionQuantity * g0.DDeltadIV * dIV * dS;  // dSdIV, Vanna, ( dSdIV == dIVdS ) - https://optionstradingiq.com/vanna-greek/

                PnLVega = positionQuantity * g0.Vega * dIV;  // dIV
                PnLVegaDecay = positionQuantity * g0.VegaDecay * dIV * dT;  // dIVdT, Veta
                PnLVolga = positionQuantity * 0.5 * g0.DIV2 * Math.Pow(dIV, 2);  // dIV2 - Vomma

                // Greek PLs

                // Delta
                PnLDelta += positionQuantity * g0.Delta * dS;  // dS
                PnLGamma += positionQuantity * 0.5 * g0.Gamma * Math.Pow(dS, 2);  // dS2
                PnLDeltaDecay += positionQuantity * g0.DeltaDecay * dS * dT;  // dSdT, Charm.

                // Theta
                PnLTheta += positionQuantity * g0.Theta * dT;
                // dTdS,Charm included with DeltaDecay above
                PnLThetaDecay += positionQuantity * 0.5 * g0.ThetaDecay * Math.Pow(dT, 2);  // dT2.
                                                                                           // dTdIV - included with dIVdT below

                // Greeks - 3rd order
                PnLdS3 += positionQuantity * (1.0 / 6.0) * g0.DS3 * Math.Pow(dS, 3);  // dS3, Speed. Change of Gamma with Underlying Price.
                PnLGammaDecay += positionQuantity * 0.5 * g0.GammaDecay * dT * Math.Pow(dS, 2);  // dS2dT, Color ; knife edge
                PnLdGammaDIV += positionQuantity * 0.5 * g0.DS2dIV * dIV * Math.Pow(dS, 2);  // dS2dIV, Zomma

                // Rho - no change simulated as of now.
                PnLRho = positionQuantity * g0.Rho * dR;

                snap0 = snap1;
            }

            PnLTotal =
                (double)PnLDeltaFillMid + (double)PnLFee + // Non-Greek
                PnLDelta + PnLGamma + PnLDeltaDecay + PnLVanna + PnLdS3 + PnLGammaDecay + PnLdGammaDIV + // dS
                PnLTheta + PnLThetaDecay + // dT
                PnLVega + PnLVegaDecay + PnLVolga + // dIV
                PnLRho;  // dR
            PnLNetHedge = PnLTotal - PnLDelta;
            PnLHedgingErrorDelta = PnLTotal / PnLDelta - 1;
            return this;
        }
    }
}
