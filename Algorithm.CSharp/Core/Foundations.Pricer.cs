using QuantConnect.Algorithm.CSharp.Core.Indicators;
using QuantConnect.Algorithm.CSharp.Core.IO;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Orders;
using QuantConnect.Securities.Equity;
using QuantConnect.Securities.Option;
using QuantConnect.Util;
using System;
using System.Linq;
using static QuantConnect.Algorithm.CSharp.Core.Statics;


namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        public enum MarketRegime
        {
            PreEarningsRelease,
            PreEarningsReleaseBeforeMarketClose,
            PostEarningsRelease,
            Normal,
            NoTrade,
        }
        public interface IPricingStrategy
        {
            decimal GetPrice(QuoteRequest<Option> qr, Foundations algo);
        }

        public void SetPricingStrategies()
        {
            Cfg.Ticker.DoForEach(ticker => SetPricingStrategy(ticker));
        }
        public void SetPricingStrategy(Symbol underlying)
        {
            // Set the pricing strategy based on the market regime
            MarketRegime regime = GetMarketRegime(underlying);

            PricingStrategy[underlying] = regime switch
            {
                MarketRegime.PreEarningsRelease => new PreEarningsReleasePricerKalman(),
                MarketRegime.PreEarningsReleaseBeforeMarketClose => new PreEarningsReleaseBeforeMarketClose(),
                MarketRegime.PostEarningsRelease => new PostEarningsReleasePricer(),
                _ => new NoTraderPricer()
            };
        }

        public MarketRegime GetMarketRegime(Symbol underlying)
        {
            // Towards end of day, go delta neutral wit options only. Reduce abs. equity position. A regime, not just exterior but still..
            // bool isReducingAbsEquityPosition = IsReducingAbsEquityPosition(qr.Underlying);

            if (IsPreparingEarningsRelease(underlying))
            {
                if (TimeToMarketClose(underlying).TotalMinutes < Cfg.MinutesBeforeCloseIsPreEarningsReleaseEOD)
                {
                    return MarketRegime.PreEarningsReleaseBeforeMarketClose;
                }
                else if (Cfg.UseKalmanFilterBeforeEarningsRelease && Time.TimeOfDay >= TimeStartKfBeforeRelease(underlying.Value))
                {
                    return MarketRegime.PreEarningsRelease;
                }
                else
                {
                    return MarketRegime.PreEarningsRelease;
                }
            }
            else if (IsAfterEarningsRelease(underlying))
            {
                return MarketRegime.PostEarningsRelease;
            }            
            return MarketRegime.NoTrade;
        }

        public class NormalMarketPricer : Foundations, IPricingStrategy
        {
            public decimal GetPrice(QuoteRequest<Option> qr, Foundations algo)
            {
                return PriceSpreadDiscounted(qr);
            }
        }

        public class NoTraderPricer : Foundations, IPricingStrategy
        {
            public decimal GetPrice(QuoteRequest<Option> qr, Foundations algo)
            {
                return 0;
            }
        }

        public decimal? PriceSpreadDiscountedSweep(QuoteRequest<Option> qr)
        {
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;
            if (SweepState[qr.Symbol][qr.OrderDirection].IsSweepScheduled())
            {
                // Aggressive. Problem: Sweepratio goes up, while order never matches that ratio because overriden further below...
                // Further bad: It sweeps the priceSpread instead of IV. Impacted by others quoting aggressively quickly.
                decimal rTspreadDiscount = SpreadDiscountSweep(qr.UtilityOrder);
                return qr.OrderDirection switch
                {
                    OrderDirection.Buy => qr.Option.BidPrice + rTspreadDiscount * marketPriceSpread,
                    OrderDirection.Sell => qr.Option.AskPrice - rTspreadDiscount * marketPriceSpread,
                    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
                };
                //Log($"GetQuote: Sweeping. spreadDiscount={rTspreadDiscount}. qr.Symbol={qr.Symbol}, qr.OrderDirection={qr.OrderDirection}");
            }
            return null;
        }

        public class PreEarningsReleasePricerKalman : Foundations, IPricingStrategy
        {
            public decimal GetPrice(QuoteRequest<Option> qr, Foundations algo)
            {
                if (IsUtilityGtMin(qr)) return 0;

                decimal? priceSweepingSpread = PriceSpreadDiscountedSweep(qr);
                decimal kfPrice = GetKalmanQuote(qr) ?? 0;
                decimal price = TakeAggressivePrice(qr.OrderDirection, kfPrice, priceSweepingSpread ?? kfPrice);

                if (IsPricerOverridePricesWithPresumedIVFillDefensively(qr.Underlying.Value))
                {
                    price = TakeDefensivePrice(qr.OrderDirection, PriceModelPresumedFill(qr) ?? price, price);
                }

                price = LimitPriceToBBO(qr, price);
                price = BufferPriceCrossingSpread(qr, price);
                price = LimitMaxSpreadDiscount(qr, price);

                return price;
            }
        }

        /// <summary>
        /// Relies on pfRiskScenario to provide a ranking of options
        /// </summary>
        public class PreEarningsReleaseBeforeMarketClose : Foundations, IPricingStrategy
        {
            public decimal GetPrice(QuoteRequest<Option> qr, Foundations algo)
            {
                // Check if sweep is already underway
                double iv = RiskScenarioHandler.SweepIV(qr.Option, qr.OrderDirection);
                if (iv == 0)
                {
                    // No risk scenario, no price.
                    return 0;
                }
                // Convert IV to a price
                decimal price = (decimal)OptionContractWrap.E(algo, qr.Option, Time.Date).NPV(iv, MidPrice(qr.Option.Underlying.Symbol));

                price = LimitPriceToBBO(qr, price);

                return price;
            }
        }

        public class PostEarningsReleasePricer : Foundations, IPricingStrategy
        {
            public decimal GetPrice(QuoteRequest<Option> qr, Foundations algo)
            {
                decimal priceSpreadDiscount = PriceSpreadDiscountedSweep(qr) ?? PriceSpreadDiscounted(qr);
                
                decimal price = TakeAggressivePrice(qr.OrderDirection, priceSpreadDiscount);

                price = LimitPriceToBBO(qr, price);
                price = BufferPriceCrossingSpread(qr, price);

                return price;
            }
        }

        public TimeSpan TimeStartKfBeforeRelease(string underlying)
        {
            try
            {
                return AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfBeforeRelease, underlying));
            }
            catch (Exception e)
            {
                Error($"GetQuote: {e.Message}");
                Log(Environment.StackTrace);
                return new TimeSpan(0, 23, 0, 0);
            }
        }
        public TimeSpan TimeStartKfAfterRelease(string underlying)
        {
            try
            {
                return AlgoConfig.GetTimeSpan(AlgoConfig.GetEntry(Cfg.TimeStartKfAfterRelease, underlying));
            }
            catch (Exception e)
            {
                Error($"GetQuote: {e.Message}");
                Log(Environment.StackTrace);
                return new TimeSpan(0, 23, 0, 0);
            }
        }
        public decimal? PriceModelPresumedFill(QuoteRequest<Option> qr)
        {
            return PresumedFillIV.ContainsKey(qr.Option) ? new PresumedFillMetrics(qr, this).DiscountedPrice : null;
        }

        public decimal TakeDefensivePrice(OrderDirection direction, params decimal[] prices)
        {
            return direction switch
            {
                OrderDirection.Buy => prices.Min(),
                OrderDirection.Sell => prices.Max(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }

        //public decimal TakeAggressivePrice(OrderDirection direction, decimal price1, decimal price2)
        public decimal TakeAggressivePrice(OrderDirection direction, params decimal[] prices)
        {
            return direction switch
            {
                OrderDirection.Buy => prices.Max(),
                OrderDirection.Sell => prices.Min(),
                _ => throw new ArgumentException($"Unknown order direction {direction}")
            };
        }
        /// <summary>
        /// // Defensive: IV Model price override
        /// Somewhat temporary and to be refactored. Limit the price to the presumedFillIV coming from the model - a discount dependent on the utility.
        /// Essentially, both KalmanFilter price and this PresumedIV-utility based price must be good enough to offer competitive quotes.
        /// </summary>
        public bool IsPricerOverridePricesWithPresumedIVFillDefensively(string underlying)
        {
            return Cfg.PricerOverridePricesWithPresumedIVFillDefensively.TryGetValue(underlying, out bool pricerOverridePricesWithPresumedIVFillDefensively) ? pricerOverridePricesWithPresumedIVFillDefensively : Cfg.PricerOverridePricesWithPresumedIVFillDefensively[CfgDefault];
        }

        public bool IsUtilityGtMin(QuoteRequest<Option> qr)
        {
            double minUtility = Cfg.MinUtility.TryGetValue(qr.Underlying.Value, out minUtility) ? minUtility : Cfg.MinUtility[CfgDefault];
            if (qr.UtilityOrder.Utility < minUtility)
            {
                Log($"GetQuote: UtilityHigh not anymore greater minUtil => Quoting Price 0. utilityOrderHigh={qr.UtilityOrder.Utility}. utilityOrderLowCrossSpread={qr.UtilityOrder.Utility}. QuoteRequest Util: {qr.UtilityOrder.Utility}");
            }
            return qr.UtilityOrder.Utility < minUtility;
        }

        public decimal BufferPriceCrossingSpread(QuoteRequest<Option> qr, decimal price)
        {
            // Shouldn't just go by ticket. Imagine it's cancelled and first new submission is crossing much of the spread. Would wanna buffer that too!
            if (Cfg.BufferIntraSpreadQuotes && SpreadBuffers[qr.OrderDirection].TryGetValue(qr.Symbol, out SpreadBuffer sp))
            {
                price = sp.BufferIntraSpreadQuote(price);
            }
            else if (Cfg.BufferIntraSpreadQuotes)
            {
                Error($"GetQuote: BufferIntraSpreadQuotes is true, but no SpreadBuffer found for {qr.Symbol}. Expected to be instantiated in SecurityInitializer.");
            }
            return price;
        }

        /// <summary>
        /// // Don't hit deep order book wasting money.
        /// </summary>
        public decimal LimitPriceToBBO(QuoteRequest<Option> qr, decimal price)
        {
            return qr.OrderDirection switch
            {
                OrderDirection.Buy => Math.Min(price, qr.Option.AskPrice),
                OrderDirection.Sell => Math.Max(price, qr.Option.BidPrice),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
        }

        /// <summary>
        /// // Limit spread crossing to configurable / mid price.
        /// </summary>
        public decimal LimitMaxSpreadDiscount(QuoteRequest<Option> qr, decimal price)
        {
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;
            decimal maxDiscountTimeSpread = AlgoConfig.GetEntry(Cfg.MaxDiscountTimeSpread, qr.Underlying.Value);
            return qr.OrderDirection switch
            {
                OrderDirection.Buy => Math.Min(price, qr.Option.AskPrice - marketPriceSpread * maxDiscountTimeSpread),
                OrderDirection.Sell => Math.Max(price, qr.Option.BidPrice + marketPriceSpread * maxDiscountTimeSpread),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
        }

        public decimal PriceSpreadDiscounted(QuoteRequest<Option> qr)
        {
            decimal marketPriceSpread = qr.Option.AskPrice - qr.Option.BidPrice;

            decimal priceKeepSpread = qr.OrderDirection switch
            {
                OrderDirection.Buy => qr.Option.BidPrice,
                OrderDirection.Sell => qr.Option.AskPrice,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };

            IUtilityOrder utilityHigh = UtilityOrderFactory.Create(this, qr.Option, qr.Quantity, priceKeepSpread);

            decimal rtSpreadDiscount = RatioSpreadDiscount(qr.Underlying, utilityHigh, qr.UtilityOrder);
            decimal spreadDiscount = rtSpreadDiscount * marketPriceSpread;

            return qr.OrderDirection switch
            {
                OrderDirection.Buy => qr.Option.BidPrice + spreadDiscount,
                OrderDirection.Sell => qr.Option.AskPrice - spreadDiscount,
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
        }


        public decimal RatioSpreadDiscount(Symbol underlying, IUtilityOrder utilityOrderHigh, IUtilityOrder utilityOrderLow)
        {
            double utilLow = utilityOrderLow.Utility;
            double utilHigh = utilityOrderHigh.Utility;

            double discountUtilHigh;
            if (utilLow > utilHigh)
            {
                if (utilLow > utilHigh * 1.05)
                {
                    Error($"SpreadDiscount: utilLow={utilLow} > utilHigh={utilHigh}. Swapping for now. Investigate.");
                }
                (utilLow, utilHigh) = (utilHigh, utilLow);
            }
            double dUdD = utilLow - utilHigh;

            double zeroSDUtil = Cfg.ZeroSDUtil.TryGetValue(underlying, out zeroSDUtil) ? zeroSDUtil : Cfg.ZeroSDUtil[CfgDefault];
            double slopeNeg = Cfg.SlopeNeg.TryGetValue(underlying, out slopeNeg) ? slopeNeg : Cfg.SlopeNeg[CfgDefault];
            double slopePos = Cfg.SlopeNeg.TryGetValue(underlying, out slopePos) ? slopePos : Cfg.SlopePos[CfgDefault];
            double utilBidTaperer = Cfg.UtilBidTaperer.TryGetValue(underlying, out utilBidTaperer) ? utilBidTaperer : Cfg.UtilBidTaperer[CfgDefault];

            if (utilHigh >= zeroSDUtil)
            {
                discountUtilHigh = 2 / (1 + Math.Exp(slopePos * (utilHigh - zeroSDUtil))) - 1;
            }
            else
            {
                discountUtilHigh = 2 / (1 + Math.Exp(slopeNeg * (utilHigh - zeroSDUtil))) - 1;
            }

            decimal maxSpreadDiscount = Cfg.MaxSpreadDiscount.TryGetValue(underlying, out maxSpreadDiscount) ? maxSpreadDiscount : Cfg.MaxSpreadDiscount[CfgDefault];
            return Math.Min(maxSpreadDiscount, ToDecimal(discountUtilHigh + utilBidTaperer * discountUtilHigh * dUdD));
        }

        public decimal? GetKalmanQuote(QuoteRequest<Option> qr)
        {
            decimal kfPriceMid;
            decimal kfPriceWSpread;

            if (!IVSurfaceSSVIMid.TryGetValue((Equity)Securities[qr.Underlying], out IIVSurface ivs))
            {
                Log($"{Time} GetKalmanQuote(): {qr.Underlying} No KalmanFilter found for {qr.Underlying}");
                return null;
            }

            if (!ivs.IsCalibrated || !ivs.HasParams(qr.Option)) return null;

            double modelIV = ivs.IV(qr.Option);
            decimal ivMeanSpread = IVSpreadSMA[qr.Option.Symbol].Current.Value;
            OptionContractWrap ocw = OptionContractWrap.E(this, qr.Option, Time.Date);
            kfPriceMid = qr.OrderDirection switch
            {
                OrderDirection.Buy => (decimal)ocw.NPV(modelIV, null),
                OrderDirection.Sell => (decimal)ocw.NPV(modelIV, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            //kfPriceWSpread = qr.OrderDirection switch
            //{
            //    OrderDirection.Buy => (decimal)ocw.NPV(modelIV - ((double)ivMeanSpread) / 2, null),
            //    OrderDirection.Sell => (decimal)ocw.NPV(modelIV + ((double)ivMeanSpread) / 2, null),
            //    _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            //};
            kfPriceWSpread = qr.OrderDirection switch
            {
                OrderDirection.Buy => (decimal)ocw.NPV(modelIV, null),
                OrderDirection.Sell => (decimal)ocw.NPV(modelIV, null),
                _ => throw new ArgumentException($"Unknown order direction {qr.OrderDirection}")
            };
            Log($"GetKalmanQuote: direction={qr.OrderDirection} option={qr.Option}, kfPriceMid={kfPriceMid}, kfModelIV={modelIV}, kfPriceWSpread={kfPriceWSpread}, bidPrice={qr.Option.BidPrice}, bidIV={IVBids[qr.Option.Symbol].IVBidAsk.IV}, askPrice={qr.Option.AskPrice}, askIV={IVAsks[qr.Option.Symbol].IVBidAsk.IV}, IVMeanSpread={ivMeanSpread}, spot={MidPrice(qr.Option.Underlying.Symbol)}");
            return kfPriceWSpread;
        }


        internal Quote<Option> GetQuote(QuoteRequest<Option> qr)
        {
            
            decimal price = PricingStrategy[qr.Underlying].GetPrice(qr, this);

            // Defensive rounding and adjustments
            decimal priceRounded = RoundTick(price, TickSize(qr.Symbol), qr.OrderDirection == OrderDirection.Sell);
            double ivPrice = (double)OptionContractWrap.E(this, qr.Option, Time.Date).IV(price, MidPrice(qr.Symbol.Underlying), 0.001);

            return new Quote<Option>(qr.Option, qr.Quantity, priceRounded, ivPrice, qr.UtilityOrder, null, 0);
        }

        public decimal SpreadDiscountSweep(IUtilityOrder utilityOrder)
        {
            Symbol symbol = utilityOrder.Symbol;

            decimal bid = Securities[symbol].BidPrice;
            decimal ask = Securities[symbol].AskPrice;
            decimal spread = ask - bid;

            decimal sweepRatio = SweepState[symbol][utilityOrder.OrderDirection].SweepRatio;

            double utilPV = utilityOrder.UtilityPV;  // That'll be okayish, directly comparable with spreads to pay.
            decimal maxAcceptableSpreadRatio = spread <= 0 || utilPV == 0 ? sweepRatio : ToDecimal((utilPV / 2)) / (100 * spread);

            var res = Math.Min(sweepRatio, maxAcceptableSpreadRatio);
            decimal spreadDiscountSweepMinSpreadRatio = Cfg.SpreadDiscountSweepMinSpreadRatio.TryGetValue(utilityOrder.Underlying.Value, out spreadDiscountSweepMinSpreadRatio) ? spreadDiscountSweepMinSpreadRatio : Cfg.SpreadDiscountSweepMinSpreadRatio[CfgDefault];
            res = Math.Min(res, spreadDiscountSweepMinSpreadRatio);

            Log($"{Time} SpreadDiscountSweep: {symbol} res={res} sweepRatio={sweepRatio}, maxAcceptableSpreadRatio={maxAcceptableSpreadRatio}, " +
                $"spread={spread} utilPV={utilPV}, utilEquityPosition={utilityOrder.UtilityEquityPosition}. bid={bid}, ask={ask}");

            return res;
        }
    }
}
