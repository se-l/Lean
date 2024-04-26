using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class PositionSnap
    {
        private readonly Foundations _algo;
        public Symbol Symbol { get; internal set; }
        public string SnapID { get => $"{Symbol.Value} {Ts0:yyyyMMddHHmmss}"; }
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
        public SecurityType SecurityType { get; internal set; }
        private Security _securityUnderlying;
        public Security SecurityUnderlying { get => _securityUnderlying ??= _algo.Securities[UnderlyingSymbol]; }
        public int Multiplier { get => SecurityType == SecurityType.Option ? 100 : 1; }
        public decimal Bid0Underlying { get; internal set; } = 0;
        public decimal Ask0Underlying { get; internal set; } = 0;
        public decimal Mid0Underlying { get => (Bid0Underlying + Ask0Underlying) / 2; }

        private GreeksPlus _greeks;
        public GreeksPlus Greeks
        {
            get
            {
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
            }
        }
        public double HistoricalVolatility { get; internal set; }
        public DateTime Ts0 { get; internal set; }
        public double Ts0Sec { get => (Ts0 - new DateTime(1970, 1, 1, 0, 0, 0)).TotalSeconds; }
        public decimal Bid0 { get; internal set; }
        public decimal Ask0 { get; internal set; }
        public decimal Mid0 { get => (Bid0 + Ask0) / 2; }
        public double IVBid0 { get; internal set; }
        public double IVAsk0 { get; internal set; }
        public double IVMid0 { get => (IVBid0 + IVAsk0) / 2; }
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
        //public decimal SurfaceIVAHdS
        //{
        //    get
        //    {
        //        if (SecurityType != SecurityType.Option) return 0;                
        //        return (decimal)_algo.IVSurfaceAndreasenHuge[(UnderlyingSymbol, Symbol.ID.OptionRight)].IVdS(Symbol);
        //    }
        //}

        /// <summary>
        /// For option expiration
        /// </summary>
        public PositionSnap(Foundations algo, Symbol symbol)
        {
            _algo = algo;
            Ts0 = algo.Time;
            Symbol = symbol;
            Security = _algo.Securities[Symbol];
            SecurityType = Security.Type;

            Bid0 = Security.BidPrice;
            Ask0 = Security.AskPrice;

            Bid0Underlying = SecurityUnderlying.BidPrice;
            Ask0Underlying = SecurityUnderlying.AskPrice;

            Snap();
        }
        private void Snap()
        {
            HistoricalVolatility = (double)_algo.Securities[UnderlyingSymbol].VolatilityModel.Volatility;
            IVBid0 = SecurityType == SecurityType.Option ? OptionContractWrap.E(_algo, (Option)Security, Ts0.Date).IV(Bid0, Mid0Underlying, 0.001) : 0;
            IVAsk0 = SecurityType == SecurityType.Option ? OptionContractWrap.E(_algo, (Option)Security, Ts0.Date).IV(Ask0, Mid0Underlying, 0.001) : 0;
            _ = Greeks;
            SurfaceIVdSBid = (decimal)(_algo.IVSurfaceRelativeStrikeBid[UnderlyingSymbol].IVdS(Symbol) ?? 0);
            SurfaceIVdSAsk = (decimal)(_algo.IVSurfaceRelativeStrikeAsk[UnderlyingSymbol].IVdS(Symbol) ?? 0);
        }
    }
}
