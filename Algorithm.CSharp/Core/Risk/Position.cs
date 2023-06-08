using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Position
    {
        public Symbol Symbol { get; }
        public Security Security { get; }
        public Symbol UnderlyingSymbol { get; }
        public DateTime Since { get; set; }
        public DateTime TimeCreated { get; }
        public DateTime LastUpdated { get; }

        public SecurityType SecurityType { get; }
        public decimal Quantity { get; }
        public int Multiplier { get; }

        public int TradesCount { get; set; }

        public IEnumerable<Trade> Trades { get; set; }

        public Position(Foundations algo, Symbol symbol, bool setPL = false)
        {
            Symbol = symbol;           
            Security = algo.Securities[symbol];
            TimeCreated = algo.Time;
            Symbol = symbol;
            SecurityType = Security.Type;

            Trades = algo.Transactions.GetOrders(x => x.Symbol == symbol && x.Value != 0 && x.CreatedTime >= Since).Select(o => new Trade(algo, o, setPL: setPL));
            TradesCount = Trades.Count();
        }
    }
}
