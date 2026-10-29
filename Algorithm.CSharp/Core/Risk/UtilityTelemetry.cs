using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using QuantConnect.Logging;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    /// <summary>
    /// OTLP emission for utility evaluations. Replaces the per-underlying UtilityOrder.csv dump:
    /// every public double on the utility becomes a `lean.utility.&lt;name&gt;` gauge carrying the
    /// dimensions (underlying, symbol, side, quantity) as attributes, so a run can be sliced in the
    /// backend instead of parsed from a CSV.
    /// </summary>
    public static class UtilityTelemetry
    {
        /// PropertyInfo is cached per concrete utility type: reflection on every construction (the
        /// utility loop builds thousands per bar) would otherwise dominate the emission itself.
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

        /// <summary>
        /// Emit one utility evaluation as gauges. `enabled=false` is a no-op so the whole stream can
        /// be switched off from config without touching call sites.
        /// </summary>
        public static void Emit(IUtilityOrder utility, bool enabled = true)
        {
            if (!enabled || utility == null)
            {
                return;
            }

            PropertyInfo[] properties = Properties.GetOrAdd(utility.GetType(), type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(double) && p.GetIndexParameters().Length == 0)
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToArray());

            if (properties.Length == 0)
            {
                return;
            }

            var attributes = new[]
            {
                new KeyValuePair<string, object>("lean.underlying", utility.Underlying.Value),
                new KeyValuePair<string, object>("lean.symbol", utility.Symbol.Value),
                new KeyValuePair<string, object>("side", utility.OrderDirection.ToString()),
                new KeyValuePair<string, object>("quantity", (double)utility.Quantity),
            };

            foreach (PropertyInfo property in properties)
            {
                double value;
                try
                {
                    value = (double)property.GetValue(utility);
                }
                catch
                {
                    continue; // a throwing component must not break the decision loop
                }

                OpenTelemetryLogHandler.Metric("lean.utility." + ToMetricName(property.Name), value, attributes);
            }
        }

        /// <summary>UtilityTargetHoldings -> target_holdings (dots are the metric namespace separator).</summary>
        private static string ToMetricName(string property)
        {
            string name = property.StartsWith("Utility", StringComparison.Ordinal)
                ? property.Substring("Utility".Length)
                : property;

            // Underscore on lower->upper transitions and before the last capital of an acronym run,
            // so IVUntilExpiry -> iv_until_expiry rather than i_vuntil_expiry.
            var builder = new StringBuilder(name.Length + 8);
            for (int i = 0; i < name.Length; i++)
            {
                char current = name[i];
                if (i > 0 && char.IsUpper(current))
                {
                    bool previousIsLowerOrDigit = !char.IsUpper(name[i - 1]);
                    bool nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    if (previousIsLowerOrDigit || nextIsLower)
                    {
                        builder.Append('_');
                    }
                }
                builder.Append(char.ToLowerInvariant(current));
            }
            return builder.ToString();
        }
    }
}
