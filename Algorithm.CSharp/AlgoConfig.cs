using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp
{
    public class AlgoConfig
    {
        public void OverrideWithEnvironmentVariables<T>()
        {
            // Loop over all getter attribuetes
            foreach (var attr in typeof(T).GetProperties())
            {
                // Get the value of the environment variable
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
                    else
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        attr.SetValue(this, convertedValue);
                        
                    }
                    Console.WriteLine($"{typeof(T)}: Overriding {attr.Name} with {envValue}");
                }
            }
        }
    }
}
