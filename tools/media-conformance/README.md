# media-conformance — demux output regression gate

The fuzz harness (`tools/media-fuzz`) proves the parsers don't **crash** on hostile input.
This proves they produce the **correct output** on valid input: it diffs our demuxer's
access-unit stream against `ffprobe`'s packet stream, down to the MD5 of the payload where the
container stores it as-is. A parser change that alters what the demuxer emits fails the gate.

Only the demux lane lives here. The decode and A/V lanes need a GPU and a Unity editor, so they
can't gate CI and aren't part of this.

## No committed media, no ffmpeg dependency in the shipped code

Fixtures are generated on the fly from a static Basis-logo video track plus a tone (multichannel
where the fixture needs it) — content is irrelevant to demuxing, so a still image and a tone are
ideal: tiny, deterministic, nothing to host. ffmpeg is used only as an external CLI tool (the
reference oracle and the fixture generator); nothing links `libav*` and nothing ffmpeg-derived is
committed. CI installs ffmpeg on the runner, uses it, and the ephemeral runner discards it.

## Run it

```
./build.sh                          # compile basis_demux_dump (the real protocol/*.c + a sink)
./gen_fixtures.sh /tmp/fx           # generate the fixture matrix with ffmpeg
python demux_gate.py /tmp/fx        # diff each fixture against ffprobe; exit 1 on any divergence
```

Needs `ffmpeg`/`ffprobe` on PATH and a C compiler (`cc`/`gcc`/`clang`, or the LLVM install on
Windows via Git Bash). `gen_fixtures.sh` skips any codec whose encoder the local ffmpeg lacks, so
the gate covers whatever the runner supports.

## What it checks, per fixture

- **announce** — codec and (video) dimensions match ffprobe.
- **count** — one access unit per demuxed packet.
- **pts** — the timestamp sequence matches within 1 µs (integer-µs vs decimal-seconds rounding).
- **md5** — the payload bytes match. Three framings are reconciled so the hashes line up:
  - VP9/AV1/AAC-in-MP4 are stored as-is and compare directly.
  - H.264/H.265 leave the demuxer as Annex B, so ffprobe is reframed with `*_mp4toannexb`, and
    keyframes are exempt (the filter inlines SPS/PPS that the demuxer passes as extradata).
  - AAC-in-TS is ADTS-framed; ffprobe is reframed with `aac_adtstoasc` to strip the header the
    sink strips.
  - LPCM has no canonical packetisation (the demuxer and ffmpeg chunk it differently), so it is
    checked on announce only.

## The matrix

`gen_fixtures.sh` covers: MP4 H.264+AAC in faststart / trailing-moov / fragmented layouts; MP4
HEVC (hvcC); MP4 + WebM VP9; MP4 + WebM AV1; MPEG-TS H.264+AAC (PAT/PMT/PES + ADTS); AAC-in-M4A;
WAV 16-bit stereo and 5.1; Blu-ray LPCM 5.1 over M2TS; Opus in WebM (muxed + audio-only) and Ogg Opus (`.opus`). That exercises the MP4 box + sample-table
parser, the TS section/PES parser, the WebM EBML parser, the WAV and M2TS LPCM paths, and the
avcC/hvcC/esds/ADTS bitstream handling.

## CI

The `conformance-demux` job in `.github/workflows/media-native.yml` runs the three steps above on
every change under `Native~/` or this directory. It's deterministic (the generate-and-compare is
self-consistent: our dump is always checked against ffprobe's view of the *same* generated file,
so ffmpeg version differences across runners don't matter).

To add a format: extend `gen_fixtures.sh` with the ffmpeg recipe, and if it needs a new framing
reconciliation, add it to `demux_gate.py`.
