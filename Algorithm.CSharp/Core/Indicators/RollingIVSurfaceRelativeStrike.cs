using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Math;
using System.Globalization;
using System.Text;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class RollingIVSurfaceRelativeStrike<T>
        where T : IVBidAsk, IIVBidAsk
    {
        public Symbol Underlying { get; }
        public bool IsReady => true;
        public readonly string Side;

        private Foundations algo;
        private Dictionary<(bool, DateTime, decimal), Bin<T>> bins = new();
        private Dictionary<DateTime, HashSet<decimal>> Strikes = new();
        private Dictionary<(DateTime, decimal), int> samples = new();
        private DateTime TimeFrontier;
        private decimal MidPrice { get { return algo.MidPrice(Underlying); } }

        // For Adaptive EWMA
        // private double gamma = 0.0001;  // HPE
        // private double eps;

        public RollingIVSurfaceRelativeStrike(Foundations algo, Symbol underlying, string side)
        {
            this.algo = algo;
            Underlying = underlying.SecurityType == SecurityType.Option ? underlying.Underlying : underlying;
            Side = side;
        }

        public void AddStrike(Symbol symbol)
        {
            if (symbol.SecurityType != SecurityType.Option) return;

            if (!Strikes.ContainsKey(symbol.ID.Date))
            {
                Strikes[symbol.ID.Date] = new HashSet<decimal>();
            }
            Strikes[symbol.ID.Date].Add(symbol.ID.StrikePrice);

            if (!samples.ContainsKey((symbol.ID.Date, symbol.ID.StrikePrice)))
            {
                samples[(symbol.ID.Date, symbol.ID.StrikePrice)] = 0;
            }
        }
        public int Samples(Symbol symbol)
        {
            return samples[(symbol.ID.Date, symbol.ID.StrikePrice)];
        }

        public void Update(IVBidAsk item)
        {
            // Only collect raw data here, that is update the bins nearest to the strike.
            if (item == null) return;

            DateTime expiry = item.Symbol.ID.Date;
            decimal strike = item.Symbol.ID.StrikePrice;
            decimal strikePct = Strike2Bin(strike, item.Symbol.ID.OptionRight, item.UnderlyingMidPrice);
            bool isOTM = IsOTM(item.Symbol.ID.OptionRight, strikePct);
            decimal binValue = RoundedStrike2Bin(strike, item.Symbol.ID.OptionRight, item.UnderlyingMidPrice);

            // First record for this expiry and bin.
            InitalizeNewBin(isOTM, expiry, binValue).Update(item);
            TimeFrontier = TimeFrontier < item.Time ? item.Time : TimeFrontier;
            samples[(expiry, strike)] += 1;
        }

        /// <summary>
        /// Refreshes the surface by updating all bins that can be updated without interpolation. Typically after the underlying price changed.
        /// </summary>
        public void RefreshSurface()
        {
            foreach (var expiry in Strikes.Keys)
            {
                foreach (decimal strike in Strikes[expiry])
                {
                    if (!bins.TryGetValue((true, expiry, RoundedStrike2Bin(strike)), out Bin<T> bin))
                    {
                        bin = InitalizeNewBin(true, expiry, RoundedStrike2Bin(strike));
                    }
                    bin.Refresh();

                    if (!bins.TryGetValue((false, expiry, RoundedStrike2Bin(strike)), out bin))
                    {
                        bin = InitalizeNewBin(false, expiry, RoundedStrike2Bin(strike));
                    }
                    bin.Refresh();
                }
            }
        }

        private Bin<T> InitalizeNewBin(bool isOTM, DateTime expiry, decimal binValue)
        {
            if (!bins.ContainsKey((isOTM, expiry, binValue)))
            {
                bins[(isOTM, expiry, binValue)] = new Bin<T>(algo, this, Side, binValue, null, expiry, DateTime.MinValue, MidPrice, isOTM);
            }
            return bins[(isOTM, expiry, binValue)];
        }

        public double? IV(Symbol symbol)
        {
            // The OnData functions first updates all indicators BEFORE business events are triggered. If bin was not updated during that step, but other bins have recent updates,
            // can be due to 2 reasons:
            // 1) There was no quote update for a particular strike. If same underlying price, we can use the most recent IV. Like fillna. Otherwise, update IV based on latest bid/ask.
            // 2) Underlying price changed so much that new quote updates flow into another bin => interpolate and store the requested bin.

            // If bins are not updated every SamplePeriod, the EWMA becomes inaccurate. Rather to fillforward from or to recalc all bins, an interpolate of neighboring bins on demand is 
            // compuationally more efficient. Lazier.

            // In Summary. Lazy is best. Minimize any interpolation. If possible get update bin based on current market data, go for it. Otherwise interpolate neighboring bins.
            double? ewma = null;

            if (symbol.SecurityType != SecurityType.Option) return ewma;

            decimal strike = symbol.ID.StrikePrice;
            DateTime expiry = symbol.ID.Date;
            decimal strikePct = Strike2Bin(strike, symbol.ID.OptionRight);
            decimal binValue = RoundedStrike2Bin(strike, symbol.ID.OptionRight);
            bool isOTM = IsOTM(symbol.ID.OptionRight, strikePct);

            if (!bins.TryGetValue((isOTM, expiry, binValue), out Bin<T> bin))
            {
                bin = InitalizeNewBin(isOTM, expiry, binValue);
            }
            else if (!bin.IsReady)
            {
                bin.Refresh();
            };

            // Correcting for binToBin % difference. One-sided interpolation.
            ewma = bin.EWMA + bin.Slope * (double)(strikePct - bin.Value);

            return ewma == null ? ewma : Math.Max((double)ewma, 0);
        }

        private decimal Strike2Bin(decimal strike, OptionRight optionRight = OptionRight.Call, decimal? midPrice = null)
        {
            return 100 * strike / (midPrice ?? MidPrice);
        }

        private decimal RoundedStrike2Bin(decimal strike, OptionRight optionRight = OptionRight.Call, decimal? midPrice = null)
        {
            return Math.Round(Strike2Bin(strike, optionRight, midPrice), 0);
        }

        public double? InterpolateNearestBins(Bin<T> bin)
        {
            Bin<T> binLow = null;
            Bin<T> binHigh = null;
            // Query bins with ready market data closest to bin, that is the bins corresponding to strikes.
            // Expected to be slope-corrected.
            // Return one-sided or central interpolation depending on data returned.

            decimal midStrikePct = MidPrice * bin.Value / 100;
            var seqLow = Strikes[bin.Expiry].Where(s => s < midStrikePct);
            var seqHigh = Strikes[bin.Expiry].Where(s => s > midStrikePct);

            // Both null, no data.
            if (seqLow.Any())
            {
                decimal strikeLow = seqLow.Max();
                // Check if key present. Otherwise, trigger update and return null if not possible for any reason.
                if (!bins.TryGetValue((bin.IsOTM, bin.Expiry, RoundedStrike2Bin(strikeLow)), out binLow))
                {
                    binLow = InitalizeNewBin(bin.IsOTM, bin.Expiry, RoundedStrike2Bin(strikeLow));
                }
                // Causes Stack Overflow. Accept potentially old IV values from neighbors.
                //else if (!binLow.IsReady) 
                //{
                //    binLow.Refresh();
                //};
            }

            if (seqHigh.Any())
            {
                decimal strikeHigh = seqHigh.Min();
                if (!bins.TryGetValue((bin.IsOTM, bin.Expiry, RoundedStrike2Bin(strikeHigh)), out binHigh))
                {
                    binHigh = InitalizeNewBin(bin.IsOTM, bin.Expiry, RoundedStrike2Bin(strikeHigh));
                }
                // Causes Stack Overflow. Accept potentially old IV values from neighbors.
                //else if (!binHigh.IsReady)
                //{
                //    binHigh.Refresh();
                //};
            }


            // Central/ linearly interpolated IV EWMA
            if (binLow != null && binLow.EWMA != null && binHigh != null && binHigh.EWMA != null)
            {
                double slope = ((double)binHigh.EWMA - (double)binLow.EWMA) / (double)(binHigh.Value - binLow.Value);
                return binLow.EWMA + slope * (double)(bin.Value - binLow.Value);
            }
            // One-sided EWMA
            else if (binLow != null && binLow.EWMA != null)
            {
                return binLow.EWMA + binLow.Slope * (double)(bin.Value - binLow.Value);
            }
            // One-sided EWMA
            else if (binHigh != null && binHigh.EWMA != null)
            {
                return binHigh.EWMA + binHigh.Slope * (double)(bin.Value - binHigh.Value);
            }
            else
            {
                return null;
            }
        }

        public Dictionary<bool, Dictionary<DateTime, Dictionary<decimal, double?>>> ToDictionary(decimal minBin = 70, decimal maxBin = 130)
        {
            // Initialize empty bin dictionary
            Dictionary<bool, Dictionary<DateTime, Dictionary<decimal, double?>>> dict = new();
            foreach (bool isOTM in new bool[] { true, false })
            {
                dict[isOTM] = new Dictionary<DateTime, Dictionary<decimal, double?>>();
                foreach (var expiry in Strikes.Keys)
                {
                    dict[isOTM][expiry] = new Dictionary<decimal, double?>();
                    for (int i = (int)minBin; i <= (int)maxBin; i++)
                    {
                        dict[isOTM][expiry][i] = null;
                    }
                }
            }

            // Fill wherever we have values
            foreach (var bin in bins.Values)
            {
                if (bin.Value >= minBin && bin.Value <= maxBin)
                {
                    dict[bin.IsOTM][bin.Expiry][bin.Value] = bin.EWMA;
                }
            }
            return dict;
        }

        public bool IsOTM(Symbol symbol)
        {
            return ((Option)algo.Securities[symbol]).GetPayOff(MidPrice) > 0;
        }

        public bool IsOTM(OptionRight right, decimal strikePct)
        {
            return right == OptionRight.Call ? strikePct < 100 : strikePct > 100;
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
        public string GetCsvRows()
        {
            var csv = new StringBuilder();
            var dict = ToDictionary();

            List<decimal> sortedKeys = dict[true][dict[true].Keys.First()].Keys.Sorted().ToList();

            foreach (bool isOTM in new bool[] { true, false })
            {
                foreach (var expiry in dict[isOTM].Keys)
                {
                    string ts = algo.Time.ToString("yyyy-MM-dd hh:mm:ss", CultureInfo.InvariantCulture);
                    string row = $"{ts},{isOTM},{expiry.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}," + string.Join(",", sortedKeys.Select(d => dict[isOTM][expiry][d]?.ToString(CultureInfo.InvariantCulture)));
                    csv.AppendLine(row);
                }
            }

            return csv.ToString();
        }

        public bool IsEmpty { get { return bins.Count == 0; } }
    }
}
