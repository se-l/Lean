namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public class GreeksPlus
    {
        // Besides HV, all meant to be sensitivities to the underlying price, P, volatility, IV, time, T, and interest rate, R. 
        // No quantity, price besides typical (delta, dPUnderlying=1; vega dHV=100 Bp; theta dTime = 1 day)
        public double HV { get; }  // historical volatility
        public double Delta { get; }  // dP ; sensitivity to underlying price
        public double Gamma { get; }  // dP2
        public double DeltaDecay { get; }  // dPdT
        public double DPdIV { get; }  // dPdIV
        public double DGdP { get; }  // dP3
        public double GammaDecay { get; }  // dP2dT
        public double DGdIV { get; }  // dP2dIV
        public double Theta { get; }  // dT ; sensitivity to time
        public double DTdP { get; }  // dTdP
        public double ThetaDecay { get; }  // dT2
        public double DTdIV { get; }  // dTdIV
        public double Vega { get; }  // dIV ; sensitivity to volatility
        public double DIVdP { get; }  // dIVdP ; vanna
        public double VegaDecay { get; }  // dIVdT
        public double DIV2 { get; }  // dIV2 ; vomma
        public double Rho { get; }  // dR ; sensitivity to interest rate
        public double TheoPrice { get; }  // theoretical price

        //Delta100BpUSD = Positions.Sum(x => x.PfDelta100BpUSD);
        //Gamma100BpUSD = Positions.Sum(x => x.PfGamma100BpUSD);
        //ThetaUSD = Positions.Sum(x => x.PfThetaUSD);
        //Vega100BpUSD


        public GreeksPlus(double hv, double delta, double gamma, double deltaDecay, double dPdIV, double dGdP, double gammaDecay, double dGdIV, double theta, double dTdP, double thetaDecay, double dTdIV, double vega, double dIVdP, double vegaDecay, double dIV2, double rho, double theoPrice)
        {
            HV = hv;
            Delta = delta;
            Gamma = gamma;
            DeltaDecay = deltaDecay;
            DPdIV = dPdIV;
            DGdP = dGdP;
            GammaDecay = gammaDecay;
            DGdIV = dGdIV;
            Theta = theta;
            DTdP = dTdP;
            ThetaDecay = thetaDecay;
            DTdIV = dTdIV;
            Vega = vega;
            DIVdP = dIVdP;
            VegaDecay = vegaDecay;
            DIV2 = dIV2;
            Rho = rho;
            TheoPrice = theoPrice;
        }
    }
}
