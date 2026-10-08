# Testing the media engine

How to run and add the engine's tests, and what each test checks. The managed
side has its own guide at [`../TESTING.md`](../TESTING.md), and
[`DIAGNOSTICS.md`](DIAGNOSTICS.md) explains the captures.
[Which rows to run](#which-rows-to-run) says which rows a change needs, and
[`README.md`](README.md#terms) explains the terms the rows use.

## Prerequisites

- **Rust through rustup.** `rust-toolchain.toml` pins the version (1.98.0)
  and components; the first `cargo` command installs them.
- **cargo-deny and cargo-vet:** `cargo install --locked cargo-deny@0.20.2 cargo-vet@0.10.2`, the versions CI uses.
- **Windows:** PowerShell 7, NASM on `PATH`, and a GPU with hardware video
  decode for the Direct3D 11, Direct3D 12 and Media Foundation tests. Set `CARGO_TARGET_DIR`
  to a short path such as `C:/t` to stay under Windows' path-length
  limit.
- **Optional:** `ffprobe` for the conformance check; librist built into
  `third_party/librist/` by `tools/build-librist.ps1` or `.sh` (needs meson and
  ninja) for RIST; `rustup target add aarch64-linux-android` and Unity's Android
  NDK (`tools/android-env.ps1` finds it) for the Android build check. Without
  each, the gate skips that step.
- **For some by-hand rows:** Python 3 and ffmpeg (the fixture scripts in
  `tools/`, and ffmpeg as a live feeder), and nightly Rust with `cargo-fuzz` on Linux (see
  `fuzz/README.md`).

## Running the tests

```
.\tools\ci.ps1        # Windows
tools/ci.sh           # Linux
```

This is the gate: run it before every commit. It runs these steps in order and
stops at the first failure. The first run builds everything and takes a while;
after that it takes a few minutes.

| Step | Checks | Needs |
| --- | --- | --- |
| `cargo fmt --check` | Formatting is `rustfmt`'s default | |
| `cargo clippy` | No lint warnings anywhere, tests and examples included | |
| `cargo test` | Every test in the workspace | Windows for the session, Media Foundation and GPU tests |
| RIST | The RIST transport lints clean and its tests pass | librist built, see [`third_party/librist/`](third_party/librist/README.md) |
| Android (Windows only) | The engine compiles and lints for Quest | the `aarch64-linux-android` Rust target and an Android NDK |
| `cargo deny` | Licences, security advisories, banned crates and crate sources, as set in `deny.toml` | network access |
| `cargo vet` | Every dependency is audited or exempted in `supply-chain/` | network access |
| Conformance | Each MP4, QuickTime and TS fixture in `fixtures/` demuxes to exactly what ffprobe reads from it | `ffprobe` |
| Software decode | AV1 and Opus play through the whole engine without a GPU | |
| Impairment | A recorded bad-network profile replayed through the engine at 1x; playback has to keep going within the buffer model | H.264 and AAC decoders; Linux has neither and skips it |
| Split source | Video and audio from two files play as one session | H.264 and AAC decoders; Linux has neither and skips it |

When what they need is missing, RIST, Android and Conformance print `SKIPPED:`
(in yellow) on Windows, and RIST, Conformance, Impairment and Split source print
it on Linux. The run still ends green. Check for those lines before trusting a
pass. Every other step fails the run if its
tool is missing.
`-Fuzz` (`--fuzz` on Linux) also builds the fuzz targets, which needs nightly
Rust on Linux or WSL (see [`fuzz/`](fuzz/README.md)).

GitHub runs the same gate on every push and pull request that touches the
package (`.github/workflows/media-engine.yml`), on Windows and Linux, with a
separate Android build and a job that replays the fuzz corpora under
AddressSanitizer. Its runners have no GPU. The session tests take the
software decode route there, and the hardware decode tests find no decoder,
return early and pass. They print `SKIPPED:`, but `cargo test` hides a passing
test's output and the log never shows it. A green check on a pull request does
not cover the **CI, Windows** rows that need hardware decode; run the gate on a
Windows machine with a GPU for those.
`cargo test -p decode-mf --test dxva_decode -- --nocapture` shows which of
those tests skipped on the machine it runs on.

On Windows, `cargo vet` rewrites the files in `supply-chain/` with Windows line
endings. `git diff` shows no change, and `git checkout -- supply-chain` puts
them back.

```
cargo test -p media-demux                          # one crate
cargo test -p media-engine --test session          # one test file
cargo test -p media-engine --test session caption  # matching tests
```

Session, Media Foundation and GPU tests need Windows. Tests marked
`#[ignore]` need a network source named by an environment variable (see the
matrix) and run with `-- --ignored`.

`bm-probe` runs the engine without Unity (`cargo run -p bm-probe -- <command>`,
with `--release` for timing):

| Command | Does |
| --- | --- |
| `probe <src>` | Reports container, codecs and any embedded cover art; `--decode` adds first-frame timing (Windows), `--artwork-out <path>` writes the art as stored |
| `play <src> --csv out.csv` | Plays through the engine, writing the engine capture. `--audio-out`, `--seek-to-ms` and `--decode` are optional |
| `bench <src>` | Measures time to first frame and seek time |
| `caps` | Prints what this machine can play |
| `conformance fixtures` | Compares each fixture's demuxed stream with ffprobe |
| `impair <src> --profile <name>` | Replays a recorded network profile and grades the buffering |

## Which rows to run

Run the gate for any change under `Native~`, then the rows for what the change
touches:

| You changed | Run |
| --- | --- |
| `media-demux`, `media-bitstream` | [Demux and containers](#demux-and-containers) |
| `media-hls` | [Demux and containers](#demux-and-containers), and the HLS rows in [Transports and sources](#transports-and-sources) |
| `media-io`, `media-rtp`, `media-rtsp`, `media-whep`, `media-rist` | [Transports and sources](#transports-and-sources) |
| `media-decode` or its adapters | [Decode](#decode); for `decode-mediacodec`, also [Android devices](#android-devices) |
| `media-clock`, `media-present`, or the audio path in `media-engine` | [Present, audio output and clock](#present-audio-output-and-clock) |
| `media-bank`, or release and pacing in `media-engine` | [Buffering, pacing and resilience](#buffering-pacing-and-resilience) |
| Sessions, seeks or state in `media-engine` | [Foundations, lifecycle and hostile input](#foundations-lifecycle-and-hostile-input); for shared-playback sync, also [Shared playback and sync](#shared-playback-and-sync) |
| `media-ffi` | The ABI rows in [Foundations, lifecycle and hostile input](#foundations-lifecycle-and-hostile-input) and the drain rows in [Harness and instrumentation](#harness-and-instrumentation) |
| `media-diag`, `bm-probe`, or a capture column | [Harness and instrumentation](#harness-and-instrumentation), and update [`DIAGNOSTICS.md`](DIAGNOSTICS.md) |
| Code for one platform | Android: [Android devices](#android-devices). Linux: [Platforms](#platforms) |
| The managed package (`Runtime/`, `Editor/`, the prefabs) | [`../TESTING.md`](../TESTING.md) |

A change that crosses areas takes the rows of each. **By hand** and **Device
(Quest)** rows in those sections apply as much as the automated ones.

## Reporting in a pull request

The pull request template asks you to tick **Tested** and the platforms you
tested on. Under **Notes**, say the gate passed and name the other rows you
ran. Name any row that applies to the change but you could not run, with the
reason: no headset for a **Device (Quest)** row, no GPU for a row that needs
hardware decode, or no source of the kind a by-hand row needs.

## Layout

| Where | What |
| --- | --- |
| `src/` of each crate | Unit tests |
| `media-demux/tests/` | Container demuxers against the fixtures |
| `media-engine/tests/` | Whole sessions (mostly Windows) |
| `media-io`, `media-hls`, `media-rtsp`, `media-whep` `tests/` | Network sources against local scripted servers |
| `media-rtp/tests/` | The RTP receiver and reordering, including property tests |
| `media-decode/decode-mf/tests/`, `decode-sw/tests/` | The decoder adapters |
| `media-bank/tests/`, `media-clock/tests/` | Buffering and the clock, including property tests |
| `media-present/tests/` | The Direct3D 11 conversion pass and the Direct3D 12 handoff |
| `media-ffi/tests/` | The plugin boundary |
| `media-testkit/` | Recorded network-delay profiles and the impairment source |
| `fixtures/`, `tools/gen-*.py` | Test media and the scripts that generate it |
| `fuzz/` | Fuzz targets |

## Adding a test

1. Use the lowest level that shows the behaviour: a unit test, then a crate
   integration test, then a session test. By-hand `bm-probe` rows are for what
   needs a real network, a device or a listener.
2. Break the code the test covers and check the test fails with a clear
   message, then restore it.
3. Assert the required value in the test, not the engine's own constant.
4. Generate fixtures from synthetic sources (`testsrc2`, sine tones) with a
   script in `tools/`; do not commit recorded media. New MP4 and TS fixtures
   must pass `bm-probe conformance fixtures`. Keep TS audio at 48 kHz with an
   ADTS track. Check behaviour that real sources exercise against a public endpoint too
   (see [Network sources](#network-sources)).
5. Add the row to the matrix in the same commit, once it has been run. Column
   changes also update [`DIAGNOSTICS.md`](DIAGNOSTICS.md).

## Network sources

Some by-hand rows need a network source of a given kind: a live RTSP stream, a
WHEP endpoint, a RIST sender, or files over HTTPS with byte ranges. Where a
row names a public endpoint, validate against it. Software this engine did not
come from wrote it, and it catches a misreading of a format that a source set up
with the same reading would share.

| Public endpoint | Kind |
| --- | --- |
| `https://stream.mux.com/v69RSHhFelSm4701snP22dYz2jICy4E4FUyk02rW4gxRM.m3u8` | Low-Latency HLS, fMP4 parts, separate audio rendition |
| `https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` | On-demand HLS, 1080p60 |
| `https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov` | QuickTime file over HTTPS, H.264 with MP3 in a `.mp3` sound description |

Self-hosted servers cover what no public endpoint offers: a stream you can
publish to, restart or impair, and a public result reproduced locally. They are
an aid, not the reference, and so are the scripts in `tools/`. A failure seen
only on a self-hosted source is checked on a public endpoint before it is put
down to the engine. ffmpeg serves live MPEG-TS over HTTP (`ffmpeg -re -i
<file.ts> -c copy -f mpegts -listen 1 http://127.0.0.1:<port>/live`),
mediamtx serves RTSP, WHEP and HLS (Low-Latency by default, plain with
`hlsVariant: mpegts`), and any static server with range requests serves
files.

A server that takes one client at a time (ffmpeg's `-listen 1`) cannot test
live-or-on-demand detection, because the player's probe uses the only
connection. Set **Liveness** to Live for those.

## What is checked

**CI** runs in the gate on every platform, **CI, Windows** only on Windows,
**By hand** needs a person, and **Device (Quest)** a headset.

### Foundations, lifecycle and hostile input

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Unit/property tests | Bank sizing, clock correction, jitter filtering, frame-pool leasing, audio ring, demux fixtures, address blocklist and HTTP source behave as specified. | `cargo test --workspace` | CI |
| Session lifecycle | Pause freezes position, seeks settle, and Ended waits for the audio ring's tail, including seeks after Ended or mid-drain. With no audio, Ended waits for the last picture's own time on screen (to the duration, else the gap from the picture before it), and position stops at the duration. | `cargo test -p media-engine --test session` + `cargo test -p media-engine --lib a_last_frame_ends` | CI, Windows |
| State transitions | A failed session keeps its Error through a seek, play or pause; an end, pause or play decided on an earlier read gives way to a pause, seek or failure published since; an end of stream from the timeline a seek left does not end the new one; a seek stays in Buffering from the request until it lands, however long the demuxer takes to find the target. | `cargo test -p media-engine --lib -- a_state_published an_end_of_stream buffering_ends` + `--test session a_failed_session` + `--test seek_state` | CI; the session row CI, Windows |
| ABI record padding | Records copied into a caller's buffer have no padding bytes; a compile-time assert fails the build if one appears. | `cargo build -p media-ffi` | CI |
| Session teardown ordering | Closing a session, even mid-open, returns only after every thread it spawned has been joined. | `cargo test -p media-engine --lib close_tests` | CI |
| Closing is not a failure | Closing a session mid-read leaves it out of Error, with no error code and no Error event; the read the close cancels is not reported. | `cargo test -p media-engine --test close` | CI |
| Seeks land on their target | A seek shows its target within a frame across picture, sound, captions and SEI, handles targets past the end, and discards no audio. It decodes forward from its keyframe for up to 720 frames of the track's rate and never less than 12 s, so a low-rate file with keyframes far apart still lands on its target. A seek queued behind another supersedes it. | `cargo test -p media-engine --test session between_keyframes` + `--test session a_seek_far_past` + `--test session a_seek_queued` + `--lib the_bound_counts_frames`; by hand: `cargo test --release -p media-engine --test session a_long_decode_forward -- --ignored` (set `BASIS_MEDIA_TEST_SPARSE_KEYFRAMES_URL` to an H.264 MP4 over HTTPS whose last keyframe before 17 s is several seconds earlier) | CI, Windows; by hand |
| Seek matrix | Seeks land on every MP4 layout, HLS VOD, Matroska and raw audio; raw TS and live HLS refuse with Unsupported. | `cargo test -p media-demux --test mp4_stream` + `--test ts_stream` + `cargo test -p media-hls --test hls` + `cargo test -p media-demux --test raw_audio`; `bm-probe bench <lane>` | CI; bench by hand |
| Conformance (ffprobe oracle) | Demuxed access units for the MP4, QuickTime and MPEG-TS/m2ts fixtures match ffprobe's packets in count, timestamps, payload hashes and keyframe flags. | `cargo run -p bm-probe -- conformance fixtures` | CI |
| Fuzz | The demuxers, HLS playlist parser, caption decoder, RTP session and WHEP signalling never panic or read out of bounds on hostile input. | `cargo +nightly fuzz run mp4_stream` (likewise `ts_stream`, `hls_playlist`, `mkv_stream`, `flac_stream`, `mp3_stream`, `adts_stream`, `ogg_stream`, `wav_stream`, `caption_scan`, `rtp_session`, `whep_signal`) | CI (corpus replay); by hand (campaigns, Linux nightly) |
| Unsafe confinement | `unsafe` appears only in the ABI boundary, the decode adapters, the present layer, the engine's video sink and the librist binding; other crates refuse it at compile time. | `cargo build --workspace` | CI |
| Compiler floor | The declared `rust-version` matches the real minimum compiler the dependency graph needs. | `rustup run 1.92 cargo check --workspace --all-targets` | By hand |
| Raw-pointer obligations | Functions dereferencing caller-supplied raw pointers are `unsafe fn`, and call sites without a documented `unsafe` block fail to compile. | `cargo build --workspace` plus the aarch64 Android graph | CI, Android build |

### Demux and containers

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| TS demuxer unit rows | The TS demuxer gets expected counts, joins mid-stream on an SPS keyframe, unwraps 33-bit timestamps, and handles m2ts and LPCM. | `cargo test -p media-demux --test ts_stream` | CI |
| TS table parsing | Each PAT/PMT section is parsed once per version, and malformed, bad-CRC, not-yet-applicable or other-program sections never bind the wrong streams. | `cargo test -p media-demux --test ts_stream`; `cargo run -p bm-probe -- conformance fixtures` | CI |
| Demux note caps | Diagnostic notes and refusals from stream content (TS, HLS, Matroska, MP4, Ogg) are deduplicated and capped at 64. | `cargo test -p media-demux --lib demuxer` + `--test ts_stream` + `--test mkv_stream` + `cargo test -p media-hls --test hls` | CI |
| Fragmented MP4 opened from its index | Fragmented MP4 opens from a trustworthy `sidx`, or else from the `tfra` in a trailing `mfra`, loads fragments on demand and reads each one's data in one pass, seeks like a full parse, otherwise walking the file. A fragment stating more samples than the demuxer holds is refused before the over-budget run's samples are built. | `cargo test -p media-demux --lib mp4` + `--lib mp4_index` + `--lib mp4_fragment` + `--test mp4_stream` | CI |
| Live fragmented MP4 over HTTP | A fragmented MP4 from a source with no length is read forward only: `moov`, then each `moof` and the media data after it, with whatever lies between them read past. It plays every sample the file does, whether a fragment carries both tracks or one; a progressive file is refused, as is a seek; a stream cut inside a fragment ends after the fragments before it; and a track `moov` declares but never sends holds the other back by no more than half a second. | `cargo test -p media-demux --test mp4_stream live` + `never_arrives`; live: `cargo run -p bm-probe -- play https://stream.vrcdn.live/live/vrcdn.live.mp4 --duration 30` | CI; by hand |
| Fragmented MP4 walked | A file whose `moov` declares fragments, and is not read by its index, is walked from the end of `moov` one fragment at a time, each built as the indexed path builds it: `moov`'s own samples play first, a box between `moov` and the first fragment hides nothing, a version 1 run's negative composition offsets keep B-frames where ffprobe places them, only fragment headers are charged to the open, and the samples the whole file may state are capped. A capture cut off inside a box plays what it holds; a fragment placed ahead of `moov` is refused. | `cargo test -p media-demux --test mp4_stream negative_composition` + `many_fragment_files` + `a_walk_cut` + `a_fragment_before` + `samples_in_the_moov` + `fragments_behind` + `cargo test -p media-demux --lib a_walked_file` + `a_fragment_without_a_base_time`; by hand: `cargo run -p bm-probe --release -- play https://stream.mux.com/v69RSHhFelSm4701snP22dYz2jICy4E4FUyk02rW4gxRM.m3u8 --duration 20` (public LL-HLS, fMP4 with negative offsets): the picture plays throughout | CI; by hand |
| MP4 edit lists | A track whose edit list opens with an empty edit starts late by the gap, as ffprobe reads it, and seeks land on the shifted times; an audio track's first real edit is its encoder priming, which is not played even when the empty edit moves it past zero. A video track with no edit list presents at the times the file states, its reorder delay included. A late audio start plays its gap as silence, so the clock starts at zero with the picture (or where a seek into the gap started) and the picture is not hurried to catch up. A track's duration is the sum of its edits, not its `mdhd`. An edit list that ends before its media stops the track there, inside a group of pictures or an AAC frame: a picture decoded ahead of the end but shown past it is decoded and not shown, a seek into the trimmed tail lands on the last picture shown, and the sound, AAC padding included, is cut to the sample. Only empty edits followed by one edit of media at the normal rate are read; any other edit list plays the media whole. | `cargo test -p media-demux --test mp4_stream an_empty_edit` + `a_video_track_without_an_edit_list` + `an_edit_list_` + `a_tracks_duration_is_its_edit_lists` + `cargo test -p media-demux --lib an_edit_list_ends` + `cargo test -p media-engine --test session late_` + `a_trimmed_file` + `a_seek_into_a_trimmed` + `cargo test -p media-engine --lib a_tracks_end` + `cargo run -p bm-probe -- conformance fixtures` | CI; CI, Windows |
| MP4 AAC config | Each `mp4a` track's AudioSpecificConfig reaches the decoder byte for byte, so HE-AAC (SBR, parametric stereo, explicit or backward-compatible) is configured at its output rate and channel count. An `esds` the walk cannot reach falls back to the fields the box parser kept, with a note; a config that ends early, or an object type that is not an AAC core, is refused. | `cargo test -p media-bitstream asc` + `cargo test -p media-demux --lib mp4_esds` + `--test mp4_stream`; by hand: `bm-probe play <an HE-AAC 5.1 MP4> --duration 20 --audio-out out.f32`, every channel carrying sound | CI; by hand |
| MP4 MP3 | MP3 is read from an `mp4a` whose `esds` names MPEG-1 or MPEG-2 audio (0x6B, 0x69) and from a QuickTime sound description coded `.mp3`, `ms\0U` or `mp3 `. Each sample reaches the MP3 decoder as stored, announced at the rate and channel count of its first frame, read through the fragment index where `moov` holds no samples; a first sample too short for a header, or a first frame that is not Layer III, is refused. | `cargo test -p media-demux --test mp4_stream mp3` + `cargo run -p bm-probe -- conformance fixtures`; by hand: `bm-probe play fixtures/h264-mp3-320x180.mov --duration 5 --audio-out out.f32`, the tone in the left channel for a second, then the right, and `bm-probe play https://download.blender.org/demo/movies/ToS/tears_of_steel_720p.mov --duration 20`, audio pulled at 44100 Hz x 2 with no silence | CI; by hand |
| Refused tracks | An MP4 or Matroska track left out because nothing here plays it (an unknown video sample entry or CodecID, an audio track that is neither AAC nor MP3 in MP4) is a refusal naming the codec, and the other track plays. A file with nothing else fails to open with the refusals as its reason. The engine reports each refusal as `CodecRefused` from the demux stage. | `cargo test -p media-demux --test mp4_stream refused` + `--test mkv_stream refused` + `cargo test -p media-engine --test session a_track_the_demuxer` | CI |
| Matroska demuxer rows | Matroska/WebM gets expected counts, converts H.264 to Annex-B, announces VP9 and Opus, applies CodecDelay and cue-seeks to keyframes. A string, codec private data or frame stating more than the demuxer holds is refused before it is allocated, whatever size the source claims. A track number past 32 bits is passed over rather than narrowed onto another track. | `cargo test -p media-demux --test mkv_stream` | CI |
| Raw audio demuxer rows | FLAC, Ogg Opus, MP3, ADTS and WAV demux with exact timestamps, report duration, seek, and refuse unsupported layouts with a typed error. | `cargo test -p media-demux --test raw_audio` + `cargo test -p media-demux --lib` | CI |
| Parse error label | A parse error from any demuxer reads `parse: …` followed by that demuxer's own message: a malformed WAV reads `parse: WAV data chunk before fmt`. | `cargo test -p media-demux --test raw_audio a_wav_parse_error` | CI |
| Embedded cover art | Cover art is extracted undecoded from FLAC, Ogg, ID3v2 and MP4 tags, refusing hostile lengths and preferring the front cover. An MP4 cover stored as PNG, or any metadata item of a type the parser does not know, neither stops the file opening nor costs its tracks. | `cargo test -p media-demux --lib artwork` + `--test raw_audio` + `--test mp4_stream a_png_cover`; by hand: `bm-probe probe <file> --artwork-out <path>` reports the art's type and size and writes it as stored, and an audio-only source with an asymmetric picture shows it upright on the output texture | CI; by hand |
| Matroska stated geometry | Matroska stating a NaN, infinite or huge audio rate, channel count or duration skips that track or reports unknown duration; video plays. | `cargo test -p media-demux --test mkv_stream` | CI |
| MKV/WebM playback | H.264+AAC, VP9/Opus, AV1/Opus and H.265+AAC Matroska/WebM files play end to end with both tracks. | `cargo run -p bm-probe -- play fixtures/mkv/h264-aac.mkv --duration 8`, and likewise `vp9-opus.webm`, `av1-opus.webm` and `h265-aac.mkv` | By hand |
| Audio track selection | Each audio track in MP4 and Matroska is listed with its language and playable by index; out-of-range picks the first. | `cargo test -p media-demux --test audio_tracks`; `cargo run -p bm-probe -- play fixtures/h264-multiaudio.mp4 --audio-track {0,1} --audio-out out.f32` | CI; by hand |
| Audio tracks with no metadata | A file with several untagged audio tracks lists and binds each one, labelled by track position. | `cargo test -p media-demux --test audio_tracks untagged` | CI |
| HLS scheduler unit rows | HLS playlists parse or refuse unsupported features, and the scheduler handles window refresh, variant choice, join point and VOD seeks. | `cargo test -p media-hls --test hls` | CI |
| HLS audio renditions | A variant's audio rendition is its `AUDIO` group's `DEFAULT=YES` entry, else the first `AUTOSELECT=YES`, else the first, and none when that entry has no URI (its audio is muxed), even beside one that has; a refused rendition URI does not refuse the playlist; a live rendition joins the variant's point by program time (within a target duration of it), else by sequence number, else on its own hold-back. End to end, a video-only variant plays its rendition's audio as a split pair, and a rendition that cannot be opened, or whose URI was refused, leaves the picture playing with the reason reported, which a failure for want of anything playable also names. | `cargo test -p media-hls --test hls` + `cargo test -p media-engine --test hls_renditions` | CI, Windows for the engine rows |
| Low-Latency HLS scheduling | A live playlist with parts and `CAN-BLOCK-RELOAD` joins `PART-HOLD-BACK` behind the end on an independent part, reads complete segments whole and parts only at the live edge, finishes a segment begun on parts (also after `ENDLIST`), resumes at the next independent part after a lost or gap part, keeps its place across a reload that drops a segment's leading parts, reads a playlist that stops advancing as dead only after twenty target durations whatever the reload cadence, held blocking reloads included, sends blocking directives only to an http(s) playlist, reloads with `_HLS_msn`/`_HLS_part` without waiting, and falls back to plain reloads for the rest of the session once a blocking one is refused; parts without `CAN-BLOCK-RELOAD`, or with a map URI that is refused, play as whole segments; a plain live join honours `HOLD-BACK`; fMP4 parts play against their init segment. | `cargo test -p media-hls --test hls` | CI |
| CEA-608 caption lane | In-band CEA-608 captions in H.264 decode into timed pop-on, roll-up, special-character and clear cues, and seeks reset the decoder. | `cargo test -p media-bitstream` + `cargo test -p media-engine --test session caption`; by hand: `bm-probe play fixtures/h264-608-640x360-30fps.ts --duration 10` | CI; the session row CI, Windows; by hand |
| SEI user-data lane | H.264/H.265 SEI `user_data_unregistered` data arrives in order with UUID and timestamp, in bounded drop-oldest storage that seeks clear. | `cargo test -p media-bitstream` + `cargo test -p media-engine --lib user_data_ring` + `cargo test -p media-engine --test session user_data`; by hand: `bm-probe play fixtures/h264-sei-userdata-640x360-30fps.ts --duration 10` | CI; the session row CI, Windows; by hand |
| Truss DMX over SEI | DMX lighting data stamped into the video as [Truss](https://github.com/towneh/Truss) writes it arrives record for record: every `TRUSSDMX` record in order, its CRC intact once the SEI's emulation-prevention bytes are removed, and its `DMXS` universes holding the values written, including a universe that joins mid-stream. | `cargo test -p media-engine --test session truss`; after regenerating the fixture with `tools/gen-truss-dmx-fixture.py`, Truss's `truss-detect ts fixtures/h264-truss-dmx-320x180-30fps.ts` reports all 60 records intact | CI, Windows; by hand |
| SEI user data: managed delivery at the playhead | `BasisMediaPlayer.UserDataReceived` fires once per message, in order, at its timestamp; a late subscriber still gets messages not yet due. | the VRSL-URP package's Basis tests (Test Runner, assembly `Towneh.VRSL.URP.Basis.Tests`) | By hand (Unity) |

### Decode

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Software decode adapters | claxon FLAC, libopus Opus and rav1d AV1 decode fixtures with correct timestamps, and 5.1 Opus comes out in WAV channel order; Opus past 7.1 or in an ambisonic layout, broken headers, a FLAC AU decoding past its ceiling or overfilling the output queue, and an AV1 frame past the size limit get typed errors. | `cargo test -p decode-sw` | CI |
| PCM adapter | WAV and Blu-ray 16- and 24-bit PCM converts to float identically in WAV channel order, and unsupported formats are refused. | `cargo test -p decode-sw` | CI |
| MF adapter contracts | H.264, AAC and MP3 decode through Windows' built-in Media Foundation decoders, and VP9 and AV1 through installed Store extensions. | `cargo test -p decode-mf` | CI, Windows |
| Strided plane copies | Decoder-reported strides, sizes and dimensions are checked before plane copies, and negative, short, overflowing or odd-sized geometry is refused. | `cargo test -p decode-mf --lib` + `cargo test -p decode-sw --lib` | CI (decode-mf half Windows only) |
| Windows hardware decode | DXVA decodes on the GPU with no CPU copy and matches software byte for byte on the visible frame for H.264, VP9 and AV1. | `cargo test -p decode-mf --test dxva_decode` + `cargo test -p media-present --test gpu_pass present_slice` | CI, Windows |
| Media Foundation decoder fed only when dry | A submit never blocks inside a Media Foundation decoder; both adapters offer input only after the decoder asks for more. | `cargo test -p decode-mf --test dxva_decode av1_submit_never_waits`; by hand: `bm-probe play <an AV1 MP4 with muxed audio> --duration 40 --csv x.csv` | CI, Windows; by hand |
| Live decode at low latency | On a live source, the Windows H.264 decoders (DXVA and the in-box MFT) hand out each frame as soon as the stream's reordering allows, with the same frames in the same order as normal decode, so a live join presents in step with the audio-led clock. | `cargo test -p decode-mf --test dxva_decode live_h264`; live: `bm-probe play <a live RTSP stream, then a live HTTP-TS stream> --duration 30` presents nearly every decoded frame, with pool drops in single figures | CI, Windows; by hand |
| Slow video costs video, picture stays in step | With a too-slow decoder, audio stays whole, position follows audio, late video skips to a keyframe, and no frame shows over 40 ms late. | `cargo test -p media-engine --test slow_video`; by hand: `BASIS_MEDIA_SLOW_VIDEO_DECODE_MS=80 bm-probe play fixtures/h264-aac-320x180-30s.mp4 --duration 25 --csv x.csv` | CI, Windows; by hand |
| Forced hardware fallback | With `BASIS_MEDIA_DISABLE_HW_DECODE` set, the default plays through in software and `hardware_only` gives a typed error while audio plays. With no audio, the refusal fails the session within seconds, carrying its reason. | `cargo test -p media-engine --test hw_fallback` | CI, Windows |
| Decode preference | `decode_preference` (hardware with fallback, hardware only, software only) picks the decode route; an unavailable route gives a typed error. | Covered by the hw_fallback rows; by hand: `bm-probe play <url> --decode fallback\|hardware\|software` | CI, Windows; by hand |
| Refusal reasons | A codec with no software route is refused for the hardware's reason: HEVC with no frame size (as a transport stream announces it) says so. VP8 is refused as an unsupported codec, naming those that are. | `cargo test -p media-engine --lib route` | CI, Windows |
| Nothing left to play | A refused track beside one that plays leaves the session playing. Once every kind is refused, or refused and known absent (the demux thread has announced all it will, or reached the end), the session fails with the refusals' reasons; a split pair waits for both legs, and a decoder built after a refusal withdraws it. | `cargo test -p media-engine --lib playable` + `--test hw_fallback` | CI; the hw_fallback row CI, Windows |
| Software-route cap | Software decode accepts up to 1080p60 and refuses larger with a typed error before building a decoder, on either route. | `cargo test -p media-engine --lib route` | CI |
| Capability contract | The capability report keeps its exact JSON shape, lists only what this build decodes, and uses the size-then-fill buffer convention. | `cargo test -p media-engine --test capabilities` + `cargo test -p media-ffi --test capabilities`; by hand: `cargo run -p bm-probe -- caps` | CI, Windows |

### Present, audio output and clock

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Clock correction law | Clock correction is proportional to the error, capped wide for 1.2 s after a snap then at 2%, with out-of-range ceilings bounded. | `cargo test -p media-clock` | CI |
| GPU conversion pass | The D3D11 NV12-to-BGRA pass matches the CPU reference for every colour matrix and range, on synthetic sweeps and decoded frames. | `cargo test -p media-present --test gpu_pass` | CI, Windows |
| Shared-texture handle lifetime | The presenter always closes its shared texture handle; the consumer reopens on a handle change, retrying failed opens up to eight times, and opens a handle only while the presenter owning it is held, so a rebuild cannot close it mid-open. | `cargo test -p media-present --test gpu_pass dropping_the_presenter` + `cargo test -p media-ffi --lib consumer`; by hand: play an HLS ladder whose renditions change resolution across a discontinuity and check the picture stays live | CI, Windows; by hand |
| Decoder slices stay on their device | A decoder slice from another D3D11 device (a decoder rebuilt mid-stream) is dropped rather than copied, while the presenter's own device's slice presents. | `cargo test -p media-present --test gpu_pass present_slice_drops` | CI, Windows |
| D3D11 destination size | The consumer refuses a destination one pixel off the shared texture's size in either direction, and opens a matching one. | `cargo test -p media-present --test gpu_pass the_consumer_refuses` | CI, Windows |
| Direct3D 12 handoff | Converted frames reach a D3D12 texture unchanged; the newest finished frame is copied, never an older one after it; a slot with a copy pending is not converted over; a destination of another size is refused; no copy is recorded while the host cannot give its next frame-fence value; a dropped consumer keeps its objects until its last copy completes. | `cargo test -p media-present --test d3d12_handoff` | CI, Windows |
| Multichannel interleave order | Multichannel PCM is interleaved in WAV channel-mask order (FL FR C LFE BL BR), checked with a 5.1 tone-per-speaker fixture. | `cargo test -p media-engine --test session multichannel_interleave` | CI, Windows |
| PTS-annotated ring serve | Ring timestamp markers track media time, audio over 300 ms late is trimmed in bounded steps, and an on-time full ring is not. | `cargo test -p media-engine --lib audio`; live: `bm-probe play <a live RTSP stream> --duration 75` | CI; by hand |
| Audio ring generation swap | Audio ring resets are atomic with the consumer swap, and a refused decoder retires its producer and consumer. | `cargo test -p media-engine --lib audio` | CI |
| Audio pts-marker budget | The 1024 timestamp markers survive tiny audio chunks: contiguous chunks need none, and without a free slot the producer waits. | `cargo test -p media-engine --lib audio` | CI |
| Audio ring sizing | Whatever rate and channel count is announced, the audio ring's allocation stays under 6 Mi samples, rounded to whole frames. | `cargo test -p media-engine --lib audio` | CI |
| A/V output-latency compensation | The device's reported output latency shifts the audio clock back, clamped to 0 to 500 ms. A report that changes the latency moves a running clock by the change at once; a parked clock is left where it is. | `cargo test -p media-engine --lib audio` + `--test session latency`; on desktop and device: listen for sync on a fixture and on the RTSP stereo stream | CI; the session row CI, Windows; by hand; Device (Quest) |
| Clock start | The host is served audio from the moment the clock starts, an output latency before the first picture, and the first picture and the first sound arrive together. Audio is held back while a pause waits on the landing, a seek is queued, or the ring belongs to another timeline. | `cargo test -p media-engine --lib audio_serves` + `--test session pull_serves`; in the Editor: play an on-demand MP4 with video; the engine log shows no `SlewCorrection` in the first second | CI; the session row CI, Windows; by hand; Device (Quest) |
| Clock under host pull jitter | The audio playhead is averaged against the wall clock before the ladder acts on it, and a slew once started runs to a 5 ms release: a playhead traced in the Editor on Windows and the Quest callback pattern start no slew once settled, and a start-up catch-up stops at its target. | `cargo test -p media-clock --test jitter` + `--test properties a_slew_runs_until_the_release_band`; in the Editor: play an audio-only 44.1 kHz file and a 1080p60 MP4 for a minute untouched; the engine log shows no `SlewCorrection` after the first second | CI; by hand; Device (Quest) |
| Render-event frame selection | The render event shows each 24 fps frame once at 72 Hz; the video thread takes over without events. On Direct3D 12, where the copy lands one event later, frames are chosen and judged late one refresh further ahead. | `cargo test -p media-engine --lib present` | CI |
| Output-texture ownership | Closing and reopening a source releases the output texture, and the texture count stays level across open and close cycles. | In the Editor or a standalone build, open, close and reopen a source half a dozen times with the Profiler's Memory module on Texture2D count | By hand |
| Headless audio lane | `bm-probe` writes decoded PCM out as raw interleaved f32. | `bm-probe play <src> --audio-out out.f32` | By hand |

### Buffering, pacing and resilience

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Live join starts on audio | A live join clocks from the first buffered audio, shows video from its keyframe, and neither snaps the clock nor discards audio. | `cargo test -p media-engine --test session`; live: `bm-probe play <a live RTSP stream> --duration 60 --csv out.csv` | CI, Windows; by hand |
| Live-vs-on-demand inference | On Auto liveness, a finite HTTP source answering range requests is on-demand, otherwise live, with its total from `Content-Range`. | `cargo test -p media-io --test http_source seekability` | CI |
| Startup burst (VOD) | The Bank (demux-to-decode buffer) releases the first 2 s unpaced at startup and after seeks, then paces at real time. | `cargo test -p media-bank` | CI |
| Priming join (live) | Live lanes prime the decoder, hold presentation until target depth arrives, then release at real time without pausing. | `cargo test -p media-bank --test priming` | CI |
| Per-track release | A full decode channel holds back only its track in the Bank, and a live join's early audio is kept. | `cargo test -p media-bank --test gated` | CI |
| Live tracks stamped apart | On a live source whose tracks arrive together but are stamped seconds apart (a relay opening with a cached keyframe), only the track stamped ahead waits; the other releases as it arrives, lag and Auto's target stay near zero, and a track that stops arriving neither pins the time cap nor holds lag and decay. | `cargo test -p media-bank --test track_offset`. Live: `bm-probe play <a live RTSP relay that opens joins with a cached keyframe> --live --duration 60 --csv out.csv` over several joins, checking the first picture and the capture's `bank_lag_us` | CI; by hand |
| Out-of-order dts | A source whose dts runs out of order (RTSP with B-frames, where dts is pts) reports nothing banked once the Bank has drained. | `cargo test -p media-bank --test reordered` | CI |
| A seek straight after open | A seek that lands before a track's Format has left the Bank keeps the Format, so both tracks still decode and the new timeline re-arms the A/V offset. | `cargo test -p media-bank --test gated a_seek_keeps` + `cargo test -p media-engine --test session a_seek_clears_the_offset`; the session row meets the window only under load: run it with the other `a_` rows (`cargo test -p media-engine --test session a_`) | CI; the session row CI, Windows |
| Audio-leading start | Live sessions start at the first buffered audio, and on-demand sessions start audio and video together. | `bm-probe bench <rtsp-lane> --live` and `bm-probe bench <an on-demand MP4>` | By hand |
| Impairment, CI lane | Under the jitter-regime profile `ts-rtt300-loss005` (+300 ms round trip, 0.05% loss) at 3 s depth, the session keeps presenting and stalls within the sizing model. | `bm-probe impair fixtures/h264-aac-320x180-30s.ts --profile ts-rtt300-loss005 --duration 25 --depth-ms 3000` | CI, Windows |
| Impairment, full profiles | All recorded profiles at full length and several depths: file lanes stay within the sizing model (except `ts-rtt300-loss05`, where no depth suffices), live lanes keep presenting. | `bm-probe impair <file.ts\|live-url> --profile <name> [--depth-ms N] [--csv out.csv]` | By hand |
| Reconnect/resilience | A dropped live connection reconnects with jittered backoff, keeping its buffer; exhausted attempts end the session (Ended for EOF, Error for I/O loss). | `cargo test -p media-engine --test reconnect`; by hand: play a feeder with `--live` and restart it mid-run | CI, Windows; by hand |

### Transports and sources

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Headless playback, file | A local MP4 plays at 1x with expected decode and present counts, no pool drops and hardware-cadence audio pulls. | `cargo run -p media-engine --example smoke -- fixtures/h264-aac-640x360-30fps.mp4 5` | By hand |
| Headless playback, TS file | A local MPEG-TS file plays likewise, with the container sniffed and stream ids read from the programme map table. | `cargo run -p bm-probe -- play fixtures/h264-aac-640x360-30fps.ts --duration 5` | By hand |
| Auto liveness costs one connection | A live URL on Auto liveness opens one connection, the live lane adopting the liveness probe's response as its stream. | `cargo test -p media-engine --test reconnect` | CI, Windows |
| Headless playback, HTTP | A file served over local HTTP plays through media-io, with range requests and a pinned connection. | serve `fixtures/` locally, then `cargo run -p bm-probe -- play http://127.0.0.1:<port>/h264-aac-640x360-30fps.mp4 --duration 5 --allow-local` | By hand |
| Headless playback, HTTP-TS live | A live HTTP TS stream given `--live` plays sequentially with per-read stall detection and the Bank in live lag mode. | `ffmpeg -re -i fixtures/h264-aac-320x180-30s.ts -c copy -f mpegts -listen 1 http://127.0.0.1:<port>/live`, then `cargo run -p bm-probe -- play http://127.0.0.1:<port>/live --live --allow-local --duration 12` | By hand |
| Live-source unit rows | The live source streams sequentially, re-reads its head cache, reports typed stall errors, cancels its connect, re-vets redirects and applies the address gate. | `cargo test -p media-io --test live_source` | CI |
| On-demand source cancellation | Closing a session cancels an on-demand HTTP open or read against a quiet server within about 200 ms. | `cargo test -p media-io --test http_source` | CI |
| One connection per sequential open | A 200 answer to the range probe becomes the stream; a quiet body gives a typed error within the read timeout. | `cargo test -p media-io --test http_source` | CI |
| A chunk outlives an idle consumer | Long pauses and low-bitrate files do not fail ranged chunk requests; a silent server still errors within the read timeout. | `cargo test -p media-io --test http_source`; by hand: `bm-probe play <an MP3 over HTTPS>` | CI; by hand |
| Ranged requests sized to the reader | A jump asks for 64 KiB or the read's size, reads that carry on double up to the chunk size, a skip of up to 128 KiB reads on, and a walk of jumps keeps one connection. | `cargo test -p media-io --test http_source`; by hand: `bm-probe probe` a fragmented MP4 without `sidx` over HTTPS and `bm-probe bench` an Ogg Opus file, comparing the bytes in the server's access log with the file size | CI; by hand |
| A dropped connection is reopened | A failed ranged read retries once from the byte reached, and a connection dropped mid-file reads back byte for byte. | `cargo test -p media-io --test http_source` | CI |
| A redirect met mid-file is followed | Ranged requests follow redirects from the caller's URL, gate-vetting each hop, and a 206's `Content-Range` must start at the requested byte. | `cargo test -p media-io --test http_source` | CI |
| Transport errors name their cause | HTTP and WHEP transport failures report the full error chain, including the operating system's reason for a refused connection. | `cargo test -p media-io --test http_source` + `cargo test -p media-whep --test signal` | CI |
| Zero-length reads | An on-demand read into an empty buffer returns at once without touching the network or discarding its held chunk. | `cargo test -p media-io --test http_source` | CI |
| Resolve ceiling and open cancellation | DNS runs under one time ceiling off the reactor, WHEP opens cancel on close, and failed opens release tokens and server sessions. | `cargo test -p media-io --lib` + `cargo test -p media-whep --test signal` | CI |
| Resource fetch caps | Playlist fetches over HTTP and file are byte-capped, accepting exactly the cap and refusing one byte more. | `cargo test -p media-io --test resource_fetcher` | CI |
| Playlist fetch connections | A network playlist's reloads, segments and parts to one host share a connection, and a transport failure makes the next fetch to that host resolve and vet it again while other hosts keep their clients; a body past the cap is refused, on a stated length before any of it arrives; an error status is an HTTP error carrying the status. | `cargo test -p media-io --test resource_fetcher` | CI |
| Playlist origin confinement | A network-fetched playlist can never read local files, however URIs are spelt; a disk playlist keeps file and network access. | `cargo test -p media-io --test resource_fetcher` + `cargo test -p media-engine --test hls_origin` | CI |
| Source routing | Schemes route normalised and case-insensitively; unsupported schemes are config errors, a path that is not relative, rooted or a drive path (every network-share and device spelling) needs the address gate off, and local paths play. | `cargo test -p media-engine --lib classify_tests` + `cargo test -p media-engine --test routing` | CI |
| Playlist URI scheme | Playlist entries must resolve to `http` or `https`; `file:`, `ftp:`, `data:` and drive-letter forms are refused, relative and cross-host entries work. | `cargo test -p media-hls --test hls` | CI |
| Playlist directory confinement | A disk playlist reaches only plain relative files beside it; absolute, `..`, drive-relative, UNC and planted-link paths are refused. | `cargo test -p media-hls --test hls` + `cargo test -p media-io --test resource_fetcher` | CI |
| HLS VOD playback | HLS from file or HTTP plays to Ended without pool drops, chaining TS segments and timing fMP4 by its timestamps. | `cargo run -p bm-probe -- play fixtures/hls/ts/index.m3u8 --duration 8` and `…/hls/fmp4/index.m3u8` | By hand |
| HLS live | Plain live HLS on TS segments with its audio muxed in plays in real time: nearly every frame presented, no silence, and the Bank in lag mode holding about the hold-back (three target durations when the playlist states none). | mediamtx with `hls: true` and `hlsVariant: mpegts` and a live stream published to it, then `bm-probe play http://127.0.0.1:8888/<path>/index.m3u8 --allow-local --duration 25` | By hand |
| Low-Latency HLS over the internet | An LL-HLS origin at internet distance plays with nearly every frame presented, no stalls or reanchors, and a Bank lag near the playlist's `PART-HOLD-BACK`: 1.5 to 3 s on the public origin (1 s parts), under a second on mediamtx (209 ms parts). The audio rendition joins at the variant's point, aligned by program time (noted as `audio rendition: joining …`), and the A/V offset sits within a frame. | `bm-probe play https://stream.mux.com/v69RSHhFelSm4701snP22dYz2jICy4E4FUyk02rW4gxRM.m3u8 --duration 30`; for short parts, mediamtx with `hls: true` behind an HTTPS proxy and `bm-probe play https://<host>/<path>/index.m3u8 --duration 30` | By hand |
| HLS audio rendition seek | An on-demand variant with its audio in a separate rendition, the two cut at different segment boundaries, lands a forward and a backward seek on target with no silence, trims or ring drops, and the A/V offset within a frame afterwards. | `ffmpeg -i fixtures/h264-aac-320x180-30s.ts -map 0:v -c copy -f hls -hls_time 2 -hls_playlist_type vod -hls_segment_filename <dir>/v%03d.ts <dir>/video.m3u8`, the same with `-map 0:a` into `a%03d.ts`/`audio.m3u8`, a `master.m3u8` naming `audio.m3u8` as the `DEFAULT=YES` audio rendition of `video.m3u8`; then `bm-probe play <dir>/master.m3u8 --duration 14 --seek-to-ms 18000`, and again with `--seek-to-ms 3000` | By hand |
| HLS over real HTTPS | HLS VOD with 5.1 audio plays from an HTTPS origin serving range requests, as does a public master playlist. | an on-demand HLS playlist with TS segments and one with fMP4 segments, served over HTTPS with ranges and carrying 5.1 audio; the public `https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8` | By hand |
| RTSP lanes | `rtsp://` falls back from UDP to TCP, `rtspt://` stays on TCP; stereo, 5.1, slow-join, restart and forced-fallback lanes play with sender-report alignment. | live RTSP streams in stereo, in 5.1, with no audio, and with keyframes about 10 s apart, each over `rtsp://` and `rtspt://`; the public `rtsp://stream.vrcdn.live/live/vrcdn` | By hand |
| RTP timestamp scaling | RTP timestamp and sender-report conversion to microseconds saturates at both signs, and ordinary spans convert unchanged. | `cargo test -p media-rtp --test receiver` | CI |
| Depacketizer drain discipline | WHEP and RTSP-UDP lanes drain the depacketizer after every refused packet, survive crafted sequences without panicking, and count refusals. | `cargo test -p media-whep --lib` + `cargo test -p media-rtsp --lib` | CI |
| AAC access units fragmented by marker bit | Multichannel AAC split across RTP packets reassembles up to the marker bit, size-bounded, loss discarding the partial unit; 5.1 decodes cleanly. | `cargo test -p media-rtsp --test aac_reassembly`; by hand: `cargo run -p bm-probe --release -- play <a live RTSP stream with 5.1 AAC> --duration 20 --audio-out out.f32` | CI; by hand |
| RTSP message size cap | An RTSP reply is capped at 1 MiB, head and body together: a reply claiming a terabyte body, a head of endless header lines and a line that never ends, alone or after most of the ceiling went on header lines, each fail the request. | `cargo test -p media-rtsp --test rtsp_message_bounds` | CI |
| H.264 access unit cap over RTP | An H.264 access unit that never ends is refused at the first packet past 16 MiB of RTP payload, or at its 65,537th NAL unit or payload piece, and not before; the depacketizer takes the next access unit. | `cargo test -p media-rtsp --test h264_access_unit_bound` | CI |
| H.265 SPS with an unreadable code | An SPS from `sprop-sps` with an unreadable code in its scaling list or palette extension is refused; the same SPS well formed is accepted. | `cargo test -p media-rtsp --test h265_sps_refusal` | CI |
| RTSP dials the vetted address | The RTSP client dials the addresses the engine resolved and vetted, in order, reaching a later one within seconds past silent or refusing ones, never looking the host up itself, and still names the host in its requests. | `cargo test -p media-rtsp --test rtsp_pinned_address` | CI |
| RTSP open bounds | An RTSP open against a server that accepts the connection and never answers ends within a second of a cancel, and on its own within 30 s. | `cargo test -p media-rtsp --test rtsp_open_bounds` | CI |
| RTSP start without sender reports | With no sender reports inside the aligner's wait, each stream counts from the PLAY response's RTP-Info `rtptime`, over UDP as over TCP. A `rtptime` more than 10 s from its stream, or start times that put the streams' first frames more than 250 ms apart, give way to each stream's first frame. When every stream counts from `rtptime` and the starts agree, the aligner waits only 300 ms past the first frames for reports rather than the full 2 s. | `cargo test -p media-rtsp --lib` + `cargo test -p media-rtsp --test alignment`. Live: `bm-probe play <a live RTSP stream whose server sends no early sender reports> --live --duration 20` over UDP and TCP, where Playing lands under a second after open | CI; by hand |
| RTSP late sender reports | Reports that arrive after the start move the video onto them once and leave the audio alone; a move over 3 s is refused. Both are logged. | `cargo test -p media-rtsp --test alignment`; live: `bm-probe play <a live RTSP stream whose server sends its first report after 2 s> --duration 20` logs the move | CI; by hand |
| A/V sync on a marker clip | The sound lands on the picture within a frame on every join, over `rtsp://`, `rtspt://` and HTTP-TS. | a clip with one white frame and a 1 kHz beep starting on the same frame every 2 s (ffmpeg `color` with `drawbox` enabled on `eq(mod(n,48),0)` at 24 fps, and `aevalsrc`), published through each feeder; watch several joins of each. Read ffmpeg's own client on the same stream first: an RTSP server can shift B-frame video against its audio, which the player then reproduces faithfully | By hand |
| WHEP lanes | `whep://` and `wheps://` play H.264 and Opus over WebRTC with vetted HTTP signalling, gate-checked media addresses and reconnect on publisher restart. | `cargo test -p media-whep`; by hand: `bm-probe play <a WHEP endpoint carrying H.264 and Opus>` | CI; by hand |
| WHEP signalling body cap | WHEP signalling answers are capped at 256 KiB while arriving, accepting exactly the cap and refusing more, including endless chunked bodies. | `cargo test -p media-whep --test signal` | CI |
| RIST header layout | `media-rist`'s declarations match the struct layout of the pinned librist headers. | `cargo test -p media-rist --features librist` | CI, Windows |
| RIST lanes | `rist://` plays plain and AES-128 streams from a gate-vetted host; a wrong secret gives a typed error, and so does a `rist://` URL on a build without RIST. | `cargo run -p bm-probe --features rist -- play rist://127.0.0.1:11968 --duration 12 --allow-local` with an ffmpeg RIST sender on loopback, or `cargo run -p bm-probe --features rist -- play rist://<host>:<port> --duration 20` against a remote sender; then the same URL on a build without `--features rist` | By hand |
| RIST on Android | The Android arm64 plugin builds with RIST linked in, adding no shared-library dependency. | `bash tools/build-librist-android.sh` then `.\tools\stage-android-plugin.ps1`, then `llvm-readelf -d` on the staged `libbasis_media.so` | By hand |
| Split sources | Split video and audio sources stay within 100 ms, seek together to the target frame, and unsupported or mismatched pairs get typed errors. | `cargo test -p media-engine --test split_source`; `bm-probe play fixtures/split/h264-640x360-30fps-video.mp4 --audio-url fixtures/split/aac-48k-stereo-audio.m4a --duration 9` | CI, Windows |
| Audio-only file lanes | Raw FLAC, Opus, PCM, MP3 and ADTS AAC play to End at their stored duration, audio driving clock and position. | `cargo run -p bm-probe -- play fixtures/sine-48k-stereo.{flac,mp3,aac,opus} --duration 9` | By hand |
| Codec breadth over HTTPS | FLAC (16/24-bit, 5.1, variable blocksize), MP3 (CBR, VBR), Opus, VP9 and AV1 files each play over HTTPS at 1x without pool drops. | `bm-probe play <file URL>` for each | By hand |

### Shared playback and sync

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Sync soft target | The engine ignores a follower's sync error under 150 ms, slews up to 2% above that and seeks past 2 s; live ignores it. | `cargo test -p media-engine --test session sync_target` | CI, Windows |
| Divergence bound | `max_divergence_ms` caps how far behind live the Bank runs, Auto's growth included, and a larger explicit depth gives a typed error. | Covered by the Bank's config validation rows; by hand: open a live lane with `"max_divergence_ms"` set in the open descriptor (the player's **Max Divergence (ms)**; `bm-probe` has no flag for it) and check Auto depth stays inside it in the capture CSV | CI; by hand |

### Platforms

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Linux headless lane | Headless on linux-x64, the engine decodes AV1, FLAC and Opus in software and gives typed errors for platform-only codecs. | `tools/ci.sh` (on a Linux host) | CI (Linux) |

### Harness and instrumentation

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Bench lane | Startup-to-first-frame and seek-to-settled times are measured per lane and aggregated over several runs. | `bm-probe bench <src\|url> [--runs N] [--seek-to-ms N] [--live]` | By hand |
| Capture recorder | The diagnostics timeline is written as a CSV with a stable column contract. | `bm-probe play <src> --csv out.csv` | By hand |
| Engine A/V offset is measurable | Video-minus-audio offset reaches the ABI snapshot, both captures and `bm-probe`, reading "unknown" until a frame is presented or with no session. | `cargo test -p media-engine --lib a_new_generation_starts_unarmed` + `--lib a_real_av_offset_never_collides`. By hand: `bm-probe impair fixtures/h264-aac-320x180-30s.ts --profile ts-rtt300-loss005 --duration 20 --depth-ms 3000 --csv x.csv`, then count rows whose `av_offset_us` is not `i32::MIN` | CI; by hand |
| Serve-trim total is a capture column | Cumulative serve-trim discards reach the capture as `audio_trimmed_frames` and do not fall across a seek. | `cargo test -p media-diag`. By hand: `bm-probe play <live-src> --csv out.csv` and confirm the summary's trimmed figure equals the last row's `audio_trimmed_frames` | CI; by hand |
| Capture reaches the file | The capture writer flushes and errors when rows cannot reach the target, and a truncated capture fails the run. | `cargo test -p media-diag`. By hand on Linux: `bm-probe play fixtures/h264-aac-320x180-30s.ts --duration 9 --interval-ms 2000 --csv /dev/full` | CI; by hand (Linux) |
| Impairment run owns its capture | A `bm-probe impair` run whose CSV capture failed grades as a failure, exits non-zero and still prints its grade. | `bm-probe impair fixtures/h264-aac-320x180-30s.ts --profile ts-rtt300-loss005 --duration 25 --depth-ms 3000 --csv <an existing directory>` | By hand |
| Diagnostic log sink | Every textual engine diagnostic goes through one replaceable sink, defaulting to stderr; the plugin's sink also writes `OutputDebugString` on Windows. | `cargo test -p media-diag`. By hand: `bm-probe play fixtures/does-not-exist.mp4 --duration 3` prints `[basis-media] session error: …` | CI; by hand |
| An event drain is a backlog, not a loss | `bm_session_drain_events` hands back at most the requested number of events and leaves the rest queued for the next call. | `cargo test -p media-diag` | CI |
| Refused events are reported | The count of events the session log refused reaches the ABI snapshot as `events_dropped`, saturating at the field's limit. | `cargo test -p media-ffi --lib` | CI |
| Bank schedule readings are capture columns | The Bank's lag, target lag, reanchor and stall totals appear as engine capture columns and in `bm-probe play`'s `bank:` line. | `cargo test -p media-diag`. By hand: `bm-probe play <live-src> --duration 60 --csv out.csv` | CI; by hand |
| Join surplus is returned, not held | Media arriving after a live join anchors is decayed back downstream as surplus, pausing while the release thread is blocked. | `cargo test -p media-bank`. Live: `bm-probe play <a live RTSP stream> --duration 90 --csv out.csv` | CI; by hand |
| A live source does not pause | A pause on a live session is ignored and logged once, and a pause arriving during opening is dropped. | `cargo test -p media-engine --test session pause` | CI, Windows |
| A pause holds across a seek | Pause and seek in either order show the target frame, stay paused there, and `play` resumes from it, with or without video. | `cargo test -p media-engine --test session a_` | CI, Windows |
| Auto-inferred HTTP live lane runs as live | A live HTTP source detected by automatic liveness runs the Bank in live mode, the same as one declared live. | `cargo test -p media-engine --test reconnect`. Live: `bm-probe play <a live MPEG-TS stream over HTTPS> --csv out.csv` | CI, Windows; by hand |
| Free text reaches a drain, not only a sink | Every free-text diagnostic line also lands in a drainable process-wide ring that evicts oldest when full and counts evictions. | `cargo test -p media-diag` | CI |
| The process log crosses the ABI | `bm_drain_log` needs no session handle, leaves lines past the cap queued, and truncates on a UTF-8 boundary. | `cargo test -p media-ffi --test log_drain` | CI |
| A cut detail says it was cut | A truncated detail ends in `…` without overrunning its buffer or splitting a multi-byte character, in event and log drains. | `cargo test -p media-ffi --no-fail-fast` | CI |
| Unity end to end | The engine and managed component play a source in Unity, with the capture graded against healthy ranges in [`DIAGNOSTICS.md`](DIAGNOSTICS.md). | `Basis > Tools > Media Player > Run Smoke Test`, or headless: `Unity -batchmode -projectPath <project> -logFile - -executeMethod BasisMediaSmokeTest.RunBatch` | By hand |

## Android devices

A Quest on USB with debugging authorised. Build librist for Android
(`bash tools/build-librist-android.sh`), stage the plugin with
`tools\stage-android-plugin.ps1`, build a Quest client of a test scene, play the
row's source, and read the captures and `adb logcat -s basis-media`.

| Row | What it checks | How to run | Runs in |
| --- | --- | --- | --- |
| Android build lane | The whole engine graph compiles and lints clean for `aarch64-linux-android`, without the AV1 software decoder. | the gate's `Android (aarch64)` step (prints SKIPPED without an NDK). By hand: `. .\tools\android-env.ps1` then `cargo clippy --target aarch64-linux-android -p media-ffi -p decode-mediacodec -- -D warnings` | CI, Android build |
| Engine .so | `libbasis_media.so` links only against `libmediandk`, `liblog` and the C runtime, and exports the ABI plus `UnityPluginLoad` and `JNI_OnLoad`. | `. .\tools\android-env.ps1; cargo build --target aarch64-linux-android -p media-ffi --release`, then `llvm-readelf -d`/`--dyn-syms` on `target/aarch64-linux-android/release/libbasis_media.so` | By hand |
| Playback on device | MediaCodec hardware decode presents via Vulkan into a `RenderTexture` under OpenXR, with AAC audio, a mid-run seek and natural end. | a Quest build playing a fixture with a seek | Device (Quest) |
| AAC from MP4 on device | AAC in MP4 whose config carries the SBR sync extension with SBR absent, as ffmpeg writes it, plays with sound in stereo and 5.1; an HE-AAC MP4 keeps its SBR. | a Quest build playing `fixtures/aac-48k-stereo.m4a`, `fixtures/sine-48k-51.m4a` and an HE-AAC 5.1 MP4 | Device (Quest) |
| Managed package on device | The package plays with Vulkan output, audio resampled to the device rate, and position advancing one second per second. | a Quest build playing a fixture; grade the frame capture | Device (Quest) |
| Android https | An `https://` lane connects and plays on device using the bundled root certificates. | a Quest build playing any `https://` source | Device (Quest) |
| Diagnostics captures | The engine writes its diagnostics CSV on close, and the frame capture writes one row per Unity frame. | `cargo test -p media-engine --test session diag_csv_written_on_close`. On device: `adb pull` both captures from the app's files directory after a run | CI, Windows; Device (Quest) |
| Steady-lane cadence | On a steady live stereo stream, frames hold for the ideal number of display refreshes through audio-callback jitter, skipping none. | a Quest build playing a steady live stereo RTSP stream over `rtspt://`; grade frame holds over a window after the join | Device (Quest) |
| Live lanes on device | RTSP (stereo, 5.1, UDP and TCP), WHEP and live HLS lanes each decode, present and play audio on device. | a Quest build playing each kind of stream | Device (Quest) |

## Known gaps

- No test covers the ordering that stops the A/V offset reading a new timeline
  against the old one after a seek.
- A fragmented MP4 with neither a segment index nor an `mfra` reads every
  fragment header at open, which is slow on a long file over HTTP.
- A seek in a fragmented MP4 opened from its index fetches the target's
  fragment, and can fetch its neighbour, before it lands; over a long
  round trip that is slower than a file whose whole table was read at open.
- A seek into a fragmented file whose audio and video fragments are cut at
  different points does not replay audio from the fragment before the landing.
- A hostname lookup cannot be cancelled once started; a queued lookup is
  refused at the same time limit.
- The engine's 7.1 tests stop at the decoder; speaker output and mix-down are
  in [`../TESTING.md`](../TESTING.md).
- A split pair whose sources start at different times by more than the decoder
  cushion is refused.
- Raw TS cannot be seeked. HLS has no encryption, byte-range segments,
  keyframe-only playlists or adaptive switching, and fetches whole segments of
  up to 64 MiB.
- Raw audio seeks are approximate except WAV and FLAC.
- No VP9 software decoder; Opus stops at 7.1 and refuses ambisonics; AV1
  above 8-bit 4:2:0 is refused.
- RTSP carries H.264 and AAC only, without credentials or multicast.
- WHEP carries H.264 and Opus only, ignores the server's STUN and TURN entries,
  and sends no authentication. Its fuzz target needs `--features whep` and
  cmake.
