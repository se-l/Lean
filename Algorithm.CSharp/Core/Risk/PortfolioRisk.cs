using QuantConnect.Orders;
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
        public PortfolioProxyIndex Ppi { get; }

        private readonly Foundations algo;
        /// <summary>
        /// Caching
        /// </summary>

        private static Func<Foundations, string> genCacheKey = algo => $"{algo.Time}{algo.OrderEvents.FindAll(x => x.Status == OrderStatus.Filled || x.Status == OrderStatus.PartiallyFilled).Count}";
        private static Func<Foundations, PortfolioRisk> func = algo => new PortfolioRisk(algo);
        private static Func<Foundations, PortfolioRisk> constructorCached = Cache(func, genCacheKey);

        public static PortfolioRisk E(Foundations algo, bool useCache = true)
        {
            // get rid of this useCache parameter.  It's a hack.
            return useCache == true ? constructorCached(algo) : func(algo);
        }

        private PortfolioRisk(Foundations algo, IEnumerable<Position> positions = null, PortfolioProxyIndex ppi = null)
        {
            this.algo = algo;
            algo.Log($"{algo.Time}: PortfolioRisk.Constructor called.");
            Positions = positions ?? GetPositions();
            Ppi = ppi ?? PortfolioProxyIndex.E(algo);  // needs also positions as option input.
            
            //foreach (Position position in Positions)
            //{
            //    position.SetPfGreeks();
            //}
            // Refactor below into GreeksPlus
            Delta = Positions.Sum(x => x.PfDelta);
            Delta100BpUSD = Positions.Sum(x => x.PfDelta100BpUSD);
            Gamma = Positions.Sum(x => x.PfGamma);
            Gamma100BpUSD = Positions.Sum(x => x.PfGamma100BpUSD);
            Theta = Positions.Sum(x => x.PfTheta);
            ThetaUSD = Positions.Sum(x => x.PfThetaUSD);
            Vega = Positions.Sum(x => x.PfVega);
            Vega100BpUSD = Positions.Sum(x => x.PfVega100BpUSD);

            //tex:
            //$$ID_i = \Delta_i * \beta_i * \frac{A_i}{I}$$
            DeltaSPY = Positions.Sum(x => x.DeltaSPY);
            DeltaSPY100BpUSD = Positions.Sum(x => x.DeltaSPY100BpUSD);

            
            //if (orderTickets != null)
            //{
            //    //TODO. What if filled risks....
            //}

            TimeCreated = algo.Time;
            Rho = 0;
        }

        public double Beta(Symbol symbol, int window=30)
        {
            return Ppi.Beta(symbol, window);
        }

        public double PfDeltaIfFilled(Symbol symbol, OrderDirection orderDirection)
        {
            // too tricky currently as it only returns a sensitity. not good for estimating what-if-filled.
            if (symbol.SecurityType == SecurityType.Option)
            {
                Option option = (Option)algo.Securities[symbol];
                return DIRECTION2NUM[orderDirection] * Ppi.DeltaIf(option) * option.ContractMultiplier;
            }
            else if (symbol.SecurityType == SecurityType.Equity)
            {
                return DIRECTION2NUM[orderDirection] * Ppi.DeltaIf((Equity)algo.Securities[symbol]);
            }
            else
            {
                throw new NotImplementedException();
            }            
        }

        public double Correlation(Symbol symbol)
        {
            return algo.Correlation(algo.spy, symbol, 30, Resolution.Daily);
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

        private IEnumerable<Position> GetPositions()
        {
            var positions = new List<Position>();
            foreach (SecurityHolding security_holding in algo.Portfolio.Values.Where(x => x.Quantity != 0))
            {
                positions.Add(new Position(algo, security_holding.Symbol));
            }
            return positions;
        }

        //private void OldWayBuildingRisk()
        //{
        //    foreach (SecurityHolding securityHolding in algo.Portfolio.Values)
        //    {
        //        if (securityHolding.Type == SecurityType.Equity && securityHolding.Quantity != 0)
        //        {
        //            double deltaSecurity = GetPfDelta((Equity)algo.Securities[securityHolding.Symbol]);  // Includes quantity/position.

        //            Delta += deltaSecurity;
        //            Delta100BpUSD += (decimal)deltaSecurity * 100 * BP * ppiPrice;
        //        }
        //        else if (securityHolding.Type == SecurityType.Option && securityHolding.Quantity != 0)
        //        {
        //            Option contract = (Option)algo.Securities.GetValueOrDefault(securityHolding.Symbol, null);
        //            if (contract != null)
        //            {
        //                decimal quantity = securityHolding.Quantity;
        //                //decimal price = algo.MidPrice(securityHolding.Symbol);
        //                OptionContractWrap ocw = OptionContractWrap.E(algo, contract);
        //                GreeksPlus greeks = ocw.Greeks(null, null);  // refactor any PF greeks there providing an index?

        //                double deltaPf = GetPfDelta((Option)algo.Securities[securityHolding.Symbol]);  // Not including quantity or option multiplier.
        //                Delta += deltaPf;

        //                var taylorTerm = contract.ContractMultiplier * quantity * 100 * BP * ppiPrice;
        //                Delta100BpUSD += (decimal)deltaPf * taylorTerm;

        //                double gammaContract = Ppi.Gamma((Option)algo.Securities[securityHolding.Symbol]);  // Not including quantity or option multiplier.
        //                Gamma += gammaContract;
        //                Gamma100BpUSD += (decimal)(0.5 * Math.Pow((double)taylorTerm, 2) * gammaContract);

        //                // Below 2 simplifications assuming a pure options portfolio.
        //                Theta += greeks.Theta;
        //                ThetaUSD += (decimal)greeks.Theta * contract.ContractMultiplier * quantity;

        //                // Summing up individual vegas. Only applicable to Ppi constructed from options, not for Ppi(SPY or any index)
        //                Vega += greeks.Vega;
        //                Vega100BpUSD += (decimal)greeks.Vega * contract.ContractMultiplier * quantity;  // * 100 * BP * algo.Securities[ocw.UnderlyingSymbol].VolatilityModel.Volatility;
        //            }
        //        }
        //    }
        //}
    }
}






