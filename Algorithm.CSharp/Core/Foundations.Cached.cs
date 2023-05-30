using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Data.Market;
using static QuantConnect.Algorithm.CSharp.Core.Statics;
using QuantConnect.Algorithm.CSharp.Core.Risk;

namespace QuantConnect.Algorithm.CSharp.Core
{
    public partial class Foundations : QCAlgorithm
    {
        // Cached Methods
        public Func<Symbol, Symbol, int, Resolution, double> Correlation;
        public Func<Symbol, int, Resolution, bool> IsLiquid;
        public VoidFunction HedgeWithIndex;
        public Func<Symbol, int, Resolution, IEnumerable<TradeBar>> HistoryWrap;
        public Func<Symbol, int, Resolution, IEnumerable<QuoteBar>> HistoryWrapQuote;
        public Func<Symbol, decimal> TickSize;
        public Func<decimal> PositionsTotal;
        public Func<int> PositionsN;

        public void AssignCachedFunctions()
        {
            Correlation = Cache(GetCorrelation, (Symbol symbol1, Symbol symbol2, int periods=30, Resolution resolution=Resolution.Daily) => $"{symbol1},{symbol2},{periods}{resolution}{Time.Date}");
            IsLiquid = Cache(GetIsLiquid, (Symbol contract, int window, Resolution resolution) => $"{Time.Date}{contract}{window}{resolution}");
            HedgeWithIndex = Cache(GetHedgeWithIndex, () => $"{Time}", maxKeys: 1);
            HistoryWrap = Cache(GetHistoryWrap, (Symbol contract, int window, Resolution resolution) => $"{Time.Date}{contract}{window}{resolution}");
            HistoryWrapQuote = Cache(GetHistoryWrapQuote, (Symbol contract, int window, Resolution resolution) => $"{Time.Date}{contract}{window}{resolution}");
            TickSize = Cache(GetTickSize, (Symbol symbol) => $"{symbol}", maxKeys: 1);
            PositionsTotal = Cache(GetPositionsTotal, () => $"{Time}", maxKeys: 1);
            PositionsN = Cache(GetPositionsN, () => $"{Time}", maxKeys: 1);            
        }
        private double GetCorrelation(Symbol symbol1, Symbol symbol2, int periods = 30, Resolution resolution = Resolution.Daily)
        {
            // Consider using MidPrices rather. Smoother... But not available for daily resolution.
            var historyIndex = HistoryWrap(symbol1, periods, resolution);
            historyIndex = !historyIndex.Any() ? History(symbol1, periods, resolution) : historyIndex;
            var historySymbol = HistoryWrap(symbol2, periods, resolution);
            historySymbol = !historySymbol.Any() ? History(symbol2, periods, resolution) : historySymbol;


            var coefficients = RollingPearsonCorr(
                historyIndex.Select(tb => (double)tb.Close),
                historySymbol.Select(tb => (double)tb.Close),
                periods
                );
            // Test whether last coefficient is NA, then return 1
            var value = coefficients.Last().IsNaNOrZero() ? 1 : coefficients.Last();
            Debug($"Corrleation({symbol1},{symbol2},{periods}: {value}");
            return value;
        }
        private void GetHedgeWithIndex()
        {
            //tex:
            //Deriving quantity to hedge
            //$$\Delta_I=\beta \frac{S_A}{S_I} \Delta_A$$

            if (IsWarmingUp || !IsMarketOpen(spy))
            {
                return;
            }
            //foreach (var ticker in hedgeTicker)
            //{
            var ticker = spy;
            var pfRisk = PortfolioRisk.E(this, useCache: false);
            decimal netSpyDelta = pfRisk.DeltaSPY100BpUSD;
            if (Math.Abs(netSpyDelta) >= deltaBand)
            {
                var quantity = -1 * Math.Round(netSpyDelta / MidPrice(ticker), 0);
                // Call cached HedgeWithIndex to avoid stack overflow here
                MarketOrder(ticker, quantity);
            }
        }

        private bool GetIsLiquid(Symbol contract, int window = 3, Resolution resolution = Resolution.Daily)
        {
            var trade_bars = HistoryWrap(contract, window, resolution).ToList();
            return trade_bars.Sum(bar => bar.Volume) > 0;
        }

        private IEnumerable<TradeBar> GetHistoryWrap(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<TradeBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private IEnumerable<QuoteBar> GetHistoryWrapQuote(Symbol symbol, int periods, Resolution resolution
            //, bool? fillForward = null, bool? extendedMarketHours = null, DataMappingMode? dataMappingMode = null, DataNormalizationMode? dataNormalizationMode = null, int? contractDepthOffset = null
            )
        {
            return History<QuoteBar>(symbol, periods, resolution);
            //fillForward, extendedMarketHours, dataMappingMode, dataNormalizationMode, contractDepthOffset
        }

        private decimal GetTickSize(Symbol symbol)
        {
            //Log($"GetTickSize called {symbol}");
            var sec = Securities[symbol];
            return sec.SymbolProperties.MinimumPriceVariation;
        }


        private decimal GetPositionsTotal()
        {
            return Portfolio.TotalHoldingsValue;
            //return Portfolio.Values.Where(s => s.Invested).Sum(s => s.HoldingsValue);
        }
        private int GetPositionsN()
        {
            // Filter out securities that are not invested
            return Portfolio.Values.Count(s => s.Invested);
        }
    }
}
