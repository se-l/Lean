using QuantConnect.Data.Market;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;


namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class IndexConstituent
    {
        /// <summary>
        /// Weight corresponds to the total absolute values of the positions and for options, corresponding underlying positions.
        /// But price changes over time historically.Weighting returns to calculate correlations.
        /// </summary>
        
        public Symbol Symbol { get; }
        public decimal Weight { get; }  // Equals Position for now.

        private Foundations algo;

        public IndexConstituent(Symbol symbol, Foundations algo, decimal weight = 0)
        {
            this.algo = algo;
            Symbol = symbol;
            Weight = weight;
        }

        public decimal Price( DateTime dt)
        {
            // Get MidPrice if possible. Cannot currently for daily resolution..
            if (dt.Date == algo.Time.Date)
            {
                return algo.Securities[Symbol].Price;
            }
            else
            {
                List<TradeBar> tradeBars = algo.HistoryWrap(Symbol, 5, Resolution.Daily).ToList();
                return tradeBars.Last().Close;
            }
        }
    }
}



