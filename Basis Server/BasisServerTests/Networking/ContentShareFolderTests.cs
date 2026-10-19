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
public class ContentShareFolderTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 28_000;
    private const string FolderId = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static FakeNetPeer NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.8.6") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"folder-share-user-{Guid.NewGuid():N}", id, peer);
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

    private static void SendDrop(FakeNetPeer from, string sphereId, string payload)
    {
        ContentShareMessage msg = new ContentShareMessage
        {
            SphereNetID = sphereId,
            ContentURL = payload,
            UnlockPassword = string.Empty,
            ContentType = ContentShareType.Folder,
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

    private static bool Relayed(string payload)
    {
        FakeNetPeer a = NewAuthenticatedPeer();
        FakeNetPeer b = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            SendDrop(a, sphereId, payload);
            bool stored = BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId);
            bool reached = ReceivedDrop(b, sphereId, out ContentShareMessage received);
            Assert.Equal(stored, reached);
            if (reached)
            {
                Assert.Equal(ContentShareType.Folder, received.ContentType);
                Assert.Equal(payload, received.ContentURL);
            }
            return reached;
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    private static ContentShareFolder.Item Item(ContentShareType type, string url, string password = "0123456789abcdef0123456789abcdef", string name = "Thing") =>
        new ContentShareFolder.Item { Type = type, Url = url, Password = password, Name = name };

    private static string Folder(params ContentShareFolder.Item[] items) => ContentShareFolder.Write(FolderId, "Party Kit", items);

    [Fact]
    public void WriteThenRead_KeepsEveryField_EvenWithSeparatorsInsideThem()
    {
        ContentShareFolder.Item[] items =
        {
            Item(ContentShareType.Avatar, "https://cdn.example.com/avatars/one.BEE", "pa\tss\nwo%rd\r", "Tab\tName"),
            Item(ContentShareType.Prop, "https://cdn.example.com/props/my file.BEE?v=1&x=%41", "%25%09", "100% prop"),
            Item(ContentShareType.World, "http://files.example.org/worlds/w.BEE", "plain", string.Empty),
        };
        string payload = ContentShareFolder.Write(FolderId, "Kit\n<b>Night</b> \U0001F600", items);

        Assert.True(ContentShareFolder.IsFolderText(payload));
        Assert.True(ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents read));
        Assert.Equal(FolderId, read.Id);
        Assert.Equal("Kit <b>Night</b> \U0001F600", read.Name);
        Assert.Equal(3, read.Items.Count);
        for (int i = 0; i < items.Length; i++)
        {
            Assert.Equal(items[i].Type, read.Items[i].Type);
            Assert.Equal(items[i].Url, read.Items[i].Url);
            Assert.Equal(items[i].Password, read.Items[i].Password);
        }
        Assert.Equal("Tab Name", read.Items[0].Name);
        Assert.Equal("100% prop", read.Items[1].Name);
        Assert.Equal(string.Empty, read.Items[2].Name);
    }

    [Fact]
    public void Read_ToleratesWindowsLineBreaksAndATrailingNewline()
    {
        string payload = Folder(Item(ContentShareType.Prop, "https://example.com/a.BEE"), Item(ContentShareType.Avatar, "https://example.com/b.BEE"));
        Assert.True(ContentShareFolder.TryRead(payload.Replace("\n", "\r\n") + "\r\n", out ContentShareFolder.Contents read));
        Assert.Equal(2, read.Items.Count);
        Assert.Equal("https://example.com/b.BEE", read.Items[1].Url);
    }

    [Fact]
    public void Read_IgnoresFieldsANewerWriterAppends()
    {
        string payload = ContentShareFolder.Header + "\n" + FolderId + "\tKit\textra\n1\thttps://example.com/a.BEE\tpw\tName\tfuture";
        Assert.True(ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents read));
        Assert.Equal("Kit", read.Name);
        Assert.Equal("Name", read.Items[0].Name);
    }

    public static IEnumerable<object[]> MalformedFolders()
    {
        string header = ContentShareFolder.Header + "\n" + FolderId + "\tKit\n";
        yield return new object[] { "" };
        yield return new object[] { "hello" };
        yield return new object[] { ContentShareFolder.Header };
        yield return new object[] { header };
        yield return new object[] { "basis-folder:2\n" + FolderId + "\tKit\n1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { "basis-folder:10\n" + FolderId + "\tKit\n1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { ContentShareFolder.Header + "\n\tKit\n1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { ContentShareFolder.Header + "\nnot an id!\tKit\n1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { ContentShareFolder.Header + "\n" + new string('a', ContentShareFolder.MaxIdLength + 1) + "\tKit\n1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "3\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "4\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "7\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "255\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "x\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + " 1\thttps://example.com/a.BEE\tpw" };
        yield return new object[] { header + "1\tfile:///C:/Windows/System32/calc.exe\tpw" };
        yield return new object[] { header + "1\tjavascript:alert(1)\tpw" };
        yield return new object[] { header + "1\texample.com/a.BEE\tpw" };
        yield return new object[] { header + "1\thttps://\tpw" };
        yield return new object[] { header + "1\thttps://example.com/a%0A.BEE\tpw" };
        yield return new object[] { header + "1\thttps://example.com/a.BEE\t" };
        yield return new object[] { header + "1\thttps://example.com/a.BEE" };
        yield return new object[] { header + "1\thttps://example.com/a.BEE\tpw\n2" };
    }

    [Theory]
    [MemberData(nameof(MalformedFolders))]
    public void Read_RefusesAnythingThatIsNotAWellFormedFolder(string payload)
    {
        Assert.False(ContentShareFolder.TryRead(payload, out ContentShareFolder.Contents read));
        Assert.Null(read);
    }

    [Fact]
    public void Read_RefusesMoreItemsThanTheCap()
    {
        ContentShareFolder.Item[] items = new ContentShareFolder.Item[ContentShareFolder.MaxItems + 1];
        for (int i = 0; i < items.Length; i++) items[i] = Item(ContentShareType.Prop, $"https://e.io/{i}", "p", string.Empty);
        Assert.False(ContentShareFolder.TryRead(Folder(items), out _));
        Assert.True(ContentShareFolder.TryRead(Folder(items[..ContentShareFolder.MaxItems]), out ContentShareFolder.Contents read));
        Assert.Equal(ContentShareFolder.MaxItems, read.Items.Count);
    }

    [Fact]
    public void Read_RefusesAPayloadOverTheInlineCeiling()
    {
        string payload = Folder(Item(ContentShareType.Prop, "https://example.com/" + new string('a', ContentSharePayload.MaxLength)));
        Assert.True(payload.Length > ContentSharePayload.MaxLength);
        Assert.False(ContentShareFolder.TryRead(payload, out _));
    }

    [Fact]
    public void CleanName_CollapsesWhitespace_DropsInvisibleCharacters_AndNeverSplitsAPair()
    {
        Assert.Equal("a b c", ContentShareFolder.CleanName("  a \t\r\n b\u0000c  ", 64));
        Assert.Equal("ab", ContentShareFolder.CleanName("a\u202Eb\u200B", 64));
        Assert.Equal("ab", ContentShareFolder.CleanName("ab\U0001F600", 3));
        Assert.Equal("ab\U0001F600", ContentShareFolder.CleanName("ab\U0001F600", 4));
        Assert.Equal("ab", ContentShareFolder.CleanName("a\uD800b", 64));
        Assert.Equal(string.Empty, ContentShareFolder.CleanName(null, 64));
    }

    [Fact]
    public void IsFolderText_NeedsTheExactHeaderLine()
    {
        Assert.True(ContentShareFolder.IsFolderText(ContentShareFolder.Header + "\nrest"));
        Assert.True(ContentShareFolder.IsFolderText(ContentShareFolder.Header + "\r\nrest"));
        Assert.False(ContentShareFolder.IsFolderText(ContentShareFolder.Header + "0\nrest"));
        Assert.False(ContentShareFolder.IsFolderText(" " + ContentShareFolder.Header + "\nrest"));
        Assert.False(ContentShareFolder.IsFolderText(null));
    }

    [Fact]
    public void FolderIsAnInlinePayload_WithTheFullCeiling()
    {
        Assert.True(ContentSharePayload.IsPayloadType(ContentShareType.Folder));
        Assert.False(ContentSharePayload.IsTextType(ContentShareType.Folder));
        Assert.Equal(ContentSharePayload.MaxLength, ContentSharePayload.MaxLengthFor(ContentShareType.Folder));
        Assert.True(ContentShareFolder.IsItemType(ContentShareType.Avatar));
        Assert.True(ContentShareFolder.IsItemType(ContentShareType.Prop));
        Assert.True(ContentShareFolder.IsItemType(ContentShareType.World));
        Assert.False(ContentShareFolder.IsItemType(ContentShareType.Server));
        Assert.False(ContentShareFolder.IsItemType(ContentShareType.Folder));
    }

    [Fact]
    public void FolderShare_IsStoredAndRelayedVerbatim()
    {
        Assert.True(Relayed(Folder(Item(ContentShareType.Avatar, "https://example.com/a.BEE"), Item(ContentShareType.World, "https://example.com/w.BEE", "pw", "A <b>World</b>"))));
    }

    [Theory]
    [MemberData(nameof(MalformedFolders))]
    public void MalformedFolderShares_AreRefused(string payload)
    {
        Assert.False(Relayed(payload));
    }

    [Fact]
    public void FolderShare_HoldingALockedType_IsRefused()
    {
        string withAvatar = Folder(Item(ContentShareType.Prop, "https://example.com/p.BEE"), Item(ContentShareType.Avatar, "https://example.com/a.BEE"));
        string propsOnly = Folder(Item(ContentShareType.Prop, "https://example.com/p.BEE"), Item(ContentShareType.Prop, "https://example.com/q.BEE"));
        try
        {
            BasisGlobalLockManager.InitializeFromConfig(new Configuration { AvatarsLocked = true });
            Assert.False(Relayed(withAvatar));
            Assert.True(Relayed(propsOnly));

            BasisGlobalLockManager.InitializeFromConfig(new Configuration { PropsLocked = true });
            Assert.False(Relayed(withAvatar));
            Assert.False(Relayed(propsOnly));

            BasisGlobalLockManager.InitializeFromConfig(new Configuration { WorldsLocked = true });
            Assert.True(Relayed(withAvatar));
            Assert.False(Relayed(Folder(Item(ContentShareType.World, "https://example.com/w.BEE"))));
        }
        finally
        {
            BasisGlobalLockManager.InitializeFromConfig(new Configuration());
        }
    }
}
