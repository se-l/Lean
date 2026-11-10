using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Algorithm.CSharp.Core.Indicators;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    /// <summary>
    /// Connection + stream naming for the live surface feed. Everything is explicit so the algo can
    /// derive it from <c>EarningsAlgorithmConfig</c> and <c>Launcher/config.json</c>.
    /// </summary>
    public sealed class SurfaceStreamOptions
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 5672;
        public string User { get; set; } = "guest";
        public string Pass { get; set; } = "guest";
        public string VirtualHost { get; set; } = "/";

        /// <summary>Topic exchange the Julia producer publishes to (<c>surface-stream-exchange</c>).</summary>
        public string Exchange { get; set; } = "surfaces";

        /// <summary>Queue name prefix; the queue is <c>prefix + UNDERLYING</c> (<c>surface-stream-prefix</c>).</summary>
        public string QueuePrefix { get; set; } = "surface.";

        /// <summary><c>first</c> replays the retained window (the algo dedupes), <c>last</c> starts at the last chunk.</summary>
        public string Offset { get; set; } = "first";

        public ushort Prefetch { get; set; } = 200;
        public int ReconnectSeconds { get; set; } = 5;
    }

    /// <summary>
    /// LIVE surface feed: one RabbitMQ STREAM per underlying (<c>surface.&lt;UNDERLYING&gt;</c>) carrying
    /// every surface id in that stream. The consumer filters by id and hands each event to the algo.
    ///
    /// Deliberately separate from <see cref="RabbitMQClient"/>: that client is request/response with one
    /// in-flight request per channel and a send gate; a long-lived stream consumer would contend with
    /// it. This class owns its own connection and never publishes.
    ///
    /// Backtest is untouched: the warehouse path (<c>R2SurfaceReader</c> -> <c>SurfaceSSVI</c> slices)
    /// remains the only backtest source.
    /// </summary>
    public sealed class SurfaceStreamClient : IDisposable
    {
        private readonly SurfaceStreamOptions _options;
        private readonly Action<string> _log;
        private readonly CancellationTokenSource _cts = new();
        private IConnection _connection;
        private IChannel _channel;
        private int _disposed;

        public SurfaceStreamClient(SurfaceStreamOptions options, Action<string> log)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _log = log ?? (_ => { });
        }

        /// <summary>
        /// Start consuming in the background. Returns immediately; connection failures are retried
        /// forever and logged. Safe to call once per algo.
        /// </summary>
        public Task StartAsync(IEnumerable<string> underlyings, Action<SurfaceEventPb> onEvent)
        {
            _ = Task.Run(() => RunAsync(underlyings, onEvent));
            return Task.CompletedTask;
        }

        private async Task RunAsync(IEnumerable<string> underlyings, Action<SurfaceEventPb> onEvent)
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndConsumeAsync(underlyings, onEvent);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"SurfaceStreamClient: {ex.Message} — reconnecting in {_options.ReconnectSeconds}s");
                }
                finally
                {
                    await CloseAsync();
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectSeconds), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task ConnectAndConsumeAsync(IEnumerable<string> underlyings, Action<SurfaceEventPb> onEvent)
        {
            var factory = new ConnectionFactory
            {
                HostName = _options.Host,
                Port = _options.Port,
                UserName = _options.User,
                Password = _options.Pass,
                VirtualHost = _options.VirtualHost,
                // Own reconnect: the stream consumer must be re-created with its offset argument.
                AutomaticRecoveryEnabled = false,
                RequestedHeartbeat = TimeSpan.FromSeconds(60),
            };

            _connection = await factory.CreateConnectionAsync("quantconnect-surface-stream");
            _channel = await _connection.CreateChannelAsync();

            await _channel.ExchangeDeclareAsync(_options.Exchange, ExchangeType.Topic, durable: true);
            // Streams require an explicit consumer prefetch; without it the broker refuses basic.consume.
            await _channel.BasicQosAsync(0, _options.Prefetch, false);

            foreach (string underlying in underlyings)
            {
                string und = (underlying ?? string.Empty).Trim().ToUpperInvariant();
                if (und.Length == 0)
                {
                    continue;
                }

                string queue = _options.QueuePrefix + und;
                await _channel.QueueDeclareAsync(
                    queue: queue,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    arguments: new Dictionary<string, object>
                    {
                        ["x-queue-type"] = "stream",
                        ["x-max-age"] = "2D",
                        ["x-stream-max-segment-size-bytes"] = 8_000_000,
                    });
                await _channel.QueueBindAsync(queue, _options.Exchange, und);

                var consumer = new AsyncEventingBasicConsumer(_channel);
                consumer.ReceivedAsync += async (sender, ea) =>
                {
                    try
                    {
                        SurfaceEventPb ev = SurfaceEventPb.Parser.ParseFrom(ea.Body.ToArray());
                        onEvent(ev);
                    }
                    catch (Exception ex)
                    {
                        _log($"SurfaceStreamClient: decode/dispatch failed on {queue}: {ex.Message}");
                    }
                    finally
                    {
                        // Streams reject auto-ack; every delivery must be acknowledged explicitly.
                        try
                        {
                            await _channel.BasicAckAsync(ea.DeliveryTag, false);
                        }
                        catch
                        {
                            // Channel died mid-flight; the reconnect loop will replay from the offset.
                        }
                    }
                };

                await _channel.BasicConsumeAsync(
                    queue: queue,
                    autoAck: false,
                    consumerTag: string.Empty,
                    noLocal: false,
                    exclusive: false,
                    arguments: new Dictionary<string, object> { ["x-stream-offset"] = _options.Offset },
                    consumer: consumer);

                _log($"SurfaceStreamClient: consuming {queue} offset={_options.Offset} exchange={_options.Exchange}");
            }

            // Keep the task (and therefore the connection) alive until Stop/Dispose.
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }

        /// <summary>SSVI parameters in the algo's native form; tenors the consumer does not need are ignored.</summary>
        public static SSVIParamsDictionary ToParams(SurfaceEventPb ev)
        {
            var parameters = new SSVIParamsDictionary();
            foreach (SurfaceTenorPb tenor in ev.Tenors)
            {
                DateTime tenorDt = DateTime.ParseExact(tenor.TenorDt, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                parameters[tenorDt] = new SSVIParamsRecord(tenor.Theta, tenor.Rho, tenor.Psi);
            }
            return parameters;
        }

        private async Task CloseAsync()
        {
            try
            {
                if (_channel != null)
                {
                    await _channel.CloseAsync();
                }
            }
            catch
            {
            }
            try
            {
                if (_connection != null)
                {
                    await _connection.CloseAsync();
                }
            }
            catch
            {
            }
            _channel = null;
            _connection = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }
            // Fire-and-forget close: the background task still holds the token, and a blocking
            // Dispose on a dead socket is how shutdowns hang.
            try
            {
                _ = CloseAsync();
            }
            catch
            {
            }
        }
    }
}
