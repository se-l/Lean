using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core;

namespace QuantConnect.Algorithm.CSharp
{
    public class ADataRecorderConfig : AlgoConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> Ticker { get; set; }
        public string DataFolderOut { get; set; }
    }
}
