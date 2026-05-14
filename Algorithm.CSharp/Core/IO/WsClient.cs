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
    public class TargetPortfoliosEventArgs : EventArgs
    {
        public TargetPortfoliosEventArgs(ResponseTargetPortfoliosPb responseTargetPortfolios)
        {
            ResponseTargetPortfolios = responseTargetPortfolios;
        }

        public ResponseTargetPortfoliosPb ResponseTargetPortfolios { get; }
    }

    public class ResultStressTestDsEventArgs : EventArgs
    {
        public ResultStressTestDsEventArgs(ResultStressTestDsPb resultStressTestDs)
        {
            ResultStressTestDs = resultStressTestDs;
        }

        public ResultStressTestDsPb ResultStressTestDs { get; }
    }

    public class CmdFetchTargetPortfolioEventArgs : EventArgs
    {
        public CmdFetchTargetPortfolioEventArgs(CmdFetchTargetPortfolio cmdFetchTargetPortfolio)
        {
            CmdFetchTargetPortfolio = cmdFetchTargetPortfolio;
        }

        public CmdFetchTargetPortfolio CmdFetchTargetPortfolio { get; }
    }

    public class CmdCancelOIDEventArgs : EventArgs
    {
        public CmdCancelOIDEventArgs(CmdCancelOID cmdCancelOid)
        {
            CmdCancelOid = cmdCancelOid;
        }

        public CmdCancelOID CmdCancelOid { get; }
    }

    public class CmdCfgOverrideEventArgs : EventArgs
    {
        public CmdCfgOverrideEventArgs(CmdCfgOverride cmdCfgOverride)
        {
            CmdCfgOverride = cmdCfgOverride;
        }

        public CmdCfgOverride CmdCfgOverride { get; }
    }

    public class ResponseKalmanInitEventArgs : EventArgs
    {
        public ResponseKalmanInitEventArgs(ResponseKalmanInitPb responseKalmanInit)
        {
            ResponseKalmanInit = responseKalmanInit;
        }

        public ResponseKalmanInitPb ResponseKalmanInit { get; }
    }

    public class ResponseSSVICalibrationEventArgs : EventArgs
    {
        public ResponseSSVICalibrationEventArgs(ResponseSSVICalibrationPb responseSSVICalibration)
        {
            ResponseSSVICalibration = responseSSVICalibration;
        }

        public ResponseSSVICalibrationPb ResponseSSVICalibration { get; }
    }

    public class ResponsePfRiskScenariosEventArgs : EventArgs
    {
        public ResponsePfRiskScenariosEventArgs(ResponsePfRiskScenariosPb responsePfRiskScenarios)
        {
            ResponsePfRiskScenarios = responsePfRiskScenarios;
        }

        public ResponsePfRiskScenariosPb ResponsePfRiskScenarios { get; }
    }

    public class WsClient : IDisposable
    {        
        public event EventHandler<object> EventHandlerWSConnected;

        private ClientWebSocket WS;
        private CancellationTokenSource CTS;
        private CancellationTokenSource CTSHealthCheck;
        public int ReceiveBufferSize { get; set; } = 8192;
        private SemaphoreSlim semaphore = new(1, 1);  // Only during backtesting
        private readonly Foundations _algo;
        private string url;
        private DateTime lastHeartbeat = DateTime.MaxValue;
        private ConcurrentQueue<MessagePb> _messageQueue = new();
        private readonly Dictionary<ChannelPb, EventHandler<EventArgs>> _eventHandlers;

        public WsClient(Foundations algo)
        {
            _algo = algo;
            _eventHandlers = new();
        }

        public void RegisterEventHandler<T>(ChannelPb channel, Action<object, T> handler) where T : EventArgs
        {
            if (!_eventHandlers.ContainsKey(channel))
            {
                _eventHandlers[channel] = null;
            }
            _eventHandlers[channel] += new EventHandler<EventArgs>((sender, e) => {
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
                    semaphore.Dispose();
                }
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ReleaseThread(): Exception: {e}");
            }
        }

        public async Task ConnectAsync(string url)
        {
            try
            {
                _algo.Log("WsClient.ConnectAsync(): Connecting...");
                this.url = url;
                if (WS != null)
                {
                    if (WS.State == WebSocketState.Open)
                    {
                        _algo.Log("WsClient.ConnectAsync(): WS.State is open. Already connected.");
                        return;
                    }
                    else WS.Dispose();
                }
                CTS?.Dispose();

                WS = new ClientWebSocket();
                CTS = new CancellationTokenSource();
                await WS.ConnectAsync(new Uri(url), CTS.Token);
                if (CTS is null)  // Was null in debug...
                {
                    _algo.Log($"WsClient.ConnectAsync(): CancellationTokenSource is null. Presuming connecting failed");
                }
                await Task.Factory.StartNew(ReceiveLoop, CTS.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                await Task.Factory.StartNew(SendingLoop, CTS.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                _algo.Log($"WsClient.ConnectAsync(): Connected successfully to: {url}");
                SubscribeToHeartbeat();
                StartHealthCheck();
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ConnectAsync(): Exception: {e}");
                ReleaseThread();
            }
        }

        public async Task StartHealthCheck()
        {
            if (CTSHealthCheck != null && !CTSHealthCheck.Token.IsCancellationRequested)
            { 
                _algo.Log("WsClient.StartHealthCheck(): HealthCheck already running...");
                return;
            };

            CTSHealthCheck = new CancellationTokenSource();
            await Task.Factory.StartNew(CheckConnectionHealth, CTSHealthCheck.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _algo.Log("WsClient.StartHealthCheck(): HealthCheck started...");
        }

        public async Task CheckConnectionHealth()
        {
            while (true)
            {
                await Task.Delay(1000);
                if (WS is null || WS.State != WebSocketState.Open)
                {
                    ReleaseThread();
                    _algo.Error($"WsClient.CheckConnectionHealth(): WS={WS}, State:{WS?.State}. Reconnecting...");
                    DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
                    ConnectAsync(url).Wait(TimeSpan.FromSeconds(5));
                }
                else if (DateTime.Now - lastHeartbeat > TimeSpan.FromSeconds(30))
                {
                    ReleaseThread();
                    _algo.Error($"WsClient.CheckConnectionHealth(): No heartbeat received. Last HB at: {lastHeartbeat}. Disconnecting");
                    DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
                }
            }
        }

        public async Task DisconnectAsync()
        {
            if (WS is null) return;

            _algo.Log("WsClient.DisconnectAsync(): Disconnecting...");

            // TODO: requests cleanup code, sub-protocol dependent.
            if (WS.State == WebSocketState.Open)
            {
                CTS?.CancelAfter(TimeSpan.FromSeconds(2));
                _algo.Log("WsClient.DisconnectAsync(): CancelationTocken canceled in 2 seconds");
                await WS.CloseOutputAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
                _algo.Log("WsClient.DisconnectAsync(): CloseOutputAsync called");

                // Below line doesnt succeed. function stops here... Therefore, now awaiting it.
                _ = WS.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                _algo.Log("WsClient.DisconnectAsync(): CloseAsync called without awaiting");
            }

            CTS?.Cancel();
            CTS?.Dispose();
            CTS = null;

            WS.Dispose();
            WS = null;

            _algo.Log("WsClient.DisconnectAsync(): Disconnected from WebSocket.");
        }

        private async Task ReceiveLoop()
        {
            _algo.Log("WsClient.ReceiveLoop(): Starting...");

            var loopToken = CTS.Token;
            MemoryStream outputStream = null;
            WebSocketReceiveResult receiveResult;
            var buffer = new byte[ReceiveBufferSize];
            try
            {
                while (!loopToken.IsCancellationRequested)
                {
                    outputStream = new MemoryStream(ReceiveBufferSize);
                    do
                    {
                        receiveResult = await WS.ReceiveAsync(buffer, CTS.Token);
                        if (receiveResult.MessageType != WebSocketMessageType.Close)
                            outputStream.Write(buffer, 0, receiveResult.Count);
                    }
                    while (!receiveResult.EndOfMessage);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                        break;
                    outputStream.Position = 0;
                    
                    OnMessage(outputStream);                    
                }
            }
            catch (TaskCanceledException e)
            {
                _algo.Error($"WsClient.ReceiveLoop(): Exception: ${e}");
                ReleaseThread();
                CTS?.Cancel();
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.ReceiveLoop(): Exception: ${e}");
                ReleaseThread();
            }
            finally
            {
                outputStream?.Dispose();
            }
        }

        private async Task SendingLoop()
        {
            _algo.Log("WsClient.SendingLoop(): Starting...");

            var loopToken = CTS.Token;
            
            try
            {
                while (!loopToken.IsCancellationRequested)
                {
                    if (_messageQueue.Count == 0)
                    {
                        await Task.Delay(100);
                        continue;
                    }
                    if (WS == null)
                    {
                        _algo.Error("WebSocket is null. Cannot send message.");
                        continue;
                    }
                    if (!_messageQueue.TryDequeue(out MessagePb message)) {
                        continue;
                    }
                    try
                    {
                        using var buffer = new MemoryStream();
                        message.WriteTo(buffer);
                        WS.SendAsync(new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), WebSocketMessageType.Binary, true, CTS.Token);
                    }
                    catch (Exception e)
                    {
                        _algo.Error($"WsClient.SendingLoop(): Exception sending message: ${e}. Enqueuing message");
                        if (message != null)
                        {
                            _messageQueue.Enqueue(message);
                        }
                        ReleaseThread();
                    }
                }
            }
            catch (Exception e)
            {
                _algo.Error($"WsClient.SendingLoop(): Exception: ${e}");
                ReleaseThread();
            }
        }

        private ChannelPb GetRequestTypeChannel<T>()
        {
            return typeof(T) switch
            {
                _ when typeof(T) == typeof(RequestTargetPortfoliosPb) => ChannelPb.TargetPortfolio,
                _ when typeof(T) == typeof(RequestKalmanInitPb) => ChannelPb.KalmanInit,
                _ when typeof(T) == typeof(RequestStressTestDsPb) => ChannelPb.StressTestDs,
                _ when typeof(T) == typeof(RequestSSVICalibrationPb) => ChannelPb.RequestSsviCalibration,
                _ when typeof(T) == typeof(RequestPfRiskScenariosPb) => ChannelPb.RequestPfRiskScenarios,
                _ => throw new InvalidOperationException($"No channel mapping found for request type {typeof(T)}.")
            };
        }

        public async Task SendMessageAsync<T>(T request) where T : IMessage<T>
        {
            _messageQueue.Enqueue(new MessagePb()
            {
                Channel = GetRequestTypeChannel<T>(),
                Id = Guid.NewGuid().ToString(),
                Action = ActionPb.Subscribe,
                Payload = request.ToByteString()
            });

            await Task.CompletedTask;
        }

        public async void SubscribeToHeartbeat()
        {
            _algo.Log("WsClient.SubscribeToHeartbeat(): Subscribing to heartbeat");
            MessagePb message = new()
            {
                Channel = ChannelPb.Hb,
                Id = Guid.NewGuid().ToString(),
                Action  = ActionPb.Subscribe,
                Payload = ByteString.Empty
            };
            lastHeartbeat = DateTime.Now;
            _messageQueue.Enqueue(message);
        }

        private void OnMessage(Stream inputStream)
        {
            MessagePb message = MessagePb.Parser.ParseFrom(inputStream);
            inputStream.Dispose();
            ChannelPb channel = message.Channel;

            switch (channel)
            {
                case ChannelPb.Hb:
                    HandleHeartbeat(message);
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

        private void HandleHeartbeat(MessagePb message)
        {
            lastHeartbeat = DateTime.Now;
            // _algo.Log($"WsClient.HandleHeartbeat(): Received heartbeat: {lastHeartbeat}");
        }

        public void StopHealthCheck()
        {
            CTSHealthCheck?.Cancel();
            CTSHealthCheck?.Dispose();
            CTSHealthCheck = null;
        }

        public void Dispose()
        {
            DisconnectAsync().Wait();
        }
    }
}
