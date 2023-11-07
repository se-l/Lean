using QuantConnect.Indicators;
using QuantConnect.Orders;
using System;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IntradayIVDirectionIndicator : IndicatorBase<IndicatorDataPoint>, IIndicatorWarmUpPeriodProvider
    {
        private readonly Foundations _algo;
        public Symbol Underlying { get; internal set; }
        private double _T1EODATMIVBid;
        private double _T1EODATMIVAsk;
        private double _T1EODATMIV;
        public double T0SODATMIV { get; internal set; }
        public double T0CurrentATMIV { get; internal set; }
        public double IntraDayIVSlope { get; internal set; }
        public override bool IsReady => _T1EODATMIV > 0 && T0CurrentATMIV > 0;
        public int WarmUpPeriod => 1;
        private readonly double _sodSeconds = TimeSpan.FromMinutes(9 * 60 + 30).TotalSeconds;
        private readonly double _tradingPeriod = TimeSpan.FromMinutes(6 * 60 + 30).TotalSeconds;
        private bool IsPM => FractionOfDay(_algo.Time) > 0.8;
        private readonly double _EOD2SODATMIVJumpThreshold;
        private double[] _intradayIVSlopeTrendingRange;

        public IntradayIVDirectionIndicator(Foundations algo, Symbol underlying) : base($"IntradayIVDirectionIndicator {underlying}")
        {
            _algo = algo;
            Underlying = underlying;
            if (!_algo.Cfg.EOD2SODATMIVJumpThreshold.TryGetValue(underlying, out _EOD2SODATMIVJumpThreshold))
            {
                _EOD2SODATMIVJumpThreshold = _algo.Cfg.EOD2SODATMIVJumpThreshold[CfgDefault];
            }
            if (!_algo.Cfg.IntradayIVSlopeTrendingRange.TryGetValue(underlying, out _intradayIVSlopeTrendingRange))
            {
                _intradayIVSlopeTrendingRange = _algo.Cfg.IntradayIVSlopeTrendingRange[CfgDefault];
            }

            _algo.IVSurfaceRelativeStrikeBid[underlying].EODATMEventHandler += (s, e) => { _T1EODATMIVBid = e.IV; SetT1EODATMIV(); };
            _algo.IVSurfaceRelativeStrikeAsk[underlying].EODATMEventHandler += (s, e) => { _T1EODATMIVAsk = e.IV; SetT1EODATMIV(); };
        }
        public OrderDirection[] Direction() {
            bool SodGtEod = (T0SODATMIV - _T1EODATMIV) > _EOD2SODATMIVJumpThreshold;
            OrderDirection IsTrending = IntraDayIVSlope switch
            {
                var slope when slope > _intradayIVSlopeTrendingRange[0] => OrderDirection.Buy,
                var slope when slope < _intradayIVSlopeTrendingRange[1] => OrderDirection.Sell,
                _ => OrderDirection.Hold
            };

            return (SodGtEod, IsTrending, IsPM) switch
            {
                (true, OrderDirection.Sell, false) => new[] { OrderDirection.Sell }, // ideal at AM. was trending throughout day. Assume same happens next day, an EOD -> SOD IV jump.
                (true, OrderDirection.Sell, true) => new[] { OrderDirection.Buy },  // Had a jump, but may not have been trending anywhere. Assume another jump.
                (true, OrderDirection.Hold, true) => new[] { OrderDirection.Buy },
                _ => new[] { OrderDirection.Sell, OrderDirection.Buy },
            };
        }
        public double FractionOfDay(DateTime time)
        {
            return (time.TimeOfDay.TotalSeconds - _sodSeconds) / _tradingPeriod;
        }
        protected override decimal ComputeNextValue(IndicatorDataPoint input)
        {
            _algo.Log($"{_algo.Time} IntradayIVDirectionIndicator.ComputeNextValue: {input.Time} {input.Value}");
            T0CurrentATMIV = (double)input.Value;
            IntraDayIVSlope = (T0CurrentATMIV - T0SODATMIV) / FractionOfDay(input.Time);
            _algo.Log($"{_algo.Time} IntradayIVDirectionIndicator.ComputeNextValue: IntraDayIVSlope={IntraDayIVSlope}, T0CurrentATMIV={T0CurrentATMIV}, FractionOfDay={FractionOfDay(input.Time)}");
            return 0;
        }
        public void SetT1EODATMIV()
        {
            if (_T1EODATMIVBid != 0 && _T1EODATMIVAsk != 0)
            {
                _T1EODATMIV = (_T1EODATMIVBid + _T1EODATMIVAsk) / 2;
            }
            else if (_T1EODATMIVBid != 0 && _T1EODATMIVAsk == 0)
            {
                _T1EODATMIV = _T1EODATMIVBid;
            }
            else if (_T1EODATMIVBid == 0 && _T1EODATMIVAsk != 0)
            {
                _T1EODATMIV = _T1EODATMIVAsk;
            }
            else
            {
                _T1EODATMIV = 0;
            }
        }

        public override void Reset() {
            base.Reset();
            //_T1EODATMIVBid = 0;
            //_T1EODATMIVAsk = 0;
            _T1EODATMIV = 0;
            T0SODATMIV = 0;
            T0CurrentATMIV = 0;
            IntraDayIVSlope = 0;
        }
    }
}
