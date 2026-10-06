/*
 * R2IcebergHistoryProvider — History() and indicator warmup served from the R2 warehouse through the
 * local rclone mount, over the SAME table registry as the feed (R2Warehouse).
 *
 * This is the piece that serves both modes: in a backtest it feeds warmup and every History() call,
 * and in live it still answers History()/warmup while the stream itself comes from Polygon. That is
 * why it was written as the shared reader rather than as a backtest accessory.
 *
 * Resolution is a routing decision, not an aggregation one:
 *
 *     tick request    -> {equity,option}_{trade,quote}_tick
 *     coarse request  -> {equity,option}_{trade,quote}_bar_<resolution>
 *
 * The warehouse's bars are read as they are stored. Nothing consolidates here, for the same reason
 * as in the feed: the bars are built from the ticks by one code path on the loader side, so this
 * class cannot drift from them by re-deriving them differently. A request for a resolution whose
 * table the warehouse does not carry is refused loudly rather than answered with an approximation.
 *
 * One Slice per distinct timestamp, which is the shape the engine's own readers produce: the
 * algorithm's History() enumerates slices in time order and each slice carries the points stamped
 * at that instant. The window is enforced on the data's own exchange-time stamp, and — like the
 * feed — no exchange-hours filtering is applied: a bar's bucket semantics were decided when it was
 * built, and re-deciding them here would contradict the stored data.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;

using NodaTime;   // DateTimeZone in the GetHistory signature

using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.HistoricalData;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    [InheritedExport(typeof(IHistoryProvider))]
    public class R2IcebergHistoryProvider : HistoryProviderBase
    {
        private R2LocalCatalog _catalog;
        private R2TickReader _ticks;
        private R2BarReader _bars;
        private R2IvReader _iv;
        private int _dataPointCount;

        public override int DataPointCount => _dataPointCount;

        public override void Initialize(HistoryProviderInitializeParameters parameters)
        {
            _catalog = new R2LocalCatalog();
            _ticks = new R2TickReader(_catalog);
            _bars = new R2BarReader(_catalog);
            _iv = new R2IvReader(_catalog);

            Log.Trace($"R2IcebergHistoryProvider: route-1 reads via {_catalog.MountRoot} " +
                      $"({R2Warehouse.AllTables.Count} tables: tick tables for tick requests, " +
                      "bar tables for second/minute/hour/daily requests)");
        }

        public override IEnumerable<Slice> GetHistory(IEnumerable<HistoryRequest> requests, DateTimeZone sliceTimeZone)
        {
            foreach (var request in requests)
            {
                foreach (var slice in GetSlices(request))
                {
                    yield return slice;
                }
            }
        }

        /// <summary>
        /// One request's worth of history: every point in the window, grouped by timestamp into
        /// slices. An empty result is a legitimate answer (bars are sparse and the warehouse may not
        /// carry the symbol); a request that cannot be served is logged as an error and yields
        /// nothing, so a misconfiguration never presents as "no data exists".
        /// </summary>
        private IEnumerable<Slice> GetSlices(HistoryRequest request)
        {
            // Open interest first — before the custom-data guard as well, because
            // AddData<OpenInterest> creates a custom-data subscription and would otherwise be
            // refused as custom data rather than answered. See R2OpenInterest.
            if (R2OpenInterest.IsRequested(request.TickType))
            {
                var oiStartEt = request.StartTimeUtc.ConvertFromUtc(TimeZones.NewYork);
                var oiEndEt = request.EndTimeUtc.ConvertFromUtc(TimeZones.NewYork);
                var zeroPoints = new List<BaseData>();
                foreach (var point in R2OpenInterest.ZeroSeries(request.Symbol, oiStartEt, oiEndEt))
                {
                    zeroPoints.Add(point);
                }

                Log.Trace($"R2IcebergHistoryProvider: {request.Symbol} {request.Resolution} " +
                          $"{request.StartTimeUtc:yyyy-MM-dd}..{request.EndTimeUtc:yyyy-MM-dd} -> " +
                          $"open interest is not in the warehouse: {zeroPoints.Count} zero-valued points");

                foreach (var point in zeroPoints)
                {
                    _dataPointCount++;
                    yield return new Slice(point.Time, new List<BaseData> { point },
                        point.Time.ConvertToUtc(TimeZones.NewYork));
                }
                yield break;
            }

            // Volatility (IV) history, before the custom-data guard for the same reason OI is:
            // History<VolatilityQuoteBar> arrives with IsCustomData set. Served from the same
            // features table the feed uses, consolidated to the requested resolution.
            if (R2Warehouse.TryResolveIvTable(request.DataType, out var iv))
            {
                var ivTicker = R2Warehouse.VendorOptionTicker(R2Warehouse.IvUnderlying(request.Symbol));
                if (ivTicker == null || _catalog.ResolveSymbol(ivTicker, iv.Name) == null)
                {
                    Log.Error($"R2IcebergHistoryProvider: {request.Symbol} {request.DataType?.Name}: " +
                              (ivTicker == null
                                  ? "no option contract to key the features table by"
                                  : $"{ivTicker} not carried by {iv.Name}") +
                              " — returning nothing");
                    yield break;
                }

                var ivStartEt = request.StartTimeUtc.ConvertFromUtc(TimeZones.NewYork);
                var ivEndEt = request.EndTimeUtc.ConvertFromUtc(TimeZones.NewYork);

                var ivRead = 0;
                var ivInWindow = 0;

                for (var day = ivStartEt.Date; day <= ivEndEt.Date; day = day.AddDays(1))
                {
                    if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                    {
                        continue;
                    }

                    var ivStream = _iv.Read(request.Symbol, ivTicker, iv, request.Resolution, day);
                    using (var ivEnumerator = ivStream.GetEnumerator())
                    {
                        while (true)
                        {
                            BaseData point;
                            try
                            {
                                if (!ivEnumerator.MoveNext())
                                {
                                    break;
                                }
                                point = ivEnumerator.Current;
                            }
                            catch (Exception exception)
                            {
                                Log.Error($"R2IcebergHistoryProvider: read failed for {request.Symbol.Value} " +
                                          $"{day:yyyy-MM-dd} ({iv}) — session skipped: {exception.Message}");
                                break;
                            }

                            ivRead++;
                            if (point.Time < ivStartEt || point.Time > ivEndEt)
                            {
                                continue;
                            }

                            ivInWindow++;
                            _dataPointCount++;
                            yield return new Slice(point.Time, new List<BaseData> { point },
                                point.Time.ConvertToUtc(TimeZones.NewYork));
                        }
                    }
                }

                Log.Trace($"R2IcebergHistoryProvider: {request.Symbol} {request.Resolution} " +
                          $"{request.DataType?.Name} {request.StartTimeUtc:yyyy-MM-dd}.." +
                          $"{request.EndTimeUtc:yyyy-MM-dd} <- {iv}: {ivRead} points, {ivInWindow} in " +
                          $"window {ivStartEt:yyyy-MM-dd HH:mm:ss}..{ivEndEt:yyyy-MM-dd HH:mm:ss} ET");
                yield break;
            }

            if (request.IsCustomData)
            {
                Log.Error($"R2IcebergHistoryProvider: custom data is not served by the warehouse " +
                          $"({request.Symbol.Value} {request.DataType?.Name}) — returning nothing");
                yield break;
            }

            if (!R2Warehouse.TryResolveTable(request.Symbol.SecurityType, request.TickType, request.Resolution,
                    out var table, out var mappingError))
            {
                Log.Error($"R2IcebergHistoryProvider: {request.Symbol} {request.Resolution} " +
                          $"({request.TickType}) has no warehouse table: {mappingError} — returning nothing");
                yield break;
            }

            var ticker = _ticks.ResolveSymbol(request.Symbol, table);
            if (ticker == null)
            {
                Log.Error($"R2IcebergHistoryProvider: {request.Symbol} not carried by {table.Name} — " +
                          "returning nothing");
                yield break;
            }

            var startEt = request.StartTimeUtc.ConvertFromUtc(TimeZones.NewYork);
            var endEt = request.EndTimeUtc.ConvertFromUtc(TimeZones.NewYork);

            var byTime = new SortedDictionary<DateTime, List<BaseData>>();
            var read = 0;

            for (var day = startEt.Date; day <= endEt.Date; day = day.AddDays(1))
            {
                if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday)
                {
                    continue;
                }

                IEnumerable<BaseData> stream;
                try
                {
                    stream = Read(request.Symbol, ticker, table, day);
                }
                catch (Exception exception)
                {
                    Log.Error($"R2IcebergHistoryProvider: read failed for {request.Symbol.Value} " +
                              $"{day:yyyy-MM-dd} ({table.Name}) — session skipped: {exception.Message}");
                    continue;
                }

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
                            Log.Error($"R2IcebergHistoryProvider: read failed for {request.Symbol.Value} " +
                                      $"{day:yyyy-MM-dd} ({table.Name}) — session skipped: {exception.Message}");
                            break;
                        }

                        if (point.Time < startEt || point.Time > endEt)
                        {
                            continue;
                        }

                        if (!byTime.TryGetValue(point.Time, out var bucket))
                        {
                            bucket = new List<BaseData>();
                            byTime[point.Time] = bucket;
                        }
                        bucket.Add(point);
                        read++;
                    }
                }
            }

            Log.Trace($"R2IcebergHistoryProvider: {request.Symbol} {request.Resolution} {request.TickType} " +
                      $"{request.StartTimeUtc:yyyy-MM-dd}..{request.EndTimeUtc:yyyy-MM-dd} <- {table.Name}: " +
                      $"{read} points in {byTime.Count} slices");

            foreach (var entry in byTime)
            {
                _dataPointCount += entry.Value.Count;
                yield return new Slice(entry.Key, entry.Value, entry.Key.ConvertToUtc(TimeZones.NewYork));
            }
        }

        /// <summary>One session of the table's data, as Lean objects in exchange time.</summary>
        private IEnumerable<BaseData> Read(Symbol symbol, string ticker, R2Table table, DateTime day)
        {
            if (!table.IsBar)
            {
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
