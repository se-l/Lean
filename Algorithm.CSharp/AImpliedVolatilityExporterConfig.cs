using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    public class AImpliedVolatilityExporterConfig : AlgoConfig
    {
        public HashSet<string> Ticker { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        
    }
}
