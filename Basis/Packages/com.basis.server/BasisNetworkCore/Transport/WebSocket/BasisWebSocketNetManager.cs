using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace Basis.Network.Core
{
    public sealed class BasisWebSocketNetManager : NetManager, IBasisSharedPeerIds
    {
        public static Func<IBasisWebSocketClientChannel> ClientChannelFactory = () => new BasisManagedWebSocketChannel();
        private const int ServiceIntervalMs = 250;
        private const int CertificateCheckIntervalMs = 60000;
        private const int ListenBacklog = 512;
        private static readonly bool[] PriorityChannels = BuildPriority();
        private readonly EventBasedNetListener _listener;
        private readonly BasisWebSocketTransportConfig _config;
        private readonly ConcurrentDictionary<BasisWebSocketServerSession, byte> _sessions = new ConcurrentDictionary<BasisWebSocketServerSession, byte>();
        private readonly ConcurrentDictionary<IPEndPoint, BasisWebSocketServerSession> _queries = new ConcurrentDictionary<IPEndPoint, BasisWebSocketServerSession>();
        private readonly List<BasisWebSocketClientConnection> _clients = new List<BasisWebSocketClientConnection>();
        private readonly List<TcpListener> _tcpListeners = new List<TcpListener>();
        private readonly ConcurrentQueue<PendingEvent> _manualEvents = new ConcurrentQueue<PendingEvent>();
        private readonly object _lifecycleLock = new object();
        private BasisPeerIdAllocator _peerIds = new BasisPeerIdAllocator();
        private bool _ownsPeerIds = true;
        private CancellationTokenSource _lifetime;
        private volatile bool _running;
        private volatile bool _listening;
        private volatile bool _manualMode;
        private int _serviceLoopStarted;
        private BasisTrustedProxyList _proxies = new BasisTrustedProxyList(string.Empty);
        private X509Certificate2 _certificate;
        private string _certificatePath;
        private DateTime _certificateStamp;
        private long _lastCertificateCheck;
        private int _listenPort;
        private int _pendingHandshakes;
        private int _connectedCount;
        private long _packetsSent;
        private long _packetsReceived;
        private long _bytesSent;
        private long _bytesReceived;
        private long _dropped;
        private long _priorityDropped;

        private struct PendingEvent
        {
            public byte Type;
            public BasisWebSocketPeer Peer;
            public NetPacketReader Reader;
            public byte Channel;
            public DeliveryMethod Method;
            public DisconnectInfo Info;
        }

        public BasisWebSocketNetManager(EventBasedNetListener listener, Configuration configuration) : this(listener, BasisTransportConfigStore.Get<BasisWebSocketTransportConfig>(BasisNetworkStackRegistry.WebSocketId))
        {
        }

        public BasisWebSocketNetManager(EventBasedNetListener listener, BasisWebSocketTransportConfig config)
        {
            _listener = listener ?? throw new ArgumentNullException(nameof(listener));
            _config = config ?? new BasisWebSocketTransportConfig();
        }

        internal EventBasedNetListener Listener => _listener;

        public BasisWebSocketTransportConfig Config => _config;

        public int ListenPort => _listenPort;

        public bool IsTls => _certificate != null;

        public string StackId => BasisNetworkStackRegistry.WebSocketId;

        public bool IsRunning => _running;

        public string ListenDescription => _listening ? $"TCP port {_listenPort} (WebSocket{(_certificate != null ? ", TLS" : string.Empty)})" : null;

        private static bool[] BuildPriority()
        {
            bool[] map = new bool[BasisWebSocketProtocol.ChannelCount];
            bool[] source = BasisNetworkCommons.BuildPriorityUnreliableChannelMap();
            Array.Copy(source, map, Math.Min(source.Length, map.Length));
            return map;
        }

        public void UsePeerIdAllocator(BasisPeerIdAllocator allocator)
        {
            _peerIds = allocator ?? new BasisPeerIdAllocator();
            _ownsPeerIds = allocator == null;
        }

        internal int AllocatePeerId() => _peerIds.Allocate();

        internal void ReleasePeerId(int id) => _peerIds.Release(id);

        public void Start(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            StartCore(IPv4Address, IPv6Address, SetPort, false);
        }

        public void StartManual(IPAddress IPv4Address, IPAddress IPv6Address, int SetPort)
        {
            StartCore(IPv4Address, IPv6Address, SetPort, true);
        }

        private void StartCore(IPAddress ipv4, IPAddress ipv6, int port, bool manual)
        {
            lock (_lifecycleLock)
            {
                if (_running) return;
                _manualMode = manual;
                _lifetime = new CancellationTokenSource();
                _running = true;
                int listenPort = port > 0 ? (_config.Port > 0 ? _config.Port : port) : 0;
                if (listenPort > 0)
                {
                    if (!StartListening(ipv4, ipv6, listenPort))
                    {
                        _running = false;
                        _lifetime.Cancel();
                        return;
                    }
                }
                if (_listening && !manual)
                {
                    EnsureServiceLoop();
                }
            }
        }

        private bool StartListening(IPAddress ipv4, IPAddress ipv6, int port)
        {
            _proxies = new BasisTrustedProxyList(_config.TrustedProxies);
            if (_config.TlsEnabled && !LoadCertificate())
            {
                return false;
            }
            bool ipv4Bound = ipv4 == null;
            if (ipv4 != null)
            {
                try
                {
                    TcpListener listener = new TcpListener(ipv4, port);
                    listener.Start(ListenBacklog);
                    _tcpListeners.Add(listener);
                    ipv4Bound = true;
                }
                catch (SocketException ex)
                {
                    BNL.LogError($"[WebSocket] could not bind TCP {ipv4}:{port}: {ex.SocketErrorCode}. Another process may already be using it.");
                }
            }
            if (ipv6 != null && Socket.OSSupportsIPv6)
            {
                try
                {
                    TcpListener listener = new TcpListener(ipv6, port);
                    listener.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                    listener.Start(ListenBacklog);
                    _tcpListeners.Add(listener);
                }
                catch (SocketException ex)
                {
                    BNL.LogWarning($"[WebSocket] could not bind TCP [{ipv6}]:{port}: {ex.SocketErrorCode}; continuing without IPv6.");
                }
            }
            if (!ipv4Bound || _tcpListeners.Count == 0)
            {
                StopListeners();
                return false;
            }
            _listenPort = port;
            _listening = true;
            CancellationToken token = _lifetime.Token;
            foreach (TcpListener listener in _tcpListeners)
            {
                TcpListener captured = listener;
                Task.Run(() => AcceptLoopAsync(captured, token));
            }
            return true;
        }

        private string ResolvePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path)) return path;
            string fromBase = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path);
            if (File.Exists(fromBase)) return fromBase;
            string fromConfig = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Configuration.ConfigFolderName, path);
            return File.Exists(fromConfig) ? fromConfig : fromBase;
        }

        private bool LoadCertificate()
        {
            string path = ResolvePath(_config.TlsCertificatePath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                BNL.LogError($"[WebSocket] TlsEnabled is set but the certificate '{_config.TlsCertificatePath}' was not found.");
                return false;
            }
            try
            {
                X509Certificate2 certificate = ReadCertificate(path, ResolvePath(_config.TlsKeyPath), _config.TlsCertificatePassword);
                X509Certificate2 previous = _certificate;
                _certificate = certificate;
                _certificatePath = path;
                _certificateStamp = File.GetLastWriteTimeUtc(path);
                if (previous != null)
                {
                    BNL.Log($"[WebSocket] reloaded TLS certificate {certificate.Subject} (expires {certificate.NotAfter:yyyy-MM-dd}).");
                }
                else
                {
                    BNL.Log($"[WebSocket] using TLS certificate {certificate.Subject} (expires {certificate.NotAfter:yyyy-MM-dd}).");
                }
                return true;
            }
            catch (Exception ex)
            {
                BNL.LogError($"[WebSocket] could not load TLS certificate '{path}': {ex.Message}");
                return false;
            }
        }

        private static X509Certificate2 ReadCertificate(string path, string keyPath, string password)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".pfx" || extension == ".p12")
            {
#if NET9_0_OR_GREATER
                return X509CertificateLoader.LoadPkcs12FromFile(path, string.IsNullOrEmpty(password) ? null : password);
#else
                return new X509Certificate2(path, string.IsNullOrEmpty(password) ? null : password);
#endif
            }
#if NET5_0_OR_GREATER
            string key = string.IsNullOrEmpty(keyPath) ? null : keyPath;
            X509Certificate2 pem = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPemFile(path, key)
                : X509Certificate2.CreateFromEncryptedPemFile(path, password, key);
            if (!OperatingSystem.IsWindows()) return pem;
            byte[] exported = pem.Export(X509ContentType.Pkcs12);
            pem.Dispose();
#if NET9_0_OR_GREATER
            return X509CertificateLoader.LoadPkcs12(exported, null);
#else
            return new X509Certificate2(exported);
#endif
#else
            throw new NotSupportedException("PEM certificates need the standalone server build; use a .pfx file here.");
#endif
        }

        private void MaybeReloadCertificate(long now)
        {
            if (_certificate == null || string.IsNullOrEmpty(_certificatePath)) return;
            if (now - _lastCertificateCheck < CertificateCheckIntervalMs * Stopwatch.Frequency / 1000) return;
            _lastCertificateCheck = now;
            try
            {
                DateTime stamp = File.GetLastWriteTimeUtc(_certificatePath);
                if (stamp != _certificateStamp)
                {
                    LoadCertificate();
                }
            }
            catch (Exception ex)
            {
                BNL.LogWarning($"[WebSocket] certificate check failed: {ex.Message}");
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptSocketAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (InvalidOperationException)
                {
                    return;
                }
                catch (SocketException ex)
                {
                    if (token.IsCancellationRequested) return;
                    BNL.LogWarning($"[WebSocket] accept failed: {ex.SocketErrorCode}");
                    continue;
                }
                if (Interlocked.Increment(ref _pendingHandshakes) > Math.Max(1, _config.MaxPendingHandshakes))
                {
                    Interlocked.Decrement(ref _pendingHandshakes);
                    try
                    {
                        socket.Dispose();
                    }
                    catch
                    {
                    }
                    continue;
                }
                Socket accepted = socket;
                _ = Task.Run(() => HandleSocketAsync(accepted, token));
            }
        }

        private async Task HandleSocketAsync(Socket socket, CancellationToken lifetime)
        {
            bool handshakeCounted = true;
            Stream stream = null;
            BasisWebSocketServerSession session = null;
            WebSocket webSocket = null;
            try
            {
                socket.NoDelay = true;
                if (_config.SocketSendBufferBytes > 0)
                {
                    socket.SendBufferSize = _config.SocketSendBufferBytes;
                }
                IPEndPoint immediate = (IPEndPoint)socket.RemoteEndPoint;
                stream = new NetworkStream(socket, true);
                using (CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
                {
                    handshake.CancelAfter(Math.Max(1000, _config.HandshakeTimeoutMs));
                    Stream timeoutTarget = stream;
                    using (handshake.Token.Register(() =>
                    {
                        try
                        {
                            timeoutTarget.Dispose();
                        }
                        catch
                        {
                        }
                    }))
                    {
                        X509Certificate2 certificate = _certificate;
                        if (certificate != null)
                        {
                            SslStream ssl = new SslStream(stream, false);
                            stream = ssl;
                            timeoutTarget = ssl;
                            await ssl.AuthenticateAsServerAsync(certificate, false, SslProtocols.None, false).ConfigureAwait(false);
                        }
                        BasisWebSocketHandshake.Request request = await BasisWebSocketHandshake.ReadRequestAsync(stream, handshake.Token).ConfigureAwait(false);
                        if (request == null)
                        {
                            stream.Dispose();
                            return;
                        }
                        byte[] refusal = Validate(request);
                        if (refusal != null)
                        {
                            await stream.WriteAsync(refusal, 0, refusal.Length, handshake.Token).ConfigureAwait(false);
                            await stream.FlushAsync(handshake.Token).ConfigureAwait(false);
                            stream.Dispose();
                            return;
                        }
                        string protocol = request.OffersProtocol(BasisWebSocketProtocol.SubProtocol) ? BasisWebSocketProtocol.SubProtocol : null;
                        byte[] response = BasisWebSocketHandshake.BuildSwitchingProtocols(BasisWebSocketHandshake.ComputeAccept(request.Header("Sec-WebSocket-Key")), protocol);
                        await stream.WriteAsync(response, 0, response.Length, handshake.Token).ConfigureAwait(false);
                        await stream.FlushAsync(handshake.Token).ConfigureAwait(false);
                        IPAddress client = BasisWebSocketHandshake.ResolveClientAddress(immediate.Address, request, _proxies);
                        session = new BasisWebSocketServerSession(this, _config, stream, new IPEndPoint(client, immediate.Port));
                        webSocket = WebSocket.CreateFromStream(stream, true, protocol, TimeSpan.Zero);
                    }
                }
                Interlocked.Decrement(ref _pendingHandshakes);
                handshakeCounted = false;
                if (!_running)
                {
                    webSocket.Abort();
                    stream.Dispose();
                    return;
                }
                _sessions.TryAdd(session, 0);
                await session.RunAsync(webSocket).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (session == null)
                {
                    try
                    {
                        if (stream != null) stream.Dispose();
                        else socket.Dispose();
                    }
                    catch
                    {
                    }
                    if (!(ex is OperationCanceledException) && !(ex is ObjectDisposedException) && !(ex is IOException) && !(ex is AuthenticationException))
                    {
                        BNL.LogWarning($"[WebSocket] handshake failed: {ex.Message}");
                    }
                }
                else
                {
                    BNL.LogWarning($"[WebSocket] session for {session.RemoteEndPoint} ended with an error: {ex.Message}");
                }
            }
            finally
            {
                if (handshakeCounted)
                {
                    Interlocked.Decrement(ref _pendingHandshakes);
                }
            }
        }

        private byte[] Validate(BasisWebSocketHandshake.Request request)
        {
            if (!request.IsWebSocketUpgrade)
            {
                return BasisWebSocketHandshake.BuildPlainResponse(426, "Upgrade Required", "This is a Basis server. Connect to it with a Basis client.");
            }
            if (!BasisWebSocketHandshake.PathMatches(_config.Path, request.Path))
            {
                return BasisWebSocketHandshake.BuildPlainResponse(404, "Not Found", "Not found.");
            }
            if (!BasisWebSocketHandshake.OriginAllowed(_config.AllowedOrigins, request.Header("Origin")))
            {
                return BasisWebSocketHandshake.BuildPlainResponse(403, "Forbidden", "This origin may not connect to this server.");
            }
            if (!string.Equals(request.Header("Sec-WebSocket-Version"), "13", StringComparison.Ordinal))
            {
                return BasisWebSocketHandshake.BuildPlainResponse(426, "Upgrade Required", "Unsupported WebSocket version.");
            }
            return null;
        }

        private void EnsureServiceLoop()
        {
            if (Interlocked.Exchange(ref _serviceLoopStarted, 1) != 0) return;
            CancellationToken token = _lifetime.Token;
            Task.Run(() => ServiceLoopAsync(token));
        }

        private async Task ServiceLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ServiceIntervalMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                try
                {
                    Service();
                }
                catch (Exception ex)
                {
                    BNL.LogError($"[WebSocket] service pass failed: {ex}");
                }
            }
            Interlocked.Exchange(ref _serviceLoopStarted, 0);
        }

        private long PingTicks => Math.Max(100, _config.PingInterval) * Stopwatch.Frequency / 1000;

        private long TimeoutTicks => Math.Max(1000, _config.DisconnectTimeout) * Stopwatch.Frequency / 1000;

        private long HandshakeTicks => Math.Max(1000, _config.HandshakeTimeoutMs) * Stopwatch.Frequency / 1000;

        private long ConnectTicks => Math.Max(1000, _config.ConnectTimeoutMs) * Stopwatch.Frequency / 1000;

        private void Service()
        {
            long now = Stopwatch.GetTimestamp();
            long ping = PingTicks;
            long timeout = TimeoutTicks;
            long handshake = HandshakeTicks;
            foreach (KeyValuePair<BasisWebSocketServerSession, byte> entry in _sessions)
            {
                entry.Key.Service(now, ping, timeout, handshake);
            }
            BasisWebSocketClientConnection[] clients = null;
            lock (_clients)
            {
                if (_clients.Count > 0) clients = _clients.ToArray();
            }
            if (clients != null)
            {
                long connect = ConnectTicks;
                foreach (BasisWebSocketClientConnection client in clients)
                {
                    if (!client.RequiresPolling)
                    {
                        client.Service(now, ping, timeout, connect);
                    }
                }
            }
            MaybeReloadCertificate(now);
        }

        internal void ServiceClient(BasisWebSocketClientConnection client)
        {
            client.Service(Stopwatch.GetTimestamp(), PingTicks, TimeoutTicks, ConnectTicks);
        }

        public void PollEvents()
        {
            while (_manualEvents.TryDequeue(out PendingEvent pending))
            {
                Dispatch(pending);
            }
        }

        public void ManualUpdate(float elapsedMilliseconds)
        {
            Service();
        }

        public void Stop()
        {
            lock (_lifecycleLock)
            {
                if (!_running && !_listening) return;
                _running = false;
                StopListeners();
                foreach (KeyValuePair<BasisWebSocketServerSession, byte> entry in _sessions)
                {
                    entry.Key.Disconnect(null, 0, 0, false);
                }
                BasisWebSocketClientConnection[] clients;
                lock (_clients)
                {
                    clients = _clients.ToArray();
                }
                foreach (BasisWebSocketClientConnection client in clients)
                {
                    client.Disconnect(null, 0, 0, false);
                }
                CancellationTokenSource lifetime = _lifetime;
                if (_sessions.Count > 0)
                {
                    BasisWebSocketServerSession[] remaining = new BasisWebSocketServerSession[_sessions.Count];
                    _sessions.Keys.CopyTo(remaining, 0);
                    Task.Run(async () =>
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        foreach (BasisWebSocketServerSession session in remaining)
                        {
                            session.Abort();
                        }
                        lifetime?.Cancel();
                    });
                }
                else
                {
                    lifetime?.Cancel();
                }
                _queries.Clear();
                if (_ownsPeerIds)
                {
                    _peerIds.Reset();
                }
            }
        }

        private void StopListeners()
        {
            foreach (TcpListener listener in _tcpListeners)
            {
                try
                {
                    listener.Stop();
                }
                catch
                {
                }
            }
            _tcpListeners.Clear();
            _listening = false;
        }

        public NetPeer Connect(string sIP, int port, NetDataWriter Writer)
        {
            if (!_running)
            {
                StartCore(null, null, 0, _manualMode);
            }
            string url = BuildUrl(sIP, port, _config);
            byte[] payload = Writer == null ? Array.Empty<byte>() : Writer.CopyData();
            IPEndPoint remote = new IPEndPoint(IPAddress.TryParse(HostOf(sIP), out IPAddress parsed) ? parsed : IPAddress.None, port > 0 ? port : 0);
            IBasisWebSocketClientChannel channel = (ClientChannelFactory ?? (() => new BasisManagedWebSocketChannel()))();
            BasisWebSocketClientConnection connection = new BasisWebSocketClientConnection(this, _config, channel, url, payload, remote);
            lock (_clients)
            {
                _clients.Add(connection);
            }
            if (!connection.RequiresPolling && !_manualMode)
            {
                EnsureServiceLoop();
            }
            connection.Start();
            return connection.Peer;
        }

        public static string BuildUrl(string address, int port, BasisWebSocketTransportConfig config)
        {
            string trimmed = (address ?? string.Empty).Trim();
            if (trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return "ws://" + trimmed.Substring(7);
            }
            if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return "wss://" + trimmed.Substring(8);
            }
            string host = IPAddress.TryParse(trimmed, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + trimmed + "]" : trimmed;
            string scheme = config != null && config.ClientUseTls ? "wss" : "ws";
            string path = config == null || string.IsNullOrEmpty(config.Path) || config.Path == "*" ? "/" : (config.Path.StartsWith("/", StringComparison.Ordinal) ? config.Path : "/" + config.Path);
            return port > 0 ? $"{scheme}://{host}:{port}{path}" : $"{scheme}://{host}{path}";
        }

        private static string HostOf(string address)
        {
            string text = (address ?? string.Empty).Trim();
            int scheme = text.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                text = text.Substring(scheme + 3);
                int slash = text.IndexOf('/');
                if (slash >= 0) text = text.Substring(0, slash);
                if (text.StartsWith("[", StringComparison.Ordinal))
                {
                    int close = text.IndexOf(']');
                    return close > 0 ? text.Substring(1, close - 1) : text;
                }
                int colon = text.LastIndexOf(':');
                if (colon > 0) text = text.Substring(0, colon);
            }
            return text;
        }

        public bool SendUnconnectedMessage(NetDataWriter writer, IPEndPoint remoteEndPoint)
        {
            if (writer == null || remoteEndPoint == null) return false;
            if (!_queries.TryGetValue(remoteEndPoint, out BasisWebSocketServerSession session)) return false;
            return session.AnswerQuery(writer.Data, 0, writer.Length);
        }

        internal void RegisterQuery(BasisWebSocketServerSession session)
        {
            if (session.RemoteEndPoint != null)
            {
                _queries[session.RemoteEndPoint] = session;
            }
        }

        internal void OnAccepted(BasisWebSocketServerSession session)
        {
            Interlocked.Increment(ref _connectedCount);
        }

        internal void OnSessionFinished(BasisWebSocketServerSession session)
        {
            if (_sessions.TryRemove(session, out _) && session.IsAccepted)
            {
                Interlocked.Decrement(ref _connectedCount);
            }
            if (session.RemoteEndPoint != null && _queries.TryGetValue(session.RemoteEndPoint, out BasisWebSocketServerSession registered) && ReferenceEquals(registered, session))
            {
                ((ICollection<KeyValuePair<IPEndPoint, BasisWebSocketServerSession>>)_queries).Remove(new KeyValuePair<IPEndPoint, BasisWebSocketServerSession>(session.RemoteEndPoint, session));
            }
        }

        internal void DeliverConnected(BasisWebSocketPeer peer)
        {
            Interlocked.Increment(ref _connectedCount);
            if (peer.Link is BasisWebSocketClientConnection connection)
            {
                connection.MarkCounted();
            }
            Deliver(new PendingEvent { Type = 0, Peer = peer });
        }

        internal void DeliverReceive(BasisWebSocketPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            Deliver(new PendingEvent { Type = 1, Peer = peer, Reader = reader, Channel = channel, Method = method });
        }

        internal void DeliverDisconnected(BasisWebSocketClientConnection connection, DisconnectInfo info)
        {
            if (connection.UnmarkCounted())
            {
                Interlocked.Decrement(ref _connectedCount);
            }
            lock (_clients)
            {
                _clients.Remove(connection);
            }
            Deliver(new PendingEvent { Type = 2, Peer = connection.Peer, Info = info });
        }

        private void Deliver(PendingEvent pending)
        {
            if (_manualMode)
            {
                _manualEvents.Enqueue(pending);
                return;
            }
            Dispatch(pending);
        }

        private void Dispatch(PendingEvent pending)
        {
            try
            {
                switch (pending.Type)
                {
                    case 0:
                        _listener.RaisePeerConnected(pending.Peer);
                        break;
                    case 1:
                        _listener.RaiseNetworkReceive(pending.Peer, pending.Reader, pending.Channel, pending.Method);
                        break;
                    case 2:
                        _listener.RaisePeerDisconnected(pending.Peer, pending.Info);
                        break;
                }
            }
            catch (Exception ex)
            {
                BNL.LogError($"[WebSocket] event handler threw: {ex}");
            }
        }

        internal void NoteSent(int bytes)
        {
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, bytes);
        }

        internal void NoteReceived(int bytes)
        {
            Interlocked.Add(ref _bytesReceived, bytes);
        }

        internal void NotePacketReceived()
        {
            Interlocked.Increment(ref _packetsReceived);
        }

        internal void NoteDropped(byte channel)
        {
            if (PriorityChannels[channel]) Interlocked.Increment(ref _priorityDropped);
            else Interlocked.Increment(ref _dropped);
        }

        public NetStatistics Statistics => new NetStatistics
        {
            PacketsSent = Interlocked.Read(ref _packetsSent),
            PacketsReceived = Interlocked.Read(ref _packetsReceived),
            BytesSent = Interlocked.Read(ref _bytesSent),
            BytesReceived = Interlocked.Read(ref _bytesReceived),
            PacketLoss = 0,
        };

        public int ConnectedPeersCount => Math.Max(0, Volatile.Read(ref _connectedCount));

        public long UnreliableDropped => Interlocked.Read(ref _dropped);

        public long PriorityUnreliableDropped => Interlocked.Read(ref _priorityDropped);

        public int PendingHandshakes => Math.Max(0, Volatile.Read(ref _pendingHandshakes));
    }
}
