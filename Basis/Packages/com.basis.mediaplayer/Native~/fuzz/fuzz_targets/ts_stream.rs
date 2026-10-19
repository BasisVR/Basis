//! Fuzz the MPEG-TS demuxer end to end: arbitrary bytes must produce typed
//! errors or a walkable event stream, never a panic or an out-of-bounds
//! read. The seed corpus carries four known crash inputs (PAT/PMT
//! section-length OOB, SPS bit-position overflow, SPS crop integer
//! overflow, SPS ue(v) shift UB) so their guards stay regression-tested.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::Generation;
use media_demux::{DemuxLimits, Demuxer, MemSource, StreamEvent, TsDemuxer};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        // Small caps keep iterations fast and exercise the cap paths.
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) = TsDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
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
