# Testing the media player

How to test changes to `com.basis.mediaplayer` without fooling yourself. The README describes
what the player does; this describes how to prove it still does it after your change.

There is no automated test suite for playback — the player's job is realtime A/V against real
networks and real hardware decoders, and regressions live exactly in the parts a mock can't
reach. Testing is therefore structured manual verification: known-good streams, a repeatable
matrix, and evidence capture.

Playback is only half of it. The native plugin parses attacker-controlled container and
protocol bytes in-process, so a change under `Native~/` carries a security exposure that a
playback matrix does not cover. If you are touching the C core, read
[Native plugin changes: the security boundary](#native-plugin-changes-the-security-boundary)
first — it sets the threat model and the malformed-input and fuzz testing that a parser change
needs, over and above "a good file still plays."

## Rule zero: prove the feed before you blame the player

Most "player bugs" found during development turn out to be feeder problems: a stalled stream,
an under-provisioned link, a server that lied about ranges. A player symptom only earns
player-side investigation once the feed is proven healthy — probe it with something that isn't
the player first:

```
# Stream shape + first decodable frame (RTSP; works for any transport ffmpeg speaks)
ffprobe -rtsp_transport tcp -show_entries stream=codec_type,codec_name,channels rtsp://host:8554/path

# Wall-time to first video frame — long GOPs make mid-stream joins slow by nature
ffmpeg -v error -rtsp_transport tcp -i <url> -map 0:v:0 -frames:v 1 -f null -
```

Things that regularly masquerade as player bugs:

- **Link capacity.** A 28 Mbps stream over a 22 Mbps path cannot play live, and no player-side
  change will fix it. Measure the actual path throughput before investigating stutter (on
  Windows, `curl.exe` under-reads badly — use a timed `urllib` read from Python instead).
- **Mid-GOP joins.** Joining a live stream between keyframes delivers audio immediately and no
  video until the next IDR. With a 10-second GOP that is a 10-second "video hang" that is
  entirely the source's fault. Know your test stream's GOP.
- **Single-client feeders.** `ffmpeg -listen 1` serves one client, then lingers in a stale
  state that trickles junk. If a second connection behaves strangely, restart the feeder.
- **Wrong delivery detection.** `Delivery = Auto` probes `Range: bytes=0-` and needs a real
  `206 Partial Content` to detect on-demand content. `python -m http.server` and
  `ffmpeg -listen` answer `200` and get treated as **live**. Serve VOD files from nginx (or
  anything with real range support).

Script these probes over whatever endpoints you use before a test session — a stalled or
mis-configured feed wastes far more time than the 30 seconds a probe takes.

## Where test streams may live (the security gates)

`BasisMediaPlayerSecurity` validates every URL before the engine opens it
(`Runtime/Core/BasisMediaPlayerSecurity.cs`). The rules shape where your streams must run:

| Rule | Effect on testing |
| --- | --- |
| Loopback allowed **in the Editor only** | `localhost` streams work for fast in-editor iteration; the same URL is refused in a build |
| Non-global-unicast addresses always blocked | RFC1918 (`192.168.*`, `10.*`, `172.16-31.*`), CGNAT, loopback, link-local, and the IANA special-use reserves (TEST-NET, benchmarking, 6to4 relay). LAN servers never work, Editor included — don't bother |
| Hostnames are DNS-validated, fail-closed | A name that resolves to any of the above (or doesn't resolve) is refused |
| Scheme allowlist | `http`, `https`, `rtsp`, `rtspt`, `rtmp`, `rtmps`, `rist` — anything else (incl. `file://`) is refused. Passing the gate isn't the same as playable, though: `rtmps` (RTMP-over-TLS) is allowlisted but the player rejects it (use `rtmp://`, or an https fMP4/TS URL), and `rist` only works in the opt-in `-DBASIS_WITH_RIST=ON` build |

Practical consequences:

- **Editor iteration:** point at a public endpoint, or run your own server (RTSP/RTMP/HTTP) locally
  and use `localhost` URLs.
- **Builds, Quest, multi-client tests:** the stream must come from a **public host with real DNS** —
  a public endpoint below, or your own content on any cheap VPS.
- **Quest/Android:** the OS cleartext policy blocks plain `http://` on the JNI fetch path —
  HTTP-TS and HLS lanes need `https://` with a certificate chain the device actually trusts
  (serve the full chain; standalone headsets are missing more roots than desktop browsers).
  `rtsp://` is unaffected.
- **Native local-address re-check (RTSP/RTMP/HLS):** the C# gate above is the first line and is
  **not** affected by the env var below — a top-level RFC1918 URL stays refused by C# regardless,
  and a top-level `localhost` URL works only in the Editor (the C# rule). Behind it, the native
  layer independently re-checks resolved addresses for the transports it opens directly — RTSP/RTMP
  (via `basis_io`) and every HLS playlist/segment fetch (the SSRF re-check that stops a hostile
  playlist steering a sub-resource URI at an internal host). That native re-check has no Editor
  concept, so it refuses `localhost` (and any private address it is handed directly, e.g. an HLS
  segment URI the C# gate never saw) unless `BASIS_MEDIA_ALLOW_LOCAL` is set (any non-empty value).
  Setting it relaxes **only** that native re-check, not the C# gate — so its practical use is
  running your own RTSP/RTMP/HLS server at `localhost` in the Editor. Plain HTTP(S) MP4/TS via the
  platform stack (WinHTTP/JNI) has no native re-check and needs no opt-in.
- The separate world-content trust allowlist (`BasisDefaultTrustedUrls`, https-only) gates the
  sandboxed `VideoPlayer` shim path, not this package — but streams hosted on already-trusted
  domains spare testers a consent prompt when worlds use the same URL.

## Public always-on endpoints (zero setup)

These cover the common lanes without standing up anything. They are third-party services —
fine for interactive test sessions, not for soak loops.

| URL | Exercises | Notes |
| --- | --- | --- |
| `rtsp://stream.vrcdn.live/live/vrcdn` | RTSP live, H.264 720p + AAC 2.0 @ 48 kHz | VRCDN's own 24/7 channel; the primary PC low-latency lane; host is on the default trust list |
| `https://stream.vrcdn.live/live/vrcdn.live.ts` | MPEG-TS over HTTPS, live | Same channel, the standalone-friendly lane (https, so Quest-safe) |
| `https://download.blender.org/peach/bigbuckbunny_movies/BigBuckBunny_640x360.m4v` | Progressive MP4 VOD, range/`206` | Official Blender hosting of the full 10-minute film, H.264 + AAC (`.m4v` is recognised as an MP4 extension). Good for seek/pause and delivery auto-detect testing |
| `https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` | HLS VOD, multi-variant master | Exercises the panel's bitrate dropdown |
| [Fraunhofer AAC multichannel page](https://www2.iis.fraunhofer.de/AAC/multichannel.html) | AAC 5.1/7.1 VOD fixtures | Includes adversarial layouts: PCE-signalled 7.1 must fail **gracefully** on Windows (muted audio or a clean error — never a crash) |

> Live endpoints join mid-GOP like any live stream — audio-before-video on join is expected
> behaviour, not a regression, unless the gap exceeds the stream's GOP length.

**Page URLs (YouTube, Twitch, …) are deliberately absent from this guide.** They only work
through an optional resolver integration, so the base package can't assume they're testable.
Everything in this document uses direct stream URLs; resolver-dependent testing lives in the
integration package that provides it — e.g.
[`com.basis.integration.ytdlp/TESTING.md`](../com.basis.integration.ytdlp/TESTING.md).
The same split applies to any future integration: endpoints that need an integration package
to function are tested in that package's own TESTING.md.

## Lanes without a public endpoint: bring your own

The public endpoints above cover the common lanes. The rest — the live transports (RTSP/RTMP/RIST),
split-stream pairs, `localhost` iteration, and a couple of fixtures no public stream carries
reliably (CEA-608 captions, LPCM 7.1 over M2TS) — you provide yourself. There is no bundled
test-server stack to maintain; stand up whatever server you already use and point your own files at
it.

What the manual pass is actually for: the CI conformance gate (`tools/media-conformance`) already
proves the **demuxers** parse every supported container/codec correctly — on synthetic fixtures, on
every native change. What it cannot touch is real **decode + present** on actual hardware, A/V sync,
and the live network transports. That is exactly what this matrix covers, and it needs real files
and servers.

Supported inputs to keep on hand (generate with `ffmpeg`, serve however you like):

- **Containers:** MP4 / fragmented MP4 (`.mp4` `.m4v` `.m4a` `.m4s`), MPEG-TS (`.ts`) and
  Blu-ray/AVCHD M2TS (`.m2ts` `.mts`), WebM/Matroska (`.webm`), Ogg (`.opus`), MP3 (`.mp3`),
  WAV (`.wav`), HLS (`.m3u8`, TS- or fMP4-segmented).
- **Video codecs:** H.264, H.265/HEVC (`hvc1`), VP9 (WebM and `vp09`-in-MP4), AV1 (progressive and
  fragmented MP4, and `V_AV1` WebM).
- **Audio codecs:** AAC (≤ 5.1 on Windows, discrete 5.1 on Android), Opus (WebM and Ogg), MP3 (bare,
  and `esds` OTI `0x6B`/`0x69` in MP4), LPCM (WAV, and 7.1 over M2TS).
- **Transports:** any RTSP/RTMP server (e.g. MediaMTX), an `ffmpeg`-served HTTP-TS feed, nginx (or
  anything with real `Range`/`206` support) for VOD, an HLS packager, and — for the opt-in
  `-DBASIS_WITH_RIST=ON` build — a RIST sender (ffmpeg/librist), plain and AES.

Two feeder traps worth repeating: a VOD host must answer `206 Partial Content` or `Delivery=Auto`
mis-detects it as live (`python -m http.server` and `ffmpeg -listen` answer `200` — use nginx); and
for A/V-sync work use **real footage with visible lip-sync**, not synthetic patterns.

## The regression matrix

Run the rows your change plausibly touches; run everything before a release-bound merge.
"Verify" always includes: plays within a sane time, A/V stays in sync, no console errors
(`BasisDebug` tag `Video`), clean stop/unload.

### Transports

| Lane | Source | Verify additionally |
| --- | --- | --- |
| RTSP live | VRCDN, or your own RTSP server (e.g. MediaMTX) | Join latency ≈ GOP-bound; pause/resume recovers cleanly. `rtsp://` negotiates UDP transport first and falls back to TCP-interleaved; the Console logs the settled choice once per load (`[NativeMedia] transport: RTSP over UDP`), and it's queryable via `BasisMediaPlayer.CurrentTransport` |
| RTSP adversarial join | your own RTSP server fed a long-GOP source | Audio leads video by up to the GOP length on join, then locks — no permanent desync |
| RTSP refusal fallback | your own RTSP server configured TCP-only (`rtspTransports: [tcp]` in MediaMTX) | UDP SETUP is refused (461); playback is indistinguishable from today, no error surfaced; Console logs `RTSP over TCP (UDP unavailable)` |
| RTSP timer fallback | any network that silently drops the RTP UDP ports (`8000-8001/udp`) | First join stalls ~3 s, then restarts transparently over TCP with the same fallback log line; a reload of the same host skips the probe and goes straight to TCP |
| RTSP forced TCP | `rtspt://` form of any RTSP URL | No UDP attempt at all (no UDP `SETUP` in the server log); Console logs `RTSP over TCP` |
| HTTP-TS live | VRCDN `.live.ts`, or your own `ffmpeg`-served TS | Same checks over plain TS; on Quest use the https lane |
| HLS VOD | Mux master, or your own HLS packaging | Variant switch via panel bitrate dropdown mid-play |
| Progressive/fMP4 MP4 | Big Buck Bunny | `Delivery=Auto` detects OnDemand (needs the 206); seek slider works |
| RTMP | your own RTMP server (e.g. MediaMTX) | Minimal client — plain `rtmp://` pull only |
| RIST plain + AES | your own RIST sender (ffmpeg/librist) | Requires RIST-enabled plugin build; loss recovery under induced packet loss |
| WAV audio-only | your own WAV over HTTP | 16/24-bit, up to 8 ch; no video track is not an error |
| Split-stream | your own video-only + audio-only pair | Windows-only today; `AudioUri` lane syncs to video |

### Content and codecs

No public host carries every codec in every container flavour, so generate these from a CC
clip — Big Buck Bunny (full URL in the endpoints table above) or any Blender open movie — with
the `ffmpeg` recipe in each row. `in.mp4` below is that source clip. For higher-res / 4K masters,
[media.xiph.org](https://media.xiph.org/) mirrors the Blender films losslessly (e.g. Sintel 4K at
`https://media.xiph.org/sintel/sintel-4k.y4m.xz`, and `sintel-4k-png/` frame sets) — grab one and
cut a short segment (`ffmpeg -i sintel-4k.y4m -t 20 -c copy in4k.y4m`). (The demux side is already
covered bit-for-bit by the CI conformance gate; these rows are the real decode + present pass.)

| Fixture | Verify |
| --- | --- |
| H.264 + AAC stereo | The baseline — everything else assumes this passes |
| H.265/HEVC | **Video actually appears** (`ffmpeg -i in.mp4 -c:v libx265 -tag:v hvc1 -c:a aac hevc.mp4` — `hvc1` from stock libx265, what most HEVC in the wild is). Check for frames, not for the absence of an error: `hvc1` keeps its parameter sets only in the `hvcC` box, so anything that loses them on the way to the decoder gives a black screen with no error raised and nothing in the Console. Absence of the codec is the other half of the row — without the HEVC Video Extension installed it must degrade cleanly, and testing only that half will pass while playback is comprehensively broken |
| VP9 in WebM (`ffmpeg -i in.mp4 -c:v libvpx-vp9 -b:v 0 -crf 32 -c:a libopus vp9.webm`; two-pass for superframes) | Plays on Windows (Store "VP9 Video Extensions" + a GPU with hardware VP9 — the probe gates both) and Quest (hardware everywhere). A two-pass encode carries superframes, so whole-superframe feeding is exercised by playing it |
| VP9 in MP4 (`ffmpeg -i in.mp4 -c:v libvpx-vp9 -c:a aac vp9.mp4`; modern ffmpeg writes the `vp09` sample entry) | The `vp09` sample-entry lane; same decode path as WebM |
| AV1 in progressive MP4 (`ffmpeg -i in.mp4 -c:v libaom-av1 -crf 30 -c:a aac av1.mp4`) | Plays with video on Windows (Store "AV1 Video Extension" + a GPU with hardware AV1 — RTX 30+/RX 6000+/Arc; the probe gates both) and Quest 3. AV1-in-MP4 historically misplayed as silent audio-only |
| AV1 in fragmented MP4 (the AV1 MP4 recipe + `-movflags frag_keyframe+empty_moov`) | The `av1C`-in-`stsd` fMP4 walk with the configOBU first-AU prepend |
| AV1 4K (a 2160p slice of Sintel 4K — see the intro above — through the AV1 MP4 recipe) | 2160p decode + ring memory on both platforms |
| AV1 in WebM (the AV1 recipe with `av1.webm`) | The `V_AV1` CodecID lane (CodecPrivate = av1C record → configOBU extradata); duration + Cues seek work as for VP9 |
| AV1 extension absent (Windows) | Uninstall/absent "AV1 Video Extension": a direct `av01` URL errors with the install hint, and the probe answers 0 so the resolver never offers AV1 |
| AV1 on Quest 2 | No AV1 decoder on the device: a direct `av01` URL refuses cleanly, and YouTube resolution still succeeds via the VP9 lane (its probe passes there) |
| Opus in muxed WebM (`ffmpeg -i in.mp4 -c:v libvpx-vp9 -c:a libopus vp9_opus.webm`) | VP9 video + Opus audio in one file: plays whole with audio on Windows and Quest. Exercises the two-track WebM demux (blocks routed to video vs audio by TrackNumber) |
| Opus audio-only WebM (`ffmpeg -i in.mp4 -vn -c:a libopus opus.webm`) | An `A_OPUS`-only WebM (YouTube's audio itags 249/250/251): audio plays with no video, driven by the audio-only contract |
| Opus decode on Windows | Native via the libopus that `com.avionblock.opussharp` ships, runtime-loaded (no Store extension, unlike VP9/AV1). Confirm audio plays in the Editor (the library resolves from the opussharp `Packages/…` path) and in a build (`opus.dll` flattened beside the plugin). If opussharp is absent the format is refused: muted audio, video unaffected, never a crash |
| Opus on Quest | Native `audio/opus` MediaCodec with OpusHead + pre-skip/pre-roll csd; gapless start sane, audio-only path works |
| Ogg Opus file (`.opus`) | A `.opus` URL routes as directly-playable (no resolver) and plays: the Ogg demuxer walks pages/lacing, verifies each page CRC, reads OpusHead, and feeds the same Opus decoder. A `.opus` with a damaged page resyncs on the next `OggS` rather than failing |
| Ogg Opus seek (`.opus`) | On a range/`206` host, a `.opus` file reports its duration (a seek bar appears) and seeks — Ogg has no index, so seek is granule bisection over the byte range; it lands at page granularity near the target and resumes. A live/no-range source has no seek bar (duration 0), which is correct. Check the Editor (Windows) |
| Unsupported video codec | VP8 (`ffmpeg -i in.mp4 -c:v libvpx vp8.webm`) and MPEG-4 Part 2 (`ffmpeg -i in.mp4 -c:v mpeg4 mp4v.mp4`) refuse with a clear "video codec 'x' is not supported" error naming the codec — never silent audio under a black screen |
| VP9/AV1 software-fallback guard | On a GPU without hardware decode for the profile, a direct VP9/AV1 URL must produce the "video decoder produced software frames" error, not a black screen (the Store MFTs silently fall back to CPU — for AV1 that is the *majority* of pre-RTX-30 desktops; only reproducible on a no-hw box or with the extension's fallback forced) |
| AAC decoder priming | Audio starts on the first real sample, not on the decoder's priming. AAC's encoder delay is one 1024-sample frame, which MP4 signals with an edit list (`elst media_time=1024` on anything `ffmpeg -c:a aac` produced); the samples ahead of that origin must not reach the output. **Do not try to hear this** — 21 ms of lag is below the lip-sync threshold, which is exactly why it went unnoticed for so long. Measure it: decode the file with `ffmpeg -i x.m4a -map a:0 -f f32le -acodec pcm_f32le ref.f32`, capture what the player served, and cross-correlate. Assert on the **peak's sample offset**, not a correlation value: aligned output peaks at offset 0, a stream still carrying its priming peaks at offset 1024 (the edit-list delay) — the actual defect, and reliable regardless of content, channels, or capture. (The absolute coefficient at offset 0 is content-dependent — a shifted stream reads roughly -0.07 on this fixture, but do not gate on that number.) An LPCM/WAV file is the control — no decoder, no priming, peaks at offset 0 |
| AAC 5.1 | Windows MF decodes ≤ 5.1; correct channel mapping (use content with known channel placement, judge by ear per output speaker) |
| AAC 5.1 in a progressive MP4 (Android) | Decodes to discrete 5.1, not silence. Generate a 5.1 AAC MP4 (`ffmpeg -i in.mp4 -c:a aac -ac 6 aac51.mp4`). The esds can carry an inert SBR sync extension the Android decoder otherwise rejects (`aacDecoder 0x1001` in logcat) — that extension is encoder-dependent, so use a clip that carries it when chasing that path |
| MP3 bare stream (`.mp3`) | CBR and VBR play forward; a leading `ID3v2` tag is skipped and a Xing/Info/VBRI header frame is dropped (not heard as a click). Duration is reported from the header's frame count and the seek slider works. Windows uses the in-box Media Foundation MP3 decoder, Quest the `audio/mpeg` MediaCodec. Generate fixtures with `ffmpeg -i src.wav -c:a libmp3lame -b:a 192k cbr.mp3` and `-q:a 2 vbr.mp3` |
| MP3 in MP4/M4A | An `mp4a` sample entry whose `esds` object-type-indication is `0x6B`/`0x69` plays as MP3, not misdetected as AAC (`ffmpeg -i cbr.mp3 -c copy out.m4a`) |
| LPCM 7.1 M2TS | All 8 lanes audible and correctly placed — the only full-7.1 path on Windows |
| PCE-signalled / >6-ch AAC | **Graceful refusal** on Windows (mute or clean error, never a crash) |
| Trailing-moov progressive MP4 | Non-faststart file (`ffmpeg -i in.mp4 -c copy out.mp4` leaves `moov` after `mdat`): on a range/`206` server it plays with seek + duration; over a one-way stream (no ranges) it refuses cleanly with a faststart-remux hint |
| CEA-608 captions | A caption-bearing TS you generate (no public stream carries captions reliably): cues appear on time, accented characters correct, clear-cue clears, CC toggle + opacity sliders live-apply |
| 44.1 kHz audio | Resamples cleanly to the DSP rate (dominant path is 48 kHz — don't let 44.1k rot) |
| Non-16-aligned coded height | No pad strip on the video edge (a thin top strip on Windows, a grey bottom strip on Android) and the RenderTexture matches the display aspect. 720p and other 16-aligned heights are clean, so test a padded height specifically — 1080p (→1088) on Windows, 640×360 (→368) on Android |

### Platforms and backends

| Target | Notes |
| --- | --- |
| Windows D3D11 | Default editor/player path |
| Windows D3D12 | Launch with `-force-d3d12`; shared-handle texture path is separate code — video must appear, no `dxgi-fmt` errors in the log |
| Android/Quest | Vulkan path, `AMediaCodec`; https for TS/HLS lanes; check `adb logcat` for codec errors; AAC 5.1 arrives in WAVE order. 5.1 AAC in a progressive MP4 decodes discretely (see the codec row); the coded-height pad is cropped off the present (grey bottom strip) |
| Desktop ↔ VR swap | Toggle mode mid-playback — the external texture must survive the graphics-device swap |
| Linux via Proton/Wine | The Windows build under a compatibility layer. No lane here can stand in for it: the plugin runs against Wine's reimplementations of WinHTTP, Media Foundation and D3D11, so behaviour can differ from native Windows even though the binary is identical. Loading the plugin at all is gated behind a one-off prompt (Media Foundation may be absent). Verify a VOD plays at 1x rather than racing through the content — delivery pacing depends on the seekability probe reading Content-Length, and a probe failure shows up as synchronised fast-forward at roughly the download-speed-over-bitrate ratio. Confirm the seek slider works and that a late joiner syncing to a mid-VOD playhead lands at the right position, since both need the same probe. Testing this needs a real Proton user; there is no rig for it here |

### Behaviour checklists

**Playback lifecycle** — load → play → pause → resume → stop → replay same URL → load a
different URL mid-play. No stale frames, no orphaned audio, position resets correctly.

**Seek (VOD)** — slider to arbitrary positions; rapid successive seeks (input is debounced);
seek-then-pause shows the sought frame. The byte-source ranged refetch that backs a seek now
runs on **Android** too (JNI `HttpsURLConnection`), not just Windows — run the same slider
checks on a Quest against a range/`206` VOD host (`https://`), watching `adb logcat` for a clean
reposition (no decoder error, playback resumes at the target). A container seek repositions to
the sync point at or before the target and the run-up from there is decoded but never shown, so
playback lands **at the requested position** — never visibly replaying from the keyframe, and
never crawling the gap at 1x. The adversarial case is a **sparse-keyframe file** (tens of
seconds per GOP, e.g. a low-motion screen capture): a mid-GOP seek there should recover after a
short silent pause that scales with decode speed, not with the distance seeked.

**Seek (HLS-TS VOD)** — on the Mux master (`https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8`),
seek both directions and confirm playback resumes **paced at 1x from the target**: a forward
seek must not freeze for the jump distance, and a backward seek must not fast-forward through
the intervening segments back to the pre-seek position. The segment producer repositions at
segment granularity but the landing is **target-exact**: the run-up from the segment boundary
to the target is decoded and discarded, so playback must resume at the requested position, not
the start of the containing segment. A mis-anchored pace clock (stall forward / flood backward)
is the failure to watch for. Also seek **from the tail**: once the fetcher has downloaded every
remaining segment (the last buffer's worth of the stream, so roughly the final ten seconds of a
short VOD) it parks rather than exits, and a backward seek from there must still reposition —
a bar that flashes the target and snaps forward means the parked-fetcher revival broke. Playing
through to the end must present the tail before ENDED is raised: the position walks all the way
to the true duration and the final content is actually shown and heard — ENDED firing early
while banked audio or video is discarded is the failure. Shared clock, so check both the Editor
(Windows) and Quest.

**Seek (integrated fMP4)** — on a self-contained fragmented MP4 (moof/mdat fragments indexed by a
top-level `sidx`) served from a range/`206` host. Produce one from a CC clip:
`ffmpeg -i in.mp4 -c copy -movflags +frag_keyframe+empty_moov+global_sidx out.mp4` (the `global_sidx`
box is what the byte-source seek indexes). Confirm
`Delivery=Auto` detects OnDemand and seeks in both directions reposition cleanly and resume at the
target with no decoder error. This is the `sidx`-driven byte-source reseek; it shares the
byte-source seek path with progressive/trailing-moov MP4, so a regression here usually surfaces on
those too. Distinct from fMP4 carried *in HLS*, which isn't seekable — a mid-fragment ring flush
can't resynchronise the box parser. Check the Editor (Windows) and Quest.

> On-demand multiplayer sync **drift-corrects by seeking**: the owner broadcasts its playhead
> and a client that drifts past `DriftSeekThresholdSeconds` seeks to catch up (set 0 to
> disable). Catch-up needs a seekable source — TS-segment HLS VOD, progressive/trailing-moov
> MP4, and integrated fMP4 qualify; a live source can't seek, so those clients converge
> independently to the live edge rather than using playhead-seek correction.

**Seek (WebM Cues)** — on your VP9 WebM fixture (the codec-row recipe; a `libvpx-vp9` encode
carries Cues) served from a range/`206` host, seek both directions: playback lands at or just
before the target (cue/cluster granularity, on a keyframe) and resumes paced at 1x — the same
stall-forward / flood-backward failure shapes as the HLS row apply. Seek near the very end of the
file as well (EOS race). A cueless variant (`-cues_to_front 0`, or strip the Cues) must show no
seek bar at all. Your AV1 WebM fixture rides the same cue walk with the AV1 branch — one
both-directions pass there covers it. Check the Editor (Windows) and Quest.

**Seek (MP3)** — on a `.mp3` VOD over a range/`206` server, seek both directions and near the
end. MP3 seek is inherently approximate (no per-frame timestamps): CBR lands within a frame via
the bitrate mapping, VBR uses the Xing TOC, so the playhead may land a fraction of a second off
the slider — that is expected, a permanent desync or a stall is not. A `.mp3` with no Xing/Info
header reports no duration and shows no seek bar.

**Networking** — two clients minimum: owner loads URL → both play; non-owner requests control
→ ownership transfers; owner pause/stop propagates; late joiner receives current state; each
client resolves the URL independently (per-client CDN/bitrate differences are fine, state
divergence is not). End-of-stream is per-client: a late joiner runs behind the owner by its
join latency and must play through to its own end of the content — the owner finishing first
must not cut it off. Clients therefore finish at slightly different wall-clock times; a peer
stopping short of the end is the failure, synchronised finishes are not expected.

**Networked audio-only** — the same two-client setup with an audio-only URL (`.wav`, `.mp3`,
`.m4a`, `.opus`). These carry no video track, so anything that waits on a video frame or an
output texture never fires for them, and a readiness regression here is invisible on the
owner's own client — it plays locally either way. Load one while the peers are mid-playback of
something else: they must switch to it, not carry on and then resume the old source when it
ends. Check the peer starts near the beginning rather than at the outgoing video's playhead,
and that a late joiner receives it too. Worth a pass on a peer with
`AutoPlayOnSourceAssigned` unticked, which is the case that relies on the owner's advertised
state rather than local autoplay.

**Panel UI** ("Media Players" panel, `Runtime/UI/BasisMediaPlayerPanelProvider.cs`) — URL
load, transport buttons, seek slider (VOD only), volume, bitrate dropdown (HLS multi-variant),
audio-track dropdown (multi-audio content), captions toggle + opacity sliders, subtitles
dropdown (only when the loaded media offers sidecar subtitle tracks — resolver-supplied, so
the scenarios live in the resolver package's guide; with plain stream URLs the dropdown must
be entirely absent). Controls that don't apply to the loaded media should be absent or inert,
not broken.

**Security gates** — negative tests matter: `http://192.168.1.10/x.ts` must refuse with a
clear reason on every platform (that RFC1918 refusal is the C# gate and holds regardless of any
env var); a plain HTTP(S) `localhost` MP4/TS URL must refuse **in a build** and work in the
Editor; `file:///` must refuse. On the native-transport lanes (HLS, RTSP, RTMP) even the Editor
`localhost` case is refused unless `BASIS_MEDIA_ALLOW_LOCAL` relaxes the native re-check (see the
security-gates section above) — a refusal there without the opt-in is correct, not a regression.
A regression that *opens* a gate is a security bug — flag it as such, not as a playback bug.

**HLS sub-resource SSRF** — the URL gate only sees the top-level playlist, so the native
source re-checks each URI a playlist steers it to. Serve a media `.m3u8` from a public host
whose segment (or `EXT-X-MAP`, or a nested variant) URI is an absolute
`http://169.254.169.254/…` / `http://192.168.x.x/…` / `http://127.0.0.1:PORT/…`: playback must
fail rather than issue that fetch (watch the target server's logs — the internal host must see no
request). A playlist that reaches an internal host is a security regression, not a broken-stream
bug. (Editor testing of the legitimate localhost lane needs `BASIS_MEDIA_ALLOW_LOCAL` — see the
security-gates section above.)

**A/V sync judgement** — use real footage with **visible speech**; synthetic patterns hide sync
drift, and Big Buck Bunny (the baseline endpoint) has no dialogue at all. A CC-BY Blender open
movie with clear lip-sync is a good source — Sintel and Spring both work; download from
[Blender Studio films](https://studio.blender.org/films/) and re-encode/serve as needed. Watch a
full minute at the live edge, not five seconds. For anything subtle, capture diagnostics (below)
rather than trusting perception.

Know what this row cannot do, and do not treat audibility as the pass bar. A fixed offset
below roughly 45 ms is still a real A/V-sync regression — it is just below the threshold where
watching harder will find it, so this perceptual row structurally cannot see it. Audibility
only explains why manual observation is insufficient here; it is not an acceptance tolerance.
Anything with a constant delay in it (decoder priming, buffer alignment, resampler latency)
must be measured against a reference decode with an explicit tolerance, not signed off by ear.

**Orientation** — a horizontal mirror is invisible on symmetric content. Verify left/right
with on-screen text or a logo, every time video-path code changes.

## Diagnostics and evidence

- **`BasisMediaPlayerDiagnostics`** (`Runtime/BasisMediaPlayerDiagnostics.cs`): add the
  component next to a `BasisMediaPlayer`, enable `AutoStart` (or call `StartLogging()`), and
  it samples ~50 snapshots/s to `Application.persistentDataPath/BasisMediaPlayerDiag.csv`
  (Windows: `%USERPROFILE%\AppData\LocalLow\<company>\<product>\`). Useful signals:
  `eng_ttff_ms` (time to first frame), `engine_pos_us` step distribution (late presents show
  as double-steps coinciding with wall-clock gaps), `eng_lag_ms`/`eng_buf_ms` (clock vs
  buffer health), `audio_queue_depth`/`eng_audio_trims` (audio starvation/overrun),
  `cpu_*_drops/skips` (CPU-path frame accounting). Filter rows to `engine_state == Playing`
  before drawing conclusions.
- **Debug window**: `Basis → Debug → Media Player Debug` shows live engine state per player.
- **Feedless harness**: `BasisSyntheticTestSource` (`Runtime/Sources/`) drives the player
  without any network feed — isolates render-path changes from transport noise.
- **Logs**: the package logs exclusively under the `Video` tag via `BasisDebug`. On Android,
  `adb logcat -s Unity` plus the codec tags carry the native side.

## Reporting a regression

A report that can be acted on contains:

1. The exact URL (or how to reproduce the source — server + asset recipe) — full URL, not a fragment
2. Platform, graphics API, Editor-or-build, headset if relevant
3. What was expected, what happened, and how reliably it reproduces
4. Console output around the failure (the `Video`-tagged lines) and, for timing/sync issues,
   the diagnostics CSV covering the incident
5. Whether `ffprobe` of the same URL was green at the time

## Acknowledgements

The always-on live lanes above are [VRCDN](https://vrcdn.live/)'s own public channel, listed
here with their permission — thanks to the VRCDN team for keeping a reliable 24/7 reference
stream running and for letting this guide point testers at it. Be a good guest: use it for
interactive test sessions, not automated soak loops, and stand up your own server for anything
sustained.

## Native plugin changes: the security boundary

`Native~/` is where the player is most exposed, and a change there is not verified the same
way a C# change is. The C core parses container and protocol bytes **by hand** — MP4 box
walking (`esds`/`avcc`/`hvcC`), MPEG-TS section parsing, RTSP/RTMP, WebM — and it does so
**in-process, with no sandbox**. The bytes are attacker-controlled: a media URL is opened
from world content and, in multiplayer, broadcast by a peer so that every other client parses
the same hostile stream at once. A parser that reads past a buffer, trusts a length field it
never bounds-checked, or dereferences a pointer it never validated is therefore reachable
remotely, on every client simultaneously.

Two outcomes to test against, in priority order:

- **Denial of service** — the common, proven case. A malformed stream crashes or hangs the
  decode thread and takes the process (editor or client) down with it. This has happened from
  an ordinary `ffmpeg`-produced file: an HEVC elementary stream that reaches the decoder with
  no frame size made the Windows Store HEVC MFT dereference a null pointer on its own worker
  thread. The parser must refuse a sizeless or otherwise under-specified track **before** it
  hands bytes to the decoder, not let it fail somewhere downstream.
- **Memory corruption** — the worst case, and the reason this is a security boundary and not
  just a stability one. Hand-rolled parsers with untrusted lengths are exactly where
  out-of-bounds reads and writes live. Treat *any* out-of-bounds access as a security bug,
  including a read that "only" crashes — the same missing bound is often writable with a
  different input.

So "a good file still plays" does not verify a parser change. Proving a *hostile* file cannot
crash, hang, or corrupt does.

### What to test after a parser or protocol change

- **Malformed and truncated input, expecting a clean refusal.** For every parser you touch:
  truncate the file mid-box or mid-packet; corrupt a length or size field so it points past
  the buffer; set a dimension, channel count, or entry count to zero or to `UINT32_MAX`; nest
  boxes to absurd depth; point an offset back at itself. The bar is **errors cleanly, never
  crashes or hangs** — a surfaced error string is a pass, a segfault or a spin is a failure.
  Valid-file checks miss all of this by construction; the regressions live in the inputs the
  author didn't picture.
- **Fuzz the demux and parse entry points under sanitizers.** The harness for this lives at
  [`tools/media-fuzz/`](../../../tools/media-fuzz/): coverage-guided libFuzzer targets that
  drive the real `protocol/*.c` readers under AddressSanitizer + UndefinedBehaviorSanitizer,
  no decoder or Unity needed (`./build.sh`, then run a target against a seed corpus). ASan
  turns a silent out-of-bounds read into a named fault with a stack — it is both how you find
  these and how you prove one is gone. An unsanitised "it didn't crash this time" is not proof.
  Fuzzing corrupt input is the single highest-value test this code has; a parser change that
  ships without a fuzz pass is under-tested. When you add a parser, add a `fuzz_<name>.c` target
  beside the others. Targets exist for the container demuxers (TS/MP4/WebM/Ogg/MP3), the caption
  scanner, the URL parser (`fuzz_url`), the HLS playlist source (`fuzz_hls`), and the RTSP/RTMP
  parsers (`fuzz_rtsp`/`fuzz_rtmp` — their harness `#include`s the real `.c` and stubs `basis_io`,
  byte-serving the read paths; `parse_sdp`/`depkt_*`/`amf_*`/FLV tag parsers are driven directly).
  Deeper full-session coverage (a scripted handshake or an injected transport vtable) is a documented
  follow-up. Still exercise all of these with the adversarial live-server rows above
  (truncated/oversized headers, a server that never sets the RTP marker) — fuzzing complements the
  matrix, it doesn't replace it.
- **Keep every crash's repro as a permanent fixture.** When a malformed stream is found to
  crash, the exact file that triggered it is pinned under `tools/media-fuzz/testcases/` and
  replayed by the `fuzz-demux` CI job (`media-native.yml`) on every native change — a fixed
  memory-safety bug that isn't pinned by a regression repro comes back the next time the
  surrounding code moves.
- **Regress the good path bit-for-bit, not by eye.** A protocol fix on one transport can shift
  the packets another transport emits, because they share the AU path. After any demux change,
  re-run the known-good fixtures and confirm the demuxer still produces the same packets and
  the same decoded frames. `ffprobe -show_packets` alone only compares metadata, not payload, so
  a byte regression with unchanged sizes/timestamps would slip through — the conformance gate
  compares per-packet payload hashes (`-show_data_hash md5`) against ffprobe, and `ffmpeg` frame
  hashes cover pixels. Those hashes are what make "the same" objective instead of "looked fine
  to me."

### Rebuilding and platform coverage

Any change under `Native~/` needs the rebuilt binaries verified on **both** platforms it ships
for (Windows x64 DLL, Android arm64 `.so`) — the shared C core means a protocol fix on one
platform can regress the other, and the malformed-input and fuzz checks above apply to each
backend's decode path (Media Foundation on Windows, `AMediaCodec` on Android) as well as the
shared parsers. See the README's "Building the native plugin" section. Note the Windows DLL
cannot be replaced while any Unity instance holds it loaded — close Unity, swap, reopen.
