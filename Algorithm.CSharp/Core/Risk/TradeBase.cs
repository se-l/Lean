using System;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public abstract class TradeBase
    {
        public abstract decimal Quantity { get; }
        public Foundations Algo { get; internal set; }
        public virtual Symbol Symbol { get; internal set; }
        public virtual Security Security { get; internal set; }
        public virtual OptionRight? Right
        {
            get => SecurityType switch
            {
                SecurityType.Option => ((Option)Security).Right,
                _ => null
            };
        }
        public virtual Symbol UnderlyingSymbol
        {
            get => SecurityType switch
            {
                SecurityType.Equity => Symbol,
                SecurityType.Option => ((Option)Security).Underlying.Symbol,
                _ => throw new NotSupportedException()
            };
        }
        public virtual OptionRight? OptionRight
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.OptionRight,
                _ => null
            };
        }
        public virtual DateTime? Expiry
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.Date,
                _ => null
            };
        }
        public virtual decimal? StrikePrice
        {
            get => SecurityType switch
            {
                SecurityType.Option => Symbol.ID.StrikePrice,
                _ => null
            };
        }
        public virtual DateTime Since { get; set; }
        public virtual DateTime TimeCreated { get; internal set; }
        public virtual SecurityType SecurityType { get; internal set; }
        public virtual int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public virtual decimal Spread { get => Security.AskPrice - Security.BidPrice; } 
        public virtual decimal Bid0 { get; internal set; }
        public virtual decimal Ask0 { get; internal set; }
        public virtual decimal Mid0 { get => (Bid0 + Ask0) / 2; }


        public virtual DateTime Ts1 { get => Algo.Time; }
        public virtual decimal P1 { get => Mid1; }
        public virtual decimal Bid1 { get => Algo.Securities[Symbol].BidPrice; }
        public virtual decimal Ask1 { get => Algo.Securities[Symbol].AskPrice; }
        public virtual decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }
        public virtual double IVBid1
        {
            get => SecurityType switch
            {
                SecurityType.Option => Algo.IVBids[Symbol].Current.IV,
                _ => 0
            };
        }
        public virtual double IVAsk1 { get => SecurityType switch
        {
            SecurityType.Option => Algo.IVAsks[Symbol].Current.IV,
            _ => 0
        };}
        public virtual double IVMid1 { get => (IVBid1 + IVAsk1) / 2; }
        public virtual decimal Bid0Underlying { get; internal set; } = 0;
        public virtual decimal Ask0Underlying { get; internal set; } = 0;
        public virtual decimal Mid0Underlying { get => (Bid0Underlying + Ask0Underlying) / 2; }
        public virtual decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public virtual decimal Bid1Underlying { get => Algo.Securities[UnderlyingSymbol].BidPrice; }
        public virtual decimal Ask1Underlying { get => Algo.Securities[UnderlyingSymbol].AskPrice; }

        public virtual GreeksPlus Greeks0 { get; internal set; }

        public virtual GreeksPlus Greeks1 { get; internal set; }
        //public virtual double DeltaSPY { get => SecurityType switch { 
        //    SecurityType.Equity => BetaUnderlying[Algo.spy] * (double)P1 / (double)Algo.MidPrice(Algo.spy),
        //    SecurityType.Option => GetGreeks1().Delta * BetaUnderlying[Algo.spy] * (double)Mid1Underlying / (double)Algo.MidPrice(Algo.spy),
        //    _ => 0
        //}; }
        //public virtual decimal DeltaSPY100BpUSD { get => (decimal)DeltaSPY * Multiplier * Quantity * Algo.MidPrice(Algo.spy); }

        public virtual decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public virtual decimal ValueWorst { get { return (Quantity > 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }  // Bid1 presumably defaults to zero. For Ask1, infinite loss for short call.
        public virtual decimal ValueClose { get { return Algo.Securities[Symbol].Close * Quantity * Multiplier; } }

        public virtual GreeksPlus GetGreeks(int version = 1)
        {
            return SecurityType switch
            {
                SecurityType.Option => new GreeksPlus(OptionContractWrap.E(Algo, (Option)Security, version)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }

        public virtual double Delta()
        {
            switch (SecurityType) {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1().Delta;
                    }
                    catch
                    {
                        Algo.Error($"Delta: Failed to derive Delta. Returning 0. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {GetGreeks1().OCW.HistoricalVolatility()}");
                        return 0;
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public virtual double DeltaZM(double? volatility = null)
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 0;  // Because ZM is to create option bands. Wouldn't want equity to dilute required bands.
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility).DeltaZM((int)Quantity);
                    }
                    catch
                    {
                        Algo.Error($"DeltaZM: Failed to derive DeltaZM. Returning BSM Delta. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {IVMid1}");
                        return Delta();
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public virtual double DeltaZMOffset(double? volatility = null)
        {
            return GetGreeks1(volatility: volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility).DeltaZMOffset((int)Quantity);
        }

        public virtual double DeltaImplied(double? volatility = null)
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility ?? IVMid1).Delta;
                    }
                    catch
                    {
                        Algo.Error($"Delta: Failed to derive ImpliedDelta. Using HV. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {IVMid1}");
                        return Delta();
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public virtual decimal TaylorTerm()
        {
            // delta/gamma gamma is unitless sensitivity. No scaling here.
            Option contract = (Option)Security;
            return contract.ContractMultiplier * Mid1Underlying;
        }

        public virtual decimal DeltaImpliedTotal(double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Quantity,
                SecurityType.Option => (decimal)DeltaImplied(volatility) * ((Option)Security).ContractMultiplier * Quantity
            };
        }

        public virtual decimal DeltaImplied100BpUSD( double? volatility = null)
        {
            return SecurityType switch
            {
                // Scaled price into a 1% change / 100BP. That changes times delta and position is risk of position moving by 1%. That's a hundreth of IB's 'Delta Dollar' metric.
                SecurityType.Equity => Mid1Underlying * 100 * BP * Quantity,
                SecurityType.Option => (decimal)DeltaImplied(volatility) * Mid1Underlying * 100 * BP * Quantity * Multiplier
            };
        }

        public virtual decimal DeltaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Equity => Quantity,
                SecurityType.Option => (decimal)Delta() * Multiplier * Quantity
            };
        }
        public virtual decimal DeltaXBpUSDTotal(double x = 100)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Mid1Underlying * (decimal)x * BP * Quantity,
                SecurityType.Option => (decimal)(Delta() * x) * Mid1Underlying * BP * Multiplier * Quantity
            };
        }
        public virtual decimal Delta100BpUSDTotal() => DeltaXBpUSDTotal(100);

        public virtual double Gamma()
        {
            return GetGreeks1().Gamma;
        }
        public virtual decimal GammaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)Gamma() * Multiplier * Quantity,
                _ => 0
            }; ;
        }

        public virtual decimal GammaXBpUSDTotal(double x = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(0.5 * Gamma() * Math.Pow((double)Mid1Underlying * x * (double)BP, 2)) * Multiplier * Quantity,
                _ => 0
            };
        }
        public virtual decimal Gamma100BpUSDTotal() => GammaXBpUSDTotal(100);

        // Below 2 simplifications assuming a pure options portfolio.
        public virtual double Theta() 
        {
            return GetGreeks1().Theta;
        }

        public virtual decimal ThetaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)Theta() * ((Option)Security).ContractMultiplier * Quantity,
                _ => 0,
            };
        }
        public virtual decimal Theta1DayUSD() 
        {
            return ThetaTotal() * Mid1Underlying;
        }

        // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        public virtual double Vega() 
        {
            return SecurityType switch
            {
                SecurityType.Option => GetGreeks1().Vega,
                _ => 0
            };
        }
        public virtual decimal Vega100BpUSD 
        { 
            get => VegaTotal * Mid1Underlying;
        }

        public virtual decimal VegaTotal
        { 
            get => SecurityType switch
            {
                SecurityType.Option => (decimal)GetGreeks1().Vega * ((Option)Security).ContractMultiplier * Quantity,
                _ => 0
            };
        }

        public virtual GreeksPlus GetGreeks0(decimal? mid0Underlying = null, decimal? mid0 = null, double? volatility = null, DateTime? calculationDate = null)
        {
            Greeks0 ??= GetGreeks(0);
            if (SecurityType == SecurityType.Option)
            {
                Greeks0.OCW.SetIndependents(mid0Underlying ?? Mid0Underlying, mid0 ?? Mid0, volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility);
            }
            return Greeks0;
        }

        public virtual GreeksPlus GetGreeks1(decimal? mid1Underlying = null, decimal? mid1 = null, double? volatility = null)
        {
            Greeks1 ??= GetGreeks(1);
            if (SecurityType == SecurityType.Option)
            {
                Greeks1.OCW.SetIndependents(Mid1Underlying, mid1 ?? Mid1, volatility ?? (double)Algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility);
            }
            return Greeks1;
        }
    }
}
