using System;
using System.Linq;
using System.Collections.Generic;
using QuantConnect.Interfaces;
using QuantConnect.Securities;
using QuantConnect.Data.Auxiliary;
using IQFeed.CSharpApiClient.Lookup;
using IQFeed.CSharpApiClient.Lookup.Chains;
using IQFeed.CSharpApiClient.Lookup.Chains.Equities;
using QuantConnect.Securities.Option;

namespace QuantConnect.ToolBox.IQFeed.IQ
{
    public class IQOptionChainProvider : IOptionChainProvider
    {
        private const int NumberOfClients = 8;
        const string market = Market.USA;
        //private IMapFileProvider _mapFileProvider;
        private IQFeedFileHistoryProvider _historyProvider;

        /// <summary>
        /// Creates a new instance
        /// </summary>
        /// <param name="dataCacheProvider">The data cache provider instance to use</param>
        /// <param name="mapFileProvider">The map file provider instance to use</param>
        public IQOptionChainProvider()
        {
            //_mapFileProvider = mapFileProvider;
            LookupClient lookupClient = LookupClientFactory.CreateNew(NumberOfClients);
            lookupClient.Connect();
            IQFeedDataQueueUniverseProvider universeProvider = new IQFeedDataQueueUniverseProvider();
            _historyProvider = new IQFeedFileHistoryProvider(lookupClient, universeProvider, MarketHoursDatabase.FromDataFolder());
        }

        /// <summary>
        /// Gets the list of option contracts for a given underlying symbol
        /// </summary>
        /// <param name="symbol">The option or the underlying symbol to get the option chain for.
        /// Providing the option allows targetting an option ticker different than the default e.g. SPXW</param>
        /// <param name="date">The date for which to request the option chain (only used in backtesting)</param>
        /// <returns>The list of option contracts</returns>
        public virtual IEnumerable<Symbol> GetOptionContractList(Symbol symbol, DateTime date)
        {
            Symbol canonicalSymbol;
            if (!symbol.SecurityType.HasOptions())
            {
                // we got an option
                if (symbol.SecurityType.IsOption() && symbol.Underlying != null)
                {
                    canonicalSymbol = symbol.Canonical; // GetCanonical(symbol, date);
                }
                else
                {
                    throw new NotSupportedException($"IQOptionChainProvider.GetOptionContractList(): " +
                        $"{nameof(SecurityType.Equity)}, {nameof(SecurityType.Future)}, or {nameof(SecurityType.Index)} is expected but was {symbol.SecurityType}");
                }
            }
            else
            {
                // we got the underlying
                //var mappedUnderlyingSymbol = MapUnderlyingSymbol(symbol, date);
                canonicalSymbol = Symbol.CreateCanonicalOption(symbol);
            }
            IEnumerable<EquityOption> optionChain = _historyProvider.GetIndexEquityOptionChain(canonicalSymbol, date, date);

            var symbols = Enumerable.Empty<Symbol>();
            foreach (var optionContract in optionChain)
            {
                OptionRight optionRight = optionContract.Side == OptionSide.Call ? OptionRight.Call : OptionRight.Put;
                // Defaulting to American style in abscence of definition in EquityOption type.
                var optionContractSymbol = Symbol.CreateOption(canonicalSymbol.Underlying, market, OptionStyle.American, optionRight, (decimal)optionContract.StrikePrice, optionContract.Expiration);
                symbols = symbols.Append(optionContractSymbol);
            }
            return symbols;
        }

        //private Symbol GetCanonical(Symbol optionSymbol, DateTime date)
        //{
        //    // Resolve any mapping before requesting option contract list for equities
        //    // Needs to be done in order for the data file key to be accurate
        //    if (optionSymbol.Underlying.RequiresMapping())
        //    {
        //        var mappedUnderlyingSymbol = MapUnderlyingSymbol(optionSymbol.Underlying, date);

        //        return Symbol.CreateCanonicalOption(mappedUnderlyingSymbol);
        //    }
        //    else
        //    {
        //        return optionSymbol.Canonical;
        //    }
        //}

        //private Symbol MapUnderlyingSymbol(Symbol underlying, DateTime date)
        //{
        //    if (underlying.RequiresMapping())
        //    {
        //        var mapFileResolver = _mapFileProvider.Get(AuxiliaryDataKey.Create(underlying));
        //        var mapFile = mapFileResolver.ResolveMapFile(underlying);
        //        var ticker = mapFile.GetMappedSymbol(date, underlying.Value);
        //        return underlying.UpdateMappedSymbol(ticker);
        //    }
        //    else
        //    {
        //        return underlying;
        //    }
        //}
    }
}
