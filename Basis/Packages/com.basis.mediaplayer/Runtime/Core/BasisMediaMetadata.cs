using System;
using System.Collections.Generic;

// Descriptive metadata for the loaded media — the display-facing answer to
// "what's playing". Two layers:
//
//   1. URL-derived defaults, built by the player at every load (FromUrl): the
//      source URL, a filename when the URL path names one, and a Title fallback
//      chain (filename without extension -> last path segment -> host). Works
//      on every platform with no resolver package installed.
//   2. Enrichment pushed by whoever knows more: a resolver sets the real
//      Title/Uploader/ThumbnailUrl/Duration on BasisMediaSource.Metadata before
//      LoadSource, or anyone calls BasisMediaPlayer.ApplyMetadata after.
//
// Consumers read BasisMediaPlayer.Metadata and subscribe to OnMetadataChanged.
public sealed class BasisMediaMetadata
{
    // The URL as handed to the player — the input/page URL for resolved media,
    // not the per-client stream endpoint. Matches what networking syncs, so
    // every client derives identical defaults.
    public string SourceUrl;

    // Decoded final path segment of the source URL when it names a file
    // ("video%20name.mp4" -> "video name.mp4"); null for extensionless sources
    // (live transport paths, page URLs).
    public string FileName;

    // Display name. Never null once a load has started: enrichment title, else
    // FileName without its extension, else the last URL path segment, else the
    // host.
    public string Title;

    // Enrichment-only fields — null unless an integration supplied them.
    public string Uploader;
    public string ThumbnailUrl;
    public TimeSpan? Duration;

    // Out-of-band subtitle tracks the player can offer for this media; null or
    // empty when the source has none (the common case).
    public List<BasisSubtitleTrack> SubtitleTracks;

    // Which layer supplied the richest data: "url" for the built-in derivation;
    // integrations stamp their own tag (e.g. "ytdlp").
    public string Provider;

    // Longest extension still treated as a filename suffix (".m3u8" = 4); keeps
    // a dotted final segment like "v2.1-launch-party" reading as a title.
    private const int MaxExtensionLength = 5;

    // Layer 1: derive defaults from the URL alone. Never returns null.
    public static BasisMediaMetadata FromUrl(string url)
    {
        var meta = new BasisMediaMetadata { SourceUrl = url, Provider = "url" };
        string last = null, host = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri parsed))
        {
            host = parsed.Host;
            string path = parsed.AbsolutePath;
            int slash = path.LastIndexOf('/');
            last = slash >= 0 ? path.Substring(slash + 1) : path;
            try { last = Uri.UnescapeDataString(last); }
            catch (UriFormatException) { /* keep the escaped form */ }
        }
        if (!string.IsNullOrEmpty(last))
        {
            int dot = last.LastIndexOf('.');
            if (dot > 0 && dot < last.Length - 1 && last.Length - dot - 1 <= MaxExtensionLength)
            {
                meta.FileName = last;
                meta.Title = last.Substring(0, dot);
            }
            else
            {
                meta.Title = last;
            }
        }
        if (string.IsNullOrEmpty(meta.Title))
        {
            // url can be null/unparseable here (a bare BasisMediaSource handed
            // straight to LoadSource); keep the never-null Title guarantee.
            meta.Title = !string.IsNullOrEmpty(host) ? host : (url ?? string.Empty);
        }
        return meta;
    }

    // Field-level snapshot. The player hands these out so external code can't
    // silently mutate its live metadata (which would bypass OnMetadataChanged
    // and the reload-origin comparisons); ApplyMetadata is the mutation path.
    public BasisMediaMetadata Clone()
    {
        var copy = (BasisMediaMetadata)MemberwiseClone();
        copy.SubtitleTracks = CopyTracks(SubtitleTracks);
        return copy;
    }

    // Copies other's non-null/non-empty fields over this instance, leaving the
    // rest in place — layers enrichment over the URL-derived defaults.
    public void MergeFrom(BasisMediaMetadata other)
    {
        if (other == null) return;
        if (!string.IsNullOrEmpty(other.SourceUrl)) SourceUrl = other.SourceUrl;
        if (!string.IsNullOrEmpty(other.FileName)) FileName = other.FileName;
        if (!string.IsNullOrEmpty(other.Title)) Title = other.Title;
        if (!string.IsNullOrEmpty(other.Uploader)) Uploader = other.Uploader;
        if (!string.IsNullOrEmpty(other.ThumbnailUrl)) ThumbnailUrl = other.ThumbnailUrl;
        if (other.Duration.HasValue) Duration = other.Duration;
        if (other.SubtitleTracks != null && other.SubtitleTracks.Count > 0) SubtitleTracks = CopyTracks(other.SubtitleTracks);
        if (!string.IsNullOrEmpty(other.Provider)) Provider = other.Provider;
    }

    private static List<BasisSubtitleTrack> CopyTracks(List<BasisSubtitleTrack> tracks)
    {
        if (tracks == null) return null;
        var copy = new List<BasisSubtitleTrack>(tracks.Count);
        foreach (BasisSubtitleTrack track in tracks)
        {
            copy.Add(new BasisSubtitleTrack
            {
                Url = track.Url,
                Format = track.Format,
                Language = track.Language,
                Label = track.Label,
                IsAutoGenerated = track.IsAutoGenerated,
            });
        }
        return copy;
    }
}
