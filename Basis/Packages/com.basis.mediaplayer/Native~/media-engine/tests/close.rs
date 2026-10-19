//! Closing a session. A close cancels whatever read is in flight, and the
//! read that returns cancelled is the close itself, not a failure.

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::sync::atomic::Ordering;
use std::thread;
use std::time::{Duration, Instant};

use media_diag::{EventCode, Stage};
use media_engine::{OpenRequest, Session, State};

/// Answers one request with a header promising a whole file, sends the
/// start of it, then stalls, so a read wanting more blocks until something
/// cancels it.
fn spawn_stalled_server(prefix: Vec<u8>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        let Ok((mut stream, _)) = listener.accept() else {
            return;
        };
        let mut reader = BufReader::new(stream.try_clone().expect("clone"));
        let mut line = String::new();
        while reader.read_line(&mut line).unwrap_or(0) > 0 {
            if line.trim_end().is_empty() {
                break;
            }
            line.clear();
        }
        let _ = stream.write_all(
            b"HTTP/1.1 200 OK\r\nContent-Length: 10000000\r\nContent-Type: video/mp4\r\n\r\n",
        );
        let _ = stream.write_all(&prefix);
        // Held open until the client goes.
        let _ = reader.read_line(&mut line);
    });
    format!("http://127.0.0.1:{port}/stall.mp4")
}

#[test]
fn closing_mid_read_is_not_an_error() {
    // The file type box and the start of the movie box: the demuxer takes
    // these and asks for the rest.
    let fixture = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures/h264-aac-640x360-30fps.mp4"),
    )
    .expect("fixture readable");
    let prefix = fixture[..48].to_vec();
    let mut request = OpenRequest::new(spawn_stalled_server(prefix.clone()));
    request.allow_local_addresses = true;
    let mut session = Session::open(request);
    let shared = session.shared().clone();
    let diag = session.diag().clone();

    let taken = || diag.stage(Stage::Source).out_bytes.load(Ordering::Relaxed);
    let end = Instant::now() + Duration::from_secs(10);
    while taken() < prefix.len() as u64 && Instant::now() < end {
        thread::sleep(Duration::from_millis(10));
    }
    assert_eq!(
        taken(),
        prefix.len() as u64,
        "the session did not read the body it was sent"
    );
    // Everything sent has been read and the session is still opening, so
    // it is blocked reading for the rest.
    thread::sleep(Duration::from_millis(100));
    assert_eq!(shared.state.load(Ordering::Relaxed), State::Opening as u32);

    session.close();

    assert_ne!(
        shared.state.load(Ordering::Relaxed),
        State::Error as u32,
        "a close reported itself as a failure"
    );
    assert_eq!(shared.last_error.load(Ordering::Relaxed), 0);
    assert!(
        !diag
            .take_events()
            .iter()
            .any(|e| e.code == EventCode::Error),
        "a close logged an Error event"
    );
}
