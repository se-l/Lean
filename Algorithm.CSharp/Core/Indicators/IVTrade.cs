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
        public IVBidAsk Current { get; internal set; }

        private Foundations algo { get; }

        public IVTrade(Option option, Foundations algo)
        {
            this.algo = algo;
            Option = option;
        }

        public void Update(Tick tick, decimal? underlyingMidPrice = null)
        {
            if (tick == null || tick.EndTime <= Time)
            {
                return;
            }
            Time = tick.EndTime;
            UnderlyingMidPrice = underlyingMidPrice ?? algo.MidPrice(Symbol.Underlying);
            Price = tick.Price;
            IV = OptionContractWrap.E(algo, Option, 1, Time.Date).IV(Price, UnderlyingMidPrice, 0.001);
            Current = new IVBidAsk(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }

        public void Update(TradeBar tradeBar, decimal? underlyingMidPrice = null)
        {
            if (tradeBar == null || tradeBar.EndTime <= Time)
            {
                return;
            }
            Time = tradeBar.EndTime;
            UnderlyingMidPrice = underlyingMidPrice ?? algo.MidPrice(Symbol.Underlying);
            Price = tradeBar.Close;
            IV = OptionContractWrap.E(algo, Option, 1, Time.Date).IV(Price, UnderlyingMidPrice, 0.001);
            Current = new IVBidAsk(Symbol, Time, UnderlyingMidPrice, Price, IV);
        }
    }
}
