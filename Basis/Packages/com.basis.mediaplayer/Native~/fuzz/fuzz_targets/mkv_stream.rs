//! Fuzz the Matroska demuxer end to end: arbitrary bytes must produce
//! typed errors or a walkable event stream, never a panic or an
//! out-of-bounds read. Covers the EBML walk (matroska-demuxer), our track
//! mapping, avcC parsing and Annex-B conversion.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::Generation;
use media_demux::{DemuxLimits, Demuxer, MemSource, MkvDemuxer, StreamEvent};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) = MkvDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
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
