using System;
using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVTrade
    {
        public Symbol Symbol { get => Option.Symbol; }
        public Option Option { get; }
        public DateTime Time { get; set; }
        public decimal UnderlyingMidPrice { get; set; }
        public decimal Price { get; set; }
        public double IV { get; set; }
        public IVQuote Current { get; internal set; }

        private readonly Foundations _algo;

        public IVTrade(Option option, Foundations algo)
        {
            _algo = algo;
            Option = option;
        }

        public void Update(Tick tick, decimal? underlyingMidPrice = null)
        {
            if (tick == null || tick.EndTime <= Time)
            {
                return;
            }
            Time = tick.EndTime;
            UnderlyingMidPrice = underlyingMidPrice ?? _algo.MidPrice(Symbol.Underlying);
            Price = tick.Price;
            IV = OptionContractWrap.E(_algo, Option, Time.Date).IV(Price, UnderlyingMidPrice, 0.001);
            Current = new IVQuote(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }

        public void Update(TradeBar tradeBar, decimal? underlyingMidPrice = null)
        {
            if (tradeBar == null || tradeBar.EndTime <= Time)
            {
                return;
            }
            Time = tradeBar.EndTime;
            UnderlyingMidPrice = underlyingMidPrice ?? _algo.MidPrice(Symbol.Underlying);
            Price = tradeBar.Close;
            IV = OptionContractWrap.E(_algo, Option, Time.Date).IV(Price, UnderlyingMidPrice, 0.001);
            Current = new IVQuote(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }
        public void SetDelta(double? delta = null)
        {
            Current.Delta = delta != null ? delta : OptionContractWrap.E(_algo, Option, Time.Date).Delta(_algo.IV(Option, Price));
        }
    }
}
