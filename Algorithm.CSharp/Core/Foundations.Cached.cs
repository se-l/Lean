using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Statistics;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using QuantConnect.Securities;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Indicators;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        // Cached Methods
        public Func<Symbol, Symbol, int, Resolution, double> Beta;
        public Func<Symbol, Symbol, int, Resolution, double> Correlation;
        public Func<Symbol, int, Resolution, bool> IsLiquid;
        public VoidFunction HedgeWithIndex;
        public VoidArg1Function<Symbol> HedgeOptionWithUnderlying;
        public VoidArg1Function<Symbol> HedgeOptionWithUnderlyingZMBands;
        public Func<Symbol, int, Resolution, IEnumerable<TradeBar>> HistoryWrap;
        public Func<Symbol, int, Resolution, IEnumerable<QuoteBar>> HistoryWrapQuote;
        public Func<Symbol, decimal> TickSize;
        public Func<decimal> PositionsTotal;
        public Func<int> PositionsN;
        public Func<Symbol, double, double> AtmIVCached;

        public void AssignCachedFunctions()
        {
            Beta = Cache(GetBeta, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            Correlation = Cache(GetCorrelation, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            IsLiquid = Cache(GetIsLiquid, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            //HedgeWithIndex = Cache(GetHedgeWithIndex, () => Time, maxKeys: 1);
            HedgeOptionWithUnderlying = Cache(GetHedgeOptionWithUnderlying, (Symbol symbol) => (Time, Underlying(symbol)));
            HedgeOptionWithUnderlyingZMBands = Cache(GetHedgeOptionWithUnderlyingZMBands, (Symbol symbol) => (Time, Underlying(symbol)));
            HistoryWrap = Cache(GetHistoryWrap, (Symbol symbol, int window, Resolution resolution) => (Time.Date, symbol, window, resolution));  // not correct for resolution < daily
            HistoryWrapQuote = Cache(GetHistoryWrapQuote, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            TickSize = Cache(GetTickSize, (Symbol symbol) => symbol, maxKeys: 1);
            PositionsTotal = Cache(GetPositionsTotal, () => Time.Ticks, maxKeys: 1);
            PositionsN = Cache(GetPositionsN, () => Time.Ticks, maxKeys: 1);
            AtmIVCached = Cache(GetAtmIV, (Symbol symbol, double defaultSpread) => (Time.Ticks, symbol, defaultSpread));

            IntrinsicValue = (Option option) => option.GetIntrinsicValue(MidPrice(option.Underlying.Symbol));
        }
        public double AtmIV(Symbol symbol, double defaultSpread = 0.005) => AtmIVCached(symbol, defaultSpread);
        /// <summary>
        /// Ask IV strongly slopes up close to expiration (1-3 days), therefore rendering midIV not a good indicator. Would wanna use contracts expiring later. This will lead to a
        /// jump in AtmIV when referenced contracts are switched. How to make it smooth?
        /// </summary>
        public double GetAtmIV(Symbol symbol, double defaultSpread = 0.005)
        {
            double bidIV = IVSurfaceRelativeStrikeBid.TryGetValue(Underlying(symbol), out IVSurfaceRelativeStrike bidSurface) ? bidSurface.AtmIv() : 0;
            double askIV = IVSurfaceRelativeStrikeAsk.TryGetValue(Underlying(symbol), out IVSurfaceRelativeStrike askSurface) ? askSurface.AtmIv() : 0;
            return InterpolateMidIVIfAnyZero(bidIV, askIV, defaultSpread);
        }

        private double GetBeta(Symbol index, Symbol asset, int periods, Resolution resolution = Resolution.Daily)
        {
            // needs caching as multiple option contracts will run this method for the same underlying.
            var logReturnsAsset = LogReturns(HistoryWrap(asset, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            var logReturnsIndex = LogReturns(HistoryWrap(index, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            int minSize = Math.Min(logReturnsAsset.Length, logReturnsIndex.Length);

            if (minSize != periods - 1)  // Log returns removes 1.
            {
                Debug($"Beta() Error: The received periods are smaller than the requested periods, likely due to missing historical data for request. Returning 0 beta. {index}. {asset} {periods} {resolution}");
            }
            if (minSize == 0)
            {
                return 0;
            }
            return Covariance(logReturnsAsset, logReturnsIndex, periods) / logReturnsIndex.Variance(unbiased: true);
        }
        private double GetCorrelation(Symbol symbol1, Symbol symbol2, int periods = 20, Resolution resolution = Resolution.Daily)
        {
            var logReturnsSymbol1 = LogReturns(HistoryWrap(symbol1, periods, resolution).Select(tb => (double)tb.Close).ToArray());
            var logReturnsSymbol2 = LogReturns(HistoryWrap(symbol2, periods, resolution).Select(tb => (double)tb.Close).ToArray());

            int minSize = Math.Min(logReturnsSymbol1.Length, logReturnsSymbol2.Length);

            if (minSize != periods - 1)  // Log returns removes 1.
            {
                Debug($"Error: The window size {minSize} is smaller than the requested periods, likely due to missing historical data for request {symbol1}. {symbol2} {periods} {resolution}");
            }

            if (minSize != 0)
            {
                logReturnsSymbol1 = logReturnsSymbol1.TakeLast(minSize).ToArray();
                logReturnsSymbol2 = logReturnsSymbol2.TakeLast(minSize).ToArray();
            }
            else // (minSize == 0)
            {
                return 0;
            }

            double corrPearson = MathNet.Numerics.Statistics.Correlation.Pearson(logReturnsSymbol1, logReturnsSymbol2);
            //double correlation = Covariance(logReturnsSymbol1, logReturnsSymbol2, periods) / (logReturnsSymbol1.Variance(unbiased: true) * logReturnsSymbol2.Variance(unbiased: true));

            Debug($"Correlation.Pearson({symbol1},{symbol2},{periods}: Pearson: {corrPearson}");
            return corrPearson;
        }

        /// <summary>
        /// Once exceeded, hedge as closely as possible to the desired hedge metric, for now that's delta.
        /// </summary>
        //private void GetHedgeWithIndex()
        //{
        //    //tex:
        //    //Deriving quantity to hedge
        //    //$$\Delta_I=\beta \frac{S_A}{S_I} \Delta_A$$

        //    var ticker = spy;
        //    var pfRisk = PortfolioRisk.E(this);
        //    decimal netSpyDelta = pfRisk.DeltaSPY100BpUSD;
        //    if ( netSpyDelta > HedgeBand.DeltaLongUSD || netSpyDelta < HedgeBand.DeltaShortUSD )
        //    {
        //        var quantity = -1 * Math.Round((netSpyDelta - HedgeBand.DeltaTargetUSD) / MidPrice(ticker), 0);
        //        // Call cached HedgeWithIndex to avoid stack overflow with MarketOrder or immediately filled Limit Orders.
        //        //LimitOrder(ticker, quantity, RoundTick(MidPrice(ticker), TickSize(ticker)));
        //    }
        //}

        /// <summary>
        /// Adjust the target hedge risk by an amount corresponding to the put call ratio signal.
        /// </summary>
        /// <returns></returns>
        public decimal TargetRiskPutCallRatio(Symbol underlying)
        {
            if (Cfg.PutCallRatioTargetRisks.ContainsKey(underlying.Value))
            {
                foreach (TargetRisk targetRisk in Cfg.PutCallRatioTargetRisks[underlying.Value])
                {
                    if (targetRisk.RangeLower <= PutCallRatios[underlying].Ratio() && PutCallRatios[underlying].Ratio() <= targetRisk.RangeUpper)
                    {
                        return targetRisk.Target100BpUSD;
                    }
                }
            }
            return 0;
        }

        public decimal Risk100BpRisk2USDDelta(Symbol symbol, decimal risk)
        {
            return risk * 100 / MidPrice(symbol);
        }
        /// <summary>
        /// Gamma long - trailing limit orders.
        /// Gamma short - hedge more tightly - midPrice Limit Orders.
        /// </summary>
        /// <param name="symbol"></param>
        private void GetHedgeOptionWithUnderlyingZMBands(Symbol symbol)
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed) || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) return;

            Symbol underlying = Underlying(symbol);

            decimal riskDeltaEquityTotal = PfRisk.RiskByUnderlying(symbol, Metric.EquityDeltaTotal);
            decimal lowerBand = PfRisk.RiskBandByUnderlying(symbol, Metric.BandZMLower);
            decimal upperBand = PfRisk.RiskBandByUnderlying(symbol, Metric.BandZMUpper);

            if (PfRisk.IsUnderlyingDeltaExceedingBandZM(symbol))
            {
                decimal midBand = (lowerBand + upperBand) / 2;
                decimal hedgeQuantity = -midBand - riskDeltaEquityTotal;
                Log($"{Time} GetHedgeOptionWithUnderlyingZMBands: ZMLowerBand={lowerBand}, ZMUpperBand={upperBand}, MidBand={midBand}, riskDeltaEquityTotal={riskDeltaEquityTotal}, hedgeQuantity={hedgeQuantity}.");
                ExecuteHedge(underlying, Math.Round(hedgeQuantity, 0));
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying: Fill Event for {symbol}, but cannot no non-zero quantity in Portfolio. Expect this function to be called only when risk is exceeded.");
            }
        }

        /// <summary>
        /// Closely related to GedHedgeWithIndex, but hedges with the underlying instead of the index.
        /// To avoid dynamic over hedging, best used rarely. For example once per fill only.
        /// To be refactored with a more generic hedging function searching for the best hedge given the current portfolio.
        /// </summary>
        private void GetHedgeOptionWithUnderlying(Symbol symbol)
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            Symbol underlying = Underlying(symbol);

            if (IsEODAcrossDsHedge(underlying))
            {
                decimal quantity = -(decimal)LastDeltaAcrossDs[underlying] - Portfolio[underlying].Quantity;
                Log($"{Time} {underlying} GetHedgeOptionWithUnderlying with DeltaTotalAcrossDs: {LastDeltaAcrossDs[underlying]}, quantity: {quantity}, Position: {Portfolio[underlying].Quantity}");
                if (Math.Abs(quantity) > 1)
                {
                    ExecuteHedge(underlying, quantity, OrderType.Market);
                }
                return;
            }

            decimal deltaTotal = DeltaMV(symbol);
            if (PfRisk.IsUnderlyingDeltaExceedingBand(symbol, deltaTotal))
            {
                ExecuteHedge(underlying, EquityHedgeQuantity(underlying));
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. underlying={underlying} Not hedging because deltaTotal={deltaTotal}.");
            }
        }

        public bool IsEODAcrossDsHedge(Symbol underlying)
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return false;

            DateTime nextReleaseDate = NextReleaseDate(underlying);
            if (LastDeltaAcrossDs.ContainsKey(underlying) && Time.TimeOfDay >= new TimeSpan(15, 45, 0) && nextReleaseDate == Time.Date)
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// Closely related to GedHedgeWithIndex, but hedges with the underlying instead of the index.
        /// To avoid dynamic over hedging, best used rarely. For example once per fill only.
        /// To be refactored with a more generic hedging function searching for the best hedge given the current portfolio.
        /// </summary>
        private void GetHedgeOptionWithUnderlyingUSD(Symbol symbol)
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            decimal riskDelta100BpUSD = 0;
            Symbol underlying = Underlying(symbol);

            decimal delta100BpUSDTotal = PfRisk.RiskByUnderlying(symbol, Metric.Delta100BpUSDTotal);
            riskDelta100BpUSD += delta100BpUSDTotal;
            decimal deltaIVdS100BpUSD = PfRisk.RiskByUnderlying(symbol, Metric.DeltaIVdS100BpUSDTotal);  // MV
            riskDelta100BpUSD += deltaIVdS100BpUSD;

            SecurityRiskLimit riskLimit = Securities[underlying].RiskLimit;

            if (riskDelta100BpUSD > riskLimit.Delta100BpLong || riskDelta100BpUSD < riskLimit.Delta100BpShort)
            {
                ExecuteHedge(underlying, EquityHedgeQuantity(underlying));
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. Not hedging because riskDelta100BpUSD={riskDelta100BpUSD}, delta100BpUSDTotal={delta100BpUSDTotal}, deltaIVdS100BpUSD={deltaIVdS100BpUSD} for symbol={symbol}.");
            }
        }

        public decimal DeltaMV(Symbol symbol)
        {
            decimal deltaMVTotal = 0;
            decimal deltaTotal = PfRisk.RiskByUnderlying(symbol, HedgeMetric(Underlying(symbol)));
            
            // decimal deltaIVdSTotal = PfRisk.RiskByUnderlying(symbol, Metric.DeltaIVdSTotal);  // MV

            deltaMVTotal += deltaTotal;
            // deltaMVTotal += deltaIVdSTotal;
            // Log($"{Time} DeltaMV {symbol}: deltaMVTotal={deltaMVTotal}, deltaTotal={deltaTotal}, deltaIVdSTotal={deltaIVdSTotal}");
            return deltaMVTotal;
        }

        public void ExecuteHedge(Symbol symbol, decimal quantity, OrderType? orderType = null)
        {
            decimal price;
            Equity equity = (Equity)Securities[symbol];

            // Adjusting Heding Frequency by adjusting volatilty. Not making sense to me how adjust vola helps with hedging frequency, but can adjust the threhold...
            // Vola Bias (Vola up -> All Deltas closer to 0.5 (C) / -0.5 (P))
            //      Short Gamma + Trending    -> Hedge often (defensively)
            //      Short Gamma + Range Bound -> Hedge less  (hedges are losers)
            //      Long  Gamma + Trending    -> Hedge less  (let delta run)
            //      Long  Gamma + Range Bound -> Hedge often (hedges are winners)

            List<OrderTicket> tickets = orderTickets.TryGetValue(symbol, out tickets) ? tickets : new List<OrderTicket>();

            // Cancel any opposite one's.
            tickets.Where(t => t.Quantity * quantity < 0).ToList().ForEach(t => t.Cancel());
            // If market order, cancel any limit orders
            if (orderType == OrderType.Market)
            {
                tickets.Where(t => t.OrderType == OrderType.Limit).ToList().ForEach(t => t.Cancel());
            }

            // Hedge is taken through EventHandler -> UpdateLimitOrderEquity.
            var existingOrders = tickets.Where(t => orderTypeMarketLimit.Contains(t.OrderType) && !orderFilledCanceledInvalid.Contains(t.Status));

            if (quantity != 0 && !existingOrders.Any())
            {
                OrderType _orderType = orderType ?? GetEquityHedgeOrderType(equity);
                price = GetEquityHedgePrice(equity, _orderType, quantity);

                // Place new order. Market if no position yet, otherwise limit
                switch (Portfolio[symbol].Quantity)
                {
                    case 0:
                        OrderEquity(symbol, quantity, price, orderType: OrderType.Market);
                        break;
                    default:
                        switch (_orderType)
                        {
                            case OrderType.Market:
                            case OrderType.Limit:
                                QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "New OrderEquity" }, { "f", $"ExecuteHedge" },
                                    { "Symbol", symbol}, { "riskDeltaTotal", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });
                                OrderEquity(symbol, quantity, price, orderType: _orderType);
                                break;
                            default:
                                throw new NotImplementedException();
                        }
                        break;
                }
            }
            else
            {
                string msg = $"{Time} ExecuteHedge: Not hedging because quantity={quantity}, anyExistingOrders={existingOrders.Any()}, OrderId={string.Join(",", existingOrders.Select(t => t.OrderId))}.";
                Log(msg);
                //DiscordClient.Send(msg, DiscordChannel.Emergencies);
            }
        }

        /// <summary>
        /// Ensure Delta is zero before starting a new LiT. Gamma scalping is only initiated if delta is within an acceptable band. A tracker/monitor then handles creation of LimitOrders. That trakcer is switched off if
        /// gamma becomes negative or another option trade moves the portfolio beyond the band.
        /// </summary>
        /// <param name="equity"></param>
        /// <returns></returns>
        public OrderType GetEquityHedgeOrderType(Equity equity, OrderTicket? ticket = null)
        {
            if (ticket != null && ticket.UpdateRequests.Count > Cfg.LimitOrderUpdateBeforeMarketOrderConversion)
            {
                return OrderType.Market;
            }

            if (Spread(equity) <= Cfg.MaxSpreadForMarketOrderHedging)
            {
                return OrderType.Market;
            }
            
            return OrderType.Limit;
        }
        /// <summary>
        /// Gamma long - trailing limit orders.
        /// Gamma short - hedge more tightly - midPrice Limit Orders.
        /// </summary>
        public decimal GetEquityHedgePrice(Equity equity, OrderType orderType, decimal quantity, OrderTicket? ticket = null)
        {
            decimal touchPrice;
            decimal price;
            OrderDirection direction = quantity > 0 ? OrderDirection.Buy : OrderDirection.Sell;

            switch (orderType)
            {
                case OrderType.Market:
                    return MidPrice(equity.Symbol);

                case OrderType.Limit:
                    if (ticket == null)
                    {
                        return direction == OrderDirection.Buy ? equity.BidPrice + 0.01m : equity.AskPrice - 0.01m;
                    }
                    else if (ticket.UpdateRequests.Count > Cfg.LimitOrderUpdateBeforeMarketOrderConversion) // Price apparently moving away. Fill quickly.
                    {
                        // Aggressively limit order at price of market.
                        return ticket.Quantity > 0 ? equity.AskPrice : equity.BidPrice;
                    }
                    else
                    {
                        return direction == OrderDirection.Buy ? equity.BidPrice + 0.01m : equity.AskPrice - 0.01m;
                    }
                default:
                    return 0;
            }
        }

        private bool GetIsLiquid(Symbol contract, int window = 3, Resolution resolution = Resolution.Daily)
        {
            var trade_bars = HistoryWrap(contract, window, resolution).ToList();
            return trade_bars.Sum(bar => bar.Volume) > 0;
        }

        private IEnumerable<TradeBar> GetHistoryWrap(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<TradeBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<QuoteBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, DateTime start, DateTime end, Resolution resolution, bool? fillForward = false
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<QuoteBar>(symbol, start, end, resolution, fillForward: fillForward);
            //extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private decimal GetTickSize(Symbol symbol)
        {
            //Log($"GetTickSize called {symbol}");
            var sec = Securities[symbol];
            return sec.SymbolProperties.MinimumPriceVariation;
        }


        private decimal GetPositionsTotal()
        {
            return Portfolio.TotalHoldingsValue;
            //return Portfolio.Values.Where(s => s.Invested).Sum(s => s.HoldingsValue);
        }
        private int GetPositionsN()
        {
            // Filter out securities that are not invested
            return Portfolio.Values.Count(s => s.Invested);
        }
    }
}