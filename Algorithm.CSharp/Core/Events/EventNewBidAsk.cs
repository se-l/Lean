using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Events
{    public class EventRiskLimitExceeded
    {
        public Symbol Symbol;
        public RiskLimitType LimitType;
        public RiskLimitScope LimitScope;
        public EventRiskLimitExceeded(Symbol symbol, RiskLimitType limitType, RiskLimitScope limitScope)
        {
            Symbol = symbol;
            LimitType = limitType;
            LimitScope = limitScope;
        }
    }
}
