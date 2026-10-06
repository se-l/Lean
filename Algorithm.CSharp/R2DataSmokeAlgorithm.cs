/*
 * R2DataSmokeAlgorithm — throwaway data-validation algorithm for the R2 feed.
 *
 * What it is FOR: proving the R2 path serves data and that the engine's own consolidation of our
 * ticks agrees with an independent DuckDB GROUP BY over the same catalog data. It is deliberately
 * dumb: one symbol, one session, no indicators, no entries, no warmup (no History() call, so no
 * dependence on the history provider yet).
 *
 * Second resolution on purpose: the feed emits ticks, so a Second subscription exercises the
 * engine's AggregationManager — this run validates modes 1 AND 2 of the plan at once.
 *
 * Read the result from the backtest log; compare against R2 with:
 *
 *   SELECT count(*), sum(size), min(price), max(price)
 *     FROM option_quote_tick  -- or the matching equity table
 *    WHERE symbol = '...' AND sip_timestamp::DATE = DATE '2026-09-25';
 *
 * Total volume and min/max price are EXACTLY comparable (a bar's low is the minimum tick price and
 * its volume the sum of tick sizes). Bar count is informational: it depends on session boundaries.
 */

using System;
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp
{
    public class R2DataSmokeAlgorithm : QCAlgorithm
    {
        private const string Ticker = "PEP";
        private Symbol _symbol;

        private long _bars;
        private decimal _volume;
        private decimal _minPrice = decimal.MaxValue;
        private decimal _maxPrice = decimal.MinValue;
        private DateTime _firstBarTime = DateTime.MaxValue;
        private DateTime _lastBarTime = DateTime.MinValue;
        private readonly int[] _barsByHour = new int[24];
        private int _firstLogged;
        private int _minLogged;
        private int _maxLogged;

        public override void Initialize()
        {
            SetStartDate(2026, 9, 16);
            SetEndDate(2026, 9, 18);
            SetCash(100000);

            _symbol = AddEquity(Ticker, Resolution.Second, extendedMarketHours: true).Symbol;

            Debug($"R2DataSmokeAlgorithm: {Ticker} at Second resolution, {StartDate:yyyy-MM-dd} .. {EndDate:yyyy-MM-dd}");
        }

        public override void OnData(Slice slice)
        {
            foreach (var bar in slice.Bars.Values)
            {
                _bars++;
                _volume += bar.Volume;
                _minPrice = Math.Min(_minPrice, bar.Low);
                _maxPrice = Math.Max(_maxPrice, bar.High);
                _barsByHour[bar.Time.Hour]++;

                if (_firstLogged < 3)
                {
                    Log($"R2SMOKE sample[{_firstLogged}] barTime={bar.Time:yyyy-MM-dd HH:mm:ss.fff} " +
                        $"endTime={bar.EndTime:yyyy-MM-dd HH:mm:ss.fff} o={bar.Open} h={bar.High} l={bar.Low} c={bar.Close} v={bar.Volume}");
                    _firstLogged++;
                }
                if (bar.Time < _firstBarTime && _minLogged < 5)
                {
                    _minLogged++;
                    Log($"R2SMOKE newMin time={bar.Time:yyyy-MM-dd HH:mm:ss.fff} barsSoFar={_bars}");
                }
                if (bar.Time > _lastBarTime && _maxLogged < 5)
                {
                    _maxLogged++;
                    Log($"R2SMOKE newMax time={bar.Time:yyyy-MM-dd HH:mm:ss.fff} barsSoFar={_bars}");
                }

                _firstBarTime = bar.Time < _firstBarTime ? bar.Time : _firstBarTime;
                _lastBarTime = bar.Time > _lastBarTime ? bar.Time : _lastBarTime;
            }
        }

        public override void OnEndOfAlgorithm()
        {
            // These lines are the deliverable: the numbers to reconcile against the DuckDB GROUP BY.
            Log($"R2SMOKE symbol={Ticker} bars={_bars} volume={_volume} " +
                $"minPrice={(_bars == 0 ? "n/a" : _minPrice.ToString())} " +
                $"maxPrice={(_bars == 0 ? "n/a" : _maxPrice.ToString())}");

            Log(_bars == 0
                ? "R2SMOKE firstBar=n/a lastBar=n/a — NO DATA REACHED THE ALGORITHM"
                : $"R2SMOKE firstBar={_firstBarTime:yyyy-MM-dd HH:mm:ss} lastBar={_lastBarTime:yyyy-MM-dd HH:mm:ss} (exchange time)");

            var histogram = string.Join(" ", Enumerable.Range(0, 24)
                .Where(h => _barsByHour[h] > 0)
                .Select(h => $"{h:D2}:{_barsByHour[h]}"));
            Log($"R2SMOKE hourHistogram(ET) {histogram}");
        }
    }
}
