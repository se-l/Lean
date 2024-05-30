using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;

namespace QuantConnect.Algorithm.CSharp.Core.IO
{
    public class WsClient : IDisposable
    {
        public event EventHandler<ResponseTargetPortfolios> EventHandlerResponseTargetPortfolios;
        public event EventHandler<ResultStressTestDs> EventHandlerResultStressTestDs;
        public event EventHandler<CmdFetchTargetPortfolio> EventHandlerCmdFetchTargetPortfolio;
        public event EventHandler<CmdCancelOID> EventHandlerCmdCancelOID;
        public event EventHandler<ResponseKalmanInit> EventHandlerResponseKalmanInit;

        private ClientWebSocket WS;
        private CancellationTokenSource CTS;
        public int ReceiveBufferSize { get; set; } = 8192;
        public SemaphoreSlim semaphore = new(1, 1);  // Only during backtesting
        private readonly Foundations _algo;
        private string url;
        private DateTime lastHeartbeat = DateTime.MaxValue;

        public WsClient(Foundations algo)
        {
            _algo = algo;
        }

        public void SetSemaphore(SemaphoreSlim sp)
        {
            if (!_algo.LiveMode)
            {
                semaphore = sp;
            }            
        }

        public void ReleaseThread()
        {
            if (semaphore != null && semaphore.CurrentCount == 0)
            {
                semaphore.Release();
            }
        }

        public async Task ConnectAsync(string url)
        {
            this.url = url;
            if (WS != null)
            {
                if (WS.State == WebSocketState.Open) return;
                else WS.Dispose();
            }
            WS = new ClientWebSocket();
            CTS?.Dispose();
            CTS = new CancellationTokenSource();
            await WS.ConnectAsync(new Uri(url), CTS.Token);
            await Task.Factory.StartNew(ReceiveLoop, CTS.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            _algo.Log($"Connected successfully to: {url}");
            await SubscribeHeartbeat();
        }

        public async Task StartHealthCheck()
        {
            var cts = new CancellationTokenSource();
            await Task.Factory.StartNew(CheckConnectionHealth, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task CheckConnectionHealth()
        {
            while (true)
            {
                await Task.Delay(1000);
                if (WS == null || WS.State != WebSocketState.Open)
                {
                    _algo.Error($"Connection {WS.State}. Reconnecting...");
                    await DisconnectAsync();
                    ReleaseThread();
                    try
                    {
                        await ConnectAsync(url);
                    }
                    catch (Exception e)
                    {
                        _algo.Error($"Reconnecting failed: {e}");
                    }
                }
                else if (DateTime.Now - lastHeartbeat > TimeSpan.FromSeconds(60))
                {
                    _algo.Error($"No heartbeat received. Last at: {lastHeartbeat}. Reconnecting...");
                    DisconnectAsync().Wait(TimeSpan.FromSeconds(10));
                    ReleaseThread();
                    try
                    {
                        await ConnectAsync(url);
                    }
                    catch (Exception e)
                    {
                        _algo.Error($"Reconnecting failed: {e}");
                    }
                }
            }
        }

        public async Task DisconnectAsync()
        {
            _algo.Log("Disconnecting from WebSocket");
            if (WS is null) return;
            // TODO: requests cleanup code, sub-protocol dependent.
            if (WS.State == WebSocketState.Open)
            {
                CTS?.CancelAfter(TimeSpan.FromSeconds(2));
                _algo.Log("CancelationTocken canceled in 2 seconds");
                await WS.CloseOutputAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
                _algo.Log("CloseOutputAsync called");

                // Below line doesnt succeed. function stops here... Therefore, now awaiting it.
                _ = WS.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                _algo.Log("CloseAsync called without awaiting");
            }
            _algo.Log("WS Dispose about to be called");
            WS.Dispose();
            WS = null;
            CTS?.Dispose();
            CTS = null;
            _algo.Log("Disconnected from WebSocket");
        }

        private async Task ReceiveLoop()
        {
            _algo.Log("Starting ReceiveLoop...");
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
            //catch (TaskCanceledException e)
            //{
            //    _algo.Log($"{_algo.Time} ReceiveLoop TaskCanceledException: ${e}");
            //}
            catch (Exception e)
            {
                _algo.Error($"ReceiveLoop Exception: ${e}");
                ReleaseThread();
            }
            finally
            {
                outputStream?.Dispose();
            }
        }

        public async Task<Task> SendMessageAsync(RequestTargetPortfolios requestTargetPortfolios)
        {
            return SendMessage(new Message()
            {
                Channel = Channel.TargetPortfolio,
                Id = Guid.NewGuid().ToString(),
                Action = Action.Subscribe,
                Payload = requestTargetPortfolios.ToByteString()
            });
        }
        public async Task<Task> SendMessageAsync(RequestKalmanInit requestKalmanInit)
        {
            return SendMessage(new Message()
            {
                Channel = Channel.KalmanInit,
                Id = Guid.NewGuid().ToString(),
                Action = Action.Subscribe,
                Payload = requestKalmanInit.ToByteString()
            });
        }

        public async Task<Task> SendMessageAsync(RequestStressTestDs requestStressTestDs)
        {
            return SendMessage(new Message()
            {
                Channel = Channel.StressTestDs,
                Id = Guid.NewGuid().ToString(),
                Action = Action.Subscribe,
                Payload = requestStressTestDs.ToByteString()
            });
        }

        public async Task<Task> SubscribeHeartbeat()
        {
            _algo.Log("Subscribing to heartbeat");
            Message message = new()
            {
                Channel = Channel.Hb,
                Id = Guid.NewGuid().ToString(),
                Action  = Action.Subscribe,
                Payload = ByteString.Empty
            };
            lastHeartbeat = DateTime.Now;
            return SendMessage(message);
        }

        public async Task<Task> SendMessage(Message message)
        {
            using var buffer = new MemoryStream();
            message.WriteTo(buffer);
            return WS.SendAsync(new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), WebSocketMessageType.Binary, true, CTS.Token);
        }

        private void ResponseReceived(Stream inputStream)
        {
            // _algo.Log($"{_algo.Time} ResponseReceived");
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
                case Channel.KalmanInit:
                    HandleKalmanInit(message);
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

        private void HandleKalmanInit(Message message)
        {
            ResponseKalmanInit responseKalmanInit = ResponseKalmanInit.Parser.ParseFrom(message.Payload);
            EventHandlerResponseKalmanInit?.Invoke(this, responseKalmanInit);

        }

        public void Dispose() => DisconnectAsync().Wait();

    }
}
