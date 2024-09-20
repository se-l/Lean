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
        //private readonly Dictionary<object, Action<object, EventArgs>> _eventHandlers;

        public event EventHandler<ResponseTargetPortfolios> EventHandlerResponseTargetPortfolios;
        public event EventHandler<ResultStressTestDs> EventHandlerResultStressTestDs;
        public event EventHandler<CmdFetchTargetPortfolio> EventHandlerCmdFetchTargetPortfolio;
        public event EventHandler<CmdCancelOID> EventHandlerCmdCancelOID;
        public event EventHandler<CmdCfgOverride> EventHandlerCmdCfgOverride;
        public event EventHandler<ResponseKalmanInit> EventHandlerResponseKalmanInit;
        public event EventHandler<ResponseSSVICalibration> EventHandlerResponseSSVICalibration;
        public event EventHandler<object> EventHandlerWSConnected;

        private ClientWebSocket WS;
        private CancellationTokenSource CTS;
        private CancellationTokenSource CTSHealthCheck;
        public int ReceiveBufferSize { get; set; } = 8192;
        private SemaphoreSlim semaphore = new(1, 1);  // Only during backtesting
        private readonly Foundations _algo;
        private string url;
        private DateTime lastHeartbeat = DateTime.MaxValue;
        private ConcurrentQueue<Message> _messageQueue = new();

        public WsClient(Foundations algo)
        {
            _algo = algo;
            //_eventHandlers = new();
        }

        //public void RegisterEventHandler(object eventName, Action<object, EventArgs> handler)
        //{
        //    if (!_eventHandlers.ContainsKey(eventName))
        //    {
        //        _eventHandlers[eventName] = handler;
        //    }
        //    else
        //    {
        //        _eventHandlers[eventName] += handler;
        //    }
        //}

        //public void OnEvent(object eventName, object sender, EventArgs e)
        //{
        //    if (_eventHandlers.TryGetValue(eventName, out var handler))
        //    {
        //        handler?.Invoke(sender, e);
        //    }
        //}

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
                    if (receiveResult.MessageType == WebSocketMessageType.Close) break;
                    outputStream.Position = 0;
                    
                    ResponseReceived(outputStream);                    
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
                    if (!_messageQueue.TryDequeue(out Message message)) {
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

        private Channel GetRequestTypeChannel<T>()
        {
            return typeof(T) switch
            {
                _ when typeof(T) == typeof(RequestTargetPortfolios) => Channel.TargetPortfolio,
                _ when typeof(T) == typeof(RequestKalmanInit) => Channel.KalmanInit,
                _ when typeof(T) == typeof(RequestStressTestDs) => Channel.StressTestDs,
                _ when typeof(T) == typeof(RequestSSVICalibration) => Channel.RequestSsviCalibration,
                _ => throw new InvalidOperationException($"No channel mapping found for request type {typeof(T)}.")
            };
        }

        public async Task SendMessageAsync<T>(T request) where T : IMessage<T>
        {
            _messageQueue.Enqueue(new Message()
            {
                Channel = GetRequestTypeChannel<T>(),
                Id = Guid.NewGuid().ToString(),
                Action = Action.Subscribe,
                Payload = request.ToByteString()
            });

            await Task.CompletedTask;
        }

        public async void SubscribeToHeartbeat()
        {
            _algo.Log("WsClient.SubscribeToHeartbeat(): Subscribing to heartbeat");
            Message message = new()
            {
                Channel = Channel.Hb,
                Id = Guid.NewGuid().ToString(),
                Action  = Action.Subscribe,
                Payload = ByteString.Empty
            };
            lastHeartbeat = DateTime.Now;
            _messageQueue.Enqueue(message);
        }

        private void ResponseReceived(Stream inputStream)
        {
            Message message = Message.Parser.ParseFrom(inputStream);
            inputStream.Dispose();

            switch (message.Channel)
            {
                case Channel.Hb:
                    HandleHeartbeat(message);
                    break;
                case Channel.TargetPortfolio:
                    HandleTargetPortfolios(message);
                    break;
                case Channel.StressTestDs:
                    HandleStressTestDs(message);
                    break;
                case Channel.CmdFetchTargetPortfolio:
                    HandleCmdFetchTargetPortfolio(message);
                    break;
                case Channel.CmdCancelOid:
                    HandleCmdCancelOID(message);
                    break;
                case Channel.CmdCfgOverride:
                    HandleCmdCfgOverride(message);
                    break;
                case Channel.KalmanInit:
                    HandleKalmanInit(message);
                    break;
                case Channel.RequestSsviCalibration:
                    HandleSSVICalibration(message);
                    break;
                default:
                    _algo.Error($"Unknown message channel: {message.Channel}");
                    break;
            }
        }

        private void HandleHeartbeat(Message message)
        {
            lastHeartbeat = DateTime.Now;
        }

        private void HandleTargetPortfolios(Message message)
        {
            ResponseTargetPortfolios responseTargetPortfolios = ResponseTargetPortfolios.Parser.ParseFrom(message.Payload);
            if (responseTargetPortfolios.IsLastTransmission)
            {
                ReleaseThread();
            }
            EventHandlerResponseTargetPortfolios?.Invoke(this, responseTargetPortfolios);
        }

        private void HandleStressTestDs(Message message)
        {
            ResultStressTestDs resultStressTestDs = ResultStressTestDs.Parser.ParseFrom(message.Payload);
            ReleaseThread();
            EventHandlerResultStressTestDs?.Invoke(this, resultStressTestDs);            
        }

        private void HandleCmdFetchTargetPortfolio(Message message)
        {
            CmdFetchTargetPortfolio cmdFetchTargetPortfolio = CmdFetchTargetPortfolio.Parser.ParseFrom(message.Payload);
            EventHandlerCmdFetchTargetPortfolio?.Invoke(this, cmdFetchTargetPortfolio);
        }

        private void HandleCmdCancelOID(Message message)
        {
            CmdCancelOID cmdCancelOID = CmdCancelOID.Parser.ParseFrom(message.Payload);
            EventHandlerCmdCancelOID?.Invoke(this, cmdCancelOID);
        }
        
        private void HandleCmdCfgOverride(Message message)
        {
            CmdCfgOverride cmdCfgOverride = CmdCfgOverride.Parser.ParseFrom(message.Payload);
            EventHandlerCmdCfgOverride?.Invoke(this, cmdCfgOverride);
        }

        private void HandleKalmanInit(Message message)
        {
            ResponseKalmanInit responseKalmanInit = ResponseKalmanInit.Parser.ParseFrom(message.Payload);
            ReleaseThread();
            EventHandlerResponseKalmanInit?.Invoke(this, responseKalmanInit);
        }
        private void HandleSSVICalibration(Message message)
        {
            ResponseSSVICalibration responseSSVICalibration = ResponseSSVICalibration.Parser.ParseFrom(message.Payload);
            ReleaseThread();
            EventHandlerResponseSSVICalibration?.Invoke(this, responseSSVICalibration);
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
