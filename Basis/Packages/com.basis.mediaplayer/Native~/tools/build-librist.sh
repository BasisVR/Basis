#!/usr/bin/env bash
# Build librist as a static library for media-rist (the `rist` cargo feature)
# on Linux x64 or macOS, and stage it into third_party/librist/.
#
# The Linux and macOS counterpart of build-librist.ps1, same pinned tag and
# same meson invocation. librist vendors its own mbedTLS and links it into the
# archive, so a single library comes out. On macOS one library is built per
# architecture (LIBRIST_ARCHS, default "arm64 x86_64"), each through a meson
# cross file so the non-native slice builds on either kind of Mac.
#
# Requires: git, a C toolchain, meson and ninja (pip install meson ninja, the
# distro packages, or brew install meson ninja).
#
# Output: third_party/librist/linux-x64/librist.a, or
#         third_party/librist/macos-arm64/librist.a and macos-x64/librist.a
#         third_party/librist/include/librist/*.h  (committed; the staged lib is not)
set -euo pipefail

LIBRIST_REF="${LIBRIST_REF:-v0.2.11}"
LIBRIST_REPO="${LIBRIST_REPO:-https://code.videolan.org/rist/librist.git}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tp="$root/third_party/librist"

for tool in git meson ninja cc; do
  command -v "$tool" >/dev/null 2>&1 || {
    echo "$tool not on PATH. Need a C toolchain and 'pip install meson ninja'." >&2
    exit 1
  }
done

stage_headers() {
  mkdir -p "$tp/include/librist"
  cp -f "$1"/include/librist/*.h "$tp/include/librist/"
  if [ -d "$1/build/include/librist" ]; then
    cp -f "$1"/build/include/librist/*.h "$tp/include/librist/"
  fi
}

if [ "$(uname -s)" = "Darwin" ]; then
  min="${MACOSX_DEPLOYMENT_TARGET:-11.0}"
  for arch in ${LIBRIST_ARCHS:-arm64 x86_64}; do
    case "$arch" in
      arm64) rid=macos-arm64 cpu=aarch64 ;;
      x86_64) rid=macos-x64 cpu=x86_64 ;;
      *) echo "unknown architecture $arch" >&2; exit 1 ;;
    esac
    work="$root/target/build-librist/$rid"
    src="$work/librist"
    rm -rf "$work"
    mkdir -p "$work"
    git clone --depth 1 -b "$LIBRIST_REF" "$LIBRIST_REPO" "$src"
    cat > "$work/cross.ini" <<CROSS
[binaries]
c = 'cc'
ar = 'ar'
strip = 'strip'

[built-in options]
c_args = ['-arch', '$arch', '-mmacosx-version-min=$min', '-Wno-error=incompatible-pointer-types-discards-qualifiers']
c_link_args = ['-arch', '$arch', '-mmacosx-version-min=$min']

[host_machine]
system = 'darwin'
cpu_family = '$cpu'
cpu = '$cpu'
endian = 'little'
CROSS
    meson setup "$src/build" "$src" --cross-file "$work/cross.ini"       --default-library=static --buildtype=release
    ninja -C "$src/build" librist.a
    mkdir -p "$tp/$rid"
    cp -f "$src/build/librist.a" "$tp/$rid/librist.a"
    stage_headers "$src"
    echo "Staged: third_party/librist/$rid/librist.a"
  done
  exit 0
fi

work="$root/target/build-librist/linux-x64"
src="$work/librist"
rm -rf "$work"
mkdir -p "$work"
git clone --depth 1 -b "$LIBRIST_REF" "$LIBRIST_REPO" "$src"

# librist builds with -pedantic-errors, and a const-discarding assignment in
# its own rist-common.c is a hard error from GCC 13 or so onwards. Demote just
# that one back to a warning rather than patching a vendored dependency.
meson setup "$src/build" "$src" --default-library=static --buildtype=release   -Dc_args=-Wno-error=discarded-qualifiers
# Only the static we link; skip librist's CLI tools and tests.
ninja -C "$src/build" librist.a

mkdir -p "$tp/linux-x64"
cp -f "$src/build/librist.a" "$tp/linux-x64/librist.a"
stage_headers "$src"

echo "Staged: third_party/librist/linux-x64/librist.a + third_party/librist/include/librist/"
