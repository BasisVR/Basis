# media-fuzz — coverage-guided fuzzing of the demux/parse layer

Developer and CI tooling. The native plugin parses attacker-controlled container and protocol bytes
by hand, in-process, with no sandbox — in multiplayer a peer-broadcast URL is parsed by every
client. This fuzzes those parsers in isolation (no decoder, no Media Foundation, no Unity)
under AddressSanitizer + UndefinedBehaviorSanitizer, so a malformed stream that reads out of
bounds or trips UB faults here instead of on a user's machine.

It compiles the real `Basis/Packages/com.basis.mediaplayer/Native~/protocol/*.c` — the same
source that ships — against a libFuzzer driver, so a find is a find in the shipping parser.

## Build

Needs clang with the fuzzer + sanitizer runtimes. On Linux/CI that's `clang` on PATH; on
Windows install LLVM (`winget install LLVM.LLVM`) and build from Git Bash.

```
./build.sh          # all targets
./build.sh ts       # just the TS demuxer target
```

Output goes to `build/`. On Windows the ASan runtime DLL is staged next to the exe so it runs
in place.

## Run

```
cd build
./fuzz_ts.exe ../corpus/ts ../seeds/ts -max_len=65536 -max_total_time=180
```

`seeds/ts/` holds small slices of real fixtures (carve them with
`head -c 49152 <real.ts> > seeds/ts/name.ts`); `corpus/ts/` is where libFuzzer saves inputs
that reach new coverage. A crash writes a `crash-<hash>` artifact — replay it with
`./fuzz_ts.exe <artifact>` for the full ASan report.

Note: libFuzzer's `-minimize_crash` can over-reduce a position-sensitive stack overflow past
the crash; if the minimized file stops reproducing, keep the original artifact.

## Targets

| Target | Parser under test | Sources compiled |
| --- | --- | --- |
| `fuzz_ts` | `basis_ts_run` — MPEG-TS PAT/PMT/PES demux | `basis_ts.c` + `basis_bitstream.c` + `basis_caption.c` |
| `fuzz_mp4` | `basis_mp4_run` — MP4/fMP4 box + sample-table demux | `basis_mp4.c` + `basis_bitstream.c` + `basis_caption.c` |
| `fuzz_webm` | `basis_webm_run` — WebM/Matroska EBML demux | `basis_webm.c` + `basis_bitstream.c` |
| `fuzz_ogg` | `basis_ogg_run` — Ogg page/lacing/CRC demux (`.opus`) | `basis_ogg.c` |
| `fuzz_mp3` | `basis_mp3_run` — MP3 frame/Xing/VBRI demux | `basis_mp3.c` |
| `fuzz_caption` | `basis_caption_scan_au` — in-band CEA-608 SEI scan | `basis_caption.c` + `basis_bitstream.c` |
| `fuzz_url` | `basis_url_parse` — scheme/userinfo/host/port/path split | `basis_url.c` |
| `fuzz_hls` | `basis_hls_*` — M3U8 master/media parse, URI resolve, segment stitch, seek/reposition | `basis_hls.c` + `basis_url.c` |
| `fuzz_rtsp` | `parse_sdp` + `depkt_video`/`depkt_audio` (RTP FU/AP/afrag reassembly) + `rtsp_recv` | `basis_rtsp.c` (via `#include`) + `basis_bitstream.c` |
| `fuzz_rtmp` | `amf_find_stream_id` + `handle_video`/`handle_audio` (FLV) + `rtmp_read_message` (chunk assembler) | `basis_rtmp.c` (via `#include`) + `basis_bitstream.c` |

`fuzz_hls` injects an in-memory HTTP provider (the fuzz bytes are the body of every fetched
URL — playlist and segments), so no network is touched; it stubs `basis_io_host_is_blocked`
to always-allow so playlist parsing is actually reached (the real SSRF host check resolves DNS
and is exercised at runtime, not in-process — its URL-parsing half is covered by `fuzz_url`).
`basis_hls.c` spawns a producer thread, so `build.sh` links `-pthread` off-Windows.

**RTSP/RTMP.** These own their sockets, so their harness `#include`s the real `basis_rtsp.c` /
`basis_rtmp.c` (statics become reachable, and a find is still a find in the shipping parser) and
provides a link-time `basis_io` stub — no-op for the write paths, byte-serving for the read paths
(`rtsp_recv`, `rtmp_read_message`). The buffer-taking parsers (`parse_sdp`, `depkt_video`/`_audio`,
`amf_find_stream_id`, `handle_video`/`_audio`) are called directly with no handshake to script, so
they hit the exact code where review found the H1/M2 OOBs. This first run found a **signed left-shift
UB** in the RTP timestamp read (`rtp[4] << 24` on a byte ≥ 128, in both `depkt_video`/`depkt_audio`,
plus the same in the RTMP chunk stream id) — fixed and pinned as `testcases/rtsp/rtp_ts_shift_ub.bin`.
A full-session seam (scripted handshake, or an injected transport vtable that would also enable
RTSP/RTMP unit tests) is the remaining depth work — tracked in the media-player backlog as B76.

New targets slot in the same way — one `fuzz_<name>.c` driver plus its protocol sources in
`build.sh`. The MP4 and WebM drivers add a `reseek` callback (both are offset-driven — MP4's `moov`
sample tables and chunk offsets, WebM's SeekHead/Cues index), so the fuzzer must reposition the
in-memory cursor or the parser never reaches the sample data or the ranged-fetch seek paths. The
caption scanner takes an AU buffer directly (no sink/read), so the fuzz input *is* the AU; it runs
both the H.264 and H.265 SEI layouts.

**Runs so far:** the TS target found four bugs (all fixed in #962, repros below). MP4 (~388k),
WebM (~5.8M, against #960's AV1 code), and caption (~280k) all ran with **zero findings** against
the #962-fixed bitstream — those parsers guard their length fields, unlike the TS section loop.

**Fuzzing against an open PR's code:** to fuzz code that isn't on `developer` yet, overlay that
branch's protocol sources into the working tree before building (`git checkout <branch> -- <file>`;
don't commit). WebM was fuzzed against `feat/mediaplayer-av1` (#960, adds the `V_AV1` `CodecPrivate`
path) this way. Run MP4/WebM against the #962-fixed `basis_bitstream.c`, or they re-find the shared
SPS bugs the TS run already surfaced.

**Sink contract:** the driver's sink must supply `on_state`/`on_error`/`on_end_of_stream` — the
parsers call these without a NULL check (only `on_duration`/`on_transport`/`take_seek` may be
NULL). A sink missing them faults inside the parser and reads as a false parser bug.

## testcases/

Every crash the fuzzer finds is pinned here as a regression input, so a re-run confirms the
fix and guards against reintroduction. Replay one with `build/fuzz_<name>.exe testcases/...`.

- `ts/pat_pmt_section_len_oob.ts` — out-of-bounds read in `parse_pat`/`parse_pmt`: the 12-bit
  `section_len` (and PMT `prog_info_len`) is trusted and walked up to ~4 KB past the ~184-byte
  TS payload, running off the demux buffer when the section packet is the last one buffered.
- `rtsp/rtp_ts_shift_ub.bin` — signed left-shift UB in the RTP timestamp read: `rtp[4] << 24`
  shifted a byte ≥ 128 into the `int` sign bit (`depkt_video`/`depkt_audio`). A 12-byte RTP
  header with `byte[4] = 0xFF` trips it; fixed by reading the timestamp through `uint32_t`.
- `rtmp/chunk_streamid_shift_ub.bin` — the same signed-shift UB in the RTMP chunk stream id
  (`sid[3] << 24`, `rtmp_read_message`), which the RTP repro above doesn't reach. A 12-byte fmt-0
  chunk header with the stream-id MSB set trips it; fixed by shifting through `uint32_t`.
