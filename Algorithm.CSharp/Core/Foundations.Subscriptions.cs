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
        /// </summary>
        public Symbol ResolveOptionContract(Symbol underlying, string contract)
        {
            if (string.IsNullOrEmpty(contract))
            {
                return null;
            }
            if (SymbolCache.TryGetSymbol(contract, out Symbol cached) && Underlying(cached) == underlying)
            {
                return cached;
            }

            (Symbol, DateTime) key = (underlying, Time.Date);
            Dictionary<string, Symbol> chain;
            lock (_chainByValue)
            {
                if (!_chainByValue.TryGetValue(key, out chain))
                {
                    chain = new Dictionary<string, Symbol>(StringComparer.Ordinal);
                    foreach (Symbol symbol in OptionChainProvider.GetOptionContractList(underlying, Time.Date))
                    {
                        chain[symbol.Value] = symbol;

                        // Prime Lean's symbol cache with the CATALOGUE, not just the subscriptions: this
                        // registers the Symbol (value -> Symbol) without creating a subscription. Every
                        // other resolver in the algo (SymbolCache.TryGetSymbol on a target-portfolio
                        // holding string, in the sweeper, in the compatibility checks) then answers with a
                        // real Symbol instead of a cache miss, and nothing has to be carried to be known.
                        SymbolCache.Set(symbol.Value, symbol);
                    }
                    _chainByValue[key] = chain;
                    Log($"{Time} ResolveOptionContract: {underlying} chain loaded, {chain.Count} contracts for {Time.Date:yyyy-MM-dd} (symbol cache primed).");
                }
            }
            if (chain.TryGetValue(contract, out Symbol resolved))
            {
                return resolved;
            }

            // Not in today's chain — a rolled/expired contract, or a value from another session. Mint the
            // Symbol from the value itself: Symbol.CreateOption only builds the SID (it caches nothing and
            // subscribes nothing), so the cache is primed explicitly. That is what keeps every later
            // SymbolCache.TryGetSymbol off the SID-parse path, without touching QuantConnect core.
            if (TryParseContractValue(contract, out OptionRight right, out DateTime expiry, out decimal strike))
            {
                Symbol built = QuantConnect.Symbol.CreateOption(underlying, underlying.Value, underlying.ID.Market,
                    OptionStyle.American, right, strike, expiry);
                SymbolCache.Set(contract, built);
                SymbolCache.Set(built.Value, built);
                Log($"{Time} ResolveOptionContract: {contract} is not in the {underlying} chain for {Time.Date:yyyy-MM-dd} — " +
                    $"built from the value (expiry={expiry:yyyy-MM-dd}, {right}, strike={strike}) and cached.");
                return built;
            }

            return null;
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
