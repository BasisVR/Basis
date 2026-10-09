# Basis Media Player

A video and audio player for the Basis framework. It plays live streams (RTSP, WHEP,
RIST, HLS and MPEG-TS over HTTPS) and on-demand files (MP4, WebM, Matroska,
HLS and common audio formats), and keeps every client in a world watching the
same thing. Video is decoded in hardware where the machine supports it and
written straight into a Unity texture.

## Requirements

Unity 6000.0 or later, with the Basis packages this one depends on
(`com.basis.common`, `com.basis.eventdriver`, `com.basis.framework`,
`com.basis.sdk`, `com.basis.settings`). The native plugin is committed; nothing
needs building.

| Platform | Graphics API | Video | Audio |
| --- | --- | --- | --- |
| Windows x64 | Direct3D 11 or 12 | H.264, HEVC, VP9 and AV1 in hardware where the GPU supports them; H.264, VP9 and AV1 also in software | AAC, MP3, FLAC, Opus, PCM |
| Android arm64 (Quest) | Vulkan | H.264, HEVC, VP8, VP9 and AV1 where the device has a hardware decoder | AAC, MP3, FLAC, Opus, PCM |

There is no plugin for Windows on ARM: an ARM64 player build has nothing to
load, and the first engine call throws `DllNotFoundException`.

On Windows, HEVC and VP9 need Microsoft's HEVC and VP9 Video Extensions from
the Store, and hardware AV1 needs the AV1 Video Extension. Without them HEVC
and VP9 are refused and AV1 decodes in software.

Direct3D 12 also needs Unity 6000.3 or later, whose native plugin API includes
`IUnityGraphicsD3D12v8`. On an earlier editor the player refuses Direct3D 12
with a logged error; Direct3D 11 is unaffected.

On other graphics APIs (Vulkan on Windows, OpenGL ES) the player logs
`needs Direct3D 11 or 12` or `needs Vulkan` and does not play. It does not play
on Linux.

## Getting started

`Basis > Tools > Media Player > Insert Player (existing scene)` adds a player
to the open scene, in a **Stereo** or an eight-speaker **Multi-Channel**
version (the prefabs `MediaPlayerStreaming` and
`MediaPlayerMultiChannelStreaming` in `Prefabs/`). Enter a URL in **URL** and
enter Play Mode.

| Field | What it does |
| --- | --- |
| **URL** | A stream or file URL, or a page URL such as a YouTube link if a resolver is installed (see [Page URLs and resolvers](#page-urls-and-resolvers)) |
| **Per-Platform URLs** | Shows **Android URL**, used instead of **URL** on Android builds. The editor always uses **URL** |
| **Play On Start** | Opens the URL when the scene starts. On by default |
| **Max Divergence (ms)** | Live sources: the furthest a viewer may fall behind the live edge. 0 uses the default |
| **Advanced > Liveness** | Forces a source to be treated as live or on-demand. Leave on Auto |
| **Advanced > Engine capture** | Writes the engine's diagnostics to a file |
| **Advanced > Allow Local Addresses** | Allows sources on private and loopback addresses, for testing |
| **Display Name** | What the Media Players menu calls this player. Empty uses the GameObject's name |
| **Playback > Auto Play On Source Assigned** | Play as soon as a source opens. Off, the session opens and holds on its first frame until Play. On by default |
| **Playback > Loop**, **Loop Restart Delay** | Start again from the beginning when an on-demand source ends, after the delay |
| **Playback > Stop After** | Stop after this much playback, in seconds. Paused time does not count; 0 never stops |
| **Playback > Stop On Disable** | Stop the session when the component is disabled. On by default; off keeps the session decoding, unpolled, until the component is enabled again |
| **Playback > Volume**, **Mute** | Handed to the audio sink's own Volume Gain and Mute when they change; the sink's values are what the audio reads |
| **Advanced > Verbose Logging** | Logs this player's own actions to the Console. Engine events are logged regardless |
| **Advanced > Flip Screenshots Vertically** | Inverts `CaptureScreenshot` images relative to what the player works out for the graphics API. Only if they still come out upside down |

From code:

```csharp
var player = gameObject.AddComponent<BasisMediaPlayer>();
gameObject.AddComponent<BasisVideoMaterialOutput>().TargetRenderer = quadRenderer;
player.LoadUrl("rtsp://stream.vrcdn.live/live/vrcdn");
```

This plays picture only; sound needs a `BasisMediaPlayerAudio` (see
[Audio](#audio)).

`Basis > Tools > Media Player > Run Smoke Test` plays a test file and reports
pass or fail. `Test Scene` in the same menu builds a scene with one player or
four, recording diagnostics.

## Supported URLs

| Scheme | Use | Example |
|---|---|---|
| `rtsp://` | Low-latency live: UDP, falling back to TCP | `rtsp://stream.vrcdn.live/live/vrcdn` |
| `rtspt://` | RTSP over TCP only | `rtspt://stream.vrcdn.live/live/vrcdn` |
| `rist://` | RIST live (optional AES) | `rist://stream.example:5000?secret=KEY&aes-type=128` |
| `whep://` / `wheps://` | WHEP (WebRTC), sub-second join | `whep://stream.example:8889/live/whep` |
| `https://….mp4`, `.mov` | Fragmented live or on-demand MP4, or QuickTime | `https://stream.vrcdn.live/live/vrcdn.live.mp4` |
| `https://….ts` | MPEG-TS | `https://stream.vrcdn.live/live/vrcdn.live.ts` |
| `https://….m3u8` | HLS, on-demand or live, including Low-Latency HLS | `https://stream.example/live/index.m3u8` |
| `https://….webm`, `.mkv` | WebM or Matroska: VP9 or AV1 video, Opus audio | `https://stream.example/vod/clip.webm` |
| `https://….flac` `.mp3` `.aac` `.opus` `.wav` | Audio only | `https://stream.example/audio/track.flac` |
| An absolute path | Local file (not a network share; `file://` URLs are refused) | `C:\media\clip.mp4` |

The format is read from the file's contents, but the extension decides the
route. An `http(s)` URL ending in one of the extensions above (or `.m4v`,
`.m4a`, `.m4s`, `.m2ts`, `.mts`) opens directly. Any other goes first to an
installed page resolver such as yt-dlp, and opens directly only when none
takes it. Private and loopback addresses (`localhost`, `192.168.…`, `10.…`) are refused
unless **Allow Local Addresses** is ticked. The tick is cleared on avatars and props as
they load, as **Play On Start** is: imported content cannot reach the viewer's own
network, whatever its author set.

A track nothing here can play is refused and the reason shown in the Media
Players panel: HEVC inside MPEG-TS, VP8 on Windows, VP8 and VP9 where the
platform has no decoder, and AV1 on Quest Pro. When the refused track has
audio beside it, the audio plays; with nothing left to play, the player stops
with an error. `BasisMediaPlayer.LastErrorMessage` carries the reason.
`BasisMediaPlayer.EngineCapabilities` lists what the current machine can play,
with each video codec's route and maximum resolution and frame rate.

### Asking before a URL opens

Before the player opens a URL on a host the user has not trusted, it shows
them the URL and waits for an answer. This happens whichever route asked for
the open, including **Play On Start**, world scripts, props and
`BasisMediaPlayerStreaming`. The prompt can remember the answer for the URL,
the host or the domain, in the same `BasisTrustedUrls` list the rest of the
client keeps. If **Play** is pressed while the prompt is up, playback starts
once the user accepts.

Two routes skip the prompt. A URL typed into the Media Players panel opens
straight away, since the user chose it, and followers in shared playback load
whatever the owner chose without being asked. A page URL prompts once, for the
page; the stream a resolver extracts from it does not prompt again.

Cilbox props can open URLs through the prompting routes only. `OpenResolved`
and `SetSubtitleTracks` are not available to them, and avatar and prop content
has **Play On Start** cleared on load.

## Video output

`BasisVideoMaterialOutput` sets the video on one or more renderers' materials
(`TargetRenderer` and `AdditionalTargets`; `_BaseMap` on URP, `_MainTex`
otherwise, or `TexturePropertyName`). `BasisVideoDisplay` sets it on a uGUI
`RawImage` and can drive an `AspectRatioFitter`.

Aspect, stereo eye and flips are applied to texture coordinates; the mesh is
not resized. `AspectMode`:

| `AspectMode` | Behaviour |
|---|---|
| `Original` (default), `Stretch` | Untransformed; the mesh or `RectTransform` stretches it |
| `FitInside` | Letterbox or pillarbox. Needs the bundled `Basis/Media Player Video` shader, which draws black outside the video |
| `FitOutside` | Crop to fill |
| `PixelPerfect` | Crop to fill on the opposite axis to `FitOutside` |

The surface's shape comes from `DisplayAspectOverride`, or at 0 from the mesh
bounds or `RectTransform`. Mesh bounds ignore the transform's scale: set
`DisplayAspectOverride` on any screen that is not uniformly scaled. The shape
is re-read only when the video texture changes.

`ProjectionMode` selects half of a stereo frame (`SideBySideLR`/`RL`,
`OverUnderTB`/`BT`, with `StereoEye`). `Equirect360`, `VR180` and `Fisheye`
set a `BASIS_PROJ_*` keyword that no bundled shader implements; they show
flat. `FlipVertically` is only for a source encoded upside down.

A custom screen shader must apply the tiling and offset of the texture
property in `TexturePropertyName` (`TRANSFORM_TEX(input.uv, _BaseMap)`), and
for `FitInside` draw black outside `[0,1]`. `Picture` (brightness, contrast,
saturation, gamma) arrives as `_BasisBrightness`, `_BasisContrast`,
`_BasisSaturation` and `_BasisGamma`, which the bundled shader ignores.

## Audio

Sound plays through a `BasisMediaPlayerAudio` on the player's GameObject. Its
`Outputs` list holds `AudioSource`s, each with a `BasisMediaAudioChannel`
choosing one channel of the source or a stereo mix. One `Stereo` output gives
ordinary stereo; one output per channel places a surround mix speaker by
speaker. A surround source through one stereo output is mixed down with the
ITU BS.775 coefficients.

- Filters (Low Pass, Reverb and so on) must sit **below** the output's
  `BasisMediaPlayerAudioTap`; above it they receive silence. The inspector
  flags and fixes the order.
- Each `AudioSource`'s `Volume` and `Mute` apply to that output;
  `BasisMediaPlayerAudio`'s `VolumeGain` and `Mute` to the whole player. They
  multiply with the viewer's main volume.
- `Pitch` has no effect. Keep **Spatialize Post Effects** ticked and **Bypass
  Effects** unticked, or the spatialiser receives silence.
- AudioLink and other `GetOutputData` analysers read silence from a normal
  output. Set `BasisMediaAudioChannel.AnalysisFeed` on the analyser's own
  `AudioSource`; it adds a small delay.
- FLAC and Opus carry up to 7.1, AAC up to 5.1.

For files with several audio tracks (MP4 and Matroska), `AudioTracks` lists
them with their language and name where the file has them, and
`SelectAudioTrack(index)` switches, reopening at the current position. A file
with one track returns an empty list. Label tracks by position as well; many
have no metadata.

## Captions and subtitles

CEA-608 captions in the video are shown by `BasisMediaCaptionOverlay`. Whether
they show, and their opacity, are viewer settings in the Media Players panel.
`SetSubtitleTracks` supplies subtitle files (usually from a resolver) and
`SelectSubtitleTrack(index)` picks one, hiding the in-video captions; -1 shows
them again.

## Shared playback

With `BasisMediaPlayerNetworking` beside the player (both prefabs have it),
one client, the owner, controls the player and the others follow. The owner
sends the URL, play, pause, seek, stop and its position; a late joiner is sent
the current state. A page URL is shared as the page URL and each client
resolves it.

| Field | Effect |
| --- | --- |
| `AdminOnly` | Only clients holding `basis.mediaplayer.control` or `*` may take control. Overrides the other two |
| `AllowAnyoneToTakeControl` | Any client may take control. On by default |
| `AnyoneCanControl` | Clients with no control permission also get the playback controls |
| `PositionHeartbeatSeconds` | How often the owner sends its position (3 by default). 0 turns it off |

On an on-demand source, a seek while paused shows everyone the new frame and
leaves them paused, and each client reaches the end on its own. A follower
compares the owner's position with its own:

| Difference | What the follower does |
| --- | --- |
| Up to 150 ms | Nothing |
| 150 ms to 2 s | Plays up to 2% faster or slower until within 150 ms; its sound shifts in pitch by up to a third of a semitone meanwhile |
| More than 2 s | Jumps to the owner's position |

A live source has no shared position: each viewer plays from the live edge,
and two viewers can be a second or more apart. Live sources cannot be paused or
seeked. **Max Divergence (ms)** caps how far behind a viewer falls; beyond it,
playback stutters.

A late joiner lands at the owner's position, or paused on the owner's frame,
usually a fraction of a second behind, and catches up within about half a
minute. A page URL takes a few seconds to resolve first.

**Resync Everyone** (playback tab) takes control and reloads the source for
every client at your position and play or pause state. **Local Resync** (My
Settings) reloads only your playback at the owner's position, or at your own
if nobody answers within three seconds.

If the owner leaves or stalls, followers keep playing. Volume, captions, audio
track, buffer depth and decode preference are per viewer.

## Live and on-demand sources

The player treats a source that states its length and serves byte ranges as
on-demand, and anything else as live. RTSP, WHEP and RIST are always live, and
HLS playlists state which they are. **Liveness** under **Advanced** overrides
this, for a server that misreports: an on-demand file served without a length
or byte ranges otherwise plays as live.

Live playback starts with the sound; the picture joins at the next keyframe.
Within a player the sound keeps real time and the picture adjusts to it.

## Seeking

```csharp
if (player.Duration > TimeSpan.Zero)
    player.Seek(TimeSpan.FromSeconds(30));
```

Live sources cannot be seeked. A video seek shows the requested frame,
decoding from the keyframe before it, unless that keyframe is more than 720
frames back at the video's frame rate (or 12 seconds, if that is longer), when
it shows the keyframe. MP4, Matroska and HLS seek by index.
WAV lands on the exact sample and FLAC on a frame. MP3, Ogg Opus and raw AAC
estimate their position.

## Viewer settings

`Settings > Developer > Media Player` holds each viewer's defaults; the Media
Players panel changes the selected player.

- **Buffer depth**: how far ahead the player buffers. 0 (Auto) sizes it from
  the viewer's connection. Changing it re-opens that player where it was.
- **Session cap**: the most players open at once, 2 on Android and 3 elsewhere
  by default, 0 for no limit. The furthest players beyond it go dormant: closed,
  with their URL and position remembered for when they wake. Selecting a player
  in the panel keeps it awake.
- **Decode route**: hardware with software fallback (default), hardware only,
  or software only.

**Media Players** in the main menu lists the scene's players. For the selected
one it shows the playback controls (to clients that may control it), **My
Settings** (volume, captions, subtitle and audio track, **Local Resync**,
buffer depth), an admin tab for clients holding `*`, and a state readout under
Advanced.

## Troubleshooting

1. The Console. A failed player logs
   `[BasisMedia] session error <code> (<category>): <reason> [<url>]`, and the
   Media Players panel shows the reason. Common reasons: an unsupported codec,
   a private address, a refusing server. When a server admin has locked media
   players, nothing opens and the log says so. On a shared player the owner logs
   `sharing '<url>'` and a follower logs `loading '<url>' from player <id>`. A
   follower with no such line never received the URL. Logged URLs leave out the
   query and any credentials.
2. What the machine supports: `Settings > Developer > Media Player`, or
   `BasisMediaPlayer.EngineCapabilities`.
3. `Basis > Debug > Media Player`, which shows a player's pipeline stage by
   stage while it runs.
4. Captures. `BasisMediaPlayerDiagnostics` beside the player writes a
   per-frame CSV to the persistent data folder, and **Engine capture** records
   the engine's side. [`Native~/DIAGNOSTICS.md`](Native~/DIAGNOSTICS.md)
   explains the columns.

## Scripting

Everything below is on `BasisMediaPlayer` unless it says otherwise. The
companions are an output (`BasisVideoMaterialOutput` or `BasisVideoDisplay`),
`BasisMediaPlayerAudio` for sound and `BasisMediaPlayerNetworking` for shared
playback; the sections above cover each.

### Open and control

`LoadUrl(url)` opens a stream, file or page URL and plays it unless
`AutoPlayOnSourceAssigned` is off. `LoadLocalPath(path)` opens a file and
`LoadSource(BasisMediaSource)` a source described with its audio leg, start
position, loop and volume. `Play`, `Pause`, `Resume`, `TogglePause`, `Stop`
and `Reload` do what they say. `Seek(TimeSpan)` moves an on-demand source and
is refused by a live one. `Loop`, `LoopRestartDelaySeconds`, `StopAfterSeconds`,
`StopOnDisable`, `Volume` and `Mute` are fields a prefab or a prop can set.

```csharp
player.LoadUrl("https://example.org/clip.mp4");
player.OnReady += () => player.Seek(TimeSpan.FromSeconds(30));
player.OnEnded += () => player.LoadUrl(nextUrl);
```

### State and position

| Member | Meaning |
| --- | --- |
| `Status` | `NoMedia`, `Connecting`, `Buffering`, `Playing`, `Paused`, `Stopped`, `Ended` or `Error`. `Ready` is never reported |
| `IsPrepared` | The load has reached playback at least once; position, duration and picture describe it |
| `IsPlaying` | A session is open and meant to be playing. False while paused, as Unity's `VideoPlayer.isPlaying` is |
| `IsPaused` | A session is open and paused |
| `Position`, `Duration` | `TimeSpan`; `Duration` is zero for a live source |
| `LastErrorMessage`, `ErrorCode` | Why the session failed, or why a track was refused while the rest plays |
| `State`, `PlayWhenReady` | The engine's raw state and the viewer's play intent, for code that needs them apart |

### Events

`OnReady` once per load when it first reaches playback, `OnStarted` when
playback starts and again after every pause, `OnFirstFrameReady` once per
load when a frame has been presented, `OnPaused` on a pause,
`OnSeekCompleted(TimeSpan)` when the engine has acted on a seek, `OnLooped`
when `Loop` restarts the source, `OnEnded` at the end, and
`OnError(Exception)` on a failure. `OnStarted` can arrive before
`OnFirstFrameReady`; wait for the latter before reading the picture.
`OnOutputTextureChanged` announces the texture (null when it is dropped),
`OnMetadataChanged` what is known about the source, `OnAudioTrackChanged`
and `OnSubtitleTrackChanged` a change of track, and `OnCaptionCueChanged`
each caption. All run on the main thread. Unsubscribe in `OnDisable`.

### Picture, audio and captions

`OutputTexture` (null until the first frame), `VideoSize` and
`CaptureScreenshot`; `AudioTracks` and `SelectAudioTrack`; `CaptionsEnabled`,
`CaptionTextOpacity`, `CaptionBackgroundOpacity`, `SubtitleTracks` and
`SelectSubtitleTrack`. `Metadata` and `CurrentTransport` describe what is
playing. The sections above have the detail.

### Not for scripts

`ReadPcm`, `TryGetPcmFormat`, `PullRateOffsetPpm`, `AudioSampleRate`,
`AudioChannels` and `AudioFramesPulled` are the audio sink's side of the
player. `LoadGeneration`, `LoadCancellation`, `LoadPending`, `OpenResolved`
and `ReportLoadError` are the resolver contract (below). `FramesDecoded`,
`PresentedFrameCount`, `BankedMilliseconds`, `SyncRatePpm` and `AvOffsetUs` are
diagnostics readouts.

## Extending the player

### Page URLs and resolvers

The player plays stream URLs. Page URLs (YouTube, Twitch) need a resolver
package registered with `BasisMediaUrlRouter`. `LoadUrl(url)`, which
**Play On Start** also uses, opens a playable URL directly and offers anything
else to the resolvers in priority order.

```csharp
internal sealed class MyResolver : IBasisVideoResolver
{
    public int Priority => 0; // higher runs first; ties run in registration order

    // Cheap and side-effect-free. Decline directly playable URLs.
    public bool CanResolve(string url) => !BasisMediaUrlRouter.IsDirectlyPlayable(url);

    // Resolve, then open. May be async; return true once you have taken it.
    // Capture player.LoadGeneration first and drop the result if it has
    // moved: a later open or a stop supersedes this load. Pass
    // player.LoadCancellation into the extraction so that work stops too.
    // Then open through OpenResolved or call ReportLoadError: a claimed
    // load that does neither stays pending.
    public bool TryResolve(BasisMediaPlayer player, string url)
    {
        // … player.OpenResolved(new BasisResolvedMedia { … }) …
        return true;
    }
}

internal static class MyResolverInstaller
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install() => BasisMediaUrlRouter.Register(new MyResolver());
}
```

`BasisResolvedMedia` carries the stream URL, any separate audio URL, whether
it is live, subtitle tracks and display details. Resolve on the main thread,
and discard an async result if `player.LoadGeneration` has changed since you
started. The address rules still apply to what a resolver opens.

`player.Open(videoOnlyUrl, audioOnlyUrl)` plays separate video and audio
streams together.

### SEI user data

`BasisMediaPlayer.OnUserDataReceived` delivers the SEI `user_data_unregistered`
messages (payload type 5, H.264 and H.265) in a video stream, each when
playback reaches its timestamp, as a UUID and payload:

```csharp
static readonly Guid Mine = Guid.Parse("b1f0a7d4-9c3e-4a52-8f61-2d7c5e0b93a8");

player.OnUserDataReceived += (ptsUs, uuid, payload) =>
{
    if (uuid != Mine) return;      // x264 stamps its own build string this way
    Decode(payload);               // borrowed for the call; copy what outlives it
};
```

- It runs on the main thread, once per message, in timestamp order. Keep it
  short.
- `payload` is valid only during the call.
- Every UUID arrives, the encoder's included; filter on yours. To build one from 16 bytes use
  `BasisMediaPlayer.GuidFromRfc4122`; `new Guid(byte[])` reverses the first
  three fields.
- Unsubscribe in `OnDisable`, comparing the player with `ReferenceEquals`.
- Validate the payload; the player passes it on unread.
- Messages over 64 KiB are refused. Seeks and loops drop pending messages.

SEI survives repackaging but not re-encoding.

## Migrating from the C player

Scripts and props written against the previous player keep working: the
members they used are the members here, with the same names and signatures.
The ones that behave differently are listed first. A script that names a
member from the last list does not compile, and a prefab value saved for one
of those fields is dropped.

| Member | What is different |
| --- | --- |
| `IsPlaying` | False while paused. The previous player kept it true, so a script that used it to mean "a session is open" should read `Status` instead: anything but `NoMedia`, `Stopped`, `Ended` and `Error` |
| `Status` | Never reports `Ready`: the engine has no state between buffering and playing |
| `Reload` | Re-opens the current source from the start. A page URL goes back through the resolver, because the stream it produced may have expired |
| `LoadSource` | Headers, options, open timeout and playback rate have no engine counterpart; setting them logs a warning once |
| `CaptionsEnabled`, `CaptionTextOpacity`, `CaptionBackgroundOpacity` | Captions are a viewer setting here. Reading gives the value in force (the viewer's setting unless this player overrides it); writing sets this player's override |
| `Metadata`, `ApplyMetadata`, `OnMetadataChanged` | Built from a resolver's answer or from the URL, with `ApplyMetadata` merged on top. Display data only |
| `CurrentTransport` | Read from the stream's scheme: `http`, `hls`, `rtsp`, `rist`, `whep`, `file` |
| `OnReady`, `OnStarted`, `OnPaused`, `OnFirstFrameReady`, `OnError` | Raised from the engine's state transitions. `OnError` carries an exception naming the code, category and reason; `State` is `Error` by then |
| `OnSeekCompleted` | Raised when the demuxer lands the seek, with the position asked for; the picture follows a few frames later. A seek a live source refuses is not reported |
| `OnCaptionCueChanged` | Sidecar cues carry their times from the file; an in-band caption carries the position it came due at, with an unknown end |
| `OnAudioTrackChanged` | Not raised for a source with one track, whose `AudioTracks` is empty. `Codec`, `BitsPerSecond` and `IsDualMono` on the track are empty: the engine's track list does not carry them |
| `Volume`, `Mute` | Handed to the audio sink's own `VolumeGain` and `Mute` when they change; the sink's values are what the audio reads, so the last side to move wins |
| `StopOnDisable` | On by default; `OnDisable` stops the session |
| `BasisMediaPlayerNetworking.SetUrl` | Returns `Task`; `TrySetUrlAsync` is the same call reporting whether the load went ahead |
| `BasisMediaPlayerAudio.HasMediaTime`, `CurrentMediaTimeUs` | The player's own position |
| `BasisMediaPlayerSecurity` | Forwards to the client's URL rules and the capture-path sandbox |

Gone, with a stub that compiles, loads and warns (`[Obsolete]`, with the
replacement in the message):

- `TrySeekBack`: no live buffer to step back into. A live source plays at its
  edge; an on-demand source takes `Seek`.
- `SelectBitrate`, `BitrateTracks`, `SelectedBitrateIndex`,
  `OnBitrateTrackChanged`: no managed bitrate ladder. A resolver picks the
  rung before the open and the engine picks an HLS variant for itself.
- `LoadResolvedSource`: resolvers call `OpenResolved`, which carries the
  stream, the liveness and the subtitle tracks a `BasisMediaSource` cannot.
- `BasisMediaPlayerNetworking.SetDriftSeekThresholdSeconds`: the engine's
  sync ladder decides when a follower seeks rather than slews.

Gone without a stub, because nothing here corresponds to it: `BufferMode`,
`MaxQueueLength`, `OverflowPolicy`, `LateFrameSkipUs`, `PresentationOffsetUs`,
`BufferMilliseconds`, `PlaybackRate` and the DVR settings (the engine owns
pacing, the frame pool and the 40 ms presentation rule; a serialised
per-player buffer depth is deliberately not offered, the client setting and
the panel's override being the two routes); the frame-queue counters
(`QueuedFrameCount`, the drop and skip counts, `HeadFramePtsUs`,
`TailFramePtsUs`: the diagnostics component and the engine capture carry
them); `Source`, `Renderer`, `Clock`, `NativeEngine`, `ActiveMediaSource`;
`OnFramePresented`; and the types `BasisVideoBufferMode`,
`QueueOverflowPolicy`, the playlist and the frame-source types. A prefab
value saved for one of these fields is dropped when the prefab loads.

Scripts written against the engine's own names in the first release of this
package on `experimental-branch-major-changes` have these replacements:
`Seek(TimeSpan)` for `Seek(double)`, `Position` and `Duration` for
`PositionSeconds` and `DurationSeconds`, `OutputTexture` for `Texture`,
`OnAudioTrackChanged` for `OnAudioTrackIndexChanged`, `CaptionsEnabled` and
the two opacities for their `Effective` forms, and `OnCaptionsEnabledChanged`
or `OnCaptionStyleChanged` for `OnCaptionPreferencesChanged`. `OnSeeked`
reported a seek as it was issued; `OnSeekCompleted` reports it once the
engine has acted on it. `SetSyncTarget` and `ClearSyncTarget` belong to
shared playback and have no replacement.

Props built against the previous player load. A prop may set `playOnStart`,
`liveness`, `maxDivergenceMs`, `BufferDepthOverrideMs`, `DisplayName`,
`AutoPlayOnSourceAssigned`, `Loop`, `LoopRestartDelaySeconds`,
`StopAfterSeconds`, `Volume`, `Mute` and `VerboseLogging`; `OpenResolved`,
`SetSubtitleTracks`, `LoadLocalPath`, `LoadSource` and `CaptureScreenshot` are
blocked for it.

## Known limits

- No RTMP.
- WebM/Matroska without a Cues index seeks, but the picture holds until the
  next keyframe.
- An MPEG-TS file opened directly plays from the start with no duration and
  no seeking. The same content served as HLS seeks.
- HLS plays the highest-quality variant with no switching. Audio carried
  as a separate rendition plays the variant's default choice, with no
  language selection. Encrypted, byte-range and keyframe-only playlists
  are refused.
- WHEP never requests a keyframe; unrecovered loss lasts until the next one.
- Shared playback is not enforced by the server, which relays its messages
  without inspecting them and keeps no media state.
- 360°, VR180 and fisheye video show flat, and `Picture` needs a custom
  shader.
- No playback on Linux, or on Vulkan under Windows.

## Building the engine

The engine is a Rust workspace in `Native~/`. After changing it, rebuild the
committed binaries in `Runtime/Plugins/` as
[`Native~/README.md`](Native~/README.md) describes (each platform needs
librist staged first).
[`Native~/TESTING.md`](Native~/TESTING.md) covers prerequisites and testing.
[`TESTING.md`](TESTING.md) has the checks for the Unity side, and
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) the licences of what the
package includes. After a change to `Native~/Cargo.lock`, update the licence
files and bake the licence manifest as that file describes.
