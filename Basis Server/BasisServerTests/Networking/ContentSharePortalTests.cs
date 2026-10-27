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
public class ContentSharePortalTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 28_000;

    private static (FakeNetPeer Peer, string Uuid) NewAuthenticatedPeer(bool canPortal)
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.7.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        string uuid = $"portal-user-{Guid.NewGuid():N}";
        Identity.Register(uuid, id, peer);
        if (canPortal) PermissionIntegration.Manager.AddUserNode(uuid, PermNodes.ContentSharePortal);
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

    private static void SendPortal(FakeNetPeer from, string sphereId, string lifetime, string destination = "portal.example:4296#pw")
    {
        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = sphereId,
            ContentURL = destination,
            UnlockPassword = lifetime,
            ContentType = ContentShareType.Portal,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f,
        };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Drop);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static long Seconds(int seconds) => seconds * Stopwatch.Frequency;

    [Fact]
    public void DefaultGroup_DoesNotHavePortalNode()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer(false);
        string sphereId = $"portal-{Guid.NewGuid():N}";
        try
        {
            SendPortal(a, sphereId, ContentSharePortal.WriteLifetime(ContentSharePortal.TimedSeconds));
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a player without the portal node dropped a portal");
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("86401")]
    [InlineData(" 60")]
    public void Portal_WithUnreadableLifetime_IsRefused(string lifetime)
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer(true);
        string sphereId = $"portal-{Guid.NewGuid():N}";
        try
        {
            SendPortal(a, sphereId, lifetime);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Fact]
    public void Portal_WithoutDestination_IsRefused()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer(true);
        string sphereId = $"portal-{Guid.NewGuid():N}";
        try
        {
            SendPortal(a, sphereId, "60", " ");
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Fact]
    public void TimedPortal_ExpiresAfterSixtySeconds()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer(true);
        string sphereId = $"portal-{Guid.NewGuid():N}";
        try
        {
            SendPortal(a, sphereId, ContentSharePortal.WriteLifetime(ContentSharePortal.TimedSeconds));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a permitted portal was not stored");
            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(59));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the portal closed early");
            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(60));
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the timed portal outlived its 60 seconds");
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Fact]
    public void PersistentPortal_SurvivesSharerLeaving_DeletionTimer_AndEmptyServer()
    {
        (FakeNetPeer a, string _) = NewAuthenticatedPeer(true);
        string sphereId = $"portal-{Guid.NewGuid():N}";
        BasisResourceLimitManager.SetContentSphereTimers(0, 120);
        try
        {
            SendPortal(a, sphereId, ContentSharePortal.WriteLifetime(ContentSharePortal.Persistent));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "a permitted portal was not stored");
            Remove(a);
            BasisNetworkContentShare.RemovePlayerSpheres(a.Id);
            BasisNetworkContentShare.ExpireSpheres(Stopwatch.GetTimestamp() + Seconds(100_000));
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the persistent portal expired");
            Assert.True(BasisNetworkContentShare.ActiveSpheres[sphereId].SharerLeft, "the sharer's id was not released");
            BasisNetworkContentShare.Reset(keepPersistentPortals: true);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), "the persistent portal was cleared when the server emptied");
            BasisNetworkContentShare.Reset();
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId));
        }
        finally
        {
            BasisResourceLimitManager.SetContentSphereTimers(60, 0);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a);
        }
    }

    [Fact]
    public void ModeratorGroup_HasPortalNode()
    {
        PermissionIntegration.Manager.EnsureDefaults();
        string uuid = $"portal-mod-{Guid.NewGuid():N}";
        PermissionIntegration.Manager.AddUserToGroup(uuid, "moderator");
        Assert.True(PermissionIntegration.HasValidRequirement(uuid, PermNodes.ContentSharePortal));
    }
}
