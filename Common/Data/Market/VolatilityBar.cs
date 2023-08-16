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
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using ProtoBuf;
using QuantConnect.Logging;
using QuantConnect.Util;
using static QuantConnect.StringExtensions;

namespace QuantConnect.Data.Market
{
    /// <summary>
    /// An OHLC implementation of the QuantConnect BaseData class with parameters for candles.
    /// </summary>
    [ProtoContract(SkipConstructor = true)]
    public class VolatilityBar : BaseData
    {
        /// <summary>
        /// Bid OHLC
        /// </summary>
        [ProtoMember(203)]
        public Bar Bid { get; set; }

        /// <summary>
        /// Ask OHLC
        /// </summary>
        [ProtoMember(204)]
        public Bar Ask { get; set; }
        public Bar PriceAsk { get; set; }
        public Bar PriceBid { get; set; }
        public Bar UnderlyingPrice { get; set; }

        /// <summary>
        /// Opening price of the bar: Defined as the price at the start of the time period.
        /// </summary>
        public decimal Open
        {
            get
            {
                if (Bid != null && Ask != null)
                {
                    if (Bid.Open != 0m && Ask.Open != 0m)
                        return (Bid.Open + Ask.Open) / 2m;

                    if (Bid.Open != 0)
                        return Bid.Open;

                    if (Ask.Open != 0)
                        return Ask.Open;

                    return 0m;
                }
                if (Bid != null)
                {
                    return Bid.Open;
                }
                if (Ask != null)
                {
                    return Ask.Open;
                }
                return 0m;
            }
        }

        /// <summary>
        /// High price of the VolatilityBar during the time period.
        /// </summary>
        public decimal High
        {
            get
            {
                if (Bid != null && Ask != null)
                {
                    if (Bid.High != 0m && Ask.High != 0m)
                        return (Bid.High + Ask.High) / 2m;

                    if (Bid.High != 0)
                        return Bid.High;

                    if (Ask.High != 0)
                        return Ask.High;

                    return 0m;
                }
                if (Bid != null)
                {
                    return Bid.High;
                }
                if (Ask != null)
                {
                    return Ask.High;
                }
                return 0m;
            }
        }

        /// <summary>
        /// Low price of the VolatilityBar during the time period.
        /// </summary>
        public decimal Low
        {
            get
            {
                if (Bid != null && Ask != null)
                {
                    if (Bid.Low != 0m && Ask.Low != 0m)
                        return (Bid.Low + Ask.Low) / 2m;

                    if (Bid.Low != 0)
                        return Bid.Low;

                    if (Ask.Low != 0)
                        return Ask.Low;

                    return 0m;
                }
                if (Bid != null)
                {
                    return Bid.Low;
                }
                if (Ask != null)
                {
                    return Ask.Low;
                }
                return 0m;
            }
        }

        /// <summary>
        /// Closing price of the VolatilityBar. Defined as the price at Start Time + TimeSpan.
        /// </summary>
        public decimal Close
        {
            get
            {
                if (Bid != null && Ask != null)
                {
                    if (Bid.Close != 0m && Ask.Close != 0m)
                        return (Bid.Close + Ask.Close) / 2m;

                    if (Bid.Close != 0)
                        return Bid.Close;

                    if (Ask.Close != 0)
                        return Ask.Close;

                    return 0m;
                }
                if (Bid != null)
                {
                    return Bid.Close;
                }
                if (Ask != null)
                {
                    return Ask.Close;
                }
                return Value;
            }
        }

        /// <summary>
        /// The closing time of this bar, computed via the Time and Period
        /// </summary>
        public override DateTime EndTime
        {
            get { return Time + Period; }
            set { Period = value - Time; }
        }

        /// <summary>
        /// The period of this quote bar, (second, minute, daily, ect...)
        /// </summary>
        [ProtoMember(205)]
        public TimeSpan Period { get; set; }

        /// <summary>
        /// Default initializer to setup an empty quotebar.
        /// </summary>
        public VolatilityBar()
        {
            Symbol = Symbol.Empty;
            Time = new DateTime();
            Bid = new Bar();
            Ask = new Bar();
            PriceBid = new Bar();
            PriceAsk = new Bar();
            UnderlyingPrice = new Bar();
            Value = 0;
            Period = QuantConnect.Time.OneSecond;
            DataType = MarketDataType.VolatilityBar;
        }

        /// <summary>
        /// Initialize Quote Bar with Bid(OHLC) and Ask(OHLC) Values:
        /// </summary>
        /// <param name="time">DateTime Timestamp of the bar</param>
        /// <param name="symbol">Market MarketType Symbol</param>
        /// <param name="bid">Bid OLHC bar</param>
        /// <param name="lastBidSize">Average bid size over period</param>
        /// <param name="ask">Ask OLHC bar</param>
        /// <param name="lastAskSize">Average ask size over period</param>
        /// <param name="period">The period of this bar, specify null for default of 1 minute</param>
        public VolatilityBar(DateTime time, Symbol symbol, IBar bid, IBar ask, IBar priceBid, IBar priceAsk, IBar underlyingPriceBar, TimeSpan? period = null)
        {
            Symbol = symbol;
            Time = time;
            Bid = bid == null ? null : new Bar(bid.Open, bid.High, bid.Low, bid.Close);
            Ask = ask == null ? null : new Bar(ask.Open, ask.High, ask.Low, ask.Close);
            PriceBid = priceBid == null ? null : new Bar(priceBid.Open, priceBid.High, priceBid.Low, priceBid.Close);
            PriceAsk = priceAsk == null ? null : new Bar(priceAsk.Open, priceAsk.High, priceAsk.Low, priceAsk.Close);
            UnderlyingPrice = underlyingPriceBar == null ? null : new Bar(underlyingPriceBar.Open, underlyingPriceBar.High, underlyingPriceBar.Low, underlyingPriceBar.Close);
            Value = Close;
            Period = period ?? QuantConnect.Time.OneSecond;
            DataType = MarketDataType.VolatilityBar;
        }

        /// <summary>
        /// Update the quotebar - build the bar from this pricing information:
        /// </summary>
        /// <param name="lastTrade">The last trade price</param>
        /// <param name="bidPrice">Current bid price</param>
        /// <param name="askPrice">Current asking price</param>
        /// <param name="volume">Volume of this trade</param>
        /// <param name="bidSize">The size of the current bid, if available, if not, pass 0</param>
        /// <param name="askSize">The size of the current ask, if available, if not, pass 0</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Update(decimal lastTrade, decimal bidPrice, decimal askPrice, decimal volume, decimal bidSize, decimal askSize)
        {
            // update our bid and ask bars - handle null values, this is to give good values for midpoint OHLC
            if (Bid == null && bidPrice != 0) Bid = new Bar(bidPrice, bidPrice, bidPrice, bidPrice);
            else if (Bid != null) Bid.Update(ref bidPrice);

            if (Ask == null && askPrice != 0) Ask = new Bar(askPrice, askPrice, askPrice, askPrice);
            else if (Ask != null) Ask.Update(ref askPrice);

            // be prepared for updates without trades
            if (lastTrade != 0) Value = lastTrade;
            else if (askPrice != 0) Value = askPrice;
            else if (bidPrice != 0) Value = bidPrice;
        }

        /// <summary>
        /// VolatilityBar Reader: Fetch the data from the QC storage and feed it line by line into the engine.
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType, </param>
        /// <param name="stream">The file data stream</param>
        /// <param name="date">Date of this reader request</param>
        /// <param name="isLiveMode">true if we're in live mode, false for backtesting mode</param>
        /// <returns>Enumerable iterator for returning each line of the required data.</returns>
        public override BaseData Reader(SubscriptionDataConfig config, StreamReader stream, DateTime date, bool isLiveMode)
        {
            try
            {
                switch (config.Symbol.Underlying.SecurityType)
                {
                    case SecurityType.Option:
                    case SecurityType.FutureOption:
                    case SecurityType.IndexOption:
                        return ParseOption(config, stream, date);
                }
            }
            catch (Exception err)
            {
                Log.Error(Invariant($"VolatilityBar.Reader(): Error parsing stream, Symbol: {config.Symbol.Value}, SecurityType: {config.SecurityType}, ") +
                          Invariant($"Resolution: {config.Resolution}, Date: {date.ToStringInvariant("yyyy-MM-dd")}, Message: {err}")
                );
            }

            // we need to consume a line anyway, to advance the stream
            stream.ReadLine();

            // if we couldn't parse it above return a default instance
            return new VolatilityBar { Symbol = config.Symbol, Period = config.Increment };
        }

        /// <summary>
        /// VolatilityBar Reader: Fetch the data from the QC storage and feed it line by line into the engine.
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType, </param>
        /// <param name="line">Line from the data file requested</param>
        /// <param name="date">Date of this reader request</param>
        /// <param name="isLiveMode">true if we're in live mode, false for backtesting mode</param>
        /// <returns>Enumerable iterator for returning each line of the required data.</returns>
        public override BaseData Reader(SubscriptionDataConfig config, string line, DateTime date, bool isLiveMode)
        {
            try
            {
                switch (config.Symbol.Underlying.SecurityType)
                {
                    case SecurityType.Option:
                    case SecurityType.FutureOption:
                    case SecurityType.IndexOption:
                        return ParseOption(config, line, date);
                }
            }
            catch (Exception err)
            {
                Log.Error(Invariant($"VolatilityBar.Reader(): Error parsing line: '{line}', Symbol: {config.Symbol.Value}, SecurityType: {config.SecurityType}, ") +
                    Invariant($"Resolution: {config.Resolution}, Date: {date.ToStringInvariant("yyyy-MM-dd")}, Message: {err}")
                );
            }

            // if we couldn't parse it above return a default instance
            return new VolatilityBar { Symbol = config.Symbol, Period = config.Increment };
        }

        private static bool HasShownWarning;

        /// <summary>
        /// "Scaffold" code - If the data being read is formatted as a TradeBar, use this method to deserialize it
        /// TODO: Once all Forex data refactored to use VolatilityBar formatted data, remove this method
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType, </param>
        /// <param name="line">Line from the data file requested</param>
        /// <param name="date">Date of this reader request</param>
        /// <returns><see cref="VolatilityBar"/> with the bid/ask prices set to same values</returns>
        [Obsolete("All Forex data should use Quotes instead of Trades.")]
        private VolatilityBar ParseTradeAsVolatilityBar(SubscriptionDataConfig config, DateTime date, string line)
        {
            if (!HasShownWarning)
            {
                Logging.Log.Error("VolatilityBar.ParseTradeAsVolatilityBar(): Data formatted as Trade when Quote format was expected.  Support for this will disappear June 2017.");
                HasShownWarning = true;
            }

            var quoteBar = new VolatilityBar
            {
                Period = config.Increment,
                Symbol = config.Symbol
            };

            var csv = line.ToCsv(5);
            if (config.Resolution == Resolution.Daily || config.Resolution == Resolution.Hour)
            {
                // hourly and daily have different time format, and can use slow, robust c# parser.
                quoteBar.Time = DateTime.ParseExact(csv[0], DateFormat.TwelveCharacter, CultureInfo.InvariantCulture).ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }
            else
            {
                //Fast decimal conversion
                quoteBar.Time = date.Date.AddMilliseconds(csv[0].ToInt32()).ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }

            // the Bid/Ask bars were already create above, we don't need to recreate them but just set their values
            quoteBar.Bid.Open = csv[1].ToDecimal();
            quoteBar.Bid.High = csv[2].ToDecimal();
            quoteBar.Bid.Low = csv[3].ToDecimal();
            quoteBar.Bid.Close = csv[4].ToDecimal();

            quoteBar.Ask.Open = csv[1].ToDecimal();
            quoteBar.Ask.High = csv[2].ToDecimal();
            quoteBar.Ask.Low = csv[3].ToDecimal();
            quoteBar.Ask.Close = csv[4].ToDecimal();

            quoteBar.Value = quoteBar.Close;

            return quoteBar;
        }

        /// <summary>
        /// Parse a quotebar representing an option with a scaling factor
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType</param>
        /// <param name="line">Line from the data file requested</param>
        /// <param name="date">Date of this reader request</param>
        /// <returns><see cref="VolatilityBar"/> with the bid/ask set to same values</returns>
        public VolatilityBar ParseOption(SubscriptionDataConfig config, string line, DateTime date)
        {
            return ParseQuote(config, date, line);
        }

        /// <summary>
        /// Parse a quotebar representing an option with a scaling factor
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType</param>
        /// <param name="streamReader">The data stream of the requested file</param>
        /// <param name="date">Date of this reader request</param>
        /// <returns><see cref="VolatilityBar"/> with the bid/ask set to same values</returns>
        public VolatilityBar ParseOption(SubscriptionDataConfig config, StreamReader streamReader, DateTime date)
        {
            // scale factor only applies for equity and index options
            return ParseQuote(config, date, streamReader);
        }

        /// <summary>
        /// "Scaffold" code - If the data being read is formatted as a VolatilityBar, use this method to deserialize it
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType, </param>
        /// <param name="streamReader">The data stream of the requested file</param>
        /// <param name="date">Date of this reader request</param>
        /// <param name="useScaleFactor">Whether the data has a scaling factor applied</param>
        /// <returns><see cref="VolatilityBar"/> with the bid/ask prices set appropriately</returns>
        private VolatilityBar ParseQuote(SubscriptionDataConfig config, DateTime date, StreamReader streamReader)
        {
            var volBar = new VolatilityBar
            {
                Period = config.Increment,
                Symbol = config.Symbol
            };

            if (config.Resolution == Resolution.Daily || config.Resolution == Resolution.Hour)
            {
                // hourly and daily have different time format, and can use slow, robust c# parser.
                volBar.Time = streamReader.GetDateTime().ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }
            else
            {
                // Using custom int conversion for speed on high resolution data.
                volBar.Time = date.Date.AddMilliseconds(streamReader.GetInt32()).ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }

            volBar.UnderlyingPrice.Open = volBar.UnderlyingPrice.High = volBar.UnderlyingPrice.Low = volBar.UnderlyingPrice.Close = streamReader.GetDecimal();
            volBar.PriceBid.Open = volBar.PriceBid.High = volBar.PriceBid.Low = volBar.PriceBid.Close = streamReader.GetDecimal();
            volBar.Bid.Open = volBar.Bid.High = volBar.Bid.Low = volBar.Bid.Close = streamReader.GetDecimal();
            volBar.PriceAsk.Open = volBar.PriceAsk.High = volBar.PriceAsk.Low = streamReader.GetDecimal();
            volBar.Ask.Open = volBar.Ask.High = volBar.Ask.Low = volBar.Ask.Close = streamReader.GetDecimal();

            volBar.Value = volBar.Close;

            return volBar;
        }

        /// <summary>
        /// "Scaffold" code - If the data being read is formatted as a VolatilityBar, use this method to deserialize it
        /// TODO: Once all Forex data refactored to use VolatilityBar formatted data, use only this method
        /// </summary>
        /// <param name="config">Symbols, Resolution, DataType, </param>
        /// <param name="line">Line from the data file requested</param>
        /// <param name="date">Date of this reader request</param>
        /// <param name="useScaleFactor">Whether the data has a scaling factor applied</param>
        /// <returns><see cref="VolatilityBar"/> with the bid/ask prices set appropriately</returns>
        private VolatilityBar ParseQuote(SubscriptionDataConfig config, DateTime date, string line)
        {
            var volBar = new VolatilityBar
            {
                Period = config.Increment,
                Symbol = config.Symbol
            };

            var csv = line.ToCsv(6);
            if (config.Resolution == Resolution.Daily || config.Resolution == Resolution.Hour)
            {
                // hourly and daily have different time format, and can use slow, robust c# parser.
                volBar.Time = DateTime.ParseExact(csv[0], DateFormat.TwelveCharacter, CultureInfo.InvariantCulture).ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }
            else
            {
                // Using custom "ToDecimal" conversion for speed on high resolution data.
                volBar.Time = date.Date.AddMilliseconds((double)csv[0].ToDecimal()).ConvertTo(config.DataTimeZone, config.ExchangeTimeZone);
            }

            // only create the if based on actual underlying prices
            if (csv[1].Length != 0)
            {
                // the Bid/Ask bars were already create above, we don't need to recreate them but just set their values
                volBar.UnderlyingPrice.Open = volBar.UnderlyingPrice.High = volBar.UnderlyingPrice.Low = volBar.UnderlyingPrice.Close = csv[1].ToDecimal();
                volBar.PriceBid.Open = volBar.PriceBid.High = volBar.PriceBid.Low = volBar.PriceBid.Close = csv[2].ToDecimal();
                volBar.Bid.Open = volBar.Bid.High = volBar.Bid.Low = volBar.Bid.Close = csv[1].ToDecimal();
                volBar.PriceAsk.Open = volBar.PriceAsk.High = volBar.PriceAsk.Low = volBar.PriceAsk.Close = csv[1].ToDecimal();
                volBar.Ask.Open = volBar.Ask.High = volBar.Ask.Low = volBar.Ask.Close = csv[1].ToDecimal();
            }
            else
            {
                volBar.Bid = null;
                volBar.Ask = null;
                volBar.UnderlyingPrice = null;
                volBar.PriceBid = null;
                volBar.PriceAsk = null;
            }

            volBar.Value = volBar.Close;
            return volBar;
        }

        /// <summary>
        /// Get Source for Custom Data File
        /// >> What source file location would you prefer for each type of usage:
        /// </summary>
        /// <param name="config">Configuration object</param>
        /// <param name="date">Date of this source request if source spread across multiple files</param>
        /// <param name="isLiveMode">true if we're in live mode, false for backtesting mode</param>
        /// <returns>String source location of the file</returns>
        public override SubscriptionDataSource GetSource(SubscriptionDataConfig config, DateTime date, bool isLiveMode)
        {
            if (isLiveMode)
            {
                // this data type is streamed in live mode
                return new SubscriptionDataSource(string.Empty, SubscriptionTransportMedium.Streaming);
            }

            var source = LeanData.GenerateZipFilePath(Globals.DataFolder, config.Symbol.Underlying, date, config.Resolution, TickType.IV_Quote);
            if (config.SecurityType == SecurityType.Future || config.SecurityType.IsOption() || config.SecurityType == SecurityType.Base)
            {
                source += "#" + LeanData.GenerateZipEntryName(config.Symbol.Underlying, date, config.Resolution, TickType.IV_Quote);
            }
            return new SubscriptionDataSource(source, SubscriptionTransportMedium.LocalFile, FileFormat.Csv);
        }

        /// <summary>
        /// Return a new instance clone of this quote bar, used in fill forward
        /// </summary>
        /// <returns>A clone of the current quote bar</returns>
        public override BaseData Clone()
        {
            return new VolatilityBar
            {
                Ask = Ask == null ? null : Ask.Clone(),
                Bid = Bid == null ? null : Bid.Clone(),
                PriceBid = PriceBid == null ? null : PriceBid.Clone(),
                PriceAsk = PriceAsk == null ? null : PriceAsk.Clone(),
                UnderlyingPrice = UnderlyingPrice == null ? null : UnderlyingPrice.Clone(),
                Symbol = Symbol,
                Time = Time,
                Period = Period,
                Value = Value,
                DataType = DataType
            };
        }

        /// <summary>
        /// Convert this <see cref="VolatilityBar"/> to string form.
        /// </summary>
        /// <returns>String representation of the <see cref="VolatilityBar"/></returns>
        public override string ToString()
        {
            return $"{Symbol}: " +
                   $"Underlying: {UnderlyingPrice?.Close.SmartRounding()} " +
                   $"BidPrice: {PriceBid?.Close.SmartRounding()} " +
                   $"Bid Vol: {Bid?.Close.SmartRounding()} " +
                   $"AskPrice: {PriceAsk?.Close.SmartRounding()} " +
                   $"Ask Vol: {Ask?.Open.SmartRounding()} ";
        }
    }
}
