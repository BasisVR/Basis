using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public interface IBasisWebSocketPollable
    {
        bool Poll();
    }

    public static class BasisWebSocketPolling
    {
        private static readonly List<IBasisWebSocketPollable> _items = new List<IBasisWebSocketPollable>();
        private static readonly List<IBasisWebSocketPollable> _snapshot = new List<IBasisWebSocketPollable>();
        private static readonly object _lock = new object();
        private static int _count;

        public static void Register(IBasisWebSocketPollable item)
        {
            if (item == null) return;
            lock (_lock)
            {
                _items.Add(item);
                _count = _items.Count;
            }
        }

        public static int Count => Volatile.Read(ref _count);

        public static void PollAll()
        {
            if (Volatile.Read(ref _count) == 0) return;
            lock (_lock)
            {
                _snapshot.Clear();
                _snapshot.AddRange(_items);
            }
            List<IBasisWebSocketPollable> finished = null;
            foreach (IBasisWebSocketPollable item in _snapshot)
            {
                bool alive;
                try
                {
                    alive = item.Poll();
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[WebSocket] poll failed: {ex}");
                    alive = false;
                }
                if (!alive)
                {
                    if (finished == null) finished = new List<IBasisWebSocketPollable>();
                    finished.Add(item);
                }
            }
            _snapshot.Clear();
            if (finished == null) return;
            lock (_lock)
            {
                foreach (IBasisWebSocketPollable item in finished)
                {
                    _items.Remove(item);
                }
                _count = _items.Count;
            }
        }
    }

    public sealed class BasisWebSocketClientConnection : BasisWebSocketLink, IBasisWebSocketChannelHandler, IBasisWebSocketPollable
    {
        private enum ClientState
        {
            Connecting,
            AwaitingAccept,
            Connected,
            Closing,
            Closed
        }

        private readonly IBasisWebSocketClientChannel _channel;
        private readonly BasisWebSocketTransportConfig _config;
        private readonly byte[] _connectPayload;
        private readonly string _url;
        private readonly long _startedAt = Stopwatch.GetTimestamp();
        private int _state;
        private int _counted;
        private byte[] _disconnectData;
        private DisconnectReason _closingReason = DisconnectReason.DisconnectPeerCalled;

        internal BasisWebSocketClientConnection(BasisWebSocketNetManager manager, BasisWebSocketTransportConfig config, IBasisWebSocketClientChannel channel, string url, byte[] connectPayload, IPEndPoint remote) : base(manager, config)
        {
            _channel = channel;
            _config = config;
            _url = url;
            _connectPayload = connectPayload ?? Array.Empty<byte>();
            RemoteEndPoint = remote;
            Peer = new BasisWebSocketPeer(this, 0);
        }

        private ClientState State => (ClientState)Volatile.Read(ref _state);

        internal override bool IsConnected => State == ClientState.Connected;

        internal bool IsFinished => State == ClientState.Closed;

        internal string Url => _url;

        protected override bool PollsForFlush => _channel.RequiresPolling;

        internal bool RequiresPolling => _channel.RequiresPolling;

        internal void MarkCounted()
        {
            Volatile.Write(ref _counted, 1);
        }

        internal bool UnmarkCounted()
        {
            return Interlocked.Exchange(ref _counted, 0) == 1;
        }

        internal void Start()
        {
            if (_channel.RequiresPolling)
            {
                BasisWebSocketPolling.Register(this);
            }
            try
            {
                _channel.Open(_url, BasisWebSocketProtocol.SubProtocol, this);
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[WebSocket] could not open {_url}: {ex.Message}");
                Finish(DisconnectReason.ConnectionFailed);
            }
        }

        public void OnOpen()
        {
            if (Interlocked.CompareExchange(ref _state, (int)ClientState.AwaitingAccept, (int)ClientState.Connecting) != (int)ClientState.Connecting) return;
            MarkReceived();
            byte[] body = new byte[BasisWebSocketProtocol.ConnectHeaderBytes + _connectPayload.Length];
            BasisWebSocketProtocol.WriteUInt32(body, 0, BasisWebSocketProtocol.ConnectMagic);
            body[4] = BasisWebSocketProtocol.Version;
            Buffer.BlockCopy(_connectPayload, 0, body, BasisWebSocketProtocol.ConnectHeaderBytes, _connectPayload.Length);
            SendControl(BasisWebSocketProtocol.KindConnect, body, 0, body.Length);
        }

        public void OnMessage(byte[] data, int offset, int count)
        {
            MarkReceived();
            Manager.NoteReceived(count);
            int cursor = offset;
            int end = offset + count;
            while (cursor < end)
            {
                if (!BasisWebSocketProtocol.TryReadFrame(data, ref cursor, end, out byte kind, out int bodyOffset, out int bodyLength))
                {
                    BNL.LogWarning("[WebSocket] malformed frame from server; disconnecting.");
                    _channel.Abort();
                    Finish(DisconnectReason.InvalidProtocol);
                    return;
                }
                if (!HandleFrame(kind, data, bodyOffset, bodyLength)) return;
            }
        }

        private bool HandleFrame(byte kind, byte[] data, int bodyOffset, int bodyLength)
        {
            switch (kind)
            {
                case BasisWebSocketProtocol.KindAccept:
                    if (State != ClientState.AwaitingAccept || bodyLength < 4) return true;
                    RemoteId = BasisWebSocketProtocol.ReadInt32(data, bodyOffset);
                    if (bodyLength >= 12) SetRemoteTime(BasisWebSocketProtocol.ReadInt64(data, bodyOffset + 4));
                    if (Interlocked.CompareExchange(ref _state, (int)ClientState.Connected, (int)ClientState.AwaitingAccept) != (int)ClientState.AwaitingAccept) return true;
                    Manager.DeliverConnected(Peer);
                    return true;
                case BasisWebSocketProtocol.KindReject:
                    _disconnectData = Slice(data, bodyOffset, bodyLength);
                    Finish(DisconnectReason.ConnectionRejected);
                    RequestCloseAfterFlush();
                    return false;
                case BasisWebSocketProtocol.KindData:
                    if (State != ClientState.Connected || bodyLength < BasisWebSocketProtocol.DataHeaderBytes) return true;
                    byte channel = data[bodyOffset];
                    byte method = data[bodyOffset + 1];
                    if (!BasisWebSocketProtocol.IsValidMethod(method)) return true;
                    Manager.NotePacketReceived();
                    NetPacketReader reader = CopyToReader(data, bodyOffset + BasisWebSocketProtocol.DataHeaderBytes, bodyLength - BasisWebSocketProtocol.DataHeaderBytes, channel, (DeliveryMethod)method);
                    Manager.DeliverReceive(Peer, reader, channel, (DeliveryMethod)method);
                    return true;
                case BasisWebSocketProtocol.KindPing:
                    HandlePing(data, bodyOffset, bodyLength);
                    return true;
                case BasisWebSocketProtocol.KindPong:
                    HandlePong(data, bodyOffset, bodyLength);
                    return true;
                case BasisWebSocketProtocol.KindDisconnect:
                    _disconnectData = Slice(data, bodyOffset, bodyLength);
                    Finish(DisconnectReason.RemoteConnectionClose);
                    RequestCloseAfterFlush();
                    return false;
                default:
                    return true;
            }
        }

        private static byte[] Slice(byte[] data, int offset, int length)
        {
            if (length <= 0) return Array.Empty<byte>();
            byte[] copy = new byte[length];
            Buffer.BlockCopy(data, offset, copy, 0, length);
            return copy;
        }

        public void OnClosed(int code, string reason, bool wasClean)
        {
            ClientState state = State;
            DisconnectReason disconnectReason;
            switch (state)
            {
                case ClientState.Connected:
                    disconnectReason = DisconnectReason.RemoteConnectionClose;
                    break;
                case ClientState.Closing:
                    disconnectReason = _closingReason;
                    break;
                default:
                    disconnectReason = DisconnectReason.ConnectionFailed;
                    break;
            }
            Finish(disconnectReason);
        }

        public void OnError(string message)
        {
            if (State == ClientState.Closing || State == ClientState.Closed) return;
            BNL.LogWarning($"[WebSocket] {_url}: {message}");
        }

        internal override void Disconnect(byte[] data, int offset, int length, bool force)
        {
            ClientState state = State;
            if (state == ClientState.Closing || state == ClientState.Closed) return;
            if (Interlocked.CompareExchange(ref _state, (int)ClientState.Closing, (int)state) != (int)state) return;
            _closingReason = DisconnectReason.DisconnectPeerCalled;
            if (force)
            {
                _channel.Abort();
            }
            else
            {
                if (state == ClientState.Connected)
                {
                    SendControl(BasisWebSocketProtocol.KindDisconnect, data ?? Array.Empty<byte>(), offset, data == null ? 0 : length);
                }
                RequestCloseAfterFlush();
            }
            Finish(DisconnectReason.DisconnectPeerCalled);
        }

        internal void Service(long now, long pingIntervalTicks, long timeoutTicks, long connectTicks)
        {
            switch (State)
            {
                case ClientState.Connecting:
                case ClientState.AwaitingAccept:
                    if (now - _startedAt > connectTicks)
                    {
                        Interlocked.Exchange(ref _state, (int)ClientState.Closing);
                        _channel.Abort();
                        Finish(DisconnectReason.ConnectionFailed);
                    }
                    break;
                case ClientState.Connected:
                    if (now - LastReceiveTimestamp > timeoutTicks)
                    {
                        Interlocked.Exchange(ref _state, (int)ClientState.Closing);
                        _channel.Abort();
                        Finish(DisconnectReason.Timeout);
                        return;
                    }
                    ServicePing(now, pingIntervalTicks);
                    break;
            }
        }

        public bool Poll()
        {
            _channel.Poll();
            Manager.ServiceClient(this);
            FlushSynchronously();
            return !IsFinished || !Outbound.IsDrained;
        }

        protected override Task SendBatchAsync(byte[] batch, int length)
        {
            return _channel.SendAsync(batch, 0, length);
        }

        protected override Task CloseTransportAsync()
        {
            return _channel.CloseAsync();
        }

        protected override void OnTransportFailed(Exception ex)
        {
            _channel.Abort();
            Finish(DisconnectReason.ConnectionFailed);
        }

        private void Finish(DisconnectReason reason)
        {
            if (!TryClaimDisconnectEvent())
            {
                return;
            }
            Interlocked.Exchange(ref _state, (int)ClientState.Closed);
            byte[] data = _disconnectData ?? Array.Empty<byte>();
            DisconnectInfo info = new DisconnectInfo
            {
                Reason = reason,
                SocketErrorCode = System.Net.Sockets.SocketError.Success,
                AdditionalData = new NetPacketReader(data, 0, data.Length, null),
            };
            Manager.DeliverDisconnected(this, info);
        }
    }
}
