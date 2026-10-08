using Basis.Network.Core;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BasisServerTests;

internal static class WebSocketTestSupport
{
    public static BasisWebSocketTransportConfig FastConfig() => new()
    {
        PingInterval = 200,
        DisconnectTimeout = 5000,
        HandshakeTimeoutMs = 3000,
        ConnectTimeoutMs = 3000,
    };

    public static int FreeTcpPort()
    {
        TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static int FreeTcpAndUdpPort()
    {
        Random random = new Random();
        for (int attempt = 0; attempt < 200; attempt++)
        {
            int port = random.Next(20000, 60000);
            try
            {
                TcpListener tcp = new TcpListener(IPAddress.Any, port);
                tcp.Start();
                tcp.Stop();
                using UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
                return port;
            }
            catch (SocketException)
            {
            }
        }
        throw new InvalidOperationException("No free port found");
    }

    public static async Task<T> Within<T>(Task<T> task, int milliseconds = 5000)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(milliseconds));
        Assert.True(ReferenceEquals(finished, task), $"Timed out after {milliseconds} ms");
        return await task;
    }

    public static async Task Until(Func<bool> condition, int milliseconds = 5000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Condition not met within {milliseconds} ms");
            await Task.Delay(10);
        }
    }

    public static NetDataWriter Writer(params byte[] bytes)
    {
        NetDataWriter writer = new NetDataWriter(true, Math.Max(8, bytes.Length));
        writer.Put(bytes);
        return writer;
    }

    public static async Task<string> RawHttpAsync(int port, string request)
    {
        using TcpClient client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        NetworkStream stream = client.GetStream();
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        byte[] buffer = new byte[4096];
        int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}

internal sealed class RecordedEvents
{
    public readonly ConcurrentQueue<(NetPeer Peer, byte[] Data, byte Channel, DeliveryMethod Method)> Received = new();
    public readonly ConcurrentQueue<NetPeer> Connected = new();
    public readonly ConcurrentQueue<(NetPeer Peer, DisconnectReason Reason, byte[] Data)> Disconnected = new();
    public readonly ConcurrentQueue<(IPEndPoint EndPoint, byte[] Data)> Unconnected = new();
    public Func<ConnectionRequest, NetPeer?>? OnRequest;
    public Action<IPEndPoint, byte[]>? OnUnconnected;

    public RecordedEvents(EventBasedNetListener listener)
    {
        listener.ConnectionRequestEvent += request =>
        {
            if (OnRequest != null) OnRequest(request);
            else request.Accept();
        };
        listener.PeerConnectedEvent += peer => Connected.Enqueue(peer);
        listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            Received.Enqueue((peer, reader.GetRemainingBytes(), channel, method));
            reader.Recycle();
        };
        listener.PeerDisconnectedEvent += (peer, info) =>
        {
            byte[] data = info.AdditionalData == null ? Array.Empty<byte>() : info.AdditionalData.GetRemainingBytes();
            Disconnected.Enqueue((peer, info.Reason, data));
        };
        listener.NetworkReceiveUnconnectedEvent += (endPoint, reader) =>
        {
            byte[] data = reader.GetRemainingBytes();
            reader.Recycle(true);
            Unconnected.Enqueue((endPoint, data));
            OnUnconnected?.Invoke(endPoint, data);
        };
    }

    public async Task<(NetPeer Peer, byte[] Data, byte Channel, DeliveryMethod Method)> NextReceived(int milliseconds = 5000)
    {
        (NetPeer, byte[], byte, DeliveryMethod) item = default;
        await WebSocketTestSupport.Until(() => Received.TryDequeue(out item), milliseconds);
        return item;
    }

    public async Task<NetPeer> NextConnected(int milliseconds = 5000)
    {
        NetPeer? peer = null;
        await WebSocketTestSupport.Until(() => Connected.TryDequeue(out peer), milliseconds);
        return peer!;
    }

    public async Task<(NetPeer Peer, DisconnectReason Reason, byte[] Data)> NextDisconnected(int milliseconds = 5000)
    {
        (NetPeer, DisconnectReason, byte[]) item = default;
        await WebSocketTestSupport.Until(() => Disconnected.TryDequeue(out item), milliseconds);
        return item;
    }
}

public class PeerIdAllocatorTests
{
    [Fact]
    public void AllocatesDenseIdsFromZero()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        Assert.Equal(new[] { 0, 1, 2, 3 }, Enumerable.Range(0, 4).Select(_ => ids.Allocate()).ToArray());
        Assert.Equal(4, ids.LiveCount);
    }

    [Fact]
    public void ReleasedIdIsHeldBackUntilTheReuseDelayPasses()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(150);
        int first = ids.Allocate();
        ids.Allocate();
        Assert.True(ids.Release(first));
        Assert.Equal(2, ids.Allocate());
        Thread.Sleep(250);
        Assert.Equal(first, ids.Allocate());
    }

    [Fact]
    public void WithoutADelayFreedIdsAreReusedOldestFirst()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        for (int index = 0; index < 5; index++) ids.Allocate();
        ids.Release(3);
        ids.Release(1);
        Assert.Equal(3, ids.Allocate());
        Assert.Equal(1, ids.Allocate());
        Assert.Equal(5, ids.Allocate());
    }

    [Fact]
    public void ReleasingAnIdTwiceOrOneNeverHandedOutIsIgnored()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        int id = ids.Allocate();
        Assert.True(ids.Release(id));
        Assert.False(ids.Release(id));
        Assert.False(ids.Release(999));
        Assert.Equal(id, ids.Allocate());
        Assert.Equal(1, ids.Allocate());
    }

    [Fact]
    public void ResetStartsOverFromZero()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        ids.Allocate();
        ids.Allocate();
        ids.Reset();
        Assert.Equal(0, ids.LiveCount);
        Assert.Equal(0, ids.Allocate());
    }

    [Fact]
    public void ConcurrentCallersNeverHoldTheSameIdAtOnce()
    {
        BasisPeerIdAllocator ids = new BasisPeerIdAllocator(0);
        ConcurrentDictionary<int, byte> held = new ConcurrentDictionary<int, byte>();
        int collisions = 0;
        Parallel.For(0, 20000, index =>
        {
            int id = ids.Allocate();
            if (!held.TryAdd(id, 0)) Interlocked.Increment(ref collisions);
            if (index % 2 == 0)
            {
                held.TryRemove(id, out _);
                ids.Release(id);
            }
        });
        Assert.Equal(0, collisions);
        Assert.Equal(held.Count, ids.LiveCount);
    }
}

public class WebSocketWireTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(127u)]
    [InlineData(128u)]
    [InlineData(16383u)]
    [InlineData(16384u)]
    [InlineData(uint.MaxValue)]
    public void VarUIntRoundTrips(uint value)
    {
        byte[] buffer = new byte[8];
        int written = BasisWebSocketProtocol.WriteVarUInt(buffer, 1, value);
        Assert.Equal(BasisWebSocketProtocol.VarUIntSize(value), written);
        int offset = 1;
        Assert.True(BasisWebSocketProtocol.TryReadVarUInt(buffer, ref offset, 1 + written, out uint read));
        Assert.Equal(value, read);
        Assert.Equal(1 + written, offset);
    }

    [Fact]
    public void VarUIntRejectsTruncatedAndOverlongInput()
    {
        int offset = 0;
        Assert.False(BasisWebSocketProtocol.TryReadVarUInt(new byte[] { 0x80, 0x80 }, ref offset, 2, out _));
        offset = 0;
        Assert.False(BasisWebSocketProtocol.TryReadVarUInt(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, ref offset, 5, out _));
    }

    [Fact]
    public void FramesReadBackToBackAndRejectTruncation()
    {
        byte[] buffer = new byte[64];
        int cursor = 0;
        cursor += BasisWebSocketProtocol.WriteFrameHeader(buffer, cursor, BasisWebSocketProtocol.KindPing, 3);
        buffer[cursor++] = 1;
        buffer[cursor++] = 2;
        buffer[cursor++] = 3;
        cursor += BasisWebSocketProtocol.WriteFrameHeader(buffer, cursor, BasisWebSocketProtocol.KindDisconnect, 0);
        int end = cursor;

        int offset = 0;
        Assert.True(BasisWebSocketProtocol.TryReadFrame(buffer, ref offset, end, out byte kind, out int bodyOffset, out int bodyLength));
        Assert.Equal(BasisWebSocketProtocol.KindPing, kind);
        Assert.Equal(3, bodyLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, buffer.Skip(bodyOffset).Take(bodyLength).ToArray());
        Assert.True(BasisWebSocketProtocol.TryReadFrame(buffer, ref offset, end, out kind, out _, out bodyLength));
        Assert.Equal(BasisWebSocketProtocol.KindDisconnect, kind);
        Assert.Equal(0, bodyLength);
        Assert.Equal(end, offset);

        offset = 0;
        Assert.False(BasisWebSocketProtocol.TryReadFrame(buffer, ref offset, 3, out _, out _, out _));
        Assert.False(BasisWebSocketProtocol.TryReadFrame(new byte[] { 0 }, ref offset, 1, out _, out _, out _));
    }

    [Fact]
    public void AcceptKeyMatchesTheRfcExample()
    {
        Assert.Equal("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", BasisWebSocketHandshake.ComputeAccept("dGhlIHNhbXBsZSBub25jZQ=="));
    }

    [Fact]
    public void RequestParsingRecognisesAnUpgrade()
    {
        BasisWebSocketHandshake.Request request = BasisWebSocketHandshake.Parse(
            "GET /basis?x=1 HTTP/1.1\r\nHost: example\r\nUpgrade: websocket\r\nConnection: keep-alive, Upgrade\r\nSec-WebSocket-Key: abc\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Protocol: chat, basis.v1\r\n\r\n");
        Assert.True(request.IsWebSocketUpgrade);
        Assert.True(request.OffersProtocol(BasisWebSocketProtocol.SubProtocol));
        Assert.False(request.OffersProtocol("basis"));
        Assert.True(BasisWebSocketHandshake.PathMatches("/basis", request.Path));
        Assert.True(BasisWebSocketHandshake.PathMatches("basis/", request.Path));
        Assert.False(BasisWebSocketHandshake.PathMatches("/other", request.Path));
        Assert.True(BasisWebSocketHandshake.PathMatches("", request.Path));

        BasisWebSocketHandshake.Request plain = BasisWebSocketHandshake.Parse("GET / HTTP/1.1\r\nHost: example\r\n\r\n");
        Assert.False(plain.IsWebSocketUpgrade);
    }

    [Fact]
    public void OriginsAreCheckedOnlyWhenRestricted()
    {
        Assert.True(BasisWebSocketHandshake.OriginAllowed("*", "https://evil.example"));
        Assert.True(BasisWebSocketHandshake.OriginAllowed("", "https://evil.example"));
        Assert.True(BasisWebSocketHandshake.OriginAllowed("https://play.example.com", "https://play.example.com/"));
        Assert.False(BasisWebSocketHandshake.OriginAllowed("https://play.example.com", "https://evil.example"));
        Assert.True(BasisWebSocketHandshake.OriginAllowed("https://play.example.com", null));
    }

    [Fact]
    public void ForwardedForIsOnlyBelievedFromTrustedProxies()
    {
        BasisTrustedProxyList proxies = new BasisTrustedProxyList("127.0.0.1, 10.0.0.0/8, fd00::/8");
        BasisWebSocketHandshake.Request request = BasisWebSocketHandshake.Parse("GET / HTTP/1.1\r\nX-Forwarded-For: 1.2.3.4, 198.51.100.7, 10.1.2.3\r\n\r\n");

        Assert.Equal(IPAddress.Parse("198.51.100.7"), BasisWebSocketHandshake.ResolveClientAddress(IPAddress.Loopback, request, proxies));
        Assert.Equal(IPAddress.Parse("203.0.113.5"), BasisWebSocketHandshake.ResolveClientAddress(IPAddress.Parse("203.0.113.5"), request, proxies));
        Assert.Equal(IPAddress.Parse("198.51.100.7"), BasisWebSocketHandshake.ResolveClientAddress(IPAddress.Parse("::ffff:127.0.0.1"), request, proxies));

        BasisWebSocketHandshake.Request realIp = BasisWebSocketHandshake.Parse("GET / HTTP/1.1\r\nX-Real-IP: 2001:db8::5\r\n\r\n");
        Assert.Equal(IPAddress.Parse("2001:db8::5"), BasisWebSocketHandshake.ResolveClientAddress(IPAddress.Parse("fd12::1"), realIp, proxies));

        Assert.True(proxies.Contains(IPAddress.Parse("10.255.0.1")));
        Assert.False(proxies.Contains(IPAddress.Parse("11.0.0.1")));
        Assert.False(new BasisTrustedProxyList("").Contains(IPAddress.Loopback));
    }

    [Fact]
    public void UrlsAreBuiltFromHostsAndKeptWhenGivenInFull()
    {
        BasisWebSocketTransportConfig config = new BasisWebSocketTransportConfig();
        Assert.Equal("ws://example.com:4296/", BasisWebSocketNetManager.BuildUrl("example.com", 4296, config));
        Assert.Equal("ws://[::1]:4296/", BasisWebSocketNetManager.BuildUrl("::1", 4296, config));
        Assert.Equal("wss://example.com/basis", BasisWebSocketNetManager.BuildUrl("wss://example.com/basis", 4296, config));
        Assert.Equal("wss://example.com/x", BasisWebSocketNetManager.BuildUrl("https://example.com/x", 4296, config));
        config.ClientUseTls = true;
        config.Path = "basis";
        Assert.Equal("wss://example.com:443/basis", BasisWebSocketNetManager.BuildUrl("example.com", 443, config));
    }

    [Fact]
    public void TargetParserUnderstandsUrlsAndHostPorts()
    {
        Assert.True(BasisWebSocketConnectionTargetParser.TryParse("wss://play.example.com/basis#secret", out string address, out ushort port, out string password));
        Assert.Equal("wss://play.example.com/basis", address);
        Assert.Equal(443, port);
        Assert.Equal("secret", password);

        Assert.True(BasisWebSocketConnectionTargetParser.TryParse("ws://10.0.0.2:5000", out address, out port, out password));
        Assert.Equal(5000, port);
        Assert.Equal(string.Empty, password);

        Assert.True(BasisWebSocketConnectionTargetParser.TryParse("example.com:4300#pw", out address, out port, out password));
        Assert.Equal("example.com", address);
        Assert.Equal(4300, port);
        Assert.Equal("pw", password);

        ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.WebSocketId, "wss://play.example.com/basis#secret");
        new BasisWebSocketConnectionTargetParser().Parse(target);
        Assert.Equal("wss://play.example.com/basis#secret", new BasisWebSocketConnectionTargetParser().Format(target));
    }
}

public class WebSocketOutboundTests
{
    private static List<(byte Kind, byte[] Body)> Frames(byte[] batch, int length)
    {
        List<(byte, byte[])> frames = new List<(byte, byte[])>();
        int offset = 0;
        while (offset < length)
        {
            Assert.True(BasisWebSocketProtocol.TryReadFrame(batch, ref offset, length, out byte kind, out int bodyOffset, out int bodyLength));
            frames.Add((kind, batch.Skip(bodyOffset).Take(bodyLength).ToArray()));
        }
        return frames;
    }

    [Fact]
    public void FirstAppendAsksForAFlushAndTheBatchKeepsOrder()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(1 << 16, 1 << 16, 1 << 20);
        Assert.Equal(BasisWebSocketOutbound.AppendResult.QueuedStartFlush, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, new byte[] { 1 }, 0, 1));
        Assert.Equal(BasisWebSocketOutbound.AppendResult.Queued, outbound.AppendData(19, DeliveryMethod.Unreliable, new byte[] { 2, 3 }, 0, 2));
        Assert.True(outbound.TryTakeBatch(out byte[] batch, out int length, out bool closeNow));
        Assert.False(closeNow);
        List<(byte Kind, byte[] Body)> frames = Frames(batch, length);
        Assert.Equal(2, frames.Count);
        Assert.Equal(new byte[] { 18, (byte)DeliveryMethod.ReliableOrdered, 1 }, frames[0].Body);
        Assert.Equal(new byte[] { 19, (byte)DeliveryMethod.Unreliable, 2, 3 }, frames[1].Body);
        Assert.False(outbound.TryTakeBatch(out _, out _, out _));
        Assert.Equal(BasisWebSocketOutbound.AppendResult.QueuedStartFlush, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, new byte[] { 1 }, 0, 1));
    }

    [Fact]
    public void QueuedCountTracksDroppableFramesPerChannelUntilTaken()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(1 << 16, 1 << 16, 1 << 20);
        for (int index = 0; index < 5; index++) outbound.AppendData(8, DeliveryMethod.Sequenced, new byte[10], 0, 10);
        outbound.AppendData(8, DeliveryMethod.ReliableOrdered, new byte[10], 0, 10);
        outbound.AppendData(9, DeliveryMethod.Unreliable, new byte[10], 0, 10);
        Assert.Equal(5, outbound.QueuedCount(8));
        Assert.Equal(1, outbound.QueuedCount(9));
        outbound.TryTakeBatch(out _, out _, out _);
        Assert.Equal(0, outbound.QueuedCount(8));
        Assert.Equal(0, outbound.QueuedCount(9));
    }

    [Fact]
    public void BulkBudgetShedsPositionUpdatesWithoutTouchingVoiceOrReliableData()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(4096, 4096, 1 << 20);
        byte[] payload = new byte[1000];
        int dropped = 0;
        for (int index = 0; index < 10; index++)
        {
            if (outbound.AppendData(BasisNetworkCommons.PlayerAvatarHighChannel, DeliveryMethod.Sequenced, payload, 0, payload.Length) == BasisWebSocketOutbound.AppendResult.Dropped) dropped++;
        }
        Assert.Equal(6, dropped);
        Assert.Equal(6, outbound.Dropped);
        Assert.NotEqual(BasisWebSocketOutbound.AppendResult.Dropped, outbound.AppendData(BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced, payload, 0, payload.Length));
        Assert.NotEqual(BasisWebSocketOutbound.AppendResult.Dropped, outbound.AppendData(BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered, payload, 0, payload.Length));
        Assert.Equal(0, outbound.PriorityDropped);
    }

    [Fact]
    public void ReliableDataPastTheCeilingReportsOverflow()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(4096, 4096, 8192);
        byte[] payload = new byte[3000];
        Assert.NotEqual(BasisWebSocketOutbound.AppendResult.Overflow, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, payload, 0, payload.Length));
        Assert.NotEqual(BasisWebSocketOutbound.AppendResult.Overflow, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, payload, 0, payload.Length));
        Assert.Equal(BasisWebSocketOutbound.AppendResult.Overflow, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, payload, 0, payload.Length));
    }

    [Fact]
    public void RawMergePatchByteLandsInThePayload()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(1 << 16, 1 << 16, 1 << 20);
        outbound.AppendData(52, DeliveryMethod.Unreliable, new byte[] { 10, 20, 30 }, 0, 3, 1, 99);
        outbound.TryTakeBatch(out byte[] batch, out int length, out _);
        Assert.Equal(new byte[] { 52, (byte)DeliveryMethod.Unreliable, 10, 99, 30 }, Frames(batch, length)[0].Body);
    }

    [Fact]
    public void CloseAfterFlushIsReportedOnceTheQueueDrains()
    {
        BasisWebSocketOutbound outbound = new BasisWebSocketOutbound(1 << 16, 1 << 16, 1 << 20);
        outbound.AppendControl(BasisWebSocketProtocol.KindDisconnect, new byte[] { 7 }, 0, 1);
        Assert.False(outbound.RequestCloseAfterFlush());
        Assert.Equal(BasisWebSocketOutbound.AppendResult.Closed, outbound.AppendData(18, DeliveryMethod.ReliableOrdered, new byte[1], 0, 1));
        Assert.True(outbound.TryTakeBatch(out _, out _, out bool closeNow));
        Assert.False(closeNow);
        Assert.False(outbound.IsDrained);
        Assert.False(outbound.TryTakeBatch(out _, out _, out closeNow));
        Assert.True(closeNow);
        Assert.True(outbound.IsDrained);
        Assert.False(outbound.TryTakeBatch(out _, out _, out closeNow));
        Assert.False(closeNow);
    }
}

public class WebSocketLoopbackTests
{
    private sealed class Pair : IDisposable
    {
        public readonly BasisWebSocketNetManager Server;
        public readonly RecordedEvents ServerEvents;
        public readonly BasisWebSocketNetManager Client;
        public readonly RecordedEvents ClientEvents;
        public readonly int Port;

        public Pair(BasisWebSocketTransportConfig? config = null, bool manualClient = false)
        {
            config ??= WebSocketTestSupport.FastConfig();
            Port = WebSocketTestSupport.FreeTcpPort();
            EventBasedNetListener serverListener = new EventBasedNetListener();
            ServerEvents = new RecordedEvents(serverListener);
            Server = new BasisWebSocketNetManager(serverListener, config);
            Server.Start(IPAddress.Loopback, null!, Port);
            Assert.True(Server.IsRunning);
            EventBasedNetListener clientListener = new EventBasedNetListener();
            ClientEvents = new RecordedEvents(clientListener);
            Client = new BasisWebSocketNetManager(clientListener, config);
            if (manualClient) ((NetManager)Client).StartManual();
            else ((NetManager)Client).Start();
        }

        public NetPeer Connect(params byte[] payload) => Client.Connect("127.0.0.1", Port, WebSocketTestSupport.Writer(payload));

        public void Dispose()
        {
            Client.Stop();
            Server.Stop();
        }
    }

    [Fact]
    public async Task ConnectAcceptAndExchangeDataBothWays()
    {
        using Pair pair = new Pair();
        byte[]? seenConnectData = null;
        pair.ServerEvents.OnRequest = request =>
        {
            seenConnectData = request.Data.GetRemainingBytes();
            return request.Accept();
        };
        NetPeer clientPeer = pair.Connect(5, 6, 7);
        NetPeer connected = await pair.ClientEvents.NextConnected();
        Assert.Same(clientPeer, connected);
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        Assert.Equal(new byte[] { 5, 6, 7 }, seenConnectData);
        Assert.Equal(serverPeer.Id, clientPeer.RemoteId);
        Assert.Equal(BasisNetworkStackRegistry.WebSocketId, serverPeer.StackId);
        Assert.False(serverPeer.SupportsDirectConnect);
        Assert.Equal(IPAddress.Loopback, serverPeer.Address);
        Assert.Equal(1, pair.Server.ConnectedPeersCount);
        Assert.Equal(1, pair.Client.ConnectedPeersCount);

        clientPeer.Send(new byte[] { 1, 2, 3 }, BasisNetworkCommons.ChatChannel, DeliveryMethod.ReliableOrdered);
        var atServer = await pair.ServerEvents.NextReceived();
        Assert.Same(serverPeer, atServer.Peer);
        Assert.Equal(new byte[] { 1, 2, 3 }, atServer.Data);
        Assert.Equal(BasisNetworkCommons.ChatChannel, atServer.Channel);
        Assert.Equal(DeliveryMethod.ReliableOrdered, atServer.Method);

        serverPeer.Send(WebSocketTestSupport.Writer(9, 8), BasisNetworkCommons.VoiceChannel, DeliveryMethod.Sequenced);
        var atClient = await pair.ClientEvents.NextReceived();
        Assert.Equal(new byte[] { 9, 8 }, atClient.Data);
        Assert.Equal(BasisNetworkCommons.VoiceChannel, atClient.Channel);
        Assert.Equal(DeliveryMethod.Sequenced, atClient.Method);

        serverPeer.SendUnreliableRawMerge(new byte[] { 4, 5, 6 }, 0, 3, BasisNetworkCommons.CompressedAvatarBundleChannel, 2, 77);
        var merged = await pair.ClientEvents.NextReceived();
        Assert.Equal(new byte[] { 4, 5, 77 }, merged.Data);
        Assert.Equal(DeliveryMethod.Unreliable, merged.Method);
    }

    [Fact]
    public async Task AcceptingAfterTheRequestEventStillConnects()
    {
        using Pair pair = new Pair();
        TaskCompletionSource<ConnectionRequest> pending = new TaskCompletionSource<ConnectionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.ServerEvents.OnRequest = request =>
        {
            pending.TrySetResult(request);
            return null;
        };
        NetPeer clientPeer = pair.Connect();
        ConnectionRequest request = await WebSocketTestSupport.Within(pending.Task);
        await Task.Delay(100);
        Assert.Empty(pair.ClientEvents.Connected);
        NetPeer accepted = request.Accept();
        Assert.Same(accepted, await pair.ServerEvents.NextConnected());
        await pair.ClientEvents.NextConnected();
        Assert.Equal(accepted.Id, clientPeer.RemoteId);
    }

    [Fact]
    public async Task ThousandsOfMessagesArriveInOrderBothWays()
    {
        using Pair pair = new Pair();
        NetPeer clientPeer = pair.Connect();
        await pair.ClientEvents.NextConnected();
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        const int count = 3000;
        Task sending = Task.Run(() =>
        {
            for (int index = 0; index < count; index++)
            {
                clientPeer.Send(BitConverter.GetBytes(index), BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered);
                serverPeer.Send(BitConverter.GetBytes(index), BasisNetworkCommons.SceneChannel, DeliveryMethod.ReliableOrdered);
            }
        });
        await sending;
        await WebSocketTestSupport.Until(() => pair.ServerEvents.Received.Count == count && pair.ClientEvents.Received.Count == count, 15000);
        Assert.Equal(Enumerable.Range(0, count), pair.ServerEvents.Received.Select(item => BitConverter.ToInt32(item.Data)));
        Assert.Equal(Enumerable.Range(0, count), pair.ClientEvents.Received.Select(item => BitConverter.ToInt32(item.Data)));
    }

    [Fact]
    public async Task MultiMegabyteMessageSurvivesIntact()
    {
        using Pair pair = new Pair();
        NetPeer clientPeer = pair.Connect();
        await pair.ClientEvents.NextConnected();
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        byte[] blob = new byte[3 * 1024 * 1024 + 17];
        new Random(42).NextBytes(blob);
        serverPeer.Send(blob, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
        clientPeer.Send(blob, BasisNetworkCommons.LoadResourceChannel, DeliveryMethod.ReliableOrdered);
        Assert.Equal(blob, (await pair.ClientEvents.NextReceived(15000)).Data);
        Assert.Equal(blob, (await pair.ServerEvents.NextReceived(15000)).Data);
    }

    [Fact]
    public async Task RejectionReachesTheClientWithItsReason()
    {
        using Pair pair = new Pair();
        pair.ServerEvents.OnRequest = request =>
        {
            NetDataWriter reason = new NetDataWriter();
            reason.Put("server full");
            request.Reject(reason);
            return null;
        };
        pair.Connect();
        var disconnected = await pair.ClientEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.ConnectionRejected, disconnected.Reason);
        Assert.Equal("server full", new NetDataReader(disconnected.Data).GetString());
        Assert.Empty(pair.ServerEvents.Connected);
        Assert.Empty(pair.ServerEvents.Disconnected);
        Assert.Equal(0, pair.Server.ConnectedPeersCount);
    }

    [Fact]
    public async Task ClientLeavingIsSeenAsARemoteClose()
    {
        using Pair pair = new Pair();
        NetPeer clientPeer = pair.Connect();
        await pair.ClientEvents.NextConnected();
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        clientPeer.Disconnect();
        var local = await pair.ClientEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.DisconnectPeerCalled, local.Reason);
        var remote = await pair.ServerEvents.NextDisconnected();
        Assert.Same(serverPeer, remote.Peer);
        Assert.Equal(DisconnectReason.RemoteConnectionClose, remote.Reason);
        await WebSocketTestSupport.Until(() => pair.Server.ConnectedPeersCount == 0);
    }

    [Fact]
    public async Task ServerKickCarriesItsMessageToTheClient()
    {
        using Pair pair = new Pair();
        pair.Connect();
        await pair.ClientEvents.NextConnected();
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        serverPeer.Disconnect(Encoding.UTF8.GetBytes("kicked"));
        var atClient = await pair.ClientEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.RemoteConnectionClose, atClient.Reason);
        Assert.Equal("kicked", Encoding.UTF8.GetString(atClient.Data));
        var atServer = await pair.ServerEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.DisconnectPeerCalled, atServer.Reason);
    }

    [Fact]
    public async Task PingsMeasureRoundTripTime()
    {
        using Pair pair = new Pair();
        NetPeer clientPeer = pair.Connect();
        await pair.ClientEvents.NextConnected();
        NetPeer serverPeer = await pair.ServerEvents.NextConnected();
        await WebSocketTestSupport.Until(() => clientPeer.RoundTripTime > 0 && serverPeer.RoundTripTime > 0);
        Assert.True(Math.Abs(clientPeer.RemoteTimeDelta) < TimeSpan.TicksPerSecond);
        Assert.True(serverPeer.TimeSinceLastPacket < 2000);
    }

    [Fact]
    public async Task SilentPeerTimesOut()
    {
        BasisWebSocketTransportConfig config = WebSocketTestSupport.FastConfig();
        config.DisconnectTimeout = 1200;
        int port = WebSocketTestSupport.FreeTcpPort();
        EventBasedNetListener listener = new EventBasedNetListener();
        RecordedEvents events = new RecordedEvents(listener);
        BasisWebSocketNetManager server = new BasisWebSocketNetManager(listener, config);
        server.Start(IPAddress.Loopback, null!, port);
        try
        {
            using TcpClient raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = raw.GetStream();
            byte[] handshake = Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
            await stream.WriteAsync(handshake);
            byte[] response = new byte[1024];
            int read = await stream.ReadAsync(response);
            Assert.StartsWith("HTTP/1.1 101", Encoding.ASCII.GetString(response, 0, read));
            byte[] body = new byte[BasisWebSocketProtocol.ConnectHeaderBytes];
            BasisWebSocketProtocol.WriteUInt32(body, 0, BasisWebSocketProtocol.ConnectMagic);
            body[4] = BasisWebSocketProtocol.Version;
            byte[] frame = new byte[BasisWebSocketProtocol.FrameSize(body.Length)];
            int cursor = BasisWebSocketProtocol.WriteFrameHeader(frame, 0, BasisWebSocketProtocol.KindConnect, body.Length);
            Buffer.BlockCopy(body, 0, frame, cursor, body.Length);
            await stream.WriteAsync(MaskedBinaryFrame(frame));
            await events.NextConnected();
            var timedOut = await events.NextDisconnected(6000);
            Assert.Equal(DisconnectReason.Timeout, timedOut.Reason);
        }
        finally
        {
            server.Stop();
        }
    }

    private static byte[] MaskedBinaryFrame(byte[] payload)
    {
        byte[] mask = { 1, 2, 3, 4 };
        List<byte> frame = new List<byte> { 0x82 };
        if (payload.Length < 126) frame.Add((byte)(0x80 | payload.Length));
        else
        {
            frame.Add(0x80 | 126);
            frame.Add((byte)(payload.Length >> 8));
            frame.Add((byte)payload.Length);
        }
        frame.AddRange(mask);
        for (int index = 0; index < payload.Length; index++) frame.Add((byte)(payload[index] ^ mask[index % 4]));
        return frame.ToArray();
    }

    [Fact]
    public async Task PlainHttpAndBadRequestsAreTurnedAway()
    {
        BasisWebSocketTransportConfig config = WebSocketTestSupport.FastConfig();
        config.AllowedOrigins = "https://play.example.com";
        config.Path = "/basis";
        using Pair pair = new Pair(config);
        string plain = await WebSocketTestSupport.RawHttpAsync(pair.Port, "GET / HTTP/1.1\r\nHost: x\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 426", plain);
        string wrongPath = await WebSocketTestSupport.RawHttpAsync(pair.Port, "GET /nope HTTP/1.1\r\nHost: x\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: a2V5\r\nSec-WebSocket-Version: 13\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404", wrongPath);
        string wrongOrigin = await WebSocketTestSupport.RawHttpAsync(pair.Port, "GET /basis HTTP/1.1\r\nHost: x\r\nOrigin: https://evil.example\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: a2V5\r\nSec-WebSocket-Version: 13\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 403", wrongOrigin);
        string allowed = await WebSocketTestSupport.RawHttpAsync(pair.Port, "GET /basis HTTP/1.1\r\nHost: x\r\nOrigin: https://play.example.com\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: a2V5\r\nSec-WebSocket-Version: 13\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 101", allowed);
    }

    [Fact]
    public async Task ProbeGetsTheServerInfoReply()
    {
        using Pair pair = new Pair();
        pair.ServerEvents.OnUnconnected = (endPoint, data) =>
        {
            NetDataReader query = new NetDataReader(data);
            Assert.Equal(BasisNetworkCommons.ServerInfoQueryMagic, query.GetUInt());
            query.GetUShort();
            ushort nonce = query.GetUShort();
            NetDataWriter reply = new NetDataWriter();
            reply.Put(BasisNetworkCommons.ServerInfoResponseMagic);
            reply.Put(BasisNetworkCommons.ServerInfoProtocolVersion);
            reply.Put(nonce);
            reply.Put((ushort)3);
            reply.Put((ushort)50);
            reply.Put("Web Test");
            reply.Put("hello browsers");
            Assert.True(pair.Server.SendUnconnectedMessage(reply, endPoint));
        };
        ConnectionTarget target = new ConnectionTarget(BasisNetworkStackRegistry.WebSocketId, $"127.0.0.1:{pair.Port}");
        target.Set(ConnectionTarget.Keys.Address, "127.0.0.1");
        target.Set(ConnectionTarget.Keys.Port, pair.Port.ToString());
        ServerProbeResult result = await WebSocketTestSupport.Within(BasisWebSocketProbe.ProbeAsync(target, 3000, CancellationToken.None));
        Assert.True(result.Reachable, result.Error);
        Assert.Equal(3, result.Online);
        Assert.Equal(50, result.Max);
        Assert.Equal("Web Test", result.Name);
        Assert.Equal("hello browsers", result.Motd);
        Assert.Empty(pair.ServerEvents.Connected);
    }

    [Fact]
    public async Task ManualModeHoldsEventsUntilPolled()
    {
        using Pair pair = new Pair(manualClient: true);
        pair.Connect();
        await pair.ServerEvents.NextConnected();
        await Task.Delay(200);
        Assert.Empty(pair.ClientEvents.Connected);
        pair.Client.PollEvents();
        Assert.Single(pair.ClientEvents.Connected);
    }

    [Fact]
    public async Task ConnectingToNothingFails()
    {
        EventBasedNetListener listener = new EventBasedNetListener();
        RecordedEvents events = new RecordedEvents(listener);
        BasisWebSocketNetManager client = new BasisWebSocketNetManager(listener, WebSocketTestSupport.FastConfig());
        ((NetManager)client).Start();
        try
        {
            client.Connect("127.0.0.1", WebSocketTestSupport.FreeTcpPort(), WebSocketTestSupport.Writer(1));
            var failed = await events.NextDisconnected(8000);
            Assert.Equal(DisconnectReason.ConnectionFailed, failed.Reason);
            Assert.Empty(events.Connected);
        }
        finally
        {
            client.Stop();
        }
    }

    [Fact]
    public async Task StoppingTheServerDisconnectsItsClients()
    {
        using Pair pair = new Pair();
        pair.Connect();
        await pair.ClientEvents.NextConnected();
        await pair.ServerEvents.NextConnected();
        pair.Server.Stop();
        var atClient = await pair.ClientEvents.NextDisconnected();
        Assert.Equal(DisconnectReason.RemoteConnectionClose, atClient.Reason);
        Assert.False(pair.Server.IsRunning);
    }

    [Fact]
    public void BindingATakenPortFailsCleanly()
    {
        TcpListener blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            int port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            BasisWebSocketNetManager server = new BasisWebSocketNetManager(new EventBasedNetListener(), WebSocketTestSupport.FastConfig());
            server.Start(IPAddress.Loopback, null!, port);
            Assert.False(server.IsRunning);
            Assert.Null(server.ListenDescription);
            server.Stop();
        }
        finally
        {
            blocker.Stop();
        }
    }
}
