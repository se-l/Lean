using Fasterflect;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// In order to avoid crossing the spread too quickly, we cache option order tickets and the IV when last updated.
    /// </summary>
    public class SpreadBuffer
    {
        private readonly Foundations _algo;
        private readonly Option Option;
        private readonly OrderDirection Direction;
        public double WorstIV;
        public DateTime LastQuoteWorstIV;
        public OptionContractWrap OCW;
        public bool HasLiveOrder;

        public SpreadBuffer(Foundations algo, Option option, OrderDirection orderDirection)
        {
            _algo = algo;
            Option = option;
            Direction = orderDirection;
            Reset();
        }

        public void Reset()
        {
            HasLiveOrder = false;
            LastQuoteWorstIV = DateTime.MinValue;
            OCW = OptionContractWrap.E(_algo, Option, _algo.Time.Date);
            WorstIV = InitWorstIV();
        }
        private decimal NBBO => Direction == OrderDirection.Buy ? Option.BidPrice : Option.AskPrice;
        private decimal Spot => _algo.MidPrice(Underlying(Option.Symbol));
        public decimal Spread => _algo.Spread(Symbol);
        public void Update(OrderTicket ticket)
        {
            if (ticket.OrderType != OrderType.Limit)
            {
                _algo.Log($"{_algo.Time} SpreadBuffer.Update: Order type is not limit: {ticket.OrderType}");
                return;
            }
            if (_algo.orderNewSubmittedUpdated.Contains(ticket.Status))
            {
                HasLiveOrder = true;
            }
            // Switch off if not in use and no ticket is live.
            else
            {
                Reset();
            }
        }
        private double InitWorstIV()
        {
            if (_algo.IsWarmingUp) return 0;

            return OCW.IV(NBBO, Spot, OptionContractWrap.Accuracy);
        }
        public Symbol Symbol => Option.Symbol;
        /// Issue here. Date from ticket is potentially in UTC.
        public bool IsReadyToQuoteWorseIV => _algo.Time - LastQuoteWorstIV > WaitingPeriod;
        public TimeSpan WaitingPeriod => TimeSpan.FromSeconds(_algo.Cfg.BufferIntraSpreadWaitingPeriodSeconds);
        public decimal RatioPriceSpreadCrossed(decimal price)
        {
            return Direction switch
            {
                OrderDirection.Buy => (price - Option.BidPrice) / Spread,
                OrderDirection.Sell => (Option.AskPrice - price) / Spread,
                _ => throw new ArgumentException($"Unknown order direction")
            };
        }

        public double RatioIVSpreadCrossed(double priceIV) => Direction switch
        {
            OrderDirection.Buy => (priceIV - AskIV) / SpreadIV,
            OrderDirection.Sell => (BidIV - priceIV) / SpreadIV,
            _ => throw new ArgumentException($"Unknown order direction {Direction}")
        };

        public bool IsPriceIVDefensive(double priceIV) => Direction switch
        {
            OrderDirection.Buy => priceIV < WorstIV,
            OrderDirection.Sell => priceIV > WorstIV,
            _ => throw new ArgumentException($"Unknown order direction {Direction}")
        };

        public bool IsPriceIVCrossingTolerance(double priceIV) => Direction switch
        {
            OrderDirection.Buy => priceIV > WorstIV,
            OrderDirection.Sell => priceIV < WorstIV,
            _ => throw new ArgumentException($"Unknown order direction {Direction}")
        };

        public void SetWorstIVToAtLeastNBBO()
        {
            WorstIV = Direction == OrderDirection.Buy ? Math.Max(WorstIV, BidIV) : Math.Min(WorstIV, AskIV);
        }

        public void SetWorstIVToMidIV()
        {
            WorstIV = (BidIV + AskIV) / 2;
        }

        private double AskIV => OCW.IV(Option.AskPrice, Spot, OptionContractWrap.Accuracy);
        private double BidIV => OCW.IV(Option.BidPrice, Spot, OptionContractWrap.Accuracy);
        private double WorseIV => Direction switch
        {
            OrderDirection.Buy => Math.Min(WorstIV + RatioSpreadTolerance * SpreadIV, (BidIV + AskIV) / 2),
            OrderDirection.Sell => Math.Max(WorstIV - RatioSpreadTolerance * SpreadIV, (BidIV + AskIV) / 2),
            _ => throw new ArgumentException($"Unknown order direction {Direction}")
        };
        private double RatioSpreadTolerance => _algo.Cfg.BufferIntraSpreadRatioSpreadThreshold;

        /// <summary>
        /// 2 conditions must be true. WaitingPeriod has elapsed and a price request must have been made that touches the new spread area.
        /// </summary>
        /// <exception cref="ArgumentException"></exception>
        private bool TryUpdateWorstIV(double priceIV)
        {
            if (
                IsReadyToQuoteWorseIV &&
                IsPriceIVCrossingTolerance(priceIV) && 
                HasLiveOrder
                )
            {
                _algo.Log($"{_algo.Time} SpreadBuffer.UpdateWorstIV(): {Direction} {Option.Symbol} bidIV={BidIV}, askIV={AskIV}, WorstIV={WorstIV}, WorseIV={WorseIV}, priceIV={priceIV}");
                WorstIV = WorseIV;
                LastQuoteWorstIV = _algo.Time;
                return true;
            }
            return false;
        }

        private double SpreadIV => AskIV - BidIV;

        /// <summary>
        /// Defensive: Don't jump too far into the spread measured in IV. A fast sweeper manages an IV gradient.
        /// All sweepers will buffer quotes going more than 10% into the spread and adjust over the course of 30 seconds.</summary>
        /// </summary>
        public decimal BufferIntraSpreadQuote(decimal price)
        {   
            decimal spot = _algo.MidPrice(Underlying(Option.Symbol));
            double priceIV = OCW.IV(price, Spot, OptionContractWrap.Accuracy);

            //SetWorstIVToAtLeastNBBO();
            SetWorstIVToMidIV();

            if (IsPriceIVCrossingTolerance(priceIV))
            {
                TryUpdateWorstIV(priceIV);
                
                double bufferedIV = Direction switch
                {
                    OrderDirection.Buy => Math.Min(priceIV, WorstIV),
                    OrderDirection.Sell => Math.Max(priceIV, WorstIV),
                    _ => throw new ArgumentException($"Unknown order direction {Direction}")
                };
                if (bufferedIV != priceIV)
                {
                    decimal bufferedPrice = (decimal)OCW.NPV(bufferedIV, spot);
                    _algo.Log($"{_algo.Time} BufferIntraSpreadQuotes: {Direction} {Option.Symbol} - Restricted crossing more spread to bufferedIV={bufferedIV}, bufferedPrice={bufferedPrice}, priceIV={priceIV}, price={price}, bidIV={BidIV}, askIV={AskIV}, WorstIV={WorstIV}");
                    return bufferedPrice;
                }
            }

            return price;
        }
    }
}
