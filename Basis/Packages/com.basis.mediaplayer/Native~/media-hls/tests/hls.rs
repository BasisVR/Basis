//! HLS scheduler + chaining tests over a virtual fetcher: VOD both
//! container flavours, live window advance, window fall-out,
//! stated discontinuities, master variant choice, feature refusals,
//! seek-to-segment, and Low-Latency HLS (join point, parts, blocking
//! reloads). Segment bytes come from the committed HLS fixtures;
//! time is virtual (recorded waits, no sleeps).

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex};
use std::time::Duration;

use media_clock::{Generation, MediaTime};
use media_demux::{DemuxError, DemuxLimits, Demuxer, Format, SourceError, StreamEvent};
use media_hls::{HlsDemuxer, ParsedPlaylist, SegmentFetcher, looks_like_playlist};

fn fixture_dir(kind: &str) -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join(format!("../fixtures/hls/{kind}"))
}

#[derive(Default)]
struct FetchLog {
    fetched: Vec<String>,
    waits: Vec<Duration>,
    /// Virtual time: advanced by waits and by held blocking reloads.
    now: Duration,
}

/// Virtual fetcher: static resources plus a sequence of playlist bodies
/// served for successive fetches of the playlist URL (a live refresh).
struct MockFetcher {
    resources: HashMap<String, Vec<u8>>,
    playlist_url: String,
    refreshes: Vec<Vec<u8>>,
    refresh_cursor: usize,
    /// Answer blocking reloads with an error, as a server past its
    /// blocking limit does.
    refuse_blocking: bool,
    /// How long the server holds each blocking reload before answering.
    blocking_hold: Duration,
    log: Arc<Mutex<FetchLog>>,
}

impl MockFetcher {
    fn new(playlist_url: &str, refreshes: Vec<Vec<u8>>) -> (Self, Arc<Mutex<FetchLog>>) {
        let log = Arc::new(Mutex::new(FetchLog::default()));
        (
            Self {
                resources: HashMap::new(),
                playlist_url: playlist_url.to_string(),
                refreshes,
                refresh_cursor: 0,
                refuse_blocking: false,
                blocking_hold: Duration::ZERO,
                log: Arc::clone(&log),
            },
            log,
        )
    }

    fn with_file(mut self, url: &str, path: PathBuf) -> Self {
        self.resources
            .insert(url.to_string(), std::fs::read(path).expect("fixture bytes"));
        self
    }
}

impl SegmentFetcher for MockFetcher {
    fn fetch(&mut self, url: &str, _cap: u64) -> Result<Vec<u8>, SourceError> {
        self.log.lock().unwrap().fetched.push(url.to_string());
        let (path, query) = url.split_once('?').unwrap_or((url, ""));
        if path == self.playlist_url {
            if query.contains("_HLS_msn") {
                self.log.lock().unwrap().now += self.blocking_hold;
            }
            if self.refuse_blocking && query.contains("_HLS_msn") {
                return Err("503 Service Unavailable".into());
            }
            let body = self.refreshes[self.refresh_cursor.min(self.refreshes.len() - 1)].clone();
            self.refresh_cursor += 1;
            return Ok(body);
        }
        self.resources
            .get(url)
            .cloned()
            .ok_or_else(|| format!("no such resource: {url}").into())
    }

    fn wait(&mut self, duration: Duration) {
        let mut log = self.log.lock().unwrap();
        log.waits.push(duration);
        log.now += duration;
    }

    fn now(&self) -> Duration {
        self.log.lock().unwrap().now
    }
}

const BASE: &str = "https://test/index.m3u8";

fn open(playlist: &str, fetcher: MockFetcher) -> Result<HlsDemuxer, DemuxError> {
    HlsDemuxer::open(
        BASE,
        playlist.as_bytes().to_vec(),
        Box::new(fetcher),
        DemuxLimits::default(),
        Generation(0),
    )
}

struct Drained {
    /// Video dts per AU (decode order; pts reorders under B-frames).
    video_aus: Vec<MediaTime>,
    max_video_pts: MediaTime,
    audio_aus: usize,
    video_formats: usize,
    audio_formats: usize,
    discontinuities: usize,
}

fn drain(demuxer: &mut dyn Demuxer) -> Result<Drained, DemuxError> {
    let mut out = Drained {
        video_aus: Vec::new(),
        max_video_pts: MediaTime::ZERO,
        audio_aus: 0,
        video_formats: 0,
        audio_formats: 0,
        discontinuities: 0,
    };
    let mut video_track = None;
    let mut audio_track = None;
    loop {
        match demuxer.next_event()? {
            StreamEvent::Format(track, Format::Video { .. }) => {
                video_track = Some(track);
                out.video_formats += 1;
            }
            StreamEvent::Format(track, Format::Audio { .. }) => {
                audio_track = Some(track);
                out.audio_formats += 1;
            }
            StreamEvent::Au(au) if Some(au.track) == video_track => {
                out.max_video_pts = out.max_video_pts.max(au.pts);
                out.video_aus.push(au.dts);
            }
            StreamEvent::Au(au) if Some(au.track) == audio_track => out.audio_aus += 1,
            StreamEvent::Au(_) => {}
            StreamEvent::Discontinuity(..) => out.discontinuities += 1,
            StreamEvent::Eos(_) => return Ok(out),
            _ => {}
        }
    }
}

fn ts_fetcher(refreshes: Vec<Vec<u8>>) -> MockFetcher {
    let dir = fixture_dir("ts");
    let (fetcher, _) = MockFetcher::new(BASE, refreshes);
    fetcher
        .with_file("https://test/seg000.ts", dir.join("seg000.ts"))
        .with_file("https://test/seg001.ts", dir.join("seg001.ts"))
        .with_file("https://test/seg002.ts", dir.join("seg002.ts"))
}

const VOD_TS: &str = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n";

#[test]
fn vod_ts_plays_every_segment_through_one_demuxer() {
    let mut demuxer = open(VOD_TS, ts_fetcher(vec![])).expect("open");
    assert!(!demuxer.is_live());
    assert_eq!(demuxer.duration(), Some(MediaTime::from_secs(6)));
    let drained = drain(&mut demuxer).expect("drain");
    // The conformance counts for the source fixture: chaining must not
    // lose an AU at any segment boundary.
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.audio_aus, 283);
    assert_eq!(drained.video_formats, 1, "one video announce, deduped");
    assert_eq!(drained.audio_formats, 1);
    assert_eq!(drained.discontinuities, 0);
    let mut sorted = drained.video_aus.clone();
    sorted.sort();
    assert_eq!(sorted, drained.video_aus, "video dts monotonic");
}

#[test]
fn vod_fmp4_plays_every_segment_with_absolute_timestamps() {
    let dir = fixture_dir("fmp4");
    let (fetcher, _) = MockFetcher::new(BASE, vec![]);
    let fetcher = fetcher
        .with_file("https://test/init.mp4", dir.join("init.mp4"))
        .with_file("https://test/seg000.m4s", dir.join("seg000.m4s"))
        .with_file("https://test/seg001.m4s", dir.join("seg001.m4s"))
        .with_file("https://test/seg002.m4s", dir.join("seg002.m4s"));
    // EXT-X-MAP appears once and applies to every later segment.
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-MAP:URI=\"init.mp4\"\n\
#EXTINF:2.0,\nseg000.m4s\n#EXTINF:2.0,\nseg001.m4s\n#EXTINF:2.0,\nseg002.m4s\n#EXT-X-ENDLIST\n";
    let mut demuxer = open(playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.audio_aus, 283);
    assert_eq!(drained.video_formats, 1);
    let mut sorted = drained.video_aus.clone();
    sorted.sort();
    assert_eq!(sorted, drained.video_aus, "tfdt keeps dts absolute");
    let last = drained.max_video_pts;
    assert!(last > MediaTime::from_millis(5900), "tail reached: {last}");
}

#[test]
fn live_window_advances_and_ends() {
    // One segment visible per refresh; ENDLIST arrives with the last.
    let win = |segments: &str, end: bool| {
        format!(
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n{segments}{}",
            if end { "#EXT-X-ENDLIST\n" } else { "" }
        )
        .into_bytes()
    };
    let refreshes = vec![
        win("#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n", false),
        win(
            "#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n",
            true,
        ),
    ];
    let initial = win("#EXTINF:2.0,\nseg000.ts\n", false);
    let fetcher = ts_fetcher(refreshes);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(std::str::from_utf8(&initial).unwrap(), fetcher).expect("open");
    assert!(demuxer.is_live());
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180, "all three segments played");
    assert_eq!(drained.discontinuities, 0);
    let log = log.lock().unwrap();
    assert!(!log.waits.is_empty(), "refreshes waited between fetches");
    assert!(
        log.waits.iter().all(|w| *w >= Duration::from_millis(500)),
        "refresh cadence respects the floor"
    );
}

#[test]
fn live_join_starts_three_segments_from_the_edge() {
    // Five segments in the initial window: the join point is sequence 2.
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nmissing0.ts\n#EXTINF:2.0,\nmissing1.ts\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n";
    let end = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nmissing0.ts\n#EXTINF:2.0,\nmissing1.ts\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n";
    let fetcher = ts_fetcher(vec![end.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180, "joined at the edge backoff");
    let fetched = log.lock().unwrap().fetched.clone();
    assert_eq!(
        fetched.first().map(String::as_str),
        Some("https://test/seg000.ts"),
        "first fetch is the join point, not the window start: {fetched:?}"
    );
    assert!(
        !fetched.iter().any(|u| u.contains("missing")),
        "segments behind the join point are never fetched"
    );
}

#[test]
fn window_fallout_jumps_forward_with_a_discontinuity() {
    // The window races past the cursor: refresh drops straight to
    // sequence 2.
    let refreshes = vec![
        "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:2\n\
#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n"
            .as_bytes()
            .to_vec(),
    ];
    let initial = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nseg000.ts\n";
    let fetcher = ts_fetcher(refreshes);
    let mut demuxer = open(initial, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(
        drained.discontinuities, 1,
        "the jump surfaces as a discontinuity"
    );
    assert_eq!(drained.video_aus.len(), 120, "seg000 + seg002 played");
    let notes = demuxer.take_notes();
    assert!(
        notes.iter().any(|n| n.contains("window advanced")),
        "fall-out noted: {notes:?}"
    );
}

#[test]
fn stated_discontinuity_rebuilds_and_reports() {
    // The same segment twice with a stated splice: timestamps restart,
    // the TS demuxer rebuilds, downstream hears about it.
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nseg000.ts\n#EXT-X-DISCONTINUITY\n#EXTINF:2.0,\nseg000.ts\n#EXT-X-ENDLIST\n";
    let mut demuxer = open(playlist, ts_fetcher(vec![])).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.discontinuities, 1);
    assert_eq!(drained.video_aus.len(), 120);
}

#[test]
fn master_playlist_picks_the_highest_bandwidth_variant() {
    let master = "#EXTM3U\n\
#EXT-X-STREAM-INF:BANDWIDTH=200000,RESOLUTION=320x180\nlow.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360\nhigh.m3u8\n";
    let (fetcher, log) = MockFetcher::new(BASE, vec![]);
    let dir = fixture_dir("ts");
    let mut fetcher = fetcher
        .with_file("https://test/seg000.ts", dir.join("seg000.ts"))
        .with_file("https://test/seg001.ts", dir.join("seg001.ts"))
        .with_file("https://test/seg002.ts", dir.join("seg002.ts"));
    fetcher
        .resources
        .insert("https://test/high.m3u8".into(), VOD_TS.as_bytes().to_vec());
    let mut demuxer = open(master, fetcher).expect("open");
    let notes = demuxer.take_notes();
    assert!(
        notes.iter().any(|n| n.contains("800000")),
        "variant choice noted: {notes:?}"
    );
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180);
    let fetched = log.lock().unwrap().fetched.clone();
    assert!(fetched.iter().any(|u| u.ends_with("high.m3u8")));
    assert!(!fetched.iter().any(|u| u.ends_with("low.m3u8")));
}

#[test]
fn unsupported_features_refuse_at_open() {
    for (playlist, what) in [
        (
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:2.0,\nseg000.ts\n#EXT-X-ENDLIST\n",
            "encryption",
        ),
        (
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXTINF:2.0,\n#EXT-X-BYTERANGE:1000@0\nseg000.ts\n#EXT-X-ENDLIST\n",
            "byte ranges",
        ),
        (
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-I-FRAMES-ONLY\n\
#EXTINF:2.0,\nseg000.ts\n#EXT-X-ENDLIST\n",
            "iframe-only",
        ),
    ] {
        match open(playlist, ts_fetcher(vec![])) {
            Err(DemuxError::Unsupported(_)) => {}
            Err(other) => panic!("{what}: expected Unsupported, got {other:?}"),
            Ok(_) => panic!("{what}: expected a typed refusal, got a demuxer"),
        }
    }
}

#[test]
fn vod_seek_lands_on_the_target_segment() {
    let mut demuxer = open(VOD_TS, ts_fetcher(vec![])).expect("open");
    // Pull a few events to learn the timeline origin.
    let mut origin = None;
    while origin.is_none() {
        if let StreamEvent::Au(au) = demuxer.next_event().expect("event") {
            origin = Some(au.pts);
        }
    }
    let origin = origin.unwrap();

    let landed = demuxer
        .seek(origin + MediaTime::from_millis(3500), Generation(1))
        .expect("seek supported on HLS VOD");
    assert_eq!(landed, origin + MediaTime::from_secs(2), "segment start");

    // The next video AUs come from segment 1 with the new generation.
    // Formats are deduped across the seek, so track identity comes from
    // the demuxer, not a re-announce.
    let video_track = demuxer.video_track();
    assert!(video_track.is_some(), "video track learned before the seek");
    loop {
        match demuxer.next_event().expect("event") {
            StreamEvent::Au(au) if Some(au.track) == video_track => {
                assert_eq!(au.generation, Generation(1));
                assert!(
                    au.pts >= origin + MediaTime::from_millis(1900),
                    "resumed at segment 1, got {}",
                    au.pts
                );
                break;
            }
            StreamEvent::Eos(_) => panic!("ended before the post-seek AU"),
            _ => {}
        }
    }
}

#[test]
fn seek_on_a_live_playlist_is_refused() {
    let initial = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nseg000.ts\n";
    let mut demuxer = open(initial, ts_fetcher(vec![])).expect("open");
    match demuxer.seek(MediaTime::from_secs(1), Generation(1)) {
        Err(DemuxError::Unsupported(_)) => {}
        other => panic!("expected Unsupported, got {other:?}"),
    }
}

#[test]
fn playlist_sniff_tolerates_bom_and_whitespace() {
    assert!(looks_like_playlist(b"#EXTM3U\n#EXT-X-VERSION:3\n"));
    assert!(looks_like_playlist(b"\xEF\xBB\xBF#EXTM3U\n"));
    assert!(looks_like_playlist(b"\r\n#EXTM3U\n"));
    assert!(!looks_like_playlist(b"{\"not\": \"a playlist\"}"));
    assert!(!looks_like_playlist(&[0x47, 0x40, 0x00, 0x10]));
}

/// Fuzz-found (`hls_playlist` target): a hostile EXTINF duration must be a
/// typed cap refusal, not a MediaTime overflow in the cumulative-duration
/// folds. The playlist is otherwise well-formed so that the duration is
/// what the refusal is about; the pinned fuzz input below carries several
/// hostile shapes at once and does not isolate any one of them.
#[test]
fn hostile_extinf_duration_is_a_cap_refusal() {
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:2679\n\
#EXTINF:44444444444444445.988,\nseg2680.ts\n#EXT-X-ENDLIST\n";
    match media_hls::parse_playlist(playlist.as_bytes(), BASE) {
        Err(DemuxError::Cap(_)) => {}
        Err(other) => panic!("expected a cap refusal, got {other:?}"),
        Ok(_) => panic!("hostile duration parsed"),
    }
}

/// The fuzz input itself: whatever the parser makes of it, the answer is a
/// typed error rather than a panic or an unbounded allocation. Which
/// refusal fires is not pinned: the input carries a hostile duration, a
/// corrupt tag and a garbage URI together, and which is reached first is
/// an implementation detail.
#[test]
fn the_pinned_campaign_input_refuses_without_panicking() {
    let bytes = std::fs::read(
        PathBuf::from(env!("CARGO_MANIFEST_DIR"))
            .join("tests/data/hls-playlist/extinf-overflow.m3u8"),
    )
    .expect("pinned input");
    assert!(
        media_hls::parse_playlist(&bytes, BASE).is_err(),
        "the campaign input must refuse"
    );
}

/// A playlist opened from disk names its resources beside itself. Path
/// joining would drop the base entirely for an absolute URI and walk out
/// of it for a `..`, so resolution refuses anything that is not a plain
/// relative name rather than handing the fetcher a path to disown. A
/// plain relative URI, with or without a `./`, still resolves.
///
/// Every row holds on every host. `Path` parses only the syntax of the
/// platform it was built for, so a Unix build reads the Windows rows as
/// ordinary filenames and would take a playlist written to attack a
/// Windows client; those shapes are screened as text so the answer does
/// not depend on the host.
#[test]
fn a_disk_playlist_refuses_a_uri_outside_its_directory() {
    let local_base = "/srv/fixtures/index.m3u8";
    let playlist = |uri: &str| {
        format!("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\n{uri}\n#EXT-X-ENDLIST\n")
            .into_bytes()
    };

    for uri in [
        // Recognised by Path on any host.
        "/etc/passwd",
        "../../../etc/passwd",
        "sub/../../escape.ts",
        // Windows syntax, refused off Windows too.
        r"C:\Windows\win.ini",
        "c:win.ini",
        "C:/Windows/win.ini",
        r"\\attacker.example\share\clip.ts",
        r"sub\..\..\escape.ts",
    ] {
        match media_hls::parse_playlist(&playlist(uri), local_base) {
            Err(DemuxError::Parse(detail)) => assert!(
                detail.contains("outside the playlist's directory"),
                "{uri:?} refused for the wrong reason: {detail}"
            ),
            Err(other) => panic!("{uri:?}: expected a resolution refusal, got {other:?}"),
            Ok(_) => panic!("{uri:?} resolved instead of refusing"),
        }
    }

    for uri in ["seg000.ts", "./seg000.ts", "nested/seg000.ts"] {
        media_hls::parse_playlist(&playlist(uri), local_base)
            .unwrap_or_else(|e| panic!("{uri:?} is a plain relative URI and must resolve: {e:?}"));
    }
}

/// A playlist named without a directory (`bm-probe play index.m3u8` from
/// inside the fixture directory) resolves its segments against the
/// current directory explicitly. The fetcher is built confined to a
/// directory and judges what it is handed against that root, so a bare
/// name coming back here would be disowned there and the playlist would
/// open and then fetch nothing.
#[test]
fn a_playlist_named_without_a_directory_resolves_against_the_current_one() {
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\nseg000.ts\n#EXT-X-ENDLIST\n";
    let parsed = media_hls::parse_playlist(playlist.as_bytes(), "index.m3u8").expect("resolves");
    let ParsedPlaylist::Media(window) = parsed else {
        panic!("media playlist expected")
    };
    let resolved = std::path::Path::new(&window.segments[0].url);
    assert!(
        resolved.strip_prefix(".").is_ok(),
        "must sit under the same root the fetcher is given: {:?}",
        window.segments[0].url
    );
}

/// A playlist served over the network may not name resources of a
/// different kind. Every URI resolves through URL joining and the result
/// has to be one the fetcher can actually go and get, so a scheme that
/// would instead land on its filesystem arm is refused at resolution.
/// The drive-letter forms are the ones that matter on Windows: `c://x`
/// carries a `://` that a naive absolute-URI check would pass through
/// untouched, and `c:/x` reads as a one-character scheme.
#[test]
fn a_network_playlist_cannot_change_a_uri_to_an_unfetchable_scheme() {
    let playlist = |uri: &str| {
        format!("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\n{uri}\n#EXT-X-ENDLIST\n")
            .into_bytes()
    };

    for uri in [
        "c://Windows/win.ini",
        "c:/Windows/win.ini",
        "C://Windows//System32/drivers/etc/hosts",
        "file:///etc/passwd",
        "ftp://attacker.example/clip.ts",
        "data:text/plain,hello",
    ] {
        match media_hls::parse_playlist(&playlist(uri), BASE) {
            Err(DemuxError::Parse(detail)) => assert!(
                detail.contains("unfetchable"),
                "{uri:?} refused for the wrong reason: {detail}"
            ),
            Err(other) => panic!("{uri:?}: expected a scheme refusal, got {other:?}"),
            Ok(_) => panic!("{uri:?} resolved instead of refusing"),
        }
    }

    // What a real playlist does, all still fine: relative, root-relative,
    // absolute onto another host (a CDN split across origins), and a
    // scheme change between the two fetchable ones.
    for uri in [
        "seg000.ts",
        "/other/seg000.ts",
        "https://cdn.example/seg000.ts",
        "http://cdn.example/seg000.ts",
    ] {
        media_hls::parse_playlist(&playlist(uri), BASE)
            .unwrap_or_else(|e| panic!("{uri:?} is an ordinary playlist URI: {e:?}"));
    }
}

/// A playlist on disk may still name network resources: that is an
/// ordinary local fixture pointing at a CDN, and the address gate vets
/// it at the fetcher. Only the schemes that would reach the filesystem
/// arm are refused.
#[test]
fn a_disk_playlist_may_still_name_network_resources() {
    let playlist = |uri: &str| {
        format!("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4.0,\n{uri}\n#EXT-X-ENDLIST\n")
            .into_bytes()
    };
    let local_base = "/srv/fixtures/index.m3u8";

    for uri in ["https://cdn.example/seg000.ts", "http://cdn.example/seg.ts"] {
        let parsed = media_hls::parse_playlist(&playlist(uri), local_base)
            .unwrap_or_else(|e| panic!("{uri:?} must resolve from a disk playlist: {e:?}"));
        match parsed {
            ParsedPlaylist::Media(window) => assert_eq!(
                window.segments[0].url, uri,
                "the URI stays whole for the fetcher to vet"
            ),
            ParsedPlaylist::Master(_) => panic!("media playlist expected"),
        }
    }

    // A scheme that would land on the filesystem arm is still refused.
    for uri in ["file:///etc/passwd", "c://Windows/win.ini"] {
        assert!(
            media_hls::parse_playlist(&playlist(uri), local_base).is_err(),
            "{uri:?} must not resolve from a disk playlist"
        );
    }
}

/// The scheduler's notes are drained once, before playback starts, so
/// nothing empties them again for the life of a live session, and a live
/// session records one on every window jump and every segment it has to
/// skip. Over hours that is unbounded growth on the demux thread, so the
/// collection is capped like the demuxers' own.
#[test]
fn scheduler_notes_stay_bounded_over_a_long_live_session() {
    // Three segments per window, so the live join point is the window
    // start and every one of them is reached; none of them is fetchable,
    // so each is a skip note. Each refresh moves the window far past the
    // cursor, which is a jump note on top.
    let window = |first: u64| {
        format!(
            "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first}\n\
             #EXTINF:2.0,\ngone{first}a.ts\n#EXTINF:2.0,\ngone{first}b.ts\n\
             #EXTINF:2.0,\ngone{first}c.ts\n"
        )
    };
    // Every refresh offers at least its own window-jump note, so asking
    // for one more window than the cap holds overruns it whatever the cap
    // is set to. Derived rather than fixed: a count chosen against today's
    // cap turns a raised cap into a failing row about nothing.
    let windows = media_demux::MAX_NOTES as u64 + 1;
    let refreshes: Vec<Vec<u8>> = (1..=windows)
        .map(|i| window(i * 100).into_bytes())
        .collect();
    let (fetcher, _log) = MockFetcher::new(BASE, refreshes);
    let mut demuxer = open(&window(0), fetcher).expect("a live playlist opens");
    // One jump note per window, plus a skip note per segment in it.
    for _ in 0..10_000 {
        match demuxer.next_event() {
            Ok(StreamEvent::Eos(_)) | Err(_) => break,
            Ok(_) => {}
        }
    }
    let notes = demuxer.take_notes();
    assert_eq!(
        notes.len(),
        media_demux::MAX_NOTES,
        "filled and stopped rather than growing with the session: {notes:?}"
    );
}

/// A live Low-Latency HLS window: segments 0 and 1 complete, segment 2
/// in progress with one part. Parts are 2 s fixture files; `missing*`,
/// `whole*` and `hint*` are never served.
const LL_TS: &str = "#EXTM3U\n#EXT-X-VERSION:9\n#EXT-X-TARGETDURATION:6\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-PART:DURATION=2.0,URI=\"missing0a.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"missing0b.ts\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"missing0c.ts\"\n\
#EXTINF:6.0,\nmissing0.ts\n\
#EXT-X-PART:DURATION=2.0,URI=\"missing1a.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\"\n\
#EXTINF:6.0,\nwhole1.ts\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg002.ts\",INDEPENDENT=YES\n\
#EXT-X-PRELOAD-HINT:TYPE=PART,URI=\"hint.ts\"\n";

/// The same window once segment 2 completes and the stream ends.
const LL_TS_ENDED: &str = "#EXTM3U\n#EXT-X-VERSION:9\n#EXT-X-TARGETDURATION:6\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:6.0,\nmissing0.ts\n\
#EXT-X-PART:DURATION=2.0,URI=\"missing1a.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\"\n\
#EXTINF:6.0,\nwhole1.ts\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg002.ts\",INDEPENDENT=YES\n\
#EXTINF:2.0,\nwhole2.ts\n#EXT-X-ENDLIST\n";

fn media_fetches(log: &FetchLog) -> Vec<String> {
    log.fetched
        .iter()
        .filter(|url| !url.contains(".m3u8"))
        .cloned()
        .collect()
}

fn playlist_fetches(log: &FetchLog) -> Vec<String> {
    log.fetched
        .iter()
        .filter(|url| url.contains(".m3u8"))
        .cloned()
        .collect()
}

#[test]
fn ll_join_is_part_hold_back_behind_the_end_on_an_independent_part() {
    // The end is 14 s in; 4 s back is 10 s, where part 2 of segment 1
    // starts, but it is not independent. Part 1, at 8 s, is.
    let fetcher = ts_fetcher(vec![LL_TS_ENDED.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(LL_TS, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(
        media_fetches(&log.lock().unwrap()),
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "joined on segment 1's independent part, then rode parts"
    );
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.discontinuities, 0);
    let notes = demuxer.take_notes();
    assert!(
        notes
            .iter()
            .any(|n| n.contains("joining sequence 1 part 1, 6000 ms behind the end")),
        "join noted: {notes:?}"
    );
}

#[test]
fn ll_reload_blocks_for_the_next_part_without_waiting() {
    let fetcher = ts_fetcher(vec![LL_TS_ENDED.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(LL_TS, fetcher).expect("open");
    drain(&mut demuxer).expect("drain");
    let log = log.lock().unwrap();
    assert_eq!(
        playlist_fetches(&log),
        ["https://test/index.m3u8?_HLS_msn=2&_HLS_part=1"],
        "one blocking reload, for part 1 of segment 2"
    );
    assert!(
        log.waits.is_empty(),
        "a blocking reload goes out at once: {:?}",
        log.waits
    );
}

/// Segment 0 in progress with one part; the reload completes it with a
/// second part, adds segment 1 whole, and ends the stream.
const LL_TS_EDGE: &str = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n";
const LL_TS_EDGE_ENDED: &str = "#EXTM3U\n#EXT-X-TARGETDURATION:4\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\"\n\
#EXTINF:4.0,\nwhole0.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n";

#[test]
fn a_segment_begun_on_parts_is_finished_on_parts_after_the_stream_ends() {
    let fetcher = ts_fetcher(vec![LL_TS_EDGE_ENDED.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(LL_TS_EDGE, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(
        media_fetches(&log.lock().unwrap()),
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "the rest of segment 0 on parts, never whole, then segment 1"
    );
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.discontinuities, 0);
}

#[test]
fn a_refused_blocking_reload_falls_back_to_a_plain_one() {
    let mut fetcher = ts_fetcher(vec![LL_TS_EDGE_ENDED.as_bytes().to_vec()]);
    fetcher.refuse_blocking = true;
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(LL_TS_EDGE, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180, "the lane kept going");
    {
        let log = log.lock().unwrap();
        assert_eq!(
            playlist_fetches(&log),
            [
                "https://test/index.m3u8?_HLS_msn=0&_HLS_part=1",
                "https://test/index.m3u8"
            ]
        );
        assert!(
            !log.waits.is_empty(),
            "the plain reload waited its interval"
        );
    }
    let notes = demuxer.take_notes();
    assert!(
        notes.iter().any(|n| n.contains("blocking reload failed")),
        "fallback noted: {notes:?}"
    );
}

#[test]
fn a_refused_blocking_reload_is_not_sent_again() {
    // The first reload brings nothing new, so a second is needed.
    let mut fetcher = ts_fetcher(vec![
        LL_TS_EDGE.as_bytes().to_vec(),
        LL_TS_EDGE_ENDED.as_bytes().to_vec(),
    ]);
    fetcher.refuse_blocking = true;
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(LL_TS_EDGE, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180, "the lane kept going");
    let log = log.lock().unwrap();
    assert_eq!(
        playlist_fetches(&log),
        [
            "https://test/index.m3u8?_HLS_msn=0&_HLS_part=1",
            "https://test/index.m3u8",
            "https://test/index.m3u8"
        ]
    );
    assert_eq!(
        log.waits.len(),
        2,
        "one interval per plain reload: {:?}",
        log.waits
    );
}

#[test]
fn a_refused_map_uri_declines_the_parts() {
    // No complete segment yet: the in-progress parts' init segment is named
    // only after the last segment URI, where only our own scan reads.
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=1.5\n\
#EXT-X-PART-INF:PART-TARGET=0.5\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-MAP:URI=\"ftp://elsewhere/init.mp4\"\n\
#EXT-X-PART:DURATION=0.5,URI=\"part0.m4s\",INDEPENDENT=YES\n";
    let (fetcher, _) = MockFetcher::new(BASE, vec![]);
    let mut demuxer = open(playlist, fetcher).expect("the playlist itself still opens");
    let notes = demuxer.take_notes();
    assert!(
        notes.iter().any(|n| n
            .contains("low-latency parts not used (a low-latency tag could not be read)")),
        "{notes:?}"
    );
}

#[test]
fn parts_without_blocking_reload_play_whole_segments() {
    // Parts listed, no CAN-BLOCK-RELOAD: whole segments, joined three
    // target durations back (seg000 at 4 s of 10 s).
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-PART-INF:PART-TARGET=1.0\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nmissing0.ts\n\
#EXT-X-PART:DURATION=1.0,URI=\"part1a.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=1.0,URI=\"part1b.ts\"\n\
#EXTINF:2.0,\nmissing1.ts\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n\
#EXT-X-PART:DURATION=1.0,URI=\"part5a.ts\",INDEPENDENT=YES\n";
    let ended = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nmissing0.ts\n#EXTINF:2.0,\nmissing1.ts\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n";
    let fetcher = ts_fetcher(vec![ended.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180);
    {
        let log = log.lock().unwrap();
        assert_eq!(
            media_fetches(&log),
            [
                "https://test/seg000.ts",
                "https://test/seg001.ts",
                "https://test/seg002.ts"
            ]
        );
        assert!(
            !log.fetched.iter().any(|u| u.contains("_HLS_")),
            "no blocking directives to a server that did not offer them"
        );
    }
    let notes = demuxer.take_notes();
    assert!(
        notes
            .iter()
            .any(|n| n.contains("low-latency parts not used")),
        "decline noted: {notes:?}"
    );
}

#[test]
fn a_plain_live_join_honours_hold_back() {
    // HOLD-BACK 8 s of a 12 s window: the join is at 4 s (sequence 2),
    // where three target durations would put it at 6 s.
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-SERVER-CONTROL:HOLD-BACK=8.0\n";
    let body = "#EXT-X-MEDIA-SEQUENCE:0\n\
#EXTINF:2.0,\nmissing0.ts\n#EXTINF:2.0,\nmissing1.ts\n\
#EXTINF:2.0,\nseg000.ts\n#EXTINF:2.0,\nseg001.ts\n#EXTINF:2.0,\nseg002.ts\n\
#EXT-X-DISCONTINUITY\n#EXTINF:2.0,\nseg000.ts\n";
    let playlist = format!("{head}{body}");
    let ended = format!("{head}{body}#EXT-X-ENDLIST\n");
    let fetcher = ts_fetcher(vec![ended.into_bytes()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(&playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    let fetched = media_fetches(&log.lock().unwrap());
    assert_eq!(
        fetched.first().map(String::as_str),
        Some("https://test/seg000.ts"),
        "joined HOLD-BACK behind the end: {fetched:?}"
    );
    assert!(!fetched.iter().any(|u| u.contains("missing")));
    assert_eq!(drained.video_aus.len(), 240);
    assert_eq!(drained.discontinuities, 1);
}

#[test]
fn ll_fmp4_parts_play_against_the_init_segment() {
    let dir = fixture_dir("fmp4");
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=6.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-MAP:URI=\"init.mp4\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg000.m4s\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.m4s\"\n";
    let ended = format!(
        "{head}#EXT-X-PART:DURATION=2.0,URI=\"seg002.m4s\"\n#EXTINF:6.0,\nwhole0.m4s\n#EXT-X-ENDLIST\n"
    );
    let (fetcher, log) = MockFetcher::new(BASE, vec![ended.into_bytes()]);
    let fetcher = fetcher
        .with_file("https://test/init.mp4", dir.join("init.mp4"))
        .with_file("https://test/seg000.m4s", dir.join("seg000.m4s"))
        .with_file("https://test/seg001.m4s", dir.join("seg001.m4s"))
        .with_file("https://test/seg002.m4s", dir.join("seg002.m4s"));
    let mut demuxer = open(head, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.audio_aus, 283);
    let mut sorted = drained.video_aus.clone();
    sorted.sort();
    assert_eq!(
        sorted, drained.video_aus,
        "tfdt keeps dts absolute across parts"
    );
    assert_eq!(
        media_fetches(&log.lock().unwrap()),
        [
            "https://test/seg000.m4s",
            "https://test/init.mp4",
            "https://test/seg001.m4s",
            "https://test/seg002.m4s"
        ],
        "init fetched once, the segment never whole"
    );
}

#[test]
fn a_variant_names_its_default_audio_rendition() {
    let master = "#EXTM3U\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"dub\",AUTOSELECT=YES,URI=\"dub.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"main\",DEFAULT=YES,AUTOSELECT=YES,URI=\"main.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"auto\",NAME=\"a\",URI=\"plain.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"auto\",NAME=\"b\",AUTOSELECT=YES,URI=\"auto.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"first\",NAME=\"x\",URI=\"x.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"first\",NAME=\"y\",URI=\"y.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"muxed\",NAME=\"m\",DEFAULT=YES\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"beside\",NAME=\"English\",DEFAULT=YES,AUTOSELECT=YES\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"beside\",NAME=\"French\",AUTOSELECT=YES,URI=\"fr.m3u8\"\n\
#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"refused\",NAME=\"r\",DEFAULT=YES,URI=\"ftp://elsewhere/r.m3u8\"\n\
#EXT-X-STREAM-INF:BANDWIDTH=7000,AUDIO=\"refused\"\ntop.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=6000,AUDIO=\"beside\"\nbeside.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=5000,AUDIO=\"aud\"\nhi.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=4000,AUDIO=\"auto\"\nmid.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=3000,AUDIO=\"first\"\nlow.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=2000,AUDIO=\"muxed\"\nlower.m3u8\n\
#EXT-X-STREAM-INF:BANDWIDTH=1000\nlowest.m3u8\n";
    let Ok(ParsedPlaylist::Master(variants)) = media_hls::parse_playlist(master.as_bytes(), BASE)
    else {
        panic!("master playlist expected");
    };
    let named: Vec<(&str, Option<&str>)> = variants
        .iter()
        .map(|v| {
            let audio = v.audio_url.as_ref().map(|rendition| match rendition {
                Ok(url) => url.as_str(),
                Err(_) => "refused",
            });
            (v.url.as_str(), audio)
        })
        .collect();
    assert_eq!(
        named,
        [
            ("https://test/top.m3u8", Some("refused")),
            ("https://test/beside.m3u8", None),
            ("https://test/hi.m3u8", Some("https://test/main.m3u8")),
            ("https://test/mid.m3u8", Some("https://test/auto.m3u8")),
            ("https://test/low.m3u8", Some("https://test/x.m3u8")),
            ("https://test/lower.m3u8", None),
            ("https://test/lowest.m3u8", None),
        ],
        "DEFAULT, else AUTOSELECT, else the first; none when the chosen entry is muxed, \
         even beside one with a URI; a refused URI does not refuse the playlist"
    );
}

/// A live variant of five 2 s segments from sequence 100: its join is
/// three target durations back, at sequence 102 (4 s in).
fn live_variant(with_program_time: bool) -> String {
    let program_time = if with_program_time {
        "#EXT-X-PROGRAM-DATE-TIME:2026-09-29T10:00:00.000Z\n"
    } else {
        ""
    };
    format!(
        "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:100\n{program_time}\
#EXTINF:2.0,\nv100.ts\n#EXTINF:2.0,\nv101.ts\n#EXTINF:2.0,\nv102.ts\n\
#EXTINF:2.0,\nv103.ts\n#EXTINF:2.0,\nv104.ts\n"
    )
}

/// A live audio rendition of six 2 s segments. On its own it would join
/// at its fourth segment (6 s in); in step with the variant, its third.
fn live_rendition(first_sequence: u64, with_program_time: bool) -> String {
    let program_time = if with_program_time {
        "#EXT-X-PROGRAM-DATE-TIME:2026-09-29T10:00:00.000Z\n"
    } else {
        ""
    };
    let mut playlist = format!(
        "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:{first_sequence}\n{program_time}"
    );
    for n in 0..6 {
        playlist.push_str(&format!("#EXTINF:2.0,\na{}.ts\n", first_sequence + n));
    }
    playlist
}

/// Open the variant, then the rendition against its join, pull one event
/// so the rendition fetches its first segment, and report that fetch and
/// the rendition's notes.
fn rendition_join(variant: &str, rendition: &str, rendition_first: u64) -> (String, Vec<String>) {
    let (variant_fetcher, _) = MockFetcher::new(BASE, vec![]);
    let variant = open(variant, variant_fetcher).expect("variant opens");
    let anchor = variant.join_anchor();
    assert!(anchor.is_some(), "a live variant has a join");

    let audio_url = "https://test/audio.m3u8";
    let (fetcher, log) = MockFetcher::new(audio_url, vec![]);
    let dir = fixture_dir("ts");
    let fetcher = (0..6).fold(fetcher, |fetcher, n| {
        fetcher.with_file(
            &format!("https://test/a{}.ts", rendition_first + n),
            dir.join(format!("seg00{}.ts", n % 3)),
        )
    });
    let mut demuxer = HlsDemuxer::open_rendition(
        audio_url,
        rendition.as_bytes().to_vec(),
        Box::new(fetcher),
        DemuxLimits::default(),
        Generation(0),
        anchor,
    )
    .expect("rendition opens");
    demuxer.next_event().expect("first event");
    let first = media_fetches(&log.lock().unwrap())
        .first()
        .cloned()
        .expect("a segment was fetched");
    (first, demuxer.take_notes())
}

#[test]
fn a_rendition_joins_the_variant_by_program_time() {
    // Numbered from 500: only the program time lines them up.
    let (first, notes) = rendition_join(&live_variant(true), &live_rendition(500, true), 500);
    assert_eq!(first, "https://test/a502.ts", "{notes:?}");
    assert!(
        notes
            .iter()
            .any(|n| n.contains("aligned with the variant by program time")),
        "{notes:?}"
    );
}

#[test]
fn a_rendition_without_program_time_joins_by_sequence_number() {
    let (first, notes) = rendition_join(&live_variant(false), &live_rendition(100, false), 100);
    assert_eq!(first, "https://test/a102.ts", "{notes:?}");
    assert!(
        notes
            .iter()
            .any(|n| n.contains("aligned with the variant by sequence number")),
        "{notes:?}"
    );
}

#[test]
fn a_rendition_on_another_program_clock_joins_by_sequence_number() {
    // An hour behind the variant's clock: every point is before the
    // anchor, but none within a target duration of it.
    let rendition = live_rendition(100, true).replace("T10:00:00", "T09:00:00");
    let (first, notes) = rendition_join(&live_variant(true), &rendition, 100);
    assert_eq!(first, "https://test/a102.ts", "{notes:?}");
    assert!(
        notes
            .iter()
            .any(|n| n.contains("aligned with the variant by sequence number")),
        "{notes:?}"
    );
}

#[test]
fn a_rendition_that_lines_up_with_nothing_joins_on_its_own() {
    let (first, notes) = rendition_join(&live_variant(false), &live_rendition(500, false), 500);
    assert_eq!(
        first, "https://test/a503.ts",
        "three target durations back: {notes:?}"
    );
    assert!(
        notes.iter().any(|n| n.contains("nothing lines up")),
        "{notes:?}"
    );
}

/// Segment 0 is complete but has lost its first part: its listed
/// independent part starts 4 s in, not 2 s. With 8 s on the list and a
/// 5 s hold-back the join belongs at the segment's start (0 s), since the
/// part (4 s) is past the 3 s target.
#[test]
fn a_complete_segment_missing_leading_parts_joins_by_where_parts_start() {
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=5.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n";
    let segment_zero = "#EXT-X-PART:DURATION=2.0,URI=\"pa.ts\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"pb.ts\",INDEPENDENT=YES\n#EXTINF:6.0,\nseg000.ts\n";
    let playlist =
        format!("{head}{segment_zero}#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\",INDEPENDENT=YES\n");
    let ended = format!(
        "{head}{segment_zero}#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\",INDEPENDENT=YES\n\
#EXTINF:2.0,\nwhole1.ts\n#EXT-X-ENDLIST\n"
    );
    let fetcher = ts_fetcher(vec![ended.into_bytes()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(&playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(
        media_fetches(&log.lock().unwrap()),
        ["https://test/seg000.ts", "https://test/seg001.ts"],
        "joined at the segment's start, not on the part counted from it"
    );
    assert_eq!(drained.video_aus.len(), 120);
}

/// A low-latency lane whose playlist stops advancing is given the same
/// twenty target durations as a plain one, however quick its reloads.
#[test]
fn a_stalled_low_latency_lane_is_given_twenty_target_durations() {
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=1.5\n\
#EXT-X-PART-INF:PART-TARGET=0.5\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-PART:DURATION=0.5,URI=\"seg000.ts\",INDEPENDENT=YES\n";
    let fetcher = ts_fetcher(vec![playlist.as_bytes().to_vec()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(playlist, fetcher).expect("open");
    let error = drain(&mut demuxer)
        .err()
        .expect("the lane is declared dead");
    assert!(error.to_string().contains("stopped advancing"), "{error}");
    // Twenty target durations of 2 s: eighty waits of 0.5 s after the
    // first reload, which goes out at once.
    let waited: Duration = log.lock().unwrap().waits.iter().sum();
    assert_eq!(waited, Duration::from_secs(40));
}

/// A server that holds each blocking reload three target durations and
/// then answers with nothing new: the hold counts towards the twenty
/// target durations, and the lane is judged dead after about forty seconds.
#[test]
fn a_held_blocking_reload_counts_towards_a_stalled_lane() {
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=1.5\n\
#EXT-X-PART-INF:PART-TARGET=0.5\n#EXT-X-MEDIA-SEQUENCE:0\n\
#EXT-X-PART:DURATION=0.5,URI=\"seg000.ts\",INDEPENDENT=YES\n";
    let mut fetcher = ts_fetcher(vec![playlist.as_bytes().to_vec()]);
    fetcher.blocking_hold = Duration::from_secs(6);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(playlist, fetcher).expect("open");
    let error = drain(&mut demuxer)
        .err()
        .expect("the lane is declared dead");
    assert!(error.to_string().contains("stopped advancing"), "{error}");
    let log = log.lock().unwrap();
    let held = log
        .fetched
        .iter()
        .filter(|url| url.contains("_HLS_msn"))
        .count();
    // 6 s held, then 0.5 s waited, each round: 45 s once the seventh
    // ends, the first round to reach forty.
    assert_eq!(held, 7, "{:?}", log.now);
}

/// A live playlist on disk that states CAN-BLOCK-RELOAD: a drive path is
/// not a URL to put directives on.
#[test]
fn a_disk_playlist_is_never_sent_blocking_directives() {
    let path = "C:/media/live.m3u8";
    let playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES\n#EXT-X-MEDIA-SEQUENCE:0\n";
    let ended = format!("{playlist}#EXT-X-ENDLIST\n");
    let (fetcher, log) = MockFetcher::new(path, vec![ended.into_bytes()]);
    let mut demuxer = HlsDemuxer::open(
        path,
        playlist.as_bytes().to_vec(),
        Box::new(fetcher),
        DemuxLimits::default(),
        Generation(0),
    )
    .expect("open");
    drain(&mut demuxer).expect("drain");
    let fetched = log.lock().unwrap().fetched.clone();
    assert!(!fetched.is_empty(), "the playlist was reloaded");
    assert!(
        fetched.iter().all(|url| !url.contains("_HLS_")),
        "{fetched:?}"
    );
}

/// Segment 0 in progress with its first part listed; on the reload it has
/// completed, but its first part has left the list.
#[test]
fn a_reload_that_drops_leading_parts_keeps_the_next_part() {
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n";
    let playlist = format!("{head}#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n");
    let ended = format!(
        "{head}#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg002.ts\"\n#EXTINF:6.0,\nwhole0.ts\n#EXT-X-ENDLIST\n"
    );
    let fetcher = ts_fetcher(vec![ended.into_bytes()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(&playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    assert_eq!(
        media_fetches(&log.lock().unwrap()),
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "placed by time, the cursor stays on the next part"
    );
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.discontinuities, 0);
}

/// After a lost part, the reload lists only the segment's later parts,
/// the first of them not independent: resync passes over it rather than
/// taking the first listed part for a segment start.
#[test]
fn a_resync_does_not_start_on_the_first_listed_part() {
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:8\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n";
    let playlist = format!(
        "{head}#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n\
#EXT-X-PART:DURATION=2.0,URI=\"lost.ts\"\n"
    );
    let ended = format!(
        "{head}#EXT-X-PART:DURATION=2.0,URI=\"dependent.ts\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\",INDEPENDENT=YES\n\
#EXTINF:8.0,\nwhole0.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n"
    );
    let fetcher = ts_fetcher(vec![ended.into_bytes()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(&playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    let fetched = media_fetches(&log.lock().unwrap());
    let served: Vec<&str> = fetched
        .iter()
        .map(String::as_str)
        .filter(|url| !url.ends_with("lost.ts"))
        .collect();
    assert_eq!(
        served,
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "the dependent part is never fetched"
    );
    assert_eq!(drained.video_aus.len(), 180);
}

/// Segment 0 in progress with four 2 s parts: `seg000.ts`, a part named
/// by `second` (never served), `dependent.ts` (not independent, never
/// served) and `seg001.ts` (independent). The reload completes segment
/// 0, adds segment 1 whole and ends the stream.
fn ll_with_a_broken_second_part(second: &str) -> (String, String) {
    let head = "#EXTM3U\n#EXT-X-TARGETDURATION:8\n\
#EXT-X-SERVER-CONTROL:CAN-BLOCK-RELOAD=YES,PART-HOLD-BACK=4.0\n\
#EXT-X-PART-INF:PART-TARGET=2.0\n#EXT-X-MEDIA-SEQUENCE:0\n";
    let parts = format!(
        "#EXT-X-PART:DURATION=2.0,URI=\"seg000.ts\",INDEPENDENT=YES\n{second}\n\
#EXT-X-PART:DURATION=2.0,URI=\"dependent.ts\"\n\
#EXT-X-PART:DURATION=2.0,URI=\"seg001.ts\",INDEPENDENT=YES\n"
    );
    (
        format!("{head}{parts}"),
        format!("{head}{parts}#EXTINF:8.0,\nwhole0.ts\n#EXTINF:2.0,\nseg002.ts\n#EXT-X-ENDLIST\n"),
    )
}

fn play_past_a_broken_part(second: &str) -> (Vec<String>, Drained, Vec<String>) {
    let (playlist, ended) = ll_with_a_broken_second_part(second);
    let fetcher = ts_fetcher(vec![ended.into_bytes()]);
    let log = Arc::clone(&fetcher.log);
    let mut demuxer = open(&playlist, fetcher).expect("open");
    let drained = drain(&mut demuxer).expect("drain");
    let fetched = media_fetches(&log.lock().unwrap());
    (fetched, drained, demuxer.take_notes())
}

#[test]
fn a_lost_part_resumes_at_the_next_independent_part() {
    let (fetched, drained, notes) =
        play_past_a_broken_part("#EXT-X-PART:DURATION=2.0,URI=\"lost.ts\"");
    let served: Vec<&str> = fetched
        .iter()
        .map(String::as_str)
        .filter(|url| !url.ends_with("lost.ts"))
        .collect();
    assert_eq!(
        served,
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "the dependent part is passed over and the rest of the segment kept"
    );
    assert_eq!(drained.video_aus.len(), 180);
    assert_eq!(drained.discontinuities, 1, "the loss is a splice");
    assert!(
        notes
            .iter()
            .any(|n| n.contains("part 1 of sequence 0 unfetchable")),
        "{notes:?}"
    );
}

#[test]
fn a_gap_part_resumes_at_the_next_independent_part() {
    let (fetched, drained, notes) =
        play_past_a_broken_part("#EXT-X-PART:DURATION=2.0,URI=\"gap.ts\",GAP=YES");
    assert_eq!(
        fetched,
        [
            "https://test/seg000.ts",
            "https://test/seg001.ts",
            "https://test/seg002.ts"
        ],
        "neither the gap nor the dependent part is fetched"
    );
    assert_eq!(drained.video_aus.len(), 180);
    assert!(
        notes
            .iter()
            .any(|n| n.contains("part 1 of sequence 0 is a gap")),
        "{notes:?}"
    );
}
