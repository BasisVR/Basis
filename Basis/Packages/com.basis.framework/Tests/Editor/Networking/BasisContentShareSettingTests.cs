using NUnit.Framework;
using static SerializableBasis;

public class BasisContentShareSettingTests
{
    [SetUp]
    public void SetUp()
    {
        BasisContentShareSetting.Register();
    }

    private static ContentShareSetting.Contents Location() =>
        new ContentShareSetting.Contents { Tab = "settings.tab.graphics", Key = "shadowquality", Title = "Shadow Quality", Path = "Graphics › Lighting" };

    private static ContentShareSetting.Contents Value(string type, string value, string text)
    {
        ContentShareSetting.Contents setting = Location();
        setting.ValueType = type;
        setting.Value = value;
        setting.ValueText = text;
        return setting;
    }

    [Test]
    public void SettingIsRegisteredAsAnInlinePayload()
    {
        Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.Setting, out BasisContentSharePayloadKind kind));
        Assert.AreEqual(BasisShareableKind.Setting, kind.ShareableKind);
        Assert.AreEqual("content.share.type.setting", kind.NameKey);
        Assert.IsNull(kind.Accept);
        Assert.IsTrue(ContentSharePayload.IsPayloadType(ContentShareType.Setting));
        Assert.IsTrue(BasisContentSharePayloadRegistry.IsHandled(ContentShareType.Setting));
    }

    [Test]
    public void TheOrbNamesTheSetting_AndAValueShareAddsTheValueAsShown()
    {
        Assert.AreEqual("Shadow Quality", BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, ContentShareSetting.Write(Location())));
        Assert.AreEqual("Shadow Quality: High", BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, ContentShareSetting.Write(Value(ContentShareSetting.TextType, "high", "High"))));
        Assert.AreEqual("Shadow Quality", BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, ContentShareSetting.Write(Value(ContentShareSetting.TextType, "high", string.Empty))));
    }

    [Test]
    public void AShareWithNoTitleIsNamedByItsKey()
    {
        ContentShareSetting.Contents untitled = Location();
        untitled.Title = "​\t";
        Assert.IsTrue(ContentShareSetting.TryRead(ContentShareSetting.Write(untitled), out ContentShareSetting.Contents read));
        Assert.AreEqual("shadowquality", BasisContentShareSetting.DisplayTitle(read));
        Assert.AreEqual("shadowquality", BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, ContentShareSetting.Write(untitled)));
    }

    [Test]
    public void AnUnreadablePayloadDescribesNothing()
    {
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, ContentShareSetting.Header + "\nnot a setting"));
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, "https://example.com"));
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Setting, null));
    }

    [Test]
    public void NothingIsCapturedOrMatchedWithoutAControl()
    {
        Assert.IsFalse(BasisContentShareSetting.TryCapture(null, out ContentShareSetting.Contents location, out ContentShareSetting.Contents value));
        Assert.IsNull(location);
        Assert.IsNull(value);
        Assert.IsFalse(BasisContentShareSetting.Matcher(Location())(null));
        Assert.IsNull(BasisContentShareSetting.Matcher(null));
        Assert.IsFalse(BasisContentShareSetting.CanOffer(null));
    }

    [Test]
    public void AShareFromAnyMenuReadsBackWithItsMenu()
    {
        ContentShareSetting.Contents camera = Value(ContentShareSetting.NumberType, "35", "35 mm");
        camera.Menu = "handheldcamera";
        camera.Tab = "camera.tab.image";
        camera.Key = "#camera.section.lens>camera.focalLength";
        Assert.IsTrue(ContentShareSetting.TryRead(ContentShareSetting.Write(camera), out ContentShareSetting.Contents read));
        Assert.AreEqual("handheldcamera", read.Menu);
        Assert.AreEqual("camera.tab.image", read.Tab);
        Assert.AreEqual("#camera.section.lens>camera.focalLength", read.Key);
        Assert.AreEqual("35", read.Value);

        ContentShareSetting.Contents player = Value(ContentShareSetting.NumberType, "0.5", "50%");
        player.Menu = "individualplayer";
        player.Tab = "@0f2c9a1e-5b7d-4c3e-9f10-2a6b8c4d7e91>menu.individualPlayer.audio";
        player.Key = "#menu.individualPlayer.volume";
        Assert.IsTrue(ContentShareSetting.TryRead(ContentShareSetting.Write(player), out ContentShareSetting.Contents readPlayer));
        Assert.AreEqual(player.Tab, readPlayer.Tab);
    }
}
