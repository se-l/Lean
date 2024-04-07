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
        public event EventHandler<Portfolios> EventHandlerPortfolios;

        private ClientWebSocket WS;
        private CancellationTokenSource CTS;
        public int ReceiveBufferSize { get; set; } = 8192;
        public SemaphoreSlim semaphore;
        private readonly Foundations _algo;
        private string url;

        public WsClient(Foundations algo)
        {
            _algo = algo;
        }

        public void SetSemaphore(SemaphoreSlim semaphore)
        {
            this.semaphore = semaphore;
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
            if (CTS != null) CTS.Dispose();
            CTS = new CancellationTokenSource();
            await WS.ConnectAsync(new Uri(url), CTS.Token);
            await Task.Factory.StartNew(ReceiveLoop, CTS.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task DisconnectAsync()
        {
            _algo.Log("Disconnecting from WebSocket");
            semaphore.Release();
            if (WS is null) return;
            // TODO: requests cleanup code, sub-protocol dependent.
            if (WS.State == WebSocketState.Open)
            {
                CTS.CancelAfter(TimeSpan.FromSeconds(2));
                await WS.CloseOutputAsync(WebSocketCloseStatus.Empty, "", CancellationToken.None);
                await WS.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            WS.Dispose();
            WS = null;
            CTS.Dispose();
            CTS = null;
            _algo.Log("Disconnected from WebSocket");
        }

        private async Task ReceiveLoop()
        {
            var loopToken = CTS.Token;
            MemoryStream outputStream = null;
            WebSocketReceiveResult receiveResult = null;
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
                _algo.Error($"ReceiveLoop: ${e}");
                _ = ConnectAsync(url);
            }
            catch (Exception e)
            {
                _algo.Error($"ReceiveLoop Exception: ${e}");
                throw;
            }
            finally
            {
                outputStream?.Dispose();
                semaphore.Release();
            }
        }

        public async Task<Task> SendMessageAsync(RequestTargetPortfolios requestTargetPortfolios)
        {
            using var buffer = new MemoryStream();
            requestTargetPortfolios.WriteTo(buffer);
            return WS.SendAsync(new ArraySegment<byte>(buffer.GetBuffer(), 0, (int)buffer.Length), WebSocketMessageType.Binary, true, CTS.Token);
        }

        private void ResponseReceived(Stream inputStream)
        {
            _algo.Log($"{_algo.Time} ResponseReceived");
            Portfolios portfolios = Portfolios.Parser.ParseFrom(inputStream);
            if (portfolios.IsLastTransmission)
            {
                semaphore.Release();
            }
            EventHandlerPortfolios?.Invoke(this, portfolios);
            inputStream.Dispose();
        }

        public void Dispose() => DisconnectAsync().Wait();

    }
}
