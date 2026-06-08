using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{    public class RiskLimitExceededEventArgs : EventArgs
    {
        public Symbol Symbol { get; }
        public RiskLimitType LimitType { get; }
        public RiskLimitScope LimitScope { get; }
        public RiskLimitExceededEventArgs(Symbol symbol, RiskLimitType limitType, RiskLimitScope limitScope)
        {
            Symbol = symbol;
            LimitType = limitType;
            LimitScope = limitScope;
        }
    }
}
