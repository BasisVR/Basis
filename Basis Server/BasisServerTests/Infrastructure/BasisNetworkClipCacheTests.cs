using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using BasisNetworkServer.Security;
using System.Text;
using Xunit;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class BasisNetworkClipCacheTests : IDisposable
{
    private const ushort ClipNetId = 4343;
    private const int ChunkBytes = BasisNetworkClipCache.ChunkBytes;

    private readonly Configuration _previous;
    private readonly List<int> _registeredPeerIds = new();
    private readonly string _directory;

    public BasisNetworkClipCacheTests()
    {
        _previous = NetworkServer.Configuration;
        _directory = Path.Combine(Path.GetTempPath(), "BasisClipCacheTests", Guid.NewGuid().ToString("N"));
        NetworkServer.Configuration = new Configuration
        {
            VideoClipCacheEnabled = true,
            VideoClipCacheMaxMegabytes = 1,
            VideoClipCacheDirectory = _directory,
            VideoClipCacheServeMegabytesPerSecond = 0,
            VideoClipMaxMegabytes = 96,
            VideoClipMaxPerPlayer = 3,
            WorldsLocked = false,
        };
        BasisGlobalLockManager.InitializeFromConfig(NetworkServer.Configuration);
        BasisNetworkClipCache.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkClipCache.ClipShareIdentifier] = ClipNetId;
        Assert.True(BasisNetworkClipCache.IsClipTraffic(ClipNetId));
    }

    public void Dispose()
    {
        BasisNetworkClipCache.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkClipCache.ClipShareIdentifier, out _);
        BasisGlobalLockManager.InitializeFromConfig(new Configuration { WorldsLocked = false });
        NetworkServer.Configuration = _previous;
        foreach (int id in _registeredPeerIds) NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
        }
    }

    private ImageCacheRecordingPeer RegisterPeer(int id)
    {
        ImageCacheRecordingPeer peer = new ImageCacheRecordingPeer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        _registeredPeerIds.Add(id);
        return peer;
    }

    private static ushort StampOf((byte Channel, byte[] Data) sent) => BitConverter.ToUInt16(sent.Data, 0);

    private static byte[] PayloadOf((byte Channel, byte[] Data) sent)
    {
        Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, sent.Channel);
        Assert.Equal(ClipNetId, BitConverter.ToUInt16(sent.Data, 2));
        byte[] payload = new byte[sent.Data.Length - 4];
        Buffer.BlockCopy(sent.Data, 4, payload, 0, payload.Length);
        return payload;
    }

    private static byte[] EncodeOffer(Guid id, int totalBytes, byte flags = 0, string name = "clip")
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] buffer = new byte[1 + 16 + 4 + 2 + 32 + 2 + nameBytes.Length];
        buffer[0] = BasisNetworkClipCache.OpOffer;
        id.ToByteArray().CopyTo(buffer, 1);
        BitConverter.GetBytes(totalBytes).CopyTo(buffer, 17);
        buffer[21] = 1;
        buffer[22] = flags;
        BitConverter.GetBytes(1f).CopyTo(buffer, 23 + 6 * 4);
        BitConverter.GetBytes(1f).CopyTo(buffer, 23 + 7 * 4);
        BitConverter.GetBytes((ushort)nameBytes.Length).CopyTo(buffer, 55);
        nameBytes.CopyTo(buffer, 57);
        return buffer;
    }

    private static byte[] EncodeChunk(Guid id, int index, byte[] data)
    {
        int length = Math.Min(ChunkBytes, data.Length - index * ChunkBytes);
        byte[] buffer = new byte[BasisNetworkClipCache.ChunkHeaderBytes + length];
        buffer[0] = BasisNetworkClipCache.OpChunk;
        id.ToByteArray().CopyTo(buffer, 1);
        BitConverter.GetBytes(index).CopyTo(buffer, 17);
        Buffer.BlockCopy(data, index * ChunkBytes, buffer, BasisNetworkClipCache.ChunkHeaderBytes, length);
        return buffer;
    }

    private static byte[] EncodeId(byte op, Guid id)
    {
        byte[] buffer = new byte[17];
        buffer[0] = op;
        id.ToByteArray().CopyTo(buffer, 1);
        return buffer;
    }

    private static byte[] EncodeNeed(Guid id, params int[] indices)
    {
        byte[] buffer = new byte[1 + 16 + 2 + indices.Length * 4];
        buffer[0] = BasisNetworkClipCache.OpNeed;
        id.ToByteArray().CopyTo(buffer, 1);
        BitConverter.GetBytes((ushort)indices.Length).CopyTo(buffer, 17);
        for (int i = 0; i < indices.Length; i++) BitConverter.GetBytes(indices[i]).CopyTo(buffer, 19 + i * 4);
        return buffer;
    }

    private static byte[] MakeData(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static void Observe(ushort sender, byte[] payload, bool toServer = false) =>
        BasisNetworkClipCache.Observe(sender, payload, payload.Length, toServer);

    private static void Upload(ushort owner, Guid id, byte[] data)
    {
        Observe(owner, EncodeOffer(id, data.Length), toServer: true);
        int chunks = (data.Length + ChunkBytes - 1) / ChunkBytes;
        for (int i = 0; i < chunks; i++) Observe(owner, EncodeChunk(id, i, data));
    }

    private static void Drain()
    {
        for (int spin = 0; spin < 2000 && BasisNetworkClipCache.PendingDeliveries > 0; spin++)
        {
            BasisNetworkClipCache.Pump();
            Thread.Sleep(1);
        }
        Assert.Equal(0, BasisNetworkClipCache.PendingDeliveries);
    }

    private static List<byte[]> PayloadsWithOp(ImageCacheRecordingPeer peer, byte op)
    {
        List<byte[]> found = new();
        foreach ((byte Channel, byte[] Data) sent in peer.Sent.ToArray())
        {
            byte[] payload = PayloadOf(sent);
            if (payload[0] == op) found.Add(payload);
        }
        return found;
    }

    [Fact]
    public void AddressedToServer_OnlyWhenEveryRecipientIsTheSender()
    {
        Assert.True(BasisNetworkClipCache.IsAddressedToServer(5, new ushort[] { 5 }, 1));
        Assert.False(BasisNetworkClipCache.IsAddressedToServer(5, new ushort[] { 5, 6 }, 2));
        Assert.False(BasisNetworkClipCache.IsAddressedToServer(5, null, 0));
        Assert.False(BasisNetworkClipCache.IsAddressedToServer(5, new ushort[] { 6 }, 1));
    }

    [Fact]
    public void Upload_AsksTheOwnerToUploadThenReportsItHeld()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(1);
        Guid id = Guid.NewGuid();
        byte[] data = MakeData(ChunkBytes * 2 + 100, 1);

        Upload(1, id, data);

        List<byte[]> states = PayloadsWithOp(owner, BasisNetworkClipCache.OpServerCacheState);
        Assert.Equal(2, states.Count);
        Assert.Equal(BasisNetworkClipCache.StateUpload, states[0][17]);
        Assert.Equal(BasisNetworkClipCache.StateHeld, states[1][17]);
        Assert.Equal(1, BasisNetworkClipCache.CompleteCount);
        Assert.Equal(data.Length, BasisNetworkClipCache.TotalBytes);
    }

    [Fact]
    public void Joiner_GetsAnOfferFromTheCatalogAndTheExactBytesOnRequest()
    {
        RegisterPeer(1);
        ImageCacheRecordingPeer joiner = RegisterPeer(2);
        Guid id = Guid.NewGuid();
        byte[] data = MakeData(ChunkBytes * 3 + 7, 2);
        Upload(1, id, data);

        Observe(2, EncodeId(BasisNetworkClipCache.OpServerCacheCatalog, id), toServer: true);
        List<byte[]> offers = PayloadsWithOp(joiner, BasisNetworkClipCache.OpServerCacheOffer);
        Assert.Single(offers);
        Assert.Equal(1, StampOf(joiner.Sent[0]));

        Observe(2, EncodeId(BasisNetworkClipCache.OpServerCacheRequest, id), toServer: true);
        Drain();

        List<byte[]> chunks = PayloadsWithOp(joiner, BasisNetworkClipCache.OpChunk);
        Assert.Equal(4, chunks.Count);
        byte[] rebuilt = new byte[data.Length];
        foreach (byte[] chunk in chunks)
        {
            int index = BitConverter.ToInt32(chunk, 17);
            Buffer.BlockCopy(chunk, BasisNetworkClipCache.ChunkHeaderBytes, rebuilt, index * ChunkBytes, chunk.Length - BasisNetworkClipCache.ChunkHeaderBytes);
        }
        Assert.Equal(data, rebuilt);
    }

    [Fact]
    public void ReadyPeers_AreOfferedAClipWhenItFinishesArriving()
    {
        RegisterPeer(1);
        ImageCacheRecordingPeer early = RegisterPeer(2);
        Observe(2, EncodeId(BasisNetworkClipCache.OpServerCacheCatalog, Guid.Empty), toServer: true);
        Assert.Empty(early.Sent);

        Upload(1, Guid.NewGuid(), MakeData(ChunkBytes, 3));

        Assert.Single(PayloadsWithOp(early, BasisNetworkClipCache.OpServerCacheOffer));
    }

    [Fact]
    public void Need_ServesOnlyTheListedChunks()
    {
        RegisterPeer(1);
        ImageCacheRecordingPeer joiner = RegisterPeer(2);
        Guid id = Guid.NewGuid();
        Upload(1, id, MakeData(ChunkBytes * 4, 4));

        Observe(2, EncodeNeed(id, 1, 3), toServer: true);
        Drain();

        List<byte[]> chunks = PayloadsWithOp(joiner, BasisNetworkClipCache.OpChunk);
        Assert.Equal(new[] { 1, 3 }, chunks.Select(chunk => BitConverter.ToInt32(chunk, 17)).ToArray());
    }

    [Fact]
    public void Need_SentToTheSharerIsNotAnsweredByTheServer()
    {
        RegisterPeer(1);
        ImageCacheRecordingPeer joiner = RegisterPeer(2);
        Guid id = Guid.NewGuid();
        Upload(1, id, MakeData(ChunkBytes * 2, 5));

        Observe(2, EncodeNeed(id, 0), toServer: false);

        Assert.Equal(0, BasisNetworkClipCache.PendingDeliveries);
        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void OwnerLeaving_DropsTheirClipsAndDeletesTheFiles()
    {
        RegisterPeer(1);
        Upload(1, Guid.NewGuid(), MakeData(ChunkBytes, 6));
        Assert.NotEmpty(Directory.GetFiles(_directory, "*.clip", SearchOption.AllDirectories));

        BasisNetworkClipCache.RemovePlayer(1);

        Assert.Equal(0, BasisNetworkClipCache.Count);
        Assert.Equal(0, BasisNetworkClipCache.TotalBytes);
        Assert.Empty(Directory.GetFiles(_directory, "*.clip", SearchOption.AllDirectories));
    }

    [Fact]
    public void Remove_ByAnotherPlayerRespectsProtection()
    {
        RegisterPeer(1);
        RegisterPeer(2);
        Guid open = Guid.NewGuid();
        Guid guarded = Guid.NewGuid();
        Observe(1, EncodeOffer(open, ChunkBytes), toServer: true);
        Observe(1, EncodeOffer(guarded, ChunkBytes, flags: 1), toServer: true);

        Observe(2, EncodeId(BasisNetworkClipCache.OpRemove, open));
        Observe(2, EncodeId(BasisNetworkClipCache.OpRemove, guarded));

        Assert.Equal(1, BasisNetworkClipCache.Count);
        Observe(1, EncodeId(BasisNetworkClipCache.OpRemove, guarded));
        Assert.Equal(0, BasisNetworkClipCache.Count);
    }

    [Fact]
    public void Chunks_FromAnyoneButTheOwnerAreIgnored()
    {
        RegisterPeer(1);
        RegisterPeer(2);
        Guid id = Guid.NewGuid();
        byte[] data = MakeData(ChunkBytes, 7);
        Observe(1, EncodeOffer(id, data.Length), toServer: true);

        Observe(2, EncodeChunk(id, 0, MakeData(ChunkBytes, 8)));

        Assert.Equal(0, BasisNetworkClipCache.CompleteCount);
    }

    [Fact]
    public void Offers_PastTheSizeLimitOrPerPlayerLimitAreNotCached()
    {
        NetworkServer.Configuration.VideoClipCacheMaxMegabytes = 64;
        NetworkServer.Configuration.VideoClipMaxMegabytes = 16;
        NetworkServer.Configuration.VideoClipMaxPerPlayer = 1;
        BasisGlobalLockManager.InitializeFromConfig(NetworkServer.Configuration);
        RegisterPeer(1);

        Observe(1, EncodeOffer(Guid.NewGuid(), 17 * 1024 * 1024), toServer: true);
        Assert.Equal(0, BasisNetworkClipCache.Count);

        Observe(1, EncodeOffer(Guid.NewGuid(), ChunkBytes), toServer: true);
        Observe(1, EncodeOffer(Guid.NewGuid(), ChunkBytes), toServer: true);
        Assert.Equal(1, BasisNetworkClipCache.Count);
    }

    [Fact]
    public void FullCache_EvictsTheHeaviestOwnersOldestClipAndTellsThem()
    {
        ImageCacheRecordingPeer heavy = RegisterPeer(1);
        RegisterPeer(2);
        Guid first = Guid.NewGuid();
        Observe(1, EncodeOffer(first, 600 * 1024), toServer: true);
        Observe(1, EncodeOffer(Guid.NewGuid(), 300 * 1024), toServer: true);

        Observe(2, EncodeOffer(Guid.NewGuid(), 300 * 1024), toServer: true);

        Assert.Equal(2, BasisNetworkClipCache.Count);
        byte[] dropped = PayloadsWithOp(heavy, BasisNetworkClipCache.OpServerCacheState).Last();
        Assert.Equal(first, new Guid(dropped.AsSpan(1, 16)));
        Assert.Equal(BasisNetworkClipCache.StateNotHeld, dropped[17]);
    }

    [Fact]
    public void Pose_IsWrittenIntoLaterOffers()
    {
        RegisterPeer(1);
        ImageCacheRecordingPeer joiner = RegisterPeer(2);
        Guid id = Guid.NewGuid();
        Upload(1, id, MakeData(ChunkBytes, 9));
        byte[] pose = new byte[1 + 16 + 32];
        pose[0] = BasisNetworkClipCache.OpPose;
        id.ToByteArray().CopyTo(pose, 1);
        BitConverter.GetBytes(12.5f).CopyTo(pose, 17);

        Observe(2, pose);
        Observe(2, EncodeId(BasisNetworkClipCache.OpServerCacheCatalog, id), toServer: true);

        byte[] offer = Assert.Single(PayloadsWithOp(joiner, BasisNetworkClipCache.OpServerCacheOffer));
        Assert.Equal(12.5f, BitConverter.ToSingle(offer, 23));
    }

    [Fact]
    public void Disabled_CachesNothing()
    {
        NetworkServer.Configuration.VideoClipCacheEnabled = false;
        ImageCacheRecordingPeer owner = RegisterPeer(1);

        Upload(1, Guid.NewGuid(), MakeData(ChunkBytes, 10));

        Assert.Equal(0, BasisNetworkClipCache.Count);
        Assert.Empty(owner.Sent);
    }
}
