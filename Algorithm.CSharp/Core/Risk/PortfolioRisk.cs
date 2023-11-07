using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Securities;
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
        public Func<Symbol, Metric, IEnumerable<Position>, double?, double?, decimal> RiskByUnderlyingCached;

        public static PortfolioRisk E(Foundations algo)
        {
            return instance ??= new PortfolioRisk(algo);
        }

        public PortfolioRisk(Foundations algo)
        {
            _algo = algo;
            TimeCreated = _algo.Time;
            TimeLastUpdated = _algo.Time;
            ResetCache();
        }

        public void ResetCache()
        {
            RiskByUnderlyingCached = _algo.Cache(RiskByUnderlying, (Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility, double? dX) => (Underlying(symbol), metric, positions.Select(p => (p.Symbol.Value, p.Quantity)).ToHashSet(), volatility, dX, _algo.Time));
        }

        private decimal RiskByUnderlying(Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility, double? dX = null)
        {
            return metric switch
            {
                Metric.DeltaTotal => positions.Sum(p => p.DeltaTotal()),
                Metric.DeltaImpliedTotal => positions.Sum(p => p.DeltaImpliedTotal(AtmIVEWMA(symbol))),

                Metric.DeltaXBpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(dX ?? 0)),
                Metric.Delta100BpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(100)),
                Metric.DeltaImplied100BpUSDTotal => positions.Sum(p => p.DeltaImpliedXBpUSDTotal(AtmIVEWMA(symbol), 100)),
                Metric.Delta500BpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(500)),
                Metric.EquityDeltaTotal => positions.Where(p => p.SecurityType == SecurityType.Equity).Sum(p => p.DeltaTotal()),

                Metric.Gamma => (decimal)positions.Sum(p => p.Gamma()),
                Metric.GammaTotal => positions.Sum(p => p.GammaTotal()),
                Metric.GammaImpliedTotal => (decimal)positions.Sum(p => p.GammaImplied(AtmIVEWMA(symbol))),
                Metric.GammaXBpUSDTotal => positions.Sum(p => p.GammaXBpUSDTotal(dX ?? 0)),
                Metric.Gamma100BpUSDTotal => positions.Sum(p => p.GammaXBpUSDTotal(100)),
                Metric.GammaImplied100BpUSDTotal => positions.Sum(p => p.GammaImpliedXBpUSDTotal(100, AtmIVEWMA(symbol))),
                Metric.Gamma500BpUSDTotal => positions.Sum(p => p.GammaXBpUSDTotal(500)),
                Metric.SpeedXBpUSDTotal => positions.Sum(p => p.SpeedXBpUSDTotal(dX ?? 0)),
                Metric.GammaImplied500BpUSDTotal => positions.Sum(p => p.GammaImpliedXBpUSDTotal(500, AtmIVEWMA(symbol))),
                Metric.Vega => (decimal)positions.Sum(p => p.Vega()),
                Metric.VegaTotal => positions.Sum(p => p.VegaTotal()),
                Metric.VegaXBpUSDTotal => positions.Sum(p => p.VegaXBpUSDTotal(dX ?? 0)),
                Metric.VannaTotal => positions.Sum(p => p.VannaTotal()),
                Metric.VannaXBpUSDTotal => positions.Sum(p => p.VannaXBpUSDTotal((decimal)(dX ?? 0))),
                Metric.Vanna100BpUSDTotal => positions.Sum(p => p.VannaXBpUSDTotal(100)),
                Metric.BsmIVdSTotal => positions.Sum(p => p.BsmIVdSTotal()),
                Metric.DeltaIVdSTotal => positions.Sum(p => p.DeltaIVdSTotal()),
                Metric.DeltaIVdS100BpUSDTotal => positions.Sum(p => p.DeltaIVdSXBpUSDTotal(100)),
                Metric.VolgaXpUSDTotal => positions.Sum(p => p.VolgaXBpUSDTotal(dX ?? 0)),
                Metric.Volga100BpUSDTotal => positions.Sum(p => p.VolgaXBpUSDTotal(100)),
                Metric.ThetaTotal => positions.Sum(p => p.ThetaTotal()),
                Metric.PosWeightedIV => positions.Any() ? positions.Sum(p => (decimal)p.IVMid1 * Math.Abs(p.Quantity)) / positions.Sum(p => Math.Abs(p.Quantity)) : 0,

                _ => throw new NotImplementedException(metric.ToString()),
            };
        }

        public decimal RiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null, Func<IEnumerable<Position>, IEnumerable<Position>>? filter = null, double? dX = null, bool skipCache=false)
        {
            Symbol underlying = Underlying(symbol);
            var positions = _algo.Positions.Values.ToList().Where(x => x.UnderlyingSymbol == underlying && x.Quantity != 0);

            if (metric == Metric.PosWeightedIV)
            {
                filter ??= positions => positions.Where(p => p.SecurityType == SecurityType.Option);
            }

            positions = filter == null ? positions : filter(positions);
            if (!positions.Any()) return 0;
            
            return skipCache ? RiskByUnderlying(symbol, metric, positions, volatility, dX) : RiskByUnderlyingCached(symbol, metric, positions, volatility, dX);
        }

        /// <summary>
        /// Excludes position of derivative's respective underlying
        /// </summary>
        public decimal DerivativesRiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null)
        {
            return RiskByUnderlying(symbol, metric, volatility, positions => positions.Where(p => p.SecurityType == SecurityType.Option && p.Quantity != 0));
        }
        public decimal RiskBySymbol(Symbol symbol, Metric riskMetric)
        {
            return RiskByUnderlying(symbol, riskMetric, null, positions => positions.Where(p => p.Symbol == symbol && p.Quantity != 0));
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
            decimal minOffset = (decimal)(_algo.Cfg.MinZMOffset.TryGetValue(underlying, out double _minOffset) ? _minOffset : _algo.Cfg.MinZMOffset[CfgDefault]);
            decimal maxOffset = _algo.Cfg.MaxZMOffset.TryGetValue(underlying, out maxOffset) ? maxOffset : _algo.Cfg.MaxZMOffset[CfgDefault];
            return Math.Min(Math.Max(CastGracefully(Math.Abs(Math.Abs(positions.Select(p => DeltaZMOffset(p, volatility)).Sum()))), minOffset), maxOffset);
        }

        public (decimal, decimal) ZMBands(Symbol underlying, IEnumerable<Position> positions, double? volatility = null)
        {
            //  Scaling Zakamulin bands with quanitity**0.5 as they become fairly large with many option positions...
            //  Better made dependent on proportional transaction costs.
            var quantity = positions.Sum(p => p.Quantity);

            double deltaZM = positions.Select(p => DeltaZM(p, volatility)).Sum();
            double offsetZM = Math.Abs(positions.Select(p => DeltaZMOffset(p, volatility)).Sum());
            // For high quantities, offset goes towards +/- 1, not good. Hence using sqrt(deltaZM) as minimum.
            double minOffset = (_algo.Cfg.MinZMOffset.TryGetValue(underlying, out minOffset) ? minOffset : _algo.Cfg.MinZMOffset[CfgDefault]);
            offsetZM = Math.Min(Math.Max(offsetZM, Math.Pow(Math.Abs(deltaZM), 0.5)), minOffset);

            // Debug why bands can be zero despite options postions open
            if (deltaZM == 0 && offsetZM == 0)
            {
                if (positions.Count() > 0)
                {
                    _algo.Log($"ZM Bands are zero for {positions.Count()} positions with quantity {quantity}. DeltaZMs: {positions.Select(p => DeltaZM(p, volatility)).ToList()}");
                }                
            }
            return (CastGracefully(deltaZM - offsetZM), CastGracefully(deltaZM + offsetZM));
        }
        /// <summary>
        /// Ask IV strongly slopes up close to expiration (1-3 days), therefore rendering midIV not a good indicator. Would wanna use contracts expiring later. This will lead to a
        /// jump in AtmIV when referenced contracts are switched. How to make it smooth?
        /// </summary>
        public double AtmIV(Symbol symbol)
        {
            return (double)(_algo.IVSurfaceRelativeStrikeBid[Underlying(symbol)].AtmIv() + _algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIv()) / 2;
        }
        
        public double AtmIVEWMA(Symbol symbol)
        {
            return (double)(_algo.IVSurfaceRelativeStrikeBid[Underlying(symbol)].AtmIvEwma() + _algo.IVSurfaceRelativeStrikeAsk[Underlying(symbol)].AtmIvEwma()) / 2;
        }

        public decimal RiskIfFilled(Symbol symbol, decimal quantity, Metric riskMetric)
        {
            Trade trade = new(_algo, symbol, quantity, _algo.MidPrice(symbol));
            Position position = new(_algo, trade);
            return RiskByUnderlyingCached(symbol, riskMetric, new List<Position>() { position }, null, null);
        }

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

        // Refactor to a sigma / vola dependent move. not X %.
        public Dictionary<string, decimal> ToDict(Symbol symbol = null)
        {
            var underlyings = symbol == null ? _algo.equities : new HashSet<Symbol>() { Underlying(symbol) };

            return new Dictionary<string, decimal>()
            {
                { "EquityPosition", underlyings.Sum(x => _algo.Portfolio[x].Quantity) },
                { "DeltaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaTotal)) },
                { "DeltaIVdSTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaIVdSTotal)) },
                { "Delta100BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Delta100BpUSDTotal)) },
                //{ "Delta500BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Delta500BpUSDTotal)) },
                { "GammaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.GammaTotal)) },
                { "Gamma100BpUSDTotal", underlyings.Sum(x =>  RiskByUnderlying(x, Metric.Gamma100BpUSDTotal)) },
                { "Gamma500BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Gamma500BpUSDTotal)) },
                { "ThetaTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.ThetaTotal)) },
                { "Vega%Total", underlyings.Sum(x => RiskByUnderlying(x, Metric.VegaTotal)) / 100 },
                { "Vanna100BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.Vanna100BpUSDTotal)) },                
                { "PosWeightedIV", underlyings.Sum(x => RiskByUnderlying(x, Metric.PosWeightedIV)) },
                { "DeltaIVdS100BpUSDTotal", underlyings.Sum(x => RiskByUnderlying(x, Metric.DeltaIVdS100BpUSDTotal)) },
                { "MarginUsedQC", _algo.Portfolio.TotalMarginUsed },
                { "InitMargin", _algo.Portfolio.MarginMetrics.FullInitMarginReq },
                { "MaintenanceMargin", _algo.Portfolio.MarginMetrics.FullMaintMarginReq },
            };
        }
        /// <summary>
        /// While waiting for an equity hedge to fill, delta risk may have flipped in the meantime warranting a cancellation of the hedge order.
        /// </summary>
        public void CancelDeltaIncreasingEquityTickets(Symbol underlying, decimal riskDelta)
        {
            if (_algo.orderTickets.TryGetValue(underlying, out var tickets))
            {
                lock (tickets)
                {
                    foreach (var t in tickets.ToList().Where(t => !_algo.orderCanceledOrPending.Contains(t.Status) && t?.CancelRequest == null && t.Quantity * riskDelta > 0))
                    {                        
                        string tag = $"{_algo.Time} CancelDeltaIncreasingEquityTickets. Cancelling ticket {t.OrderId} for {t.Symbol} with quantity {t.Quantity} because riskDelta={riskDelta}.";
                        _algo.Log(tag);
                        _algo.Cancel(t, tag);
                    }
                }
            }
        }

        public bool IsRiskLimitExceedingBand(Symbol symbol)
        {
            if (_algo.IsWarmingUp) { return false; }

            decimal riskDeltaTotal = 0;
            Security security = _algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            var deltaMVTotal = _algo.DeltaMV(symbol);
            decimal riskPutCallRatio = 0;// _algo.Risk100BpRisk2USDDelta(underlying, _algo.TargetRiskPutCallRatio(underlying));

            riskDeltaTotal += deltaMVTotal;
            riskDeltaTotal += riskPutCallRatio;

            CancelDeltaIncreasingEquityTickets(underlying, riskDeltaTotal);

            if (riskDeltaTotal > _algo.Cfg.RiskLimitHedgeDeltaTotalLong || riskDeltaTotal < _algo.Cfg.RiskLimitHedgeDeltaTotalShort)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZMBands: riskDSTotal={riskDeltaTotal}, deltaMVTotal={deltaMVTotal}, riskPutCallRatio={riskPutCallRatio}");
                _algo.Publish(new RiskLimitExceededEventArgs(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            else if (Math.Abs(riskDeltaTotal) > 50)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZMBands: ?NOT EXCEEDED WHY? HIGH OFFSET? riskDSTotal={riskDeltaTotal}, deltaMVTotal={deltaMVTotal}, riskPutCallRatio={riskPutCallRatio}");
            }
            else
            {
                _algo.LimitIfTouchedOrderInternals.Remove(symbol);
            }

            return false;
        }

        public bool IsRiskLimitExceededZMBands(Symbol symbol)
        {
            if (_algo.IsWarmingUp) { return false; }

            decimal riskDeltaTotal = 0;
            Security security = _algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            decimal zmOffset = RiskBandByUnderlying(symbol, Metric.ZMOffset);
            var deltaMVTotal = _algo.DeltaMV(symbol);
            decimal riskPutCallRatio = 0;// _algo.Risk100BpRisk2USDDelta(underlying, _algo.TargetRiskPutCallRatio(underlying));

            riskDeltaTotal += deltaMVTotal;
            riskDeltaTotal += riskPutCallRatio;

            CancelDeltaIncreasingEquityTickets(underlying, riskDeltaTotal);

            if (riskDeltaTotal > zmOffset || riskDeltaTotal < -zmOffset)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZMBands: riskDSTotal={riskDeltaTotal}, deltaMVTotal={deltaMVTotal}, zmOffset={zmOffset}, riskPutCallRatio={riskPutCallRatio}");
                _algo.Publish(new RiskLimitExceededEventArgs(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            else if (Math.Abs(riskDeltaTotal) > 50)
            {
                _algo.Log($"{_algo.Time} IsRiskLimitExceededZMBands: ?NOT EXCEEDED WHY? HIGH OFFSET? riskDSTotal={riskDeltaTotal}, deltaMVTotal={deltaMVTotal}, zmOffset={zmOffset}, riskPutCallRatio ={riskPutCallRatio}");
            }
            else
            {
                _algo.LimitIfTouchedOrderInternals.Remove(symbol);
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
                _algo.Publish(new RiskLimitExceededEventArgs(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            return false;
        }
    }
}
