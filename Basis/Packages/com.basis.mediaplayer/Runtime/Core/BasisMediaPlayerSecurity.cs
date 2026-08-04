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
        => Basis.Scripts.Common.BasisUrlSecurity.IsBlockedHost(host, out reason);

    // DNS layer: resolves a real host name off the main thread and blocks it if any
    // resolved address is non-global. Closes the name-that-points-at-a-private-IP
    // bypass that the literal-only IsBlockedHost can't see. null = allowed. Fails
    // closed: a resolver the check can't get an answer from could serve the
    // engine's own lookup a private address moments later, so an unvalidatable
    // host is a blocked host (a genuinely dead name couldn't be played anyway).
    public static Task<string> ValidateResolvedHostAsync(string url)
        => Basis.Scripts.Common.BasisUrlSecurity.ValidateResolvedHostAsync(url);

    // Blocks anything that is not global unicast, including a private/loopback target
    // smuggled through IPv4-mapped or 6to4 IPv6. allowLoopback exempts loopback only.
    public static bool IsBlockedAddress(IPAddress ip, bool allowLoopback, out string reason)
        => Basis.Scripts.Common.BasisUrlSecurity.IsBlockedAddress(ip, allowLoopback, out reason);

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
