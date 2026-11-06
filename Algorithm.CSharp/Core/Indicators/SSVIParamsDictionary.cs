using QuantConnect.Algorithm.CSharp.Core.Indicators;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using MathNet.Numerics.LinearAlgebra;
using QuantConnect;



public class SSVIParamsDictionary : Dictionary<DateTime, SSVIParamsRecord>
{
    public SSVIParamsDictionary() { }
    public SSVIParamsDictionary(QuantConnect.Algorithm.CSharp.Core.IO.SSVIParamsPb[] ssviParams)
    {
        foreach (QuantConnect.Algorithm.CSharp.Core.IO.SSVIParamsPb param in ssviParams)
        {
            DateTime tenorDt = DateTime.ParseExact(param.TenorDt, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Add(tenorDt, new SSVIParamsRecord(param.ModelParams.Theta, param.ModelParams.Rho, param.ModelParams.Psi));
        }
    }
    public new void Add(DateTime key, SSVIParamsRecord value)
    {
        base.Add(key, value);
    }
    public new SSVIParamsRecord this[DateTime key]
    {
        get => base[key];
        set => base[key] = value;
    }
    public SSVIParamsDictionary Update(SSVIParamsDictionary other)
    {
        /// Update only values for keys that exist in this dictionary.
        foreach (var kvp in other)
        {
            if (ContainsKey(kvp.Key))
            {
                this[kvp.Key] = kvp.Value;
            }
            else
            {
                Trace.WriteLine($"SSVIParamsDictionary.Update: Key {kvp.Key} not found in dictionary. Ignoring.");
            }
        }
        return this;
    }

    public List<KeyValuePair<DateTime, SSVIParamsRecord>> GetSortedRecords()
    {
        return this.OrderBy(kvp => kvp.Key) // Sort by DateTime
                   .ToList();
    }

    /// <summary>
    /// Sorted keys
    /// </summary>
    /// <returns></returns>
    public List<DateTime> GetSortedKeys()
    {
        return GetSortedRecords().Select(kvp => kvp.Key).ToList();
    }

    public Vector<double> ToVector(IReadOnlyList<DateTime> keys)
    {
        Vector<double> v = Vector<double>.Build.Dense(keys.Count * 3, double.NaN);
        for (int i = 0; i < keys.Count; i++)
        {
            if (!TryGetValue(keys[i], out SSVIParamsRecord p) || p == null) continue;
            v[i * 3 + 0] = p.Theta;
            v[i * 3 + 1] = p.Rho;
            v[i * 3 + 2] = p.Psi;
        }
        return v;
    }
}
