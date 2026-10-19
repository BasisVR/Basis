using UnityEngine;

/// <summary>
/// Points a <see cref="BasisMediaPlayer"/> at a URL, optionally
/// choosing between a desktop and an Android URL by build target.
///
/// A player with a URL already set needs none of this. It is useful
/// when the right URL differs by platform: RTSP is lowest latency on
/// desktop, and Quest wants MPEG-TS over HTTPS from the same source.
///
/// Per-platform guidance from a VRCDN panel (https://panel.vrcdn.live/preview/&lt;name&gt;):
///   PC / VR (low latency) : rtsp://stream.vrcdn.live/live/&lt;name&gt;
///   Quest (Android)       : https://stream.vrcdn.live/live/&lt;name&gt;.live.ts
/// </summary>
[AddComponentMenu("Basis/Basis Media Player Streaming")]
[RequireComponent(typeof(BasisMediaPlayer))]
public sealed class BasisMediaPlayerStreaming : MonoBehaviour
{
    [Header("Stream")]
    [Tooltip("URL to play when AutoSelectPerPlatform is off.")]
    public string StreamUrl = "rtsp://stream.vrcdn.live/live/vrcdn";

    [Tooltip("If true, pick PcUrl or QuestUrl automatically by build target instead of using StreamUrl. RTSP is lowest latency on PC/VR; Quest pulls MPEG-TS over HTTPS.")]
    public bool AutoSelectPerPlatform = false;

    [Tooltip("URL used on desktop/standalone (and in the editor) when AutoSelectPerPlatform is on.")]
    public string PcUrl = "rtsp://stream.vrcdn.live/live/vrcdn";

    [Tooltip("URL used on Android/Quest when AutoSelectPerPlatform is on.")]
    public string QuestUrl = "https://stream.vrcdn.live/live/vrcdn.live.ts";

    [Header("Lifecycle")]
    [Tooltip("If true, the resolved URL is written to the player before it starts. Disable to call Configure() yourself.")]
    public bool ConfigureOnStart = true;

    // Awake, not Start: every Awake runs before any Start, so the player
    // finds its URL in place and opens it through its own playOnStart.
    // Doing this in Start would race the player's and could open twice.
    private void Awake()
    {
        if (!ConfigureOnStart) return;
        if (!TryGetComponent(out BasisMediaPlayer player)) return;
        string url = ResolveUrl();
        if (!string.IsNullOrEmpty(url)) player.url = url;
    }

    /// <summary>Resolves the URL for this platform and opens it now,
    /// through the router so an authored page URL resolves rather than
    /// failing to open.</summary>
    public void Configure()
    {
        if (!TryGetComponent(out BasisMediaPlayer player))
        {
            BasisDebug.LogError("[BasisMedia] BasisMediaPlayerStreaming needs a BasisMediaPlayer on the same GameObject.", BasisDebug.LogTag.Video);
            return;
        }

        string url = ResolveUrl();
        if (string.IsNullOrEmpty(url))
        {
            BasisDebug.LogWarning("[BasisMedia] BasisMediaPlayerStreaming has no URL to load.", BasisDebug.LogTag.Video);
            return;
        }

        player.LoadUrl(url);
    }

    public string ResolveUrl()
    {
        if (!AutoSelectPerPlatform) return StreamUrl?.Trim();
#if UNITY_ANDROID && !UNITY_EDITOR
        return QuestUrl?.Trim();
#else
        return PcUrl?.Trim();
#endif
    }
}
