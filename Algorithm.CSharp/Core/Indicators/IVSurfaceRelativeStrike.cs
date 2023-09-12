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
        private readonly List<OptionRight> OptionRights = new() { OptionRight.Call, OptionRight.Put };
        public DateTime Time;
        public Symbol Underlying { get; }
        private readonly QuoteSide _side;
        public QuoteSide Side { get => _side; }

        private readonly Foundations _algo;
        private readonly Dictionary<(OptionRight, DateTime, ushort), Bin> _bins = new();
        private readonly HashSet<DateTime> _expiries = new();
        private readonly Dictionary<DateTime, HashSet<decimal>> _strikes = new();
        private readonly Dictionary<(DateTime, decimal), int> _samples = new();

        private readonly Dictionary<(OptionRight, DateTime), ushort> _minBin = new();
        private readonly Dictionary<(OptionRight, DateTime), ushort> _maxBin = new();
        private readonly double Alpha;
        public readonly TimeSpan SamplingPeriod;


        private readonly string _path;
        private readonly string _pathRaw;
        private readonly StreamWriter _writer;
        private readonly StreamWriter _writerRaw;
        private bool _headerWritten;

        private Func<Symbol, IVQuoteIndicator> _ivQuote;
        private decimal MidPriceUnderlying { get { return _algo.MidPrice(Underlying); } }
        public enum Status
        {
            Samples,
            Smoothings
        }
        private const string _dateTimeFmt = "yyyy-MM-dd HH:mm:ss";
        private const string _dateFmt = "yyyy-MM-dd";

        public IVSurfaceRelativeStrike(Foundations algo, Symbol underlying, QuoteSide side)
        {
            _algo = algo;
            Alpha = _algo.Cfg.IVSurfaceRelativeStrikeAlpha;
            SamplingPeriod = TimeSpan.FromMinutes(1);
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
            InitalizeNewBin(OptionRight.Put, symbol.ID.Date, (ushort)Math.Floor(StrikePct(option)));
            InitalizeNewBin(OptionRight.Call, symbol.ID.Date, (ushort)Math.Floor(StrikePct(option)));
        }

        /// <summary>
        /// Assumes all IVBidAsk update events have been processed. Only need to interpolate in between strikes that actually have changed values. Their timestamps would
        /// need to greater than the Bin's 'raw' IV timestamp.
        /// </summary>
        public IVSurfaceRelativeStrike Update()
        {
            if (
                _algo.Time.TimeOfDay > new TimeSpan(16, 0, 0) || _algo.Time.TimeOfDay < new TimeSpan(9, 30, 0)   // Only RTH
                || Time + SamplingPeriod > _algo.Time) 
            {
                return this; 
            }
            Time = _algo.Time;

            Symbol symbolLeft = null;
            IVQuoteIndicator ivQuoteIndicatorLeft;
            IVQuoteIndicator ivQuoteIndicatorRight;
            decimal strikePctLeft;
            decimal strikePctRight;
            double ivLeft=0;
            double ivRight;
            bool otmRight;
            DateTime timeLeft = default;
            DateTime timeRight;
            DateTime maxTime;


            // Functions instead of for loops and ideally concurrently.
            foreach (var expiry in Expiries())
            {
                foreach (OptionRight optionRight in OptionRights)
                {
                    var symbols = OptionSymbols(expiry, optionRight);
                    var maxStrike = symbols.Select(s => s.ID.StrikePrice).Max();
                    foreach (Symbol symbolRight in symbols)
                    {
                        ivQuoteIndicatorRight = _ivQuote(symbolRight);
                        strikePctRight = StrikePct(symbolRight.ID.StrikePrice, MidPriceUnderlying);
                        otmRight = IsOTM(optionRight, strikePctRight);

                        if (ivQuoteIndicatorRight.IV == 0)
                        {
                            // Use the current IV rather. Otherwise this Bin's values would be overwritten by the interpolation of its neighbors.
                            // Get it from Bin
                            var binRight = GetBin(optionRight, expiry, (ushort)Math.Round(strikePctRight));
                            ivRight = binRight.IV ?? 0;
                            timeRight = binRight.Time;
                        }
                        else
                        {
                            ivRight = ivQuoteIndicatorRight.IV;
                            timeRight = ivQuoteIndicatorRight.Time;
                        }

                        // A change at inception that above is zero nonetheless. Dont use that.
                        if (ivRight == 0)
                        {
                            continue;
                        }
                        else
                        {
                            if (ivLeft != 0 && timeLeft != default)
                            {
                                strikePctLeft = StrikePct(symbolLeft.ID.StrikePrice, MidPriceUnderlying);
                                maxTime = timeLeft > ivQuoteIndicatorRight.Time ? timeLeft : timeRight;

                                var slope = (ivRight - ivLeft) / (double)(strikePctRight - strikePctLeft);

                                // When crossing 100, optionRight changes. Investigate whether surface is smoother regressing with ITM of same right.
                                // Then whole loop goes over expiries -> binValues. OTM derived.
                                foreach (Bin bin in GetBins(optionRight, expiry, Math.Floor(strikePctLeft), strikePctRight))
                                {
                                    double ivInterpolated = ivLeft + slope * (double)(bin.Value - strikePctLeft);
                                    bin.Update(maxTime, ivInterpolated);
                                }

                                // Update bin on right edge. No right neighbor to interpolate from. Extrapolate from left neighbor.
                                if (symbolRight.ID.StrikePrice == maxStrike)
                                {
                                    var strikePctMax = StrikePct(maxStrike, MidPriceUnderlying);
                                    OptionRight optionRightRightEdge = symbolRight.ID.OptionRight;
                                    var binRightEdge = GetBin(optionRightRightEdge, expiry, (ushort)Math.Ceiling(strikePctMax));
                                    double ivRightEdgeInterpolated = ivRight + slope * (double)(binRightEdge.Value - strikePctMax);
                                    binRightEdge.Update(maxTime, ivRightEdgeInterpolated);
                                }
                            }

                            symbolLeft = symbolRight;
                        }
                        ivLeft = ivRight;
                        timeLeft = timeRight;
                    }
                }
            }
            
            return this;
        }

        public void CheckExecuteIVJumpReset()
        {
            return;
            //double epsilonLeft;
            //double epsilonRight;
            //DateTime expiry = MinExpiry();
            //// Every Bin will get an error term between IV and IVEWMA.
            //// Getting the error terms from all 8 ATM contracts. Each side 4.
            //// averaged. If non-zero = presume whole surface has moved vertically / jump. If error small, can profit from it presuming return to average. if large, my quotes will be rather junk, selling into rising IV, buying into falling market...
            //// configurable threshold: start with 1%.
            //List<double> epsilons = new();
            //var atmContracts = ATMContracts();
            //foreach (Symbol symbol in atmContracts)
            //{
            //    var ivQuote = _ivQuote(symbol);
            //    if (ivQuote.IV == null || ivQuote.IV == 0)
            //    {
            //        continue;
            //    }
            //    var strikePct = StrikePct(symbol.ID.StrikePrice, MidPriceUnderlying);
            //    var isOTM = IsOTM(symbol.ID.OptionRight, strikePct);
            //    if (!_bins.TryGetValue((isOTM, expiry, (ushort)Math.Floor(strikePct)), out Bin binLeft))
            //    {
            //        continue;
            //    }
            //    if (!_bins.TryGetValue((isOTM, expiry, (ushort)Math.Ceiling(strikePct)), out Bin binRight))
            //    {
            //        continue;
            //    }
                
            //    //epsilonLeft = ivQuote.IV + (binLeft.SlopeEWMA ?? 0) * (double)(strikePct - binLeft.Value) - (binLeft.IVEWMA ?? ivQuote.IV);
            //    //epsilonRight = ivQuote.IV + (binRight.SlopeEWMA ?? 0) * (double)(strikePct - binRight.Value) - (binRight.IVEWMA ?? ivQuote.IV);
            //    epsilons.Add((epsilonLeft + epsilonRight) / 2);
            //}
            //if (epsilons.Mean() > _algo.Cfg.SurfaceVerticalResetThreshold)
            //{
            //    _algo.Log($"Detected significant vertical ATM IV Surface jump. Setting all EWMAs to their current IV zeroing epsilon. {string.Join(",", epsilons)}. Derived from {string.Join(",", atmContracts)}");
            //}
            //foreach (var bin in _bins.Values)
            //{
            //    if (_algo.Time.TimeOfDay < new TimeSpan(hours: 9, minutes: 40, seconds: 0));
            //    {
            //        //bin.ResetEWMA();
            //    }
            //}
        }

        private IEnumerable<Bin> GetBins(OptionRight optionRight, DateTime expiry, decimal gt, decimal lt)
        {
            InitializeMoreBins(optionRight, expiry, gt, lt);
            return _bins.Values.Where(bin => bin.OptionRight == optionRight && bin.Expiry == expiry && bin.Value >= gt && bin.Value <= lt);
        }

        //private IEnumerable<Bin> GetBinsBC(bool otm, DateTime expiry, decimal gt, decimal lt)
        //{
        //    InitializeMoreBins(otm, expiry, gt, lt);
        //    return _bins.Values.Where(bin => bin.IsOTM == otm && bin.Expiry == expiry && bin.Value >= gt && bin.Value <= lt);
        //}

        private Bin GetBin(OptionRight optionRight, DateTime expiry, ushort binValue)
        {
            if (!_bins.TryGetValue((optionRight, expiry, binValue), out Bin bin))
            {
                bin = InitalizeNewBin(optionRight, expiry, binValue);
                Update();
            }
            return bin;
        }

        /// <summary>
        /// Watch out for interpolation across StrikPct 100. OptionRight flips to stay in the correct OTM/ITM surface. Put into a GetSurfaceBins function....
        /// </summary>
        /// <returns></returns>
        private IEnumerable<Symbol> OptionSymbols(DateTime expiry, OptionRight optionRight)
        {
            var _midPriceUnderlying = MidPriceUnderlying;
            return _algo.Securities.Where(x => 
            x.Key.SecurityType == SecurityType.Option 
            && x.Key.Underlying == Underlying
            && x.Key.ID.Date == expiry
            && x.Key.ID.OptionRight == optionRight)
                .Select(x => x.Key)
                .OrderBy(x => x.ID.StrikePrice);
        }

        private Bin InitalizeNewBin(OptionRight optionRight, DateTime expiry, ushort binValue)
        {
            if (!_bins.ContainsKey((optionRight, expiry, binValue)))
            {
                _bins[(optionRight, expiry, binValue)] = new Bin(Side, binValue, expiry, optionRight, Alpha);

                // Add to min max
                if (_maxBin.TryGetValue((optionRight, expiry), out ushort maxBin))
                {
                    _maxBin[(optionRight, expiry)] = Math.Max(maxBin, binValue);
                }
                else
                {
                    _maxBin[(optionRight, expiry)] = binValue;
                }

                if (_minBin.TryGetValue((optionRight, expiry), out ushort minBin))
                {
                    _minBin[(optionRight, expiry)] = Math.Min(minBin, binValue);
                }
                else
                {
                    _minBin[(optionRight, expiry)] = binValue;
                }

                InitializeInBetweenBins(optionRight, expiry);
            }

            return _bins[(optionRight, expiry, binValue)];
        }

        private void InitializeInBetweenBins(OptionRight optionRight, DateTime expiry)
        {
            // Ensuring there's a bin for every value in between min and max.
            foreach (ushort binValue in Enumerable.Range(_minBin[(optionRight, expiry)],(_maxBin[(optionRight, expiry)] - _minBin[(optionRight, expiry)])))
            {
                if (!_bins.ContainsKey((optionRight, expiry, binValue)))
                {
                    _bins[(optionRight, expiry, binValue)] = new Bin(Side, binValue, expiry, optionRight, Alpha);
                }
            }
        }

        /// <summary>
        /// Ensures continuity of the surface in between strikes. Every bin's neighbor is 1 strike pct away.
        /// </summary>
        private void InitializeMoreBins(OptionRight optionRight, DateTime expiry, decimal gt, decimal lt)
        {
            if (gt < _minBin[(optionRight, expiry)])
            {
                foreach (ushort binValue in Enumerable.Range((ushort)Math.Floor(gt), (ushort)Math.Ceiling(_minBin[(optionRight, expiry)] - gt)))
                {
                    InitalizeNewBin(optionRight, expiry, binValue);
                }
            }
            if (lt > _maxBin[(optionRight, expiry)])
            {
                foreach (ushort binValue in Enumerable.Range(_maxBin[(optionRight, expiry)]+1, (ushort)Math.Ceiling(lt - _maxBin[(optionRight, expiry)])+1))
                {
                    InitalizeNewBin(optionRight, expiry, binValue);
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
            OptionRight optionRight = symbol.ID.OptionRight;

            ushort binValue = (ushort)Math.Round(strikePct);
            Bin bin = GetBin(optionRight, expiry, binValue);
            Bin binB = GetBin(optionRight, expiry, (ushort)(binValue + Math.Sign(strikePct - binValue)));
            // Correcting for binToBin % difference. One-sided interpolation.
            double slopeEWMA = Slope(bin, binB);
            double? ewmaInterpolated = bin.IVEWMA + slopeEWMA * (double)(strikePct - bin.Value);
            if (ewmaInterpolated == null) // No IV, no price. Major problem, especially on bid side.
            {
                _algo.Error($"IVSurfaceRelativeStrike: {symbol} - Null EWMA interpolated. Will cause pricing to fail Fix. " +
                    $"IsOTM={isOTM}, strikePct={strikePct}, slopeEWMA={slopeEWMA}, " +
                    $"bin.IVEWMA={bin.IVEWMA}, bin.IV={bin.IV}, bin.Value={bin.Value}, binB.Samples={bin.Samples}, " +
                    $"binB.IVEWMA={binB.IVEWMA}, binB.IV={binB.IV}, binB.Value={binB.Value}, binB.Samples={binB.Samples}, ");
            }
            //if (symbol.Value == "PFE   240119C00038000" && _algo.Time.TimeOfDay > new TimeSpan(9, 49, 0) & _algo.Time.TimeOfDay < new TimeSpan(9, 50, 0))
            //{
            //    _algo.Log($"side: {Side}");
            //    _algo.Log($"strike: {strike}");
            //    _algo.Log($"strikePct: {strikePct}");
            //    _algo.Log($"bin: {bin}");
            //    _algo.Log($"bin.IsOTM: {bin.IsOTM}");
            //    _algo.Log($"bin.Value: {bin.Value}");
            //    _algo.Log($"bin.IVEWMA: {bin.IVEWMA}");
            //    _algo.Log($"bin.IV: {bin.IV}");
            //    _algo.Log($"ewmaInterpolated: {ewmaInterpolated}");

                //    _algo.Log($"binLeft: {binB}");
                //    _algo.Log($"binB.IsOTM: {binB.IsOTM}");
                //    _algo.Log($"binB.Value: {binB.Value}");
                //    _algo.Log($"binB.IVEWMA: {binB.IVEWMA}");
                //    _algo.Log($"binB.IV: {binB.IV}");

                //    _algo.Log($"slopeEWMA: {slopeEWMA}");
                //}
            return ewmaInterpolated;
        }

        public bool IsReady(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option) return false;

            decimal strikePct = StrikePct(symbol.ID.StrikePrice);
            Bin bin = GetBin(
                symbol.ID.OptionRight,
                symbol.ID.Date,
                (ushort)Math.Round(strikePct)
                );
            return bin.Samples > 0;
        }

        public double Slope(Bin binA, Bin binB)
        {
            if (binB.IVEWMA == null || binA.IVEWMA == null) { return 0; }
            return ((double)binB.IVEWMA - (double)binA.IVEWMA) / (binB.Value - binA.Value);
        }

        private decimal StrikePct(decimal strike, decimal? midPrice = null)
        {
            //  K/S, which is known as the (spot) simple moneyness
            decimal _midPrice = midPrice ?? MidPriceUnderlying;
            return _midPrice == 0 ? 0 : 100 * strike / _midPrice;
        }

        private decimal StrikePct(Option option, decimal? midPrice = null)
        {
            return StrikePct(option.Symbol.ID.StrikePrice, midPrice);
        }

        public IEnumerable<Symbol> ATMContracts()
        {
            var atmStrikeLower = _strikes[MinExpiry()].Where(x => x < MidPriceUnderlying).Max();
            var atmStrikeUpper = _strikes[MinExpiry()].Where(x => x > MidPriceUnderlying).Min();
            var atmStrikes = new HashSet<decimal>() { atmStrikeLower, atmStrikeUpper };
            return _algo.Securities.Where(x => x.Key.SecurityType == SecurityType.Option && x.Key.Underlying == Underlying && x.Key.ID.Date == MinExpiry() && atmStrikes.Contains(x.Key.ID.StrikePrice)).Select(x => x.Key);
        }

        public IEnumerable<Bin> ATMBins()
        {
            // Should be warmed up. No null here.
            // Derive ATM volatility from the bin with value 100. OTM only. And smallest expiry.
            var minExpiry = MinExpiry();
            foreach (OptionRight optionRight in OptionRights)
            {
                if (!_bins.ContainsKey((optionRight, minExpiry, 100)))
                {
                    InitalizeNewBin(optionRight, minExpiry, 100);
                    Update();
                }
            }
            
            return OptionRights.Select(right => _bins[(right, minExpiry, 100)]);
        }

        public double AtmIVEWMA()
        {
            /// Mean is not good. Use weighted mean. Weight by distance.
            IEnumerable<double?> vols = ATMBins().Select(b => b.IVEWMA);
            if (vols.Any(x => x == null))
            {
                _algo.Error($"An AtmIVEWMA is null. WarmUp Failed for expiry={MinExpiry()}");
                return 0;
            }
            return (double)vols.Mean();
        }

        public double AtmIv()
        {
            /// Mean is not good. Use weighted mean. Weight by distance.
            IEnumerable<double?> vols = ATMBins().Select(b => b.IV);
            if (vols.Any(x => x == null))
            {
                _algo.Error($"AtmIV is null for expiry={MinExpiry()}");
                return 0;
            }
            return (double)vols.Mean();
        }

        public DateTime MinExpiry()
        {
            return Expiries().Min();
        }

        public List<DateTime> Expiries()
        {
            return _expiries.Where(e => e > _algo.Time.Date).ToList();
        }

        public Dictionary<OptionRight, Dictionary<DateTime, Dictionary<decimal, double?>>> ToDictionary(decimal minBin = 70, decimal maxBin = 130, Func<Bin, double?>? binGetter = null)
        {
            binGetter ??= ((bin) => bin.IVEWMA);
            // Initialize empty bin dictionary
            Dictionary<OptionRight, Dictionary<DateTime, Dictionary<decimal, double?>>> dict = new();
            foreach (OptionRight optionRight in OptionRights)
            {
                dict[optionRight] = new Dictionary<DateTime, Dictionary<decimal, double?>>();
                foreach (var expiry in _strikes.Keys)
                {
                    dict[optionRight][expiry] = new Dictionary<decimal, double?>();
                    for (int i = (int)minBin; i <= (int)maxBin; i++)
                    {
                        dict[optionRight][expiry][i] = null;
                    }
                }
            }

            // Fill wherever we have values
            foreach (Bin bin in _bins.Values)
            {
                if (bin.Value >= minBin && bin.Value <= maxBin)
                {
                    dict[bin.OptionRight][bin.Expiry][bin.Value] = binGetter(bin);
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

            List<decimal> sortedKeys = dict[OptionRight.Call][dict[OptionRight.Call].Keys.First()].Keys.Sorted().ToList();
            List<string> header = sortedKeys.Select(d => d.ToString(CultureInfo.InvariantCulture)).ToList();
            csv.AppendLine("Time,OptionRight,Expiry," + string.Join(",", header));
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

            List<decimal> sortedKeys = dict[OptionRight.Call][dict[OptionRight.Call].Keys.First()].Keys.Sorted().ToList();

            // Smoothened IVs
            foreach (OptionRight optionRight in OptionRights)
            {
                foreach (var expiry in dict[optionRight].Keys)
                {
                    string ts = _algo.Time.ToString(_dateTimeFmt, CultureInfo.InvariantCulture);
                    string row = $"{ts},{optionRight},{expiry.ToString(_dateFmt, CultureInfo.InvariantCulture)}," + string.Join(",", sortedKeys.Select(d => dict[optionRight][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                    csv.AppendLine(row);
                }
            }

            _writer.Write(csv.ToString());

            // Raw Non-Smoothened IVs
            csv = new StringBuilder();
            dict = ToDictionary(binGetter: (bin) => bin.IV);
            foreach (OptionRight optionRight in OptionRights)
            {
                foreach (var expiry in dict[optionRight].Keys)
                {
                    string ts = _algo.Time.ToString(_dateTimeFmt, CultureInfo.InvariantCulture);
                    string row = $"{ts},{optionRight},{expiry.ToString(_dateFmt, CultureInfo.InvariantCulture)}," + string.Join(",", sortedKeys.Select(d => dict[optionRight][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                    csv.AppendLine(row);
                }
            }
            _writerRaw.Write(csv.ToString());
        }
        public Dictionary<string, uint> Samples()
        {
            return _bins.Values.ToDictionary(x => x.Id(), x => x.Samples);
        }
        public Dictionary<string, uint> Smoothings()
        {
            return _bins.Values.ToDictionary(x => x.Id(), x => x.Smoothings);
        }

        public string GetStatus(Status status)
        {
            StringBuilder binLog = new();
            string metric;
            Func<Dictionary<string, uint>> func;
            switch (status)
            {
                case Status.Samples:
                    metric = "Samples";
                    func = Samples;
                    break;
                case Status.Smoothings:
                    metric = "Smoothings";
                    func = Smoothings;
                    break;
                default:
                    throw new NotImplementedException();
            };
            binLog.AppendLine($"{Time} {Underlying} IV Surface Relative Strike {metric} {Side}:");
            foreach (var kvp in func())
            {
                binLog.Append($"{kvp.Key}: {kvp.Value}; ");
            };
            binLog.AppendLine(".");
            return binLog.ToString();
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
