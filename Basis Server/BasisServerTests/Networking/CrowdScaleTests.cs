using Basis.Network.Core;
using Basis.Network.Core.Compression;
using Basis.Network.Server.Generic;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.BasisNetworkingReductionSystem;
using BasisNetworkServer.Security;
using BasisServerHandle;
using Xunit;
using Xunit.Abstractions;
using BitQuality = Basis.Network.Core.Compression.BasisAvatarBitPacking.BitQuality;
using Vector3 = Basis.Scripts.Networking.Compression.Vector3;
using static SerializableBasis;

namespace BasisServerTests;

/// <summary>
/// The crowd cases: everything here runs at the 2000-player scale the server is built for, because
/// several of these paths are correct at ten players and wrong at two thousand.
///
/// The join fill is the one that matters most. It is the only place where the server assembles a
/// payload whose size is driven by the population rather than by one message, so it is the only
/// place where a batching rule that is off by one record stops being a rounding error and starts
/// being "a hundred players never spawned for whoever just joined". It is also invisible in a small
/// test: a ten-player instance never fills a batch at all.
///
/// These drive the real <see cref="BasisServerHandleEvents.SendClientListToNewClient"/> through the
/// real transport interfaces, then decode what the joiner received exactly the way the Unity client
/// does, so producer and consumer are checked against each other rather than against a restatement
/// of either.
/// </summary>
/// <summary>
/// A peer whose send queue is already deep. The fan-out consults GetPacketsCountInQueue before
/// queuing anything sequenced, so this is how backpressure becomes observable offline.
/// </summary>
internal sealed class BackedUpPeer : NetPeer
{
    private readonly int queued;

    public BackedUpPeer(int id, int queuedMessages)
    {
        Id = id;
        queued = queuedMessages;
    }

    public readonly List<byte[]> Sent = new();

    public int Id { get; }
    public System.Net.IPAddress Address => System.Net.IPAddress.Loopback;
    public int RemoteId => Id;
    public int RoundTripTime => 0;
    public float TimeSinceLastPacket => 0f;
    public long RemoteTimeDelta => 0;
    public int Mtu => 1200;
    public object Tag { get; set; } = new();

    public void Disconnect() { }
    public void Disconnect(byte[] b) { }
    public void DisconnectForce() { }
    public int GetPacketsCountInQueue(byte channel, DeliveryMethod deliveryMethod) => queued;

    public void Send(byte[] data, byte channelNumber, DeliveryMethod deliveryMethod) => Sent.Add((byte[])data.Clone());
    public void Send(NetDataWriter data, byte channelNumber, DeliveryMethod deliveryMethod) => Sent.Add(data.AsReadOnlySpan().ToArray());
    public void SendUnreliableRawMerge(byte[] data, int offset, int length, byte channelNumber, int patchOffset = -1, byte patchValue = 0) { }
}

[Collection("BasisServer shared network statics")]
public class CrowdScaleTests
{
    private readonly ITestOutputHelper output;
    public CrowdScaleTests(ITestOutputHelper o) => output = o;

    private const int CrowdSize = 2000;

    // Peer ids must fit the ushort the relays and spawn records put on the wire; anything above
    // 65535 is silently truncated (or, for chat typing, refused outright with a log line). A fixed
    // band keeps the crowd clear of the id ranges the other suites in this collection use.
    private const int CrowdIdBase = 30_000;

    private static readonly MapAuthIdentity Identity = new();

    /// <summary>
    /// Stands up a crowd: authenticated peers with saved avatar records and pose tiers, exactly the
    /// state the join fill reads. Positions are spread so the distance tiering behaves as it would
    /// in a real instance, where almost everyone is far enough away to be sent at a low tier.
    /// </summary>
    private sealed class Crowd : IDisposable
    {
        public readonly List<FakeNetPeer> Peers = new();
        public readonly FakeNetPeer Joiner;

        public Crowd(int size, int avatarBlobBytes, int idBase = CrowdIdBase)
        {
            NetworkServer.AuthIdentity = Identity;
            NetworkServer.HighQualityLength = BasisAvatarBitPacking.ConvertToSize(BitQuality.High);

            Random random = new Random(4242);
            for (int index = 0; index < size; index++)
            {
                int id = idBase + index;
                FakeNetPeer peer = new FakeNetPeer(id, "10.60.0.1") { Tag = NetworkServer.AuthenticatedPeerTag };
                Identity.Register($"crowd-{id}", id, peer);
                NetworkServer.AuthenticatedPeers[id] = peer;
                Peers.Add(peer);

                byte[] avatarBlob = new byte[avatarBlobBytes];
                random.NextBytes(avatarBlob);

                BasisSavedState.AddLastData(peer, new ReadyMessage
                {
                    playerMetaDataMessage = new ClientMetaDataMessage
                    {
                        playerUUID = (76561198000000000L + id).ToString(),
                        playerDisplayName = $"Player{id}",
                        playerPlatform = index % 3 == 0 ? "Android" : "WindowsPlayer",
                    },
                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        loadMode = 1,
                        byteArray = avatarBlob,
                        LocalAvatarIndex = (byte)(index % 256),
                        ArmScale = 1f,
                        LegScale = 1f,
                        TorsoScale = 1f,
                    },
                });

                // Spread through a 200 m instance so the tiering picks a realistic spread of
                // qualities rather than High for everyone.
                BasisServerReductionSystemEvents.playerStates[id] = new PlayerState
                {
                    IsActive = true,
                    Position = new Vector3 { x = (float)(random.NextDouble() * 200.0 - 100.0), y = 0f, z = (float)(random.NextDouble() * 200.0 - 100.0) },
                    SyncMessage = new ServerSideSyncPlayerMessage
                    {
                        playerIdMessage = new PlayerIdMessage { playerID = (ushort)id },
                        avatarSerialization = Payload(BitQuality.High),
                    },
                    AvatarMedium = Payload(BitQuality.Medium),
                    AvatarLow = Payload(BitQuality.Low),
                    AvatarVeryLow = Payload(BitQuality.VeryLow),
                    // The reduction tick walks PeerTracking on any churn tick and does not
                    // null-check it, so injected state has to carry one. The LENGTH is not load
                    // bearing: every reader bounds-checks against it, and nothing in these tests
                    // reads a row. The real path allocates 256, which at 40 bytes a row is 10 KB
                    // per player -- 671 MB across a full house, enough to slow the whole suite
                    // down and trip the timing-sensitive soak tests. Eight is plenty here.
                    PeerTracking = new PeerTrackingData[8],
                };
            }

            int joinerId = idBase + size;
            Joiner = new FakeNetPeer(joinerId, "10.60.9.9") { Tag = NetworkServer.AuthenticatedPeerTag };
            Identity.Register($"crowd-joiner-{joinerId}", joinerId, Joiner);
            NetworkServer.AuthenticatedPeers[joinerId] = Joiner;

            NetworkServer.RebuildPeerSnapshot();
        }

        private static LocalAvatarSyncMessage Payload(BitQuality quality)
        {
            return new LocalAvatarSyncMessage
            {
                DataQualityLevel = (byte)quality,
                array = new byte[BasisAvatarBitPacking.ConvertToSize(quality)],
            };
        }

        public void Dispose()
        {
            foreach (FakeNetPeer peer in Peers)
            {
                NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
                BasisServerReductionSystemEvents.RemovePlayer(peer.Id);
                // RemovePlayer only ENQUEUES; playerStates is not cleared until the reduction tick
                // drains the queue. The crowd wrote playerStates directly, so it has to clear it
                // directly too, or every id it used stays visible to whatever runs next in this
                // collection (JoinSnapshotTierTests asserts on ids being absent).
                BasisServerReductionSystemEvents.playerStates.TryRemove(peer.Id, out _);
                BasisSavedState.RemovePlayer(peer.Id);
            }
            NetworkServer.AuthenticatedPeers.TryRemove(Joiner.Id, out _);
            BasisSavedState.RemovePlayer(Joiner.Id);
            NetworkServer.RebuildPeerSnapshot();
        }
    }

    /// <summary>
    /// Decodes one join-fill packet the way BasisAvatarLoadThread.DecodeBatch does, and reports the
    /// player ids it yielded. A batch the client refuses outright yields nothing, which is the
    /// failure this whole file exists to catch.
    /// </summary>
    private static List<ushort> DecodeAsTheClientWould(byte[] packet, out string error)
    {
        error = string.Empty;
        List<ushort> spawned = new List<ushort>();
        ServerReadyBatchMessage batch = default;
        try
        {
            batch.Deserialize(new NetDataReader(packet));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return spawned;
        }

        NetDataReader reader = new NetDataReader(batch.Payload);
        for (int index = 0; index < batch.Count; index++)
        {
            ServerReadyMessage message = default;
            try
            {
                message.Deserialize(reader);
            }
            catch (Exception ex)
            {
                error = $"entry {index}/{batch.Count}: {ex.Message}";
                break;
            }
            spawned.Add(message.playerIdMessage.playerID);
        }
        return spawned;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Join fill
    // ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(256)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void JoinFill_AtCrowdScale_DeliversEveryPlayerToTheJoiner(int avatarBlobBytes)
    {
        // The avatar blob size is swept because it sets how many records fit in a batch, and
        // therefore where the batch boundary lands relative to a record. A rule that only breaks
        // when a record straddles the cap would pass at one size and fail at another.
        using Crowd crowd = new Crowd(CrowdSize, avatarBlobBytes);

        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        Assert.NotEmpty(crowd.Joiner.Sent);

        HashSet<ushort> spawned = new HashSet<ushort>();
        List<string> refusals = new List<string>();
        foreach ((byte[] data, byte channel, DeliveryMethod method) in crowd.Joiner.Sent)
        {
            Assert.Equal(BasisNetworkCommons.CreateRemotePlayersForNewPeerChannel, channel);
            Assert.Equal(DeliveryMethod.ReliableOrdered, method);

            List<ushort> decoded = DecodeAsTheClientWould(data, out string error);
            foreach (ushort id in decoded)
            {
                spawned.Add(id);
            }
            if (!string.IsNullOrEmpty(error))
            {
                refusals.Add(error);
            }
        }

        output.WriteLine($"blob={avatarBlobBytes}B  packets={crowd.Joiner.Sent.Count}  spawned={spawned.Count}/{CrowdSize}  refused={refusals.Count}");

        Assert.True(refusals.Count == 0,
            $"{refusals.Count} of {crowd.Joiner.Sent.Count} join-fill packets were refused by the client decoder. First: {refusals.FirstOrDefault()}");

        foreach (FakeNetPeer peer in crowd.Peers)
        {
            Assert.Contains((ushort)peer.Id, spawned);
        }
        Assert.DoesNotContain((ushort)crowd.Joiner.Id, spawned);
    }

    [Fact]
    public void JoinFill_AtCrowdScale_StaysUnderTheReceiverCeiling()
    {
        // MaxPayloadBytes is the producer's flush target, not a ceiling: it appends a whole record
        // and flushes once the buffer has reached the target, so a real batch always runs one
        // record past it. What has to hold is the ceiling the receiver actually enforces.
        using Crowd crowd = new Crowd(CrowdSize, 1024);

        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        int largest = 0;
        foreach ((byte[] data, byte _, DeliveryMethod _) in crowd.Joiner.Sent)
        {
            ServerReadyBatchMessage batch = default;
            batch.Deserialize(new NetDataReader(data));
            largest = Math.Max(largest, batch.Payload.Length);
        }

        output.WriteLine($"largest batch payload {largest} B, flush target {ServerReadyBatchMessage.MaxPayloadBytes} B, ceiling {ServerReadyBatchMessage.MaxInflatedBytes} B");
        Assert.True(largest <= ServerReadyBatchMessage.MaxInflatedBytes,
            $"largest batch was {largest} B against the {ServerReadyBatchMessage.MaxInflatedBytes} B receiver ceiling");

        // The overshoot has to stay small, or the ceiling is doing no work at all.
        Assert.True(largest < ServerReadyBatchMessage.MaxPayloadBytes * 2,
            $"a batch overshot its flush target by more than one record's worth: {largest} B");
    }

    [Fact]
    public void JoinFill_AtCrowdScale_CostsFarFewerPacketsThanPlayers()
    {
        // The reason batching exists: 2000 reliable sends queued to one peer in a tight loop
        // stalls that client behind its own backlog.
        using Crowd crowd = new Crowd(CrowdSize, 1024);

        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        output.WriteLine($"{CrowdSize} players delivered in {crowd.Joiner.Sent.Count} packets");
        Assert.True(crowd.Joiner.Sent.Count * 10 < CrowdSize,
            $"expected at least a 10x packet reduction, got {CrowdSize} -> {crowd.Joiner.Sent.Count}");
    }

    [Fact]
    public void JoinFill_IntoAnEmptyInstance_SendsNothing()
    {
        using Crowd crowd = new Crowd(0, 512);
        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());
        Assert.Empty(crowd.Joiner.Sent);
    }

    [Fact]
    public void JoinFill_WithOnePlayerPresent_StillArrives()
    {
        using Crowd crowd = new Crowd(1, 512);
        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        Assert.Single(crowd.Joiner.Sent);
        List<ushort> spawned = DecodeAsTheClientWould(crowd.Joiner.Sent[0].Data, out string error);
        Assert.Equal(string.Empty, error);
        Assert.Equal(new[] { (ushort)crowd.Peers[0].Id }, spawned);
    }

    [Fact]
    public void JoinFill_WithAnOversizedSingleRecord_StillDeliversEveryone()
    {
        // One player whose avatar record is larger than a whole batch ends up alone in a batch that
        // overshoots the flush target by a long way. The receiver ceiling is sized to admit it, so
        // that player spawns like everyone else instead of being the one nobody can see.
        using Crowd crowd = new Crowd(40, 512);

        FakeNetPeer whale = crowd.Peers[20];
        byte[] huge = new byte[48 * 1024];
        new Random(9).NextBytes(huge);
        BasisSavedState.AddLastData(whale, new ClientAvatarChangeMessage
        {
            loadMode = 1,
            byteArray = huge,
            LocalAvatarIndex = 3,
            ArmScale = 1f,
            LegScale = 1f,
            TorsoScale = 1f,
        });

        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        HashSet<ushort> spawned = new HashSet<ushort>();
        foreach ((byte[] data, byte _, DeliveryMethod _) in crowd.Joiner.Sent)
        {
            foreach (ushort id in DecodeAsTheClientWould(data, out _))
            {
                spawned.Add(id);
            }
        }

        foreach (FakeNetPeer peer in crowd.Peers)
        {
            Assert.Contains((ushort)peer.Id, spawned);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fan-out
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Broadcast_AtCrowdScale_ReachesEveryoneButTheSender()
    {
        using Crowd crowd = new Crowd(CrowdSize, 64);
        FakeNetPeer sender = crowd.Peers[0];

        BasisNetworkHandleChatTyping.HandleEvent(
            NetPacketReader.Create(new byte[] { 1 }, 0, 1, () => { }),
            sender,
            BasisNetworkCommons.EventType_PlayerChatTyping);

        int reached = 0;
        foreach (FakeNetPeer peer in crowd.Peers)
        {
            if (peer.Id == sender.Id)
            {
                Assert.Empty(peer.Sent);
                continue;
            }
            if (peer.Sent.Count > 0) reached++;
        }

        Assert.Equal(CrowdSize - 1, reached);
        Assert.Single(crowd.Joiner.Sent);
    }

    [Fact]
    public void TargetedRelay_AtCrowdScale_TouchesExactlyOnePeer()
    {
        // A relay that walked the peer list instead of the id map would be invisible at ten
        // players and a 2000x amplification here.
        using Crowd crowd = new Crowd(CrowdSize, 64);
        FakeNetPeer sender = crowd.Peers[0];
        FakeNetPeer target = crowd.Peers[CrowdSize - 1];

        NetDataWriter writer = new NetDataWriter();
        writer.Put((ushort)target.Id);
        writer.Put(true);
        byte[] bytes = writer.CopyData();

        BasisNetworkHandleTempBlock.HandleEvent(
            NetPacketReader.Create(bytes, 0, bytes.Length, () => { }),
            sender,
            BasisNetworkCommons.EventType_PlayerTempBlock);

        int touched = 0;
        foreach (FakeNetPeer peer in crowd.Peers)
        {
            touched += peer.Sent.Count;
        }
        Assert.Equal(1, touched);
        Assert.Single(target.Sent);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-peer bookkeeping at scale
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MuteGates_AtCrowdScale_StayPerPlayer()
    {
        // The gates take a fast path while nobody is muted. One muted player turns that path off
        // for the whole instance, so the per-peer answers still have to be right afterwards.
        BasisPlayerMuteManager.UseFileOnDisc = false;
        using Crowd crowd = new Crowd(500, 64);

        foreach (FakeNetPeer peer in crowd.Peers)
        {
            Assert.False(BasisPlayerMuteManager.IsVoiceMutedFor(peer));
        }

        FakeNetPeer muted = crowd.Peers[250];
        Assert.True(NetworkServer.AuthIdentity.NetIDToUUID(muted, out string uuid));
        try
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: true);

            int mutedCount = 0;
            foreach (FakeNetPeer peer in crowd.Peers)
            {
                if (BasisPlayerMuteManager.IsVoiceMutedFor(peer)) mutedCount++;
            }
            Assert.Equal(1, mutedCount);
            Assert.True(BasisServerHandleEvents.IsVoiceBlockedFor(muted));
        }
        finally
        {
            BasisPlayerMuteManager.Apply(uuid, voice: true, muted: false);
        }
    }

    [Fact]
    public void RateLimiter_HoldsUpAcrossAWholeCrowd()
    {
        // Every peer in a full instance flooding at once: each still gets its own budget, and
        // none of them gets more than the burst.
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 4f);
        List<FakeNetPeer> peers = new List<FakeNetPeer>();
        for (int index = 0; index < CrowdSize; index++)
        {
            peers.Add(new FakeNetPeer(200_000 + index, "10.61.0.1"));
        }

        int granted = 0;
        for (int round = 0; round < 10; round++)
        {
            foreach (FakeNetPeer peer in peers)
            {
                if (limiter.TryConsume(peer)) granted++;
            }
        }

        Assert.Equal(CrowdSize * 4, granted);
    }

    [Fact]
    public void RateLimiter_BeyondItsTrackingCeiling_HandsTheBurstBackToEveryone()
    {
        // The limiter tracks at most 4096 peers and drops the whole table when it goes over, which
        // is what keeps peer-id churn from growing memory without bound. The cost is that above
        // that population every peer gets its burst reissued whenever the table is dropped.
        //
        // PeerLimit defaults to ushort.MaxValue, so an operator CAN run past this. Pinned here so
        // the trade is a decision rather than a surprise: at the 2000-player scale the server is
        // tuned for, the table never fills and the limiter holds (see the test above).
        var limiter = new BasisPeerRateLimiter(tokensPerSecond: 0f, tokenBurst: 1f);

        FakeNetPeer early = new FakeNetPeer(150_000, "10.62.0.1");
        Assert.True(limiter.TryConsume(early));
        Assert.False(limiter.TryConsume(early), "its single token is spent");

        for (int index = 0; index < 5000; index++)
        {
            limiter.TryConsume(new FakeNetPeer(151_000 + index, "10.62.0.2"));
        }

        Assert.True(limiter.TryConsume(early),
            "past the tracking ceiling the table is dropped, so a spent peer starts over");
    }

    [Fact]
    public void Broadcast_ToABackedUpPeer_IsDroppedRatherThanQueuedForever()
    {
        // Sequenced fan-out is where a crowd turns one message into N sends. A peer whose queue is
        // already deep is skipped instead of being buried, so one bad connection cannot make the
        // send loop grow the server's memory on everyone else's behalf.
        using Crowd crowd = new Crowd(4, 64);
        FakeNetPeer sender = crowd.Peers[0];
        BackedUpPeer stalled = new BackedUpPeer(CrowdIdBase + 900, queuedMessages: 5000);
        NetworkServer.AuthenticatedPeers[stalled.Id] = stalled;
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleChatTyping.HandleEvent(
                NetPacketReader.Create(new byte[] { 1 }, 0, 1, () => { }),
                sender,
                BasisNetworkCommons.EventType_PlayerChatTyping);

            Assert.Empty(stalled.Sent);
            Assert.Single(crowd.Peers[1].Sent);
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(stalled.Id, out _);
            NetworkServer.RebuildPeerSnapshot();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // The top of the peer-id space
    //
    // PeerLimit defaults to (and the admin path clamps to) ushort.MaxValue, and LiteNetLib hands
    // a departing peer's id back to a free list, so ids are bounded by PEAK CONCURRENCY rather
    // than by lifetime connections. A full 65535-player house therefore reaches the very top of
    // the range the wire format can express, and every relay that rewrites a sender id casts to
    // ushort with no room left over. These run there.
    // ─────────────────────────────────────────────────────────────────────────

    private const int TopOfRange = ushort.MaxValue;

    private static FakeNetPeer RegisterAt(int id)
    {
        NetworkServer.AuthIdentity = Identity;
        FakeNetPeer peer = new FakeNetPeer(id, "10.63.0.1") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"top-{id}", id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        return peer;
    }

    [Fact]
    public void Relays_AtTheHighestPeerIds_CarryTheSenderIntact()
    {
        FakeNetPeer sender = RegisterAt(TopOfRange);
        FakeNetPeer target = RegisterAt(TopOfRange - 1);
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            NetDataWriter block = new NetDataWriter();
            block.Put((ushort)target.Id);
            block.Put(true);
            byte[] blockBytes = block.CopyData();
            BasisNetworkHandleTempBlock.HandleEvent(
                NetPacketReader.Create(blockBytes, 0, blockBytes.Length, () => { }),
                sender, BasisNetworkCommons.EventType_PlayerTempBlock);

            Assert.Single(target.Sent);
            NetDataReader read = new NetDataReader(target.Sent[0].Data);
            Assert.Equal(BasisNetworkCommons.EventType_PlayerTempBlock, read.GetByte());
            Assert.Equal((ushort)TopOfRange, read.GetUShort());
            Assert.True(read.GetBool());

            target.Sent.Clear();

            NetDataWriter consent = new NetDataWriter();
            consent.Put((ushort)target.Id);
            consent.Put((byte)1);
            consent.Put((byte)2);
            byte[] consentBytes = consent.CopyData();
            BasisNetworkHandleVoiceRecord.HandleEvent(
                NetPacketReader.Create(consentBytes, 0, consentBytes.Length, () => { }),
                sender, BasisNetworkCommons.EventType_VoiceRecordConsent);

            Assert.Single(target.Sent);
            NetDataReader consentRead = new NetDataReader(target.Sent[0].Data);
            consentRead.GetByte();
            Assert.Equal((ushort)TopOfRange, consentRead.GetUShort());
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(sender.Id, out _);
            NetworkServer.AuthenticatedPeers.TryRemove(target.Id, out _);
            NetworkServer.RebuildPeerSnapshot();
        }
    }

    [Fact]
    public void ChatTyping_AtTheHighestPeerId_IsStillBroadcast()
    {
        // The typing handler is the one relay that refuses an id it cannot express, so the exact
        // top of the range is the value most worth pinning: 65535 must be inside the format, not
        // one past it.
        FakeNetPeer sender = RegisterAt(TopOfRange);
        FakeNetPeer listener = RegisterAt(TopOfRange - 2);
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            BasisNetworkHandleChatTyping.HandleEvent(
                NetPacketReader.Create(new byte[] { 1 }, 0, 1, () => { }),
                sender, BasisNetworkCommons.EventType_PlayerChatTyping);

            Assert.Single(listener.Sent);
            NetDataReader read = new NetDataReader(listener.Sent[0].Data);
            read.GetByte();
            Assert.Equal((ushort)TopOfRange, read.GetUShort());
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(sender.Id, out _);
            NetworkServer.AuthenticatedPeers.TryRemove(listener.Id, out _);
            NetworkServer.RebuildPeerSnapshot();
        }
    }

    [Fact]
    public void JiggleGrab_AtTheHighestPeerIds_RelaysBothIds()
    {
        FakeNetPeer denier = RegisterAt(TopOfRange);
        FakeNetPeer grabber = RegisterAt(TopOfRange - 3);
        NetworkServer.RebuildPeerSnapshot();
        try
        {
            NetDataWriter deny = new NetDataWriter();
            deny.Put(BasisNetworkCommons.JiggleGrabOp_Deny);
            deny.Put((ushort)grabber.Id);
            byte[] bytes = deny.CopyData();
            BasisNetworkHandleJiggleGrab.HandleEvent(
                NetPacketReader.Create(bytes, 0, bytes.Length, () => { }),
                denier, BasisNetworkCommons.EventType_JiggleGrab);

            Assert.Single(grabber.Sent);
            NetDataReader read = new NetDataReader(grabber.Sent[0].Data);
            read.GetByte();
            Assert.Equal(BasisNetworkCommons.JiggleGrabOp_Deny, read.GetByte());
            Assert.Equal((ushort)TopOfRange, read.GetUShort());
            Assert.Equal((ushort)(TopOfRange - 3), read.GetUShort());
        }
        finally
        {
            NetworkServer.AuthenticatedPeers.TryRemove(denier.Id, out _);
            NetworkServer.AuthenticatedPeers.TryRemove(grabber.Id, out _);
            NetworkServer.RebuildPeerSnapshot();
        }
    }

    [Fact]
    public void JoinFill_WithPeerIdsAtTheTopOfTheRange_SpawnsThemAll()
    {
        // The spawn record carries playerID as a ushort too, so a full house has to survive the
        // join fill with its ids unchanged rather than wrapping to low numbers.
        const int size = 200;
        using Crowd crowd = new Crowd(size, 512, idBase: TopOfRange - size);

        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());

        HashSet<ushort> spawned = new HashSet<ushort>();
        foreach ((byte[] data, byte _, DeliveryMethod _) in crowd.Joiner.Sent)
        {
            List<ushort> decoded = DecodeAsTheClientWould(data, out string error);
            Assert.Equal(string.Empty, error);
            foreach (ushort id in decoded) spawned.Add(id);
        }

        Assert.Equal(size, spawned.Count);
        foreach (FakeNetPeer peer in crowd.Peers)
        {
            Assert.Contains((ushort)peer.Id, spawned);
        }
        Assert.Contains((ushort)(TopOfRange - 1), spawned);
    }

    [Fact]
    public void JoinFill_AtAFullHouse_DeliversEveryOneOfThem()
    {
        // The real ceiling: PeerLimit clamps to ushort.MaxValue, so this is the largest instance
        // the protocol can express. Ids run 0..65534 with the joiner taking the last slot, which
        // is also the only configuration where a spawn record's ushort playerID has no headroom.
        const int fullHouse = ushort.MaxValue;
        using Crowd crowd = new Crowd(fullHouse, 32, idBase: 0);

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        BasisServerHandleEvents.SendClientListToNewClient(crowd.Joiner, new LocalAvatarSyncMessage());
        clock.Stop();

        int refused = 0;
        HashSet<ushort> spawned = new HashSet<ushort>();
        int largestPayload = 0;
        foreach ((byte[] data, byte _, DeliveryMethod _) in crowd.Joiner.Sent)
        {
            List<ushort> decoded = DecodeAsTheClientWould(data, out string error);
            if (!string.IsNullOrEmpty(error)) refused++;
            foreach (ushort id in decoded) spawned.Add(id);

            ServerReadyBatchMessage batch = default;
            batch.Deserialize(new NetDataReader(data));
            largestPayload = Math.Max(largestPayload, batch.Payload.Length);
        }

        output.WriteLine($"full house: {fullHouse} players in {crowd.Joiner.Sent.Count} packets, "
                         + $"largest payload {largestPayload} B, refused {refused}, built in {clock.ElapsedMilliseconds} ms");

        Assert.Equal(0, refused);
        Assert.Equal(fullHouse, spawned.Count);
        Assert.True(largestPayload <= ServerReadyBatchMessage.MaxInflatedBytes);
        Assert.Contains((ushort)0, spawned);
        Assert.Contains((ushort)(fullHouse - 1), spawned);
    }

    [Fact]
    public void ProtocolErrorCounts_AtCrowdScale_AreIsolatedPerPeer()
    {
        // One broken client must not push anyone else toward the disconnect threshold.
        BasisServerMessageRegistry.EnsureInitialized();
        using Crowd crowd = new Crowd(200, 64);

        FakeNetPeer offender = crowd.Peers[0];
        byte[] pooled = new byte[32];
        pooled[0] = BasisNetworkCommons.EventType_PlayerTempBlock;
        try
        {
            for (int index = 0; index < 600; index++)
            {
                BasisNetworkMessageProcessor.ProcessMessage(
                    offender, NetPacketReader.Create(pooled, 0, 1, () => { }),
                    BasisNetworkCommons.EventsChannel, DeliveryMethod.ReliableOrdered);
            }

            Assert.True(offender.DisconnectCalls >= 1);
            for (int index = 1; index < crowd.Peers.Count; index++)
            {
                Assert.Equal(0, crowd.Peers[index].DisconnectCalls);
            }
        }
        finally
        {
            BasisNetworkMessageProcessor.ClearPeerErrors(offender.Id);
        }
    }
}
