using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Util;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public event EventHandler<NewBidAskEventArgs> NewBidAskEventHandler;
        public event EventHandler<RiskLimitExceededEventArgs> RiskLimitExceededEventHandler;
        public event EventHandler<TradeEventArgs> TradeEventHandler;
        public void Publish(NewBidAskEventArgs e) => NewBidAskEventHandler?.Invoke(this, e);
        public void Publish(RiskLimitExceededEventArgs e) => RiskLimitExceededEventHandler?.Invoke(this, e);
        public void Publish(TradeEventArgs e) => TradeEventHandler?.Invoke(this, e);
        public void OnNewBidAskEventUpdateLimitPrices(object? sender, NewBidAskEventArgs newBidAsk)
        {
            if (newBidAsk.Symbol.SecurityType == SecurityType.Option)
            {
                UpdateLimitPrice(newBidAsk.Symbol);
            }
            else if (newBidAsk.Symbol.SecurityType == SecurityType.Equity)
            {
                // LogOnEventNewBidAsk(newBidAsk);  // Because Backtest and LiveTrading differ significantly in price update logs.
                var scopedTickets = orderTickets.Keys.Where(k => k.SecurityType == SecurityType.Option && k.Underlying == newBidAsk.Symbol && orderTickets[k].Count > 0).ToHashSet();  // ToHashSet(): ToList, avoid concurrent modification error
                scopedTickets.Add(newBidAsk.Symbol);
                scopedTickets.DoForEach(s => UpdateLimitPrice(s));
            }
        }
        public void OnNewBidAskEventCheckRiskLimits(object? sender, NewBidAskEventArgs newBidAsk)
        {
            if (newBidAsk.Symbol.SecurityType == SecurityType.Equity)
            {
                PfRisk.CheckHandleDeltaRiskExceedingBand(newBidAsk.Symbol);
            }
        }

        public void OnRiskLimitExceededEventHedge(object? sender, RiskLimitExceededEventArgs e)
        {
            switch (e.LimitType)
            {
                case RiskLimitType.Delta:
                    if (GetHedgingMode(e.Symbol) == HedgingMode.Zakamulin)
                    {
                        HedgeOptionWithUnderlyingZMBands(e.Symbol);
                    } 
                    else
                    {
                        HedgeOptionWithUnderlying(e.Symbol);
                    }
                    break;
            }
        }

        public void StoreRealizedPosition(Position position)
        {
            if (!PositionsRealized.ContainsKey(position.Symbol))
            {
                PositionsRealized[position.Symbol] = new();
            };
            PositionsRealized[position.Symbol].Add(position);
        }
    }
}
