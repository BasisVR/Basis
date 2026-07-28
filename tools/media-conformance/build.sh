#!/usr/bin/env bash
#
# Build basis_demux_dump: the real protocol/*.c demuxers compiled against an
# observing sink that prints every access unit as JSON. Plain C, no Unity, no
# Media Foundation -- builds on Linux/CI (cc/clang/gcc) and Windows (Git Bash +
# the LLVM install). Uses -o for the output name, so the compiler must accept it
# (clang/gcc); MSVC cl, which spells it /Fe, is not supported here.
#
#   ./build.sh
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
native="$here/../../Basis/Packages/com.basis.mediaplayer/Native~"
out="$here/build"
mkdir -p "$out"

CC="${CC:-cc}"
if ! command -v "$CC" >/dev/null 2>&1; then
    if command -v clang >/dev/null 2>&1; then CC="clang"
    elif command -v gcc >/dev/null 2>&1; then CC="gcc"
    else CC="/c/Program Files/LLVM/bin/clang.exe"; fi
fi

srcs=(
    "$here/native/basis_demux_dump.c"
    "$native/protocol/basis_webm.c"
    "$native/protocol/basis_ogg.c"
    "$native/protocol/basis_mp4.c"
    "$native/protocol/basis_ts.c"
    "$native/protocol/basis_wav.c"
    "$native/protocol/basis_mp3.c"
    "$native/protocol/basis_bitstream.c"
    "$native/protocol/basis_caption.c"
)

echo "building basis_demux_dump with $CC ..."
"$CC" -O2 -I "$native" "${srcs[@]}" -o "$out/basis_demux_dump"
echo "  -> $out/basis_demux_dump"
