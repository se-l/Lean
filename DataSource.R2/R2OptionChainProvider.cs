/*
 * R2OptionChainProvider — the option chain served from the R2 warehouse, not from QuantConnect's
 * zip/map-file layout (the trade mount carries no chain files).
 *
 * A contract is offered when BOTH hold:
 *   1. option_contract_live says it was live: registered at some as_of <= date and expiry after
 *      date (a contract expired before the requested date cannot trade on it);
 *   2. loaded_symbol_sessions says the warehouse actually carries tick data for it on that exact
 *      date (option_trade_tick / option_quote_tick). Subscribing an unloaded contract buys an
 *      empty feed, so the intersection is the point: the chain is the loadable universe.
 *
 * Quotes/prices still flow through the normal R2 readers (option quote bars); the vol/IV side is
 * served by the ZMQ pricer (mind.dev), which must function with no volatility quote bars loaded —
 * this provider never gates on the IV surface.
 */
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Linq;

using QuantConnect;
using QuantConnect.Interfaces;
using QuantConnect.Logging;

namespace QuantConnect.Lean.DataSource.R2
{
    /// <summary>
    /// IOptionChainProvider backed by the R2 warehouse: option_contract_live for liveness, the
    /// loaded_symbol_sessions ledger for data presence on the requested date.
    /// </summary>
    [InheritedExport(typeof(IOptionChainProvider))]
    public class R2OptionChainProvider : IOptionChainProvider
    {
        private const string RegistryTable = "option_contract_live";
        private const string LedgerTable = "loaded_symbol_sessions";

        private readonly R2LocalCatalog _catalog;

        /// <summary>The registry/ledger read is the same for every caller on a day; cache per day.</summary>
        private readonly Dictionary<(string underlying, DateTime date), IReadOnlyList<Symbol>> _cache
            = new Dictionary<(string, DateTime), IReadOnlyList<Symbol>>();

        private readonly object _cacheGate = new object();

        /// <param name="catalog">Route-aware catalog helper. Null = <see cref="R2LocalCatalog.Shared"/>.</param>
        public R2OptionChainProvider(R2LocalCatalog catalog = null)
        {
            _catalog = catalog ?? R2LocalCatalog.Shared;
        }

        /// <summary>
        /// MEF/Composer instantiates exports via Activator, which requires a true parameterless
        /// constructor — optional parameters do not count.
        /// </summary>
        public R2OptionChainProvider()
            : this(null)
        {
        }

        /// <summary>
        /// Contracts of <paramref name="symbol"/>'s underlying that were live AND loaded on <paramref name="date"/>.
        /// </summary>
        public IEnumerable<Symbol> GetOptionContractList(Symbol symbol, DateTime date)
        {
            var underlying = symbol.SecurityType.IsOption() ? symbol.Underlying.Value : symbol.Value;
            var day = date.Date;

            lock (_cacheGate)
            {
                if (_cache.TryGetValue((underlying, day), out var cached))
                {
                    return cached;
                }
            }

            var contracts = LoadContracts(underlying, day);

            lock (_cacheGate)
            {
                _cache[(underlying, day)] = contracts;
            }
            return contracts;
        }

        private IReadOnlyList<Symbol> LoadContracts(string underlying, DateTime date)
        {
            var und = underlying.Replace("'", "''");
            var day = $"DATE '{date:yyyy-MM-dd}'";

            // (1) Registry: registered at some as_of <= date, expiry strictly after date. One row
            // per contract: expiry/strike/right are contract properties (max() over duplicates).
            var registrySql =
                "SELECT symbol, max(expiry), max(strike), max(is_call) FROM r2.market." + RegistryTable + " " +
                $"WHERE underlying = '{und}' AND as_of <= {day} AND expiry > {day} " +
                "GROUP BY symbol;";
            var registered = new Dictionary<string, (DateTime expiry, decimal strike, bool isCall)>(StringComparer.Ordinal);
            foreach (var row in _catalog.CatalogRows(registrySql))
            {
                if (row.Length < 4 || string.IsNullOrEmpty(row[0]))
                {
                    continue;
                }
                // Registry dates print as plain "yyyy-MM-dd"; midnight, unspecified kind — the same
                // shape DateTime.ParseExact(DateFormat.SixCharacter) hands Lean's own parser.
                if (!DateTime.TryParseExact(row[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var expiry) ||
                    !long.TryParse(row[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var strikeInt))
                {
                    continue;
                }
                // Registry integer strike is OSI * 10_000 (O:GE260619C00130000 -> 130000 -> 13.0).
                registered[row[0]] = (expiry, strikeInt / 10_000m, IsTrue(row[3]));
            }

            // (2) Ledger: tick sessions actually loaded for this underlying on this date.
            var ledgerSql =
                "SELECT DISTINCT symbol FROM r2.market." + LedgerTable + " " +
                $"WHERE scope = 'option' AND underlying = '{und}' AND session_date = {day} " +
                "AND table_name IN ('option_trade_tick', 'option_quote_tick');";
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _catalog.CatalogRows(ledgerSql))
            {
                if (row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                {
                    loaded.Add(row[0]);
                }
            }

            var symbols = new List<Symbol>();
            foreach (var (ticker, (expiry, strike, isCall)) in registered
                         .Where(entry => loaded.Contains(entry.Key))
                         .OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                try
                {
                    var right = isCall ? OptionRight.Call : OptionRight.Put;
                    // Round-trip through Lean's own OSI parser: GenerateOptionTickerOSI builds
                    // the padded OSI string from the registry's structured fields, and
                    // ParseOptionTickerOSI constructs the canonical Symbol (with underlying)
                    // exactly as the rest of Lean does — no manual Symbol construction.
                    var tickerOsi = SymbolRepresentation.GenerateOptionTickerOSI(underlying, right, strike, expiry);
                    symbols.Add(SymbolRepresentation.ParseOptionTickerOSI(tickerOsi));
                }
                catch (Exception error)
                {
                    Log.Error($"R2OptionChainProvider: could not build symbol for registry ticker '{ticker}': {error.Message}");
                }
            }

            Log.Trace($"R2OptionChainProvider: {underlying} {date:yyyy-MM-dd}: {registered.Count} registered, " +
                      $"{symbols.Count} loaded & alive -> offered.");
            return symbols;
        }

        private static bool IsTrue(string value) =>
            value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
