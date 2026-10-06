/*
 * R2IcebergDataFeed — backtest data feed fed by the local rclone mount of the R2 warehouse
 * (route 1: allow_moved_paths; see R2LocalCatalog and the two readers).
 *
 * EVERY (asset class, tick kind, resolution) IS SERVED FROM ITS OWN WAREHOUSE TABLE. The
 * subscription shape is translated once, by R2Warehouse, into one of the twenty tables:
 *
 *     tick subscription   -> {equity,option}_{trade,quote}_tick        (passthrough)
 *     second/minute/hour/daily subscription -> {equity,option}_{trade,quote}_bar_<resolution>
 *
 * A bar subscription reads the warehouse's BARS, not ticks it re-consolidates. The bars are a real
 * product, built from the tick tables by one code path on the loader side (Fino's consolidation
 * step), so the tick tables stay the single source of truth and this feed stays a reader. That is a
 * change from the earlier cut, which consolidated ticks in-process with the engine's own
 * consolidator classes: correct by construction, but it made Lean the aggregator — and a daily
 * subscription then meant pushing a session of ticks through the engine to obtain four numbers.
 * With bar tables present there is nothing left to consolidate here, which is why no consolidator,
 * no pending queue and no Scan() remain in this file.
 *
 * Deliberate decisions kept from the earlier cut, all learned the hard way:
 *
 * - The user-defined universe (Lean creates one for every AddEquity security) IS SERVED, via
 *   TimeTriggeredUniverseSubscriptionEnumeratorFactory exactly as FileSystemDataFeed does. Without
 *   it the universe subscription ends immediately and the added security never gets a data
 *   subscription at all — the engine logs no feed, only a skipped run.
 *
 * - No cache, no staging: reads go through the mount; rclone serves cached files and fetches
 *   missing ones. The earlier "materialize a session before running" contract is gone.
 *
 * A session with no rows is silent: bars are sparse by construction and the warehouse may simply
 * not carry that symbol-day. A symbol the warehouse does not carry ends its subscription with a
 * loud log, as does a subscription shape that maps to no table. Read failures (mount/duckdb) are
 * loud too: Log.Error naming the session.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Common;
using QuantConnect.Data.Market;
using QuantConnect.Data.UniverseSelection;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.DataFeeds;
using QuantConnect.Lean.Engine.DataFeeds.Enumerators.Factories;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Logging;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// Serves Lean subscriptions out of the R2 warehouse through the local rclone mount.
    /// </summary>
    [InheritedExport(typeof(IDataFeed))]
    public class R2IcebergDataFeed : IDataFeed
    {
        private IAlgorithm _algorithm;
        private IFactorFileProvider _factorFileProvider;
        private IDataProvider _dataProvider;
        private ITimeProvider _timeProvider;
        private MarketHoursDatabase _marketHoursDatabase;
        private R2LocalCatalog _catalog;
        private R2TickReader _ticks;
        private R2BarReader _bars;
        private R2IvReader _iv;
        private volatile bool _exited;

        public bool IsActive => !_exited;

        public void Initialize(IAlgorithm algorithm,
            AlgorithmNodePacket job,
            IResultHandler resultHandler,
            IMapFileProvider mapFileProvider,
            IFactorFileProvider factorFileProvider,
            IDataProvider dataProvider,
            IDataFeedSubscriptionManager subscriptionManager,
            IDataFeedTimeProvider dataFeedTimeProvider,
            IDataChannelProvider dataChannelProvider)
        {
            _algorithm = algorithm;
            _factorFileProvider = factorFileProvider;
            _dataProvider = dataProvider;
            _timeProvider = dataFeedTimeProvider.FrontierTimeProvider;
            _marketHoursDatabase = MarketHoursDatabase.FromDataFolder();

            _catalog = new R2LocalCatalog();
            _ticks = new R2TickReader(_catalog);
            _bars = new R2BarReader(_catalog);
            _iv = new R2IvReader(_catalog);

            Log.Trace($"R2IcebergDataFeed: route-1 reads via {_catalog.MountRoot} " +
                      $"(iceberg metadata + daily parquet; rclone serves cached, fetches missing). " +
                      $"Tables: {R2Warehouse.AllTables.Count} " +
                      $"({string.Join(", ", R2Warehouse.AllTables)}) + {R2Warehouse.FeaturesTable} " +
                      "for volatility subscriptions");
        }

        public Subscription CreateSubscription(SubscriptionRequest request)
        {
            if (request.IsUniverseSubscription)
            {
                return CreateUniverseSubscription(request);
            }

            var config = request.Configuration;

            // Open interest: the warehouse holds none, so every OI request is answered with zeros
            // rather than refused. Keyed on TickType, not Type: at tick resolution Lean's
            // GetDataType returns typeof(Tick) for an OI subscription, so the type is not reliable.
            if (R2OpenInterest.IsRequested(config.TickType))
            {
                Log.Trace($"R2IcebergDataFeed: {config.Symbol} {config.Resolution} {config.Type.Name} " +
                          "-> no open interest in the warehouse; serving a zero series");
                var zeroOpenInterest = R2OpenInterest
                    .ZeroSeries(config.Symbol,
                        request.StartTimeUtc.ConvertFromUtc(TimeZones.NewYork),
                        request.EndTimeUtc.ConvertFromUtc(TimeZones.NewYork))
                    .GetEnumerator();
                return SubscriptionUtils.CreateAndScheduleWorker(request, zeroOpenInterest, _factorFileProvider,
                    true, _algorithm.Settings.DailyStrictEndTimeEnabled);
            }

            // Volatility (IV) subscriptions, served from the features table and consolidated to the
            // requested resolution. The feed sees a SecurityType.Base custom data symbol whose
            // Underlying is the option contract (AddData<VolatilityQuoteBar>(contract, resolution));
            // a history request carries the contract itself. Both key the table by the contract.
            if (R2Warehouse.TryResolveIvTable(config.Type, out var iv))
            {
                var ivTicker = R2Warehouse.VendorOptionTicker(R2Warehouse.IvUnderlying(config.Symbol));
                if (ivTicker == null || _catalog.ResolveSymbol(ivTicker, iv.Name) == null)
                {
                    Log.Error($"R2IcebergDataFeed: {config.Symbol} {config.Type.Name}: " +
                              (ivTicker == null
                                  ? "no option contract to key the features table by"
                                  : $"{ivTicker} not carried by {iv.Name}") +
                              " — ending subscription");
                    return SubscriptionUtils.CreateAndScheduleWorker(request, null, _factorFileProvider, false,
                        _algorithm.Settings.DailyStrictEndTimeEnabled);
                }

                Log.Trace($"R2IcebergDataFeed: {config.Symbol} {config.Resolution} {config.Type.Name} " +
                          $"-> {iv} as ticker {ivTicker}");

                var ivResolution = config.Resolution;
                var ivSymbol = config.Symbol;
                var ivEnumerator = GetEnumerator(request, iv.ToString(),
                    day => _iv.Read(ivSymbol, ivTicker, iv, ivResolution, day)).GetEnumerator();
                return SubscriptionUtils.CreateAndScheduleWorker(request, ivEnumerator, _factorFileProvider, true,
                    _algorithm.Settings.DailyStrictEndTimeEnabled);
            }

            // The subscription shape decides the table, in one place, before any I/O.
            if (!R2Warehouse.TryResolveTable(config.SecurityType, config.TickType, config.Resolution,
                    out var table, out var mappingError))
            {
                Log.Error($"R2IcebergDataFeed: {config.Symbol} {config.Resolution} {config.Type.Name} " +
                          $"({config.TickType}) has no warehouse table: {mappingError} — ending subscription");
                return SubscriptionUtils.CreateAndScheduleWorker(request, null, _factorFileProvider, false,
                    _algorithm.Settings.DailyStrictEndTimeEnabled);
            }

            // Resolved once per subscription. Throws on transport failure (fail fast, loudly);
            // null means "the warehouse does not carry this symbol" (e.g. the SPY benchmark).
            var ticker = _ticks.ResolveSymbol(config.Symbol, table);
            if (ticker == null)
            {
                Log.Error($"R2IcebergDataFeed: {config.Symbol} not carried by {table.Name} — " +
                          "ending subscription");
                return SubscriptionUtils.CreateAndScheduleWorker(request, null, _factorFileProvider, false,
                    _algorithm.Settings.DailyStrictEndTimeEnabled);
            }

            Log.Trace($"R2IcebergDataFeed: {config.Symbol} {config.Resolution} {config.Type.Name} " +
                      $"-> {table.Name} as ticker {ticker}");

            var enumerator = GetEnumerator(request, table.Name,
                day => Read(config.Symbol, ticker, table, day)).GetEnumerator();
            return SubscriptionUtils.CreateAndScheduleWorker(request, enumerator, _factorFileProvider, true,
                _algorithm.Settings.DailyStrictEndTimeEnabled);
        }

        private Subscription CreateUniverseSubscription(SubscriptionRequest request)
        {
            IEnumerator<BaseData> enumerator = null;
            if (request.Universe is ITimeTriggeredUniverse timeTriggered)
            {
                var factory = new TimeTriggeredUniverseSubscriptionEnumeratorFactory(
                    timeTriggered, _marketHoursDatabase, _timeProvider);
                enumerator = factory.CreateEnumerator(request, _dataProvider);
            }
            else
            {
                Log.Error($"R2IcebergDataFeed: universe {request.Configuration.Symbol} is not time-triggered — " +
                          "ending subscription");
            }

            if (request.Universe is UserDefinedUniverse)
            {
                // No worker task for the user-defined universe: AddData()/RemoveSecurity() injections
                // happen on the algorithm thread and must not race a worker (see FileSystemDataFeed).
                return SubscriptionUtils.Create(request, enumerator, _algorithm.Settings.DailyStrictEndTimeEnabled);
            }

            return SubscriptionUtils.CreateAndScheduleWorker(request, enumerator, _factorFileProvider, false,
                _algorithm.Settings.DailyStrictEndTimeEnabled);
        }

        public void RemoveSubscription(Subscription subscription)
        {
        }

        public void Exit()
        {
            _exited = true;
        }

        /// <summary>
        /// Session-by-session stream for one subscription, in exchange (ET) time: raw ticks for a
        /// tick table, warehouse bars for a bar table, consolidated IV bars for the features table.
        /// Nothing is bucketed here — <paramref name="readSession"/> yields one session already
        /// shaped, and <paramref name="source"/> names it in the read-failure log.
        /// </summary>
        private IEnumerable<BaseData> GetEnumerator(SubscriptionRequest request, string source,
            Func<DateTime, IEnumerable<BaseData>> readSession)
        {
            var config = request.Configuration;

            // Data outside the request window must not leak (mirrors the engine's own enumerator stacks).
            var startEt = request.StartTimeUtc.ConvertFromUtc(TimeZones.NewYork);
            var endEt = request.EndTimeUtc.ConvertFromUtc(TimeZones.NewYork);

            for (var day = startEt.Date; day <= endEt.Date; day = day.AddDays(1))
            {
                // Weekends carry no sessions; the loader does no discovery for them either.
                if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                {
                    continue;
                }

                var stream = readSession(day);
                using (var enumerator = stream.GetEnumerator())
                {
                    while (true)
                    {
                        BaseData point;
                        try
                        {
                            if (!enumerator.MoveNext())
                            {
                                break;
                            }
                            point = enumerator.Current;
                        }
                        catch (Exception exception)
                        {
                            Log.Error($"R2IcebergDataFeed: read failed for {config.Symbol.Value} {day:yyyy-MM-dd} " +
                                      $"({source}) — session skipped: {exception.Message}");
                            break;
                        }

                        if (point.Time < startEt || point.Time > endEt)
                        {
                            continue;
                        }

                        yield return point;
                    }
                }
            }
        }

        /// <summary>One session of the table's data, as Lean objects in exchange time.</summary>
        private IEnumerable<BaseData> Read(Symbol symbol, string ticker, R2Table table, DateTime day)
        {
            if (!table.IsBar)
            {
                // Tick subscription: passthrough, so the engine's own aggregators (live and backtest)
                // see the same stream they would from any other tick source.
                return table.IsQuote
                    ? (IEnumerable<BaseData>)_ticks.ReadQuotes(symbol, ticker, table, day)
                    : _ticks.ReadTrades(symbol, ticker, table, day);
            }

            return table.IsQuote
                ? (IEnumerable<BaseData>)_bars.ReadQuoteBars(symbol, ticker, table, day)
                : _bars.ReadTradeBars(symbol, ticker, table, day);
        }
    }
}
