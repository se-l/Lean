using NodaTime;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
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
        public IEnumerable<Position> Positions { get; protected set; }

        // Refactor below into GreeksPlus
        public decimal DeltaTotal { get => Positions.Sum(t => t.DeltaTotal()); }
        public decimal Delta100BpUSDTotal { get => Positions.Sum(t => t.DeltaXBpUSDTotal(100)); }
        public decimal Delta500BpUSDTotal { get => Positions.Sum(t => t.DeltaXBpUSDTotal(500)); }
        public decimal GammaTotal { get => Positions.Sum(t => t.GammaTotal()); }
        public decimal Gamma100BpUSDTotal { get => Positions.Sum(t => t.GammaXBpUSDTotal(100)); }
        public decimal Gamma500BpUSDTotal { get => Positions.Sum(t => t.GammaXBpUSDTotal(500)); }
        public double ThetaTotal { get => Positions.Sum(t => t.Theta()); }
        public decimal ThetaUSDTotal { get => Theta1DayUSD; }
        public decimal Theta1DayUSD { get => Positions.Sum(t => t.Theta1DayUSD()); }
        public double VegaTotal { get => Positions.Sum(t => t.Vega()); }
        public decimal VegaUSDTotal { get => Vega100BpUSD; }
        public decimal Vega100BpUSD { get => Positions.Sum(t => t.Vega100BpUSD); }
        public double Rho { get; }
        //public double DeltaSPY { get => Positions.Sum(t => t.DeltaSPY); }
        //tex:
        //$$ID_i = \Delta_i * \beta_i * \frac{A_i}{I}$$
        //public decimal DeltaSPY100BpUSD { get => Positions.Sum(t => t.DeltaSPY100BpUSD); }
        // Need 2 risk measures. One excluding hedge instruments and one with to avoid increasing position in hedge instruments.
        //public decimal DeltaSPYUnhedged100BpUSD { get => Positions.Sum(t => t.DeltaSPY100BpUSD); }  // At first SPY, eventually something derived from option portfolio
        //private PortfolioProxyIndex ppi { get; set; }
        //public PortfolioProxyIndex Ppi => ppi ??= PortfolioProxyIndex.E(algo);

        private readonly Foundations algo;
        // private static string instanceKey { get; set; }
        private static PortfolioRisk instance;


        public static PortfolioRisk E(Foundations algo)
        {
            return instance ??= new PortfolioRisk(algo);
        }

        public PortfolioRisk(Foundations algo)
        {
            this.algo = algo;
            //algo.Log($"{algo.Time}: PortfolioRisk.Constructor called.");
            TimeCreated = algo.Time;
            TimeLastUpdated = algo.Time;
            ResetPositions();
        }

        /// <summary>
        /// Triggers reset of Positions property.
        /// </summary>
        public void ResetPositions()
        {
            Positions = algo.Portfolio.Values.Where(h => h.Invested && !algo.Securities[h.Symbol].IsDelisted).Select(h => new Position(algo, h));
            TimeLastUpdated = algo.Time;
        }

        public decimal RiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null, Func<IEnumerable<Position>, IEnumerable<Position>>? filter = null)
        {
            Symbol underlying = Underlying(symbol);
            var positions = Positions.Where(x => x.UnderlyingSymbol == underlying);
            positions = filter == null ? positions : filter(positions);
            if (!positions.Any()) return 0;

            return metric switch
            {
                Metric.DeltaTotal => positions.Sum(t => t.DeltaTotal()),
                Metric.Delta100BpUSDTotal => positions.Sum(t => t.DeltaXBpUSDTotal(100)),
                Metric.Delta500BpUSDTotal => positions.Sum(t => t.DeltaXBpUSDTotal(500)),
                Metric.Delta100BpUSDTotalImplied => positions.Sum(t => t.DeltaImpliedTotal(volatility)),
                Metric.EquityDeltaTotal => positions.Where(p => p.SecurityType == SecurityType.Equity).Sum(t => t.DeltaTotal()),
                Metric.DeltaMeanImplied => (decimal)positions.Where(p => p.SecurityType == SecurityType.Option).Sum(t => t.DeltaImplied(volatility)) / positions.Count(),
                Metric.DeltaTotalImplied => positions.Sum(t => t.DeltaImpliedTotal(volatility)),
                Metric.Gamma => (decimal)positions.Sum(t => t.Gamma()),
                Metric.GammaTotal => (decimal)positions.Sum(t => t.Gamma()),
                Metric.Gamma100BpUSDTotal => positions.Sum(t => t.GammaXBpUSDTotal(100)),
                Metric.Gamma500BpUSDTotal => positions.Sum(t => t.GammaXBpUSDTotal(500)),
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

        /// <summary>
        /// CACHE THIS! Excludes position of derivative's respective underlying
        /// </summary>
        public decimal DerivativesRiskByUnderlying(Symbol symbol, Metric metric, double? volatility = null)
        {
            return RiskByUnderlying(symbol, metric, volatility, filter: positions => positions.Where(p => p.SecurityType == SecurityType.Option));
        }

        public decimal RiskBandByUnderlying(Symbol symbol, Metric metric, double? volatility = null)
        {
            (decimal, decimal) tupZMBands = (0, 0);
            Symbol underlying = Underlying(symbol);
            var positions = Positions.Where(p => p.UnderlyingSymbol == underlying && p.SecurityType == SecurityType.Option);
            
            if (!positions.Any()) return 0;
            if (new HashSet<Metric>() { Metric.BandZMLower, Metric.BandZMUpper }.Contains(metric))
            {
                tupZMBands = ZMBands(positions, volatility);
            };
         
            return metric switch
            {
                Metric.BandZMLower => tupZMBands.Item1,
                Metric.BandZMUpper => tupZMBands.Item2,

                // To be removed. Instead of hard ceiling on limit/-no-limit, rather adapt the prices.
                Metric.GammaUpperStopBuying => algo.GammaUpperStopBuying,
                Metric.GammaLowerStopSelling => algo.GammaLowerStopSelling,
                Metric.GammaUpperContinuousHedge => algo.GammaUpperContinuousHedge,
                Metric.GammaLowerContinuousHedge => algo.GammaLowerContinuousHedge,
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

        public (decimal, decimal) ZMBands(IEnumerable<Position> positions, double? volatility = null)
        {
            //  Scaling Zakamulin bands with quanitity**0.5 as they become fairly large with many option positions...
            //  Better made dependent on proportional transaction costs.
            var quantity = positions.Sum(p => p.Quantity);

            double deltaZM = positions.Select(p => DeltaZM(p, volatility)).Sum();
            double offsetZM = positions.Select(p => DeltaZMOffset(p, volatility)).Sum();
            
            return (algo.CastGracefully(deltaZM - offsetZM), algo.CastGracefully(deltaZM + offsetZM));
        }

        public decimal RiskAddedIfFilled(Symbol symbol, decimal quantity, Metric riskMetric)
        {
            if (symbol.SecurityType == SecurityType.Option)
            {
                OptionContractWrap.E(algo, (Option)algo.Securities[symbol], 1).GreeksP();
            }
            return (symbol.SecurityType, riskMetric) switch
            {
                // Delta
                (SecurityType.Option, Metric.DeltaTotal) => 100 * quantity * (decimal)OptionContractWrap.E(algo, (Option)algo.Securities[symbol], 1).Delta(),
                (SecurityType.Equity, Metric.DeltaTotal) => quantity,
                (SecurityType.Option, Metric.Delta100BpUSDTotal) => 100 * quantity * OptionContractWrap.E(algo, (Option)algo.Securities[symbol], 1).Delta100Bp(),
                (SecurityType.Equity, Metric.Delta100BpUSDTotal) => quantity * algo.MidPrice(symbol),

                // Gamma
                (SecurityType.Option, Metric.Gamma100BpUSDTotal) => 100 * quantity * OptionContractWrap.E(algo, (Option)algo.Securities[symbol], 1).GammaXBp(100),
                (SecurityType.Option, Metric.Gamma500BpUSDTotal) => 100 * quantity * OptionContractWrap.E(algo, (Option)algo.Securities[symbol], 1).GammaXBp(500),
                (SecurityType.Equity, Metric.Gamma100BpUSDTotal) => 0,
                (SecurityType.Equity, Metric.Gamma500BpUSDTotal) => 0,
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
                return Positions.Sum(t => t.ValueMid) + algo.Portfolio.Cash;
            }
            else if (method == "Close")
            {
                return Positions.Sum(t => t.ValueClose) + algo.Portfolio.Cash;
            }
            else if (method == "Worst")
            {
                return Positions.Sum(t => t.ValueWorst) + algo.Portfolio.Cash;
            }
            else if (method == "UnrealizedProfit")
            {
                return Positions.Sum(t => t.UnrealizedProfit);
            }
            else if (method == "AvgPositionPnLMid")
            {
                return Positions.Any() ? (algo.Portfolio.TotalPortfolioValue - algo.TotalPortfolioValueSinceStart) / Positions.Count() : 0;
            }
            else if (method == "PnlMidPerOptionAbsQuantity")
            {
                return (Positions.Any() && Positions.Where(p => p.SecurityType == SecurityType.Option).Select(p => Math.Abs(p.Quantity)).Sum() > 0) ? (algo.Portfolio.TotalPortfolioValue - algo.TotalPortfolioValueSinceStart) / Positions.Where(p => p.SecurityType == SecurityType.Option).Select(p=>Math.Abs(p.Quantity)).Sum() : 0;
            }
            else
            {
                return algo.Portfolio.TotalPortfolioValue;
            }            
        }

        public double Correlation(Symbol symbol)
        {
            return algo.Correlation(algo.equity1, symbol, 20, Resolution.Daily);
        }


        public Dictionary<string, decimal> ToDict(Symbol symbol = null)
        {            
            return new Dictionary<string, decimal>()
            {
                { "EquityPosition", symbol == null ? 0 : algo.Portfolio[Underlying(symbol)].Quantity },
                { "DeltaTotal", symbol == null ? DeltaTotal : RiskByUnderlying(symbol, Metric.DeltaTotal) },
                { "Delta100BpUSDTotal", symbol == null ? Delta100BpUSDTotal : RiskByUnderlying(symbol, Metric.Delta100BpUSDTotal) },
                { "Delta500BpUSDTotal", symbol == null ? Delta500BpUSDTotal : RiskByUnderlying(symbol, Metric.Delta500BpUSDTotal) },
                { "GammaTotal", symbol == null ? GammaTotal : RiskByUnderlying(symbol, Metric.GammaTotal) },
                { "Gamma100BpUSDTotal", symbol == null ? Gamma100BpUSDTotal : RiskByUnderlying(symbol, Metric.Gamma100BpUSDTotal) },
                { "Gamma500BpUSDTotal", symbol == null ? Gamma500BpUSDTotal : RiskByUnderlying(symbol, Metric.Gamma500BpUSDTotal) },
                { "VegaTotal", symbol == null ? (decimal)VegaTotal: RiskByUnderlying(symbol, Metric.VegaTotal) },
                { "VegaUSDTotal", symbol == null ? VegaUSDTotal : RiskByUnderlying(symbol, Metric.VegaUSDTotal) },
                { "ThetaTotal", symbol == null ? (decimal)ThetaTotal: RiskByUnderlying(symbol, Metric.ThetaTotal) },
                { "ThetaUSDTotal", symbol == null ? ThetaUSDTotal : RiskByUnderlying(symbol, Metric.ThetaUSDTotal) },
            };
        }

        public bool IsRiskLimitExceededGamma(Symbol symbol)
        {
            if (algo.IsWarmingUp) { return false; }

            Security security = algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            decimal total100BpGamma = RiskByUnderlying(symbol, Metric.Gamma100BpUSDTotal);
            decimal lowerBand = RiskBandByUnderlying(symbol, Metric.GammaLowerContinuousHedge);
            decimal upperBand = RiskBandByUnderlying(symbol, Metric.GammaUpperContinuousHedge);

            if (total100BpGamma > upperBand || total100BpGamma < lowerBand)
            {
                algo.PublishEvent(new EventRiskLimitExceeded(symbol, RiskLimitType.Gamma, RiskLimitScope.Underlying));
                return true;
            }
            return false;
        }

        public bool IsRiskLimitExceededZM(Symbol symbol)
        {
            if (algo.IsWarmingUp) { return false; }

            Security security = algo.Securities[symbol];
            Symbol underlying = Underlying(symbol);

            decimal riskDeltaEquityTotal = RiskByUnderlying(symbol, Metric.EquityDeltaTotal);  // Normalized risk of an assets 1% change in price for band breach check.
            decimal lowerBand = RiskBandByUnderlying(symbol, Metric.BandZMLower);
            decimal upperBand = RiskBandByUnderlying(symbol, Metric.BandZMUpper);

            if (riskDeltaEquityTotal > upperBand || riskDeltaEquityTotal < lowerBand)
            {
                algo.Log($"{algo.Time} IsRiskLimitExceededZM. ZMLowerBand={lowerBand}, ZMUpperBand={upperBand}, DeltaEquityTotal={riskDeltaEquityTotal}.");
                algo.PublishEvent(new EventRiskLimitExceeded(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;

            }
            return false;
        }

        public bool IsRiskLimitExceededBSM(Symbol symbol)
        {
            if (algo.IsWarmingUp) { return false; }

            Security security = algo.Securities[symbol];
            // Risk By Security

            // Risk By Underlying
            Symbol underlying = Underlying(symbol);
            SecurityRiskLimit riskLimit = algo.Securities[underlying].RiskLimit;
            //var riskByUnderlying = RiskByUnderlying(symbol, Metric.Delta100BpUSDImplied, volatility: algo.IVAtm(symbol));
            var risk100BpByUnderlying = RiskByUnderlying(symbol, Metric.Delta100BpUSDTotalImplied);

            //// Delta Bias
            ////var optionPositions = algo.pfRisk.Positions.Where(x => x.UnderlyingSymbol == underlying & x.SecurityType == SecurityType.Option);            
            ////var biases = optionPositions.Select(p => p.Delta(implied: true)).Select(d => d-0.5 <= 0 ? -0.05 : 0.05);
            ////riskByUnderlying += (decimal)biases.Sum()*100;

            if (risk100BpByUnderlying > riskLimit.Delta100BpLong || risk100BpByUnderlying < riskLimit.Delta100BpShort)
            {
                algo.PublishEvent(new EventRiskLimitExceeded(symbol, RiskLimitType.Delta, RiskLimitScope.Underlying));
                return true;
            }

            // Risk By Portfolio

            return false;
        }

        public RiskRecord GetRiskRecord(Equity equity)
        {
            return new RiskRecord(algo, this, equity);
        }
    }

    public class RiskRecord
    {
        public DateTime Time { get; internal set; }
        public Symbol Symbol { get; internal set; }
        
        public decimal DeltaTotal { get; internal set; }
        public decimal Delta100BpUSDTotal { get; internal set; }
        public decimal Delta100BpUSDOptionsTotal { get; internal set; }
        public decimal GammaTotal { get; internal set; }
        public decimal Gamma100BpUSDTotal { get; internal set; }
        public decimal Gamma500BpUSDTotal { get; internal set; }
        public decimal VegaTotal { get; internal set; }
        public decimal ThetaTotal { get; internal set; }

        public decimal PositionUSD { get; internal set; }
        public decimal PositionUnderlying { get; internal set; }
        public decimal PositionUnderlyingUSD { get; internal set; }
        public decimal PositionOptions { get; internal set; }
        public decimal PositionOptionsUSD { get; internal set; }

        public decimal PnL { get; internal set; }
        public decimal MidPriceUnderlying { get; internal set; }
        public RiskRecord(Foundations algo, PortfolioRisk pfRisk, Equity equity)
        {
            Time = algo.Time;
            Symbol = equity.Symbol;
            PositionUnderlying = algo.Securities[Symbol].Holdings.Quantity;
            PositionUnderlyingUSD = algo.Securities[Symbol].Holdings.HoldingsValue;

            DeltaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.DeltaTotal);
            Delta100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Delta100BpUSDTotal);
            Delta100BpUSDOptionsTotal = Delta100BpUSDTotal - PositionUnderlying;
            GammaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.GammaTotal);
            Gamma100BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma100BpUSDTotal);
            Gamma500BpUSDTotal = pfRisk.RiskByUnderlying(Symbol, Metric.Gamma500BpUSDTotal);
            VegaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.VegaTotal);
            ThetaTotal = pfRisk.RiskByUnderlying(Symbol, Metric.ThetaTotal);

            var optionHoldings = algo.Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == Symbol).Select(kvp => kvp.Value.Holdings);
            PositionOptions = optionHoldings.Select(h => h.Quantity).Sum();
            PositionOptionsUSD = optionHoldings.Select(h => h.HoldingsValue).Sum();

            PnL = TradesCumulative.Cumulative(algo).Where(t => t.UnderlyingSymbol == Symbol).Select(t => t.PL).Sum();
            MidPriceUnderlying = algo.MidPrice(Symbol);
        }
    }
}






