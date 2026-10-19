//! Fuzz the ADTS demuxer: sync hunting, header-length arithmetic, the
//! ASC reconstruction and the byte-rate seek estimate must hold up under
//! arbitrary bytes: typed errors only, no panics, no unbounded loops.

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::{Generation, MediaTime};
use media_demux::{AdtsDemuxer, DemuxLimits, Demuxer, MemSource, StreamEvent};

fuzz_target!(|data: &[u8]| {
    let limits = DemuxLimits::default();
    let Ok(mut demux) =
        AdtsDemuxer::open(Box::new(MemSource(data.to_vec())), limits, Generation(1))
    else {
        return;
    };
    // A seek derived from the input drives the byte estimate and the
    // confirmed-landing scan before the walk. It is read from the tail,
    // which the fuzzer can vary freely (the head is the sync word the open
    // hunts for), and folded into a range a fixture-sized input reaches
    // rather than always past its end.
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
    let _ = demux.take_notes();
});
