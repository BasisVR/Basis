//! Split sources: a video-only stream played against a separate
//! audio-only one, which is how adaptive ladders serve every rung above
//! their muxed fallback. The two legs are cuts of the same content, so one
//! Bank meters them against one timeline and the session is otherwise an
//! ordinary one.

#![cfg(windows)]

use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_engine::{EngineError, OpenRequest, Session, State};

fn fixture(name: &str) -> String {
    std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name)
        .to_string_lossy()
        .into_owned()
}

fn video_leg() -> String {
    fixture("split/h264-640x360-30fps-video.mp4")
}

fn audio_leg() -> String {
    fixture("split/aac-48k-stereo-audio.m4a")
}

/// Separates "the second leg is playing" from "the second leg was dropped".
/// Not a throughput measure: `run` pulls at wall-clock cadence, so the total
/// also reflects scheduling, and the rest of the workspace suite running
/// alongside can cost a second. A dropped leg pulls about zero, so the bar
/// sits well under the fixtures' six seconds on purpose.
const AUDIO_LEG_PLAYING: u64 = 3 * 48_000;

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

/// Drives a session as the managed host does (pulling audio at the
/// hardware cadence) until `until` or the deadline. Returns the audio
/// frames pulled.
fn run(session: &Session, deadline: Duration, mut until: impl FnMut() -> bool) -> u64 {
    let shared = session.shared().clone();
    let px = session.pipeline().clone();
    let mut pulled = 0u64;
    let mut buf = vec![0f32; 2048];
    let mut epoch: Option<Instant> = None;
    let start = Instant::now();
    while start.elapsed() < deadline {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "error {}",
            shared.last_error.load(Ordering::Relaxed)
        );
        if until() {
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
    pulled
}

/// Two sources, one session. Video comes off one leg, audio off the
/// other, both play to a natural end, and the audio total shows the second
/// leg is being demuxed and decoded rather than dropped.
#[test]
fn a_split_pair_plays_both_legs_to_a_natural_end() {
    let mut request = OpenRequest::new(video_leg());
    request.audio_url = Some(audio_leg());
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let pulled = run(&session, Duration::from_secs(20), || {
        shared.state.load(Ordering::Relaxed) == State::Ended as u32
    });

    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Ended as u32,
        "a split session must end naturally once both legs are exhausted"
    );
    // Both fixtures are 6 s: 180 video frames and 48 kHz stereo audio.
    assert!(
        shared.frames_decoded.load(Ordering::Relaxed) >= 175,
        "decoded only {} video frames from the video leg",
        shared.frames_decoded.load(Ordering::Relaxed)
    );
    assert!(
        pulled >= AUDIO_LEG_PLAYING,
        "pulled only {pulled} audio frames — the audio leg is not playing"
    );
    assert_eq!(
        shared.audio_channels.load(Ordering::Relaxed),
        2,
        "the audio leg announces its own format"
    );
    session.close();
}

/// The video leg takes the seek and the audio leg follows it to the same
/// landing on the same generation, so the pair stays together. Without the
/// follow the audio leg would keep serving the old position and every
/// post-seek sample would be dropped as stale.
#[test]
fn seeking_takes_both_legs_to_the_same_place() {
    let mut request = OpenRequest::new(video_leg());
    request.audio_url = Some(audio_leg());
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "split session never reached Playing"
    );

    let before = shared.generation.load(Ordering::Relaxed);
    session.seek(media_clock::MediaTime::from_millis(4_000));
    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.generation.load(Ordering::Relaxed) != before
        }),
        "seek never advanced the generation"
    );

    // Audio pulled after the seek proves the audio leg re-anchored: its
    // pre-seek events carry the old generation and are dropped, so a leg
    // that never followed would go silent for the rest of the session.
    let pulled = run(&session, Duration::from_secs(15), || {
        shared.state.load(Ordering::Relaxed) == State::Ended as u32
    });
    assert!(
        pulled >= 48_000,
        "pulled only {pulled} audio frames after the seek — the audio leg did not follow"
    );
    assert!(
        shared.position_us.load(Ordering::Relaxed) >= 4_000_000,
        "position went backwards past the seek target"
    );
    session.close();
}

/// The video leg's keyframes are at 0, 2 and 4 s, so a seek to 3.2 s lands
/// its demuxer on 2 s and the span up to the target goes straight to the
/// video decoder. The video leg's track id is not the one the Bank sees,
/// and the span must still be recognised as video: dropped as audio, the
/// decoder never gets the picture the target's frame is built from.
#[test]
fn a_paused_seek_between_keyframes_shows_the_target_on_a_split_pair() {
    const TARGET_US: i64 = 3_200_000;
    // One frame of the 30 fps fixture, and a little for rounding.
    const FRAME_US: i64 = 34_000;
    let mut request = OpenRequest::new(video_leg());
    request.audio_url = Some(audio_leg());
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let diag = session.diag().clone();
    let px = session.pipeline().clone();
    let presented = || {
        diag.stage(media_diag::Stage::Present)
            .out_count
            .load(Ordering::Relaxed)
    };

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "split session never reached Playing"
    );
    session.pause();
    let presented_before = presented();
    let fed_before = px.seek_fed.load(Ordering::Relaxed);
    session.seek(media_clock::MediaTime::from_micros(TARGET_US));
    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Paused as u32
                && presented() > presented_before
        }),
        "the seek did not settle paused on a new frame (state {}, position {})",
        shared.state.load(Ordering::Relaxed),
        shared.position_us.load(Ordering::Relaxed),
    );

    assert!(
        px.seek_fed.load(Ordering::Relaxed) > fed_before,
        "no access unit ahead of the target reached the video decoder"
    );
    let shown = px.presented_pts_us.load(Ordering::Relaxed);
    assert!(
        (TARGET_US..=TARGET_US + FRAME_US).contains(&shown),
        "paused on {shown}, not on the frame at {TARGET_US}"
    );
    session.close();
}

/// Each leg contributes only its own kind of track. Handing the same muxed
/// file to both legs tests this hardest: without the filter the session
/// would announce two video and two audio tracks, whose ids would also
/// collide in the Bank.
#[test]
fn each_leg_contributes_only_its_own_kind_of_track() {
    let muxed = fixture("h264-aac-640x360-30fps.mp4");
    let mut request = OpenRequest::new(muxed.clone());
    request.audio_url = Some(muxed);
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let pulled = run(&session, Duration::from_secs(20), || {
        shared.state.load(Ordering::Relaxed) == State::Ended as u32
    });

    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Ended as u32,
        "session must still end when both legs carry both tracks"
    );
    assert_eq!(
        shared.audio_channels.load(Ordering::Relaxed),
        2,
        "audio must be announced exactly once"
    );
    assert!(
        pulled >= AUDIO_LEG_PLAYING,
        "pulled only {pulled} audio frames"
    );
    // 180 frames in the fixture. Decoding appreciably more would mean the
    // audio leg's video track was banked as well.
    let decoded = shared.frames_decoded.load(Ordering::Relaxed);
    assert!(
        (175..=190).contains(&decoded),
        "decoded {decoded} video frames — the audio leg's video track was not dropped"
    );
    session.close();
}

/// A second leg only makes sense against an on-demand byte stream. Every
/// live transport already carries both tracks, so asking for one there is
/// a typed refusal, not a session that silently plays no audio.
#[test]
fn a_split_request_on_a_live_transport_is_refused() {
    let mut request = OpenRequest::new("rtsp://192.0.2.1/nothing");
    request.audio_url = Some(audio_leg());
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    assert!(
        wait_for(Duration::from_secs(5), || {
            shared.state.load(Ordering::Relaxed) == State::Error as u32
        }),
        "a split request on an RTSP source must fail rather than open"
    );
    session.close();
}

/// Two sources whose timelines start too far apart cannot be banked
/// against one another. The Bank measures what it holds as one span across
/// everything banked, so the gap between the origins counts as held media.
/// Past the decoder cushion it reads as full from the trailing leg's first
/// access unit, both legs park, release never drains, and the session
/// would sit silently in Buffering until closed. It fails with a typed
/// error instead.
#[test]
fn a_split_pair_whose_timelines_disagree_is_refused() {
    // The TS fixture's first pts is ~1.47 s and the audio-only m4a starts
    // at zero, which is what an MPEG-TS rendition against an MP4 one does
    // by default.
    let mut request = OpenRequest::new(fixture("h264-aac-640x360-30fps.ts"));
    request.audio_url = Some(audio_leg());
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Error as u32
        }),
        "a mismatched pair must fail rather than sit in Buffering — state {}",
        shared.state.load(Ordering::Relaxed)
    );
    // Which failure, not just that one happened: an unreadable fixture or
    // a codec the host will not decode reaches Error too.
    assert_eq!(
        shared.last_error.load(Ordering::Relaxed),
        EngineError::split_origin_mismatch("").code,
        "the pair failed for some other reason"
    );
    session.close();
}

/// Container timelines rarely start at zero: MPEG-TS carries an arbitrary
/// 33-bit clock, fMP4 a `baseMediaDecodeTime`, Matroska its own first
/// cluster timestamp. The lead cap that keeps the two legs in step meters
/// their progress against each other, so a non-zero origin must not read
/// as one leg being ahead before either has banked anything.
#[test]
fn a_split_pair_whose_timeline_does_not_start_at_zero_still_plays() {
    // The TS fixture's first pts is ~1.47 s, well past the lead cap.
    let ts = fixture("h264-aac-640x360-30fps.ts");
    let mut request = OpenRequest::new(ts.clone());
    request.audio_url = Some(ts);
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    assert!(
        wait_for(Duration::from_secs(10), || {
            shared.state.load(Ordering::Relaxed) == State::Playing as u32
        }),
        "split session never reached Playing — state {}",
        shared.state.load(Ordering::Relaxed)
    );

    let pulled = run(&session, Duration::from_secs(20), || {
        shared.state.load(Ordering::Relaxed) == State::Ended as u32
    });
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Ended as u32,
        "a split pair on a non-zero timeline must still end naturally"
    );
    // Ending is not the same as playing: a pair that reached Eos having
    // produced nothing would satisfy the state alone. Both bars are loose on
    // purpose, since this harness pulls at wall-clock cadence and a
    // throughput figure would measure scheduling.
    assert!(
        shared.frames_decoded.load(Ordering::Relaxed) > 0,
        "the session ended without decoding a frame"
    );
    assert!(pulled > 0, "the session ended without serving any audio");
    session.close();
}
