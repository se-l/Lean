using System;
using QuantConnect.Securities;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// A wrapper around a filled order or option assignment or security assignment - essentially anything that creates or alters a position. 
    /// Snaps makret attributes as of fill time. Applied to position resulting in a new position, therefore able to 
    /// generate a lifecycle: position0 -> fill/trade -> position1.
    /// </summary>
    public class Trade
    {
        private readonly Foundations _algo;
        public Symbol Symbol { get; internal set; }
        private Security _security;
        public Security Security => _security ??= _algo.Securities[Symbol];
        public Symbol UnderlyingSymbol
        {
            get => SecurityType switch
            {
                SecurityType.Equity => Symbol,
                SecurityType.Option => ((Option)Security).Underlying.Symbol,
                _ => throw new NotSupportedException()
            };
        }
        public OptionRight? OptionRight
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.OptionRight,
                _ => null
            };
        }
        public DateTime? Expiry
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.Date,
                _ => null
            };
        }
        public decimal? StrikePrice
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.StrikePrice,
                _ => null
            };
        }
        public SecurityType SecurityType => Security.Type;
        private Security _securityUnderlying;
        public Security SecurityUnderlying { get => _securityUnderlying ??= _algo.Securities[UnderlyingSymbol]; }
        public int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public decimal Spread0 { get => Ask0 - Bid0; }
        public decimal Bid0Underlying { get; internal set; } = 0;
        public decimal Ask0Underlying { get; internal set; } = 0;
        public decimal Mid0Underlying { get => (Bid0Underlying + Ask0Underlying) / 2; }

        private GreeksPlus _greeks;
        public GreeksPlus Greeks {
            get {
                if (_greeks == null)
                {
                    switch (SecurityType)
                    {
                        case SecurityType.Option:
                            OptionContractWrap ocw = OptionContractWrap.E(_algo, (Option)Security, Ts0.Date);
                            ocw.SetIndependents(Mid0Underlying, Mid0, HistoricalVolatility);
                            _greeks = new GreeksPlus(_algo, ocw).Snap();
                            break;
                        case SecurityType.Equity:
                            _greeks = new GreeksPlus(_algo, Security).Snap();
                            break;
                        default:
                            throw new NotSupportedException();
                    }                    
                }
                return _greeks;
            } }
        public double HistoricalVolatility { get; internal set; }
        public decimal Quantity { get; internal set; }
        public decimal Fee { get; internal set; }
        public decimal PriceFillAvg { get; internal set; }
        public DateTime FirstFillTime { get; internal set; }
        public DateTime Ts0 { get => FirstFillTime; }
        public decimal P0 { get => PriceFillAvg; }
        public decimal BidTN1 { get; internal set; } = 0;
        public decimal AskTN1 { get; internal set; } = 0;
        public decimal MidTN1 { get => (BidTN1 + AskTN1) / 2; }
        public double Ts0Sec { get => (Ts0 - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds; }
        public decimal Delta2MidFill { get => Quantity > 0 ? Mid0 - P0 : P0 - Mid0; }
        public decimal BidTN1Underlying { get; } = 0;
        public decimal AskTN1Underlying { get; } = 0;
        public decimal MidTN1Underlying { get => (BidTN1Underlying + AskTN1Underlying) / 2; }
        public decimal Bid0 { get; internal set; }
        public decimal Ask0 { get; internal set; }
        public decimal Mid0 { get => (Bid0 + Ask0) / 2; }
        public bool FilledByQuoteBarMove
        {
            get => (Quantity > 0) switch
            {
                true => Ask0 <= PriceFillAvg && AskTN1 > PriceFillAvg,
                false => Bid0 > PriceFillAvg && BidTN1 <= PriceFillAvg
            };
        }
        public bool FilledByTradeBar
        {
            get => (Quantity > 0) switch
            {
                true => Ask0 > PriceFillAvg && AskTN1 > PriceFillAvg,
                false => Bid0 <= PriceFillAvg && BidTN1 <= PriceFillAvg
            };
        }
        public double IVBid0 { get; internal set; }
        public double IVAsk0 { get; internal set; }
        public double IVPrice0 { get; internal set; }
        public double IVMid0 { get => (IVBid0 + IVAsk0) / 2; }
        public string Tag { get; internal set; } = "";
        public decimal SurfaceIVdSBid { get; internal set; } // not differentiating the options price here, but getting slope of strike skew.
        public decimal SurfaceIVdSAsk { get; internal set; } // not differentiating the options price here, but getting slope of strike skew.
        public decimal SurfaceIVdS
        {
            get
            {
                if (SurfaceIVdSBid == 0) { return SurfaceIVdSAsk; }
                if (SurfaceIVdSAsk == 0) { return SurfaceIVdSBid; }
                return (SurfaceIVdSBid + SurfaceIVdSAsk) / 2;
            }
        }
        public UtilityOrder? UtilityOrder { get; internal set; }
        public Quote<Option>? Quote { get; internal set; }

        /// <summary>
        /// For initiating a position from an existing holding. No trade fill, quote, utility.
        /// </summary>
        /// <param name="algo"></param>
        /// <param name="holding"></param>
        public Trade(Foundations algo, SecurityHolding holding)
        {
            _algo = algo;
            Fee = 0;
            Symbol = holding.Symbol;
            Quantity = holding.Quantity;
            
            BidTN1 = Security.BidPrice;
            AskTN1 = Security.AskPrice;
            BidTN1Underlying = BidTN1;
            AskTN1Underlying = AskTN1;

            Bid0 = Security.BidPrice;
            Ask0 = Security.AskPrice;

            Bid0Underlying = SecurityUnderlying.BidPrice;
            Ask0Underlying = SecurityUnderlying.AskPrice;


            PriceFillAvg = _algo.MidPrice(Symbol);  // holding.AveragePrice;
            FirstFillTime = algo.Time;
            Snap();
            Tag = "Simulated Fill derived from existing Portfolio Holding.";
        }

        private Trade(Foundations algo, Symbol symbol, decimal quantity, string tag, decimal priceFillAvg, DateTime fillTime)
        {
            _algo = algo;
            Fee = 0;
            Symbol = symbol;
            Quantity = quantity;
            Tag = tag;

            BidTN1 = 0;
            AskTN1 = 0;
            BidTN1Underlying = SecurityUnderlying.BidPrice;
            AskTN1Underlying = SecurityUnderlying.AskPrice;

            Bid0 = 0;
            Ask0 = 0;

            Bid0Underlying = SecurityUnderlying.BidPrice;
            Ask0Underlying = SecurityUnderlying.AskPrice;
            PriceFillAvg = priceFillAvg;

            FirstFillTime = fillTime;

            SnapExpired();
        }

        /// <summary>
        /// OTM Option Expirations
        /// </summary>
        public Trade(Foundations algo, OrderEvent orderEvent, decimal quantity, bool isITM) : this(algo, orderEvent.Symbol, quantity, "Simulated option OTM expiry setting algo.Position to zero.", 0, orderEvent.UtcTime.ConvertFromUtc(algo.TimeZone))
        {
            Tag = isITM ? "Simulated option ITM Exercise/Assignment setting algo.Position to zero." : "Simulated option OTM expiry setting algo.Position to zero.";
            if (Security.Type == SecurityType.Equity)
            {
                PriceFillAvg = _algo.MidPrice(orderEvent.Symbol);
                //PriceFillAvg = order?.OrderFillData.Price ?? Security.Price; // Should be strike price of the option and in Order.Price!
            }
        }

        /// <summary>
        /// ITM Option Assignments / Exercise
        /// </summary>
        //public Trade(Foundations algo, OrderEvent orderEvent, OptionExerciseOrder order) : this(algo, orderEvent.Symbol, orderEvent.FillQuantity, "Simulated option ITM Assignment/Exercise.", 0, orderEvent.UtcTime.ConvertFromUtc(algo.TimeZone)
        //    )
        //{
        //    if (Security.Type == SecurityType.Equity)
        //    {
        //        PriceFillAvg = _algo.MidPrice(orderEvent.Symbol);
        //        //PriceFillAvg = order?.OrderFillData.Price ?? Security.Price; // Should be strike price of the option and in Order.Price!
        //    }
        //}

        public Trade(Foundations algo, OrderEvent orderEvent, Order order)
        {
            _algo = algo;
            Fee = _algo.OrderFillDataTN1.TryGetValue(order.Id, out OrderFillData ofd) ? -ofd.Fee : -1;
            Symbol = order.Symbol;
            Quantity = orderEvent.FillQuantity;
            Tag = order.Tag;

            if (_algo.OrderFillDataTN1.TryGetValue(order.Id, out OrderFillData orderFillDataTN1))
            {
                BidTN1 = orderFillDataTN1?.BidPrice ?? 0;
                AskTN1 = orderFillDataTN1?.AskPrice ?? 0;
                BidTN1Underlying = _algo.OrderFillDataTN1[order.Id]?.BidPriceUnderlying ?? BidTN1;
                AskTN1Underlying = _algo.OrderFillDataTN1[order.Id]?.AskPriceUnderlying ?? AskTN1;
            }

            Bid0 = order.OrderFillData.BidPrice;
            Ask0 = order.OrderFillData.AskPrice;
            // Data issue. In rare instance. OrderFillData.Bid/MidPrice is 0. Filling with TN1
            if (Bid0 == 0) Bid0 = BidTN1;
            if (Ask0 == 0) Ask0 = AskTN1;

            Bid0Underlying = SecurityType == SecurityType.Option ? order.OrderFillData.BidPriceUnderlying ?? Bid0 : Bid0;
            Ask0Underlying = SecurityType == SecurityType.Option ? order.OrderFillData.AskPriceUnderlying ?? Ask0 : Ask0;

            if (SecurityType == SecurityType.Option && algo.Quotes.ContainsKey(order.Id))
            {
                Quote = algo.Quotes[order.Id];
            }
            
            if (order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Equity)
            {
                PriceFillAvg = order?.OrderFillData.Price ?? Security.Price; // Should be strike price of the option and in Order.Price!
            }
            else
            {
                PriceFillAvg = order?.Price ?? Security.Price;
            }

            FirstFillTime = (DateTime)(order?.LastFillTime?.ConvertFromUtc(_algo.TimeZone) ?? order?.Time);
            Snap();
            Quote = algo.Quotes.TryGetValue(order.Id, out Quote<Option> quote) ? quote : null;
            UtilityOrder = algo.OrderTicket2UtilityOrder.TryGetValue(order.Id, out UtilityOrder utilityOrder) ? utilityOrder : null;
        }

        /// <summary>
        /// Constructor for WhatIf Scenario
        /// </summary>
        public Trade(Foundations algo, Symbol symbol, decimal quantity, decimal fillPrice)
        {
            _algo = algo;
            Fee = -1;
            Symbol = symbol;
            Quantity = quantity;

            Bid0 = BidTN1 = Security.BidPrice;
            Ask0 = AskTN1 = Security.AskPrice;
            BidTN1Underlying = Bid0Underlying = SecurityUnderlying.BidPrice;
            AskTN1Underlying = Ask0Underlying = SecurityUnderlying.AskPrice;
            PriceFillAvg = fillPrice;
            FirstFillTime = algo.Time;
            //Snap();
            Tag = "WhatIf Trade";
        }
        private void Snap()
        {
            HistoricalVolatility = (double)SecurityUnderlying.VolatilityModel.Volatility;
            IVBid0 = SecurityType == SecurityType.Option ? OptionContractWrap.E(_algo, (Option)Security, Ts0.Date).IV(Bid0, Mid0Underlying, 0.001) : 0;
            IVAsk0 = SecurityType == SecurityType.Option ? OptionContractWrap.E(_algo, (Option)Security, Ts0.Date).IV(Ask0, Mid0Underlying, 0.001) : 0;
            IVPrice0 = SecurityType == SecurityType.Option ? OptionContractWrap.E(_algo, (Option)Security, Ts0.Date).IV(PriceFillAvg, Mid0Underlying, 0.001) : 0;
            _ = Greeks;
            SurfaceIVdSBid = ToDecimal(_algo.IVSurfaceRelativeStrikeBid[UnderlyingSymbol].IVdS(Symbol) ?? 0);
            SurfaceIVdSAsk = ToDecimal(_algo.IVSurfaceRelativeStrikeAsk[UnderlyingSymbol].IVdS(Symbol) ?? 0);
        }

        private void SnapExpired()
        {
            HistoricalVolatility = (double)SecurityUnderlying.VolatilityModel.Volatility;
            IVBid0 = HistoricalVolatility;
            IVAsk0 = HistoricalVolatility;
            IVPrice0 = HistoricalVolatility;
            _ = Greeks;
            SurfaceIVdSBid = (decimal)HistoricalVolatility;
            SurfaceIVdSAsk = (decimal)HistoricalVolatility;
        }
    }
}

