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

        /// <summary>
        /// Requests allowed in flight against the pricer at once. 0 (default) = this machine's CPU
        /// count, which equals the pricer's `julia -t auto` worker count while both run on the same
        /// host. Set it explicitly when the pricer container has its own -t or lives elsewhere:
        /// Env override: PRICERCONCURRENCY.
        /// </summary>
        public int PricerConcurrency { get; set; } = 0;

        public string MQHost { get; set; }
        public int MQPort { get; set; }
        public string MQVirtualHost { get; set; }
        public string MQUser { get; set; }
        public string MQPass { get; set; }
        // Pricer-cache plane (vhost defaults to MQVirtualHost when null/empty).
        public bool PricerCacheEnabled { get; set; } = false;
        public string PricerCacheVHost { get; set; }
        public Dictionary<string, List<int>> EarningsEntryStartTime { get; set; }

    }
}
