//! What an HLS playlist may reach, end to end through the engine: a
//! playlist served over HTTP names a real, readable, playable local file
//! and must not get it. The fetcher's own tests cover the refusal; these
//! cover the wiring that decides which fetcher the session is given,
//! which a unit test cannot see.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::sync::Arc;
use std::sync::atomic::Ordering;
use std::thread;
use std::time::{Duration, Instant};

use media_engine::{ErrorCategory, OpenRequest, Session, SourceLiveness, State};

/// Serve `playlist` at `/index.m3u8` and **nothing else**, 200 with a
/// length, ranges ignored. Every other path is a 404 on purpose: serving
/// the playlist for any path would answer a segment URI that resolved
/// back to this origin with playlist bytes, the session would fail at
/// demux, and a test would pass without asserting anything about where the
/// URI was allowed to go.
fn spawn_playlist_server(playlist: String) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let body = Arc::new(playlist.into_bytes());
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { break };
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut line = String::new();
                let mut wants_playlist = false;
                let mut request_line = true;
                while reader.read_line(&mut line).unwrap_or(0) > 0 {
                    if line.trim_end().is_empty() {
                        break;
                    }
                    if request_line {
                        wants_playlist = line.contains(" /index.m3u8 ");
                        request_line = false;
                    }
                    line.clear();
                }
                if !wants_playlist {
                    let _ = stream.write_all(
                        b"HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n",
                    );
                    return;
                }
                let head = format!(
                    "HTTP/1.1 200 OK\r\nContent-Type: application/vnd.apple.mpegurl\r\n\
                     Content-Length: {}\r\nConnection: close\r\n\r\n",
                    body.len()
                );
                let _ = stream.write_all(head.as_bytes());
                let _ = stream.write_all(&body);
            });
        }
    });
    format!("http://127.0.0.1:{port}/index.m3u8")
}

/// A copy of the TS fixture somewhere off the playlist's origin. It is
/// real, readable and playable, so a session that reaches it plays and the
/// test can tell "refused" from "failed for some other reason".
///
/// Removed on drop, because a failed assertion panics past any cleanup
/// written after it, and the directory is named for the process, so every
/// failing run would leave one behind.
struct Planted(std::path::PathBuf);

impl Drop for Planted {
    fn drop(&mut self) {
        let dir = self.0.parent().map(std::path::Path::to_path_buf);
        let _ = std::fs::remove_file(&self.0);
        if let Some(dir) = dir {
            // Refuses while it holds anything, so a sibling test's planted
            // file is safe.
            let _ = std::fs::remove_dir(dir);
        }
    }
}

fn planted_fixture(name: &str) -> Planted {
    let fixture = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures/h264-aac-640x360-30fps.ts");
    let dir = std::env::temp_dir().join(format!("bm-hls-origin-{}", std::process::id()));
    std::fs::create_dir_all(&dir).expect("scratch dir");
    let planted = dir.join(name);
    std::fs::copy(&fixture, &planted).expect("plant the fixture");
    Planted(planted)
}

/// Run a one-segment VOD playlist naming `segment` and report the state
/// the session settles in, how many frames it decoded, and which error
/// category it settled under.
fn play_playlist_naming(segment: &str) -> (u32, u64, u32) {
    let playlist = format!(
        "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:7\n#EXTINF:6.0,\n{segment}\n#EXT-X-ENDLIST\n"
    );
    let mut request = OpenRequest::new(spawn_playlist_server(playlist));
    // The address gate is not under test. Leave it open so a pass cannot
    // be the gate refusing 127.0.0.1 by accident.
    request.allow_local_addresses = true;
    // Stated, so the source is always the ranged on-demand one rather than
    // liveness inference deciding from the server's answer.
    request.liveness = SourceLiveness::Vod;
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut state = shared.state.load(Ordering::Relaxed);
    while start.elapsed() < Duration::from_secs(30) {
        state = shared.state.load(Ordering::Relaxed);
        if state == State::Error as u32
            || state == State::Playing as u32
            || state == State::Ended as u32
        {
            break;
        }
        thread::sleep(Duration::from_millis(50));
    }
    // A moment for any frame that was going to arrive to arrive.
    thread::sleep(Duration::from_millis(250));
    let decoded = shared.frames_decoded.load(Ordering::Relaxed);
    let category = shared.last_error_category.load(Ordering::Relaxed);
    session.close();
    (state, decoded, category)
}

/// A `file:` URL for the planted fixture, in the spelling the host uses.
///
/// The temp directory sits under a user profile whose name this test does
/// not choose. An unescaped space would make the URL name something other
/// than the planted file, and the test would then see a refusal for the
/// wrong reason. `%` is escaped first, or it would re-encode the escape the
/// space just produced.
fn file_url(path: &std::path::Path) -> String {
    let text = path.to_str().expect("scratch path is UTF-8");
    let escaped = text.replace('%', "%25").replace(' ', "%20");
    if cfg!(windows) {
        format!("file:///{}", escaped.replace('\\', "/"))
    } else {
        format!("file://{escaped}")
    }
}

/// Every spelling names the *same planted, playable* copy of the TS
/// fixture, so a pass means the read was refused, not that nothing was
/// found.
///
/// Which spellings can name a local file depends on the host, so they are
/// listed per platform. A case that cannot fail on the running host would
/// read as coverage it does not give.
///
/// On Windows a bare path is drive-absolute, and URL joining turns it into
/// a one-character scheme; `c://…` has the `://` form of an absolute URI;
/// `c:/…` is the same drive path again. All three reach the filesystem if
/// nothing stops them.
///
/// Off Windows, a POSIX absolute path joined onto an http base is a
/// **root-relative URL**: `/tmp/x` becomes `http://origin/tmp/x`, which
/// names no local file. Only the `file:` URL names a local file there, so
/// it is the only spelling listed.
#[test]
fn a_network_playlist_cannot_play_a_local_file() {
    let planted = planted_fixture("planted.ts");
    let planted = &planted.0;
    #[cfg(windows)]
    let path = planted.to_str().expect("scratch path is UTF-8").to_owned();

    #[cfg(windows)]
    let spellings = vec![
        path.clone(),
        file_url(planted),
        path.replacen(':', "://", 1),
        path.replacen(":\\", ":/", 1),
    ];
    #[cfg(not(windows))]
    let spellings = vec![file_url(planted)];

    for segment in &spellings {
        let (state, decoded, category) = play_playlist_naming(segment);
        assert_eq!(
            state,
            State::Error as u32,
            "{segment:?} settled in state {state} rather than refusing"
        );
        assert_eq!(
            decoded, 0,
            "{segment:?} decoded {decoded} frames off the local file"
        );
        // The refusal must come from the source, not the decoder. On a host
        // with no H.264 decoder (Linux), a session that *did* read the
        // planted file would also settle in Error with nothing decoded, so
        // state alone cannot tell the two apart.
        assert_ne!(
            category,
            ErrorCategory::Decode as u32,
            "{segment:?} was refused by the decoder, so the read was not prevented"
        );
    }
}

/// The disk fetcher's wiring, on every host. The confinement in
/// `ResourceFetcher::local` is platform-independent, so it is tested here
/// without needing decoded output.
///
/// Media in the Bank proves the fetcher was asked for the playlist's own
/// segment and delivered it. So does a decoder refusing its tracks, which
/// is how a host without decoders ends this session: the tracks it refuses
/// were announced from the segment's bytes. A fetch the fetcher refused
/// fails as I/O or configuration instead.
#[test]
fn a_disk_playlist_reaches_its_own_segments_without_a_decoder() {
    let playlist =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/hls/ts/index.m3u8");
    let mut request =
        OpenRequest::new(playlist.to_str().expect("fixture path is UTF-8").to_owned());
    request.liveness = SourceLiveness::Vod;
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut banked = 0;
    let mut category = ErrorCategory::None as u32;
    while start.elapsed() < Duration::from_secs(20) {
        banked = shared.banked_us.load(Ordering::Relaxed);
        category = shared.last_error_category.load(Ordering::Relaxed);
        if banked > 0 || category != ErrorCategory::None as u32 {
            break;
        }
        thread::sleep(Duration::from_millis(50));
    }
    session.close();
    assert!(
        banked > 0 || category == ErrorCategory::Decode as u32,
        "nothing from the playlist's own segment reached the Bank \
         (error category {category})"
    );
}

/// A playlist opened from disk plays the segments beside it. The refusal
/// above depends on where the playlist came from; it is not a ban on local
/// media.
///
/// Windows only: the Linux backend has no H.264 or AAC decoder.
#[cfg(windows)]
#[test]
fn a_disk_playlist_still_plays_its_own_segments() {
    let playlist =
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../fixtures/hls/ts/index.m3u8");
    let mut request =
        OpenRequest::new(playlist.to_str().expect("fixture path is UTF-8").to_owned());
    request.liveness = SourceLiveness::Vod;
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut played = false;
    while start.elapsed() < Duration::from_secs(30) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "the fixture lane must still play"
        );
        if state == State::Playing as u32 && shared.frames_decoded.load(Ordering::Relaxed) > 0 {
            played = true;
            break;
        }
        thread::sleep(Duration::from_millis(50));
    }
    session.close();
    assert!(
        played,
        "never decoded a frame from the local fixture playlist"
    );
}
