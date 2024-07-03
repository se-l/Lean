using Fasterflect;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;
using System.Globalization;

namespace QuantConnect.Algorithm.CSharp.Core
{
    //public static bool HasProperty(this object obj, string propertyName)
    //{
    //    return obj.GetType().GetProperty(propertyName) != null;
    //}

    public class AlgoConfig
    {
        public const string CfgDefault = "_";
        public void OverrideWithEnvironmentVariables<T>()
        {
            // Loop over all getter attribuetes
            foreach (var attr in typeof(T).GetProperties())
            {
                var envValue = Environment.GetEnvironmentVariable(attr.Name);

                if (envValue != null)
                {
                    if (attr.PropertyType == typeof(List<string>))
                    {
                        List<string> convertedValue = envValue.Split(",").ToList();
                        attr.SetValue(this, convertedValue);
                    }
                    else if (attr.PropertyType == typeof(HashSet<string>))
                    {
                        HashSet<string> convertedValue = envValue.Split(",").ToHashSet();
                        attr.SetValue(this, convertedValue);
                    }
                    else if (attr.PropertyType.GenericTypeArguments.Length > 0 && attr.PropertyType?.GetGenericTypeDefinition() == typeof(Dictionary<,>))
                    {
                        string jsonString = envValue.Replace("{", "{\"").Replace(":", "\":").Replace(",", ",\"");

                        Log.Trace($"AlgoConfig.OverrideWithEnvironmentVariables Dictionary: {attr.Name}: {envValue}     jsonString:  {jsonString}");
                        var convertedValue = JsonConvert.DeserializeObject(jsonString, attr.PropertyType);
                        attr.SetValue(this, convertedValue);
                    }
                    else
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        attr.SetValue(this, convertedValue);

                    }
                    Log.Trace($"OverrideWithEnvironmentVariables: {typeof(T)}, {attr.Name}: {envValue}");
                }
            }
        }        

        public void OverrideWith<T>(T other) where T : AlgoConfig
        {
            // Loop over all getter attributes
            foreach (var otherAttr in typeof(T).GetProperties().Where(otherAttr => this.GetType().GetProperty(otherAttr.Name) != null))
            {
                try
                {
                    this.SetPropertyValue(otherAttr.Name, otherAttr.GetValue(other));
                    Log.Trace($"OverrideWith: {otherAttr.Name}: {otherAttr}");
                }
                catch (Exception e)
                {
                    Log.Error($"OverrideWith: {otherAttr.Name}: {e.Message}");
                }
            }

        }

        public static TimeSpan GetTimeSpan(List<int> list)
        {
            if (list == null)
            {
                return TimeSpan.Zero;
            }
            return list.Count switch
            {
                1 => new TimeSpan(list[0]),
                2 => TimeSpan.Zero,
                3 => new TimeSpan(list[0], list[1], list[2]),
                4 => new TimeSpan(list[0], list[1], list[2], list[3]),
                5 => new TimeSpan(list[0], list[1], list[2], list[3], list[4]),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        public static TimeSpan GetTimeSpan(string timeString)
        {
            if (timeString == null)
            {
                return TimeSpan.Zero;
            }        
            return TimeSpan.ParseExact(timeString, "g", CultureInfo.InvariantCulture);
        }

        public static T GetEntry<T>(Dictionary<string, T> dict, string key)
        {
            if (dict.ContainsKey(key))
            {
                return dict[key];
            }
            return dict[CfgDefault];
        }
    }
}
