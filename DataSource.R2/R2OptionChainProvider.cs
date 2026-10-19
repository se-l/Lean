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
        /// First call for (underlying, date) also PREFETCHES the session batch: one catalog query
        /// per table replaces the per-contract round trips that follow.
        /// </summary>
        public IEnumerable<Symbol> GetOptionContractList(Symbol symbol, DateTime date)
        {
            var underlying = symbol.SecurityType.IsOption() ? symbol.Underlying.Value : symbol.Value;
            var day = date.Date;

            // The (session, underlying) batch: serves ResolveSymbol/HoldsDay for every contract of
            // this underlying for the TTL window (live setup asks per contract; on the cloudflare
            // metadata route each ask was a ~2.5 s catalog round trip).
            R2SessionPrefetch.Ensure(underlying, day, _catalog);

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

            var sw = System.Diagnostics.Stopwatch.StartNew();

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
            var registryMs = sw.ElapsedMilliseconds;

            // (2) Ledger: tick sessions actually loaded. The REQUESTED session may legitimately
            // have no data yet (live: the loader writes during/after the session), so walk back up
            // to 7 calendar days to the most recent session WITH data — that session's contract
            // set is what warmup can actually serve. Liveness still comes from the registry at the
            // requested date; the walk-back only relaxes the data-presence half.
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            DateTime referenceDay = date;
            for (var back = 0; back <= 7; back++)
            {
                var probe = date.AddDays(-back);
                loaded = LedgerSymbols(und, probe);
                if (loaded.Count > 0)
                {
                    referenceDay = probe;
                    break;
                }
            }
            var ledgerMs = sw.ElapsedMilliseconds - registryMs;

            if (referenceDay != date)
            {
                Log.Trace($"R2OptionChainProvider: {underlying} {date:yyyy-MM-dd}: no ledger data for the session — " +
                          $"using {referenceDay:yyyy-MM-dd}'s contract set ({loaded.Count} contracts).");
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
                      $"{symbols.Count} loaded & alive -> offered. (registry {registryMs} ms, ledger walk {ledgerMs} ms, total {sw.ElapsedMilliseconds} ms)");
            return symbols;
        }

        /// <summary>Distinct loaded symbols for one probe day, unioned over both option tick tables.</summary>
        private HashSet<string> LedgerSymbols(string und, DateTime probe)
        {
            var ledgerSql =
                "SELECT DISTINCT symbol FROM r2.market." + LedgerTable + " " +
                $"WHERE scope = 'option' AND underlying = '{und}' AND session_date = DATE '{probe:yyyy-MM-dd}' " +
                "AND table_name IN ('option_trade_tick', 'option_quote_tick');";
            var loaded = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in _catalog.CatalogRows(ledgerSql))
            {
                if (row.Length > 0 && !string.IsNullOrEmpty(row[0]))
                {
                    loaded.Add(row[0]);
                }
            }
            return loaded;
        }

        private static bool IsTrue(string value) =>
            value != null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
    }
}
