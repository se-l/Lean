using System;
using System.Linq;
using System.Text;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using Accord.Math;
using QuantConnect.Securities.Option;
using MathNet.Numerics.Statistics;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVSurfaceRelativeStrike : IDisposable
    {
        public DateTime Time;
        public Symbol Underlying { get; }
        public bool IsReady => true;
        private readonly QuoteSide _side;
        public QuoteSide Side { get => _side; }

        private readonly Foundations _algo;
        private readonly Dictionary<(bool, DateTime, decimal), Bin> _bins = new();
        private readonly HashSet<DateTime> _expiries = new();
        private readonly Dictionary<DateTime, HashSet<decimal>> _strikes = new();
        private readonly Dictionary<(DateTime, decimal), int> _samples = new();

        private readonly Dictionary<(bool, DateTime), decimal> _minBin = new();
        private readonly Dictionary<(bool, DateTime), decimal> _maxBin = new();
        private readonly double Alpha;


        private readonly string _path;
        private readonly string _pathRaw;
        private readonly StreamWriter _writer;
        private readonly StreamWriter _writerRaw;
        private bool _headerWritten;

        private Func<Symbol, IVQuoteIndicator> _ivQuote;
        private decimal MidPriceUnderlying { get { return _algo.MidPrice(Underlying); } }

        public IVSurfaceRelativeStrike(Foundations algo, Symbol underlying, QuoteSide side)
        {
            _algo = algo;
            Alpha = _algo.Cfg.IVSurfaceRelativeStrikeAlpha;
            Underlying = underlying.SecurityType == SecurityType.Option ? underlying.Underlying : underlying;
            _side = side;
            _ivQuote = side == QuoteSide.Ask ? (Symbol s) => _algo.IVAsks[s] : (Symbol s) => _algo.IVBids[s];

            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "IVSurface", Underlying.Value, $"{Side}.csv");
            _pathRaw = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "IVSurface", Underlying.Value, $"{Side}Raw.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            if (File.Exists(_pathRaw))
            {
                File.Delete(_pathRaw);
            }
            _writer = new StreamWriter(_path, true);
            _writerRaw = new StreamWriter(_pathRaw, true);
        }

        public void RegisterSymbol(Option option)
        {
            Symbol symbol = option.Symbol;
            if (symbol.SecurityType != SecurityType.Option) return;

            // Add Expiry
            _expiries.Add(symbol.ID.Date);

            // Add Strike
            if (!_strikes.ContainsKey(symbol.ID.Date))
            {
                _strikes[symbol.ID.Date] = new HashSet<decimal>();
            }
            _strikes[symbol.ID.Date].Add(symbol.ID.StrikePrice);            

            // WarmUp Samples
            if (!_samples.ContainsKey((symbol.ID.Date, symbol.ID.StrikePrice)))
            {
                _samples[(symbol.ID.Date, symbol.ID.StrikePrice)] = 0;
            }

            // Initialize some bins to get min max boundaries
            InitalizeNewBin(true, symbol.ID.Date, (int)Math.Floor(symbol.ID.StrikePrice));
            InitalizeNewBin(false, symbol.ID.Date, (int)Math.Floor(symbol.ID.StrikePrice));
        }
        public int Samples(Symbol symbol)
        {
            return _samples[(symbol.ID.Date, symbol.ID.StrikePrice)];
        }
        /// <summary>
        /// Assumes all IVBidAsk update events have been processed. Only need to interpolate in between strikes that actually have changed values. Their timestamps would
        /// need to greater than the Bin's 'raw' IV timestamp.
        /// </summary>
        public IVSurfaceRelativeStrike Update()
        {
            if (_algo.Time.TimeOfDay > new TimeSpan(16, 0, 0) || _algo.Time.TimeOfDay < new TimeSpan(9, 30, 0)) { return this; }  // Only RTH
            Time = _algo.Time;

            Symbol symbolLeft = null;
            IVQuoteIndicator ivQuoteIndicatorLeft;
            IVQuoteIndicator ivQuoteIndicatorRight;
            decimal strikePctLeft;
            decimal strikePctRight;
            DateTime maxTime;


            // Functions instead of for loops and ideally concurrently.
            foreach (bool otm in new[] { true, false })
            {
                foreach (var expiry in _expiries)
                {
                    foreach (Symbol symbolRight in OptionSymbols(expiry, otm))
                    {
                        ivQuoteIndicatorRight = _ivQuote(symbolRight);
                        if (ivQuoteIndicatorRight.IV == 0)
                        {
                            continue;
                        }
                        strikePctRight = StrikePct(symbolRight.ID.StrikePrice, MidPriceUnderlying);                        

                        if (symbolLeft != null)
                        {
                            ivQuoteIndicatorLeft = _ivQuote(symbolLeft);
                            strikePctLeft = StrikePct(symbolLeft.ID.StrikePrice, MidPriceUnderlying);
                            maxTime = ivQuoteIndicatorLeft.Time > ivQuoteIndicatorRight.Time ? ivQuoteIndicatorLeft.Time : ivQuoteIndicatorRight.Time;

                            var slope = (ivQuoteIndicatorRight.IV - ivQuoteIndicatorLeft.IV) / (double)(strikePctRight - strikePctLeft);
                            foreach (Bin bin in GetBins(otm, expiry, strikePctLeft, strikePctRight))
                            {
                                double ivInterpolated = ivQuoteIndicatorLeft.IV + slope * (double)(bin.Value - strikePctLeft);
                                bin.Update(maxTime, ivInterpolated, slope);
                            }
                        }

                        symbolLeft = symbolRight;
                    }
                    symbolLeft = null;
                }
            }
            return this;
        }

        public void CheckExecuteIVJumpReset()
        {
            return;
            double epsilonLeft;
            double epsilonRight;
            DateTime expiry = _expiries.Min();
            // Every Bin will get an error term between IV and IVEWMA.
            // Getting the error terms from all 8 ATM contracts. Each side 4.
            // averaged. If non-zero = presume whole surface has moved vertically / jump. If error small, can profit from it presuming return to average. if large, my quotes will be rather junk, selling into rising IV, buying into falling market...
            // configurable threshold: start with 1%.
            List<double> epsilons = new();
            var atmContracts = ATMContracts();
            foreach (Symbol symbol in atmContracts)
            {
                var ivQuote = _ivQuote(symbol);
                if (ivQuote.IV == null || ivQuote.IV == 0)
                {
                    continue;
                }
                var strikePct = StrikePct(symbol.ID.StrikePrice, MidPriceUnderlying);
                var isOTM = IsOTM(symbol.ID.OptionRight, strikePct);
                if (!_bins.TryGetValue((isOTM, expiry, Math.Floor(strikePct)), out Bin binLeft))
                {
                    continue;
                }
                if (!_bins.TryGetValue((isOTM, expiry, Math.Ceiling(strikePct)), out Bin binRight))
                {
                    continue;
                }
                
                epsilonLeft = ivQuote.IV + (binLeft.SlopeEWMA ?? 0) * (double)(strikePct - binLeft.Value) - (binLeft.IVEWMA ?? ivQuote.IV);
                epsilonRight = ivQuote.IV + (binRight.SlopeEWMA ?? 0) * (double)(strikePct - binRight.Value) - (binRight.IVEWMA ?? ivQuote.IV);
                epsilons.Add((epsilonLeft + epsilonRight) / 2);
            }
            if (epsilons.Mean() > _algo.Cfg.SurfaceVerticalResetThreshold)
            {
                _algo.Log($"Detected significant vertical ATM IV Surface jump. Setting all EWMAs to their current IV zeroing epsilon. {string.Join(",", epsilons)}. Derived from {string.Join(",", atmContracts)}");
            }
            foreach (var bin in _bins.Values)
            {
                if (_algo.Time.TimeOfDay < new TimeSpan(hours: 9, minutes: 40, seconds: 0));
                {
                    //bin.ResetEWMA();
                }
            }
        }

        private IEnumerable<Bin> GetBins(bool otm, DateTime expiry, decimal gt, decimal lt)
        {
            InitializeMoreBins(otm, expiry, gt, lt);
            return _bins.Values.Where(bin => bin.IsOTM == otm && bin.Expiry == expiry && bin.Value >= gt && bin.Value <= lt);
        }

        private Bin GetBin(bool otm, DateTime expiry, decimal binValue)
        {
            if (!_bins.TryGetValue((otm, expiry, binValue), out Bin bin))
            {
                bin = InitalizeNewBin(otm, expiry, binValue);
                Update();
            }
            return bin;
        }

        /// <summary>
        /// Watch out for interpolation across StrikPct 100. OptionRight flips to stay in the correct OTM/ITM surface. Put into a GetSurfaceBins function....
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Symbol> OptionSymbols(DateTime expiry, bool otm)
        {
            var _midPriceUnderlying = MidPriceUnderlying;
            return _algo.Securities.Where(x => 
            x.Key.SecurityType == SecurityType.Option 
            && x.Key.Underlying == Underlying
            && x.Key.ID.Date == expiry
            && ((Option)x.Value).GetPayOff(MidPriceUnderlying) <= 0 == otm)
                .Select(x => x.Key)
                .OrderBy(x => x.ID.StrikePrice);
        }
        
        private Bin InitalizeNewBin(bool isOTM, DateTime expiry, decimal binValue)
        {
            if (!_bins.ContainsKey((isOTM, expiry, binValue)))
            {
                _bins[(isOTM, expiry, binValue)] = new Bin(Side, binValue, expiry, isOTM, Alpha);

                // Add to min max
                if (_maxBin.TryGetValue((isOTM, expiry), out decimal maxBin))
                {
                    _maxBin[(isOTM, expiry)] = Math.Max(maxBin, binValue);
                }
                else
                {
                    _maxBin[(isOTM, expiry)] = binValue;
                }

                if (_minBin.TryGetValue((isOTM, expiry), out decimal minBin))
                {
                    _minBin[(isOTM, expiry)] = Math.Min(minBin, binValue);
                }
                else
                {
                    _minBin[(isOTM, expiry)] = binValue;
                }
            }
            return _bins[(isOTM, expiry, binValue)];
        }

        /// <summary>
        /// Ensures continuity of the surface in between strikes. Every bin's neighbor is 1 strike pct away.
        /// </summary>
        private void InitializeMoreBins(bool otm, DateTime expiry, decimal gt, decimal lt)
        {
            if (gt < _minBin[(otm, expiry)])
            {
                foreach (int binValue in Enumerable.Range((int)Math.Floor(gt), (int)Math.Ceiling(gt - _minBin[(otm, expiry)])+1))
                {
                    InitalizeNewBin(otm, expiry, binValue);
                }
            }
            if (lt > _maxBin[(otm, expiry)])
            {
                foreach (int binValue in Enumerable.Range((int)_maxBin[(otm, expiry)], (int)Math.Ceiling(lt - (int)_maxBin[(otm, expiry)])+1))
                {
                    InitalizeNewBin(otm, expiry, binValue);
                }
            }
        }

        public double? IV(Symbol symbol)
        {
            // The OnData functions first updates all indicators BEFORE business events are triggered.
            // Lazy is best. Minimize any interpolation. If possible get update bin based on current market data, go for it. Otherwise interpolate neighboring _bins.
            double? ewma = null;

            if (symbol.SecurityType != SecurityType.Option) return ewma;

            DateTime expiry = symbol.ID.Date;
            decimal strike = symbol.ID.StrikePrice;
            decimal strikePct = StrikePct(strike);
            bool isOTM = IsOTM(symbol.ID.OptionRight, strikePct);

            Bin bin = GetBin(isOTM, expiry, Math.Round(strikePct));
            // Correcting for binToBin % difference. One-sided interpolation.
            double? ewmaInterpolated = bin.IVEWMA + bin.SlopeEWMA * (double)(strikePct - bin.Value);
            return ewmaInterpolated;
        }

        private decimal StrikePct(decimal strike, decimal? midPrice = null)
        {
            //  K/S, which is known as the (spot) simple moneyness
            return 100 * strike / (midPrice ?? MidPriceUnderlying);
        }

        public IEnumerable<Symbol> ATMContracts()
        {
            var atmStrikeLower = _strikes[_expiries.Min()].Where(x => x < MidPriceUnderlying).Max();
            var atmStrikeUpper = _strikes[_expiries.Min()].Where(x => x > MidPriceUnderlying).Min();
            var atmStrikes = new HashSet<decimal>() { atmStrikeLower, atmStrikeUpper };
            return _algo.Securities.Where(x => x.Key.SecurityType == SecurityType.Option && x.Key.Underlying == Underlying && x.Key.ID.Date == _expiries.Min() && atmStrikes.Contains(x.Key.ID.StrikePrice)).Select(x => x.Key);
        }

        public Bin ATMBin(bool otm = true)
        {
            // Should be warmed up. No null here.
            // Derive ATM volatility from the bin with value 100. OTM only. And smallest expiry.
            var minExpiry = _expiries.Min();
            if (!_bins.ContainsKey((otm, minExpiry, 100)))
            {
                InitalizeNewBin(otm, minExpiry, 100);
                Update();
            }
            return _bins[(otm, minExpiry, 100)];
        }

        public double AtmIVEWMA(bool otm = true)
        {
            double? vol = ATMBin(otm).IVEWMA;
            if (vol == null)
            {
                _algo.Error($"AtmIVEWMA is null. WarmUp Failed. {otm} {_expiries.Min()}");
                return 0;
            }
            return (double)vol;
        }

        public double AtmIv(bool otm = true)
        {
            double? vol = ATMBin(otm).IV;
            if (vol == null)
            {
                _algo.Error($"AtmIVEWMA is null. WarmUp Failed. {otm} {_expiries.Min()}");
                return 0;
            }
            return (double)vol;
        }

        public Dictionary<bool, Dictionary<DateTime, Dictionary<decimal, double?>>> ToDictionary(decimal minBin = 70, decimal maxBin = 130, Func<Bin, double?>? binGetter = null)
        {
            binGetter = binGetter == null ? (bin) => bin.IVEWMA : binGetter;
            // Initialize empty bin dictionary
            Dictionary<bool, Dictionary<DateTime, Dictionary<decimal, double?>>> dict = new();
            foreach (bool isOTM in new bool[] { true, false })
            {
                dict[isOTM] = new Dictionary<DateTime, Dictionary<decimal, double?>>();
                foreach (var expiry in _strikes.Keys)
                {
                    dict[isOTM][expiry] = new Dictionary<decimal, double?>();
                    for (int i = (int)minBin; i <= (int)maxBin; i++)
                    {
                        dict[isOTM][expiry][i] = null;
                    }
                }
            }

            // Fill wherever we have values
            foreach (var bin in _bins.Values)
            {
                if (bin.Value >= minBin && bin.Value <= maxBin)
                {
                    dict[bin.IsOTM][bin.Expiry][bin.Value] = binGetter(bin);
                }
            }
            return dict;
        }

        public bool IsOTM(OptionRight right, decimal strikePct)
        {
            return right == OptionRight.Call ? strikePct > 100 : strikePct < 100;
        }

        public string GetCsvHeader()
        {
            var csv = new StringBuilder();
            var dict = ToDictionary();

            List<decimal> sortedKeys = dict[true][dict[true].Keys.First()].Keys.Sorted().ToList();
            List<string> header = sortedKeys.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
            csv.AppendLine("Time,IsOTM,Expiry," + string.Join(",", header));
            return csv.ToString();
        }
        public void WriteCsvRows()
        {
            var csv = new StringBuilder();
            var dict = ToDictionary(binGetter: (bin) => bin.IVEWMA);
            if (!_headerWritten)
            {
                _writer.Write(GetCsvHeader());
                _writerRaw.Write(GetCsvHeader());
                _headerWritten = true;
            }

            List<decimal> sortedKeys = dict[true][dict[true].Keys.First()].Keys.Sorted().ToList();

            // Smoothened IVs
            foreach (bool isOTM in new bool[] { true, false })
            {
                foreach (var expiry in dict[isOTM].Keys)
                {
                    string ts = _algo.Time.ToString("yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture);
                    string row = $"{ts},{isOTM},{expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}," + string.Join(",", sortedKeys.Select(d => dict[isOTM][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                    csv.AppendLine(row);
                }
            }

            _writer.Write(csv.ToString());

            // Raw Non-Smoothened IVs
            csv = new StringBuilder();
            dict = ToDictionary(binGetter: (bin) => bin.IV);
            foreach (bool isOTM in new bool[] { true, false })
            {
                foreach (var expiry in dict[isOTM].Keys)
                {
                    string ts = _algo.Time.ToString("yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture);
                    string row = $"{ts},{isOTM},{expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}," + string.Join(",", sortedKeys.Select(d => dict[isOTM][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                    csv.AppendLine(row);
                }
            }
            _writerRaw.Write(csv.ToString());
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
            _writerRaw.Flush();
            _writerRaw.Dispose();
        }
    }
}
