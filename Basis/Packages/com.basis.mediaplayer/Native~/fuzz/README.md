# Fuzzing

`cargo-fuzz` targets for the parsers that read untrusted input: the container
demuxers, the HLS playlist parser, the caption scanner, RTP sessions and WHEP
signalling.

Needs nightly Rust and [`cargo-fuzz`](https://github.com/rust-fuzz/cargo-fuzz),
on Linux or WSL: libFuzzer targets do not build for `*-pc-windows-msvc`. Run from
`Native~`:

```sh
cargo +nightly fuzz run mp4_stream fuzz/corpus/mp4_stream               # a campaign
cargo +nightly fuzz run mp4_stream fuzz/corpus/mp4_stream -- -runs=0    # replay the corpus once
cargo +nightly fuzz run --features whep whep_signal fuzz/corpus/whep_signal
```

`whep_signal` is behind the `whep` feature because its Linux build compiles
`aws-lc-sys`, which needs cmake. With a prebuilt `cargo-fuzz`, add
`--target x86_64-unknown-linux-gnu`.

CI replays every corpus once on each change (`media-engine.yml`).

`fuzz/corpus/<target>/` holds the committed seeds. Keep new seeds
small: a small seed reaches the same code as a large one and mutates faster.
`seed-frag-sidx.mp4` is a fragmented MP4 with a segment index, the layout that
opens from the index.

## Contained upstream panics

Some dependencies panic on hostile input. The engine catches those panics
where it calls the crate, with `catch_unwind`, and reports a typed error, which works in the
shipped build (`panic = "unwind"`). Fuzz builds abort on any panic, so the same
inputs show up as crashes there. A crash whose backtrace ends inside one of
these crates is that class, not an escape:

| Target | Crate | Pin the input under |
| --- | --- | --- |
| `mp4_stream` | `re_mp4` | `media-demux/tests/data/re-mp4-panics/` |
| `mkv_stream` | `matroska_demuxer` | `media-demux/tests/data/mkv-panics/` |
| `rtp_session` | `rtp`, `rtcp` | `media-rtp/tests/data/rtp-panics/` |

The crate's tests replay each pinned input and assert a typed error. Keep these
inputs out of `fuzz/corpus/`, or every later campaign stops on them at start-up.

Inputs that make the Matroska seek-head or MP4 reads slow are a different
case: the read budgets bound them, they live in `media-demux/tests/data/slow-mkv/`
and `slow-mp4/`, and they belong in the corpus.

A panic inside `str0m` under `whep_signal` is a bug to report upstream: str0m
holds that user input must never panic.
