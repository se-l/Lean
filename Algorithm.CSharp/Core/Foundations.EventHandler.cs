using QLNet;
using QuantConnect.Algorithm.CSharp.Core.Events;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public event EventHandler<NewBidAskEventArgs> NewBidAskEventHandler;
        public event EventHandler<RiskLimitExceededEventArgs> RiskLimitExceededEventHandler;
        public void Publish(NewBidAskEventArgs e) => NewBidAskEventHandler?.Invoke(this, e);
        public void Publish(RiskLimitExceededEventArgs e) => RiskLimitExceededEventHandler?.Invoke(this, e);
        public void OnNewBidAskEventUpdateLimitPrices(object? sender, NewBidAskEventArgs newBidAsk)
        {
            if (newBidAsk.Symbol.SecurityType == SecurityType.Option)
            {
                UpdateLimitPrice(newBidAsk.Symbol);
            }
            else if (newBidAsk.Symbol.SecurityType == SecurityType.Equity)
            {
                // LogOnEventNewBidAsk(newBidAsk);  // Because Backtest and LiveTrading differ significantly in price update logs.
                var scopedTickets = orderTickets.Keys.Where(k => k.SecurityType == SecurityType.Option && k.Underlying == newBidAsk.Symbol && orderTickets[k].Count > 0).ToList();  // ToList, avoid concurrent modification error
                foreach (Symbol symbol in scopedTickets)
                {
                    UpdateLimitPrice(symbol);
                }
                UpdateLimitPrice(newBidAsk.Symbol);
            }
        }
        public void OnNewBidAskEventCheckRiskLimits(object? sender, NewBidAskEventArgs newBidAsk)
        {
            if(newBidAsk.Symbol.SecurityType == SecurityType.Equity)
            {                
                PfRisk.IsRiskLimitExceedingBand(newBidAsk.Symbol);
            }
        }

        public void OnRiskLimitExceededEventHedge(object? sender, RiskLimitExceededEventArgs e)
        {
            switch (e.LimitType)
            {
                case RiskLimitType.Delta:
                    HedgeOptionWithUnderlyingZMBands(e.Symbol);
                    break;
            }
        }

        //public void StoreRealizedPLExplain(PLExplain pLExplain)
        //{
        //    if (!PLExplainsRealized.ContainsKey(pLExplain.Symbol))
        //    {
        //        PLExplainsRealized[pLExplain.Symbol] = new();
        //    };
        //    PLExplainsRealized[pLExplain.Symbol].Add(pLExplain);
        //}
    }
}
