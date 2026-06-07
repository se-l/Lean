using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Collections.Generic;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        internal void SetPricingStrategies()
        {
            Cfg.Ticker.DoForEach(s => SetPricingStrategy(s));
        }

        internal void SetPricingStrategy(Symbol underlying)
        {
            PricingStrategy[underlying] = GetPricingStrategy(underlying);
        }

        private Func<QuoteRequest<Option>, decimal> GetPricingStrategy(Symbol underlying)
        {
            // Set the pricing strategy based on the market regime
            HashSet<MarketRegime> regimes = ActiveRegimes.TryGetValue(ToEquity(underlying), out regimes) ? regimes : new HashSet<MarketRegime>();

            if (regimes.Contains(MarketRegime.PreEarningsRelease)) { return GetPricePreEarningsReleasePricerKalman; }
            if (regimes.Contains(MarketRegime.PreEarningsReleaseBeforeMarketClose)) { return GetPricePreEarningsReleaseBeforeMarketClose; }
            if (regimes.Contains(MarketRegime.PostEarningsRelease)) { return GetPricePostEarningsReleasePricer; }
            return GetPriceNoTraderPricer;
        }

        private static decimal GetPriceNoTraderPricer(QuoteRequest<Option> qr)
        {
            return 0;
        }

        private decimal GetPricePreEarningsReleasePricerKalman(QuoteRequest<Option> qr)
        {
            if (qr == null || IsUtilityGtMin(qr)) return 0;

            // Should be replaced with a sweep that is anchored on the option with best utility. Hence quote all option with equal utility. That'll improve
            // chances on arriving at the most profitbale scenario.
            double? iv = RiskScenarioHandler.SweepIv(qr.Option, qr.OrderDirection);
            if ((iv ?? 0) == 0 || !double.IsFinite(iv ?? 0))
            {
                // No risk scenario, no price.
                return 0;
            }
            // Convert IV to a price
            double priceSweepRaw = OptionContractWrap.E(this, qr.Option, Time.Date).NPV((double)iv, MidPrice(qr.Option.Underlying.Symbol));
            if (!double.IsFinite(priceSweepRaw))
                return 0;
            decimal priceSweep = (decimal)priceSweepRaw;

            decimal kfPrice = GetKalmanQuote(qr) ?? 0;
            decimal priceAggressive = TakeAggressivePrice(qr.OrderDirection, kfPrice, priceSweep);
            decimal price = priceAggressive;

            decimal bid = qr.Option.BidPrice;
            decimal ask = qr.Option.AskPrice;
            decimal spread = ask - bid;

            decimal aggressiveCrossRatio = 0;
            if (spread > 0)
            {
                aggressiveCrossRatio = qr.OrderDirection switch
                {
                    OrderDirection.Buy => (priceAggressive - bid) / spread,
                    OrderDirection.Sell => (ask - priceAggressive) / spread,
                    _ => 0
                };
            }

            if (IsPricerOverridePricesWithPresumedIvFillDefensively(qr.Underlying.Value))
            {
                price = TakeDefensivePrice(qr.OrderDirection, PriceModelPresumedFill(qr) ?? price, price);
            }

            decimal priceBeforeBboCap = price;
            price = LimitPriceToBBO(qr, price);

            decimal finalCrossRatio = 0;
            if (spread > 0)
            {
                finalCrossRatio = qr.OrderDirection switch
                {
                    OrderDirection.Buy => (price - bid) / spread,
                    OrderDirection.Sell => (ask - price) / spread,
                    _ => 0
                };
            }

            Log($"{Time} GetPricePreEarningsReleasePricerKalman(): symbol={qr.Option.Symbol.Value}, direction={qr.OrderDirection}, utility={qr.UtilityOrder.Utility:0.00}, sweepIV={iv:0.000}, sweepPrice={priceSweep:0.0000}, kfPrice={kfPrice:0.0000}, aggressivePrice={priceAggressive:0.0000}, preBboPrice={priceBeforeBboCap:0.0000}, finalPrice={price:0.0000}, bid={bid:0.0000}, ask={ask:0.0000}, spread={spread:0.0000}, aggressiveCrossRatio={aggressiveCrossRatio:0.000}, finalCrossRatio={finalCrossRatio:0.000}");
            price = BufferPriceCrossingSpread(qr, price);
            price = LimitPriceToBBO(qr, price);
            // price = LimitMaxSpreadDiscount(qr, price);

            return price;
        }

        /// <summary>
        /// Relies on pfRiskScenario to provide a ranking of options
        /// </summary>
        private decimal GetPricePreEarningsReleaseBeforeMarketClose(QuoteRequest<Option> qr)
        {
            // Check if sweep is already underway
            double? iv = RiskScenarioHandler.SweepIv(qr.Option, qr.OrderDirection);
            if ((iv ?? 0) == 0)
            {
                // No risk scenario, no price.
                return 0;
            }
            // Convert IV to a price
            double priceRaw = OptionContractWrap.E(this, qr.Option, Time.Date).NPV((double)iv, MidPrice(qr.Option.Underlying.Symbol));
            if (!double.IsFinite(priceRaw))
                return 0;
            decimal price = (decimal)priceRaw;
            price = LimitPriceToBBO(qr, price);

            return price;
        }


        private decimal GetPricePostEarningsReleasePricer(QuoteRequest<Option> qr)
        {
            double? iv = RiskScenarioHandler.SweepIv(qr.Option, qr.OrderDirection);
            double npvRaw = (iv != 0 && iv != null) ? OptionContractWrap.E(this, qr.Option, Time.Date).NPV((double)iv, MidPrice(qr.Option.Underlying.Symbol)) : 0;
            decimal priceSweep = double.IsFinite(npvRaw) ? (decimal)npvRaw : 0;
            
            // Convert IV to a price
            decimal kfPrice = GetKalmanQuote(qr) ?? 0;
            
            decimal price = TakeAggressivePrice(qr.OrderDirection, priceSweep, kfPrice);

            price = LimitPriceToBBO(qr, price);
            //price = BufferPriceCrossingSpread(qr, price);

            return price;
        }

        private TimeSpan TimeStartKfBeforeRelease(string underlying)
        {
            try
            {
                return AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfBeforeRelease, underlying));
            }
            catch (Exception e)
            {
                Error($"{Time} GetQuote: {e.Message}");
                Log(Environment.StackTrace);
                return new TimeSpan(0, 23, 0, 0);
            }
        }

        private decimal? PriceModelPresumedFill(QuoteRequest<Option> qr)
        {
            return PresumedFillIV.ContainsKey(qr.Option) ? new PresumedFillMetrics(qr, this).DiscountedPrice : null;
        }

        private static decimal TakeDefensivePrice(OrderDirection direction, params decimal[] prices)
        {
            decimal[] okPrices = prices.Where(p => p != 0).ToArray();
            return direction switch
            {
                OrderDirection.Buy => okPrices.Min(),
                OrderDirection.Sell => okPrices.Max(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }

        private static decimal TakeAggressivePrice(OrderDirection direction, params decimal[] prices)
        {
            decimal[] okPrices = prices.Where(p => p != 0).ToArray();
            return direction switch
            {
                OrderDirection.Buy => okPrices.Max(),
                OrderDirection.Sell => okPrices.Min(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }
        /// <summary>
        /// // Defensive: IV Model price override
        /// Somewhat temporary and to be refactored. Limit the price to the presumedFillIV coming from the model - a discount dependent on the utility.
        /// Essentially, both KalmanFilter price and this PresumedIV-utility based price must be good enough to offer competitive quotes.
        /// </summary>
        private bool IsPricerOverridePricesWithPresumedIvFillDefensively(string underlying)
        {
            return Cfg.PricerOverridePricesWithPresumedIvFillDefensively.TryGetValue(underlying, out bool pricerOverridePricesWithPresumedIvFillDefensively) ? pricerOverridePricesWithPresumedIvFillDefensively : Cfg.PricerOverridePricesWithPresumedIvFillDefensively[CfgDefault];
        }

        private bool IsUtilityGtMin(QuoteRequest<Option> qr)
        {
            double minUtility = Cfg.MinUtility.TryGetValue(qr.Underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
            if (qr.UtilityOrder.Utility < minUtility)
            {
                Log($"{Time} GetQuote: UtilityHigh not anymore greater minUtil => Quoting Price 0. utilityOrderHigh={qr.UtilityOrder.Utility}. utilityOrderLowCrossSpread={qr.UtilityOrder.Utility}. QuoteRequest Util: {qr.UtilityOrder.Utility}");
            }
            return qr.UtilityOrder.Utility < minUtility;
        }

        private decimal BufferPriceCrossingSpread(QuoteRequest<Option> qr, decimal price)
        {
            // Shouldn't just go by ticket. Imagine it's cancelled and first new submission is crossing much of the spread. Would wanna buffer that too!
            if (Cfg.BufferIntraSpreadQuotes && SpreadBuffers[qr.OrderDirection].TryGetValue(qr.Symbol, out SpreadBuffer sp))
            {
                price = sp.BufferIntraSpreadQuote(price);
            }
            else if (Cfg.BufferIntraSpreadQuotes)
            {
                Error($"{Time} GetQuote: BufferIntraSpreadQuotes is true, but no SpreadBuffer found for {qr.Symbol}. Expected to be instantiated in SecurityInitializer.");
            }
            return price;
        }

        /// <summary>
        /// // Don't hit deep order book wasting money.
        /// </summary>
        private static decimal LimitPriceToBBO(QuoteRequest<Option> qr, decimal price)
        {
            return qr.OrderDirection switch
            {
                OrderDirection.Buy => Math.Min(price, qr.Option.AskPrice),
                OrderDirection.Sell => Math.Max(price, qr.Option.BidPrice),
                OrderDirection.Hold => throw new ArgumentException($"Unsupported order direction {qr.OrderDirection}"),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
        }

        private decimal? GetKalmanQuote(QuoteRequest<Option> qr)
        {
            if (!IvSurfaceSsviMid.TryGetValue((Equity)Securities[qr.Underlying], out IIVSurface ivs))
            {
                Log($"{Time} GetKalmanQuote(): {qr.Underlying} No KalmanFilter found for {qr.Underlying}");
                return null;
            }

            if (!ivs.IsCalibrated || !ivs.HasParams(qr.Option)) return null;

            double modelIv = ivs.IV(qr.Option);
            decimal ivMeanSpread = IVSpreadSMA[qr.Option.Symbol].Current.Value;
            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            double kfPriceMid = qr.OrderDirection switch
            {
                OrderDirection.Buy => ocw.NPV(modelIv, null),
                OrderDirection.Sell => ocw.NPV(modelIv, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            //kfPriceWSpread = qr.OrderDirection switch
            //{
            //    OrderDirection.Buy => (decimal)ocw.NPV(modelIV - ((double)ivMeanSpread) / 2, null),
            //    OrderDirection.Sell => (decimal)ocw.NPV(modelIV + ((double)ivMeanSpread) / 2, null),
            //    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            //};
            double kfPriceWSpread = qr.OrderDirection switch
            {
                OrderDirection.Buy => ocw.NPV(modelIv, null),
                OrderDirection.Sell => ocw.NPV(modelIv, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            Log($"{Time} GetKalmanQuote(): direction={qr.OrderDirection} option={qr.Option}, kfPriceMid={kfPriceMid:0.0000}, kfModelIV={modelIv:0.00}, kfPriceWSpread={kfPriceWSpread:0.0000}, bidPrice={qr.Option.BidPrice}, bidIV={IvBids[qr.Option.Symbol].IVBidAsk.IV:0.00}, askPrice={qr.Option.AskPrice}, askIV={IvAsks[qr.Option.Symbol].IVBidAsk.IV:0.00}, IVMeanSpread={ivMeanSpread:0.00}, spot={MidPrice(qr.Option.Underlying.Symbol)}");
            return (decimal?)kfPriceWSpread;
        }


        internal Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            
            decimal price = PricingStrategy[qr.Underlying](qr);

            // Defensive rounding and adjustments
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection == OrderDirection.Sell);
            double ivPrice = OptionContractWrap.E(this, qr.Option, Time.Date).IV(price, MidPrice(qr.Symbol.Underlying), 0.001);
            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, qr.UtilityOrder, null, 0);
        }
    }
}
