using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using QuantConnect.Orders;
using QuantConnect.Securities;
using QuantConnect.Securities.Option;
using static QuantConnect.Algorithm.CSharp.Core.Statics;

namespace QuantConnect.Algorithm.CSharp.Core
{
    /// <summary>
    /// Need-to-know option subscriptions.
    ///
    /// The algo no longer subscribes the chain at start: it carries exactly the contracts it can act on —
    /// what Lean says we hold, plus what the current target holdings say we should hold — and nothing
    /// else. The rest of the chain is Mind's problem; carrying thousands of dead subscriptions only
    /// costs websocket capacity.
    ///
    /// Both inputs are existing state, not a second book: <see cref="Security.Holdings"/> is maintained
    /// by Lean, and <see cref="TargetHoldings"/> is rebuilt from the surviving target portfolios. The set
    /// is rebuilt from scratch and diffed against <see cref="Securities"/>, so the call is idempotent and
    /// safe from every change site.
    /// </summary>
    public partial class Foundations
    {
        /// <summary>
        /// Unsubscribing is only ARMED once the carried set is this large. Below it, a contract missing
        /// from the desired set is far more likely to be a transient state — target holdings cleared a
        /// moment before they are rebuilt, a portfolio response still streaming — than a real orphan, and
        /// carrying a dead subscription costs one idle feed entry next to dropping something needed again.
        /// </summary>
        private const int UnsubscribeThreshold = 1000;

        /// <summary>Re-entrancy guard: AddOptionContract/RemoveSecurity raise holdings and security events.</summary>
        private bool _syncingOptionSubscriptions;

        /// <summary>The session's contract list for an underlying, keyed by <see cref="Symbol.Value"/>.</summary>
        private readonly Dictionary<(Symbol Underlying, DateTime Day), Dictionary<string, Symbol>> _chainByValue = new();

        /// <summary>
        /// Turn a contract string from a target portfolio into a Symbol WITHOUT requiring it to be
        /// subscribed: SymbolCache only knows symbols Lean has already created, so a contract we are
        /// being asked to trade for the first time resolves through the session chain instead.
        ///
        /// This is the ONLY entry point for a contract string that came off the wire, and it does not
        /// ask <see cref="SymbolCache"/> first on purpose. On a miss that lookup falls through to
        /// <see cref="SecurityIdentifier.TryParse"/>, which decodes a fixed-width option value as a
        /// base36 "symbol properties" pair. The 15-char tail overflows <c>ulong</c>, so the decoded
        /// SecurityType (properties % 100) is garbage: usually an invalid type — one ERROR line plus a
        /// stack trace per value, cached as null. About one value in 25 decodes to
        /// FutureOption/Option/IndexOption, and for one of those in ~25 the market field is registered
        /// too (~1 contract in 700 of a real chain); then <c>new Symbol(sid, sid.Symbol)</c> dereferences
        /// a null underlying in <see cref="Symbol.GetAlias"/> and throws NullReferenceException out of
        /// the caller. That is what killed OnResponseTargetPortfolios: the response was dropped before
        /// TargetHoldings were set, so nothing was subscribed and no orders were requested.
        /// </summary>
        public Symbol ResolveOptionContract(Symbol underlying, string contract)
        {
            if (string.IsNullOrEmpty(contract) || underlying == null)
            {
                return null;
            }
            if (underlying.SecurityType != SecurityType.Equity)
            {
                if (underlying.SecurityType != SecurityType.Option)
                {
                    return null;   // Underlying() throws for anything that is neither an equity nor an option.
                }
                underlying = Underlying(underlying);
            }

            // Value-shaped ("NET   260807P00287500"): the chain, else the value itself.
            if (TryParseContractValue(contract, out OptionRight right, out DateTime expiry, out decimal strike))
            {
                string key = NormalizeContractValue(contract);
                Dictionary<string, Symbol> chain = ChainFor(underlying);
                if (chain.TryGetValue(key, out Symbol resolved))
                {
                    return resolved;
                }

                // Not in today's chain — a rolled/expired contract, or a value from another session.
                // Symbol.CreateOption only builds the SID (it caches nothing and subscribes nothing),
                // so the cache is primed explicitly under every spelling we have seen, and the symbol is
                // remembered in the day's catalogue so one value is minted once, not once per response.
                Symbol built = QuantConnect.Symbol.CreateOption(underlying, underlying.Value, underlying.ID.Market,
                    OptionStyle.American, right, strike, expiry);
                SymbolCache.Set(contract, built);
                SymbolCache.Set(key, built);
                SymbolCache.Set(built.Value, built);
                lock (_chainByValue)
                {
                    if (_chainByValue.TryGetValue((underlying, Time.Date), out Dictionary<string, Symbol> dayChain))
                    {
                        dayChain[key] = built;
                    }
                }
                Log($"{Time} ResolveOptionContract: {contract} is not in the {underlying} chain for {Time.Date:yyyy-MM-dd} — " +
                    $"built from the value (expiry={expiry:yyyy-MM-dd}, {right}, strike={strike}) and cached.");
                return built;
            }

            // Not a contract value: a plain ticker or a real SecurityIdentifier string. Still guarded —
            // a malformed identifier must answer "unresolvable", never throw out of a handler.
            try
            {
                if (SymbolCache.TryGetSymbol(contract, out Symbol cached) &&
                    cached.SecurityType == SecurityType.Option &&
                    Underlying(cached) == underlying)
                {
                    return cached;
                }
            }
            catch (Exception exception)
            {
                Log($"{Time} ResolveOptionContract: '{contract}' is not a resolvable identifier ({exception.Message}).");
            }

            return null;
        }

        /// <summary>
        /// The session's contract catalogue for an underlying, keyed by
        /// <see cref="NormalizeContractValue"/>. Loading it also primes <see cref="SymbolCache"/> with
        /// the CATALOGUE, not just the subscriptions: every contract becomes known (value -> Symbol)
        /// without creating a subscription, so later lookups are dictionary hits.
        /// </summary>
        private Dictionary<string, Symbol> ChainFor(Symbol underlying)
        {
            (Symbol, DateTime) key = (underlying, Time.Date);
            lock (_chainByValue)
            {
                if (_chainByValue.TryGetValue(key, out Dictionary<string, Symbol> cached))
                {
                    return cached;
                }

                var chain = new Dictionary<string, Symbol>(StringComparer.OrdinalIgnoreCase);
                foreach (Symbol symbol in OptionChainProvider.GetOptionContractList(underlying, Time.Date))
                {
                    chain[NormalizeContractValue(symbol.Value)] = symbol;
                    SymbolCache.Set(symbol.Value, symbol);
                }
                _chainByValue[key] = chain;
                Log($"{Time} ResolveOptionContract: {underlying} chain loaded, {chain.Count} contracts for {Time.Date:yyyy-MM-dd} (symbol cache primed).");
                return chain;
            }
        }

        /// <summary>
        /// "CRWV  270917C00072500" -> "CRWV270917C00072500". The wire string, <see cref="Symbol.Value"/>
        /// and the chain catalogue agree on the digits but not always on the ticker padding, so keys are
        /// compared without whitespace.
        /// </summary>
        internal static string NormalizeContractValue(string contract)
        {
            return contract == null
                ? string.Empty
                : string.Concat(contract.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        }

        /// <summary>
        /// "NET   260807P00287500" -> (right, expiry, strike). The value is fixed width: the ticker is
        /// padded, then yyMMdd, then the right, then the strike * 1000 on eight digits.
        /// </summary>
        private static bool TryParseContractValue(string contract, out OptionRight right, out DateTime expiry, out decimal strike)
        {
            right = default;
            expiry = default;
            strike = default;
            if (string.IsNullOrWhiteSpace(contract))
            {
                return false;
            }

            string value = contract.Trim();
            const int tailLength = 15;                       // yyMMdd (6) + C/P (1) + strike*1000 (8)
            if (value.Length < tailLength)
            {
                return false;
            }
            string tail = value.Substring(value.Length - tailLength);

            if (!DateTime.TryParseExact(tail.Substring(0, 6), "yyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out expiry))
            {
                return false;
            }

            char rightChar = tail[6];
            if (rightChar != 'C' && rightChar != 'P')
            {
                return false;
            }
            right = rightChar == 'C' ? OptionRight.Call : OptionRight.Put;

            if (!decimal.TryParse(tail.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out decimal strikeMilli))
            {
                return false;
            }
            strike = strikeMilli / 1000m;
            return true;
        }

        /// <summary>
        /// Contracts this underlying must be subscribed to: everything we hold, plus everything with a
        /// non-zero target holding. A contract we neither hold nor intend to hold is not carried.
        /// </summary>
        public HashSet<Symbol> DesiredOptionContracts(Symbol underlying)
        {
            var desired = new HashSet<Symbol>();

            // 1. Holdings. `Invested` is Lean's own flag on Security.Holdings, so it cannot drift from
            //    Portfolio the way a separate quantity book would.
            foreach (Security security in Securities.Values)
            {
                if (security.Type == SecurityType.Option &&
                    security.Holdings.Invested &&
                    Underlying(security.Symbol) == underlying)
                {
                    desired.Add(security.Symbol);
                }
            }

            // 2. Target holdings. A non-zero target is the only reason to carry a contract we do not own.
            foreach (var kvp in TargetHoldings)
            {
                if (kvp.Value != 0 && Underlying(kvp.Key) == underlying)
                {
                    desired.Add(kvp.Key);
                }
            }

            return desired;
        }

        /// <summary>
        /// Diff the desired contract set against the current subscriptions and add/remove the delta.
        /// </summary>
        public void SyncOptionSubscriptions(Symbol underlying, string reason)
        {
            if (underlying == null)
            {
                return;
            }
            if (underlying.SecurityType == SecurityType.Option)
            {
                underlying = Underlying(underlying);
            }
            if (underlying.SecurityType != SecurityType.Equity || _syncingOptionSubscriptions)
            {
                // Underlying() throws for anything that is neither an equity nor an option.
                return;
            }

            _syncingOptionSubscriptions = true;
            try
            {
                HashSet<Symbol> desired = DesiredOptionContracts(underlying);
                HashSet<Symbol> current = Securities.Keys
                    .Where(s => s.SecurityType == SecurityType.Option && !s.IsCanonical() && Underlying(s) == underlying)
                    .ToHashSet();

                DateTime today = Time.Date;
                List<Symbol> add = desired.Where(s => !current.Contains(s) && s.ID.Date >= today).ToList();

                // Removals are armed only above the threshold: below it a transient "target holdings are
                // momentarily empty" cannot wipe the subscriptions, which is exactly what happened when
                // ClearTargetHoldings ran a sync before the survivors were rebuilt.
                bool armed = current.Count > UnsubscribeThreshold;
                List<Symbol> remove = armed ? current.Where(s => !desired.Contains(s)).ToList() : new List<Symbol>();
                if (!armed && current.Count > 0)
                {
                    int wouldRemove = current.Count(s => !desired.Contains(s));
                    if (wouldRemove > 0)
                    {
                        Log($"{Time} SyncOptionSubscriptions: {underlying} ({reason}) would unsubscribe {wouldRemove} of " +
                            $"{current.Count} contracts; removals are armed only above {UnsubscribeThreshold}. Carrying them.");
                    }
                }

                foreach (Symbol symbol in add)
                {
                    AddOptionContract(symbol, resolution: Resolution.Second, fillForward: false, extendedMarketHours: true);
                }

                int removed = remove.Count(symbol => RemoveOptionContract(symbol, reason));
                if (add.Count != 0 || removed != 0)
                {
                    Log($"{Time} SyncOptionSubscriptions: {underlying} ({reason}) subscribed=+{add.Count} unsubscribed=-{removed} " +
                        $"carried={current.Count + add.Count - removed}");
                }
            }
            catch (Exception exception)
            {
                // Subscription housekeeping must never take the algorithm down.
                Error($"{Time} SyncOptionSubscriptions: {underlying} ({reason}) failed: {exception.Message}");
            }
            finally
            {
                _syncingOptionSubscriptions = false;
            }
        }

        /// <summary>
        /// Unsubscribe one contract, but only when nothing can still act on it: no holding and no live
        /// ticket. Option-keyed side state is dropped with it so a later re-subscribe starts clean.
        /// </summary>
        public bool RemoveOptionContract(Symbol symbol, string reason)
        {
            if (Securities.TryGetValue(symbol, out Security security) && security.Holdings.Invested)
            {
                return false;
            }

            if (orderTickets.TryGetValue(symbol, out List<OrderTicket> tickets) &&
                tickets.Any(t => t != null && !orderFilledCanceledInvalid.Contains(t.Status)))
            {
                return false;
            }

            Log($"{Time} RemoveOptionContract: {symbol} ({reason}) — no holding, no live ticket.");

            if (SpreadBuffers.TryGetValue(OrderDirection.Buy, out var buys)) buys.TryRemove(symbol, out _);
            if (SpreadBuffers.TryGetValue(OrderDirection.Sell, out var sells)) sells.TryRemove(symbol, out _);
            if (security is Option option)
            {
                PresumedFillIV.TryRemove(option, out _);
            }
            RiskScenarioHandler?.ClearSweep(symbol);
            return RemoveSecurity(symbol);
        }
    }
}
