using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisPermissions;
using System.Diagnostics;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ContentShareCleanupRoundTripTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 26_000;

    private static (FakeNetPeer Peer, string Uuid) NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.9.9") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"share-user-{Guid.NewGuid():N}";
        Identity.Register(uuid, id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return (peer, uuid);
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers)
        {
            NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        }
        NetworkServer.RebuildPeerSnapshot();
    }

    private static NetPacketReader Packet(Action<NetDataWriter> write)
    {
        NetDataWriter w = new NetDataWriter();
        write(w);
        byte[] bytes = w.AsReadOnlySpan().ToArray();
        return NetPacketReader.Create(bytes, 0, bytes.Length, () => { });
    }

    private static void SendDrop(FakeNetPeer from, string sphereId)
    {
        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = sphereId,
            ContentURL = "https://cdn.example/prop.BEE",
            UnlockPassword = "pw",
            ContentType = ContentShareType.Prop,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f,
        };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Drop);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static void SendCleanup(FakeNetPeer from, string sphereId)
    {
        ContentShareCleanupMessage msg = new ContentShareCleanupMessage { SphereNetID = sphereId };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Cleanup);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static List<(byte Sub, ushort PlayerId, string SphereId)> ShareTraffic(FakeNetPeer peer)
    {
        List<(byte, ushort, string)> found = new();
        foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
        {
            if (channel != BasisNetworkCommons.ContentShareChannel) continue;
            NetDataReader r = new NetDataReader(data);
            byte sub = r.GetByte();
            if (sub == BasisNetworkCommons.ContentShareSub_Cleanup)
            {
                ServerContentShareCleanupMessage m = new ServerContentShareCleanupMessage();
                m.Deserialize(r);
                found.Add((sub, m.playerIdMessage.playerID, m.contentShareCleanupMessage.SphereNetID));
            }
            else
            {
                ServerContentShareMessage m = new ServerContentShareMessage();
                m.Deserialize(r);
                found.Add((sub, m.playerIdMessage.playerID, m.contentShareMessage.SphereNetID));
            }
        }
        return found;
    }

    [Fact]
    public void Sharer_DeletesOwnSphere_ServerDropsItAndBroadcastsCleanup()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "drop was not stored");
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId);

            SendCleanup(a, sphereId);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "cleanup did not remove the sphere");
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    [Fact]
    public void UnknownSphere_RequesterAloneGetsCleanup_SoStaleOrbSelfHeals()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendCleanup(a, sphereId);
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
            Assert.DoesNotContain(ShareTraffic(b), t => t.SphereId == sphereId);
        }
        finally
        {
            Remove(a, b);
        }
    }

    [Fact]
    public void NonSharer_WithoutProtection_IsRefused_WithProtection_IsAllowed()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string bUuid) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));

            SendCleanup(b, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a non-sharer without protection removed the sphere");
            Assert.DoesNotContain(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup);

            PermissionIntegration.Manager.AddUserNode(bUuid, PermNodes.protection);
            try
            {
                SendCleanup(b, sphereId);
                Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a protected non-sharer could not remove the sphere");
                Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)b.Id);
            }
            finally
            {
                PermissionIntegration.Manager.RemoveUserNode(bUuid, PermNodes.protection);
            }
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    private static long Seconds(int seconds) => seconds * Stopwatch.Frequency;

    [Fact]
    public void SharerLeaves_SphereOutlivesThemForTheLeaveTimeout_ThenExpiresForEveryone()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        (FakeNetPeer late, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(60, 0);
        try
        {
            SendDrop(a, sphereId);
            Remove(a);
            BasisNetworkContentShare.RemovePlayerSpheres(a.Id);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the sphere was removed the moment its sharer left");
            Assert.DoesNotContain(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup);

            BasisNetworkContentShare.SendAllSpheresToPeer(late);
            Assert.Contains(ShareTraffic(late), t => t.Sub == BasisNetworkCommons.ContentShareSub_Drop && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);

            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(59));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the sphere expired before the leave timeout ran out");

            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(60));
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the sphere outlived the leave timeout");
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
            Assert.Contains(ShareTraffic(late), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(b, late);
        }
    }

    [Fact]
    public void LeaveTimeoutOfZero_RemovesTheSphereTheMomentItsSharerLeaves()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(0, 0);
        try
        {
            SendDrop(a, sphereId);
            Remove(a);
            BasisNetworkContentShare.RemovePlayerSpheres(a.Id);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a zero leave timeout still kept the sphere");
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId && t.PlayerId == (ushort)a.Id);
        }
        finally
        {
            BasisResourceLimitManager.SetContentSphereTimers(60, 0);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(b);
        }
    }

    [Fact]
    public void DeletionTimer_RemovesTheSphereWhileItsSharerIsStillHere()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(60, 120);
        try
        {
            SendDrop(a, sphereId);
            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(119));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the deletion timer fired early");

            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(120));
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the deletion timer never removed the sphere");
            Assert.Contains(ShareTraffic(a), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
            Assert.Contains(ShareTraffic(b), t => t.Sub == BasisNetworkCommons.ContentShareSub_Cleanup && t.SphereId == sphereId);
        }
        finally
        {
            BasisResourceLimitManager.SetContentSphereTimers(60, 0);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    [Fact]
    public void DeletionTimerOfZero_NeverExpiresASphereWhoseSharerIsHere()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(60, 0);
        try
        {
            SendDrop(a, sphereId);
            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(86400 * 30));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Fact]
    public void RecycledPlayerId_DoesNotInheritTheLeaversSphere()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer();
        (FakeNetPeer b, string _) = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        string ownSphereId = $"sphere-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(60, 0);
        FakeNetPeer recycled = new FakeNetPeer(a.Id, "10.9.9.10") { Tag = NetworkServer.AuthenticatedPeerTag };
        try
        {
            SendDrop(a, sphereId);
            Remove(a);
            BasisNetworkContentShare.RemovePlayerSpheres(a.Id);
            long leftAt = BasisNetworkContentShare.ActiveSpheres[sphereId].SharerLeftAt;

            Identity.Register($"share-user-{Guid.NewGuid():N}", a.Id, recycled);
            NetworkServer.AuthenticatedPeers[a.Id] = recycled;
            NetworkServer.RebuildPeerSnapshot();

            SendCleanup(recycled, sphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the next holder of the id removed the sphere the leaver shared");

            BasisResourceLimitManager.SetLimits(1);
            SendDrop(recycled, ownSphereId);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(ownSphereId), "the leaver's sphere counted against the next holder's cap");

            Remove(recycled);
            BasisNetworkContentShare.RemovePlayerSpheres(a.Id);
            Assert.Equal(leftAt, BasisNetworkContentShare.ActiveSpheres[sphereId].SharerLeftAt);
            Assert.True(BasisNetworkContentShare.ActiveSpheres[ownSphereId].SharerLeft);
        }
        finally
        {
            BasisResourceLimitManager.SetLimits(32);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(ownSphereId, out _);
            Remove(b, recycled);
        }
    }
}
