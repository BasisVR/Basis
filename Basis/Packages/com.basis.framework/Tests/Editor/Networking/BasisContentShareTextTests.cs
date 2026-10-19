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
    [TestCase("https://trusted.example@evil.example/", ContentShareType.Text)]
    [TestCase("https://evil.example\\@trusted.example/", ContentShareType.Text)]
    [TestCase("http://0x/", ContentShareType.Text)]
    [TestCase("http://2130706433/", ContentShareType.Link)]
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
    public void TheOrbNamesALinksSiteInItsAsciiSpelling()
    {
        Assert.AreEqual("xn--bcher-kva.example", BasisContentSharePayloadRegistry.Describe(ContentShareType.Link, "https://b" + (char)0xFC + "cher.example/"));
        Assert.AreEqual("xn--pple-43d.com", BasisContentSharePayloadRegistry.Describe(ContentShareType.Link, "https://" + (char)0x0430 + "pple.com/"));
        Assert.IsNull(BasisContentSharePayloadRegistry.Describe(ContentShareType.Link, "https://trusted.example@evil.example/"));
    }

    [Test]
    public void NormalizeDropsInvisibleFormattingButKeepsJoiners()
    {
        string family = char.ConvertFromUtf32(0x1F468) + (char)0x200D + char.ConvertFromUtf32(0x1F469) + (char)0x200D + char.ConvertFromUtf32(0x1F467);
        string persian = "ab" + (char)0x200C + "cd";
        Assert.AreEqual(family, BasisContentShareText.Normalize(family));
        Assert.AreEqual(persian, BasisContentShareText.Normalize(persian));

        string hidden = "pay" + (char)0x202E + "lo" + (char)0x200B + "ad" + (char)0x2060 + (char)0xFEFF + (char)0x00AD + char.ConvertFromUtf32(0xE0041) + "!";
        Assert.AreEqual("payload!", BasisContentShareText.Normalize(hidden));
    }

    [Test]
    public void NormalizeTurnsUnicodeLineBreaksIntoNewlinesAndDropsBrokenSurrogates()
    {
        Assert.AreEqual("a\nb\nc", BasisContentShareText.Normalize("a" + (char)0x2028 + "b" + (char)0x2029 + "c"));
        Assert.AreEqual("ab", BasisContentShareText.Normalize("a" + (char)0xD800 + "b" + (char)0xDC00));
    }

    [Test]
    public void NormalizeCutsStackedCombiningMarks()
    {
        string zalgo = "e" + new string((char)0x0301, 30) + "x";
        Assert.AreEqual("e" + new string((char)0x0301, BasisContentShareText.MaxCombiningMarks) + "x", BasisContentShareText.Normalize(zalgo));

        string vietnamese = "e" + (char)0x0302 + (char)0x0301;
        string keycap = "1" + (char)0xFE0F + (char)0x20E3;
        Assert.AreEqual(vietnamese, BasisContentShareText.Normalize(vietnamese));
        Assert.AreEqual(keycap, BasisContentShareText.Normalize(keycap));
    }

    [Test]
    public void VerbatimShowsTheTextButNoEscapeCanDecode()
    {
        string backslash = "\\";
        string raw = "<b>" + backslash + "u0041" + backslash + "U0001F600</b>";
        string shown = BasisContentShareText.Verbatim(raw);
        Assert.IsTrue(shown.StartsWith("<b>"), shown);
        for (int i = 0; i < shown.Length; i++)
        {
            if (shown[i] == '\\') Assert.AreEqual((char)0x200B, shown[i + 1], shown);
        }
        Assert.AreEqual(raw, shown.Replace(((char)0x200B).ToString(), string.Empty));
    }

    [TestCase("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA")]
    [TestCase("ghp_0123456789abcdefghijABCDEFGHIJ012345")]
    [TestCase("AKIAIOSFODNN7EXAMPLE")]
    [TestCase("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [TestCase("https://user:hunter2@example.com/")]
    [TestCase("https://example.com/reset?token=abcdef123456")]
    [TestCase("my login: https://example.com/#access_token=abcdefgh12345678")]
    [TestCase("Tr0ub4dor&3")]
    [TestCase("aB3dE5fG7hJ9kL1mN3pQ5rS7tU9vW1xY3z")]
    public void LooksSensitiveCatchesSecretsBeforeTheyAreShared(string pasted)
    {
        Assert.IsTrue(BasisContentShareText.LooksSensitive(pasted), pasted);
    }

    [TestCase("hello world")]
    [TestCase("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("Meet at 7pm!")]
    [TestCase("Bob.Smith1@Example.com")]
    [TestCase("d2b5c3f0e1a4b6c8d0e2f4a6b8c0d2e4f6a8b0c2")]
    [TestCase("550e8400-e29b-41d4-a716-446655440000")]
    [TestCase("correcthorsebatterystaple")]
    [TestCase("https://example.com:8080/path@x")]
    public void LooksSensitiveLeavesOrdinaryTextAlone(string pasted)
    {
        Assert.IsFalse(BasisContentShareText.LooksSensitive(pasted), pasted);
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
