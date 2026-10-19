# MP4 parser

`re_mp4` is a Rust library for parsing the `.mp4` video container.

(NOTE: `re_mp4` does NOT decode the video).

Originally a fork of the [mp4](https://github.com/alfg/mp4-rust) crate. Some code was ported from [mp4box.js](https://github.com/gpac/mp4box.js).

The goal behind forking was to make this library suitable for use with the [`WebCodecs`](https://developer.mozilla.org/en-US/docs/Web/API/WebCodecs_API) API to build Rust-based video players for the web.

## Related Projects
* https://github.com/alfg/mp4-rust
* https://github.com/gpac/mp4box.js
* https://github.com/mozilla/mp4parse-rust
* https://github.com/pcwalton/rust-media
* https://github.com/alfg/mp4

## License
MIT

