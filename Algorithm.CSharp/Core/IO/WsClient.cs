using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{

    public class WsClient : IDisposable
    {
        public event EventHandler<object> EventHandlerConnected;

        // ── Timeout / interval constants ──────────────────────────────────
        private static readonly TimeSpan ConnectTimeout        = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan DisconnectTimeout     = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan KeepAliveInterval     = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan HealthCheckInterval   = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan ReconnectBackoffMin   = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReconnectBackoffMax   = TimeSpan.FromSeconds(30);
        private const int MaxConsecutiveReconnectFailures      = 10;

        // ── Connection state ──────────────────────────────────────────────
        private ClientWebSocket _ws;
        private CancellationTokenSource _connectionCts;   // lifetime of receive/send loops
        private CancellationTokenSource _healthCheckCts;  // lifetime of health-check loop
        private readonly SemaphoreSlim _connectLock = new(1, 1);
        private bool _disposed;

        // ── Tuning ────────────────────────────────────────────────────────
        private int ReceiveBufferSize { get; set; } = 8192;
        private SemaphoreSlim semaphore = new(1, 1);
        private readonly Foundations _algo;
        private readonly int _heartbeatTimeoutSeconds;
        private string _url;
        private DateTime _lastHeartbeat = DateTime.MaxValue;
        private int _consecutiveReconnectFailures;

        private readonly ConcurrentQueue<MessagePb> _messageQueue = new();
        private readonly Dictionary<ChannelPb, EventHandler<EventArgs>> _eventHandlers;

        public WsClient(Foundations algo)
        {
            _algo = algo;
            _eventHandlers = new();
            // 120 s in live mode — tolerant of a missed heartbeat or two
            _heartbeatTimeoutSeconds = _algo.LiveMode ? 120 : 9999999;
        }

        public void RegisterEventHandler<T>(ChannelPb channel, Action<object, T> handler) where T : EventArgs
        {
            if (!_eventHandlers.ContainsKey(channel))
            {
                _eventHandlers[channel] = null;
            }
            _eventHandlers[channel] += new EventHandler<EventArgs>((sender, e) =>
            {
                try
                {
                    handler(sender, (T)e);
                }
                catch (Exception ex)
                {
                    _algo.Error($"WsClient.{handler.Method.Name}(): Exception: {ex}");
                }
            });
        }

        // ── Semaphore helpers (back-testing gate) ─────────────────────────

        public void SetSemaphore(SemaphoreSlim sp)
        {
            if (!_algo.LiveMode)
            {
                semaphore = sp;
            }
        }

        public void WaitThread()
        {
            if (semaphore != null)
            {
                semaphore.Wait();
            }
        }

        public void ReleaseThread()
        {
            try
            {
                if (semaphore != null && semaphore.CurrentCount == 0)
                {
                    semaphore.Release();
                }
            }
            catch (ObjectDisposedException) { /* Already gone */ }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ReleaseThread(): Exception: {e}");
            }
        }

        // ── Connect ───────────────────────────────────────────────────────

        public async Task ConnectAsync(string url)
        {
            // Serialize connect / disconnect so they never overlap
            if (!await _connectLock.WaitAsync(ConnectTimeout))
            {
                _algo.Error("WsClient.ConnectAsync(): Could not acquire connect lock (another connect/disconnect in progress).");
                return;
            }

            try
            {
                // Already open? Nothing to do.
                if (_ws != null && _ws.State == WebSocketState.Open)
                {
                    _algo.Log("WsClient.ConnectAsync(): Already connected.");
                    return;
                }

                _algo.Log("WsClient.ConnectAsync(): Connecting...");
                _url = url;

                // ── Tear down previous socket if any ──
                CleanupConnection();

                // ── Create fresh socket with keep-alive ──
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = KeepAliveInterval;
                // If your server uses a specific sub-protocol, set it here:
                // _ws.Options.SetRequestHeader("Authorization", "Bearer ...");

                // Use a dedicated CTS for the connect handshake with a hard timeout
                using var connectCts = new CancellationTokenSource(ConnectTimeout);
                // The long-lived CTS that receive/send loops will observe
                _connectionCts = new CancellationTokenSource();

                await _ws.ConnectAsync(new Uri(url), connectCts.Token);

                _consecutiveReconnectFailures = 0;

                // Start loops on the long-lived token (NOT the connect-timeout token)
                _ = Task.Factory.StartNew(() => ReceiveLoopAsync(_connectionCts.Token),
                    _connectionCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _ = Task.Factory.StartNew(() => SendingLoopAsync(_connectionCts.Token),
                    _connectionCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                _algo.Log($"WsClient.ConnectAsync(): Connected successfully to: {url}");

                SubscribeToHeartbeat();
                EnsureHealthCheckRunning();
            }
            catch (OperationCanceledException)
            {
                _algo.Error($"WsClient.ConnectAsync(): Connect timed out after {ConnectTimeout.TotalSeconds}s.");
                CleanupConnection();
                ReleaseThread();
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ConnectAsync(): Exception: {e}");
                CleanupConnection();
                ReleaseThread();
            }
            finally
            {
                _connectLock.Release();
            }
        }

        // ── Disconnect ────────────────────────────────────────────────────

        public async Task DisconnectAsync()
        {
            if (!await _connectLock.WaitAsync(DisconnectTimeout))
            {
                _algo.Error("WsClient.DisconnectAsync(): Could not acquire connect lock.");
                return;
            }

            try
            {
                if (_ws is null)
                {
                    _algo.Log("WsClient.DisconnectAsync(): Already disconnected.");
                    return;
                }

                _algo.Log("WsClient.DisconnectAsync(): Disconnecting...");

                // Signal loops to stop
                _connectionCts?.Cancel();

                if (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived)
                {
                    using var closeCts = new CancellationTokenSource(DisconnectTimeout);
                    try
                    {
                        await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", closeCts.Token);
                        _algo.Log("WsClient.DisconnectAsync(): CloseOutputAsync completed.");
                    }
                    catch (Exception e)
                    {
                        _algo.Log($"WsClient.DisconnectAsync(): CloseOutputAsync failed (expected during disconnect): {e.Message}");
                    }
                }

                CleanupConnection();

                _algo.Log("WsClient.DisconnectAsync(): Disconnected from WebSocket.");
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.DisconnectAsync(): Exception: {e}");
            }
            finally
            {
                _connectLock.Release();
            }
        }

        /// <summary>
        /// Disposes the WebSocket and CTS safely. Caller must hold <see cref="_connectLock"/>.
        /// </summary>
        private void CleanupConnection()
        {
            try { _connectionCts?.Cancel(); } catch { }
            try { _connectionCts?.Dispose(); } catch { }
            _connectionCts = null;

            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }

        // ── Health check ──────────────────────────────────────────────────

        private void EnsureHealthCheckRunning()
        {
            if (_healthCheckCts != null && !_healthCheckCts.Token.IsCancellationRequested)
            {
                return; // already running
            }

            _healthCheckCts?.Dispose();
            _healthCheckCts = new CancellationTokenSource();
            _ = Task.Factory.StartNew(() => CheckConnectionHealthAsync(_healthCheckCts.Token),
                _healthCheckCts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _algo.Log("WsClient.EnsureHealthCheckRunning(): HealthCheck started.");
        }

        public void StopHealthCheck()
        {
            _healthCheckCts?.Cancel();
            _healthCheckCts?.Dispose();
            _healthCheckCts = null;
        }

        private async Task CheckConnectionHealthAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HealthCheckInterval, token);
                }
                catch (OperationCanceledException) { break; }

                // ── Case 1: socket gone or in a terminal state ──
                if (_ws is null ||
                    _ws.State == WebSocketState.Closed ||
                    _ws.State == WebSocketState.Aborted ||
                    _ws.State == WebSocketState.None)
                {
                    _algo.Error($"WsClient.CheckConnectionHealth(): WS={_ws?.State ?? null}. Triggering reconnect...");
                    ReleaseThread();
                    await ReconnectWithBackoffAsync();
                    continue;
                }

                // ── Case 2: socket is still Connecting or CloseSent — just wait, don't interfere ──
                if (_ws.State != WebSocketState.Open)
                {
                    _algo.Log($"WsClient.CheckConnectionHealth(): WS in transient state {_ws.State}, waiting...");
                    continue;
                }

                // ── Case 3: heartbeat timeout ──
                if (DateTime.UtcNow - _lastHeartbeat > TimeSpan.FromSeconds(_heartbeatTimeoutSeconds))
                {
                    _algo.Error($"WsClient.CheckConnectionHealth(): No heartbeat for {_heartbeatTimeoutSeconds}s. Last HB: {_lastHeartbeat}. Reconnecting...");
                    ReleaseThread();
                    await ReconnectWithBackoffAsync();
                }
            }
        }

        private async Task ReconnectWithBackoffAsync()
        {
            // Disconnect first (best-effort — don't block if it fails)
            try
            {
                await DisconnectAsync();
            }
            catch (Exception e)
            {
                _algo.Log($"WsClient.ReconnectWithBackoffAsync(): Disconnect error (ignored): {e.Message}");
            }

            // Exponential back-off
            _consecutiveReconnectFailures++;
            var backoff = TimeSpan.FromSeconds(Math.Min(
                ReconnectBackoffMin.TotalSeconds * Math.Pow(2, _consecutiveReconnectFailures - 1),
                ReconnectBackoffMax.TotalSeconds));

            _algo.Log($"WsClient.ReconnectWithBackoffAsync(): Waiting {backoff.TotalSeconds:F0}s before reconnect (attempt #{_consecutiveReconnectFailures})...");
            try
            {
                await Task.Delay(backoff, _healthCheckCts?.Token ?? CancellationToken.None);
            }
            catch (OperationCanceledException) { return; }

            if (_consecutiveReconnectFailures > MaxConsecutiveReconnectFailures)
            {
                _algo.Error($"WsClient.ReconnectWithBackoffAsync(): {MaxConsecutiveReconnectFailures} consecutive failures. Resetting counter but will keep trying.");
                _consecutiveReconnectFailures = 0;
            }

            _algo.Log("WsClient.ReconnectWithBackoffAsync(): Attempting reconnect...");
            await ConnectAsync(_url);
        }

        // ── Receive loop ──────────────────────────────────────────────────

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            _algo.Log("WsClient.ReceiveLoopAsync(): Starting...");
            MemoryStream outputStream = null;
            var buffer = new byte[ReceiveBufferSize];

            try
            {
                while (!token.IsCancellationRequested && _ws != null && _ws.State == WebSocketState.Open)
                {
                    outputStream = new MemoryStream(ReceiveBufferSize);
                    WebSocketReceiveResult receiveResult;
                    do
                    {
                        receiveResult = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                        if (receiveResult.MessageType != WebSocketMessageType.Close)
                            outputStream.Write(buffer, 0, receiveResult.Count);
                    }
                    while (!receiveResult.EndOfMessage);

                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        _algo.Log("WsClient.ReceiveLoopAsync(): Server initiated close.");
                        break;
                    }

                    outputStream.Position = 0;
                    OnMessage(outputStream);
                    outputStream = null; // OnMessage disposes it
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown via CTS — not an error
                _algo.Log("WsClient.ReceiveLoopAsync(): Cancelled.");
            }
            catch (WebSocketException e)
            {
                _algo.Error($"WsClient.ReceiveLoopAsync(): WebSocketException: {e.WebSocketErrorCode} - {e.Message}");
                ReleaseThread();
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ReceiveLoopAsync(): Exception: {e}");
                ReleaseThread();
            }
            finally
            {
                outputStream?.Dispose();
                _algo.Log("WsClient.ReceiveLoopAsync(): Exiting.");
            }
        }

        // ── Send loop ─────────────────────────────────────────────────────

        private async Task SendingLoopAsync(CancellationToken token)
        {
            _algo.Log("WsClient.SendingLoopAsync(): Starting...");

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (_messageQueue.IsEmpty)
                    {
                        await Task.Delay(100, token);
                        continue;
                    }

                    if (_ws == null || _ws.State != WebSocketState.Open)
                    {
                        // Socket not ready — pause briefly to avoid tight loop
                        await Task.Delay(500, token);
                        continue;
                    }

                    if (!_messageQueue.TryDequeue(out MessagePb message))
                        continue;

                    try
                    {
                        using var buffer = new MemoryStream();
                        message.WriteTo(buffer);
                        await _ws.SendAsync(
                            new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length),
                            WebSocketMessageType.Binary,
                            true,
                            token);
                    }
                    catch (OperationCanceledException)
                    {
                        _messageQueue.Enqueue(message); // put it back
                        break;
                    }
                    catch (Exception e)
                    {
                        _algo.Error($"WsClient.SendingLoopAsync(): Send failed: {e.Message}. Re-enqueuing message.");
                        _messageQueue.Enqueue(message);
                        ReleaseThread();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _algo.Log("WsClient.SendingLoopAsync(): Cancelled.");
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.SendingLoopAsync(): Exception: {e}");
                ReleaseThread();
            }

            _algo.Log("WsClient.SendingLoopAsync(): Exiting.");
        }

        // ── Messaging ─────────────────────────────────────────────────────

        private ChannelPb GetRequestTypeChannel<T>()
        {
            return typeof(T) switch
            {
                _ when typeof(T) == typeof(RequestTargetPortfoliosPb)  => ChannelPb.TargetPortfolio,
                _ when typeof(T) == typeof(RequestKalmanInitPb)        => ChannelPb.KalmanInit,
                _ when typeof(T) == typeof(RequestStressTestDsPb)      => ChannelPb.StressTestDs,
                _ when typeof(T) == typeof(RequestSSVICalibrationPb)   => ChannelPb.RequestSsviCalibration,
                _ when typeof(T) == typeof(RequestPfRiskScenariosPb)   => ChannelPb.RequestPfRiskScenarios,
                _ => throw new InvalidOperationException($"No channel mapping found for request type {typeof(T)}.")
            };
        }

        public Task SendMessageAsync<T>(T request) where T : IMessage<T>
        {
            _messageQueue.Enqueue(new MessagePb
            {
                Channel = GetRequestTypeChannel<T>(),
                Id      = Guid.NewGuid().ToString(),
                Action  = ActionPb.Subscribe,
                Payload = request.ToByteString()
            });
            return Task.CompletedTask;
        }

        private void SubscribeToHeartbeat()
        {
            _algo.Log("WsClient.SubscribeToHeartbeat(): Subscribing to heartbeat");
            _lastHeartbeat = DateTime.UtcNow;
            _messageQueue.Enqueue(new MessagePb
            {
                Channel = ChannelPb.Hb,
                Id      = Guid.NewGuid().ToString(),
                Action  = ActionPb.Subscribe,
                Payload = ByteString.Empty
            });
        }

        private void OnMessage(Stream inputStream)
        {
            MessagePb message = MessagePb.Parser.ParseFrom(inputStream);
            inputStream.Dispose();
            ChannelPb channel = message.Channel;

            switch (channel)
            {
                case ChannelPb.Hb:
                    _lastHeartbeat = DateTime.UtcNow;
                    break;
                case ChannelPb.TargetPortfolio:
                    _eventHandlers[channel]?.Invoke(this, new TargetPortfoliosEventArgs(ResponseTargetPortfoliosPb.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.StressTestDs:
                    _eventHandlers[channel]?.Invoke(this, new ResultStressTestDsEventArgs(ResultStressTestDsPb.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.CmdFetchTargetPortfolio:
                    _eventHandlers[channel]?.Invoke(this, new CmdFetchTargetPortfolioEventArgs(CmdFetchTargetPortfolio.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.CmdCancelOid:
                    _eventHandlers[channel]?.Invoke(this, new CmdCancelOIDEventArgs(CmdCancelOID.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.CmdCfgOverride:
                    _eventHandlers[channel]?.Invoke(this, new CmdCfgOverrideEventArgs(CmdCfgOverride.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.KalmanInit:
                    _eventHandlers[channel]?.Invoke(this, new ResponseKalmanInitEventArgs(ResponseKalmanInitPb.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.RequestSsviCalibration:
                    _eventHandlers[channel]?.Invoke(this, new ResponseSSVICalibrationEventArgs(ResponseSSVICalibrationPb.Parser.ParseFrom(message.Payload)));
                    break;
                case ChannelPb.RequestPfRiskScenarios:
                    _eventHandlers[channel]?.Invoke(this, new ResponsePfRiskScenariosEventArgs(ResponsePfRiskScenariosPb.Parser.ParseFrom(message.Payload)));
                    break;
                default:
                    _algo.Error($"Unknown message channel: {message.Channel}");
                    break;
            }
        }

        // ── Dispose ───────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopHealthCheck();
            DisconnectAsync().Wait(TimeSpan.FromSeconds(10));
            _connectLock.Dispose();
        }
    }
}
