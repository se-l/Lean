using System;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class NewTradeEventArgs : EventArgs
    {
        public Symbol Symbol { get; }

        public NewTradeEventArgs(Symbol symbol)
        {
            Symbol = symbol;
        }
    }
}
