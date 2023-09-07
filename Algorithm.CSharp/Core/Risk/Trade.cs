using System;
using QuantConnect.Securities;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;

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
        public Security Security { get; internal set; }
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
        public GreeksPlus GetGreeks(int version = 1)
        {
            return SecurityType switch
            {
                // version 0 means as of a time in the past. Passing Fill time to calculate all Greeks as of that date.
                SecurityType.Option => version == 0 ? new(OptionContractWrap.E(_algo, (Option)Security, version, Ts0.Date)) : new(OptionContractWrap.E(_algo, (Option)Security, version)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }
        public GreeksPlus GetGreeks0(decimal? mid0Underlying = null, decimal? mid0 = null, double? volatility = null)
        {
            Greeks0 ??= GetGreeks(0);
            if (SecurityType == SecurityType.Option)
            {
                Greeks0.OCW.SetIndependents(mid0Underlying ?? Mid0Underlying, mid0 ?? Mid0, volatility ?? (double)SecurityUnderling.VolatilityModel.Volatility);
            }
            return Greeks0;
        }
        public SecurityType SecurityType { get; internal set; }
        private Security _securityUnderlying;
        public Security SecurityUnderling { get => _securityUnderlying ??= _algo.Securities[UnderlyingSymbol]; }
        public int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public decimal Spread { get => Security.AskPrice - Security.BidPrice; }
        public decimal Bid0Underlying { get; internal set; } = 0;
        public decimal Ask0Underlying { get; internal set; } = 0;
        public decimal Mid0Underlying { get => (Bid0Underlying + Ask0Underlying) / 2; }
        public GreeksPlus Greeks0 { get; internal set; }
        public double HistoricalVolatility0 { get; internal set; }
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
        public decimal DeltaFillMid0 { get => P0 - Mid0; }
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
        public Quote<Option>? Quote { get; internal set; }
        public double IVBid0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(_algo, (Option)Security, 0, Ts0.Date).IV(Bid0, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public double IVAsk0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(_algo, (Option)Security, 0, Ts0.Date).IV(Ask0, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public double IVPrice0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(_algo, (Option)Security, 0, Ts0.Date).IV(PriceFillAvg, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public double IVMid0 { get => (IVBid0 + IVAsk0) / 2; }

        public Trade(Foundations algo, SecurityHolding holding)
        {
            _algo = algo;
            Fee = 0;
            Symbol = holding.Symbol;
            Security = _algo.Securities[Symbol];
            SecurityType = Security.Type;
            Quantity = holding.Quantity;
            
            BidTN1 = Security.BidSize;
            AskTN1 = Security.AskPrice;
            BidTN1Underlying = BidTN1;
            AskTN1Underlying = AskTN1;

            Bid0 = Security.BidSize;
            Ask0 = Security.AskSize;

            Bid0Underlying = _algo.Securities[UnderlyingSymbol].BidPrice;
            Ask0Underlying = _algo.Securities[UnderlyingSymbol].AskPrice;


            PriceFillAvg = holding.AveragePrice;
            FirstFillTime = algo.Time;
            HistoricalVolatility0 = (double)_algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
            GetGreeks0();
        }

        public Trade(Foundations algo, Order order)
        {
            _algo = algo;
            Fee = _algo.OrderFillDataTN1.TryGetValue(order.Id, out OrderFillData ofd) ? -ofd.Fee : -1;
            Symbol = order.Symbol;
            Security = _algo.Securities[Symbol];
            SecurityType = Security.Type;
            Quantity = order.Quantity;

            if (_algo.OrderFillDataTN1.TryGetValue(order.Id, out OrderFillData orderFillDataTN1))
            {
                BidTN1 = orderFillDataTN1?.BidPrice ?? 0;
                AskTN1 = orderFillDataTN1?.AskPrice ?? 0;
                BidTN1Underlying = _algo.OrderFillDataTN1[order.Id]?.BidPriceUnderlying ?? BidTN1;
                AskTN1Underlying = _algo.OrderFillDataTN1[order.Id]?.AskPriceUnderlying ?? AskTN1;
            }

            if (order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Option)
            {
                Bid0 = 0;
                Ask0 = 0;

                Bid0Underlying = _algo.Securities[UnderlyingSymbol].BidPrice;
                Ask0Underlying = _algo.Securities[UnderlyingSymbol].AskPrice;
            }
            else
            {
                Bid0 = order.OrderFillData.BidPrice;
                Ask0 = order.OrderFillData.AskPrice;
                // Data issue. In rare instance. OrderFillData.Bid/MidPrice is 0. Filling with TN1
                if (Bid0 == 0) Bid0 = BidTN1;
                if (Ask0 == 0) Ask0 = AskTN1;

                Bid0Underlying = SecurityType == SecurityType.Option ? order.OrderFillData.BidPriceUnderlying ?? Bid0 : Bid0;
                Ask0Underlying = SecurityType == SecurityType.Option ? order.OrderFillData.AskPriceUnderlying ?? Ask0 : Ask0;
                Quote = SecurityType == SecurityType.Option ? algo.Quotes[order.Id] : null;
            }

            if (order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Option)
            {
                PriceFillAvg = 0;
            }
            else if (order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Equity)
            {
                PriceFillAvg = order?.OrderFillData.Price ?? Security.Price; // Should be strike price of the option and in Order.Price!
            }
            else
            {
                PriceFillAvg = order?.Price ?? Security.Price;
            }

            FirstFillTime = (DateTime)(order?.LastFillTime?.ConvertFromUtc(_algo.TimeZone) ?? order?.Time);
            HistoricalVolatility0 = (double)_algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
            GetGreeks0();
        }        
    }
}

