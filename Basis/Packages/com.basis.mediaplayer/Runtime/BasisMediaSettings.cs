using Unity.Scripting.LifecycleManagement;
using System.Collections.Generic;
using Basis;
using Basis.Scripts.Settings;

/// <summary>
/// Client-persisted settings for the media engine, owned by this package
/// rather than the framework's BasisSettingsDefaults. Values live in the
/// user's settings file and are never world content.
/// </summary>
[NoAutoStaticsCleanup]
public static class BasisMediaSettings
{
    /// <summary>Stored value of <see cref="DecodePreference"/> for
    /// <see cref="BmDecodePreference.HardwareWithFallback"/>.</summary>
    public const string DecodeHardwareWithFallback = "hardware_fallback";

    /// <summary>Stored value of <see cref="DecodePreference"/> for
    /// <see cref="BmDecodePreference.HardwareOnly"/>.</summary>
    public const string DecodeHardwareOnly = "hardware_only";

    /// <summary>Stored value of <see cref="DecodePreference"/> for
    /// <see cref="BmDecodePreference.SoftwareOnly"/>.</summary>
    public const string DecodeSoftwareOnly = "software_only";

    static BasisMediaSettings()
    {
        BasisSettingsBindingPostLoad.Register(typeof(BasisMediaSettings));
        DecodePreference.OnChanged += ApplyDecodePreference;
        BufferDepthMs.OnChanged += ApplyBufferDepthAndRebank;
    }

    /// <summary>How many media players may decode at once on this machine;
    /// 0 lifts the cap. Beyond it the furthest players go dormant. The defaults
    /// are the per-platform session counts the engine is budgeted and measured
    /// for, so raising this is the viewer spending headroom they may not
    /// have.</summary>
    public static BasisSettingsBinding<int> MaxActivePlayers =
        new("mediaplayermaxactive", new BasisPlatformDefault<int>(3) { android = 2 });

    /// <summary>Whether closed captions are drawn. Per viewer: it changes
    /// nothing about playback or sync, only whether an overlay draws the cues
    /// the engine already produces.</summary>
    public static BasisSettingsBinding<bool> CaptionsEnabled =
        new("mediaplayercaptionsenabled", new BasisPlatformDefault<bool>(false));

    /// <summary>Caption text opacity, 0..1.</summary>
    public static BasisSettingsBinding<float> CaptionTextOpacity =
        new("mediaplayercaptiontextopacity", new BasisPlatformDefault<float>(1f));

    /// <summary>Caption background opacity, 0..1.</summary>
    public static BasisSettingsBinding<float> CaptionBackgroundOpacity =
        new("mediaplayercaptionbackgroundopacity", new BasisPlatformDefault<float>(0.5f));

    /// <summary>Jitter buffer depth for players the viewer has not tuned
    /// individually, in milliseconds; 0 is Auto, which sizes itself from the
    /// delivery delays each session observes and is right for almost everyone.
    /// A per-viewer setting because what it trades off is this connection
    /// against how soon a frame appears, and a world author cannot see this
    /// connection. A single player is tuned away from this figure from the
    /// Media Players panel.</summary>
    public static BasisSettingsBinding<int> BufferDepthMs =
        new("mediaplayerbufferdepthms", new BasisPlatformDefault<int>(0));

    /// <summary>Which decode route the engine may take. Stored as one of the
    /// Decode* constants; applied to <see cref="BasisMediaPlayer.DecodePreference"/>,
    /// which every session reads when it opens.</summary>
    public static BasisSettingsBinding<string> DecodePreference =
        new("mediaplayerdecodepreference", new BasisPlatformDefault<string>(DecodeHardwareWithFallback));

    /// <summary>Touches the binding so the static constructor runs, then pushes
    /// the stored decode preference into the engine.</summary>
    public static void EnsureLoaded()
    {
        ApplyDecodePreference(DecodePreference.RawValue);
        ApplyBufferDepth(BufferDepthMs.RawValue);
    }

    /// <summary>Maps a stored value onto the engine enum. Unrecognised values
    /// (a hand-edited settings file, or a key written by a later build) resolve
    /// to the default route rather than refusing to play.</summary>
    public static BmDecodePreference ToEnginePreference(string stored) => stored switch
    {
        DecodeHardwareOnly => BmDecodePreference.HardwareOnly,
        DecodeSoftwareOnly => BmDecodePreference.SoftwareOnly,
        _ => BmDecodePreference.HardwareWithFallback,
    };

    static void ApplyDecodePreference(string stored)
    {
        BasisMediaPlayer.DecodePreference = ToEnginePreference(stored);
    }

    static void ApplyBufferDepth(int milliseconds)
    {
        BasisMediaPlayer.DefaultBufferDepthMs = milliseconds < 0 ? 0 : milliseconds;
    }

    /// <summary>Take the new default and re-bank the sessions running against
    /// it. Players the viewer has tuned individually keep their own figure,
    /// and players holding no session pick it up when they next open.
    ///
    /// Not the load path: <see cref="EnsureLoaded"/> applies the stored
    /// value without this, since nothing is playing yet.</summary>
    static void ApplyBufferDepthAndRebank(int milliseconds)
    {
        ApplyBufferDepth(milliseconds);
        IReadOnlyList<BasisMediaPlayer> players = BasisMediaPlayerRegistry.Players;
        for (int i = 0; i < players.Count; i++)
        {
            BasisMediaPlayer player = players[i];
            if (player == null || player.BufferDepthOverrideMs.HasValue) continue;
            // A follower's session is placed by its owner, and a re-open would
            // only cost it a rejoin; the new depth applies on its next load.
            if (player.TryGetComponent(out BasisMediaPlayerNetworking networking) && networking.IsFollowerInSession)
                continue;
            player.ReopenAtPosition();
        }
    }
}
