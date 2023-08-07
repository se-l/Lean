using System;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class EarningsAnnouncement
    {
        public string Symbol { get; set; }
        public DateTime Date { get; set; }
        public DateTime EmbargoPrior { get; set; }
        public DateTime EmbargoPost { get; set; }
    }
}
