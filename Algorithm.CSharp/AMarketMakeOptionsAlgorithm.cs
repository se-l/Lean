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
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Scheduling;
using QuantConnect.Securities.Equity;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using QuantConnect.Algoalgorithm.CSharp.Core.Risk;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// 
    /// </summary>
    public partial class AMarketMakeOptionsAlgorithm : Foundations
    {
        private const string VolatilityBar = "VolatilityBar";
        private DateTime endOfDay;
        DiskDataCacheProvider _diskDataCacheProvider = new();
        Dictionary<(Resolution, Symbol, TickType), LeanDataWriter> writers = new();

        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            // Configurable Settings
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(2023, 8, 15);
            //SetStartDate(2023, 8, 1);
            SetEndDate(2023, 9, 6);
            SetCash(100_000);
            SetBrokerageModel(BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 1;
            Cfg = JsonConvert.DeserializeObject<AMarketMakeOptionsAlgorithmConfig>(File.ReadAllText("AMarketMakeOptionsAlgorithmConfig.json"));
            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText("EarningsAnnouncements.json"));

            mmWindow = new MMWindow(new TimeSpan(9, 31, 00), new TimeSpan(16, 0, 0) - ScheduledEvent.SecurityEndOfDayDelta);  // 2mins before EOD EOD market close events fire
            orderType = OrderType.Limit;

            SetSecurityInitializer(new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays));

            AssignCachedFunctions();

            // Subscriptions            
            optionTicker = Cfg.OptionTicker;
            ticker = optionTicker;
            equity1 = AddEquity(optionTicker.First(), resolution).Symbol;
            liquidateTicker = Cfg.LiquidateTicker;

            int subscriptions = 0;
            foreach (string ticker in ticker)
            {
                var equity = AddEquity(ticker, resolution: resolution, fillForward: false);

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    options.Add(option);
                    var subscribedSymbols = AddOptionIfScoped(option);
                    subscriptions += subscribedSymbols.Count;

                    foreach (string t in optionTicker)
                    {
                        DeltaDiscounts[equity.Symbol] = new RiskDiscount(Cfg, equity.Symbol, Metric.Delta100BpUSDTotal);
                        GammaDiscounts[equity.Symbol] = new RiskDiscount(Cfg, equity.Symbol, Metric.Gamma100BpUSDTotal);
                        EventDiscounts[equity.Symbol] = new RiskDiscount(Cfg, equity.Symbol, Metric.Events);
                    }
                }
                RiskPnLProfiles[equity.Symbol] = new RiskPnLProfile(this, equity);
            }

            Debug($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            PfRisk = PortfolioRisk.E(this);

            // SCHEDULED EVENTS
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.AfterMarketOpen(ticker[0]), OnMarketOpen);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.At(mmWindow.Start), RunSignals);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.At(mmWindow.End), CancelOpenOptionTickets);  // Leaves EOD Equity hedges.
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.Every(TimeSpan.FromMinutes(60)), UpdateUniverseSubscriptions);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.Every(TimeSpan.FromMinutes(30)), LogRiskSchedule);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.Every(TimeSpan.FromMinutes(1)), RunSignals); // not event driven, bad. Essentially
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.Every(TimeSpan.FromMinutes(5)), ExportRiskRecords);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.Every(TimeSpan.FromMinutes(5)), ExportIVSurface);
            Schedule.On(DateRules.EveryDay(ticker[0]), TimeRules.BeforeMarketClose(ticker[0], 3), HedgeDeltaFlat); // Meeds to fill within a minute, otherwise canceled. Refactor to turn to MarketOrder then
            // Turn Limit Equity into EOD before market close...

            // WARMUP
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, equity1, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            var timeSpan = StartDate - QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, StartDate.AddDays(-10), StartDate).TakeLast(Cfg.WarmUpDays + 1).First();
            Log($"WarmUp TimeSpan: {timeSpan} starting on {StartDate - timeSpan}");
            SetWarmUp(timeSpan);

            // Logging
            RiskRecorder = new(this);
        }

        public override void OnData(Slice slice)
        {
            equities.DoForEach(underlying => IVSurfaceRelativeStrikeBid[underlying].Update().CheckExecuteIVJumpReset());
            equities.DoForEach(underlying => IVSurfaceRelativeStrikeAsk[underlying].Update().CheckExecuteIVJumpReset());

            if (IsWarmingUp) return;

            foreach (Symbol symbol in slice.QuoteBars.Keys)
            {
                if (IsEventNewQuote(symbol)) // also called in Consolidator. Should cache result at timestamp, update PriceCache and read here from cache.
                {
                    PublishEvent(new EventNewBidAsk(symbol));
                }
                PriceCache[symbol] = Securities[symbol].Cache.Clone();
            }

            // Move into consolidator events. Not driving any biz logic.
            // Record data for restarts and next day comparison with history. Avoid conflict with Paper on same instance by running this for live mode only.
            if (LiveMode && Config.Get("ib-trading-mode") == "live")
            {
                foreach (KeyValuePair<Symbol, QuoteBar> kvp in slice.QuoteBars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Quote), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, Config.Get("data-folder"), TickType.Quote, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
                    writer.Write(dataW);
                }

                foreach (KeyValuePair<Symbol, TradeBar> kvp in slice.Bars)
                {
                    Symbol symbol = kvp.Key;
                    var dataW = new List<BaseData>() { kvp.Value };

                    var writer = writers.TryGetValue((resolution, symbol, TickType.Trade), out LeanDataWriter dataWriter)
                        ? dataWriter
                        : writers[(resolution, symbol, TickType.Quote)] = new LeanDataWriter(resolution, symbol, Config.Get("data-folder"), TickType.Trade, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
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
            PublishEvent(orderEvent);
        }

        static decimal Strike(Order o) => o.Symbol.ID.StrikePrice;
        Order NewEquityExerciseOrder(OptionExerciseOrder o) => new EquityExerciseOrder(o, new OrderFillData(o.Time, Strike(o), Strike(o), Strike(o), Strike(o), Strike(o), Strike(o)))
        {
            Status = OrderStatus.Filled
        };

        public override void OnAssignmentOrderEvent(OrderEvent assignmentEvent)
        {
            Log(assignmentEvent.ToString());

            OptionExerciseOrder optionExerciseOrder = (OptionExerciseOrder)Transactions.GetOrderById(assignmentEvent.OrderId);            
            var equityExerciseOrders = NewEquityExerciseOrder(optionExerciseOrder);

            if (!Trades.ContainsKey(assignmentEvent.Symbol))
            {
                Trades[assignmentEvent.Symbol] = new();
            }
            if (!Trades.ContainsKey(assignmentEvent.Symbol.Underlying))
            {
                Trades[assignmentEvent.Symbol.Underlying] = new();
            }

            Trades[assignmentEvent.Symbol].Add(new(this, optionExerciseOrder));
            Trades[equityExerciseOrders.Symbol].Add(new(this, equityExerciseOrders));
        }



        /// <summary>
        /// Event driven: On MarketOpen, OnFill, OnRiskProfileChanges / Thresholds crossed
        /// </summary>
        public void RunSignals()
        {
            if (IsWarmingUp || !IsMarketOpen(ticker[0]) || Time.TimeOfDay < mmWindow.Start || Time.TimeOfDay > mmWindow.End) return;
            if (!OnWarmupFinishedCalled)
            {
                OnWarmupFinished();
            }

            var signals = GetDesiredOrders();
            HandleDesiredOrders(signals);
        }

        public List<Symbol> AddOptionIfScoped(Symbol option)
        {
            //if (!IsMarketOpen(hedgeTicker[0])) return new List<Symbol>();

            int susbcriptions = 0;
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            List<Symbol> subscribedSymbol = new();
            foreach (var symbol in contractSymbols)
            {
                if ( Securities.ContainsKey(symbol) && Securities[symbol].IsTradable ) continue;  // already subscribed


                Symbol symbolUnderlying = symbol.ID.Underlying.Symbol;
                // Todo: move the period parameter to a config
                var historyUnderlying = HistoryWrap(symbolUnderlying, 7, Resolution.Daily).ToList();
                if (historyUnderlying.Any())
                {
                    decimal lastClose = historyUnderlying.Last().Close;
                    if (ContractInScope(symbol, lastClose))
                    {
                        var item = AddData<VolatilityBar>(symbol, resolution: Resolution.Second, fillForward: false);
                        item.IsTradable = false;

                        AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
                        QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"Adding {symbol}. Scoped." } });
                        subscribedSymbol.Add(symbol);
                    }
                }
                else
                {
                    QuickLog(new Dictionary<string, string>() { { "topic", "UNIVERSE" }, { "msg", $"No history for {symbolUnderlying}. Not subscribing to its options." } });
                }
            }
            return subscribedSymbol;
        }

        public void UpdateUniverseSubscriptions()
        {
            if (IsWarmingUp || !IsMarketOpen(ticker[0])) return;

            // Remove securities that have gone out of scope and are not in the portfolio. Cancel any open tickets.
            Securities.Values.Where(sec => sec.Type == SecurityType.Option).DoForEach(sec =>
            {
                RemoveUniverseSecurity(sec);
            });

            // Add options that have moved into scope
            options.ForEach(s => AddOptionIfScoped(s));
        }

        public override void OnEndOfDay(Symbol symbol)
        {
            if (IsWarmingUp || Time.Date == endOfDay) { return; }
            LogPortfolioHighLevel();
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            //equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            endOfDay = Time.Date;
        }

        public override void OnEndOfAlgorithm()
        {
            OnEndOfDay();
            ExportToCsv(Position.AllLifeCycles(this), Path.Combine(Directory.GetCurrentDirectory(), "Analytics", "PositionLifeCycle.csv"));
            RiskRecorder.Dispose();
            IVSurfaceRelativeStrikeBid.Values.DoForEach(s => s.Dispose());
            IVSurfaceRelativeStrikeAsk.Values.DoForEach(s => s.Dispose());
            RiskPnLProfiles.Values.DoForEach(s => s.Dispose());
            Utility.Dispose();
            base.OnEndOfAlgorithm();
            _diskDataCacheProvider.DisposeSafely();
        }

        public void OnMarketOpen()
        {
            if (IsWarmingUp) { return; }

            CancelGammaHedgeBeyondScope();

            // Trigger events
            foreach (Security security in Securities.Values.Where(s => s.Type == SecurityType.Equity))  // because risk is hedged by underlying
            {
                PublishEvent(new EventNewBidAsk(security.Symbol));
                PfRisk.IsRiskLimitExceededZM(security.Symbol);
            }

            LogRisk();
            LogPnL();
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

            LogRisk();
            LogPnL();

            OnMarketOpen();

            equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeBid[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));
            equities.DoForEach(underlying => Log(IVSurfaceRelativeStrikeAsk[underlying].GetStatus(Core.Indicators.IVSurfaceRelativeStrike.Status.Smoothings)));

            OnWarmupFinishedCalled = true;
        }
        /// <summary>
        /// Dump portfolio risk metrics by underlying to csv for outside plotting
        /// </summary>
        public void ExportRiskRecords()
        {
            if (IsWarmingUp || !IsMarketOpen(ticker[0])) return;
            optionTicker.DoForEach(ticker => RiskRecorder.Record(ticker));
        }

        public void ExportIVSurface()
        {           
            if (IsWarmingUp || !IsMarketOpen(ticker[0])) return;
            IVSurfaceRelativeStrikeBid.Values.Union(IVSurfaceRelativeStrikeAsk.Values).DoForEach(s => s.WriteCsvRows());
        }

        public void HedgeDeltaFlat()
        {
            if (IsWarmingUp || !IsMarketOpen(ticker[0])) return;

            foreach (string ticker in optionTicker)
            {
                Equity equity = (Equity)Securities[ticker];
                Log($"HedgeDeltaFlat EOD: {equity.Symbol}");
                HedgeOptionWithUnderlying(equity.Symbol);
            }
        }

        // OTHER
        public IEnumerable<BaseData> GetLastKnownPricesTradeOrQuote(Security security)
        {
            Symbol symbol = security.Symbol;
            if (
                symbol.ID.Symbol.Contains(VolatilityBar)
                || !HistoryRequestValid(symbol)
                || HistoryProvider == null
                )
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

            if (!requestData(Periods(Resolution.Minute, days: 1)))
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
    }
}
