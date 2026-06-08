using System;
using System.Collections.Generic;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class MarketRegimesChangeEventArgs : EventArgs
    {
        public Symbol Symbol { get; }
        public HashSet<MarketRegime> MarketRegimes { get; }

        public MarketRegimesChangeEventArgs(Symbol symbol, HashSet<MarketRegime> marketRegimes)
        {
            Symbol = symbol;
            MarketRegimes = marketRegimes;
        }
    }
}
