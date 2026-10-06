# Plan: backtesting Lean against the R2 Iceberg catalog

Status: proposal (no code yet). **Scope: the backtesting path only** — no live trading, no live
data queue, no replay. Source of data: the Cloudflare R2 Iceberg catalog (`trade.market.*`, loaded
by `Fino.TickLoad`), replacing the Lean-format `.zip` tree under `data-folder`
(`../../../trade/data`, config line 28).

## 1. What a backtest actually reads

`Launcher/config.json`, environment `backtesting` (lines 342–351):

| Key | Handler | Reads from |
|---|---|---|
| `data-feed-handler` | `FileSystemDataFeed` | the zip tree — **the tick stream the engine sees** |
| `history-provider` | `SubscriptionDataReaderHistoryProvider` | the same tree — `History()` and warmup |
| `map-file-provider` | `LocalDiskMapFileProvider` | map files in the tree — ticker→symbol identity |
| `factor-file-provider` | `LocalDiskFactorFileProvider` | factor files — splits/dividends |
| `setup-handler` / `result-handler` / `real-time-handler` / `transaction-handler` | Backtesting* variants | stay as-is |

So "a custom data feed handler" is one of **four** readers pointed at the same tree, and all four
matter *especially* in a backtest: the feed drives the streamed ticks, the history provider drives
every indicator's warmup, and the map/factor providers decide what the prices even mean. Swapping
only `data-feed-handler` yields a backtest that streams from R2 while warmup silently comes from
files — the divergence lands in the indicator values, which is the worst place to find it.

Activation is one line: a new `backtesting-r2` block cloned from `backtesting`, plus
`"environment": "backtesting-r2"` (line 10).

## 1b. Where this sits: live stays on Polygon, historical comes from R2

Agreed split:

- **Live data feed: existing Polygon handler, unchanged.** `data-queue-handler:
  QuantConnect.Lean.DataSource.Polygon.PolygonDataProvider` and `LiveTradingDataFeed` stay exactly
  as they are. This plan does not touch the live path.
- **Historical data: the R2 catalog**, via the components here.

The consequence worth being precise about: **the piece that serves both modes is the history
provider, not the feed.**

- In a **backtest**, two readers matter: the feed (the ticks the engine streams) and the history
  provider (`History()`, and every indicator's warmup).
- In **live**, the feed is Polygon's — but `History()` and warmup still route through
  `history-provider`. So a live algorithm subscribing to Polygon live ticks can still warm its
  indicators from the R2 catalog, with no change to the live handler at all.

That reorders the work: **`R2IcebergHistoryProvider` first** (it pays off in live warmup
immediately, and is reused by the backtest), then the feed handler for backtest streaming. The feed
swap is backtest-only; the history provider is the shared asset.

**New risk this split introduces — vendor consistency.** A live algorithm would warm up on Massive
`sip_timestamp` ticks and then trade on Polygon's live feed. Those are two different vendors: sizes,
exchange attribution and even prints can differ slightly, and an indicator warmed on one vendor's
tape feeding signals driven by another's is a subtle, hard-to-see discrepancy. Mitigation: before
trusting it, pull the same symbol-day from both sources and compare (count, VWAP, OHLC by minute).
If they disagree materially, warmup needs a documented tolerance rather than an assumption.

**Possible simplification to check before P2.** `data-provider` is its own config key (line 48,
currently `DefaultDataProvider`). If that seam can serve our data as the stream the existing
readers expect, a custom `IDataProvider` may be far less work than replacing `IDataFeed` wholesale,
because `FileSystemDataFeed` and its scheduling stay untouched. Not verified against this checkout
yet — worth an hour before committing to the heavier path.

## 2. The interface contract (read from this checkout)

`Engine/DataFeeds/IDataFeed.cs` — MEF-exported (`[InheritedExport(typeof(IDataFeed))]`), 5 members:

```csharp
bool IsActive { get; }
void Initialize(IAlgorithm algorithm, AlgorithmNodePacket job, IResultHandler resultHandler,
                IMapFileProvider mapFileProvider, IFactorFileProvider factorFileProvider,
                IDataProvider dataProvider, IDataFeedSubscriptionManager subscriptionManager,
                IDataFeedTimeProvider dataFeedTimeProvider, IDataChannelProvider dataChannelProvider);
Subscription CreateSubscription(SubscriptionRequest request);
void RemoveSubscription(Subscription subscription);
void Exit();
```

`FileSystemDataFeed` is the reference implementation; `SubscriptionUtils.CreateAndScheduleWorker`
(`Engine/DataFeeds/SubscriptionUtils.cs:72`) turns a per-subscription enumerator into a scheduled
`Subscription`. The handler is therefore thin: one enumerator factory per subscription. The work
is in the enumerator and in the sibling providers (§5).

## 3. Data access: R2 is never in the hot path

Measured on this host: **one cold query against the R2 catalog ~5.0s wall, 0.4s CPU** (serial
round-trips: catalog → metadata → manifest → Parquet footer → first range). A backtest reading
ticks on demand would stall on that thousands of times.

**A backtest is the best case for caching, because nothing needs to arrive live.** Preflight
materializes the requested range as local per-symbol-day Parquet and the enumerator streams from
NVMe. R2 stays the system of record; the local tree is a rebuildable cache.

**Read shape — selective, not whole-file, and not per-tick either.** Two failure modes to avoid:

- *Not* a live SELECT-per-tick stream from R2 during the run: each query re-walks
  catalog → metadata → manifest, and a synchronous enumerator would stall on it thousands of times.
  The run reads **zero** bytes from R2.
- *Not* a whole-file dump either. The materialization is a **column-projected, session-filtered**
  read (`COPY (SELECT <needed columns> FROM … WHERE session …) TO …`): only fields Lean consumes
  (time, price, size, exchange; bid/ask/sizes for quotes; `sequence_number` for tie-breaking), and
  only sessions in the backtest range.

Be clear about how far pruning actually reaches, though: Iceberg prunes at **file** granularity from
manifest bounds, so a session filter avoids the sessions in other files, and Parquet row-group stats
prune within a file — but a *symbol* filter does not reliably narrow a file that holds many symbols
in mixed order. The bandwidth wins here are session pruning plus column projection; symbol-level
pruning is not free, and shouldn't be assumed.

**The cheapest cache is built at load time, not read back.** The loader already stages parts
locally per symbol-day before committing; materializing the Lean cache from those parts costs
almost nothing and skips R2 entirely for fresh data. Use the R2 read-back path for sessions already
in the catalog, and the staging path for sessions just loaded.

**Coverage preflight is metadata-only.** `tick_load_log` answers "is this range complete?" from the
ledger without touching a single tick — so the guard that fails a backtest on holes costs one small
query, not a data scan.

| Subscription | Source table | Session column | Order |
|---|---|---|---|
| Equity trade tick | `equity_trade_tick` | `sip_timestamp` (UTC ns) | `(symbol_id, sip_timestamp)` |
| Equity quote tick | `equity_quote_tick` | `sip_timestamp` | `(symbol_id, sip_timestamp)` |
| Option trade tick | `option_trade_tick` | `session_date` + `sip_timestamp` | `(symbol_id, session_date, sequence_number)` |
| Option quote tick | `option_quote_tick` | as above (load in flight) | as above |

## 4. Backtest-specific design points

- **Preflight coverage check — the highest-value item in this plan.** `tick_load_log` already
  records `loaded` / `rows_n` per symbol-day and per dataset. Before a run, intersect the
  requested backtest range with that ledger and **fail fast, listing the holes**, if any session is
  missing or unverified. A backtest cannot detect a missing day on its own: an absent file and a
  day of no trading produce the same silence, and the result is a strategy that looks fine because
  it never traded. The file-based path has no equivalent guard; this is a real upgrade, not just
  parity.
- **Determinism.** Same range + same data = same run. Emit ticks in a total order (break ties on
  `sequence_number` where present) so results are reproducible across runs and machines.
- **Time.** `sip_timestamp` is UTC nanoseconds; Lean wants market time (ET) on `BaseData.Time`.
  Convert at the boundary, and take the session from `session_date` on the option tables rather
  than from a bare UTC date — a UTC day is not an ET session (a trap already documented in the
  loader).
- **Ticks in, bars by Lean.** Emit raw `Tick` objects (trades: `Tick(t, sym, "", exch, price,
  size)`; quotes: `Tick(t, sym, exch, bid, ask, bidSz, askSz)`) and let
  `data-aggregator: AggregationManager` (line 51) build seconds/minutes. The handler never
  constructs bars.
- **Universe scope must be explicit.** The catalog supports fixed-instrument subscriptions and
  option chains. It has **no** market-wide data, so `AddUniverse`-style selection (fundamentals,
  ETF constituents, market-wide screens) must be out of scope and fail loudly, not return an empty
  universe that silently trades nothing.
- **Exchange code.** Tables carry an integer `exchange_id`; Lean wants an exchange string on the
  `Tick`. One static id→name map, applied per row.
- **Option volume.** Option quotes across thousands of contracts for a month dominate everything
  else; a backtest may want the window narrowed or quotes excluded per contract. Decide before P3,
  not during.

## 4b. Resolutions: tick vs second / minute / hour / daily

Lean asks for one resolution per subscription (`AddEquity("CRWD", Resolution.Minute)`,
`History(sym, 200, Resolution.Daily)`), and our catalog holds **ticks only**. Three serving modes,
in order of how much I'd trust them:

1. **Tick subscription → raw ticks.** Direct from the local cache. Nothing to decide.
2. **Coarse subscription → let Lean aggregate the ticks.** `data-aggregator: AggregationManager`
   (line 51) already consolidates tick→second→minute→hour→daily inside the engine. This is
   *correct by construction*: Lean's consolidators own the session-boundary, extended-hours and
   bar-shape semantics, so OHLC matches what a file-sourced backtest would produce. The cost is
   brutal for coarse work — a daily bar for one session means feeding 168k CRWD ticks through the
   engine to get four numbers.
3. **Coarse subscription → pre-aggregate.** `GROUP BY` over the local Parquet with DuckDB, then
   serve bars. Fast, but it makes *us* responsible for matching Lean's semantics exactly.

**If mode 3 is used, two things are load-bearing:**

- **Bucket by ET session, never by UTC day.** `sip_timestamp` is UTC nanoseconds; a naive
  `date_trunc('day')` produces the wrong daily bar, because a UTC day is not an ET session. Use
  `session_date` where it exists (option tables) and derive the ET session otherwise. This trap is
  already documented on the loader side; it reappears here with prices attached.
- **Extended hours is a declared decision, not a default.** Whether a daily bar's O/H/L/C includes
  pre/post ticks changes the values. Lean makes this configurable per subscription; a pre-aggregate
  must be told which answer it is producing, and say so.

**Where to store aggregates — derived in R2, built by the loader.** For `History()` at coarse
resolutions, streaming ticks to answer a daily question is indefensible, so bars should exist. Make
them a **derived dataset** rather than a second authority:

- Build them in the loader, from the same locally staged parts the tick load already uses — nearly
  free, and it keeps the single entry point. Never hand-maintained; always rebuilt, so they cannot
  drift from the ticks.
- Mark them derived and rebuildable, with the tick tables as the only source of truth. A
  pre-aggregated table that disagrees with its ticks is worse than no table.
- R2 egress is free, and bars are tiny, so storage is not the constraint — the reason to be careful
  is correctness, not cost.
- Validate against mode 2: compare pre-aggregated bars against the aggregator's output on the same
  session (same idea as P4). Any difference is our aggregation semantics, not "noise".

**Decided: implement modes 1 and 2.** The feed serves ticks for every subscription (the engine
consolidates, so tick and minute subscriptions are one code path). The **history provider** is where
resolutions diverge: it must return bars for a coarse `History()` request, and it does so by
running **Lean's own consolidators** (`TradeBarConsolidator` / `QuoteBarConsolidator`) over the tick
stream. Same classes the engine uses, so bars match Lean by construction rather than by
resemblance.

That single choice resolves three problems at once:

- the history provider satisfies coarse `History()` calls without a second aggregation implementation
- the **pre-aggregates we own for outside-Lean analytics** are produced by the same consolidators,
  so "must match Lean" is a property of the code, not a hope
- reconciliation needs no Lean run at all — see the oracle below

**Reconciliation oracle: the zip bars, not a backtest pair.** Pre-aggregates are already stored as
daily / hourly / minute / second bars in zip form. Consolidate the ticks and compare against those
zips directly. That is a cheaper and sharper oracle than running the same backtest twice: it tests
the aggregation itself, on every session in the range, with no engine in the loop. Divergences are
then either our consolidation or a genuine data difference between the two vendors — both worth
knowing, and both visible immediately.

**Ownership rule that follows:** the pre-aggregates stored in R2 are a real product (research and
analytics outside Lean), not a cache. They still must be built from the tick tables by one code
path, so the tick data remains the single source of truth — but they are expected to be read,
queried and relied on directly, and validated against the zip bars rather than regenerated
casually.

## 5. Sibling providers (the other three readers)

1. **`R2IcebergHistoryProvider`** — the same local-Parquet reader behind `IHistoryProvider`, so
   warmup and `History()` come from the catalog too. Without it, indicators read zips while ticks
   come from R2: two sources inside one run.
2. **Map/factor providers — decided: keep them local (agreed).** `LocalDiskMapFileProvider` /
   `LocalDiskFactorFileProvider` read from `data-folder`, so the R2 migration does **not** mean
   deleting the data tree; it shrinks it from all the tick data down to a small metadata sliver
   that Lean needs to interpret prices at all:

   - `map_files/` — ticker→symbol identity. One row for CRWD (no ticker changes in the window);
     hand-written is fine.
   - `factor_files/` — splits/dividends. The catalog has none, and alignment matters here: our data
     is **raw** SIP ticks, so the matching Lean setting is `DataNormalizationMode.Raw`, under which
     factors are not used for adjustment. That is only safe because no corporate action falls in
     the sample window — a split or dividend inside any future window makes the factor file
     load-bearing, not optional.
   - `symbol-properties/symbol-properties-database.csv` — **the third piece, easy to forget.** Tick
     size, lot size, market hours, settlement. This is not covered by either provider above, and
     Lean reads a local override when present. Without a correct CRWD entry, fills and bar
     boundaries are quietly wrong.

   Options do not need per-contract map files: Lean builds option symbols from the underlying's
   identity plus expiry/strike/right, which the catalog already carries in `option_contract_live`.
   So the local metadata burden is one underlying, not 3,674 contracts.
3. **Option chain universe** — Lean's chain comes from a universe emitting chain data, which a
   custom feed must supply itself. `option_contract_live` is the right source (one row per live
   contract per session: 3,674 contracts for CRWD Sept 2026, ~3,050–3,250 live each session,
   stepping up through the week and dropping after expiry). Largest single piece of work, and the
   schedule risk.

## 6. Integration and packaging

- Clone the `backtesting` environment to `backtesting-r2` in `Launcher/config.json`, swapping
  `data-feed-handler` + `history-provider` (and optionally the map/factor providers). The original
  `backtesting` block stays untouched as the fallback.
- Activate with `"environment": "backtesting-r2"` (line 10). No live environment is involved.
- Plugin assembly dropped beside the Launcher — the same MEF-over-app-directory mechanism that
  makes `QuantConnect.Lean.DataSource.Polygon.PolygonDataProvider` resolvable (there is no
  `plugin-directory` key in the config).
- Template to mirror: the in-repo `ToolBox/Polygon/*` sources (`PolygonDataDownloader.cs`,
  `PolygonSymbolMapper.cs`) — an existing vendor→Lean mapping pipeline.

## 7. The alternative, and where it stands for a backtest-only goal

Convert the catalog once into Lean-format zips (a batch job, no engine changes) and every
backtest reader above works unmodified — history, map files, option chains, ToolBox, all of it.

Be clear-eyed here. The custom handler's main structural payoff is *one* source for backtest **and**
live. With live out of scope, that payoff does not apply, while the costs (history provider, chain
universe, symbol identity) are unchanged. So for a backtest-only goal the converter is the cheaper
and lower-risk path, and the handler's remaining advantages are narrower: no duplicated tree, no
conversion step to keep in sync, and the coverage-ledger preflight that the zip path cannot offer.

Recommendation: build the handler if the catalog should be the single source of truth for research
runs; otherwise convert. Either way, use the converter as the **oracle** — same data both ways,
compare fills — rather than validating the handler by inspection.

## 8. Phases

- **P0 — offline spike, no engine.** Console app: read one session of CRWD equity trades from local
  Parquet (DuckDB.NET/Arrow), assert the row count against `tick_load_log.rows_n`, convert UTC→ET,
  build `Tick` objects. Proves the reader and the time conversion.
- **P1 — equity ticks end-to-end.** `R2IcebergDataFeed` + enumerator wired into `backtesting-r2`,
  run `EarningsAlgorithm` over CRWD 2026-09-25. Acceptance: run completes and consumed tick count
  equals the ledger row count for that session.
- **P2 — quotes + history provider.** Quote enumeration and `R2IcebergHistoryProvider`; verify
  warmup/indicators see the same series as the tick stream.
- **P3 — options.** Trade/quote ticks by contract, then the chain universe from
  `option_contract_live`. Acceptance: selected contracts' metadata matches the catalog
  (expiry/strike/right) and per-contract volume is bounded by an explicit decision (§4).
- **P4 — equivalence and falsification.** Same backtest through converted zips and through the
  handler; diff fills, positions, cash. Both paths see identical bytes, so any divergence is a
  handler bug — never "noise".

## 9. Coverage, risks, open questions

- **One underlying.** CRWD only: 18 equity sessions (2026-09-01..25); option trades for 09-25
  loaded, full-month option quotes in flight. Anything else must fail loudly.
- **No factor data** (§5.2). **Option chain complexity** (§5.3) is the schedule risk.
- **Tick volume** for option quotes is the dominant cost; decide the window/selection up front.
- **Language**: C# plugin is the natural fit (`IDataFeed` is a C# interface); a Python provider via
  PythonNet is possible but adds a moving part with no backtest-side benefit.
- **Sync step ownership**: Fino (`TickLoad` already knows coverage) vs Lean ToolBox. Single entry
  point argues for Fino.

## 10. What this plan does not claim

`FileSystemDataFeed.CreateSubscription` and the option-chain universe flow were located, not read
end-to-end. §2's interface was read directly from the checkout. P0/P1 are sized to surface the rest
cheaply before P3 commits to anything.
