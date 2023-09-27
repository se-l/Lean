using System;
using System.IO;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using System.Collections.Generic;

namespace QuantConnect.Algorithm.CSharp.Core.Risk
{
    // The CSV part is quite messy. Improve
    public class Utility : IDisposable
    {
        private readonly string _path;
        private readonly StreamWriter _writer;
        private bool _headerWritten;
        public Utility()
        {
            _path = Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "UtilityOrder.csv");
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
            }
            _writer = new StreamWriter(_path, true);
        }
        public void Write(UtilityOrder utility)
        {
            if (!_headerWritten)
            {
                _writer.WriteLine(utility.CsvHeader());
                _headerWritten = true;
            }
            _writer.WriteLine(utility.CsvRow());
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Close();
            _writer.Dispose();
        }
    }
    /// <summary>
    /// All Utilities in USD. Weighs easy-to-measure profit opportunities vs. uncertain Risk/Opportunities. Turning Risk into a USD estimate will be challenging and requires frequent review.
    /// Each public instance attribute is exported to CSV if exported. Methods wont be.
    /// Probably needs to be coupled more with Pricer to avoid unneccessary re-calculations.
    /// A utility to me translates into a price. But some opportunities/risk may me cheaper than others - that's for the pricer to optimize.
    /// </summary>
    public class UtilityOrder
    {
        // Constructor
        private readonly Foundations _algo;
        public Option Option { get; internal set; }
        public decimal Quantity { get; internal set; }
        public Symbol Symbol { get => Option.Symbol; }
        public DateTime Time { get; internal set; }
        public OrderDirection OrderDirection { get; internal set; }
        private decimal Multiplier { get => Option.ContractMultiplier; }

        public double Utility { get => UtilityProfit + UtilityRisk; }
        public double UtilityProfit
        {
            get =>
            //UtilityProfitSpread +
            //UtilityProfitVega +
            IntradayVolatilityRisk;
        }
        public double UtilityRisk
        {
            get =>
                UtilityRiskInventory +
                UtilityRiskExpiry +
                QuoteDiscounts;
        }

        private double? _utilityProfitSpread;
        private double UtilityProfitSpread { get => _utilityProfitSpread ??= GetUtilityProfitSpread(); }
        private double? _utilityProfitVega;
        private double UtilityProfitVega { get => _utilityProfitVega ??= GetUtilityProfitVega(); }

        private double? _intradayVolatilityRisk;
        public double IntradayVolatilityRisk { get => _intradayVolatilityRisk ??= GetIntradayVolatilityRisk(); }

        private double? _utilityRiskInventory;
        private double UtilityRiskInventory { get => _utilityRiskInventory ??= GetUtilityRiskInventory(); }
        private double? _utilityRiskExpiry;
        private double UtilityRiskExpiry { get => _utilityRiskExpiry ??= GetUtilityRiskExpiry(); }
        private double? _quoteDiscounts;
        public double QuoteDiscounts { get => _quoteDiscounts ??= GetQuoteDiscounts(); }

        public UtilityOrder(Foundations algo, Option option, decimal quantity)
        {
            _algo = algo;
            Option = option;
            Quantity = quantity;
            Time = _algo.Time;
            OrderDirection = Num2Direction(Quantity);

            // Calling Utility to snap the risk => cached for future use.
            _ = Utility;
        }

        /// <summary>
        /// Don't really want to increase inventory. Hard to Quantity. Attach price tag of 50...
        /// </summary>
        private double GetUtilityRiskInventory()
        {
            return _algo.Portfolio[Symbol].Quantity * Quantity > 0 ? -30 * (double)Quantity : 0;
        }
        /// <summary>
        /// To be refactored - these discounts are in the pricing function. There, they lower the price. Here, the reversed. Whatever we give discount for is because we want to sell or buy it.
        /// Pricing may not necessarily wanna impact the utility. The discounts is always smaller than the utility.
        /// </summary>
        private double GetQuoteDiscounts()
        {
            var quoteDiscounts = _algo.GetQuoteDiscounts(new QuoteRequest<Option>(Option, Quantity));
            var discount = (1 - quoteDiscounts.Sum(qd => qd.SpreadFactor));  // Whatever is discounted is because it's an opportunity or risk reduction, hence positive utility.
            return discount * (double)(Quantity * Multiplier);
        }
        /// <summary>
        /// Dont buy stuff about to expire. But that should be quantified. A risk is underlying moving after market close.
        /// </summary>
        private double GetUtilityRiskExpiry()
        {
            return OrderDirection == OrderDirection.Buy && (Option.Symbol.ID.Date - _algo.Time.Date).Days <= 1 ? -10 * (double)Quantity : 0;
        }

        /// <summary>
        /// Sell AM, Buy PM.
        /// </summary>
        private double GetIntradayVolatilityRisk()
        {
            if (_intradayVolatilityRisk != null) return (double)_intradayVolatilityRisk;

            if (_algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].Direction().Length > 1) return 0;

            double intraDayIVSlope = _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].IntraDayIVSlope;
            double fractionOfDayRemaining = 1 - _algo.IntradayIVDirectionIndicators[Option.Underlying.Symbol].FractionOfDay(_algo.Time);
            return OptionContractWrap.E(_algo, Option, _algo.Time.Date).Vega() * intraDayIVSlope * fractionOfDayRemaining * (double)(Quantity * Option.ContractMultiplier);
        }

        private double GetUtilityProfitSpread()
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
        private double GetUtilityProfitVega()
        {
            Symbol underlying = Symbol.Underlying;

            double IVEWMA = OrderDirection switch
            {
                OrderDirection.Buy => _algo.IVSurfaceRelativeStrikeBid.ContainsKey(underlying) ? _algo.IVSurfaceRelativeStrikeBid[underlying].IV(Symbol) : 0,
                OrderDirection.Sell => _algo.IVSurfaceRelativeStrikeAsk.ContainsKey(underlying) ? _algo.IVSurfaceRelativeStrikeAsk[underlying].IV(Symbol) : 0,
                _ => throw new ArgumentException($"AdjustPriceForMarket: Unknown order direction {OrderDirection}")
            } ?? 0;
            if (IVEWMA == 0) { return 0; }

            double iv = OrderDirection switch
            {
                OrderDirection.Buy => _algo.IVBids[Symbol].IVBidAsk.IV,
                OrderDirection.Sell => _algo.IVAsks[Symbol].IVBidAsk.IV,
                _ => 0
            };
            if (iv == 0) { return 0; }

            var ocw = OptionContractWrap.E(_algo, Option, _algo.Time.Date);
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
        private List<string>? _header;
        public List<string> CsvHeader() => _header ??= ObjectsToHeaderNames(this).OrderBy(x => x).ToList();
        public string CsvRow() => ToCsv(new[] { this }, _header, skipHeader: true);
        public void Export()
        {
            _algo.Utility.Write(this);
        }
    }
}
