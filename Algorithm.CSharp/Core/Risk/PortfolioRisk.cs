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

        private decimal RiskByUnderlying(Symbol symbol, Metric metric, IEnumerable<Position> positions, double? volatility = null, double? dX = null)
        {
            return metric switch
            {
                Metric.DeltaTotal => positions.Sum(p => p.DeltaTotal(volatility)),
                Metric.DeltaImpliedTotal => positions.Sum(p => p.DeltaImpliedTotal(_algo.MidIV(p.Symbol))),
                Metric.DeltaImpliedAtmTotal => positions.Sum(p => p.DeltaImpliedTotal(AtmIVEWMA(symbol))),
                Metric.DeltaImpliedEWMATotal => positions.Sum(p => p.DeltaImpliedTotal(_algo.MidIVEWMA(symbol))),

                Metric.DeltaXBpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(dX ?? 0)),
                Metric.Delta100BpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(100)),
                Metric.DeltaImplied100BpUSDTotal => positions.Sum(p => p.DeltaImpliedXBpUSDTotal(AtmIVEWMA(symbol), 100)),
                Metric.Delta500BpUSDTotal => positions.Sum(p => p.DeltaXBpUSDTotal(500)),
                Metric.EquityDeltaTotal => positions.Where(p => p.SecurityType == SecurityType.Equity).Sum(p => p.DeltaTotal()),
                Metric.EquityDPriceMidTotal => positions.Where(p => p.SecurityType == SecurityType.Equity).Sum(p => p.DPMidTotal),
                Metric.OptionDPriceMidTotal => positions.Where(p => p.SecurityType == SecurityType.Option).Sum(p => p.DPMidTotal),

                Metric.Gamma => (decimal)positions.Sum(p => p.Gamma(volatility)),
                Metric.GammaTotal => positions.Sum(p => p.GammaTotal(volatility)),
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

        public decimal RiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null, Func<IEnumerable<Position>, IEnumerable<Position>>? filter = null, double? dX = null, bool skipCache = false)
        {
            Symbol underlying = Underlying(symbol);
            IEnumerable<Position> positions;
            lock (_algo.Positions)
            {
                positions = _algo.Positions.Values.ToList();
            }
            positions = positions.Where(x => x.UnderlyingSymbol == underlying && x.Quantity != 0);

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
        public decimal DerivativesRiskByUnderlying(Symbol symbol, Metric metric, double? volatility=null)
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
                tupZMBands = ZMBands2(positions);
                //tupZMBands = ZMBands(underlying, positions, volatility);
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
            return p.Multiplier * (double)p.Quantity * p.DeltaZMOffset(volatility);
            //double scaledQuantity = Math.Sign(p.Quantity) * Math.Pow((double)Math.Abs(p.Quantity), 0.5);
            //return -p.Multiplier * scaledQuantity * p.DeltaZMOffset(volatility);
        }

        public decimal ZMOffset(Symbol underlying, IEnumerable<Position> positions, double? volatility = null)
        {
            decimal minOffset = (decimal)(_algo.Cfg.MinZMOffset.TryGetValue(underlying, out double _minOffset) ? _minOffset : _algo.Cfg.MinZMOffset[CfgDefault]);
            decimal maxOffset = _algo.Cfg.MaxZMOffset.TryGetValue(underlying, out maxOffset) ? maxOffset : _algo.Cfg.MaxZMOffset[CfgDefault];
            return Math.Min(Math.Max(ToDecimal(Math.Abs(Math.Abs(positions.Select(p => DeltaZMOffset(p, volatility)).Sum()))), minOffset), maxOffset);
        }

        public (decimal, decimal) ZMBands2(IEnumerable<Position> positions)
        {
            double deltaZM = positions.Select(p => DeltaZM(p)).Sum();
            double offsetZM = Math.Abs(positions.Select(p => DeltaZMOffset(p)).Sum());

            // Debug why bands can be zero despite options postions open
            if (deltaZM == 0 && offsetZM == 0)
            {
                if (positions.Count() > 0)
                {
                    _algo.Log($"ZM Bands are zero for {positions.Count()} positions with quantity {positions.Sum(p => p.Quantity)}. DeltaZMs: {positions.Select(p => DeltaZM(p)).ToList()}");
                }
            }
            return (ToDecimal(deltaZM - offsetZM), ToDecimal(deltaZM + offsetZM));
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
            return (ToDecimal(deltaZM - offsetZM), ToDecimal(deltaZM + offsetZM));
        }
        
        public double AtmIVEWMA(Symbol symbol) => _algo.AtmIVEWMA(symbol);

        public decimal RiskIfFilled(Symbol symbol, decimal quantity, Metric riskMetric, double? volatility = null)
        {
            Trade trade = new(_algo, symbol, quantity, _algo.MidPrice(symbol));
            Position position = new(_algo, trade);
            return RiskByUnderlyingCached(symbol, riskMetric, new List<Position>() { position }, volatility, null);
        }

        public decimal PortfolioValue(string method = "Mid")
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
                return (_algo.Positions.Values.Any() && _algo.Positions.Values.Where(p => p.SecurityType == SecurityType.Option).Select(p => Math.Abs(p.Quantity)).Sum() > 0) ? (_algo.Portfolio.TotalPortfolioValue - _algo.TotalPortfolioValueSinceStart) / _algo.Positions.Values.Where(p => p.SecurityType == SecurityType.Option).Select(p => Math.Abs(p.Quantity)).Sum() : 0;
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
        public bool CheckHandleDeltaRiskExceedingBand(Symbol symbol)
        {
            Symbol underlying = Underlying(symbol);

            var riskDeltaTotal = _algo.DeltaMV(symbol);

            decimal riskPutCallRatio = 0;  // _algo.Risk100BpRisk2USDDelta(underlying, _algo.TargetRiskPutCallRatio(underlying));
            riskDeltaTotal += riskPutCallRatio;

            CancelDeltaIncreasingEquityTickets(underlying, riskDeltaTotal);
            bool exceeded = IsUnderlyingDeltaExceedingBand(symbol, riskDeltaTotal);
            if (exceeded)
            {
                _algo.Publish(new RiskLimitExceededEventArgs(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;
            }            
            return false;
        }
        public bool IsUnderlyingDeltaExceedingBand(Symbol symbol, decimal riskDeltaTotal)
        {
            if (_algo.IsWarmingUp) return false;

            Symbol underlying = Underlying(symbol);
            if (!_algo.ticker.Contains(underlying)) return false;

            decimal gammaTotal = RiskByUnderlying(underlying, Metric.GammaTotal);

            decimal totalDeltaHedgeThresholdIntercept = _algo.Cfg.TotalDeltaHedgeThresholdIntercept.TryGetValue(underlying.Value, out totalDeltaHedgeThresholdIntercept) ? totalDeltaHedgeThresholdIntercept : _algo.Cfg.TotalDeltaHedgeThresholdIntercept[CfgDefault];
            decimal totalDeltaHedgeThresholdGammaFactor = _algo.Cfg.TotalDeltaHedgeThresholdGammaFactor.TryGetValue(underlying.Value, out totalDeltaHedgeThresholdGammaFactor) ? totalDeltaHedgeThresholdGammaFactor : _algo.Cfg.TotalDeltaHedgeThresholdGammaFactor[CfgDefault];
            decimal totalDeltaHedgeThreshold = totalDeltaHedgeThresholdIntercept + Math.Abs(totalDeltaHedgeThresholdGammaFactor * gammaTotal);

            if (Math.Abs(riskDeltaTotal) > totalDeltaHedgeThreshold)
            {
                string gammaScalpingStatus = _algo.GammaScalpers.TryGetValue(underlying, out GammaScalper gs) ? gs.StatusShort() : "NoGammaScalper";
                _algo.Log($"{_algo.Time} IsPortfolioDeltExceedingBand: totalDeltaHedgeThreshold={totalDeltaHedgeThreshold}, riskDSTotal={riskDeltaTotal}, MidUnderlying={_algo.MidPrice(underlying)}, gammaTotal={gammaTotal}, {gammaScalpingStatus}");
                return true;
            }
            return false;
        }

        public bool IsUnderlyingDeltaExceedingBandZM(Symbol symbol)
        {
            if (_algo.IsWarmingUp) { return false; }

            Symbol underlying = Underlying(symbol);

            decimal riskDeltaEquityTotal = RiskByUnderlying(symbol, Metric.EquityDeltaTotal);
            decimal lowerBand = RiskBandByUnderlying(symbol, Metric.BandZMLower);
            decimal upperBand = RiskBandByUnderlying(symbol, Metric.BandZMUpper);
            decimal bandSize = Math.Abs(upperBand - lowerBand);

            if ((-riskDeltaEquityTotal > upperBand || -riskDeltaEquityTotal < lowerBand) && bandSize >= 15)
            {
                _algo.Log($"{_algo.Time} IsUnderlyingDeltaExceedingBandZM. ZMLowerBand={lowerBand}, ZMUpperBand={upperBand}, -DeltaEquityTotal={-riskDeltaEquityTotal}.");
                return true;
            }
            return false;
        }
    }
}
