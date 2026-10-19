using System;
using NUnit.Framework;
using static SerializableBasis;

public class BasisLinkSafetyTests
{
    private static Uri Parse(string value)
    {
        Assert.IsTrue(ContentSharePayload.TryParseLink(value, out Uri link), value);
        return link;
    }

    [TestCase("https://trusted.example@evil.example/")]
    [TestCase("https://user:pass@example.com/")]
    [TestCase("https://evil.example\\@trusted.example/")]
    [TestCase("https://example.com/\"onmouseover")]
    [TestCase("https://example.com/<script>")]
    [TestCase("http://0x/")]
    [TestCase("http://1.2.3.4.5/")]
    [TestCase("http://[fe80::1%25eth0]/")]
    [TestCase("http://-example.com/")]
    [TestCase("ftp://example.com/")]
    [TestCase("javascript:alert(1)")]
    public void TheSharedLinkRulesRefuseMisleadingAddresses(string value)
    {
        Assert.IsFalse(ContentSharePayload.TryParseLink(value, out Uri link), value);
        Assert.IsNull(link);
    }

    [Test]
    public void TheSharedLinkRulesRefuseHiddenCharacters()
    {
        Assert.IsFalse(ContentSharePayload.TryParseLink("https://example.com/" + char.ConvertFromUtf32(0xE0041) + "x", out _));
        Assert.IsFalse(ContentSharePayload.TryParseLink("https://example.com/" + (char)0x202E + "gpj.exe", out _));
        Assert.IsFalse(ContentSharePayload.TryParseLink("https://example.com/" + (char)0xD800 + "x", out _));
        Assert.IsFalse(ContentSharePayload.TryParseLink("https://exam" + (char)0x00AD + "ple.com/", out _), "soft hyphen");
    }

    [TestCase("https://example.com/")]
    [TestCase("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [TestCase("https://notbit.ly/")]
    [TestCase("https://example.com/readme.txt")]
    public void OrdinaryLinksCarryNoWarnings(string value)
    {
        Assert.AreEqual(BasisLinkSafety.Warning.None, BasisLinkSafety.Inspect(Parse(value)), value);
    }

    [TestCase("http://localhost:8080/")]
    [TestCase("http://127.0.0.1/")]
    [TestCase("http://2130706433/")]
    [TestCase("http://0.0.0.0/")]
    [TestCase("http://10.0.0.5/")]
    [TestCase("http://172.20.1.1/")]
    [TestCase("http://192.168.1.1/admin")]
    [TestCase("http://169.254.169.254/latest/meta-data/")]
    [TestCase("http://100.100.1.1/")]
    [TestCase("http://[::1]:8080/")]
    [TestCase("http://[fd12:3456::1]/")]
    [TestCase("http://[fe80::1]/")]
    [TestCase("http://[::ffff:192.168.0.1]/")]
    [TestCase("http://router/")]
    [TestCase("https://printer.local/")]
    [TestCase("https://nas.home.arpa/")]
    [TestCase("https://app.localtest.me/")]
    [TestCase("https://192.168.1.1.nip.io/")]
    public void LinksIntoTheLocalNetworkAreFlagged(string value)
    {
        Uri link = Parse(value);
        BasisLinkSafety.Warning warnings = BasisLinkSafety.Inspect(link);
        Assert.AreNotEqual(BasisLinkSafety.Warning.None, warnings & BasisLinkSafety.Warning.LocalNetwork, value);
        Assert.AreEqual(BasisLinkSafety.Warning.None, warnings & BasisLinkSafety.Warning.Unencrypted, "the local-network warning already covers plain http");
        Assert.IsTrue(BasisLinkSafety.TargetsLocalNetwork(link), value);
    }

    [TestCase("https://8.8.8.8/", BasisLinkSafety.Warning.IpAddress)]
    [TestCase("https://[2001:4860:4860::8888]/", BasisLinkSafety.Warning.IpAddress)]
    [TestCase("https://8.8.8.8.nip.io/", BasisLinkSafety.Warning.IpAddress)]
    [TestCase("https://xn--pple-43d.com/", BasisLinkSafety.Warning.LookAlikeName)]
    [TestCase("https://bit.ly/3abcd", BasisLinkSafety.Warning.Shortened)]
    [TestCase("https://go.bit.ly/x", BasisLinkSafety.Warning.Shortened)]
    [TestCase("https://example.com/setup.exe", BasisLinkSafety.Warning.Program)]
    [TestCase("https://example.com/download/Setup.EXE?x=1", BasisLinkSafety.Warning.Program)]
    [TestCase("https://example.com/setup%2Eexe", BasisLinkSafety.Warning.Program)]
    [TestCase("https://example.com/setup.exe.", BasisLinkSafety.Warning.Program)]
    [TestCase("https://example.com/app.apk", BasisLinkSafety.Warning.Program)]
    [TestCase("http://example.com/", BasisLinkSafety.Warning.Unencrypted)]
    public void RiskyLinksAreFlagged(string value, BasisLinkSafety.Warning expected)
    {
        Assert.AreEqual(expected, BasisLinkSafety.Inspect(Parse(value)), value);
    }

    [Test]
    public void UnicodeLookAlikesAreFlagged()
    {
        Assert.AreEqual(BasisLinkSafety.Warning.LookAlikeName, BasisLinkSafety.Inspect(Parse("https://" + (char)0x0430 + "pple.com/")));
    }

    [Test]
    public void WarningsAddUp()
    {
        BasisLinkSafety.Warning warnings = BasisLinkSafety.Inspect(Parse("http://192.168.1.1/setup.exe"));
        Assert.AreEqual(BasisLinkSafety.Warning.LocalNetwork | BasisLinkSafety.Warning.Program, warnings);
        Assert.AreEqual(warnings, warnings & BasisLinkSafety.Severe);
    }

    [TestCase("https://example.com/watch?v=abc&list=x#t=10", "https://example.com/watch?v=abc&list=x#t=10")]
    [TestCase("HTTPS://Example.COM/Path?Q=1", "https://example.com/Path?Q=1")]
    [TestCase("https://example.com/a|b{c}^d`e", "https://example.com/a%7Cb%7Bc%7D%5Ed%60e")]
    [TestCase("https://example.com/a/../b/./c", "https://example.com/b/c")]
    [TestCase("https://[2001:db8::1]:8443/x", "https://[2001:db8::1]:8443/x")]
    [TestCase("http://2130706433/", "http://127.0.0.1/")]
    public void TheOpenedAddressIsCanonicalAscii(string value, string expected)
    {
        Assert.IsTrue(BasisLinkSafety.TryGetOpenAddress(Parse(value), out string address), value);
        Assert.AreEqual(expected, address);
    }

    [Test]
    public void InternationalSitesOpenByTheirAsciiName()
    {
        string value = "https://b" + (char)0xFC + "cher.example/stra" + (char)0xDF + "e?q=" + (char)0xFC + "#" + char.ConvertFromUtf32(0x1F600);
        Assert.IsTrue(BasisLinkSafety.TryGetOpenAddress(Parse(value), out string address));
        Assert.AreEqual("https://xn--bcher-kva.example/stra%C3%9Fe?q=%C3%BC#%F0%9F%98%80", address);
    }

    [TestCase("https://example.com/watch?v=abc&list=x#t=10")]
    [TestCase("HTTPS://Example.COM/Path?Q=1")]
    [TestCase("https://example.com/a|b{c}^d`e")]
    [TestCase("https://[2001:db8::1]:8443/x")]
    [TestCase("https://example.com/%")]
    [TestCase("https://example.com/a'b(c)!d*e;f=g")]
    [TestCase("https://example.com/search?q=caf%c3%a9")]
    [TestCase("https://example.com/%=%4C;-Y")]
    [TestCase("https://example.com/%%35##x")]
    [TestCase("https://example.com/%7e%2D%5f")]
    public void TheOpenedAddressRebuildsToItselfAndPassesTheSharedRules(string value)
    {
        Assert.IsTrue(BasisLinkSafety.TryGetOpenAddress(Parse(value), out string address), value);
        Assert.IsTrue(ContentSharePayload.TryParseLink(address, out Uri again), address);
        Assert.IsTrue(BasisLinkSafety.TryGetOpenAddress(again, out string rebuilt), address);
        Assert.AreEqual(address, rebuilt);
        foreach (char c in address)
        {
            Assert.IsTrue(c > ' ' && c < (char)0x7F && "\"<>\\^`{|}".IndexOf(c) < 0, address);
        }
    }

    [TestCase("https://example.com/", "example.com")]
    [TestCase("http://example.com:8080/", "example.com:8080")]
    [TestCase("https://[2001:db8::1]:8443/", "[2001:db8::1]:8443")]
    [TestCase("http://2130706433/", "127.0.0.1")]
    public void TheSiteIsNamedTheWayTheBrowserWillUseIt(string value, string expected)
    {
        Assert.AreEqual(expected, BasisLinkSafety.SiteLabel(Parse(value)));
    }

    [Test]
    public void AnInternationalSiteShowsBothSpellings()
    {
        Assert.AreEqual("b" + (char)0xFC + "cher.example (xn--bcher-kva.example)", BasisLinkSafety.SiteLabel(Parse("https://b" + (char)0xFC + "cher.example/")));
    }

    [Test]
    public void OpenRefusesAnythingButAPlainWebAddress()
    {
        Assert.IsFalse(BasisLinkSafety.Open("file:///C:/Windows/System32/calc.exe"));
        Assert.IsFalse(BasisLinkSafety.Open("javascript:alert(1)"));
        Assert.IsFalse(BasisLinkSafety.Open("ms-settings:privacy"));
        Assert.IsFalse(BasisLinkSafety.Open("steam://run/440"));
        Assert.IsFalse(BasisLinkSafety.Open(string.Empty));
        Assert.IsFalse(BasisLinkSafety.Open(null));
        Assert.IsFalse(BasisLinkSafety.Open("https://trusted.example@evil.example/"));
        Assert.IsFalse(BasisLinkSafety.Open("https://example.com/a b"));
        Assert.IsFalse(BasisLinkSafety.Open("https://b" + (char)0xFC + "cher.example/"));
    }
}
