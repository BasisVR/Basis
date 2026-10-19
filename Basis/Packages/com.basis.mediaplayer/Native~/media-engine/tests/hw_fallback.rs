//! Decode routing with the hardware path withheld. With
//! `BASIS_MEDIA_DISABLE_HW_DECODE` set, every hardware probe reports
//! absent, so the default preference falls back to software with a
//! `DecodeFallbackHwToSw` diagnostic and play continues. The hardware-only
//! preference must instead refuse with CodecRefused: video mutes, audio
//! plays out and owns Ended. With no audio to play, the refusal fails the
//! session.
//!
//! Lives in its own integration-test binary because the environment
//! variable is process-wide.

#![cfg(windows)]

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_diag::EventCode;
use media_engine::{DecodePreference, OpenRequest, Session, State};

fn fixture_path(name: &str) -> String {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name)
        .to_string_lossy()
        .into_owned()
}

fn run_session(preference: DecodePreference) -> (Vec<EventCode>, u64, bool) {
    let mut request = OpenRequest::new(fixture_path("h264-aac-640x360-30fps.mp4"));
    request.decode_preference = preference;
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut events = Vec::new();
    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let start = Instant::now();
    let ended = loop {
        if start.elapsed() > Duration::from_secs(20) {
            break false;
        }
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        events.extend(px.diag.take_events().into_iter().map(|e| e.code));
        if state == State::Ended as u32 {
            break true;
        }
        // Pull audio at the hardware cadence so the session can end.
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        if rate > 0 && state == State::Playing as u32 {
            let at = *epoch.get_or_insert_with(Instant::now);
            let budget = at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - pulled;
            if budget as usize >= buf.len() / channels as usize {
                pulled += Session::read_audio(&px, &mut buf) as u64;
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    };
    let decoded = shared.frames_decoded.load(Ordering::Relaxed);
    session.close();
    (events, decoded, ended)
}

#[test]
fn withheld_hardware_falls_back_reported_and_hardware_only_refuses() {
    // SAFETY: set before any session thread exists; this test binary has
    // exactly one test, so nothing else reads the environment
    // concurrently.
    unsafe { std::env::set_var(decode_mf::DISABLE_HW_DECODE_ENV, "1") };

    // Default preference: software fallback engages, is reported, plays out.
    let (events, decoded, ended) = run_session(DecodePreference::HardwareWithFallback);
    assert!(
        events.contains(&EventCode::DecodeFallbackHwToSw),
        "fallback must be reported, got {events:?}"
    );
    assert!(decoded > 100, "software rung must decode ({decoded})");
    assert!(ended, "session must end naturally");

    // Hardware-only: typed refusal, video mutes, audio plays out.
    let (events, decoded, ended) = run_session(DecodePreference::HardwareOnly);
    assert!(
        events.contains(&EventCode::CodecRefused),
        "hardware-only without hardware must refuse typed, got {events:?}"
    );
    assert_eq!(decoded, 0, "no video decoder may open");
    assert!(ended, "audio must own Ended after the video refusal");

    // Hardware-only with no audio to fall back on: the refusal leaves
    // nothing to play, so the session fails with its reason at once rather
    // than reading the whole 12 s file to an end.
    let mut request = OpenRequest::new(fixture_path("h264-640x360-30fps.mp4"));
    request.decode_preference = DecodePreference::HardwareOnly;
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let start = Instant::now();
    let mut events = Vec::new();
    let state = loop {
        let state = shared.state.load(Ordering::Relaxed);
        events.extend(px.diag.take_events());
        if state == State::Error as u32 || state == State::Ended as u32 {
            break state;
        }
        assert!(
            start.elapsed() < Duration::from_secs(20),
            "the session never settled"
        );
        std::thread::sleep(Duration::from_millis(2));
    };
    let settled_in = start.elapsed();
    events.extend(px.diag.take_events());
    session.close();
    assert_eq!(
        state,
        State::Error as u32,
        "a session with nothing playable must fail"
    );
    assert_eq!(shared.last_error.load(Ordering::Relaxed), 302);
    assert!(
        settled_in < Duration::from_secs(3),
        "the failure must not wait for the file to end ({settled_in:?})"
    );
    let error = events
        .iter()
        .find(|e| e.code == EventCode::Error)
        .expect("the failure must carry its reason");
    assert!(
        error.detail.starts_with("hardware-only decode preference:"),
        "{}",
        error.detail
    );
}
