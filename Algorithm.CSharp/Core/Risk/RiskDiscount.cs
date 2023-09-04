using System;
using System.Globalization;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class DiscountParams
    {
        public double TargetRisk { get; set; }
        public double X0 { get; set; }
        public double X1 { get; set; }
        public double X2 { get; set; }
        public double CapMin { get; set; }
        public double CapMax { get; set; }
    }
    public class RiskDiscount
    {
        public Symbol Symbol { get; }
        public Metric Metric { get; }
        private DiscountParams DiscountParams { get; }
        public double TargetRisk { get => DiscountParams.TargetRisk; }
        public double X0 { get => DiscountParams.X0; }
        public double X1 { get => DiscountParams.X1; }
        public double X2 { get => DiscountParams.X2; }
        public double CapMin { get => DiscountParams.CapMin; }
        public double CapMax { get => DiscountParams.CapMax; }

        public RiskDiscount(Symbol symbol, Metric metric, double targetRisk, double x0, double x1, double x2, double capMin, double capMax)
        {
            Symbol = symbol;
            Metric = metric;
            DiscountParams = new DiscountParams
            {
                TargetRisk = targetRisk,
                X0 = x0,
                X1 = x1,
                X2 = x2,
                CapMin = capMin,
                CapMax = capMax
            };
        }

        /// <summary>
        /// Helper constructor reading arguments from config.json
        /// </summary>
        /// <param name="metric"></param>
        public RiskDiscount(AMarketMakeOptionsAlgorithmConfig cfg, Symbol symbol, Metric metric)
        {
            Symbol = symbol;
            Metric = metric;
            //DiscountParams = JsonConvert.DeserializeObject<DiscountParams>(Config.Get($"{symbol.Value.ToUpper(CultureInfo.InvariantCulture)}-{metric}-discount-params"));
            DiscountParams = cfg.DiscountParams[$"{symbol.Value.ToUpper(CultureInfo.InvariantCulture)}-{metric}-discount-params"];
        }
        public double Discount(double riskBenefit)
        {
            return X0 + X1 * Math.Abs(riskBenefit) + X2 * Math.Pow(riskBenefit, 2);
        }
    }
}
