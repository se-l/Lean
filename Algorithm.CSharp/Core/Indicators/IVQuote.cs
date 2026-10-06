using System;

namespace QuantConnect.Algorithm.CSharp.Core.Indicators
{
    /// <summary>
    /// What the IV indicators and the rolling windows need from an IV sample: a time to order by and
    /// the IV itself. Lets the rolling window stay generic over the sample type without boxing.
    /// </summary>
    public interface IIVQuote
    {
        Symbol Symbol { get; }
        DateTime Time { get; }
        double IV { get; }
    }

    /// <summary>
    /// A single IV sample for one option contract.
    /// A value type on purpose: the feed pushes two of these per volatility bar (millions per
    /// backtest), so this must not allocate.
    /// The option price and the underlying mid are deliberately absent. They are inputs to the pricer
    /// (see <see cref="IVQuoteIndicator"/>), not something consumers read: every downstream consumer
    /// reads only <see cref="IV"/>, and the R2 warehouse does not carry option prices.
    /// </summary>
    public readonly struct IVQuote : IIVQuote
    {
        public Symbol Symbol { get; }
        public DateTime Time { get; }
        public double IV { get; }

        public IVQuote(Symbol symbol, DateTime time, double iv)
        {
            Symbol = symbol;
            Time = time;
            IV = iv;
        }
    }
}
