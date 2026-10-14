using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using QuantConnect.Scheduling;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    public class RabbitMQClient : IDisposable
    {
        public event EventHandler<object> EventHandlerConnected;

        // ── Fields ────────────────────────────────────────────────────
        private readonly Foundations _algo;
        private readonly Dictionary<ChannelPb, EventHandler<EventArgs>> _eventHandlers;
        // Per-request send timing: utcTime = algo clock at send, systemTime = wall clock at send
        private readonly ConcurrentDictionary<string, DateTime> _activeRequests = new();
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

        public bool HasActiveRequests() => !_activeRequests.IsEmpty;

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

            _activeRequests[msg.Id] = _algo.Time;

            var props = new BasicProperties
            {
                ReplyTo = _replyQueueName,
                CorrelationId = msg.Id
            };

            await _channel.BasicPublishAsync(
                exchange: ExchangeIn,
                routingKey: RoutingKeyPrefix + rabbitChannel.ToString().ToLowerInvariant(), // e.g. "calc.targetportfolio"
                mandatory: false,
                basicProperties: props,
                body: body);
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
            if (_activeRequests.TryRemove(id, out var sentTime))
            {
                return sentTime;
            }
            return null;
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
