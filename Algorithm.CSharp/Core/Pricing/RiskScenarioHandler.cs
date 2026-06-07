using Google.Protobuf.Collections;
using NodaTime;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    /// <summary>
    /// Need to log this well. What do I wanna know.
    /// Key is the IV or price being returned by this class and whether the utility of the respective option aligns with what is quoted. Given all option quotes depend on 
    /// how much the cheapest option is swept, best to log that too in each order. Simpler later...
    /// Therefore, need to log each time: 
    /// 
    /// Time | Option | IV | Price | PriceUnderlying | Utility | Vega | BestOption | BestOptionIV | BestOptionUtility
    /// All this will be difficult to test for correctness. But the limit order files should have a reference of IV, so could plot the quoted IV per option. Still tricky to test. Would need to check that the quoted util is equal
    /// 
    /// A handler like this will be also implemented for sweeping TargetPortfolioOptions... Highest utility first.
    /// 
    /// How to sweep and keep track of it.
    /// Best scenario is the benchmark. Its utility is slowly reduced by the sweep speed. The sweep speed is the difference between the scenario and the minimum IV divided by time.
    /// So how keep track of it? Send every orderEvent into this. Once best scenario has been received, can start sweeping all scenarios...
    /// </summary>
    public class RiskScenarioHandler
    {
        private Dictionary<Equity, IEnumerable<PfRiskScenarioPb>> PfRiskScenarios { get; set; }
        public Dictionary<Equity, Option[]> TradableOptions { get; internal set; }
        private readonly Foundations _algo;
        private Dictionary<Tuple<Option, OrderDirection>, double> WorstIVs { get; set; }
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioUtilities { get; set; }
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioEntryIVs { get; set; }
        private Dictionary<Equity, Tuple<Option, OrderDirection>> BestOrder { get; set; }
        private Dictionary<Equity, PfRiskScenarioPb> BestScenario { get; set; }        
        private Dictionary<Equity, DateTime> SweepStart { get; set; }
        private Dictionary<Equity, TimeSpan> SweepDuration { get; set; }
        public ConcurrentDictionary<Equity, (StreamWriter writer, object lockObj)> Writers { get; }
        private ConcurrentDictionary<Equity, List<SweepSchedule>> _schedules = new();
        public RiskScenarioHandler(Foundations algo)
        {
            PfRiskScenarios = new();
            TradableOptions = new();
            WorstIVs = new();
            ScenarioUtilities = new();
            ScenarioEntryIVs = new();
            BestOrder = new();
            BestScenario = new();
            SweepStart = new();
            SweepDuration = new();
            _algo = algo;
            Writers = new();
        }
        private bool IsSweepScheduled(Equity equity, OrderDirection direction)
        {
            if (!_schedules.ContainsKey(equity))
            {
                _algo.Error($"{_algo.Time} RiskScenarioHandler.IsSweepScheduled(): No schedules found for equity: {equity.Symbol.Value}");
                return false;
            }
            
            return _schedules[equity].Any(s => 
            _algo.ActiveRegimes[equity].Contains(s.MarketRegime)
            && _algo.Time.Date + s.Start <= _algo.Time 
            && _algo.Time <= _algo.Time.Date + s.End
            && s.Direction == direction
            );
        }
        internal void SetScenarios(PfRiskScenarioPb[] scenarios)
        {
            if (scenarios.Length == 0)
            {
                return;
            }
            Equity equity = _algo.ToEquity(scenarios.First().Underlying);
            if (equity == null)
            {
                _algo.Error($"{_algo.Time} RiskScenarioHandler.SetScenarios(): No equity found for scenarios: {scenarios.First().Underlying}");
                return;
            }

            PfRiskScenarios[equity] = scenarios
                .Where(s => s.Score > 0)
                .Where(s => _algo.Securities.ContainsKey(GetOptionHolding(equity, s.HoldingsHedge)?.Symbol ?? ""));

            if (!PfRiskScenarios[equity].Any())
            {
                _algo.Error($"{_algo.Time} RiskScenarioHandler.SetScenarios(): No valid scenarios found for equity: {equity.Symbol.Value}");
                return;
            }

            TradableOptions[equity] = PfRiskScenarios[equity]
                .Select(s => GetOptionHolding(equity, s.HoldingsHedge))
                .Select(h => (Option)_algo.Securities[h?.Symbol ?? ""])
                .ToArray();

            _algo.Log($"{_algo.Time} SetScenarios(): # ${PfRiskScenarios[equity].Count()} / ${scenarios.Length} scenarios.");

            SetScenarioFillIVs(equity);
            SetWorstIVs(equity);
            SetBestScenario(equity);
            SetSweepSchedules(equity, OrderDirection.Buy);
            SetSweepSchedules(equity, OrderDirection.Sell);
            SetSweepDuration(equity);
        }

        /// <summary>
        /// </summary>
        /// <param name="option"></param>
        /// <param name="direction"></param>
        /// <returns></returns>
        internal double? SweepIv(Option option, OrderDirection direction)
        {
            Write(option, direction);

            if (IsSweepScheduled(_algo.ToEquity(option.Underlying.Symbol), direction)) {
                double? iv = GetEquiUtilityIv(option, direction);
                _algo.Log($"{_algo.Time} RiskScenarioHandler.SweepIV(): Sweeping {option.Symbol.Value}, direction={direction}, iv={iv:0.000}");
                return iv;
            }
            else
            {
                _algo.Log($"{_algo.Time} RiskScenarioHandler.SweepIV(): Sweep not scheduled hence return null IV - {option.Symbol.Value}, direction={direction}, iv=");
                return null;
            }
        }

        internal void ClearScenarios(Equity equity)
        {
            if (PfRiskScenarios.Remove(equity))
            {
                BestScenario.Remove(equity);
                BestOrder.Remove(equity);
                TradableOptions.Remove(equity);
                SweepStart.Remove(equity);
            }
            WorstIVs.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioEntryIVs.Remove(o));
            ScenarioEntryIVs.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioEntryIVs.Remove(o));
            ScenarioUtilities.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioEntryIVs.Remove(o));
            ScenarioEntryIVs.Keys
                    .Where(o => o.Item1.Underlying == equity)
                    .ToList()
                    .ForEach(o => ScenarioEntryIVs.Remove(o));
        }

        public bool ShouldUpdateSweepOrders(Equity equity)
        {
            if (BestOrder.TryGetValue(equity, out var bestOrderTuple) &&
                SweepStart.TryGetValue(equity, out DateTime sweepStartTime) &&
                ScenarioEntryIVs.TryGetValue(bestOrderTuple, out double bestEntryIv) &&
                SweepDuration.TryGetValue(equity, out TimeSpan sweepDuration) &&
                BestOrderTicket(bestOrderTuple.Item1) != null
                )
            {
                OrderDirection direction = bestOrderTuple.Item2;
                double? bestOrderSweepIv = BestOrderSweepIv(equity);
                if (bestOrderSweepIv == null)
                {
                    _algo.Log($"{_algo.Time} RiskScenarioHandler.ShouldUpdateSweepOrders(): No bestOrderSweepIV found for {bestOrderTuple.Item1.Symbol.Value}, direction={direction}");
                    return false;
                }
                // Requesting IV for best scenario option. Incrementing towards better value in steps of 0.1 and 5mins from best to worst.
                double ratioTimeSwept = (_algo.Time - sweepStartTime).TotalSeconds / sweepDuration.TotalSeconds;
                double rangeIv = RangeIv(bestOrderTuple, bestEntryIv);
                
                bool shouldUpdate = direction == OrderDirection.Buy
                    ? bestOrderSweepIv + 0.01 < bestEntryIv + ratioTimeSwept * rangeIv
                    : bestOrderSweepIv - 0.01 > bestEntryIv - ratioTimeSwept * rangeIv;
                if (shouldUpdate)
                {
                    _algo.Log($"{_algo.Time} RiskScenarioHandler.ShouldUpdateSweepOrders(): Yes.BestSymbol={bestOrderTuple.Item1.Symbol.Value}, direction={direction}, bestOrderSweepIV={bestOrderSweepIv:0.000}, bestEntryIV={bestEntryIv:0.000}, ratioTimeSwept={ratioTimeSwept:0.000}, rangeIV={rangeIv:0.000}");
                    return true;
                }
            }
            return false;
        }
        private static IO.HoldingPb GetOptionHolding(Equity underlying, PfRiskScenarioPb pfRiskScenario)
        {
            return pfRiskScenario.HoldingsHedge.FirstOrDefault(h => h.Key != underlying.Symbol.Value).Value;
        }
        private static double GetOptionQuantity(Equity underlying, MapField<string, IO.HoldingPb> holdings)
        {
            // Example logic: Sum all the values in the holdings
            return holdings.FirstOrDefault(h => h.Key == underlying.Symbol.Value).Value.Quantity;
        }

        private static IO.HoldingPb? GetOptionHolding(Equity underlying, MapField<string, IO.HoldingPb> holdings)
        {
            // Example logic: Sum all the values in the holdings
            return holdings.FirstOrDefault(h => h.Key != underlying.Symbol.Value).Value;
        }

        private void SetSweepDuration(Equity equity)
        {
            SweepDuration[equity] = _schedules[equity].Where(s => _algo.Time.Date + s.Start <= _algo.Time && _algo.Time <= _algo.Time.Date + s.End).FirstOrDefault()?.Duration ?? TimeSpan.FromSeconds(1);
        }

        private void SetSweepSchedules(Equity equity, OrderDirection direction)
        {
            Symbol underlying = equity.Symbol;
            _schedules[equity] = new();
            DateTime nextReleaseDate = _algo.NextReleaseDate(underlying).Date;
            DateTime prevReleaseDate = _algo.PreviouReleaseDate(underlying).Date;


            List<SweepScheduleCfg> cfgSchedules = _algo.Cfg.SweepSchedules.TryGetValue(underlying, out cfgSchedules) ? cfgSchedules : _algo.Cfg.SweepSchedules[CfgDefault];
            _schedules[equity] = cfgSchedules.Select((s) => new SweepSchedule(s)).ToList();
        }

        /// <summary>
        ///     /// 1) A minimum IV that corresponds to a utility equal to equity hedge.
        ///     min_iv = (utilRiskScenario - utilPureHedge) * a / vega (dPL / dIV)
        /// </summary>
        /// <returns></returns>
        private void SetWorstIVs(Equity equity)
        {
            PfRiskScenarios[equity].DoForEach(x =>
            {
                var utilityScenario = x.Score;

                var optionHolding = GetOptionHolding(equity, x);
                var option = (Option)_algo.Securities[optionHolding.Symbol];
                var vega = Vega(option);
                var entryIv = ScenarioEntryIVs[Tuple.Create(option, Num2Direction(optionHolding.Quantity))];
                var direction = Num2Direction(optionHolding.Quantity);
                var key = Tuple.Create(option, direction);
                var sign = DIRECTION2NUM[direction];

                // Add x amount to the utility to force the sweeper go further.
                double utilityBump = _algo.Cfg.SweepWorstIVUtilityBump.TryGetValue(equity.ToString(), out utilityBump) ? utilityBump : _algo.Cfg.SweepWorstIVUtilityBump[CfgDefault];
                utilityScenario += utilityBump;  // ~20 USD

                WorstIVs[key] = entryIv + sign * utilityScenario / (100 * vega);
                // For testing purposes, we set the worst IV to a fixed extreme value
                // so the range is large and sweep IVs exceed bid/ask.
                // WorstIVs[key] = direction == OrderDirection.Buy ? 3 : 0;
            });
        }

        private double Vega(Option option)
        {
            OptionContractWrap ocw = OptionContractWrap.E(_algo, option, _algo.Time.Date);
            return ocw.Vega(_algo.MidIVSsvi(option.Symbol));
        }

        private void SetScenarioFillIVs(Equity equity)
        {
            PfRiskScenarios[equity].DoForEach(x =>
                {
                    var sym = GetOptionHolding(equity, x).Symbol;
                    var optionHolding = GetOptionHolding(equity, x);
                    var option = (Option)_algo.Securities[optionHolding.Symbol];
                    ScenarioEntryIVs[Tuple.Create(option, Num2Direction(optionHolding.Quantity))] = x.IvEnter[sym];
                    ScenarioUtilities[Tuple.Create(option, Num2Direction(optionHolding.Quantity))] = x.Score;
                }
            );
        }

        private double GetWorstIv(Option option, OrderDirection direction)
        {
            var key = Tuple.Create(option, direction);
            if (WorstIVs.TryGetValue(key, out double worstIv))
            {
                return worstIv;
            }
            return direction == OrderDirection.Sell ? 0 : 99;
        }

        private void SetBestScenario(Equity equity)
        {
            BestScenario[equity] = PfRiskScenarios[equity]
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            
            IO.HoldingPb h = GetOptionHolding(equity, BestScenario[equity]);
            Option option = (Option)_algo.Securities[h.Symbol];
            OrderDirection direction = Num2Direction(h.Quantity);

            BestOrder[equity] = Tuple.Create(option, direction);
            _algo.Log($"{_algo.Time} RiskScenarioHandler.SetBestScenario(): BestOrder={option.Symbol.Value}, Direction={direction}");
        }

        private double? BestOrderSweepIv(Equity equity)
        {
            if (!BestOrder.TryGetValue(equity, out var bestOrder))
            {
                return null;
            }
            
            // Have a live order
            OrderTicket t = BestOrderTicket(bestOrder.Item1);
            if (t != null)
            {
                Option option = bestOrder.Item1;
                return OptionContractWrap.E(_algo, option, _algo.Time).IV(t.Get(OrderField.LimitPrice), (decimal)_algo.MidPrice(option.Underlying.Symbol), 0.001);
            }
            
            // No live order. So take entry IV
            return GetEntryIv(bestOrder.Item1, bestOrder.Item2);
        }

        public double? GetEquiUtility(Option option, OrderDirection direction)
        {
            double? ivNow = GetEquiUtilityIv(option, direction);
            if (ivNow != null && ScenarioEntryIVs.TryGetValue(Tuple.Create(option, direction), out double entryIv)) 
            {
                double entryUtility = ScenarioUtilities[Tuple.Create(option, direction)];
                return entryUtility - 100 * ((double)ivNow - entryIv) * Vega(option);
            };
            return null;
        }

        /// 2) EquiUtilitySweeping - A sweeping IV, a minimum that's determined by the sweeper and utility of other options...
        /// Now we have n options, with n vegas and n utilities        
        /// Each other option gets a sweepIV assigned which is:
        /// utilSweepBest = utilScenario - dIV * vega
        /// sweep_iv_scenario = scenario_iv + (utilScenario - utilBest) / vega
        private double? GetEquiUtilityIv(Option option, OrderDirection direction)
        {
            var equity = _algo.ToEquity(Underlying(option.Symbol));
            double? bestOrderSweepIv = BestOrderSweepIv(equity);
            var key = Tuple.Create(option, direction);

            // In that case we also dont have best scenario and should not quote anything.
            if (bestOrderSweepIv == null || bestOrderSweepIv <= 0)
            {
                return null;
            }

            // option is BestOption. Incrementing towards better value in steps of 0.5%.
            else if (
                BestOrder.TryGetValue(equity, out var bestOrderOptionDirection) &&
                bestOrderOptionDirection.Item1.Symbol == option.Symbol &&
                bestOrderOptionDirection.Item2 == direction
                )
            {
                DateTime sweepStartTime = SweepStart.TryGetValue(equity, out sweepStartTime) ? sweepStartTime : _algo.Time;
                double ratioTimeSwept = (_algo.Time - sweepStartTime).TotalSeconds / SweepDuration[equity].TotalSeconds;
                double? entryIv = GetEntryIv(key);
                if (entryIv == null)
                {
                    _algo.Error($"{_algo.Time} RiskScenarioHandler.GetEquiUtilityIV(): No entry IV found for {option.Symbol.Value} in direction {direction}");
                    return null;
                }
                double rangeIv = RangeIv(key, (double)entryIv);
                int sign = DIRECTION2NUM[direction];
                return (double)entryIv + sign * ratioTimeSwept * rangeIv;
            }                

            if (ScenarioEntryIVs.TryGetValue(key, out double scenarioFillIv) &&
                ScenarioUtilities.TryGetValue(key, out double scenarioUtility) &&
                BestScenario.TryGetValue((Equity)option.Underlying, out PfRiskScenarioPb bestScenario))
            {
                double utilityBestScenario = bestScenario.Score - UtilitySwept(equity);
                double offsetToBestIv = (scenarioUtility - utilityBestScenario) / (100 * Vega(option));  // a negative number by construction
                // For buys, quote lower than best. Doesnt make fully sense, given we are on a surface and best and this option and not on the same point.
                return scenarioFillIv + offsetToBestIv * DIRECTION2NUM[direction];
            }

            return null;
        }
        /// <summary>
        /// Avoid crossing spread on first submitting order.
        /// </summary>
        private double? GetEntryIv(Option option, OrderDirection direction)
        {
            var key = Tuple.Create(option, direction);
            if (!ScenarioEntryIVs.TryGetValue(key, out double scenarioEntryIv))
            {
                _algo.Error($"{_algo.Time} RiskScenarioHandler.GetEntryIv(): No ScenarioEntryIVs found for {option.Symbol.Value} in direction {direction}");
                return null;
            }
            decimal currentBBPrice = direction == OrderDirection.Buy 
                ? _algo.Securities[option.Symbol].BidPrice 
                : _algo.Securities[option.Symbol].AskPrice;
            double entryIv = OptionContractWrap.E(_algo, option, _algo.Time.Date).IV(currentBBPrice, _algo.MidPrice(Underlying(option)), 0.001);
            return direction == OrderDirection.Buy
                ? Math.Min(scenarioEntryIv, entryIv)
                : Math.Max(scenarioEntryIv, entryIv);
        }
        private double? GetEntryIv(Tuple<Option, OrderDirection> key) => GetEntryIv(key.Item1, key.Item2);

        private double RangeIv(Tuple<Option, OrderDirection> orderTuple, double entryIv)
        {
            double worstIv = GetWorstIv(orderTuple.Item1, orderTuple.Item2);
            return Math.Abs(entryIv - worstIv);
        }

        /// <summary>
        /// Best Scenario EntryIV - Where we are at now.
        /// So    deltaIV  * dP / DIV
        /// </summary>
        /// <returns></returns>
        private double UtilitySwept(Equity equity)
        {
            if (
                BestOrder.TryGetValue(equity, out var bestOrder)
            )
            {
                double? bestOrderSweepIv = BestOrderSweepIv(equity);
                if (bestOrderSweepIv == null)
                {
                    return 0;
                }
                double entryIv = ScenarioEntryIVs[bestOrder];
                double deltaIv = Math.Abs(entryIv - (double)bestOrderSweepIv);
                return deltaIv == 0 ? 0 : 100 * deltaIv * Vega(bestOrder.Item1);
            }
            return 0;
        }

        private OrderTicket? BestOrderTicket(Option option)
        {
            if (_algo.orderTickets.TryGetValue(option.Symbol, out var tickets) && tickets.Any())
            {
                var t = tickets.First();
                if (t != null)
                {
                    if (!SweepStart.ContainsKey(_algo.ToEquity(Underlying(option.Symbol))))
                    {
                        SweepStart[_algo.ToEquity(Underlying(option.Symbol))] = t.SubmitRequest.Time.ConvertTo(DateTimeZone.Utc, _algo.TimeZone);
                    }                    
                    return t;
                }
            }
            return null;
        }

        private List<string> _header = new()
        {
            "Time", "Option", "Direction", "IV", "PriceOfIV", "PriceUnderlying", "Vega", "ScenarioFillIV", "MarketEntryIV", "ScenarioUtility", "Utility", "BestOption", 
            "BestOptionIV", "BestOptionUtility", "UtilitySwept"
        };

        private (StreamWriter writer, object lockObj) GetStreamWriter(Equity equity)
        {
            if (!Writers.TryGetValue(equity, out var entry))
            {
                var path = Path.Combine(Globals.PathAnalytics, equity.Symbol.Value, "RiskScenarioHandler.csv");
                _algo.Log($"{_algo.Time} RiskScenarioHandler.GetStreamWriter(): Creating path {path}");
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
                entry = (new StreamWriter(path, true), new object());
                Writers[equity] = entry;
                Writers[equity].writer.WriteLine(string.Join(",", _header));
            }
            return entry;
        }

        private void Write(Option option, OrderDirection direction)
        {
            Equity equity = _algo.ToEquity(Underlying(option.Symbol));

            double? iv = GetEquiUtilityIv(option, direction);
            double npvRaw = (iv == null || !double.IsFinite(iv.Value))
                ? double.NaN
                : OptionContractWrap.E(_algo, option, _algo.Time.Date).NPV(iv.Value, _algo.MidPrice(option.Underlying.Symbol));
            
            (StreamWriter writer, object lockObj) = GetStreamWriter(equity);
            lock (lockObj)
            {
                // Need to put a file lock here because access is concurrent.
                writer.WriteLine(string.Join(",",
                    _algo.Time,
                    option.Symbol.Value,
                    direction,
                    iv,
                    npvRaw,
                    _algo.MidPrice(equity.Symbol),
                    Vega(option),
                    CollectionExtensions.GetValueOrDefault(ScenarioEntryIVs, Tuple.Create(option, direction), 0),
                    GetEntryIv(option, direction),
                    CollectionExtensions.GetValueOrDefault(ScenarioUtilities, Tuple.Create(option, direction), 0),
                    GetEquiUtility(option, direction),
                    BestOrder.TryGetValue(equity, out var tup) ? tup.Item1.Symbol.Value : "",
                    BestOrderSweepIv(equity),
                    BestOrder.ContainsKey(equity) ? GetEquiUtility(BestOrder[equity].Item1, BestOrder[equity].Item2) : "",
                    UtilitySwept(equity)
                    )
                );
            }
            
        }
    }
}
