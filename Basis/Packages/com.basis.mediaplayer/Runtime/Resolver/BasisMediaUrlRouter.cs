using Unity.Scripting.LifecycleManagement;
using System;
using System.Collections.Generic;
using System.Net;
using UnityEngine;

/// <summary>
/// A URL resolver registered with <see cref="BasisMediaUrlRouter"/>. Several resolvers
/// can be registered at once; the router tries them in descending <see cref="Priority"/>
/// order (registration order breaks ties) until one takes ownership of the load. A
/// resolver that declines lets the router fall through to the next one, and finally to a
/// direct load. Resolvers route only. They never gate host trust, which is the
/// job of the client's URL security and the engine's own vetting.
/// </summary>
public interface IBasisVideoResolver
{
    /// <summary>Higher runs first; equal priorities run in the order they registered.</summary>
    int Priority { get; }

    /// <summary>
    /// Cheap, side-effect-free test of whether this resolver handles <paramref name="url"/>.
    /// Returning false skips straight to the next resolver. Must not block or load.
    /// </summary>
    bool CanResolve(string url);

    /// <summary>
    /// Takes ownership of loading <paramref name="url"/> into <paramref name="player"/>
    /// (resolving a page URL to its stream(s) and opening them, possibly async) and
    /// returns true; or returns false to let the router try the next resolver, then a
    /// direct load.
    /// </summary>
    bool TryResolve(BasisMediaPlayer player, string url);
}

/// <summary>
/// Optional URL-resolution seam between the player and higher-level resolvers (the
/// yt-dlp integration being the one in the box). The player core has no reference to any
/// integration, so this is how a URL field can steer page URLs through a resolver
/// without the player depending on it.
///
/// Integrations <see cref="Register"/> an <see cref="IBasisVideoResolver"/> at load (a
/// <c>RuntimeInitializeOnLoadMethod</c>). Callers with a raw URL hand it to
/// <see cref="TryResolveAndLoad"/>, which walks the registered resolvers in priority order
/// until one takes ownership; if none do, the caller opens the URL directly. With nothing
/// registered every URL opens directly.
///
/// Not thread-safe. Register, Unregister and TryResolveAndLoad all run on Unity's main
/// thread: the resolver list is unsynchronised, and registering while a resolve is
/// iterating would corrupt it.
/// </summary>
[AutoStaticsCleanup]
public static partial class BasisMediaUrlRouter
{
    // Integrations register once per process (the yt-dlp installer skips a
    // repeat), so a list cleared on a Play entry would stay empty until the
    // next code reload.
    [NoAutoStaticsCleanup] private static readonly List<IBasisVideoResolver> Resolvers = new List<IBasisVideoResolver>();

    /// <summary>
    /// Registers <paramref name="resolver"/> so <see cref="TryResolveAndLoad"/> consults it,
    /// keeping the list ordered by descending <see cref="IBasisVideoResolver.Priority"/>
    /// (registration order breaks ties). Registering the same instance twice is a no-op.
    /// </summary>
    public static void Register(IBasisVideoResolver resolver)
    {
        if (resolver == null) throw new ArgumentNullException(nameof(resolver));
        if (Resolvers.Contains(resolver)) return;
        int i = 0;
        while (i < Resolvers.Count && Resolvers[i].Priority >= resolver.Priority) i++;
        Resolvers.Insert(i, resolver);
    }

    /// <summary>Removes a resolver registered via <see cref="Register"/>. Returns false if it wasn't registered.</summary>
    public static bool Unregister(IBasisVideoResolver resolver) => Resolvers.Remove(resolver);

    // Delimiters that end the path portion of a URL (query / fragment), hoisted so
    // IsDirectlyPlayable doesn't allocate a char[] per call.
    [NoAutoStaticsCleanup] private static readonly char[] PathEnd = { '?', '#' };

    /// <summary>
    /// Routes <paramref name="url"/> through the registered resolvers in priority order.
    /// Returns true as soon as one takes ownership (the caller should not also open it);
    /// false when none handle it (the caller opens directly).
    /// </summary>
    public static bool TryResolveAndLoad(BasisMediaPlayer player, string url)
    {
        for (int i = 0; i < Resolvers.Count; i++)
        {
            IBasisVideoResolver resolver = Resolvers[i];
            // Resolvers are external integrations. Contain a throwing one so a single bad
            // resolver can't break lower-priority resolvers or the direct-open fallback; a
            // failed attempt is treated as "declined" and routing continues.
            try
            {
                if (resolver.CanResolve(url) && resolver.TryResolve(player, url)) return true;
            }
            catch (Exception e)
            {
                BasisDebug.LogError(
                    $"[BasisMedia] resolver '{resolver.GetType().Name}' threw while handling '{Redact(url)}'; skipping it. {e}", BasisDebug.LogTag.Video);
            }
        }
        return false;
    }

    /// <summary>
    /// True if the player can open <paramref name="url"/> directly, without a resolver:
    /// any non-HTTP scheme (a transport like rtsp/rist/whep, or a local file), or an
    /// http(s) URL whose path ends in a container extension this engine demuxes. An
    /// http(s) URL with no media extension is a page URL (a YouTube/Twitch watch page)
    /// and needs a resolver. Resolvers and callers both consult this for the
    /// direct-vs-resolve decision. It classifies only and never blocks.
    /// </summary>
    public static bool IsDirectlyPlayable(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isHttp) return true; // transport scheme or local file, opened directly

        // Match against the URI path only: a host that happens to end in a media
        // extension (https://example.mkv) is not a media URL. The manual
        // query/fragment strip is the fallback for anything System.Uri can't parse.
        string path;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            path = uri.AbsolutePath;
        }
        else
        {
            path = url;
            int cut = path.IndexOfAny(PathEnd);
            if (cut >= 0) path = path.Substring(0, cut);
        }

        for (int i = 0; i < DirectExtensions.Length; i++)
        {
            if (path.EndsWith(DirectExtensions[i], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Containers the engine demuxes from a plain byte stream. No .mpd: with no DASH
    // demuxer, a raw manifest goes through a resolver. .opus but not .ogg:
    // .opus is Opus by convention, while .ogg is a generic container that may carry
    // codecs the engine does not decode.
    [NoAutoStaticsCleanup] private static readonly string[] DirectExtensions =
    {
        ".mp4", ".m4v", ".m4a", ".m4s", ".mov",
        ".ts", ".m2ts", ".mts",
        ".m3u8",
        ".mkv", ".webm",
        ".opus", ".mp3", ".flac", ".aac", ".wav",
    };

    /// <summary>
    /// A log-safe form of <paramref name="url"/>: the scheme, host and path with the query and
    /// fragment dropped, since those can carry signed tokens or private identifiers that
    /// shouldn't reach logs. Returns the input unchanged when it has no query/fragment, and a
    /// placeholder for null/blank.
    /// </summary>
    public static string Redact(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "(empty)";
        string trimmed = url.Trim();
        int cut = trimmed.IndexOfAny(PathEnd); // '?' or '#'
        string withoutQuery = cut >= 0 ? trimmed.Substring(0, cut) : trimmed;

        // Strip userinfo (user:pass@host) too, since it holds credentials. Rebuild from the
        // parsed parts only when it's present, so ordinary URLs keep their exact form.
        if (Uri.TryCreate(withoutQuery, UriKind.Absolute, out Uri uri) && !string.IsNullOrEmpty(uri.UserInfo))
        {
            string host = uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
            return $"{uri.Scheme}://{host}{uri.AbsolutePath}";
        }
        return withoutQuery;
    }

    // Delimiters that end the authority portion of a scheme-less URL, hoisted so
    // SchemeFor doesn't allocate a char[] per call.
    [NoAutoStaticsCleanup] private static readonly char[] AuthorityEnd = { '/', '?', '#' };

    /// <summary>
    /// The scheme to default a scheme-less <paramref name="authority"/> ("host[:port][/path…]")
    /// to: "http" when the host is an IP literal or a local name (localhost, a single-label
    /// host, or a .localhost/.local/.lan/.internal/.home.arpa suffix), and "https" otherwise.
    /// Those targets are LAN boxes and dev servers that rarely serve TLS, whereas a public
    /// domain name should never be defaulted down to cleartext.
    /// </summary>
    public static string SchemeFor(string authority)
    {
        if (string.IsNullOrEmpty(authority)) return "https";
        string host = authority;

        int cut = host.IndexOfAny(AuthorityEnd);
        if (cut >= 0) host = host.Substring(0, cut);

        int at = host.LastIndexOf('@');                                   // strip userinfo (user:pass@host)
        if (at >= 0) host = host.Substring(at + 1);

        if (host.StartsWith("[", StringComparison.Ordinal))               // bracketed IPv6 literal
        {
            int close = host.IndexOf(']');
            host = close > 1 ? host.Substring(1, close - 1) : host.Trim('[', ']');
        }
        else
        {
            int colon = host.IndexOf(':');
            if (colon >= 0) host = host.Substring(0, colon);              // drop :port
        }

        if (host.Length == 0) return "https";

        // Guarded on a '.' or ':' being present because IPAddress.TryParse accepts partial
        // forms ("1" → 0.0.0.1) on some runtimes, which would misread a single-label host.
        if ((host.IndexOf('.') >= 0 || host.IndexOf(':') >= 0) && IPAddress.TryParse(host, out _)) return "http";

        string lower = host.ToLowerInvariant();
        if (lower == "localhost") return "http";
        if (lower.IndexOf('.') < 0) return "http";                        // single-label host can't be a public domain
        if (lower.EndsWith(".localhost", StringComparison.Ordinal) ||
            lower.EndsWith(".local", StringComparison.Ordinal) ||
            lower.EndsWith(".lan", StringComparison.Ordinal) ||
            lower.EndsWith(".internal", StringComparison.Ordinal) ||
            lower.EndsWith(".home.arpa", StringComparison.Ordinal)) return "http";

        return "https";
    }

    /// <summary>
    /// Guarantees <paramref name="url"/> carries a scheme so it can route and open as an
    /// absolute URL. A bare web URL with no scheme ("www.youtube.com/watch?v=…") gets
    /// "https://" prepended and a protocol-relative URL ("//host/…") gets "https:", except
    /// when <see cref="SchemeFor"/> classifies the host as local, which take "http://" /
    /// "http:" instead; anything that already has a scheme or is a local path (a single
    /// leading slash, a backslash UNC path, or a drive letter) is returned trimmed but
    /// otherwise unchanged. Because "//host/…" is read as a protocol-relative web URL, a
    /// network path must use the backslash UNC form. Null/whitespace passes through
    /// untouched.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string trimmed = url.Trim();
        if (trimmed.Contains("://")) return trimmed;                      // already has a scheme
        if (trimmed.StartsWith("//", StringComparison.Ordinal))          // protocol-relative ("//host/…")
            return SchemeFor(trimmed.Substring(2)) + ":" + trimmed;
        if (trimmed[0] == '/' || trimmed[0] == '\\') return trimmed;      // unix / UNC / rooted path
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':') return trimmed; // windows drive path; letter-prefixed so "[::1]/…" isn't caught
        return SchemeFor(trimmed) + "://" + trimmed;
    }
}
