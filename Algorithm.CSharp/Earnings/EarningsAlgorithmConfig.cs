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
        public string WsHost { get; set; }
        public int WsPort { get; set; }
    }
}
