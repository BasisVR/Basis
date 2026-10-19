//! Fuzz the streaming MP4 demuxer end to end: arbitrary bytes must produce
//! typed errors or a walkable event stream, never a panic escaping the
//! demuxer or an out-of-bounds read. The open path fences re_mp4's own
//! panics into typed errors; everything after open (sample walking,
//! Annex-B conversion, seek) runs unfenced and is what this target
//! exercises.
//!
//! The seeds include a fragmented file carrying a segment index, which is
//! the layout that opens from the index and reads a movie fragment per
//! seek: a mutation inside a fragment leaves the index still covering the
//! file, so the index walk, the subsegment reader and the per-fragment
//! sample builder all run on bytes they did not expect.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::{Generation, MediaTime};
use media_demux::{DemuxLimits, Demuxer, MemSource, Mp4Demuxer, StreamEvent};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        // Small caps keep iterations fast and exercise the cap paths.
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) = Mp4Demuxer::open(
        Box::new(MemSource(data.to_vec())),
        limits,
        Generation(1),
    ) else {
        return;
    };
    let _ = demux.duration();
    let _ = demux.take_notes();
    for _ in 0..4096 {
        match demux.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
    // The start, somewhere inside, and past the last sample: an index
    // seek searches forward and back from where the index puts the
    // target, and every landing reads a fragment of its own.
    for (generation, secs) in [0i64, 1, 5, 3600].into_iter().enumerate() {
        let _ = demux.seek(
            MediaTime::from_secs(secs),
            Generation(generation as u64 + 2),
        );
        for _ in 0..64 {
            match demux.next_event() {
                Ok(StreamEvent::Eos(_)) | Err(_) => break,
                Ok(_) => {}
            }
        }
    }
});
