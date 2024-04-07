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
        private Dictionary<Symbol, List<Portfolio>> TargetPortfolios = new();

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
            wsClient.ConnectAsync($"ws://{CfgAlgo.localhost}:8000/ws/target-portfolios").Wait();

            var utilityOrderFactory = new UtilityOrderFactory(typeof(UtilityOrderEarnings));
            InitializeAlgo(utilityOrderFactory);

            // After release date. Reset the target holdings
            foreach (string ticker in Cfg.Ticker)
            {
                Symbol symbol = Securities[ticker].Symbol;
                DateTime releaseDate = EarningsBySymbol[Securities[ticker].Symbol].Where(ea => ea.Date > StartDate).Select(ea => ea.Date).Min();
                Schedule.On(DateRules.EveryDay(symbol), TimeRules.Midnight, () => ResetTargetHoldings(symbol, releaseDate));
            }

            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 2), FetchTargetPortfolios);
            Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.AfterMarketOpen(symbolSubscribed, 2), SetTargetHoldingsFromPortfolios);

            wsClient.EventHandlerPortfolios += OnTargetPortfolios;
        }
        public override void OnOrderEvent(OrderEvent orderEvent)
        {
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
                    tickets.RemoveAll(t => orderFilledCanceledInvalid.Contains(t.Status));
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
                    foreach (var kvp in TargetPortfolios)
                    {
                        TargetPortfolios[kvp.Key] = kvp.Value.Where(p => IsTargetPortfolioCompatibleWithHoldings(p)).ToList();
                    }
                    SetTargetHoldingsFromPortfolios();
                    CancelOrdersNotAlignedWithTargetPortfolio();
                    FetchTargetPortfolios(Underlying(orderEvent.Symbol));
                }
            }
        }

        public string Portfolio2String(Portfolio portfolio)
        {
            return string.Join(", ", portfolio.Holdings.Select(h => $"{h.Symbol}:{h.Quantity}"));
        }

        public void SetTargetHoldingsFromPortfolios(Symbol underlying)
        {
            if ((NextReleaseDate(underlying) - Time.Date).TotalDays > 3)
            {
                TargetHoldings = new();
                return;
            }

            foreach (Portfolio portfolio in TargetPortfolios[underlying].ToList())
            {
                Log($"{Time} {underlying} Obj: {portfolio.Objective} TargetPortfolio: {Portfolio2String(portfolio)}");
                foreach (var kvpHolding in portfolio.Holdings)
                {
                    string option = kvpHolding.Symbol;
                    decimal quantity = (decimal)Math.Round(kvpHolding.Quantity, 0);
                    if (TargetHoldings.TryGetValue(option, out decimal currentQuantity))
                    {
                        if (currentQuantity * quantity < 0)
                        {
                            throw new Exception($"{Time} TargetPortfolio quantities have oppposite sides. Fix API to not send contradicting instructions");
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

        public void OnTargetPortfolios(object sender, Portfolios portfolios)
        {
            Log($"{Time} OnTargetPortfolios: {portfolios.Portfolios_.Count} portfolios received");
            foreach (Portfolio portfolio in portfolios.Portfolios_)
            {
                if (!IsTargetPortfolioCompatibleWithHoldings(portfolio))
                {
                    continue;
                }

                Symbol underlying = Securities[portfolio.Underlying].Symbol;
                if (TargetPortfolios.TryGetValue(underlying, out List<Portfolio> list))
                {
                    list.Add(portfolio);
                }
                else
                {
                    TargetPortfolios[underlying] = new List<Portfolio> { portfolio };
                }
            }

            foreach (var kvp in TargetPortfolios)
            {
                TargetPortfolios[kvp.Key] = kvp.Value.Where(p => IsTargetPortfolioCompatibleWithHoldings(p)).ToList();
            }
            SetTargetHoldingsFromPortfolios();
            CancelOrdersNotAlignedWithTargetPortfolio();
        }

        public void CancelOrdersNotAlignedWithTargetPortfolio()
        {
            foreach (var kvp in orderTickets.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Value.Any()))
            {
                Symbol symbol = kvp.Key;
                var tickets = kvp.Value;
                if (tickets.Count() > 1)
                {
                    Log($"{Time} CancelOrdersNotAlignedWithTargetPortfolio: Symbol={symbol}, Count={tickets.Count()}");
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

        public bool IsTargetPortfolioCompatibleWithHoldings(Portfolio portfolio)
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
                var algoQuantity = Portfolio[option].Quantity;
                if (holdings.Quantity * (float)algoQuantity < 0)
                {
                    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, holdings.Quantity={holdings.Quantity}, algoQuantity={algoQuantity}");
                    return false;
                }
                if (Math.Abs((float)algoQuantity) > Math.Abs((float)holdings.Quantity))
                {
                    Log($"{Time} Removing TargetPortfolio. Symbol={option.Value}, AlgoQuantity={algoQuantity}, PortfolioQuantity={holdings.Quantity}");
                    return false;
                }
            }
            return true;
        }

        public void FetchTargetPortfolios(Symbol underlying)
        {
            // Only when release date is upcoming. Notthing to fetch afterwards.
            if ((NextReleaseDate(underlying) - Time.Date).TotalDays > 3)
            {
                return;
            }

            // Build request
            RequestTargetPortfolios requestTargetPortfolios = new()
            {
                Ts = Time.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                Underlying = underlying,
                UnderlyingPrice = (float)Securities[underlying].Price,
            };
            requestTargetPortfolios.Holdings.Add(Portfolio.Where(kvp => kvp.Value.Quantity != 0 && kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).ToDictionary(kvp => kvp.Key.Value, kvp => (float)kvp.Value.Quantity));
            Dictionary<string, OptionQuote> mapSymbolQuote = new();
            foreach (var option in Securities.Where(kvp => kvp.Key.SecurityType == SecurityType.Option && kvp.Key.Underlying == underlying).Select(kvp => kvp.Value))
            {
                if (IsPriceStale(option.Symbol))
                {
                    continue;
                }
                OptionQuote quote = new() { Bid = (float)option.BidPrice, Ask = (float)option.AskPrice };
                mapSymbolQuote[option.Symbol.Value] = quote;
            }
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

            requestTargetPortfolios.OptionQuotes.Add(mapSymbolQuote);

            // Send request - response is handled in WsClient.ResponseReceived -> EventHandlers
            Log($"{Time} FetchTargetPortfolios: {underlying}");
            if (!LiveMode)
            {
                wsClient.SetSemaphore(new SemaphoreSlim(0));
                _ = wsClient.SendMessageAsync(requestTargetPortfolios);
                wsClient.semaphore.Wait(); //  timeout: TimeSpan.FromMinutes(3));
            }
            else
            {
                _ = wsClient.SendMessageAsync(requestTargetPortfolios);
            }
        }

        public async void FetchTargetPortfolios()
        {
            if (IsWarmingUp || !IsMarketOpen(symbolSubscribed)) return;

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

        public void SetTargetHoldingsFromConfig()
        {
            if (LiveMode || !CfgAlgo.TargetHoldings.Any()) return;

            foreach ((string ticker, decimal quantity) in CfgAlgo.TargetHoldings.Select(h => (h.Key, h.Value)))
            {
                try
                {
                    if (!Securities.Keys.Select(s => s.Value).Contains(ticker))
                    {
                        string underlyingTicker = ticker.Split(' ')[0];
                        var optionSymbol = QuantConnect.Symbol.CreateCanonicalOption(underlyingTicker, Market.USA, $"?{underlyingTicker}");
                        var contractSymbols = OptionChainProvider.GetOptionContractList(optionSymbol, Time);
                        contractSymbols.Where(s => s.Value == ticker).DoForEach(contractSymbol => AddOptionContract(contractSymbol, Resolution.Second, fillForward: false, extendedMarketHours: true));
                    }

                    var symbol = Securities[ticker].Symbol;
                    TargetHoldings[symbol] = quantity;

                    Log($"{Time} SetTargetHoldings: Symbol={ticker}, Quantity={quantity}");
                }
                catch (Exception e)
                {
                    Error($"{Time} {e.Message} in SetTargetHoldings: {ticker} ");
                    throw e;
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
