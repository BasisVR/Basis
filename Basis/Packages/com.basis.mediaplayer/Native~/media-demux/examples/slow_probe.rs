//! Diagnostic: time each phase of demuxing a pathological input.
//! `cargo run -p media-demux --example slow_probe -- <file>`

use std::time::Instant;

use media_clock::Generation;
use media_demux::{DemuxLimits, Demuxer, MemSource, StreamEvent};

fn main() {
    let path = std::env::args().nth(1).expect("input path");
    let bytes = std::fs::read(&path).expect("readable");
    println!("{path}: {} bytes", bytes.len());
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    let t0 = Instant::now();
    let opened = media_demux::Mp4Demuxer::open(Box::new(MemSource(bytes)), limits, Generation(1));
    println!("open: {:?} -> {}", t0.elapsed(), opened.is_ok());
    let Ok(mut demux) = opened else { return };
    let t1 = Instant::now();
    let mut events = 0usize;
    for _ in 0..4096 {
        match demux.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => events += 1,
        }
    }
    println!("walk: {:?} ({events} events)", t1.elapsed());
}
