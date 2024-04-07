using Fasterflect;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Logging;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public class AlgoConfig
    {
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
            foreach (var otherAttr in typeof(T).GetProperties())
            {
                try
                {
                    // check if the attribute exists in this object, this
                    this.SetPropertyValue(otherAttr.Name, otherAttr.GetValue(other));
                    Log.Trace($"OverrideWith: {otherAttr.Name}: {otherAttr}");
                }
                catch (Exception e)
                {
                    Log.Error($"OverrideWith: {otherAttr.Name}: {e.Message}");
                }
            }

        }
    }
}
