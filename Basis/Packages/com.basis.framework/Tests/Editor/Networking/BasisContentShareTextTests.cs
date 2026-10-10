using System;
using Basis.Scripts.Platform;
using NUnit.Framework;
using static SerializableBasis;

public class BasisContentShareTextTests
{
    [SetUp]
    public void SetUp()
    {
        BasisContentShareText.Register();
    }

    [Test]
    public void LinkAndTextAreRegisteredAsInlinePayloads()
    {
        Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.Link, out BasisContentSharePayloadKind link));
        Assert.IsTrue(BasisContentSharePayloadRegistry.TryGet(ContentShareType.Text, out BasisContentSharePayloadKind text));
        Assert.AreEqual(BasisShareableKind.Link, link.ShareableKind);
        Assert.AreEqual(BasisShareableKind.Text, text.ShareableKind);
        Assert.IsTrue(ContentSharePayload.IsPayloadType(ContentShareType.Link));
        Assert.IsTrue(ContentSharePayload.IsPayloadType(ContentShareType.Text));
    }

    [TestCase("https://example.com/watch?v=1", ContentShareType.Link)]
    [TestCase("HTTP://EXAMPLE.COM", ContentShareType.Link)]
    [TestCase("  http://localhost:8080/  \r\n", ContentShareType.Link)]
    [TestCase("look at https://example.com", ContentShareType.Text)]
    [TestCase("javascript:alert(1)", ContentShareType.Text)]
    [TestCase("file:///C:/Windows/System32/calc.exe", ContentShareType.Text)]
    [TestCase("steam://run/440", ContentShareType.Text)]
    [TestCase("example.com", ContentShareType.Text)]
    public void PastedTextIsALinkOnlyWhenItIsOneWebAddress(string pasted, ContentShareType expected)
    {
        Assert.AreEqual(expected, BasisContentShareText.Classify(BasisContentShareText.Normalize(pasted)));
    }

    [Test]
    public void NormalizeTrimsAndKeepsLineBreaksButDropsControlCharacters()
    {
        Assert.AreEqual("a\nb\tc\nd", BasisContentShareText.Normalize("  a\r\nb\tc\rd\0\u0007  \r\n"));
        Assert.AreEqual(string.Empty, BasisContentShareText.Normalize(" \r\n\t "));
        Assert.AreEqual(string.Empty, BasisContentShareText.Normalize(null));
    }

    [Test]
    public void PreviewIsOneShortLine()
    {
        Assert.AreEqual("hello world", BasisContentShareText.Preview("hello\n\n  world", 24));
        Assert.AreEqual("abcdef...", BasisContentShareText.Preview("abcdefghij", 6));
        Assert.AreEqual("short", BasisContentShareText.Preview("short", 24));
    }

    [Test]
    public void PreviewNeverSplitsASurrogatePair()
    {
        string emoji = "\U0001F600";
        Assert.AreEqual("ab...", BasisContentShareText.Preview("ab" + emoji + emoji, 3));
        Assert.AreEqual("ab" + emoji + "...", BasisContentShareText.Preview("ab" + emoji + emoji, 4));
    }

    [Test]
    public void PlainLeavesNothingForTextMeshProToParse()
    {
        string plain = BasisContentShareText.Plain("<size=500>big</size> \\u003Csize=500\\u003E \\n");
        Assert.IsFalse(plain.Contains("<"), plain);
        for (int i = 0; i < plain.Length; i++)
        {
            if (plain[i] == '\\') Assert.AreEqual('\u200B', plain[i + 1], plain);
        }
    }

    [Test]
    public void DescribeShowsTheHostForLinksAndAPlainPreviewForText()
    {
        Assert.AreEqual("example.com", BasisContentSharePayloadRegistry.Describe(ContentShareType.Link, "https://example.com/some/long/path?q=1"));
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Link, "javascript:alert(1)"));
        string text = BasisContentSharePayloadRegistry.Describe(ContentShareType.Text, "<color=red>hello</color>\nsecond line that goes on");
        Assert.IsFalse(text.Contains("<"), text);
        Assert.IsFalse(text.Contains("\n"), text);
        Assert.LessOrEqual(text.Length, BasisContentSphere.MaxTitleNameLength + 3);
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Text, "   "));
    }

    [Test]
    public void OpenLinkRefusesAnythingButWebAddresses()
    {
        Assert.IsFalse(BasisContentShareText.OpenLink("file:///C:/Windows/System32/calc.exe"));
        Assert.IsFalse(BasisContentShareText.OpenLink("javascript:alert(1)"));
        Assert.IsFalse(BasisContentShareText.OpenLink("ms-settings:privacy"));
        Assert.IsFalse(BasisContentShareText.OpenLink(string.Empty));
    }

    [Test]
    public void PastedTextReachesTextSubscribers()
    {
        BasisDesktopClipboard.OnTextPasted -= BasisContentShareText.Share;
        string received = null;
        Action<string> handler = pasted => received = pasted;
        BasisDesktopClipboard.OnTextPasted += handler;
        try
        {
            BasisDesktopClipboard.SubmitText("hello");
            BasisDesktopClipboard.Dispatch();
            Assert.AreEqual("hello", received);
        }
        finally
        {
            BasisDesktopClipboard.OnTextPasted -= handler;
        }
    }
}
