using QuantConnect.Algorithm.CSharp.Core;
using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    public class EarningsAlgorithmConfig : AlgoConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> Ticker { get; set; }
        public string PricerProtocol { get; set; }
        public string PricerHost { get; set; }
        public int PricerPort { get; set; }
        public string MQHost { get; set; }
        public int MQPort { get; set; }
        public string MQVirtualHost { get; set; }
        public string MQUser { get; set; }
        public string MQPass { get; set; }
        public Dictionary<string, List<int>> EarningsEntryStartTime { get; set; }

    }
}
