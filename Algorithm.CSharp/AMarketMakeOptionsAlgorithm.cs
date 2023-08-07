/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Orders;
using QuantConnect.Data.Market;
using QuantConnect.ToolBox.IQFeed.IQ;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Configuration;
using QuantConnect.Securities.Option;
using QuantConnect.Scheduling;
using QuantConnect.Securities.Equity;
using System.Globalization;
using Newtonsoft.Json;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using static QuantConnect.Algorithm.CSharp.Core.Events.EventSignal;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Basic template algorithm simply initializes the date range and cash. This is a skeleton
    /// framework you can use for designing an algorithm.
    /// </summary>
    /// <meta name="tag" content="using data" />
    /// <meta name="tag" content="using quantconnect" />
    /// <meta name="tag" content="trading and orders" />
    public partial class AMarketMakeOptionsAlgorithm : Foundations
    {
        private DateTime endOfDay;
        DiskDataCacheProvider _diskDataCacheProvider = new();
        string dataDirectory = Config.Get("C:\\repos\\trade\\dataLive");
        Dictionary<(Resolution, Symbol, TickType), LeanDataWriter> writers = new();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            // Configurable Settings
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(2023, 6, 21);
            //SetEndDate(2023, 6, 6);
            //SetStartDate(2023, 7, 5);
            SetEndDate(2023, 8, 1);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 5;

            if (LiveMode)
            {
                SetOptionChainProvider(new IQOptionChainProvider());
            }

            mmWindow = new MMWindow(new TimeSpan(9, 30, 15), new TimeSpan(16, 00, 0) - ScheduledEvent.SecurityEndOfDayDelta);
            int volatilityPeriodDays = 5;
            orderType = OrderType.Limit;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), volatilityPeriodDays));

            AssignCachedFunctions();

            //string path = @"C:\repos\quantconnect\Lean\Algorithm.CSharp\Core\IVBounds.json";
            //string json = File.ReadAllText(path);
            //IVBounds = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<DateTime, List<List<Dictionary<DateTime, double>>>>>>(json);

            // Subscriptions
            spy = AddEquity("SPY", resolution).Symbol;
            hedgeTicker = new List<string> { "SPY" };
            // costing more than USD 100 - A, ALL, ARE, ZBRA, APD, ALLE, ZTS, ZBH
            //optionTicker = new List<string> { "HPE", "IPG", "AKAM", "AOS", "MO", "FL", "AES", "LNT", "A", "ALL", "ARE", "ZBRA", "APD", "ALLE", "ZTS", "ZBH", "PFE" };
            optionTicker = new List<string> { "HPE" };
            ticker = optionTicker.Concat(hedgeTicker).ToList();

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: Resolution.Second, fillForward: false);

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    options.Add(option);
                    subscriptions += AddOptionIfScoped(option);
                }
            }

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            pfRisk = PortfolioRisk.E(this);

            // Scheduled functions
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.AfterMarketOpen(hedgeTicker[0]), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(mmWindow.Start), OrderOppositeOrders);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(mmWindow.Start), RunSignals);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.At(mmWindow.End), CancelOpenTickets);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), LogRiskSchedule);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(1)), RunSignals); // too late. need to put liquidating order right after fill.
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(5)), LogHealth);
            Schedule.On(DateRules.EveryDay(hedgeTicker[0]), TimeRules.Every(TimeSpan.FromMinutes(5)), RecordRisk);

            securityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, spy, SecurityType.Equity);
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(securityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(2).First();
            Log($"WarmUp TimeSpan: {timeSpan}");
            SetWarmUp(timeSpan);

            pathRiskRecords = Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_risk_records_{EndDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.csv");
            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText(@"C:\repos\quantconnect\Lean\Algorithm.CSharp\Core\EarningsAnnouncements.json"));
        }
        /// <summary>
        /// For a given Symbol, warmup Underlying MidPrices and Option Bid/Ask to calculate implied volatility primarily. Add other indicators where necessary. 
        /// Required for scoping new options as part of the dynamic universe.
        /// Consider moving this into the SecurityInitializer.
        /// </summary>
        public void WarmUpSecurities(ICollection<Security> securities)
        {
            QuoteBar quoteBar;
            QuoteBar quoteBarUnderlying;
            Symbol symbol;
            Dictionary<Symbol, decimal> underlyingMidPrice = new();

            var history = History<QuoteBar>(securities.Select(sec => sec.Symbol), 60 * 7 * 5, Resolution.Minute, fillForward: false);

            foreach (DataDictionary<QuoteBar> data in history)
            {
                foreach (KeyValuePair<Symbol, QuoteBar> kvp in data)
                {
                    symbol = kvp.Key;
                    quoteBar = kvp.Value;
                    if (quoteBar.Symbol.SecurityType == SecurityType.Equity)
                    {
                        underlyingMidPrice[symbol] = (quoteBar.Bid.Close + quoteBar.Ask.Close) / 2;
                    }
                    else if (quoteBar.Symbol.SecurityType == SecurityType.Option)
                    {
                        if (underlyingMidPrice.TryGetValue(quoteBar.Symbol.Underlying, out decimal underlyingMidPriceValue))
                        {
                            IVBids[symbol].Update(quoteBar, underlyingMidPriceValue);
                            IVAsks[symbol].Update(quoteBar, underlyingMidPriceValue);
                            RollingIVBid[symbol].Update(IVBids[symbol].Current);
                            RollingIVAsk[symbol].Update(IVAsks[symbol].Current);
                        }
                    }
                }
            };
        }


        public override void OnData(Slice data)
        {
            if (IsWarmingUp) return;

            // Likely want to replace WarmUp with history calls for RollingIV
            foreach (KeyValuePair<Symbol, QuoteBar> kvp in data.QuoteBars)
            {
                Symbol symbol = kvp.Key;
                if (symbol.SecurityType == SecurityType.Option && kvp.Value != null)
                {
                    IVBids[symbol].Update(kvp.Value);
                    IVAsks[symbol].Update(kvp.Value);
                    RollingIVBid[symbol].Update(IVBids[symbol].Current);
                    RollingIVAsk[symbol].Update(IVAsks[symbol].Current);
                }
            }

            foreach (Symbol symbol in data.QuoteBars.Keys)
            {
                if (symbol.SecurityType == SecurityType.Equity && IsEventNewBidAsk(symbol))
                {
                    PublishEvent(new EventNewBidAsk(symbol));
                }
                else if (symbol.SecurityType == SecurityType.Option && IsEventNewBidAsk(symbol))
                {
                    PublishEvent(new EventNewBidAsk(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }

            // Logging Fills
            //foreach (KeyValuePair<Symbol, TradeBar> kvp in data.Bars)
            //{
            //    Symbol symbol = kvp.Key;
            //    if (symbol.ID.SecurityType == SecurityType.Option)
            //    {
            //        Log($"{Time} OnData.FILL Detected: symbol: {symbol} Time: {kvp.Value.Time} Close: {kvp.Value.Close} Volume: {kvp.Value.Volume} RollingIVBid: {RollingIVBid[symbol].EWMA} RollingIVAsk: {RollingIVAsk[symbol].EWMA}" +
            //            $"Best Bid: {Securities[symbol].BidPrice} Best Ask: {Securities[symbol].AskPrice}");
            //    }
            //}

            // Record data for next day comparison with history

            if (LiveMode)
            {
                foreach (KeyValuePair<Symbol, QuoteBar> kvp in data.QuoteBars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Quote), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, dataDirectory, TickType.Quote, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
                    writer.Write(dataW);
                }

                foreach (KeyValuePair<Symbol, TradeBar> kvp in data.Bars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Trade), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, dataDirectory, TickType.Trade, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
                    writer.Write(dataW);
                }
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            OrderEvents.Add(orderEvent);
            LogOrderEvent(orderEvent);
            foreach (var tickets in orderTickets.Values)
            {
                tickets.RemoveAll(t => t.Status == OrderStatus.Filled || t.Status == OrderStatus.Canceled || t.Status == OrderStatus.Invalid);
            }
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                var symbol = orderEvent.Symbol;
                var security = Securities[symbol];
                var order = Transactions.GetOrderById(orderEvent.OrderId);
                if (!PriceCache.ContainsKey(symbol))
                {
                    PriceCache[symbol] = Securities[symbol].Cache.Clone();
                }
                //orderEvent.OrderFee
                orderFillDataTN1[order.Id] = symbol.SecurityType switch
                {
                    SecurityType.Option => new OrderFillData(
                        orderEvent.UtcTime, PriceCache[symbol].BidPrice, PriceCache[symbol].AskPrice, PriceCache[symbol].Price,
                        ((Option)security).Underlying.Cache.BidPrice,
                        ((Option)security).Underlying.Cache.AskPrice,
                        ((Option)security).Underlying.Cache.Price,
                        orderEvent.OrderFee
                        ),
                    _ => new OrderFillData(Time, PriceCache[symbol].BidPrice, PriceCache[symbol].AskPrice, PriceCache[symbol].Price, fee: orderEvent.OrderFee) // Time is off.
                };
            }
            PublishEvent(orderEvent);
        }

        public IEnumerable<BaseData> GetLastKnownPricesTradeOrQuote(Security security)
        {
            Symbol symbol = security.Symbol;
            if (!HistoryRequestValid(symbol) || HistoryProvider == null)
            {
                return Enumerable.Empty<BaseData>();
            }

            var result = new Dictionary<TickType, BaseData>();
            Resolution? resolution = null;
            Func<int, bool> requestData = period =>
            {
                var historyRequests = CreateBarCountHistoryRequests(new[] { symbol }, period)
                    .Select(request =>
                    {
                        // For speed and memory usage, use Resolution.Minute as the minimum resolution
                        request.Resolution = (Resolution)Math.Max((int)Resolution.Minute, (int)request.Resolution);
                        // force no fill forward behavior
                        request.FillForwardResolution = null;

                        resolution = request.Resolution;
                        return request;
                    })
                    // request only those tick types we didn't get the data we wanted
                    .Where(request => !result.ContainsKey(request.TickType))
                    .ToList();
                foreach (var slice in History(historyRequests))
                {
                    for (var i = 0; i < historyRequests.Count; i++)
                    {
                        var historyRequest = historyRequests[i];
                        var data = slice.Get(historyRequest.DataType);
                        if (data.ContainsKey(symbol))
                        {
                            // keep the last data point per tick type
                            result[historyRequest.TickType] = (BaseData)data[symbol];
                        }
                    }
                }
                // true when all history requests tick types have a data point
                return historyRequests.All(request => result.ContainsKey(request.TickType));
            };

            if (!requestData(5))
            {
                if (resolution.HasValue)
                {
                    // If the first attempt to get the last know price returns null, it maybe the case of an illiquid security.
                    // Use Quote data to return MidPrice
                    var periods = Periods(security.Resolution, days: 5);
                    requestData(periods);
                }
                else
                {
                    // this shouldn't happen but just in case
                    Error($"QCAlgorithm.GetLastKnownPrices(): no history request was created for symbol {symbol} at {Time}");
                }
            }
            // return the data ordered by time ascending
            return result.Values.OrderBy(data => data.Time);
        }

        public void RunSignals()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0]) || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) return;
            if (!OnWarmupFinishedCalled)
            {
                OnWarmupFinished();
            }

            var signals = GetSignals();
            if (signals.Any())
            {
                signals = FilterSignalByRisk(signals); // once anything is filled. this calcs a lot
                if (signals.Any())
                {
                    {
                        PublishEvent(new EventSignals(signals));
                    }
                }
            }
        }

        public int AddOptionIfScoped(Symbol option)
        {
            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            foreach (var symbol in contractSymbols)
            {
                if (Securities.ContainsKey(symbol) && Securities[symbol].IsTradable) continue;  // already subscribed

                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                var historyUnderlying = HistoryWrap(symbolUnderlying, 30, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractInScope(symbol, lastClose))
                    {
                        var optionContract = AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
                        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                        susbcriptions++;
                    }
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"No history for {symbolUnderlying}. Not subscribing to its options." } });
                }
            }
            return susbcriptions;
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                RemoveUniverseSecurity(sec);
            });

            // Add options that have moved into scope
            options.ForEach(s => AddOptionIfScoped(s));

            PopulateOptionChains();
        }

        public void LogHealth()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) { return; }

            // Tickcounts
            //var d1 = new Dictionary<string, string>
            //{
            //    { "ts", Time.ToString() },
            //    { "topic", "HEALTH" },
            //    { "TickCountFilterSnap", TickCounterFilter.Snap().ToString() },
            //    { "TickCountFilterTotal", TickCounterFilter.Total.ToString() },
            //    { "TickCountOnDataSnap", TickCounterOnData.Snap().ToString() },
            //    { "TickCountOnDataTotal", TickCounterOnData.Total.ToString() },
            //};
            //d1 = d1.ToDictionary(x => x.Key, x => x.Value.ToString());
            //var d2 = PortfolioRisk.E(this).ToDict().ToDictionary(x => x.Key, x => x.Value.ToString());
            //string tag = Humanize(d1.Union(d2));

            // Ensure we're flipping positions. Need opposite limit order on every option position I hold.
            var positions = Portfolio.Securities.Values.Where(sec => sec.Invested && sec.Type == SecurityType.Option);
            int oppositeLimitOrderCount = 0;
            List<Symbol> positionsWithoutLiquidatingOrder = new List<Symbol>();
            foreach (var position in positions)
            {
                if (orderTickets.TryGetValue(position.Symbol, out List<OrderTicket> tickets))
                {
                    oppositeLimitOrderCount += tickets.Count(t => t.OrderType == OrderType.Limit && t.Quantity * position.Holdings.Quantity < 0);
                }
                else
                {
                    positionsWithoutLiquidatingOrder.Add(position.Symbol);
                }
            }
            if (positionsWithoutLiquidatingOrder.Count != 0)
            {
                var d1 = new Dictionary<string, string>
                {
                    { "ts", Time.ToString() },
                    { "topic", "HEALTH" },
                    { "# Option Positions", positions.Count().ToString() },
                    { "# Option Position With opposite Limit Order", oppositeLimitOrderCount.ToString() },
                    { "# Option Position Not Liquidating", string.Join(", ", positionsWithoutLiquidatingOrder) },
                };
                d1 = d1.ToDictionary(x => x.Key, x => x.Value.ToString());
                var d2 = PortfolioRisk.E(this).ToDict().ToDictionary(x => x.Key, x => x.Value.ToString());
                string tag = Humanize(d1.Union(d2));

                Log(tag);
            }
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay)
            {
                return;
            }
            LogRisk();

            Log($"Cash: {Portfolio.Cash}");
            Log($"UnsettledCash: {Portfolio.UnsettledCash}");
            Log($"TotalFeesQC: {Portfolio.TotalFees}");
            Log($"RealizedProfitQC: {Portfolio.TotalNetProfit}");
            Log($"TotalUnrealizedProfitQC: {Portfolio.TotalUnrealizedProfit}");

            Log($"TotalPortfolioValueMid: {pfRisk.PortfolioValue("Mid")}");
            Log($"TotalPortfolioValueQC/Close: {pfRisk.PortfolioValue("QC")}");
            Log($"TotalPortfolioValueWorst: {pfRisk.PortfolioValue("Worst")}");
            Log($"TotalUnrealizedProfitMineExFees: {pfRisk.PortfolioValue("UnrealizedProfit")}");
            Log($"PnLClose: {Portfolio.TotalPortfolioValue - TotalPortfolioValueSinceStart}");
            endOfDay = Time.Date;

            var positions = TradesCumulative.Cumulative(this);
            //var trades = Transactions.GetOrders().Where(o => o.LastFillTime != null && o.Status != OrderStatus.Canceled).Select(order => new Trade(this, order));
            //ExportToCsv(trades, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_trades_{Time:yyyyMMdd}.csv"));
            ExportToCsv(positions, Path.Combine(Directory.GetCurrentDirectory(), $"{Name}_trades_cumulative_{Time:yyMMdd}.csv"));
            Log($"PnLMid: {positions.Select(p => p.PL).Sum()}");
            Log($"PnlMidPerPosition: {pfRisk.PortfolioValue("AvgPositionPnLMid")}");
            Log($"PnlMidPerOptionAbsQuantity: {pfRisk.PortfolioValue("PnlMidPerOptionAbsQuantity")}");
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            fileHandleRiskRecords.Close();
            base.OnEndOfAlgorithm();
            _diskDataCacheProvider.DisposeSafely();
        }

        public void OrderOppositeOrders()
        {
            // Get all Securities with non-zero position. Would not get through as it's not yet time to trade....
            var nonZeroPositions = Portfolio.Values.Where(x => x.Invested && x.Type == SecurityType.Option);
            foreach (var position in nonZeroPositions)
            {
                OrderOppositeOrder(position.Symbol);
            }
        }

        public void OnMarketOpen()
        {
            PopulateOptionChains();
            CancelRiskIncreasingOrderTickets();

            // Trigger events
            foreach (Security security in Securities.Values)
            {
                PublishEvent(new EventNewBidAsk(security.Symbol));
                pfRisk.IsRiskLimitExceededZM(security.Symbol);
            }

            LogRisk();
            LogPnL();

            foreach (var indicator in RollingIVBid.Values)
            {
                if (!indicator.IsReadyLongMean)
                {
                    Log($"RollingIVBid {indicator.Symbol} Bid not ready at startup. Samples {indicator.Samples}");
                }
            }
            foreach (var indicator in RollingIVAsk.Values)
            {
                if (!indicator.IsReadyLongMean)
                {
                    Log($"RollingIVAsk {indicator.Symbol} Ask not ready at startup. Samples {indicator.Samples}");
                }
            }
        }

        public override void OnWarmupFinished()
        {
            IEnumerable<OrderTicket> openTransactions = Transactions.GetOpenOrderTickets();
            Log($"Adding Open Transactions to OrderTickets: {openTransactions.Count()}");
            foreach (OrderTicket ticket in openTransactions)
            {
                if (!orderTickets.ContainsKey(ticket.Symbol))
                {
                    orderTickets[ticket.Symbol] = new List<OrderTicket>();
                }
                orderTickets[ticket.Symbol].Add(ticket);
            }

            TotalPortfolioValueSinceStart = Portfolio.TotalPortfolioValue;

            pfRisk.ResetPositions();

            CancelRiskIncreasingOrderTickets();

            LogRisk();
            LogPnL();

            OnMarketOpen();

            OnWarmupFinishedCalled = true;
        }
        /// <summary>
        /// Dumpy portfolio risk metrics by underlying to csv for outside plotting
        /// </summary>
        public void RecordRisk()
        {
            if (IsWarmingUp || !IsMarketOpen(hedgeTicker[0])) return;

            if (fileHandleRiskRecords == null)
            {
                fileHandleRiskRecords = new StreamWriter(pathRiskRecords, true);
                fileHandleRiskRecords.AutoFlush = true;
                fileHandleRiskRecords.Write(string.Join(",", riskRecordsHeader) + @"\r\n");
            }
            foreach (string ticker in optionTicker)
            {
                var riskRecords = new List<RiskRecord>() { pfRisk.GetRiskRecord((Equity)Securities[ticker]) };
                string csv = ToCsv(riskRecords, riskRecordsHeader, skipHeader: true);
                fileHandleRiskRecords.Write(csv);
            }
        }
    }
}
