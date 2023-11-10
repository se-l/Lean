using System;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Risk;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{    public class TradeEventArgs : EventArgs
    {
        public List<Trade> Trades;
        public TradeEventArgs(List<Trade> trades)
        {
            Trades = trades;
        }
    }
}
