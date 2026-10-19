using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;
using static SerializableBasis;

/// <summary>
/// What somebody should know before a link another player shared opens in their browser, and the
/// one way such a link reaches the operating system. Everything here reads a <see cref="Uri"/> that
/// already passed <see cref="ContentSharePayload.TryParseLink"/>. The warnings are advice shown next
/// to the "do you want to open this link?" question; they never stand in for asking it.
/// </summary>
public static class BasisLinkSafety
{
    [Flags]
    public enum Warning
    {
        None = 0,
        /// <summary>This computer or a device on the local network: a router, a NAS, a tool listening on localhost.</summary>
        LocalNetwork = 1 << 0,
        /// <summary>A bare numeric address where a site name would be.</summary>
        IpAddress = 1 << 1,
        /// <summary>The site name is spelled with characters outside plain ASCII, which can imitate another site.</summary>
        LookAlikeName = 1 << 2,
        /// <summary>A link shortener, so the real destination stays hidden until it opens.</summary>
        Shortened = 1 << 3,
        /// <summary>The address ends in a file that installs or runs software.</summary>
        Program = 1 << 4,
        /// <summary>Plain http: the page, and anything typed into it, travels unencrypted.</summary>
        Unencrypted = 1 << 5,
    }

    /// <summary>The warnings that should turn the question red rather than amber.</summary>
    public const Warning Severe = Warning.LocalNetwork | Warning.LookAlikeName | Warning.Program;

    private static readonly string[] Shorteners =
    {
        "bit.ly", "bitly.com", "tinyurl.com", "t.co", "goo.gl", "ow.ly", "is.gd", "v.gd", "buff.ly",
        "rebrand.ly", "cutt.ly", "shorturl.at", "rb.gy", "tiny.cc", "t.ly", "s.id", "bl.ink", "lnkd.in",
        "adf.ly", "shorte.st", "clck.ru", "dub.sh", "qrco.de",
    };

    // Names that only ever mean "this network": private-use suffixes, mDNS, and the public names
    // that resolve to 127.0.0.1 for local development.
    private static readonly string[] LocalSuffixes =
    {
        "localhost", "local", "lan", "home", "internal", "intranet", "corp", "localdomain", "home.arpa",
        "localtest.me", "lvh.me", "vcap.me",
    };

    // Lower case, without the dot. Installers, executables, scripts the shell runs on a double
    // click, and the disk images and shortcuts used to smuggle them.
    private static readonly string[] ProgramExtensions =
    {
        "exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "application",
        "bat", "cmd", "scr", "pif", "cpl", "msc", "ps1", "psm1", "vbs", "vbe", "jse", "wsf", "wsh",
        "hta", "lnk", "url", "scf", "reg", "chm", "diagcab", "xll", "settingcontent-ms", "jar", "dll",
        "iso", "vhd", "vhdx", "dmg", "pkg", "apk", "xapk", "deb", "rpm", "appimage",
    };

    /// <summary>Everything a person should be told about <paramref name="link"/> before agreeing to open it.</summary>
    public static Warning Inspect(Uri link)
    {
        if (link == null || !link.IsAbsoluteUri) return Warning.None;

        Warning warnings = Warning.None;
        bool local = false;
        switch (link.HostNameType)
        {
            case UriHostNameType.IPv4:
            case UriHostNameType.IPv6:
                local = !IPAddress.TryParse(link.IdnHost, out IPAddress address) || IsLocal(address);
                warnings |= local ? Warning.LocalNetwork : Warning.IpAddress;
                break;
            default:
                string host = link.IdnHost.TrimEnd('.').ToLowerInvariant();
                if (IsLocalName(host))
                {
                    local = true;
                    warnings |= Warning.LocalNetwork;
                }
                else if (TryFindEmbeddedAddress(host, out IPAddress embedded))
                {
                    // 192.168.1.1.nip.io and friends resolve to the address they spell out.
                    local = IsLocal(embedded);
                    warnings |= local ? Warning.LocalNetwork : Warning.IpAddress;
                }
                if (HasPunycodeLabel(host)) warnings |= Warning.LookAlikeName;
                if (MatchesDomain(host, Shorteners)) warnings |= Warning.Shortened;
                break;
        }

        // On the local network the bigger worry is already on screen.
        if (link.Scheme == Uri.UriSchemeHttp && !local) warnings |= Warning.Unencrypted;
        if (PointsAtProgram(link)) warnings |= Warning.Program;
        return warnings;
    }

    /// <summary>True when <paramref name="link"/> reaches this computer or a device on the local network.</summary>
    public static bool TargetsLocalNetwork(Uri link) => (Inspect(link) & Warning.LocalNetwork) != 0;

    /// <summary>
    /// The exact text handed to the browser for <paramref name="link"/>: plain ASCII, the site in its
    /// punycode spelling, every other character percent-encoded. The confirmation shows this same
    /// string, so what somebody agreed to open and what opens cannot differ, and nothing in it can
    /// be read by the shell as a second argument. False when it cannot be rebuilt into an address
    /// that still names the same site.
    /// </summary>
    public static bool TryGetOpenAddress(Uri link, out string address)
    {
        address = null;
        if (link == null || !link.IsAbsoluteUri) return false;

        // Uri's canonical form does not always survive a second reading: lower-case hex escapes,
        // escaped plain letters and a bare '%' beside real escapes all come back different. Rebuilt
        // until it stops moving, so this is the one string the confirmation shows, Open accepts and
        // the browser receives.
        Uri current = link;
        string previous = null;
        for (int pass = 0; pass < 4; pass++)
        {
            if (!TryBuildAddress(current, link, out string candidate, out Uri reread)) return false;
            if (candidate == previous)
            {
                address = candidate;
                return true;
            }
            previous = candidate;
            current = reread;
        }
        return false;
    }

    private static bool TryBuildAddress(Uri link, Uri original, out string address, out Uri reread)
    {
        address = null;
        reread = null;
        if (link.Scheme != Uri.UriSchemeHttp && link.Scheme != Uri.UriSchemeHttps) return false;
        if (!string.IsNullOrEmpty(link.UserInfo)) return false;

        string absolute = link.AbsoluteUri;
        string prefix = link.Scheme + "://";
        if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        // AbsoluteUri keeps an international site name as typed. The browser gets the ASCII form,
        // which is what IdnHost resolves and what the confirmation shows.
        if (link.HostNameType == UriHostNameType.Dns && !string.Equals(link.Host, link.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Compare(absolute, prefix.Length, link.Host, 0, link.Host.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            absolute = prefix + link.IdnHost + absolute.Substring(prefix.Length + link.Host.Length);
        }

        StringBuilder builder = new StringBuilder(absolute.Length + 16);
        for (int i = 0; i < absolute.Length; i++)
        {
            char c = absolute[i];
            if (IsUriCharacter(c))
            {
                builder.Append(c);
                continue;
            }
            int length = char.IsSurrogatePair(absolute, i) ? 2 : 1;
            if (length == 1 && char.IsSurrogate(c)) return false;
            byte[] bytes = Encoding.UTF8.GetBytes(absolute.Substring(i, length));
            for (int b = 0; b < bytes.Length; b++)
            {
                builder.Append('%').Append(bytes[b].ToString("X2", CultureInfo.InvariantCulture));
            }
            i += length - 1;
        }

        string candidate = builder.ToString();
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out reread)) return false;
        if (reread.Scheme != original.Scheme || reread.Port != original.Port || !string.IsNullOrEmpty(reread.UserInfo)) return false;
        if (!string.Equals(reread.IdnHost, original.IdnHost, StringComparison.OrdinalIgnoreCase)) return false;
        address = candidate;
        return true;
    }

    /// <summary>
    /// Opens an address from <see cref="TryGetOpenAddress"/> in the system browser, and only after
    /// the person said yes. Checked again here, because this is the last step before the operating
    /// system: anything but a plain ASCII web address that passes the shared link rules is refused.
    /// </summary>
    public static bool Open(string address)
    {
        if (!IsPlainAscii(address) || !ContentSharePayload.TryParseLink(address, out Uri link) || !TryGetOpenAddress(link, out string rebuilt) || !string.Equals(rebuilt, address, StringComparison.Ordinal))
        {
            BasisDebug.LogWarning("A shared link was not opened: it is not a plain http or https address.", BasisDebug.LogTag.Networking);
            return false;
        }
        Application.OpenURL(address);
        return true;
    }

    /// <summary>
    /// The site a link goes to, as the confirmation names it: the ASCII spelling the browser will
    /// use, after the readable international spelling when the two differ, with the port when it is
    /// not the scheme's default.
    /// </summary>
    public static string SiteLabel(Uri link)
    {
        if (link == null || !link.IsAbsoluteUri) return string.Empty;
        string ascii = link.HostNameType == UriHostNameType.IPv6 ? link.Host : link.IdnHost;
        string port = link.IsDefaultPort ? string.Empty : ":" + link.Port.ToString(CultureInfo.InvariantCulture);
        if (link.HostNameType == UriHostNameType.Dns && !string.Equals(link.Host, link.IdnHost, StringComparison.OrdinalIgnoreCase))
        {
            return link.Host + port + " (" + ascii + port + ")";
        }
        return ascii + port;
    }

    private static bool IsLocal(IPAddress address)
    {
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv4MappedToIPv6) return IsLocal(address.MapToIPv4());
            if (IPAddress.IsLoopback(address)) return true;
            bool unspecified = true;
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] != 0)
                {
                    unspecified = false;
                    break;
                }
            }
            return unspecified
                || (b[0] & 0xFE) == 0xFC               // fc00::/7 unique local
                || (b[0] == 0xFE && (b[1] & 0x80) == 0x80) // fe80::/10 link-local, fec0::/10 site-local
                || b[0] == 0xFF;                       // multicast
        }
        if (b.Length != 4) return true;
        switch (b[0])
        {
            case 0:   // "this network"; browsers treat 0.0.0.0 as this machine
            case 10:
            case 127:
                return true;
            case 100: return b[1] >= 64 && b[1] <= 127;     // carrier-grade NAT, VPN meshes
            case 169: return b[1] == 254;                   // link-local
            case 172: return b[1] >= 16 && b[1] <= 31;
            case 192: return b[1] == 168 || (b[1] == 0 && b[2] == 0);
            case 198: return b[1] == 18 || b[1] == 19;      // benchmarking range
        }
        return b[0] >= 224;                                 // multicast, reserved, broadcast
    }

    private static bool IsLocalName(string host)
    {
        if (host.IndexOf('.') < 0) return true; // one label: "router", "nas", "localhost"
        return MatchesDomain(host, LocalSuffixes);
    }

    private static bool MatchesDomain(string host, string[] domains)
    {
        for (int i = 0; i < domains.Length; i++)
        {
            string domain = domains[i];
            if (host.Length == domain.Length ? host == domain : host.EndsWith("." + domain, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool HasPunycodeLabel(string host)
    {
        return host.StartsWith("xn--", StringComparison.Ordinal) || host.IndexOf(".xn--", StringComparison.Ordinal) >= 0;
    }

    /// <summary>Four labels in a row that spell a dotted IPv4 address, as wildcard DNS services use.</summary>
    private static bool TryFindEmbeddedAddress(string host, out IPAddress address)
    {
        address = null;
        string[] labels = host.Split('.');
        for (int i = 0; i + 3 < labels.Length; i++)
        {
            if (!IsOctet(labels[i]) || !IsOctet(labels[i + 1]) || !IsOctet(labels[i + 2]) || !IsOctet(labels[i + 3])) continue;
            return IPAddress.TryParse(string.Join(".", labels, i, 4), out address);
        }
        return false;
    }

    private static bool IsOctet(string label)
    {
        if (label.Length == 0 || label.Length > 3) return false;
        int value = 0;
        for (int i = 0; i < label.Length; i++)
        {
            char c = label[i];
            if (c < '0' || c > '9') return false;
            value = value * 10 + (c - '0');
        }
        return value <= 255;
    }

    private static bool PointsAtProgram(Uri link)
    {
        string path = link.AbsolutePath;
        int slash = path.LastIndexOf('/');
        string file = slash >= 0 ? path.Substring(slash + 1) : path;
        if (file.Length == 0) return false;
        try
        {
            file = Uri.UnescapeDataString(file);
        }
        catch (Exception)
        {
            return false;
        }
        // Windows ignores trailing dots and spaces, so "setup.exe." still runs as setup.exe.
        file = file.TrimEnd('.', ' ');
        int dot = file.LastIndexOf('.');
        if (dot < 0 || dot == file.Length - 1) return false;
        string extension = file.Substring(dot + 1).ToLowerInvariant();
        return Array.IndexOf(ProgramExtensions, extension) >= 0;
    }

    // RFC 3986 unreserved and reserved characters, plus '%' (AbsoluteUri already escaped any bare one).
    private static bool IsUriCharacter(char c)
    {
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return true;
        switch (c)
        {
            case '-': case '.': case '_': case '~':
            case ':': case '/': case '?': case '#': case '[': case ']': case '@':
            case '!': case '$': case '&': case '\'': case '(': case ')': case '*': case '+': case ',': case ';': case '=':
            case '%':
                return true;
            default:
                return false;
        }
    }

    private static bool IsPlainAscii(string value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        for (int i = 0; i < value.Length; i++)
        {
            if (!IsUriCharacter(value[i])) return false;
        }
        return true;
    }
}
