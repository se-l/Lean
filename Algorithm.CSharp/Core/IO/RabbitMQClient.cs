using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using QuantConnect.Configuration;
using QuantConnect.Scheduling;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    public class RabbitMQClient : IDisposable
    {
        public event EventHandler<object> EventHandlerConnected;

        // ── Fields ────────────────────────────────────────────────────
        private readonly Foundations _algo;
        private readonly Dictionary<ChannelPb, EventHandler<EventArgs>> _eventHandlers;
        // Unanswered requests: id -> (channel, algo clock at send, wall clock at send). Every
        // submission is tracked until its response arrives, so any response — including the request
        // the recipient is still processing — can compute its scheduled callback time as
        // tsSent + elapsedMs (DispatchResponse). The wall clock is what ExpireUnansweredRequests
        // measures: the recipient conflates channels (MQMsgBroker.jl "latest-wins"), so an
        // overwritten request is never answered and without a deadline it would gate pacing
        // forever.
        private readonly ConcurrentDictionary<string, (ChannelPb Channel, DateTime Sent, DateTime SentUtc)> _activeRequests = new();

        // Per channel, the id of the single in-flight submission. One at a time is what makes every
        // sent request answerable: the recipient drops overwritten messages, so a second send while
        // one is computing buys a response that can never arrive. The newest payload of a
        // coalesced send waits here and is published when the outstanding response lands.
        private readonly ConcurrentDictionary<ChannelPb, string> _lastSentIds = new();
        private readonly ConcurrentDictionary<ChannelPb, PendingRequest> _pending = new();
        private readonly object _sendGate = new();

        /// <summary>
        /// Deadline for one request (<c>mq-response-timeout-s</c>, default 300 s). A conflated or
        /// lost request is never answered; past this the wait is dropped and pacing continues.
        /// </summary>
        private readonly int _responseTimeoutSeconds;

        /// <summary>A coalesced request body waiting for the channel's in-flight response.</summary>
        private sealed class PendingRequest
        {
            public PendingRequest(string id, byte[] body)
            {
                Id = id;
                Body = body;
            }

            public string Id { get; }
            public byte[] Body { get; }
        }
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private IConnection _connection;
        private IChannel _channel;
        private string _replyQueueName;
        private bool _disposed;

        // ── Exchange / queue names ────────────────────────────────────
        private const string ExchangeIn  = "calc_engine";           // publish requests here
        private const string ExchangeOut = "calc_engine.responses"; // consume responses from here
        private const string RoutingKeyPrefix = "calc.";            // matches calc.# on Julia side

        public RabbitMQClient(Foundations algo)
        {
            _algo = algo;
            _eventHandlers = new Dictionary<ChannelPb, EventHandler<EventArgs>>();
            _responseTimeoutSeconds = Math.Max(1, int.Parse(Config.Get("mq-response-timeout-s", "400"),
                System.Globalization.CultureInfo.InvariantCulture));
        }

        // ── Register event handlers ───────────────────────────────────
        public void RegisterEventHandler<T>(ChannelPb channel, Action<object, T> handler)
            where T : EventArgs
        {
            if (!_eventHandlers.ContainsKey(channel))
                _eventHandlers[channel] = null;

            _eventHandlers[channel] += (sender, e) =>
            {
                try
                {
                    handler(sender, (T)e);
                }
                catch (Exception ex)
                {
                    _algo.Error($"RabbitMqClient.{handler.Method.Name}(): Exception: {ex}");
                }
            };
        }

        /// <summary>
        /// True while a channel's in-flight submission still awaits a response. With one request
        /// in flight per channel (see <see cref="SendMessageAsync{T}"/>) this is at most one per
        /// channel, and every id it can name is one the recipient was actually given a chance to
        /// answer. Requests older than <c>mq-response-timeout-s</c> are expired first, so a
        /// conflated or lost response can never gate pacing indefinitely.
        /// </summary>
        public bool HasActiveRequests()
        {
            ExpireUnansweredRequests();
            foreach (var (_, id) in _lastSentIds)
            {
                if (_activeRequests.ContainsKey(id))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Drops submissions older than <see cref="_responseTimeoutSeconds"/>.
        ///
        /// The recipient conflates: a newer message on a conflating channel overwrites the pending
        /// one and the overwritten request is never answered (MQMsgBroker.jl acks conflating
        /// channels on receipt precisely because they may be dropped). Without this bound one lost
        /// response stalls <c>PaceBacktestNearRealtime</c> forever — observed 2026-10-22: a 1,197 s
        /// wait on a single timestep, killed by Lean's 20-minute Isolator. Dropping the wait does
        /// not fabricate a response: the handler simply never fires for that request, pacing moves
        /// on, and the newest coalesced payload for the channel is sent instead.
        /// </summary>
        private void ExpireUnansweredRequests()
        {
            var now = DateTime.UtcNow;
            foreach (var (id, entry) in _activeRequests)
            {
                var age = (now - entry.SentUtc).TotalSeconds;
                if (age < _responseTimeoutSeconds)
                {
                    continue;
                }
                if (!_activeRequests.TryRemove(id, out var expired))
                {
                    continue;
                }
                _algo.Error($"{_algo.Time} RabbitMqClient: no response for {expired.Channel} id={id} after " +
                            $"{age:F0}s (mq-response-timeout-s={_responseTimeoutSeconds}) — dropping the wait; " +
                            "pacing continues.");
                if (_lastSentIds.TryGetValue(expired.Channel, out var last) && last == id)
                {
                    _lastSentIds.TryRemove(new KeyValuePair<ChannelPb, string>(expired.Channel, id));
                    TryPublishPending(expired.Channel);
                }
            }
        }

        // ── Connect ───────────────────────────────────────────────────
        public async Task ConnectAsync(
            string host,
            int port,
            string user,
            string pass,
            string virtualHost = "/"
            )
        {
            var factory = new ConnectionFactory
            {
                HostName = host,
                Port = port,
                UserName = user,
                Password = pass,
                VirtualHost = virtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                RequestedHeartbeat = TimeSpan.FromSeconds(60),
            };

            await _connectLock.WaitAsync();
            try
            {
                _connection = await factory.CreateConnectionAsync("quantconnect-algo");
                _channel = await _connection.CreateChannelAsync();

                // Declare BOTH exchanges
                await _channel.ExchangeDeclareAsync(
                    exchange: ExchangeIn,
                    type: ExchangeType.Topic,
                    durable: true);

                await _channel.ExchangeDeclareAsync(
                    exchange: ExchangeOut,
                    type: ExchangeType.Topic,
                    durable: true);

                // Exclusive reply queue — bind to the OUTBOUND exchange
                var queueResult = await _channel.QueueDeclareAsync(
                    queue: "",
                    durable: false,
                    exclusive: true,
                    autoDelete: true);
                _replyQueueName = queueResult.QueueName;

                await _channel.QueueBindAsync(
                    queue: _replyQueueName,
                    exchange: ExchangeOut,
                    routingKey: "#");          // catch all responses; narrow if needed

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += OnMessageReceivedAsync;
                
                await _channel.BasicConsumeAsync(
                    queue: _replyQueueName,
                    autoAck: true,
                    consumer: consumer);

                _algo.Log("RabbitMqClient: Connected.");
                EventHandlerConnected?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // ── Send ──────────────────────────────────────────────────────
        public async Task SendMessageAsync<T>(T request)
            where T : IMessage<T>
        {
            var rabbitChannel = GetRequestTypeChannel<T>();
            var msg = new MessagePb
            {
                Channel = rabbitChannel,
                Id = Guid.NewGuid().ToString(),
                Action = ActionPb.Subscribe,
                Payload = request.ToByteString()
            };

            var body = msg.ToByteArray();

            // Expire stale waits BEFORE the in-flight check, in every mode. Nothing else calls
            // HasActiveRequests() in live (Foundations only consults it when
            // BacktestNearRealtimePacingEnabled), so without this an unanswered request — a
            // conflated payload, a lost response, or a hung handler — wedges its channel for the
            // rest of the session and every later fetch is dropped (observed 2026-10-28: live
            // TargetPortfolio stuck for 26+ min on one in-flight id).
            ExpireUnansweredRequests();

            // One request in flight per channel. A request that arrives while one is computing is
            // DROPPED, not queued: the recipient conflates (latest-wins) and only ever answers the
            // message it is working on, so a queued replay would be answered a full compute later —
            // which in a backtest stretches the pacing wait to minutes. Dropping keeps the loop
            // moving; the caller's next tick re-derives whatever is current then.
            lock (_sendGate)
            {
                if (HasOutstandingRequest(rabbitChannel))
                {
                    _algo.Log($"{_algo.Time} RabbitMqClient: {rabbitChannel} request dropped " +
                              "(one already in flight; not queued).");
                    return;
                }
                _activeRequests[msg.Id] = (rabbitChannel, _algo.Time, DateTime.UtcNow);
                _lastSentIds[rabbitChannel] = msg.Id;
            }

            await PublishAsync(rabbitChannel, msg.Id, body);
        }

        /// <summary>Publishes a request already registered as in flight; forgets it if the send fails.</summary>
        private async Task PublishAsync(ChannelPb rabbitChannel, string id, byte[] body)
        {
            var props = new BasicProperties
            {
                ReplyTo = _replyQueueName,
                CorrelationId = id
            };

            try
            {
                await _channel.BasicPublishAsync(
                    exchange: ExchangeIn,
                    routingKey: RoutingKeyPrefix + rabbitChannel.ToString().ToLowerInvariant(), // e.g. "calc.targetportfolio"
                    mandatory: false,
                    basicProperties: props,
                    body: body);
            }
            catch
            {
                // A send that never reached the broker can never be answered; forgetting it keeps
                // pacing from waiting on a response that will not come.
                _activeRequests.TryRemove(id, out _);
                _lastSentIds.TryRemove(new KeyValuePair<ChannelPb, string>(rabbitChannel, id));
                throw;
            }
        }

        /// <summary>True while the channel's in-flight submission still awaits its response.</summary>
        private bool HasOutstandingRequest(ChannelPb channel) =>
            _lastSentIds.TryGetValue(channel, out var id) && _activeRequests.ContainsKey(id);

        /// <summary>
        /// Publishes the newest coalesced payload for a channel, if one is waiting. Called when a
        /// response retires the in-flight request (OnMessageReceived) or when that request expires.
        /// </summary>
        private void TryPublishPending(ChannelPb channel)
        {
            // Publishing no longer queues a replacement (see the publish path): anything still here
            // is a leftover from an older code path and is dropped, loudly, rather than replayed a
            // compute later.
            if (_pending.TryRemove(channel, out _))
            {
                _algo.Log($"{_algo.Time} RabbitMqClient: dropped a queued {channel} payload " +
                          "(publishing does not queue a second request).");
            }
        }

        // ── Receive ───────────────────────────────────────────────────
        private async Task OnMessageReceivedAsync(object sender, BasicDeliverEventArgs ea)
        {
            try
            {
                using var ms = new MemoryStream(ea.Body.ToArray());
                MessagePb msg = MessagePb.Parser.ParseFrom(ms);

                _algo.Log($"RabbitMqClient: Received response: {msg.Channel}.");

                var requestSentTime = RemoveActiveRequest(msg.Id);
                // The recipient works channel by channel: a response for this channel retires
                // every earlier unanswered submission on it (overwritten while conflating, or its
                // result dropped when superseded). They will never send a response of their own.
                PruneSuperseded(msg.Channel);
                // The channel is free again: send the newest payload that was coalesced while this
                // request was in flight.
                TryPublishPending(msg.Channel);

                switch (msg.Channel)
                {
                    case ChannelPb.Hb:
                        _algo.Log("RabbitMqClient: Heartbeat received.");
                        break;
                    case ChannelPb.TargetPortfolio:
                    {
                        var response = ResponseTargetPortfoliosPb.Parser.ParseFrom(msg.Payload);
                        DispatchResponse(msg.Channel, requestSentTime, response.ElapsedMs,
                            new TargetPortfoliosEventArgs(response));
                        break;
                    }
                    case ChannelPb.StressTestDs:
                    {
                        var response = ResultStressTestDsPb.Parser.ParseFrom(msg.Payload);
                        DispatchResponse(msg.Channel, requestSentTime, response.ElapsedMs,
                            new ResultStressTestDsEventArgs(response));
                        break;
                    }
                    case ChannelPb.CmdFetchTargetPortfolio:
                        DispatchResponse(msg.Channel, requestSentTime, 0,
                            new CmdFetchTargetPortfolioEventArgs(CmdFetchTargetPortfolio.Parser.ParseFrom(msg.Payload)));
                        break;
                    case ChannelPb.CmdCancelOid:
                        DispatchResponse(msg.Channel, requestSentTime, 0,
                            new CmdCancelOIDEventArgs(CmdCancelOID.Parser.ParseFrom(msg.Payload)));
                        break;
                    case ChannelPb.CmdCfgOverride:
                        DispatchResponse(msg.Channel, requestSentTime, 0,
                            new CmdCfgOverrideEventArgs(CmdCfgOverride.Parser.ParseFrom(msg.Payload)));
                        break;
                    case ChannelPb.KalmanInit:
                    {
                        var response = ResponseKalmanInitPb.Parser.ParseFrom(msg.Payload);
                        DispatchResponse(msg.Channel, requestSentTime, response.ElapsedMs,
                            new ResponseKalmanInitEventArgs(response));
                        break;
                    }
                    case ChannelPb.RequestSsviCalibration:
                    {
                        var response = ResponseSSVICalibrationPb.Parser.ParseFrom(msg.Payload);
                        DispatchResponse(msg.Channel, requestSentTime, response.ElapsedMs,
                            new ResponseSSVICalibrationEventArgs(response));
                        break;
                    }
                    case ChannelPb.RequestPfRiskScenarios:
                    {
                        var response = ResponsePfRiskScenariosPb.Parser.ParseFrom(msg.Payload);
                        DispatchResponse(msg.Channel, requestSentTime, response.ElapsedMs,
                            new ResponsePfRiskScenariosEventArgs(response));
                        break;
                    }
                    default:
                        _algo.Error($"RabbitMqClient: Unknown msg channel: {msg.Channel}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _algo.Error($"RabbitMqClient.OnMessage: {ex}");
            }
        }

        private DateTime? RemoveActiveRequest(string id)
        {
            if (_activeRequests.TryRemove(id, out var entry))
            {
                return entry.Sent;
            }
            return null;
        }

        /// <summary>
        /// Drops every unanswered submission on a channel except the newest one sent
        /// (<see cref="_lastSentIds"/>). Called on receive: a response proves the recipient moved
        /// past this channel's older messages, so whatever was buried in the LIFO queue will never
        /// be answered. The newest submission is preserved regardless — it is the next one the
        /// recipient will process, and its tsSent must stay available for tsSent + elapsedMs
        /// scheduling.
        /// </summary>
        private void PruneSuperseded(ChannelPb channel)
        {
            _lastSentIds.TryGetValue(channel, out var keepId);
            foreach (var (id, entry) in _activeRequests)
            {
                if (entry.Channel == channel && id != keepId)
                {
                    _activeRequests.TryRemove(id, out _);
                }
            }
        }

        // Dispatches a parsed response to its registered handler. In backtests, when the calc engine
        // computed the request faster than the backtest's algo clock advanced since send, the handler
        // is deferred to utcSendTime + elapsedMs via a scheduled event instead of firing immediately.
        private void DispatchResponse(
            ChannelPb channel,
            DateTime? requestSentTime,
            uint elapsedMs,
            EventArgs args
            )
        {
            if (!_eventHandlers.TryGetValue(channel, out var handler) || handler == null)
            {
                return;
            }

            if (!_algo.LiveMode && requestSentTime.HasValue && elapsedMs > 0)
            {
                var targetTime = requestSentTime.Value + TimeSpan.FromMilliseconds(elapsedMs);
                if (targetTime > _algo.Time)
                {
                    _algo.Log($"{_algo.Time} RabbitMqClient: {channel} computed in {elapsedMs/1000}s; scheduling callback at {targetTime} (backtest at {_algo.Time}).");
                    _algo.Schedule.On(
                        $"{nameof(RabbitMQClient)}.{channel}Callback",
                        _algo.DateRules.Today,
                        _algo.TimeRules.At(targetTime.TimeOfDay),
                        () => handler(this, args)
                        );
                    return;
                }
            }

            handler(this, args);
        }

        // ── Channel routing ───────────────────────────────────────────
        private static ChannelPb GetRequestTypeChannel<T>()
        {
            return typeof(T) switch
            {
                _ when typeof(T) == typeof(RequestTargetPortfoliosPb) => ChannelPb.TargetPortfolio,
                _ when typeof(T) == typeof(RequestKalmanInitPb) => ChannelPb.KalmanInit,
                _ when typeof(T) == typeof(RequestStressTestDsPb) => ChannelPb.StressTestDs,
                _ when typeof(T) == typeof(RequestSSVICalibrationPb) => ChannelPb.RequestSsviCalibration,
                _ when typeof(T) == typeof(RequestPfRiskScenariosPb) => ChannelPb.RequestPfRiskScenarios,
                _ => throw new InvalidOperationException($"No channel mapping for request type {typeof(T)}.")
            };
        }

        // ── Disconnect ────────────────────────────────────────────────
        public async Task DisconnectAsync()
        {
            if (!await _connectLock.WaitAsync(TimeSpan.FromSeconds(10)))
                return;
            try
            {
                if (_channel != null)
                {
                    await _channel.CloseAsync();
                    _channel.Dispose();
                    _channel = null;
                }

                if (_connection != null)
                {
                    await _connection.CloseAsync();
                    _connection.Dispose();
                    _connection = null;
                }

                _algo.Log($"{_algo.Time} RabbitMqClient: Disconnected.");
            }
            catch (Exception e)
            {
                _algo.Error($"{_algo.Time} RabbitMqClient.DisconnectAsync(): {e}");
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // ── Dispose ───────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisconnectAsync().Wait(TimeSpan.FromSeconds(10));
            _connectLock.Dispose();
        }
    }
}
