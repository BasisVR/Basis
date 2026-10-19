#!/usr/bin/env bash
# Build librist as a static library for media-rist (the `rist` cargo feature)
# on Linux x64, and stage it into third_party/librist/.
#
# The Linux counterpart of build-librist.ps1, same pinned tag and same meson
# invocation. librist vendors its own mbedTLS and links it into the archive, so
# a single library comes out.
#
# Requires: git, a C toolchain, meson and ninja (pip install meson ninja, or
# the distro packages).
#
# Output: third_party/librist/linux-x64/librist.a
#         third_party/librist/include/librist/*.h  (committed; the staged lib is not)
set -euo pipefail

LIBRIST_REF="${LIBRIST_REF:-v0.2.11}"
LIBRIST_REPO="${LIBRIST_REPO:-https://code.videolan.org/rist/librist.git}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$root/target/build-librist/linux-x64"
tp="$root/third_party/librist"
src="$work/librist"

for tool in git meson ninja cc; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "$tool not on PATH. Need a C toolchain and 'pip install meson ninja'." >&2
    exit 1
  }
done

rm -rf "$work"
mkdir -p "$work"
git clone --depth 1 -b "$LIBRIST_REF" "$LIBRIST_REPO" "$src"

# librist builds with -pedantic-errors, and a const-discarding assignment in
# its own rist-common.c is a hard error from GCC 13 or so onwards. Demote just
# that one back to a warning rather than patching a vendored dependency.
meson setup "$src/build" "$src" --default-library=static --buildtype=release \
  -Dc_args=-Wno-error=discarded-qualifiers
# Only the static we link; skip librist's CLI tools and tests.
ninja -C "$src/build" librist.a

mkdir -p "$tp/linux-x64" "$tp/include/librist"
cp -f "$src/build/librist.a" "$tp/linux-x64/librist.a"
cp -f "$src"/include/librist/*.h "$tp/include/librist/"
if [ -d "$src/build/include/librist" ]; then
  cp -f "$src"/build/include/librist/*.h "$tp/include/librist/"
fi

echo "Staged: third_party/librist/linux-x64/librist.a + third_party/librist/include/librist/"
