# matroska-demuxer

[![Latest version](https://img.shields.io/crates/v/matroska-demuxer.svg)](https://crates.io/crates/matroska-demuxer)
[![Documentation](https://docs.rs/matroska-demuxer/badge.svg)](https://docs.rs/matroska-demuxer)
![ZLIB](https://img.shields.io/badge/license-zlib-blue.svg)
![MIT](https://img.shields.io/badge/license-MIT-blue.svg)
![Apache](https://img.shields.io/badge/license-Apache-blue.svg)

A demuxer that can demux Matroska and WebM container files.

For simplicity only the elements supported by both Matroska and WebM are supported.

## Integration test

To run the integration test you need to
download [the Matroska test suite](https://sourceforge.net/projects/matroska/files/test_files/matroska_test_w1_1.zip/download)
video files and extract them into the `tests/data` folder (test1.mkv to test8.mkv).

## License

Licensed under MIT or Apache-2.0 or ZLIB.
