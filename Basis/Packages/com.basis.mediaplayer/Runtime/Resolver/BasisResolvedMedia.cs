using System;
using System.Collections.Generic;

/// <summary>
/// What a resolver turns a page URL into: the stream (or pair of streams) to
/// open, plus what it learned along the way. Everything past the URLs is
/// display material the resolver already has from its extraction.
/// </summary>
public sealed class BasisResolvedMedia
{
    /// <summary>The stream to open. Video-only when
    /// <see cref="AudioUrl"/> is set, otherwise muxed.</summary>
    public string Url;

    /// <summary>The audio-only stream that belongs with
    /// <see cref="Url"/>, for adaptive ladders that serve the two apart.
    /// Null for a muxed source.</summary>
    public string AudioUrl;

    /// <summary>What the resolver knows the source to be. A split pair is
    /// on-demand by construction; a live HLS variant is live. Auto leaves
    /// the engine to work it out from the source itself.</summary>
    public BmLiveness Liveness = BmLiveness.Auto;

    /// <summary>The URL the viewer actually asked for, rather than the
    /// per-client stream endpoint it resolved to. This is what shared
    /// playback synchronises, so every client derives the same
    /// identity from it.</summary>
    public string SourceUrl;

    public string Title;
    public string Uploader;
    public string ThumbnailUrl;
    public TimeSpan? Duration;

    /// <summary>Out-of-band subtitle tracks offered for this media; null
    /// or empty when there are none, which is the common case.</summary>
    public List<BasisSubtitleTrack> SubtitleTracks;

    /// <summary>Which integration supplied this, for display and
    /// diagnostics ("ytdlp").</summary>
    public string Provider;
}
