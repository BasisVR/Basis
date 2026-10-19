//! An RTSP open against a server that accepts the connection and never
//! answers. The thread running the open is one a session close joins from
//! the client's main thread, so the open has to end when the session is
//! cancelled, and on its own within a bounded time when nobody cancels it.

use std::io::Read;
use std::net::{SocketAddr, TcpListener};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::time::{Duration, Instant};

use media_clock::Generation;
use media_rtsp::RtspDemuxer;

/// Accepts connections and reads them to the end without ever replying.
fn silent_server() -> SocketAddr {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let addr = listener.local_addr().expect("addr");
    std::thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { return };
            std::thread::spawn(move || {
                let mut sink = [0u8; 1024];
                while stream.read(&mut sink).unwrap_or(0) > 0 {}
            });
        }
    });
    addr
}

/// Opens `scheme://` against `server` on a thread of its own and reports
/// how the open ended and when, or `None` if it had not ended by `wait`.
fn open(
    scheme: &str,
    server: SocketAddr,
    cancel: Arc<AtomicBool>,
    wait: Duration,
) -> Option<(Result<(), String>, Duration)> {
    let runtime = tokio::runtime::Builder::new_multi_thread()
        .worker_threads(2)
        .enable_all()
        .build()
        .expect("runtime");
    let handle = runtime.handle().clone();
    let url = format!("{scheme}://{server}/stream");
    let (done_tx, done_rx) = mpsc::channel();
    let started = Instant::now();
    std::thread::spawn(move || {
        let result = RtspDemuxer::open(
            &url,
            vec![server],
            Generation(0),
            handle,
            Box::new(move || cancel.load(Ordering::Relaxed)),
            Arc::new(|_| true),
        );
        let _ = done_tx.send((
            result.map(|_| ()).map_err(|e| e.to_string()),
            started.elapsed(),
        ));
    });
    let outcome = done_rx.recv_timeout(wait).ok();
    // A still-blocked open keeps its runtime; the test is failing anyway.
    if outcome.is_none() {
        std::mem::forget(runtime);
    }
    outcome
}

#[test]
fn a_cancel_during_an_unanswered_describe_ends_the_open_within_a_second() {
    let server = silent_server();
    let cancel = Arc::new(AtomicBool::new(false));
    let trip = Arc::clone(&cancel);
    std::thread::spawn(move || {
        std::thread::sleep(Duration::from_millis(300));
        trip.store(true, Ordering::Relaxed);
    });
    // `rtsp://` tries UDP first, so this waits in the UDP lane's DESCRIBE.
    let (result, elapsed) = open("rtsp", server, cancel, Duration::from_secs(5))
        .expect("the open still blocked 5 s after it was cancelled");
    assert!(result.is_err(), "a cancelled open reported success");
    assert!(
        elapsed < Duration::from_millis(1300),
        "the open took {elapsed:?} to see a cancel made at 300 ms"
    );
}

#[test]
fn an_open_nobody_answers_ends_on_its_own_within_30_s() {
    let server = silent_server();
    let (result, elapsed) = open(
        "rtspt",
        server,
        Arc::new(AtomicBool::new(false)),
        Duration::from_secs(40),
    )
    .expect("the open still blocked after 40 s with no answer");
    let err = result.expect_err("an unanswered open reported success");
    assert!(
        elapsed <= Duration::from_secs(30),
        "took {elapsed:?}: {err}"
    );
}
