//! Session lifecycle over the real pipeline (Windows: MF decode): state
//! machine, pause freezing the position, keyframe-clean seek, natural end.

#![cfg(windows)]

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_clock::MediaTime;
use media_engine::{OpenRequest, Session, State};

fn fixture_path() -> String {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned()
}

fn wait_for(deadline: Duration, mut check: impl FnMut() -> bool) -> bool {
    let end = Instant::now() + deadline;
    while Instant::now() < end {
        if check() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(10));
    }
    false
}

/// Audio-only: the session plays without a video track, and Ended waits
/// for the ring's tail to be consumed instead of firing at demux EOS. The
/// pulled total must cover (nearly) the whole fixture.
#[test]
fn audio_only_plays_out_the_tail() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/aac-48k-stereo.m4a")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    // Pull like the Unity audio thread: at the hardware cadence.
    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(15) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        if state == State::Ended as u32 {
            break;
        }
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
    }

    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Ended as u32,
        "audio-only session must end naturally"
    );
    // The fixture is ~6 s at 48 kHz; the tail must not be cut at EOS,
    // which would lose up to the ring depth (2 s).
    assert!(
        pulled >= 5 * 48_000,
        "pulled only {pulled} frames — the ring tail was cut"
    );
    assert!(
        shared.position_us.load(Ordering::Relaxed) > 4_000_000,
        "position must be clock-derived for audio-only sessions"
    );
    session.close();
}

/// The A/V twin of `audio_only_plays_out_the_tail`. A session with both
/// kinds of track must not declare Ended when the last *picture* is
/// presented while the audio ring still holds sound. Once the session has
/// ended `read_audio` serves nothing, and the rest would be unreachable.
///
/// Asserted as an invariant rather than a frame total, because a total
/// cannot separate this from the serve-side lateness trim, which discards
/// late audio deliberately. At Ended every frame pushed into the ring must
/// be accounted for: handed to the consumer, or trimmed. Anything else was
/// cut.
#[test]
fn an_av_session_plays_out_the_audio_tail() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(30) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        if state == State::Ended as u32 {
            break;
        }
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
    }

    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Ended as u32,
        "an A/V session must still end naturally"
    );
    let pushed = px.audio_shared.pushed_frames.load(Ordering::Relaxed);
    let consumed = px.audio_shared.consumed_frames.load(Ordering::Relaxed);
    let trimmed = px.audio_shared.trimmed_frames.load(Ordering::Relaxed);
    assert!(pushed > 0, "the fixture has audio");
    assert_eq!(
        pushed,
        consumed + trimmed,
        "Ended left {} frames in the ring — the tail was cut when the picture ran out          (pushed {pushed}, consumed {consumed}, trimmed {trimmed})",
        pushed - consumed - trimmed
    );
    session.close();
}

/// A seek issued near EOS, where the audio side has already announced that it
/// has nothing left to play out for the generation being left behind.
///
/// The two decode threads observe a seek independently, so the video thread can
/// process its Flush, run the short remainder of the new generation and reach
/// its end check while the audio thread is still on the old one. A bare "audio
/// is done" flag would end the session on the previous generation's answer,
/// cutting the new one off before it plays, so the published value carries its
/// generation.
///
/// This guards the outcome rather than the race, which cannot be scheduled on
/// demand.
#[test]
fn a_seek_near_eos_does_not_end_the_new_generation_early() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut buf = vec![0f32; 2048];
    let pull = |pulled: &mut u64, buf: &mut [f32]| {
        if shared.audio_rate.load(Ordering::Relaxed) > 0
            && shared.state.load(Ordering::Relaxed) == State::Playing as u32
        {
            *pulled += Session::read_audio(&px, buf) as u64;
        }
    };

    // Up to the tail of the fixture, so the audio side has drained and said so.
    let mut before = 0u64;
    let reached = wait_for(Duration::from_secs(20), || {
        pull(&mut before, &mut buf);
        std::thread::sleep(Duration::from_millis(2));
        shared.position_us.load(Ordering::Relaxed) > 5_400_000
            || shared.state.load(Ordering::Relaxed) == State::Ended as u32
    });
    assert!(reached, "never reached the tail of the fixture");

    session.seek(MediaTime::from_millis(5_400));

    // The new generation has only ~0.6 s to play, deliberately: the video
    // thread finishes it almost at once, so if the end check took the old
    // generation's answer the session would end before any of it is heard.
    let mut after = 0u64;
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(20) {
        if shared.state.load(Ordering::Relaxed) == State::Ended as u32 {
            break;
        }
        pull(&mut after, &mut buf);
        std::thread::sleep(Duration::from_millis(2));
    }

    assert!(
        after >= 20_000,
        "pulled only {after} frames after the seek — the new generation was cut short"
    );
    session.close();
}

/// The PCM interleave for multichannel audio is WAV/channel-mask order (FL
/// FR C LFE BL BR), the order every decoder behind the engine emits (Media
/// Foundation's PCM convention here; the Android AAC decoder's FDK default
/// and FLAC's stored order elsewhere). The managed stereo downmix keys its
/// matrix on it. Checked with a channel-marker fixture: one distinct sine
/// per speaker, identified per interleave slot.
#[test]
fn multichannel_interleave_is_wav_order() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/sine-48k-51.m4a")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut pulled: Vec<f32> = Vec::new();
    let mut buf = vec![0f32; 6 * 512];
    let mut epoch: Option<Instant> = None;
    let mut frames = 0u64;
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(10) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(state, State::Error as u32);
        if state == State::Ended as u32 {
            break;
        }
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        if rate > 0 && state == State::Playing as u32 {
            let at = *epoch.get_or_insert_with(Instant::now);
            let budget = at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - frames;
            if budget as usize >= 512 {
                let got = Session::read_audio(&px, &mut buf);
                frames += got as u64;
                pulled.extend_from_slice(&buf[..got * 6]);
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    session.close();
    assert_eq!(
        shared.audio_channels.load(Ordering::Relaxed),
        6,
        "fixture must announce 5.1"
    );
    let total = pulled.len() / 6;
    assert!(total > 96_000, "pulled only {total} frames");

    // Goertzel power at each marker tone, one window mid-stream.
    let window = &pulled[6 * 48_000..6 * 96_000];
    let tones = [400.0f64, 800.0, 1200.0, 60.0, 1600.0, 2000.0];
    let goertzel = |slot: usize, freq: f64| -> f64 {
        let w = 2.0 * std::f64::consts::PI * freq / 48_000.0;
        let c = 2.0 * w.cos();
        let (mut s1, mut s2) = (0.0f64, 0.0f64);
        for frame in window.as_chunks::<6>().0 {
            let s0 = f64::from(frame[slot]) + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }
        s1 * s1 + s2 * s2 - c * s1 * s2
    };
    for (slot, _) in tones.iter().enumerate() {
        let powers: Vec<f64> = tones.iter().map(|&f| goertzel(slot, f)).collect();
        let best = powers
            .iter()
            .enumerate()
            .max_by(|a, b| a.1.total_cmp(b.1))
            .unwrap()
            .0;
        assert_eq!(
            best, slot,
            "slot {slot} carries the tone for WAV-order channel {best}"
        );
    }
}

/// Pull a session's audio at the hardware cadence until `frames` have been
/// served or it ends, keeping the first channel.
fn pull_mono(session: &Session, frames: usize) -> Vec<f32> {
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let mut out = Vec::new();
    let mut buf = vec![0f32; 2048];
    let mut served = 0u64;
    let mut epoch: Option<Instant> = None;
    let start = Instant::now();
    while out.len() < frames && start.elapsed() < Duration::from_secs(15) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        if state == State::Ended as u32 {
            break;
        }
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1) as usize;
        if rate > 0 && state == State::Playing as u32 {
            let at = *epoch.get_or_insert_with(Instant::now);
            let budget = (at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000)
                .saturating_sub(served);
            if budget as usize >= buf.len() / channels {
                let got = Session::read_audio(&px, &mut buf);
                served += got as u64;
                out.extend(buf[..got * channels].iter().step_by(channels));
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    out
}

fn late_start_fixture(name: &str) -> String {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name)
        .to_string_lossy()
        .into_owned()
}

/// An MP4 whose sound starts 500 ms after its picture, primed by 1,024
/// samples. The gap plays as silence from zero, so the audio clock starts
/// with the picture, and the priming, which decodes to 478.7 ms, is not
/// played in it. The sound itself starts at 500 ms, whole.
#[test]
fn a_late_audio_start_plays_its_gap_as_silence() {
    let mut session = Session::open(OpenRequest::new(late_start_fixture(
        "h264-aac-late-audio.mp4",
    )));
    let heard = pull_mono(&session, 36_000);
    let first = session
        .pipeline()
        .audio_shared
        .base_pts_us
        .load(Ordering::Relaxed);
    session.close();
    assert_eq!(
        first, 0,
        "the ring starts at {first} us, not with the picture"
    );
    assert!(heard.len() >= 36_000, "only {} frames served", heard.len());
    // 24,000 frames is 500 ms at 48 kHz; a frame either side for rounding.
    let gap = &heard[..23_999];
    let loudest = gap.iter().fold(0f32, |m, v| m.max(v.abs()));
    assert_eq!(loudest, 0.0, "the gap carries sound (peak {loudest})");
    let sound = &heard[24_001..36_000];
    assert!(
        sound.iter().any(|v| v.abs() > 0.01),
        "no sound after the gap"
    );
}

/// The same sound on its own. The clock starts at zero rather than at the
/// sound, so the position covers the gap; and a seek into the gap starts
/// the ring where the seek did.
#[test]
fn an_audio_only_late_start_keeps_the_clock_at_zero() {
    let mut session = Session::open(OpenRequest::new(late_start_fixture("aac-late-start.m4a")));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    let first = px.audio_shared.base_pts_us.load(Ordering::Relaxed);
    let position = shared.position_us.load(Ordering::Relaxed);
    assert_eq!(first, 0, "the ring starts at {first} us");
    assert!(position < 250_000, "playback opened at {position} us");

    session.seek(MediaTime::from_millis(200));
    let settled = wait_for(Duration::from_secs(5), || {
        shared.state.load(Ordering::Relaxed) == State::Playing as u32
            && px.audio_shared.base_pts_us.load(Ordering::Relaxed) != 0
    });
    let landed = px.audio_shared.base_pts_us.load(Ordering::Relaxed);
    session.close();
    assert!(settled, "the seek did not settle");
    assert_eq!(
        landed, 200_000,
        "the ring starts at {landed} us after the seek"
    );
}

/// The ABI-facing latency setter clamps to 0..=500 ms before the playhead
/// subtracts it.
#[test]
fn audio_latency_setter_clamps_to_a_sane_range() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let px = session.pipeline().clone();

    Session::set_audio_latency(&px, 60_000);
    assert_eq!(
        px.audio_shared.output_latency_us.load(Ordering::Relaxed),
        60_000
    );
    Session::set_audio_latency(&px, -5);
    assert_eq!(px.audio_shared.output_latency_us.load(Ordering::Relaxed), 0);
    Session::set_audio_latency(&px, 10_000_000);
    assert_eq!(
        px.audio_shared.output_latency_us.load(Ordering::Relaxed),
        500_000
    );
    session.close();
}

/// An audio-only clock starts the moment the ring fills, before the host
/// has reported its latency, so a later report moves the running clock by
/// the change. A parked clock is left alone.
#[test]
fn a_latency_report_moves_a_running_clock_by_the_change() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/sine-48k-stereo.flac")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || px
            .clock_playing
            .load(Ordering::Relaxed)),
        "the clock never started"
    );
    // Read at one wall before and after, so the figure is the move alone.
    let reading = |wall| px.clock.lock().expect("clock lock").now(wall);

    let wall = px.wall.now();
    let before = reading(wall);
    Session::set_audio_latency(&px, 100_000);
    let moved = before - reading(wall);
    assert!(
        (moved - MediaTime::from_millis(100)).abs() <= MediaTime::from_millis(1),
        "a 100 ms report moved the clock back {moved}"
    );

    let wall = px.wall.now();
    let before = reading(wall);
    Session::set_audio_latency(&px, 100_000);
    let moved = before - reading(wall);
    assert!(
        moved.abs() <= MediaTime::from_millis(1),
        "an unchanged report moved the clock {moved}"
    );

    let wall = px.wall.now();
    let before = reading(wall);
    Session::set_audio_latency(&px, 40_000);
    let moved = reading(wall) - before;
    assert!(
        (moved - MediaTime::from_millis(60)).abs() <= MediaTime::from_millis(1),
        "a 60 ms smaller report moved the clock forward {moved}"
    );

    session.pause();
    assert!(
        wait_for(Duration::from_secs(5), || !px
            .clock_playing
            .load(Ordering::Relaxed)),
        "the pause never parked the clock"
    );
    let wall = px.wall.now();
    let before = reading(wall);
    Session::set_audio_latency(&px, 200_000);
    let moved = before - reading(wall);
    session.close();
    assert!(
        moved.abs() <= MediaTime::from_millis(1),
        "a report while paused moved the parked clock {moved}"
    );
}

/// A clock started with video is anchored an output latency before the
/// first picture, which is not due until the clock covers that latency.
/// Sound pulled from the start is heard as the picture shows, so the pull
/// serves from the clock start rather than waiting for the present.
#[test]
fn the_pull_serves_from_the_clock_start_before_the_first_present() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    Session::set_audio_latency(&px, 300_000);

    let mut buf = vec![0f32; 2048];
    let mut served_in_buffering = 0usize;
    let reached_playing = wait_for(Duration::from_secs(10), || {
        let state = shared.state.load(Ordering::Relaxed);
        if state == State::Buffering as u32 && px.clock_playing.load(Ordering::Relaxed) {
            served_in_buffering += Session::read_audio(&px, &mut buf);
        }
        state == State::Playing as u32
    });
    session.close();
    assert!(reached_playing, "the session never reached Playing");
    assert!(
        served_in_buffering > 0,
        "nothing was served between the clock start and the first present"
    );
}

/// A pause that lands while the open is still settling is ignored: nothing
/// is playing yet, and the request must not carry forward into the session
/// that follows. The state is read before the liveness flag, so a request
/// that sees Opening returns before either could be misread.
#[test]
fn a_pause_during_opening_is_not_carried_into_the_session() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    // Immediately, before the opener has published Buffering.
    session.pause();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "a pause during opening held the session (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.close();
}

/// A live source is not pausable: the request is ignored and the state
/// stays where it was. The fixture is on-demand, so liveness is forced,
/// the same path RTSP, WHEP and RIST take.
#[test]
fn pause_is_ignored_on_a_live_source() {
    let request = OpenRequest {
        liveness: media_engine::SourceLiveness::Live,
        ..OpenRequest::new(fixture_path())
    };
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    let before = shared.position_us.load(Ordering::Relaxed);
    session.pause();
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Playing as u32,
        "a live session paused"
    );
    assert!(
        wait_for(Duration::from_secs(2), || {
            shared.position_us.load(Ordering::Relaxed) > before + 100_000
        }),
        "position stopped advancing after a pause request on a live source"
    );
    session.close();
}

/// A failed session keeps its Error. Its pipeline threads have stopped, so
/// a seek, play or pause arriving after the failure has nothing to act on,
/// and reporting anything else would hide the reason.
#[test]
fn a_failed_session_keeps_its_error() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/no-such-clip.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Error as u32
        }),
        "a missing file never failed (state {})",
        shared.state.load(Ordering::Relaxed),
    );

    session.seek(MediaTime::from_millis(1000));
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Error as u32,
        "a seek revived the failed session"
    );
    session.play();
    session.pause();
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Error as u32,
        "a play or pause revived the failed session"
    );
    px.set_state(State::Playing);
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Error as u32,
        "a pipeline thread's store replaced the Error"
    );
    session.close();
}

#[test]
fn pause_seek_and_natural_end() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );

    // Pause freezes the position (the pacer clock is credited).
    assert!(wait_for(Duration::from_secs(5), || {
        shared.position_us.load(Ordering::Relaxed) > 200_000
    }));
    session.pause();
    assert_eq!(shared.state.load(Ordering::Relaxed), State::Paused as u32);
    let frozen = shared.position_us.load(Ordering::Relaxed);
    std::thread::sleep(Duration::from_millis(300));
    let still = shared.position_us.load(Ordering::Relaxed);
    assert!(
        (still - frozen).abs() < 40_000,
        "position moved while paused: {frozen} -> {still}"
    );

    // Resume advances again.
    session.play();
    assert!(
        wait_for(Duration::from_secs(2), || {
            shared.position_us.load(Ordering::Relaxed) > still + 100_000
        }),
        "position did not advance after resume"
    );

    // Seek near the end lands keyframe-clean at/before the target and plays
    // through to the natural end.
    session.seek(MediaTime::from_millis(5500));
    assert!(
        wait_for(Duration::from_secs(5), || {
            let position = shared.position_us.load(Ordering::Relaxed);
            (3_900_000..=6_100_000).contains(&position)
                && shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "seek did not settle (position {}, state {})",
        shared.position_us.load(Ordering::Relaxed),
        shared.state.load(Ordering::Relaxed),
    );
    assert!(
        wait_for(Duration::from_secs(8), || {
            shared.state.load(Ordering::Relaxed) == State::Ended as u32
        }),
        "never reached Ended (position {}, state {})",
        shared.position_us.load(Ordering::Relaxed),
        shared.state.load(Ordering::Relaxed),
    );

    session.close();
}

fn open_playing() -> Session {
    let session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > 200_000
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session
}

/// Asserts the session settles Paused on a newly presented frame at the
/// seek target and stays there, then that `play` carries on from it.
fn assert_lands_paused_then_resumes(session: &Session, presented_before: u64) {
    let shared = session.shared().clone();
    let diag = session.diag().clone();
    let presented = || {
        diag.stage(media_diag::Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
    };
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Paused as u32
        }),
        "the seek did not settle paused (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    let landed = shared.position_us.load(Ordering::Relaxed);
    assert!(
        (3_000_000..=4_100_000).contains(&landed),
        "paused away from the seek target: {landed}"
    );
    assert!(
        presented() > presented_before,
        "paused without presenting the landed frame"
    );
    std::thread::sleep(Duration::from_millis(400));
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Paused as u32,
        "the pause did not hold"
    );
    let held = shared.position_us.load(Ordering::Relaxed);
    assert!(
        (held - landed).abs() < 40_000,
        "position moved while paused: {landed} -> {held}"
    );

    session.play();
    assert!(
        wait_for(Duration::from_secs(2), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > held + 100_000
        }),
        "play did not resume from the landed position (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
}

/// A pause straight after a seek waits for the seek: the session lands the
/// target, presents it and holds there. Freezing the wall under the seek
/// would leave a session reporting Playing on a stopped clock that `play`
/// cannot restart.
#[test]
fn a_pause_straight_after_a_seek_lands_the_seek_first() {
    let mut session = open_playing();
    let presented_before = session
        .diag()
        .stage(media_diag::Stage::Present)
        .out_count
        .load(Ordering::Relaxed);
    session.seek(MediaTime::from_millis(4000));
    session.pause();
    assert_lands_paused_then_resumes(&session, presented_before);
    session.close();
}

/// A seek on a paused session shows the new position and stays paused.
#[test]
fn a_seek_while_paused_stays_paused() {
    let mut session = open_playing();
    session.pause();
    assert_eq!(
        session.shared().state.load(Ordering::Relaxed),
        State::Paused as u32
    );
    let presented_before = session
        .diag()
        .stage(media_diag::Stage::Present)
        .out_count
        .load(Ordering::Relaxed);
    session.seek(MediaTime::from_millis(4000));
    assert_lands_paused_then_resumes(&session, presented_before);
    session.close();
}

/// The fixture's keyframes are at 0, 2 and 4 s, so a seek to 3.2 s lands
/// the demuxer on 2 s. The session must present the target's frame, not
/// the keyframe's: paused, nothing would ever move it off the wrong one.
#[test]
fn a_paused_seek_between_keyframes_shows_the_target() {
    const TARGET_US: i64 = 3_200_000;
    // One frame of the 30 fps fixture, and a little for rounding.
    const FRAME_US: i64 = 34_000;
    let mut session = open_playing();
    session.pause();
    let shared = session.shared().clone();
    let diag = session.diag().clone();
    let presented_before = diag
        .stage(media_diag::Stage::Present)
        .out_count
        .load(Ordering::Relaxed);
    session.seek(MediaTime::from_micros(TARGET_US));
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Paused as u32
                && diag
                    .stage(media_diag::Stage::Present)
                    .out_count
                    .load(Ordering::Relaxed)
                    > presented_before
        }),
        "the seek did not settle paused on a new frame (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    // The frame on screen is read separately: position is the clock's, and
    // a parked clock sits on the target whatever was shown.
    let shown = session.pipeline().presented_pts_us.load(Ordering::Relaxed);
    assert!(
        (TARGET_US..=TARGET_US + FRAME_US).contains(&shown),
        "paused on {shown}, not on the frame at {TARGET_US}"
    );
    session.close();
}

/// Position is the clock's, not the picture's, so it moves between frames.
/// Read from presented frames it could only take a frame's pts (about
/// thirty values a second here), and would stand still whenever the
/// picture did, stalling captions, user data and shared playback.
#[test]
fn position_is_the_clocks_and_moves_between_frames() {
    let mut session = open_playing();
    let shared = session.shared().clone();
    let mut seen = std::collections::BTreeSet::new();
    let started = Instant::now();
    while started.elapsed() < Duration::from_secs(1) {
        seen.insert(shared.position_us.load(Ordering::Relaxed));
        std::thread::sleep(Duration::from_millis(1));
    }
    assert!(
        seen.len() > 60,
        "position took {} values in a second of 30 fps video",
        seen.len()
    );
    session.close();
}

/// Playing, the same seek never reports or shows anything from the span
/// between the keyframe and the target: the position goes to the target
/// when the seek runs and moves on from there.
#[test]
fn a_seek_between_keyframes_plays_on_from_the_target() {
    const TARGET_US: i64 = 3_200_000;
    let mut session = open_playing();
    let shared = session.shared().clone();
    session.seek(MediaTime::from_micros(TARGET_US));
    // The session was a fraction of a second in; the seek moves the
    // position to at least the keyframe at 2 s when it runs.
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.position_us.load(Ordering::Relaxed) >= 1_900_000
        }),
        "the seek never ran"
    );
    let mut earliest = i64::MAX;
    let resumed = wait_for(Duration::from_secs(5), || {
        let position = shared.position_us.load(Ordering::Relaxed);
        earliest = earliest.min(position);
        shared.state.load(Ordering::Relaxed) == State::Playing as u32
            && position > TARGET_US + 200_000
    });
    assert!(
        resumed,
        "playback did not carry on past the target (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    assert!(
        earliest >= TARGET_US,
        "the new timeline showed {earliest}, ahead of the target {TARGET_US}"
    );
    // Audio starts on the same line: the ring's first sample is the
    // target's, to within a sample of the 48 kHz fixture.
    let first_heard = session
        .pipeline()
        .audio_shared
        .base_pts_us
        .load(Ordering::Relaxed);
    assert!(
        (TARGET_US..=TARGET_US + 50).contains(&first_heard),
        "the new timeline's audio starts at {first_heard}, not at the target {TARGET_US}"
    );
    session.close();
}

/// A seek 15 s past the only keyframe of a 4 fps file decodes 60 frames
/// forward and plays on from the target, rather than from the keyframe.
#[test]
fn a_seek_far_past_a_low_rate_keyframe_plays_on_from_the_target() {
    const TARGET_US: i64 = 15_000_000;
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-4fps-longgop.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > 600_000
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.seek(MediaTime::from_micros(TARGET_US));
    // The picture, not only the audio-led position, reaches the target.
    let px = session.pipeline().clone();
    let shown = wait_for(Duration::from_secs(5), || {
        px.presented_pts_us.load(Ordering::Relaxed) >= TARGET_US
    });
    assert!(
        shown,
        "the picture never reached the target (presented {})",
        px.presented_pts_us.load(Ordering::Relaxed),
    );
    let resumed = wait_for(Duration::from_secs(5), || {
        shared.state.load(Ordering::Relaxed) == State::Playing as u32
            && shared.position_us.load(Ordering::Relaxed) > TARGET_US + 200_000
    });
    assert!(
        resumed,
        "playback did not carry on past the target (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    session.close();
}

/// The same on an audio-only session, where nothing presents: the ring
/// standing ready at the landed position is what completes the pause.
#[test]
fn a_seek_while_paused_stays_paused_on_audio_only() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/aac-48k-stereo.m4a")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.pause();
    assert_eq!(shared.state.load(Ordering::Relaxed), State::Paused as u32);

    session.seek(MediaTime::from_millis(1500));
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Paused as u32
                && shared.position_us.load(Ordering::Relaxed) > 1_000_000
        }),
        "the seek did not settle paused (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    let landed = shared.position_us.load(Ordering::Relaxed);
    std::thread::sleep(Duration::from_millis(400));
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Paused as u32,
        "the pause did not hold"
    );
    let held = shared.position_us.load(Ordering::Relaxed);
    assert!(
        (held - landed).abs() < 40_000,
        "position moved while paused: {landed} -> {held}"
    );

    session.play();
    assert!(
        wait_for(Duration::from_secs(2), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > held + 100_000
        }),
        "play did not resume from the landed position (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    session.close();
}

/// A seek after Ended revives the pipeline: the generation advance
/// rebuilds decode state and presentation resumes on the new timeline.
/// Runs on progressive MP4 and on HLS-TS on-demand, whose demuxer latches
/// an internal end state the seek must clear.
#[test]
fn seek_after_ended_revives_the_session() {
    for lane in [
        fixture_path(),
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures/hls/ts/index.m3u8")
            .to_string_lossy()
            .into_owned(),
    ] {
        let mut session = Session::open(OpenRequest::new(lane.clone()));
        let shared = session.shared().clone();
        let diag = session.diag().clone();

        assert!(
            wait_for(Duration::from_secs(10), || {
                shared.state.load(Ordering::Relaxed) == State::Playing as u32
            }),
            "{lane}: never reached Playing"
        );
        // Jump near the end and let it finish.
        let origin = shared.position_us.load(Ordering::Relaxed);
        session.seek(MediaTime::from_micros(origin + 5_300_000));
        assert!(
            wait_for(Duration::from_secs(10), || {
                shared.state.load(Ordering::Relaxed) == State::Ended as u32
            }),
            "{lane}: never reached Ended (state {})",
            shared.state.load(Ordering::Relaxed)
        );

        let presented_at_end = diag.snapshot()[media_diag::Stage::Present as usize].out_count;
        session.seek(MediaTime::from_micros(origin + 1_000_000));
        assert!(
            wait_for(Duration::from_secs(10), || {
                shared.state.load(Ordering::Relaxed) == State::Playing as u32
                    && diag.snapshot()[media_diag::Stage::Present as usize].out_count
                        > presented_at_end
            }),
            "{lane}: seek after Ended did not revive (state {})",
            shared.state.load(Ordering::Relaxed)
        );
        // And it ends cleanly a second time.
        assert!(
            wait_for(Duration::from_secs(15), || {
                shared.state.load(Ordering::Relaxed) == State::Ended as u32
            }),
            "{lane}: revived session never ended again"
        );
        session.close();
    }
}

/// A seek issued while the video thread is inside its EOS drain (the whole
/// fixture released, banked at zero, the pool still presenting the tail).
/// The demux thread parks the clock and advances the generation, but until
/// the video thread processes the Flush, stale pre-seek frames sit in the
/// pool, and restarting the parked clock from one would resume the old
/// timeline and end in a backwards snap to the audio master. On Quest the
/// OMX drain stretches this window to seconds. The clock must stay parked
/// until the new generation's first frame: the settle then has no master
/// snap and the tail plays out at 1x.
#[test]
fn seek_during_eos_drain_settles_without_a_snap() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let diag = session.diag().clone();

    // Pull audio like the Unity thread (budget at the stream rate) so the
    // audio master is live, since the failure shows through it.
    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let mut pump_until = |deadline: Duration, pred: &mut dyn FnMut() -> bool| -> bool {
        let end = Instant::now() + deadline;
        while Instant::now() < end {
            let state = shared.state.load(Ordering::Relaxed);
            assert_ne!(
                state,
                State::Error as u32,
                "error {}",
                shared.last_error.load(Ordering::Relaxed)
            );
            if state != State::Playing as u32 {
                // Budget restarts across settles so the catch-up after a
                // parked-clock window cannot race the playhead.
                epoch = None;
                pulled = 0;
            } else {
                let rate = shared.audio_rate.load(Ordering::Relaxed);
                let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
                if rate > 0 {
                    let at = *epoch.get_or_insert_with(Instant::now);
                    let budget =
                        at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - pulled;
                    if budget as usize >= buf.len() / channels as usize {
                        pulled += Session::read_audio(&px, &mut buf) as u64;
                    }
                }
            }
            if pred() {
                return true;
            }
            std::thread::sleep(Duration::from_millis(2));
        }
        false
    };

    assert!(
        pump_until(Duration::from_secs(10), &mut || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing"
    );
    // The whole 6 s fixture is released well before it finishes playing
    // (read-ahead plus startup burst): banked zero with the position
    // mid-file means the Eos is through and the drain tail is presenting.
    assert!(
        pump_until(Duration::from_secs(10), &mut || {
            shared.banked_us.load(Ordering::Relaxed) == 0
                && shared.position_us.load(Ordering::Relaxed) > 3_500_000
        }),
        "drain-tail window never reached (banked {}, position {})",
        shared.banked_us.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );

    let _ = diag.take_events();
    session.seek(MediaTime::from_millis(2_500));
    assert!(
        pump_until(Duration::from_secs(3), &mut || {
            let position = shared.position_us.load(Ordering::Relaxed);
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && (1_800_000..=3_200_000).contains(&position)
        }),
        "seek did not settle (position {}, state {})",
        shared.position_us.load(Ordering::Relaxed),
        shared.state.load(Ordering::Relaxed),
    );
    let settled = Instant::now();
    assert!(
        pump_until(Duration::from_secs(10), &mut || {
            shared.state.load(Ordering::Relaxed) == State::Ended as u32
        }),
        "never reached Ended after the seek"
    );
    // ~4 s of tail from the 2 s keyframe: played at 1x, not rushed out
    // against a stale clock.
    assert!(
        settled.elapsed() >= Duration::from_secs(3),
        "tail rushed: Ended {}ms after settle",
        settled.elapsed().as_millis()
    );
    let snaps: Vec<_> = diag
        .take_events()
        .into_iter()
        .filter(|e| e.code == media_diag::EventCode::SnapCorrection)
        .collect();
    assert!(
        snaps.is_empty(),
        "master snap during the post-seek settle: {:?}",
        snaps.iter().map(|e| e.detail.as_str()).collect::<Vec<_>>()
    );
    session.close();
}

/// In-band CEA-608: the caption fixture's scripted cue sequence comes
/// through the caption path, with text (including special and extended
/// characters and the two-row roll-up), clears, and 2 s spacing keyed to
/// the video PTS. The script is tools/gen-caption-fixture.py's.
#[test]
fn caption_lane_delivers_the_scripted_cues() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-608-640x360-30fps.ts")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut cues = Vec::new();
    assert!(
        wait_for(Duration::from_secs(20), || {
            cues.extend(Session::drain_captions(&px, 16));
            let state = shared.state.load(Ordering::Relaxed);
            assert_ne!(
                state,
                State::Error as u32,
                "error {}",
                shared.last_error.load(Ordering::Relaxed)
            );
            state == State::Ended as u32
        }),
        "caption session never ended"
    );
    cues.extend(Session::drain_captions(&px, 16));

    let texts: Vec<&str> = cues.iter().map(|c| c.text.as_str()).collect();
    assert_eq!(
        texts,
        vec![
            "HELLO WORLD",
            "CAFÉ MAÑANA",
            "",
            "ROLL UP",
            "ROLL UP\nSECOND",
            "",
        ],
        "cue sequence mismatch"
    );
    // The script spaces cues on the video timeline: 0/2/4/6/7/8 s from the
    // first frame, whatever base the mux added.
    let origin = cues[0].pts_us;
    let offsets: Vec<i64> = cues.iter().map(|c| (c.pts_us - origin) / 1000).collect();
    assert_eq!(offsets, vec![0, 2000, 4000, 6000, 7000, 8000]);
    session.close();
}

/// A long decode-forward must not cost audio. The Bank starts releasing at
/// its first push, the audio ring holds two seconds, and nothing pulls from
/// it until the seek lands. Pushed while the decoder is still working
/// through the span, the Bank would release for as long as that takes and
/// the overflow would be discarded, heard as a skip two seconds after Play.
/// Paused is the hardest case, since nothing pulls afterwards either.
///
/// Needs a span long enough to take real time to decode, which no checked-in
/// fixture has. Set `BASIS_MEDIA_TEST_SPARSE_KEYFRAMES_URL` to an H.264 MP4
/// over HTTPS whose last keyframe before 17 s is several seconds earlier,
/// and run with `-- --ignored`.
#[test]
#[ignore = "needs BASIS_MEDIA_TEST_SPARSE_KEYFRAMES_URL: an H.264 MP4 with a keyframe several seconds before 17 s"]
fn a_long_decode_forward_discards_no_audio() {
    let url = std::env::var("BASIS_MEDIA_TEST_SPARSE_KEYFRAMES_URL").expect(
        "set BASIS_MEDIA_TEST_SPARSE_KEYFRAMES_URL to an H.264 MP4 with a keyframe several seconds before 17 s",
    );
    let mut session = Session::open(OpenRequest::new(url));
    let shared = session.shared().clone();
    let diag = session.diag().clone();
    let ring_drops = || {
        diag.stage(media_diag::Stage::AudioRing)
            .drops
            .load(Ordering::Relaxed)
    };
    let presented = || {
        diag.stage(media_diag::Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
    };
    assert!(
        wait_for(Duration::from_secs(20), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > 200_000
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.pause();
    // Paused, the release schedule is frozen, so whatever the non-pulling
    // consumer has cost the ring has stopped growing.
    std::thread::sleep(Duration::from_millis(800));
    let drops_before = ring_drops();
    let presented_before = presented();

    session.seek(MediaTime::from_millis(17_000));
    assert!(
        wait_for(Duration::from_secs(20), || {
            shared.state.load(Ordering::Relaxed) == State::Paused as u32
                && presented() > presented_before
        }),
        "the seek did not settle paused (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    // Past the liveness window, so anything stuck against the ring has
    // been given up on by now.
    std::thread::sleep(Duration::from_millis(1500));
    let lost = ring_drops() - drops_before;
    session.close();
    assert_eq!(lost, 0, "the landing discarded {lost} chunks of audio");
}

/// A caption that went up ahead of a seek's target and is still up at it
/// is on screen after the seek. The caption decoder is stateful, so the
/// span the seek decodes unseen is scanned too, its cues held back, and
/// the last one published at the target. The fixture's keyframes are
/// 2 s apart and its roll-up caption gains its second row at 7 s, so a
/// seek to 7.5 s lands the demuxer on 6 s with that row still to come.
#[test]
fn a_caption_already_up_at_a_seeks_target_is_shown() {
    const TARGET_US: i64 = 7_500_000;
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-608-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.seek(MediaTime::from_micros(TARGET_US));
    // Settled first: the seek empties the caption ring part-way through,
    // and everything in it after that belongs to the new timeline.
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > TARGET_US + 100_000
        }),
        "playback did not carry on past the target (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    let cues = Session::drain_captions(&px, 16);
    session.close();
    let up: Vec<_> = cues.iter().filter(|c| !c.text.is_empty()).collect();
    assert!(
        up.first()
            .is_some_and(|c| c.pts_us == TARGET_US && c.text.ends_with("SECOND")),
        "the caption up at the target was not published there: {cues:?}"
    );
}

/// The caption already up goes out ahead of anything the access unit at the
/// target carries. The fixture's second roll-up row arrives on the frame at
/// 7 s exactly, so a seek to 7 s has both: "ROLL UP" held over from the
/// span decoded unseen, and the two-row text from the target's own frame.
/// In the other order the display ends on the text that was replaced.
#[test]
fn a_caption_held_over_precedes_the_targets_own_cue() {
    const TARGET_US: i64 = 7_000_000;
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-608-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.seek(MediaTime::from_micros(TARGET_US));
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > TARGET_US + 100_000
        }),
        "playback did not carry on past the target (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    let cues = Session::drain_captions(&px, 16);
    session.close();
    let up: Vec<(i64, &str)> = cues
        .iter()
        .filter(|c| !c.text.is_empty())
        .map(|c| (c.pts_us, c.text.as_str()))
        .collect();
    assert_eq!(
        up,
        vec![(TARGET_US, "ROLL UP"), (TARGET_US, "ROLL UP\nSECOND")],
        "what was up comes first, then what the target's frame brings"
    );
}

/// A target past the last picture has no frame at or after it. Every frame
/// the seek decodes is ahead of the floor, so none would reach the pool,
/// the parked clock would never start and the session would sit in
/// Buffering. It lands on the last frame instead and ends from there. The
/// fixture's last frame is at 5.967 s of 6 s.
#[test]
fn a_seek_past_the_last_frame_lands_on_it_and_ends() {
    let mut session = open_playing();
    let shared = session.shared().clone();
    let diag = session.diag().clone();
    let presented_before = diag
        .stage(media_diag::Stage::Present)
        .out_count
        .load(Ordering::Relaxed);
    session.seek(MediaTime::from_millis(5_990));
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Ended as u32
        }),
        "the session never ended (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    assert!(
        diag.stage(media_diag::Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
            > presented_before,
        "it ended without showing the last frame"
    );
    // The frame on screen is read separately: position is the clock's, and
    // the clock runs on to the end of the audio before Ended is reported.
    let shown = session.pipeline().presented_pts_us.load(Ordering::Relaxed);
    assert!(
        (5_900_000..6_000_000).contains(&shown),
        "it landed on {shown}, not on the last frame"
    );
    let position = shared.position_us.load(Ordering::Relaxed);
    let duration = shared.duration_us.load(Ordering::Relaxed);
    assert!(
        position >= 5_900_000 && position <= duration,
        "position {position} at Ended is not within the media ({duration})"
    );
    session.close();
}

/// With no audio to play out, the end waits for the last frame's own time on
/// screen, and position stops at the duration. The fixture is 5 s at 4 fps, so
/// its last frame starts at 4.75 s and ends at 5 s.
#[test]
fn a_video_only_session_ends_at_its_duration() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-4fps-320x180.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.seek(MediaTime::from_millis(4_000));
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Ended as u32
        }),
        "the session never ended (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    assert_eq!(shared.duration_us.load(Ordering::Relaxed), 5_000_000);
    assert_eq!(
        shared.position_us.load(Ordering::Relaxed),
        5_000_000,
        "the session did not end at its last frame's end"
    );
    session.close();
}

/// A file trimmed by its edit lists to 3.9 s of six: the picture stops at
/// the last frame shown before then (3.867 s, the 117th), the sound stops at
/// exactly 3.9 s of samples, and position at the end. The P-frame at 3.9 s
/// is decoded, since the frame before it depends on it, but never shown.
#[test]
fn a_trimmed_file_plays_only_what_its_edit_list_presents() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-edit-trimmed.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let mut latest_shown = i64::MIN;
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(30) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        latest_shown = latest_shown.max(px.presented_pts_us.load(Ordering::Relaxed));
        if state == State::Ended as u32 {
            break;
        }
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
    }

    assert_eq!(shared.state.load(Ordering::Relaxed), State::Ended as u32);
    assert_eq!(latest_shown, 3_866_666, "the last picture shown");
    // Where the last sound handed to the consumer ends, within a frame at
    // 48 kHz. A starved consumer lets the ring discard a chunk it held too
    // long, so the exact end is only asserted where nothing was discarded.
    let sound_end = px.audio_shared.playhead_pts_us.load(Ordering::Relaxed);
    assert!(sound_end <= 3_900_021, "sound played on to {sound_end}");
    let ring_drops = session
        .diag()
        .stage(media_diag::Stage::AudioRing)
        .drops
        .load(Ordering::Relaxed);
    if ring_drops == 0 {
        assert!(sound_end >= 3_899_979, "sound stopped at {sound_end}");
    }
    assert_eq!(shared.duration_us.load(Ordering::Relaxed), 3_900_000);
    assert_eq!(shared.position_us.load(Ordering::Relaxed), 3_900_000);
    session.close();
}

/// A seek into the tail an edit list trims lands on the last picture shown
/// before the end, not on the decoded one past it, and ends from there.
#[test]
fn a_seek_into_a_trimmed_tail_lands_on_the_last_picture_shown() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-edit-trimmed.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    session.seek(MediaTime::from_millis(3_950));
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Ended as u32
        }),
        "the session never ended (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    assert_eq!(
        session.pipeline().presented_pts_us.load(Ordering::Relaxed),
        3_866_666
    );
    assert!(shared.position_us.load(Ordering::Relaxed) <= 3_900_000);
    session.close();
}

/// Two seeks queued while the demux thread is busy land on the second: the
/// first is superseded, not run after it.
#[test]
fn a_seek_queued_behind_another_supersedes_it() {
    let mut session = open_playing();
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    {
        // Holding the Bank stalls the demux thread in its pump, so both
        // seeks are queued before it looks for one.
        let _bank = px.bank.bank.lock().expect("bank lock");
        std::thread::sleep(Duration::from_millis(300));
        session.seek(MediaTime::from_millis(3_000));
        session.seek(MediaTime::from_millis(1_000));
    }
    assert!(
        wait_for(Duration::from_secs(10), || {
            px.seeks_pending.load(Ordering::Acquire) == 0
                && shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && px.presented_pts_us.load(Ordering::Relaxed) != i64::MIN
        }),
        "the seeks never settled (state {}, pending {})",
        shared.state.load(Ordering::Relaxed),
        px.seeks_pending.load(Ordering::Acquire),
    );
    let shown = px.presented_pts_us.load(Ordering::Relaxed);
    assert!(
        (1_000_000..2_000_000).contains(&shown),
        "it landed on {shown}, not on the second seek's target"
    );
    session.close();
}

/// `diag_csv` writes the capture-recorder CSV on close: a header row per
/// the pinned column contract plus at least one 100 ms sample per second
/// of playback.
#[test]
fn diag_csv_written_on_close() {
    let dir = std::env::temp_dir().join(format!("bm-diag-test-{}", std::process::id()));
    std::fs::create_dir_all(&dir).expect("temp dir");
    let path = dir.join("capture.csv");

    let mut request = OpenRequest::new(fixture_path());
    request.diag_csv = Some(path.clone());
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing"
    );
    std::thread::sleep(Duration::from_secs(1));
    session.close();

    let csv = std::fs::read_to_string(&path).expect("csv written on close");
    let mut lines = csv.lines();
    assert_eq!(
        lines.next().map(|h| h.to_owned()),
        Some(media_diag::CaptureRecorder::header()),
        "header is the pinned column contract"
    );
    assert!(lines.count() >= 10, "expected >=10 samples over >=1s");
    let _ = std::fs::remove_dir_all(&dir);
}

/// The sync ladder over a playing A/V session (audio master): a target
/// inside the dead band asks for nothing, a target ahead engages the +2%
/// slew (surfaced for the managed audio pull), and a target past the seek
/// threshold seeks.
#[test]
fn sync_target_ladder_slew_then_seek() {
    let mut session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut buf = vec![0f32; 2048];
    let mut pulled = 0u64;
    let mut epoch: Option<Instant> = None;
    let mut pull = |px: &std::sync::Arc<media_engine::PipelineShared>| {
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        if rate > 0 && shared.state.load(Ordering::Relaxed) == State::Playing as u32 {
            let at = *epoch.get_or_insert_with(Instant::now);
            let budget = at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000 - pulled;
            if budget as usize >= buf.len() / channels as usize {
                pulled += Session::read_audio(px, &mut buf) as u64;
            }
        }
    };

    assert!(
        wait_for(Duration::from_secs(10), || {
            pull(&px);
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > 200_000
        }),
        "never started playing"
    );

    // Dead band: the current position is (near) the target, so no action.
    let position = shared.position_us.load(Ordering::Relaxed);
    Session::set_sync_target(&px, position);
    assert_eq!(px.sync_rate_ppm.load(Ordering::Relaxed), 0);

    // Slew band: 1 s ahead wants +2%, and the wanted rate is visible to
    // the audio consumer contract.
    let position = shared.position_us.load(Ordering::Relaxed);
    Session::set_sync_target(&px, position + 1_000_000);
    assert_eq!(px.sync_rate_ppm.load(Ordering::Relaxed), 20_000);

    // Behind by 1 s wants -2%. Needs a second of track behind the playhead
    // first, since a negative target is the clear sentinel.
    assert!(
        wait_for(Duration::from_secs(5), || {
            pull(&px);
            shared.position_us.load(Ordering::Relaxed) > 1_300_000
        }),
        "position never reached 1.3 s"
    );
    let position = shared.position_us.load(Ordering::Relaxed);
    Session::set_sync_target(&px, position - 1_000_000);
    assert_eq!(px.sync_rate_ppm.load(Ordering::Relaxed), -20_000);

    // Clearing releases the correction.
    Session::set_sync_target(&px, -1);
    assert_eq!(px.sync_rate_ppm.load(Ordering::Relaxed), 0);

    // Seek rung: 3 s ahead is past the threshold; the session re-buffers
    // and settles near the target on the fixture's keyframe grid.
    let position = shared.position_us.load(Ordering::Relaxed);
    let target = position + 3_000_000;
    Session::set_sync_target(&px, target);
    assert_eq!(
        px.sync_rate_ppm.load(Ordering::Relaxed),
        0,
        "seek clears the slew"
    );
    assert!(
        wait_for(Duration::from_secs(10), || {
            pull(&px);
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && (shared.position_us.load(Ordering::Relaxed) - target).abs() < 2_500_000
        }),
        "never settled near the sync-seek target (position {} vs target {})",
        shared.position_us.load(Ordering::Relaxed),
        target
    );
    session.close();
}

/// With a wall master (no audio track) the sync slew goes to the clock
/// directly: there is no audio consumer to apply it, so the correction is
/// engine-side and `sync_rate_ppm` mirrors it.
#[test]
fn sync_target_slews_the_wall_clock_on_video_only() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > 200_000
        }),
        "never started playing"
    );

    let position = shared.position_us.load(Ordering::Relaxed);
    Session::set_sync_target(&px, position + 1_000_000);
    assert_eq!(px.sync_rate_ppm.load(Ordering::Relaxed), 20_000);
    assert_eq!(
        px.clock.lock().unwrap().rate_ppm(),
        20_000,
        "wall-master slew is applied to the clock itself"
    );

    Session::set_sync_target(&px, -1);
    assert_eq!(px.clock.lock().unwrap().rate_ppm(), 0);
    session.close();
}

/// `diag_csv_append` keeps every session's capture in one file instead of the
/// last one only, as a player that goes dormant and wakes needs. The header
/// belongs to the file, not to each capture, so a second run adds rows and
/// nothing else: a header row in the middle would read as data.
#[test]
fn diag_csv_appends_without_a_second_header() {
    let dir = std::env::temp_dir().join(format!("bm-diag-append-{}", std::process::id()));
    std::fs::create_dir_all(&dir).expect("temp dir");
    let path = dir.join("capture.csv");
    let _ = std::fs::remove_file(&path);

    let run = || {
        let mut request = OpenRequest::new(fixture_path());
        request.diag_csv = Some(path.clone());
        request.diag_csv_append = true;
        let mut session = Session::open(request);
        let shared = session.shared().clone();
        assert!(
            wait_for(Duration::from_secs(10), || {
                shared.state.load(Ordering::Relaxed) == State::Playing as u32
            }),
            "never reached Playing"
        );
        std::thread::sleep(Duration::from_millis(600));
        session.close();
        std::fs::read_to_string(&path).expect("csv written on close")
    };

    let first = run();
    let first_lines = first.lines().count();
    assert!(first_lines > 1, "first run wrote no rows");

    let second = run();
    let header = media_diag::CaptureRecorder::header();
    assert_eq!(
        second.lines().filter(|l| *l == header).count(),
        1,
        "the header belongs to the file, once"
    );
    assert!(
        second.lines().count() > first_lines,
        "the second run replaced the first instead of appending"
    );
    // The first run's rows survive verbatim.
    assert!(second.starts_with(&first), "earlier rows were rewritten");

    let _ = std::fs::remove_file(&path);
}

/// SEI user data: the fixture stamps one type-5 message into every access
/// unit (tools/gen-sei-userdata-fixture.py's layout), and each is handed
/// over with its UUID split off and its PTS, in order and without loss.
/// x264's own build-string message comes through on the first AU under its
/// own UUID, which is why the consumer filters on UUID.
#[test]
fn user_data_lane_delivers_every_frames_message() {
    const FIXTURE_UUID: [u8; 16] = [
        0x7a, 0x1c, 0x3e, 0x5f, 0x9b, 0x2d, 0x4c, 0x6e, 0x8f, 0x0a, 0x1b, 0x2c, 0x3d, 0x4e, 0x5f,
        0x60,
    ];
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-sei-userdata-640x360-30fps.ts")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut messages = Vec::new();
    assert!(
        wait_for(Duration::from_secs(20), || {
            messages.extend(Session::drain_user_data(&px, 64, 1 << 20));
            let state = shared.state.load(Ordering::Relaxed);
            assert_ne!(
                state,
                State::Error as u32,
                "error {}",
                shared.last_error.load(Ordering::Relaxed)
            );
            state == State::Ended as u32
        }),
        "user data session never ended"
    );
    messages.extend(Session::drain_user_data(&px, 64, 1 << 20));
    session.close();

    let ours: Vec<_> = messages.iter().filter(|m| m.uuid == FIXTURE_UUID).collect();
    assert_eq!(ours.len(), 180, "one message per AU, none dropped");
    for (i, m) in ours.iter().enumerate() {
        assert_eq!(&m.payload[..4], b"BMUD");
        let frame = u32::from_be_bytes(m.payload[4..8].try_into().unwrap());
        assert_eq!(frame as usize, i, "decode order preserved");
        assert_eq!(m.payload.len(), 8 + 512);
        assert!(
            m.payload[8..]
                .iter()
                .enumerate()
                .all(|(k, &b)| b == ((i + k) & 0xFF) as u8),
            "filler intact on frame {i}"
        );
        if i > 0 {
            let step = m.pts_us - ours[i - 1].pts_us;
            assert!(
                (33_000..=34_000).contains(&step),
                "pts step {step} on frame {i}"
            );
        }
    }
    let foreign: Vec<_> = messages.iter().filter(|m| m.uuid != FIXTURE_UUID).collect();
    assert!(
        foreign.iter().any(|m| m.payload.starts_with(b"x264")),
        "x264's own user data passes through under its UUID"
    );
    assert_eq!(foreign[0].pts_us, ours[0].pts_us);
}

/// DMX lighting data stamped into the video by Truss
/// (https://github.com/towneh/Truss) comes out of the engine as Truss
/// wrote it: every record, in order, its CRC intact once the SEI's
/// emulation-prevention bytes are removed, and its DMXS payload decoding to
/// the values tools/gen-truss-dmx-fixture.py wrote, including universe 4
/// joining at frame 30.
#[test]
fn truss_dmx_records_arrive_whole_and_decode() {
    const TRUSS_UUID: [u8; 16] = [
        0xb1, 0xf0, 0xa7, 0xd4, 0x9c, 0x3e, 0x4a, 0x52, 0x8f, 0x61, 0x2d, 0x7c, 0x5e, 0x0b, 0x93,
        0xa8,
    ];
    fn crc32(bytes: &[u8]) -> u32 {
        let mut crc = !0u32;
        for &b in bytes {
            crc ^= u32::from(b);
            for _ in 0..8 {
                crc = if crc & 1 != 0 {
                    (crc >> 1) ^ 0xEDB8_8320
                } else {
                    crc >> 1
                };
            }
        }
        !crc
    }
    let be16 = |b: &[u8], at: usize| u16::from_be_bytes([b[at], b[at + 1]]) as usize;
    let be32 = |b: &[u8], at: usize| u32::from_be_bytes(b[at..at + 4].try_into().unwrap());

    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-truss-dmx-320x180-30fps.ts")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();

    let mut messages = Vec::new();
    assert!(
        wait_for(Duration::from_secs(20), || {
            messages.extend(Session::drain_user_data(&px, 64, 1 << 20));
            let state = shared.state.load(Ordering::Relaxed);
            assert_ne!(
                state,
                State::Error as u32,
                "error {}",
                shared.last_error.load(Ordering::Relaxed)
            );
            state == State::Ended as u32
        }),
        "Truss session never ended"
    );
    messages.extend(Session::drain_user_data(&px, 64, 1 << 20));
    session.close();

    let records: Vec<_> = messages.iter().filter(|m| m.uuid == TRUSS_UUID).collect();
    assert_eq!(
        records.len(),
        60,
        "one record per access unit, none dropped"
    );
    for (i, m) in records.iter().enumerate() {
        let r = &m.payload[..];
        assert_eq!(&r[..8], b"TRUSSDMX", "record {i} magic");
        assert_eq!((r[8], r[9]), (1, 1), "record {i} version and carrier");
        assert_eq!(be32(r, 10) as usize, i, "record {i} seq");
        assert_eq!(be32(r, 22) as usize, i, "record {i} frame index");
        let len = be16(r, 26);
        assert_eq!(r.len(), 28 + len + 4, "record {i} length");
        assert_eq!(be32(r, 28 + len), crc32(&r[..28 + len]), "record {i} CRC");

        let p = &r[28..28 + len];
        assert_eq!(&p[..4], b"DMXS", "record {i} payload magic");
        assert_eq!(
            (p[4], p[5]),
            (1, 0x01),
            "record {i} payload version and flags"
        );
        let universes: Vec<usize> = if i >= 30 {
            (0..5).collect()
        } else {
            (0..4).collect()
        };
        assert_eq!(be16(p, 6), universes.len(), "record {i} block count");
        let mut at = 8;
        for &u in &universes {
            assert_eq!(be16(p, at), u, "record {i} universe");
            assert_eq!(be16(p, at + 2), 0, "record {i} start slot");
            assert_eq!(be16(p, at + 4), 512, "record {i} universe {u} length");
            let values = &p[at + 10..at + 10 + 512];
            for (s, &v) in values.iter().enumerate() {
                assert_eq!(
                    usize::from(v),
                    (u * 520 + s + i) % 251,
                    "record {i} universe {u} slot {s}"
                );
            }
            at += 10 + 512;
        }
        assert_eq!(at, len, "record {i} has nothing past its blocks");
        if i > 0 {
            let step = m.pts_us - records[i - 1].pts_us;
            assert!(
                (33_000..=34_000).contains(&step),
                "pts step {step} on record {i}"
            );
        }
    }
}

/// A seek that decodes forward to its target delivers no user data from
/// the span it skipped: those frames are never shown, and a consumer
/// driving lights off them would replay a second of cues in an instant.
#[test]
fn a_seek_delivers_no_user_data_from_ahead_of_its_target() {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-sei-userdata-640x360-30fps.mp4")
        .to_string_lossy()
        .into_owned();
    let mut session = Session::open(OpenRequest::new(path));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "never reached Playing (state {}, error {})",
        shared.state.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed),
    );
    // The fixture's keyframes are at 0, 2 and 4 s, so this lands the
    // demuxer more than a second short of the target.
    let target_us = 3_200_000;
    session.seek(MediaTime::from_micros(target_us));
    // Settled first, drained after. The seek empties the ring part-way
    // through, so an earlier drain can pick up the old timeline's messages.
    // Once playback has passed the target, everything in the ring was
    // scanned after the clear, and the span the seek covers is a fraction
    // of what the ring holds.
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
                && shared.position_us.load(Ordering::Relaxed) > target_us + 200_000
        }),
        "playback did not carry on past the target (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );
    let messages = Session::drain_user_data(&px, 1024, 1 << 24);
    session.close();
    assert!(!messages.is_empty(), "the new timeline delivered nothing");
    let earliest = messages.iter().map(|m| m.pts_us).min().expect("non-empty");
    assert!(
        earliest >= target_us,
        "user data from {earliest} was delivered for a seek to {target_us}"
    );
}

/// A seek clears the A/V offset and only re-arms it from the new timeline.
///
/// **This does not cover the race it appears to.** The offset is computed
/// on the audio thread and the presentation gate is per generation, so a
/// stale video position must never pair with a new playhead. This test
/// cannot force the interleaving that would expose that: the video thread
/// reaches its Flush fast enough that the sentinel appears anyway, and
/// holding it back needs a seam in `run_video` that does not exist. The
/// gate itself is covered by the unit tests
/// `the_gate_answers_only_for_the_timeline_in_force` and
/// `a_new_generation_starts_unarmed_and_arms_itself`, and the gap is
/// recorded in TESTING.md.
///
/// What it does check end to end: a seek clears the offset, and it comes
/// back only once the new timeline has presented. Both halves assert they
/// were reached, so neither can pass by never arriving.
#[test]
fn a_seek_clears_the_offset_and_re_arms_on_the_new_timeline() {
    let session = Session::open(OpenRequest::new(fixture_path()));
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let mut buf = vec![0f32; 2048];
    let pull = |buf: &mut [f32]| {
        if shared.audio_rate.load(Ordering::Relaxed) > 0
            && shared.state.load(Ordering::Relaxed) == State::Playing as u32
        {
            Session::read_audio(&px, buf);
        }
    };

    // Play until the offset is genuinely being exported, so the pre-seek
    // state is "armed" and a stale carry-over would be visible.
    let armed = wait_for(Duration::from_secs(20), || {
        pull(&mut buf);
        std::thread::sleep(Duration::from_millis(2));
        shared.av_offset_us.load(Ordering::Relaxed) != i32::MIN
    });
    assert!(armed, "never exported an offset before the seek");
    // `shared.frames_presented` counts host render events, so it stays 0
    // in a headless session; the pipeline's own present path books into the
    // diag stage instead.
    let presented = || {
        px.diag
            .stage(media_diag::Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
    };
    let presented_before = presented();

    session.seek(MediaTime::from_millis(1_500));

    // `seek` only queues the command, so the old timeline's offset is
    // legitimately exported until the demux thread reaches it. From the
    // clear onwards the offset must stay unknown until the new timeline
    // presents.
    let cleared = wait_for(Duration::from_secs(10), || {
        pull(&mut buf);
        std::thread::sleep(Duration::from_millis(1));
        shared.av_offset_us.load(Ordering::Relaxed) == i32::MIN
    });
    assert!(
        cleared,
        "the seek never cleared the offset, so the origin survived the flush"
    );

    let deadline = Instant::now() + Duration::from_secs(10);
    let mut saw_new_presentation = false;
    while Instant::now() < deadline {
        let offset = shared.av_offset_us.load(Ordering::Relaxed);
        let now_presented = presented();
        if offset != i32::MIN {
            assert!(
                now_presented > presented_before,
                "offset {offset} exported after the flush before the new                  timeline presented anything ({now_presented} presented,                  {presented_before} before the seek)"
            );
            saw_new_presentation = true;
            break;
        }
        pull(&mut buf);
        std::thread::sleep(Duration::from_millis(1));
    }
    assert!(
        saw_new_presentation,
        "the new timeline never re-armed the offset, so the row proved only          that it had been cleared"
    );
}

/// A track the demuxer leaves out because nothing here plays it is put in
/// front of the viewer as a refusal, not left in the log as a note.
#[test]
fn a_track_the_demuxer_refuses_is_reported_as_a_refusal() {
    let mut bytes = std::fs::read(fixture_path()).expect("fixture");
    // The sample entry, not the brand list in `ftyp` ahead of it.
    let at = bytes
        .windows(4)
        .rposition(|w| w == b"avc1")
        .expect("the fixture's sample entry");
    bytes[at..at + 4].copy_from_slice(b"xvid");
    let path = std::env::temp_dir().join(format!("bm-refused-video-{}.mp4", std::process::id()));
    std::fs::write(&path, bytes).expect("write the patched fixture");

    let mut session = Session::open(OpenRequest::new(path.to_string_lossy().into_owned()));
    let px = session.pipeline().clone();
    let mut refusal = None;
    wait_for(Duration::from_secs(10), || {
        refusal = px
            .diag
            .take_events()
            .into_iter()
            .find(|e| e.code == media_diag::EventCode::CodecRefused);
        refusal.is_some()
    });
    session.close();
    let _ = std::fs::remove_file(&path);
    let refusal = refusal.expect("the refusal must be reported");
    assert_eq!(refusal.stage, media_diag::Stage::Demux);
    assert_eq!(
        refusal.detail,
        "video codec 'xvid' is not supported (supported: H.264, H.265, VP9, AV1)"
    );
}
