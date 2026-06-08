using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class Holding
    {
        public Symbol Symbol { get; }
        public decimal Quantity { get; }

        public Holding(Symbol symbol, decimal quantity)
        {
            Symbol = symbol;
            Quantity = quantity;
        }
        public override bool Equals(object obj)
        {
            return Equals(obj as Holding);
        }

        private bool Equals(Holding other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;
            return EqualityComparer<Symbol>.Default.Equals(Symbol, other.Symbol)
                && Quantity == other.Quantity;
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 23 + (Symbol != null ? Symbol.GetHashCode() : 0);
                hash = hash * 23 + Quantity.GetHashCode();
                return hash;
            }
        }
    }
}
