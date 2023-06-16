using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

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

        public Position(Foundations algo, Symbol symbol)
        {
            Symbol = symbol;
            Security = algo.Securities[symbol];
            if (Security.Type == SecurityType.Option)
            {
                UnderlyingSymbol = ((Option)Security).Underlying.Symbol;
            }
            TimeCreated = algo.Time;
            Symbol = symbol;
            SecurityType = Security.Type;

            //Trades = algo.Transactions.GetOrders(x => x.Symbol == symbol && x.Value != 0 && x.CreatedTime >= Since).Select(o => new Trade(algo, o));
            Trades = algo.Transactions.GetOrders(x => x.Symbol == symbol && x.Value != 0).Select(o => new Trade(algo, o));
            TradesCount = Trades.Count();
        }
    }
}
