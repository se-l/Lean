using QuantConnect.Algorithm.CSharp.Core.Risk;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class EventHighPortfolioRisk
    {
        readonly PortfolioRisk pfRisk;

        public EventHighPortfolioRisk(PortfolioRisk pfRisk)
        {
            this.pfRisk = pfRisk;
        }
    }
}
