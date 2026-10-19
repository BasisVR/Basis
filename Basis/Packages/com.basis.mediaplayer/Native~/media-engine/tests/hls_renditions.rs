//! HLS audio renditions: a variant whose audio is a separate `EXT-X-MEDIA`
//! playlist plays it as the audio leg of a split pair, and a rendition that
//! cannot be opened leaves the picture playing with the reason reported.

#![cfg(windows)]

use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_diag::EventCode;
use media_engine::{OpenRequest, Session, State};

/// A master playlist beside a video-only variant and, unless `rendition`
/// is `None`, an audio-only rendition, each one on-demand segment copied
/// from the split fixtures. Removed on drop.
struct Presentation(PathBuf);

impl Presentation {
    fn new(name: &str, rendition: Option<&str>) -> Self {
        let fixtures = Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/split");
        let dir =
            std::env::temp_dir().join(format!("bm-hls-renditions-{}-{name}", std::process::id()));
        std::fs::create_dir_all(&dir).expect("scratch dir");
        std::fs::copy(
            fixtures.join("h264-640x360-30fps-video.mp4"),
            dir.join("video.mp4"),
        )
        .expect("video segment");
        std::fs::copy(
            fixtures.join("aac-48k-stereo-audio.m4a"),
            dir.join("audio.m4a"),
        )
        .expect("audio segment");
        let media = |segment: &str| {
            format!(
                "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:6.0,\n{segment}\n#EXT-X-ENDLIST\n"
            )
        };
        std::fs::write(dir.join("video.m3u8"), media("video.mp4")).expect("variant");
        std::fs::write(dir.join("audio.m3u8"), media("audio.m4a")).expect("rendition");
        let rendition = rendition.map_or(String::new(), |uri| {
            format!(
                "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"main\",DEFAULT=YES,URI=\"{uri}\"\n"
            )
        });
        std::fs::write(
            dir.join("master.m3u8"),
            format!(
                "#EXTM3U\n{rendition}#EXT-X-STREAM-INF:BANDWIDTH=1000000,AUDIO=\"aud\"\nvideo.m3u8\n"
            ),
        )
        .expect("master");
        Self(dir)
    }

    fn master(&self) -> String {
        self.0.join("master.m3u8").to_string_lossy().into_owned()
    }
}

impl Drop for Presentation {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// Plays the session to its end or the deadline, pulling audio at the
/// hardware cadence as the managed host does. Returns the audio frames
/// pulled.
fn play(session: &Session, deadline: Duration) -> u64 {
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
        if state == State::Ended as u32 {
            break;
        }
        let rate = shared.audio_rate.load(Ordering::Relaxed);
        let channels = shared.audio_channels.load(Ordering::Relaxed).max(1);
        if rate > 0 && state == State::Playing as u32 {
            let at = *epoch.get_or_insert_with(Instant::now);
            let budget = (at.elapsed().as_micros() as u64 * u64::from(rate) / 1_000_000)
                .saturating_sub(pulled);
            if budget as usize >= buf.len() / channels as usize {
                pulled += Session::read_audio(&px, &mut buf) as u64;
            }
        }
        std::thread::sleep(Duration::from_millis(2));
    }
    pulled
}

/// The variant carries no audio: every sample pulled came off the
/// rendition.
#[test]
fn a_separate_audio_rendition_plays_beside_its_variant() {
    let presentation = Presentation::new("plays", Some("audio.m3u8"));
    let mut session = Session::open(OpenRequest::new(presentation.master()));
    let shared = session.shared().clone();
    let pulled = play(&session, Duration::from_secs(20));

    assert_eq!(shared.state.load(Ordering::Relaxed), State::Ended as u32);
    assert!(
        shared.frames_decoded.load(Ordering::Relaxed) >= 175,
        "decoded only {} frames",
        shared.frames_decoded.load(Ordering::Relaxed)
    );
    assert_eq!(shared.audio_channels.load(Ordering::Relaxed), 2);
    assert!(
        pulled >= 3 * 48_000,
        "pulled only {pulled} audio frames: the rendition is not playing"
    );
    session.close();
}

/// With the picture refused as well (a video codec nothing here plays),
/// nothing is left to play, and the failure names the missing rendition.
#[test]
fn a_failed_rendition_is_named_when_nothing_plays() {
    let presentation = Presentation::new("nothing", Some("absent.m3u8"));
    // The sample entry, not the brand list in `ftyp` ahead of it.
    let video = presentation.0.join("video.mp4");
    let mut bytes = std::fs::read(&video).expect("video segment");
    let at = bytes
        .windows(4)
        .rposition(|w| w == b"avc1")
        .expect("the sample entry");
    bytes[at..at + 4].copy_from_slice(b"xvid");
    std::fs::write(&video, bytes).expect("patch the video segment");
    let mut session = Session::open(OpenRequest::new(presentation.master()));
    let shared = session.shared().clone();
    let start = Instant::now();
    while shared.state.load(Ordering::Relaxed) != State::Error as u32
        && start.elapsed() < Duration::from_secs(20)
    {
        std::thread::sleep(Duration::from_millis(10));
    }
    assert_eq!(shared.state.load(Ordering::Relaxed), State::Error as u32);
    let events = session.pipeline().diag.take_events();
    assert!(
        events.iter().any(|event| event.code == EventCode::Error
            && event.detail.contains("audio rendition not played")),
        "{:?}",
        events.iter().map(|e| &e.detail).collect::<Vec<_>>()
    );
    session.close();
}

#[test]
fn a_rendition_that_cannot_open_leaves_the_picture_playing() {
    // A playlist that is not there, and a URI outside the master's
    // directory, which the playlist parse refuses.
    for (name, uri) in [("missing", "absent.m3u8"), ("outside", "../elsewhere.m3u8")] {
        let presentation = Presentation::new(name, Some(uri));
        let mut session = Session::open(OpenRequest::new(presentation.master()));
        let shared = session.shared().clone();
        play(&session, Duration::from_secs(20));

        assert_eq!(
            shared.state.load(Ordering::Relaxed),
            State::Ended as u32,
            "{uri}"
        );
        assert!(
            shared.frames_decoded.load(Ordering::Relaxed) >= 175,
            "{uri}"
        );
        let events = session.pipeline().diag.take_events();
        assert!(
            events
                .iter()
                .any(|event| event.code == EventCode::CodecRefused
                    && event.detail.contains("audio rendition not played")),
            "{uri}: the reason is reported where a left-out track is: {:?}",
            events.iter().map(|e| &e.detail).collect::<Vec<_>>()
        );
        session.close();
    }
}
