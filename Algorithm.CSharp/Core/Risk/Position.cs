using QuantConnect.Securities;
using System;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Position : TradeBase
    {
        public override decimal Quantity { get => Holding.Quantity; }
        public SecurityHolding Holding { get; }
        public override decimal P1 { get => Algo.MidPrice(Symbol); }
        public override decimal Bid1 { get => Algo.Securities[Symbol].BidPrice; }
        public override decimal Ask1 { get => Algo.Securities[Symbol].AskPrice; }
        public override decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }
        public override decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public override decimal Bid1Underlying { get => Algo.Securities[UnderlyingSymbol].BidPrice; }
        public override decimal Ask1Underlying { get => Algo.Securities[UnderlyingSymbol].AskPrice; }
        public decimal UnrealizedProfit { get => Holding.UnrealizedProfit; }

        public Position(Foundations algo, SecurityHolding holding)
        {
            if (algo == null || holding == null) { throw new ArgumentNullException("Position.Constructor"); } // TODO: Add more checks for nulls and throw exceptions

            Algo = algo;
            Holding = holding;
            Symbol = holding.Symbol;
            Security = algo.Securities[Symbol];
            TimeCreated = algo.Time;
            SecurityType = Security.Type;            
        }     
    }
}
