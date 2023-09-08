using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algoalgorithm.CSharp.Core.Risk
{
    public class Position
    {
        private readonly Foundations _algo;
        public Trade Trade0 { get; internal set; }
        public Trade? Trade1 { get; internal set; }
        private readonly Position? _prevPosition;
        public SecurityType SecurityType { get => Security.Type; }

        /// <summary>
        /// Constructor for Pos0 off Portfolio Holdings.
        /// </summary>
        public Position(Foundations algo, SecurityHolding holding)
        {
            _algo = algo;
            Symbol = holding.Symbol;
            Security = _algo.Securities[Symbol];
            Quantity = holding.Quantity;
            Trade0 = new Trade(algo, holding);
        }

        /// <summary>
        /// Generates a position from a previous and applied a trade to it.
        /// </summary>
        public Position(Position? position, Trade trade, Foundations algo)
        {
            _prevPosition = position;
            Trade0 = trade;
            _algo = algo;
            Symbol = trade.Symbol;
            Security = trade.Security;
            OrderQuantity = trade.Quantity;            
            Quantity = (_prevPosition?.Quantity ?? 0) + trade.Quantity;
            Greeks1 = GetGreeks1();
        }

        public decimal Quantity { get; internal set; }
        public decimal OrderQuantity { get; internal set; } = 0m;
        public Symbol Symbol { get; internal set; }
        private Security _securityUnderlying;
        public Security SecurityUnderling { get => _securityUnderlying ??= _algo.Securities[UnderlyingSymbol]; }
        public Security Security { get; internal set; }
        private Option? Option { get => SecurityType == SecurityType.Option ? (Option)Security : null; }
        public OptionRight? Right
        {
            get => SecurityType switch
            {
                SecurityType.Option => ((Option)Security).Right,
                _ => null
            };
        }
        public Symbol UnderlyingSymbol
        {
            get => SecurityType switch
            {
                SecurityType.Option => Option?.Underlying.Symbol,
                _ => Symbol
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
        private int? _multipier;
        public int Multiplier { get => _multipier ??= SecurityType == SecurityType.Option ? ((Option)Security).ContractMultiplier : 1; }
        public decimal P1 { get => Trade1?.P0 ?? _algo.MidPrice(Symbol); }
        public decimal Mid1 { get { return (Bid1 + Ask1) / 2; } }
        public decimal Bid1Underlying { get => Trade1?.Bid0Underlying ?? SecurityUnderling.BidPrice; }
        public decimal Ask1Underlying { get => Trade1?.Ask0Underlying ?? SecurityUnderling.AskPrice; }
        public decimal UnrealizedProfit { get => _algo.Portfolio[Symbol].UnrealizedProfit; }
        public decimal Spread1 { get => Trade1?.Spread0 ?? ( Ask1 - Bid1 ); }
        public DateTime Ts1 { get => Trade1?.Ts0 ?? _algo.Time; }
        public decimal Bid1 { get => Trade1?.Bid0 ?? Security.BidPrice; }
        public decimal Ask1 { get => Trade1?.Ask0 ?? Security.AskPrice; }
        public double IVBid1
        {
            get => Trade1?.IVBid0 ?? SecurityType switch
            {
                SecurityType.Option => _algo.IVBids[Symbol].IVBidAsk.IV,
                _ => 0
            };
        }
        public double IVAsk1
        {
            get => Trade1?.IVBid0 ?? SecurityType switch
            {
                SecurityType.Option => _algo.IVAsks[Symbol].IVBidAsk.IV,
                _ => 0
            };
        }
        public double IVMid1 { get => (IVBid1 + IVAsk1) / 2; }
        public decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public decimal Mid0 { get => (Trade0.Bid0 + Trade0.Ask0) / 2; }
        public decimal Mid0Underlying { get => Trade0.Mid0Underlying; }
        public decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public decimal ValueWorst { get { return (Quantity > 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }
        public GreeksPlus Greeks1 { get; internal set; }
        public GreeksPlus GetGreeks(int version = 1)
        {
            return SecurityType switch
            {
                // version 0 means as of a time in the past. Passing Fill time to calculate all Greeks as of that date.
                SecurityType.Option => version == 0 ? new(OptionContractWrap.E(_algo, (Option)Security, version, Trade0.Ts0.Date)) : new(OptionContractWrap.E(_algo, (Option)Security, version)),
                SecurityType.Equity => new GreeksPlus(),
                _ => throw new NotSupportedException()
            };
        }
        public GreeksPlus GetGreeks1(double? volatility = null)
        {
            Greeks1 ??= Trade1?.GetGreeks(0) ?? GetGreeks(1);
            if (SecurityType == SecurityType.Option)
            {
                Greeks1.OCW.SetIndependents(Mid1Underlying, Mid1, volatility ?? (double)SecurityUnderling.VolatilityModel.Volatility);
            }
            return Greeks1;
        }

        public double Delta()
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1().Delta;
                    }
                    catch
                    {
                        _algo.Error($"Delta: Failed to derive Delta. Returning 0. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {GetGreeks1().OCW.HistoricalVolatility()}");
                        return 0;
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public double DeltaZM(double? volatility = null)
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 0;  // Because ZM is to create option bands. Wouldn't want equity to dilute required bands.
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility ?? (double)SecurityUnderling.VolatilityModel.Volatility).DeltaZM((int)Quantity);
                    }
                    catch
                    {
                        _algo.Error($"DeltaZM: Failed to derive DeltaZM. Returning BSM Delta. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {IVMid1}");
                        return Delta();
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public double DeltaZMOffset(double? volatility = null)
        {
            return GetGreeks1(volatility: volatility ?? (double)SecurityUnderling.VolatilityModel.Volatility).DeltaZMOffset((int)Quantity);
        }

        public double DeltaImplied(double? volatility = null)
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
                        _algo.Error($"Delta: Failed to derive ImpliedDelta. Using HV. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {IVMid1}");
                        return Delta();
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        public decimal TaylorTerm()
        {
            // delta/gamma gamma is unitless sensitivity. No scaling here.
            Option contract = (Option)Security;
            return contract.ContractMultiplier * Mid1Underlying;
        }
        public decimal DeltaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Equity => Quantity,
                SecurityType.Option => (decimal)Delta() * Multiplier * Quantity
            };
        }
        public decimal DeltaXBpUSDTotal(double x = 100)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Mid1Underlying * (decimal)x * BP * Quantity,
                SecurityType.Option => (decimal)(Delta() * x) * Mid1Underlying * BP * Multiplier * Quantity
            };
        }
        public decimal DeltaImpliedTotal(double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Quantity,
                SecurityType.Option => (decimal)DeltaImplied(volatility) * Multiplier * Quantity
            };
        }
        public decimal DeltaImpliedXBpUSDTotal(double x = 100, double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Mid1Underlying * (decimal)x * BP * Quantity,
                SecurityType.Option => (decimal)(DeltaImplied(volatility) * x) * Mid1Underlying * BP * Multiplier * Quantity
            };
        }

        public double Gamma()
        {
            return GetGreeks1().Gamma;
        }
        public double GammaImplied(double? volatility = null)
        {
            switch (SecurityType)
            {
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility ?? IVMid1).Gamma;
                    }
                    catch
                    {
                        _algo.Error($"GammaImplied: Failed to derive ImpliedDelta. Using HV. Symbol: {Symbol} PriceUnderlying: {Mid1Underlying} HV: {IVMid1}");
                        return Delta();
                    }
                default:
                    return 0;
            }
        }
        public decimal GammaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)Gamma() * Multiplier * Quantity,
                _ => 0
            }; ;
        }
        public decimal GammaImpliedTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)Gamma() * Multiplier * Quantity,
                _ => 0
            }; ;
        }

        public decimal GammaXBpUSDTotal(double x = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(0.5 * Gamma() * Math.Pow((double)Mid1Underlying * x * (double)BP, 2)) * Multiplier * Quantity,
                _ => 0
            };
        }
        public decimal GammaImpliedXBpUSDTotal(double x = 100, double? volatility = null)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(0.5 * GammaImplied(volatility) * Math.Pow((double)Mid1Underlying * x * (double)BP, 2)) * Multiplier * Quantity,
                _ => 0
            };
        }

        // Below 2 simplifications assuming a pure options portfolio.
        public double Theta()
        {
            return GetGreeks1().Theta;
        }

        public decimal ThetaTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)Theta() * Multiplier * Quantity,
                _ => 0,
            };
        }
        public decimal Theta1DayUSD()
        {
            return ThetaTotal() * Mid1Underlying;
        }

        // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        public double Vega()
        {
            return SecurityType switch
            {
                SecurityType.Option => GetGreeks1().Vega,
                _ => 0
            };
        }
        public decimal Vega100BpUSD
        {
            get => VegaTotal * Mid1Underlying;
        }

        public decimal VegaTotal
        {
            get => SecurityType switch
            {
                SecurityType.Option => (decimal)GetGreeks1().Vega * Multiplier * Quantity,
                _ => 0
            };
        }

        public double Ts0Sec { get => (Trade0.Ts0 - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds; }
        public decimal DP { get => P1 - Trade0.PriceFillAvg; }
        public decimal DeltaFillMid1 { get => P1 - Mid1; }
        public decimal DPUnderlying { get => Mid1Underlying - Mid0Underlying; }
        public decimal PL
        {
            get
            {
                var pl = (P1 - Trade0.P0) * Quantity * Multiplier + Trade0.Fee;
                return pl;
            }
        }
        public Quote<Option>? Quote { get; internal set; }
        public int DDaysToExpiration
        {
            get => SecurityType switch
            {
                SecurityType.Option => Trade0.Greeks0.OCW.DaysToExpiration(Trade0.Ts0.Date) - GetGreeks1().OCW.DaysToExpiration(Ts1.Date),
                _ => 0
            };
        }
        public double IVPrice1 { get => IVMid1; }
        public double DMidIV { get => IVMid1 - Trade0.IVMid0; }
        public double DIVPrice { get => IVPrice1 - Trade0.IVPrice0; }
        public PLExplain PLExplain { get => GetPLExplain(); }  // public getter for easy CSV export
        private PLExplain GetPLExplain()
        {
            return new PLExplain(
                    Trade0.Greeks0,
                    (double)DPUnderlying,
                    DDaysToExpiration,
                    DIVPrice,  // the realized IV.
                    0,
                    (double)(Quantity * Multiplier),
                    DeltaFillMid1 - Trade0.DeltaFillMid0, // the realized bit.
                    Trade0.Fee
                    );
        }

        /// <summary>
        /// Generate trade history backwards as every Position is linked to its previous position via trade.
        /// </summary>
        public static IEnumerable<Position> AllLifeCycles(Foundations algo)
        {            
            List<Position> positions = new();

            foreach (var (symbol, trades) in algo.Trades)
            {
                Position position = null;
                Trade trade1 = null;
                foreach (Trade trade in trades.OrderBy(t => t.Ts0))
                {
                    if (position != null)
                    {
                        position = new Position(position, trade, algo);
                    }
                    else
                    {
                        position = new Position(null, trade, algo);
                    }
                    positions.Add(position);
                    if (trade1 != null && positions.Any())
                    {
                        positions.Last().Trade1 = trade1;
                    }
                    trade1 = trade;
                }
            }
            return positions;
        }
    }
}
