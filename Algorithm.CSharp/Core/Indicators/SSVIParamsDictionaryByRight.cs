using QuantConnect.Algorithm.CSharp.Core.Indicators;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using MathNet.Numerics.LinearAlgebra;
using QuantConnect;



public class SSVIParamsDictionaryByRight : Dictionary<(DateTime, OptionRight), SSVIParamsRecord>
{
    public SSVIParamsDictionaryByRight() { }
    public SSVIParamsDictionaryByRight(QuantConnect.Algorithm.CSharp.Core.IO.SSVIParamsByRight[] ssviParams)
    {
        foreach (QuantConnect.Algorithm.CSharp.Core.IO.SSVIParamsByRight param in ssviParams)
        {
            OptionRight right = RightPb2Right(param.Right);
            DateTime tenorDt = DateTime.ParseExact(param.TenorDt, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            Add((tenorDt, right), new SSVIParamsRecord(param.ModelParams.Theta, param.ModelParams.Rho, param.ModelParams.Psi));
        }
    }
    public new void Add((DateTime, OptionRight) key, SSVIParamsRecord value)
    {
        base.Add(key, value);
    }
    public new SSVIParamsRecord this[(DateTime, OptionRight) key]
    {
        get => base[key];
        set => base[key] = value;
    }
    public SSVIParamsDictionaryByRight Update(SSVIParamsDictionaryByRight other)
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

    public List<KeyValuePair<(DateTime, OptionRight), SSVIParamsRecord>> GetSortedRecords()
    {
        return this.OrderBy(kvp => kvp.Key.Item1) // Sort by DateTime
                   .ThenBy(kvp => kvp.Key.Item2) // Sort by OptionRight (Call first, then Put)
                   .ToList();
    }

    /// <summary>
    /// Sorted keys
    /// </summary>
    /// <returns></returns>
    public List<(DateTime, OptionRight)> GetSortedKeys()
    {
        return GetSortedRecords().Select(kvp => kvp.Key).ToList();
    }

    public Vector<double> ToVector()
    {
        return Vector<double>.Build.DenseOfEnumerable(GetSortedRecords().SelectMany(kvp => new[] { kvp.Value.Theta, kvp.Value.Rho, kvp.Value.Psi }));
    }
    //public static Vector<double> KalmanStatesSSVIToVector(SSVIParams[] ssviParams)
    //{
    //    return Vector<double>.Build.DenseOfEnumerable(ssviParams.SelectMany(s => new[] { s.ModelParams.Theta, s.ModelParams.Rho, s.ModelParams.Psi }));
    //}
    // Tuple<DateTime, OptionRight>[] keys = initState.Select(s => Tuple.Create(DateTime.ParseExact(s.TenorDt, "yyyy-MM-dd", CultureInfo.InvariantCulture), RightPb2Right(s.Right))).ToArray();
}
