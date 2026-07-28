using System;
using System.Collections.Generic;

// Live-vs-on-demand delivery policy. Auto lets the engine decide at open from the
// scheme and, for HTTP, the response (a finite, seekable body or an HLS playlist
// with EXT-X-ENDLIST => on-demand; an open-ended stream => live). Live and OnDemand
// force the choice when the caller already knows (e.g. a resolver that produced a
// split adaptive pair knows it is on-demand). Values match the native ABI hint.
public enum BasisMediaDelivery
{
    Auto = 0,      // detect at open (default)
    Live = 1,      // force live-edge clock (real-time broadcast)
    OnDemand = 2,  // force real-time-paced VOD delivery + presentation
}

// Declarative description of "what to play" + "how to play it", consumed by
// BasisMediaPlayer.LoadSource. The player hands every network URL to the OS-codec
// engine (basis_media_native), which decodes it zero-copy into a GPU texture:
//
//   rtsp://, rtspt://     RTSP (rtspt = RTP interleaved over TCP — PC/VR low latency)
//   rtmp://, rtmps://     RTMP / RTMP-over-TLS
//   https://, http://     fragmented MP4 (.mp4), MPEG-TS (.ts) or WAV (.wav) over HTTP(S)
//
// The CPU IBasisFrameSource path (e.g. BasisSyntheticTestSource for tests) is
// entered only by assigning BasisMediaPlayer.Source directly. Disallowed schemes
// fall through to OnError with a clear message.
public sealed class BasisMediaSource
{
    public BasisMediaSource() { }

    public BasisMediaSource(string uri)
    {
        Uri = uri;
    }

    // URI of the media. Absolute URL or absolute local path (file://, /foo/bar,
    // C:\foo\bar). Relative paths are resolved against Application.streamingAssetsPath.
    public string Uri;

    // Optional headers passed to the underlying transport when the scheme
    // supports them (HTTP(S), WebSocket). Ignored by file/local sources.
    public Dictionary<string, string> Headers;

    // Optional separate audio-only stream, played in sync with the video carried
    // by Uri. When set, Uri is treated as video-only and AudioUri as audio-only;
    // null (default) means Uri is a single muxed stream and behaviour is unchanged.
    // Lets a source deliver video and audio as distinct streams, e.g. adaptive
    // YouTube above ~360p (H.264 video-only + AAC audio-only).
    public string AudioUri;

    // Live-vs-on-demand policy. On-demand content (VOD that arrives faster than real
    // time, e.g. yt-dlp-resolved YouTube) is paced to a fixed 1x clock so it doesn't
    // fast-forward; live broadcasts (RTSP/RTMP/RIST/live HLS) present at the live edge.
    // Auto (default) detects this at open; set Live/OnDemand to force it.
    public BasisMediaDelivery Delivery = BasisMediaDelivery.Auto;

    // Per-source loop flag. Overridden by BasisMediaPlayer.Loop when assigned;
    // BasisMediaPlayer.Loop is the runtime-mutable knob.
    public bool Loop;

    // Initial playback rate (1.0 = real-time). Mirrors BasisMediaPlayer.PlaybackRate.
    public float PlaybackRate = 1f;

    // Optional volume override (0..1); null keeps the player's current volume.
    public float? Volume;

    // Optional mute override; null keeps the player's current mute state.
    public bool? Mute;

    // If set, the player will Seek to this position once OnReady fires.
    // Use TimeSpan.Zero to start at the beginning explicitly.
    public TimeSpan StartPosition = TimeSpan.Zero;

    // Connect/open timeout. Zero or negative means "use source default".
    public TimeSpan OpenTimeout = TimeSpan.Zero;

    // Free-form bag for source-specific options (e.g. preferred adaptive track,
    // hardware decoding hints). The player passes this through to the resolved
    // source; unknown keys are ignored.
    public Dictionary<string, object> Options;

    // Optional display metadata carried with the source. A resolver that knows
    // the real title/uploader sets this before handing the source to LoadSource,
    // with Metadata.SourceUrl set to the input/page URL it resolved. Null means
    // the player derives URL-based defaults at load.
    public BasisMediaMetadata Metadata;

    public static BasisMediaSource FromUrl(string url) => new BasisMediaSource(url);
    public static BasisMediaSource FromLocalPath(string path) => new BasisMediaSource(NormalizeLocalPath(path));

    private static string NormalizeLocalPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return path;
        return "file://" + path.Replace('\\', '/');
    }
}
