using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{    public class QuoteDiscount
    {
        public Metric Metric { get; internal set; }
        public double SpreadFactor { get; internal set; }
        public double RiskCurrent;
        public double RiskIfFilled;
        public double RiskBenefit;
        public QuoteDiscount(Metric metric, double spreadFactor, double riskCurrent, double riskIfFilled, double riskBenefit)
        {
            Metric = metric;
            SpreadFactor = spreadFactor;
            RiskCurrent = riskCurrent;
            RiskIfFilled = riskIfFilled;
            RiskBenefit = riskBenefit;
        }

        public override string ToString()
        {
            return $"{Metric.ToString().ToUpper()}-{SpreadFactor}-{RiskCurrent}-{RiskIfFilled}-{RiskBenefit}";
        }

    }
}
