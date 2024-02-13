using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using Accord.Math;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

using AndreasenHugeVolatilityAdapter = QuantLib.AndreasenHugeVolatilityAdapter;
using AndreasenHugeVolatilityInterpl = QuantLib.AndreasenHugeVolatilityInterpl;
using CalibrationSet = QuantLib.CalibrationSet;
using PlainVanillaPayoff = QuantLib.PlainVanillaPayoff;
using EuropeanExercise = QuantLib.EuropeanExercise;
using VanillaOption = QuantLib.VanillaOption;
using SimpleQuote = QuantLib.SimpleQuote;
using CalibrationPair = QuantLib.CalibrationPair;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVSurfaceAndreasenHuge : IDisposable
    {
        private readonly Foundations _algo;
        public Symbol Underlying { get; }
        private AndreasenHugeVolatilityInterpl _andreasenHugeVolatilityInterpl;
        private AndreasenHugeVolatilityAdapter _andreasenHugeVolatilityAdapter;

        // Surface
        public DateTime Time;
        private readonly OptionRight _optionRight;
        private readonly QuantLib.Option.Type _optionRightQl;

        // CSV writer
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;

        // Logs
        private const string _dateTimeFmt = "yyyy-MM-dd HH:mm:ss";
        private const string _dateFmt = "yyyy-MM-dd";

        // EventHandlers
        public event EventHandler<bool> RecalibratedEventHandler;

        // Other
        private HashSet<Option> _subscribedSymbols = new();
        private readonly double _dividendYield;
        private readonly double _riskFreeRate;
        private readonly QuantLib.DayCounter _dayCounter = new QuantLib.Actual365Fixed();
        private decimal MidPriceUnderlying { get { return _algo.MidPrice(Underlying); } }
        public Func<Symbol, double?> IV;

        public IVSurfaceAndreasenHuge(Foundations algo, Symbol underlying, OptionRight optionRight, bool createFile = false)
        {
            _algo = algo;
            Underlying = underlying.SecurityType == SecurityType.Option ? underlying.Underlying : underlying;
            _optionRight = optionRight;
            _optionRightQl = optionRight == OptionRight.Call ? QuantLib.Option.Type.Call : QuantLib.Option.Type.Put;
            _riskFreeRate = (double)_algo.Cfg.DiscountRateMarket;
            _dividendYield = _algo.Cfg.DividendYield.TryGetValue(Underlying.Value, out _dividendYield) ? _dividendYield : _algo.Cfg.DividendYield[CfgDefault];

            //IV = algo.Cache(GetIV, (Symbol symbol) => (_algo.Time, symbol), ttl: 10); // Cache until recalibrated
            IV = GetIV;

            if (createFile)
            {
                _path = Path.Combine(Globals.PathAnalytics, "IVSurface", Underlying.Value, $"AndreaseHuge_{_optionRight}.csv");
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                }                
                _writer = new StreamWriter(_path, true);
            }
        }

        public void RegisterSymbol(Option option)
        {
            _algo.Log($"{_algo.Time} Registered {option} at IVSurfaceAndreasenHuge {Underlying.Value} {_optionRight}");
            _subscribedSymbols.Add(option);
        }

        public void UnRegisterSymbol(Option option)
        {
            _algo.Log($"{_algo.Time} UnRegistered {option} at IVSurfaceAndreasenHuge {Underlying.Value} {_optionRight}");
            _subscribedSymbols.Remove(option);
        }

        /// <summary>
        /// Assumes all IVBidAsk update events have been processed. Only need to interpolate in between strikes that actually have changed values. Their timestamps would
        /// need to greater than the Bin's 'raw' IV timestamp.
        /// </summary>
        public void Recalibrate()
        {
            if (
                _algo.Time.TimeOfDay > new TimeSpan(16, 0, 0) || _algo.Time.TimeOfDay < new TimeSpan(9, 30, 0)   // Only RTH
               ) 
            {
                return; 
            }
            Time = _algo.Time;
            using CalibrationSet calibrationSet = new();
            using QuantLib.QuoteHandle spotQuoteHandle = new(new SimpleQuote((double)MidPriceUnderlying));

            using QuantLib.YieldTermStructureHandle riskFreeTS = new(new QuantLib.FlatForward(DateQl(Time.Date), _riskFreeRate, _dayCounter));
            using QuantLib.YieldTermStructureHandle dividendTS = new(new QuantLib.FlatForward(DateQl(Time.Date), _dividendYield, _dayCounter));

            foreach (Option option in _subscribedSymbols)
            {
                // Cache these objects...
                using PlainVanillaPayoff payoff = new(_optionRightQl, (double)option.StrikePrice);
                using EuropeanExercise exercise = new(DateQl(option.Expiry));
                using SimpleQuote iv = new((double)_algo.MidIV(option.Symbol));
                calibrationSet.Add(new CalibrationPair(new VanillaOption(payoff, exercise), iv));
            }            
            _andreasenHugeVolatilityInterpl = new(calibrationSet, spotQuoteHandle, riskFreeTS, dividendTS);
            _andreasenHugeVolatilityAdapter = new(_andreasenHugeVolatilityInterpl);
            _andreasenHugeVolatilityAdapter.enableExtrapolation();
            //_algo.Log($"{_algo.Time} Recalibrated IVSurfaceAndreasenHuge for {Underlying.Value} {_optionRight} at Spot: {MidPriceUnderlying}; Calibration Error Min/Max/Avg: {_andreasenHugeVolatilityInterpl.calibrationError().first()} / {_andreasenHugeVolatilityInterpl.calibrationError().second()} / {_andreasenHugeVolatilityInterpl.calibrationError().third()}");
            RecalibratedEventHandler?.Invoke(this, true);
        }

        /// <summary>
        /// Cache it. Surface iV.
        /// </summary>
        /// <param name="symbol"></param>
        private double? GetIV(Symbol symbol) => GetIV(symbol.ID.Date, (double)symbol.ID.StrikePrice);
        private double? GetIV(DateTime expiry, double strike)
        {
            if (_andreasenHugeVolatilityAdapter == null)
            {
                return null;
            }
            try
            {
                return _andreasenHugeVolatilityAdapter.blackVol(DateQl(expiry), strike);
            }
            catch (Exception e)
            {
                //_algo.Error($"{_algo.Time} {e.Message}");
                return null;
            }
        }
        /// <summary>
        /// IVdS = -IVdK
        /// </summary>
        public double? IVdS(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option) { return 0; }
            double step = 0.001;
            return -(GetIV(symbol.ID.Date, (double)symbol.ID.StrikePrice + step) - GetIV(symbol.ID.Date, (double)symbol.ID.StrikePrice - step)) / 2*step ?? 0;
        }

        public bool IsReady(Symbol symbol)
        {
            return _andreasenHugeVolatilityAdapter != null;
        }

        public Dictionary<DateTime, Dictionary<decimal, double?>> ToDictionary()
        {
            Dictionary<DateTime, Dictionary<decimal, double?>> dict = new();
            HashSet<DateTime> expiries = new();
            HashSet<decimal> strikes = new();
            foreach (Option option in _subscribedSymbols)
            {
                DateTime expiry = option.Expiry.Date;
                if (!expiries.Contains(expiry))
                {
                    expiries.Add(expiry);
                    dict[expiry] = new Dictionary<decimal, double?>();
                }
                dict[expiry][option.StrikePrice] = IV(option.Symbol);
                strikes.Add(option.StrikePrice);
            }

            // Fill in missing strikes
            foreach (DateTime expiry in expiries)
            {
                foreach (decimal strike in strikes)
                {
                    if (!dict[expiry].ContainsKey(strike))
                    {
                        dict[expiry][strike] = GetIV(expiry, (double)strike);
                    }
                }
            }
            return dict;
        }

        public string GetCsvHeader()
        {
            List<string> header;
            var csv = new StringBuilder();
            var dict = ToDictionary();

            if (dict.Keys.Any())
            {
                List<decimal> sortedStrikes = dict[dict.Keys.First()].Keys.Sorted().ToList();
                header = sortedStrikes.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
            }
            else
            {
                header = new List<string>();
            }
            
            csv.AppendLine("Time,Expiry,Spot," + string.Join(",", header));
            return csv.ToString();
        }
        public void WriteCsvRows()
        {
            var csv = new StringBuilder();
            var dict = ToDictionary();
            if (!dict.Keys.Any()) return;

            if (!_headerWritten)
            {
                _writer.Write(GetCsvHeader());
                _headerWritten = true;
            }

            List<decimal> sortedStrikes = dict[dict.Keys.First()].Keys.Sorted().ToList();

            foreach (var expiry in dict.Keys)
            {
                string ts = _algo.Time.ToString(_dateTimeFmt, CultureInfo.InvariantCulture);
                string row = $"{ts},{expiry.ToString(_dateFmt, CultureInfo.InvariantCulture)},{_algo.MidPrice(Underlying)}," + string.Join(",", sortedStrikes.Select(d => dict[expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                csv.AppendLine(row);
            }
            _writer.Write(csv.ToString());
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
}
