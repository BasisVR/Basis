#!/bin/sh
# The engine's gate on Linux: run it before committing, and every step must
# pass. The RIST, conformance, impairment and split-source steps print
# SKIPPED when what they need is missing and the run still ends green, so
# look for those lines; any other missing tool fails the run. TESTING.md
# ("Running the tests") lists what each step checks and what it needs.
#
#   tools/ci.sh          # the gate
#   tools/ci.sh --fuzz   # the gate, then build the fuzz targets
#                        # (needs nightly Rust and cargo-fuzz)
set -eu
cd "$(dirname "$0")/.."

echo "== cargo fmt --check"
cargo fmt --check
echo "== cargo clippy"
cargo clippy --workspace --all-targets --examples -- -D warnings
echo "== cargo test"
cargo test --workspace
# RIST is an optional transport linked against librist, so its steps run
# only once tools/build-librist.sh has built the library.
if [ -f third_party/librist/linux-x64/librist.a ]; then
    echo "== RIST clippy"
    cargo clippy -p media-rist -p media-engine --features media-engine/rist --all-targets -- -D warnings
    echo "== RIST tests"
    cargo test -p media-rist --features librist
else
    echo "SKIPPED: RIST, librist not built (tools/build-librist.sh)"
fi
echo "== cargo deny check"
cargo deny check
echo "== cargo vet"
cargo vet
# Conformance: every MP4 and TS fixture must demux to what ffprobe reads.
if command -v ffprobe >/dev/null 2>&1; then
    echo "== conformance (ffprobe oracle)"
    cargo run -q -p bm-probe -- conformance fixtures
else
    echo "SKIPPED: conformance, ffprobe not on PATH (install ffmpeg)"
fi
# Software decode: AV1 and Opus through the whole engine, no GPU needed.
echo "== software decode (AV1 + Opus)"
cargo run -q -p bm-probe -- play fixtures/mkv/av1-opus.webm --duration 8
# The next two play H.264 and AAC fixtures, so a host without those decoders
# skips them.
caps=$(cargo run -q -p bm-probe -- caps --compact)
if echo "$caps" | grep -q '"h264"' && echo "$caps" | grep -q '"aac"'; then
    # Impairment: a recorded bad-network profile replayed through the engine
    # over a fixture paced at 1x, graded against the buffer sizing model.
    # Kept short here; TESTING.md has the full-length runs.
    echo "== impairment (recorded network delay)"
    cargo run -q -p bm-probe -- impair fixtures/h264-aac-320x180-30s.ts \
        --profile ts-rtt300-loss005 --duration 25 --depth-ms 3000
    # Split source: video off one file, audio off another, one session.
    echo "== split source (two legs, one session)"
    cargo run -q -p bm-probe -- play fixtures/split/h264-640x360-30fps-video.mp4 \
        --audio-url fixtures/split/aac-48k-stereo-audio.m4a --duration 9
else
    echo "SKIPPED: impairment and split source, no H.264 or AAC decoder on this platform"
fi
if [ "${1:-}" = "--fuzz" ]; then
    echo "== cargo fuzz build"
    cargo +nightly fuzz build
fi
echo "CI green"
