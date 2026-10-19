# Third-Party Notices

The native engine (`basis_media`) is a Rust binary that links its dependencies
statically, so the shipped `.dll` / `.so` embeds them. Everything in the graph is
permissively licensed, and every licence in it except CC0-1.0 (`to_method`)
requires attribution. The licence texts and copyright notices, `to_method`'s
included, are in [`THIRD_PARTY_LICENSES.txt`](THIRD_PARTY_LICENSES.txt); this
file is the overview.

The same texts are split one file per licence, plus one per C library, in
`ThirdPartyLicenses~/`. The licence manifest in
`dev.hai-vr.hvr.license-review` tracks those files, so the client lists them
under Settings, Third-party licences, beside every other package's licence.

Nothing here is copyleft. `Native~/deny.toml` holds the allowed licence set and
`Native~/tools/ci.ps1` fails the build on anything outside it, so a dependency
carrying an unexpected licence cannot land quietly.

## What ships

| Binary | Contains |
| --- | --- |
| `Runtime/Plugins/x86_64/basis_media.dll` | the Rust graph below, plus Media Foundation and Direct3D from the OS |
| `Runtime/Plugins/Android/arm64-v8a/libbasis_media.so` | the Rust graph below, plus MediaCodec and Vulkan from the OS |
| `Runtime/Plugins/Linux/x86_64/libbasis_media.so` | the Rust graph below; software decode only, no OS codec framework |

Operating-system frameworks carry no attribution obligation. The Rust graph
does.

## Licences in the shipped graph

`THIRD_PARTY_LICENSES.txt` covers the shipping library (`media-ffi` with the
`rist` feature) on the three targets it ships on, without build and dev
dependencies or the workspace's own crates, plus the C libraries linked into
it. Where a crate offers a choice of licence, the text given is the licence
taken; `Native~/deny.toml` sets what may be chosen at all.

`ThirdPartyLicenses~/` holds the same sections, so a change to
`Native~/Cargo.lock` or to a vendored C library that changes what the plugin
links needs both updated to match.

The client shows the texts as baked into the licence manifest, not the files,
so after changing a file, select `BasisFrameworkLicenseManifest` in
`dev.hai-vr.hvr.license-review` and press Bake. Bake does not mark the asset
as changed, so edit any field in its inspector and set it back before File,
Save Project, or nothing reaches the disk. Bake also re-reads every other
package's licence, so entries baked from a checkout with other line endings
show in the diff as whitespace-only changes.

A licence that enters the graph needs a new file in `ThirdPartyLicenses~/`,
with `_license_` in its name so the manifest's inspector finds it: tick it
there, then under Edit Licenses give it its SPDX identifier and a product name
in the form the others use. A licence that leaves the graph takes its file
with it, and the inspector no longer lists it: delete its entry from the
package's `trackedPackages` list with the inspector in Debug mode. Bake after
either.

## Libraries worth naming

The graph is mostly small Rust crates. These are the ones that do the heavy
lifting, or whose licence differs from the MIT/Apache norm:

| Library | Licence | What it does |
| --- | --- | --- |
| **rav1d** | BSD-2-Clause | AV1 video decoding in software. A Rust port of dav1d |
| **libopus** (via `audiopus_sys`, ISC) | BSD-3-Clause | Opus audio decoding. Built from vendored C source and linked statically |
| **claxon** | Apache-2.0 | FLAC audio decoding |
| **retina** | MIT or Apache-2.0 | RTSP client and RTP depacketisation. Vendored, see below |
| **matroska-demuxer** | Zlib, or MIT, or Apache-2.0 | Matroska/WebM parsing. Vendored, see below |
| **re_mp4** | MIT | MP4 and fragmented-MP4 parsing. Vendored, see below |
| **m3u8-rs** | MIT | HLS playlist parsing |
| **str0m** | MIT or Apache-2.0 | WebRTC (the WHEP receive path) |
| **webrtc-rs** `rtp` / `rtcp` / `webrtc-util` | MIT or Apache-2.0 | RTP and RTCP packet formats |
| **rustls** | Apache-2.0, or ISC, or MIT | TLS |
| **ring** | Apache-2.0 and ISC | Cryptographic primitives under rustls. Embeds BoringSSL-derived assembly |
| **aws-lc-rs** / **aws-lc-sys** | ISC and (Apache-2.0 or ISC), with further terms | Cryptography reachable from the WebRTC stack. Windows uses the OS provider instead |
| **webpki-roots** | CDLA-Permissive-2.0 | The CCADB trust-anchor bundle, used on Android, which has no readable CA store |
| **tokio** | MIT | Async runtime for the network transports |
| **ash** | MIT or Apache-2.0 | Vulkan bindings, Android only |
| **jni** | MIT or Apache-2.0 | JNI bindings, Android only |
| **windows** | MIT or Apache-2.0 | Windows API bindings, Windows only |
| **to_method** | CC0-1.0 | A small conversion helper, pulled in by rav1d |

Unicode-3.0 covers the ICU data crates that reach the graph through URL and
IDNA handling.

## Vendored sources

Four dependencies live under `Native~/third_party/` rather than coming from
crates.io. The three Rust crates are vendored, each with its patches and the
reason for them documented in a `PATCHES.md` beside the source; librist is
built from a pinned upstream tag:

- **retina** (MIT or Apache-2.0) — patched for servers that advertise an
  all-zero SSRC, for AAC access units delivered without the RTP marker bit, for
  UDP socket binding against Windows' excluded port ranges, and for the seams
  the UDP transport needs.
- **matroska-demuxer** (Zlib, or MIT, or Apache-2.0) — patched so cue-based
  seeking resolves against the right cluster and lands on the keyframe at or
  before the requested time.
- **re_mp4** (MIT) — patched so a metadata item of a type the crate does not
  list, such as a PNG cover, is read as binary instead of failing the file.
- **librist** (BSD-2-Clause), which vendors **mbedTLS** (Apache-2.0) and
  **cJSON** (MIT) — the RIST live-ingest transport. Built from source by
  `Native~/tools/build-librist.ps1` on Windows and `build-librist.sh` on Linux;
  the static library is not committed. Its notices, and those of the code it
  compiles in, are in `Native~/third_party/librist/LICENSES.txt`.

## RIST, per platform

RIST is behind a Cargo feature, and every shipped binary is now built with it
on — Windows, Linux and Android alike — so librist and mbedTLS are statically
linked into all three and the attribution above applies to each. Android's
librist is cross-compiled against the NDK by
`Native~/tools/build-librist-android.sh`; as on the other platforms the static
library itself is not committed.

## Test clips

`Native~/fixtures/captest/A.mp4` through `D.mp4` are test content for the
session-cap pass, not shipped runtime assets — `Native~` is invisible to
Unity, so nothing imports them. The video is generated; the music is:

> Kevin MacLeod (incompetech.com) — licensed under Creative Commons: By
> Attribution 4.0, <https://creativecommons.org/licenses/by/4.0/>
>
> - `A.mp4` — *Blue Ska*
> - `B.mp4` — *Achaidh Cheide*
> - `C.mp4` — *Vibe Ace*
> - `D.mp4` — *Bass Walker*

Every other fixture under `Native~/fixtures` is generated by ffmpeg from
synthetic sources and carries no third-party content.

## This package

Licensed under either of MIT or Apache-2.0, at your option. See `LICENSE.md`.
