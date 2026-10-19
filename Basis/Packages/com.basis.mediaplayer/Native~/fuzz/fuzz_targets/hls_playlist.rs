//! Fuzz the HLS playlist surface: arbitrary bytes through the parser and
//! the schedule-shaped derivations the demuxer builds on it (variant
//! ordering, window shape, URI resolution, duration folding). Typed
//! errors are fine; panics and unbounded allocations are not. Segment
//! bytes go through the separately-fuzzed TS/MP4 demuxers, so this target
//! owns just the playlist/scheduler layer.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_hls::{ParsedPlaylist, looks_like_playlist, parse_playlist};

fuzz_target!(|data: &[u8]| {
    let _ = looks_like_playlist(data);
    match parse_playlist(data, "https://fuzz.invalid/path/index.m3u8") {
        Ok(ParsedPlaylist::Master(variants)) => {
            // Best-candidate ordering must hold for any parse result.
            assert!(variants.windows(2).all(|w| w[0].bandwidth >= w[1].bandwidth));
        }
        Ok(ParsedPlaylist::Media(window)) => {
            let mut total = media_clock::MediaTime::ZERO;
            for segment in &window.segments {
                total = total + segment.duration;
            }
            let _ = (window.first_sequence, window.ended, total);
        }
        Err(_) => {}
    }
    // Filesystem-base resolution is the local-fixture path; it must be
    // just as panic-free.
    let _ = parse_playlist(data, "C:/fixtures/hls/index.m3u8");
});
