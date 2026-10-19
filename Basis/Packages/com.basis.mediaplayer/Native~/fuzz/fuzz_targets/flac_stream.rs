//! Fuzz the raw FLAC demuxer: arbitrary bytes must produce typed errors
//! or a walkable event stream, never a panic, out-of-bounds read or
//! unbounded loop. Covers the metadata walk, frame-header validation
//! (CRC-8), the frame-boundary scan and the seek bisection, including
//! the SEEKTABLE points it takes its bounds from.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::{Generation, MediaTime};
use media_demux::{DemuxLimits, Demuxer, FlacDemuxer, MemSource, StreamEvent};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) =
        FlacDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
    else {
        return;
    };
    // A seek derived from the input drives the bisection before the walk.
    // It is read from the tail, which the fuzzer can vary freely (the head
    // is the magic and STREAMINFO, where a mutation just fails the open),
    // and folded into a range a fixture-sized input reaches so the
    // bisection runs rather than clamping to the end every time.
    let mut key = [0u8; 4];
    for (slot, byte) in key.iter_mut().zip(data.iter().rev()) {
        *slot = *byte;
    }
    let target = i64::from(u32::from_le_bytes(key)) % 30_000_000;
    let _ = demux.seek(MediaTime::from_micros(target), Generation(2));
    let _ = demux.duration();
    for _ in 0..8192 {
        match demux.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
});
