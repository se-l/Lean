using System;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {        public void PublishEvent<T>(T @event, Func<bool> condition = null)
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
                    CancelRiskIncreasingOrderTickets();
                    UpdateLimitPrice(newBidAsk.Symbol);
                    EmitNewFairOptionPrices(newBidAsk.Symbol);
                }
            }
            // (e.Status is OrderStatus.Filled || e.Status is OrderStatus.PartiallyFilled)
            // Can you refactor above  using a shorter notation?

            if (@event is OrderEvent orderEvent && (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled))
            {
                CancelRiskIncreasingOrderTickets();
                HedgePortfolioRiskIs();
                LogRisk();
                IsRiskBandExceeded();
            }

            if (@event is EventHighPortfolioRisk)
            {
                CancelRiskIncreasingOrderTickets();
                HedgePortfolioRiskIs();
            }

            if (@event is EventSignals signals)
            {
                HandleSignals(signals.Signals);
            }

            if (@event is EventNewFairOptionPrice newOptionPrice)
            {
                UpdateLimitPrice(newOptionPrice.Symbol);
            }

            if (@event is EventRiskBandExceeded)
            {
                HedgeWithIndex();
            }
        }
    }
}
