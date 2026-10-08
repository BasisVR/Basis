using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public static class BasisWebSocketProbe
    {
        public static Task<ServerProbeResult> ProbeAsync(ConnectionTarget target, int timeoutMs, CancellationToken ct)
        {
            if (target == null) return Task.FromResult(new ServerProbeResult { Error = "Target is null" });
            string address = target.Get(ConnectionTarget.Keys.Address, string.Empty);
            if (string.IsNullOrWhiteSpace(address)) return Task.FromResult(new ServerProbeResult { Error = "Host is empty" });
            string portString = target.Get(ConnectionTarget.Keys.Port, LNLConnectionTargetParser.DefaultPort.ToString(CultureInfo.InvariantCulture));
            ushort.TryParse(portString, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort port);
            BasisWebSocketTransportConfig config = BasisTransportConfigStore.Get<BasisWebSocketTransportConfig>(BasisNetworkStackRegistry.WebSocketId);
            string url = BasisWebSocketNetManager.BuildUrl(address, port, config);
            IBasisWebSocketClientChannel channel = (BasisWebSocketNetManager.ClientChannelFactory ?? (() => new BasisManagedWebSocketChannel()))();
            ProbeSession session = new ProbeSession(channel, Math.Max(250, timeoutMs), ct);
            return session.Run(url);
        }

        public static byte[] BuildQueryFrame(ushort nonce)
        {
            NetDataWriter writer = new NetDataWriter(true, BasisNetworkCommons.ServerInfoMinRequestBytes);
            writer.Put(BasisNetworkCommons.ServerInfoQueryMagic);
            writer.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
            writer.Put(nonce);
            int padBytes = BasisNetworkCommons.ServerInfoMinRequestBytes - writer.Length;
            if (padBytes > 0) writer.Put(new byte[padBytes]);
            byte[] frame = new byte[BasisWebSocketProtocol.FrameSize(writer.Length)];
            int cursor = BasisWebSocketProtocol.WriteFrameHeader(frame, 0, BasisWebSocketProtocol.KindQuery, writer.Length);
            Buffer.BlockCopy(writer.Data, 0, frame, cursor, writer.Length);
            return frame;
        }

        public static bool TryReadResponse(byte[] buffer, int offset, int length, ushort nonce, out ServerProbeResult result)
        {
            result = null;
            try
            {
                NetDataReader reader = new NetDataReader(buffer, offset, offset + length);
                if (reader.AvailableBytes < 8) return false;
                if (reader.GetUInt() != BasisNetworkCommons.ServerInfoResponseMagic) return false;
                ushort protocol = reader.GetUShort();
                if (reader.GetUShort() != nonce) return false;
                ushort online = reader.GetUShort();
                ushort max = reader.GetUShort();
                string name = reader.GetString();
                string motd = reader.GetString();
                result = new ServerProbeResult
                {
                    Reachable = true,
                    Online = online,
                    Max = max,
                    ProtocolVersion = protocol,
                    Name = name,
                    Motd = motd,
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private sealed class ProbeSession : IBasisWebSocketChannelHandler, IBasisWebSocketPollable
        {
            private readonly IBasisWebSocketClientChannel _channel;
            private readonly TaskCompletionSource<ServerProbeResult> _result = new TaskCompletionSource<ServerProbeResult>();
            private readonly Stopwatch _clock = new Stopwatch();
            private readonly int _timeoutMs;
            private readonly CancellationToken _cancel;
            private readonly ushort _nonce;

            public ProbeSession(IBasisWebSocketClientChannel channel, int timeoutMs, CancellationToken cancel)
            {
                _channel = channel;
                _timeoutMs = timeoutMs;
                _cancel = cancel;
                unchecked
                {
                    _nonce = (ushort)Guid.NewGuid().GetHashCode();
                }
            }

            public Task<ServerProbeResult> Run(string url)
            {
                _clock.Start();
                if (_channel.RequiresPolling)
                {
                    BasisWebSocketPolling.Register(this);
                }
                else
                {
                    _ = TimeoutAsync();
                }
                try
                {
                    _channel.Open(url, BasisWebSocketProtocol.SubProtocol, this);
                }
                catch (Exception ex)
                {
                    Complete(new ServerProbeResult { Error = ex.Message });
                }
                return _result.Task;
            }

            private async Task TimeoutAsync()
            {
                try
                {
                    await Task.Delay(_timeoutMs, _cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                Complete(new ServerProbeResult { TimedOut = true, Error = "Timed out" });
            }

            private void Complete(ServerProbeResult result)
            {
                if (!_result.TrySetResult(result)) return;
                try
                {
                    if (result.Reachable) _ = _channel.CloseAsync();
                    else _channel.Abort();
                }
                catch
                {
                }
            }

            public void OnOpen()
            {
                byte[] frame = BuildQueryFrame(_nonce);
                try
                {
                    Task send = _channel.SendAsync(frame, 0, frame.Length);
                    if (send.IsFaulted) Complete(new ServerProbeResult { Error = "Send failed" });
                }
                catch (Exception ex)
                {
                    Complete(new ServerProbeResult { Error = ex.Message });
                }
            }

            public void OnMessage(byte[] data, int offset, int count)
            {
                int cursor = offset;
                int end = offset + count;
                while (cursor < end)
                {
                    if (!BasisWebSocketProtocol.TryReadFrame(data, ref cursor, end, out byte kind, out int bodyOffset, out int bodyLength)) return;
                    if (kind != BasisWebSocketProtocol.KindQueryResponse) continue;
                    if (TryReadResponse(data, bodyOffset, bodyLength, _nonce, out ServerProbeResult result))
                    {
                        result.RoundTripMs = (int)_clock.ElapsedMilliseconds;
                        Complete(result);
                        return;
                    }
                }
            }

            public void OnClosed(int code, string reason, bool wasClean)
            {
                Complete(new ServerProbeResult { Error = string.IsNullOrEmpty(reason) ? "Connection closed" : reason });
            }

            public void OnError(string message)
            {
            }

            public bool Poll()
            {
                _channel.Poll();
                if (!_result.Task.IsCompleted && (_clock.ElapsedMilliseconds > _timeoutMs || _cancel.IsCancellationRequested))
                {
                    Complete(new ServerProbeResult { TimedOut = true, Error = "Timed out" });
                }
                return !_result.Task.IsCompleted;
            }
        }
    }
}
