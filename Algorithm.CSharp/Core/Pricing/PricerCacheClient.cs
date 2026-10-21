/*
 * PricerCacheClient — the execution-node side of the split-topology pricer cache
 * (spec: Fino.jl/docs/pricer-cache-dotnet-agent-spec.md).
 *
 * Role split (do not blur):
 *   - This agent PUBLISHES every pricer request (JSON, full price-of-cash `env` bundle
 *     forwarded verbatim) to exchange `pricer.worker`, rk `pricer.req`. It NEVER subscribes
 *     to `pricer.worker.requests` — that queue belongs to the Julia GPU feeder.
 *   - This agent SUBSCRIBES to exchange `pricer.cache` (rk `greek.#`) with its OWN exclusive
 *     auto-delete queue and applies greek deltas — iv/delta/theta/vega/volga per node key
 *     [ticker, right, K, tq, s_tick, c_tick] — into an in-process book. Application is
 *     idempotent (content-addressed keys, at-least-once delivery is harmless). A resync BURST
 *     is chunked: only its FIRST message carries `resync:true`, and the consumer clears the
 *     book once per burst (guarded by `cycle`, so a feeder that flags every chunk cannot wipe
 *     it repeatedly). A `seq` gap suspends serving until the next resync.
 *   - LOOKUP: quantize (T, S, price) to (tq, s_tick, c_tick), exact-node key only (no
 *     interpolation — the feeder's rounding + corridor construction guarantee the cell).
 *   - MISS: caller falls back to the existing ZMQ JuliaPricer call (unchanged path) AND
 *     this request is published so the feeder warms the node — same request = cache hit next time.
 *
 * Hard rules:
 *   - The agent never computes greeks and never resolves the env itself: the env bundle is
 *     resolved ONCE per (ticker, calc date) by the same enrichment the ZMQ pricer resolves
 *     (PricerZMQ.enrich_request via the MoneynessForward port), serialized ONCE into a
 *     cached byte fragment, and forwarded verbatim — byte-identical across all messages
 *     sharing that (ticker, date), as the Julia content-hash requires.
 *   - v1 cannot recompute Julia's env-hash: the ACTIVE book is the most recently updated
 *     env book that is still fresh (5 s staleness gate; proper cross-language hash = later work).
 *   - Broker trouble → ZMQ-fallback-only mode with re-publish backoff; a Lean call is NEVER
 *     blocked on MQ (publish is a non-blocking TryWrite into a bounded channel; lookups are
 *     pure in-memory reads under a short lock).
 *   - Mode-agnostic, warmup-gated at the call site: warmup publishes only (training the feeder
 *     on the data about to matter); serving starts at warmup completion. Backtests run the
 *     cache by design — each sim date trains its own env-hash book, so dates cannot cross-serve.
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Newtonsoft.Json.Linq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuantConnect.Algorithm.CSharp.Core.Pricing
{
    public sealed class PricerCacheClient : IDisposable
    {
        // Wire constants — must match BulkPricerMQ.jl (REQ_EXCHANGE/REQ_RK/OUT_EXCHANGE).
        private const string RequestExchange = "pricer.worker";
        private const string RequestRoutingKey = "pricer.req";
        private const string DeltaExchange = "pricer.cache";
        private const string DeltaRoutingKeyBind = "greek.#";

        // Serving config — mirrors PricerConfig defaults (PricerCache.jl). tick = one US cent.
        private const double Tick = 0.01;
        private const int TenorDigits = 3;
        // The feeder publishes ~2-3 delta messages per solve cycle and cycles take 3-5 s
        // (8 s+ under load) — a 5 s window blacked out lookups between publishes and turned
        // live cells into misses. 30 s ≈ 6-10 cycles still catches a dead feeder quickly.
        private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(3);

        private readonly Foundations _algo;
        private readonly string _host;
        private readonly int _port;
        private readonly string _vhost;
        private readonly string _user;
        private readonly string _pass;
        private readonly string _queueName;
        private readonly object _bookLock = new();
        private readonly Dictionary<string, EnvBook> _books = new();          // env-hash hex -> book
        private readonly Dictionary<string, ushort> _tickerIds = new(StringComparer.Ordinal);  // ticker -> id (keys are string-free)
        private readonly List<string> _tickerNames = new();
        private readonly Channel<byte[]> _outbound;                           // bounded; DropOldest
        private readonly CancellationTokenSource _cts = new();
        private IConnection _connection;
        private IChannel _publishChannel;
        private IChannel _consumeChannel;
        private Task _publishLoop;
        private Timer _statsTimer;
        private volatile bool _brokerUp;
        private volatile bool _staleUntilResync;   // seq gap / restart / incomplete burst — serve nothing until resync
        private long _lastSeq = -1;
        private long _lastResyncCycle = -1;        // burst id — the book is cleared once per resync
        private DateTime _bookSimDate;             // sim date the resident env books belong to
        private long _lookups, _published, _publishDropped, _deltasApplied, _rowsSkipped;

        /// <summary>Per-greek-kind lookup counters (exact / sigma-mismatch / miss).</summary>
        private readonly Dictionary<GreekKind, KindCounters> _byKind = new();

        private KindCounters Counters(GreekKind kind)
        {
            lock (_bookLock)
            {
                if (!_byKind.TryGetValue(kind, out var c))
                {
                    c = new KindCounters();
                    _byKind[kind] = c;
                }
                return c;
            }
        }

        private sealed class KindCounters
        {
            public long Exact;
            public long SigmaMismatch;
            public long Miss;
            public long Lookups;   // last-snapshot total for StatsByKindSinceLast
        }

        /// <summary>Per-kind counters as of the previous StatsByKindSinceLast call.</summary>
        private readonly Dictionary<GreekKind, KindCounters> _lastByKind = new();

        public PricerCacheClient(Foundations algo, string host, int port, string vhost, string user, string pass)
        {
            _algo = algo;
            _host = host;
            _port = port;
            _vhost = vhost;
            _user = user;
            _pass = pass;
            _queueName = $"pricer.cache.{Environment.MachineName.ToLowerInvariant()}";
            _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4_096)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
        }

        public void Start()
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = _host,
                    Port = _port,
                    VirtualHost = _vhost,
                    UserName = _user,
                    Password = _pass,
                    AutomaticRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                    RequestedHeartbeat = TimeSpan.FromSeconds(30),
                };
                _connection = factory.CreateConnectionAsync($"pricer-cache-{Environment.MachineName}").GetAwaiter().GetResult();

                _publishChannel = _connection.CreateChannelAsync().GetAwaiter().GetResult();
                _publishChannel.ExchangeDeclareAsync(RequestExchange, ExchangeType.Topic, durable: true)
                    .GetAwaiter().GetResult();

                _consumeChannel = _connection.CreateChannelAsync().GetAwaiter().GetResult();
                _consumeChannel.ExchangeDeclareAsync(DeltaExchange, ExchangeType.Topic, durable: true)
                    .GetAwaiter().GetResult();
                // Exclusive + auto-delete: a push replica, loss-tolerant by design (periodic
                // resync heals). Exclusive transient queues remain legal on RabbitMQ 4.x.
                var queueResult = _consumeChannel.QueueDeclareAsync(
                    queue: _queueName, durable: false, exclusive: true, autoDelete: true).GetAwaiter().GetResult();
                _consumeChannel.QueueBindAsync(queueResult.QueueName, DeltaExchange, DeltaRoutingKeyBind)
                    .GetAwaiter().GetResult();

                var consumer = new AsyncEventingBasicConsumer(_consumeChannel);
                consumer.ReceivedAsync += OnDeltaReceivedAsync;
                _consumeChannel.CallbackExceptionAsync += (sender, args) =>
                {
                    // Without an exception handler, failures inside ReceivedAsync are swallowed
                    // and the log stays clean while every message is dropped — wire it first.
                    MarkBrokerDown($"consumer callback: {args.Exception.Message}");
                    return Task.CompletedTask;
                };
                _consumeChannel.BasicConsumeAsync(queueResult.QueueName, autoAck: true, consumer)
                    .GetAwaiter().GetResult();

                _publishLoop = Task.Run(PublishLoopAsync);
                _statsTimer = new Timer(_ => LogStats(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                _brokerUp = true;
                _algo.Log($"PricerCacheClient: connected. queue={_queueName} vhost={_vhost} exchange_in={RequestExchange} exchange_out={DeltaExchange}");
            }
            catch (Exception e)
            {
                // Fail open: the ZMQ path is untouched; keep retrying the broker in background.
                _brokerUp = false;
                _algo.Log($"PricerCacheClient: connect failed ({e.Message}) — ZMQ-fallback-only; will retry.");
                _publishLoop = Task.Run(PublishLoopAsync);
            }
        }

        // ── Lookup ────────────────────────────────────────────────────────────────

        /// <summary>The greek kinds the feeder publishes (PR_WORKER_GREEKS default set) + IV.</summary>
        public enum GreekKind { Iv, Delta, Theta, Vega, Volga }

        /// <summary>Node key, stored as a SAFE (exact), PACKED type: strike in cents (uint),
        /// tenor ×10000 (ushort — fits any tenor below 6.55 years), right as byte 0/1, the
        /// feeder's own tick integers for spot/price (uint), and a per-process ticker id
        /// (ushort) instead of a string. 20 bytes, no per-node string allocation — a string in
        /// every key cost ~32 B/node and a GC allocation per parsed row at tens of millions of
        /// nodes. Integer keys compare exactly on both sides; K/tq convert through the feeder's
        /// own quantization (strike ×100, tq 3-significant-digit ×10000).
        /// </summary>
        public readonly struct NodeKey : IEquatable<NodeKey>
        {
            public readonly ushort TickerId;  // per-process ticker pool id
            public readonly byte Right;       // 1 = call, 0 = put
            public readonly ushort Tq10k;     // tenor ×10000 (3-sig-rounded by the feeder)
            public readonly uint KCents;      // strike ×100
            public readonly uint STick;       // spot ticks — the feeder's round(S/0.01), received verbatim
            public readonly uint CTick;       // price ticks — the feeder's round(max(price,0.01)/0.01), verbatim

            public NodeKey(ushort tickerId, byte right, ushort tq10k, uint kCents, uint sTick, uint cTick)
            {
                TickerId = tickerId;
                Right = right;
                Tq10k = tq10k;
                KCents = kCents;
                STick = sTick;
                CTick = cTick;
            }

            public bool Equals(NodeKey other) =>
                TickerId == other.TickerId && Right == other.Right && Tq10k == other.Tq10k &&
                KCents == other.KCents && STick == other.STick && CTick == other.CTick;

            public override bool Equals(object obj) => obj is NodeKey other && Equals(other);

            public override int GetHashCode() =>
                HashCode.Combine(TickerId, Right, Tq10k, KCents, STick, CTick);
        }

        // ── Ticker pool — keys carry ids, not strings ──

        private ushort GetOrCreateTickerId(string ticker)
        {
            if (_tickerIds.TryGetValue(ticker, out var id))
            {
                return id;
            }
            if (_tickerNames.Count >= ushort.MaxValue)
            {
                throw new InvalidOperationException("PricerCacheClient: ticker id space exhausted");
            }
            id = (ushort)_tickerNames.Count;
            _tickerNames.Add(ticker);
            _tickerIds[ticker] = id;
            return id;
        }

        // ── Quantization — the conversion layer between doubles and integer keys ──

        /// <summary>strike → cents. The feeder emits K as a float; cents is exact for OSI strikes.</summary>
        private static uint KCents(double k) => (uint)Math.Round(k * 100.0);

        /// <summary>tenor → ×10000 (ushort). The feeder's tq is 3-significant-digit rounded; the
        /// same rounding in double then scaled is the feeder's exact integer. Tenors above
        /// 6.55 years cannot be represented — no such equity option exists, so such a row is
        /// skipped / such a lookup is a clean miss rather than a silent alias.</summary>
        private static bool TryTq10k(double t, out ushort tq10k)
        {
            var v = RoundSig(t, TenorDigits) * 10_000.0;
            if (!(v >= 0) || v > ushort.MaxValue)
            {
                tq10k = 0;
                return false;
            }
            tq10k = (ushort)Math.Round(v);
            return true;
        }

        /// <summary>Same quantization as TryTq10k, kept as int for the keepalive bookkeeping.</summary>
        private static int Tq10kInt(double t) => (int)Math.Round(RoundSig(t, TenorDigits) * 10_000.0);

        /// <summary>spot → ticks. Julia: round(Int, S / 0.01) — ties-to-even, Math.Round default.</summary>
        private static uint STick(double s) => (uint)Math.Round(s / Tick);

        /// <summary>price → ticks with the floor at one tick. Julia: round(Int, max(price, 0.01) / 0.01).</summary>
        private static uint CTick(double price) => (uint)Math.Round(Math.Max(price, Tick) / Tick);

        /// <summary>NodeKey for a request, from doubles, through the exact integer layer.</summary>
        private static bool TryNodeKeyOf(ushort tickerId, bool isCall, double k, double t,
            double s, double price, out NodeKey key)
        {
            key = default;
            if (!TryTq10k(t, out var tq))
            {
                return false;
            }
            key = new NodeKey(tickerId, (byte)(isCall ? 1 : 0), tq,
                KCents(k), STick(s), CTick(price));
            return true;
        }

        /// <summary>NodeKey from a feeder delta row [ticker, right, K, tq, s_tick, c_tick].
        /// Returns false for a row the packed key cannot represent or no lookup can address —
        /// the row is skipped, never aliased. IMPORTANT: the feeder's price ticks CAN be
        /// NEGATIVE (a deep-OTM lattice centres on one tick and its corridor extends below it:
        /// C0 + dj goes negative). No client can address those cells (CTick floors at one
        /// tick), so they are simply skipped — and every value is validated in DOUBLE space
        /// before any integer cast, because a single negative cast to uint used to throw
        /// "Value was either too large or too small for a UInt32" and abort the whole message.</summary>
        private bool TryNodeKeyOf(JArray a, out NodeKey key)
        {
            key = default;
            var right = (double)a[1];
            var k = (double)a[2];
            var tq = (double)a[3] * 10_000.0;     // wire tq is the fraction (0.0575) — scale to ×10k
            var sTick = (double)a[4];
            var cTick = (double)a[5];
            if ((right != 0 && right != 1) ||
                !(k > 0) || k > uint.MaxValue / 100.0 ||
                !(tq >= 0) || tq > ushort.MaxValue ||
                !(sTick >= 1) || sTick > uint.MaxValue ||
                !(cTick >= 1) || cTick > uint.MaxValue)
            {
                return false;
            }
            key = new NodeKey(
                GetOrCreateTickerId((string)a[0]),
                (byte)right,
                (ushort)Math.Round(tq),
                (uint)Math.Round(k * 100.0),
                (uint)Math.Round(sTick),
                (uint)Math.Round(cTick));
            return true;
        }
        /// <summary>
        /// The served greek for (ticker, right, K, T, S, price) from the local book, or null on
        /// any miss — exact node, freshness and the sigma-match gate applied,
        /// everything else falls back to ZMQ.
        ///
        /// Sigma gate: the feeder solves greeks AT the node's cached IV (sigma-based solvers), so
        /// a cached greek is only valid when the caller's requested sigma equals the node's IV.
        /// </summary>
        public double? TryLookupGreek(GreekKind kind, string ticker, bool isCall, double k, double t,
            double s, double price, double sigma, long calcTimeUtc = 0)
        {
            Interlocked.Increment(ref _lookups);
            var metric = Counters(kind);
            // Sigma is a GREEK input (the feeder solves greeks at the node's IV) — required only
            // for greek kinds. The IV itself needs no sigma: the node's stored Iv IS the answer,
            // and TryLookupIV passes NaN here deliberately.
            if (price <= 0 || s <= 0 || t <= 0 || k <= 0 ||
                (kind != GreekKind.Iv && !double.IsFinite(sigma)))
            {
                Interlocked.Increment(ref metric.Miss);
                return null;
            }

            lock (_bookLock)
            {
                if (_staleUntilResync || _books.Count == 0)
                {
                    Interlocked.Increment(ref metric.Miss);
                    return null;
                }

                if (!_tickerIds.TryGetValue(ticker, out var tickerId) ||
                    !TryNodeKeyOf(tickerId, isCall, k, t, s, price, out var nodeKey))
                {
                    // Unknown ticker (nothing ever received for it) or an unrepresentable
                    // tenor — either way this node cannot be in the book.
                    Interlocked.Increment(ref metric.Miss);
                    return null;
                }

                // v1 active-env heuristic: most recently updated book; freshness gate.
                EnvBook active = null;
                foreach (var book in _books.Values)
                {
                    if (active == null || book.LastDeltaUtc > active.LastDeltaUtc)
                    {
                        active = book;
                    }
                }
                if (active == null || DateTime.UtcNow - active.LastDeltaUtc > StaleAfter)
                {
                    Interlocked.Increment(ref metric.Miss);
                    return null;
                }

                if (active.Nodes.TryGetValue(nodeKey, out var row))
                {
                    var value = kind switch
                    {
                        GreekKind.Iv => (double)row.Iv,
                        GreekKind.Delta => (double)row.Delta,
                        GreekKind.Theta => (double)row.Theta,
                        GreekKind.Vega => (double)row.Vega,
                        GreekKind.Volga => (double)row.Volga,
                        _ => double.NaN,
                    };
                    if (double.IsFinite(value))
                    {
                        // Greeks are valid only at the sigma the feeder solved them at — the
                        // node's IV. Compare the CALLER's requested sigma against row.Iv.
                        if (kind == GreekKind.Iv || SigmaMatches(row.Iv, sigma))
                        {
                            Interlocked.Increment(ref metric.Exact);
                            // Hit ≠ silence: the feeder keeps its lattice centered on the latest
                            // request's spot (corridor priority keys off ContractState.S), so sampled
                            // hits keep the book tracking a moving underlying. Misses always publish.
                            SamplePublishOnHit(ticker, isCall, k, t, s, price, calcTimeUtc);
                            return value;
                        }
                        // Node exists but the caller's sigma differs from the node's IV: the cached
                        // greek answers a different question — count a miss so the caller falls back
                        // to the ZMQ pricer (which serves at the requested sigma) and the request
                        // warms the feeder.
                        Interlocked.Increment(ref metric.SigmaMismatch);
                        return null;
                    }
                    // Known node, unknown greek (NaN) — a miss, not a sigma mismatch.
                    Interlocked.Increment(ref metric.Miss);
                    return null;
                }

                // No interpolation, by design: the feeder's rounding + corridor construction
                // guarantee the request's exact (s_tick, c_tick) is solved. An unsolved tick means
                // the data is not there yet — miss and publish; the next cycle solves it.
                Interlocked.Increment(ref metric.Miss);
                return null;
            }
        }

        /// <summary>
        /// On a cache HIT, still publish — but sampled: one request per (contract, kind) per
        /// <see cref="HitKeepalive"/> window. Rationale: the feeder's corridor priority centers
        /// each contract's lattice on the latest request's spot; if hits never reach it, the book
        /// goes stale as the underlying moves and the next moves push every lookup out-of-band.
        /// A steady trickle keeps ContractState.S current at a cost of a few messages per
        /// contract per second — bounded by the channel and harmless to the feeder.
        /// Misses always publish (warm the node); hits publish only on this keepalive.
        /// </summary>
        private void SamplePublishOnHit(string ticker, bool isCall, double k, double t,
            double s, double price, long calcTimeUtc)
        {
            var key = (ticker, isCall ? 1 : 0, (int)KCents(k), Tq10kInt(t));
            var now = Environment.TickCount64;
            if (_lastHitPublish.TryGetValue(key, out var last) && now - last < HitKeepaliveMs)
            {
                return;
            }
            _lastHitPublish[key] = now;
            PublishRequest(ticker, isCall, k, t, s, price, calcTimeUtc);
        }

        /// <summary>Per-(contract, kind) hit keepalive window. One request per 2 s keeps the
        /// feeder's spot view current without measurable load (≈ a handful of msgs/contract).</summary>
        private const int HitKeepaliveMs = 2_000;

        /// <summary>Last hit-publish time per (ticker, right, K-cents, tq-10k); entries are tiny,
        /// and contract churn is bounded by the option chain.</summary>
        private readonly Dictionary<(string, int, int, int), long> _lastHitPublish = new();

        /// <summary>
        /// A miss publishes the exact requested cell so the feeder's demand tier can solve it. The
        /// same cell is asked again within a bar by several consumers and for several greeks, and
        /// re-publishing it changes nothing — the feeder already queued or solved it. Measured on
        /// 2026-10-21: ~440 requests/s of mostly repeats, and parsing them was the feeder's whole
        /// cycle time. A 5 s window collapses that without delaying a genuinely new cell.
        /// </summary>
        private const int PublishDedupeMs = 5_000;
        private readonly Dictionary<NodeKey, long> _lastPublishTicks = new();
        private long _publishDeduped;

        /// <summary>
        /// The served IV for (ticker, right, K, T, S, price) from the local book, or null on any
        /// miss — exact node, freshness, everything else falls back to ZMQ.
        /// </summary>
        public double? TryLookupIV(string ticker, bool isCall, double k, double t, double s, double price)
            => TryLookupGreek(GreekKind.Iv, ticker, isCall, k, t, s, price, double.NaN);

        /// <summary>
        /// Cached greeks are solved at the node's IV; a requested sigma matches when it IS that
        /// node IV (same 3-sig-tenor/tick key family) — relative tolerance, float32-solved values.
        /// </summary>
        private static bool SigmaMatches(double nodeIv, double requestedSigma)
            => Math.Abs(nodeIv - requestedSigma) <= 1e-4 * Math.Max(1e-6, Math.Abs(requestedSigma));

        // ── Publish ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Publish one request so the feeder warms the node. Non-blocking: a TryWrite into a
        /// bounded channel; the background loop owns the socket. Call on every cache miss.
        /// </summary>
        public void PublishRequest(string ticker, bool isCall, double k, double t, double s, double price, long calcTimeUtc)
        {
            if (!_brokerUp)
            {
                return;   // ZMQ-fallback-only mode; the publish loop drains on recovery
            }

            // Dedupe on the same packed key the lookup uses: a cell already asked for within the
            // window is queued (or solved) on the feeder, so sending it again is pure parse load.
            if (_tickerIds.TryGetValue(ticker, out var tickerId) &&
                TryNodeKeyOf(tickerId, isCall, k, t, s, price, out var nodeKey))
            {
                var nowTicks = Environment.TickCount64;
                if (_lastPublishTicks.TryGetValue(nodeKey, out var last) && nowTicks - last < PublishDedupeMs)
                {
                    Interlocked.Increment(ref _publishDeduped);
                    return;
                }
                _lastPublishTicks[nodeKey] = nowTicks;
                if (_lastPublishTicks.Count > 1_000_000)
                {
                    _lastPublishTicks.Clear();   // bounded; a clear only costs one re-publish per cell
                }
            }

            var body = BuildRequest(ticker, isCall, k, t, s, price, calcTimeUtc);
            if (_outbound.Writer.TryWrite(body))
            {
                Interlocked.Increment(ref _published);
            }
            else
            {
                Interlocked.Increment(ref _publishDropped);
            }
        }

        /// <summary>Request JSON — env fragment is cached per (ticker, calc date) so every
        /// message carrying the same bundle is byte-identical (the Julia side content-hashes it).</summary>
        private byte[] BuildRequest(string ticker, bool isCall, double k, double t, double s, double price, long calcTimeUtc)
        {
            var calcDate = DateTimeOffset.FromUnixTimeSeconds(calcTimeUtc).UtcDateTime.Date;
            var envJson = PricerEnvProvider.EnvFragment(ticker, calcDate);
            var tUnix = (double)Math.Max(0, (_algo.Time.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
            var sb = new StringBuilder(256);
            sb.Append("{\"t\":").Append(Format(tUnix));
            sb.Append(",\"cid\":[\"").Append(ticker).Append("\",").Append(isCall ? 1 : 0).Append(',').Append(Format(k)).Append(']');
            sb.Append(",\"K\":").Append(Format(k));
            sb.Append(",\"right\":").Append(isCall ? 1 : 0);
            sb.Append(",\"T\":").Append(Format(t));
            sb.Append(",\"S\":").Append(Format(s));
            sb.Append(",\"price\":").Append(Format(price));
            sb.Append(",\"side\":\"mid\",\"metric\":\"supply_volatility\",\"env\":");
            sb.Append(Encoding.ASCII.GetString(envJson));
            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private async Task PublishLoopAsync()
        {
            var ct = _cts.Token;
            while (!ct.IsCancellationRequested)
            {
                if (!_brokerUp)
                {
                    await Task.Delay(Backoff, ct).ContinueWith(_ => { }, ct);
                    continue;
                }
                try
                {
                    var body = await _outbound.Reader.ReadAsync(ct);
                    await _publishChannel.BasicPublishAsync(
                        exchange: RequestExchange,
                        routingKey: RequestRoutingKey,
                        mandatory: false,
                        basicProperties: new BasicProperties { ContentType = "application/json", DeliveryMode = DeliveryModes.Persistent },
                        body: body);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    MarkBrokerDown($"publish: {e.Message}");
                }
            }
        }

        // ── Consume ───────────────────────────────────────────────────────────────

        private Task OnDeltaReceivedAsync(object sender, BasicDeliverEventArgs ea)
        {
            try
            {
                ApplyDelta(ea.Body.ToArray());
            }
            catch (Exception e)
            {
                // A malformed delta is loss-tolerable (periodic resync heals); never kill the consumer.
                _algo.Log($"PricerCacheClient: delta apply failed: {e.Message}");
            }
            return Task.CompletedTask;
        }

        private void ApplyDelta(byte[] body)
        {
            var root = JObject.Parse(Encoding.UTF8.GetString(body));
            var seq = root.Value<long>("seq");
            var cycle = root.Value<long?>("cycle") ?? -1;
            var resync = root.Value<bool?>("resync") ?? false;
            var adds = (JObject)root["adds"];
            var removes = (JObject)root["removes"];
            var greekSet = (JArray)root["greek_set"];

            lock (_bookLock)
            {
                PrunePastDayBooks(_algo.Time.Date);

                if (seq < _lastSeq)
                {
                    // Feeder restarted (its seq restarts at 0): the old book belongs to a dead
                    // run — drop it, forget the burst id, and serve nothing until its first
                    // resync completes.
                    _books.Clear();
                    _lastResyncCycle = -1;
                    _staleUntilResync = true;
                }
                else if (_lastSeq >= 0 && seq > _lastSeq + 1 && !resync)
                {
                    // Gap → assume loss → serve nothing until the feeder's next full resync.
                    _staleUntilResync = true;
                    _algo.Log($"PricerCacheClient: seq gap {_lastSeq} -> {seq} — serving suspended until resync.");
                }
                _lastSeq = seq;

                if (resync && cycle != _lastResyncCycle)
                {
                    // A resync BURST is chunked into many messages; the feeder flags only the
                    // first. The cycle guard makes a feeder that flags every chunk harmless —
                    // clearing per message was capping the book at one batch_max chunk.
                    _books.Clear();
                    _lastResyncCycle = cycle;
                }
                if (resync)
                {
                    _staleUntilResync = false;
                }
                if (_staleUntilResync)
                {
                    return;   // still waiting for the healing resync
                }

                var now = DateTime.UtcNow;

                if (adds != null)
                {
                    foreach (var (hex, group) in adds)
                    {
                        var book = GetOrCreateBook(hex, now);
                        var keys = (JArray)group["keys"];
                        // `iv` is its own array next to the greek_set columns — it is NOT a
                        // member of greek_set. Reading it only through greek_set made every IV
                        // NaN and every IV lookup a guaranteed miss.
                        var ivCol = (JArray)group["iv"];
                        var columns = new Dictionary<string, JArray>(StringComparer.Ordinal);
                        if (greekSet != null)
                        {
                            foreach (var g in greekSet)
                            {
                                var name = (string)g;
                                columns[name] = (JArray)group[name];
                            }
                        }
                        for (var i = 0; i < keys.Count; i++)
                        {
                            if (!(keys[i] is JArray row) || !TryNodeKeyOf(row, out var nodeKey))
                            {
                                // Unreachable/unrepresentable row (negative price tick, tenor
                                // > 6.55y, bad right) — skip THIS row, never the message.
                                Interlocked.Increment(ref _rowsSkipped);
                                continue;
                            }
                            book.Nodes[nodeKey] = new GreekRow
                            {
                                Iv = Col(ivCol, i),
                                Delta = Col(columns, "delta", i),
                                Theta = Col(columns, "theta", i),
                                Vega = Col(columns, "vega", i),
                                Volga = Col(columns, "volga", i),
                            };
                        }
                    }
                }

                if (removes != null)
                {
                    foreach (var (hex, arr) in removes)
                    {
                        if (!_books.TryGetValue(hex, out var book))
                        {
                            continue;
                        }
                        foreach (var keyToken in (JArray)arr)
                        {
                            if (keyToken is JArray row && TryNodeKeyOf(row, out var nodeKey))
                            {
                                book.Nodes.Remove(nodeKey);
                            }
                        }
                    }
                }

                _deltasApplied++;
            }
        }

        /// <summary>One column value as float; missing/NaN → NaN ("known node, unknown greek").</summary>
        private static float Col(JArray col, int i) =>
            col != null && i < col.Count && col[i].Type != JTokenType.Null ? (float)(double)col[i] : float.NaN;

        private static float Col(Dictionary<string, JArray> columns, string name, int i) =>
            columns.TryGetValue(name, out var col) ? Col(col, i) : float.NaN;

        /// <summary>Drop every env book when the algorithm's sim date advances. A book's env
        /// hash encodes the calculation date (rates/div times are date-relative), so yesterday's
        /// books can never serve today's lookups — keeping them only costs memory (warmup dates
        /// would otherwise stay resident through the whole trading day). The new day's books
        /// rebuild from the feeder's deltas; until then lookups are honest misses.</summary>
        private void PrunePastDayBooks(DateTime simDate)
        {
            if (_bookSimDate == default)
            {
                _bookSimDate = simDate;
                return;
            }
            if (simDate <= _bookSimDate)
            {
                return;
            }

            var dropped = _books.Count;
            _bookSimDate = simDate;
            _books.Clear();
            _lastHitPublish.Clear();
            _lastPublishTicks.Clear();
            _lastResyncCycle = -1;
            _staleUntilResync = false;   // fresh day, fresh book — no carry-over suspension
            if (dropped > 0)
            {
                _algo.Log($"PricerCacheClient: sim date -> {simDate:yyyy-MM-dd}: dropped {dropped} past-day env book(s).");
            }
        }

        private EnvBook GetOrCreateBook(string hex, DateTime now)
        {
            if (!_books.TryGetValue(hex, out var book))
            {
                book = new EnvBook { Hex = hex };
                _books[hex] = book;
            }
            book.LastDeltaUtc = now;
            return book;
        }

        // ── Health / diagnostics ──────────────────────────────────────────────────

        private void MarkBrokerDown(string reason)
        {
            if (_brokerUp)
            {
                _brokerUp = false;
                _algo.Log($"PricerCacheClient: broker down ({reason}) — ZMQ-fallback-only; retrying every {Backoff.TotalSeconds:0}s.");
            }
        }

        private void LogStats()
        {
            lock (_bookLock)
            {
                PrunePastDayBooks(_algo.Time.Date);   // catch a rollover even on a quiet delta path
                var nodes = 0;
                foreach (var book in _books.Values)
                {
                    nodes += book.Nodes.Count;
                }
                var perKind = string.Join("; ", _byKind.OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}:#{kvp.Value.Exact + kvp.Value.SigmaMismatch + kvp.Value.Miss},hit={kvp.Value.Exact} (exact={kvp.Value.Exact}),sigmamiss={kvp.Value.SigmaMismatch},miss={kvp.Value.Miss}"));
                // ≈53 B/node: 20 B packed key + 28 B dictionary entry overhead + 20 B float row
                // (bucket array amortised). Read as an order-of-magnitude, not an allocation count.
                var estMb = nodes * 53L / 1_048_576.0;
                _algo.Log($"PricerCacheClient stats: simDate={_bookSimDate:yyyy-MM-dd} books={_books.Count} nodes={nodes} (~{estMb:0.0} MB est, {_tickerNames.Count} tickers) " +
                          $"lookups={_lookups} published={_published} deduped={_publishDeduped} dropped={_publishDropped} skipped={_rowsSkipped} " +
                          $"deltas={_deltasApplied} brokerUp={_brokerUp} stale={_staleUntilResync}" +
                          (perKind.Length > 0 ? $"\n{_algo.Time} PricerCacheClient by kind: {perKind}" : ""));
            }
        }

        /// <summary>
        /// One-line, per-greek-kind hit/miss breakdown for embedding in an existing stats log
        /// (e.g. next to JuliaPricingClient.StatsSnapshot() in GetDesiredOrders) — same format,
        /// so the two read as one pair: feeder-side round trips vs cache-plane outcomes.
        /// </summary>
        public string StatsByKind()
        {
            lock (_bookLock)
            {
                var parts = new List<string>();
                foreach (var kvp in _byKind.OrderBy(k => k.Key))
                {
                    var hit = kvp.Value.Exact;
                    parts.Add($"{kvp.Key}:#{hit + kvp.Value.Miss},hit={hit} (exact={kvp.Value.Exact})," +
                              $"sigmamiss={kvp.Value.SigmaMismatch},miss={kvp.Value.Miss}");
                }
                return parts.Count > 0 ? string.Join("; ", parts) : "no cache lookups yet";
            }
        }

        /// <summary>
        /// Per-kind hit/miss for the interval since the last call, run totals in brackets:
        /// <c>Iv:#123(8216) hit=45(0) sig=0(0) miss=78(8216)</c>. Same reason as
        /// <see cref="JuliaPricingClient.StatsSinceLast"/>: the cumulative line repeated verbatim
        /// across reports and could not tell an idle interval from a cache-served one.
        /// </summary>
        public string StatsByKindSinceLast()
        {
            lock (_bookLock)
            {
                var parts = new List<string>();
                foreach (var kvp in _byKind.OrderBy(k => k.Key))
                {
                    // First report for a kind: the previous snapshot does not exist yet, so the
                    // interval delta is the run total (KindCounters is a class — a missing entry is
                    // null, not a zeroed struct).
                    if (!_lastByKind.TryGetValue(kvp.Key, out var last))
                    {
                        last = new KindCounters();
                        _lastByKind[kvp.Key] = last;
                    }
                    var lookups = kvp.Value.Exact + kvp.Value.SigmaMismatch + kvp.Value.Miss;
                    var dLookups = lookups - last.Lookups;
                    var dHit = kvp.Value.Exact - last.Exact;
                    var dSigma = kvp.Value.SigmaMismatch - last.SigmaMismatch;
                    var dMiss = kvp.Value.Miss - last.Miss;
                    _lastByKind[kvp.Key] = new KindCounters
                    {
                        Exact = kvp.Value.Exact,
                        SigmaMismatch = kvp.Value.SigmaMismatch,
                        Miss = kvp.Value.Miss,
                        Lookups = lookups
                    };
                    if (dLookups == 0 && lookups == 0) continue;
                    parts.Add($"{kvp.Key}:#{dLookups}({lookups}) hit={dHit}({kvp.Value.Exact}) " +
                              $"sig={dSigma}({kvp.Value.SigmaMismatch}) miss={dMiss}({kvp.Value.Miss})");
                }
                return parts.Count > 0 ? string.Join("; ", parts) : "no cache lookups in this interval";
            }
        }

        // ── Plumbing ──────────────────────────────────────────────────────────────

        private sealed class EnvBook
        {
            public string Hex;
            public DateTime LastDeltaUtc;
            public readonly Dictionary<NodeKey, GreekRow> Nodes = new();
        }

        /// <summary>Per-node greek values: 5 × float (20 B), stored INLINE in the dictionary
        /// entry — no per-node object header, no boxing, no reference. The feeder solves in
        /// Float32, so double buys no accuracy here, only bytes (≈0.5 GB vs ≈1.7 GB at 10M
        /// nodes).</summary>
        private struct GreekRow
        {
            public float Iv;
            public float Delta;
            public float Theta;
            public float Vega;
            public float Volga;
        }

        /// <summary>Julia _round_sig (PricerCache.jl:235): ties-to-even, exactly Math.Round's default.</summary>
        private static double RoundSig(double x, int sig)
        {
            if (x == 0) return 0;
            var f = Math.Pow(10.0, sig - Math.Ceiling(Math.Log10(Math.Abs(x))));
            return Math.Round(x * f) / f;
        }

        /// <summary>Shortest round-trip invariant format — deterministic bytes for the env hash.</summary>
        private static string Format(double v) => v.ToString("R", CultureInfo.InvariantCulture);

        public void Dispose()
        {
            _cts.Cancel();
            _statsTimer?.Dispose();
            try { _publishChannel?.CloseAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _consumeChannel?.CloseAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _connection?.CloseAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        }
    }
}
