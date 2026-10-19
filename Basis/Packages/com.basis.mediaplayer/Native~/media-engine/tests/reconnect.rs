//! Live reconnect end to end: a live TS server kills the first connection
//! mid-stream, and the session must rebuild the transport (the banked depth
//! playing through the outage), rejoin at the live edge mid-GOP and keep
//! presenting without entering Error.

#![cfg(windows)]

use std::io::{BufRead, BufReader, Write};
use std::net::TcpListener;
use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use media_engine::{OpenRequest, Session, SourceLiveness, State};

/// Serve the fixture at ~1x from a shared live origin. The first
/// connection is dropped after `kill_after`; later connections join at the
/// current live position, like any real edge.
fn spawn_live_server(bytes: Vec<u8>, rate: u64, kill_after: Duration) -> (String, Arc<AtomicU32>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let connections = Arc::new(AtomicU32::new(0));
    let seen = Arc::clone(&connections);
    let body = Arc::new(bytes);
    thread::spawn(move || {
        let origin = Instant::now();
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { break };
            let n = seen.fetch_add(1, Ordering::SeqCst) + 1;
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut line = String::new();
                while reader.read_line(&mut line).unwrap_or(0) > 0 {
                    if line.trim_end().is_empty() || line == "\r\n" {
                        break;
                    }
                    line.clear();
                }
                if stream
                    .write_all(b"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n")
                    .is_err()
                {
                    return;
                }
                let deadline = (n == 1).then(|| Instant::now() + kill_after);
                // Join at the live edge, aligned down to a packet.
                let mut pos =
                    (origin.elapsed().as_micros() as u64 * rate / 1_000_000 / 188 * 188) as usize;
                loop {
                    if let Some(deadline) = deadline
                        && Instant::now() >= deadline
                    {
                        return; // drop the connection
                    }
                    let live = (origin.elapsed().as_micros() as u64 * rate / 1_000_000) as usize;
                    let due = live.min(body.len());
                    if pos < due {
                        if stream.write_all(&body[pos..due]).is_err() {
                            return;
                        }
                        pos = due;
                    }
                    if pos >= body.len() {
                        return; // stream over
                    }
                    thread::sleep(Duration::from_millis(20));
                }
            });
        }
    });
    (format!("http://127.0.0.1:{port}/live"), connections)
}

#[test]
fn live_session_survives_a_dropped_connection() {
    let bytes = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures/h264-aac-320x180-30s.ts"),
    )
    .expect("fixture readable");
    // ~30 s of media; rate from size/duration.
    let rate = bytes.len() as u64 / 30;
    let (url, connections) = spawn_live_server(bytes, rate, Duration::from_secs(5));

    let mut request = OpenRequest::new(url);
    request.allow_local_addresses = true;
    request.liveness = SourceLiveness::Live;
    // A shallow bank keeps the run short; this tests the rebuild, not
    // riding out the gap.
    request.buffer_depth_ms = Some(1000);
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut decoded_at_drop = None;
    let mut recovered = false;
    while start.elapsed() < Duration::from_secs(20) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "session must reconnect, not error (code {})",
            shared.last_error.load(Ordering::Relaxed)
        );
        if state == State::Ended as u32 {
            break;
        }
        let decoded = shared.frames_decoded.load(Ordering::Relaxed);
        if connections.load(Ordering::SeqCst) >= 2 {
            let baseline = *decoded_at_drop.get_or_insert(decoded);
            // Recovered means decode moved a whole GOP past the pre-drop
            // point, so it cannot be pool residue.
            if decoded > baseline + 60 {
                recovered = true;
                break;
            }
        }
        thread::sleep(Duration::from_millis(100));
    }

    assert!(
        connections.load(Ordering::SeqCst) >= 2,
        "the engine never reconnected"
    );
    assert!(recovered, "decode did not resume after the reconnect");
    let events = session.diag().take_events();
    assert!(
        events
            .iter()
            .any(|e| matches!(e.code, media_diag::EventCode::Reconnect)),
        "reconnect must narrate itself in the diagnostics"
    );
    session.close();
}

/// Auto is the default liveness in the engine and on the managed
/// component, so a plain live URL settles it by probing, and that probe is
/// a GET whose 200 *is* the stream. A second connection would cost a
/// handshake, rejoin the edge later than the probe left off, and be
/// refused by an origin serving one client at a time. The live path adopts
/// the probe's body, so the whole session is one connection.
#[test]
fn an_auto_live_session_costs_one_connection() {
    let bytes = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures/h264-aac-320x180-30s.ts"),
    )
    .expect("fixture readable");
    let rate = bytes.len() as u64 / 30;
    // Never killed: this tests the join, not the rebuild.
    let (url, connections) = spawn_live_server(bytes, rate, Duration::from_secs(3600));

    let mut request = OpenRequest::new(url);
    request.allow_local_addresses = true;
    // Left at Auto deliberately: that is the path under test.
    assert_eq!(request.liveness, SourceLiveness::Auto);
    request.buffer_depth_ms = Some(1000);
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut playing = false;
    while start.elapsed() < Duration::from_secs(20) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "the Auto lane must play, not error (code {})",
            shared.last_error.load(Ordering::Relaxed)
        );
        // A whole GOP past the join, so this cannot be the pool draining
        // what the probe happened to have in hand.
        if state == State::Playing as u32 && shared.frames_decoded.load(Ordering::Relaxed) > 60 {
            playing = true;
            break;
        }
        thread::sleep(Duration::from_millis(100));
    }
    assert!(playing, "the Auto lane never reached playback");
    assert_eq!(
        connections.load(Ordering::SeqCst),
        1,
        "the liveness probe reopened instead of handing its body over"
    );

    // Without this the test proves nothing: a source read as on-demand
    // would also take one connection and never touch the handover.
    let events = session.diag().take_events();
    assert!(
        events
            .iter()
            .any(|e| matches!(e.code, media_diag::EventCode::CapabilityProbe)),
        "the session must have inferred Live, or this counted the on-demand lane"
    );
    session.close();
}

/// The probe settles Auto as live and hands its body to the live path,
/// which must run the Bank as live, not as the request's on-demand
/// default. The observable part is the engine's own liveness, which
/// refuses a pause. A session that pauses here was running a live edge as
/// on-demand.
#[test]
fn an_auto_inferred_live_session_is_live_to_the_engine() {
    let bytes = std::fs::read(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../fixtures/h264-aac-320x180-30s.ts"),
    )
    .expect("fixture readable");
    let rate = bytes.len() as u64 / 30;
    let (url, _connections) = spawn_live_server(bytes, rate, Duration::from_secs(3600));

    let mut request = OpenRequest::new(url);
    request.allow_local_addresses = true;
    assert_eq!(request.liveness, SourceLiveness::Auto);
    request.buffer_depth_ms = Some(1000);
    let mut session = Session::open(request);
    let shared = session.shared().clone();

    let start = Instant::now();
    let mut playing = false;
    while start.elapsed() < Duration::from_secs(20) {
        let state = shared.state.load(Ordering::Relaxed);
        assert_ne!(
            state,
            State::Error as u32,
            "the Auto lane must play, not error (code {})",
            shared.last_error.load(Ordering::Relaxed)
        );
        if state == State::Playing as u32 && shared.frames_decoded.load(Ordering::Relaxed) > 60 {
            playing = true;
            break;
        }
        thread::sleep(Duration::from_millis(100));
    }
    assert!(playing, "the Auto lane never reached playback");
    let events = session.diag().take_events();
    assert!(
        events
            .iter()
            .any(|e| matches!(e.code, media_diag::EventCode::CapabilityProbe)),
        "the session must have inferred Live, or this counted the on-demand lane"
    );

    session.pause();
    assert_eq!(
        shared.state.load(Ordering::Relaxed),
        State::Playing as u32,
        "an Auto-inferred live session paused: the live lane ran the Bank as on-demand"
    );
    session.close();
}
