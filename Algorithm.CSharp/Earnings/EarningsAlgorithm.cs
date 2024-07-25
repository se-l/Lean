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

using System.IO;
using QuantConnect.Algorithm.CSharp.Core;
using Newtonsoft.Json;
using System.Linq;
using System;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core.Risk;
using QuantConnect.Securities;
using QuantConnect.Algorithm.CSharp.Core.IO;
using System.Globalization;
using System.Collections.Generic;
using QuantConnect.Algorithm.CSharp.Core.Events;
using QuantConnect.Orders;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Securities.Equity;
using System.Threading;
using QuantConnect.Securities.Option;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp.Earnings
{
    /// <summary>
    /// 
    /// </summary>
    public partial class EarningsAlgorithm : Foundations
    {
        private EarningsAlgorithmConfig CfgAlgo;
        private readonly string CfgAlgoName = "EarningsAlgorithmConfig.json";
        private WsClient wsClient;
        private readonly Dictionary<Symbol, List<TargetPortfolio>> TargetPortfolios = new();
        private bool ExecuteScheduledTargetPortfolioFetch = true;
        private readonly Dictionary<Symbol, IEnumerable<MarketDataSnapByUnderlying>> MarketDataSnaps = new();

        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            CfgAlgo = JsonConvert.DeserializeObject<EarningsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            CfgAlgo.OverrideWithEnvironmentVariables<EarningsAlgorithmConfig>();
            Cfg.OverrideWith(CfgAlgo);  // Override with config
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));

            wsClient = new(this);
            Log($"{Time} Connecting to ws://{CfgAlgo.localhost}:{Cfg.WS_PORT}/ws ...");
            wsClient.ConnectAsync($"ws://{CfgAlgo.localhost}:{Cfg.WS_PORT}/ws").Wait();
            wsClient.StartHealthCheck().Wait();

            var utilityOrderFactory = new UtilityOrderFactory(typeof(UtilityOrderEarnings));
            InitializeAlgo(utilityOrderFactory);

            // After release date. Reset the target holdings
            foreach (string ticker in Cfg.Ticker)
            {
                Symbol symbol = Securities[ticker].Symbol;
                DateTime nextReleaseDate = NextReleaseDate(symbol, StartDate);
                ResetTargetHoldings(symbol, nextReleaseDate);
                Schedule.On(DateRules.EveryDay(symbol), TimeRules.Midnight, () => ResetTargetHoldings(symbol, nextReleaseDate));  // Not just at midnight, but on algo start and better keep this, dont overwrite dict.
            }

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 2), FetchTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 2), SetTargetHoldingsFromPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(5)), LogDifferenceTargetHoldingsOrderTickets);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.At(15, 50), RequestStressTestDs);

            foreach (string ticker in ticker)
            {
                var equity = Securities[ticker] as Equity;
                var releaseDate = NextReleaseDate(equity.Symbol, StartDate);
                if (
                    (releaseDate - Time.Date).TotalDays > 3 || // too early
                    (Time.Date > releaseDate) // too late
                )
                {
                    continue;
                }

                UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += (object sender, Symbol underlying) =>
                {
                    Log($"{Time} UnderlyingMovedX: {underlying} 0.5% event fired: FetchTargetPortfolios({underlying})");
                    FetchTargetPortfolios(underlying);
                };
                // UnderlyingMovedX[(equity.Symbol, 0.005m)].UnderlyingMovedXEvent += SnapMarketData;

                Schedule.On(DateRules.On(releaseDate), TimeRules.At(new TimeSpan(0, 23, 0, 0)), () => {
                    Log($"{Time} Clearing MarginalWeightedDNLV, TargetPortfolios and TargetHoldings");
                    MarginalWeightedDNLV.Clear();
                    TargetHoldings.Clear();
                    TargetPortfolios.Clear();
                });
            }
            wsClient.EventHandlerResponseTargetPortfolios += OnTargetPortfolios;
            wsClient.EventHandlerResultStressTestDs += OnResultStressTestDs;
        }

        public override void OnWarmupFinished()
        {
            base.OnWarmupFinished();
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(5)), FetchTargetPortfolios);
        }

        public void SnapMarketData(object sender, Symbol underlying)
        {
            if (!IsMarketOpen(underlying)) return;

            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (snap == null) return;
            if (string.IsNullOrEmpty(snap.Ts)) return;

            MarketDataSnaps[underlying] = MarketDataSnaps.TryGetValue(underlying, out IEnumerable<MarketDataSnapByUnderlying> snaps) ? snaps.Append(snap) : new List<MarketDataSnapByUnderlying> { snap };
        }

        public MarketDataSnapByUnderlying GetMarketDataSnapByUnderlying(Symbol underlying)
        {
            Dictionary<string, OptionQuote> mapSymbolQuote = new();
            DateTime mostRecentPriceUpdate = DateTime.MinValue;
            foreach (var option in Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).Select(kvp => kvp.Value))
            {
                var cache = Securities[option.Symbol].Cache;
                var lastUpdated = cache.LastQuoteBarUpdate > cache.LastOHLCUpdate ? cache.LastQuoteBarUpdate : cache.LastOHLCUpdate;
                mostRecentPriceUpdate = mostRecentPriceUpdate > lastUpdated ? mostRecentPriceUpdate : lastUpdated;
                
                OptionQuote quote = new() { Bid = (float)option.BidPrice, Ask = (float)option.AskPrice };
                mapSymbolQuote[option.Symbol.Value] = quote;
            }

            MarketDataSnapByUnderlying marketDataSnap = new();

            // Return empty snap if no option data is included
            if (mapSymbolQuote.Count == 0)
            {
                Log($"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data because zero quotes fetched. Not subscribed to any options?");                
            }
            else if ((Time - mostRecentPriceUpdate) > TimeSpan.FromMinutes(30))
            {
                Log($"{Time} GetMarketDataSnapByUnderlying {underlying}. Returning empty market data snap because prices are stale. mostRecentPriceUpdate: {mostRecentPriceUpdate}");
            }
            else
            {
                // Ensure current holdings are included
                foreach (var kvp in Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying))
                {
                    string sym = kvp.Key.Value;
                    if (!mapSymbolQuote.ContainsKey(kvp.Key.Value))
                    {
                        OptionQuote quote = new() { Bid = (float)Securities[sym].BidPrice, Ask = (float)Securities[sym].AskPrice };
                        mapSymbolQuote[kvp.Key.Value] = quote;
                    }
                }
                marketDataSnap.Underlying = underlying;
                marketDataSnap.Ts = Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
                marketDataSnap.UnderlyingPrice = (float)Securities[underlying].Price;                
                marketDataSnap.OptionQuotes.Add(mapSymbolQuote);
            }

            return marketDataSnap;
        }

        public override void OnData(Slice slice)
        {
            base.OnData(slice);

            if (IsWarmingUp) return;

            UpdateSweepRatios();
        }

        public void UpdateSweepRatios()
        {
            foreach (var kvp in SweepState)
            {
                if (!orderTickets.ContainsKey(kvp.Key)) continue;

                OrderTicket t = orderTickets[kvp.Key].Any() ? orderTickets[kvp.Key].First() : null;
                if (t != null && kvp.Value[Num2Direction(t.Quantity)].IsSweepScheduled())
                {
                    kvp.Value[Num2Direction(t.Quantity)].UpdateSweepRatio(t);
                }
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null) return;

            ConsumeSignal();
            OrderEvents.Add(orderEvent);
            if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            {
                LogOrderEvent(orderEvent);
            }
            (OrderEventWriters.TryGetValue(Underlying(orderEvent.Symbol), out OrderEventWriter writer) ? writer : OrderEventWriters[orderEvent.Symbol] = new(this, (Equity)Securities[Underlying(orderEvent.Symbol)])).Write(orderEvent);

            lock (orderTickets)
            {
                foreach (var tickets in orderTickets.Values)
                {
                    var removeTickets = tickets.Where(t => orderFilledCanceledInvalid.Contains(t.Status));
                    tickets.RemoveAll(t => removeTickets.Contains(t));

                    // refactor into dedicated function. Avoid sweeping through all tickets.
                    removeTickets.DoForEach(t => {
                        var direction = Num2Direction(t.Quantity);
                        Sweep sweep = SweepState[t.Symbol][direction];                        
                        if (orderStatusFilled.Contains(orderEvent.Status))
                        {
                            sweep.StopSweep();
                        } else
                        {
                            sweep.PauseSweep();
                        }
                    });
                }
            }
            if (orderEvent.Status is OrderStatus.Filled or OrderStatus.PartiallyFilled)
            {
                UpdateOrderFillData(orderEvent);

                CancelOcaGroup(orderEvent);

                var trades = WrapToTrade(orderEvent);
                ApplyToPosition(trades);
                Publish(new TradeEventArgs(trades));  // Continues asynchronously. Sure that's wanted?

                LogOnEventOrderFill(orderEvent);

                RunSignals(orderEvent.Symbol);

                PfRisk.CheckHandleDeltaRiskExceedingBand(orderEvent.Symbol);
                RiskProfiles[Underlying(orderEvent.Symbol)].Update();

                InternalAudit(orderEvent);
                SnapPositions();

                // Earnings algo specific. On Option fills, want to rerun the target portfolio
                if (orderEvent.Symbol.SecurityType == SecurityType.Option)
                {
                    // Remove current TargetHoldings. Expect triggers cancelation of all other orders.
                    TargetPortfolios[orderEvent.Symbol.Underlying] = new();
                    SetTargetHoldingsFromPortfolios();
                    CancelOrdersNotAlignedWithTargetPortfolio();
                    FetchTargetPortfolios(Underlying(orderEvent.Symbol));
                    RequestStressTestDs(Underlying(orderEvent.Symbol));
                }
            }
        }

        public void LogDifferenceTargetHoldingsOrderTickets()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            foreach (var kvp in TargetHoldings)
            {
                Symbol symbol = kvp.Key;
                decimal quantityToOrder = kvp.Value - Portfolio[symbol].Quantity;
                if (quantityToOrder == 0) continue;
                if (!orderTickets.TryGetValue(symbol, out List<OrderTicket> _))
                {
                    var option = (Option)Securities[symbol];
                    IUtilityOrder util = UtilityOrderFactory.Create(this, option, SignalQuantity(symbol, Num2Direction(quantityToOrder)), (option.AskPrice + option.BidPrice)/2);
                    Error($"{Time} No order ticket present for {symbol}. Remaining Quantity to fill: {quantityToOrder}. Util: {util} UUtil: {util.Utility} UEquity: {util.UtilityEquityPosition} UGamma: {util.UtilityGamma}");
                }
            }
        }

        public static string Portfolio2String(TargetPortfolio portfolio)
        {
            if (portfolio == null) return "";
            return string.Join(", ", portfolio.Holdings.Select(h => $"{h.Symbol}:{h.Quantity}"));
        }

        public void SetTargetHoldingsFromPortfolios(Symbol underlying)
        {
            var nextReleaseDate = NextReleaseDate(underlying);
            if ((nextReleaseDate - Time.Date).TotalDays > 3)  // too early
            {
                TargetHoldings = new();
                return;
            } 
            else if (Time.Date > nextReleaseDate)  // too late
            {
                TargetHoldings = new();
                foreach (var kvp in Portfolio.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying))
                {
                    TargetHoldings[kvp.Key] = 0;
                }
            }

            foreach (TargetPortfolio portfolio in TargetPortfolios[underlying].ToList())
            {
                Log($"{Time} {underlying} WeightedAvgDNLV: {portfolio.ResultStressTestDs.WeightedDnlv}, Obj: {portfolio.Objective}, TargetPortfolio: {Portfolio2String(portfolio)}");
                foreach (var kvpHolding in portfolio.Holdings)
                {
                    string option = kvpHolding.Symbol;
                    decimal quantity = kvpHolding.Quantity;

                    // Delaying adding highly liquid options to target portfolio until last trading session before release to allow room for spot moves.
                    Option security = (Option)Securities[option];
                    bool skipToday = IsPresumablyLiquid(security) && Time.Date < nextReleaseDate && quantity < 0;
                    if (skipToday) Log($"{Time} {underlying} Skipping {option} from target portfolio because liquid and today is not release day.");
                    quantity = skipToday ? 0 : quantity;

                    if (TargetHoldings.TryGetValue(option, out decimal currentQuantity))
                    {
                        // To be removed if reduction of abs position comes in.
                        if (currentQuantity * quantity < -0.5m)
                        {
                            Error($"{Time} TargetPortfolio quantities have oppposite sides. {option} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity} Fix API to not send contradicting instructions");
                            // Simulate this whole pf first, ensure it's valid.
                            break;
                        }
                        TargetHoldings[option] = quantity < 0 ? Math.Min(currentQuantity, quantity) : Math.Max(currentQuantity, quantity);
                    }
                    else
                    {
                        TargetHoldings[option] = quantity;
                    }
                }
            }
            Log($"{Time} {underlying} SetTargetHoldingsFromPortfolios: {string.Join(", ", TargetHoldings.Select(kvp => $"{kvp.Key}:{kvp.Value}"))}");
        }

        public IEnumerable<TargetPortfolio> TargetPortfoliosWithoutOppositeQuantities(IEnumerable<TargetPortfolio> ports)
        {
            List<TargetPortfolio> result = new();
            Dictionary<Symbol, decimal> simulatedTargetHoldings = new();
            foreach (TargetPortfolio portfolio in ports.ToList())
            {
                if (portfolio.Objective <= 0)
                {
                    continue;
                }
                bool introducesOppositeQuantities = false;
                foreach (var kvpHolding in portfolio.Holdings)
                {
                    string option = kvpHolding.Symbol;
                    decimal quantity = kvpHolding.Quantity;
                    if (simulatedTargetHoldings.TryGetValue(option, out decimal currentQuantity))
                    {
                        if (currentQuantity * quantity < -0.5m)
                        {
                            Error($"{Time} TargetPortfolio quantities have oppposite sides. {option} TargetHoldingQuantity:{currentQuantity}. PortfolioQuantity: {quantity}. Removing Portfolio: {Portfolio2String(portfolio)}");
                            introducesOppositeQuantities = true;
                            break;
                        }
                        simulatedTargetHoldings[option] = quantity < 0 ? Math.Min(currentQuantity, quantity) : Math.Max(currentQuantity, quantity);
                    }
                }
                if (!introducesOppositeQuantities)
                {
                    result.Add(portfolio);
                }                
            }
            return result;
        }

        public void SetTargetHoldingsFromPortfolios()
        {
            TargetHoldings = new();
            foreach (var kvp in TargetPortfolios)
            {
                Symbol underlying = kvp.Key;
                SetTargetHoldingsFromPortfolios(underlying);
            }
            string pf_str = string.Join(", ", TargetHoldings.Select(kvp => $"{kvp.Key}={kvp.Value}"));
            Log($"{Time} SetTargetHoldingsFromPortfolios: {pf_str}");
        }

        public void OnTargetPortfolios(object sender, ResponseTargetPortfolios responseTargetPortfolios)
        {
            if (responseTargetPortfolios == null)
            {
                Log($"{Time} OnTargetPortfolios: responseTargetPortfolios is null. Ignoring.");
                return;
            }
            var targetPfs = TargetPortfoliosWithoutOppositeQuantities(responseTargetPortfolios.TargetPortfolios);
            Log($"{Time} OnTargetPortfolios: {targetPfs.Count()} portfolios received. IsLastTransmission: {responseTargetPortfolios.IsLastTransmission}");
            if (!targetPfs.Any()) {
                if (responseTargetPortfolios.IsLastTransmission && !ExecuteScheduledTargetPortfolioFetch)
                {
                    Log($"{Time} OnTargetPortfolios: Last transmission contained zero portfolios. Scheduling retry in 5min");
                    ExecuteScheduledTargetPortfolioFetch = true;
                    Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(5)), ScheduledTargetPortfolioFetch);
                }
                return;
            };

            Symbol underlying = Securities[targetPfs.First().Underlying].Symbol;

            foreach (var underlyingStr in targetPfs.Select((p) => p.Underlying).Distinct())
            {
                underlying = Securities[targetPfs.First().Underlying].Symbol;
                TargetPortfolios[underlying] = new();
            }

            foreach (TargetPortfolio pf in targetPfs)
            {
                if (!IsTargetPortfolioCompatibleWithHoldings(pf))
                {
                    continue;
                }

                underlying = Securities[pf.Underlying].Symbol;
                if (TargetPortfolios.TryGetValue(underlying, out List<TargetPortfolio> list))
                {
                    list.Add(pf);
                }
                else
                {
                    TargetPortfolios[underlying] = new List<TargetPortfolio> { pf };
                }

                foreach (var kvp in pf.ResultStressTestDs.MarginalScaledObjectiveByHolding)
                {
                    //Log($"{Time} MarginalWeightedDNLV {kvp.Key}: {kvp.Value}");
                    MarginalWeightedDNLV[Securities[kvp.Key].Symbol] = kvp.Value;
                    decimal q = Securities[kvp.Key].Holdings.Quantity;                    
                    if ((decimal)kvp.Value * q < 0)
                    {
                        Error($"{Time} MarginalWeightedDNLV {kvp.Key}: {kvp.Value}. Negative marginal objective. Revert this position in target holdings.");
                    }
                }
            }

            foreach (var kvp in TargetPortfolios)
            {
                TargetPortfolios[kvp.Key] = kvp.Value.Where(p => IsTargetPortfolioCompatibleWithHoldings(p)).ToList();
            }
            SetTargetHoldingsFromPortfolios();
            Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromSeconds(1)), () => RunSignals());
        }

        public void OnResultStressTestDs(object sender, ResultStressTestDs resultStressTestDs)
        {
            if (resultStressTestDs == null)
            {
                Log($"{Time} OnResultStressTestDs: resultStressTestDs is null. Ignoring.");
                return;
            }
            if (!Securities.ContainsKey(resultStressTestDs.Underlying))
            {
                Log($"{Time} OnResultStressTestDs: {resultStressTestDs.Underlying} not subscribed. Ignoring.");
                return;
            }
            Symbol underlying = Securities[resultStressTestDs.Underlying].Symbol;
            LastDeltaAcrossDs[underlying] = resultStressTestDs.DeltaTotalAcrossDs;
            string dsString = string.Join(",\n", resultStressTestDs.DsDnlv.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key[..Math.Min(4, kvp.Key.Length)]}:{kvp.Value}"));
            Log($"{Time} OnResultStressTestDs: {resultStressTestDs.Underlying} @ {resultStressTestDs.Ts}, DeltaTotalAcrossDs: {resultStressTestDs.DeltaTotalAcrossDs}, DeltaTotal: {resultStressTestDs.DeltaTotal}\n{dsString}");

            foreach (var kvp in resultStressTestDs.MarginalScaledObjectiveByHolding)
            {
                MarginalWeightedDNLV[Securities[kvp.Key].Symbol] = kvp.Value;
            }
        }

        internal bool IsPresumablyLiquid(Option option)
        {
            var ocw = OptionContractWrap.E(this, option, Time.Date);
            var absDelta = Math.Abs(ocw.Delta(MidIV(option.Symbol)));
            return ocw.DaysToExpiration() / 365 < 0.1 && absDelta < 0.8 && absDelta > 0.2 ;
        }

        public void CancelOrdersNotAlignedWithTargetPortfolio()
        {
            foreach (var kvp in orderTickets.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Any()))
            {
                Symbol symbol = kvp.Key;
                var tickets = kvp.Value;
                if (tickets.Count > 1)
                {
                    Log($"{Time} CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, Count={tickets.Count}");
                    tickets.DoForEach(ticket => Cancel(ticket));
                }
                var ticket = tickets.First();
                if (TargetHoldings.TryGetValue(symbol, out decimal targetQuantity))
                {
                    var quantityIfFilled = ticket.Quantity + Portfolio[symbol].Quantity;
                    bool shouldCancel = (targetQuantity < 0 ? quantityIfFilled < targetQuantity : quantityIfFilled > targetQuantity) || ticket.Quantity * targetQuantity < 0;
                    if (shouldCancel)
                    {
                        Cancel(ticket);
                        Log($"{Time} CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, TicketQuantity={ticket.Quantity}, PortfolioQuantity={Portfolio[symbol].Quantity}, TargetQ={targetQuantity}");
                    }
                }
            }
        }

        internal bool IsTargetPortfolioCompatibleWithHoldings(TargetPortfolio portfolio)
        {
            Symbol underlying = Securities[portfolio.Underlying].Symbol;
            var options = Portfolio.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Quantity != 0 && kvp.Key.Underlying == underlying).Select(kvp => kvp.Key).ToList();

            foreach (var option in options)
            {
                var holdings = portfolio.Holdings.Where(h => h.Symbol == option.Value).FirstOrDefault();
                if (holdings == null)
                {
                    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, No Quantity in Target Portfolio");
                    return false;
                }
                //var algoQuantity = Portfolio[option].Quantity;
                //if (holdings.Quantity * (float)algoQuantity < 0)
                //{
                //    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, holdings.Quantity={holdings.Quantity}, algoQuantity={algoQuantity}");
                //    return false;
                //}
                //if (Math.Abs((float)algoQuantity) > Math.Abs((float)holdings.Quantity))
                //{
                //    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, AlgoQuantity={algoQuantity}, PortfolioQuantity={holdings.Quantity}");
                //    return false;
                //}
            }
            return true;
        }

        public DateTime SubtractBusinessDays(DateTime dt, int days)
        {
            var startIteratingFrom = dt.AddDays(-(days+7));
            SecurityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, symbolSubscribed, SecurityType.Equity);
            // first digit ensure looking beyond past holidays. Second digit is days of trading days to warm up.
            int i = 0;
            foreach (var day in QuantConnect.Time.EachTradeableDay(SecurityExchangeHours, startIteratingFrom, dt).Reverse())
            {
                if (i == days)
                {
                    return day;
                }
                i++;
            }
            Error($"SubtractBusinessDays: Could not find {days} trading days before {dt}");
            return (dt - TimeSpan.FromDays(days)) + dt.TimeOfDay;
        }

        public static bool IsValidMarketDataSnap(MarketDataSnapByUnderlying snap)
        {
            return
                snap != null &&
                !string.IsNullOrEmpty(snap.Ts) &&
                snap.OptionQuotes.Count > 0;
        }

        public IEnumerable<Core.IO.Holding> PortfolioOptionHoldings(Symbol underlying)
        {
            return Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).Select(kvp => new Core.IO.Holding() { Symbol = kvp.Key.Value, Quantity = (int)kvp.Value.Quantity });
        }

        public void FetchTargetPortfolios(Symbol underlying)
        {
            // ToDo: Need a new class. History of MarketDataSnaps every x% change + latest one when requested. history for skew calculation... 

            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

            // Only when release date is upcoming. Notthing to fetch afterwards.
            var releaseDate = NextReleaseDate(underlying);
            if (
                (releaseDate - Time.Date).TotalDays > 3 || // too early
                (Time.Date > releaseDate) // too late
                )
            {
                return;
            }

            // Build request
            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                Underlying = underlying,
            };
            requestTargetPortfolios.Holdings.Add(PortfolioOptionHoldings(underlying));
            
            var startTime = SubtractBusinessDays(Time, 1);
            // DateTime.Parse(s.Ts, "yyyy-MM-ddTHH:mm:ss")

            // Historical snaps are used to calculate a smoothened skew. Latest snap's prices is used to calculate the current target portfolio.
            //if (MarketDataSnaps.TryGetValue(underlying, out IEnumerable<MarketDataSnapByUnderlying> snaps))
            //{
            //    requestTargetPortfolios.MarketDataSnaps.Add(snaps.Where(s => IsValidMarketDataSnap(s) && DateTimeOffset.Parse(s.Ts, CultureInfo.InvariantCulture) >= startTime));
            //}
            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (IsValidMarketDataSnap(snap))
            {
                requestTargetPortfolios.MarketDataSnaps.Add(snap);

                // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
                Log($"{Time} FetchTargetPortfolios: {underlying}");
                if (!LiveMode)
                {
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                    wsClient.semaphore.Wait();
                    wsClient.semaphore.Dispose();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                }
            }
            else
            {
                Log($"{Time} FetchTargetPortfolios: {underlying}. No valid snap found. Not fetching target portfolios. Scheduling next try in 1min");
                Schedule.On(DateRules.Today, TimeRules.At(Time.TimeOfDay + TimeSpan.FromMinutes(1)), () => FetchTargetPortfolios(underlying));
            }
        }
        public void RequestStressTestDs()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(ticker => RequestStressTestDs(Securities[ticker].Symbol));
        }

        public void RequestStressTestDs(Symbol underlying)
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            // Only when release date is upcoming. Notthing to fetch afterwards.
            var releaseDate = NextReleaseDate(underlying);
            if (
                (releaseDate - Time.Date).TotalDays > 3 || // too early
                (Time.Date > releaseDate) // too late
                )
            {
                return;
            }
            Log($"{Time} RequestStressTestDs: {underlying}");

            // Build request
            RequestStressTestDs requestStressTestDs = new()
            {
                Ts = Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                Underlying = underlying,
            };
            requestStressTestDs.Holdings.Add(PortfolioOptionHoldings(underlying));

            // DateTime.Parse(s.Ts, "yyyy-MM-ddTHH:mm:ss")

            // var startTime = SubtractBusinessDays(Time, 1);
            // Historical snaps are used to calculate a smoothened skew. Latest snap's prices is used to calculate the current target portfolio.
            // requestStressTestDs.MarketDataSnaps.Add(MarketDataSnaps[underlying].Where(s => IsValidMarketDataSnap(s) && DateTimeOffset.Parse(s.Ts, CultureInfo.InvariantCulture) >= startTime));
            var snap = GetMarketDataSnapByUnderlying(underlying);
            if (IsValidMarketDataSnap(snap))
            {
                if (!LiveMode)
                {
                    requestStressTestDs.MarketDataSnaps.Add(snap);
                    wsClient.SetSemaphore(new SemaphoreSlim(0, 1));
                    _ = wsClient.SendMessageAsync(requestStressTestDs);
                    wsClient.semaphore.Wait();
                    wsClient.semaphore.Dispose();
                }
                else
                {
                    _ = wsClient.SendMessageAsync(requestStressTestDs);
                }
            }
            else
            {
                Log($"{Time} RequestStressTestDs: {underlying}. No valid snap found. Not fetching target portfolios.");
            }
        }

        public void ScheduledTargetPortfolioFetch()
        {
            if (ExecuteScheduledTargetPortfolioFetch)
            {
                FetchTargetPortfolios();
            }
        }

        public void FetchTargetPortfolios()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;
            ExecuteScheduledTargetPortfolioFetch = false;

            Cfg.Ticker.DoForEach(ticker => FetchTargetPortfolios(Securities[ticker].Symbol));
        }

        public void ResetTargetHoldings(Symbol underlying, DateTime releaseDate)
        {
            // Release date is referred to as last trading session before earnings release, so adding a day.
            if (Time.Date <= releaseDate.Date + TimeSpan.FromDays(1))
            {
                return;
            }
            foreach (var kvp in TargetHoldings)
            {
                Security sec = Securities[kvp.Key];
                if (sec.Type == SecurityType.Equity && sec.Symbol == underlying)
                {
                    TargetHoldings[kvp.Key] = 0;
                    Log($"{Time} ResetTargetHoldings: Symbol={kvp.Key}, Quantity=0");
                }
                else if (sec.Type == SecurityType.Option && sec.Symbol.Underlying == underlying)
                {
                    TargetHoldings[kvp.Key] = 0;
                    Log($"{Time} ResetTargetHoldings: Symbol={kvp.Key}, Quantity=0");
                }
            }
        }

        public override void OnEndOfAlgorithm()
        {
            base.OnEndOfAlgorithm();
            wsClient.Dispose();
        }
    }
}
