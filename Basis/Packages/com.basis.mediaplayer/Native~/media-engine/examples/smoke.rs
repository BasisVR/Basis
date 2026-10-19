//! Headless vertical-slice smoke test: HTTP/file MP4 -> streaming demux ->
//! Bank -> MF decode (video + audio) -> FramePool -> shared texture ->
//! consumer copy on a second D3D11 device, with an audio pull standing in
//! for the Unity audio thread.
//!
//! Usage: cargo run -p media-engine --example smoke -- <fixture.mp4|url> [seconds]

#[cfg(windows)]
use std::sync::atomic::Ordering;
#[cfg(windows)]
use std::time::{Duration, Instant};

#[cfg(windows)]
use media_engine::{OpenRequest, Session, State};
#[cfg(windows)]
use media_present::{SharedTextureConsumer, TestConsumerTarget};

/// The consumer half is the D3D11 shared-texture contract; other
/// platforms exercise the headless pipeline through `bm-probe play`.
#[cfg(not(windows))]
fn main() {
    eprintln!("smoke is Windows-only (D3D11 shared-texture consumer); use bm-probe play");
}

#[cfg(windows)]
fn main() {
    let mut args = std::env::args().skip(1);
    let url = args
        .next()
        .expect("usage: smoke <fixture.mp4|url> [seconds]");
    let seconds: u64 = args
        .next()
        .map(|s| s.parse().expect("seconds"))
        .unwrap_or(5);

    let mut request = OpenRequest::new(url);
    request.allow_local_addresses = true;
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    // Wait for the pipeline to publish dimensions + shared texture handle.
    let deadline = Instant::now() + Duration::from_secs(10);
    let (width, height, handle) = loop {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(state, State::Error as u32, "session errored during open");
        let handle = shared.shared_texture_handle.load(Ordering::Acquire);
        if handle != 0 {
            break (
                shared.width.load(Ordering::Relaxed),
                shared.height.load(Ordering::Relaxed),
                handle,
            );
        }
        assert!(
            Instant::now() < deadline,
            "timed out waiting for shared texture"
        );
        std::thread::sleep(Duration::from_millis(10));
    };
    println!(
        "stream: {width}x{height}, duration {} ms",
        shared.duration_us.load(Ordering::Relaxed) / 1000
    );

    let target = TestConsumerTarget::new(width, height).expect("consumer target");
    // SAFETY: texture_ptr is a live ID3D11Texture2D* owned by `target`,
    // which outlives the consumer; handle is the session's live shared
    // handle.
    let mut consumer = unsafe { SharedTextureConsumer::open(target.texture_ptr(), handle) }
        .expect("open consumer");

    // Pull audio as the Unity audio thread does: interleaved buffers at the
    // hardware rate. Never faster, or the audio playhead (the clock master)
    // would race ahead of real time.
    let mut copies = 0u64;
    let mut audio_frames = 0u64;
    let mut audio_epoch: Option<Instant> = None;
    let mut audio_buf = vec![0.0f32; 1024 * 2];
    let end = Instant::now() + Duration::from_secs(seconds);
    while Instant::now() < end {
        if consumer.copy_if_fresh().expect("copy") {
            copies += 1;
            shared.frames_presented.fetch_add(1, Ordering::Relaxed);
        }
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        if rate > 0 && shared.state.load(Ordering::Relaxed) == State::Playing as u32 {
            // The hardware-cadence budget starts when playback does.
            let epoch = *audio_epoch.get_or_insert_with(Instant::now);
            let budget =
                epoch.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - audio_frames;
            if budget as usize >= audio_buf.len() / channels as usize {
                audio_frames += Session::read_audio(&px, &mut audio_buf) as u64;
            }
        }
        std::thread::sleep(Duration::from_millis(4));
    }

    let decoded = shared.frames_decoded.load(Ordering::Relaxed);
    let position_ms = shared.position_us.load(Ordering::Relaxed) / 1000;
    let state = shared.state.load(Ordering::Relaxed);
    let audio_rate = shared.audio_rate.load(Ordering::Relaxed);
    let diag = session.diag().snapshot();
    println!(
        "stages: demux out {}, bank in/out {}/{}, decode in/out {}/{}, pool occ {} drops {}, present out {}",
        diag[media_diag::Stage::Demux as usize].out_count,
        diag[media_diag::Stage::Bank as usize].in_count,
        diag[media_diag::Stage::Bank as usize].out_count,
        diag[media_diag::Stage::Decode as usize].in_count,
        diag[media_diag::Stage::Decode as usize].out_count,
        diag[media_diag::Stage::Pool as usize].occupancy,
        diag[media_diag::Stage::Pool as usize].drops,
        diag[media_diag::Stage::Present as usize].out_count,
    );
    println!(
        "state {state}, decoded {decoded} frames, consumed {copies} frames, \
         audio {audio_frames} frames @ {audio_rate} Hz, position {position_ms} ms"
    );
    session.close();

    assert!(
        state == State::Playing as u32 || state == State::Ended as u32,
        "expected Playing/Ended, got {state}"
    );
    assert!(decoded > 0, "no frames decoded");
    assert!(copies > 0, "no frames crossed the shared texture");
    assert!(position_ms > 0, "position never advanced");
    println!("smoke OK");
}
