using System;
using System.Linq;
using System.Collections.Generic;
using Accord.Statistics;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Equity;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        // Cached Methods
        public Func<Symbol, Symbol, int, Resolution, double> Beta;
        public Func<Symbol, Symbol, int, Resolution, double> Correlation;
        public Func<Symbol, int, Resolution, bool> IsLiquid;
        public VoidFunction HedgeWithIndex;
        public VoidArg1Function<Symbol> HedgeGammaRisk;
        public VoidArg1Function<Symbol> HedgeOptionWithUnderlying;
        public VoidArg1Function<Symbol> HedgeOptionWithUnderlyingZM;
        public Func<Symbol, int, Resolution, IEnumerable<TradeBar>> HistoryWrap;
        public Func<Symbol, int, Resolution, IEnumerable<QuoteBar>> HistoryWrapQuote;
        public Func<Symbol, decimal> TickSize;
        public Func<decimal> PositionsTotal;
        public Func<int> PositionsN;

        public void AssignCachedFunctions()
        {
            Beta = Cache(GetBeta, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            Correlation = Cache(GetCorrelation, (Symbol symbol1, Symbol symbol2, int periods, Resolution resolution) => (symbol1, symbol2, periods, resolution, Time.Date));  // not correct for resolution < daily
            IsLiquid = Cache(GetIsLiquid, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            //HedgeWithIndex = Cache(GetHedgeWithIndex, () => Time, maxKeys: 1);
            HedgeGammaRisk = Cache(GetHedgeGammaRisk, (Symbol symbol) => (Time, symbol));
            HedgeOptionWithUnderlying = Cache(GetHedgeOptionWithUnderlying, (Symbol symbol) => (Time, Underlying(symbol)));
            HedgeOptionWithUnderlyingZM = Cache(GetHedgeOptionWithUnderlyingZM, (Symbol symbol) => (Time, Underlying(symbol)));
            HistoryWrap = Cache(GetHistoryWrap, (Symbol symbol, int window, Resolution resolution) => (Time.Date, symbol, window, resolution));  // not correct for resolution < daily
            HistoryWrapQuote = Cache(GetHistoryWrapQuote, (Symbol contract, int window, Resolution resolution) => (Time.Date, contract, window, resolution));
            TickSize = Cache(GetTickSize, (Symbol symbol) => symbol, maxKeys: 1);
            PositionsTotal = Cache(GetPositionsTotal, () => Time, maxKeys: 1);
            PositionsN = Cache(GetPositionsN, () => Time, maxKeys: 1);
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

        private void GetHedgeGammaRisk(Symbol symbol)
        {
            // Need to unify this with Signal functions. Scoped should be ALL options. Whether to Buy or Sell based on relative prices, events, inventory and current risk profile.
            // This method triggers limit orders for a range of securities, does not price them. Some may get blocked on last-mile check. Need better flow from order scan through various checks and placement.

            if (IsWarmingUp || !IsMarketOpen(equity1) || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) return;

            List<Option> optionContracts = new();

            Symbol underlying = Underlying(symbol);

            decimal total500BpGamma = pfRisk.RiskByUnderlying(symbol, Metric.Gamma500BpUSDTotal);
            double totalOptionsDelta = (double)pfRisk.DerivativesRiskByUnderlying(symbol, Metric.DeltaTotal);

            decimal lowerBand = pfRisk.RiskBandByUnderlying(symbol, Metric.GammaLowerContinuousHedge);
            decimal upperBand = pfRisk.RiskBandByUnderlying(symbol, Metric.GammaUpperContinuousHedge);

            optionChains.TryGetValue(underlying, out var options);
            var records = options.Where(o => 
                    o.Symbol.ID.Date > Time + TimeSpan.FromDays(60) 
                    && o.BidPrice > 0.05m  // No units
                    && Portfolio[o.Symbol].Quantity <= 0  // Get rid of this criteria eventually. Must be able to modify existing positions and adjust hedge.
                ).Select(o => new
            {
                Option = o,
                Delta = OptionContractWrap.E(this, o, 1).Delta(),
                Gamma100Bp = OptionContractWrap.E(this, o, 1).GammaXBp(100)
            });

            //if (total100BpGamma > upperBand)
            //{
            //    // Need options of same underlying with negative gamma. So sell any
            //    records = records.Where(r => r.Gamma100Bp < 0);
            //}
            //else if (total100BpGamma < lowerBand)
            //{
            //    // Need options of same underlying with positive gamma. So buy any. This is the more likely case, given strategy starts by selling options.
            //    records = records.Where(r => r.Gamma100Bp > 0);
            //}
            //else
            //{
            //    Log($"{Time} HedgeGammaRisk. Fill Event for {symbol}, but cannot no non-zero quantity in Portfolio. Expect this function to be called only when there is a non-zero quantity in Portfolio.");
            //    return;
            //}

            
            foreach (var record in records.Where(r => 
                r.Gamma100Bp > 0.05m 
                && r.Gamma100Bp < Math.Abs(total500BpGamma)
                && r.Delta * totalOptionsDelta <= 0  // Only hedge with options that dont increase existing delta.
            ))
            {
                decimal quantity = -Math.Round(total500BpGamma / record.Gamma100Bp, 0);

                // Subtract existing limit orders from quantity to avoid over hedging. No Market orders on options yet
                // decimal orderedQuantityMarket = ticketsUnderlying.Where(t => t.OrderType == OrderType.Market).Sum(t => t.Quantity);
                // quantity -= orderedQuantityMarket;

                if (quantity != 0)
                {
                    orderTickets.TryGetValue(record.Option.Symbol, out List<OrderTicket> tickets);
                    if (tickets != null && tickets.Any())
                    {
                        continue;
                    }
                    //Refactor to option, not equity quantity.
                    //decimal orderedQuantityLimit = tickets.Where(t => t.OrderType == OrderType.Limit).Sum(t => t.Quantity);
                    //if (orderedQuantityLimit != 0 && orderedQuantityLimit != quantity)
                    //{
                    //    // Update existing orders price and quantity.
                    //    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", $"Update Order" }, { "f", $"GetHedgeOptionWithUnderlying" },
                    //        { "Symbol", symbol}, { "riskDeltaTotalImplied", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });
                    //    UpdateLimitOrderEquity(equity, quantity, price);
                    //    return;
                    //}

                    else
                    {
                        QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "New Order" }, { "f", $"GetHedgeGammaRisk" },
                        { "Symbol", symbol}, { "riskDeltaTotalImplied", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });

                        OrderOptionContract(
                            record.Option,
                            quantity,
                            OrderType.Limit,
                            " Gamma Risk Hedge"
                        );
                    }
                }
            }
        }
        private void GetHedgeOptionWithUnderlyingZM(Symbol symbol)
        {
            if (IsWarmingUp || !IsMarketOpen(equity1) || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) return;
            
            Symbol underlying = Underlying(symbol);

            decimal equityHedge = pfRisk.RiskByUnderlying(symbol, Metric.EquityDeltaTotal);  // Normalized risk of an assets 1% change in price for band breach check.
            decimal lowerBand = pfRisk.RiskBandByUnderlying(symbol, Metric.BandZMLower);
            decimal upperBand = pfRisk.RiskBandByUnderlying(symbol, Metric.BandZMUpper);

            // Bands inverted. Need to understand better. Likely overall switches from neg. to pos. and vv.
            //decimal lowerBand = Math.Min(band1, band2);
            //decimal upperBand = Math.Max(band1, band2);

            if (equityHedge > upperBand || equityHedge < lowerBand)
            {
                Log($"{Time} GetHedgeOptionWithUnderlyingZM. ZMLowerBand={lowerBand}, ZMUpperBand={upperBand}, DeltaEquityTotal={equityHedge}.");
                ExecuteHedge(underlying, (upperBand + lowerBand) / 2 - equityHedge);
            }
            else
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. Fill Event for {symbol}, but cannot no non-zero quantity in Portfolio. Expect this function to be called only when risk is exceeded.");
            }
        }

            /// <summary>
            /// Closely related to GedHedgeWithIndex, but hedges with the underlying instead of the index.
            /// To avoid dynamic over hedging, best used rarely. For example once per fill only.
            /// To be refactored with a more generic hedging function searching for the best hedge given the current portfolio.
            /// </summary>
            private void GetHedgeOptionWithUnderlying(Symbol symbol)
        {
            if (IsWarmingUp || !IsMarketOpen(equity1)) return;

            Symbol underlying = Underlying(symbol);

            decimal riskDelta100BpUSDTotal = pfRisk.RiskByUnderlying(symbol, Metric.Delta100BpUSDTotal);
            SecurityRiskLimit riskLimit = Securities[underlying].RiskLimit;


            if (riskDelta100BpUSDTotal > riskLimit.Delta100BpLong || riskDelta100BpUSDTotal < riskLimit.Delta100BpShort)
            {
                var riskDeltaTotal = pfRisk.RiskByUnderlying(symbol, Metric.DeltaTotal);
                ExecuteHedge(underlying, -riskDeltaTotal);
            }
            else 
            {
                Log($"{Time} GetHedgeOptionWithUnderlying. Fill Event for {symbol}, but cannot no non-zero quantity in Portfolio. Expect this function to be called only when risk is exceeded.");
            }
        }

        private void ExecuteHedge(Symbol symbol, decimal quantity)
        {
            Equity equity = (Equity)Securities[symbol];

            // Adjusting Heding Frequency by adjusting volatilty. Not making sense to me how adjust vola helps with hedging frequency, but can adjust the threhold...
            // Vola Bias (Vola up -> All Deltas closer to 0.5 (C) / -0.5 (P))
            //      Short Gamma + Trending    -> Hedge often (defensively)
            //      Short Gamma + Range Bound -> Hedge less  (hedges are losers)
            //      Long  Gamma + Trending    -> Hedge less  (let delta run)
            //      Long  Gamma + Range Bound -> Hedge often (hedges are winners)

            //var optionPositions = pfRisk.Positions.Where(x => x.UnderlyingSymbol == underlying & x.SecurityType == SecurityType.Option);            
            //var biases = optionPositions.Select(p => p.Delta(implied: true)).Select(d => d-0.5 <= 0 ? -0.05 : 0.05);
            //riskDeltaTotal += (decimal)biases.Sum()*100;
            //var volaBias = -0.01;  // Current Strat is just selling call/puts expecting vola decline over time. hedgingVolatilityBias[NUM2DIRECTION[Math.Sign(quantity)]];
            //riskDeltaTotal = pfRisk.RiskByUnderlying(symbol, Metric.DeltaTotalImplied, volatility: IVAtm(symbol) + volaBias);

            List<OrderTicket> tickets;
            decimal price = GetEquityHedgeLimitOrderPrice(quantity, equity);
            quantity = Math.Round(quantity, 0);

            // subtract pending Market order fills
            if (orderTickets.TryGetValue(symbol, out tickets))
            {
                decimal orderedQuantityMarket = tickets.Where(t => t.OrderType == OrderType.Market).Sum(t => t.Quantity);
                quantity -= orderedQuantityMarket;
            }

            if (quantity != 0)
            {
                // Subtract existing limit orders from quantity to avoid over hedging.
                if (orderTickets.TryGetValue(symbol, out tickets))
                {
                    decimal orderedQuantityLimit = tickets.Where(t => t.OrderType == OrderType.Limit).Sum(t => t.Quantity);
                    if (orderedQuantityLimit != 0 && orderedQuantityLimit != quantity)
                    {
                        // Update existing orders price and quantity.
                        QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", $"Update Order" }, { "f", $"GetHedgeOptionWithUnderlying" },
                                { "Symbol", symbol}, { "riskDeltaTotalImplied", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });
                        UpdateLimitOrderEquity(equity, quantity, price);
                        return;
                    }
                }
                if (quantity != 0 && tickets == null || tickets.Count == 0)
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "HEDGE" }, { "action", "New Order" }, { "f", $"GetHedgeOptionWithUnderlying" },
                            { "Symbol", symbol}, { "riskDeltaTotalImplied", quantity.ToString() }, { "OrderQuantity", quantity.ToString() }, { "Position", Portfolio[symbol].Quantity.ToString() } });

                    // Place new order. Market if no position yet, otherwise limit
                    switch (Portfolio[symbol].Quantity)
                    {
                        case 0:
                            OrderEquity(symbol, quantity, price, orderType: OrderType.Market);
                            break;
                        default:
                            OrderEquity(symbol, quantity, price, orderType: OrderType.Limit);
                            break;
                    }
                }
            }
        }

        public decimal GetEquityHedgeLimitOrderPrice(decimal riskUSD, Equity equity)
        {
            // more aggressively hedge for immediate fill as time delay caused losses.
            // obviously deserves a model to optimize.
            // decimal midPriceUnderlying = MidPrice(option.Underlying.Symbol);
            // var ideal_limit_price = ticket.Quantity > 0 ? equity.BidPrice : equity.AskPrice;
            // return riskUSD > 0 ? equity.BidPrice : equity.AskPrice;
            return MidPrice(equity.Symbol);
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

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, DateTime start, DateTime end, Resolution resolution, bool? fillForward=false
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
