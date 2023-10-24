using System;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp
{
    public class ADataRecorderConfig
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public HashSet<string> Ticker { get; set; }
        public string DataFolderOut { get; set; }

        public ADataRecorderConfig OverrideWithEnvironmentVariables()
        {
            // Loop over all getter attribuetes
            foreach (var attr in typeof(AMarketMakeOptionsAlgorithmConfig).GetProperties())
            {
                // Get the value of the environment variable
                var envValue = Environment.GetEnvironmentVariable(attr.Name);
                if (envValue != null)
                {
                    // Convert the value to the correct type
                    if (attr.PropertyType == typeof(HashSet<string>) || attr.PropertyType == typeof(List<string>))
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        var type = attr.PropertyType;
                        var convertValue = type.GetConstructor(new[] { typeof(string) });
                        // Set the value of the property
                        attr.SetValue(this, convertedValue);
                        // Log it
                        Console.WriteLine($"AMarketMakeOptionsAlgorithmConfig: Overriding {attr.Name} with {convertedValue}");
                    } else
                    {
                        var convertedValue = Convert.ChangeType(envValue, attr.PropertyType);
                        // Set the value of the property
                        attr.SetValue(this, convertedValue);
                        // Log it
                        Console.WriteLine($"AMarketMakeOptionsAlgorithmConfig: Overriding {attr.Name} with {convertedValue}");
                    }                    
                }
            }
            return this;
        }
    }
}
