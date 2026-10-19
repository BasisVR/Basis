//! Fuzz the Ogg Opus demuxer end to end: the `ogg` crate's page walk plus
//! our OpusHead/OpusTags handling and TOC duration derivation. Arbitrary
//! bytes must produce typed errors or a walkable stream, with no panics,
//! out-of-bounds reads or unbounded loops (the SourceIo budgets bound the
//! walk).

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::Generation;
use media_demux::{DemuxLimits, Demuxer, MemSource, OggOpusDemuxer, StreamEvent};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) =
        OggOpusDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
    else {
        return;
    };
    for _ in 0..8192 {
        match demux.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
    let _ = demux.take_notes();
});
