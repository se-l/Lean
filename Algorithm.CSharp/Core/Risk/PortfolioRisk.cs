using Fasterflect;
using QuantConnect.Algoalgorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class PortfolioRisk
    {
        public DateTime TimeCreated { get; }
        public DateTime TimeLastUpdated { get; protected set; }

        //public double DeltaSPY { get => Positions.Sum(t => t.DeltaSPY); }
        //tex:
        //$$ID_i = \Delta_i * \beta_i * \frac{A_i}{I}$$
        //public decimal DeltaSPY100BpUSD { get => Positions.Sum(t => t.DeltaSPY100BpUSD); }
        // Need 2 risk measures. One excluding hedge instruments and one with to avoid increasing position in hedge instruments.
        //public decimal DeltaSPYUnhedged100BpUSD { get => Positions.Sum(t => t.DeltaSPY100BpUSD); }  // At first SPY, eventually something derived from option portfolio
        //private PortfolioProxyIndex ppi { get; set; }
        //public PortfolioProxyIndex Ppi => ppi ??= PortfolioProxyIndex.E(algo);

        private readonly Foundations _algo;
        private static PortfolioRisk instance;
        public readonly Func<Symbol, Metric, decimal> RiskBySymbol;
        public readonly Func<Symbol, Metric, IEnumerable<Position>, double?, decimal> RiskByUnderlyingCached;

        public static PortfolioRisk E(Foundations algo)
        {
            return instance ??= new PortfolioRisk(algo);
        }

        public PortfolioRisk(Foundations algo)
        {
            _algo = algo;
            //algo.Log($"{algo.Time}: PortfolioRisk.Constructor called.");
            TimeCreated = _algo.Time;
            TimeLastUpdated = _algo.Time;
            //RiskBySymbol = _algo.Cache(GetRiskBySymbol, (Symbol symbol, Metric riskMetric) => (symbol, riskMetric, _algo.Time), ttl: 10);
            RiskBySymbol = _algo.Cache(GetRiskBySymbol, (Symbol symbol, Metric riskMetric) => (symbol, riskMetric, _algo.Time));
            //RiskByUnderlyingCached = _algo.Cache(RiskByUnderlying, (Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility) => (Underlying(symbol), metric, positions.Count(), volatility ?? 0, _algo.Time), clearCacheEvery: TimeSpan.FromMinutes(5));
            RiskByUnderlyingCached = _algo.Cache(RiskByUnderlying, (Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility) => (Underlying(symbol), metric, positions.Count(), volatility ?? 0, _algo.Time));
            //RiskByUnderlyingCached = _algo.Cache(RiskByUnderlying, (Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility) => (Underlying(symbol), metric, positions.Count(), volatility ?? 0, _algo.Time), ttl: 5);
        }

        private decimal RiskByUnderlying(Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility)
        {
            return metric switch
            {
                Metric.DeltaTotal => positions.Sum(t => t.DeltaTotal()),
                Metric.DeltaImpliedTotal => positions.Sum(t => t.DeltaImpliedTotal(AtmIVEWMA(symbol))),

                Metric.Delta100BpUSDTotal => positions.Sum(t => t.DeltaXBpUSDTotal(100)),
                Metric.DeltaImplied100BpUSDTotal => positions.Sum(t => t.DeltaImpliedXBpUSDTotal(AtmIVEWMA(symbol), 100)),
                Metric.Delta500BpUSDTotal => positions.Sum(t => t.DeltaXBpUSDTotal(500)),
                Metric.EquityDeltaTotal => positions.Where(p => p.SecurityType == SecurityType.Equity).Sum(t => t.DeltaTotal()),

                Metric.Gamma => (decimal)positions.Sum(t => t.Gamma()),
                Metric.GammaTotal => (decimal)positions.Sum(t => t.Gamma()),
                Metric.GammaImpliedTotal => (decimal)positions.Sum(t => t.GammaImplied(AtmIVEWMA(symbol))),
                Metric.Gamma100BpUSDTotal => positions.Sum(t => t.GammaXBpUSDTotal(100)),
                Metric.GammaImplied100BpUSDTotal => positions.Sum(t => t.GammaImpliedXBpUSDTotal(100, AtmIVEWMA(symbol))),
                Metric.Gamma500BpUSDTotal => positions.Sum(t => t.GammaXBpUSDTotal(500)),
                Metric.GammaImplied500BpUSDTotal => positions.Sum(t => t.GammaImpliedXBpUSDTotal(500, AtmIVEWMA(symbol))),
                Metric.Vega => (decimal)positions.Sum(t => t.Vega()),
                Metric.VegaTotal => positions.Sum(t => t.VegaTotal),
                Metric.VegaUSDTotal => positions.Sum(t => t.Vega100BpUSD),
                Metric.Vega100BpUSD => positions.Sum(t => t.Vega100BpUSD),
                Metric.Theta => (decimal)positions.Sum(t => t.Theta()),
                Metric.ThetaTotal => positions.Sum(t => t.ThetaTotal()),
                Metric.ThetaUSDTotal => positions.Sum(t => t.Theta1DayUSD()),
                Metric.Theta1DayUSD => positions.Sum(t => t.Theta1DayUSD()),
                _ => throw new NotImplementedException(metric.ToString()),
            };
        }

        public decimal RiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null, Func<IEnumerable<Position>, IEnumerable<Position>>? filter = null, bool skipCache=false)
        {
            Symbol underlying = Underlying(symbol);
            var positions = _algo.Positions.Values.Where(x => x.UnderlyingSymbol == underlying && x.Quantity != 0);
            positions = filter == null ? positions : filter(positions);
            if (!positions.Any()) return 0;
            
            return skipCache ? RiskByUnderlying(symbol, metric, positions, volatility) : RiskByUnderlyingCached(symbol, metric, positions, volatility);
        }

        /// <summary>
        /// Excludes position of derivative's respective underlying
        /// </summary>
        public decimal DerivativesRiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null)
        {
            return RiskByUnderlying(symbol, metric, volatility, positions => positions.Where(p => p.SecurityType == SecurityType.Option && p.Quantity != 0));
        }

        public decimal RiskBandByUnderlying(Symbol symbol, Metric metric, double? volatility = null)
        {
            (decimal, decimal) tupZMBands = (0, 0);
            Symbol underlying = Underlying(symbol);
            var positions = _algo.Positions.Values.Where(p => p.UnderlyingSymbol == underlying && p.SecurityType == SecurityType.Option && p.Quantity != 0);
            
            if (!positions.Any()) return 0;
            if (new HashSet<Metric>() { Metric.BandZMLower, Metric.BandZMUpper }.Contains(metric))
            {
                tupZMBands = ZMBands(underlying, positions, volatility);
            };

            return metric switch
            {
                Metric.ZMOffset => ZMOffset(underlying, positions, volatility),
                Metric.BandZMLower => tupZMBands.Item1,
                Metric.BandZMUpper => tupZMBands.Item2,
                _ => throw new NotImplementedException(metric.ToString()),
            };
        }

        public double DeltaZM(Position p, double? volatility = null)
        {
            return -p.Multiplier * (double)p.Quantity * p.DeltaZM(volatility);
        }

        public double DeltaZMOffset(Position p, double? volatility = null)
        {
            double scaledQuantity = Math.Sign(p.Quantity) * Math.Pow((double)Math.Abs(p.Quantity), 0.5);
            return -p.Multiplier * scaledQuantity * p.DeltaZMOffset(volatility);
        }

        public decimal ZMOffset(Symbol underlying, IEnumerable<Position> positions, double? volatility = null)
        {
            return Math.Max(_algo.CastGracefully(Math.Abs(Math.Abs(positions.Select(p => DeltaZMOffset(p, volatility)).Sum()))), (decimal)_algo.Cfg.MinZMOffset[underlying]);
        }

        public (decimal, decimal) ZMBands(Symbol underlying, IEnumerable<Position> positions, double? volatility = null)
        {
            //  Scaling Zakamulin bands with quanitity**0.5 as they become fairly large with many option positions...
            //  Better made dependent on proportional transaction costs.
            var quantity = positions.Sum(p => p.Quantity);

            double deltaZM = positions.Select(p => DeltaZM(p, volatility)).Sum();
            double offsetZM = Math.Abs(positions.Select(p => DeltaZMOffset(p, volatility)).Sum());
            // For high quantities, offset goes towards +/- 1, not good. Hence using sqrt(deltaZM) as minimum.
            offsetZM = Math.Min(Math.Max(offsetZM, Math.Pow(Math.Abs(deltaZM), 0.5)), _algo.Cfg.MinZMOffset[underlying]);

            // Debug why bands can be zero despite options postions open
            if (deltaZM == 0 && offsetZM == 0)
            {
                if (positions.Count() > 0)
                {
                    _algo.Log($"ZM Bands are zero for {positions.Count()} positions with quantity {quantity}. DeltaZMs: {positions.Select(p => DeltaZM(p, volatility)).ToList()}");
                }                
            }
            return (_algo.CastGracefully(deltaZM - offsetZM), _algo.CastGracefully(deltaZM + offsetZM));
        }

        public double AtmIV(Symbol symbol)
        {
            return (double)(_algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIv() + _algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIv()) / 2;
        }
        
        public double AtmIVEWMA(Symbol symbol)
        {
            return (double)(_algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIVEWMA() + _algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIVEWMA()) / 2;
        }

        public decimal RiskAddedIfFilled(Symbol symbol, decimal quantity, Metric riskMetric)
        {
            return quantity * RiskBySymbol(symbol, riskMetric);
        }

        private decimal GetRiskBySymbol(Symbol symbol, Metric riskMetric)
        {
            OptionContractWrap ocw = null;
            if (symbol.SecurityType == SecurityType.Option)
            {
                ocw = OptionContractWrap.E(_algo, (Option)_algo.Securities[symbol], _algo.Time.Date);
                ocw.SetIndependents();
            }
            return (symbol.SecurityType, riskMetric) switch
            {
                // Delta
                (SecurityType.Option, Metric.DeltaTotal) => 100 * (decimal)ocw.Delta(),
                (SecurityType.Equity, Metric.DeltaTotal) => 1,
                (SecurityType.Option, Metric.Delta100BpUSDTotal) => 100 * ocw.DeltaXBp(100),
                (SecurityType.Equity, Metric.Delta100BpUSDTotal) => _algo.MidPrice(symbol),
                (SecurityType.Option, Metric.DeltaImpliedTotal) => 100 * (decimal)ocw.Delta(volatility: AtmIVEWMA(symbol)),
                (SecurityType.Equity, Metric.DeltaImpliedTotal) => 1,
                (SecurityType.Option, Metric.DeltaImplied100BpUSDTotal) => 100 * ocw.DeltaXBp(100, volatility: AtmIVEWMA(symbol)),
                (SecurityType.Equity, Metric.DeltaImplied100BpUSDTotal) => _algo.MidPrice(symbol),

                // Gamma
                (SecurityType.Option, Metric.Gamma100BpUSDTotal) => 100 * ocw.GammaXBp(100),
                (SecurityType.Option, Metric.Gamma500BpUSDTotal) => 100 * ocw.GammaXBp(500),
                (SecurityType.Equity, Metric.Gamma100BpUSDTotal) => 0,
                (SecurityType.Equity, Metric.Gamma500BpUSDTotal) => 0,
                (SecurityType.Option, Metric.GammaImplied100BpUSDTotal) => 100 * ocw.GammaXBp(100, volatility: AtmIVEWMA(symbol)),
                (SecurityType.Option, Metric.GammaImplied500BpUSDTotal) => 100 * ocw.GammaXBp(500, volatility: AtmIVEWMA(symbol)),
                (SecurityType.Equity, Metric.GammaImplied100BpUSDTotal) => 0,
                (SecurityType.Equity, Metric.GammaImplied500BpUSDTotal) => 0,
                _ => throw new NotImplementedException(riskMetric.ToString()),
            };
        }

        //public decimal DPfDeltaIfFilled(Symbol symbol, decimal quantity)
        //{
        //    // Hedging against an entire Portfolio risk not yet reliable enough. For now, just use risk grouped by the underlying.
        //    // too tricky currently as it only returns a sensitity. not good for estimating what-if-filled.
        //    if (symbol.SecurityType == SecurityType.Option)
        //    {
        //        Option option = (Option)algo.Securities[symbol];
        //        var delta = OptionContractWrap.E(algo, option).Greeks(null, null).Delta;
        //        double betaUnderlying = algo.Beta(algo.spy, option.Underlying.Symbol, 20, Resolution.Daily);
        //        var deltaSPY = delta * betaUnderlying * (double)option.Underlying.Price / (double)algo.MidPrice(algo.spy);
        //        decimal deltaSPY100BpUSD = (decimal)deltaSPY * option.ContractMultiplier * quantity * algo.MidPrice(algo.spy);
        //        return deltaSPY100BpUSD;
        //    }
        //    else if (symbol.SecurityType == SecurityType.Equity)
        //    {
        //        return (decimal)(Math.Sign(quantity) * Ppi.DeltaIf((Equity)algo.Securities[symbol]));
        //    }
        //    else
        //    {
        //        throw new NotImplementedException();
        //    }
        //}

        //public double Beta(Symbol symbol, int window=30)
        //{
        //    return Ppi.Beta(symbol, window);
        //}

        public decimal PortfolioValue(string method= "Mid")
        {
            if (method == "Mid")
            {
                return _algo.Positions.Values.Sum(t => t.ValueMid) + _algo.Portfolio.Cash;
            }
            else if (method == "Worst")
            {
                return _algo.Positions.Values.Sum(t => t.ValueWorst) + _algo.Portfolio.Cash;
            }
            else if (method == "UnrealizedProfit")
            {
                return _algo.Positions.Values.Sum(t => t.UnrealizedProfit);
            }
            else if (method == "AvgPositionPnLMid")
            {
                return _algo.Positions.Values.Any() ? (_algo.Portfolio.TotalPortfolioValue - _algo.TotalPortfolioValueSinceStart) / _algo.Positions.Values.Count() : 0;
            }
            else if (method == "PnlMidPerOptionAbsQuantity")
            {
                return (_algo.Positions.Values.Any() && _algo.Positions.Values.Where(p => p.SecurityType == SecurityType.Option).Select(p => Math.Abs(p.Quantity)).Sum() > 0) ? (_algo.Portfolio.TotalPortfolioValue - _algo.TotalPortfolioValueSinceStart) / _algo.Positions.Values.Where(p => p.SecurityType == SecurityType.Option).Select(p=>Math.Abs(p.Quantity)).Sum() : 0;
            }
            else
            {
                return _algo.Portfolio.TotalPortfolioValue;
            }            
        }

        //public double Correlation(Symbol symbol)
        //{
        //    return _algo.Correlation(_algo.equity1, symbol, 20, Resolution.Daily);
        //}


        public Dictionary<string, decimal> ToDict(Symbol symbol = null)
        {
            var underlyings = symbol == null ? _algo.equities : new HashSet<Symbol>() { Underlying(symbol) };
            return new Dictionary<string, decimal>()
            {
                { "EquityPosition", underlyings.Sum(x => _algo.Portfolio[x].Quantity) },
                { "DeltaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaTotal)) },
                //{ "DeltaImpliedTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaImpliedTotal)) },
                { "Delta100BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Delta100BpUSDTotal)) },
                //{ "DeltaImplied100BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaImplied100BpUSDTotal)) },
                { "Delta500BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Delta500BpUSDTotal)) },
                { "GammaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.GammaTotal)) },
                //{ "GammaImpliedTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.GammaImpliedTotal)) },
                { "Gamma100BpUSDTotal", underlyings.Sum(x =>  RiskByUnderlying(x, Metric.Gamma100BpUSDTotal)) },
                { "Gamma500BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Gamma500BpUSDTotal)) },
                //{ "GammaImplied500BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.GammaImplied500BpUSDTotal)) },
                { "VegaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.VegaTotal)) },
                { "VegaUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.VegaUSDTotal)) },
                { "ThetaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.ThetaTotal)) },
                { "ThetaUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.ThetaUSDTotal)) },
            };
        }

        public bool IsRiskLimitExceededZMBands(Symbol symbol)
        {
            if (_algo.IsWarmingUp) { return false; }

            Security security = _algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            decimal zmOffset = RiskBandByUnderlying(symbol, Metric.ZMOffset);
            decimal riskDeltaTotal = RiskByUnderlying(symbol, Metric.DeltaTotal);

            if (riskDeltaTotal > zmOffset || riskDeltaTotal < -zmOffset)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZM. riskDeltaTotal={riskDeltaTotal}.");
                _algo.PublishEvent(new EventRiskLimitExceeded(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            return false;
        }

        public bool IsRiskLimitExceededZM(Symbol symbol)
        {
            if (_algo.IsWarmingUp) { return false; }

            Security security = _algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            decimal riskDeltaEquityTotal = RiskByUnderlying(symbol, Metric.EquityDeltaTotal);  // Normalized risk of an assets 1% change in price for band breach check.
            decimal lowerBand = RiskBandByUnderlying(symbol, Metric.BandZMLower);
            decimal upperBand = RiskBandByUnderlying(symbol, Metric.BandZMUpper);

            if ((riskDeltaEquityTotal > upperBand || riskDeltaEquityTotal < lowerBand) && Math.Abs(RiskByUnderlying(symbol, Metric.DeltaTotal)) >= 20)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZM. ZMLowerBand={lowerBand}, ZMUpperBand={upperBand}, DeltaEquityTotal={riskDeltaEquityTotal}.");
                _algo.PublishEvent(new EventRiskLimitExceeded(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            return false;
        }
    }
}
