using System;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.RealityModeling;
using QuantConnect.Brokerages;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using System.Linq;
using QuantConnect.Data.Consolidators;
using QuantConnect.Securities.Equity;
using QuantConnect.Data;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class SecurityInitializerMine : BrokerageModelSecurityInitializer
    {
        public int VolatilityPeriodDays { get; set; }

        private readonly Foundations _algo;
        public SecurityInitializerMine(IBrokerageModel brokerageModel, Foundations algo, ISecuritySeeder securitySeeder, int volatilityPeriodDays)
        : base(brokerageModel, securitySeeder)
        {
            _algo = algo;
            VolatilityPeriodDays = volatilityPeriodDays;
        }

        private void HoldingsOnQuantityChanged(object sender, SecurityHoldingQuantityChangedEventArgs e)
        {
            // This methods appears to be called before algo updates Positions dictionary through an orderEvent. In that case, this causes a mismatch as a fill is applied as trade incrementing the quantity rather than updating it.
            // This method is only meant to take care of fills for which algo had no order tickets.
            // Ideally becomes an OrderEvent... but there's no order!!!
            Symbol symbol = e.Security.Symbol;
            if (_algo.Positions.TryGetValue(symbol, out Position position) && position.Quantity != e.Security.Holdings.Quantity)
            {
                _algo.Log($"{_algo.Time} HoldingsOnQuantityChanged(): {symbol} Initialize new Position from Security Holding as Quantity differs. Previously: {position.Quantity}, Now: {e.Security.Holdings.Quantity}. May impact post trade reporting.");
                _algo.Positions[symbol] = new Position(_algo, e.Security.Holdings);
            }
            else if (!_algo.Positions.ContainsKey(symbol))
            {
                _algo.Log($"{_algo.Time} HoldingsOnQuantityChanged(): {symbol} Initialize new Position from Security Holding as Symbol is not present.");
                _algo.Positions[symbol] = new Position(_algo, e.Security.Holdings);
            }

            // A holding is the one contract we can never be unsubscribed from: this also re-subscribes
            // anything that arrived while unsubscribed (assignment, exercise, a fill on a dropped
            // contract) and lets the diff drop contracts the portfolio no longer names.
            if (symbol.SecurityType == SecurityType.Option)
            {
                _algo.SyncOptionSubscriptions(Underlying(symbol), "holdings update");
            }
        }

        public override void Initialize(Security security)
        {
            // First, call the superclass definition
            // This method sets the reality models of each security using the default reality models of the brokerage model
            base.Initialize(security);
            Symbol symbol = security.Symbol;

            if (!_algo.LiveMode && security.Type == SecurityType.Option)
            {
                // Vega-weighted probabilistic fill model: pFill = 1 - exp(-vega * weight * spreadTerm),
                // with vega from the contract's previous-day quote (OptionContractWrap, cached once per
                // contract per day).
                _algo.SeedDailyTradeBars.Remove(security.Symbol);
                security.SetFillModel(FillModelVega.FromConfig(_algo));
                _algo.Log($"SecurityInitializer.Initialize FillModelVega: {symbol}.");
            }
            else if(!_algo.LiveMode)
            {
                security.SetFillModel(new FillModelMine());
            }
            // Margin Model
            security.MarginModel = SecurityMarginModel.Null;
            security.SetBuyingPowerModel(BuyingPowerModel.Null);
            // security.MarginModel = new BuyingPowerModelMine(_algo);
            // security.SetBuyingPowerModel(new BuyingPowerModelMine(_algo));
            security.Holdings.QuantityChanged += HoldingsOnQuantityChanged;

            if (security.Type == SecurityType.Equity)
            {
                Equity equity = (Equity)security;
                if (!_algo.QuoteBarConsolidators.ContainsKey(symbol))
                {
                    _algo.QuoteBarConsolidators[symbol] = new QuoteBarConsolidator(TimeSpan.FromSeconds(1));
                    _algo.TradeBarConsolidators[symbol] = new TradeBarConsolidator(TimeSpan.FromSeconds(1));
                }

                int samplePeriods = _algo.resolution switch
                {
                    Resolution.Daily => 1,
                    Resolution.Hour => 1,
                    Resolution.Minute => 5,
                    Resolution.Second => 300,
                    _ => 1
                };

                int totalRollingPeriods = (int)(VolatilityPeriodDays * 6.5 * 60 * 60 / samplePeriods);
                security.VolatilityModel = new VolatilityModelMine(_algo, security, periods: totalRollingPeriods, _algo.resolution, TimeSpan.FromSeconds(samplePeriods));
                int samples = 0;
                int historyPeriods = _algo.Periods(days: VolatilityPeriodDays + 2);
                // foreach (var tradeBar in _algo.History(symbol, _algo.Time.AddDays(-(VolatilityPeriodDays + 2)), _algo.Time, _algo.resolution))
                foreach (var tradeBar in _algo.HistoryWrap(symbol, historyPeriods, _algo.resolution))
                {
                    security.VolatilityModel.Update(security, tradeBar);
                    samples++;
                }
                _algo.Log($"SecurityInitializer.Initialized VolatilityModel: {symbol} Resolution={_algo.resolution}, Periods={totalRollingPeriods}, historySamples={samples}.");
                
                // A missing history is a DEGRADED volatility model, not a reason to skip the rest of the
                // equity wiring. It used to return here, which left UnderlyingMovedX/SignalsLastRun/
                // RiskProfiles/... unset for any symbol the warehouse does not carry, and the caller
                // (InitializeAlgo) then threw KeyNotFoundException on UnderlyingMovedX[(symbol, 0.005)].
                if (samples == 0)
                {
                    _algo.Log($"SecurityInitializer.Initialize: {symbol} no history found — volatility model " +
                              "stays empty; continuing with the structural initialization.");
                }

                // Initialize a Security Specific Hedge Band or Risk Limit object. Constitutes underlying, hence risk limit not just by security but also its derivatives.
                // Adjust delta by underlying's volatility.
                security.RiskLimit = new SecurityRiskLimit(security, delta100BpLong: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalLong, delta100BpShort: _algo.Cfg.RiskLimitEODDelta100BpUSDTotalShort);

                decimal vola = security.VolatilityModel.Volatility;
                InitializeIVSurfaces(equity);
                _algo.Log($"SecurityInitializer.Initialize: {symbol} WarmedUp Volatility To: PostSurface: {security.VolatilityModel.Volatility}, PreSurface: {vola}");

                _algo.GammaScalpers[symbol] = new(_algo, equity);

                // Refactor - turn percentages over to some config.
                _algo.UnderlyingMovedX[(symbol, 0.001m)] = new(equity, 0.001m);
                _algo.UnderlyingMovedX[(symbol, 0.002m)] = new(equity, 0.002m);
                _algo.UnderlyingMovedX[(symbol, 0.005m)] = new(equity, 0.005m);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.001m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.002m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);
                _algo.RegisterIndicator(symbol, _algo.UnderlyingMovedX[(symbol, 0.005m)], _algo.TradeBarConsolidators[symbol], (IBaseData b) => ((TradeBar)b)?.Close ?? 0);

                //_algo.ConsecutiveTicksTrend[symbol] = new(equity);
                //_algo.RegisterIndicator(symbol, _algo.ConsecutiveTicksTrend[symbol], _algo.QuoteBarConsolidators[symbol], (IBaseData b) => (((QuoteBar)b).Bid.Close + ((QuoteBar)b).Ask.Close) / 2);

                _algo.SignalsLastRun[security.Symbol] = DateTime.MinValue;

                _algo.RiskProfiles[security.Symbol] = new RiskProfile(_algo, equity);
                _algo.OrderEventWriters[security.Symbol] = new OrderEventWriter(_algo, equity);
                _algo.AbsoluteDiscounts[security.Symbol] = new RiskDiscount(_algo, _algo.Cfg, security.Symbol, Metric.Absolute);
                _algo.RequestContractsHandlers[security.Symbol] = new RequestContractsHandler(_algo, equity);
            }

            else if (security.Type == SecurityType.Option)
            {
                // Need to overrride fee model, given discounts by exchanges (NASDQAQM) matters significantly.

                Option option = (Option)security;
                option.PriceModel = new CurrentPriceOptionPriceModel();
                option.SetOptionAssignmentModel(new DefaultOptionAssignmentModel(0, new TimeSpan(-1, 2, 30, 0)));  //CustomOptionAssignmentModel

                // No need for particular option contract's volatility.
                security.VolatilityModel = VolatilityModel.Null;

                // Initialize a Security Specific Hedge Band or Risk Limit object.
                option.RiskLimit = new SecurityRiskLimit(option);
                
                foreach (OrderDirection direction in new[] { OrderDirection.Buy, OrderDirection.Sell })
                {
                    _algo.SpreadBuffers[direction][symbol] = new(_algo, option, direction);
                }
            }

            if (security.Resolution == Resolution.Tick)
            {
                security.SetDataFilter(new OptionTickDataFilter(_algo));
            }
        }

        private void InitializeIVSurfaces(Equity underlying)
        {
            if (!_algo.IvSurfaceSsviExecution.ContainsKey(underlying))
            {
                _algo.IvSurfaceSsviExecution[underlying] = new IVSurfaceSSVI(_algo, underlying, null, false);
            }
            if (!_algo.IvSurfaceSsviRisk.ContainsKey(underlying))
            {
                _algo.IvSurfaceSsviRisk[underlying] = new IVSurfaceSSVI(_algo, underlying, null, false);
            }

            // ONE-SHOT WARM-UP SEED. Here because this is the one place that knows the underlying's
            // surface object exists: the equity security is being initialized and its IIVSurface was
            // just created. The engine's own warm-up window for a custom type is a couple of calendar
            // days and lands on the weekend before a Monday release, so the anchor is fetched
            // explicitly — the PREVIOUS session's surfaces, whose LAST one is the state this session
            // starts.
            // The SEED is a history read, not a subscription: it is served by the same warehouse
            // provider in live as in backtest, so live also starts from the previous session's surface.
            // Without it a live run has no params until the first non-empty calibration response, and a
            // target-portfolio request built in that window carries no surface at all. Only the
            // AddData<SurfaceSSVI> subscription is backtest-only (see SurfaceFromSlice).

            if (!_algo.SurfaceSeedEnabled)
            {
                _algo.Log($"{_algo.Time} InitializeIVSurfaces: {underlying.Symbol} history seed skipped " +
                          $"(surface-mode={_algo.SurfaceMode}, surface-seed=false) — surfaces must come from the MQ producer.");
                return;
            }

            try
            {
                // ANCHOR ON THE LIVE SESSION, not on Cfg.StartDate. In live that config still carries the
                // BACKTEST window (StartDate 2026-08-06), so the seed used to install the 2026-08-05
                // surface into a November session: its tenors are Aug/Sep expiries, the book holds
                // Nov/Dec/Jan, the coverage check rejects it and every hedge is skipped as "no surface".
                DateTime to = _algo.LiveMode ? _algo.Time.Date : _algo.Cfg.StartDate;
                var from = to.AddDays(-1);  // On intraday restart serving old one from R2. Better to query RabbitMQ Stream then or eventually ClickHouse.
                var history = _algo.History<SurfaceSSVI>(underlying.Symbol, from, to).ToList();
                if (history.Count == 0)
                {
                    _algo.Log($"{_algo.Time} InitializeIVSurfaces: {underlying.Symbol} warm-up seed found no " +
                              $"surface in {from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
                    return;
                }

                // The read can carry BOTH products (execution id and, when configured, the risk id), so
                // seed each store from its OWN last row instead of applying one id's state to both.
                foreach (var group in history.GroupBy(h => h.SurfaceId))
                {
                    bool forExecution = _algo.SurfaceIdMatches(group.Key, _algo.ExecutionSurfaceId);
                    bool forRisk = _algo.SurfaceIdMatches(group.Key, _algo.RiskSurfaceId);
                    if (!forExecution && !forRisk)
                    {
                        continue;
                    }

                    SurfaceSSVI seed = group.Last();
                    var modelParams = new SSVIParamsDictionary();
                    foreach (var (tenor, p) in seed.Tenors)
                    {
                        modelParams[tenor] = new SSVIParamsRecord(p.Theta, p.Rho, p.Psi);
                    }
                    // Same entry point as the live rows: the seed is the product unless the filter is on.
                    _algo.ApplySurface(underlying, modelParams, $"history seed {seed.SurfaceId}", seed.IsRaw, forExecution, forRisk);
                    _algo.Log($"{_algo.Time} InitializeIVSurfaces: {underlying.Symbol} warm-up seed from " +
                              $"{group.Count()} surface(s), last {seed.SurfaceId} {seed.Status} " +
                              $"event={seed.TimeSnap:yyyy-MM-dd HH:mm:ss} tenors={seed.Tenors.Count} " +
                              $"as={(forExecution ? "execution" : "")}{(forExecution && forRisk ? "+" : "")}{(forRisk ? "risk" : "")}");
                }
            }
            catch (Exception exception)
            {
                // A missing seed is a degraded start, not a failed initialisation: the first surface
                // delivered in OnData re-anchors the state anyway.
                _algo.Log($"{_algo.Time} InitializeIVSurfaces: {underlying.Symbol} warm-up seed failed: " +
                          exception.Message);
            }
        }
    }
}
