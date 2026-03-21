namespace QuantConnect.Algorithm.CSharp.Core;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using QuantConnect;

public class MyDividend
{
    public string Ticker { get; set; }
    public DateTime ExDate { get; set; }
    public decimal Amount { get; set; }
    public bool Predicted { get; set; }

    public override string ToString() => 
        $"{ExDate:yyyy-MM-dd} | {Ticker} | {Amount:F4} | Predicted: {Predicted}";
}

/// <summary>
/// Reads cash dividend .csv files from {Globals.DataFolder}/equity/usa/dividends/{symbol}.csv
/// </summary>
public class DividendManager
{
    private string GetDividendsCsvPath(string ticker)
    {
        // Custom derivation of path as requested
        var dir = Path.Combine(Globals.DataFolder, "equity", "usa", "dividends");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{ticker.ToUpper()}.csv");
    }

    private void WriteDividendsCsv(string ticker, List<MyDividend> dividends)
    {
        var path = GetDividendsCsvPath(ticker);
        var lines = new List<string> { "ExDate,Amount,Ccy" };
        
        var sorted = dividends.OrderBy(d => d.ExDate);
        foreach (var d in sorted)
        {
            lines.Add($"{d.ExDate:yyyyMMdd},{d.Amount:F6},USD");
        }
        File.WriteAllLines(path, lines);
    }

    private List<MyDividend> ReadDividendsCsv(string ticker)
    {
        var path = GetDividendsCsvPath(ticker);
        if (!File.Exists(path)) return null;

        var results = new List<MyDividend>();
        var lines = File.ReadAllLines(path);
        
        // Skip header
        foreach (var line in lines.Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;

            if (DateTime.TryParseExact(parts[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                decimal.TryParse(parts[1], out var amount))
            {
                results.Add(new MyDividend { Ticker = ticker.ToUpper(), ExDate = date, Amount = amount });
            }
        }
        return results;
    }

    public List<MyDividend> GetDividends(string ticker, DateTime start, DateTime? end = null)
    {
        var endDate = end ?? start.AddYears(3);
        
        // Try load from disk
        var history = ReadDividendsCsv(ticker);

        var today = DateTime.Now.Date;
        var inRangeHist = history
            .Where(d => d.ExDate >= start && d.ExDate <= (endDate < today ? endDate : today))
            .ToList();

        var forecasts = ForecastDividends(ticker, history, endDate);
        var inRangeForecasts = forecasts.Where(d => d.ExDate >= start && d.ExDate <= endDate);

        return inRangeHist.Concat(inRangeForecasts).OrderBy(d => d.ExDate).ToList();
    }

    private List<MyDividend> ForecastDividends(string ticker, List<MyDividend> history, DateTime until)
    {
        if (history == null || !history.Any()) return new List<MyDividend>();

        var today = DateTime.Now.Date;
        var hist = history.Where(d => d.ExDate <= today).OrderBy(d => d.ExDate).ToList();
        if (!hist.Any()) return new List<MyDividend>();

        var last = hist.Last();
        int months = InferFrequencyMonths(hist.Select(d => d.ExDate).TakeLast(12).ToList());
        
        var forecast = new List<MyDividend>();
        var nextDate = AddMonths(last.ExDate, months);
        var horizon = until < today.AddDays(365.25 * 2) ? until : today.AddDays(365.25 * 2);

        while (nextDate <= horizon)
        {
            double years = (nextDate - last.ExDate).TotalDays / 365.25;
            // 5% Annual growth
            decimal amt = last.Amount * (decimal)Math.Pow(1.05, years);
            
            forecast.Add(new MyDividend { 
                Ticker = ticker.ToUpper(), 
                ExDate = nextDate, 
                Amount = amt, 
                Predicted = true 
            });
            nextDate = AddMonths(nextDate, months);
        }
        return forecast;
    }

    private int InferFrequencyMonths(List<DateTime> exDates)
    {
        if (exDates.Count < 2) return 3;

        var sorted = exDates.OrderBy(d => d).ToList();
        var deltas = new List<double>();
        for (int i = 1; i < sorted.Count; i++)
        {
            deltas.Add((sorted[i] - sorted[i - 1]).TotalDays);
        }

        var medianDays = deltas.OrderBy(d => d).ElementAt(deltas.Count / 2);
        var candidates = new Dictionary<int, int> { { 1, 30 }, { 2, 60 }, { 3, 91 }, { 4, 121 }, { 6, 182 }, { 12, 365 } };

        return candidates.OrderBy(c => Math.Abs(c.Value - medianDays)).First().Key;
    }

    private DateTime AddMonths(DateTime d, int months)
    {
        try { return d.AddMonths(months); }
        catch { return d.AddDays(months * 30); } // Fallback for edge cases
    }
}
