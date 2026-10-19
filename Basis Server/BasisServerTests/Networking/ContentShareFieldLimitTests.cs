using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ContentShareFieldLimitTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 28_000;

    private static FakeNetPeer NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.7.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"share-limit-user-{Guid.NewGuid():N}", id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return peer;
    }

    private static void Remove(params FakeNetPeer[] peers)
    {
        foreach (FakeNetPeer peer in peers) NetworkServer.AuthenticatedPeers.TryRemove(peer.Id, out _);
        NetworkServer.RebuildPeerSnapshot();
    }

    private static void SendDrop(FakeNetPeer from, ContentShareMessage msg)
    {
        NetDataWriter writer = new NetDataWriter();
        writer.Put(BasisNetworkCommons.ContentShareSub_Drop);
        msg.Serialize(writer);
        byte[] bytes = writer.AsReadOnlySpan().ToArray();
        BasisNetworkMessageProcessor.ProcessMessage(from, NetPacketReader.Create(bytes, 0, bytes.Length, () => { }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static bool Reached(FakeNetPeer peer, string sphereId)
    {
        lock (peer.Sent)
        {
            foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
            {
                if (channel != BasisNetworkCommons.ContentShareChannel) continue;
                NetDataReader reader = new NetDataReader(data);
                if (reader.GetByte() != BasisNetworkCommons.ContentShareSub_Drop) continue;
                ServerContentShareMessage message = new ServerContentShareMessage();
                message.Deserialize(reader);
                if (message.contentShareMessage.SphereNetID == sphereId) return true;
            }
        }
        return false;
    }

    private static bool Shared(ContentShareType type, string sphereId, string url, string password)
    {
        FakeNetPeer sharer = NewAuthenticatedPeer();
        FakeNetPeer watcher = NewAuthenticatedPeer();
        try
        {
            SendDrop(sharer, new ContentShareMessage { SphereNetID = sphereId, ContentURL = url, UnlockPassword = password, ContentType = type, PositionX = 1f, PositionY = 2f, PositionZ = 3f });
            bool stored = BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId);
            Assert.Equal(stored, Reached(watcher, sphereId));
            return stored;
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(sharer, watcher);
        }
    }

    private static string SphereId(int length) => (Guid.NewGuid().ToString("N") + new string('s', length)).Substring(0, length);

    [Fact]
    public void SphereIds_UpToTheCeiling_AreRelayed_AndLongerOnesAreRefused()
    {
        Assert.True(Shared(ContentShareType.Prop, SphereId(40), "https://cdn.example/prop.BEE", "pw"));
        Assert.True(Shared(ContentShareType.Prop, SphereId(ContentSharePayload.MaxSphereIdLength), "https://cdn.example/prop.BEE", "pw"));
        Assert.False(Shared(ContentShareType.Prop, SphereId(ContentSharePayload.MaxSphereIdLength + 1), "https://cdn.example/prop.BEE", "pw"));
        Assert.False(Shared(ContentShareType.Text, SphereId(4000), "hello", string.Empty));
    }

    [Fact]
    public void AnEmptySphereId_IsRefused()
    {
        Assert.False(Shared(ContentShareType.Prop, string.Empty, "https://cdn.example/prop.BEE", "pw"));
        Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(string.Empty));
    }

    [Fact]
    public void Passwords_UpToTheCeiling_AreRelayed_AndLongerOnesAreRefused()
    {
        string generated = new string('a', 64);
        Assert.True(Shared(ContentShareType.Prop, SphereId(40), "https://cdn.example/prop.BEE", generated));
        Assert.True(Shared(ContentShareType.Prop, SphereId(40), "https://cdn.example/prop.BEE", new string('p', ContentSharePayload.MaxPasswordLength)));
        Assert.False(Shared(ContentShareType.Prop, SphereId(40), "https://cdn.example/prop.BEE", new string('p', ContentSharePayload.MaxPasswordLength + 1)));
        Assert.False(Shared(ContentShareType.Text, SphereId(40), "hello", new string('p', 60000)));
    }

    [Fact]
    public void BundleAddresses_AreCappedLikeEveryOtherInlineField()
    {
        string prefix = "https://cdn.example/";
        Assert.True(Shared(ContentShareType.Prop, SphereId(40), prefix + new string('x', ContentSharePayload.MaxLength - prefix.Length), "pw"));
        Assert.False(Shared(ContentShareType.Prop, SphereId(40), prefix + new string('x', ContentSharePayload.MaxLength - prefix.Length + 1), "pw"));
    }

    [Fact]
    public void Joiners_AreOnlyReplayedOrbsWithinTheLimits()
    {
        FakeNetPeer sharer = NewAuthenticatedPeer();
        string kept = SphereId(40), refused = SphereId(ContentSharePayload.MaxSphereIdLength + 10);
        try
        {
            SendDrop(sharer, new ContentShareMessage { SphereNetID = kept, ContentURL = "https://cdn.example/prop.BEE", UnlockPassword = "pw", ContentType = ContentShareType.Prop });
            SendDrop(sharer, new ContentShareMessage { SphereNetID = refused, ContentURL = "https://cdn.example/prop.BEE", UnlockPassword = new string('p', 50000), ContentType = ContentShareType.Prop });
            FakeNetPeer joiner = NewAuthenticatedPeer();
            try
            {
                BasisNetworkContentShare.SendAllSpheresToPeer(joiner);
                Assert.True(Reached(joiner, kept));
                Assert.False(Reached(joiner, refused));
                foreach (BasisNetworkContentShare.ActiveSphere sphere in BasisNetworkContentShare.ActiveSpheres.Values)
                {
                    Assert.InRange(sphere.Message.contentShareMessage.SphereNetID.Length, 1, ContentSharePayload.MaxSphereIdLength);
                    Assert.InRange(sphere.Message.contentShareMessage.UnlockPassword?.Length ?? 0, 0, ContentSharePayload.MaxPasswordLength);
                }
            }
            finally
            {
                Remove(joiner);
            }
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(kept, out _);
            BasisNetworkContentShare.ActiveSpheres.TryRemove(refused, out _);
            Remove(sharer);
        }
    }
}
