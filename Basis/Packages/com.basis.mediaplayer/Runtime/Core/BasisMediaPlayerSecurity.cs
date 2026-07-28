using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using UnityEngine;

public static class BasisMediaPlayerSecurity
{
    public const int MaxQueueLengthCap = 256;
    public const int MaxPayloadBytesCap = 16 * 1024 * 1024;
    public const float ClipLengthSecondsCap = 30f;
    public const int MaxQueuedAudioFramesCap = 512;

    public static bool IsUrlAllowed(string url, out string reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "URL is empty.";
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            reason = "URL must be absolute.";
            return false;
        }
        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme == "file")
        {
            reason = "file:// URLs are blocked.";
            return false;
        }
        // Live-streaming schemes handled by the OS-codec engine (basis_media_native):
        //   rtsp/rtspt  RTSP over UDP/TCP (rtspt = interleaved over TCP, low latency)
        //   rtmp/rtmps  RTMP / RTMP-over-TLS
        //   http/https  fragmented MP4 (.mp4), MPEG-TS (.ts) and WAV (.wav) over HTTP(S)
        //   rist        RIST live ingest (MPEG-TS over UDP; requires a BASIS_WITH_RIST build)
        if (scheme != "http" && scheme != "https" &&
            scheme != "rtsp" && scheme != "rtspt" &&
            scheme != "rtmp" && scheme != "rtmps" &&
            scheme != "rist")
        {
            reason = $"Scheme '{scheme}' is not allowed.";
            return false;
        }

        string host = uri.Host;
        if (string.IsNullOrEmpty(host))
        {
            reason = "URL is missing a host.";
            return false;
        }

        if (IsBlockedHost(host, out string hostReason))
        {
            reason = hostReason;
            return false;
        }

        return true;
    }

    public static bool IsBlockedHost(string host, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(host)) { reason = "missing host"; return true; }

        bool allowLoopback = Application.isEditor;
        string lower = host.ToLowerInvariant();
        if (!allowLoopback && (lower == "localhost" || lower.EndsWith(".localhost")))
        {
            reason = "loopback host is blocked in builds.";
            return true;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress ip))
            return IsBlockedAddress(ip, allowLoopback, out reason);

        return false;
    }

    // DNS layer: resolves a real host name off the main thread and blocks it if any
    // resolved address is non-global. Closes the name-that-points-at-a-private-IP
    // bypass that the literal-only IsBlockedHost can't see. null = allowed. Fails
    // closed: a resolver the check can't get an answer from could serve the
    // engine's own lookup a private address moments later, so an unvalidatable
    // host is a blocked host (a genuinely dead name couldn't be played anyway).
    public static async Task<string> ValidateResolvedHostAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri)) return null;
        string host = uri.Host;
        if (string.IsNullOrEmpty(host)) return null;
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return null;

        bool allowLoopback = Application.isEditor;
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host); }
        catch (Exception ex) { return $"host '{host}' could not be validated (DNS lookup failed: {ex.Message})."; }
        if (addresses == null || addresses.Length == 0)
            return $"host '{host}' could not be validated (DNS returned no addresses).";

        foreach (IPAddress ip in addresses)
            if (IsBlockedAddress(ip, allowLoopback, out string reason))
                return $"host '{host}' resolves to a blocked address ({reason}).";
        return null;
    }

    // Blocks anything that is not global unicast, including a private/loopback target
    // smuggled through IPv4-mapped or 6to4 IPv6. allowLoopback exempts loopback only.
    public static bool IsBlockedAddress(IPAddress ip, bool allowLoopback, out string reason)
    {
        reason = null;
        if (ip == null) { reason = "null address"; return true; }

        byte[] b = ip.GetAddressBytes();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && b.Length == 16)
        {
            bool mapped = true;
            for (int i = 0; i < 10; i++) if (b[i] != 0) { mapped = false; break; }
            if (mapped && b[10] == 0xFF && b[11] == 0xFF)
                return IsBlockedAddress(new IPAddress(new[] { b[12], b[13], b[14], b[15] }), allowLoopback, out reason);
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            if (b[0] == 127) { if (allowLoopback) return false; reason = "loopback 127/8"; return true; }
            if (b[0] == 0) { reason = "unspecified 0/8"; return true; }
            if (b[0] == 10) { reason = "RFC1918 10/8"; return true; }
            if (b[0] == 100 && (b[1] & 0xC0) == 64) { reason = "CGNAT 100.64/10"; return true; }
            if (b[0] == 169 && b[1] == 254) { reason = "link-local 169.254/16"; return true; }
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { reason = "RFC1918 172.16/12"; return true; }
            if (b[0] == 192 && b[1] == 168) { reason = "RFC1918 192.168/16"; return true; }
            // IANA "Globally Reachable: False" special-use reserves — non-global-unicast, and may be
            // routed to internal infrastructure. Kept in lockstep with the native guard (basis_io.c
            // ipv4_octets_blocked) — change both together.
            // https://www.iana.org/assignments/iana-ipv4-special-registry/
            if (b[0] == 192 && b[1] == 0 && b[2] == 0) { reason = "IETF protocol 192.0.0/24"; return true; }
            if (b[0] == 192 && b[1] == 0 && b[2] == 2) { reason = "TEST-NET-1 192.0.2/24"; return true; }
            if (b[0] == 192 && b[1] == 88 && b[2] == 99) { reason = "6to4 relay 192.88.99/24"; return true; }
            if (b[0] == 198 && (b[1] & 0xFE) == 18) { reason = "benchmarking 198.18/15"; return true; }
            if (b[0] == 198 && b[1] == 51 && b[2] == 100) { reason = "TEST-NET-2 198.51.100/24"; return true; }
            if (b[0] == 203 && b[1] == 0 && b[2] == 113) { reason = "TEST-NET-3 203.0.113/24"; return true; }
            if (b[0] >= 224) { reason = "multicast/reserved >=224/4"; return true; }
            return false;
        }

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            bool allZero = true;
            for (int i = 0; i < 16; i++) if (b[i] != 0) { allZero = false; break; }
            if (allZero) { reason = "IPv6 unspecified ::"; return true; }

            bool isLoop = b[15] == 1;
            if (isLoop) for (int i = 0; i < 15; i++) if (b[i] != 0) { isLoop = false; break; }
            if (isLoop) { if (allowLoopback) return false; reason = "IPv6 loopback ::1"; return true; }

            if ((b[0] & 0xFE) == 0xFC) { reason = "IPv6 ULA fc00::/7"; return true; }
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) { reason = "IPv6 link-local fe80::/10"; return true; }
            if (b[0] == 0xFF) { reason = "IPv6 multicast ff00::/8"; return true; }

            if (b[0] == 0x20 && b[1] == 0x02 &&
                IsBlockedAddress(new IPAddress(new[] { b[2], b[3], b[4], b[5] }), allowLoopback, out string r6to4))
            { reason = "6to4→" + r6to4; return true; }

            return false;
        }

        reason = "non-IP address family";
        return true;
    }

    public static bool TrySandboxLogPath(string requested, out string sandboxed, out string reason)
    {
        reason = null;
        if (string.IsNullOrEmpty(requested))
        {
            sandboxed = string.Empty;
            return true;
        }
        string root = Path.GetFullPath(Application.persistentDataPath);
        string full;
        try
        {
            full = Path.GetFullPath(Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested));
        }
        catch (Exception ex)
        {
            sandboxed = null;
            reason = "Path normalization failed: " + ex.Message;
            return false;
        }
        string rootWithSep = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) && full != root)
        {
            sandboxed = null;
            reason = "Log path must live under Application.persistentDataPath.";
            return false;
        }
        sandboxed = full;
        return true;
    }
}
