# retina

[![crates.io version](https://img.shields.io/crates/v/retina.svg)](https://crates.io/crates/retina)
[![Documentation](https://docs.rs/retina/badge.svg)](https://docs.rs/retina)
[![CI](https://github.com/scottlamb/retina/workflows/CI/badge.svg)](https://github.com/scottlamb/retina/actions?query=workflow%3ACI)

High-level RTSP multimedia streaming library, in Rust. Good support for
ONVIF RTSP/1.0 IP surveillance cameras, as needed by
[Moonfire NVR](https://github.com/scottlamb/moonfire-nvr). Works around
brokenness in cheap closed-source cameras.

Status: In production use in Moonfire NVR. Many missing features.

Progress:

*   [x] client support
    *   [x] basic authentication.
    *   [x] digest authentication.
    *   [x] RTP over TCP via RTSP interleaved channels.
    *   [x] RTP over UDP (experimental).
    *   *   [ ] re-order buffer. (Out-of-order packets are dropped now.)
    *   [x] RTSP/1.0.
    *   [ ] RTSP/2.0.
    *   [ ] SRTP.
    *   [ ] ONVIF backchannel support (for sending audio).
    *   [ ] ONVIF replay mode.
    *   [x] receiving RTCP Sender Reports (currently only uses the timestamp)
    *   [ ] sending RTCP Receiver Reports
*   [ ] server support
*   I/O modes
    *   [x] async with tokio
    *   [ ] async-std
    *   [ ] synchronous with std only
*   codec depacketization
    *   [x] video
        * [x] H.264 (RFC 6184](https://datatracker.ietf.org/doc/html/rfc6184))
            *   [ ] SVC
            *   [ ] periodic infra refresh
            *   [x] multiple slices per picture
            *   [ ] multiple SPS/PPS
            *   [ ] interleaved mode
            *   [x] AAC output format
            *   [ ] Annex B output format ([#44](https://github.com/scottlamb/retina/issues/44))
        * [x] H.265 ([RFC 7798](https://tools.ietf.org/html/rfc7798))
        * [x] MJPEG ([RFC 2435](https://datatracker.ietf.org/doc/html/rfc2435))
    *   audio
        *   [x] AAC
            *   [ ] interleaving
        *   [x] [RFC 3551](https://datatracker.ietf.org/doc/html/rfc3551)
            codecs: G.711, G.723, L8/L16
    *   [x] application: ONVIF metadata
*   [ ] clean, stable API. (See [#47](https://github.com/scottlamb/retina/issues/47).)
*   quality errors
*   *   [x] detailed error description text.
*   *   [ ] programmatically inspectable error type.
*   [ ] good functional testing coverage. (Currently lightly / unevenly tested.
        Most depacketizers have no tests.)
*   [ ] fuzz testing. (In progress.)
*   [x] benchmark

Help welcome!

## Getting started

Try the `mp4` example. It streams from an RTSP server to a `.mp4` file until
you hit ctrl-C.

```shell
$ cargo run --package client mp4 -- --url rtsp://ip.address.goes.here/ --username admin --password test out.mp4
...
^C
```

## Example client

```shell
$ cargo run --package client <CMD>
```

Where CMD:

* **info** - Gets info about available streams and exits.
* **jpeg** - Writes depacketized JPEG images to disk; exit with CTRL+C.
* **mp4** - Writes RTSP streams to mp4 file; exit with Ctrl+C.
* **onvif** - Gets realtime onvif metadata if available; exit with Ctrl+C.
* **webcodecs** - Decodes video frames using WebCodecs API; exit with Ctrl+C.
  This is the absolute lowest-latency way to watch RTSP streams from a browser!

The client also respects the common `RUST_LOG` environment variable:
`RUST_LOG=client=debug,retina=debug,info` will enable `debug` logging for the
`client` and `retina` crates, leaving other Rust crates at `info` level.

## Example WebRTC proxy

This allows viewing a H.264 video stream from your browser, with the help of
[`webrtc-rs`](https://crates.io/crates/webrtc).

```shell
$ cargo run --package webrtc-proxy -- --help
```

## Example ffmpeg decode tool

This decodes video from an RTSP source into raw video frames in [PPM format](https://en.wikipedia.org/wiki/Netpbm)
using the `ffmpeg-next` crate.

## Acknowledgements

This builds on the whole Rust ecosystem. A couple folks have been especially
helpful:

*   Sebastian Dröge, author of
    [`sdp-types`](https://crates.io/crates/sdp-types)
*   David Holroyd, author of
    [`h264-reader`](https://crates.io/crates/h264-reader)

## Why "retina"?

It's a working name. Other ideas welcome. I started by looking at dictionary
words with the letters R, T, S, and P in order and picking out ones related to
video:

| `$ egrep '^r.*t.*s.*p' /usr/share/dict/words'` |                                                                              |
| ---------------------------------------------- | ---------------------------------------------------------------------------- |
| <b>r</b>e<b>t</b>ino<b>s</b>co<b>p</b>e        | close but too long, thus `retina`                                            |
| <b>r</b>e<b>t</b>ro<b>sp</b>ect                | good name for an NVR, but I already picked Moonfire                          |
| <b>r</b>o<b>t</b>a<b>s</b>co<b>p</b>e          | misspelling of "rotascope" (animation tool) or archaic name for "gyroscope"? |

## License

Your choice of MIT or Apache; see [LICENSE-MIT.txt](LICENSE-MIT.txt) or
[LICENSE-APACHE](LICENSE-APACHE.txt), respectively.
