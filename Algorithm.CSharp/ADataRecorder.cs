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
using System.Collections.Generic;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Brokerages;
using QuantConnect.Data;
using QuantConnect.Securities;
using QuantConnect.Data.Market;
using QuantConnect.Util;
using QuantConnect.Algorithm.CSharp.Core;
using Newtonsoft.Json;
using QuantConnect.Configuration;
using System.Linq;
using QuantConnect.Data.Consolidators;
using System;
using System.Text.RegularExpressions;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// 
    /// </summary>
    public partial class ADataRecorder : QCAlgorithm
    {
        Resolution resolution;
        ADataRecorderConfig Cfg;
        DiskDataCacheProvider _diskDataCacheProvider = new();
        readonly Dictionary<(Resolution, Symbol, TickType), LeanDataWriter> writers = new();
        public HashSet<Symbol> equities = new();
        public HashSet<string> optionTicker = new();
        public Dictionary<Symbol, QuoteBarConsolidator> QuoteBarConsolidators = new();
        public Dictionary<Symbol, TradeBarConsolidator> TradeBarConsolidators = new();
        string dataFolderTmp = "";
        /// <summary>
        /// Initialise the data and resolution required, as well as the cash and start-end dates for your algorithm. All algorithms must initialized.
        /// </summary>
        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<ADataRecorderConfig>(File.ReadAllText("ADataRecorderConfig.json"));
            Cfg.OverrideWithEnvironmentVariables<ADataRecorderConfig>();
            File.Copy("./ADataRecorderConfig.json", Path.Combine(Globals.PathAnalytics, "ADataRecorderConfig.json"));
            dataFolderTmp = string.IsNullOrEmpty(Cfg.DataFolderTmp) ? Config.Get("data-folder") : Cfg.DataFolderTmp;
            Log("DATA FOLDER TMP: " + dataFolderTmp);

            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(30_000);
            SetBrokerageModel(BrokerageName.Default, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;

            SetSecurityInitializer(new SecurityInitializerDataRecorder(BrokerageModel, this, new FuncSecuritySeeder(NoSeeding)));

            // Subscriptions
            HashSet<string> tickers = optionTicker = Cfg.Ticker;
            Symbol symbolSubscribed = null;

            int subscriptions = 0;
            foreach (string ticker in tickers)
            {
                var equity = AddEquity(ticker, resolution: resolution, fillForward: false);
                symbolSubscribed ??= equity.Symbol;

                subscriptions++;
                equities.Add(equity.Symbol);

                if (optionTicker.Contains(ticker))
                {
                    var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
                    var subscribedSymbols = AddOptionIfScoped(option);
                    subscriptions += subscribedSymbols.Count;
                }
            }

            Log($"Subscribing to {subscriptions} securities");
            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            // WARMUP
            SetWarmUp(0);

            QuoteBarConsolidators.DoForEach((kvp) => SubscriptionManager.AddConsolidator(kvp.Key, kvp.Value));
            TradeBarConsolidators.DoForEach((kvp) => SubscriptionManager.AddConsolidator(kvp.Key, kvp.Value));

            //Schedule.On(DateRules.EveryDay(symbolSubscribed), TimeRules.Every(TimeSpan.FromMinutes(1)), CopyTmp2DataFolder);
        }

        /// <summary>
        /// The algorithm manager calls events in the following order:
        /// Scheduled Events
        /// Consolidation event handlers
        /// OnData event handler
        /// </summary>
        public override void OnData(Slice slice)
        {
            // Move into consolidator events. Not driving any biz logic.
            // Record data for restarts and next day comparison with history. Avoid conflict with Paper on same instance by running this for live mode only.
            if (LiveMode)
            {
                slice.QuoteBars.Values.ToList().ForEach((b) => RecordQuoteBar(b));
                slice.Bars.Values.ToList().ForEach((b) => RecordTradeBar(b));
            }
        }
        public void RecordQuoteBar(QuoteBar quoteBar, Resolution? res = null)
        {
            Resolution thisResolution = res ?? resolution;
            var writer = writers.TryGetValue((thisResolution, quoteBar.Symbol, TickType.Quote), out LeanDataWriter dataWriter)
                ? dataWriter
                : writers[(thisResolution, quoteBar.Symbol, TickType.Quote)] = new LeanDataWriter(thisResolution, quoteBar.Symbol, dataFolderTmp, TickType.Quote, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
            writer.Write(new List<BaseData>() { quoteBar });
        }

        public void RecordTradeBar(TradeBar tradeBar, Resolution? res = null)
        {
            Resolution thisResolution = res ?? resolution;
            var writer = writers.TryGetValue((thisResolution, tradeBar.Symbol, TickType.Trade), out LeanDataWriter dataWriter)
                ? dataWriter
                : writers[(thisResolution, tradeBar.Symbol, TickType.Trade)] = new LeanDataWriter(thisResolution, tradeBar.Symbol, dataFolderTmp, TickType.Trade, _diskDataCacheProvider, writePolicy: WritePolicy.Merge);
            writer.Write(new List<BaseData>() { tradeBar });
        }

        public IEnumerable<BaseData> NoSeeding(Security security) => Enumerable.Empty<BaseData>();

        public List<Symbol> AddOptionIfScoped(Symbol option)
        {
            var contractSymbols = OptionChainProvider.GetOptionContractList(option, Time);
            List<Symbol> subscribedSymbol = new();
            foreach (var symbol in contractSymbols)
            {
                if ( Securities.ContainsKey(symbol) && Securities[symbol].IsTradable ) continue;  // already subscribed

                if (symbol.ID.OptionStyle == OptionStyle.American)
                {
                    AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false);
                    Log($"{Time} topic=UNIVERSE, msg=Adding {symbol}. Scoped.");
                    subscribedSymbol.Add(symbol);
                }
            }
            return subscribedSymbol;
        }
       
        public override void OnEndOfAlgorithm()
        {
            _diskDataCacheProvider.DisposeSafely();
        }
        
        /// <summary>
        /// Copy any .zip file in resolution folder second and minute where filenames are prefixed with the current date.
        /// </summary>
        public void CopyTmp2DataFolder()
        {
            string dt = Time.ToString("yyyyMMdd");
            Regex pattern = new($".*[second|minute].*{dt}.*\\.zip$");

            string[] files = Directory.GetFiles(dataFolderTmp, "*", SearchOption.AllDirectories).Where(f => pattern.IsMatch(f)).ToArray();
            Log($"{Time} - CopyTmp2DataFolder. Pattern: {pattern} Found {files.Length} files to copy from {dataFolderTmp} to data folder");
            foreach (string file in files)
            {
                // Extract the path of the file after dataFolderTmp
                string dest = file.Replace("dataLive", "data");
                try
                {
                    // Create folder recursively if it doesn't exist
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    //var sourceFile = new FileInfo(file);
                    //sourceFile.CopyTo(dest, true);

                    //File.Copy(file, dest, true);

                    using (var inf = new FileStream(
                        file,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite))
                    {
                        using (var outf = new FileStream(dest, FileMode.Create))
                        {
                            int a;
                            while ((a = inf.ReadByte()) != -1)
                            {
                                outf.WriteByte((byte)a);
                            }
                            inf.Close();
                            inf.Dispose();
                            outf.Close();
                            outf.Dispose();
                        }
                    }
                    Log($"Copied {file} to {dest}");
                }
                catch (Exception ex)
                {
                    Log($"Error copying {file} to {dest}: {ex.Message}");
                }                
            }
        }
    }
}
