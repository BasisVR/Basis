using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkCore;
using BasisNetworkServer.Security;
using BasisPermissions;
using System.Net;
using System.Text;
using Xunit;

namespace BasisServerTests;

/// <summary>
/// Records what the cache actually put on the wire. The channel matters as much as the bytes: a
/// client routes scene data to a different handler table per channel, so a payload delivered on the
/// wrong one is silently never dispatched.
/// </summary>
internal sealed class ImageCacheRecordingPeer : NetPeer
{
    public readonly List<(byte Channel, byte[] Data)> Sent = new();

    public ImageCacheRecordingPeer(int id) => Id = id;

    public int Id { get; }
    public IPAddress Address => IPAddress.Loopback;
    public int RemoteId => Id;
    public int RoundTripTime => 0;
    public float TimeSinceLastPacket => 0f;
    public long RemoteTimeDelta => 0;
    public int Mtu => 1200;
    public object? Tag { get; set; }

    public void Disconnect() { }
    public void Disconnect(byte[] b) { }
    public void DisconnectForce() { }

    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod) =>
        Sent.Add((channelNumber, (byte[])data.Clone()));

    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod) =>
        Sent.Add((channelNumber, data.CopyData()));

    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0) { }

    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => 0;
}

/// <summary>
/// Covers the server-side image buffer: what it retains, when it hands images to a joiner, and the
/// per-owner fairness that stops one player's uploads crowding out everyone else's.
///
/// These drive <see cref="BasisNetworkImageCache.Observe"/> with the same bytes a client puts on
/// the wire, so the header walking (including the variable-length owner name) is exercised for real
/// rather than against a convenient stand-in.
/// </summary>
// Mutates NetworkServer.Configuration and BasisNetworkIDDatabase, both process-wide statics, so it
// shares the collection that serialises every other test touching them.
[Collection("BasisServer shared network statics")]
public class BasisNetworkImageCacheTests : IDisposable
{
    /// <summary>Chunk payload sized so a megabyte-granularity budget still exercises eviction.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>Server to client offer, mirrored from the cache's wire protocol.</summary>
    private const byte OpServerCacheOffer = 9;

    private const byte OpSpawn = 1;
    private const byte OpChunk = 2;
    private const byte OpTransform = 3;
    private const byte OpDespawn = 4;
    private const byte OpAnimationSpawn = 6;
    private const byte OpAnimationChunk = 7;

    private const ushort ManagerNetId = 4242;

    private readonly Configuration _previous;
    private readonly List<int> _registeredPeerIds = new();

    public BasisNetworkImageCacheTests()
    {
        _previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration
        {
            ImageCacheEnabled = true,
            ImageCacheMaxMegabytes = 4,
            ImageCacheMinimumPerOwnerMegabytes = 0,
            // Replay inline, which is what 0 means. These tests are about WHAT the cache serves,
            // not how fast; leaving pacing on would make every one of them drive a pump to observe
            // a result that has nothing to do with what it is asserting. The paced path has its own
            // tests in BasisImageBandwidthGovernorTests.
            ImageShareDownloadMegabitsPerSecond = 0,
            ImagePickupRangeMeters = 0f,
        };

        BasisNetworkImageCache.Reset();
        BasisImageBandwidthGovernor.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase[BasisNetworkImageCache.ImageManagerIdentifier] = ManagerNetId;
    }

    public void Dispose()
    {
        BasisNetworkImageCache.Reset();
        BasisNetworkIDDatabase.UshortNetworkDatabase.TryRemove(BasisNetworkImageCache.ImageManagerIdentifier, out _);
        NetworkServer.Configuration = _previous;
        foreach (int id in _registeredPeerIds)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Registers a stand-in peer the cache can reach through <see cref="NetworkServer.AuthenticatedPeers"/>,
    /// remembering it so teardown removes only what this fixture added — the dictionary is shared with
    /// every other test in the collection.
    /// </summary>
    private ImageCacheRecordingPeer RegisterPeer(int id)
    {
        ImageCacheRecordingPeer peer = new ImageCacheRecordingPeer(id);
        NetworkServer.AuthenticatedPeers[id] = peer;
        _registeredPeerIds.Add(id);
        return peer;
    }

    // ---- wire helpers: byte-for-byte what BasisImagePickupManager encodes -------------------

    /// <summary>
    /// The image payload inside one recorded send. ServerSceneDataMessage puts the player id and the
    /// message index in front of it and writes the payload raw, so the offset is fixed.
    /// </summary>
    private static byte[] PayloadOf((byte Channel, byte[] Data) sent)
    {
        const int PayloadOffset = 2 + 2;
        byte[] payload = new byte[sent.Data.Length - PayloadOffset];
        Buffer.BlockCopy(sent.Data, PayloadOffset, payload, 0, payload.Length);
        return payload;
    }

    private static byte PayloadOpcode((byte Channel, byte[] Data) sent) => PayloadOf(sent)[0];

    private static byte[] EncodeSpawn(
        Guid id,
        ushort ownerId,
        string ownerName,
        int totalChunks,
        float positionX = 0f,
        float positionY = 0f,
        float positionZ = 0f
    )
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(OpSpawn);
        writer.Write(id.ToByteArray());
        writer.Write(ownerId);
        writer.Write(ownerName);
        writer.Write(64);
        writer.Write(64);
        writer.Write(totalChunks * 16);
        writer.Write(totalChunks);
        writer.Write(positionX);
        writer.Write(positionY);
        writer.Write(positionZ);
        for (int axis = 0; axis < 4; axis++)
        {
            writer.Write(0f);
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeChunk(Guid id, int chunkIndex, int payloadBytes, byte opcode = OpChunk)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(opcode);
        writer.Write(id.ToByteArray());
        writer.Write(chunkIndex);
        writer.Write(payloadBytes);
        writer.Write(new byte[payloadBytes]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeTransform(
        Guid id,
        float positionX,
        float positionY,
        float positionZ,
        float rotationX = 0f,
        float rotationY = 0f,
        float rotationZ = 0f,
        float rotationW = 1f,
        float scale = 1f
    )
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(OpTransform);
        writer.Write(id.ToByteArray());
        writer.Write(positionX);
        writer.Write(positionY);
        writer.Write(positionZ);
        writer.Write(rotationX);
        writer.Write(rotationY);
        writer.Write(rotationZ);
        writer.Write(rotationW);
        writer.Write(scale);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>
    /// Walks a spawn header or an offer the way the client does, so the pose is read past the
    /// variable-length owner name rather than from an offset this fixture assumes. Position and
    /// rotation together, because a picture on a wall is turned as well as placed and the two are
    /// one seven-float block that the cache either carries across or does not.
    /// </summary>
    private static (float X, float Y, float Z, float RX, float RY, float RZ, float RW) ReadSpawnPose(byte[] header)
    {
        using MemoryStream stream = new MemoryStream(header, false);
        using BinaryReader reader = new BinaryReader(stream, Encoding.UTF8);
        reader.ReadByte();
        reader.ReadBytes(16);
        reader.ReadUInt16();
        reader.ReadString();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        return (
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle(),
            reader.ReadSingle()
        );
    }

    private static byte[] EncodeAnimationSpawn(Guid id, int totalChunks)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(OpAnimationSpawn);
        writer.Write(id.ToByteArray());
        writer.Write((byte)2); // AnimationFormatNativeLz4
        writer.Write(totalChunks * 16);
        writer.Write(totalChunks);
        writer.Write(0L);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] EncodeDespawn(Guid id)
    {
        using MemoryStream stream = new MemoryStream();
        using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(OpDespawn);
        writer.Write(id.ToByteArray());
        writer.Flush();
        return stream.ToArray();
    }

    private static void Observe(ushort sender, byte[] payload)
    {
        // HandleScene reaches Observe only through IsImageTraffic, which is also what resolves the
        // manager's network id. Replaying and the owner notice both need that id, so going through
        // the same gate here keeps the fixture honest about the order the server does it in.
        BasisNetworkImageCache.IsImageTraffic(ManagerNetId);
        BasisNetworkImageCache.Observe(sender, payload, payload.Length);
    }

    /// <summary>
    /// Observes a share the sharer aimed at a specific set of players, which is what a client that
    /// range-filters its own sends produces.
    /// </summary>
    private static void ObserveTargeted(ushort sender, byte[] payload, ushort[] recipients)
    {
        BasisNetworkImageCache.IsImageTraffic(ManagerNetId);
        BasisNetworkImageCache.Observe(sender, payload, payload.Length, recipients, recipients.Length);
    }

    private static Guid ShareImage(
        ushort owner,
        int chunks = 2,
        int chunkBytes = ChunkBytes,
        string name = "Sharer",
        float positionX = 0f,
        float positionY = 0f,
        float positionZ = 0f
    )
    {
        Guid id = Guid.NewGuid();
        Observe(owner, EncodeSpawn(id, owner, name, chunks, positionX, positionY, positionZ));
        for (int index = 0; index < chunks; index++)
        {
            Observe(owner, EncodeChunk(id, index, chunkBytes));
        }
        return id;
    }

    // ---- identification --------------------------------------------------------------------

    [Fact]
    public void IsImageTraffic_MatchesOnlyTheImageManagersNetworkId()
    {
        Assert.True(BasisNetworkImageCache.IsImageTraffic(ManagerNetId));
        Assert.False(BasisNetworkImageCache.IsImageTraffic(ManagerNetId + 1));
    }

    // ---- retention -------------------------------------------------------------------------

    [Fact]
    public void AFullyReceivedImage_IsHeldAndServable()
    {
        ShareImage(owner: 7);

        Assert.Equal(1, BasisNetworkImageCache.Count);
        Assert.Equal(1, BasisNetworkImageCache.ServableCount);
        Assert.True(BasisNetworkImageCache.TotalBytes > 0);
    }

    [Fact]
    public void AnImageMissingChunks_IsHeldButNotServed()
    {
        // A joiner must never be handed a half-received picture; it stays pending until complete.
        Guid id = Guid.NewGuid();
        Observe(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        Assert.Equal(1, BasisNetworkImageCache.Count);
        Assert.Equal(0, BasisNetworkImageCache.ServableCount);
    }

    [Fact]
    public void NetIdZeroOwner_IsCachedLikeAnyOtherPlayer()
    {
        // Peer ids are handed out from zero up, so the first player to join is net id 0 and must
        // not be mistaken for a blank owner.
        ShareImage(owner: 0);

        Assert.Equal(1, BasisNetworkImageCache.ServableCount);
        Assert.True(BasisNetworkImageCache.BytesHeldFor(0) > 0);
    }

    [Fact]
    public void AnimationPayloads_AreHeldAlongsideTheStill()
    {
        Guid id = ShareImage(owner: 7);
        long stillOnly = BasisNetworkImageCache.TotalBytes;

        Observe(7, EncodeAnimationSpawn(id, totalChunks: 2));
        Observe(7, EncodeChunk(id, 0, ChunkBytes, OpAnimationChunk));
        Observe(7, EncodeChunk(id, 1, ChunkBytes, OpAnimationChunk));

        Assert.True(BasisNetworkImageCache.TotalBytes > stillOnly);
        Assert.Equal(1, BasisNetworkImageCache.Count);
    }

    [Fact]
    public void ARepeatedSpawnHeader_DoesNotDoubleCount()
    {
        Guid id = Guid.NewGuid();
        byte[] spawn = EncodeSpawn(id, 7, "Sharer", totalChunks: 1);

        Observe(7, spawn);
        long afterFirst = BasisNetworkImageCache.TotalBytes;
        Observe(7, spawn);

        Assert.Equal(afterFirst, BasisNetworkImageCache.TotalBytes);
        Assert.Equal(1, BasisNetworkImageCache.Count);
    }

    // ---- removal ---------------------------------------------------------------------------

    [Fact]
    public void TheOwnersDespawn_ClearsTheServerCopy()
    {
        Guid id = ShareImage(owner: 7);

        Observe(7, EncodeDespawn(id));

        Assert.Equal(0, BasisNetworkImageCache.Count);
        Assert.Equal(0, BasisNetworkImageCache.TotalBytes);
    }

    [Fact]
    public void ADespawnFromSomebodyElse_LeavesTheServerCopyAlone()
    {
        // Anyone may ask; only the player who shared it removes the server's copy.
        Guid id = ShareImage(owner: 7);

        Observe(9, EncodeDespawn(id));

        Assert.Equal(1, BasisNetworkImageCache.Count);
    }

    [Fact]
    public void RemoveRequest_ClearsRegardlessOfRequesterWhenNotOwnerGated()
    {
        // The moderation path removes on someone else's behalf, so it opts out of the owner gate.
        Guid id = ShareImage(owner: 7);

        Assert.True(BasisNetworkImageCache.Remove(id, requesterId: 9, ownerOnly: false));
        Assert.Equal(0, BasisNetworkImageCache.Count);
    }

    [Fact]
    public void WhenTheSharerDisconnects_TheirImagesAreDropped()
    {
        ShareImage(owner: 7);
        ShareImage(owner: 7);
        ShareImage(owner: 9);

        BasisNetworkImageCache.RemovePlayerImages(7);

        Assert.Equal(1, BasisNetworkImageCache.Count);
        Assert.Equal(0, BasisNetworkImageCache.BytesHeldFor(7));
        Assert.True(BasisNetworkImageCache.BytesHeldFor(9) > 0);
    }

    // ---- budget and fairness ---------------------------------------------------------------

    [Fact]
    public void AnImageBiggerThanTheWholeBuffer_IsNotCached()
    {
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 1;

        ShareImage(owner: 7, chunks: 2, chunkBytes: 1024 * 1024);

        Assert.Equal(0, BasisNetworkImageCache.ServableCount);
        Assert.True(BasisNetworkImageCache.TotalBytes <= 1024 * 1024);
    }

    [Fact]
    public void TheBufferNeverExceedsItsCap()
    {
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 1;
        long cap = 1024 * 1024;

        for (int index = 0; index < 40; index++)
        {
            ShareImage(owner: (ushort)(index % 4), chunks: 2);
            Assert.True(
                BasisNetworkImageCache.TotalBytes <= cap,
                $"cache overran its cap after {index + 1} shares"
            );
        }
    }

    [Fact]
    public void OnePlayerFloodingTheBuffer_CannotEvictAnotherPlayersImages()
    {
        // The fairness rule: an owner over their slice evicts their OWN oldest image. Without it,
        // whoever uploads most simply deletes everybody else's pictures from the cache.
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 1;

        ShareImage(owner: 1, chunks: 1);
        long quietOwnerBytes = BasisNetworkImageCache.BytesHeldFor(1);
        Assert.True(quietOwnerBytes > 0);

        for (int index = 0; index < 30; index++)
        {
            ShareImage(owner: 2, chunks: 2);
        }

        Assert.Equal(quietOwnerBytes, BasisNetworkImageCache.BytesHeldFor(1));
    }

    [Fact]
    public void AnOwnerOverTheirShare_LosesTheirOwnOldestImageFirst()
    {
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 1;

        Guid oldest = ShareImage(owner: 5, chunks: 1);
        for (int index = 0; index < 30; index++)
        {
            ShareImage(owner: 5, chunks: 1);
        }

        // The first image they shared is the first to go, and they still hold something.
        Assert.False(BasisNetworkImageCache.Remove(oldest, requesterId: 5, ownerOnly: true));
        Assert.True(BasisNetworkImageCache.BytesHeldFor(5) > 0);
    }

    // ---- disabled ---------------------------------------------------------------------------

    [Fact]
    public void WithTheCacheOff_NothingIsRetained()
    {
        NetworkServer.Configuration.ImageCacheEnabled = false;

        ShareImage(owner: 7);

        Assert.Equal(0, BasisNetworkImageCache.Count);
        Assert.Equal(0, BasisNetworkImageCache.TotalBytes);
    }

    [Fact]
    public void WithAZeroBudget_NothingIsRetained()
    {
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 0;

        ShareImage(owner: 7);

        Assert.Equal(0, BasisNetworkImageCache.Count);
    }

    // ---- offer to a joiner, replay on request -------------------------------------------------

    /// <summary>
    /// Joining costs a catalogue, not a gallery. One offer per image and not a single chunk until
    /// the client has decided the picture is close enough to be worth having.
    /// </summary>
    [Fact]
    public void AJoinerIsOfferedEachImageAndSentNoChunks()
    {
        ShareImage(owner: 7, chunks: 3);
        ShareImage(owner: 7, chunks: 3);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Equal(2, joiner.Sent.Count);
        Assert.All(joiner.Sent, sent => Assert.Equal(OpServerCacheOffer, PayloadOpcode(sent)));
    }

    /// <summary>
    /// The offer is the sharer's own spawn header with one byte changed, so the position the client
    /// needs rides along without the server ever reading it.
    /// </summary>
    [Fact]
    public void AnOfferCarriesTheSharersSpawnHeaderVerbatimApartFromTheOpcode()
    {
        Guid id = ShareImage(owner: 7, chunks: 2, positionX: 12.5f, positionZ: -3f);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        byte[] offer = PayloadOf(joiner.Sent[0]);
        byte[] expected = EncodeSpawn(id, 7, "Sharer", 2, 12.5f, 0f, -3f);
        expected[0] = OpServerCacheOffer;
        Assert.Equal(expected, offer);
    }

    [Fact]
    public void RequestingAnOfferedImage_SendsTheSpawnAndEveryChunk()
    {
        Guid id = ShareImage(owner: 7, chunks: 3);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);
        joiner.Sent.Clear();

        BasisNetworkImageCache.ServeRequestedImage(9, id);

        Assert.Equal(4, joiner.Sent.Count);
    }

    [Fact]
    public void ReplayedImages_GoOutOnTheChannelTheImageManagerListensOn()
    {
        // The image pickup manager registers a *direct* scene handler, so its traffic reaches it
        // only on DirectSceneServerChannel. Replaying on SceneChannel lands in the other handler
        // table, where nothing is registered, and the joiner silently sees no image at all.
        Guid id = ShareImage(owner: 7, chunks: 2);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);
        BasisNetworkImageCache.ServeRequestedImage(9, id);

        Assert.NotEmpty(joiner.Sent);
        Assert.All(joiner.Sent, sent => Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, sent.Channel));
    }

    [Fact]
    public void AnIncompleteImage_IsNotOffered()
    {
        Guid id = Guid.NewGuid();
        Observe(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Empty(joiner.Sent);
    }

    /// <summary>
    /// A request for something never offered - or never finished arriving - buys nothing. The cache
    /// answers requests, it does not take instructions.
    /// </summary>
    [Fact]
    public void RequestingAnIncompleteImage_SendsNothing()
    {
        Guid id = Guid.NewGuid();
        Observe(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 3));
        Observe(7, EncodeChunk(id, 0, ChunkBytes));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.ServeRequestedImage(9, id);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void RequestingTheSameImageTwice_SendsItOnce()
    {
        Guid id = ShareImage(owner: 7, chunks: 2);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.ServeRequestedImage(9, id);
        int first = joiner.Sent.Count;
        Assert.True(first > 0);

        joiner.Sent.Clear();
        BasisNetworkImageCache.ServeRequestedImage(9, id);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void APeerIsOfferedAnImageOnlyOnce()
    {
        ShareImage(owner: 7);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);
        Assert.NotEmpty(joiner.Sent);

        joiner.Sent.Clear();
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Empty(joiner.Sent);
    }

    /// <summary>
    /// The sharer already sent this to peer 9, and the relay saw exactly who it was aimed at.
    /// Offering it back would invite them to download a picture they are already holding.
    /// </summary>
    [Fact]
    public void APeerTheSharerAlreadyTargetedIsNotOffered()
    {
        ImageCacheRecordingPeer nearby = RegisterPeer(9);

        Guid id = Guid.NewGuid();
        ushort[] targeted = { 9 };
        ObserveTargeted(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 2), targeted);
        for (int index = 0; index < 2; index++)
        {
            ObserveTargeted(7, EncodeChunk(id, index, ChunkBytes), targeted);
        }
        nearby.Sent.Clear();

        BasisNetworkImageCache.OfferCachedImagesToPeer(nearby);

        Assert.Empty(nearby.Sent);
    }

    /// <summary>
    /// The other half: somebody the sharer decided was too far away is exactly who the cache exists
    /// for, and they are told the moment the image finishes arriving rather than on their next join.
    /// </summary>
    [Fact]
    public void APeerTheSharerCouldNotReachIsOfferedTheImageAsItCompletes()
    {
        ImageCacheRecordingPeer latecomer = RegisterPeer(11);

        Guid id = Guid.NewGuid();
        ushort[] targeted = { 9 };
        ObserveTargeted(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 2), targeted);
        for (int index = 0; index < 2; index++)
        {
            ObserveTargeted(7, EncodeChunk(id, index, ChunkBytes), targeted);
        }

        Assert.Single(latecomer.Sent);
        Assert.Equal(OpServerCacheOffer, PayloadOpcode(latecomer.Sent[0]));
    }

    [Fact]
    public void AnOwnerIsNeverOfferedTheirOwnImage()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(7);

        ShareImage(owner: 7);
        owner.Sent.Clear();
        BasisNetworkImageCache.OfferCachedImagesToPeer(owner);

        Assert.Empty(owner.Sent);
    }

    [Fact]
    public void WithTheCacheOff_AJoinerIsOfferedNothing()
    {
        ShareImage(owner: 7);
        NetworkServer.Configuration.ImageCacheEnabled = false;

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Empty(joiner.Sent);
    }

    [Fact]
    public void TheOwnerIsToldOnTheSameChannelWhenTheirImageBecomesServable()
    {
        // The owner only stops re-uploading to each arrival if this notice reaches its handler,
        // which is the direct table again.
        ImageCacheRecordingPeer owner = RegisterPeer(7);

        ShareImage(owner: 7, chunks: 2);

        Assert.NotEmpty(owner.Sent);
        Assert.All(owner.Sent, sent => Assert.Equal(BasisNetworkCommons.DirectSceneServerChannel, sent.Channel));
    }

    [Fact]
    public void EvictingAnImage_TellsItsOwnerTheyAreProvidingItAgain()
    {
        ImageCacheRecordingPeer owner = RegisterPeer(5);
        NetworkServer.Configuration.ImageCacheMaxMegabytes = 1;

        ShareImage(owner: 5, chunks: 1);
        int afterFirstShare = owner.Sent.Count;
        for (int index = 0; index < 30; index++)
        {
            ShareImage(owner: 5, chunks: 1);
        }

        Assert.True(owner.Sent.Count > afterFirstShare);
    }

    // ---- GIF lock ------------------------------------------------------------------------------

    private static Guid ShareAnimatedImage(ushort owner, int chunks = 2, int animationChunks = 2)
    {
        Guid id = ShareImage(owner, chunks);
        Observe(owner, EncodeAnimationSpawn(id, animationChunks));
        for (int index = 0; index < animationChunks; index++)
        {
            Observe(owner, EncodeChunk(id, index, ChunkBytes, OpAnimationChunk));
        }
        return id;
    }

    private static int CountOpcode(ImageCacheRecordingPeer peer, byte opcode) =>
        peer.Sent.Count(sent => PayloadOpcode(sent) == opcode);

    [Fact]
    public void IsAnimationPayload_MatchesOnlyTheAnimationOpcodes()
    {
        Assert.True(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpAnimationSpawn, 0 }, 2));
        Assert.True(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpAnimationChunk }, 1));
        Assert.False(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpSpawn }, 1));
        Assert.False(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpChunk }, 1));
        Assert.False(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpTransform }, 1));
        Assert.False(BasisNetworkImageCache.IsAnimationPayload(new byte[] { OpAnimationChunk }, 0));
        Assert.False(BasisNetworkImageCache.IsAnimationPayload(null, 0));
    }

    [Fact]
    public void IsGifBlockedForUuid_BlocksOnlyLockedUsersWithoutTheGlobalLockNode()
    {
        PermissionManager manager = PermissionManager.PermissionIntegration.Manager;
        string plainUuid = $"gif-plain-{Guid.NewGuid():N}";
        string bypassUuid = $"gif-bypass-{Guid.NewGuid():N}";
        bool wasLocked = BasisGlobalLockManager.GifsLocked;
        if (wasLocked)
        {
            BasisGlobalLockManager.ToggleGifs();
        }

        try
        {
            manager.AddUserNode(bypassUuid, PermNodes.ModerationGlobalLock);

            Assert.False(BasisNetworkImageCache.IsGifBlockedForUuid(plainUuid));
            Assert.False(BasisNetworkImageCache.IsGifBlockedForUuid(bypassUuid));

            Assert.True(BasisGlobalLockManager.ToggleGifs());
            Assert.True(BasisNetworkImageCache.IsGifBlockedForUuid(plainUuid));
            Assert.True(BasisNetworkImageCache.IsGifBlockedForUuid(null));
            Assert.False(BasisNetworkImageCache.IsGifBlockedForUuid(bypassUuid));
        }
        finally
        {
            manager.RemoveUserNode(bypassUuid, PermNodes.ModerationGlobalLock);
            if (BasisGlobalLockManager.GifsLocked != wasLocked)
            {
                BasisGlobalLockManager.ToggleGifs();
            }
        }
    }

    [Fact]
    public void WhileGifsAreLocked_ARequesterGetsTheStillAndTheAnimationFollowsOnUnlock()
    {
        Guid id = ShareAnimatedImage(owner: 7);
        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        bool wasLocked = BasisGlobalLockManager.GifsLocked;
        if (!wasLocked)
        {
            BasisGlobalLockManager.ToggleGifs();
        }

        try
        {
            BasisNetworkImageCache.ServeRequestedImage(9, id);
            Assert.Equal(1, CountOpcode(joiner, OpSpawn));
            Assert.Equal(2, CountOpcode(joiner, OpChunk));
            Assert.Equal(0, CountOpcode(joiner, OpAnimationSpawn));
            Assert.Equal(0, CountOpcode(joiner, OpAnimationChunk));

            BasisNetworkImageCache.ResumeAnimationsAfterUnlock();
            Assert.Equal(0, CountOpcode(joiner, OpAnimationSpawn));
        }
        finally
        {
            if (BasisGlobalLockManager.GifsLocked)
            {
                BasisGlobalLockManager.ToggleGifs();
            }
        }

        BasisNetworkImageCache.ResumeAnimationsAfterUnlock();
        Assert.Equal(1, CountOpcode(joiner, OpAnimationSpawn));
        Assert.Equal(2, CountOpcode(joiner, OpAnimationChunk));

        joiner.Sent.Clear();
        BasisNetworkImageCache.ResumeAnimationsAfterUnlock();
        Assert.Empty(joiner.Sent);

        if (wasLocked)
        {
            BasisGlobalLockManager.ToggleGifs();
        }
    }

    [Fact]
    public void ARequesterServedBeforeTheAnimationFinished_IsSentItWhenItCompletes()
    {
        Guid id = ShareImage(owner: 7, chunks: 2);
        Observe(7, EncodeAnimationSpawn(id, 2));
        Observe(7, EncodeChunk(id, 0, ChunkBytes, OpAnimationChunk));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.ServeRequestedImage(9, id);
        Assert.Equal(1, CountOpcode(joiner, OpSpawn));
        Assert.Equal(0, CountOpcode(joiner, OpAnimationSpawn));

        Observe(7, EncodeChunk(id, 1, ChunkBytes, OpAnimationChunk));

        Assert.Equal(1, CountOpcode(joiner, OpAnimationSpawn));
        Assert.Equal(2, CountOpcode(joiner, OpAnimationChunk));
    }

    [Fact]
    public void ThePeersTheSharerSentTheAnimationTo_AreNotSentItAgainByTheCache()
    {
        ImageCacheRecordingPeer nearby = RegisterPeer(9);

        Guid id = Guid.NewGuid();
        ushort[] targeted = { 9 };
        ObserveTargeted(7, EncodeSpawn(id, 7, "Sharer", totalChunks: 1), targeted);
        ObserveTargeted(7, EncodeChunk(id, 0, ChunkBytes), targeted);
        ObserveTargeted(7, EncodeAnimationSpawn(id, 1), targeted);
        ObserveTargeted(7, EncodeChunk(id, 0, ChunkBytes, OpAnimationChunk), targeted);

        Assert.Equal(0, CountOpcode(nearby, OpAnimationSpawn));
        BasisNetworkImageCache.ResumeAnimationsAfterUnlock();
        Assert.Equal(0, CountOpcode(nearby, OpAnimationSpawn));
    }

    // ---- where the picture actually is -------------------------------------------------------

    /// <summary>
    /// A spawn header says where a picture was hung, and pictures get carried around. The offer is
    /// the only thing a joiner measures its distance against, so a stale one both draws the card in
    /// the wrong place and can decide a picture propped against the joiner is too far away to want.
    /// </summary>
    [Fact]
    public void AnOfferCarriesWhereTheImageIsNow_NotWhereItWasSpawned()
    {
        // Turned as well as moved: a picture hung flat on one wall and re-hung on the wall opposite
        // is at a new position AND a new facing, and the two travel as one block.
        Guid id = ShareImage(owner: 7, chunks: 2, positionX: 12.5f, positionZ: -3f);
        Observe(7, EncodeTransform(id, 1f, 2f, 3f, rotationY: 0.7071f, rotationW: 0.7071f));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Equal((1f, 2f, 3f, 0f, 0.7071f, 0f, 0.7071f), ReadSpawnPose(PayloadOf(joiner.Sent[0])));
    }

    /// <summary>
    /// Control of a card passes to whoever picks it up, so the player who moved a picture is very
    /// often not the player who shared it. Following only the owner would leave every borrowed card
    /// frozen where it was put down.
    /// </summary>
    [Fact]
    public void ATransformFromWhoeverPickedTheImageUp_IsFollowed()
    {
        Guid id = ShareImage(owner: 7, chunks: 2);
        Observe(11, EncodeTransform(id, 4f, 5f, 6f, rotationX: 0.5f, rotationW: 0.5f));

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Equal((4f, 5f, 6f, 0.5f, 0f, 0f, 0.5f), ReadSpawnPose(PayloadOf(joiner.Sent[0])));
    }

    [Fact]
    public void RequestingAnImageThatMoved_ReplaysThePoseAheadOfTheChunks()
    {
        // Ahead of the chunks because the receiver raises its card off the header: a transform
        // arriving after the last chunk would leave the card loading in the wrong place, and the
        // transform is also the only payload carrying scale.
        Guid id = ShareImage(owner: 7, chunks: 3);
        byte[] moved = EncodeTransform(id, 1f, 2f, 3f, rotationZ: 0.3827f, rotationW: 0.9239f, scale: 2.5f);
        Observe(7, moved);

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);
        joiner.Sent.Clear();

        BasisNetworkImageCache.ServeRequestedImage(9, id);

        Assert.Equal(5, joiner.Sent.Count);
        Assert.Equal(OpSpawn, PayloadOpcode(joiner.Sent[0]));
        Assert.Equal((1f, 2f, 3f, 0f, 0f, 0.3827f, 0.9239f), ReadSpawnPose(PayloadOf(joiner.Sent[0])));
        Assert.Equal(moved, PayloadOf(joiner.Sent[1]));
        Assert.All(joiner.Sent.Skip(2), sent => Assert.Equal(OpChunk, PayloadOpcode(sent)));
    }

    [Fact]
    public void RepeatedTransforms_AreChargedOnce()
    {
        // A card being dragged across a room sends one of these several times a second; each has to
        // overwrite the last rather than accumulate, or moving a picture slowly evicts the room.
        Guid id = ShareImage(owner: 7, chunks: 2);
        long beforeAnyPose = BasisNetworkImageCache.TotalBytes;

        Observe(7, EncodeTransform(id, 1f, 0f, 0f));
        long afterFirstPose = BasisNetworkImageCache.TotalBytes;
        for (int step = 0; step < 32; step++)
        {
            Observe(7, EncodeTransform(id, step, 0f, 0f));
        }

        Assert.True(afterFirstPose > beforeAnyPose);
        Assert.Equal(afterFirstPose, BasisNetworkImageCache.TotalBytes);
    }

    [Fact]
    public void ATransformOfTheWrongLength_LeavesThePoseAlone()
    {
        Guid id = ShareImage(owner: 7, chunks: 2, positionX: 12.5f);
        Observe(7, EncodeTransform(id, 1f, 2f, 3f, rotationW: 0.5f).AsSpan(0, 30).ToArray());

        ImageCacheRecordingPeer joiner = RegisterPeer(9);
        BasisNetworkImageCache.OfferCachedImagesToPeer(joiner);

        Assert.Equal((12.5f, 0f, 0f, 0f, 0f, 0f, 0f), ReadSpawnPose(PayloadOf(joiner.Sent[0])));
    }

    [Fact]
    public void ATransformForAnImageTheCacheDoesNotHold_IsIgnored()
    {
        Observe(7, EncodeTransform(Guid.NewGuid(), 1f, 2f, 3f));

        Assert.Equal(0, BasisNetworkImageCache.Count);
        Assert.Equal(0, BasisNetworkImageCache.TotalBytes);
    }

    // ---- malformed input --------------------------------------------------------------------

    [Fact]
    public void MalformedPayloads_AreIgnoredWithoutThrowing()
    {
        Observe(7, new byte[0]);
        Observe(7, new byte[] { OpSpawn });
        Observe(7, new byte[] { OpChunk, 1, 2, 3 });

        Observe(7, new byte[] { OpTransform, 1, 2, 3 });

        byte[] truncatedSpawn = EncodeSpawn(Guid.NewGuid(), 7, "Sharer", totalChunks: 2);
        Observe(7, truncatedSpawn.AsSpan(0, 20).ToArray());

        Assert.Equal(0, BasisNetworkImageCache.Count);
    }

    // ---- handover on leave -------------------------------------------------------------------

    private const byte OpServerHandover = 11;
    private const byte OpPersistence = 12;

    private (ImageCacheRecordingPeer Leaver, ImageCacheRecordingPeer Oldest, ImageCacheRecordingPeer Newer) RegisterHandoverRoom()
    {
        ImageCacheRecordingPeer leaver = RegisterPeer(41100);
        ImageCacheRecordingPeer oldest = RegisterPeer(41101);
        ImageCacheRecordingPeer newer = RegisterPeer(41102);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(leaver.Id, -1003);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(oldest.Id, -1002);
        BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.RegisterPeer(newer.Id, -1001);
        return (leaver, oldest, newer);
    }

    private static void ReleaseHandoverRoom()
    {
        for (int id = 41100; id <= 41102; id++)
        {
            BasisServerHandle.BasisServerHandleEvents.JoinBroadcast.UnregisterPeer(id);
            BasisMediaPersistenceRegistry.RemovePeer(id);
        }
    }

    private static (ushort Stamp, ushort Leaver, ushort Successor) SingleHandover(ImageCacheRecordingPeer peer)
    {
        var notices = peer.Sent.Where(s => PayloadOpcode(s) == OpServerHandover).ToList();
        var notice = Assert.Single(notices);
        byte[] payload = PayloadOf(notice);
        return (BitConverter.ToUInt16(notice.Data, 0), BitConverter.ToUInt16(payload, 1), BitConverter.ToUInt16(payload, 3));
    }

    [Fact]
    public void RemovePlayerImages_WithPersistence_HandsCompleteImagesToTheLongestConnectedPeer()
    {
        var (leaver, oldest, newer) = RegisterHandoverRoom();
        try
        {
            BasisMediaPersistenceRegistry.Set((ushort)leaver.Id, true);
            ShareImage(owner: (ushort)leaver.Id);
            Observe((ushort)leaver.Id, EncodeSpawn(Guid.NewGuid(), (ushort)leaver.Id, "Sharer", 2, 0f, 0f, 0f));

            BasisNetworkImageCache.RemovePlayerImages(leaver.Id);

            Assert.Equal(1, BasisNetworkImageCache.Count);
            Assert.Equal(0, BasisNetworkImageCache.BytesHeldFor((ushort)leaver.Id));
            Assert.True(BasisNetworkImageCache.BytesHeldFor((ushort)oldest.Id) > 0);
            foreach (var peer in new[] { oldest, newer })
            {
                var (stamp, left, successor) = SingleHandover(peer);
                Assert.Equal((ushort)peer.Id, stamp);
                Assert.Equal((ushort)leaver.Id, left);
                Assert.Equal((ushort)oldest.Id, successor);
            }
            Assert.Contains(newer.Sent, s => PayloadOpcode(s) == OpServerCacheOffer);
            Assert.DoesNotContain(oldest.Sent, s => PayloadOpcode(s) == OpServerCacheOffer);
            Assert.Empty(leaver.Sent.Where(s => PayloadOpcode(s) == OpServerHandover));
        }
        finally
        {
            ReleaseHandoverRoom();
        }
    }

    [Fact]
    public void RemovePlayerImages_WithoutPersistence_DropsEverythingAndSaysNobodyTakesOver()
    {
        var (leaver, oldest, newer) = RegisterHandoverRoom();
        try
        {
            ShareImage(owner: (ushort)leaver.Id);

            BasisNetworkImageCache.RemovePlayerImages(leaver.Id);

            Assert.Equal(0, BasisNetworkImageCache.Count);
            foreach (var peer in new[] { oldest, newer })
            {
                Assert.Equal(BasisMediaPersistenceRegistry.NoSuccessor, SingleHandover(peer).Successor);
            }
        }
        finally
        {
            ReleaseHandoverRoom();
        }
    }

    [Fact]
    public void RemovePlayerImages_AfterAKick_DropsEverythingEvenWithPersistenceAndTheAdminToggle()
    {
        var (leaver, oldest, _) = RegisterHandoverRoom();
        bool toggled = false;
        try
        {
            BasisMediaPersistenceRegistry.Set((ushort)leaver.Id, true);
            if (!BasisGlobalLockManager.KeepSharedMediaOnLeave) toggled = BasisGlobalLockManager.ToggleKeepSharedMediaOnLeave();
            ShareImage(owner: (ushort)leaver.Id);
            BasisMediaPersistenceRegistry.MarkRemoved(leaver.Id);

            BasisNetworkImageCache.RemovePlayerImages(leaver.Id);

            Assert.Equal(0, BasisNetworkImageCache.Count);
            Assert.Equal(BasisMediaPersistenceRegistry.NoSuccessor, SingleHandover(oldest).Successor);
        }
        finally
        {
            if (toggled) BasisGlobalLockManager.ToggleKeepSharedMediaOnLeave();
            ReleaseHandoverRoom();
        }
    }

    [Fact]
    public void PersistenceMessage_AddressedToSelf_RecordsTheChoice_EvenWithTheCacheOff()
    {
        NetworkServer.Configuration.ImageCacheEnabled = false;
        try
        {
            byte[] on = new byte[18];
            on[0] = OpPersistence;
            on[17] = 1;
            ObserveTargeted(41103, on, new ushort[] { 41103 });
            Assert.True(BasisMediaPersistenceRegistry.IsPersistent(41103));

            ObserveTargeted(41104, on, new ushort[] { 41103 });
            Assert.False(BasisMediaPersistenceRegistry.IsPersistent(41104));

            byte[] off = new byte[18];
            off[0] = OpPersistence;
            ObserveTargeted(41103, off, new ushort[] { 41103 });
            Assert.False(BasisMediaPersistenceRegistry.IsPersistent(41103));
        }
        finally
        {
            BasisMediaPersistenceRegistry.RemovePeer(41103);
            BasisMediaPersistenceRegistry.RemovePeer(41104);
        }
    }
}
