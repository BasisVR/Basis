using Unity.Scripting.LifecycleManagement;
using System;
using System.Buffers;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Basis.BasisUI;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Playback component over the basis_media engine (ABI v4; Direct3D 11 or 12 on
/// Windows, Vulkan on Android).
///
/// Poll-driven: one snapshot per frame, one render event per frame once
/// the video texture exists, and the decoded audio ring offered as an
/// <see cref="IBasisPcmSource"/>.
///
/// It draws nothing on its own. The video texture reaches the world through
/// an output sink (<see cref="BasisVideoMaterialOutput"/> for a renderer,
/// <see cref="BasisVideoDisplay"/> for a uGUI RawImage), which owns aspect,
/// projection, stereo eye and the frame-origin correction.
///
/// It makes no sound on its own. Audio belongs to
/// <see cref="BasisMediaPlayerAudio"/> on the same GameObject, which
/// broadcasts this ring to one AudioSource per speaker, so each channel can
/// be positioned, spatialised and filtered independently. Without one, the
/// session decodes audio that nothing consumes.
/// </summary>
// Ahead of every default-order MonoBehaviour, so anything that reads the poll's
// snapshot without being one of the registered consumers below (a component in
// another package, a test harness's own Update) still sees this frame's values
// rather than the previous frame's.
[AutoStaticsCleanup]
[DefaultExecutionOrder(-100)]
[AddComponentMenu("Basis/Basis Media Player")]
public partial class BasisMediaPlayer : MonoBehaviour, IBasisPcmSource
{
    /// <summary>The authored URL: a stream, a file path or a page URL.</summary>
    [Tooltip("http(s) URL or absolute file path.")]
    public string url;

    // The audio-only half of a split pair, set by Open(url, audioUrl) and
    // by the resolver, never authored. Adaptive ladders serve every rung
    // above their muxed fallback as a video-only stream plus this one, so
    // it only comes from a resolver. Not serialised and not public: an
    // authored value would take effect with nothing on screen to show it.
    // What is actually open is on ActiveAudioStreamUrl.
    string audioUrl;

    /// <summary>Shows <see cref="androidUrl"/> in the inspector. At runtime a
    /// set Android URL is what decides.</summary>
    [Tooltip("Publish this source differently per platform, rather than using " +
             "one URL everywhere. RTSP is lowest latency on desktop where a " +
             "headset wants MPEG-TS over HTTPS from the same feed, which is the " +
             "case this exists for. Authoring only: what actually decides at " +
             "runtime is whether the Android URL is set.")]
    public bool perPlatformUrls;

    /// <summary>Used instead of <see cref="url"/> on Android builds when set.
    /// The editor always uses <see cref="url"/>.</summary>
    [Tooltip("Used instead of the URL above on Android builds. Empty means the " +
             "same URL everywhere. This is for a source published differently " +
             "per platform — RTSP is lowest latency on desktop, where Quest " +
             "wants MPEG-TS over HTTPS from the same feed. The editor always " +
             "takes the URL above, whatever the build target is set to.")]
    public string androidUrl;

    /// <summary>Open the authored URL when the scene starts. Cleared on
    /// avatars and props as they load.</summary>
    public bool playOnStart = true;

    [Tooltip("What the Media Players menu calls this player. Empty uses the GameObject's name.")]
    public string DisplayName;

    [Tooltip("Play as soon as a source opens. Off, the session opens and holds on its first frame until Play.")]
    public bool AutoPlayOnSourceAssigned = true;

    [Tooltip("Stop the session when this component is disabled. Off, it keeps decoding, unpolled, until it is enabled again or stopped.")]
    public bool StopOnDisable = true;

    [Tooltip("Start again from the beginning when an on-demand source ends.")]
    public bool Loop;

    [Tooltip("Seconds to hold on the last frame before looping.")]
    [Min(0f)] public float LoopRestartDelaySeconds;

    [Tooltip("Stop after this much playback, seconds; 0 never stops. Paused time does not count.")]
    [Min(0f)] public float StopAfterSeconds;

    [Tooltip("Gain for the audio sink beside this player, 0..1. A change here is handed to the sink's own Volume Gain; the sink's value is what the audio reads.")]
    [Range(0f, 1f)] public float Volume = 1f;

    [Tooltip("Silence the audio sink beside this player without stopping it.")]
    public bool Mute;

    [Tooltip("Log this player's own actions to the Console: state changes, seeks, loops, the sleep timer. Engine events are logged regardless.")]
    public bool VerboseLogging;

    [Tooltip("Invert the rows of a screenshot relative to what the player works out for this graphics API. Set it only if screenshots still come out upside down.")]
    public bool FlipVerticallyForScreenshot;

    /// <summary>Overrides the inferred liveness of an HTTP source; Auto
    /// infers. RTSP, WHEP and RIST are always live, and an HLS playlist
    /// decides for itself.</summary>
    [Tooltip("Override what the player works out for itself. Auto reads it from the " +
             "source and is almost always right; set Live or Vod only to overrule a " +
             "server whose headers mislead. RTSP, WHEP and RIST are always live, and " +
             "an HLS playlist decides for itself, whatever is set here.")]
    public BmLiveness liveness = BmLiveness.Auto;

    /// <summary>Live sources: the furthest behind the live edge this viewer
    /// may fall, milliseconds; 0 uses the engine default.</summary>
    [Tooltip("Shared playback, live sources: the furthest behind the live edge this " +
             "viewer may sit, milliseconds (a ceiling on automatic buffer growth). " +
             "Live position is never hard-synced between viewers — this bound is the " +
             "world author's instrument. 0 = the engine default.")]
    public int maxDivergenceMs;

    /// <summary>Allow private and loopback hosts, for testing. Cleared on
    /// avatars and props as they load.</summary>
    [Tooltip("Permit sources on private/loopback addresses (local test rigs only).")]
    public bool allowLocalAddresses;

    /// <summary>Write the engine's own diagnostics capture when the session
    /// ends.</summary>
    [Tooltip("Have the ENGINE capture its own side of the session: bank depth, " +
             "decode and present counts and clock, sampled every 100 ms on its " +
             "own thread. It is held in memory and written as one file when the " +
             "session ends, so a session killed rather than closed leaves no " +
             "capture. Not the BasisMediaPlayerDiagnostics component, which " +
             "records the other side of the boundary — render cadence, frame " +
             "hold times and audio pull, which the engine cannot observe. Run " +
             "both to see a session from each side.")]
    public bool engineCapture;

    /// <summary>The capture's filename under
    /// <c>Application.persistentDataPath</c>; empty uses
    /// <c>BasisMediaEngine.csv</c>.</summary>
    [Tooltip("Filename for the engine capture, under Application.persistentDataPath " +
             "beside the frame capture. Empty uses BasisMediaEngine.csv.")]
    public string engineCaptureFileName = "";

    /// <summary>Append each session's capture rather than replacing the
    /// file.</summary>
    [Tooltip("Append each session's capture to that file instead of replacing it. " +
             "A player that opens and closes repeatedly — one going dormant and " +
             "waking under the session cap, or a source re-opened to switch audio " +
             "track — otherwise leaves only the last session behind. The header is " +
             "written once, when the file is created.")]
    public bool engineCaptureAppend;

    ulong _handle;
    bool _open;
    bool _abiChecked;

    // URL consent. The request id tells a prompt's answer apart from one that
    // a later open or a close has already replaced. _approvedUrl is the last URL
    // accepted or trusted on this player, so a resolver handing it straight
    // back to Open is not asked twice for one action.
    bool _urlApprovalPending;
    bool _playRequestedWhileApprovalPending;
    string _pendingApprovalUrl = string.Empty;
    int _urlApprovalRequestId;
    Action _pendingApprovalDeclined;
    string _approvedUrl;
    Texture _texture;
    Texture2D _artwork;
    bool _artworkRead;
    /// Whether the outputs hold the cover art. Art stays on them until the
    /// source's first picture is presented, so the engine may already be
    /// drawing into a video texture in _texture while this is true.
    bool _textureIsArtwork;
    /// Whether _texture has been handed to the outputs. A video texture is
    /// registered with the engine as soon as the frame size is known, while
    /// the session is still buffering, but holds nothing to show until the
    /// first present.
    bool _textureShown;
    CommandBuffer _commandBuffer;
    long _audioFramesPulled;
    int _engineChannels;
    int _engineSampleRate;
    int _syncRatePpm;
    int _avOffsetUs = int.MinValue;
    BasisMediaPlayerAudio _audio;
    BasisMediaDriverTick _frameTick;
#if UNITY_ANDROID && !UNITY_EDITOR
    bool _renderHooked;
    int _lastRenderEventFrame = -1;
#endif
    long _sentAudioLatencyUs = -1;
    // Ordered by TickStage on insert, so the tick runs them in the order they
    // read each other rather than in registration order.
    readonly System.Collections.Generic.List<IBasisMediaTickConsumer> _consumers = new();
    readonly System.Collections.Generic.List<IBasisMediaTickConsumer> _consumersRan = new();
    readonly System.Collections.Generic.Queue<(long ptsUs, string text)> _captionQueue = new();
    // Pending messages in timestamp order, delivered from the front. The
    // engine hands them over in decode order, and under B-frames that runs
    // up to a reorder depth ahead of presentation, so a FIFO would hold a
    // B-frame's message behind the P-frame's that was decoded before it.
    readonly System.Collections.Generic.List<(long ptsUs, Guid uuid, byte[] buffer, int length)> _userDataPending = new();
    // The largest timestamp queued, so a backwards jump is measured against
    // the far end of the backlog. long.MinValue while nothing is queued.
    long _userDataMaxPtsUs = long.MinValue;
    // The entries due this tick, lifted out of the pending list before any
    // handler runs, so a handler that seeks (and so clears the list) meets
    // a consistent one.
    readonly System.Collections.Generic.List<(long ptsUs, Guid uuid, byte[] buffer, int length)> _userDataDue = new();
    // Bumped by every clear, so a delivery loop can tell that a handler
    // abandoned the timeline under it and stop handing out the rest.
    int _userDataTimeline;
    // Sized to the engine's per-message ceiling, so nothing it holds is
    // ever too large to cross; allocated on the first drain and kept for
    // the component's life.
    byte[] _userDataBytes;
    const int UserDataBytesCapacity = 64 * 1024;
    const int UserDataRecordsPerDrain = 64;
    readonly BasisSidecarSubtitleEngine _subtitles = new();
    readonly System.Collections.Generic.List<BasisSubtitleTrack> _subtitleTracks = new();

    /// <summary>The engine's own state. <see cref="Status"/> is the usual
    /// readout; this is the raw value the panel and shared playback work
    /// from.</summary>
    public BmState State { get; private set; } = BmState.Idle;

    /// <summary>Whether playback is meant to run: true from every open and
    /// from <see cref="Play"/>, false from <see cref="Pause"/>. The engine's
    /// <see cref="State"/> passes through Buffering on a seek and lands paused
    /// again by itself, so this is the flag for what the viewer asked for, and
    /// the one shared playback replicates. A live source cannot pause; when
    /// the engine declines, the flag follows the session back to true.</summary>
    public bool PlayWhenReady { get; private set; } = true;

    /// <summary>The session's state. <c>Ready</c> is never reported: the
    /// engine has no state between buffering and playing.</summary>
    public BasisMediaPlayerStatus Status
    {
        get
        {
            switch (State)
            {
                case BmState.Opening: return BasisMediaPlayerStatus.Connecting;
                case BmState.Buffering: return BasisMediaPlayerStatus.Buffering;
                case BmState.Playing: return BasisMediaPlayerStatus.Playing;
                case BmState.Paused: return BasisMediaPlayerStatus.Paused;
                case BmState.Ended: return BasisMediaPlayerStatus.Ended;
                case BmState.Error: return BasisMediaPlayerStatus.Error;
                default:
                    return string.IsNullOrEmpty(ActiveStreamUrl)
                        ? BasisMediaPlayerStatus.NoMedia
                        : BasisMediaPlayerStatus.Stopped;
            }
        }
    }

    // A session is open: it has neither ended nor failed, and nothing has
    // stopped it. Opening counts, so a load in progress is a session.
    bool SessionOpen =>
        State == BmState.Opening || State == BmState.Buffering
        || State == BmState.Playing || State == BmState.Paused;

    /// <summary>A session is open and meant to be playing: true from the
    /// open, while it is still opening or buffering, until <see cref="Pause"/>,
    /// <see cref="Stop"/>, the end or a failure. False while paused.</summary>
    public bool IsPlaying => SessionOpen && PlayWhenReady;

    /// <summary>A session is open and the viewer has paused it.</summary>
    public bool IsPaused => SessionOpen && !PlayWhenReady;

    /// <summary>This load has reached playback at least once, so the
    /// position, duration and picture describe it; true after it ended
    /// as well.</summary>
    public bool IsPrepared => (_readyThisLoad && SessionOpen) || State == BmState.Ended;

    /// <summary>The engine's code for the current failure, 0 when there is
    /// none; <see cref="LastErrorMessage"/> has the reason.</summary>
    public int ErrorCode { get; private set; }

    /// <summary>Why the session failed, or, while it plays on, why part of
    /// it was refused (a track no decoder here can play). Null when there is
    /// nothing to say. <see cref="State"/> tells the two apart.</summary>
    public string LastErrorMessage { get; private set; }

    /// <summary>Seeks the engine has acted on, landed or refused. Once it
    /// moves past the count read when seeking, the position can be from that
    /// seek's timeline rather than the one it left.</summary>
    internal int SeeksActedOn { get; private set; }
    /// <summary>The targets of the latest seeks the engine acted on, in
    /// microseconds, indexed by <see cref="SeeksActedOn"/>; -1 where an event
    /// could not be read.</summary>
    private readonly long[] _seekAnswersUs = new long[16];

    /// <summary>Whether the engine has acted on a seek to
    /// <paramref name="targetUs"/>, landed or refused, since
    /// <see cref="SeeksActedOn"/> read <paramref name="actedOnBefore"/>.
    /// Only the latest answers are kept; an older one reads as not
    /// answered.</summary>
    internal bool SeekAnsweredSince(int actedOnBefore, long targetUs, long withinUs)
    {
        int from = System.Math.Max(actedOnBefore, SeeksActedOn - _seekAnswersUs.Length);
        for (int n = from; n < SeeksActedOn; n++)
        {
            long answered = _seekAnswersUs[n % _seekAnswersUs.Length];
            if (answered >= 0 && System.Math.Abs(answered - targetUs) <= withinUs)
                return true;
        }
        return false;
    }
    // The position and duration in seconds, for the package's own arithmetic.
    internal double PositionSeconds { get; private set; }
    internal double DurationSeconds { get; private set; }

    /// <summary>The playback position: the clock's, not the last presented
    /// frame's.</summary>
    public TimeSpan Position => TimeSpan.FromSeconds(Math.Max(0d, PositionSeconds));

    /// <summary>The duration; zero for a live source.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0d, DurationSeconds));

    /// <summary>Media the engine holds ahead of playback, milliseconds.</summary>
    public long BankedMilliseconds { get; private set; }

    /// <summary>Video frames decoded this session.</summary>
    public ulong FramesDecoded { get; private set; }

    /// <summary>Video frames presented this session.</summary>
    public long PresentedFrameCount { get; private set; }
    internal Texture Texture => _textureIsArtwork ? _artwork : _textureShown ? _texture : null;

    /// <summary>Cover art the container carried, decoded, or null. Audio-only
    /// sources with art drive it onto the output texture, so a screen shows
    /// the sleeve rather than black.</summary>
    public Texture2D Artwork => _artwork;

    /// <summary>The output texture, or null until the first frame is
    /// presented (or cover art is shown). <see cref="OnOutputTextureChanged"/>
    /// announces it; a sink binds on that rather than polling.</summary>
    public Texture OutputTexture => Texture;

    /// <summary>Audio frames the sink has pulled, cumulative across
    /// sessions.</summary>
    public long AudioFramesPulled => System.Threading.Interlocked.Read(ref _audioFramesPulled);

    /// <summary>Where the engine capture was written for the current session,
    /// or null when <see cref="engineCapture"/> is off or the filename was
    /// refused. Set at open.</summary>
    public string EngineCapturePath { get; private set; }

    /// <summary>The audio sink on this GameObject, or null when there is
    /// none and the session plays silently.</summary>
    public BasisMediaPlayerAudio AudioComponent => _audio;

    /// <summary>The decoded frame size (zero until the engine announces
    /// dimensions).</summary>
    public Vector2Int VideoSize { get; private set; }

    /// <summary>Whether row 0 of <see cref="Texture"/> is the top of the
    /// picture, so a sink sampling with v=0 at the bottom has to flip.
    /// Direct3D hands over a top-left-origin texture; the Android convert
    /// pass already writes rows in Unity's Vulkan sampling
    /// orientation.</summary>
    public bool OutputFrameIsTopLeftOrigin
    {
        get
        {
            // Cover art is decoded by Unity, which writes row 0 at the
            // bottom as it does for any Texture2D. Only the engine's own
            // frames follow the platform's convention, so a sink told
            // otherwise flips a picture that was already the right way up.
            if (_textureIsArtwork) return false;
#if UNITY_ANDROID && !UNITY_EDITOR
            return false;
#else
            return true;
#endif
        }
    }

    /// <summary>Raised when the output texture is ready to show (the first
    /// frame presented, or cover art) or dropped (null on close). Output
    /// sinks bind on this rather than polling.
    /// </summary>
    public event Action<Texture> OnOutputTextureChanged;

    /// <summary>Raised once when the session reaches the end of the
    /// stream.</summary>
    public event Action OnEnded;

    /// <summary>Raised once per load when the session first reaches playback
    /// (Playing or Paused), so position, duration and picture describe
    /// it.</summary>
    public event Action OnReady;

    /// <summary>Raised when playback starts or resumes: on the first Playing
    /// of a load, and again after every pause.</summary>
    public event Action OnStarted;

    /// <summary>Raised once per load when its first frame has been
    /// presented.</summary>
    public event Action OnFirstFrameReady;

    /// <summary>Raised when the session pauses.</summary>
    public event Action OnPaused;

    /// <summary>Raised when <see cref="Loop"/> restarts the source after it
    /// ended.</summary>
    public event Action OnLooped;

    /// <summary>Raised when a load fails, before the session is released:
    /// the engine's failure (<see cref="State"/> is Error, with
    /// <see cref="ErrorCode"/> and <see cref="LastErrorMessage"/>), or a
    /// resolver's. The message names the code, category and reason.</summary>
    public event Action<Exception> OnError;

    /// <summary>Raised when the engine has acted on a seek, with the position
    /// that was asked for. A seek a live source refuses is not
    /// reported.</summary>
    public event Action<TimeSpan> OnSeekCompleted;

    /// <summary>Raised beside <see cref="OnCaptionChanged"/> with the cue:
    /// its times from the file for a sidecar track, the position it came due
    /// at for an in-band caption (end unknown, so equal to the start), and
    /// a null text when the display clears.</summary>
    public event Action<BasisCaptionCue> OnCaptionCueChanged;

    /// <summary>Raised when this player's override moves captions on or
    /// off. The viewer's own setting changes do not pass through here; a
    /// presenter reads <see cref="CaptionsEnabled"/>.</summary>
    public event Action<bool> OnCaptionsEnabledChanged;

    /// <summary>Raised when one of this player's opacity overrides changes
    /// the value in force.</summary>
    public event Action OnCaptionStyleChanged;

    /// <summary>The stream's audio sample rate (Hz; 0 until announced).
    /// The pull consumes the engine ring at this rate regardless of the
    /// device DSP rate.</summary>
    public int AudioSampleRate => System.Threading.Volatile.Read(ref _engineSampleRate);

    /// <summary>The stream's channel count (0 until announced).</summary>
    public int AudioChannels => System.Threading.Volatile.Read(ref _engineChannels);

    /// <summary>The engine-declared capability set: what this
    /// basis_media build will decode and play. Queried once and cached;
    /// null when the plugin is unavailable or the ABI mismatched. See
    /// <see cref="BasisMediaCapabilities"/> for the raw JSON and
    /// re-query.</summary>
    public static BmCapabilitySet EngineCapabilities => BasisMediaCapabilities.Set;

    /// <summary>Decode-route preference applied to every descriptor this
    /// component builds at open (takes effect on the next open).
    /// A static rather than a serialised inspector field: it is the user's
    /// machine setting, persisted client-side by the settings UI, never world
    /// content.</summary>
    public static BmDecodePreference DecodePreference = BmDecodePreference.HardwareWithFallback;

    /// <summary>Jitter buffer depth for players that have not been tuned
    /// individually, in milliseconds; 0 = Auto, which sizes itself from the
    /// delivery delays it observes and is right for almost everyone.
    ///
    /// A static for the same reason as <see cref="DecodePreference"/>: it
    /// trades the viewer's own connection against how soon they see a frame,
    /// and a world author cannot see that connection. The divergence bound
    /// stays authored because it belongs to the shared experience.</summary>
    public static int DefaultBufferDepthMs;

    /// <summary>This player's own depth, when the viewer has tuned it away
    /// from the default; null while it follows <see cref="DefaultBufferDepthMs"/>.
    ///
    /// Per player because one scene can hold both: a source next door, where
    /// the point is the latency a shallow buffer buys, and one from the far
    /// side of the world that only plays smoothly with depth behind it. A
    /// single figure cannot serve those two at once.
    ///
    /// Not serialised and never synced: it describes this viewer's route to
    /// that source, which no other client shares and no world can know.
    /// Read when a session opens, so <see cref="ReopenAtPosition"/> is what
    /// makes a change take effect on one already running.</summary>
    /// <remarks>Unity cannot serialise a nullable either way, so
    /// <see cref="NonSerializedAttribute"/> changes no behaviour. It stops the
    /// serialisation analyser reporting a field that is skipped on purpose.</remarks>
    [NonSerialized] public int? BufferDepthOverrideMs;

    /// <summary>The depth this player actually opens with.</summary>
    public int EffectiveBufferDepthMs => BufferDepthOverrideMs ?? DefaultBufferDepthMs;

    /// <summary>Raised when one of this player's caption overrides moves, so a
    /// presenter showing it can re-read. Style follows the viewer, so it is not
    /// part of the session and nothing here is synced.</summary>
    internal event Action OnCaptionPreferencesChanged;

    bool? _captionsEnabledOverride;
    float? _captionTextOpacityOverride;
    float? _captionBackgroundOpacityOverride;

    /// <summary>Whether captions are drawn for this player, when the viewer
    /// has decided it for this one; null while it follows the stored
    /// preference. Per player because a scene can hold one source worth
    /// reading and another that is only ever background.</summary>
    public bool? CaptionsEnabledOverride
    {
        get => _captionsEnabledOverride;
        set
        {
            if (_captionsEnabledOverride == value) return;
            bool before = CaptionsEnabledEffective;
            _captionsEnabledOverride = value;
            OnCaptionPreferencesChanged?.Invoke();
            if (CaptionsEnabledEffective != before) OnCaptionsEnabledChanged?.Invoke(CaptionsEnabledEffective);
        }
    }

    /// <summary>Caption text opacity for this player, or null to follow the
    /// stored preference.</summary>
    public float? CaptionTextOpacityOverride
    {
        get => _captionTextOpacityOverride;
        set
        {
            if (_captionTextOpacityOverride == value) return;
            float before = CaptionTextOpacityEffective;
            _captionTextOpacityOverride = value;
            OnCaptionPreferencesChanged?.Invoke();
            if (CaptionTextOpacityEffective != before) OnCaptionStyleChanged?.Invoke();
        }
    }

    /// <summary>Caption background opacity for this player, or null to follow
    /// the stored preference.</summary>
    public float? CaptionBackgroundOpacityOverride
    {
        get => _captionBackgroundOpacityOverride;
        set
        {
            if (_captionBackgroundOpacityOverride == value) return;
            float before = CaptionBackgroundOpacityEffective;
            _captionBackgroundOpacityOverride = value;
            OnCaptionPreferencesChanged?.Invoke();
            if (CaptionBackgroundOpacityEffective != before) OnCaptionStyleChanged?.Invoke();
        }
    }

    /// <summary>Whether captions should be drawn for this player.</summary>
    internal bool CaptionsEnabledEffective =>
        _captionsEnabledOverride ?? BasisMediaSettings.CaptionsEnabled.RawValue;

    /// <summary>Caption text opacity for this player, 0..1.</summary>
    internal float CaptionTextOpacityEffective =>
        _captionTextOpacityOverride ?? BasisMediaSettings.CaptionTextOpacity.RawValue;

    /// <summary>Caption background opacity for this player, 0..1.</summary>
    internal float CaptionBackgroundOpacityEffective =>
        _captionBackgroundOpacityOverride ?? BasisMediaSettings.CaptionBackgroundOpacity.RawValue;

    /// <summary>Captions on or off for this player. Reads the value in force
    /// (the viewer's setting unless this player overrides it); writing sets
    /// this player's override. <see cref="CaptionsEnabledOverride"/> is the
    /// override itself, and null there returns to the viewer's setting.</summary>
    public bool CaptionsEnabled
    {
        get => CaptionsEnabledEffective;
        set => CaptionsEnabledOverride = value;
    }

    /// <summary>Caption text opacity in force, 0..1; writing sets this
    /// player's override.</summary>
    public float CaptionTextOpacity
    {
        get => CaptionTextOpacityEffective;
        set => CaptionTextOpacityOverride = Mathf.Clamp01(value);
    }

    /// <summary>Caption background opacity in force, 0..1; writing sets this
    /// player's override.</summary>
    public float CaptionBackgroundOpacity
    {
        get => CaptionBackgroundOpacityEffective;
        set => CaptionBackgroundOpacityOverride = Mathf.Clamp01(value);
    }

    /// <summary>The in-band CEA-608 caption currently due at the playback
    /// position (empty = none). Rows are joined with '\n'.</summary>
    public string CurrentCaption { get; private set; } = "";

    /// <summary>Raised when <see cref="CurrentCaption"/> changes (an empty
    /// string is a clear). Raised regardless of whether the viewer has
    /// captions switched on, so a display can stay primed while hidden.
    /// </summary>
    public event Action<string> OnCaptionChanged;

    /// <summary>
    /// One SEI user-data message due at the playback position. The engine
    /// hands these over unparsed: <paramref name="payload"/> is whatever
    /// followed the 16-byte UUID inside the `user_data_unregistered`
    /// message, and the handler decides what it means. It is borrowed for
    /// the call; copy what outlives it.
    /// </summary>
    public delegate void UserDataHandler(long ptsUs, Guid uuid, ReadOnlySpan<byte> payload);

    /// <summary>
    /// Raised for each SEI user_data_unregistered message (H.264 and H.265
    /// payload type 5) once playback reaches its timestamp, in timestamp
    /// order (messages with equal timestamps keep their stream order).
    /// Every UUID arrives, the encoder's own included (x264 stamps its build
    /// string on keyframes this way), so a consumer filters on the UUID it
    /// expects. A seek drops whatever was queued from the old position.
    /// Messages are held until due whether or not anyone is subscribed, so
    /// a subscriber attaching mid-session receives everything still to come
    /// and nothing already past.
    /// </summary>
    public event UserDataHandler OnUserDataReceived;

    /// <summary>Out-of-band subtitle tracks offered for this source. The
    /// media carries none of these; a resolver or other enrichment source
    /// supplies them through <see cref="SetSubtitleTracks"/>.</summary>
    public System.Collections.Generic.IReadOnlyList<BasisSubtitleTrack> SubtitleTracks => _subtitleTracks;

    /// <summary>Index into <see cref="SubtitleTracks"/>, or -1 for the
    /// default: no sidecar track, with in-band captions flowing as usual.
    /// While a sidecar track is selected, in-band cues are suppressed.
    /// Client-side only, like the viewer's caption preferences in
    /// <see cref="BasisMediaSettings"/>.</summary>
    public int SelectedSubtitleTrackIndex { get; private set; } = -1;

    /// <summary>Selection changed, including the automatic revert to -1
    /// when a fetch fails or the session closes.</summary>
    public event Action<int> OnSubtitleTrackChanged;

    readonly System.Collections.Generic.List<BasisAudioTrack> _audioTracks =
        new System.Collections.Generic.List<BasisAudioTrack>();
    int _audioTrackIndex;
    bool _audioTracksRead;
    // Set while SelectAudioTrack is re-opening, so the position restore
    // and the enumeration refresh know this is a track switch rather than
    // a fresh load.
    double _reopenResumeAt = -1d;
    // A start position LoadSource asked for; applied once and cleared.
    double _startAtSeconds = -1d;
    // Playing or Paused seen since this load opened.
    bool _readyThisLoad;
    // Whether the engine has been told about the current pause intent.
    bool _pauseIssued;
    // Polls spent in Playing against a pause the engine was told about.
    int _pollsPlayingAgainstPause;
    // About half a second at 60 to 72 Hz. A pause that lands shows within a
    // frame or two, so a session still playing after this many polls was
    // refused.
    const int DeclinedPausePolls = 30;
    // When a loop restart is due (unscaled time), or -1.
    float _loopRestartAt = -1f;
    // Playback time this load has run, for StopAfterSeconds.
    float _playedSeconds;
    // The last Volume and Mute handed to the sink; NaN forces the first push.
    float _volumeSent;
    bool _muteSent;

    /// <summary>Hand a changed <see cref="Volume"/> or <see cref="Mute"/> to
    /// the audio sink. Only changes travel, so the sink's own fields stay
    /// usable: the last side to move wins.</summary>
    void PushVolumeToSink()
    {
        if (_audio == null) return;
        if (Volume != _volumeSent)
        {
            _volumeSent = Volume;
            _audio.VolumeGain = Mathf.Clamp01(Volume);
        }
        if (Mute != _muteSent)
        {
            _muteSent = Mute;
            _audio.Mute = Mute;
        }
    }
    // OnStarted has been raised for the current run of playback.
    bool _playingAnnounced;
    // OnFirstFrameReady has been raised for this load.
    bool _firstFrameAnnounced;
    // Seeks the drain saw acted on, raised once it is done.
    readonly System.Collections.Generic.List<long> _seekCompletedPendingUs = new();

    /// <summary>The audio tracks this source offers, in container order.
    /// Empty when there is nothing to choose between (one track, or a
    /// container that does not enumerate them), so a picker can simply
    /// hide itself on an empty list.</summary>
    public System.Collections.Generic.IReadOnlyList<BasisAudioTrack> AudioTracks => _audioTracks;

    /// <summary>Which of <see cref="AudioTracks"/> is playing.</summary>
    public int SelectedAudioTrackIndex => _audioTrackIndex;

    // The index form, for the panel's picker.
    internal event Action<int> OnAudioTrackIndexChanged;

    /// <summary>Raised when the offered tracks or the selection change: on
    /// open once the engine has read the container, and after a successful
    /// switch. Not raised for a source with one track, whose
    /// <see cref="AudioTracks"/> is empty.</summary>
    public event Action<BasisAudioTrack> OnAudioTrackChanged;

    void RaiseAudioTrackChanged()
    {
        OnAudioTrackIndexChanged?.Invoke(_audioTrackIndex);
        if (_audioTrackIndex >= 0 && _audioTrackIndex < _audioTracks.Count)
            OnAudioTrackChanged?.Invoke(_audioTracks[_audioTrackIndex]);
    }

    /// <summary>Play a different audio track. The engine binds its audio
    /// track when the container is opened, so this re-opens the session
    /// and returns to the current position rather than switching in
    /// place: a short re-buffer, in exchange for no race against a running
    /// session. Returns false, and does nothing, when the index is already
    /// selected, out of range, or there is no session to switch.</summary>
    public bool SelectAudioTrack(int index)
    {
        if (index < 0 || index >= _audioTracks.Count || index == _audioTrackIndex)
            return false;
        if (string.IsNullOrEmpty(ActiveStreamUrl))
            return false;

        _audioTrackIndex = index;
        ReopenAtPosition();
        RaiseAudioTrackChanged();
        return true;
    }

    /// <summary>Re-open the current source where it is, so a setting the
    /// engine only reads at open takes effect now. Costs a short re-buffer;
    /// a live source rejoins the edge because it has nothing to return to.
    /// The same load on a new session: it re-opens the stream the session
    /// is already on rather than routing through the resolver again, keeps
    /// what the load knows (the URL asked for, a resolver's answer, the
    /// audio track and subtitle choices, a pause), does not move
    /// <see cref="LoadGeneration"/>,
    /// and so is not a new load to shared playback: nobody else's playback
    /// moves because of it.</summary>
    public void ReopenAtPosition()
    {
        // ActiveStreamUrl outlives a close, so the state check is what stops
        // a setting change starting a player that was stopped or had ended.
        if (string.IsNullOrEmpty(ActiveStreamUrl))
            return;
        if (State != BmState.Opening && State != BmState.Buffering
            && State != BmState.Playing && State != BmState.Paused)
            return;

        // Remembered across the re-open; a live source has nothing to
        // return to, so it simply rejoins the edge.
        _reopenResumeAt = DurationSeconds > 0d ? PositionSeconds : -1d;
        int subtitle = SelectedSubtitleTrackIndex;
        OpenStreams(ActiveStreamUrl, ActiveAudioStreamUrl, reopen: true);
        // Ending the session dropped the selection; the tracks are the same.
        if (subtitle >= 0 && State == BmState.Opening)
            SelectSubtitleTrack(subtitle);
    }

    /// <summary>Replaces the offered tracks and drops any selection. The
    /// tracks belong to a source, so set them per open.</summary>
    public void SetSubtitleTracks(System.Collections.Generic.IReadOnlyList<BasisSubtitleTrack> tracks)
    {
        SelectSubtitleTrack(-1);
        _subtitleTracks.Clear();
        if (tracks == null)
            return;
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i] != null && !string.IsNullOrEmpty(tracks[i].Url))
                _subtitleTracks.Add(tracks[i]);
        }
    }

    /// <summary>Selects a sidecar track by <see cref="SubtitleTracks"/>
    /// index, or -1 to return to in-band captions. The track is fetched
    /// once, and the URL is checked against the client's URL security
    /// first; on failure the selection reverts to -1.</summary>
    public void SelectSubtitleTrack(int index)
    {
        if (index < 0 || index >= _subtitleTracks.Count)
            index = -1;
        if (index == SelectedSubtitleTrackIndex)
            return;

        SelectedSubtitleTrackIndex = index;
        _subtitleSelection++;
        // Whatever is on screen belongs to the previous selection; the
        // in-band feed repaints at its next cue change.
        ClearCaptionDisplay();
        if (index < 0)
        {
            _subtitles.Clear();
            OnSubtitleTrackChanged?.Invoke(-1);
            return;
        }
        OnSubtitleTrackChanged?.Invoke(index);
        _ = LoadSubtitleTrackAsync(_subtitleTracks[index], index, _subtitleSelection, LoadGeneration);
    }

    // Moves on every selection change and every session end, so a fetch
    // started for one selection can tell that another has taken over, even
    // one that landed on the same index for a different source.
    int _subtitleSelection;

    async System.Threading.Tasks.Task LoadSubtitleTrackAsync(BasisSubtitleTrack track, int index, int selection, int generation)
    {
        BasisSubtitleLoad result = await _subtitles.LoadTrackAsync(track);
        // A later selection, another source or a stop has taken over since
        // this fetch began: nothing here is its to touch, and a fetch that
        // was overtaken has nothing to report.
        if (selection != _subtitleSelection || generation != LoadGeneration || result == BasisSubtitleLoad.Superseded)
            return;
        if (result == BasisSubtitleLoad.Loaded)
        {
            // The cue covering the current position has never been
            // reported, so let it through on the next tick.
            _subtitles.ResetCueTracking();
            return;
        }
        BasisDebug.LogWarning($"[BasisMedia] subtitle track {index} failed to load; reverting to in-band captions.", BasisDebug.LogTag.Video);
        SelectedSubtitleTrackIndex = -1;
        _subtitleSelection++;
        _subtitles.Clear();
        OnSubtitleTrackChanged?.Invoke(-1);
    }

    void ClearUserData()
    {
        foreach (var pending in _userDataPending)
            ArrayPool<byte>.Shared.Return(pending.buffer);
        _userDataPending.Clear();
        _userDataMaxPtsUs = long.MinValue;
        _userDataTimeline++;
    }

    void ClearCaptionDisplay()
    {
        _captionQueue.Clear();
        _subtitles.ResetCueTracking();
        SetCaption("");
    }

    void SetCaption(string text) => SetCaption(text, 0, 0);

    void SetCaption(string text, long startUs, long endUs)
    {
        text ??= "";
        if (CurrentCaption == text)
            return;
        CurrentCaption = text;
        OnCaptionChanged?.Invoke(text);
        OnCaptionCueChanged?.Invoke(text.Length == 0
            ? new BasisCaptionCue(null, 0, 0)
            : new BasisCaptionCue(text, startUs, endUs));
    }

    void Awake()
    {
        _frameTick = new BasisMediaDriverTick(Tick);
        // The sink lives beside the player, as it does in the authored
        // prefabs. It is optional: a player with none is a decoder with no
        // speakers wired to it.
        TryGetComponent(out _audio);
        // Values authored away from the defaults reach the sink on the first
        // tick; defaults do not, so a prefab's own sink settings stand.
        _volumeSent = Mathf.Approximately(Volume, 1f) ? 1f : float.NaN;
        _muteSent = false;
        BasisMediaPlayerRegistry.Add(this);
    }

    void OnEnable() => _frameTick.Arm();

    void OnDisable()
    {
        _frameTick.Disarm();
        // A load a resolver still holds counts too: left alone, it would open
        // a session on a component nothing ticks.
        if (StopOnDisable && (_open || LoadPending)) Stop();
    }

    /// <summary>The authored URL for the platform this build runs on:
    /// <see cref="androidUrl"/> on an Android player when it is set, and
    /// <see cref="url"/> otherwise. The editor always reports
    /// <see cref="url"/>, so entering play mode with the Android build target
    /// selected does not silently exercise the other one.</summary>
    public string ResolvedUrl
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!string.IsNullOrEmpty(androidUrl)) return androidUrl.Trim();
#endif
            return url?.Trim();
        }
    }

    void Start()
    {
        // Through the router, so an authored page URL resolves rather
        // than failing to open.
        string authored = ResolvedUrl;
        if (playOnStart && !string.IsNullOrEmpty(authored))
            LoadUrl(authored);
    }

    /// <summary>Open a split pair: a video-only source and the
    /// audio-only one that belongs with it. Pass null for the second
    /// argument to open an ordinary muxed source and drop any pair
    /// left over from a previous open. An untrusted host asks the user
    /// first, as <see cref="Open(string)"/> does.</summary>
    public void Open(string sourceUrl, string sourceAudioUrl)
    {
        // Both legs are fetched, so both are asked about. Coming back through
        // Open after the first answer is what reaches the second check.
        if (!IsApproved(sourceUrl))
        {
            RequestUrlApproval(sourceUrl, approved => Open(approved, sourceAudioUrl));
            return;
        }
        if (!string.IsNullOrEmpty(sourceAudioUrl) && !IsApproved(sourceAudioUrl))
        {
            RequestUrlApproval(sourceAudioUrl, approvedAudio => OpenStreams(sourceUrl, approvedAudio));
            return;
        }
        OpenStreams(sourceUrl, sourceAudioUrl);
    }

    /// <summary>
    /// Open whatever the user actually typed or a world author authored,
    /// steering page URLs (a YouTube or Twitch watch page) through any
    /// installed resolver. A directly-playable URL opens straight
    /// through, and with no resolver installed every URL does. A host the
    /// user has not trusted (<see cref="BasisTrustedUrls"/>) is shown to
    /// them first and opens only if they accept.
    /// </summary>
    public void LoadUrl(string sourceUrl)
    {
        if (!PrepareUserUrl(ref sourceUrl))
            return;
        if (!IsApproved(sourceUrl))
        {
            RequestUrlApproval(sourceUrl, OpenPreparedUrl);
            return;
        }
        OpenPreparedUrl(sourceUrl);
    }

    /// <summary>
    /// <see cref="LoadUrl"/> without the consent prompt, for a URL the
    /// user has already answered for: one they typed into the menu, or the
    /// one the shared-playback owner chose for the room.
    /// </summary>
    internal void LoadApprovedUrl(string sourceUrl)
    {
        if (PrepareUserUrl(ref sourceUrl))
            OpenPreparedUrl(sourceUrl);
    }

    bool PrepareUserUrl(ref string sourceUrl)
    {
        // Refused up front as well as at Open, so a locked client never hands a page
        // URL to the resolver: that route leaves this method and comes back through
        // OpenResolved later, by which time the extraction has already happened.
        if (BasisNetworkModeration.MediaPlayerBlockedLocally)
        {
            BasisDebug.LogWarning(
                "BasisMediaPlayer.LoadUrl blocked: media players are locked by an admin.",
                BasisDebug.LogTag.Video);
            return false;
        }
        sourceUrl = BasisMediaUrlRouter.NormalizeUrl(sourceUrl);
        return !string.IsNullOrEmpty(sourceUrl);
    }

    void OpenPreparedUrl(string sourceUrl)
    {
        _approvedUrl = sourceUrl;
        if (BasisMediaUrlRouter.IsDirectlyPlayable(sourceUrl))
        {
            OpenStreams(sourceUrl, null);
            return;
        }
        // The resolver owns the load once it claims the URL: it opens the
        // player itself, asynchronously, when extraction finishes. The
        // generation moves now, not when that open lands, so of two page
        // URLs asked for in a row the later one wins whichever extraction
        // finishes first, and a stop in between drops the result.
        LoadGeneration++;
        BeginLoadCancellation();
        LoadPending = true;
        if (BasisMediaUrlRouter.TryResolveAndLoad(this, sourceUrl))
            return;
        LoadPending = false;
        OpenStreams(sourceUrl, null);
    }

    CancellationTokenSource _loadCancellation;

    /// <summary>Cancelled when the load a resolver is working on is
    /// superseded: by a later open, a stop, or the player's destruction. A
    /// resolver passes it into its extraction so the work stops rather than
    /// running on for a result nobody wants; the generation check still
    /// stands for a resolver that ignores it.</summary>
    public CancellationToken LoadCancellation => _loadCancellation?.Token ?? CancellationToken.None;

    /// <summary>A resolver has claimed the current load and has not opened
    /// it yet. The previous session, if any, plays on meanwhile; shared
    /// playback waits on this before it treats the player's state as the new
    /// load's.</summary>
    public bool LoadPending { get; private set; }

    void BeginLoadCancellation()
    {
        CancelLoad();
        _loadCancellation = new CancellationTokenSource();
    }

    void CancelLoad()
    {
        LoadPending = false;
        if (_loadCancellation == null) return;
        _loadCancellation.Cancel();
        _loadCancellation.Dispose();
        _loadCancellation = null;
    }

    bool IsApproved(string sourceUrl)
        => !string.IsNullOrWhiteSpace(sourceUrl)
           && (string.Equals(sourceUrl, _approvedUrl, StringComparison.Ordinal)
               || BasisTrustedUrls.IsTrusted(sourceUrl));

    /// <summary>
    /// Calls <paramref name="open"/> with the normalised URL once the user has
    /// approved it (at once when it is trusted or already approved on this
    /// player), or <paramref name="declined"/> when the URL is refused, the
    /// user says no, nobody can be asked, or another open supersedes the
    /// question. Exactly one of the two runs. Shared playback uses this to ask
    /// before it announces a URL to the room.
    /// </summary>
    internal void WhenApproved(string sourceUrl, Action<string> open, Action declined)
    {
        if (!PrepareUserUrl(ref sourceUrl))
        {
            declined?.Invoke();
            return;
        }
        if (IsApproved(sourceUrl))
        {
            open(sourceUrl);
            return;
        }
        RequestUrlApproval(sourceUrl, open, declined);
    }

    void RequestUrlApproval(string sourceUrl, Action<string> open, Action declined = null)
    {
        ClearPendingUrlApproval();
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out Uri uri))
        {
            BasisDebug.LogWarning("[BasisMedia] refused invalid URL.", BasisDebug.LogTag.Video);
            declined?.Invoke();
            return;
        }

        _pendingApprovalUrl = sourceUrl;
        _urlApprovalPending = true;
        _pendingApprovalDeclined = declined;
        int requestId = ++_urlApprovalRequestId;
        // The question is a newer load than any resolve still in flight, whose
        // late result would otherwise open and take the prompt down with it.
        LoadGeneration++;

        if (!BasisNotificationCenter.RouteToNotifications)
            BasisMainMenu.Open();

        BasisMenuURLPromptPanel panel = BasisMenuURLPromptPanel.CreateNew(
            sourceUrl,
            response =>
            {
                if (this == null
                    || !_urlApprovalPending
                    || _urlApprovalRequestId != requestId
                    || !string.Equals(_pendingApprovalUrl, sourceUrl, StringComparison.Ordinal))
                    return;

                bool playAfterApproval = _playRequestedWhileApprovalPending;
                Action onDeclined = _pendingApprovalDeclined;
                _pendingApprovalDeclined = null;
                ClearPendingUrlApproval();
                if (!response.Accepted)
                {
                    onDeclined?.Invoke();
                    return;
                }

                if (response.RememberChoice)
                {
                    switch (response.Scope)
                    {
                        case BasisMenuURLPromptPanel.RememberChoiceScope.URL:
                            BasisTrustedUrls.Add(sourceUrl);
                            break;
                        case BasisMenuURLPromptPanel.RememberChoiceScope.Hostname:
                            BasisTrustedUrls.Add(uri.Scheme + "://" + uri.Host + "/*");
                            break;
                        case BasisMenuURLPromptPanel.RememberChoiceScope.Domain:
                            BasisTrustedUrls.AddDomain(uri);
                            break;
                    }
                }

                _approvedUrl = sourceUrl;
                open(sourceUrl);
                if (playAfterApproval)
                    Play();
            },
            divertible: true);

        // Null is also what a prompt diverted to the notification list
        // returns; that one is still pending. With nothing to divert to and
        // no menu to show it in (a headless client), nobody can answer.
        if (panel == null && !BasisNotificationCenter.RouteToNotifications
            && _urlApprovalPending && _urlApprovalRequestId == requestId)
        {
            BasisDebug.LogWarning(
                $"[BasisMedia] refused '{BasisMediaUrlRouter.Redact(sourceUrl)}': no way to ask the user.",
                BasisDebug.LogTag.Video);
            ClearPendingUrlApproval();
        }
    }

    void ClearPendingUrlApproval()
    {
        if (!_urlApprovalPending && !_playRequestedWhileApprovalPending)
            return;
        Action declined = _pendingApprovalDeclined;
        _pendingApprovalDeclined = null;
        _urlApprovalPending = false;
        _playRequestedWhileApprovalPending = false;
        _pendingApprovalUrl = string.Empty;
        _urlApprovalRequestId++;
        declined?.Invoke();
    }

    /// <summary>
    /// Open what a resolver produced: the stream or pair of streams, the
    /// liveness it already knows, and the subtitle tracks and display
    /// metadata it picked up on the way.
    /// </summary>
    public void OpenResolved(BasisResolvedMedia media)
    {
        // A continuation can land after the component is gone; opening a
        // session then would leave the engine decoding for nobody.
        if (!this)
            return;
        if (media == null || string.IsNullOrEmpty(media.Url))
            return;
        // Refused up front as well as inside the open, because the open leaves
        // the previous session playing and nothing of this answer may touch it,
        // the liveness hint included.
        if (BasisNetworkModeration.MediaPlayerBlockedLocally)
            return;
        liveness = media.Liveness;
        // The URL fields keep what a person actually asked for. What a
        // resolver extracted is issued per client and carries an expiry, so
        // writing it back would replace a page URL that keeps working with a
        // stream URL that stops, and would overwrite an authored field on the
        // way. See ActiveStreamUrl for what is really open.
        string askedFor = !string.IsNullOrEmpty(media.SourceUrl) ? media.SourceUrl : url;
        string askedForAudio = audioUrl;
        // All of this lands after the open, which clears what the previous
        // source left behind. No consent prompt: a resolver calls this for a
        // URL the user already answered for, and the prop sandbox refuses it
        // to world scripts.
        // The metadata is announced once below, with the resolver's answer,
        // not first with the stream URL the open would build it from.
        OpenStreams(media.Url, media.AudioUrl, announceMetadata: false);
        url = askedFor;
        audioUrl = askedForAudio;
        // A stream that failed to open has nothing to describe.
        if (State != BmState.Opening)
            return;
        Media = media;
        SetSubtitleTracks(media.SubtitleTracks);
        OnMediaChanged?.Invoke(media);
        OnMetadataChanged?.Invoke(Metadata);
    }

    /// <summary>The stream the engine was actually handed, which for a
    /// resolved page URL is the extracted stream rather than the page. Not
    /// serialised, and not shareable: these carry an expiry and are issued per
    /// client. Null until something has been opened.</summary>
    public string ActiveStreamUrl { get; private set; }

    /// <summary>The audio leg the engine was handed, when the source is a
    /// split pair. Empty otherwise.</summary>
    public string ActiveAudioStreamUrl { get; private set; }

    /// <summary>What is playing, as far as anyone could tell: a
    /// resolver's answer when one handled the load, otherwise null.
    /// </summary>
    public BasisResolvedMedia Media { get; private set; }

    /// <summary>Raised when <see cref="Media"/> changes.</summary>
    public event Action<BasisResolvedMedia> OnMediaChanged;

    /// <summary>What is known about the source, for display: a
    /// copy, built from <see cref="Media"/> when a resolver answered and from
    /// the URL otherwise, with anything <see cref="ApplyMetadata"/> added on
    /// top. Null until the first load.</summary>
    public BasisMediaMetadata Metadata
    {
        get
        {
            if (string.IsNullOrEmpty(ActiveStreamUrl))
                return null;
            BasisMediaMetadata meta = Media != null
                ? BasisMediaMetadata.FromResolved(Media)
                : BasisMediaMetadata.FromUrl(url);
            meta.MergeFrom(_metadataApplied);
            return meta;
        }
    }

    /// <summary>Raised when <see cref="Metadata"/> changes: once a load's
    /// metadata is known, again when a resolver fills it in, and after
    /// <see cref="ApplyMetadata"/>.</summary>
    public event Action<BasisMediaMetadata> OnMetadataChanged;

    BasisMediaMetadata _metadataApplied;

    /// <summary>Add to or correct what is known about the current source:
    /// every field <paramref name="partial"/> sets replaces the known one.
    /// Display data only; nothing here reaches the engine or other
    /// viewers.</summary>
    public void ApplyMetadata(BasisMediaMetadata partial)
    {
        if (partial == null)
            return;
        _metadataApplied ??= new BasisMediaMetadata();
        _metadataApplied.MergeFrom(partial);
        OnMetadataChanged?.Invoke(Metadata);
    }

    /// <summary>Moves on every load request (an open, a page URL handed to
    /// a resolver, a consent prompt) and on every stop. A resolver captures
    /// it before its extraction and drops the result if it moved, so a slow
    /// resolve cannot overwrite a load the user started after it, nor re-open
    /// a player the user stopped.</summary>
    public int LoadGeneration { get; private set; }

    /// <summary>Report a load that failed before the engine ever saw it,
    /// such as a resolver that could not extract a page URL. The engine's own
    /// failures arrive through the snapshot instead.</summary>
    public void ReportLoadError(Exception error)
    {
        LoadPending = false;
        BasisDebug.LogError($"[BasisMedia] load failed: {error?.GetType().Name ?? "unknown"}", BasisDebug.LogTag.Video);
        State = BmState.Error;
        OnError?.Invoke(error ?? new InvalidOperationException("media load failed"));
    }

    /// <summary>Open (or re-open) a source. The engine opens asynchronously;
    /// watch <see cref="State"/>. Keeps whichever split-pair audio source
    /// the last <see cref="Open(string, string)"/> set, so re-opening a
    /// resolved pair re-opens it as a pair; pass null there to drop it.
    /// </summary>
    public void Open(string sourceUrl)
    {
        if (!IsApproved(sourceUrl))
        {
            RequestUrlApproval(sourceUrl, approved => OpenStreams(approved, audioUrl));
            return;
        }
        OpenStreams(sourceUrl, audioUrl);
    }

    /// <summary>Open a file on this machine by path. A
    /// <c>file://</c> prefix is accepted and dropped: the engine takes bare
    /// paths. Drops any split-pair audio source a previous open set.</summary>
    public void LoadLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        path = path.Trim();
        if (path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            path = path.Substring("file://".Length);
        Open(path, null);
    }

    /// <summary>Open a described source. The URL, the
    /// audio leg, the delivery hint and the start position are taken; the
    /// loop, volume and mute land on this player's fields; the rest of
    /// <see cref="BasisMediaSource"/> (headers, options, timeout, rate) has
    /// no engine counterpart and is ignored. An untrusted host is asked
    /// about first, as <see cref="Open(string, string)"/> does; the start
    /// position and metadata apply once the source has opened.</summary>
    public void LoadSource(BasisMediaSource media)
    {
        if (media == null || string.IsNullOrWhiteSpace(media.Uri))
            return;
        if (media.Delivery == BasisMediaDelivery.Live) liveness = BmLiveness.Live;
        else if (media.Delivery == BasisMediaDelivery.OnDemand) liveness = BmLiveness.Vod;
        Loop = media.Loop;
        if (media.Volume.HasValue) Volume = Mathf.Clamp01(media.Volume.Value);
        if (media.Mute.HasValue) Mute = media.Mute.Value;
        if ((media.Headers != null && media.Headers.Count > 0)
            || (media.Options != null && media.Options.Count > 0)
            || media.OpenTimeout > TimeSpan.Zero
            || !Mathf.Approximately(media.PlaybackRate, 1f))
        {
            BasisDebug.LogWarningOnce(
                "[BasisMedia] LoadSource: headers, options, open timeout and playback rate have no engine counterpart and are ignored.",
                BasisDebug.LogTag.Video);
        }
        string audio = string.IsNullOrWhiteSpace(media.AudioUri) ? null : media.AudioUri;
        OpenSource(media.Uri, audio, media);
    }

    // Open(string, string)'s approval chain, carrying the source so its start
    // position and metadata land after the open, not before a prompt the
    // open then clears. One URL is remembered as approved at a time, so the
    // second answer opens the pair directly rather than asking again.
    void OpenSource(string sourceUrl, string sourceAudioUrl, BasisMediaSource media)
    {
        if (!IsApproved(sourceUrl))
        {
            RequestUrlApproval(sourceUrl, approved => OpenSource(approved, sourceAudioUrl, media));
            return;
        }
        if (!string.IsNullOrEmpty(sourceAudioUrl) && !IsApproved(sourceAudioUrl))
        {
            RequestUrlApproval(sourceAudioUrl, approvedAudio => OpenSourceApproved(sourceUrl, approvedAudio, media));
            return;
        }
        OpenSourceApproved(sourceUrl, sourceAudioUrl, media);
    }

    void OpenSourceApproved(string sourceUrl, string sourceAudioUrl, BasisMediaSource media)
    {
        OpenStreams(sourceUrl, sourceAudioUrl);
        if (State != BmState.Opening)
            return;
        if (media.StartPosition > TimeSpan.Zero)
            _startAtSeconds = media.StartPosition.TotalSeconds;
        if (media.Metadata != null)
            ApplyMetadata(media.Metadata);
    }

    /// <summary>Re-open the current source from the start. A page URL goes
    /// back through the resolver, since the stream
    /// it produced may have expired; anything else re-opens as it was. With
    /// nothing loaded it does nothing. <see cref="ReopenAtPosition"/> is the
    /// settings path, which keeps the position.</summary>
    public void Reload()
    {
        if (string.IsNullOrEmpty(ActiveStreamUrl))
            return;
        if (!string.IsNullOrEmpty(url) && !string.Equals(url, ActiveStreamUrl, StringComparison.Ordinal))
        {
            LoadUrl(url);
            return;
        }
        Open(ActiveStreamUrl, string.IsNullOrEmpty(ActiveAudioStreamUrl) ? null : ActiveAudioStreamUrl);
    }

    /// <summary>The transport of what is open, from the stream's scheme:
    /// "http" (a file over HTTP), "hls", "rtsp", "rist", "whep" or "file";
    /// any other scheme as written; null with nothing open.</summary>
    public string CurrentTransport
    {
        get
        {
            string active = ActiveStreamUrl;
            if (string.IsNullOrEmpty(active))
                return null;
            if (!Uri.TryCreate(active, UriKind.Absolute, out Uri uri))
                return "file";
            string scheme = uri.Scheme.ToLowerInvariant();
            switch (scheme)
            {
                case "http":
                case "https":
                    return uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? "hls" : "http";
                case "rtsp":
                case "rtspt":
                    return "rtsp";
                default:
                    return scheme;
            }
        }
    }

    [NoAutoStaticsCleanup] static readonly BasisBitrateTrack[] NoBitrateTracks = Array.Empty<BasisBitrateTrack>();

    /// <summary>Always empty: the engine has no managed bitrate ladder (a
    /// resolver picks the rung before the open; the engine picks an HLS
    /// variant for itself).</summary>
    [Obsolete("The Rust engine has no managed bitrate ladder; BitrateTracks is always empty. This member only exists so content written against the C player keeps loading.")]
    public System.Collections.Generic.IReadOnlyList<BasisBitrateTrack> BitrateTracks => NoBitrateTracks;

    /// <summary>Always -1; see <see cref="BitrateTracks"/>.</summary>
    [Obsolete("The Rust engine has no managed bitrate ladder; SelectedBitrateIndex is always -1. This member only exists so content written against the C player keeps loading.")]
    public int SelectedBitrateIndex => -1;

    /// <summary>Always false; see <see cref="BitrateTracks"/>.</summary>
    [Obsolete("The Rust engine has no managed bitrate ladder; SelectBitrate does nothing and returns false. This member only exists so content written against the C player keeps loading.")]
    public bool SelectBitrate(int index) => false;

    /// <summary>Never raised; see <see cref="BitrateTracks"/>.</summary>
#pragma warning disable 67 // declared for compatibility, never raised
    [Obsolete("The Rust engine has no managed bitrate ladder; OnBitrateTrackChanged is never raised. This member only exists so content written against the C player keeps loading.")]
    public event Action<BasisBitrateTrack> OnBitrateTrackChanged;
#pragma warning restore 67

    /// <summary>Always false: the engine keeps no live buffer to step back
    /// into. A live source plays at its edge; an on-demand source takes
    /// <see cref="Seek(TimeSpan)"/>.</summary>
    [Obsolete("The Rust engine keeps no live DVR buffer; TrySeekBack does nothing and returns false. Seek an on-demand source instead. This member only exists so content written against the C player keeps loading.")]
    public bool TrySeekBack(TimeSpan back) => false;

    /// <summary>Does nothing: a resolver hands its answer to
    /// <see cref="OpenResolved"/>, which carries the stream, the liveness
    /// and the subtitle tracks that a <see cref="BasisMediaSource"/>
    /// cannot.</summary>
    [Obsolete("Resolvers open through OpenResolved(BasisResolvedMedia) on the Rust engine; LoadResolvedSource does nothing. This member only exists so content written against the C player keeps loading.")]
    public void LoadResolvedSource(BasisMediaSource media, int originatingGeneration)
    {
        BasisDebug.LogWarningOnce("[BasisMedia] LoadResolvedSource does nothing on this engine; resolvers call OpenResolved.", BasisDebug.LogTag.Video);
    }

    /// <summary>Write the current picture to a PNG under
    /// <c>Application.persistentDataPath</c>. Reads the
    /// output texture back from the GPU through a temporary render texture,
    /// encodes off the main thread, and reports the path or the failure back
    /// on the main thread. Call it from the main thread: the readback request
    /// needs it, and so does the synchronization context the completion is
    /// posted through. A path outside the data folder is refused.</summary>
    public void CaptureScreenshot(string relativePath = null, Action<string, Exception> onComplete = null)
    {
        Texture tex = Texture;
        if (tex == null || tex.width <= 0 || tex.height <= 0)
        {
            onComplete?.Invoke(null, new InvalidOperationException("no picture to capture yet"));
            return;
        }
        if (string.IsNullOrEmpty(relativePath))
            relativePath = $"Screenshots/basis_video_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}.png";
        if (!BasisMediaPlayerSecurity.TrySandboxLogPath(relativePath, out string fullPath, out string reason))
        {
            onComplete?.Invoke(null, new UnauthorizedAccessException(reason));
            return;
        }
        try
        {
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch (Exception e)
        {
            onComplete?.Invoke(null, e);
            return;
        }

        SynchronizationContext mainThread = SynchronizationContext.Current;
        if (mainThread == null)
        {
            onComplete?.Invoke(null, new InvalidOperationException("CaptureScreenshot must be called from the main thread"));
            return;
        }
        int width = tex.width, height = tex.height;
        // A readback hands rows back bottom-up, so a frame the engine wrote
        // top-down (Direct3D) needs flipping to encode upright; the field
        // inverts whatever the platform needs.
        bool flip = FlipVerticallyForScreenshot != OutputFrameIsTopLeftOrigin;
        // Through a temporary render texture. The engine's own texture is
        // BGRA sRGB on Direct3D and a Vulkan-written RenderTexture on Android,
        // and a readback cannot convert from either; the blit lands the
        // picture in RGBA sRGB, which it can read as it is.
        RenderTexture staging = RenderTexture.GetTemporary(
            width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(tex, staging);
        AsyncGPUReadback.Request(staging, 0, TextureFormat.RGBA32, request =>
        {
            RenderTexture.ReleaseTemporary(staging);
            if (request.hasError)
            {
                onComplete?.Invoke(null, new InvalidOperationException("the GPU readback failed"));
                return;
            }
            byte[] pixels;
            try
            {
                var data = request.GetData<byte>();
                pixels = new byte[data.Length];
                data.CopyTo(pixels);
            }
            catch (Exception e)
            {
                onComplete?.Invoke(null, e);
                return;
            }
            Task.Run(() =>
            {
                string resultPath = null;
                Exception resultError = null;
                try
                {
                    if (flip) FlipRowsRgba32(pixels, width, height);
                    byte[] png = ImageConversion.EncodeArrayToPNG(
                        pixels, UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_SRGB,
                        (uint)width, (uint)height);
                    File.WriteAllBytes(fullPath, png);
                    resultPath = fullPath;
                }
                catch (Exception e)
                {
                    resultError = e;
                }
                if (onComplete != null)
                    mainThread.Post(_ => onComplete(resultPath, resultError), null);
            });
        });
    }

    static void FlipRowsRgba32(byte[] data, int width, int height)
    {
        int stride = width * 4;
        var row = new byte[stride];
        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;
            Buffer.BlockCopy(data, top, row, 0, stride);
            Buffer.BlockCopy(data, bottom, data, top, stride);
            Buffer.BlockCopy(row, 0, data, bottom, stride);
        }
    }

    void OpenStreams(string sourceUrl, string sourceAudioUrl, bool reopen = false, bool announceMetadata = true)
    {
        // The load funnel: both public Open overloads, LoadUrl's direct path, a
        // resolver's OpenResolved and the re-open all land here, so the moderation
        // lock is enforced here rather than at each of them.
        if (BasisNetworkModeration.MediaPlayerBlockedLocally)
        {
            BasisDebug.LogWarning(
                "BasisMediaPlayer.Open blocked: media players are locked by an admin.",
                BasisDebug.LogTag.Video);
            return;
        }
        ClearPendingUrlApproval();
        audioUrl = sourceAudioUrl;
        if (!reopen)
        {
            LoadGeneration++;
            CancelLoad();
        }
        _startAtSeconds = -1d;
        // The track list belongs to a source. A re-open is the same source
        // and keeps its choice; anything else starts over, because a
        // remembered index means nothing against different content.
        _audioTracksRead = false;
        _audioTracks.Clear();
        if (!reopen) _audioTrackIndex = 0;
        // Whatever a resolver told us about the last source does not
        // describe this one. OpenResolved fills it back in, and raises
        // the change; a plain open just has nothing to say. A re-open is
        // the source it described.
        if (!reopen) Media = null;
        EndSession();
        if (Application.platform == RuntimePlatform.WebGLPlayer)
        {
            LastErrorMessage = "the media player is not available in the web player";
            BasisDebug.LogWarning($"[BasisMedia] {LastErrorMessage}", BasisDebug.LogTag.Video);
            State = BmState.Error;
            return;
        }
        if (!_abiChecked)
        {
            uint abi = BasisMediaNative.bm_abi_version();
            if (abi != BasisMediaNative.AbiVersion)
            {
                LastErrorMessage = $"basis_media ABI v{abi}, this package needs v{BasisMediaNative.AbiVersion}";
                BasisDebug.LogError($"[BasisMedia] {LastErrorMessage}; refusing.", BasisDebug.LogTag.Video);
                State = BmState.Error;
                return;
            }
            _abiChecked = true;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Vulkan)
        {
            LastErrorMessage = $"needs Vulkan on Android, running on {SystemInfo.graphicsDeviceType}";
            BasisDebug.LogError($"[BasisMedia] {LastErrorMessage}", BasisDebug.LogTag.Video);
            State = BmState.Error;
            return;
        }
#else
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11
            && SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
        {
            LastErrorMessage = $"needs Direct3D 11 or 12, running on {SystemInfo.graphicsDeviceType}";
            BasisDebug.LogError($"[BasisMedia] {LastErrorMessage}", BasisDebug.LogTag.Video);
            State = BmState.Error;
            return;
        }
#endif

        // The URL fields keep what was asked for; a re-open opens the stream
        // that answered it.
        if (!reopen) url = sourceUrl;
        ActiveStreamUrl = sourceUrl;
        ActiveAudioStreamUrl = audioUrl;
        byte[] descriptor = Encoding.UTF8.GetBytes(BuildDescriptor(sourceUrl));
        int rc = BasisMediaNative.bm_session_open(descriptor, (UIntPtr)descriptor.Length, out _handle);
        if (rc != 0)
        {
            LastErrorMessage = $"the media engine refused to open a session ({rc})";
            BasisDebug.LogError($"[BasisMedia] bm_session_open failed: {rc}", BasisDebug.LogTag.Video);
            State = BmState.Error;
            return;
        }
        _open = true;
        State = BmState.Opening;
        _seekCompletedPendingUs.Clear();
        _loopRestartAt = -1f;
        if (reopen)
        {
            // The same load: readiness, the sleep timer and the metadata carry
            // over, and so does a pause, which the poll issues to the new
            // session once it is buffering.
            _pauseIssued = false;
        }
        else
        {
            _reopenResumeAt = -1d;
            _restoreTargetUs = -1;
            PlayWhenReady = true;
            _pauseIssued = false;
            _readyThisLoad = false;
            _playingAnnounced = false;
            _firstFrameAnnounced = false;
            _metadataApplied = null;
            _playedSeconds = 0f;
            if (!AutoPlayOnSourceAssigned)
            {
                // Held on the first frame: the poll issues the pause once
                // the session is buffering, which is the earliest it can
                // take one.
                PlayWhenReady = false;
            }
        }
        System.Threading.Volatile.Write(ref _engineChannels, 0);
        System.Threading.Volatile.Write(ref _engineSampleRate, 0);
        System.Threading.Volatile.Write(ref _syncRatePpm, 0);
        System.Threading.Volatile.Write(ref _avOffsetUs, int.MinValue);
        // Re-resolved here as well as in Awake, so a rig that adds the sink
        // after the player still gets sound.
        if (_audio == null) TryGetComponent(out _audio);
        if (_audio != null) _audio.NativePcmSource = this;
        _sentAudioLatencyUs = -1;
        _commandBuffer ??= new CommandBuffer { name = "BasisMedia present" };
        if (!reopen && announceMetadata) OnMetadataChanged?.Invoke(Metadata);
    }

    static void AppendJsonString(StringBuilder json, string value)
    {
        json.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': json.Append("\\\""); break;
                case '\\': json.Append("\\\\"); break;
                default:
                    if (c < ' ') json.Append($"\\u{(int)c:x4}");
                    else json.Append(c);
                    break;
            }
        }
        json.Append('"');
    }

    string BuildDescriptor(string sourceUrl)
    {
        var json = new StringBuilder(sourceUrl.Length + 96);
        json.Append("{\"url\":");
        AppendJsonString(json, sourceUrl);
        if (!string.IsNullOrEmpty(audioUrl))
        {
            json.Append(",\"audio_url\":");
            AppendJsonString(json, audioUrl);
        }
        if (allowLocalAddresses)
            json.Append(",\"allow_local_addresses\":true");
        int depthMs = EffectiveBufferDepthMs;
        if (depthMs > 0)
            json.Append($",\"buffer_depth_ms\":{depthMs}");
        if (liveness == BmLiveness.Live)
            json.Append(",\"liveness\":\"live\"");
        else if (liveness == BmLiveness.Vod)
            json.Append(",\"liveness\":\"vod\"");
        if (_audioTrackIndex > 0)
            json.Append($",\"audio_track\":{_audioTrackIndex}");
        if (maxDivergenceMs > 0)
            json.Append($",\"max_divergence_ms\":{maxDivergenceMs}");
        if (DecodePreference == BmDecodePreference.HardwareOnly)
            json.Append(",\"decode_preference\":\"hardware_only\"");
        else if (DecodePreference == BmDecodePreference.SoftwareOnly)
            json.Append(",\"decode_preference\":\"software_only\"");
        if (engineCapture)
        {
            string path = BasisMediaCapturePath.Resolve(
                engineCaptureFileName, "BasisMediaEngine.csv", out string refusal);
            if (path == null)
            {
                BasisDebug.LogWarning($"[BasisMedia] engine capture refused: {refusal}", BasisDebug.LogTag.Video);
            }
            else
            {
                EngineCapturePath = path;
                json.Append(",\"diag_csv\":");
                AppendJsonString(json, path);
                if (engineCaptureAppend)
                    json.Append(",\"diag_csv_append\":true");
            }
        }
        json.Append('}');
        return json.ToString();
    }

    /// <summary>Play, or resume after <see cref="Pause"/>. While a consent
    /// prompt is up, playback starts once the user accepts. With nothing
    /// open and no prompt it does nothing lasting: an open takes its intent
    /// from <see cref="AutoPlayOnSourceAssigned"/>.</summary>
    public void Play()
    {
        PlayWhenReady = true;
        _pauseIssued = false;
        if (_urlApprovalPending)
        {
            _playRequestedWhileApprovalPending = true;
            return;
        }
        if (_open) BasisMediaNative.bm_session_play(_handle);
    }

    /// <summary>The same as <see cref="Play"/>.</summary>
    public void Resume() => Play();

    /// <summary>Pause an on-demand session. The engine holds the pause
    /// across a seek and declines it on a live source. Asked before the
    /// session can take it (while it is still opening), the pause is
    /// issued once it can.</summary>
    public void Pause()
    {
        if (!_open)
            return;
        PlayWhenReady = false;
        _pollsPlayingAgainstPause = 0;
        _pauseIssued = State != BmState.Opening;
        if (_pauseIssued) BasisMediaNative.bm_session_pause(_handle);
    }

    /// <summary>Pause if playback is meant to run, otherwise play.</summary>
    public void TogglePause()
    {
        if (PlayWhenReady) Pause();
        else Play();
    }

    // Raised when a seek is issued, with the requested position in seconds.
    // Shared playback broadcasts on this, so a seek from a world script
    // reaches the other viewers the same way one from the menu does.
    internal event Action<double> OnSeeked;

    /// <summary>Seek an on-demand source; a live one refuses. A negative
    /// position seeks to the start. <see cref="OnSeekCompleted"/> reports
    /// when the engine has acted on it.</summary>
    public void Seek(TimeSpan position) =>
        Seek(position < TimeSpan.Zero ? 0d : position.TotalSeconds);

    internal void Seek(double seconds)
    {
        if (!_open)
            return;
        if (VerboseLogging)
            BasisDebug.Log($"[BasisMedia] {name}: seek to {seconds:F3}s", BasisDebug.LogTag.Video);
        // A viewer's seek ends any restoration, whether still to be issued
        // or in flight; the re-open must not override where they went.
        _reopenResumeAt = -1d;
        _restoreTargetUs = -1;
        IssueSeek(seconds);
        OnSeeked?.Invoke(seconds);
    }

    // The seek and the resets a new timeline needs, with no OnSeeked: a seek
    // the player makes for itself (returning to its place after a reopen) is
    // not the viewer's, and shared playback must not replicate it.
    void IssueSeek(double seconds)
    {
        BasisMediaNative.bm_session_seek(_handle, (long)(seconds * 1_000_000.0));
        // Audio still in the sink's window belongs to the timeline being
        // left behind; playing it out would be heard against the landed
        // picture.
        _audio?.ResetSyncAnchor();
        // Queued in-band cues are stamped against the old timeline, so
        // they would either flush in a burst or sit undue past a backwards
        // seek. The engine emits its own clear at the landed position.
        ClearCaptionDisplay();
        ClearUserData();
    }

    // The re-open's restore target until the engine has acted on the seek (or
    // a bound passes): a position read meanwhile is the new session's start.
    long _restoreTargetUs = -1;
    int _restoreSeekActedOnBefore;
    float _restoreIssuedAt;
    const float RestoreSeekBoundSeconds = 10f;

    /// <summary>A re-open is returning the session to where it was and the
    /// engine has not landed there yet, so <see cref="Position"/>
    /// reads the new session's start rather than the viewer's place.
    /// <see cref="RestoringToSeconds"/> is that place.</summary>
    public bool RestoringPosition => _reopenResumeAt >= 0d || _restoreTargetUs >= 0;

    /// <summary>Where a re-open is returning the session to, while
    /// <see cref="RestoringPosition"/>; otherwise the position.</summary>
    public double RestoringToSeconds
    {
        get
        {
            if (_reopenResumeAt >= 0d) return _reopenResumeAt;
            if (_restoreTargetUs >= 0) return _restoreTargetUs / 1e6;
            return PositionSeconds;
        }
    }

    /// <summary>
    /// Feed the shared-playback owner's position (seconds) as a soft
    /// sync target. The engine corrects with dead band → gentle rate
    /// slew → seek only past a large threshold, and extrapolates the
    /// target at 1x between calls, so one call per received heartbeat
    /// is enough. Live sources ignore targets (divergence is bounded
    /// by <see cref="maxDivergenceMs"/> instead).
    /// </summary>
    internal void SetSyncTarget(double seconds)
    {
        if (_open) BasisMediaNative.bm_session_set_sync_target(_handle, (long)(seconds * 1_000_000.0));
    }

    /// <summary>Stop chasing a sync target (local user took control,
    /// or the owner left).</summary>
    internal void ClearSyncTarget()
    {
        if (_open) BasisMediaNative.bm_session_set_sync_target(_handle, -1);
    }

    /// <summary>The sync ladder's current rate offset from 1x, ppm
    /// (0 = none). Applied by the audio pull automatically; exposed
    /// for diagnostics/UI.</summary>
    public int SyncRatePpm => System.Threading.Volatile.Read(ref _syncRatePpm);

    /// <summary>Presented video pts minus the audio playhead, microseconds,
    /// as the engine measures it (both read on one engine tick, so it is a
    /// difference rather than two samples taken a frame apart).
    /// <c>int.MinValue</c> until audio and a presented frame both exist.
    /// Diagnostics only.</summary>
    public int AvOffsetUs => System.Threading.Volatile.Read(ref _avOffsetUs);

    /// <summary>End the session: the picture is dropped, the position
    /// forgotten and a resolve still running cancelled. <see cref="LoadUrl"/>
    /// starts afresh.</summary>
    public void Stop()
    {
        // A resolve still running belongs to a load the user has ended: its
        // result must not re-open the player, and its work can stop.
        LoadGeneration++;
        CancelLoad();
        EndSession();
    }

    /// <summary>End the session and clear what described it. The load's
    /// identity (its URLs, generation and any pending resolve) is the
    /// caller's: <see cref="Stop"/> ends the load too, a re-open keeps
    /// it.</summary>
    void EndSession()
    {
        ClearPendingUrlApproval();
        ReleaseSession();
        State = BmState.Idle;
        LastErrorMessage = null;
        // The per-session engine readings describe a session that no longer
        // exists. The open path clears them only after `bm_session_open`
        // succeeds. Without this, a close, or an open that fails before that
        // point, would leave the previous session's values for the diagnostics
        // recorder to write into its capture for an idle player. `OpenStreams`
        // closes first and is covered too. A moderation-blocked open returns
        // before `Stop` and deliberately leaves a still-playing session's
        // readings alone.
        System.Threading.Volatile.Write(ref _engineChannels, 0);
        System.Threading.Volatile.Write(ref _engineSampleRate, 0);
        System.Threading.Volatile.Write(ref _syncRatePpm, 0);
        System.Threading.Volatile.Write(ref _avOffsetUs, int.MinValue);
        _eventsDroppedSeen = 0;
        bool hadTexture = _textureShown || _textureIsArtwork;
        SetOutputTexture(null);
        _textureIsArtwork = false;
        _textureShown = false;
        _artworkRead = false;
        if (_artwork != null)
        {
            Destroy(_artwork);
            _artwork = null;
        }
        VideoSize = Vector2Int.zero;
        _subtitleSelection++;
        _subtitles.Clear();
        if (SelectedSubtitleTrackIndex != -1)
        {
            SelectedSubtitleTrackIndex = -1;
            OnSubtitleTrackChanged?.Invoke(-1);
        }
        ClearCaptionDisplay();
        ClearUserData();
        if (hadTexture)
            OnOutputTextureChanged?.Invoke(null);
    }

    /// <summary>
    /// End the native session and stop everything that drives it. The
    /// player's state, error and output texture are left for the caller, so
    /// a session that failed still shows why until the next open or close.
    /// </summary>
    void ReleaseSession()
    {
        // Before the handle goes: the sink pulls on the audio thread, and
        // dropping the source is what stops it.
        if (_audio != null && ReferenceEquals(_audio.NativePcmSource, this))
            _audio.NativePcmSource = null;
        if (_open)
        {
            BasisMediaNative.bm_session_close(_handle);
            _open = false;
        }
#if UNITY_ANDROID && !UNITY_EDITOR
        if (_renderHooked)
        {
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            _renderHooked = false;
        }
#endif
    }

    /// <summary>
    /// Install <paramref name="texture"/> as the output and dispose of whatever
    /// it replaces.
    ///
    /// On Vulkan the plugin holds an image view over the texture it was
    /// registered with and can only destroy it from a later render event, so
    /// the image has to stay alive past the call that ends the registration
    /// (a close or a replacement alike), and the retirement queue holds it.
    /// Elsewhere the plugin owns nothing that outlives the session, so the
    /// texture is destroyed here; Unity releases the graphics resource through
    /// the render command queue, which orders it after the events already
    /// issued for it. A replacement while the session runs (the video changed
    /// size) goes through the same path.
    /// </summary>
    void SetOutputTexture(Texture texture)
    {
        Texture previous = _texture;
        _texture = texture;
        // The cover art has an owner already: Stop destroys it by name, and it
        // is the one texture here the session never drew into.
        if (previous == null || ReferenceEquals(previous, texture)
            || ReferenceEquals(previous, _artwork))
            return;
#if UNITY_ANDROID && !UNITY_EDITOR
        BasisMediaTextureRetirement.Retire(previous);
#else
        Destroy(previous);
#endif
    }

    void Update() => _frameTick.RunFromUpdate();

    /// <summary>
    /// The frame's work for this player and for everything wired to it: the
    /// engine poll first, then the components that read what it wrote, in the
    /// order they depend on each other. Left to their own Update methods they
    /// have no order between them, so a consumer could describe the previous
    /// frame's snapshot instead of this one's.
    /// </summary>
    void Tick()
    {
        // Process-wide and idempotent within a frame: the engine's free-text
        // log is one queue for the whole plugin, so every player pumps it and
        // the first through each frame takes the lot.
        BasisMediaLogDrain.Pump();
        PushVolumeToSink();
        PollSession();
        BasisMediaTickLoop.Run(_consumers, _consumersRan);
    }

    /// <summary>Register for the ordered tick. From the consumer's OnEnable;
    /// it is dropped again from OnDisable.</summary>
    internal void AddTickConsumer(IBasisMediaTickConsumer consumer)
    {
        if (consumer == null || _consumers.Contains(consumer))
            return;
        int at = _consumers.Count;
        while (at > 0 && _consumers[at - 1].TickStage > consumer.TickStage)
            at--;
        _consumers.Insert(at, consumer);
    }

    internal void RemoveTickConsumer(IBasisMediaTickConsumer consumer) => _consumers.Remove(consumer);

    void PollSession()
    {
        if (!_open)
            return;

        if (BasisMediaNative.bm_session_poll(_handle, out var snapshot) != 0)
            return;
        BmState previousState = State;
        State = (BmState)snapshot.State;
        if (VerboseLogging && State != previousState)
            BasisDebug.Log($"[BasisMedia] {name}: {previousState} -> {State}", BasisDebug.LogTag.Video);
        ErrorCode = snapshot.ErrorCode;
        if (snapshot.Width > 0)
            VideoSize = new Vector2Int((int)snapshot.Width, (int)snapshot.Height);
        PositionSeconds = snapshot.PositionUs / 1e6;
        DurationSeconds = snapshot.DurationUs / 1e6;
        BankedMilliseconds = snapshot.BankedMs;
        FramesDecoded = snapshot.FramesDecoded;
        PresentedFrameCount = (long)snapshot.FramesPresented;
        System.Threading.Volatile.Write(ref _engineChannels, (int)snapshot.AudioChannels);
        System.Threading.Volatile.Write(ref _engineSampleRate, (int)snapshot.AudioSampleRate);
        System.Threading.Volatile.Write(ref _syncRatePpm, snapshot.SyncRatePpm);
        System.Threading.Volatile.Write(ref _avOffsetUs, snapshot.AvOffsetUs);
        if (_audio != null && snapshot.AudioSampleRate > 0 && snapshot.AudioChannels > 0)
            _audio.SetExpectedFormat((int)snapshot.AudioSampleRate, (int)snapshot.AudioChannels);

        // A/V output-latency compensation: the engine masters the clock
        // on the pull playhead, but audible audio leaves the speaker one
        // DSP output chain later. Report the sink's estimate so video
        // paces to the audible position. With no sink or no audio track
        // there is no audio master to compensate.
        long latencyUs = _audio != null
            && snapshot.AudioSampleRate > 0
            && snapshot.AudioChannels > 0
            ? _audio.EstimatedOutputLatencyUs
            : 0;
        if (latencyUs != _sentAudioLatencyUs)
        {
            BasisMediaNative.bm_session_set_audio_latency(_handle, latencyUs);
            _sentAudioLatencyUs = latencyUs;
        }

        DrainEvents();
        ReportDroppedEvents(snapshot.EventsDropped);
        if (_seekCompletedPendingUs.Count > 0)
        {
            for (int i = 0; i < _seekCompletedPendingUs.Count; i++)
                OnSeekCompleted?.Invoke(TimeSpan.FromSeconds(_seekCompletedPendingUs[i] / 1e6));
            _seekCompletedPendingUs.Clear();
        }
        DrainCaptions(snapshot.PositionUs);
        DrainUserData(snapshot.PositionUs);
        RefreshAudioTracks();
        RefreshArtwork(snapshot);

        // A track switch re-opened the session; return to where playback
        // was once the new one can accept a seek.
        if (_reopenResumeAt >= 0d && DurationSeconds > 0d
            && (State == BmState.Playing || State == BmState.Buffering))
        {
            double resume = _reopenResumeAt;
            _reopenResumeAt = -1d;
            _restoreTargetUs = (long)(resume * 1_000_000.0);
            _restoreSeekActedOnBefore = SeeksActedOn;
            _restoreIssuedAt = Time.unscaledTime;
            IssueSeek(resume);
        }
        if (_restoreTargetUs >= 0
            && (SeekAnsweredSince(_restoreSeekActedOnBefore, _restoreTargetUs, 1_000)
                || Time.unscaledTime - _restoreIssuedAt > RestoreSeekBoundSeconds))
        {
            _restoreTargetUs = -1;
        }

        if ((State == BmState.Playing || State == BmState.Paused) && !_readyThisLoad)
        {
            _readyThisLoad = true;
            OnReady?.Invoke();
        }
        if (State == BmState.Playing && !_playingAnnounced)
        {
            _playingAnnounced = true;
            OnStarted?.Invoke();
        }
        if (State == BmState.Paused && previousState != BmState.Paused)
        {
            _playingAnnounced = false;
            OnPaused?.Invoke();
        }
        if (PresentedFrameCount > 0 && !_firstFrameAnnounced)
        {
            _firstFrameAnnounced = true;
            OnFirstFrameReady?.Invoke();
        }

        if (!PlayWhenReady)
        {
            if (!_pauseIssued && _open && (State == BmState.Buffering || State == BmState.Playing))
            {
                // A pause asked while the session was still opening, or a
                // source meant to open paused: the session can take it now.
                _pauseIssued = true;
                _pollsPlayingAgainstPause = 0;
                BasisMediaNative.bm_session_pause(_handle);
            }
            else if (State == BmState.Playing)
            {
                // The engine declines a pause on a live source and keeps
                // playing. A session still playing well after the request is
                // the truth; a pause that lands takes a frame or two.
                if (++_pollsPlayingAgainstPause > DeclinedPausePolls)
                    PlayWhenReady = true;
            }
            else
            {
                _pollsPlayingAgainstPause = 0;
            }
        }

        // A start position from LoadSource, once the session can take a seek.
        if (_startAtSeconds >= 0d && DurationSeconds > 0d
            && (State == BmState.Playing || State == BmState.Buffering))
        {
            double startAt = _startAtSeconds;
            _startAtSeconds = -1d;
            Seek(startAt);
        }

        if (State == BmState.Ended && previousState != BmState.Ended)
        {
            OnEnded?.Invoke();
            if (Loop && DurationSeconds > 0d)
                _loopRestartAt = Time.unscaledTime + Mathf.Max(0f, LoopRestartDelaySeconds);
        }
        if (State != BmState.Ended)
        {
            _loopRestartAt = -1f;
        }
        else if (_loopRestartAt >= 0f && Time.unscaledTime >= _loopRestartAt)
        {
            // An ended session takes a seek and resumes into buffering at the
            // new position, so looping is a seek to the start.
            _loopRestartAt = -1f;
            if (VerboseLogging)
                BasisDebug.Log($"[BasisMedia] {name}: looping", BasisDebug.LogTag.Video);
            Seek(0d);
            OnLooped?.Invoke();
            // A sink that cleared itself on OnEnded re-binds on this; the
            // session keeps drawing into the same texture across the seek.
            if (_textureShown && _texture != null)
                OnOutputTextureChanged?.Invoke(_texture);
        }

        if (StopAfterSeconds > 0f && State == BmState.Playing)
        {
            _playedSeconds += Time.unscaledDeltaTime;
            if (_playedSeconds >= StopAfterSeconds)
            {
                if (VerboseLogging)
                    BasisDebug.Log($"[BasisMedia] {name}: sleep timer elapsed, stopping", BasisDebug.LogTag.Video);
                Stop();
                return;
            }
        }

        if (State == BmState.Error)
        {
            string why = string.IsNullOrEmpty(LastErrorMessage) ? "no detail reported" : LastErrorMessage;
            BasisDebug.LogError(
                $"[BasisMedia] session error {snapshot.ErrorCode} " +
                $"({(BmErrorCategory)snapshot.ErrorCategory}): {why} [{url}]",
                BasisDebug.LogTag.Video);
            OnError?.Invoke(new InvalidOperationException(
                $"media session error {snapshot.ErrorCode} ({(BmErrorCategory)snapshot.ErrorCategory}): {why}"));
            // Nothing ticks this session again, so what is queued would hold
            // its pooled buffers until the next open.
            ClearUserData();
            ReleaseSession();
            return;
        }

        // The engine rebuilds its decoder when a source changes size (a
        // reconnect, an encoder change, a variant switch), and the output has
        // to follow: Direct3D refuses to copy into a texture of another size,
        // and the Vulkan pass would smear or crop.
        // Art shown before the frame size was known makes way for the video at
        // its first frame.
        bool engineTexture = _texture != null && !ReferenceEquals(_texture, _artwork);
        bool resized = engineTexture && snapshot.Width > 0
            && (_texture.width != (int)snapshot.Width || _texture.height != (int)snapshot.Height);
        if (!engineTexture && snapshot.Width > 0)
            _textureShown = false;
        if ((!engineTexture || resized) && snapshot.Width > 0)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Vulkan graphics contract (normative in the ABI header):
            // RGBA32 RenderTexture with the default read/write at display
            // size, so sRGB in a Linear project and sampled back to linear
            // light like the Direct3D texture. The engine's convert pass
            // writes rows in Unity's Vulkan sampling orientation, so no
            // material flip.
            var target = new RenderTexture((int)snapshot.Width, (int)snapshot.Height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
            target.Create();
            SetOutputTexture(target);
            BasisMediaNative.bm_session_set_output_texture(_handle, target.GetNativeTexturePtr());
#else
            var texture = new Texture2D((int)snapshot.Width, (int)snapshot.Height, TextureFormat.BGRA32, false);
            SetOutputTexture(texture);
            BasisMediaNative.bm_session_set_output_texture(_handle, texture.GetNativeTexturePtr());
#endif
            // Sinks still hold the texture just retired, so they move now
            // rather than wait for a first frame they have already seen.
            if (resized && _textureShown)
                OnOutputTextureChanged?.Invoke(_texture);
        }

        if (_texture != null && !_textureShown && snapshot.FramesPresented > 0)
        {
            _textureShown = true;
            _textureIsArtwork = false;
            OnOutputTextureChanged?.Invoke(_texture);
        }

        if (_texture != null && !ReferenceEquals(_texture, _artwork))
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Camera.AddCommandBuffer is silently ignored under URP;
            // the render event rides the pipeline's per-camera callback
            // instead, once per frame after the first camera finishes.
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                if (!_renderHooked)
                {
                    _commandBuffer.Clear();
                    _commandBuffer.IssuePluginEventAndData(BasisMediaNative.bm_render_event_func(),
                        BasisMediaNative.RenderEventPresent, (IntPtr)_handle);
                    RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                    _renderHooked = true;
                }
            }
            else
            {
                _commandBuffer.Clear();
                _commandBuffer.IssuePluginEventAndData(BasisMediaNative.bm_render_event_func(),
                    BasisMediaNative.RenderEventPresent, (IntPtr)_handle);
                Graphics.ExecuteCommandBuffer(_commandBuffer);
            }
#else
            _commandBuffer.Clear();
            _commandBuffer.IssuePluginEventAndData(BasisMediaNative.bm_render_event_func(),
                BasisMediaNative.RenderEventPresent, (IntPtr)_handle);
            Graphics.ExecuteCommandBuffer(_commandBuffer);
#endif
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!_open || _texture == null || !isActiveAndEnabled)
            return;
        if (_lastRenderEventFrame == Time.frameCount)
            return;
        _lastRenderEventFrame = Time.frameCount;
        Graphics.ExecuteCommandBuffer(_commandBuffer);
    }
#endif

    /// Refused events already accounted for, so a session's running total
    /// is reported as it grows rather than every frame it stays non-zero.
    uint _eventsDroppedSeen;

    /// The engine's log has a cap, and what it refuses is a hole in the
    /// drained sequence. Silence here would leave the log looking whole.
    void ReportDroppedEvents(uint total)
    {
        if (total <= _eventsDroppedSeen) return;
        uint lost = total - _eventsDroppedSeen;
        _eventsDroppedSeen = total;
        BasisDebug.LogWarning(
            $"[BasisMedia] diagnostics log full: {lost} event(s) refused, {total} this session",
            BasisDebug.LogTag.Video);
    }

    /// Events per drain call. A frame that opens a session or loses a
    /// transport produces a burst, and what the engine holds beyond one
    /// batch has to be asked for again, so the drain loops.
    const int EventDrainBatch = 64;

    /// Ceiling on one tick's drain. The engine's log holds 1024, and every
    /// record costs a UTF-8 decode and a Console line, so a full queue taken
    /// in one frame is a visible hitch. Nothing is lost by stopping short:
    /// the drain leaves what it cannot carry, and the rest arrives over the
    /// next few ticks.
    const int EventDrainPerTick = 256;

    unsafe void DrainEvents()
    {
        var events = stackalloc BmEvent[EventDrainBatch];
        int drained = 0;
        int count;
        do
        {
            count = BasisMediaNative.bm_session_drain_events(_handle, events, EventDrainBatch);
            for (int i = 0; i < count; i++)
            {
                string detail = Encoding.UTF8.GetString(events[i].Detail, (int)events[i].DetailLen);
                // The snapshot carries only a number; the reason travels in
                // these two events.
                if (events[i].Code == (uint)BmEventCode.Error || events[i].Code == (uint)BmEventCode.CodecRefused)
                    LastErrorMessage = detail;
                if (events[i].Code == (uint)BmEventCode.Seek && events[i].Stage == (uint)BmStage.Demux)
                {
                    long targetUs = ParseSeekTargetUs(detail);
                    _seekAnswersUs[SeeksActedOn % _seekAnswersUs.Length] = targetUs;
                    SeeksActedOn++;
                    // Raised once the drain is done, so a handler that seeks
                    // again does not do so from inside the engine's queue.
                    if (targetUs >= 0 && !detail.Contains("refused"))
                        _seekCompletedPendingUs.Add(targetUs);
                }
                // WallUs is the session's own monotonic clock, so a line can be
                // lined up against either diagnostics CSV without hand-aligning.
                string at = (events[i].WallUs / 1_000_000.0).ToString("F3", CultureInfo.InvariantCulture);
                BasisDebug.Log(
                    $"[BasisMedia +{at}s] {(BmEventCode)events[i].Code}/{(BmStage)events[i].Stage}: {detail}",
                    BasisDebug.LogTag.Video);
            }
            if (count > 0) drained += count;
            // A short batch is the queue's end. A negative is an error code,
            // which ends the loop the same way.
        } while (count == EventDrainBatch && drained < EventDrainPerTick);
    }

    /// <summary>The target in the engine's seek event ("to 15000000us,
    /// landed …" or "to 15000000us, refused: …"), or -1.</summary>
    static long ParseSeekTargetUs(string detail)
    {
        const string prefix = "to ";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal))
            return -1;
        int end = detail.IndexOf("us", prefix.Length, StringComparison.Ordinal);
        return end > prefix.Length
            && long.TryParse(detail.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out long us)
            ? us
            : -1;
    }

    /// <summary>Fetch the container's cover art once the session has opened.
    /// The engine hands over the stored JPEG/PNG bytes; Unity decodes them,
    /// which is why no image parser lives in native code.</summary>
    unsafe void RefreshArtwork(BmSnapshot snapshot)
    {
        if (_artworkRead) return;
        int len = BasisMediaNative.bm_session_artwork_len(_handle);
        if (len <= 0)
        {
            // 0 is "no art"; only settle once the container has been read,
            // since the answer is not known while still opening.
            if (State != BmState.Opening) _artworkRead = true;
            return;
        }
        _artworkRead = true;

        var data = new byte[len];
        var mime = new byte[64];
        int got;
        fixed (byte* d = data)
        fixed (byte* m = mime)
        {
            got = BasisMediaNative.bm_session_get_artwork(_handle, d, (uint)data.Length, m, (uint)mime.Length);
        }
        if (got <= 0)
        {
            BasisDebug.LogWarning($"[BasisMedia] cover art present but unreadable ({got})", BasisDebug.LogTag.Video);
            return;
        }

        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!texture.LoadImage(data))
        {
            // A container can state any MIME it likes; Unity decodes PNG
            // and JPEG, and anything else is simply not shown.
            int end = System.Array.IndexOf(mime, (byte)0);
            string stated = Encoding.UTF8.GetString(mime, 0, end < 0 ? mime.Length : end);
            BasisDebug.Log($"[BasisMedia] cover art in an undecodable format ({stated})", BasisDebug.LogTag.Video);
            Destroy(texture);
            return;
        }
        _artwork = texture;

        // A source with pictures of its own owns the output; art fills it
        // until there is video to show.
        if (snapshot.Width == 0 && _texture == null)
        {
            SetOutputTexture(_artwork);
            _textureIsArtwork = true;
            _textureShown = true;
            VideoSize = new Vector2Int(_artwork.width, _artwork.height);
            OnOutputTextureChanged?.Invoke(_texture);
        }
    }

    // The engine learns the track list when it opens the container, which
    // is after the session handle exists, so this polls until the list
    // arrives, then stops: one integer call per frame until the container
    // is parsed, none afterwards.
    unsafe void RefreshAudioTracks()
    {
        if (_audioTracksRead) return;
        int count = BasisMediaNative.bm_session_audio_track_count(_handle);
        if (count <= 0)
        {
            // Nothing to offer yet, or nothing to offer at all; settle
            // once the session is past opening.
            if (State != BmState.Opening) _audioTracksRead = true;
            return;
        }

        var records = stackalloc BmAudioTrack[16];
        int got = BasisMediaNative.bm_session_get_audio_tracks(_handle, records, 16);
        if (got <= 0) return;

        _audioTracks.Clear();
        for (int i = 0; i < got; i++)
        {
            _audioTracks.Add(new BasisAudioTrack
            {
                Index = i,
                TrackId = (int)records[i].TrackId,
                SampleRate = (int)records[i].SampleRate,
                ChannelCount = (int)records[i].Channels,
                Language = records[i].LanguageLen > 0
                    ? Encoding.UTF8.GetString(records[i].Language, (int)records[i].LanguageLen)
                    : null,
                Label = records[i].LabelLen > 0
                    ? Encoding.UTF8.GetString(records[i].Label, (int)records[i].LabelLen)
                    : null,
            });
        }
        // A remembered index that this source cannot honour: the engine
        // already fell back to the first track, so agree with it.
        if (_audioTrackIndex >= _audioTracks.Count) _audioTrackIndex = 0;
        _audioTracksRead = true;
        RaiseAudioTrackChanged();
    }

    // Cues arrive ahead of presentation stamped with their due PTS; hold
    // them until the playback position reaches each one, so captions stay
    // in lockstep with the frame they belong to regardless of the decode
    // buffer's lead.
    unsafe void DrainCaptions(long positionUs)
    {
        var cues = stackalloc BmCaption[8];
        int count = BasisMediaNative.bm_session_drain_captions(_handle, cues, 8);
        for (int i = 0; i < count; i++)
        {
            string text = cues[i].TextLen > 0
                ? Encoding.UTF8.GetString(cues[i].Text, (int)cues[i].TextLen)
                : "";
            _captionQueue.Enqueue((cues[i].PtsUs, text));
        }

        // A selected sidecar track replaces the in-band feed. The engine
        // ring is still drained above so it can't back up behind the
        // selection; the cues themselves are dropped.
        if (SelectedSubtitleTrackIndex >= 0)
        {
            _captionQueue.Clear();
            if (_subtitles.TryGetCueChange(positionUs, out BasisCaptionCue cue))
                SetCaption(cue.Text, cue.StartUs, cue.EndUs);
            return;
        }

        string due = null;
        long dueAtUs = 0;
        while (_captionQueue.Count > 0 && _captionQueue.Peek().ptsUs <= positionUs)
            (dueAtUs, due) = _captionQueue.Dequeue();
        if (due != null)
            SetCaption(due, dueAtUs, dueAtUs);
    }

    // The engine ring is drained every tick and everything drained is held
    // until the playback position reaches it, subscriber or not. An
    // on-demand open banks seconds of media before the first frame shows,
    // so what the engine hands over here is mostly still in the future; a
    // subscriber that attaches a frame after Open (a component whose
    // Update runs after this one's, say) must see all of it. Without a
    // subscriber a message is dropped only as it falls due.
    unsafe void DrainUserData(long positionUs)
    {
        var handler = OnUserDataReceived;
        _userDataBytes ??= new byte[UserDataBytesCapacity];

        var records = stackalloc BmUserData[UserDataRecordsPerDrain];
        int count;
        fixed (byte* bytes = _userDataBytes)
        {
            count = BasisMediaNative.bm_session_drain_user_data(
                _handle, records, UserDataRecordsPerDrain, bytes, UserDataBytesCapacity);
        }
        for (int i = 0; i < count; i++)
        {
            int length = (int)records[i].Len;
            long ptsUs = records[i].PtsUs;
            // A backwards jump past the engine's 1 s reordering slack is a
            // new timeline (a loop, or a discontinuity the engine did not
            // flag); what is queued from the old one would sit undue in
            // front of it. Within the slack it is B-frame reordering, and
            // the ordered insert below puts it where it belongs.
            if (_userDataPending.Count > 0
                && _userDataMaxPtsUs != long.MinValue
                && ptsUs < _userDataMaxPtsUs - 1_000_000)
                ClearUserData();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(length, 1));
            Buffer.BlockCopy(_userDataBytes, (int)records[i].Offset, buffer, 0, length);
            // After every entry with the same or an earlier timestamp, so
            // equal timestamps keep their stream order. Walks from the
            // back: the common case lands on the end.
            int at = _userDataPending.Count;
            while (at > 0 && _userDataPending[at - 1].ptsUs > ptsUs)
                at--;
            _userDataPending.Insert(at, (ptsUs, GuidFromRfc4122(records[i].Uuid), buffer, length));
            if (ptsUs > _userDataMaxPtsUs)
                _userDataMaxPtsUs = ptsUs;
        }

        // One removal for the whole due run: a catch-up tick after the
        // open-time burst can have hundreds due, and shifting the list
        // once per entry would make that quadratic.
        int due = 0;
        while (due < _userDataPending.Count && _userDataPending[due].ptsUs <= positionUs)
            due++;
        if (due == 0)
            return;
        _userDataDue.Clear();
        for (int i = 0; i < due; i++)
            _userDataDue.Add(_userDataPending[i]);
        _userDataPending.RemoveRange(0, due);
        int timeline = _userDataTimeline;
        for (int i = 0; i < _userDataDue.Count; i++)
        {
            var (ptsUs, uuid, buffer, length) = _userDataDue[i];
            // A handler that seeks or closes has left this timeline; what
            // is still due belongs to it and is not delivered. With no
            // handler at all, due messages are simply let go.
            if (handler == null || _userDataTimeline != timeline)
            {
                ArrayPool<byte>.Shared.Return(buffer);
                continue;
            }
            try
            {
                handler?.Invoke(ptsUs, uuid, new ReadOnlySpan<byte>(buffer, 0, length));
            }
            catch (Exception e)
            {
                BasisDebug.LogErrorOnce($"[BasisMedia] user data handler failed: {e}", BasisDebug.LogTag.Video);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        _userDataDue.Clear();
    }

    /// <summary>
    /// A UUID as the wire carries it (RFC 4122, big-endian fields) as a
    /// <see cref="Guid"/> whose text form matches, so
    /// <c>Guid.Parse("b1f0a7d4-...")</c> compares equal to the UUID an
    /// encoder wrote as those bytes. <c>new Guid(byte[])</c> would not: it
    /// reads the first three fields little-endian. Exactly 16 bytes.
    /// </summary>
    public static unsafe Guid GuidFromRfc4122(ReadOnlySpan<byte> uuid)
    {
        if (uuid.Length != 16)
            throw new ArgumentException("a UUID is 16 bytes", nameof(uuid));
        fixed (byte* p = uuid)
            return GuidFromRfc4122(p);
    }

    // The pointer form the drain uses on the record's fixed buffer; the
    // caller vouches for 16 readable bytes.
    static unsafe Guid GuidFromRfc4122(byte* uuid)
    {
        int a = uuid[0] << 24 | uuid[1] << 16 | uuid[2] << 8 | uuid[3];
        short b = (short)(uuid[4] << 8 | uuid[5]);
        short c = (short)(uuid[6] << 8 | uuid[7]);
        return new Guid(a, b, c, uuid[8], uuid[9], uuid[10], uuid[11], uuid[12], uuid[13], uuid[14], uuid[15]);
    }

    // ---- IBasisPcmSource: the decoded ring, offered to the audio stack ----
    //
    // Everything above the ring (de-interleaving, per-speaker routing,
    // downmixing, device rate conversion, spatialisation) belongs to
    // BasisMediaPlayerAudio and its per-output taps. The engine's side of the
    // boundary is one interleaved stream at the stream's own rate.

    /// <summary>The stream's audio format. False until the engine has
    /// announced one, which is what keeps the sink silent rather than
    /// building outputs against a guess.</summary>
    public bool TryGetPcmFormat(out int sampleRate, out int channels)
    {
        sampleRate = System.Threading.Volatile.Read(ref _engineSampleRate);
        channels = System.Threading.Volatile.Read(ref _engineChannels);
        return sampleRate > 0 && channels > 0;
    }

    /// <summary>The shared-playback rate trim, handed to the consumer
    /// because consuming faster or slower is how the audio master moves
    /// towards the owner's position.</summary>
    public int PullRateOffsetPpm => System.Threading.Volatile.Read(ref _syncRatePpm);

    /// <summary>
    /// Audio thread. Fills <paramref name="buffer"/> with interleaved floats
    /// at the stream rate and returns how many it wrote, in whole frames. The
    /// engine zero-fills what it could not serve, so an underrun reads as
    /// silence rather than repeated samples, and it takes no media-path lock.
    /// </summary>
    public unsafe int ReadPcm(float[] buffer)
    {
        if (!_open || buffer == null || buffer.Length == 0)
            return 0;
        int channels = System.Threading.Volatile.Read(ref _engineChannels);
        if (channels <= 0)
            return 0;
        // The engine serves whole frames; asking for a partial one would
        // leave the caller's de-interleave carrying a remainder forever.
        int frames = buffer.Length / channels;
        if (frames <= 0)
            return 0;
        fixed (float* p = buffer)
        {
            int pulled = BasisMediaNative.bm_session_read_audio(_handle, p, (uint)(frames * channels));
            if (pulled <= 0)
                return 0;
            System.Threading.Interlocked.Add(ref _audioFramesPulled, pulled);
            return pulled * channels;
        }
    }

    void OnDestroy()
    {
        BasisMediaPlayerRegistry.Remove(this);
        Stop();
    }
}
