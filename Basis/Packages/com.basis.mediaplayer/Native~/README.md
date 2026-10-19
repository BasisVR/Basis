# basis-media

The Rust engine behind `com.basis.mediaplayer`. It decodes with the platform's
decoders (Media Foundation with D3D11VA on Windows, MediaCodec on Android) and
hands frames to Unity on the GPU. Container and protocol parsing (MP4,
MPEG-TS, MKV/WebM, HLS, RTSP/RTP, WHEP, raw audio) is
`#![forbid(unsafe_code)]` in the engine's own crates; RIST goes through librist
(C) behind FFI. The package's managed code is in `../Runtime/`, and
the built binaries are committed in `../Runtime/Plugins/`.

| Crate | Role |
| --- | --- |
| `media-ffi` | The plugin boundary Unity loads |
| `media-engine` | Sessions and their pipelines |
| `media-clock` | The media clock and drift correction |
| `media-bank` | The buffer between demux and decode |
| `media-demux` | Container demuxers |
| `media-bitstream` | Elementary-stream parsing (Annex-B, captions, SEI) |
| `media-hls` | HLS playlists, segments and Low-Latency HLS parts |
| `media-io` | Sockets and files, with the address rules |
| `media-rtp`, `media-rtsp` | RTP and RTSP |
| `media-whep` | WHEP (WebRTC receive) |
| `media-rist` | RIST, through librist |
| `media-decode` | The decoder trait, with `decode-mf` (Windows), `decode-mediacodec` (Android) and `decode-sw` (FLAC, Opus, AV1, PCM) |
| `media-present` | Frame hand-off to Unity: Direct3D 11 and 12 on Windows, Vulkan on Android |
| `media-diag` | Counters, the event log and the capture writer |
| `media-testkit` | Recorded network-delay profiles for tests |
| `bm-probe` | A command-line player for running the engine without Unity |

```sh
cargo build --release -p media-ffi --features rist       # the plugin
cargo run -p bm-probe -- probe fixtures/h264-640x360-30fps.mp4 --decode
```

The built plugins are committed under `../Runtime/Plugins/`: copy
`basis_media.dll` to `x86_64/` on Windows, run `tools/stage-android-plugin.ps1`
for `Android/arm64-v8a/`, and on a Linux host copy `libbasis_media.so`, stripped,
to `Linux/x86_64/`. Each needs librist staged first (see
[`third_party/librist/`](third_party/librist/README.md)).

| Tool | Does |
| --- | --- |
| `tools/ci.ps1`, `tools/ci.sh` | The gate to run before every commit; [`TESTING.md`](TESTING.md#running-the-tests) describes each step |
| `tools/android-env.ps1` | Finds an Android NDK (Unity's by default) and sets cargo up for `aarch64-linux-android`; dot-source it |
| `tools/stage-android-plugin.ps1` | Builds the Android plugin and copies it into the package |
| `tools/build-librist.ps1`, `build-librist.sh`, `build-librist-android.sh` | Build the librist library for Windows, Linux and Android |
| `tools/gen-*.py` | Generate some of the test fixtures in `fixtures/` |

[`TESTING.md`](TESTING.md) covers prerequisites and testing, and
[`DIAGNOSTICS.md`](DIAGNOSTICS.md) the captures. Fuzz targets are in `fuzz/`.

## Terms

The code, the tests and the captures use these names:

| Term | Meaning |
| --- | --- |
| Access unit (AU) | One frame's worth of compressed data, as the demuxer hands it on |
| Bank | The buffer between demux and decode (`media-bank`). It holds access units and releases them to the decoders on a real-time schedule |
| Release | The Bank handing access units to the decoders |
| Anchor | The wall-clock moment the release schedule counts from. A join or a seek sets it; a stall can move it later |
| Lag, target lag | On a live source, how far behind the live edge the Bank holds release, and how far it aims to |
| Auto | The buffer depth setting that sizes the target lag from what the connection has delivered |
| Decay, surplus | Surplus is lag beyond the target. Decay gives it back, slowly enough to present smoothly |
| Priming | Access units released early so a decoder has output ready when presentation starts. For audio, also the encoder delay at the start of an AAC or Opus track, which is not played |
| Liveness | Whether a source is played as live or on-demand |
| Generation | Each open and each seek starts a new one; anything still carrying an older generation is dropped |
| Clock, master | The clock (`media-clock`) is a session's one source of position. With audio it follows the audio playhead, the master; without, the wall clock |
| Playhead | How far the host's pull has got through the decoded audio |
| Audio ring | Decoded audio waiting for the host to pull it |
| Ladder, slew, snap | How the clock follows the master: no correction within a 20 ms dead band, a slew (up to 2% fast or slow, briefly up to 50% after a snap or a change of master) beyond it, a snap (a jump) past 700 ms |
| Release band | The 5 ms a running slew closes to before it stops |
| Route | The decoder a track takes: hardware (DXVA, MediaCodec) or software |
| Pool drop | A decoded frame that fell due but was never shown, because a newer frame was also due or it was more than 40 ms late |
| Address gate | The check that refuses private, loopback and other unsafe addresses, made for every resolved address and every redirect |
| Split source | Video and audio from two URLs played as one session |

Licensed MIT OR Apache-2.0, at your option.
