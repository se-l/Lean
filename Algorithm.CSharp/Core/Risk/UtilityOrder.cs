using System;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using System.IO;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    // The CSV part is quite messy. Improve
    public class Utility : IDisposable
    {
        private string _path;
        private readonly StreamWriter _writer;
        public Utility()
        {
            _path = Path.Combine(Directory.GetCurrentDirectory(), "UtilityOrder.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true);

            _writer.WriteLine("Time,Symbol,Quantity,OrderDirection,Utility,UtilityRisk,UtilityProfit,UtilityProfitSpread},UtilityProfitVega");
        }
        public void Write(UtilityOrder utility)
        {
            _writer.WriteLine(utility.CsvRow());
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }
    public class UtilityOrder
    {
        public Option Option;
        public Symbol Symbol { get => Option.Symbol; }
        public DateTime Time;
        public decimal Quantity;
        public OrderDirection OrderDirection;

        private readonly Foundations _algo;

        private double? _utilityProfit;
        private double? _utilityProfitSpread;
        private double? _utilityProfitVega;
        private double? _utilityRisk;

        public double Utility
        {
            // Utility functions. Not normed currently. Risk to Money and discount to a PV.
            // Excluding UtilProfit for now as it is not comparable to UtilRisk and outsized UtilRisk.
            get
            {
                //_utilityProfit ??= UtilityProfit();
                _utilityRisk ??= UtilityRisk();
                //return (double)(_utilityProfit + _utilityRisk);
                return (double)_utilityRisk;
            }
        }

        public string CsvRow()
        {
            return $"{Time},{Symbol},{Quantity},{OrderDirection},{Utility},{_utilityRisk},{_utilityProfit},{_utilityProfitSpread},{_utilityProfitVega}";
        }

        public UtilityOrder(Foundations algo, Option option, decimal quantity)
        {
            _algo = algo;
            Option = option;
            Time = algo.Time;
            Quantity = quantity;
            OrderDirection = Num2Direction(quantity);

            // Calling Utility to snap the risk. Cached for future use.
            _ = Utility;
            _algo.Utility.Write(this);
        }

        public double UtilityRisk()
        {
            // Greater 1. Good for portfolio. Risk reducing. Negative: Risk increasing.
            var _inventoryRisk = _algo.Portfolio[Symbol].Quantity * Quantity > 0 ? -100 : 0;  // for now, dont order in the same direction as position
            return
                _inventoryRisk + 
                (1 / ( 1 - _algo.SpreadFactor(Option, Quantity)));
        }

        /// <summary>
        /// Spread and IVBid/Ask - IVEWMA Bid/Ask
        /// </summary>
        public double UtilityProfit()
        {
            _utilityProfitSpread ??= UtilityProfitSpread();
            _utilityProfitVega ??= UtilityProfitVega();
            return (double)(_utilityProfitSpread + _utilityProfitVega);
        }

        public double UtilityProfitSpread()
        {
            decimal midPrice = _algo.MidPrice(Symbol);
            var util = OrderDirection switch
            {
                OrderDirection.Buy => midPrice - Option.BidPrice,
                OrderDirection.Sell => Option.AskPrice - midPrice,
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {OrderDirection}")
            };
            return (double)util / 2;
        }
        public double UtilityProfitVega()
        {
            Symbol underlying = Symbol.Underlying;

            double IVEWMA = OrderDirection switch
            {
                OrderDirection.Buy => _algo.RollingIVStrikeBid.ContainsKey(underlying) ? _algo.RollingIVStrikeBid[underlying].IV(Symbol) : 0,
                OrderDirection.Sell => _algo.RollingIVStrikeAsk.ContainsKey(underlying) ? _algo.RollingIVStrikeAsk[underlying].IV(Symbol) : 0,
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {OrderDirection}")
            } ?? 0;
            if (IVEWMA == 0) { return 0; }

            double iv = OrderDirection switch
            {
                OrderDirection.Buy => _algo.IVBids[Symbol].Current.IV,
                OrderDirection.Sell => _algo.IVAsks[Symbol].Current.IV,
                _ => 0
            };
            if (iv == 0) { return 0; }

            var ocw = OptionContractWrap.E(_algo, Option, 1);
            ocw.SetIndependents(_algo.MidPrice(underlying), _algo.MidPrice(Symbol), (double)_algo.Securities[underlying].VolatilityModel.Volatility);
            double vega = ocw.Vega();

            double util = OrderDirection switch
            {
                OrderDirection.Buy => 100 * (IVEWMA - iv) * vega * 100,
                OrderDirection.Sell => 100 * (iv - IVEWMA) * vega * 100,
                _ => 0
            };
            return util;
        }
    }
}
