using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MathNet.Numerics.Statistics;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Data.Market;
using System.Reflection;
using System.Text;
using System.IO;
using QuantConnect.Securities;
using Accord.Statistics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public static class Statics
    {
        public static HashSet<Type> PrimitiveTypes = new()
        {
            typeof(decimal),
            typeof(int),
            typeof(double),
            typeof(float),
            typeof(long),
            typeof(short),
            typeof(ulong),
            typeof(ushort),
            typeof(byte),
            typeof(sbyte),
            typeof(bool),
            typeof(char),
            typeof(string),
            typeof(DateTime),
            typeof(DateTimeOffset),
            typeof(TimeSpan),
            typeof(Guid),
            typeof(decimal?),
            typeof(int?),
            typeof(double?),
            typeof(float?),
            typeof(long?),
            typeof(short?),
            typeof(ulong?),
            typeof(ushort?),
            typeof(byte?),
            typeof(sbyte?),
            typeof(bool?),
            typeof(char?),
            typeof(DateTime?),
            typeof(DateTimeOffset?),
            typeof(TimeSpan?),
            typeof(Guid?)
        };
        public static HashSet<Type> ToStringTypes = new()
        {
            typeof(Symbol), typeof(Security), typeof(SecurityType), typeof(SecurityType?)
        };
        public static decimal BP = 1m / 10_000m;
        public static OrderDirection Num2Direction(decimal num)
        {
            if (num > 0)
            {
                return OrderDirection.Buy;
            }
            if (num < 0)
            {
                return OrderDirection.Sell;
            }
            if (num == 0)
            {
                return OrderDirection.Hold;
            }
            throw new Exception("Unknown direction");
        }
        public static Dictionary<int, OrderDirection> NUM2DIRECTION = new()
        {
                { 1, OrderDirection.Buy },
                { -1, OrderDirection.Sell },
                { 0, OrderDirection.Hold }
        };
        public static Dictionary<OrderDirection, int> DIRECTION2NUM = new()
        {
            { OrderDirection.Buy, 1 },
            { OrderDirection.Sell, -1 },
            { OrderDirection.Hold, 0 }
        };

        public static Dictionary<OptionRight, int> OptionRight2Int = new()
        {
            { OptionRight.Call, 1 },
            { OptionRight.Put, -1 }
        };

        public static double Covariance(double[] x, double[] y, int window)
        {
            double[] xWindow = x.Length != window ? x.TakeLast(window).ToArray() : x;
            double[] yWindow = y.Length != window ? y.TakeLast(window).ToArray() : y;
            return Measures.Covariance(xWindow, xWindow.Mean(), y, yWindow.Mean(), unbiased: true);
        }

        public static int GetBusinessDays(DateTime startDate, DateTime endDate, IEnumerable<DateTime> holidays = null)
        {
            int days = 0;
            for (DateTime date = startDate.Date; date <= endDate.Date; date = date.AddDays(1))
            {
                if (date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday && (holidays == null || !holidays.Contains(date)))
                {
                    days++;
                }
            }
            return days;
        }
        public enum RiskLimitScope
        {
            Security,
            Underlying,
            Portfolio,
        }
        public enum RiskLimitType
        {
            Delta,
            Gamma,
            Theta,
            Vega
        }
        public enum Metric
        {
            Delta, // Unit free sensitivity
            DeltaTotal,
            DeltaImpliedTotal,

            Delta100BpUSDTotal,
            DeltaImplied100BpUSDTotal,
            Delta500BpUSDTotal,           
            
            EquityDeltaTotal,

            Gamma,
            GammaTotal,
            GammaImpliedTotal,
            Gamma100BpUSDTotal,
            GammaImplied100BpUSDTotal,
            Gamma500BpUSDTotal,
            GammaImplied500BpUSDTotal,

            Theta,
            ThetaTotal,
            ThetaUSDTotal,
            Theta1DayUSD,

            Vega,
            VegaTotal,
            VegaUSDTotal,
            Vega100BpUSD,

            // Bands
            ZMOffset,
            BandZMLower,
            BandZMUpper,

            Events,
            Absolute
        }

        public static decimal RoundTick(decimal x, decimal tickSize, bool? ceil = null, decimal? reference = null)
        {
            if (reference != null && Math.Abs((decimal)(reference - x)) < (tickSize / 2))

            {
                return (decimal)reference;
            }
            if (ceil == true)
            {
                return Math.Ceiling(x * (1 / tickSize)) / (1 / tickSize);
            }
            else if (ceil == false)
            {
                return Math.Floor(x * (1 / tickSize)) / (1 / tickSize);
            }
            else if (tickSize == 0)
            {
                return x;
            }
            else
            {
                return Math.Round(x * (1 / tickSize)) / (1 / tickSize);
            }
        }
        public static decimal MidPrice(QuoteBar quoteBar)
        {
            return (quoteBar.Ask.Close + quoteBar.Bid.Close) / 2;
        }

        public static IEnumerable<double> RollingPearsonCorr(IEnumerable<double> x, IEnumerable<double> y, int window)
        {
            var corr = new List<double>();
            //var p_val = new List<double>();
            corr.AddRange(Enumerable.Repeat(double.NaN, x.Count()));
            //p_val.AddRange(Enumerable.Repeat(double.NaN, x.Count));
            for (int i = window - 1; i < x.Count(); i++)
            {
                var xWindow = x.Skip(i - window + 1).Take(window);
                var yWindow = y.Skip(i - window + 1).Take(window);
                var corrPval = Correlation.Pearson(xWindow, yWindow);
                corr.Add(corrPval);
                //p_val[i] = corrPval.Item2;
            }
            return corr;
        }

        public static PropertyInfo[] GetProperties<T>(T obj) 
        {
            // typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);  Didnt work for GreeksPlus & PLExplain
            if (obj is QCAlgorithm) {
                return new PropertyInfo[0];
            }
            return obj == null ? new PropertyInfo[0] : obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
        }

        public static double[] LogReturns(double[] prices)
        {
            // Calculate the log return of a price series
            var logReturns = new double[prices.Length - 1];
            for (int i = 0; i < prices.Length - 1; i++)
            {
                logReturns[i] = Math.Log(prices[i + 1] / prices[i]);
            }
            return logReturns;
        }

        public static HashSet<string> ObjectsToHeaderNames<T>(T obj, string prefix = "")
        {
            if (obj == null || obj is QCAlgorithm)
            {
                return new HashSet<string>();
            }
            if (obj.GetType() == typeof(Dictionary<Symbol, double>))
            {
                var dict = (Dictionary<Symbol, double>)(object)obj;
                var header = new HashSet<string>();
                foreach (var key in dict.Keys)
                {
                    header.Add(prefix + obj.GetType().Name + key.Value);
                }
                return header;
            }
            else
            {
                var properties = GetProperties(obj);
                var header = new HashSet<string>();
                foreach (var prop in properties)
                {
                    if (PrimitiveTypes.Contains(prop.PropertyType) || ToStringTypes.Contains(prop.PropertyType))
                    {
                        header.Add(prefix + prop.Name);
                    }
                    else
                    {
                        object? val;
                        try
                        {
                            val = prop.GetValue(obj);
                        }
                        catch (Exception e)
                        {
                            Logging.Log.Debug(e.ToString());
                            val = null;
                        }                        
                        header.UnionWith(ObjectsToHeaderNames(val, prefix: prefix + prop.Name + "."));
                    }
                }
                return header;
            }
        }
        public static Symbol Underlying(Symbol symbol)
        {
            return symbol.SecurityType switch
            {
                SecurityType.Option => symbol.ID.Underlying.Symbol,
                SecurityType.Equity => symbol,
                _ => throw new NotImplementedException(),
            };
        }

        public static string ValueFromPropSequence<T>(T obj, string[] propSequence)
        {
            object val = null;
            for (int i = 0; i < propSequence.Length; i++)
            {
                if (i == 0)
                {
                    if (obj.GetType() == typeof(Dictionary<Symbol, double>))
                    {
                        return "";
                    }
                    else
                    {
                        try
                        {
                            val = obj.GetType().GetProperty(propSequence[i]).GetValue(obj);
                        }
                        catch
                        {
                            Logging.Log.Error($"ValueFromPropSequence. Failed to get property for {obj} in {string.Join(",", propSequence)}.");
                            return "";
                        }
                    }
                }
                else
                {
                    if (val == null || val.GetType() == typeof(Dictionary<Symbol, double>))
                    {
                        return "";
                    }
                    else
                    {
                        try
                        {
                            val = val.GetType().GetProperty(propSequence[i]).GetValue(val);
                        }
                        catch
                        {
                            Logging.Log.Error($"ValueFromPropSequence. Failed to get property for {obj} in {string.Join(",", propSequence)}.");
                            return "";
                        }
                    }                    
                }
            }
            string value = val?.ToString() ?? "";
            value = value.Contains(",") ? "\"" + value + "\"" : value;
            return value;
        }

        public static string ToCsv<T>(IEnumerable<T> objects, List<string>? header = null, bool skipHeader = false)
        {
            if (objects == null || !objects.Any()) return "";
            
            List<string> headerNames = header ?? objects.ToList().SelectMany(x => ObjectsToHeaderNames(x)).Distinct().OrderBy(x => x).ToList();

            var csv = new StringBuilder();
            if (!skipHeader)
            {
                csv.AppendLine(string.Join(",", headerNames));
            }

            var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

            foreach (var obj in objects)
            {
                if (obj == null) continue;
                csv.AppendLine(string.Join(",", headerNames.Select(name => ValueFromPropSequence(obj, name.Split(".")))));
            }
            return csv.ToString();
        }

        public static void ExportToCsv<T>(IEnumerable<T> objects, string filePath)
        {
            var fileExists = File.Exists(filePath);
            // If our file doesn't exist its possible the directory doesn't exist, make sure at least the directory exists
            if (!fileExists)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            }
            File.WriteAllText(filePath, ToCsv(objects));
        }

        public static Func<TArgs, TResult> Cache<TCacheKey, TArgs, TResult>(Func<TArgs, TResult> decorated, Func<TArgs, TCacheKey> genCacheKey)
        {
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return args =>
            {
                var key = genCacheKey(args);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(args);
                cache[key] = result;
                return result;
            };
        }
        public static Func<TArg1, TArg2, TResult> Cache<TCacheKey, TArg1, TArg2, TResult>(Func<TArg1, TArg2, TResult> decorated, Func<TArg1, TArg2, TCacheKey> genCacheKey)
        {
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2) =>
            {
                var key = genCacheKey(arg1, arg2);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2);
                cache[key] = result;
                return result;
            };
        }

        public static Func<TArg1, TArg2, TArg3, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TResult>(Func<TArg1, TArg2, TArg3, TResult> decorated, Func<TArg1, TArg2, TArg3, TCacheKey> genCacheKey)
        {
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3) =>
            {
                var key = genCacheKey(arg1, arg2, arg3);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3);
                cache[key] = result;
                return result;
            };
        }

        public static Func<TArg1, TArg2, TArg3, TArg4, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TArg4, TResult>(Func<TArg1, TArg2, TArg3, TArg4, TResult> decorated, Func<TArg1, TArg2, TArg3, TArg4, TCacheKey> genCacheKey)
        {
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3, arg4) =>
            {
                var key = genCacheKey(arg1, arg2, arg3, arg4);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3, arg4);
                cache[key] = result;
                return result;
            };
        }

        public static Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult> Cache<TCacheKey, TArg1, TArg2, TArg3, TArg4, TArg5, TResult>(Func<TArg1, TArg2, TArg3, TArg4, TArg5, TResult> decorated, Func<TArg1, TArg2, TArg3, TArg4, TArg5, TCacheKey> genCacheKey)
        {
            var cache = new ConcurrentDictionary<TCacheKey, TResult>();
            return (arg1, arg2, arg3, arg4, arg5) =>
            {
                var key = genCacheKey(arg1, arg2, arg3, arg4, arg5);
                if (cache.TryGetValue(key, out var result))
                {
                    return result;
                }
                result = decorated(arg1, arg2, arg3, arg4, arg5);
                cache[key] = result;
                return result;
            };
        }
    }
}
