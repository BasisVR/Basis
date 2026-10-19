//! What a video decoder too slow for its stream may cost: video frames,
//! and nothing else. With `BASIS_MEDIA_SLOW_VIDEO_DECODE_MS` set, every
//! routed video decoder takes that long over each frame, so video falls
//! steadily behind an audio leg that has no such trouble.
//!
//! Audio is master and never gives way, so it must hold its stream rate
//! regardless, and position, captions and user data with it. The picture
//! may be held, but it may not move out of step with the sound: a frame
//! that cannot be shown within the lip-sync limit is not shown.
//!
//! The consumer asks for a buffer on the hardware cadence and never asks
//! again for a shortfall, as an audio device behaves: time lost to an
//! underrun is gone. A consumer that paced itself off what it was served
//! would make up the shortfall on the next pass and hide it.
//!
//! The source must outlast the decode channel, which absorbs 256 access
//! units (eight and a half seconds of this fixture) before the video track
//! is gated at all. The measured window starts well past that; a short
//! fixture would pass having shown nothing.
//!
//! Lives in its own integration-test binary because the environment
//! variable is process-wide.

#![cfg(windows)]

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_diag::{EventCode, Stage};
use media_engine::{OpenRequest, SLOW_VIDEO_DECODE_ENV, Session, State};

const PER_FRAME_MS: u64 = 80;
const BUFFER_FRAMES: usize = 1024;
const WINDOW_FROM: Duration = Duration::from_secs(18);
const WINDOW_TO: Duration = Duration::from_secs(26);
/// How far behind the sound a frame may go up: the lip-sync limit. Stated
/// here rather than taken from the engine, so changing the engine's figure
/// has to change this test too.
const LIP_SYNC_LIMIT_US: i64 = 40_000;
/// What presenting and the poll below add to a lateness read after the fact.
const POLL_SLACK_US: i64 = 40_000;

fn fixture_path() -> String {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-320x180-30s.mp4")
        .to_string_lossy()
        .into_owned()
}

struct Mark {
    asked: u64,
    served: u64,
    decoded: u64,
    presented: u64,
    position_us: i64,
}

#[test]
fn a_slow_video_decoder_costs_video_and_not_audio() {
    // SAFETY: set before any session thread exists; this test binary has
    // exactly one test, so nothing else reads the environment
    // concurrently.
    unsafe { std::env::set_var(SLOW_VIDEO_DECODE_ENV, PER_FRAME_MS.to_string()) };

    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let presented = || {
        px.diag
            .stage(Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
    };

    let mut buf: Vec<f32> = Vec::new();
    let mut playing_since: Option<Instant> = None;
    let mut asked = 0u64;
    let mut served = 0u64;
    let mut window: Option<Mark> = None;
    let mut timeline = String::new();
    let mut next_line = Duration::from_secs(1);
    let mut last = (0u64, 0u64, 0u64, 0u64);
    let mut shown_pts = i64::MIN;
    let mut worst_late_us = i64::MIN;
    let mut skips = 0usize;
    let opened = Instant::now();

    let (from, to) = loop {
        assert!(
            opened.elapsed() < Duration::from_secs(40),
            "the session never reached the measured window\n{timeline}"
        );
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        skips += px
            .diag
            .take_events()
            .iter()
            .filter(|e| e.code == EventCode::LateVideoSkip)
            .count();
        let rate = u64::from(shared.audio_rate.load(Ordering::Relaxed));
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1) as usize;
        if rate > 0 && state == State::Playing as u32 {
            let since = *playing_since.get_or_insert_with(Instant::now);
            let elapsed = since.elapsed();
            buf.resize(BUFFER_FRAMES * channels, 0.0);
            while asked + BUFFER_FRAMES as u64 <= elapsed.as_micros() as u64 * rate / 1_000_000 {
                served += Session::read_audio(&px, &mut buf) as u64;
                asked += BUFFER_FRAMES as u64;
            }
            // A frame that has just gone up: how far behind the clock was it?
            let pts = px.presented_pts_us.load(Ordering::Relaxed);
            if pts != shown_pts && pts != i64::MIN {
                shown_pts = pts;
                let clock = px
                    .clock
                    .lock()
                    .expect("clock lock")
                    .now(px.wall.now())
                    .as_micros();
                worst_late_us = worst_late_us.max(clock - pts);
            }
            let decoded = shared.frames_decoded.load(Ordering::Relaxed);
            if elapsed >= next_line {
                timeline.push_str(&format!(
                    "  {:>2} s: audio served {:>6} of {:>6} asked, video decoded {:>3}, presented {:>3}, discarded {:>4} in total, position {:>6} ms, trimmed {} in total\n",
                    next_line.as_secs(),
                    served - last.0,
                    asked - last.1,
                    decoded - last.2,
                    presented() - last.3,
                    px.diag.stage(Stage::Decode).drops.load(Ordering::Relaxed),
                    shared.position_us.load(Ordering::Relaxed) / 1000,
                    px.diag.audio_trimmed(),
                ));
                last = (served, asked, decoded, presented());
                next_line += Duration::from_secs(1);
            }
            let mark = || Mark {
                asked,
                served,
                decoded,
                presented: presented(),
                position_us: shared.position_us.load(Ordering::Relaxed),
            };
            if window.is_none() && elapsed >= WINDOW_FROM {
                window = Some(mark());
            }
            if window.is_some() && elapsed >= WINDOW_TO {
                break (window.take().expect("window opened"), mark());
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    };
    session.close();
    println!("{timeline}");
    println!("worst lateness of a presented frame: {worst_late_us} us; {skips} skips reported");

    let seconds = (WINDOW_TO - WINDOW_FROM).as_secs();
    let decoded = to.decoded - from.decoded;
    assert!(
        decoded <= 20 * seconds,
        "the decoder was not slow, so the row shows nothing: {decoded} frames in {seconds} s\n{timeline}"
    );
    let (asked, served) = (to.asked - from.asked, to.served - from.served);
    assert!(
        served * 100 >= asked * 98,
        "slow video cost audio: {served} of {asked} frames served between {} s and {} s\n{timeline}",
        WINDOW_FROM.as_secs(),
        WINDOW_TO.as_secs()
    );
    let moved_us = to.position_us - from.position_us;
    assert!(
        moved_us * 100 >= seconds as i64 * 1_000_000 * 98,
        "position moved {moved_us} us in {seconds} s: it is the clock's, and slow video must not hold it\n{timeline}"
    );
    assert!(
        worst_late_us <= LIP_SYNC_LIMIT_US + POLL_SLACK_US,
        "a frame went up {worst_late_us} us behind the clock, out of step with the sound\n{timeline}"
    );
    assert!(
        to.presented > from.presented,
        "no frame was shown in {seconds} s: late video has to rejoin at a keyframe, not stop\n{timeline}"
    );
    assert!(
        skips > 0,
        "video was discarded without the session saying so\n{timeline}"
    );
}
