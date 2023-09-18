using System;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Algorithm.CSharp.Core.Events;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

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
                    var scopedTickets = orderTickets.Keys.Where(k => k.SecurityType == SecurityType.Option && k.Underlying == newBidAsk.Symbol && orderTickets[k].Count > 0).ToList();  // ToList, avoid concurrent modification error
                    foreach (Symbol symbol in scopedTickets)
                    {
                        UpdateLimitPrice(symbol);
                    }
                    UpdateLimitPrice(newBidAsk.Symbol);
                    PfRisk.IsRiskLimitExceededZM(newBidAsk.Symbol);
                }
            }

            if (@event is OrderEvent orderEvent && (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled))
            {
                UpdateOrderFillData(orderEvent);
                UpdatePositionLifeCycle(orderEvent);
                LogOnEventOrderFill(orderEvent);
                GetDesiredOrders();
                PfRisk.IsRiskLimitExceededZM(orderEvent.Symbol);
                RiskPnLProfiles[Underlying(orderEvent.Symbol)].Update();
            }

            if (@event is EventRiskLimitExceeded riskLimitExceeded)
            {
                switch (riskLimitExceeded.LimitType)
                {
                    case RiskLimitType.Delta:
                        HedgeOptionWithUnderlyingZM(riskLimitExceeded.Symbol);
                        break;
                    //case RiskLimitType.Gamma:
                    //    CancelRiskIncreasingOrderTickets(RiskLimitType.Gamma);
                    //    HedgeGammaRisk(riskLimitExceeded.Symbol);
                    //    break;
                }
            }
        }
    }
}
