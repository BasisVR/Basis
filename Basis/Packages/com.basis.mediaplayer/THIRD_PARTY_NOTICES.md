# Third-Party Notices

By default the native plugin `basis_media_native` decodes and presents using
operating-system frameworks only, with **no statically-linked third-party
libraries**. Building with `-DBASIS_WITH_RIST=ON` adds the RIST live-ingest
transport, which statically links the two permissively-licensed libraries listed
under *RIST transport* below; their attribution obligations apply only to a
RIST-enabled build.

| Binary shipped | Embeds | License obligation |
|---|---|---|
| `Plugins/Windows/x86_64/basis_media_native.dll` | OS frameworks; + librist + mbedTLS when built with `BASIS_WITH_RIST` | none, or BSD-2-Clause + Apache-2.0 (RIST build) |
| `Plugins/Android/arm64-v8a/libbasis_media_native.so` | OS frameworks; + librist + mbedTLS when built with `BASIS_WITH_RIST` | none, or BSD-2-Clause + Apache-2.0 (RIST build) |

## RIST transport (only when built with `BASIS_WITH_RIST`)

The RIST live-ingest transport statically links these into `basis_media_native`.
Both are permissive and MIT-compatible, and both require attribution:

- **librist** — Reliable Internet Stream Transport (ARQ, GRE tunnel, profiles).
  BSD-2-Clause. <https://code.videolan.org/rist/librist>
- **Mbed TLS** — AES primitives used by librist for PSK-AES content encryption.
  Apache-2.0. <https://github.com/Mbed-TLS/mbedtls>

## Opus decode (runtime-loaded, not shipped by this package)

Windows Opus decode does **not** statically link libopus. The plugin resolves
libopus's decode entry points at runtime (`LoadLibrary` / `GetProcAddress`) from
the `opus.dll` that **`com.avionblock.opussharp`** already ships; that package
redistributes libopus (BSD-3-Clause) and carries its licence
(`Opus_LICENSE_PLEASE_READ.txt`). This package neither bundles nor statically
links it, so the "no statically-linked third-party libraries by default" stance
above is unchanged. Android decodes Opus with the OS `audio/opus` MediaCodec —
no third-party library.

## Operating-system frameworks used at runtime

These are part of the OS and are **not** redistributed by this package:

- **Windows** — Media Foundation (`mfplat`, `mfuuid`, AAC/H.264/H.265 decoder
  MFTs), Direct3D 11/12, DXGI, WinHTTP, Winsock.
- **Android** — NDK Media (`mediandk`: AMediaCodec/AMediaExtractor), Vulkan,
  `AHardwareBuffer`, Android NDK platform libraries.

## Build-time only (not shipped)

- **Unity PluginAPI headers** (`IUnityInterface.h`, `IUnityGraphics*.h`) are
  required to compile the native plugin glue. They are part of the Unity Editor
  (Unity Companion License) and are **not** included here — copy them from your
  Unity install at build time (see `Native~/unity/README.md`). The package
  `.gitignore` excludes them.

The previous pipeline's third-party components (libvpx for VP9, gorilla/websocket
and the Go runtime for the transcode server) were removed along with the VP9
transcode path. Opus decode later returned in a different form — runtime-loaded,
not statically linked (see *Opus decode* above).
