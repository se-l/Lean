using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Indicators;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class PortfolioRisk
    {
        public DateTime TimeCreated { get; }
        public IEnumerable<Position> Positions { get; }
        // Refactor below into GreeksPlus
        public double Delta { get; }  // sensitivity to the Portfolio's value.
        public decimal Delta100BpUSD { get; }
        public double Gamma { get; }
        public decimal Gamma100BpUSD { get; }
        public double Theta { get; }
        public decimal ThetaUSD { get; }
        public double Vega { get; }
        public decimal Vega100BpUSD { get; }
        public double Rho { get; }
        public double DeltaSPY { get; }
        public decimal DeltaSPY100BpUSD { get; }
        // Need 2 risk measures. One excluding hedge instruments and one with to avoid increasing position in hedge instruments.
        public decimal DeltaSPYUnhedged100BpUSD { get; }  // At first SPY, eventually something derived from option portfolio
        public PortfolioProxyIndex Ppi { get; }

        private readonly Foundations algo;
        private static string instanceKey { get; set; }
        private static DateTime instanceCreatedTime { get; set; }
        private static PortfolioRisk instance;


        public static PortfolioRisk E(Foundations algo, IEnumerable<Position> positions = null, PortfolioProxyIndex ppi = null)
        {
            // Only 1 instance of PF Risk at a time. Invalidated after 30mins.
            // Also may need to have event driven refresh or PF risk instead of cache key. Should get into key....
            string key = $"{algo.OrderEvents.FindAll(x => x.Status == OrderStatus.Filled || x.Status == OrderStatus.PartiallyFilled).Count}";
            if (instanceKey != key || instanceCreatedTime.AddMinutes(30) <= algo.Time)
            {
                instanceKey = key;
                instanceCreatedTime = algo.Time;
                instance = new PortfolioRisk(algo, positions, ppi);
            }
            return instance;
        }

        public PortfolioRisk(Foundations algo, IEnumerable<Position> positions = null, PortfolioProxyIndex ppi = null)
        {
            this.algo = algo;
            //algo.Log($"{algo.Time}: PortfolioRisk.Constructor called.");
            Positions = positions ?? GetPositions();
            Ppi = ppi ?? PortfolioProxyIndex.E(algo);  // needs also positions as option input.
            
            //foreach (Position position in Positions)
            //{
            //    position.SetPfGreeks();
            //}
            // Refactor below into GreeksPlus
            Delta = Positions.Sum(x => x.Trades.Sum(t => t.PfDelta()));
            Delta100BpUSD = Positions.Sum(x => x.Trades.Sum(t => t.PfDelta100BpUSD()));
            Gamma = Positions.Sum(x => x.Trades.Sum(t => t.PfGamma()));
            Gamma100BpUSD = Positions.Sum(x => x.Trades.Sum(t => t.PfGamma100BpUSD()));
            Theta = Positions.Sum(x => x.Trades.Sum(t => t.PfTheta()));
            ThetaUSD = Positions.Sum(x => x.Trades.Sum(t => t.PfThetaUSD()));
            Vega = Positions.Sum(x => x.Trades.Sum(t => t.PfVega()));
            Vega100BpUSD = Positions.Sum(x => x.Trades.Sum(t => t.PfVega100BpUSD()));

            //tex:
            //$$ID_i = \Delta_i * \beta_i * \frac{A_i}{I}$$
            DeltaSPY = Positions.Sum(x => x.Trades.Sum(t => t.DeltaSPY));
            DeltaSPY100BpUSD = Positions.Sum(x => x.Trades.Sum(t => t.DeltaSPY100BpUSD));
            DeltaSPYUnhedged100BpUSD = Positions.Where(x => x.SecurityType == SecurityType.Option).Sum(x => x.Trades.Sum(t => t.DeltaSPY100BpUSD));

            
            //if (orderTickets != null)
            //{
            //    //TODO. What if filled risks....
            //}

            TimeCreated = algo.Time;
            Rho = 0;
        }

        public static Symbol Underlying(Symbol symbol)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Option => symbol.ID.Underlying.Symbol,
                SecurityType.Equity => symbol,
                _ => throw new NotImplementedException(),
            };
        }

        public decimal RiskByUnderlyingUSD(Symbol symbol)
        {
            Symbol underlying = Underlying(symbol);
            var positions = Positions.Where(x => x.Symbol == underlying || x.UnderlyingSymbol == underlying);
            return positions.Sum(x => x.Trades.Sum(t => t.PfDelta100BpUSD()));
        }

        public double Beta(Symbol symbol, int window=30)
        {
            return Ppi.Beta(symbol, window);
        }

        public decimal DPfDeltaIfFilled(Symbol symbol, decimal quantity)
        {
            // Hedging against an entire Portfolio risk not yet reliable enough. For now, just use risk grouped by the underlying.
            // too tricky currently as it only returns a sensitity. not good for estimating what-if-filled.
            //if (symbol.SecurityType == SecurityType.Option)
            //{
            //    Option option = (Option)algo.Securities[symbol];
            //    var delta = OptionContractWrap.E(algo, option).Greeks(null, null).Delta;
            //    double betaUnderlying = algo.Beta(algo.spy, option.Underlying.Symbol, 20, Resolution.Daily);
            //    var deltaSPY = delta * betaUnderlying * (double)option.Underlying.Price / (double)algo.MidPrice(algo.spy);
            //    decimal deltaSPY100BpUSD = (decimal)deltaSPY * option.ContractMultiplier * quantity * algo.MidPrice(algo.spy);
            //    return deltaSPY100BpUSD;
            //}
            //else if (symbol.SecurityType == SecurityType.Equity)
            //{
            //    return (decimal)(Math.Sign(quantity) * Ppi.DeltaIf((Equity)algo.Securities[symbol]));
            //}
            //else
            //{
            //    throw new NotImplementedException();
            //}
            return symbol.SecurityType switch
            {
                // the 100BPUnderlyingMoves risk...
                SecurityType.Option => quantity * (decimal)OptionContractWrap.E(algo, (Option)algo.Securities[symbol]).Greeks(null, null).Delta * algo.MidPrice(symbol.ID.Underlying.Symbol),
                SecurityType.Equity => quantity * algo.MidPrice(symbol),
                _ => throw new NotImplementedException(),
            };
        }

        public decimal PortfolioValue(string method= "Mid")
        {
            if (method == "Mid")
            {
                return Positions.Sum(p => p.Trades.Sum(t => t.ValueMid)) + algo.Portfolio.Cash;
            }
            else if (method == "Close")
            {
                return Positions.Sum(p => p.Trades.Sum(t => t.ValueClose)) + algo.Portfolio.Cash;
            }
            else if (method == "Worst")
            {
                return Positions.Sum(p => p.Trades.Sum(t => t.ValueWorst)) + algo.Portfolio.Cash;
            }
            else if (method == "UnrealizedProfit")
            {
                return Positions.Sum(p => p.Trades.Sum(t => t.UnrealizedProfit));
            }
            else
            {
                return algo.Portfolio.TotalPortfolioValue;
            }            
        }

        public double Correlation(Symbol symbol)
        {
            return algo.Correlation(algo.spy, symbol, 20, Resolution.Daily);
        }


        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>()
            {
                {"TimeCreated", TimeCreated},
                {"Delta", Delta},
                {"Delta100BpUSD", Delta100BpUSD},
                {"DeltaSPY", DeltaSPY},
                {"DeltaSPY100BpUSD", DeltaSPY100BpUSD},
                {"Gamma", Gamma},
                {"Gamma100BpUSD", Gamma100BpUSD},
                {"Theta", Theta},
                {"ThetaUSD", ThetaUSD},
                {"Vega", Vega},
                {"Vega100BpUSD", Vega100BpUSD},
                {"Rho", Rho},
                {"Ppi", Ppi}
            };
        }

        public IEnumerable<Position> GetPositions()
        {
            var positions = new List<Position>();
            foreach (SecurityHolding security_holding in algo.Portfolio.Values.Where(x => x.Quantity != 0))
            {
                positions.Add(new Position(algo, security_holding.Symbol));
            }
            return positions;
        }

        public static IEnumerable<Position> GetPositionsSince(Foundations algo, DateTime? since = null)
        {
            var positions = new List<Position>();
            if (since == null)
            {
                return algo.Transactions.GetOrders().Where(o => o.LastFillTime != null && o.Status != OrderStatus.Canceled).ToHashSet(o => o.Symbol).Select(symbol => new Position(algo, symbol));  // setPL to be deleted.
            }
            else
            {
                throw new NotImplementedException();
            }
        }
    }
}






