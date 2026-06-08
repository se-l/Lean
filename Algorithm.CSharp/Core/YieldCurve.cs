
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Accord.Math.Geometry;
using QLNet;
using Path = System.IO.Path;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class ZeroCurveData
    {
        public float[] Times { get; set; }
        public float[] Rates { get; set; }
    }

    public class YieldCurve
    {
        private const string DailyTreasuryBillRates = "daily-treasury-bill-rates";
        private const string DailyTreasuryParYieldCurveRates = "daily-treasury-par-yield-curve-rates";
        
        private static readonly Lazy<YieldCurve> _instance = new(() => new YieldCurve());
        public static YieldCurve Instance => _instance.Value;

        // Maps date to a dictionary of {TenorInYears: ZeroRate}
        private readonly Dictionary<DateTime, SortedDictionary<double, double>> _billRates = new();
        private readonly Dictionary<DateTime, SortedDictionary<double, double>> _bootstrappedParRates = new();
        private readonly Dictionary<DateTime, YieldTermStructure> _termStructureCache = new();
        private readonly HashSet<DateTime> _initializedDates = new();

        private YieldCurve() { }

        public void Initialize(DateTime date, string market = "usa")
        {
            LoadBillRates(market, date);
            LoadParYieldCurve(market, date);
        }

        private static string GetFilePath(string market, string type, int year)
        {
            return Path.Combine(Globals.DataFolder, "alternative", "interest-rate", market, type, $"{year}.csv");
        }

        public void LoadBillRates(string market, DateTime date)
        {
            var path = GetFilePath(market, DailyTreasuryBillRates, date.Year);
            if (!File.Exists(path)) return;

            // Header: Date, 4 WKS BD, 4 WKS CE, 6 WKS BD, 6 WKS CE, 8 WKS BD, 8 WKS CE, 13 WKS BD, 13 WKS CE, 17 WKS BD, 17 WKS CE, 26 WKS BD, 26 WKS CE, 52 WKS BD, 52 WKS CE
            // Indices for Coupon Equivalent (CE) columns: 2, 4, 6, 8, 10, 12, 14
            int[] ceIndices = { 2, 4, 6, 8, 10, 12, 14 };
            double[] tenors = { 4.0 / 52, 6.0 / 52, 8.0 / 52, 13.0 / 52, 17.0 / 52, 26.0 / 52, 52.0 / 52 };

            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 15 || !DateTime.TryParseExact(parts[0], "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rowDate))
                    continue;

                if (!_billRates.ContainsKey(rowDate)) _billRates[rowDate] = new SortedDictionary<double, double>();

                for (int i = 0; i < tenors.Length; i++)
                {
                    if (double.TryParse(parts[ceIndices[i]], NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                    {
                        _billRates[rowDate][tenors[i]] = rate / 100.0;
                    }
                }
            }
        }

        public void LoadParYieldCurve(string market, DateTime date)
        {
            var path = GetFilePath(market, DailyTreasuryParYieldCurveRates, date.Year);
            if (!File.Exists(path)) return;

            // Header: Date,"1 Mo","1.5 Month","2 Mo","3 Mo","4 Mo","6 Mo","1 Yr","2 Yr","3 Yr","5 Yr","7 Yr","10 Yr","20 Yr","30 Yr"
            double[] tenors = { 1.0/12, 1.5/12, 2.0/12, 3.0/12, 4.0/12, 6.0/12, 1.0, 2.0, 3.0, 5.0, 7.0, 10.0, 20.0, 30.0 };

            foreach (var line in File.ReadAllLines(path).Skip(1))
            {
                var parts = line.Split(',');
                if (parts.Length < 15 || !DateTime.TryParseExact(parts[0], "MM/dd/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var rowDate))
                    continue;

                var parYields = new List<(double Tenor, double Rate)>();
                for (int i = 0; i < tenors.Length; i++)
                {
                    if (double.TryParse(parts[i + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rate))
                    {
                        parYields.Add((tenors[i], rate / 100.0));
                    }
                }

                if (parYields.Count > 0)
                {
                    var bootstrapped = BootstrapToZeroRates(parYields);
                    if (!_bootstrappedParRates.ContainsKey(rowDate)) _bootstrappedParRates[rowDate] = new SortedDictionary<double, double>();
                    
                    foreach (var kvp in bootstrapped)
                    {
                        _bootstrappedParRates[rowDate][kvp.Key] = kvp.Value;
                    }
                }
            }
        }

        public ZeroCurveData GetZeroCurve(DateTime calculationDate)
        {
            DateTime date = calculationDate.Date;
            if (!_initializedDates.Contains(date))
            {
                Initialize(date);
                _initializedDates.Add(date);
            }

            var resultCurve = new SortedDictionary<double, double>();

            // 1. Add T-Bill rates (typically for tenors <= 1.0 year)
            if (_billRates.TryGetValue(date, out var bills))
            {
                foreach (var kvp in bills)
                {
                    resultCurve[kvp.Key] = kvp.Value;
                }
            }

            // 2. Add Par Yield zero rates (typically for tenors > 1.0 year)
            if (_bootstrappedParRates.TryGetValue(date, out var parZeros))
            {
                foreach (var kvp in parZeros)
                {
                    // If we have bills for short tenors, we prefer them.
                    // Only add par yields if tenor > 1.0 or if no bill rate exists for that tenor.
                    if (kvp.Key > 1.0 || !resultCurve.ContainsKey(kvp.Key))
                    {
                        resultCurve[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (resultCurve.Count == 0)
            {
                return new ZeroCurveData { Times = Array.Empty<float>(), Rates = Array.Empty<float>() };
            }

            return new ZeroCurveData
            {
                Times = resultCurve.Keys.Select(t => (float)t).ToArray(),
                Rates = resultCurve.Values.Select(r => (float)r).ToArray()
            };
        }
        
        // <summary>
        /// Returns the zero curve expanded to include a point for every calendar day
        /// between T=0 and the last available tenor (step = 1/365 years).
        /// Rates are linearly interpolated in (time -> zero rate) space.
        /// </summary>
        public ZeroCurveData GetZeroCurveInterpolated(DateTime calculationDate)
        {
            var baseCurve = GetZeroCurve(calculationDate);
            if (baseCurve.Times.Length == 0)
            {
                return baseCurve;
            }

            var maxTimeYears = baseCurve.Times[baseCurve.Times.Length - 1];
            var maxDays = Math.Max(0, (int)Math.Ceiling(maxTimeYears * 365f));

            var times = new float[maxDays + 1];
            var rates = new float[maxDays + 1];

            for (int d = 0; d <= maxDays; d++)
            {
                var t = d / 365.0; // year fraction (double for stable interpolation)
                times[d] = (float)t;
                rates[d] = (float)InterpolateZeroRateLinear(baseCurve.Times, baseCurve.Rates, t);
            }

            return new ZeroCurveData { Times = times, Rates = rates };
        }

        private static double InterpolateZeroRateLinear(float[] times, float[] rates, double t)
        {
            if (times == null || rates == null || times.Length == 0 || rates.Length == 0)
                return 0.0;

            int n = Math.Min(times.Length, rates.Length);

            if (t <= times[0]) return rates[0];
            if (t >= times[n - 1]) return rates[n - 1];

            for (int i = 0; i < n - 1; i++)
            {
                var t1 = (double)times[i];
                var t2 = (double)times[i + 1];

                if (t >= t1 && t <= t2)
                {
                    var r1 = (double)rates[i];
                    var r2 = (double)rates[i + 1];
                    var w = (t - t1) / (t2 - t1);
                    return r1 + (r2 - r1) * w;
                }
            }

            return rates[n - 1];
        }

        private SortedDictionary<double, double> BootstrapToZeroRates(List<(double Tenor, double Rate)> parYields)
        {
            var zeroRates = new SortedDictionary<double, double>();
            var sortedPar = parYields.OrderBy(x => x.Tenor).ToList();

            foreach (var (t, c) in sortedPar)
            {
                if (t <= 1.0)
                {
                    zeroRates[t] = c;
                }
                else
                {
                    // Treasury bonds pay semi-annually. We need PV of all coupons at 0.5, 1.0, 1.5 ... t
                    double coupon = c / 2.0;
                    double presentValueCoupons = 0;

                    // Number of semi-annual payments
                    int numPayments = (int)Math.Round(t * 2);
                    for (int i = 1; i < numPayments; i++)
                    {
                        double paymentTime = i * 0.5;
                        // Interpolate the zero rate for the payment time
                        double z_i = InterpolateZeroRate(zeroRates, paymentTime);
                        presentValueCoupons += coupon / Math.Pow(1 + z_i / 2.0, 2 * paymentTime);
                    }

                    double remainingPrice = 1.0 - presentValueCoupons;
                    if (remainingPrice <= 0)
                    {
                        zeroRates[t] = c;
                        continue;
                    }

                    double z_n = 2.0 * (Math.Pow((1.0 + coupon) / remainingPrice, 1.0 / (2.0 * t)) - 1.0);
                    zeroRates[t] = z_n;
                }
            }
            return zeroRates;
        }

        private static double InterpolateZeroRate(SortedDictionary<double, double> zeroRates, double t)
        {
            if (zeroRates.Count == 0) return 0;
            
            var keys = zeroRates.Keys.ToList();
            if (t <= keys[0]) return zeroRates[keys[0]];
            if (t >= keys[keys.Count - 1]) return zeroRates[keys[keys.Count - 1]];

            for (int i = 0; i < keys.Count - 1; i++)
            {
                if (t >= keys[i] && t <= keys[i + 1])
                {
                    double t1 = keys[i];
                    double t2 = keys[i + 1];
                    double r1 = zeroRates[t1];
                    double r2 = zeroRates[t2];
                    return r1 + (r2 - r1) * (t - t1) / (t2 - t1);
                }
            }
            return zeroRates[keys[keys.Count - 1]];
        }
        
        public Handle<YieldTermStructure> GetYieldTermStructure(DateTime calculationDate, Date qlCalculationDate, DayCounter dayCounter)
        {
            DateTime date = calculationDate.Date;
            if (_termStructureCache.TryGetValue(date, out var cachedTs))
            {
                return new Handle<YieldTermStructure>(cachedTs);
            }

            var zeroCurveData = GetZeroCurve(calculationDate);
            if (zeroCurveData.Times.Length == 0)
            {
                // Fallback to a very small flat rate if no data is available
                var flatTs = new FlatForward(qlCalculationDate, 0.0001, dayCounter);
                return new Handle<YieldTermStructure>(flatTs);
            }

            var qlDates = new List<Date>();
            var qlRates = new List<double>();

            // Reference point at T=0
            qlDates.Add(qlCalculationDate);
            qlRates.Add(zeroCurveData.Rates[0]);

            var lastDate = qlCalculationDate;

            // IMPORTANT: skip any node that maps to the same date as the reference (e.g. t=0)
            for (int i = 0; i < zeroCurveData.Times.Length; i++)
            {
                var days = (int)Math.Round(zeroCurveData.Times[i] * 365f);
                if (days <= 0) continue;

                var nextDate = qlCalculationDate + days;
                if (nextDate <= lastDate) continue;

                qlDates.Add(nextDate);
                qlRates.Add(zeroCurveData.Rates[i]);
                lastDate = nextDate;
            }

            // ConvexMonotone, Cubic, LogCubic, Linear, LogLinear, ForwardFlat, BackwardFlat 
            var interpolatedZeroCurve = new InterpolatedZeroCurve<ConvexMonotone>(qlDates, qlRates, dayCounter);
            _termStructureCache[date] = interpolatedZeroCurve;

            return new Handle<YieldTermStructure>(interpolatedZeroCurve);
        }
    }
}
