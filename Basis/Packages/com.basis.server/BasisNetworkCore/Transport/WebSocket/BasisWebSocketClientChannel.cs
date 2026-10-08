using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public interface IBasisWebSocketChannelHandler
    {
        void OnOpen();
        void OnMessage(byte[] data, int offset, int count);
        void OnClosed(int code, string reason, bool wasClean);
        void OnError(string message);
    }

    public interface IBasisWebSocketClientChannel
    {
        bool RequiresPolling { get; }
        void Open(string url, string subProtocol, IBasisWebSocketChannelHandler handler);
        Task SendAsync(byte[] buffer, int offset, int count);
        Task CloseAsync();
        void Abort();
        void Poll();
    }

    public sealed class BasisManagedWebSocketChannel : IBasisWebSocketClientChannel
    {
        private const int CloseWaitMs = 2000;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private ClientWebSocket _socket;
        private IBasisWebSocketChannelHandler _handler;
        private int _closedRaised;
        private readonly TaskCompletionSource<bool> _receiveDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool RequiresPolling => false;

        public int MaxMessageBytes = 256 * 1024 * 1024;

        public void Open(string url, string subProtocol, IBasisWebSocketChannelHandler handler)
        {
            _handler = handler;
            _socket = new ClientWebSocket();
            if (!string.IsNullOrEmpty(subProtocol))
            {
                _socket.Options.AddSubProtocol(subProtocol);
            }
            _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            _ = RunAsync(new Uri(url));
        }

        private async Task RunAsync(Uri uri)
        {
            try
            {
                await _socket.ConnectAsync(uri, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _handler.OnError(ex.Message);
                RaiseClosed(0, ex.Message, false);
                _receiveDone.TrySetResult(true);
                return;
            }
            _handler.OnOpen();
            byte[] buffer = new byte[64 * 1024];
            int count = 0;
            int code = 0;
            string reason = string.Empty;
            bool clean = false;
            try
            {
                while (true)
                {
                    if (count == buffer.Length)
                    {
                        if (buffer.Length >= MaxMessageBytes) throw new InvalidOperationException("Message too large");
                        Array.Resize(ref buffer, Math.Min(MaxMessageBytes, buffer.Length * 2));
                    }
                    ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(new Memory<byte>(buffer, count, buffer.Length - count), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        code = (int)(_socket.CloseStatus ?? WebSocketCloseStatus.Empty);
                        reason = _socket.CloseStatusDescription ?? string.Empty;
                        clean = true;
                        if (_socket.State == WebSocketState.CloseReceived)
                        {
                            try
                            {
                                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                        }
                        break;
                    }
                    count += result.Count;
                    if (!result.EndOfMessage) continue;
                    if (result.MessageType == WebSocketMessageType.Binary)
                    {
                        _handler.OnMessage(buffer, 0, count);
                    }
                    count = 0;
                    if (buffer.Length > 1024 * 1024)
                    {
                        buffer = new byte[64 * 1024];
                    }
                }
            }
            catch (OperationCanceledException)
            {
                reason = "aborted";
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                _handler.OnError(ex.Message);
            }
            finally
            {
                _receiveDone.TrySetResult(true);
                RaiseClosed(code, reason, clean);
            }
        }

        private void RaiseClosed(int code, string reason, bool clean)
        {
            if (Interlocked.Exchange(ref _closedRaised, 1) != 0) return;
            _handler.OnClosed(code, reason, clean);
        }

        public Task SendAsync(byte[] buffer, int offset, int count)
        {
            ClientWebSocket socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open) return Task.CompletedTask;
            return socket.SendAsync(new ArraySegment<byte>(buffer, offset, count), WebSocketMessageType.Binary, true, _cts.Token);
        }

        public async Task CloseAsync()
        {
            ClientWebSocket socket = _socket;
            if (socket == null) return;
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    using (CancellationTokenSource timeout = new CancellationTokenSource(CloseWaitMs))
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, timeout.Token).ConfigureAwait(false);
                    }
                }
                Task finished = await Task.WhenAny(_receiveDone.Task, Task.Delay(CloseWaitMs)).ConfigureAwait(false);
                if (finished != _receiveDone.Task) Abort();
            }
            catch
            {
                Abort();
            }
        }

        public void Abort()
        {
            try
            {
                _cts.Cancel();
            }
            catch
            {
            }
            try
            {
                _socket?.Abort();
            }
            catch
            {
            }
        }

        public void Poll()
        {
        }
    }
}
