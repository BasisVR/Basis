//! Fuzz the RIFF/WAVE demuxer: arbitrary bytes must produce typed errors
//! or a walkable event stream, never a panic, out-of-bounds read or
//! unbounded loop. Covers the chunk walk (including its budget), the
//! `fmt ` parse with its EXTENSIBLE arm, the format screen, and seeks
//! landing anywhere in or past the data chunk.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::{Generation, MediaTime};
use media_demux::{DemuxLimits, Demuxer, MemSource, StreamEvent, WavDemuxer};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let Ok(mut demux) = WavDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
    else {
        return;
    };
    // A seek derived from the input drives the byte-offset arithmetic
    // (including targets past the end) before the walk.
    let target = i64::from(u32::from_le_bytes([
        data.first().copied().unwrap_or(0),
        data.get(1).copied().unwrap_or(0),
        data.get(2).copied().unwrap_or(0),
        data.get(3).copied().unwrap_or(0),
    ]));
    let _ = demux.seek(MediaTime::from_micros(target), Generation(2));
    let _ = demux.duration();
    for _ in 0..8192 {
        match demux.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
});
