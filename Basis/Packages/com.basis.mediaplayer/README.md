# Basis Media Player

Live and on-demand video — and audio-only media — for Basis, decoded with the
**operating-system hardware codecs** and presented **zero-copy** into a Unity texture. No transcode server, no
bundled codec libraries, no `UnityEngine.Video.MediaPlayer`.

- **Windows (PC / VR)** — Media Foundation H.264/H.265/VP9/AV1 + AAC/MP3, and Opus
  through a runtime-loaded libopus, on a DXVA D3D11
  device; NV12 → BGRA via the D3D11 video processor into a texture Unity samples.
  (VP9 and AV1 need their Store extensions and a GPU with hardware decode —
  `basis_media_probe_video_codec` answers for both legs.)
  Works on **D3D11** (primary) and **D3D12** (shared-handle interop).
- **Android (Quest)** — `AMediaCodec`/`AMediaExtractor`; decoded frames arrive as
  `AHardwareBuffer`s imported into **Vulkan** as a `VkImage` Unity samples.

## Supported URLs (VRCDN and friends)

| Scheme | Use | Example |
|---|---|---|
| `rtsp://`  | PC/VR low latency — UDP first, TCP-interleaved fallback | `rtsp://stream.vrcdn.live/live/vrcdn` |
| `rtspt://` | PC/VR low latency, TCP-interleaved pinned (legacy; prefer `rtsp://` unless a host needs forced TCP) | `rtspt://stream.vrcdn.live/live/vrcdn` |
| `rtmp://`  | RTMP pull | `rtmp://stream.vrcdn.live/live/vrcdn` |
| `rist://`  | RIST live ingest (UDP, loss recovery + optional AES) | `rist://stream.example:5000?secret=KEY&aes-type=128` |
| `https://…​.mp4` | MP4 over HTTPS — fragmented (live) or progressive VOD (faststart or trailing moov, seekable) | `https://stream.vrcdn.live/live/vrcdn.live.mp4` |
| `https://…​.ts`  | MPEG-TS over HTTPS (Quest) | `https://stream.vrcdn.live/live/vrcdn.live.ts` |
| `https://…​.m3u8` | HLS / Low-Latency HLS | `https://stream.example/live/index.m3u8` |
| `https://….wav` | WAV audio (integer PCM, mono up to 7.1) | `https://stream.example/audio/track.wav` |
| `https://….webm` | WebM VP9/AV1 video and/or Opus audio (YouTube's >1080p carriage; Cues-indexed files seek) | `https://stream.example/vod/clip.webm` |
| `https://….opus` | Ogg Opus audio | `https://stream.example/audio/track.opus` |
| `https://….mp3` | MP3 audio — standalone, or MP3-in-MP4 (`.m4a`) | `https://stream.example/audio/track.mp3` |

The protocol/demux core (RTSP/RTP, RTMP/FLV, MPEG-TS, fMP4, WebM, RIFF/WAV) is portable C,
picking demuxers by content sniff so extensionless CDN URLs (googlevideo and friends)
route correctly. On Android, eligible http(s) URLs are first offered to the OS extractor
(`AMediaExtractor`, which demuxes as well as decodes); anything it declines falls back to
the portable demux path. Windows always demuxes portably and only decodes + presents
natively.

### HLS / Low-Latency HLS

`.m3u8` URLs are handled by `protocol/basis_hls.c`, which is **not** a demuxer: it
parses the playlist, selects one rendition, starts at the live edge, and stitches
the segments — and, for LL-HLS, the partial segments (`EXT-X-PART`) — into one byte
stream that the existing MPEG-TS / fMP4 demuxers consume. When the origin advertises
`EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD` with parts, the client uses blocking
`_HLS_msn`/`_HLS_part` playlist reloads and rides parts to target roughly
`PART-HOLD-BACK` latency (~5 s). **The ~5 s target needs an LL-HLS origin** — against
a plain HLS origin you get its segment-bound latency, not 5 s.

Runs on **Windows** (WinHTTP fetch) and **Android/Quest** (`HttpsURLConnection`
fetch via JNI), **clear streams**, **single rendition**.

### RIST

`rist://` ingests a RIST stream — MPEG-TS over UDP via librist, with
packet-loss recovery and optional AES encryption. librist reads its connection
options straight from the URL query: `?secret=<key>&aes-type=128` (or `256`)
for encryption, and `?buffer=<ms>` to size the recovery buffer. The buffer can
also be set from C# via `BasisMediaSource.Options["buffer"]`, folded into the
URL automatically. The recovered transport stream feeds the same MPEG-TS
demuxer as the HTTP/TS path.

RIST is **opt-in at build time** — the default plugin links only OS frameworks.
Build with `-DBASIS_WITH_RIST=ON` against prebuilt librist (see *Building the
native plugin* below).

### RTSP transport

`rtsp://` negotiates its transport: it attempts **UDP** (RTP/AVP) first and falls back to
**RTP interleaved over the TCP** control channel on refusal, a socket error, or a no-data
timer — and remembers a host that fails UDP so later loads go straight to TCP. `rtspt://`
skips the probe and **pins TCP-interleaved**, for hosts or networks where UDP never works.
The settled transport is logged once per load and exposed on
`BasisMediaPlayer.CurrentTransport`.

## Live vs on-demand

Every source is either **live** (presented at the live edge, lowest latency) or
**on-demand** (VOD — paced to real time, so a file that arrives faster than it plays
doesn't fast-forward). `BasisMediaSource.Delivery` selects which:

| `Delivery` | Behaviour |
|---|---|
| `Auto` (default) | Decided at open from the source — see below |
| `Live` | Force the live-edge clock |
| `OnDemand` | Force real-time pacing |

`Auto` reads the source: a non-HTTP transport (`rtsp`/`rtmp`/`rist`) is live; an HTTP
response with a known `Content-Length` and byte-range support, or an HLS playlist
carrying `EXT-X-ENDLIST`, is on-demand; an open-ended HTTP response is live. On-demand
throttles delivery and presents on a fixed 1× clock, with a compressed read-ahead
buffer absorbing bursty CDN delivery.

`LoadUrl(url)` uses `Auto`. For explicit control, load a `BasisMediaSource`:

```csharp
player.LoadSource(new BasisMediaSource { Uri = url, Delivery = BasisMediaDelivery.OnDemand });
```

The live jitter buffer is tunable via `BasisMediaPlayer.BufferMilliseconds` /
`BufferMode` (Fixed, or auto-tuning Dynamic — lower = less latency, higher = smoother).
On-demand currently presents on a fixed internal buffer; `BufferMilliseconds` applies
to the live path only.

## Seeking

Sources that report a duration (`BasisMediaPlayer.Duration > 0` — a progressive MP4, a WAV, a
finished TS-segment HLS VOD playlist) are seekable. A duration is necessary but not on its own
a guarantee: a source whose transport can't reposition still refuses the seek. `Seek(TimeSpan
position)` requests an **absolute** seek; the demuxer repositions at the next sample (or
segment) boundary and resumes from the preceding keyframe, so playback lands **at or shortly
before** the target — watch `Position`, and `OnSeekCompleted` fires once it settles. `TrySeekBack(TimeSpan)` is a relative rewind. Seeking a live or unindexed
source throws `NotSupportedException`.

```csharp
if (player.Duration > TimeSpan.Zero)
    player.Seek(TimeSpan.FromSeconds(30));
```

## Split-stream (separate video + audio)

Adaptive sources often serve high-resolution video and audio as **separate** streams
(H.264 video-only + AAC audio-only). Set `BasisMediaSource.AudioUri` alongside `Uri`
and the engine runs a second demux thread feeding the same decoder, so both present in
sync on one clock:

```csharp
player.LoadSource(new BasisMediaSource {
    Uri = videoOnlyUrl, AudioUri = audioOnlyUrl, Delivery = BasisMediaDelivery.OnDemand,
});
```

A null `AudioUri` (the default) is an ordinary single muxed stream.

## What's playing — metadata

`BasisMediaPlayer.Metadata` describes the current media for display: `Title`,
`FileName`, `SourceUrl`, and — when an integration supplies them — `Uploader`,
`ThumbnailUrl` and `Duration`. `OnMetadataChanged` fires whenever it updates.

With no resolver installed, the player derives defaults from the URL alone:
`https://host/videos/My%20Video.mp4` titles as "My Video" (`FileName`
"My Video.mp4"); extensionless stream paths fall back to the last path segment
(`rtsp://host/live/vrcdn` → "vrcdn"), then the host. A resolver can push the
real page title (and the richer fields) by setting `BasisMediaSource.Metadata`
before `LoadSource`; anyone can merge fields in later with
`player.ApplyMetadata(...)`.

On networked players every client derives metadata from the same synced input
URL, so titles agree across clients with no extra synced state.

## Playlists

`BasisMediaPlayerPlaylist` (`Runtime/Examples`, beside
`BasisMediaPlayerStreaming`) is an optional orchestration component that drives
a player through an ordered list of entries (`Url` + optional `DisplayName`).
With `PlayOnStart` (the default) the first entry loads on Start — when a
playlist drives the player, disable `BasisMediaPlayerStreaming`'s
`ConfigureOnStart` (or remove that component) so they don't both load a source.

```csharp
playlist.Entries.Add(new BasisMediaPlaylistEntry { Url = url, DisplayName = "Opening set" });
playlist.PlayAt(0);   // Next() / Previous() wrap; OnEntryChanged reports jumps
```

`Advance` selects what happens when an entry ends: `None`, `Sequential` (stop
after the last entry) or `LoopAll`. Live entries never end, so they never
auto-advance.

Entries load through the player's normal routing — page URLs resolve per
client and the security gates apply. On a networked player the playlist routes
loads through `BasisMediaPlayerNetworking`, so entry changes reach remote
clients via the existing URL sync; only the controlling client needs the
playlist populated, and auto-advance runs on the owning client alone. The
playlist itself is not networked: late joiners see the current entry, not the
queue.

## Page URLs (optional resolver package)

The player opens **stream** URLs (the schemes above) directly. It does **not** itself
turn a **page** URL — a YouTube or Twitch watch page — into a stream. That resolution
is provided by a **separate, optional resolver package** which registers itself on
`BasisMediaUrlRouter`; the player core has no dependency on it and never references it.
Basis ships a yt-dlp-based resolver as that package, but any
[resolver](#writing-a-resolver) can fill the role.

**With the resolver package installed**, a URL field such as
`BasisMediaPlayerStreaming.StreamUrl` steers each URL automatically:

- A **directly-playable** URL — a transport scheme, or an HTTP URL whose path ends in a
  media extension (`.mp4`/`.m4v`/`.m4a`/`.m4s`/`.ts`/`.m2ts`/`.mts`/`.m3u8`/`.wav`/`.webm`/
  `.opus`/`.mp3`) — loads directly. `.ogg` is deliberately absent: it's a generic container
  the pipeline doesn't decode, so it routes to the resolver.
- **Anything else** (an HTTP page URL with no media extension) is handed to the
  resolver, which turns it into the playable stream endpoint(s) and loads them.

**Without it**, the router is inert: every URL loads directly, so all the stream URLs
above keep working — but page URLs are no longer resolved, so **YouTube, Twitch and
similar links won't play**. Loading one degrades gracefully rather than failing silently:
the player reports a short message — *"…needs a media URL resolver
package, and none is installed."* — surfaced in the **Media Players** panel and logged
as a warning on each such load (it never throws or tries to demux the HTML page). Removing the
package is a supported choice: you lose common-site resolution and nothing else. (This
only steers — it never blocks a URL; host trust is enforced separately.)

> **Known gap.** The steering keys off the URL's form, so a **direct HTTP stream with
> no file extension** (e.g. `https://host/live/feed` with no `.ts`/`.mp4`) can't be
> told apart from a page URL: with the resolver installed it is sent to the resolver,
> which finds no extractable stream and reports an error — so playback fails rather
> than loading directly. Give direct HTTP streams a recognised
> extension, or use a transport scheme (`rtsp`/`rtmp`), to avoid this.

### Writing a resolver

A resolver is any `IBasisVideoResolver` registered on `BasisMediaUrlRouter`. The player
core never references it — register one at startup and the router consults it for every
load, in `Priority` order, until one takes ownership. The bundled
[yt-dlp integration](https://github.com/BasisVR/BasisYtDlpIntegration) is a complete worked
example; the shape is:

```csharp
using UnityEngine;

internal sealed class MyResolver : IBasisVideoResolver
{
    public int Priority => 0; // higher runs first; equal priorities run in registration order

    // Cheap, side-effect-free pre-filter. Decline directly-playable URLs so the player
    // opens them itself — IsDirectlyPlayable is the shared steering check.
    public bool CanResolve(string url) => !BasisMediaUrlRouter.IsDirectlyPlayable(url);

    // Take ownership: turn the page URL into its stream(s) and load them (may be async).
    // Return true once taken; false to fall through to the next resolver, then a direct load.
    public bool TryResolve(BasisMediaPlayer player, string url)
    {
        // … resolve, then player.LoadSource(resolvedSource) / player.LoadUrl(streamUrl) …
        return true;
    }
}

internal static class MyResolverInstaller
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Install() => BasisMediaUrlRouter.Register(new MyResolver());
}
```

- **Async resolves must guard against stale loads.** If `TryResolve` resolves
  asynchronously, capture `player.LoadGeneration` before you start and skip your
  `LoadSource` / `LoadUrl` when the async work completes if it no longer matches. The player
  bumps `LoadGeneration` on every source replacement — `LoadUrl`, `LoadLocalPath`,
  `LoadSource` and a direct `Source` assignment — so without this a slow resolve of an
  earlier URL can overwrite a newer load. Return `true` as soon as you take ownership (kick
  off the resolve), not when it finishes. When you complete the load, call
  `player.LoadResolvedSource(source, capturedGeneration)` rather than `LoadSource` so the
  URL-derived metadata the originating `LoadUrl` seeded is matched to your load and not to
  an unrelated `LoadSource` that raced the resolve.
- **Main thread only.** The resolver list is unsynchronised — `Register` / `Unregister`
  and resolution all run on Unity's main thread. Registering from
  `RuntimeInitializeOnLoadMethod` and resolving from the player's load path satisfies this.
- **Routing only, never trust.** A resolver decides *how* a URL loads, not *whether* it's
  allowed — host trust stays with `BasisMediaPlayerSecurity`.

## Usage

```csharp
var player = gameObject.AddComponent<BasisMediaPlayer>();
gameObject.AddComponent<BasisVideoMaterialOutput>().TargetRenderer = quadRenderer;
player.LoadUrl("rtsp://stream.vrcdn.live/live/vrcdn"); // auto-plays
```

Or drop the `Prefabs/MediaPlayerStreaming` prefab in a scene and set the URL on
`BasisMediaPlayerStreaming` (it can auto-pick RTSP on PC / MPEG-TS on Quest).
Add a `BasisMediaPlayerAudio` (+ `AudioSource`) for sound;
`BasisMediaPlayerNetworking` syncs URL/state across the room.

The CPU `IBasisFrameSource` path (e.g. `BasisSyntheticTestSource`) is still
available by assigning `player.Source` directly — useful for tests without a feed.

### Video output (screens and UI)

Frames reach the world through one of two sinks, both driven from the player's
`OnOutputTextureChanged`:

- **`BasisVideoMaterialOutput`** — binds the frame to one or more `Renderer` material
  properties (`_BaseMap` on URP, `_MainTex` on legacy BiRP, per `TexturePropertyName`).
  `TargetRenderer` plus every entry in `AdditionalTargets` is driven from the same
  texture, so one player can feed several screens at once.
- **`BasisVideoDisplay`** — binds it to a uGUI `RawImage`, optionally driving an
  `AspectRatioFitter` from the player's reported `VideoSize`.

Aspect, stereo-eye selection and flips are applied as a **UV scale/offset on the sampled
texture**, composed once and written to the material's texture ST (or the `RawImage`'s
`uvRect`). The `Equirect360`, `VR180` and `Fisheye` projections are the exception: they
can't be expressed as a UV scale/offset, so they enable a shader keyword instead.

On the material path nothing touches the mesh, so a screen placed at a known size in a
world keeps that size whatever plays on it. On the UI path an `AspectRatioFitter` is the
one thing that does resize its `RectTransform`, which is what makes it the right way to
letterbox a `RawImage`.

#### The screen shader

`Basis/Media Player Video` is URP/Unlit with one change: **UVs outside `[0,1]` render
opaque black** rather than being resolved by the sampler. That single branch is what
makes letterboxing possible at all. `FitInside` fits the whole source inside the screen
by scaling the *bar* axis above 1, which pushes the sampled UV outside the texture over
the bar region — and a UV transform has no other way to produce a bar.

The frame texture is `Clamp`-wrapped, so on **any other material** that same region
resolves to the outermost row or column of video pixels stretched flat across the bar:
a streak of edge colour that shifts with the content, not a black bar. The video itself
stays correctly proportioned either way, so it's the bars that give it away.

`FitInside` therefore needs this shader (or your own equivalent, see below). Every other
mode keeps the sampled UV inside `[0,1]` and looks identical on any material. One
consequence of baking the fit into the texture ST: this shader can't tile — authored
tiling on the material is composed into the fit and then blacked out.

#### Aspect

`AspectMode` compares the source's aspect against the **display aspect** — the shape of
the surface you're drawing on, not the shape of the video:

| `AspectMode` | Behaviour | Safe on any material? |
|---|---|---|
| `Original` (default) | Sample untransformed. The mesh or `RectTransform` stretches the frame to its own shape | yes |
| `Stretch` | Same as `Original` | yes |
| `FitInside` | Letterbox / pillarbox — whole source visible, bars on the remaining axis | **no** — needs the shader above |
| `FitOutside` | Crop to fill — no bars, edges of the source lost | yes |
| `PixelPerfect` | Crop to fill, insetting on the opposite axis to `FitOutside` (it does not map source texels to screen pixels — there's no display-resolution input on this path) | yes |

`DisplayAspectOverride` supplies the display aspect directly. Left at 0, it's derived
from the target: the renderer's **local** bounds on the material path, the
`RectTransform`'s rect on the UI path.

> **Known gap.** Local bounds are mesh-local and exclude transform scale, so a 1×1 quad
> scaled to (16, 9, 1) still reports 1:1 and `FitInside` letterboxes the video into a
> square in the middle of a wide screen. Set `DisplayAspectOverride` on any screen that
> isn't uniformly scaled. The aspect is also recomputed only when the frame texture
> changes, so a screen resized at runtime keeps the fit it was given.

On the UI path, prefer `Original` plus an `AspectRatioFitter` — a `RawImage` draws through
the UI material, which smears rather than blacking out, so `FitInside` isn't available
there. `MediaPlayerStreaming` ships `FitInside` on a uniformly scaled screen.

#### Projection

`ProjectionMode` describes how the source frame is laid out. `SideBySideLR`/`RL` and
`OverUnderTB`/`BT` select one half of a stereo frame via the same UV transform, with
`StereoEye` picking which. `Equirect360`, `VR180` and `Fisheye` don't reshape the UV —
they enable a `BASIS_PROJ_EQUIRECT` / `_VR180` / `_FISHEYE` keyword for a shader that
implements the mapping. **No bundled shader implements those keywords**, so on the stock
material those three modes render the source flat, as mono.

#### Orientation

Some backends publish the frame top-left origin, and whether they do can depend on the
GPU rather than the content, so the player reports it as `OutputFrameIsTopLeftOrigin` and
both sinks fold that correction in automatically. Leave `FlipVertically` **off** for
normal content; it's there for a source that is genuinely encoded upside-down, which is
consistent across every machine. `FlipHorizontally` is for a screen mesh whose UV winding
presents the video mirrored.

#### Picture

`Picture` (Brightness, Contrast, Saturation, Gamma) is a per-output adjustment published
as `_BasisBrightness` / `_BasisContrast` / `_BasisSaturation` / `_BasisGamma` — through a
`MaterialPropertyBlock` on the material path, and onto `RawImage.material` on the UI path.
A shader that doesn't declare them ignores them, which today includes
`Basis/Media Player Video`, so on the stock setup only the UI path's Brightness has any
effect (it's multiplied into `RawImage.color`). Wire the four properties into your own
shader to use the rest.

#### Using your own shader

Anything bound as the screen material needs to:

- expose the texture property named in `TexturePropertyName` (`_BaseMap` by default), and
- transform the sampled UV by that property's ST — `TRANSFORM_TEX(input.uv, _BaseMap)` —
  since that's where aspect, stereo-eye selection and flips arrive.

That much is enough for every aspect mode except `FitInside`. For that, also **render UVs
outside `[0,1]` as black** before sampling, the way the bundled forward pass does.

`Equirect360`, `VR180` and `Fisheye` need more than the ST transform: implement the
mapping for whichever of `BASIS_PROJ_EQUIRECT`, `BASIS_PROJ_VR180` and
`BASIS_PROJ_FISHEYE` you support, keyed off the enabled keyword. A shader that handles
only the ST transform renders those three flat.

Declare the four `_Basis*` picture floats if you want those, and note that the bundled
shader's black-out lives in its forward pass only — a deferred renderer's GBuffer pass
would smear the bars instead.

### Audio (stereo and multichannel)

Audio routes through a `BasisMediaPlayerAudio` on the player GameObject. List the
`AudioSource`s in `Outputs`, each carrying a `BasisMediaAudioChannel` that selects
what it plays — a single decoded channel, or a stereo downmix of the whole stream.
For stereo, use a single `Output` set to `Stereo` (the `Prefabs/MediaPlayerStreaming`
prefab); for surround, one `Output` per channel so a 5.1 / 7.1 mix (up to 8
channels) can be positioned speaker-by-speaker in the world (the
`Prefabs/MediaPlayerMultiChannelStreaming` prefab).

Each output `AudioSource` carries a `BasisMediaPlayerAudioTap`, which writes the
decoded stream straight into that source's DSP block. Unity applies audio filters in
component order, so a Low Pass / High Pass / Reverb filter has to sit **below** the
tap on the same GameObject; anything above it is handed silence.

An output carrying filters with no tap above them is flagged in the inspector, on the
owning `BasisMediaPlayerAudio` and on the tap itself where there is one, with a button
that fixes the order by raising the tap, or by lowering the filters past it where the
tap can't move. An output with no filters isn't flagged: it gets its tap at runtime and
there's no ordering to get wrong. Component order is fixed once play starts, so an
output assembled in code that already carries filters can't be put right, and logs a
warning naming the filter instead.

Each source's own `Volume` and `Mute` are folded into that tap's gain, so they behave
as they would for a clip and stay per-output — on a surround setup you can trim one
speaker without touching the rest. `BasisMediaPlayerAudio`'s `VolumeGain` / `Mute` are
the player-wide pair, and the client's main volume scales the lot; all three multiply.

Two of the AudioSource's own controls behave differently from a clip. `Pitch` does
nothing, since it belongs to clip playback, and pitching the stream would pull the
audio off the video in any case. Spatialisation needs the spatialiser to run *after*
the tap, so `Spatialize Post Effects` stays ticked and `Bypass Effects` unticked (both
prefabs ship that way): with either the wrong way round, the spatialiser processes the
silent keepalive clip and the tap overwrites the result, which sounds the same as
dropping `Spatial Blend` to 2D.

Per-source audio analysers — AudioLink and anything else built on
`AudioSource.GetOutputData` / `GetSpectrumData` — can't see audio a script generates, so
they read silence from a tap-driven output. `BasisMediaAudioChannel.AnalysisFeed` switches
that output to a streaming `AudioClip` written once a frame instead, which those APIs can
read back. It costs the output a small delay behind the others (`AnalysisFeedLatency`,
50 ms by default, 20–500 ms), so set it on the analyser's own `AudioSource` rather than on
a speaker you listen to.

Channel ceiling depends on the source: **LPCM** — Blu-ray-style over MPEG-TS, or a
**WAV** file — carries a full 7.1 (8 channels); **AAC on Windows** decodes up to 5.1
(the Media Foundation decoder's limit — wider or PCE-signalled AAC layouts play muted
rather than failing the stream; Android decodes what the device's codec supports).

Audio-only sources — a WAV, an MP3, an Ogg Opus file, or an MP4/`.m4a` with no video
track — play through the same outputs with no video output. If an audio-only source's format can't be decoded on
the platform, the load reports an error rather than playing silence.

## Networked sync

`BasisMediaPlayerNetworking` keeps playback aligned across the room. It syncs the
**input URL** — the page URL you entered, not the resolved stream — plus play / pause /
stop. A page URL resolves to a per-client, expiring CDN URL that can't be shared, so each
client resolves the shared page URL itself; direct stream URLs travel verbatim. To keep a
shared load tight, the owner broadcasts a page URL up front so peers resolve it in parallel
rather than only after the owner is playing, and re-loading a URL (even the same one)
restarts every client together.

> **On-demand clients drift-correct by seeking.** The owner broadcasts its playhead, and a
> client whose position drifts more than `DriftSeekThresholdSeconds` (default 2 s) seeks to
> catch up; set it to 0 to disable. Catch-up needs a **seekable** source — a client on a live
> or unindexed stream can't be advanced, so those converge to the live edge instead. A late
> joiner starts the shared source and is pulled into alignment on the next position broadcast;
> an owner seek propagates to the room.

Two resync entry points sit on either side of that. **`ResyncLocal()`** re-aligns the calling
client and nothing else: it asks the room for the current state and reloads onto the answer,
falling back to reloading what it already holds when nobody answers. It takes no ownership and
needs no permission, so the menu offers it under **My Settings** to every client — including
one that can't see the playback controls at all. **`ResyncEveryone()`** goes the other way:
it takes control like any other playback command, then re-announces the load so every client
reloads onto the caller's state and position, the caller included. It is on the **Playback**
tab, behind the same control gate as play/pause/seek. A page URL is re-resolved by each client
rather than reloaded from its expired stream, and the shared URL stays the page URL throughout.

## Building the native plugin

Source is under `Native~/`. By default it links **only OS frameworks** (no
third-party libs). The optional RIST transport (`-DBASIS_WITH_RIST=ON`)
statically links prebuilt librist (which vendors its own mbedTLS) from
`Native~/third_party/`. Build that archive with `Native~/build-librist.ps1`
(Windows) or `build-librist.sh` (Linux/Android), or download it from the
**media-native** CI workflow's artifacts — see
`Native~/third_party/README.md`. Then add `-DBASIS_WITH_RIST=ON` to the cmake
configure step below. You also need Unity's PluginAPI headers — see
`Native~/unity/README.md`.

**Windows → `Plugins/Windows/x86_64/basis_media_native.dll`**
```sh
cmake -S Native~ -B Native~/build -A x64 -DUNITY_PLUGIN_API_DIR="<UnityEditor>/Editor/Data/PluginAPI"
cmake --build Native~/build --config Release
```

**Android (arm64, Vulkan) → `Plugins/Android/arm64-v8a/libbasis_media_native.so`**
```sh
cmake -S Native~ -B Native~/build-android \
  -DCMAKE_TOOLCHAIN_FILE=$NDK/build/cmake/android.toolchain.cmake \
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-29 \
  -DCMAKE_BUILD_TYPE=Release \
  -DUNITY_PLUGIN_API_DIR=<UnityEditor>/Editor/Data/PluginAPI
cmake --build Native~/build-android --config Release
```

After building, set the plugin's platform/CPU in the Unity import settings and the
`Texture2D.CreateExternalTexture` format follows `SystemInfo.graphicsDeviceType`
(BGRA32 on D3D11/D3D12, RGBA32 on Vulkan) — handled in `BasisNativeVideoSource`.

## Known limits

- **RTMP** — handshake/AMF is minimal (simple handshake, no Digest auth, no rtmps).
  `rtsp://` and MPEG-TS are the primary, more-complete paths, with `rtspt://` the
  TCP-pinned option for hosts or networks where UDP never works.
- **HEVC on Windows** needs the system HEVC decoder MFT (HEVC Video Extensions).
- **VP9 on Windows** needs the Store "VP9 Video Extensions" **and** a GPU with
  hardware VP9 (2016-era or newer). Without hardware decode the source errors
  clearly rather than falling back to CPU decode. 8-bit SDR only — a 10-bit
  (profile 2) file surfaces a decoder error, not tone-mapped HDR.
- **AV1 on Windows** needs the Store "AV1 Video Extension" **and** a GPU with
  hardware AV1 (e.g. RTX 30-series / RX 6000 / Arc or newer — examples, not an
  exhaustive list; `basis_media_probe_video_codec` is the authoritative check).
  On GPUs without it, the extension's internal software decoder is rejected, the
  probe answers 0 and the resolver keeps serving VP9/avc1 instead. On Quest, AV1 is hardware on
  Quest 3 (XR2 Gen 2); Quest 2 has no AV1 decoder and errors cleanly on a
  direct `av01` URL. 8-bit Main profile SDR only, as with VP9. MP4/fMP4 and
  WebM carriage only (no AV1-in-TS or RTSP/RTMP).
- **WebM** — VP9/AV1 video and/or Opus audio (`A_OPUS`): muxed VP9/AV1+Opus, or
  audio-only Opus (YouTube's audio legs). Other audio codecs (Vorbis, …) are
  skipped, and a WebM whose video codec isn't supported refuses cleanly rather
  than dropping to audio under a black screen. Seek needs a Cues index and a
  range-capable host; cueless/streamed WebM plays forward-only with no duration.
- **Ogg Opus** — a direct `.opus` URL plays through the same Opus decoder (Ogg
  page framing); duration and granule-bisection seek need a range-capable host.
- **WAV** — 16/24-bit integer PCM only (no float or 20-bit), 1–8 channels, 8–96 kHz.
- **Video output** — the `Equirect360` / `VR180` / `Fisheye` projection modes set a
  shader keyword that no bundled shader implements, so they render the source flat.
  `Picture` needs a shader declaring the `_Basis*` floats, which
  `Basis/Media Player Video` doesn't, so only the UI path's Brightness currently applies.
