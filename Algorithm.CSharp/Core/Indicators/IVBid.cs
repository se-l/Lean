using QuantConnect.Algorithm.CSharp.Core.Pricing;
using QuantConnect.Data.Market;
using QuantConnect.Indicators;
using QuantConnect.Securities.Option;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    public class IVBid : IVBidAskIndicator
    {
        public IVBid(Option option, Foundations algo) : base(QuoteSide.Bid, option, algo)
        {
        }
        public override void Update(QuoteBar quoteBar, decimal? underlyingMidPrice = null)
        {
            if (quoteBar == null || quoteBar.Bid == null || quoteBar.EndTime <= Time)
            {
                return;
            }
            Time = quoteBar.EndTime;
            decimal midPriceUnderlying = underlyingMidPrice ?? _algo.MidPrice(Symbol.Underlying);
            decimal quote = quoteBar.Bid.Close;
            if (HaveInputsChanged(quote, midPriceUnderlying, Time.Date))
            {
                MidPriceUnderlying = midPriceUnderlying;
                Price = quote;
                IV = OptionContractWrap.E(_algo, Option, 1, Time.Date).IV(Price, MidPriceUnderlying, 0.001);
            }
            IVBidAsk = new IVBidAsk(Symbol, Time, MidPriceUnderlying, Price, IV);
            Current = new IndicatorDataPoint(Time, (decimal)IVBidAsk.IV);
        }
    }
}
