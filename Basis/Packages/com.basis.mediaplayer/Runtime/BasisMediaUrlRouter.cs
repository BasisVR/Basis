using System;
using System.Collections.Generic;
using System.Net;

/// <summary>
/// A URL resolver registered with <see cref="BasisMediaUrlRouter"/>. Several resolvers
/// can be registered at once; the router tries them in descending <see cref="Priority"/>
/// order (registration order breaks ties) until one takes ownership of the load. A
/// resolver that declines lets the router fall through to the next one, and finally to a
/// direct load. Resolvers route only — they never gate host trust (that's
/// BasisMediaPlayerSecurity).
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
    /// — resolving a page URL to its stream(s) and loading them, possibly async — and
    /// returns true; or returns false to let the router try the next resolver, then a
    /// direct load.
    /// </summary>
    bool TryResolve(BasisMediaPlayer player, string url);
}

/// <summary>
/// Optional URL-resolution seam between the player and higher-level resolvers (e.g. the
/// yt-dlp integration package). The player core has no reference to any integration, so
/// this is how a URL field can steer page URLs through a resolver without the player
/// depending on it.
///
/// Integrations <see cref="Register"/> an <see cref="IBasisVideoResolver"/> at load (e.g.
/// via <c>RuntimeInitializeOnLoadMethod</c>). Callers with a raw URL hand it to
/// <see cref="TryResolveAndLoad"/>, which walks the registered resolvers in priority order
/// until one takes ownership (returns true); if none do, the caller loads the URL directly.
/// With nothing registered every URL loads directly — identical to having no integration at
/// all. This routes only; it never blocks a URL (host trust is enforced separately by
/// BasisMediaPlayerSecurity).
///
/// Not thread-safe. Register, Unregister and TryResolveAndLoad must all run on Unity's main
/// thread — the resolver list is unsynchronised, so registering while a resolve is iterating
/// would corrupt it. Integrations register from <c>RuntimeInitializeOnLoadMethod</c> and the
/// player resolves from its load path, both main-thread, so this holds in practice.
/// </summary>
public static class BasisMediaUrlRouter
{
    private static readonly List<IBasisVideoResolver> Resolvers = new List<IBasisVideoResolver>();

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
    private static readonly char[] PathEnd = { '?', '#' };

    /// <summary>
    /// Routes <paramref name="url"/> through the registered resolvers in priority order.
    /// Returns true as soon as one takes ownership (the caller should not also load); false
    /// when none handle it (the caller loads directly).
    /// </summary>
    public static bool TryResolveAndLoad(BasisMediaPlayer player, string url)
    {
        for (int i = 0; i < Resolvers.Count; i++)
        {
            IBasisVideoResolver resolver = Resolvers[i];
            // Resolvers are external integrations (third-party packages). Contain a throwing
            // one so a single bad resolver can't break lower-priority resolvers or the
            // direct-load fallback; a failed attempt is treated as "declined" and routing
            // continues. Not a hot path — this runs on a user-initiated load.
            try
            {
                if (resolver.CanResolve(url) && resolver.TryResolve(player, url)) return true;
            }
            catch (Exception e)
            {
                BasisDebug.LogError(
                    $"BasisMediaUrlRouter: resolver '{resolver.GetType().Name}' threw while handling '{Redact(url)}'; skipping it. {e}",
                    BasisDebug.LogTag.Video);
            }
        }
        return false;
    }

    /// <summary>
    /// True if the player can open <paramref name="url"/> directly, without a resolver:
    /// any non-HTTP scheme (transport like rtsp/rtmp/rist, or a local file), or an
    /// http(s) URL whose path ends in a media-container extension
    /// (.mp4/.m4v/.m4a/.m4s/.ts/.m2ts/.mts/.m3u8/.wav/.webm/.opus/.mp3). An http(s) URL with no media extension is a page URL
    /// (e.g. a YouTube/Twitch watch page) and needs a resolver. This is the single
    /// source of truth for the live-vs-resolve steering; resolvers and callers both
    /// consult it. It classifies only — it never blocks (host trust is separate).
    /// </summary>
    public static bool IsDirectlyPlayable(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        bool isHttp = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        if (!isHttp) return true; // transport scheme or local file — opened directly

        // Match against the URI path only — a host that happens to end in a media
        // extension (https://example.wav) is not a media URL. The manual
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

        // No .mpd — there is no DASH demuxer in the native engine, so a raw MPD must go
        // through a resolver (yt-dlp) rather than being treated as directly playable.
        return path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)   // MP4 container, video flavour
            || path.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)   // MP4 container, audio-only
            || path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase)   // Blu-ray-flavour MPEG-TS
            || path.EndsWith(".mts", StringComparison.OrdinalIgnoreCase)    // AVCHD-flavour MPEG-TS
            || path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            // .opus only: the native Ogg demuxer handles Opus, and .opus is
            // Opus by convention. .ogg is a generic container (Vorbis/FLAC/…)
            // the pipeline doesn't decode, so it isn't routed directly.
            || path.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A log-safe form of <paramref name="url"/> — the scheme, host and path with the query and
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

        // Strip userinfo (user:pass@host) too — those are credentials. Rebuild from the
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
    private static readonly char[] AuthorityEnd = { '/', '?', '#' };

    /// <summary>
    /// The scheme to default a scheme-less <paramref name="authority"/> ("host[:port][/path…]")
    /// to: "http" when the host is an IP literal or a local name — localhost, a single-label
    /// host, or a .localhost/.local/.lan/.internal/.home.arpa suffix — and "https" otherwise.
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
    /// Guarantees <paramref name="url"/> carries a scheme so it can route and load as an
    /// absolute URL. A bare web URL with no scheme (e.g. "www.youtube.com/watch?v=…") gets
    /// "https://" prepended and a protocol-relative URL ("//host/…") gets "https:", except
    /// when <see cref="SchemeFor"/> classifies the host as local (an IP literal or a local
    /// name), which take "http://" / "http:" instead; anything
    /// that already has a scheme (http(s)/rtsp/rtmp/rist/file/…) or is a local path (a single
    /// leading slash, a backslash UNC path like \\server\share, or a drive letter like C:\) is
    /// returned trimmed but otherwise unchanged. Because "//host/…" is read as a protocol-relative
    /// web URL, a network path must use the backslash UNC form (or a file:// URL), not forward
    /// slashes. Null/whitespace passes through untouched.
    /// </summary>
    public static string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url;
        string trimmed = url.Trim();
        if (trimmed.Contains("://")) return trimmed;                      // already has a scheme
        if (trimmed.StartsWith("//", StringComparison.Ordinal))          // protocol-relative ("//host/…")
            return SchemeFor(trimmed.Substring(2)) + ":" + trimmed;
        if (trimmed[0] == '/' || trimmed[0] == '\\') return trimmed;      // unix / UNC / rooted path
        if (trimmed.Length >= 2 && char.IsLetter(trimmed[0]) && trimmed[1] == ':') return trimmed; // windows drive path (C:\, C:/) — letter-prefixed so "[::1]/…" isn't caught
        return SchemeFor(trimmed) + "://" + trimmed;
    }
}
