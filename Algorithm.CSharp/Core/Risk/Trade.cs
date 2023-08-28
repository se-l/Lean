using System;
using System.Collections.Generic;
using QuantConnect.Securities;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Trade : TradeBase
    {
        public override decimal Quantity { get => Order.Status == OrderStatus.Filled ? Order.Quantity : 0; }  // Ignore PartialFill for now. Likely need to reference ticket....
        public decimal Fee { get => Algo.orderFillDataTN1.TryGetValue(Order.Id, out OrderFillData ofd) ? -ofd.Fee : -1; }                
        public Order? Order { get; }
        public decimal PriceFillAvg { get
            {
                if (Order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Option)
                {
                    return 0;
                }
                else if (Order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Equity)
                {
                    return Order?.OrderFillData.Price ?? Security.Price; // Should be strike price of the option and in Order.Price!
                }
                else
                {
                    return Order?.Price ?? Security.Price;
                }
                            }
        }
        public DateTime FirstFillTime { get => (DateTime)(Order?.LastFillTime?.ConvertFromUtc(Algo.TimeZone) ?? Order?.Time); }
        public DateTime Ts0 { get => FirstFillTime; }
        public decimal P0 { get => PriceFillAvg; }
        public override decimal P1 { get => (Order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Option) ? 0 : Mid1; }
        public decimal BidTN1 { get; internal set; } = 0;
        public decimal AskTN1 { get; internal set; } = 0;
        public decimal MidTN1 { get => (BidTN1 + AskTN1) / 2; }
        public double Ts0Sec { get => (Ts0 - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds; }
        public decimal DP { get => P1 - PriceFillAvg; }
        public virtual decimal DeltaFillMid0 { get => P0 - Mid0; }
        public virtual decimal DeltaFillMid1 { get => P1 - Mid1; }
        public decimal BidTN1Underlying { get; } = 0;
        public decimal AskTN1Underlying { get; } = 0;
        public decimal MidTN1Underlying { get => (BidTN1Underlying + AskTN1Underlying) / 2; }
        public decimal DPUnderlying { get => Mid1Underlying - Mid0Underlying; }
        public decimal PL {
            get  
                {
                var pl = (P1 - P0) * Quantity * Multiplier + Fee;
                return pl;
            }
        }
        public decimal UnrealizedProfit { get => TotalUnrealizedProfit(); }
        public PLExplain PLExplain { get => GetPLExplain(); }
        public Dictionary<Symbol, double> BetaUnderlying { get => new() { { Algo.equity1, Algo.Beta(Algo.equity1, UnderlyingSymbol, 20, Resolution.Daily) } }; }
        public Dictionary<Symbol, double> Correlations { get => new() { { Algo.equity1, Algo.Correlation(Algo.equity1, UnderlyingSymbol, 20, Resolution.Daily) } }; }        
        public bool FilledByQuoteBarMove { get => (Quantity > 0) switch
        {
            true => Ask0 <= PriceFillAvg && AskTN1 > PriceFillAvg,
            false => Bid0 > PriceFillAvg && BidTN1 <= PriceFillAvg
        }; 
        }
        public bool FilledByTradeBar { get => (Quantity > 0) switch {
            true => Ask0 > PriceFillAvg && AskTN1 > PriceFillAvg,
            false => Bid0 <= PriceFillAvg && BidTN1 <= PriceFillAvg
        };
        }
        public double DeltaImplied0 { get => GetGreeks0().Delta; }
        public double DeltaImplied1 { get => GetGreeks1().Delta; }
        public double GammaImplied0 { get => GetGreeks0().Gamma; }
        public double GammaImplied1 { get => GetGreeks1().Gamma; }
        public double VegaImplied0 { get => GetGreeks0().Vega; }
        public double VegaImplied1 { get => GetGreeks1().Vega; }
        public double ThetaImplied0 { get => GetGreeks0().Theta; }
        public double ThetaImplied1 { get => GetGreeks1().Theta; }
        public int DDaysToExpiration { get => SecurityType switch
        {
            SecurityType.Option => Greeks0.OCW.DaysToExpiration(Ts0.Date) - Greeks0.OCW.DaysToExpiration(Ts1.Date),
            _ => 0
        }; }
        public virtual double IVBid0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(Algo, (Option)Security, 0, Ts0.Date).IV(Bid0, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public virtual double IVAsk0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(Algo, (Option)Security, 0, Ts0.Date).IV(Ask0, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public virtual double IVPrice0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(Algo, (Option)Security, 0, Ts0.Date).IV(PriceFillAvg, Mid0Underlying, 0.001),
                _ => 0
            };
        }
        public virtual double IVPrice1 { get => IVMid1; }
        public virtual double IVMid0 { get => (IVBid0 + IVAsk0) / 2; }
        public virtual double DMidIV { get => IVMid1 - IVMid0; }
        public virtual double DIVPrice { get => IVPrice1 - IVPrice0; }

        public Trade(Foundations algo, Order order)
        {
            Algo  = algo;
            Order = order;
            Symbol = Order.Symbol;
            Security = Algo.Securities[Symbol];
            TimeCreated = Algo.Time;
            SecurityType = Security.Type;

            if (Algo.orderFillDataTN1.TryGetValue(order.Id, out OrderFillData orderFillDataTN1))
            {
                BidTN1 = orderFillDataTN1?.BidPrice ?? 0;
                AskTN1 = orderFillDataTN1?.AskPrice ?? 0;
                BidTN1Underlying = Algo.orderFillDataTN1[order.Id]?.BidPriceUnderlying ?? BidTN1;
                AskTN1Underlying = Algo.orderFillDataTN1[order.Id]?.AskPriceUnderlying ?? AskTN1;
            }

            if (Order.Type == OrderType.OptionExercise && Security.Type == SecurityType.Option)
            {
                Bid0 = 0;
                Ask0 = 0;

                Bid0Underlying = Bid1Underlying;
                Ask0Underlying = Ask1Underlying;
            }
            else
            {
                Bid0 = Order.OrderFillData.BidPrice;
                Ask0 = Order.OrderFillData.AskPrice;
                // Data issue. In rare instance. OrderFillData.Bid/MidPrice is 0. Filling with TN1
                if (Bid0 == 0) Bid0 = BidTN1;
                if (Ask0 == 0) Ask0 = AskTN1;

                Bid0Underlying = SecurityType == SecurityType.Option ? Order.OrderFillData.BidPriceUnderlying ?? Bid0 : Bid0;
                Ask0Underlying = SecurityType == SecurityType.Option ? Order.OrderFillData.AskPriceUnderlying ?? Ask0 : Ask0;
            }

            GetGreeks0();
            GetGreeks1();
        }

        private PLExplain GetPLExplain()
        {
            return new PLExplain(
                    //GetGreeks0(volatility: IVMid0),
                    GetGreeks0(volatility: (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility),  // Needs to be as of Ts0.Date
                    (double)DPUnderlying,
                    DDaysToExpiration,
                    DIVPrice,  // the realized IV.
                    0,
                    (double)(Quantity * Multiplier),
                    DeltaFillMid1 - DeltaFillMid0, // the realized bit.
                    Fee
                    );
        }

        private decimal TotalUnrealizedProfit()
        {
            OrderDirection orderDirection = NUM2DIRECTION[-Math.Sign(Quantity)];
            var price = orderDirection == OrderDirection.Sell ? Security.BidPrice : Security.AskPrice;
            if (price == 0)
            {
                // Bid/Ask prices can both be equal to 0. This usually happens when we request our holdings from
                // the brokerage, but only the last trade price was provided.
                price = Security.Price;
            }
            return (price - PriceFillAvg) * Quantity * Multiplier;
        }

        public override GreeksPlus GetGreeks(int version = 1)
        {
            DateTime? calculationDate = version == 0 ? Ts0.Date : null;
            return SecurityType switch
            {
                SecurityType.Option => new GreeksPlus(OptionContractWrap.E(Algo, (Option)Security, version, calculationDate)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }
        public override GreeksPlus GetGreeks0(decimal? mid0Underlying = null, decimal? mid0 = null, double? volatility = null, DateTime? calculationDate = null)
        {
            Greeks0 ??= GetGreeks(0);
            if (SecurityType == SecurityType.Option)
            {
                Greeks0.OCW.SetIndependents(mid0Underlying ?? Mid0Underlying, mid0 ?? Mid0, volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility);
            }
            return Greeks0;
        }

        public override GreeksPlus GetGreeks1(decimal? mid1Underlying = null, decimal? mid1 = null, double? volatility = null)
        {
            Greeks1 ??= GetGreeks(1);
            if (SecurityType == SecurityType.Option)
            {
                Greeks1.OCW.SetIndependents(Mid1Underlying, mid1 ?? Mid1, volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility);
            }
            return Greeks1;
        }
        public decimal ExtrinsicValue
        {
            get => SecurityType switch
            {
                SecurityType.Option => PriceFillAvg - ((Option)Security).GetPayOff(Mid0Underlying),
                _ => 0
            };
        }
    }
}
