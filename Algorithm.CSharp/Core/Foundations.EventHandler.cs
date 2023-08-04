using System;
using QuantConnect.Orders;
using QuantConnect.Algorithm.CSharp.Core.Events;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;
using System.Linq;

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
                    foreach (Symbol symbol in orderTickets.Keys.Where(k => k.SecurityType == SecurityType.Option && k.Underlying == newBidAsk.Symbol && orderTickets[k].Count > 0))
                    {
                        UpdateLimitPrice(symbol);
                    }
                    UpdateLimitPrice(newBidAsk.Symbol);
                    pfRisk.IsRiskLimitExceededZM(newBidAsk.Symbol);
                    //EmitNewFairOptionPrices(newBidAsk.Symbol);
                }
            }

            if (@event is OrderEvent orderEvent && (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled))
            {
                pfRisk.ResetPositions();
                LogOnEventOrderFill(orderEvent);
                CancelRiskIncreasingOrderTickets();
                pfRisk.IsRiskLimitExceededZM(orderEvent.Symbol);
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
                HedgeOptionWithUnderlyingZM(riskLimitExceeded.Symbol);
            }
        }
    }
}
