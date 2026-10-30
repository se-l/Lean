using Google.Protobuf.Collections;
using NodaTime;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
        /// <summary>Cap on scored scenarios carried into the order loop (see SetScenarios).</summary>
        private const int MaxScenarios = 100;

        private Dictionary<Equity, IEnumerable<PfRiskScenarioPb>> PfRiskScenarios { get; set; }
        public Dictionary<Equity, Option[]> TradableOptions { get; internal set; }
        private readonly Foundations _algo;
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioUtilities { get; set; }
        private Dictionary<Tuple<Option, OrderDirection>, double> ScenarioEntryIVs { get; set; }
        private Dictionary<Equity, Tuple<Option, OrderDirection>> BestOrder { get; set; }
        private Dictionary<Equity, PfRiskScenarioPb> BestScenario { get; set; }        
        private Dictionary<Equity, TimeSpan> SweepDuration { get; set; }
        private ConcurrentDictionary<Equity, List<SweepSchedule>> _schedules = new();

        /// <summary>
        /// One sweep clock per ORDER TICKET, i.e. per (contract, direction); removed when that ticket
        /// reaches a terminal state. The anchors are FROZEN at the first quote: a live bid/ask as the
        /// anchor means the walk collapses onto our own quote when other participants tighten the
        /// spread around it (observed: spread 10 -> 1, then the sweep covered the whole original range
        /// in one step). Targets stay dynamic but are capped by our own surface.
        /// </summary>
        private sealed class SweepState
        {
            public DateTime StartTime;
            public TimeSpan Duration;
            public double IvStart;       // frozen: IV(favourable(near touch, surface price)) at start
            public decimal PriceStart;   // NPV(IvStart), the price at ratio 0
            public double UtilityStart;  // USD utility of trading at PriceStart
        }

        private readonly Dictionary<(Symbol Symbol, OrderDirection Direction), SweepState> _sweeps = new();
        private readonly ConcurrentDictionary<(Symbol, DateTime), double> _vegaByBar = new();

        // Two throttles for the sweep's diagnostics: one line per contract per minute saying WHY a
        // sweep IV is missing, and one sweep-refresh trigger per equity per minute (the gate runs on
        // every slice, and the run it triggers costs a full RunSignals pass).
        private readonly Dictionary<(Symbol, OrderDirection), DateTime> _lastSweepNullReasonLog = new();
        private readonly Dictionary<Equity, DateTime> _lastSweepRefresh = new();
        private static readonly TimeSpan SweepLogThrottle = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan SweepRefreshPeriod = TimeSpan.FromMinutes(1);
        public RiskScenarioHandler(Foundations algo)
        {
            PfRiskScenarios = new();
            TradableOptions = new();
            ScenarioUtilities = new();
            ScenarioEntryIVs = new();
            BestOrder = new();
            BestScenario = new();
            SweepDuration = new();
            _algo = algo;
        }
        private bool IsSweepScheduled(Equity equity, OrderDirection direction)
        {
            if (!_schedules.ContainsKey(equity))
            {
                // The schedule is CONFIG, not a scenario response: populate it on demand so a sweep can
                // start before the first SetScenarios round trip (and after a fill cleared the state).
                SetSweepSchedules(equity, direction);
            }

            if (!_schedules.ContainsKey(equity) || _schedules[equity].Count == 0)
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
                .Where(s => _algo.Securities.ContainsKey(GetOptionHolding(equity, s.HoldingsHedge)?.Symbol ?? ""))
                // Only the best scenarios are actionable, and every survivor costs a full utility
                // evaluation in GetDesiredOrders (two utilities, each with a greek vector across DS
                // buckets: 736 scenarios measured ~94 s of compute per pass). Keeping the top 100 by
                // score bounds the loop without changing which hedge wins — the discarded tail is
                // exactly what the loop's minUtility gate rejects.
                .OrderByDescending(s => s.Score)
                .Take(MaxScenarios)
                .ToArray();

            if (!PfRiskScenarios[equity].Any())
            {
                _algo.Error($"{_algo.Time} RiskScenarioHandler.SetScenarios(): No valid scenarios found for equity: {equity.Symbol.Value}");
                return;
            }

            TradableOptions[equity] = PfRiskScenarios[equity]
                .Select(s => GetOptionHolding(equity, s.HoldingsHedge))
                .Select(h => (Option)_algo.Securities[h?.Symbol ?? ""])
                // Several scenarios commonly share one hedge contract; the utility loop is keyed by
                // contract, so duplicates would only re-evaluate the same option with different DS
                // weights.
                .Distinct()
                .ToArray();

            _algo.Log($"{_algo.Time} SetScenarios(): # ${PfRiskScenarios[equity].Count()} / ${scenarios.Length} scenarios.");

            SetScenarioFillIVs(equity);
            SetBestScenario(equity);
            SetSweepSchedules(equity, OrderDirection.Buy);
            SetSweepSchedules(equity, OrderDirection.Sell);
            SetSweepDuration(equity);
        }

        /// <summary>
        /// The sweep curve for a contract, in PRICE terms: the live touch at ratio 0, walking towards the
        /// branch's target price as the active schedule's Duration elapses.
        ///
        /// Anchored on the touch PRICE, not on an entry IV. An entry IV from any source (feed, solver,
        /// scenario) that does not price back to the touch moves the whole walk, and the pricer turns that
        /// IV straight into a limit price: with a stale ask IV of 0.92 against a 9.55 ask that prices near
        /// 0.66, the second quote crossed 80% of the spread on the first tick. Anchoring on the price makes
        /// ratio 0 exactly the touch, by construction.
        /// </summary>
        internal (decimal Price, decimal EntryPrice, decimal TargetPrice, double Ratio, double TargetIv)? SweepCurve(Option option, OrderDirection direction)
        {
            var sw = Stopwatch.StartNew();
            var (juliaCallsBefore, juliaMsBefore) = JuliaPricingClient.Snapshot();

            var curve = ComputeSweepCurve(option, direction);

            var (juliaCalls, juliaMs) = JuliaPricingClient.Delta(juliaCallsBefore, juliaMsBefore);
            _algo.Log($"{_algo.Time} RiskScenarioHandler.SweepIV(): Sweeping {option.Symbol.Value}, direction={direction}, " +
                      (curve.HasValue
                          ? $"targetIv={curve.Value.TargetIv:0.000}, entry={curve.Value.EntryPrice:0.0000}, target={curve.Value.TargetPrice:0.0000}, ratio={curve.Value.Ratio:0.000}, price={curve.Value.Price:0.0000}"
                          : "no sweep (see the sweep source line for the reason)"));
            _algo.Log($"{_algo.Time} RiskScenarioHandler.SweepIvTiming: {option.Symbol.Value}, {direction}, total={sw.ElapsedMilliseconds}ms, juliaCalls={juliaCalls}, juliaMs={juliaMs:0}");
            return curve;
        }

        /// <summary>Current sweep price only, for callers that must not log on every slice.</summary>
        internal decimal? SweepPriceNow(Option option, OrderDirection direction) => ComputeSweepCurve(option, direction)?.Price;

        /// <summary>
        /// The sweep curve for one contract: its FROZEN start anchor (IV) walking towards a
        /// surface-capped target over the active schedule's duration.
        ///
        /// He asked for exactly this: the anchor cannot be a live bid/ask, because other participants
        /// tighten the spread around our own quote and the whole walk then collapses onto the new,
        /// much smaller spread (spread 10 -> 1, and the sweep covered the original range in one step).
        /// Start = favourable(near touch, surface price) captured once; target =
        /// favourable(far touch, surface price) derived per evaluation, so our own surface - not the
        /// book - is the ceiling.
        ///
        /// The ratio is not time-only: see SweepRatio, which applies the equi-utility frontier.
        /// </summary>
        private (decimal Price, decimal EntryPrice, decimal TargetPrice, double Ratio, double TargetIv)? ComputeSweepCurve(Option option, OrderDirection direction)
        {
            Equity equity = _algo.ToEquity(Underlying(option.Symbol));
            if (equity == null)
            {
                LogSweepNullReason(option, direction, $"no equity security for underlying {Underlying(option.Symbol)}");
                return null;
            }

            if (!IsSweepScheduled(equity, direction))
            {
                LogSweepNullReason(option, direction, "no sweep schedule matches the active regime/direction at this time");
                return null;
            }

            decimal nearTouch = NearTouch(option, direction);
            decimal farTouch = FarTouch(option, direction);
            decimal? surfacePrice = SurfacePrice(option);
            if (nearTouch <= 0 || farTouch <= 0 || surfacePrice == null)
            {
                LogSweepNullReason(option, direction, $"one-sided book or uncalibrated surface (near={nearTouch:0.0000}, far={farTouch:0.0000}, surface={(surfacePrice.HasValue ? surfacePrice.Value.ToString("0.0000") : "none")})");
                return null;
            }

            SweepState state = GetOrCreateSweep(option, direction, nearTouch, surfacePrice.Value);
            if (state == null)
            {
                return null;   // GetOrCreateSweep logged the reason
            }

            // Target: dynamic, capped by our own surface (never better than fair value on our side).
            double ivTarget = Iv(Favourable(direction, farTouch, surfacePrice.Value), option);
            if (!double.IsFinite(ivTarget) || ivTarget <= 0)
            {
                LogSweepNullReason(option, direction, "the surface-capped target does not invert to an IV");
                return null;
            }

            // Monotone inwards. A target on the passive side of the start means our fair value sits
            // outside the touch on our own side: do not sweep at all, rest on the anchor, and say so.
            bool degenerate = direction == OrderDirection.Buy ? ivTarget <= state.IvStart : ivTarget >= state.IvStart;
            if (degenerate)
            {
                LogSweepNullReason(option, direction, $"fair value sits outside the touch (startIv={state.IvStart:0.000}, targetIv={ivTarget:0.000}) - not swept, resting at the anchor");
                ivTarget = state.IvStart;
            }

            double ratio = degenerate ? 0 : SweepRatio(state, equity, direction, option, ivTarget);
            double ivNow = state.IvStart + ratio * (ivTarget - state.IvStart);

            decimal price = Npv(ivNow, option);
            decimal targetPrice = degenerate ? state.PriceStart : Npv(ivTarget, option);
            if (price <= 0 || targetPrice <= 0)
            {
                LogSweepNullReason(option, direction, $"the sweep IV does not price (ivNow={ivNow:0.000}, ivTarget={ivTarget:0.000})");
                return null;
            }

            return (price, state.PriceStart, targetPrice, ratio, ivTarget);
        }

        /// <summary>
        /// Sweep ratio for this contract at this instant.
        ///
        /// The contract with the highest USD utility is the FRONTIER and sweeps on its own clock,
        /// r_f = elapsed / duration. Every other contract is priced by the EQUI-UTILITY solve: the
        /// frontier's consumed USD (100 * vega_f * |iv_f - iv_start_f|) is transferred onto this
        /// contract's own utility budget, converted to IV with ITS vega, expressed as a fraction of its
        /// own range, and capped by r_f. Consequences:
        ///   * at t = 0 every contract sits exactly on its frozen anchor (nothing jumps into the spread);
        ///   * a contract with a lower USD utility can never be swept further than the frontier;
        ///   * a contract whose utility budget is exhausted stops (utility 0 = the MinUtility boundary).
        /// All quote in parallel - there is no gate.
        /// </summary>
        private double SweepRatio(SweepState state, Equity equity, OrderDirection direction, Option option, double ivTarget)
        {
            double range = ivTarget - state.IvStart;
            if (Math.Abs(range) < 1e-9)
            {
                return 0;   // no room between the anchor and the surface: nothing to sweep
            }

            double vega = VegaForBar(option.Symbol);
            if (!(vega > 0))
            {
                return 0;
            }

            // PACE comes from the schedule: the GROUP's elapsed fraction. One clock per
            // (equity, direction), seeded by the earliest live instance and inherited by every later
            // ticket, so a fresh ticket cannot restart the group (it did: the group froze on 68 of 899
            // sampled timestamps and a lower-utility contract sat ahead of the frontier on 69).
            List<(Symbol Symbol, SweepState State)> live = LiveSweeps(equity, direction);
            double rGroup = live.Count > 0 ? live.Max(i => ElapsedRatio(i.State)) : ElapsedRatio(state);

            // DEPTH comes from the equi-utility constraint: this contract's USD utility buys at most
            // budgetFraction of its OWN range (100 * vega * |range| USD is what the whole range costs).
            // A contract whose utility cannot pay for the full range stops early; the highest-utility
            // contract runs the whole window. This is the same relation as the old
            // `sweep_iv = scenario_iv + (util - utilBest)/vega`, expressed as a fraction so the schedule
            // still decides the pace: at r = 0 every contract sits on its anchor, at r = 1 it is on its
            // surface-capped target - unless its utility budget runs out first.
            double rangeUsd = 100.0 * vega * Math.Abs(range);
            double budgetFraction = rangeUsd > 0 ? Math.Max(0.0, state.UtilityStart) / rangeUsd : 1.0;

            return Math.Clamp(Math.Min(rGroup, budgetFraction), 0, 1);
        }

        /// <summary>Live sweep instances of this equity and direction (a terminal ticket leaves the group).</summary>
        private List<(Symbol Symbol, SweepState State)> LiveSweeps(Equity equity, OrderDirection direction)
        {
            List<(Symbol, SweepState)> live = new();
            foreach (var kvp in _sweeps)
            {
                if (kvp.Key.Direction != direction) continue;
                if (kvp.Key.Symbol.Underlying != equity.Symbol) continue;
                if (LiveTicket(kvp.Key.Symbol) == null) continue;
                live.Add((kvp.Key.Symbol, kvp.Value));
            }
            return live;
        }

        /// <summary>
        /// The sweep instance for this contract, created with its FROZEN anchors on first use. Called from
        /// the pricing path (the first price is computed before the ticket exists) and confirmed by
        /// NoteOptionTicket, so the clock starts at the first quote of the order that ends up submitted.
        /// </summary>
        private SweepState? GetOrCreateSweep(Option option, OrderDirection direction, decimal nearTouch, decimal surfacePrice)
        {
            var key = (option.Symbol, direction);
            if (_sweeps.TryGetValue(key, out SweepState existing))
            {
                return existing;
            }

            decimal startPrice = Favourable(direction, nearTouch, surfacePrice);
            double ivStart = Iv(startPrice, option);
            if (!double.IsFinite(ivStart) || ivStart <= 0)
            {
                LogSweepNullReason(option, direction, $"the start anchor {startPrice:0.0000} does not invert to an IV");
                return null;
            }

            // Round-trip check: the anchor IV has to price back to the anchor PRICE. Where vega is small
            // the inversion is not identifiable - a deep-ITM bid below intrinsic solved to IV ~ 0 and
            // priced ~0.9 away - and an IV-space walk from such a point is meaningless (the 20-minute
            // sweep moved that quote 4 cents). Skip the sweep and let the pricer quote the model instead.
            decimal roundTrip = Npv(ivStart, option);
            decimal tolerance = Math.Max(2 * _algo.TickSize(option.Symbol), startPrice * 0.01m);
            if (roundTrip <= 0 || Math.Abs(roundTrip - startPrice) > tolerance)
            {
                LogSweepNullReason(option, direction, $"IV not identifiable at the anchor (anchor={startPrice:0.0000}, iv={ivStart:0.000} prices {roundTrip:0.0000}, tolerance={tolerance:0.0000}) - not swept, quoting the model");
                return null;
            }

            Equity equity = _algo.ToEquity(Underlying(option.Symbol));

            // Inherit the group clock: a new ticket must not restart the sweep for the whole group, but
            // its instance still dies with its own ticket (ClearSweep on Filled/Canceled/Invalid).
            List<(Symbol Symbol, SweepState State)> live = equity == null ? new() : LiveSweeps(equity, direction);
            DateTime startTime = live.Count > 0 ? live.Min(i => i.State.StartTime) : _algo.Time;

            SweepState state = new()
            {
                StartTime = startTime,
                Duration = ResolveSweepDuration(equity, direction),
                IvStart = ivStart,
                PriceStart = startPrice,
                UtilityStart = _algo.UtilityAt(option, direction, startPrice),
            };
            _sweeps[key] = state;

            _algo.Log($"{_algo.Time} RiskScenarioHandler.GetOrCreateSweep(): {option.Symbol.Value} {direction} startIv={ivStart:0.000} startPrice={startPrice:0.0000} surface={surfacePrice:0.0000} nearTouch={nearTouch:0.0000} utilityStart={state.UtilityStart:0.00} duration={state.Duration}.");
            return state;
        }

        /// <summary>Ends this contract's sweep clock - called when its ticket reaches a terminal state.</summary>
        internal void ClearSweep(Symbol symbol)
        {
            foreach (var key in _sweeps.Keys.Where(k => k.Symbol == symbol).ToList())
            {
                if (_sweeps.Remove(key, out SweepState removed))
                {
                    _algo.Log($"{_algo.Time} RiskScenarioHandler.ClearSweep(): {key.Symbol.Value} {key.Direction} sweep clock removed (started {removed.StartTime:HH:mm:ss}).");
                }
            }
            foreach (var key in _vegaByBar.Keys.Where(k => k.Item1 == symbol).ToList())
            {
                _vegaByBar.TryRemove(key, out _);
            }
        }

        private double ElapsedRatio(SweepState state)
        {
            if (state.Duration <= TimeSpan.Zero) return 0;
            double ratio = (_algo.Time - state.StartTime).TotalSeconds / state.Duration.TotalSeconds;
            return double.IsFinite(ratio) ? Math.Clamp(ratio, 0, 1) : 0;
        }

        private static decimal Favourable(OrderDirection direction, decimal a, decimal b) =>
            direction == OrderDirection.Buy ? Math.Min(a, b) : Math.Max(a, b);

        private static decimal NearTouch(Option option, OrderDirection direction) =>
            direction == OrderDirection.Buy ? option.BidPrice : option.AskPrice;

        private static decimal FarTouch(Option option, OrderDirection direction) =>
            direction == OrderDirection.Buy ? option.AskPrice : option.BidPrice;

        /// <summary>Price implied by the current SSVI mid surface for this contract.</summary>
        private decimal? SurfacePrice(Option option)
        {
            double modelIv = _algo.MidIVSsvi(option.Symbol);
            if (!(modelIv > 0) || !double.IsFinite(modelIv)) return null;
            decimal price = Npv(modelIv, option);
            return price > 0 ? price : (decimal?)null;
        }

        private decimal Npv(double iv, Option option)
        {
            double price = OptionContractWrap.E(_algo, option, _algo.Time.Date).NPV(iv, _algo.MidPrice(Underlying(option.Symbol)));
            if (!double.IsFinite(price) || price <= 0 || price > (double)decimal.MaxValue / 100) return 0;
            return (decimal)price;
        }

        private double Iv(decimal price, Option option) =>
            OptionContractWrap.E(_algo, option, _algo.Time.Date).IV(price, _algo.MidPrice(Underlying(option.Symbol)), 0.001);

        /// <summary>Vega for this contract on this bar - one pricer call per bar, reused by every quote.</summary>
        private double VegaForBar(Symbol symbol)
        {
            var key = (symbol, _algo.Time.Date.AddHours(_algo.Time.Hour).AddMinutes(_algo.Time.Minute));
            if (_vegaByBar.TryGetValue(key, out double cached)) return cached;

            double vega = 0;
            try
            {
                if (_algo.Securities.TryGetValue(symbol, out var security) && security is Option option)
                {
                    vega = OptionContractWrap.E(_algo, option, _algo.Time.Date).Vega(_algo.MidIVSsvi(symbol));
                }
            }
            catch (Exception error)
            {
                _algo.Log($"{_algo.Time} RiskScenarioHandler.VegaForBar(): vega failed for {symbol.Value} ({error.GetType().Name}: {error.Message}) - the sweep ratio falls back to 0.");
            }
            if (!double.IsFinite(vega) || vega < 0) vega = 0;

            if (_vegaByBar.Count > 50_000) _vegaByBar.Clear();
            _vegaByBar[key] = vega;
            return vega;
        }

        private OrderTicket? LiveTicket(Symbol symbol) =>
            _algo.orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets)
                ? tickets.LastOrDefault(t => t != null && !_algo.orderFilledCanceledInvalid.Contains(t.Status))
                : null;

        internal void ClearScenarios(Equity equity)
        {
            if (PfRiskScenarios.Remove(equity))
            {
                BestScenario.Remove(equity);
                BestOrder.Remove(equity);
                TradableOptions.Remove(equity);
                _lastSweepRefresh.Remove(equity);
            }

            // Sweep clocks are NOT scenario state any more: they belong to the order tickets and are
            // removed by ClearSweep when a ticket reaches a terminal state, so an equity hedge fill can
            // no longer wipe an option sweep.
            foreach (var key in ScenarioEntryIVs.Keys.Where(o => o.Item1.Underlying == equity).ToList()) ScenarioEntryIVs.Remove(key);
            foreach (var key in ScenarioUtilities.Keys.Where(o => o.Item1.Underlying == equity).ToList()) ScenarioUtilities.Remove(key);
        }

        /// <summary>
        /// True when a live option ticket on this equity has not caught up with its sweep target, i.e.
        /// the quantity/utility view deserves a RunSignals refresh.
        ///
        /// Two defects fixed here: it only ever looked at BestOrder - a different contract AND often a
        /// different direction from the one actually trading - so a sell swept on a book whose best
        /// scenario was a buy could never trigger it; and it ran on every slice without a rate limit,
        /// while what it triggers costs a full GetDesiredOrders pass. The limit price itself no longer
        /// depends on this gate: UpdateLimitPrice re-prices the ticket from the sweep curve on every
        /// quote.
        /// </summary>
        public bool ShouldUpdateSweepOrders(Equity equity)
        {
            if (equity == null) return false;
            if (_lastSweepRefresh.TryGetValue(equity, out DateTime lastRefresh) && _algo.Time - lastRefresh < SweepRefreshPeriod) return false;

            foreach ((Option option, OrderTicket ticket) in LiveOptionTickets(equity))
            {
                OrderDirection direction = Num2Direction(ticket.Quantity);
                decimal? sweepPrice = SweepPriceNow(option, direction);
                if (sweepPrice == null) continue;

                decimal limitPrice = ticket.Get(OrderField.LimitPrice);
                if (limitPrice <= 0) continue;

                // Positive = the ticket sits more passively than the sweep curve says it should right now.
                // Compared in PRICE space against the CURRENT sweep price, not against the end target:
                // the target carries no time component, so comparing to it flagged every fresh quote as
                // "lagging" (lag=0.099 at ratio 0.004 in the run that prompted this).
                decimal lag = direction == OrderDirection.Buy
                    ? (decimal)sweepPrice - limitPrice
                    : limitPrice - (decimal)sweepPrice;
                decimal tolerance = 2 * _algo.TickSize(option.Symbol);
                if (lag <= tolerance) continue;

                _lastSweepRefresh[equity] = _algo.Time;
                double ratio = _sweeps.TryGetValue((option.Symbol, direction), out SweepState state) ? ElapsedRatio(state) : 0;
                _algo.Log($"{_algo.Time} RiskScenarioHandler.ShouldUpdateSweepOrders(): Yes. Symbol={option.Symbol.Value}, direction={direction}, ticketLimit={limitPrice}, sweepPrice={(decimal)sweepPrice:0.0000}, lag={lag:0.0000}, tolerance={tolerance}, elapsedRatio={ratio:0.000}, bid={option.BidPrice}, ask={option.AskPrice}");
                return true;
            }
            return false;
        }

        /// <summary>
        /// Open (non-terminal) option tickets of the given equity, latest per contract.
        /// </summary>
        private IEnumerable<(Option Option, OrderTicket Ticket)> LiveOptionTickets(Equity equity)
        {
            foreach (var kvp in _algo.orderTickets)
            {
                if (kvp.Key.SecurityType != SecurityType.Option || kvp.Key.Underlying != equity.Symbol) continue;
                if (!_algo.Securities.TryGetValue(kvp.Key, out var security) || security is not Option option) continue;

                OrderTicket ticket = kvp.Value.LastOrDefault(t => t != null && !_algo.orderFilledCanceledInvalid.Contains(t.Status));
                if (ticket == null) continue;

                yield return (option, ticket);
            }
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


        /// <summary>
        /// Utility of this contract at its CURRENT sweep quote - the offered-quote utility the frontier
        /// logic is defined on. Consumed by UtilityOrderBase.GetUtilityRiskScenario, so utility-based
        /// pricing stays in the path; it is not a linear approximation any more.
        /// </summary>
        public double? GetEquiUtility(Option option, OrderDirection direction)
        {
            decimal? sweepPrice = SweepPriceNow(option, direction);
            if (sweepPrice == null || sweepPrice <= 0) return null;
            return _algo.UtilityAt(option, direction, (decimal)sweepPrice);
        }




        /// <summary>
        /// The active schedule's Duration for this equity AND direction, resolved on demand.
        /// SweepDuration is only filled by SetScenarios (once per refetch, direction-blind), so a sweep
        /// that starts before the first scenario response - or after a fill cleared them - would
        /// otherwise have no duration at all and freeze at the touch.
        /// </summary>
        private TimeSpan ResolveSweepDuration(Equity equity, OrderDirection direction)
        {
            if (equity != null && _schedules.TryGetValue(equity, out List<SweepSchedule> schedules))
            {
                HashSet<MarketRegime> regimes = _algo.ActiveRegimes.TryGetValue(equity, out var activeRegimes) ? activeRegimes : null;
                SweepSchedule active = schedules
                    .Where(s => s.Direction == direction)
                    .Where(s => regimes != null && regimes.Contains(s.MarketRegime))
                    .FirstOrDefault(s => _algo.Time.Date + s.Start <= _algo.Time && _algo.Time <= _algo.Time.Date + s.End);
                if (active != null && active.Duration > TimeSpan.Zero)
                {
                    return active.Duration;
                }
            }

            return SweepDuration.TryGetValue(equity, out TimeSpan configured) ? configured : TimeSpan.Zero;
        }

        /// <summary>
        /// One line per contract per minute naming why no sweep IV could be produced. The old code
        /// logged "iv=" with nothing after it, so "not scheduled", "no scenario entry" and "no quote
        /// to invert" were indistinguishable from the log.
        /// </summary>
        private void LogSweepNullReason(Option option, OrderDirection direction, string reason)
        {
            var key = (option.Symbol, direction);
            if (_lastSweepNullReasonLog.TryGetValue(key, out DateTime last) && _algo.Time - last < SweepLogThrottle)
            {
                return;
            }
            if (_lastSweepNullReasonLog.Count > 5000)
            {
                _lastSweepNullReasonLog.Clear();
            }
            _lastSweepNullReasonLog[key] = _algo.Time;
            _algo.Log($"{_algo.Time} RiskScenarioHandler.GetEquiUtilityIV(): sweep source for {option.Symbol.Value} {direction} — {reason}.");
        }




        /// <summary>
        /// Confirms the sweep instance for a freshly stored ticket. The instance is normally created by
        /// the pricing call that precedes the submit (GetOrCreateSweep), which is what freezes the
        /// anchors; this only creates it if no price was computed first, and never restarts a clock.
        /// </summary>
        internal void NoteOptionTicket(OrderTicket ticket)
        {
            if (ticket?.Symbol.SecurityType != SecurityType.Option) return;

            Option option = _algo.Securities.TryGetValue(ticket.Symbol, out var security) ? security as Option : null;
            if (option == null) return;

            OrderDirection direction = Num2Direction(ticket.Quantity);
            if (_sweeps.ContainsKey((ticket.Symbol, direction))) return;

            decimal nearTouch = NearTouch(option, direction);
            decimal? surfacePrice = SurfacePrice(option);
            if (nearTouch <= 0 || surfacePrice == null) return;

            GetOrCreateSweep(option, direction, nearTouch, surfacePrice.Value);
        }
    }
}
