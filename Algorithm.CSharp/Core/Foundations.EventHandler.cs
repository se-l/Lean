using System;
using QuantConnect.Orders;
using QuantConnect.Algorithm.CSharp.Core.Events;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public void PublishEvent<T>(T @event, Func<bool> condition = null)
            where T : class
            //
            //Publishes an event to the event queue.
            //condition: later used to register functions to event handlers
            //
        {
            if (@event is EventNewBidAsk newBidAsk)
            {
                if (newBidAsk.Symbol.SecurityType == SecurityType.Option)
                {
                    UpdateLimitPrice(newBidAsk.Symbol);
                }
                else if (newBidAsk.Symbol.SecurityType == SecurityType.Equity)
                {
                    // LogOnEventNewBidAsk(newBidAsk);  // Because Backtest and LiveTrading differ significantly in price update logs.
                    UpdateLimitPrice(newBidAsk.Symbol);
                    pfRisk.IsRiskLimitExceeded(newBidAsk.Symbol);
                    //EmitNewFairOptionPrices(newBidAsk.Symbol);
                }
            }

            if (@event is OrderEvent orderEvent && (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled))
            {
                pfRisk.ResetPositions();
                LogOnEventOrderFill(orderEvent);
                CancelRiskIncreasingOrderTickets();
                pfRisk.IsRiskLimitExceeded(orderEvent.Symbol);
                if (orderEvent.Symbol.SecurityType == SecurityType.Option)
                {
                    OrderOppositeOrder(orderEvent.Symbol);
                }
            }

            //if (@event is EventNewFairOptionPrice newFairOptionPrice)
            //{
            //    UpdateLimitPrice(newFairOptionPrice.Symbol);
            //}

            if (@event is EventSignals signals)
            {
                HandleSignals(signals.Signals);  // just orders all currently...
            }

            if (@event is EventRiskLimitExceeded riskLimitExceeded)
            {
                CancelRiskIncreasingOrderTickets();
                HedgeOptionWithUnderlying(riskLimitExceeded.Symbol);
            }
        }
    }
}
