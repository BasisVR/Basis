//! A seek's state while the demuxer is still finding the target. Ogg and
//! Matroska do their reads inside the seek itself, so over a slow host the
//! timeline in force stays the old one for seconds.

use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use media_clock::MediaTime;
use media_demux::{ByteSource, SourceError};
use media_engine::{OpenRequest, Session, State};

/// Serves a file from memory, each read taking `READ_DELAY` once `slow` is
/// set, like a distant host answering range requests.
struct SlowSource {
    bytes: Vec<u8>,
    slow: Arc<AtomicBool>,
}

const READ_DELAY: Duration = Duration::from_millis(250);

impl ByteSource for SlowSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(Some(self.bytes.len() as u64))
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        if self.slow.load(Ordering::Relaxed) {
            thread::sleep(READ_DELAY);
        }
        if offset >= self.bytes.len() as u64 {
            return Ok(0);
        }
        let from = offset as usize;
        let n = buf.len().min(self.bytes.len() - from);
        buf[..n].copy_from_slice(&self.bytes[from..from + n]);
        Ok(n)
    }
}

fn wait_for(deadline: Duration, mut check: impl FnMut() -> bool) -> bool {
    let end = Instant::now() + deadline;
    while Instant::now() < end {
        if check() {
            return true;
        }
        thread::sleep(Duration::from_millis(5));
    }
    false
}

/// An audio-only Ogg Opus seek over slow reads: the state stays Buffering
/// from the request until the position has jumped to the target, and only
/// then returns to Playing.
#[test]
fn a_seek_stays_buffering_until_it_lands() {
    let fixture =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/sine-48k-stereo.opus");
    let bytes = std::fs::read(&fixture).expect("fixture readable");
    let slow = Arc::new(AtomicBool::new(false));
    let mut session = Session::open_with_source(
        OpenRequest::new("slow-seek".to_owned()),
        Box::new(SlowSource {
            bytes,
            slow: slow.clone(),
        }),
    );
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );

    // The 6 s fixture, from near its start to 4.5 s: the clock running on
    // through the slow seek stays well short of the target.
    let target_us = 4_500_000i64;
    slow.store(true, Ordering::Relaxed);
    session.seek(MediaTime::from_micros(target_us));

    let mut early_playing = None;
    let landed = wait_for(Duration::from_secs(20), || {
        let state = shared.state.load(Ordering::Relaxed);
        let position = shared.position_us.load(Ordering::Relaxed);
        if position >= target_us - 300_000 {
            return true;
        }
        if state == State::Playing as u32 && early_playing.is_none() {
            early_playing = Some(position);
        }
        false
    });
    slow.store(false, Ordering::Relaxed);
    assert!(
        landed,
        "the seek never landed (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    assert_eq!(
        early_playing, None,
        "Playing was reported at position {early_playing:?} us, before the seek to {target_us} us landed"
    );
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "the landed seek never returned to Playing (state {})",
        shared.state.load(Ordering::Relaxed),
    );
    session.close();
}
