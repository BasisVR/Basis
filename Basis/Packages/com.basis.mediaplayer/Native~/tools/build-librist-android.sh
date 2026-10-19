#!/usr/bin/env bash
# Cross-build librist as a static library for media-rist (the `rist` cargo
# feature) targeting android-arm64, and stage it into third_party/librist/.
#
# Same pinned tag and same static/release shape as build-librist.{ps1,sh};
# the difference is a meson cross file pointing at the NDK toolchain. librist
# vendors its own mbedTLS and links it into the archive, so one library comes
# out.
#
# Toolchain: the NDK that ships inside Unity's AndroidPlayer, which is also
# what tools/android-env.ps1 finds for the Rust side. Override with
# ANDROID_NDK_HOME (pointing at the NDK root, not the toolchain bin).
#
# Requires: git, meson, ninja. On Windows those are typically pip-installed
# under ~/AppData/Roaming/Python/PythonXXX/Scripts and are NOT on PATH in Git
# Bash; this script looks there before giving up.
#
# Output: third_party/librist/android-arm64/librist.a
#         third_party/librist/include/librist/*.h  (committed; the lib is not)
set -euo pipefail

LIBRIST_REF="${LIBRIST_REF:-v0.2.11}"
LIBRIST_REPO="${LIBRIST_REPO:-https://code.videolan.org/rist/librist.git}"
API="${ANDROID_API:-29}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$root/target/build-librist/android-arm64"
tp="$root/third_party/librist"
src="$work/librist"

# --- toolchain ------------------------------------------------------------
if [ -n "${ANDROID_NDK_HOME:-}" ]; then
  ndk="$ANDROID_NDK_HOME"
else
  ndk=""
  for editor in /c/Program\ Files/Unity/Hub/Editor/*/Editor/Data/PlaybackEngines/AndroidPlayer/NDK; do
    [ -d "$editor" ] && ndk="$editor"
  done
fi
[ -n "$ndk" ] && [ -d "$ndk" ] || {
  echo "No Android NDK found. Set ANDROID_NDK_HOME to the NDK root." >&2
  exit 1
}

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) host_tag="windows-x86_64"; sfx=".cmd"; exe=".exe" ;;
  Darwin)               host_tag="darwin-x86_64";  sfx="";     exe=""     ;;
  *)                    host_tag="linux-x86_64";   sfx="";     exe=""     ;;
esac
bin="$ndk/toolchains/llvm/prebuilt/$host_tag/bin"
# The extensionless clang files are shell scripts the Windows toolchain
# cannot exec; the .cmd wrappers are the callable ones there.
cc="$bin/aarch64-linux-android$API-clang$sfx"
[ -f "$cc" ] || { echo "No $cc — check the NDK layout / ANDROID_API." >&2; exit 1; }

# --- meson + ninja --------------------------------------------------------
find_tool() {
  if command -v "$1" >/dev/null 2>&1; then command -v "$1"; return; fi
  for d in "$HOME"/AppData/Roaming/Python/Python*/Scripts; do
    [ -x "$d/$1$exe" ] && { echo "$d/$1$exe"; return; }
  done
  return 1
}
meson="$(find_tool meson)" || { echo "meson not found (pip install meson ninja)." >&2; exit 1; }
ninja="$(find_tool ninja)" || { echo "ninja not found (pip install meson ninja)." >&2; exit 1; }

rm -rf "$work"
mkdir -p "$work"
git clone --depth 1 -b "$LIBRIST_REF" "$LIBRIST_REPO" "$src"

# meson is a native Windows binary under Git Bash and cannot resolve
# /c/-style paths, so the cross file carries host-native ones.
winpath() {
  if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else echo "$1"; fi
}

cross="$work/android-arm64.cross"
cat > "$cross" <<EOF
[binaries]
c = '$(winpath "$cc")'
ar = '$(winpath "$bin/llvm-ar$exe")'
strip = '$(winpath "$bin/llvm-strip$exe")'
ranlib = '$(winpath "$bin/llvm-ranlib$exe")'

[host_machine]
system = 'android'
cpu_family = 'aarch64'
cpu = 'aarch64'
endian = 'little'
EOF

# librist builds with -pedantic-errors, and a const-discarding assignment in
# its own rist-common.c is a hard error on newer compilers. Demote just that
# one rather than patching a vendored dependency. This is clang's spelling of
# it, not GCC's -Wno-error=discarded-qualifiers: meson probes the compiler
# with -Werror=unknown-warning-option, so a name clang does not know fails
# every check in the configure run rather than being ignored.
"$meson" setup "$src/build" "$src" --cross-file "$cross" \
  --default-library=static --buildtype=release \
  -Dc_args="-Wno-error=incompatible-pointer-types-discards-qualifiers"
"$ninja" -C "$src/build" librist.a

mkdir -p "$tp/android-arm64" "$tp/include/librist"
cp -f "$src/build/librist.a" "$tp/android-arm64/librist.a"
cp -f "$src"/include/librist/*.h "$tp/include/librist/"
if [ -d "$src/build/include/librist" ]; then
  cp -f "$src"/build/include/librist/*.h "$tp/include/librist/"
fi

echo "Staged: third_party/librist/android-arm64/librist.a"
