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
using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json;
using QuantConnect.Algorithm.CSharp.Core;
using QuantConnect.Securities.Equity;
using System.IO;
using System.Linq;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Algorithm.CSharp.Earnings;
using QuantConnect.Algorithm.Framework.Selection;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using Merlin;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.PerformanceAndRegression
{
    /// <summary>
    /// 
    /// </summary>
    public class PerformanceAndRegression : Foundations
    {
        private EarningsAlgorithmConfig CfgAlgo;
        private readonly string CfgAlgoName = "EarningsAlgorithmConfig.json";

        public override void Initialize()
        {
            Cfg = JsonConvert.DeserializeObject<FoundationsConfig>(File.ReadAllText(FoundationsConfigFileName));
            Cfg.OverrideWithEnvironmentVariables<FoundationsConfig>();
            CfgAlgo = JsonConvert.DeserializeObject<EarningsAlgorithmConfig>(File.ReadAllText(CfgAlgoName));
            CfgAlgo.OverrideWithEnvironmentVariables<EarningsAlgorithmConfig>();
            Cfg.OverrideWith(CfgAlgo); // Override with config
            File.Copy($"./{FoundationsConfigFileName}", Path.Combine(Globals.PathAnalytics, FoundationsConfigFileName));
            File.Copy($"./{CfgAlgoName}", Path.Combine(Globals.PathAnalytics, CfgAlgoName));
            
            UniverseSettings.Resolution = resolution = Resolution.Second;
            SetStartDate(Cfg.StartDate);
            SetEndDate(Cfg.EndDate);
            SetCash(1_000_000);
            SetBrokerageModel(Brokerages.BrokerageName.InteractiveBrokersBrokerage, AccountType.Margin);
            UniverseSettings.DataNormalizationMode = DataNormalizationMode.Raw;
            UniverseSettings.Leverage = 10;
            Portfolio.MarginCallModel = MarginCallModel.Null;

            EarningsAnnouncements = JsonConvert.DeserializeObject<EarningsAnnouncement[]>(File.ReadAllText(Path.Combine(Globals.DataFolder, "symbol-properties", "EarningsAnnouncements.json")));

            EarningsBySymbol = EarningsAnnouncements.GroupBy(ea => ea.Symbol).ToDictionary(g => g.Key, g => g.ToArray());

            securityInitializer = new SecurityInitializerMine(BrokerageModel, this, new FuncSecuritySeeder(GetLastKnownPricesTradeOrQuote), Cfg.VolatilityPeriodDays);
            SetSecurityInitializer(securityInitializer);

            AssignCachedFunctions();

            // Subscriptions
            optionTicker = Cfg.Ticker;
            ticker = optionTicker;
            symbolSubscribed = null;
            liquidateTicker = Cfg.LiquidateTicker;

            SetUniverseSelection(new ManualUniverseSelectionModel(equities));

            // After release date. Reset the target holdings
            string tickerStr = Cfg.Ticker.First();
            Equity equity = AddEquity(tickerStr, resolution: resolution, Market.USA, fillForward: false, extendedMarketHours: true);
            
            var option = QuantConnect.Symbol.CreateCanonicalOption(equity.Symbol, Market.USA, $"?{equity.Symbol}");
            options.Add(option);

            TestPerformance(equity, new DateTime(2025, 12, 24));
            
            OnEndOfAlgorithm();
        }

        public List<decimal> GetPrices(decimal strike, int n = 100)
        {
            List<decimal> prices = new();
            var random = new Random();
            var stdDev = 5.0; // Define standard deviation (e.g., $5 around strike)

            for (int i = 0; i < n; i++)
            {
                // Generate Normally Distributed Random Price using Box-Muller
                double u1 = 1.0 - random.NextDouble();
                double u2 = 1.0 - random.NextDouble();
                double randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

                // Scale and shift by strike
                decimal randomPrice = strike + (decimal)(stdDev * randStdNormal);

                // Ensure price is positive
                prices.Add(Math.Max(0.01m, randomPrice));
            }

            return prices;
        }

        /// <summary>
        /// Contrast the speed of calculating implied volatilities for
        /// EU options
        /// American options using QLNet
        /// American options using Merlin
        ///
        /// All with a fixed risk free rate and cash dividends.
        /// </summary>
        /// <param name="symbol"></param>
        public void TestPerformance(
            Equity underlying,
            DateTime calculationDate,
            decimal strike = 100,
            int n = 100,
            decimal priceOption = 5
            )
        {
            var tenors = Enumerable.Range(0, 10)
                .Select(i => 0 + (2.0 - 0.01) * i / 9.0)
                .ToArray();

            var testContractsEu = new List<OptionContractWrap>();
            var testContractsAm = new List<OptionContractWrap>();


            foreach (var tenor in tenors)
            {
                var expiry = calculationDate.AddDays(tenor * 365);

                // Create Call ;
                Symbol s = QuantConnect.Symbol.CreateOption(underlying.Symbol, Market.USA, OptionStyle.American, OptionRight.Call, strike, expiry);
                AddOptionContract(s, resolution: Resolution.Daily, fillForward: false, extendedMarketHours: false);
                Option o = (Option)Securities[s];
                testContractsAm.Add(OptionContractWrap.E(this, o, calculationDate));
                
                // Create Put
                s = QuantConnect.Symbol.CreateOption(underlying.Symbol, Market.USA, OptionStyle.American, OptionRight.Put, strike, expiry);
                AddOptionContract(s, resolution: Resolution.Daily, fillForward: false, extendedMarketHours: false);
                o = (Option)Securities[s];
                testContractsAm.Add(OptionContractWrap.E(this, o, calculationDate));

                // Create Call
                s = QuantConnect.Symbol.CreateOption(underlying.Symbol, Market.USA, OptionStyle.European, OptionRight.Call, strike, expiry);
                AddOptionContract(s, resolution: Resolution.Daily, fillForward: false, extendedMarketHours: false);
                o = (Option)Securities[s];
                testContractsEu.Add(OptionContractWrap.E(this, o, calculationDate));
                
                // Create Put
                s = QuantConnect.Symbol.CreateOption(underlying.Symbol, Market.USA, OptionStyle.European, OptionRight.Put, strike, expiry);
                AddOptionContract(s, resolution: Resolution.Daily, fillForward: false, extendedMarketHours: false);
                o = (Option)Securities[s];
                testContractsEu.Add(OptionContractWrap.E(this, o, calculationDate));
            }

            var prices = GetPrices(strike, n);

            // var msEu = LogPerformance("EU Options", testContractsEu, prices, priceOption);
            // var msAmQl = LogPerformance("AM Options QLNet", testContractsAm, prices, priceOption);
            // var msAmMerlinCuda = LogPerformanceMerlin("AM Options Merlin Cuda", testContractsAm, prices, calculationDate, priceOption);
            // RegressIVDotNetMerlinCPU(testContractsAm, prices, priceOption);
            // RegressPriceDotNetMerlinCPU(testContractsAm, prices, 0.3);
            RegressPriceDotNetMerlinGPU(testContractsAm, prices, 0.3);
            RegressIVDotNetMerlinGPU(testContractsAm, prices, priceOption);
            
            // AM Options QNet takes 30x longer. Dont even have dividends yet.
            // Log($"AM Options QLNet took {(msAmQl / msEu):F2} longer than vanilla eu options");
            // Log($"AM Options AmMerlinCuda took {(msAmMerlinCuda / msEu):F2} longer than vanilla eu options");
            
            // Log($"AM Options QLNet took {(msAmQl / msAmQl):F2} longer than vanilla am options");
            // Log($"AM Options AmMerlinCuda took {(msAmMerlinCuda / msAmQl):F2} longer than vanilla am options");
            
        }

        public double LogPerformance(String tag, List<OptionContractWrap> contracts, List<decimal> prices, decimal priceOption)
        {
            var sw = Stopwatch.StartNew();
            var totalCalculations = 0;

            foreach (var ocw in contracts)
            {
                double sumIv = 0;
                int count = 0;

                foreach (decimal price in prices)
                {
                    double iv = ocw.IV(priceOption, price, 0.001);
                    sumIv += iv == null ? 0 : iv;
                    count++;
                    totalCalculations++;
                }

                var avgIv = count > 0 ? sumIv / count : 0;
                Log($"Contract: {ocw.Contract.Symbol} | Avg IV: {avgIv:F4}");
            }

            sw.Stop();

            double totalMs = sw.Elapsed.TotalMilliseconds;
            var avgMsPerIv = totalCalculations > 0 ? totalMs / totalCalculations : 0;

            Log($"--- Performance Metrics {tag}---");
            Log($"Contracts Measured: {contracts.Count}");
            Log($"Total IV Calcs: {totalCalculations}");
            Log($"Total Time: {totalMs:F2} ms");
            Log($"Average Time/Calc: {avgMsPerIv:F4} ms");

            return totalMs;
        }
        
        /// <summary>
        /// 20260117 15:42:56.614 TRACE:: Log: -----------------------------------------------------------------------------------------------------
        /// 20260117 15:42:56.614 TRACE:: Log: Symbol               | QLNet IV   | Merlin IV  | Diff IV    | QL ms    | Merlin ms  | TsMerlin / TsQL
        /// 20260117 15:42:56.614 TRACE:: Log: -----------------------------------------------------------------------------------------------------
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   251224C00100000 |      0.00% |    128.47% |  128.4663% |   33.708 |   1770.761 | 52.533
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   251224P00100000 |      0.00% |     60.14% |   60.1450% |   13.514 |   1934.321 | 143.135
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260314C00100000 |     24.03% |     23.88% |   -0.1537% |  417.272 |   2728.685 | 6.539
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260314P00100000 |     24.83% |     24.83% |    0.0072% |  311.932 |   2998.865 | 9.614
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260603C00100000 |     17.96% |     17.76% |   -0.2079% |  913.821 |   2911.554 | 3.186
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260603P00100000 |     16.06% |     16.08% |    0.0195% |  555.332 |   3130.248 | 5.637
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260823C00100000 |     15.30% |     15.13% |   -0.1705% |  884.443 |   2675.750 | 3.025
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   260823P00100000 |     12.15% |     12.03% |   -0.1217% |  742.303 |   2983.737 | 4.020
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   261111C00100000 |     13.85% |     13.63% |   -0.2140% | 1211.896 |   2881.319 | 2.378
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   261111P00100000 |      9.58% |      9.46% |   -0.1187% | 1230.372 |   2986.560 | 2.427
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   270131C00100000 |     12.77% |     12.61% |   -0.1570% | 1464.751 |   2711.656 | 1.851
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   270131P00100000 |      7.98% |      7.73% |   -0.2420% | 1151.878 |   3048.438 | 2.646
        /// 20260117 15:42:56.614 TRACE:: Log: FDX   270422C00100000 |     12.07% |     11.93% |   -0.1397% | 1749.084 |   2639.899 | 1.509
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   270422P00100000 |      6.55% |      6.29% |   -0.2608% | 1370.453 |   2891.226 | 2.110
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   270711C00100000 |     11.55% |     11.35% |   -0.1917% | 1994.243 |   2534.029 | 1.271
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   270711P00100000 |      5.40% |      5.16% |   -0.2375% | 1382.655 |   2792.292 | 2.020
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   270930C00100000 |     11.13% |     10.95% |   -0.1864% | 2286.504 |   2490.882 | 1.089
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   270930P00100000 |      4.42% |      4.19% |   -0.2340% | 1730.727 |   2815.563 | 1.627
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   271220C00100000 |     10.82% |     10.58% |   -0.2387% | 2276.347 |   2477.443 | 1.088
        /// 20260117 15:42:56.615 TRACE:: Log: FDX   271220P00100000 |      4.25% |      4.07% |   -0.1766% | 1799.426 |   2798.271 | 1.555
        /// </summary>
        /// <param name="contracts"></param>
        /// <param name="prices"></param>
        /// <param name="priceOption"></param>
        public void RegressIVDotNetMerlinCPU(List<OptionContractWrap> contracts, List<decimal> prices, decimal priceOption)
        {
            var results = new List<RegressionResult>();
            var calculationDate = Time;
            var zeroRates = YieldCurve.Instance.GetZeroCurve(calculationDate);
            
            var underlyingSymbol = contracts.First().Contract.Underlying.Symbol.Value;
            var dividends = new DividendManager().GetDividends(underlyingSymbol, CfgAlgo.StartDate);

            foreach (var ocw in contracts)
            {
                double sumQlMs = 0;
                double sumMerlinMs = 0;
                double sumQlMetric = 0;
                double sumMerlinMetric = 0;
                int count = 0;
                var dividendTenors = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)ToTenor(d.ExDate, calculationDate)).ToArray();
                var dividendAmounts = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)d.Amount).ToArray();

                foreach (decimal price in prices)
                {
                    // QLNet Benchmark
                    var sw = Stopwatch.StartNew();
                    sumQlMetric += (float)ocw.IV(priceOption, price, 0.0001);
                    // (float)ocw.IV((decimal)optionPrices[i], (decimal)spotPrices[i], 0.0001);
                    sw.Stop();
                    sumQlMs += sw.Elapsed.TotalMilliseconds;

                    // MerlinNative Test
                    sw.Restart();
                    var merlinIv = MerlinNative.merlin_implied_vol_american_fd_host(
                        (float)priceOption,
                        (float)price,
                        (float)ocw.Contract.StrikePrice,
                        (float)ToTenor(ocw.Contract.Expiry, calculationDate),
                        ocw.Contract.Right == OptionRight.Call,
                        zeroRates.Rates,
                        zeroRates.Times,
                        zeroRates.Rates.Length,
                        dividendAmounts,
                        dividendTenors,
                        dividendAmounts.Length,
                        0.0001f,
                        400
                    );
                    sumMerlinMetric += (float.IsNaN(merlinIv) || merlinIv >= 5) ? 0 : merlinIv;
                    sw.Stop();
                    sumMerlinMs += sw.Elapsed.TotalMilliseconds;
                    count++;

                }
                results.Add(new RegressionResult
                {
                    Symbol = ocw.Contract.Symbol.Value,
                    QlMetric = sumQlMetric / count,
                    MerlinMetric = sumMerlinMetric / count,
                    QlMs = sumQlMs,
                    MerlinMs = sumMerlinMs
                });
            }

            LogRegressionTable(results);
        }
        
        public void RegressPriceDotNetMerlinCPU(List<OptionContractWrap> contracts, List<decimal> spots, double iv)
        {
            var results = new List<RegressionResult>();
            var calculationDate = Time;
            var zeroRates = YieldCurve.Instance.GetZeroCurve(calculationDate);
            
            var underlyingSymbol = contracts.First().Contract.Underlying.Symbol.Value;
            var dividends = new DividendManager().GetDividends(underlyingSymbol, CfgAlgo.StartDate);

            foreach (var ocw in contracts)
            {
                double sumQlMs = 0;
                double sumMerlinMs = 0;
                double sumQlMetric = 0;
                double sumMerlinMetric = 0;
                int count = 0;
                var dividendTenors = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)ToTenor(d.ExDate, calculationDate)).ToArray();
                var dividendAmounts = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)d.Amount).ToArray();

                foreach (decimal spot in spots)
                {
                    // QLNet Benchmark
                    var sw = Stopwatch.StartNew();
                    sumQlMetric += (float)ocw.NPV(iv, spot);
                    sw.Stop();
                    sumQlMs += sw.Elapsed.TotalMilliseconds;

                    // MerlinNative Test
                    sw.Restart();
                    var merlinIv = MerlinNative.merlin_get_price_fd_cpu(
                        (float)spot,
                        (float)ocw.Contract.StrikePrice,
                        (float)ToTenor(ocw.Contract.Expiry, calculationDate),
                        (float)iv,
                        ocw.Contract.Right == OptionRight.Call,
                        zeroRates.Rates,
                        zeroRates.Times,
                        zeroRates.Rates.Length,
                        dividendAmounts,
                        dividendTenors,
                        dividendAmounts.Length,
                        200,
                        200
                    );
                    sumMerlinMetric += merlinIv;
                    sw.Stop();
                    sumMerlinMs += sw.Elapsed.TotalMilliseconds;
                    count++;

                }
                results.Add(new RegressionResult
                {
                    Symbol = ocw.Contract.Symbol.Value,
                    QlMetric = sumQlMetric / count,
                    MerlinMetric = sumMerlinMetric / count,
                    QlMs = sumQlMs,
                    MerlinMs = sumMerlinMs
                });
            }

            LogRegressionTable(results);
        }
        public void RegressPriceDotNetMerlinGPU(List<OptionContractWrap> contracts, List<decimal> spots, double iv)
        {
            var results = new List<RegressionResult>();
            var calculationDate = Time;
            var zeroRates = YieldCurve.Instance.GetZeroCurve(calculationDate);
            
            var underlyingSymbol = contracts.First().Contract.Underlying.Symbol.Value;
            var dividends = new DividendManager().GetDividends(underlyingSymbol, CfgAlgo.StartDate);

            var sw = Stopwatch.StartNew();
            foreach (var ocw in contracts)
            {
                double sumQlMs = 0;
                double sumMerlinMs = 0;
                double sumQlMetric = 0;
                double sumMerlinMetric = 0;
                
                var dividendTenors = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)ToTenor(d.ExDate, calculationDate)).ToArray();
                var dividendAmounts = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)d.Amount).ToArray();
                
                var ivs = spots.Select(_ => (float)iv).ToArray();
                var spotPrices = spots.Select(p => (float)p).ToArray();
                var strikes = spots.Select(_ => (float)ocw.Contract.StrikePrice).ToArray();
                var tenors = spots.Select(_ => (float)ToTenor(ocw.Contract.Expiry, calculationDate)).ToArray();
                var isCallsByte = spots.Select(_ => Convert.ToByte(ocw.Contract.Right == OptionRight.Call)).ToArray();

                foreach (decimal spot in spots)
                {
                    // QLNet Benchmark
                    sw.Restart();
                    sumQlMetric += (float)ocw.NPV(iv, spot);
                    sw.Stop();
                    sumQlMs += sw.Elapsed.TotalMilliseconds;
                }
                
                // var shift = 1f / 365f;
                // for (int i = 0; i < zeroRates.Times.Length; i++)
                //     zeroRates.Times[i] += shift;
                
                // var shift = 0.0001f;
                // for (int i = 0; i < zeroRates.Times.Length; i++)
                //     zeroRates.Rates[i] -= shift;
                
                // MerlinNative Test
                sw.Restart();
                float[] pricesOut = new float[spots.Count];
                MerlinNative.merlin_get_price_fd_cuda(
                    pricesOut,
                    spotPrices,
                    strikes,
                    tenors,
                    isCallsByte,
                    ivs,
                    spots.Count,
                    zeroRates.Rates,
                    zeroRates.Times,
                    zeroRates.Rates.Length,
                    dividendAmounts,
                    dividendTenors,
                    // 0
                    dividendAmounts.Length
                );
                sw.Stop();
                sumMerlinMs += sw.Elapsed.TotalMilliseconds;
                sumMerlinMetric += pricesOut.Sum();
                results.Add(new RegressionResult
                {
                    Symbol = ocw.Contract.Symbol.Value,
                    QlMetric = sumQlMetric / spots.Count,
                    MerlinMetric = sumMerlinMetric / spots.Count,
                    QlMs = sumQlMs,
                    MerlinMs = sumMerlinMs
                });
            }

            LogRegressionTable(results);
        }
        
        /// </summary>
        /// <param name="contracts"></param>
        /// <param name="prices"></param>
        /// <param name="priceOption"></param>
        public void RegressIVDotNetMerlinGPU(List<OptionContractWrap> contracts, List<decimal> prices, decimal priceOption)
        {
            var results = new List<RegressionResult>();
            var calculationDate = Time;
            var zeroRates = YieldCurve.Instance.GetZeroCurve(calculationDate);
            
            var underlyingSymbol = contracts.First().Contract.Underlying.Symbol.Value;
            var dividends = new DividendManager().GetDividends(underlyingSymbol, CfgAlgo.StartDate);
            
            foreach (var ocw in contracts)
            {
                var numPrices = prices.Count;
                var ivsCleanCpu  = new float[numPrices];

                // Prepare arrays for vectorized call
                var optionPrices = prices.Select(_ => (float)priceOption).ToArray();
                var spotPrices = prices.Select(p => (float)p).ToArray();
                var strikes = prices.Select(_ => (float)ocw.Contract.StrikePrice).ToArray();
                var tenors = prices.Select(_ => (float)ToTenor(ocw.Contract.Expiry, calculationDate)).ToArray();
                var isCalls = prices.Select(_ => ocw.Contract.Right == OptionRight.Call).ToArray();
                var isCallsByte = prices.Select(_ => Convert.ToByte(ocw.Contract.Right == OptionRight.Call)).ToArray();
                
                var dividendTenors = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)ToTenor(d.ExDate, calculationDate)).ToArray();
                var dividendAmounts = dividends.
                    Where(d => d.ExDate <= ocw.Contract.Expiry).
                    Select(d => (float)d.Amount).ToArray();
                
                // 1. Calculate using Single CPU method
                var swSingle = Stopwatch.StartNew();
                for (int i = 0; i < numPrices; i++)
                {
                    ivsCleanCpu[i] = (float)ocw.IV((decimal)optionPrices[i], (decimal)spotPrices[i], 0.0001);
                }
                swSingle.Stop();

                // 2. Calculate using Vectorized FD method
                var swVec = Stopwatch.StartNew();
                float[] ivs = new float[numPrices];
                MerlinNative.merlin_get_iv_fd_gpu(
                    ivs,
                    optionPrices,
                    spotPrices,
                    strikes,
                    tenors,
                    isCallsByte,
                    numPrices,
                    zeroRates.Rates,
                    zeroRates.Times,
                    zeroRates.Rates.Length,
                    dividendAmounts,
                    dividendTenors,
                    dividendAmounts.Length,
                    0.0001f,
                    400,
                    time_steps: 200,
                    space_steps: 200
                );
                swVec.Stop();

                results.Add(new RegressionResult
                {
                    Symbol = ocw.Contract.Symbol.Value,
                    QlMetric = ivsCleanCpu.Average(v => float.IsNaN(v) || v >= 5 ? 0 : v),
                    MerlinMetric = ivs.Average(v => float.IsNaN(v) || v >= 5 ? 0 : v),
                    QlMs = swSingle.Elapsed.TotalMilliseconds,
                    MerlinMs = swVec.Elapsed.TotalMilliseconds
                });
            }

            Log("--- Regression: Single CPU vs Vectorized FD ---");
            LogRegressionTable(results);
        }
        
        public double LogPerformanceMerlin(String tag, List<OptionContractWrap> contracts, List<decimal> prices, DateTime calculationDate, decimal priceOption)
        {
            var sw = Stopwatch.StartNew();
            var totalCalculations = 0;
            
            int nSteps = 10;

            foreach (var ocw in contracts)
            {
                double sumIv = 0;
                int count = 0;

                foreach (decimal price in prices)
                {
                    // Submit each contract-price combination on its own (size 1 arrays)
                    float outIv = MerlinNative.merlin_implied_vol_american_fd_host(
                        (float)priceOption,
                        (float)price,
                        (float)ocw.Contract.StrikePrice,
                        (float)ToTenor(ocw.Contract.Expiry, calculationDate),
                        ocw.Contract.Right == OptionRight.Call,
                        Array.Empty<float>(), Array.Empty<float>(),0,
                        Array.Empty<float>(), Array.Empty<float>(), 0,
                        0.001f,
                        max_iter: 200
                    );

                    sumIv += float.IsNaN(outIv) ? 0 : outIv;
                    count++;
                    totalCalculations++;
                }

                var avgIv = count > 0 ? sumIv / count : 0;
                Log($"Contract: {ocw.Contract.Symbol} | Avg IV (Merlin-Single): {avgIv:F4}");
            }

            sw.Stop();

            double totalMs = sw.Elapsed.TotalMilliseconds;
            var avgMsPerIv = totalCalculations > 0 ? totalMs / totalCalculations : 0;

            Log($"--- Performance Metrics {tag}---");
            Log($"Contracts Measured: {contracts.Count}");
            Log($"Total IV Calcs: {totalCalculations}");
            Log($"Total Time: {totalMs:F2} ms");
            Log($"Average Time/Calc: {avgMsPerIv:F4} ms");

            return totalMs;
        }

        private void LogRegressionTable(List<RegressionResult> results)
        {
            var header = $"{"Symbol",-20} | {"QLNet Metric",-10} | {"Merlin Metric",-10} | {"Diff Metric",-10} | {"QL ms",-8} | {"Merlin ms",-10} | {"TsMerlin / TsQL",-10}";
            var line = new string('-', header.Length);
        
            Log(line);
            Log(header);
            Log(line);

            double totalQlMs = 0;
            double totalMerlinMs = 0;

            foreach (var res in results)
            {
                var diff = (res.MerlinMetric - res.QlMetric) / res.QlMetric;
                Log($"{res.Symbol,-20} | {res.QlMetric,10:F4} | {res.MerlinMetric,10:F4} | {diff,10:P2} | {res.QlMs,8:F3} | {res.MerlinMs,10:F3} | {res.MerlinMs / res.QlMs:F3}");
            
                totalQlMs += res.QlMs;
                totalMerlinMs += res.MerlinMs;
            }

            Log(line);
            Log($"TOTAL PERFORMANCE: QLNet: {totalQlMs:F2}ms | Merlin: {totalMerlinMs:F2}ms | Merlin slower by: {totalMerlinMs / totalQlMs:F2}x");
            Log(line);
        }
        
        private class RegressionResult
        {
            public string Symbol { get; set; }
            public double QlMetric { get; set; }
            public double MerlinMetric { get; set; }
            public double QlMs { get; set; }
            public double MerlinMs { get; set; }
        }
    }
}
