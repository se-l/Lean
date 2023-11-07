using System;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{
    public class NewBidAskEventArgs : EventArgs
    {
        public Symbol Symbol;

        public NewBidAskEventArgs(Symbol symbol)
        {
            Symbol = symbol;
        }
    }
}
