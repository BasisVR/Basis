using Basis.Network.Core;
using BasisNetworkServer;
using BasisNetworkServer.BasisNetworking;
using BasisPermissions;
using Xunit;
using static BasisPermissions.PermissionManager;
using static SerializableBasis;

namespace BasisServerTests;

[Collection("BasisServer shared network statics")]
public class ContentShareSettingTests
{
    private static readonly MapAuthIdentity Identity = new();
    private static int peerIdCounter = 29_000;

    private static FakeNetPeer NewAuthenticatedPeer()
    {
        NetworkServer.AuthIdentity = Identity;
        PermissionIntegration.Manager.EnsureDefaults();
        int id = Interlocked.Increment(ref peerIdCounter);
        FakeNetPeer peer = new FakeNetPeer(id, "10.9.8.7") { Tag = NetworkServer.AuthenticatedPeerTag };
        Identity.Register($"setting-share-user-{Guid.NewGuid():N}", id, peer);
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

    private static bool Relayed(string payload)
    {
        FakeNetPeer a = NewAuthenticatedPeer();
        FakeNetPeer b = NewAuthenticatedPeer();
        string sphereId = $"sphere-{Guid.NewGuid():N}";
        try
        {
            ContentShareMessage msg = new ContentShareMessage
            {
                SphereNetID = sphereId,
                ContentURL = payload,
                UnlockPassword = string.Empty,
                ContentType = ContentShareType.Setting,
                PositionX = 1f, PositionY = 2f, PositionZ = 3f,
            };
            BasisNetworkMessageProcessor.ProcessMessage(a, Packet(w =>
            {
                w.Put(BasisNetworkCommons.ContentShareSub_Drop);
                msg.Serialize(w);
            }), BasisNetworkCommons.ContentShareChannel, DeliveryMethod.ReliableOrdered);

            bool stored = BasisNetworkContentShare.ActiveSpheres.ContainsKey(sphereId);
            bool reached = false;
            foreach ((byte[] data, byte channel, DeliveryMethod _) in b.Sent)
            {
                if (channel != BasisNetworkCommons.ContentShareChannel) continue;
                NetDataReader r = new NetDataReader(data);
                if (r.GetByte() != BasisNetworkCommons.ContentShareSub_Drop) continue;
                ServerContentShareMessage m = new ServerContentShareMessage();
                m.Deserialize(r);
                if (m.contentShareMessage.SphereNetID != sphereId) continue;
                Assert.Equal(ContentShareType.Setting, m.contentShareMessage.ContentType);
                Assert.Equal(payload, m.contentShareMessage.ContentURL);
                reached = true;
            }
            Assert.Equal(stored, reached);
            return reached;
        }
        finally
        {
            BasisNetworkContentShare.ActiveSpheres.TryRemove(sphereId, out _);
            Remove(a, b);
        }
    }

    private static ContentShareSetting.Contents Location(string key = "main volume", string tab = "settings.tab.audio") =>
        new ContentShareSetting.Contents { Tab = tab, Key = key, Title = "Main Volume", Path = "Audio › Volume" };

    private static ContentShareSetting.Contents Value(string type, string value, string text = "75%") =>
        new ContentShareSetting.Contents { Tab = "settings.tab.audio", Key = "main volume", Title = "Main Volume", Path = "Audio", ValueType = type, Value = value, ValueText = text };

    [Fact]
    public void LocationShare_RoundTrips_WithoutAValue()
    {
        string payload = ContentShareSetting.Write(Location());
        Assert.True(ContentShareSetting.IsSettingText(payload));
        Assert.True(ContentShareSetting.TryRead(payload, out ContentShareSetting.Contents read));
        Assert.Equal(ContentShareSetting.SettingsMenu, read.Menu);
        Assert.Equal("settings.tab.audio", read.Tab);
        Assert.Equal("main volume", read.Key);
        Assert.Equal("Main Volume", read.Title);
        Assert.Equal("Audio › Volume", read.Path);
        Assert.False(read.HasValue);
        Assert.Null(read.Value);
    }

    [Theory]
    [InlineData(ContentShareSetting.BoolType, "true")]
    [InlineData(ContentShareSetting.BoolType, "false")]
    [InlineData(ContentShareSetting.NumberType, "75")]
    [InlineData(ContentShareSetting.NumberType, "-0.125")]
    [InlineData(ContentShareSetting.NumberType, "1E-05")]
    [InlineData(ContentShareSetting.TextType, "High")]
    [InlineData(ContentShareSetting.TextType, "Microphone (USB) 100% <b>")]
    public void ValueShare_RoundTripsTheValueExactly(string type, string value)
    {
        Assert.True(ContentShareSetting.TryRead(ContentShareSetting.Write(Value(type, value)), out ContentShareSetting.Contents read));
        Assert.True(read.HasValue);
        Assert.Equal(type, read.ValueType);
        Assert.Equal(value, read.Value);
        Assert.Equal("75%", read.ValueText);
    }

    [Fact]
    public void Write_EscapesSeparatorsInKeys_AndCleansTheLabels()
    {
        ContentShareSetting.Contents setting = new ContentShareSetting.Contents
        {
            Tab = "Media 100%",
            Key = "odd%key",
            Title = "Line\nBreak\tTitle",
            Path = "A‮ › B",
            ValueType = ContentShareSetting.TextType,
            Value = "a%09b",
            ValueText = "Shown\r\nValue",
        };
        Assert.True(ContentShareSetting.TryRead(ContentShareSetting.Write(setting), out ContentShareSetting.Contents read));
        Assert.Equal("Media 100%", read.Tab);
        Assert.Equal("odd%key", read.Key);
        Assert.Equal("Line Break Title", read.Title);
        Assert.Equal("A › B", read.Path);
        Assert.Equal("a%09b", read.Value);
        Assert.Equal("Shown Value", read.ValueText);
    }

    [Fact]
    public void NumbersAreWrittenWithoutTheLocalCulture()
    {
        System.Globalization.CultureInfo previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("0.25", ContentShareSetting.WriteNumber(0.25f));
            Assert.Equal("-3", ContentShareSetting.WriteNumber(-3));
            Assert.True(ContentShareSetting.TryReadNumber("0.25", out double number));
            Assert.Equal(0.25, number);
            Assert.False(ContentShareSetting.TryReadNumber("0,25", out _));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Read_ToleratesWindowsLineBreaks_AndIgnoresWhatANewerWriterAppends()
    {
        string payload = ContentShareSetting.Header + "\r\nsettings\tsettings.tab.audio\tmain volume\tfuture\r\nMain Volume\tAudio\tfuture\r\nnumber\t75\t75%\tfuture\r\nfuture line\r\n";
        Assert.True(ContentShareSetting.TryRead(payload, out ContentShareSetting.Contents read));
        Assert.Equal("main volume", read.Key);
        Assert.Equal("Audio", read.Path);
        Assert.Equal("75", read.Value);
        Assert.Equal("75%", read.ValueText);
    }

    public static IEnumerable<object[]> MalformedSettings()
    {
        string where = "settings\tsettings.tab.audio\tmain volume\n";
        string label = "Main Volume\tAudio";
        yield return new object[] { "" };
        yield return new object[] { "hello" };
        yield return new object[] { ContentShareSetting.Header };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\tmain volume" };
        yield return new object[] { "basis-setting:2\n" + where + label };
        yield return new object[] { "basis-setting:10\n" + where + label };
        yield return new object[] { " " + ContentShareSetting.Header + "\n" + where + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nSettings\tsettings.tab.audio\tmain volume\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\n\tsettings.tab.audio\tmain volume\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\t\tmain volume\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\t   \n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\tmain%09volume\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\tmain​volume\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\nsettings\tsettings.tab.audio\t" + new string('k', ContentShareSetting.MaxKeyLength + 1) + "\n" + label };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nbool" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nbool\tyes" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nbool\tTrue" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\tNaN" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\tInfinity" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\t1e400" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\t 5" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\t0x10" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\nnumber\t" + new string('1', ContentShareSetting.MaxNumberLength + 1) };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\ntext\tline%0Abreak" };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\ntext\t" + new string('t', ContentShareSetting.MaxValueLength + 1) };
        yield return new object[] { ContentShareSetting.Header + "\n" + where + label + "\ncolor\t#ffffff" };
    }

    [Theory]
    [MemberData(nameof(MalformedSettings))]
    public void Read_RefusesAnythingThatIsNotAWellFormedSetting(string payload)
    {
        Assert.False(ContentShareSetting.TryRead(payload, out ContentShareSetting.Contents read));
        Assert.Null(read);
    }

    [Fact]
    public void Read_RefusesAPayloadOverTheTextCeiling()
    {
        ContentShareSetting.Contents setting = Location();
        setting.Path = new string('p', ContentSharePayload.MaxTextLength);
        Assert.True(ContentShareSetting.TryRead(ContentShareSetting.Write(setting), out _));
        string padded = ContentShareSetting.Write(Location()) + "\n" + new string('x', ContentSharePayload.MaxTextLength);
        Assert.False(ContentShareSetting.TryRead(padded, out _));
    }

    [Fact]
    public void SettingIsAnInlinePayload_WithTheTextCeiling()
    {
        Assert.Equal(8, (byte)ContentShareType.Setting);
        Assert.True(ContentSharePayload.IsPayloadType(ContentShareType.Setting));
        Assert.False(ContentSharePayload.IsTextType(ContentShareType.Setting));
        Assert.Equal(ContentSharePayload.MaxTextLength, ContentSharePayload.MaxLengthFor(ContentShareType.Setting));
    }

    [Fact]
    public void SettingShares_AreStoredAndRelayedVerbatim()
    {
        Assert.True(Relayed(ContentShareSetting.Write(Location())));
        Assert.True(Relayed(ContentShareSetting.Write(Value(ContentShareSetting.NumberType, "0.5"))));
    }

    [Fact]
    public void SharesFromAnyMenu_AreRelayed_NotOnlySettings()
    {
        ContentShareSetting.Contents camera = Value(ContentShareSetting.NumberType, "35", "35 mm");
        camera.Menu = "handheldcamera";
        camera.Tab = "camera.tab.image";
        camera.Key = "#camera.section.lens>camera.focalLength";
        ContentShareSetting.Contents servers = Location("=Host Locally", "@host");
        servers.Menu = "servers";
        ContentShareSetting.Contents player = Location("#menu.individualPlayer.volume", "@0f2c9a1e-5b7d-4c3e-9f10-2a6b8c4d7e91>menu.individualPlayer.audio");
        player.Menu = "individualplayer";
        Assert.True(Relayed(ContentShareSetting.Write(camera)));
        Assert.True(Relayed(ContentShareSetting.Write(servers)));
        Assert.True(Relayed(ContentShareSetting.Write(player)));
        Assert.True(ContentShareSetting.TryRead(ContentShareSetting.Write(camera), out ContentShareSetting.Contents read));
        Assert.Equal("handheldcamera", read.Menu);
        Assert.Equal("#camera.section.lens>camera.focalLength", read.Key);
    }

    [Theory]
    [MemberData(nameof(MalformedSettings))]
    public void MalformedSettingShares_AreRefused(string payload)
    {
        Assert.False(Relayed(payload));
    }

    [Fact]
    public void SettingShare_OverTheTextCeiling_IsRefused()
    {
        string payload = ContentShareSetting.Write(Location()) + "\n" + new string('x', ContentSharePayload.MaxTextLength);
        Assert.False(Relayed(payload));
    }
}
