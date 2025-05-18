using Fasterflect;
using Google.Protobuf.Collections;
using MathNet.Numerics.LinearAlgebra.Factorization;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Couple options - have 1 handler loading with algo that's never cleared. That way, it can be subscribed over lifetime of process.
    ///     Relevant events could be: Cleared scenario to cancel any existing orders...
    ///     The handler itself can be wired up with fill events to clear its own scenarios once an option is filled...
    ///     All this event handling doesnt really work when I kill the handler upon every received scenario...
    /// 
    /// How should the sweep work? The handler can calc IVs..
    /// New a new sweeper class: SweepIV with start, end, speed.
    /// 
    ///             /// Each scneario will contain 1 option + hedge - in future, could design more complicated multi option risk scenarios, comparing mini portfolios of options
    /// Execution:
    /// Pick the 'eligible scenarios', ie, all option whose utility is better than pure hedging. That utility better be in USD and can be directly converted into discounts.
    /// So each option containing scenario needs to have 
    ///     
    /// Additional complication - dPL of scenario is not the same PL we measure during trading... assume approximation is ok.
    /// 
    /// All this will be difficult to test for correctness. But the limit order files should have a reference of IV, so could plot the quoted IV per option. Still tricky to test. Would need to check that the quoted util is equal
    /// 
    /// How to sweep and keep track of it.
    /// Best scenario is the benchmark. Its utility is slowly reduced by the sweep speed. The sweep speed is the difference between the scenario and the minimum IV divided by time.
    /// So how keep track of it? Send every orderEvent into this. Once best scenario has been received, can start sweeping all scenarios...
    /// </summary>
    public class RiskScenerioHandler
    {
        public Dictionary<Equity, PfRiskScenario[]> PfRiskScenarios { get; internal set; }
        public Dictionary<Equity, Option[]> TradeableOptions { get; internal set; }
        private readonly Foundations _algo;
        private Dictionary<Tuple<Option, OrderDirection>, double> MinimumIVs { get; set; }
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioUtilities { get; set; }
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioFillIVs { get; set; }
        private Dictionary<Equity, Tuple<Option, OrderDirection>> BestOrder { get; set; }
        private Dictionary<Equity, PfRiskScenario> BestScenario { get; set; }        
        private Dictionary<Equity, bool> IsSweeping { get; set; }
        private Dictionary<Equity, double> _bestOrderSweepIV { get; set; }
        public RiskScenerioHandler(Foundations algo)
        {
            PfRiskScenarios = new();
            TradeableOptions = new();
            _algo = algo;
        }
        public void SetScenarios(PfRiskScenario[] scenarios)
        {
            if (scenarios.Length == 0)
            {
                return;
            }
            Equity equity = _algo.ToEquity(scenarios.First().Underlying);
            PfRiskScenario equityHedgeScenario = EquityHedgeScenario(equity);
            double dPLPureEquityHedge = equityHedgeScenario.DPL + equityHedgeScenario.DPLEqHedged;
            if (equityHedgeScenario == null)
            {
                return;
            }
            double equityHedgeQuantity = equityHedgeScenario.HoldingsHedge.FirstOrDefault(x => x.Key == equity.Symbol).Value.Quantity;


            IEnumerable<IO.Holding> possibleOptions = scenarios.Where(x => (x.DPL + x.DPLEqHedged) > dPLPureEquityHedge & Math.Abs(GetOptionQuantity(equity, x.HoldingsHedge)) < equityHedgeQuantity).
                Select(s => GetOptionHolding(equity, s.HoldingsHedge));

            var validScenarios = scenarios.Where(x => 
                (x.DPL + x.DPLEqHedged) > dPLPureEquityHedge 
                & Math.Abs(GetOptionQuantity(equity, x.HoldingsHedge)) < equityHedgeQuantity
            );

            _algo.Log($"SetScenarios(): # ${validScenarios.Count()} / ${scenarios.Length} scenarios.");
            PfRiskScenarios[equity] = scenarios;
            TradeableOptions[equity] = possibleOptions.Select(h => (Option)_algo.Securities[h.Symbol]).ToArray();

            SetMinimumIVs(equity);
            SetScenarioFillIVs(equity);
            SetBestScenario(equity);
        }

        public static double GetOptionQuantity(Equity underlying, MapField<string, IO.Holding> holdings)
        {
            // Example logic: Sum all the values in the holdings
            return holdings.FirstOrDefault(h => h.Key == underlying.Symbol.Value).Value.Quantity;
        }

        public static IO.Holding GetOptionHolding(Equity underlying, MapField<string, IO.Holding> holdings)
        {
            // Example logic: Sum all the values in the holdings
            return holdings.FirstOrDefault(h => h.Key != underlying.Symbol.Value).Value;
        }
        public static IO.Holding GetOptionHolding(Equity underlying, PfRiskScenario pfRiskScenario)
        {
            return pfRiskScenario.HoldingsHedge.FirstOrDefault(h => h.Key != underlying.Symbol.Value).Value;
        }

        public PfRiskScenario EquityHedgeScenario(Equity underlying)
        {
            return PfRiskScenarios[underlying].FirstOrDefault(x => x.HoldingsHedge.Keys.Count == 1);
        }


        /// <summary>
        /// 2 conditions. Each dPL must be greater than the pure equity hedge. The utility of the scenario must be greater than the pure equity hedge.
        /// </summary>
        /// <param name="equity"></param>
        /// <returns></returns>
        public Dictionary<Option, decimal> GetScenarioDeltaOptionPositions(Equity equity)
        {
            PfRiskScenarios.TryGetValue(equity, out var scenarios);
            if (scenarios == null)
            {
                return new Dictionary<Option, decimal>();
            }
            PfRiskScenario equityHedgeScenario = EquityHedgeScenario(equity);
            double dPLPureEquityHedge = equityHedgeScenario.DPL + equityHedgeScenario.DPLEqHedged;
            if (equityHedgeScenario == null)
            {
                return new Dictionary<Option, decimal>();
            }


            IEnumerable<IO.Holding> possibleOptions = scenarios.Select(s => GetOptionHolding(equity, s.HoldingsHedge));
            return possibleOptions.ToDictionary(x => (Option)_algo.Securities[x.Symbol], x => (decimal)x.Quantity);
        }

        /// <summary>
        ///     /// 1) A minimum IV that corresponds to a utility equal to equity hedge.
        ///     min_iv = (utilRiskScenario - utilPureHedge) * a / vega (dPL / dIV)
        /// </summary>
        /// <param name="options"></param>
        /// <returns></returns>
        internal void SetMinimumIVs(Equity equity)
        {
            PfRiskScenario equityHedgeScenario = EquityHedgeScenario(equity);
            var pureHedgeUtility = equityHedgeScenario.DPLEqHedged;

            PfRiskScenarios[equity].DoForEach(x =>
            {
                var utilityScenario = x.DPLEqHedged;

                var optionHolding = GetOptionHolding(equity, x);
                var option = (Option)_algo.Securities[optionHolding.Symbol];
                var vega = Vega(option);

                MinimumIVs[Tuple.Create(option, Num2Direction(optionHolding.Quantity))] = (utilityScenario - pureHedgeUtility) / vega;
            });
        }

        internal double Vega(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time.Date);
            return ocw.Vega(_algo.MidIVSSVI(option.Symbol));
        }

        internal void SetScenarioFillIVs(Equity equity)
        {
            PfRiskScenarios[equity].DoForEach(x =>
                {
                    var sym = GetOptionHolding(equity, x).Symbol;
                    var optionHolding = GetOptionHolding(equity, x);
                    var option = (Option)_algo.Securities[optionHolding.Symbol];
                    ScenarioFillIVs[Tuple.Create(option, Num2Direction(optionHolding.Quantity))] = x.IvEnter[sym];
                }
            );
        }

        internal void SetBestScenario(Equity equity)
        {
            BestScenario[equity] = PfRiskScenarios[equity]
                .Where(x => x.HoldingsHedge.Keys.Count > 1)
                .OrderByDescending(x => x.DPLEqHedged)
                .FirstOrDefault();
            var h = GetOptionHolding(equity, BestScenario[equity]);
            Option option = (Option)_algo.Securities[h.Symbol];
            OrderDirection direction = Num2Direction(h.Quantity);
            BestOrder[equity] = Tuple.Create((Option)_algo.Securities[h.Symbol], direction);
            var key = Tuple.Create(option, direction);
            _bestOrderSweepIV[equity] = BestScenario[equity].IvEnter[option.Symbol.Value];
        }

        internal double BestOrderSweepIV(Equity equity, OrderDirection direction)
        {
            if (_bestOrderSweepIV.TryGetValue(equity, out var bestOrderSweepIV))
            {
                return bestOrderSweepIV;
            }
            // Default if nothing else present
            return 0;
        }

        /// 2) EquiUtilitySweeping - A sweeping IV, a minimum that's determined by the sweeper and utility of other options...
        /// Now we have n options, with n vegas and n utilities        
        /// Each other option gets a sweepIV assigned which is:
        /// utilSweepBest = utilScenario - dIV * vega
        /// sweep_iv_scenario = scenario_iv + (utilScenario - utilBest) / vega
        public double GetEquiUtilityIV(Option option, OrderDirection direction)
        {
            var equity = _algo.ToEquity(Underlying(option.Symbol));
            double bestOrderSweepIV = BestOrderSweepIV(equity, direction);

            // In that case we also dont have best scenario and should not quote anything.
            if (bestOrderSweepIV <= 0)
            {
                return 0;
            }

            var key = Tuple.Create(option, direction);

            if (ScenarioFillIVs.TryGetValue(key, out var scenarioFillIV) &&
                ScenarioUtilities.TryGetValue(key, out var scenarioUtility) &&
                BestScenario.TryGetValue((Equity)option.Underlying, out var bestScenario))
            {
                double utilityBestScenario = bestScenario.DPLEqHedged - UtilitySwept(equity);
                double offsetToBestIV = (scenarioUtility - utilityBestScenario) / Vega(option);
                return scenarioFillIV + offsetToBestIV * DIRECTION2NUM[direction];
            }

            return 0;
        }
        /// <summary>
        /// Best Scenario EntryIV - Where we are at now.
        /// So    deltaIV  * dP / DIV
        /// </summary>
        /// <returns></returns>
        public double UtilitySwept(Equity equity)
        {
            if (
                    BestScenario.TryGetValue(equity, out var bestScenario)
                    & _bestOrderSweepIV.TryGetValue(equity, out var currentSweepIV)
                    )
            {
                var h = GetOptionHolding(equity, BestScenario[equity]);
                Option option = (Option)_algo.Securities[h.Symbol];
                OrderDirection direction = Num2Direction(h.Quantity);
                BestOrder[equity] = Tuple.Create((Option)_algo.Securities[h.Symbol], direction);
                
                double entryIV = BestScenario[equity].IvEnter[option.Symbol.Value];
                double deltaIV = entryIV - currentSweepIV;
                return 100 * deltaIV * Vega(option);
            }
            return 0;
        }

        /// Scenario IVs - UtilityIV
        /// sweep speed = (scenario IV - minIV ) / 5min
        public double GetSweepIVSpeed(Option option, OrderDirection direction)
        {
            var key = Tuple.Create(option, direction);
            if (ScenarioFillIVs.TryGetValue(key, out var scenarioFillIV) &&
                MinimumIVs.TryGetValue(key, out var minimumIV))
            {
                return ( scenarioFillIV - minimumIV ) / 300;
            }
            return 0;
        }

        /// <summary>
        /// </summary>
        /// <param name="option"></param>
        /// <param name="direction"></param>
        /// <returns></returns>
        public double SweepIV(Option option, OrderDirection direction)
        {
            return GetEquiUtilityIV(option, direction);
        }

        internal OrderTicket? BestOrderTicket(Option option)
        {
            var t = _algo.orderTickets[option.Symbol].Any() ? _algo.orderTickets[option.Symbol].First() : null;
            IsSweeping[_algo.ToEquity(Underlying(option.Symbol))] = t != null;
            return t;
        }


        public void ClearScenarios(Equity equity)
        {
            if (PfRiskScenarios.ContainsKey(equity))
            {
                PfRiskScenarios.Remove(equity);
                BestScenario.Remove(equity);
                BestOrder.Remove(equity);
                _bestOrderSweepIV.Remove(equity);
                TradeableOptions.Remove(equity);
                IsSweeping.Remove(equity);
            }
            MinimumIVs.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioFillIVs.Remove(o));
            ScenarioFillIVs.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioFillIVs.Remove(o));
        }


        public void ClearScenarios()
        {
            PfRiskScenarios.Keys.DoForEach(x => ClearScenarios(x));
        }
    }
}
