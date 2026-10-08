using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketServerSession : BasisWebSocketLink
    {
        private enum SessionState
        {
            Handshaking,
            Query,
            Pending,
            Connected,
            Closing,
            Closed
        }

        private const int CloseGraceMs = 2000;
        private readonly BasisWebSocketTransportConfig _config;
        private readonly Stream _stream;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly long _createdAt = Stopwatch.GetTimestamp();
        private WebSocket _socket;
        private int _state;
        private long _closingSince;
        private BasisWebSocketConnectionRequest _request;
        private int _peerId = -1;
        private DisconnectReason _closeReason = DisconnectReason.RemoteConnectionClose;
        private byte[] _remoteDisconnectData;
        private int _queryAnswered;
        private int _resolved;
        private int _raisingRequest;

        internal BasisWebSocketServerSession(BasisWebSocketNetManager manager, BasisWebSocketTransportConfig config, Stream stream, IPEndPoint remote) : base(manager, config)
        {
            _config = config;
            _stream = stream;
            RemoteEndPoint = remote;
        }

        private SessionState State => (SessionState)Volatile.Read(ref _state);

        internal override bool IsConnected => State == SessionState.Connected;

        internal bool IsAccepted => _peerId >= 0;

        internal int PeerId => _peerId;

        internal async Task RunAsync(WebSocket socket)
        {
            _socket = socket;
            byte[] buffer = new byte[16 * 1024];
            int count = 0;
            SocketError socketError = SocketError.Success;
            try
            {
                while (true)
                {
                    if (count == buffer.Length)
                    {
                        if (buffer.Length >= _config.MaxMessageBytes)
                        {
                            BNL.LogWarning($"[WebSocket] {RemoteEndPoint} sent a message over {_config.MaxMessageBytes} bytes; closing.");
                            _closeReason = DisconnectReason.InvalidProtocol;
                            break;
                        }
                        Array.Resize(ref buffer, Math.Min(_config.MaxMessageBytes, buffer.Length * 2));
                    }
                    ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(new Memory<byte>(buffer, count, buffer.Length - count), _cts.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        if (_socket.State == WebSocketState.CloseReceived && State != SessionState.Closing)
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
                        MarkReceived();
                        Manager.NoteReceived(count);
                        if (!HandleMessage(buffer, count)) break;
                    }
                    count = 0;
                    if (buffer.Length > 256 * 1024)
                    {
                        buffer = new byte[16 * 1024];
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (WebSocketException ex)
            {
                if (State != SessionState.Closing)
                {
                    _closeReason = ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely ? DisconnectReason.RemoteConnectionClose : DisconnectReason.ConnectionFailed;
                }
                if (ex.InnerException is SocketException se) socketError = se.SocketErrorCode;
            }
            catch (IOException ex)
            {
                if (State != SessionState.Closing) _closeReason = DisconnectReason.RemoteConnectionClose;
                if (ex.InnerException is SocketException se) socketError = se.SocketErrorCode;
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[WebSocket] receive loop for {RemoteEndPoint} failed: {ex.Message}");
                _closeReason = DisconnectReason.ConnectionFailed;
            }
            finally
            {
                Finish(socketError);
            }
        }

        private bool HandleMessage(byte[] buffer, int count)
        {
            int offset = 0;
            while (offset < count)
            {
                if (!BasisWebSocketProtocol.TryReadFrame(buffer, ref offset, count, out byte kind, out int bodyOffset, out int bodyLength))
                {
                    BNL.LogWarning($"[WebSocket] malformed frame from {RemoteEndPoint}; closing.");
                    _closeReason = DisconnectReason.InvalidProtocol;
                    return false;
                }
                switch (State)
                {
                    case SessionState.Handshaking:
                        if (!HandleFirstFrame(kind, buffer, bodyOffset, bodyLength)) return false;
                        break;
                    case SessionState.Connected:
                        if (!HandleConnectedFrame(kind, buffer, bodyOffset, bodyLength)) return false;
                        break;
                    case SessionState.Pending:
                    case SessionState.Query:
                        if (kind == BasisWebSocketProtocol.KindPing) HandlePing(buffer, bodyOffset, bodyLength);
                        else if (kind == BasisWebSocketProtocol.KindDisconnect) return false;
                        break;
                    default:
                        if (kind == BasisWebSocketProtocol.KindDisconnect) return false;
                        break;
                }
            }
            return true;
        }

        private bool HandleFirstFrame(byte kind, byte[] buffer, int bodyOffset, int bodyLength)
        {
            if (kind == BasisWebSocketProtocol.KindQuery)
            {
                Volatile.Write(ref _state, (int)SessionState.Query);
                Manager.RegisterQuery(this);
                NetPacketReader reader = CopyToReader(buffer, bodyOffset, bodyLength, 255, DeliveryMethod.Unreliable);
                try
                {
                    Manager.Listener.RaiseNetworkReceiveUnconnected(RemoteEndPoint, reader);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[WebSocket] unconnected handler threw: {ex.Message}");
                }
                return true;
            }
            if (kind != BasisWebSocketProtocol.KindConnect || bodyLength < BasisWebSocketProtocol.ConnectHeaderBytes)
            {
                _closeReason = DisconnectReason.InvalidProtocol;
                return false;
            }
            uint magic = BasisWebSocketProtocol.ReadUInt32(buffer, bodyOffset);
            byte version = buffer[bodyOffset + 4];
            if (magic != BasisWebSocketProtocol.ConnectMagic || version != BasisWebSocketProtocol.Version)
            {
                BNL.LogWarning($"[WebSocket] {RemoteEndPoint} spoke an unknown protocol (magic {magic:X8}, version {version}); closing.");
                _closeReason = DisconnectReason.InvalidProtocol;
                return false;
            }
            int payloadOffset = bodyOffset + BasisWebSocketProtocol.ConnectHeaderBytes;
            int payloadLength = bodyLength - BasisWebSocketProtocol.ConnectHeaderBytes;
            byte[] payload = new byte[payloadLength];
            Buffer.BlockCopy(buffer, payloadOffset, payload, 0, payloadLength);
            _request = new BasisWebSocketConnectionRequest(this, payload);
            Volatile.Write(ref _state, (int)SessionState.Pending);
            Volatile.Write(ref _raisingRequest, 1);
            try
            {
                Manager.Listener.RaiseConnectionRequest(_request);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[WebSocket] connection request handler threw: {ex.Message}");
                _request.RejectWithData(null, 0, 0);
            }
            finally
            {
                Volatile.Write(ref _raisingRequest, 0);
            }
            if (State == SessionState.Connected)
            {
                RaisePeerConnected();
            }
            return true;
        }

        private void RaisePeerConnected()
        {
            try
            {
                Manager.Listener.RaisePeerConnected(Peer);
            }
            catch (Exception ex)
            {
                BNL.LogError($"[WebSocket] peer connected handler threw: {ex.Message}");
            }
        }

        private bool HandleConnectedFrame(byte kind, byte[] buffer, int bodyOffset, int bodyLength)
        {
            switch (kind)
            {
                case BasisWebSocketProtocol.KindData:
                    if (bodyLength < BasisWebSocketProtocol.DataHeaderBytes) return false;
                    byte channel = buffer[bodyOffset];
                    byte method = buffer[bodyOffset + 1];
                    if (!BasisWebSocketProtocol.IsValidMethod(method) || channel >= BasisNetworkCommons.TotalChannels)
                    {
                        _closeReason = DisconnectReason.InvalidProtocol;
                        return false;
                    }
                    NetPacketReader reader = CopyToReader(buffer, bodyOffset + BasisWebSocketProtocol.DataHeaderBytes, bodyLength - BasisWebSocketProtocol.DataHeaderBytes, channel, (DeliveryMethod)method);
                    Manager.NotePacketReceived();
                    try
                    {
                        Manager.Listener.RaiseNetworkReceive(Peer, reader, channel, (DeliveryMethod)method);
                    }
                    catch (Exception ex)
                    {
                        BNL.LogError($"[WebSocket] receive handler threw for {Peer}: {ex}");
                    }
                    return true;
                case BasisWebSocketProtocol.KindPing:
                    HandlePing(buffer, bodyOffset, bodyLength);
                    return true;
                case BasisWebSocketProtocol.KindPong:
                    HandlePong(buffer, bodyOffset, bodyLength);
                    return true;
                case BasisWebSocketProtocol.KindDisconnect:
                    _remoteDisconnectData = new byte[bodyLength];
                    Buffer.BlockCopy(buffer, bodyOffset, _remoteDisconnectData, 0, bodyLength);
                    _closeReason = DisconnectReason.RemoteConnectionClose;
                    Volatile.Write(ref _state, (int)SessionState.Closing);
                    RequestCloseAfterFlush();
                    return true;
                default:
                    return true;
            }
        }

        internal BasisWebSocketPeer Accept()
        {
            if (State != SessionState.Pending || Interlocked.Exchange(ref _resolved, 1) != 0)
            {
                return Peer;
            }
            _peerId = Manager.AllocatePeerId();
            RemoteId = _peerId;
            Peer = new BasisWebSocketPeer(this, _peerId);
            byte[] body = new byte[BasisWebSocketProtocol.AcceptBodyBytes];
            BasisWebSocketProtocol.WriteInt32(body, 0, _peerId);
            BasisWebSocketProtocol.WriteInt64(body, 4, DateTime.UtcNow.Ticks);
            BasisWebSocketProtocol.WriteInt32(body, 12, _config.DisconnectTimeout);
            SendControl(BasisWebSocketProtocol.KindAccept, body, 0, body.Length);
            if (Interlocked.CompareExchange(ref _state, (int)SessionState.Connected, (int)SessionState.Pending) == (int)SessionState.Pending)
            {
                Manager.OnAccepted(this);
                if (Volatile.Read(ref _raisingRequest) == 0)
                {
                    RaisePeerConnected();
                }
            }
            return Peer;
        }

        internal void Reject(byte[] data, int offset, int length)
        {
            if (State != SessionState.Pending || Interlocked.Exchange(ref _resolved, 1) != 0)
            {
                return;
            }
            Volatile.Write(ref _state, (int)SessionState.Closing);
            Interlocked.Exchange(ref _closingSince, Stopwatch.GetTimestamp());
            _closeReason = DisconnectReason.ConnectionRejected;
            SendControl(BasisWebSocketProtocol.KindReject, data ?? Array.Empty<byte>(), offset, data == null ? 0 : length);
            RequestCloseAfterFlush();
        }

        internal bool AnswerQuery(byte[] data, int offset, int length)
        {
            if (State != SessionState.Query) return false;
            if (Interlocked.Exchange(ref _queryAnswered, 1) != 0) return false;
            SendControl(BasisWebSocketProtocol.KindQueryResponse, data, offset, length);
            Volatile.Write(ref _state, (int)SessionState.Closing);
            Interlocked.Exchange(ref _closingSince, Stopwatch.GetTimestamp());
            RequestCloseAfterFlush();
            return true;
        }

        internal override void Disconnect(byte[] data, int offset, int length, bool force)
        {
            SessionState state = State;
            if (state == SessionState.Closed) return;
            if (state == SessionState.Pending)
            {
                Reject(data, offset, length);
                return;
            }
            if (Interlocked.CompareExchange(ref _state, (int)SessionState.Closing, (int)state) != (int)state) return;
            Interlocked.Exchange(ref _closingSince, Stopwatch.GetTimestamp());
            _closeReason = DisconnectReason.DisconnectPeerCalled;
            if (force)
            {
                Abort();
                return;
            }
            if (state == SessionState.Connected)
            {
                SendControl(BasisWebSocketProtocol.KindDisconnect, data ?? Array.Empty<byte>(), offset, data == null ? 0 : length);
            }
            RequestCloseAfterFlush();
        }

        internal void Service(long now, long pingIntervalTicks, long timeoutTicks, long handshakeTicks)
        {
            switch (State)
            {
                case SessionState.Connected:
                    if (now - LastReceiveTimestamp > timeoutTicks)
                    {
                        _closeReason = DisconnectReason.Timeout;
                        Volatile.Write(ref _state, (int)SessionState.Closing);
                        Interlocked.Exchange(ref _closingSince, now);
                        Abort();
                        return;
                    }
                    ServicePing(now, pingIntervalTicks);
                    break;
                case SessionState.Handshaking:
                case SessionState.Query:
                    if (now - _createdAt > handshakeTicks)
                    {
                        _closeReason = DisconnectReason.Timeout;
                        Volatile.Write(ref _state, (int)SessionState.Closing);
                        Interlocked.Exchange(ref _closingSince, now);
                        Abort();
                    }
                    break;
                case SessionState.Pending:
                    if (now - _createdAt > handshakeTicks)
                    {
                        BNL.LogWarning($"[WebSocket] connection request from {RemoteEndPoint} was never answered; rejecting it.");
                        Reject(null, 0, 0);
                    }
                    break;
                case SessionState.Closing:
                    if (now - Interlocked.Read(ref _closingSince) > CloseGraceMs * Stopwatch.Frequency / 1000)
                    {
                        Abort();
                    }
                    break;
            }
        }

        protected override Task SendBatchAsync(byte[] batch, int length)
        {
            WebSocket socket = _socket;
            if (socket == null) return Task.CompletedTask;
            return socket.SendAsync(new ArraySegment<byte>(batch, 0, length), WebSocketMessageType.Binary, true, _cts.Token);
        }

        protected override async Task CloseTransportAsync()
        {
            WebSocket socket = _socket;
            if (socket == null) return;
            Interlocked.CompareExchange(ref _closingSince, Stopwatch.GetTimestamp(), 0);
            try
            {
                if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, string.Empty, _cts.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                Abort();
            }
        }

        protected override void OnTransportFailed(Exception ex)
        {
            if (State != SessionState.Closing && State != SessionState.Closed)
            {
                _closeReason = DisconnectReason.ConnectionFailed;
            }
            Abort();
        }

        internal void Abort()
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
            try
            {
                _stream.Dispose();
            }
            catch
            {
            }
        }

        private void Finish(SocketError socketError)
        {
            SessionState previous = (SessionState)Interlocked.Exchange(ref _state, (int)SessionState.Closed);
            Outbound.Close();
            Abort();
            Manager.OnSessionFinished(this);
            if (_peerId >= 0 && TryClaimDisconnectEvent())
            {
                byte[] data = _remoteDisconnectData ?? Array.Empty<byte>();
                DisconnectInfo info = new DisconnectInfo
                {
                    Reason = _closeReason,
                    SocketErrorCode = socketError,
                    AdditionalData = new NetPacketReader(data, 0, data.Length, null),
                };
                try
                {
                    Manager.Listener.RaisePeerDisconnected(Peer, info);
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[WebSocket] disconnect handler threw for {Peer}: {ex.Message}");
                }
                Manager.ReleasePeerId(_peerId);
            }
        }
    }

    public sealed class BasisWebSocketConnectionRequest : ConnectionRequest
    {
        private readonly BasisWebSocketServerSession _session;
        private readonly NetDataReader _data;

        internal BasisWebSocketConnectionRequest(BasisWebSocketServerSession session, byte[] payload)
        {
            _session = session;
            _data = new NetDataReader(payload);
        }

        public NetDataReader Data => _data;

        public IPEndPoint RemoteEndPoint => _session.RemoteEndPoint;

        public NetPeer Accept()
        {
            return _session.Accept();
        }

        public void Reject(NetDataWriter w)
        {
            if (w == null)
            {
                _session.Reject(null, 0, 0);
                return;
            }
            _session.Reject(w.Data, 0, w.Length);
        }

        internal void RejectWithData(byte[] data, int offset, int length)
        {
            _session.Reject(data, offset, length);
        }
    }
}
