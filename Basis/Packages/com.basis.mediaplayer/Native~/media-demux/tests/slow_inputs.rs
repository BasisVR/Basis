//! Fuzz-found slow inputs must demux (or refuse) quickly. Each pinned input
//! here once ran past the libFuzzer timeout in the mp4_stream target, and
//! has to complete inside a wall-clock budget that is generous for CI but
//! far below pathological.

use std::time::{Duration, Instant};

use media_clock::{Generation, MediaTime};
use media_demux::{DemuxLimits, Demuxer, MemSource, StreamEvent};

fn walk(bytes: Vec<u8>) -> Duration {
    let started = Instant::now();
    let limits = DemuxLimits {
        max_metadata_bytes: 4 * 1024 * 1024,
        max_au_bytes: 4 * 1024 * 1024,
    };
    if let Ok(mut demux) =
        media_demux::Mp4Demuxer::open(Box::new(MemSource(bytes)), limits, Generation(1))
    {
        let _ = demux.duration();
        for _ in 0..4096 {
            match demux.next_event() {
                Ok(StreamEvent::Eos(_)) | Err(_) => break,
                Ok(_) => {}
            }
        }
        let _ = demux.seek(MediaTime::from_secs(1), Generation(2));
    }
    started.elapsed()
}

/// Fuzz-found re_mp4 panic paths (u64 underflow on inconsistent mdhd
/// durations and kin): the open boundary's catch_unwind fence must turn
/// each into a typed error on the shipped panic=unwind build. These stay
/// out of the fuzz seed corpus: under panic=abort they read as crashes
/// and would kill every campaign at startup (see fuzz/README.md).
#[test]
fn pinned_re_mp4_panics_are_contained() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/data/re-mp4-panics");
    let mut checked = 0usize;
    for entry in std::fs::read_dir(dir).expect("pinned inputs present") {
        let path = entry.expect("entry").path();
        let bytes = std::fs::read(&path).expect("input readable");
        let limits = DemuxLimits {
            max_metadata_bytes: 4 * 1024 * 1024,
            max_au_bytes: 4 * 1024 * 1024,
        };
        let opened =
            media_demux::Mp4Demuxer::open(Box::new(MemSource(bytes)), limits, Generation(1));
        assert!(opened.is_err(), "{} must be refused", path.display());
        checked += 1;
    }
    assert!(checked >= 1, "expected pinned panic inputs");
}

#[test]
fn pinned_slow_inputs_stay_fast() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/data/slow-mp4");
    let mut checked = 0usize;
    for entry in std::fs::read_dir(dir).expect("pinned inputs present") {
        let path = entry.expect("entry").path();
        let bytes = std::fs::read(&path).expect("input readable");
        let took = walk(bytes);
        assert!(
            took < Duration::from_secs(5),
            "{} took {took:?}",
            path.display()
        );
        checked += 1;
    }
    assert!(checked >= 6, "expected the pinned timeout inputs");
}

/// Fuzz-found Matroska inputs that looped the EBML walker through the
/// source (millions of end-of-source reads chasing hostile seek-head
/// positions): the SourceIo budgets must turn each into a fast typed
/// refusal.
#[test]
fn pinned_slow_mkv_inputs_stay_fast() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/data/slow-mkv");
    let mut checked = 0usize;
    for entry in std::fs::read_dir(dir).expect("pinned inputs present") {
        let path = entry.expect("entry").path();
        let bytes = std::fs::read(&path).expect("input readable");
        let limits = DemuxLimits {
            max_metadata_bytes: 4 * 1024 * 1024,
            max_au_bytes: 4 * 1024 * 1024,
        };
        let started = std::time::Instant::now();
        let opened =
            media_demux::MkvDemuxer::open(Box::new(MemSource(bytes)), limits, Generation(1));
        let elapsed = started.elapsed();
        assert!(opened.is_err(), "{} must be refused", path.display());
        assert!(
            elapsed < std::time::Duration::from_secs(5),
            "{} took {elapsed:?}",
            path.display()
        );
        checked += 1;
    }
    assert!(checked >= 8, "expected the pinned timeout inputs");
}

/// Fuzz-found matroska-demuxer panic paths (block-timestamp overflow in
/// `parse_timestamp` on hostile blocks): the catch_unwind fences must
/// turn each into a typed error on the shipped panic=unwind build. Kept
/// out of the fuzz seed corpus: under panic=abort they read as crashes
/// and would kill campaigns at startup (see fuzz/README.md).
#[test]
fn pinned_matroska_panics_are_contained() {
    let dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/data/mkv-panics");
    let mut checked = 0usize;
    for entry in std::fs::read_dir(dir).expect("pinned inputs present") {
        let path = entry.expect("entry").path();
        let bytes = std::fs::read(&path).expect("input readable");
        let limits = DemuxLimits {
            max_metadata_bytes: 4 * 1024 * 1024,
            max_au_bytes: 4 * 1024 * 1024,
        };
        let opened =
            media_demux::MkvDemuxer::open(Box::new(MemSource(bytes)), limits, Generation(1));
        // The panic can sit in open (eager parse) or in the frame walk.
        if let Ok(mut demux) = opened {
            loop {
                match demux.next_event() {
                    Ok(media_demux::StreamEvent::Eos(_)) => break,
                    Ok(_) => {}
                    Err(_) => break,
                }
            }
        }
        checked += 1;
    }
    assert!(checked >= 2, "expected pinned panic inputs");
}
