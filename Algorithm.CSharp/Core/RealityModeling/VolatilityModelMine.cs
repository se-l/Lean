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
using System.Linq;
using MathNet.Numerics.Statistics;

using QuantConnect.Data;
using QuantConnect.Indicators;
using QuantConnect.Securities;
using QuantConnect.Securities.Volatility;

namespace QuantConnect.Algorithm.CSharp.Core.RealityModeling
{
    /// <summary>
    /// Provides an implementation of <see cref="IVolatilityModel"/> that computes the
    /// annualized sample standard deviation of daily returns as the volatility of the security
    /// </summary>
    public class VolatilityModelMine : BaseVolatilityModel
    {
        private Foundations _algo;
        private Security _security;
        private bool _needsUpdate;
        private decimal _volatility;
        private DateTime _lastUpdate = DateTime.MinValue;
        private decimal _lastPrice;
        private Resolution? _resolution;
        private TimeSpan _periodSpan;
        private readonly object _sync = new object();
        private RollingWindow<double> _window;
        private double _samplesPerDay;
        private readonly TimeSpan _openingTimeCutoff = new(9, 31, 0);
        private readonly Dictionary<Security, IEnumerable<DateTime>> _postEarningsReleaseDates = new();

        /// <summary>
        /// Gets the volatility of the security as a percentage
        /// </summary>
        public override decimal Volatility
        {
            get
            {
                lock (_sync)
                {
                    if (_window.Count < 2)
                    {
                        return 0m;
                    }

                    if (_needsUpdate)
                    {
                        _needsUpdate = false;
                        var std = _window.StandardDeviation().SafeDecimalCast();
                        _volatility = std * (decimal)Math.Sqrt(252.0 * _samplesPerDay);
                    }
                }

                return _volatility;
            }
        }

        public double SamplesPerDay()
        {
            double nSamples;
            switch (_resolution)
            {
                //case Resolution.Tick:
                //    nSamples = (double)_window.Samples; // divide by number of days sampled over
                case Resolution.Second:
                    nSamples = 6.5 * 60 * 60 / _periodSpan.TotalSeconds;
                    break;
                case Resolution.Minute:
                    nSamples = 6.5 * 60 / (_periodSpan.TotalSeconds / 60);
                    break;
                case Resolution.Hour:
                    nSamples = 6.5 / (_periodSpan.TotalSeconds / (60*60));
                    break;
                default:
                    nSamples = 1;
                    break;
            }
            return nSamples;
        }

        public bool IsAfterEarningsRelease(DateTime time, Security security)
        {
            if (!_postEarningsReleaseDates.ContainsKey(security))
            {
                SecurityExchangeHours securityExchangeHours = MarketHoursDatabase.FromDataFolder().GetExchangeHours(Market.USA, security.Symbol, security.Type);
                Func<DateTime, Security, DateTime> nextTradeDate = (DateTime date, Security security) => Time.EachTradeableDay(securityExchangeHours, date.AddDays(1), date.AddDays(10)).First();
                _postEarningsReleaseDates[security] = _algo.EarningsBySymbol[security.Symbol].Select(x => nextTradeDate(x.Date, security));
            }
            return _postEarningsReleaseDates[security].Contains(time.Date);
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VolatilityModelMine"/> class
        /// </summary>
        /// <param name="periods">The max number of samples in the rolling window to be considered for calculating the standard deviation of returns</param>
        /// <param name="resolution">
        /// Resolution of the price data inserted into the rolling window series to calculate standard deviation.
        /// Will be used as the default value for update frequency if a value is not provided for <paramref name="updateFrequency"/>.
        /// This only has a material effect in live mode. For backtesting, this value does not cause any behavioral changes.
        /// </param>
        /// <param name="updateFrequency">Frequency at which we insert new values into the rolling window for the standard deviation calculation</param>
        /// <remarks>
        /// The volatility model will be updated with the most granular/highest resolution data that was added to your algorithm.
        /// That means that if I added <see cref="Resolution.Tick"/> data for my Futures strategy, that this model will be
        /// updated using <see cref="Resolution.Tick"/> data as the algorithm progresses in time.
        ///
        /// Keep this in mind when setting the period and update frequency. The Resolution parameter is only used for live mode, or for
        /// the default value of the <paramref name="updateFrequency"/> if no value is provided.
        /// </remarks>
        public VolatilityModelMine(
            Foundations algo,
            Security security,
            int periods,
            Resolution? resolution = null,
            TimeSpan? updateFrequency = null
            )
        {
            if (periods < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(periods), "'periods' must be greater than or equal to 2.");
            }
            _algo = algo;
            _security = security;
            _window = new RollingWindow<double>(periods);
            _resolution = resolution;
            _periodSpan = updateFrequency ?? resolution?.ToTimeSpan() ?? TimeSpan.FromDays(1);
            _samplesPerDay = SamplesPerDay();
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="VolatilityModelMine"/> class
        /// </summary>
        /// <param name="resolution">
        /// Resolution of the price data inserted into the rolling window series to calculate standard deviation.
        /// Will be used as the default value for update frequency if a value is not provided for <paramref name="updateFrequency"/>.
        /// This only has a material effect in live mode. For backtesting, this value does not cause any behavioral changes.
        /// </param>
        /// <param name="updateFrequency">Frequency at which we insert new values into the rolling window for the standard deviation calculation</param>
        /// <remarks>
        /// The volatility model will be updated with the most granular/highest resolution data that was added to your algorithm.
        /// That means that if I added <see cref="Resolution.Tick"/> data for my Futures strategy, that this model will be
        /// updated using <see cref="Resolution.Tick"/> data as the algorithm progresses in time.
        ///
        /// Keep this in mind when setting the period and update frequency. The Resolution parameter is only used for live mode, or for
        /// the default value of the <paramref name="updateFrequency"/> if no value is provided.
        /// </remarks>
        public VolatilityModelMine(
            Foundations algo,
            Security security,
            Resolution resolution,
            TimeSpan? updateFrequency = null
            ) : this(algo, security, PeriodsInResolution(resolution), resolution, updateFrequency)
        {
        }

        /// <summary>
        /// Updates this model using the new price information in
        /// the specified security instance
        /// </summary>
        /// <param name="security">The security to calculate volatility for</param>
        /// <param name="data">Data to update the volatility model with</param>
        public override void Update(Security security, BaseData data)
        {
            var timeSinceLastUpdate = data.EndTime - _lastUpdate;
            //decimal midPrice = (data.BidPrice + data.AskPrice) / 2m;
            if (timeSinceLastUpdate >= _periodSpan && data.Price > 0 && !IsAfterEarningsRelease(data.EndTime, security))
            {
                lock (_sync)
                {
                    if (_lastPrice > 0.0m 
                        && !(data.Time.TimeOfDay < _openingTimeCutoff && Math.Abs(data.Price / _lastPrice - 1) > .05m)  // ignore opening jumps with a retur greater x (earnings jumps)
                        )
                    {
                        _needsUpdate = true;
                        _window.Add((double)(data.Price / _lastPrice) - 1.0);
                    }
                }

                _lastUpdate = data.EndTime;
                _lastPrice = data.Price;
            }
        }

        /// <summary>
        /// Returns history requirements for the volatility model expressed in the form of history request
        /// </summary>
        /// <param name="security">The security of the request</param>
        /// <param name="utcTime">The date of the request</param>
        /// <returns>History request object list, or empty if no requirements</returns>
        public override IEnumerable<HistoryRequest> GetHistoryRequirements(Security security, DateTime utcTime)
        {
            // Let's reset the model since it will get warmed up again using these history requirements
            Reset();

            return GetHistoryRequirements(
                security,
                utcTime,
                _resolution,
                _window.Size + 1);
        }

        /// <summary>
        /// Resets the model to its initial state
        /// </summary>
        private void Reset()
        {
            _needsUpdate = false;
            _volatility = 0m;
            _lastUpdate = DateTime.MinValue;
            _lastPrice = 0m;
            _window.Reset();
        }

        private static int PeriodsInResolution(Resolution resolution)
        {
            int periods;
            switch (resolution)
            {
                case Resolution.Tick:
                case Resolution.Second:
                    periods = 600;
                    break;
                case Resolution.Minute:
                    periods = 60 * 24;
                    break;
                case Resolution.Hour:
                    periods = 24 * 30;
                    break;
                default:
                    periods = 30;
                    break;
            }

            return periods;
        }
    }
}
