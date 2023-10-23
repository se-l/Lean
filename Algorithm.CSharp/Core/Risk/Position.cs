using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
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
            Quantity = holding.Quantity;
            Trade0 = new Trade(algo, holding);
        }

        /// <summary>
        /// Generates a position from a previous and applied a trade to it.
        /// </summary>
        public Position(Position? position, Trade trade0, Foundations algo, Trade? trade1 = null)
        {
            _prevPosition = position;
            Trade0 = trade0;
            _algo = algo;
            Trade1 = trade1;
            Symbol = trade0.Symbol;
            Quantity = (_prevPosition?.Quantity ?? 0) + trade0.Quantity;
        }

        /// <summary>
        /// Constructor for WhatIfFilled Scenario.
        /// </summary>
        public Position(Foundations algo, Trade trade)
        {
            _algo = algo;
            Trade0 = trade;
            Symbol = trade.Symbol;
            Quantity = trade.Quantity;
        }

        public decimal Quantity { get; internal set; }
        public Symbol Symbol { get; internal set; }
        private Security _securityUnderlying;
        public Security SecurityUnderlying { get => _securityUnderlying ??= _algo.Securities[UnderlyingSymbol]; }
        private Security _security;
        public Security Security { get => _security ??= _algo.Securities[Symbol]; }
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
        public decimal Bid1Underlying { get => Trade1?.Bid0Underlying ?? SecurityUnderlying.BidPrice; }
        public decimal Ask1Underlying { get => Trade1?.Ask0Underlying ?? SecurityUnderlying.AskPrice; }
        public decimal UnrealizedProfit { get => _algo.Portfolio[Symbol].UnrealizedProfit; }
        public decimal Spread1 { get => Trade1?.Spread0 ?? (Ask1 - Bid1); }
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
        public double IVMid1 { get => Trade1?.IVMid0 ?? (IVBid1 + IVAsk1) / 2; }
        public decimal Mid1Underlying { get => (Bid1Underlying + Ask1Underlying) / 2; }
        public decimal Mid0 { get => (Trade0.Bid0 + Trade0.Ask0) / 2; }
        public decimal Mid0Underlying { get => Trade0.Mid0Underlying; }
        public decimal ValueMid { get { return Mid1 * Quantity * Multiplier; } }
        public decimal ValueWorst { get { return (Quantity > 0 ? Bid1 : Ask1) * Quantity * Multiplier; } }
        public GreeksPlus Greeks1 { get => Trade1?.Greeks ?? GetGreeks1(); }  // This one gets exported to CSV because it's not a method.
        public GreeksPlus GetGreeks()
        {
            return SecurityType switch
            {
                SecurityType.Option => new(_algo, OptionContractWrap.E(_algo, (Option)Security, _algo.Time.Date)),
                SecurityType.Equity => new GreeksPlus(_algo, Security),
                _ => throw new NotSupportedException()
            };
        }
        public GreeksPlus GetGreeks1(double? volatility = null)
        {
            GreeksPlus greeks = Trade1?.Greeks ?? GetGreeks();
            if (SecurityType == SecurityType.Option && Trade1 == null)
            {
                greeks.OCW.SetIndependents(Mid1Underlying, Mid1, volatility ?? (double)SecurityUnderlying.VolatilityModel.Volatility);
            }
            return greeks;
        }
        // dS
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
                        return GetGreeks1(volatility: volatility ?? (double)SecurityUnderlying.VolatilityModel.Volatility).DeltaZM((int)Quantity);
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
            return GetGreeks1(volatility: volatility ?? (double)SecurityUnderlying.VolatilityModel.Volatility).DeltaZMOffset((int)Quantity);
        }

        public double DeltaImplied(double volatility)
        {
            switch (SecurityType)
            {
                case SecurityType.Equity:
                    return 1;
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility).Delta;  // Contract IV wouldnt make much sense. ATM most likely. kill this call
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
        public decimal DeltaXBpUSDTotal(double dS = 100)
        {
            //if (x==100)
            //{
            //    _algo.Log($"DeltaXBpUSDTotal: {SecurityType} {Symbol} Mid1Underlying={Mid1Underlying} Quantity={Quantity} Delta={Delta()} DecimalDeltaxX={(decimal)(Delta() * x)} BP={BP} Multiplier={Multiplier} Result={(decimal)(Delta() * x) * Mid1Underlying * BP * Multiplier * Quantity}");
            //}            
            return SecurityType switch
            {
                SecurityType.Equity => Mid1Underlying * (decimal)dS * BP * Quantity,
                SecurityType.Option => (decimal)(Delta() * dS) * Mid1Underlying * BP * Multiplier * Quantity
            };
        }
        public decimal DeltaImpliedTotal(double volatility)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Quantity,
                SecurityType.Option => (decimal)DeltaImplied(volatility) * Multiplier * Quantity
            };
        }
        public decimal DeltaImpliedXBpUSDTotal(double volatility, double x = 100)
        {
            return SecurityType switch
            {
                SecurityType.Equity => Mid1Underlying * (decimal)x * BP * Quantity,
                SecurityType.Option => (decimal)(DeltaImplied(volatility) * x) * Mid1Underlying * BP * Multiplier * Quantity
            };
        }

        //dS2
        public double Gamma()
        {
            return GetGreeks1().Gamma;
        }
        public double GammaImplied(double volatility)
        {
            switch (SecurityType)
            {
                case SecurityType.Option:
                    try
                    {
                        return GetGreeks1(volatility: volatility).Gamma;
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
        public decimal GammaImpliedTotal(double volatility)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)GetGreeks1(volatility: volatility).Gamma * Multiplier * Quantity,
                _ => 0
            }; ;
        }
        public decimal GammaXBpUSDTotal(double dS = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(0.5 * Gamma() * Math.Pow((double)Mid1Underlying * dS * (double)BP, 2)) * Multiplier * Quantity,
                _ => 0
            };
        }
        // dSdIV
        public decimal VannaXBpUSDTotal(decimal dIV = 100)  // How much USD costs a 1% change in IV and its change to Delta?
        {
            return SecurityType switch
            {
                // Change in Delta * Position
                SecurityType.Option => (decimal)GetGreeks1().Vanna * BP * dIV * Mid0Underlying * Multiplier * Quantity,
                _ => 0
            };
        }
        public decimal VannaTotal() => (decimal)GetGreeks1().Vanna * Multiplier * Quantity;
        public decimal BsmIVdS() => (decimal)GetGreeks1().IVdS;

        public decimal BsmIVdSTotal() => BsmIVdS() * Multiplier * Quantity;
        public decimal SurfaceIVdSBid => Trade1?.SurfaceIVdSBid ?? (decimal)(_algo.IVSurfaceRelativeStrikeBid[UnderlyingSymbol].IVdS(Symbol) ?? 0);
        public decimal SurfaceIVdSAsk => Trade1?.SurfaceIVdSAsk ?? (decimal)(_algo.IVSurfaceRelativeStrikeAsk[UnderlyingSymbol].IVdS(Symbol) ?? 0);
        public decimal SurfaceIVdS { get {
            if (SurfaceIVdSBid == 0) { return SurfaceIVdSAsk; }
            if (SurfaceIVdSAsk == 0) { return SurfaceIVdSBid; }
            return (SurfaceIVdSBid + SurfaceIVdSAsk) / 2;
        }}
        public decimal SurfacedIVdSTotal => SurfaceIVdSAsk * Multiplier * Quantity;
        public decimal DeltaIVdSTotal()
        {
            return (decimal)GetGreeks1().Vega * SurfaceIVdS * Multiplier * Quantity;
        }
        public decimal DeltaIVdSXBpUSDTotal(decimal dS = 100)
        {
            return (decimal)GetGreeks1().Vega * SurfaceIVdS * Mid1Underlying * dS * BP * Multiplier * Quantity;
        }
        public decimal SpeedXBpUSDTotal(double dS = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)((1/6.0) * GetGreeks1().DS3 * Math.Pow((double)Mid1Underlying * dS * (double)BP, 3)) * Multiplier * Quantity,
                _ => 0
            };
        }
        public decimal VegaXBpUSDTotal(double dIV = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(GetGreeks1().Vega * dIV) * BP * Multiplier * Quantity,
                _ => 0
            };
        }
        public decimal VolgaXBpUSDTotal(double dIV = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(GetGreeks1().DIV2 * Math.Pow(dIV * (double)BP, 2) * (double)(Multiplier * Quantity)),
                _ => 0
            };
        }
        public decimal GammaImpliedXBpUSDTotal(double volatility, double dS = 100)
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)(0.5 * GammaImplied(volatility) * Math.Pow((double)Mid1Underlying * dS * (double)BP, 2)) * Multiplier * Quantity,
                _ => 0
            };
        }
        public decimal ThetaTillExpiryTotal()
        {
            return SecurityType switch
            {
                SecurityType.Option => (decimal)GetGreeks1().ThetaTillExpiry * Multiplier * Quantity,
                _ => 0,
            };
        }
        public decimal ThetaTotal(decimal dT = 1) => (decimal)GetGreeks1().Theta * Multiplier * Quantity * dT;

        public decimal IntrinsicValue1 => SecurityType switch
        {
            SecurityType.Option => Option.GetPayOff(Trade1?.Mid0Underlying ?? Mid1Underlying),
            _ => 0
        };
        public bool IsITM1 => IntrinsicValue1 > 0;
        public bool IsExercised { get => (Trade1?.Tag.Contains("Automatic Exercise") ?? false) || (Trade1?.Tag.Contains("Simulated option") ?? false); }  // improve, together with LifeCycleUpdate

        // dIV
        public double Vega() => GetGreeks1().Vega;
        public decimal VegaTotal() => (decimal)GetGreeks1().Vega * Multiplier * Quantity;
        public double Ts0Sec { get => (Trade0.Ts0 - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds; }
        public decimal DP { get => P1 - Trade0.PriceFillAvg; }
        public decimal DeltaFillMid1 { get => P1 - Mid1; }
        public decimal DS { get => Mid1Underlying - Mid0Underlying; }
        public decimal PL
        {
            get
            {
                decimal pl;
                if (IsExercised && IsITM1)
                {
                    // (K - S) - P_option
                    pl = IntrinsicValue1 * Multiplier * Quantity - Trade0.P0 * Quantity * Multiplier;
                }else
                {
                    pl = (P1 - Trade0.P0) * Quantity * Multiplier;
                }                
                return pl + Trade0.Fee;
            }
        }
        public double DDaysToExpiration
        {
            get => SecurityType switch
            {
                SecurityType.Option => (int)(Ts1.Date - Trade0.Ts0.Date).TotalSeconds / 84600,
                //SecurityType.Option => GetGreeks1().OCW.DaysToExpiration(Ts1.Date) - Trade0.Greeks.OCW.DaysToExpiration(Trade0.Ts0.Date),
                _ => 0
            };
        }
        public double IVPrice1 { get => IVMid1; }
        public double DIVMid { get => IVMid1 - Trade0.IVMid0; }
        private PLExplain _pLExplain { get; set; }
        public PLExplain PLExplain { get => _pLExplain ??= GetPLExplain(); }  // public getter for easy CSV export

        /// <summary>
        /// Initialize PL Explain with Trade0 and other details, then update with PositionSnaps.
        /// </summary>
        private PLExplain GetPLExplain()
        {
            LastSnap = new PositionSnap(_algo, Symbol);
            _pLExplain ??= new PLExplain(_algo, this);
            return _pLExplain.Update(LastSnap);
        }

        /// <summary>
        /// Generate trade history backwards as every Position is linked to its previous position via trade. Redundant with frequent position snapping.
        /// </summary>
        public static IEnumerable<Position> AllLifeCycles(Foundations algo)
        {
            List<Position> positions = new();

            foreach (var (symbol, trades) in algo.Trades)
            {
                Position position = null;
                List<Trade> _trades = trades.OrderBy(t => t.Ts0).ToList();

                for (ushort i = 0; i < _trades.Count; i++)
                {
                    Trade trade1 = (i + 1) < _trades.Count ? _trades[i + 1] : null;
                    position = new Position(position, _trades[i], algo, trade1);
                    positions.Add(position);
                }
            }
            return positions;
        }

        //private readonly List<PositionSnap> Snaps = new();
        public PositionSnap LastSnap { get; internal set; }

        public void Snap()
        {
            PositionSnap LastSnap = new(_algo, Symbol);
            //Snaps.Add(snap);
            _pLExplain = GetPLExplain();
            _pLExplain.Update(LastSnap);
        }
    }
}
