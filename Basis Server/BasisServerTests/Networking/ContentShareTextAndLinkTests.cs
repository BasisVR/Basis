using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ContentShareTextAndLinkTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 27_000;

    private static FakeNetPeer NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.8.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"text-share-user-{Guid.NewGuid():N}", id, peer);
        NetworkServer.AuthenticatedPeers[id] = peer;
        NetworkServer.RebuildPeerSnapshot();
        return peer;
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

    private static void SendDrop(FakeNetPeer from, string sphereId, ContentShareType type, string payload)
    {
        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = sphereId,
            ContentURL = payload,
            UnlockPassword = string.Empty,
            ContentType = type,
            PositionX = 1f, PositionY = 2f, PositionZ = 3f,
        };
        BasisNetworkMessageProcessor.ProcessMessage(from, Packet(w =>
        {
            w.Put(BasisNetworkCommons.ContentShareSub_Drop);
            msg.Serialize(w);
        }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);
    }

    private static bool ReceivedDrop(FakeNetPeer peer, string sphereId, out ContentShareMessage received)
    {
        foreach ((byte[] data, byte channel, DeliveryMethod _) in peer.Sent)
        {
            if (channel != BasisNetworkCommons.ContentShareChannel) continue;
            NetDataReader r = new NetDataReader(data);
            if (r.GetByte() != BasisNetworkCommons.ContentShareSub_Drop) continue;
            ServerContentShareMessage m = new ServerContentShareMessage();
            m.Deserialize(r);
            if (m.contentShareMessage.SphereNetID != sphereId) continue;
            received = m.contentShareMessage;
            return true;
        }
        received = default;
        return false;
    }

    private static void AssertRefused(ContentShareType type, string payload)
    {
        FakeNetPeer a = NewAuthenticatedPeer();
        FakeNetPeer b = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId, type, payload);
            Assert.False(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), $"a {type} share of '{payload}' was stored");
            Assert.False(ReceivedDrop(b, sphereId, out _), $"a {type} share of '{payload}' reached the room");
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    private static void AssertRelayed(ContentShareType type, string payload)
    {
        FakeNetPeer a = NewAuthenticatedPeer();
        FakeNetPeer b = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId, type, payload);
            Assert.True(BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId), $"a {type} share was not stored");
            Assert.True(ReceivedDrop(b, sphereId, out ContentShareMessage received), $"a {type} share never reached the room");
            Assert.Equal(type, received.ContentType);
            Assert.Equal(payload, received.ContentURL);
            Assert.True(ReceivedDrop(a, sphereId, out _), "the sharer did not get their own orb back");
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    [Theory]
    [InlineData(ContentShareType.Link, "https://example.com/watch?v=abc&list=x#t=10")]
    [InlineData(ContentShareType.Link, "http://localhost:8080/")]
    [InlineData(ContentShareType.Link, "https://bücher.example/straße")]
    [InlineData(ContentShareType.Text, "hello\nworld")]
    [InlineData(ContentShareType.Text, "<size=500>not markup on the server</size>")]
    [InlineData(ContentShareType.Text, "javascript:alert(1)")]
    public void TextAndLinkShares_AreStoredAndRelayedVerbatim(ContentShareType type, string payload)
    {
        AssertRelayed(type, payload);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("steam://run/440")]
    [InlineData("ms-settings:privacy")]
    [InlineData("search-ms:query=x")]
    [InlineData("https://example.com/a b")]
    [InlineData(" https://example.com")]
    [InlineData("https://example.com/\u202Egpj.exe")]
    [InlineData("https://")]
    [InlineData("example.com")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void LinkShares_ThatAreNotWebAddresses_AreRefused(string payload)
    {
        AssertRefused(ContentShareType.Link, payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public void EmptyTextShares_AreRefused(string payload)
    {
        AssertRefused(ContentShareType.Text, payload);
    }

    [Fact]
    public void TextShares_AreCappedAtTheTextCeiling_NotTheDollyTrackOne()
    {
        AssertRelayed(ContentShareType.Text, new string('x', ContentSharePayload.MaxTextLength));
        AssertRefused(ContentShareType.Text, new string('x', ContentSharePayload.MaxTextLength + 1));
        AssertRefused(ContentShareType.Link, "https://example.com/" + new string('a', ContentSharePayload.MaxTextLength));
    }

    [Fact]
    public void TextAndLinkAreInlinePayloads_WithTheirOwnCeiling()
    {
        Assert.True(ContentSharePayload.IsPayloadType(ContentShareType.Link));
        Assert.True(ContentSharePayload.IsPayloadType(ContentShareType.Text));
        Assert.True(ContentSharePayload.IsPayloadType(ContentShareType.DollyTrack));
        Assert.False(ContentSharePayload.IsPayloadType(ContentShareType.Prop));
        Assert.False(ContentSharePayload.IsTextType(ContentShareType.DollyTrack));
        Assert.Equal(ContentSharePayload.MaxTextLength, ContentSharePayload.MaxLengthFor(ContentShareType.Link));
        Assert.Equal(ContentSharePayload.MaxTextLength, ContentSharePayload.MaxLengthFor(ContentShareType.Text));
        Assert.Equal(ContentSharePayload.MaxLength, ContentSharePayload.MaxLengthFor(ContentShareType.DollyTrack));
        Assert.True(ContentSharePayload.MaxTextLength < ContentSharePayload.MaxLength);
    }

    [Fact]
    public void TryParseLink_NormalisesSchemeAndHost()
    {
        Assert.True(ContentSharePayload.TryParseLink("HTTPS://Example.COM/Path?Q=1", out Uri link));
        Assert.Equal(Uri.UriSchemeHttps, link.Scheme);
        Assert.Equal("example.com", link.Host);
        Assert.Equal("/Path", link.AbsolutePath);
        Assert.False(ContentSharePayload.TryParseLink("ftp://example.com/file", out Uri refused));
        Assert.Null(refused);
    }
}
