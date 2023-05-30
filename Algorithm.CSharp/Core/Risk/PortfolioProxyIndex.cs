using QuantConnect.Securities;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Securities.Option;
using QuantConnect.Securities.Equity;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using Accord.Statistics;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    public class PortfolioProxyIndex
    {
        public DateTime TimeCreated { get;  }
        public int Window { get; } = 30;
        public IEnumerable<IndexConstituent> Constituents { get; }

        private bool isSPY { get; }
        private readonly Foundations algo;

        //public Func<Symbol, double> Beta = Cov(stock returns, index returns) / Var(index returns);

        private static readonly Func<Foundations, PortfolioProxyIndex> constructor = algo  => new PortfolioProxyIndex(algo);
        private static readonly Func<Foundations, string> genCacheKey = algo => $"{algo.Time.Date}{algo.Portfolio.Keys.OrderBy(k => k.Value).Aggregate("", (res, sym) => res + sym.Value + algo.Portfolio[sym].Quantity.ToString(CultureInfo.InvariantCulture))}";
        private static readonly Func<Foundations, PortfolioProxyIndex> constructorCached = Cache(constructor, genCacheKey);


        public static PortfolioProxyIndex E(Foundations algo)
        {
            // Intstead of getting the entire portfolio's positions, may want to refactor to trigger an event to reconstruct the proxy index object whenever we have a fill. As genCacheKey can be a tad expensive.
            return constructorCached(algo);
        }

        private PortfolioProxyIndex(Foundations algo)
        {
            this.algo = algo;
            TimeCreated = algo.Time;
            Constituents = ProjectPortfolioToEquityConstituents();
            //algo.Debug($"{algo.Time}: PortfolioProxyIndex: {genCacheKey(algo)}");
        }
        public decimal Value(DateTime? dt = null)
        {
            return Constituents.Sum(c => c.Weight * c.Price(dt ?? algo.Time.Date));
        }

        public double Beta(Symbol symbol, int window = 30)
        {
            // Cov(stock returns, index returns) / Var(index returns);
            // symbol would typically the asset we want to hedge with, like an ETF.
            var tradeBarsIndex = algo.HistoryWrap(symbol, window, Resolution.Daily);
            var ppiReturns = LogReturns(tradeBarsIndex.Select(tb => (double)Value(tb.EndTime)).ToArray());
            var indexReturns = LogReturns(tradeBarsIndex.Select(tb => (double)tb.Close).ToArray());
            double covariance = Covariance(
                indexReturns,
                ppiReturns,
                window);
            double indexVariance = indexReturns.Variance();
            return covariance / indexVariance;
        }

        //public double Correlation()
        //{
        //    // Cov(stock returns, index returns) / (std(stock returns) * std(index returns))
        //    // Cov(stock returns, index returns) = E[(r_s - E[r_s])(r_i - E[r_i])]
        //    // std(stock returns) = E[(r_s - E[r_s])^2]
        //    // std(index returns) = E[(r_i - E[r_i])^2]
        //    // E[r_s] = E[r_i] = 0
        //    // Cov(stock returns,
        //}

        public double Delta(Equity equity) {
            // Includes quantity of equity.
            Symbol symbol = equity.Symbol;
            if (Constituents.Any(c => c.Symbol == symbol))
            {
                return (double)Constituents.First(c => c.Symbol == symbol).Weight;
            }
            else
            {
                return 0;  //If-Filled Scenario: Beta(symbol);  // Or Correlation?
            }
        }

        public double DeltaIf(Equity equity)
        {
            // Includes quantity of equity.
            Symbol symbol = equity.Symbol;
            if (Constituents.Any(c => c.Symbol == symbol))
            {
                return (double)Constituents.First(c => c.Symbol == symbol).Price(algo.Time);
            }
            else
            {
                return Beta(equity.Symbol);  //If-Filled Scenario: Beta(symbol);  // Or Correlation?
            }
        }

        public double DeltaIf(Option option)
        {
            return Delta(option);
        }

        public double Delta(Option option)
        {
            // Delta(OptionContract, Underlying) * Delta(Equity, Portfolio) / PortfolioQuantity(Equity)
            // This somehow presumes all underlying I have in my portfolio are perfectly correlated. A 1% change in PF implies a 1% change in all constituents. That's not realistic.
            Equity equity = (Equity)option.Underlying;
            double optionDelta = OptionContractWrap.E(algo, option).Greeks(null, null).Delta;
            decimal quantityUnderlying = !Constituents.Any() ? Constituents?.FirstOrDefault(c => c.Symbol == equity.Symbol, null)?.Weight ?? 1 : 1;
            return optionDelta * Delta(equity) / (double)quantityUnderlying;
        }

        public double Gamma(Option option)
        {
            // Review. Delta(OptionContract, Underlying) * Delta(Equity, Portfolio) / PortfolioQuantity(Equity)
            Equity equity = (Equity)option.Underlying;
            double optionGamma = OptionContractWrap.E(algo, option).Greeks(null, null).Gamma;
            decimal quantityUnderlying = !Constituents.Any() ? Constituents?.FirstOrDefault(c => c.Symbol == equity.Symbol, null)?.Weight ?? 1 : 1;
            return optionGamma * Delta(equity) / (double)quantityUnderlying;
        }

        private IEnumerable<IndexConstituent> ProjectPortfolioToEquityConstituents()
        {
            // { Constituent(Symbol s, Weight 1) }  | s is equity } U { Constituent(Symbol s.Underlying, Weight 100) | s is option }
            Symbol indexKey;
            decimal q;
            var constituentWeight = new Dictionary<Symbol, decimal>();

            if (isSPY) {
                return SPYConstituents();
            }

            foreach (var kv in algo.Portfolio)
            {
                indexKey = kv.Key;
                if (indexKey.SecurityType == SecurityType.Equity && (kv.Value?.Quantity ?? 0) != 0)
                {
                    q = kv.Value?.Quantity * 1 ?? 0;
                }
                else if (indexKey.SecurityType == SecurityType.Option && (kv.Value?.Quantity ?? 0) != 0)
                {
                    Option contract = (Option)algo.Securities[indexKey];
                    q = kv.Value?.Quantity * 100 ?? 0;
                    indexKey = contract.Symbol.Underlying;
                }
                else
                {
                    continue;
                }
                constituentWeight[indexKey] = constituentWeight.ContainsKey(indexKey) ? constituentWeight[indexKey] + q : q;
            }

            return constituentWeight.Select(kv => new IndexConstituent(kv.Key, algo, kv.Value));
        }

        private IEnumerable<IndexConstituent> SPYConstituents()
        {
            var constituentWeight = new Dictionary<Symbol, decimal>() { { algo.spy, 1 } };
            return constituentWeight.Select(kv => new IndexConstituent(kv.Key, algo, kv.Value));
        }
    }
}
