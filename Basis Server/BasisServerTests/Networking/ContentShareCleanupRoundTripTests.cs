using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisNetworkServer.Security;
using BasisPermissions;
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
}
