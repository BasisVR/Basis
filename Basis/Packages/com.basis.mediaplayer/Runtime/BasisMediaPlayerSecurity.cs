using System.Net;
using System.Threading.Tasks;
using UrlSecurity = Basis.Scripts.Common.BasisUrlSecurity;

/// <summary>URL and path checks, forwarding to the client's own: the
/// address rules live in <c>BasisUrlSecurity</c> and the write sandbox in
/// <see cref="BasisMediaCapturePath"/>. The caps are kept for code that
/// reads them; the engine sizes its own buffers.</summary>
public static class BasisMediaPlayerSecurity
{
    public const int MaxQueueLengthCap = 256;
    public const int MaxPayloadBytesCap = 16 * 1024 * 1024;
    public const float ClipLengthSecondsCap = 30f;
    public const int MaxQueuedAudioFramesCap = 512;

    public static bool IsUrlAllowed(string url, out string reason)
        => UrlSecurity.IsHttpUrlAllowed(url, out reason);

    public static bool IsBlockedHost(string host, out string reason)
        => UrlSecurity.IsBlockedHost(host, out reason);

    public static Task<string> ValidateResolvedHostAsync(string url)
        => UrlSecurity.ValidateResolvedHostAsync(url);

    public static bool IsBlockedAddress(IPAddress ip, bool allowLoopback, out string reason)
        => UrlSecurity.IsBlockedAddress(ip, allowLoopback, out reason);

    /// <summary>Resolve a name or path under <c>Application.persistentDataPath</c>,
    /// refusing anything that escapes it.</summary>
    public static bool TrySandboxLogPath(string requested, out string sandboxed, out string reason)
    {
        sandboxed = BasisMediaCapturePath.Resolve(requested, "BasisMediaPlayer.log", out reason);
        return sandboxed != null;
    }
}
