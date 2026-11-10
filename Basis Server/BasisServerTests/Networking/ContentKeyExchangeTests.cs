using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Basis.Network.Core;
using Basis.Network.Server.Generic;
using Xunit;
using Xunit.Abstractions;
using static Basis.Network.Core.BasisContentKeyProtocol;

namespace BasisServerTests;

public sealed class ContentKeyExchangeTests
{
    private readonly ITestOutputHelper output;
    public ContentKeyExchangeTests(ITestOutputHelper output) => this.output = output;

    private sealed class Mailbox
    {
        private readonly ConcurrentQueue<Action> queue = new();
        private int running;
        public void Post(Action action)
        {
            queue.Enqueue(action);
            if (Interlocked.CompareExchange(ref running, 1, 0) == 0) ThreadPool.UnsafeQueueUserWorkItem(_ => Drain(), null);
        }
        private void Drain()
        {
            while (true)
            {
                while (queue.TryDequeue(out Action action))
                {
                    try { action(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
                }
                Volatile.Write(ref running, 0);
                if (queue.IsEmpty || Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
            }
        }
    }

    private sealed class SimNet : BasisContentKeyRelay.WireSink, IDisposable
    {
        public readonly BasisContentKeyLedger Ledger;
        public readonly BasisContentKeyPeer[] Peers;
        public readonly Func<NetDataReader, bool>[] Intercept;
        private readonly Mailbox server = new();
        private readonly Mailbox[] boxes;
        private readonly ConcurrentDictionary<int, byte> gone = new();
        private readonly Timer ticker;
        public long ServerToClientBytes, ClientToServerBytes;
        public readonly ConcurrentDictionary<int, byte> Unreachable = new();
        public SimNet(int count)
        {
            Ledger = new BasisContentKeyLedger(this);
            Peers = new BasisContentKeyPeer[count];
            Intercept = new Func<NetDataReader, bool>[count];
            boxes = new Mailbox[count];
            for (int index = 0; index < count; index++)
            {
                int id = index;
                boxes[id] = new Mailbox();
                Peers[id] = new BasisContentKeyPeer(write => SendFrom(id, write));
            }
            ticker = new Timer(_ => server.Post(Ledger.Tick), null, 100, 100);
        }
        public bool SendFrom(int id, Action<NetDataWriter> write)
        {
            if (gone.ContainsKey(id)) return false;
            NetDataWriter writer = new NetDataWriter();
            write(writer);
            byte[] bytes = writer.CopyData();
            Interlocked.Add(ref ClientToServerBytes, bytes.Length);
            server.Post(() => BasisContentKeyRelay.Dispatch(Ledger, id, new NetDataReader(bytes)));
            return true;
        }
        public override bool IsConnected(int peerId) => peerId >= 0 && peerId < Peers.Length && !gone.ContainsKey(peerId);
        protected override bool Send(int peerId, Action<NetDataWriter> write)
        {
            if (!IsConnected(peerId) || Unreachable.ContainsKey(peerId)) return false;
            NetDataWriter writer = new NetDataWriter();
            write(writer);
            byte[] bytes = writer.CopyData();
            Interlocked.Add(ref ServerToClientBytes, bytes.Length);
            boxes[peerId].Post(() =>
            {
                if (gone.ContainsKey(peerId)) return;
                Func<NetDataReader, bool> intercept = Intercept[peerId];
                if (intercept != null && intercept(new NetDataReader(bytes))) return;
                Peers[peerId].Handle(new NetDataReader(bytes));
            });
            return true;
        }
        public void Leave(int id)
        {
            gone[id] = 0;
            Peers[id].Dispose();
            server.Post(() => Ledger.RemovePeer(id));
        }
        public async Task Settle()
        {
            for (int pass = 0; pass < 200 && Ledger.LiveCount > 0; pass++) await Task.Delay(20);
        }
        public void Dispose() => ticker.Dispose();
    }

    private static byte[] MakeGrant(int entries)
    {
        byte[] grant = new byte[2 + entries * GrantEntryBytes];
        RandomNumberGenerator.Fill(grant);
        grant[0] = (byte)entries;
        grant[1] = 0;
        return grant;
    }

    private static readonly byte[] AvatarId = ContentId("https://example.invalid/avatar.bee", "v1");

    [Fact]
    public async Task ThousandPlayersAskForOneAvatarAtOnce()
    {
        using SimNet net = new(1000);
        byte[] grant = MakeGrant(9);
        Assert.NotNull(await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant)));
        Stopwatch watch = Stopwatch.StartNew();
        byte[][] results = await Task.WhenAll(Enumerable.Range(1, 999).Select(i => net.Peers[i].ResolveAsync(0, AvatarId))).WaitAsync(TimeSpan.FromSeconds(60));
        watch.Stop();
        Assert.All(results, result => Assert.Equal(grant, result));
        long wearer = net.Peers[0].Served, total = net.Peers.Sum(peer => peer.Served);
        output.WriteLine($"999 requests answered in {watch.ElapsedMilliseconds} ms; the wearer served {wearer}, other players served {total - wearer}; server sent {net.ServerToClientBytes / 1024} KiB, received {net.ClientToServerBytes / 1024} KiB.");
        Assert.Equal(999, total);
        Assert.True(wearer < 300, $"the wearer served {wearer} of 999, so the load did not spread");
        Assert.Equal(0, net.Peers.Sum(peer => peer.Rejected));
        await net.Settle();
        Assert.Equal(0, net.Ledger.LiveCount);
        Assert.Equal(1000, net.Ledger.HolderCount(Commit(grant)));
    }

    [Fact]
    public async Task EveryPlayerLoadsEveryOtherPlayersAvatar()
    {
        const int players = 200;
        using SimNet net = new(players);
        byte[][] grants = new byte[players][], ids = new byte[players][];
        for (int index = 0; index < players; index++)
        {
            grants[index] = MakeGrant(5 + index % 6);
            ids[index] = ContentId($"https://example.invalid/{index}.bee", string.Empty);
            byte[] grant = grants[index];
            await net.Peers[index].Own(ids[index], () => Task.FromResult(grant));
        }
        Stopwatch watch = Stopwatch.StartNew();
        Task<bool>[] checks = Enumerable.Range(0, players).SelectMany(asker => Enumerable.Range(0, players).Where(wearer => wearer != asker).Select(async wearer => (await net.Peers[asker].ResolveAsync((ushort)wearer, ids[wearer])).AsSpan().SequenceEqual(grants[wearer]))).ToArray();
        bool[] results = await Task.WhenAll(checks).WaitAsync(TimeSpan.FromSeconds(120));
        output.WriteLine($"{results.Length} exchanges in {watch.ElapsedMilliseconds} ms, {net.ServerToClientBytes / results.Length} bytes server to client per exchange.");
        Assert.All(results, Assert.True);
        await net.Settle();
        Assert.Equal(0, net.Ledger.LiveCount);
    }

    [Fact]
    public async Task HolderServingWrongKeysIsDroppedAndTheRequestStillSucceeds()
    {
        using SimNet net = new(20);
        byte[] grant = MakeGrant(7), commit = Commit(grant);
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        Assert.Equal(grant, await net.Peers[1].ResolveAsync(0, AvatarId));
        Assert.Equal(grant, await net.Peers[2].ResolveAsync(0, AvatarId));
        NewKeyPair(out byte[] liarPrivate, out byte[] liarPublic);
        int liarServed = 0;
        net.Intercept[1] = reader =>
        {
            if (!reader.TryGetByte(out byte op) || op != OpRequest) return false;
            reader.TryGetUShort(out ushort requester);
            reader.TryGetUInt(out uint requestId);
            TryReadBytes(reader, IdBytes, out byte[] asked);
            TryReadBytes(reader, PublicKeyBytes, out byte[] requesterPublic);
            byte[] sealedData = Seal(SessionKey(liarPrivate, requesterPublic, requesterPublic, liarPublic), requestId, asked, requesterPublic, liarPublic, MakeGrant(7), out byte[] nonce);
            Interlocked.Increment(ref liarServed);
            net.SendFrom(1, writer =>
            {
                writer.Put(OpGrant);
                writer.Put(requester);
                writer.Put(requestId);
                writer.Put(liarPublic, 0, PublicKeyBytes);
                writer.Put(nonce, 0, NonceBytes);
                writer.Put((ushort)sealedData.Length);
                writer.Put(sealedData, 0, sealedData.Length);
            });
            return true;
        };
        net.Leave(0);
        byte[][] results = await Task.WhenAll(Enumerable.Range(3, 17).Select(i => net.Peers[i].ResolveAsync(0, AvatarId))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, result => Assert.Equal(grant, result));
        long rejected = net.Peers.Sum(peer => peer.Rejected);
        output.WriteLine($"the lying holder answered {liarServed} time(s); requesters rejected {rejected} answer(s).");
        Assert.True(liarServed >= 1);
        Assert.Equal(liarServed, rejected);
        net.SendFrom(1, writer => { writer.Put(OpHave); writer.Put(commit, 0, IdBytes); });
        await Task.Delay(100);
        Assert.Equal(18, net.Ledger.HolderCount(commit));
    }

    [Fact]
    public async Task HoldersKeepServingAfterTheOwnerLeaves()
    {
        using SimNet net = new(6);
        byte[] grant = MakeGrant(4);
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        Assert.Equal(grant, await net.Peers[1].ResolveAsync(0, AvatarId));
        net.Leave(0);
        await Task.Delay(50);
        Assert.Equal(grant, await net.Peers[2].ResolveAsync(0, AvatarId).WaitAsync(TimeSpan.FromSeconds(10)));
        net.Leave(1);
        net.Leave(2);
        Stopwatch watch = Stopwatch.StartNew();
        Assert.Null(await net.Peers[3].ResolveAsync(0, AvatarId).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task RequestSentBeforeTheOwnerAnnouncesIsAnsweredOnceItDoes()
    {
        using SimNet net = new(3);
        byte[] grant = MakeGrant(3);
        Task<byte[]> early = net.Peers[1].ResolveAsync(0, AvatarId);
        await Task.Delay(300);
        Assert.False(early.IsCompleted);
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        Assert.Equal(grant, await early.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task UnknownOwnerIsRefusedQuickly()
    {
        using SimNet net = new(3);
        net.Leave(0);
        await Task.Delay(50);
        Stopwatch watch = Stopwatch.StartNew();
        Assert.Null(await net.Peers[1].ResolveAsync(0, AvatarId).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(watch.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public async Task HolderThatLeavesMidRequestIsReplaced()
    {
        using SimNet net = new(5);
        byte[] grant = MakeGrant(4);
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        await net.Peers[1].ResolveAsync(0, AvatarId);
        await net.Peers[2].ResolveAsync(0, AvatarId);
        net.Leave(0);
        int swallowedBy = -1;
        for (int holder = 1; holder <= 2; holder++)
        {
            int id = holder;
            net.Intercept[id] = reader => reader.TryGetByte(out byte op) && op == OpRequest && Interlocked.CompareExchange(ref swallowedBy, id, -1) == -1;
        }
        Task<byte[]> ask = net.Peers[3].ResolveAsync(0, AvatarId);
        for (int wait = 0; wait < 200 && Volatile.Read(ref swallowedBy) < 0; wait++) await Task.Delay(10);
        Assert.True(swallowedBy > 0);
        net.Leave(swallowedBy);
        Assert.Equal(grant, await ask.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task HolderTheServerCannotReachIsSkipped()
    {
        using SimNet net = new(6);
        byte[] grant = MakeGrant(4), commit = Commit(grant);
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        for (int i = 1; i <= 3; i++) Assert.Equal(grant, await net.Peers[i].ResolveAsync(0, AvatarId));
        net.Unreachable[1] = 0;
        net.Unreachable[2] = 0;
        byte[][] results = await Task.WhenAll(Enumerable.Range(4, 2).Select(i => net.Peers[i].ResolveAsync(0, AvatarId))).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.Equal(grant, result));
        await net.Settle();
        Assert.Equal(0, net.Ledger.LiveCount);
    }

    [Fact]
    public async Task OwnerThatAnnouncesBeforeSubscribingIsServedOnceItSubscribes()
    {
        using SimNet net = new(4);
        byte[] grant = MakeGrant(4);
        net.Unreachable[0] = 0;
        await net.Peers[0].Own(AvatarId, () => Task.FromResult(grant));
        Task<byte[]> ask = net.Peers[1].ResolveAsync(0, AvatarId);
        await Task.Delay(300);
        Assert.False(ask.IsCompleted);
        net.Unreachable.TryRemove(0, out _);
        net.Ledger.Reachable(0);
        Assert.Equal(grant, await ask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AnnounceRepeatsOwnershipLostBeforeTheHandshake()
    {
        using SimNet net = new(3);
        byte[] grant = MakeGrant(4);
        bool online = false;
        BasisContentKeyPeer late = new(write => online && net.SendFrom(0, write));
        net.Peers[0].Dispose();
        net.Peers[0] = late;
        await late.Own(AvatarId, () => Task.FromResult(grant));
        Task<byte[]> ask = net.Peers[1].ResolveAsync(0, AvatarId);
        await Task.Delay(200);
        online = true;
        late.Announce();
        Assert.Equal(grant, await ask.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ServerHeldContentIsServedByTheServer()
    {
        using SimNet net = new(50);
        byte[] grant = MakeGrant(6), worldId = ContentId("https://example.invalid/world.bee", string.Empty);
        net.Ledger.Hold(worldId, grant);
        byte[][] results = await Task.WhenAll(Enumerable.Range(0, 50).Select(i => net.Peers[i].ResolveAsync(ServerOrigin, worldId))).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(results, result => Assert.Equal(grant, result));
    }

    [Fact]
    public async Task ServerCanFetchKeysFromAHolderToSaveContent()
    {
        using SimNet net = new(3);
        byte[] grant = MakeGrant(5), propId = ContentId("https://example.invalid/prop.bee", string.Empty);
        await net.Peers[2].Own(propId, () => Task.FromResult(grant));
        TaskCompletionSource<byte[]> fetched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        net.Ledger.Fetch(2, propId, result => fetched.TrySetResult(result));
        Assert.Equal(grant, await fetched.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(grant, net.Ledger.HeldByServer(propId));
        net.Leave(2);
        await Task.Delay(50);
        Assert.Equal(grant, await net.Peers[1].ResolveAsync(2, propId).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task KeysFetchedBeforeTheLoaderOwnsThemOutliveEveryHolder()
    {
        using SimNet net = new(3);
        byte[] grant = MakeGrant(4), worldId = ContentId("https://example.invalid/admin-world.bee", string.Empty);
        TaskCompletionSource<byte[]> fetched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        net.Ledger.Fetch(0, worldId, result => fetched.TrySetResult(result));
        await Task.Delay(50);
        await net.Peers[0].Own(worldId, () => Task.FromResult(grant));
        Assert.Equal(grant, await fetched.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(grant, await net.Peers[1].ResolveAsync(0, worldId).WaitAsync(TimeSpan.FromSeconds(10)));
        net.Leave(0);
        net.Leave(1);
        await Task.Delay(50);
        Assert.Equal(grant, await net.Peers[2].ResolveAsync(0, worldId).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void SealedKeysOpenOnlyForTheirRequestAndContent()
    {
        NewKeyPair(out byte[] requesterPrivate, out byte[] requesterPublic);
        NewKeyPair(out byte[] holderPrivate, out byte[] holderPublic);
        byte[] grant = MakeGrant(3), commit = Commit(grant);
        byte[] sealedData = Seal(SessionKey(holderPrivate, requesterPublic, requesterPublic, holderPublic), 42, commit, requesterPublic, holderPublic, grant, out byte[] nonce);
        uint[] requesterSession = SessionKey(requesterPrivate, holderPublic, requesterPublic, holderPublic);
        Assert.False(sealedData.AsSpan(0, grant.Length).SequenceEqual(grant));
        Assert.Equal(grant, Open(requesterSession, 42, commit, requesterPublic, holderPublic, nonce, sealedData));
        Assert.Null(Open(requesterSession, 43, commit, requesterPublic, holderPublic, nonce, sealedData));
        Assert.Null(Open(requesterSession, 42, Commit(MakeGrant(3)), requesterPublic, holderPublic, nonce, sealedData));
        NewKeyPair(out byte[] otherPrivate, out byte[] otherPublic);
        Assert.Null(Open(SessionKey(otherPrivate, holderPublic, otherPublic, holderPublic), 42, commit, otherPublic, holderPublic, nonce, sealedData));
        byte[] tampered = (byte[])sealedData.Clone();
        tampered[5] ^= 1;
        Assert.Null(Open(requesterSession, 42, commit, requesterPublic, holderPublic, nonce, tampered));
        byte[] otherGrant = MakeGrant(3);
        byte[] honestlySealedWrongKeys = Seal(SessionKey(holderPrivate, requesterPublic, requesterPublic, holderPublic), 42, commit, requesterPublic, holderPublic, otherGrant, out byte[] otherNonce);
        Assert.Null(Open(requesterSession, 42, commit, requesterPublic, holderPublic, otherNonce, honestlySealedWrongKeys));
        Assert.Equal(grant, GrantFromToken(Token(grant)));
        Assert.Null(GrantFromToken(GrantPrefix + "!!"));
        Assert.True(TryParseMarker(Marker(ServerOrigin, AvatarId), out ushort origin, out byte[] parsedId) && origin == ServerOrigin && parsedId.AsSpan().SequenceEqual(AvatarId));
        string bare = Marker(AvatarId);
        Assert.False(HasOrigin(bare));
        Assert.False(TryParseMarker(bare, out _, out _));
        Assert.True(TryParseMarker(WithOrigin(bare, 7), out origin, out parsedId) && origin == 7 && parsedId.AsSpan().SequenceEqual(AvatarId));
        Assert.Equal(Marker(9, AvatarId), WithOrigin(Marker(9, AvatarId), 7));
        Assert.True(TryParseMarker(WithOrigin(RequestPrefix, 3), out origin, out parsedId) && origin == 3 && parsedId == null);
        Assert.False(TryParseMarker(RequestPrefix + "3/zz", out _, out _));
    }

    [Fact]
    public void CurrentClientsFallBackOnlyToServersTheyCanSpeakTo()
    {
        Assert.True(BasisNetworkVersion.TryGetFallback(BasisNetworkVersion.PasswordRecordsVersion, out bool legacy, out ushort protocol));
        Assert.False(legacy);
        Assert.Equal(57, protocol);
        Assert.True(BasisNetworkVersion.TryGetFallback(BasisNetworkVersion.LegacyLoginVersion, out legacy, out protocol));
        Assert.True(legacy);
        Assert.Equal(0, protocol);
        Assert.False(BasisNetworkVersion.TryGetFallback(56, out _, out _));
        Assert.False(BasisNetworkVersion.TryGetFallback(BasisNetworkVersion.ServerVersion, out _, out _));
        Assert.False(BasisNetworkVersion.TryGetFallback(59, out _, out _));
        Assert.True(BasisNetworkVersion.SendsUnlockPasswords(false, 57));
        Assert.True(BasisNetworkVersion.SendsUnlockPasswords(true, 0));
        Assert.False(BasisNetworkVersion.SendsUnlockPasswords(false, 0));
        Assert.False(BasisNetworkVersion.SendsUnlockPasswords(false, BasisNetworkVersion.ServerVersion));
        NetworkClient client = new NetworkClient();
        Assert.Equal(0, client.ProtocolVersion);
    }

    private static byte[] Encrypt(string password, byte[] plain)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), iv = RandomNumberGenerator.GetBytes(16);
        using Aes aes = Aes.Create();
        aes.Key = BasisServerBeeKeys.Key(password, salt);
        aes.IV = iv;
        byte[] cipher = aes.EncryptCbc(plain, iv, PaddingMode.PKCS7);
        return salt.Concat(iv).Concat(cipher).ToArray();
    }

    private static byte[] KeyFor(byte[] grant, byte[] blob)
    {
        int count = grant[0] | grant[1] << 8;
        for (int index = 0; index < count; index++)
        {
            int offset = 2 + index * GrantEntryBytes;
            if (grant.AsSpan(offset, 16).SequenceEqual(blob.AsSpan(0, 16))) return grant.AsSpan(offset + 16, 32).ToArray();
        }
        return null;
    }

    private static byte[] DecryptWith(byte[] key, byte[] blob)
    {
        using Aes aes = Aes.Create();
        aes.Key = key;
        return aes.DecryptCbc(blob.AsSpan(32), blob.AsSpan(16, 16), PaddingMode.PKCS7);
    }

    [Fact]
    public async Task ServerDerivesKeysForEveryPartOfABeeFileOverHttp()
    {
        const string password = "creator password";
        byte[] windows = RandomNumberGenerator.GetBytes(5000), sidecar = RandomNumberGenerator.GetBytes(300), generic = RandomNumberGenerator.GetBytes(7000);
        byte[] windowsBlob = Encrypt(password, windows), sidecarBlob = Encrypt(password, sidecar), genericBlob = Encrypt(password, generic);
        string json = "{\"Value\":{\"UniqueVersion\":\"x\",\"BasisBundleDescription\":{\"AssetBundleName\":\"Q\\\"uote \\u00e9\"},\"BasisBundleGenerated\":[" +
            $"{{\"Platform\":\"StandaloneWindows64\",\"EndByte\":{windowsBlob.Length + sidecarBlob.Length},\"GraphicsStatePayloads\":[{{\"GraphicsAPI\":\"Direct3D11\",\"Offset\":{windowsBlob.Length},\"Length\":{sidecarBlob.Length},\"VariantCount\":3}}],\"GraphicsAPIs\":[\"Direct3D11\"]}}," +
            $"{{\"Platform\":\"Generic\",\"EndByte\":{genericBlob.Length},\"GraphicsStatePayloads\":null,\"IsEncrypted\":true}}" +
            "],\"ImageBase64\":\"aGVsbG8=\",\"MetaData\":{\"TrianglesCount\":12,\"Ratio\":-1.5e3}}}";
        byte[] connector = Encrypt(password, Encoding.UTF8.GetBytes(json));
        byte[] bee = BitConverter.GetBytes((long)connector.Length).Concat(connector).Concat(windowsBlob).Concat(sidecarBlob).Concat(genericBlob).ToArray();
        int port = FreePort();
        using HttpListener listener = new();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        int ranged = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); } catch { return; }
                string range = context.Request.Headers["Range"];
                long start = 0, end = bee.Length - 1;
                if (range != null && range.StartsWith("bytes="))
                {
                    string[] parts = range.Substring(6).Split('-');
                    start = long.Parse(parts[0]);
                    end = Math.Min(long.Parse(parts[1]), bee.Length - 1);
                    context.Response.StatusCode = 206;
                    Interlocked.Increment(ref ranged);
                }
                context.Response.ContentLength64 = end - start + 1;
                await context.Response.OutputStream.WriteAsync(bee, (int)start, (int)(end - start + 1));
                context.Response.Close();
            }
        });
        string url = $"http://127.0.0.1:{port}/item.bee";
        (byte[] grant, string error) = await BasisServerBeeKeys.DeriveAsync(url, password);
        Assert.Null(error);
        Assert.True(IsValidGrant(grant));
        Assert.Equal(4, grant[0]);
        Assert.Equal(json, Encoding.UTF8.GetString(DecryptWith(KeyFor(grant, connector), connector)));
        Assert.Equal(windows, DecryptWith(KeyFor(grant, windowsBlob), windowsBlob));
        Assert.Equal(sidecar, DecryptWith(KeyFor(grant, sidecarBlob), sidecarBlob));
        Assert.Equal(generic, DecryptWith(KeyFor(grant, genericBlob), genericBlob));
        Assert.Equal(5, ranged);
        (byte[] wrong, string wrongError) = await BasisServerBeeKeys.DeriveAsync(url, "not the password");
        Assert.Null(wrong);
        Assert.NotNull(wrongError);
        Assert.Null((await BasisServerBeeKeys.DeriveAsync("file:///c:/x.bee", password)).Grant);
        listener.Stop();
    }

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
