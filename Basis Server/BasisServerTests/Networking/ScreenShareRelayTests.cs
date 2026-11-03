using Basis.Network.Core;
using Basis.Network.Server.Generic;
using BasisNetworkServer;
using BasisPermissions;
using Xunit;
using static Basis.Network.Core.BasisScreenShareProtocol;
using static BasisPermissions.PermissionManager;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ScreenShareRelayTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 31_000;

    private static FakeNetPeer NewPeer(bool authenticated = true)
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.8.8") { Tag = NetworkServer.AuthenticatedPeerTag };
        if (authenticated) Identity.Register($"screenshare-user-{Guid.NewGuid():N}", id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return peer;
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers)
        {
            BasisNetworkScreenShare.RemovePeer(peer.Id);
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static ushort Id(string name)
    {
        BasisServerMessageRegistry.EnsureInitialized();
        Assert.True(BasisServerMessageRegistry.TryGetPluginId(name, out ushort id), $"{name} is not registered");
        return id;
    }

    private static void Deliver(FakeNetPeer from, string name, Action<NetDataWriter> write)
    {
        NetDataWriter w = new NetDataWriter();
        w.Put(Id(name));
        write(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        BasisNetworkMessageProcessor.ProcessMessage(from, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), BasisNetworkCommons.PluginReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    private static StreamInfo Info(ushort width = 1920, ushort height = 1080) => new StreamInfo { Width = width, Height = height, Fps = 30, HasAudio = true, Title = "Display 1" };

    private static ScreenPose DefaultPose() => new ScreenPose { PX = 1, PY = 2, PZ = 3, RW = 1, Scale = 1 };

    private static void Start(FakeNetPeer sharer)
    {
        Deliver(sharer, ControlMessage, w =>
        {
            w.Put(StartSub);
            Info().Serialize(w);
            DefaultPose().Serialize(w);
        });
    }

    private static void Watch(FakeNetPeer viewer, FakeNetPeer sharer, bool on = true)
    {
        Deliver(viewer, ControlMessage, w =>
        {
            w.Put(WatchSub);
            w.Put((ushort)sharer.Id);
            w.Put(on);
        });
    }

    private static void Frame(FakeNetPeer sharer, uint sequence, bool key, int length, byte kind = KindVideo)
    {
        int count = ChunkCount(length);
        for (int i = 0; i < count; i++)
        {
            int size = Math.Min(MaxChunkPayload, length - i * MaxChunkPayload);
            Deliver(sharer, MediaMessage, w =>
            {
                new ChunkHeader { Sequence = sequence, Kind = kind, Flags = key ? FlagKey : (byte)0, Index = (ushort)i, Count = (ushort)count, PtsUs = sequence * 33_333L, TotalLength = length }.Serialize(w);
                w.Put(new byte[size]);
            });
        }
    }

    private static List<byte[]> Messages(FakeNetPeer peer, string name)
    {
        ushort id = Id(name);
        lock (peer.Sent) return peer.Sent.Where(s => s.Data.Length >= 2 && BitConverter.ToUInt16(s.Data, 0) == id).Select(s => s.Data).ToList();
    }

    private static List<byte> ControlSubs(FakeNetPeer peer) => Messages(peer, ControlMessage).Select(d => d[2]).ToList();

    private static List<(uint Sequence, ushort Index)> MediaChunks(FakeNetPeer peer)
    {
        return Messages(peer, MediaMessage).Select(d =>
        {
            NetDataReader reader = new NetDataReader(d);
            reader.GetUShort();
            reader.GetUShort();
            ChunkHeader header = default;
            Assert.True(header.TryDeserialize(reader));
            return (header.Sequence, header.Index);
        }).ToList();
    }

    [Fact]
    public void Protocol_RejectsMalformedValues()
    {
        Assert.False(new StreamInfo { Width = 8, Height = 1080, Fps = 30, Title = "" }.IsValid);
        Assert.False(new StreamInfo { Width = 1920, Height = 1080, Fps = 0, Title = "" }.IsValid);
        Assert.True(Info().IsValid);
        Assert.False(new ScreenPose { PX = float.NaN, RW = 1, Scale = 1 }.IsValid);
        Assert.False(new ScreenPose { RW = 0, Scale = 1 }.IsValid);
        Assert.False(new ScreenPose { RW = 1, Scale = 100 }.IsValid);
        Assert.True(DefaultPose().IsValid);

        NetDataWriter w = new NetDataWriter();
        new ChunkHeader { Sequence = 1, Kind = KindVideo, Index = 0, Count = 3, PtsUs = 0, TotalLength = 10 }.Serialize(w);
        w.Put(new byte[10]);
        ChunkHeader header = default;
        Assert.False(header.TryDeserialize(new NetDataReader(w.AsReadOnlySpan().ToArray())), "a count that disagrees with the length is refused");

        w = new NetDataWriter();
        new ChunkHeader { Sequence = 1, Kind = KindAudio, Index = 0, Count = 1, PtsUs = 5, TotalLength = 10 }.Serialize(w);
        w.Put(new byte[10]);
        Assert.True(header.TryDeserialize(new NetDataReader(w.AsReadOnlySpan().ToArray())));
        Assert.Equal(5, header.PtsUs);
    }

    [Fact]
    public void ScreenShareNode_IsInTheDefaultGroup()
    {
        Assert.Equal("basis.screenshare", PermNodes.ScreenShare);
        FakeNetPeer sharer = NewPeer();
        try
        {
            Start(sharer);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));
            Assert.Contains(AcceptedSub, ControlSubs(sharer));
        }
        finally
        {
            Remove(sharer);
        }
    }

    [Fact]
    public void UnknownIdentity_IsRefused()
    {
        FakeNetPeer sharer = NewPeer(authenticated: false);
        try
        {
            Start(sharer);
            Assert.False(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));
            byte[] refused = Messages(sharer, ControlMessage).Single();
            Assert.Equal(RefusedSub, refused[2]);
            Assert.Equal(RefusedNoPermission, refused[3]);
        }
        finally
        {
            Remove(sharer);
        }
    }

    [Fact]
    public void Start_IsAnnouncedToOthers_AndReplayedToLateSubscribers()
    {
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer early = NewPeer();
        try
        {
            Start(sharer);
            Assert.Contains(AnnounceSub, ControlSubs(early));
            Assert.DoesNotContain(AnnounceSub, ControlSubs(sharer));

            FakeNetPeer late = NewPeer();
            try
            {
                Assert.DoesNotContain(AnnounceSub, ControlSubs(late));
                NetDataWriter w = new NetDataWriter();
                w.Put(BasisNetworkCommons.RegistrySub_Subscribe);
                new SerializableBasis.BasisMessageSubscribe { Ids = new[] { Id(ControlMessage), Id(MediaMessage) } }.Serialize(w);
                byte[] bytes = w.AsReadOnlySpan().ToArray();
                BasisNetworkMessageProcessor.ProcessMessage(late, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), BasisNetworkCommons.RegistryControlChannel, DeliveryMethod.ReliableOrdered);
                Assert.Contains(AnnounceSub, ControlSubs(late));
            }
            finally
            {
                Remove(late);
            }
        }
        finally
        {
            Remove(sharer, early);
        }
    }

    [Fact]
    public void Media_ReachesOnlyViewers_StartingAtAKeyframe()
    {
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer viewer = NewPeer();
        FakeNetPeer bystander = NewPeer();
        try
        {
            Start(sharer);
            Frame(sharer, 1, true, 3000);
            Assert.Empty(MediaChunks(viewer));

            Watch(viewer, sharer);
            Assert.Equal(1, BasisNetworkScreenShare.ViewerCount((ushort)sharer.Id));
            Assert.Contains(KeyframeSub, ControlSubs(sharer));
            Assert.Contains(ViewersSub, ControlSubs(sharer));

            Frame(sharer, 2, false, 1500);
            Assert.Empty(MediaChunks(viewer));

            Frame(sharer, 3, true, 3000);
            Frame(sharer, 4, false, 900);
            Frame(sharer, 5, false, 40, KindAudio);
            List<(uint Sequence, ushort Index)> chunks = MediaChunks(viewer);
            Assert.Equal(new[] { (3u, (ushort)0), (3u, (ushort)1), (3u, (ushort)2), (4u, (ushort)0), (5u, (ushort)0) }, chunks);
            Assert.Empty(MediaChunks(bystander));

            byte[] relayed = Messages(viewer, MediaMessage)[0];
            Assert.Equal((ushort)sharer.Id, BitConverter.ToUInt16(relayed, 2));

            Watch(viewer, sharer, false);
            Frame(sharer, 6, true, 500);
            Assert.Equal(5, MediaChunks(viewer).Count);
        }
        finally
        {
            Remove(sharer, viewer, bystander);
        }
    }

    [Fact]
    public void ViewerKeyframeRequest_HoldsDeltasUntilTheNextKey()
    {
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer viewer = NewPeer();
        try
        {
            Start(sharer);
            Watch(viewer, sharer);
            Frame(sharer, 1, true, 100);
            Deliver(viewer, ControlMessage, w =>
            {
                w.Put(KeyframeSub);
                w.Put((ushort)sharer.Id);
            });
            Frame(sharer, 2, false, 100);
            Frame(sharer, 3, true, 100);
            Assert.Equal(new uint[] { 1, 3 }, MediaChunks(viewer).Select(c => c.Sequence).ToArray());
        }
        finally
        {
            Remove(sharer, viewer);
        }
    }

    [Fact]
    public void ConcurrencyCap_RefusesTheExtraShare()
    {
        Configuration previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration { ScreenShareMaxConcurrent = 1 };
        FakeNetPeer first = NewPeer();
        FakeNetPeer second = NewPeer();
        try
        {
            BasisNetworkScreenShare.Reset();
            Start(first);
            Start(second);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)first.Id));
            Assert.False(BasisNetworkScreenShare.IsSharing((ushort)second.Id));
            byte[] refused = Messages(second, ControlMessage).Last();
            Assert.Equal(RefusedSub, refused[2]);
            Assert.Equal(RefusedTooMany, refused[3]);
            Start(first);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)first.Id), "restarting an existing share is not a second share");
        }
        finally
        {
            NetworkServer.Configuration = previous;
            Remove(first, second);
        }
    }

    [Fact]
    public void Lock_RefusesNewShares_EndsRunningOnes_AndExemptsAdmins()
    {
        Configuration previous = NetworkServer.Configuration;
        NetworkServer.Configuration = new Configuration();
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer viewer = NewPeer();
        FakeNetPeer admin = NewPeer();
        try
        {
            Identity.NetIDToUUID(admin, out string adminUuid);
            PermissionIntegration.Manager.AddUserNode(adminUuid, PermNodes.ModerationGlobalLock);
            Start(sharer);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));

            Deliver(viewer, ControlMessage, w =>
            {
                w.Put(LockSub);
                w.Put(true);
            });
            Assert.False(BasisNetworkScreenShare.Locked, "a player without the global lock node cannot lock screen sharing");

            Deliver(admin, ControlMessage, w =>
            {
                w.Put(LockSub);
                w.Put(true);
            });
            Assert.True(BasisNetworkScreenShare.Locked);
            Assert.False(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id), "locking ends shares by players without the bypass");
            byte[] refused = Messages(sharer, ControlMessage).Last(d => d[2] == RefusedSub);
            Assert.Equal(RefusedLocked, refused[3]);
            Assert.Contains(EndedSub, ControlSubs(viewer));
            Assert.Contains(Messages(viewer, ControlMessage), d => d[2] == PolicySub && d[3] == 1);

            Start(sharer);
            Assert.False(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));
            Start(admin);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)admin.Id), "admins are exempt from the lock");

            Deliver(admin, ControlMessage, w =>
            {
                w.Put(LockSub);
                w.Put(false);
            });
            Start(sharer);
            Assert.True(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));
        }
        finally
        {
            NetworkServer.Configuration = previous;
            Remove(sharer, viewer, admin);
        }
    }

    [Fact]
    public void ScreenShareLimits_AreEditableProtectionSettings()
    {
        BasisNetworkServer.Security.BasisServerProtectionSettings.RegisterAll();
        List<string> keys = BasisProtectionSettings.Snapshot().Select(e => e.Key).ToList();
        Assert.Contains(nameof(Configuration.ScreenShareMaxKilobitsPerSecond), keys);
        Assert.Contains(nameof(Configuration.ScreenShareEgressMegabitsPerSecond), keys);
        Assert.Contains(nameof(Configuration.ScreenShareMaxConcurrent), keys);
    }

    [Fact]
    public void SharerLeaving_EndsTheShareForEveryone()
    {
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer viewer = NewPeer();
        try
        {
            Start(sharer);
            Watch(viewer, sharer);
            Remove(sharer);
            Assert.False(BasisNetworkScreenShare.IsSharing((ushort)sharer.Id));
            Assert.Contains(EndedSub, ControlSubs(viewer));
        }
        finally
        {
            Remove(viewer);
        }
    }

    [Fact]
    public void PoseUpdates_AreRelayedWithTheSharerId()
    {
        FakeNetPeer sharer = NewPeer();
        FakeNetPeer other = NewPeer();
        try
        {
            Start(sharer);
            Deliver(sharer, ControlMessage, w =>
            {
                w.Put(PoseSub);
                new ScreenPose { PX = 9, RW = 1, Scale = 2 }.Serialize(w);
            });
            byte[] pose = Messages(other, ControlMessage).Last();
            Assert.Equal(PoseSub, pose[2]);
            Assert.Equal((ushort)sharer.Id, BitConverter.ToUInt16(pose, 3));
            Deliver(other, ControlMessage, w =>
            {
                w.Put(PoseSub);
                DefaultPose().Serialize(w);
            });
            Assert.DoesNotContain(PoseSub, ControlSubs(sharer));
        }
        finally
        {
            Remove(sharer, other);
        }
    }
}
