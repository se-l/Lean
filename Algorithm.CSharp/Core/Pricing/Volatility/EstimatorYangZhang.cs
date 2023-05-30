using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Securities.Volatility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing.Volatility
{
    public class EstimatorYangZhang : IVolatilityModel
    {
        // https://github.com/QuantConnect/Lean/blob/master/Algorithm.Python/CustomVolatilityModelAlgorithm.py
        public decimal Volatility { get; set; } = 0m;

        private readonly int _windowSize;
        private readonly RollingWindow<TradeBar> _tradeBars;
        private readonly Resolution? _resolution;

        public EstimatorYangZhang(int periods, Resolution? resolution=Resolution.Daily)
        {            
            _windowSize = periods;
            _tradeBars = new RollingWindow<TradeBar>(periods + 1);
            // https://www.quantconnect.com/docs/v2/writing-algorithms/indicators/manual-indicators
            _resolution = resolution;
        }

        // Updates this model using the new price information in the specified security instance
        // Update is a mandatory method
        public void Update(Security security, BaseData data)
        {
            if (!(data is TradeBar tradeBar)) return;
            if (_resolution == Resolution.Daily && _tradeBars.Count > 0 && tradeBar.EndTime.Date <= _tradeBars[0].EndTime.Date) return;

            _tradeBars.Add(tradeBar);

            if (_tradeBars.Count < _windowSize + 1) return;

            var logReturns = new List<decimal>();
            var prevClose = _tradeBars[0].Close;
            for (int i = 1; i < _tradeBars.Count; i++)
            {
                var currClose = _tradeBars[i].Close;
                logReturns.Add((decimal)Math.Log((double)(currClose / prevClose)));
                prevClose = currClose;
            }

            var n = logReturns.Count;
            var sum1 = logReturns.Skip(1).Take(n - 1).Aggregate(0m, (acc, x) => acc + x * x);
            var sum2 = logReturns.Take(n - 1).Zip(logReturns.Skip(1), (x, y) => x * y)
                                       .Aggregate(0m, (acc, x) => acc + x);
            var sum3 = logReturns.Skip(1).Take(n - 2).Zip(logReturns.Take(n - 2), (x, y) => x * y)
                                       .Aggregate(0m, (acc, x) => acc + x);
            var alpha = 0.34m / (1 + (_windowSize + 1) / (_windowSize - 1));
            var beta = 1 - alpha;
            var sigma2 = alpha * (sum1 - 2 * beta * sum2 + beta * beta * sum3);
            try
            {
                Volatility = (decimal)Math.Sqrt((double)(sigma2 * 252m));
            } catch (OverflowException e)
            {
                Console.WriteLine($"{security.Symbol}-{e.Message} - Setting Volatility to 0.");
                if (sigma2 * 252m < 0)
                {
                    Volatility = 0m;
                }
            }
        }

        // Returns history requirements for the volatility model expressed in the form of history request
        // GetHistoryRequirements is a mandatory method
        public IEnumerable<HistoryRequest> GetHistoryRequirements(Security security, DateTime utcTime)
        // For simplicity's sake, we will not set a history requirement
        {
            return Enumerable.Empty<HistoryRequest>();
        }
    }
}
