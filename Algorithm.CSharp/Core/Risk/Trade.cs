using System;
using System.Collections.Generic;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class Trade  // needs to be stored. Cannot be constructed from Brokerage API...
    {
        public Symbol Symbol { get; }
        public Security Security { get; }
        public Symbol UnderlyingSymbol
        {
            get => SecurityType switch
            {
                SecurityType.Equity => Symbol,
                SecurityType.Option => ((Option)Security).Underlying.Symbol,
                _ => throw new NotSupportedException()
            };
        }
        public decimal Fee { get => algo.orderFillDataTN1.TryGetValue(Order.Id, out OrderFillData ofd) ? -ofd.Fee : -1; }
        public DateTime Since { get; set; }
        public DateTime TimeCreated { get; }

        public SecurityType SecurityType { get; }
        public virtual decimal Quantity { get => Order.Status == OrderStatus.Filled ? Order.Quantity : 0; }  // Ignore PartialFill for now. Likely need to reference ticket....
        public int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public Order? Order { get; }
        public decimal Spread { get => Security.AskPrice - Security.BidPrice; } 

        public decimal PriceFillAvg { get => Order?.Price ?? Security.Price; }
        public DateTime FirstFillTime { get => (DateTime)(Order?.LastFillTime?.ConvertFromUtc(algo.TimeZone) ?? Order?.Time); }
        public DateTime Ts0 { get => FirstFillTime; }
        public decimal P0 { get => PriceFillAvg; }
        public decimal BidTN1 { get; internal set; } = 0;
        public decimal AskTN1 { get; internal set; } = 0;
        public decimal MidTN1 { get => (BidTN1 + AskTN1) / 2; }
        public decimal Bid0 { get; internal set; }
        public decimal Ask0 { get; internal set; }
        public decimal Mid0 { get => (Bid0 + Ask0) / 2; }


        public virtual DateTime Ts1 { get => algo.Time; }
        public virtual decimal P1 { get => algo.MidPrice(Symbol); }
        public virtual decimal Bid1 { get => algo.Securities[Symbol].BidPrice; }
        public virtual decimal Ask1 { get => algo.Securities[Symbol].AskPrice; }
        public decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }


        public decimal DP { get => P1 - PriceFillAvg; }
        public double IVBid0 { get => SecurityType switch {
            SecurityType.Option => OptionContractWrap.E(algo, (Option)Security).IV(Bid0, Mid0Underlying, 0.001) ?? 0,
            _ => 0
            }; }
        public double IVAsk0
        {
            get => SecurityType switch
            {
                SecurityType.Option => OptionContractWrap.E(algo, (Option)Security).IV(Ask0, Mid0Underlying, 0.001) ?? 0,
                _ => 0
            };
        }
        public double IVMid0 { get => (IVBid0 + IVAsk0) / 2; }
        public virtual double IVBid1
        {
            get => SecurityType switch
            {
                SecurityType.Option => algo.RollingIVBid[Symbol].Current,
                _ => 0
            };
        }
        public virtual double IVAsk1 { get => SecurityType switch
        {
            SecurityType.Option => algo.RollingIVAsk[Symbol].Current,
            _ => 0
        };}
        public double IVMid1 { get => (IVBid1 + IVAsk1) / 2; }
        public double DMidIV { get => IVMid1 - IVMid0; }
        public  double IVLongBid1
        {
            get => SecurityType switch
            {
                SecurityType.Option => algo.RollingIVBid[Symbol].LongMean,
                _ => 0
            };
        }
        public double IVLongAsk1
        {
            get => SecurityType switch
            {
                SecurityType.Option => algo.RollingIVAsk[Symbol].LongMean,
                _ => 0
            };
        }
        public double IVLongMid1 { get => (IVLongBid1 + IVLongAsk1) / 2; }

        public decimal BidTN1Underlying { get; } = 0;
        public decimal AskTN1Underlying { get; } = 0;
        public decimal MidTN1Underlying { get => (BidTN1Underlying + AskTN1Underlying) / 2; }
        public decimal Bid0Underlying { get; } = 0;
        public decimal Ask0Underlying { get; } = 0;
        public decimal Mid0Underlying { get => (Bid0Underlying + Ask0Underlying) / 2; }
        public decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public virtual decimal Bid1Underlying { get => algo.Securities[UnderlyingSymbol].BidPrice; }
        public virtual decimal Ask1Underlying { get => algo.Securities[UnderlyingSymbol].AskPrice; }
        public decimal DPUnderlying { get => Mid1Underlying - Mid0Underlying; }

        public decimal PL { get => (P1 - P0) * Quantity * Multiplier + Fee; }
        public decimal UnrealizedProfit { get => TotalUnrealizedProfit(); }
        public PLExplain PLExplain { get => GetPLExplain(); }
        public Dictionary<Symbol, double> BetaUnderlying { get => new() { { algo.spy, algo.Beta(algo.spy, UnderlyingSymbol, 20, Resolution.Daily) } }; }
        public Dictionary<Symbol, double> Correlations { get => new() { { algo.spy, algo.Correlation(algo.spy, UnderlyingSymbol, 20, Resolution.Daily) } }; }

        private GreeksPlus greeks0;
        public GreeksPlus Greeks0 { get => GetGreeks0(); }

        private GreeksPlus greeks1;
        public GreeksPlus Greeks1 { get => GetGreeks1(); }
        public double DeltaSPY { get => SecurityType switch { 
            SecurityType.Equity => BetaUnderlying[algo.spy] * (double)P1 / (double)algo.MidPrice(algo.spy),
            SecurityType.Option => Greeks1.Delta * BetaUnderlying[algo.spy] * (double)Mid1Underlying / (double)algo.MidPrice(algo.spy),
            _ => 0
        }; }
        public decimal DeltaSPY100BpUSD { get => (decimal)DeltaSPY * Multiplier * Quantity * algo.MidPrice(algo.spy); }

        public decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public decimal ValueWorst { get { return (Quantity > 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }  // Bid1 presumably defaults to zero. For Ask1, infinite loss for short call.
        public decimal ValueClose { get { return algo.Securities[Symbol].Close * Quantity * Multiplier; } }
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


        private readonly Foundations algo;

        public Trade(Foundations algo, Order order)
        {
            this.algo  = algo;

            Order = order;
            Symbol = Order.Symbol;
            Security = algo.Securities[Symbol];
            TimeCreated = algo.Time;
            SecurityType = Security.Type;

            if (algo.orderFillDataTN1.TryGetValue(order.Id, out OrderFillData orderFillDataTN1))
            {
                BidTN1 = orderFillDataTN1?.BidPrice ?? 0;
                AskTN1 = orderFillDataTN1?.AskPrice ?? 0;
                BidTN1Underlying = algo.orderFillDataTN1[order.Id]?.BidPriceUnderlying ?? BidTN1;
                AskTN1Underlying = algo.orderFillDataTN1[order.Id]?.AskPriceUnderlying ?? AskTN1;
            }

            Bid0 = Order.OrderFillData.BidPrice;
            Ask0 = Order.OrderFillData.AskPrice;
            // Data issue. In rare instance. OrderFillData.Bid/MidPrice is 0. Filling with TN1
            if (Bid0 == 0) Bid0 = BidTN1;
            if (Ask0 == 0) Ask0 = AskTN1;

            Bid0Underlying = SecurityType == SecurityType.Option ? Order.OrderFillData.BidPriceUnderlying ?? Bid0 : Bid0;
            Ask0Underlying = SecurityType == SecurityType.Option ? Order.OrderFillData.AskPriceUnderlying ?? Ask0 : Ask0;
        }

        private GreeksPlus GetGreeks()
        {
            return SecurityType switch
            {
                SecurityType.Option => new GreeksPlus(OptionContractWrap.E(algo, (Option)Security)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }

        public double Delta(bool implied=false, double? volatility = null)
        {
            switch (SecurityType) {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    var greeks = Greeks1;
                    if (implied)
                    {
                        greeks.OCW.SetIndependents(volatility: volatility ?? IVMid1);
                    }
                    try
                    {
                        return greeks.Delta;
                    }
                    catch
                    {
                        greeks.OCW.SetIndependents();
                        return greeks.Delta;
                    }
                    
                default:
                    throw new NotSupportedException();
            }
        }

        public decimal TaylorTerm()
        {
            // delta/gamma gamma is unitless sensitivity. No scaling here.
            Option contract = (Option)Security;
            return contract.ContractMultiplier * Quantity * Mid1Underlying;
        }

        public decimal DeltaTotal(bool implied=false, double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Equity => (decimal)Delta(implied, volatility: volatility) * Quantity,
                SecurityType.Option => (decimal)Delta(implied, volatility: volatility) * ((Option)Security).ContractMultiplier * Quantity
            };
        }

        public decimal Delta100BpUSD(bool implied = false, double? volatility = null) 
        { 
            return SecurityType switch
                {
                    // Scaled price into a 1% change / 100BP. That changes times delta and position is risk of position moving by 1%. That's a hundreth of IB's 'Delta Dollar' metric.
                    SecurityType.Equity => (decimal)Delta(implied, volatility: volatility) * Quantity * P1 * 100 * BP,
                    SecurityType.Option => (decimal)Delta(implied, volatility: volatility) * TaylorTerm() * 100 * BP
                };
        }

        public double Gamma()
        {
            return Greeks1.Gamma;
        }
        public decimal Gamma100BpUSD 
        {
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)(0.5 * Math.Pow((double)TaylorTerm(), 2) * Gamma() * Math.Pow((double)(100 * BP), 2))
            };
        }

        public decimal GammaTotal 
        { 
            get => SecurityType switch
        {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Gamma() * ((Option)Security).ContractMultiplier * Quantity
            };
               }

        // Below 2 simplifications assuming a pure options portfolio.
        public double Theta() 
        {
            return Greeks1.Theta;
        }

        public decimal ThetaTotal 
        {
            get => SecurityType switch
            {
                SecurityType.Equity => (decimal)Theta() * Quantity,
                SecurityType.Option => (decimal)Theta() * ((Option)Security).ContractMultiplier * Quantity
            };
        }
        public decimal Theta1DayUSD() 
        {
            return SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Theta * TaylorTerm() * 100*BP
            };
        }

        // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        public double Vega() 
        {
            return Greeks1.Vega;
        }
        public decimal Vega100BpUSD 
        { 
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Vega * TaylorTerm() * 100*BP
            };
        }

        public decimal VegaTotal
        { 
            get => SecurityType switch
            {
                SecurityType.Equity => 0,
                SecurityType.Option => (decimal)Greeks1.Vega * ((Option)Security).ContractMultiplier * Quantity
            };
        }

        private PLExplain GetPLExplain()
        {
            return new PLExplain(
                    Greeks0,
                    (double)DPUnderlying,
                    Time.EachTradeableDay(algo.securityExchangeHours, Ts0, algo.Time).Count(),
                    DMidIV,
                    0,
                    (double)(Quantity * Multiplier),
                    Mid0 - P0,
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

        private GreeksPlus GetGreeks0()
        {
            greeks0 ??= GetGreeks();
            if (SecurityType == SecurityType.Option)
            {
                //greeks0.OCW.SetIndependents(Mid0Underlying, null, IVMid0);
                greeks0.OCW.SetIndependents(Mid0Underlying, Mid0);
            }
            return greeks0;
        }

        private GreeksPlus GetGreeks1()
        {
            greeks1 ??= GetGreeks();
            if (SecurityType == SecurityType.Option)
            {
                greeks1.OCW.SetIndependents(Mid1Underlying, volatility: (double)greeks1.OCW.HistoricalVolatility());  // to be revised. we are now interested in implied Greeks.
            }
            return greeks1;
        }
    }
}
