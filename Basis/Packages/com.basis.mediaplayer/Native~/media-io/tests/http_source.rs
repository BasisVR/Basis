//! HttpSource against an in-process HTTP/1.1 server: range serving,
//! sequential fallback, redirect re-vetting, and the address gate. No
//! external network anywhere.

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::{Arc, Mutex, mpsc};
use std::thread;
use std::time::{Duration, Instant};

use media_demux::{ByteSource, DemuxLimits, Demuxer, Mp4Demuxer, StreamEvent};
use media_io::{AllowAllGate, CancelToken, HttpSource, IoErrorKind, IoLimits, PublicAddressGate};

/// How long a cancelled request may take to come back. The request
/// timeout it stands against is 20 s, so this is loose enough not to
/// flake on a host running the rest of the suite alongside it.
const CANCEL_WINDOW: Duration = Duration::from_secs(2);

#[derive(Clone, Copy, PartialEq)]
enum Mode {
    Ranges,
    /// Ignores Range headers: always a 200 with the full body.
    Sequential,
    /// Declines the range request but advertises `Accept-Ranges: bytes`,
    /// which is the second arm of "will this server serve ranges".
    SequentialAdvertised,
    /// A live edge: 200, no length of any kind, closed when done.
    Unbounded,
}

/// Serve `body` at `/media`, with `/hop1` -> `/hop2` -> `/media` redirects
/// and `/loop` redirecting to itself. Returns the base URL.
fn spawn_server(body: Vec<u8>, mode: Mode) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let body = Arc::new(body);
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let body = Arc::clone(&body);
            thread::spawn(move || serve_connection(stream, &body, mode));
        }
    });
    format!("http://127.0.0.1:{port}")
}

fn serve_connection(stream: TcpStream, body: &[u8], mode: Mode) {
    let mut reader = BufReader::new(stream.try_clone().expect("clone"));
    let mut stream = stream;
    loop {
        let mut request_line = String::new();
        if reader.read_line(&mut request_line).unwrap_or(0) == 0 {
            return;
        }
        let path = request_line
            .split_whitespace()
            .nth(1)
            .unwrap_or("/")
            .to_string();
        let mut range: Option<(u64, Option<u64>)> = None;
        loop {
            let mut line = String::new();
            if reader.read_line(&mut line).unwrap_or(0) == 0 {
                return;
            }
            let line = line.trim_end();
            if line.is_empty() {
                break;
            }
            if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=") {
                let mut parts = spec.split('-');
                let start = parts.next().and_then(|s| s.parse().ok());
                let end = parts.next().and_then(|s| s.parse().ok());
                if let Some(start) = start {
                    range = Some((start, end));
                }
            }
        }

        let response = match path.as_str() {
            "/hop1" => redirect("/hop2"),
            "/hop2" => redirect("/media"),
            "/loop" => redirect("/loop"),
            _ => match (mode, range) {
                (Mode::Ranges, Some((start, end))) if start <= body.len() as u64 => {
                    let start = start as usize;
                    // Inclusive range end, clamped to the body.
                    let stop = end
                        .map(|e| (e as usize + 1).min(body.len()))
                        .unwrap_or(body.len());
                    let slice = &body[start..stop];
                    let mut r = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {}-{}/{}\r\nContent-Length: {}\r\n\r\n",
                        start,
                        stop.saturating_sub(1),
                        body.len(),
                        slice.len()
                    )
                    .into_bytes();
                    r.extend_from_slice(slice);
                    r
                }
                (Mode::Unbounded, _) => {
                    let mut r = b"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n".to_vec();
                    r.extend_from_slice(body);
                    let _ = stream.write_all(&r);
                    return;
                }
                _ => {
                    let accept = if mode == Mode::SequentialAdvertised {
                        "Accept-Ranges: bytes\r\n"
                    } else {
                        ""
                    };
                    let mut r = format!(
                        "HTTP/1.1 200 OK\r\n{accept}Content-Length: {}\r\n\r\n",
                        body.len()
                    )
                    .into_bytes();
                    r.extend_from_slice(body);
                    r
                }
            },
        };
        if stream.write_all(&response).is_err() {
            return;
        }
    }
}

/// A server that answers its first ranged GET with `serve` bytes of a body
/// it never finishes, or with `serve` as `None` never answers at all.
/// Every later request is taken and never answered, which is what a link
/// that has gone quiet looks like to a client that asks again. All of it
/// leaves the client waiting on a socket nothing will write to again,
/// which only the read timeout and the cancel token end.
fn start_stalling_server(total: u64, serve: Option<usize>) -> (String, mpsc::Receiver<()>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let (tx, rx) = mpsc::channel();
    thread::spawn(move || {
        // Held so the connections stay open rather than being closed by
        // the drop, which the client would read as a finished body.
        let mut held = Vec::new();
        let mut serve = serve;
        for stream in listener.incoming() {
            let Ok(mut stream) = stream else { break };
            let mut reader = BufReader::new(stream.try_clone().expect("clone"));
            loop {
                let mut line = String::new();
                if reader.read_line(&mut line).unwrap_or(0) == 0 {
                    break;
                }
                if line.trim_end().is_empty() {
                    break;
                }
            }
            if let Some(n) = serve.take() {
                let head = format!(
                    "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-{}/{total}\r\n\
                     Content-Length: {total}\r\n\r\n",
                    // A zero total is not a shape any row wants, but it
                    // would underflow here and stop the server thread
                    // accepting, which reads as a hung client.
                    total.saturating_sub(1)
                );
                let _ = stream.write_all(head.as_bytes());
                let _ = stream.write_all(&vec![0u8; n]);
                let _ = stream.flush();
            }
            let _ = tx.send(());
            held.push(stream);
        }
    });
    (format!("http://127.0.0.1:{port}"), rx)
}

/// A server that accepts exactly one connection, answers it with the
/// whole body however it was asked for, and then stops listening so a
/// second connection is refused: the shape of a feeder that serves one
/// client at a time, and of any origin that answers a ranged GET with a
/// plain 200.
fn spawn_single_slot_server(body: Vec<u8>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        let Ok((stream, _)) = listener.accept() else {
            return;
        };
        drop(listener);
        let mut reader = BufReader::new(stream.try_clone().expect("clone"));
        let mut stream = stream;
        loop {
            let mut line = String::new();
            if reader.read_line(&mut line).unwrap_or(0) == 0 {
                return;
            }
            if line.trim_end().is_empty() {
                break;
            }
        }
        let mut response =
            format!("HTTP/1.1 200 OK\r\nContent-Length: {}\r\n\r\n", body.len()).into_bytes();
        response.extend_from_slice(&body);
        let _ = stream.write_all(&response);
        let _ = stream.flush();
    });
    format!("http://127.0.0.1:{port}")
}

fn redirect(to: &str) -> Vec<u8> {
    format!("HTTP/1.1 302 Found\r\nLocation: {to}\r\nContent-Length: 0\r\n\r\n").into_bytes()
}

fn fixture(name: &str) -> Vec<u8> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name);
    std::fs::read(path).expect("fixture readable")
}

fn open(url: &str) -> Result<HttpSource, media_io::IoError> {
    HttpSource::open(
        url,
        IoLimits::default(),
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
}

fn count_aus(source: HttpSource) -> (u32, u32) {
    let mut demux = Mp4Demuxer::open(
        Box::new(source),
        DemuxLimits::default(),
        media_clock::Generation(1),
    )
    .expect("demux opens over http");
    let (mut video, mut audio) = (0u32, 0u32);
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Au(au) => {
                if au.data.starts_with(&[0, 0, 0, 1]) {
                    video += 1;
                } else {
                    audio += 1;
                }
            }
            StreamEvent::Eos(_) => return (video, audio),
            _ => {}
        }
    }
}

#[test]
fn range_server_demuxes_trailing_moov() {
    let base = spawn_server(fixture("h264-aac-moov-trailing.mp4"), Mode::Ranges);
    let mut source = open(&format!("{base}/media")).expect("open");
    assert_eq!(
        source.size().expect("size"),
        Some(fixture("h264-aac-moov-trailing.mp4").len() as u64)
    );
    assert_eq!(count_aus(source), (180, 283));
}

#[test]
fn small_chunks_span_the_whole_file() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.mp4"), Mode::Ranges);
    let source = HttpSource::open(
        &format!("{base}/media"),
        IoLimits {
            // Force many chunk-boundary reopens across the ~670 KiB body.
            chunk_bytes: 64 * 1024,
            ..IoLimits::default()
        },
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .expect("open");
    assert_eq!(count_aus(source), (180, 283));
}

#[test]
fn sequential_server_demuxes_faststart() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.mp4"), Mode::Sequential);
    let source = open(&format!("{base}/media")).expect("open");
    assert_eq!(count_aus(source), (180, 283));
}

/// An origin that answers the opening ranged probe with a 200 has
/// already handed over the whole entity from byte 0, so that response is
/// the sequential stream. Opening a second one would cost a second
/// connection, and the origins that answer this way are the ones least
/// able to give it: every live edge answers 200, and a feeder serving one
/// client at a time has nothing left once the probe has taken its slot.
#[test]
fn a_sequential_open_costs_one_connection() {
    let base = spawn_single_slot_server(fixture("h264-aac-640x360-30fps.mp4"));
    let source = open(&format!("{base}/media")).expect("open rides the connection it already has");
    assert_eq!(count_aus(source), (180, 283));
}

/// The opening request carries no total timeout, because its response is
/// the body itself on a server that answers 200 and no bound can be put
/// on how long that runs. A link that simply goes quiet is held by the
/// client's read timeout instead, which measures the wait rather than the
/// exchange; without it the only way out of this read is the session's
/// cancel token.
#[test]
fn a_quiet_link_surfaces_as_a_read_error() {
    const SERVED: usize = 32;
    let (base, _requested) = start_stalling_server(4 * 1024 * 1024, Some(SERVED));
    let mut source = HttpSource::open(
        &format!("{base}/media"),
        IoLimits {
            request_timeout: Duration::from_millis(500),
            ..IoLimits::default()
        },
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .expect("the open lands on the bytes the server did write");

    let mut buf = [0u8; 16];
    let mut got = 0usize;
    while got < SERVED {
        let n = source
            .read_at(got as u64, &mut buf)
            .expect("the served head reads back");
        assert!(n > 0, "the server wrote {SERVED} bytes");
        got += n;
    }

    // Read on a worker: without a bound on the wait this never comes
    // back at all, and a row that hangs the suite says less than one
    // that fails it.
    let (tx, rx) = mpsc::channel();
    thread::spawn(move || {
        let outcome = source
            .read_at(got as u64, &mut buf)
            .map_err(|e| e.to_string());
        let _ = tx.send(outcome);
    });
    let err = rx
        .recv_timeout(CANCEL_WINDOW)
        .expect("the read comes back on the timeout, not on the socket")
        .expect_err("the quiet link is given up on");
    assert!(
        err.starts_with("Read:"),
        "a stalled body is a read error: {err}"
    );
}

/// A ranged chunk is read at the pace its consumer plays it, and a paused
/// session stops reading altogether, so nothing may bound how long one
/// chunk's exchange runs end to end. The consumer here idles for longer
/// than the request timeout in the middle of a reopened chunk, on a link
/// that never went quiet, and the body has to carry on from where it was.
#[test]
fn an_idle_consumer_does_not_time_a_chunk_out() {
    const CHUNK: usize = 1024 * 1024;
    const TIMEOUT: Duration = Duration::from_millis(500);
    let body: Vec<u8> = (0..3 * CHUNK).map(|i| (i % 251) as u8).collect();
    let base = spawn_server(body.clone(), Mode::Ranges);
    let mut source = HttpSource::open(
        &format!("{base}/media"),
        IoLimits {
            request_timeout: TIMEOUT,
            chunk_bytes: CHUNK as u64,
            ..IoLimits::default()
        },
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .expect("open");

    // Start inside the second chunk, which is the first one a positioned
    // reopen fetches. A short read leaves most of it unread on the wire.
    let mut buf = [0u8; 16];
    let mut pos = CHUNK;
    let n = source.read_at(pos as u64, &mut buf).expect("first read");
    assert!(n > 0, "the second chunk has bytes in it");
    assert_eq!(&buf[..n], &body[pos..pos + n]);
    pos += n;

    thread::sleep(TIMEOUT * 3);

    let mut rest = vec![0u8; 64 * 1024];
    while pos < 2 * CHUNK {
        let n = source
            .read_at(pos as u64, &mut rest)
            .expect("the chunk outlives the idle consumer");
        assert!(n > 0, "the chunk ended early at {pos}");
        assert_eq!(&rest[..n], &body[pos..pos + n], "bytes continue at {pos}");
        pos += n;
    }
}

/// A connection that dies part-way through a chunk is replaced, not
/// reported: the server serves ranges, so the source asks again from the
/// byte it had reached. The server here promises the whole opening chunk,
/// sends half of it and closes, which is what a server does to a reader
/// that stopped for a long pause. Everything after that it serves properly.
#[test]
fn a_dropped_connection_is_reopened_at_the_same_byte() {
    const CHUNK: usize = 64 * 1024;
    const SENT: usize = CHUNK / 2;
    let body: Vec<u8> = (0..2 * CHUNK).map(|i| (i % 251) as u8).collect();

    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let starts = Arc::new(Mutex::new(Vec::<usize>::new()));
    let seen = Arc::clone(&starts);
    let served = body.clone();
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let seen = Arc::clone(&seen);
            let body = served.clone();
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let start: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            let end: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            range = start.zip(end);
                        }
                    }
                    let Some((start, end)) = range else { return };
                    let first = {
                        let mut seen = seen.lock().expect("starts lock");
                        seen.push(start);
                        seen.len() == 1
                    };
                    let stop = (end + 1).min(body.len());
                    let head = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{}/{}\r\n\
                         Content-Length: {}\r\n\r\n",
                        stop - 1,
                        body.len(),
                        stop - start
                    );
                    let _ = stream.write_all(head.as_bytes());
                    if first {
                        let _ = stream.write_all(&body[start..start + SENT]);
                        let _ = stream.flush();
                        let _ = stream.shutdown(std::net::Shutdown::Both);
                        return;
                    }
                    if stream.write_all(&body[start..stop]).is_err() {
                        return;
                    }
                }
            });
        }
    });

    let mut source = HttpSource::open(
        &format!("http://127.0.0.1:{port}/media"),
        IoLimits {
            chunk_bytes: CHUNK as u64,
            ..IoLimits::default()
        },
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .expect("open");

    let mut buf = vec![0u8; 4 * 1024];
    let mut pos = 0usize;
    while pos < body.len() {
        let n = source
            .read_at(pos as u64, &mut buf)
            .expect("the drop is answered with a fresh request");
        assert!(n > 0, "the body ended early at {pos}");
        assert_eq!(&buf[..n], &body[pos..pos + n], "bytes continue at {pos}");
        pos += n;
    }

    let starts = starts.lock().expect("starts lock").clone();
    assert_eq!(
        starts,
        vec![0, SENT, SENT + CHUNK],
        "one request for the open, one from the byte the drop left off at, one for the chunk after"
    );
}

/// With no total on the exchange, the wait for a reopened chunk's response
/// head still has to end on its own. The server here serves the opening
/// chunk and then takes every later request without answering it.
#[test]
fn a_chunk_request_nobody_answers_surfaces_as_a_read_error() {
    const CHUNK: usize = 64 * 1024;
    let body: Vec<u8> = (0..2 * CHUNK).map(|i| (i % 251) as u8).collect();

    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let served = Arc::new(Mutex::new(0usize));
    let first = body[..CHUNK].to_vec();
    let total = body.len();
    thread::spawn(move || {
        let mut held = Vec::new();
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            held.push(stream.try_clone().expect("clone"));
            let served = Arc::clone(&served);
            let first = first.clone();
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        if line.trim_end().is_empty() {
                            break;
                        }
                    }
                    let mut served = served.lock().expect("served lock");
                    *served += 1;
                    if *served > 1 {
                        continue;
                    }
                    let head = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-{}/{total}\r\n\
                         Content-Length: {}\r\n\r\n",
                        first.len() - 1,
                        first.len()
                    );
                    let _ = stream.write_all(head.as_bytes());
                    let _ = stream.write_all(&first);
                    let _ = stream.flush();
                }
            });
        }
    });

    let mut source = HttpSource::open(
        &format!("http://127.0.0.1:{port}/media"),
        IoLimits {
            request_timeout: Duration::from_millis(500),
            chunk_bytes: CHUNK as u64,
            ..IoLimits::default()
        },
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .expect("the opening chunk is served");

    let mut buf = vec![0u8; 16 * 1024];
    let mut pos = 0usize;
    while pos < CHUNK {
        let n = source.read_at(pos as u64, &mut buf).expect("opening chunk");
        assert!(n > 0, "the opening chunk ended early at {pos}");
        assert_eq!(&buf[..n], &body[pos..pos + n]);
        pos += n;
    }

    let (tx, rx) = mpsc::channel();
    thread::spawn(move || {
        let outcome = source
            .read_at(pos as u64, &mut buf)
            .map_err(|e| e.to_string());
        let _ = tx.send(outcome);
    });
    let err = rx
        .recv_timeout(CANCEL_WINDOW)
        .expect("the read comes back on the timeout, not on the socket")
        .expect_err("the unanswered request is given up on");
    assert!(
        err.starts_with("Read:"),
        "an unanswered chunk request is a read error: {err}"
    );
}

#[test]
fn redirects_are_followed_and_capped() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.mp4"), Mode::Ranges);
    let source = open(&format!("{base}/hop1")).expect("redirect chain resolves");
    assert!(source.final_url().path().ends_with("/media"));
    assert_eq!(count_aus(source), (180, 283));

    let err = open(&format!("{base}/loop")).expect_err("redirect loop trips the cap");
    assert_eq!(err.kind, IoErrorKind::Redirect);
}

#[test]
fn public_gate_blocks_loopback() {
    let base = spawn_server(b"irrelevant".to_vec(), Mode::Ranges);
    let err = HttpSource::open(
        &format!("{base}/media"),
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
    .expect_err("loopback must be blocked");
    assert_eq!(err.kind, IoErrorKind::Blocked);
}

/// `Session::close` joins the opener thread from the client's main
/// thread, so an open that cannot be abandoned freezes the client for the
/// request budget: six redirect hops' worth, in the worst case.
#[test]
fn a_close_during_open_abandons_the_request() {
    let (base, requested) = start_stalling_server(1024, None);
    let cancel = CancelToken::new();
    let closing = cancel.clone();
    // `None` afterwards means the open never reached a request, so the
    // cancel would have proved nothing.
    let cancelled_at = Arc::new(Mutex::new(None));
    let stamp = Arc::clone(&cancelled_at);
    thread::spawn(move || {
        // Building the pinned client loads the OS trust store, which is
        // not quick; cancelling on a timer alone can beat the request
        // onto the wire.
        requested.recv().expect("the server reads the request");
        thread::sleep(Duration::from_millis(200));
        *stamp.lock().expect("stamp") = Some(Instant::now());
        closing.cancel();
    });

    let err = HttpSource::open(
        &format!("{base}/media"),
        IoLimits::default(),
        Arc::new(AllowAllGate),
        cancel,
    )
    .map(|_| ())
    .expect_err("a cancelled open cannot succeed");

    let cancelled_at = cancelled_at
        .lock()
        .expect("stamp")
        .expect("the request was in flight when the cancel landed");
    assert_eq!(err.kind, IoErrorKind::Connect);
    assert!(
        err.detail.contains("cancelled"),
        "the refusal names the cancel, not a transport error: {err}"
    );
    assert!(
        cancelled_at.elapsed() < CANCEL_WINDOW,
        "returned on the cancel, not after the request timeout: {:?}",
        cancelled_at.elapsed()
    );
}

/// The same for the demux thread, which `close` joins too and which is
/// where a session spends the whole of its life.
#[test]
fn a_close_during_a_read_abandons_it() {
    const SERVED: usize = 64 * 1024;
    let (base, _requested) = start_stalling_server(4 * 1024 * 1024, Some(SERVED));
    let cancel = CancelToken::new();
    let mut source = HttpSource::open(
        &format!("{base}/media"),
        IoLimits::default(),
        Arc::new(AllowAllGate),
        cancel.clone(),
    )
    .expect("the open lands on the bytes the server did write");

    // Drain exactly what was written; the read after this one has nothing
    // to wait for.
    let mut buf = vec![0u8; 32 * 1024];
    let mut got = 0usize;
    while got < SERVED {
        let n = source
            .read_at(got as u64, &mut buf)
            .expect("the served head reads back");
        assert!(n > 0, "the server wrote {SERVED} bytes");
        got += n;
    }

    let closing = cancel.clone();
    let cancelled_at = Arc::new(Mutex::new(None));
    let stamp = Arc::clone(&cancelled_at);
    thread::spawn(move || {
        thread::sleep(Duration::from_millis(200));
        *stamp.lock().expect("stamp") = Some(Instant::now());
        closing.cancel();
    });

    let err = source
        .read_at(got as u64, &mut buf)
        .expect_err("the stalled read is abandoned");

    let cancelled_at = cancelled_at
        .lock()
        .expect("stamp")
        .expect("the read was waiting when the cancel landed");
    assert!(
        err.to_string().contains("cancelled"),
        "the refusal names the cancel, not a transport error: {err}"
    );
    assert!(
        cancelled_at.elapsed() < CANCEL_WINDOW,
        "returned on the cancel, not after the request timeout: {:?}",
        cancelled_at.elapsed()
    );
}

/// `read_at` is trait surface, so a zero-length buffer is legal. The held
/// chunk has to survive it: treated as spent, the unread remainder is
/// dropped and the loop pulls until the body ends, which on a live or
/// stalled source never happens. Pinned against a server that goes quiet,
/// so a read that went to the network rather than to the chunk in hand
/// cannot come back inside the window.
#[test]
fn a_zero_length_read_keeps_the_chunk_it_is_holding() {
    // Small and written in one call, so it arrives as one chunk on
    // loopback and the first read leaves a remainder inside it. A larger
    // body would depend on where the transport split it. The server never
    // writes again, so a source that dropped the chunk waits out the
    // request timeout rather than refetching unnoticed.
    const SERVED: usize = 32;
    let (base, _requested) = start_stalling_server(4 * 1024 * 1024, Some(SERVED));
    let mut source = open(&format!("{base}/media")).expect("open");

    let mut buf = [0u8; 16];
    assert_eq!(
        source
            .read_at(0, &mut buf)
            .expect("the served head reads back"),
        buf.len(),
        "the first read leaves a remainder in the chunk"
    );

    let started = Instant::now();
    assert_eq!(
        source.read_at(16, &mut []).expect("a zero-length read"),
        0,
        "nothing was asked for and nothing is served"
    );
    assert!(
        started.elapsed() < CANCEL_WINDOW,
        "the zero-length read went to the network: {:?}",
        started.elapsed()
    );

    let started = Instant::now();
    assert_eq!(
        source
            .read_at(16, &mut buf)
            .expect("the remainder reads back"),
        buf.len(),
        "the chunk the zero-length read was holding is still there"
    );
    assert!(
        started.elapsed() < CANCEL_WINDOW,
        "the remainder was refetched rather than served: {:?}",
        started.elapsed()
    );
}

/// A transport failure has to say what failed. reqwest's own `Display`
/// stops at "error sending request for url (...)"; the refusal, TLS alert
/// or resolver answer is one `source()` hop below it.
#[test]
fn a_transport_failure_names_its_cause() {
    // A port that was bound long enough to be sure nothing else holds it,
    // and released.
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    drop(listener);

    let err = open(&format!("http://127.0.0.1:{port}/media")).expect_err("nothing is listening");
    assert_eq!(err.kind, IoErrorKind::Connect);
    assert!(
        err.detail.to_lowercase().contains("refused"),
        "the detail carries the cause, not just the request: {err}"
    );
}

#[test]
fn unsupported_scheme_is_a_url_error() {
    let err = open("ftp://example.invalid/media").expect_err("ftp refused");
    assert_eq!(err.kind, IoErrorKind::Url);
}

/// The liveness inference reads two things off the source: whether the
/// server will serve byte ranges, and whether it stated any length.
/// Finite and rangeable reads as on-demand, which keeps a VOD off the
/// jitter-buffer path, where it would play at delivery speed.
#[test]
fn seekability_reads_ranges_and_length_not_guesses() {
    let body = fixture("h264-aac-640x360-30fps.mp4");

    // A real 206 answer.
    let ranged = spawn_server(body.clone(), Mode::Ranges);
    assert!(
        open(&format!("{ranged}/media"))
            .expect("open")
            .is_seekable()
    );

    // 200 to a range request, no advertisement: nothing says ranges work.
    let plain = spawn_server(body.clone(), Mode::Sequential);
    assert!(!open(&format!("{plain}/media")).expect("open").is_seekable());

    // 200 to a range request but `Accept-Ranges: bytes` advertised: the
    // server honours ranges generally.
    let advertised = spawn_server(body.clone(), Mode::SequentialAdvertised);
    assert!(
        open(&format!("{advertised}/media"))
            .expect("open")
            .is_seekable()
    );

    // No ranges and no length at all: the live-edge shape.
    let unbounded = spawn_server(body, Mode::Unbounded);
    assert!(
        !open(&format!("{unbounded}/media"))
            .expect("open")
            .is_seekable()
    );
}

/// What a scripted server does with one ranged request.
enum Answer {
    Serve,
    Redirect(String),
    Gone,
    /// A 206 for a range that starts this many bytes after the one asked
    /// for, stated honestly in its `Content-Range`.
    Shifted(usize),
}

/// A range server on `ip` that asks `script` what to do with each request,
/// telling it how many came before and where this one's range starts. The
/// log is every range start it was asked for, in order.
fn spawn_scripted_server(
    ip: &str,
    body: Vec<u8>,
    script: impl Fn(usize, usize) -> Answer + Send + Sync + 'static,
) -> (String, Arc<Mutex<Vec<usize>>>) {
    let listener = TcpListener::bind((ip, 0)).expect("bind");
    let port = listener.local_addr().unwrap().port();
    let log = Arc::new(Mutex::new(Vec::<usize>::new()));
    let seen = Arc::clone(&log);
    let script = Arc::new(script);
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let seen = Arc::clone(&seen);
            let script = Arc::clone(&script);
            let body = body.clone();
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let start: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            let end: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            range = start.zip(end);
                        }
                    }
                    let Some((start, end)) = range else { return };
                    let before = {
                        let mut seen = seen.lock().expect("log lock");
                        seen.push(start);
                        seen.len() - 1
                    };
                    let answer = script(before, start);
                    let start = match answer {
                        Answer::Shifted(by) => start + by,
                        _ => start,
                    };
                    let response = match answer {
                        Answer::Serve | Answer::Shifted(_) => {
                            let stop = (end + 1).min(body.len());
                            let mut r = format!(
                                "HTTP/1.1 206 Partial Content\r\n\
                                 Content-Range: bytes {start}-{}/{}\r\n\
                                 Content-Length: {}\r\n\r\n",
                                stop - 1,
                                body.len(),
                                stop - start
                            )
                            .into_bytes();
                            r.extend_from_slice(&body[start..stop]);
                            r
                        }
                        Answer::Redirect(to) => redirect(&to),
                        Answer::Gone => b"HTTP/1.1 410 Gone\r\nContent-Length: 0\r\n\r\n".to_vec(),
                    };
                    if stream.write_all(&response).is_err() {
                        return;
                    }
                }
            });
        }
    });
    (format!("http://{ip}:{port}"), log)
}

const REDIRECT_CHUNK: usize = 64 * 1024;

fn patterned(len: usize) -> Vec<u8> {
    (0..len).map(|i| (i % 251) as u8).collect()
}

fn open_chunked(url: &str, gate: Arc<dyn media_io::AddressGate>) -> HttpSource {
    HttpSource::open(
        url,
        IoLimits {
            chunk_bytes: REDIRECT_CHUNK as u64,
            ..IoLimits::default()
        },
        gate,
        CancelToken::new(),
    )
    .expect("the opening chunk is served without a redirect")
}

fn read_through(source: &mut HttpSource, body: &[u8]) -> Result<(), String> {
    let mut buf = vec![0u8; 16 * 1024];
    let mut pos = 0usize;
    while pos < body.len() {
        let n = source
            .read_at(pos as u64, &mut buf)
            .map_err(|e| e.to_string())?;
        assert!(n > 0, "the body ended early at {pos}");
        assert_eq!(&buf[..n], &body[pos..pos + n], "bytes continue at {pos}");
        pos += n;
    }
    Ok(())
}

/// A CDN node that starts redirecting part-way through a file has not
/// refused the request, it has said where the bytes are. The origin here
/// serves the open itself and answers every later chunk request with a 302
/// to a second server.
#[test]
fn a_redirected_chunk_request_is_followed() {
    let body = patterned(3 * REDIRECT_CHUNK);
    let (target, target_log) =
        spawn_scripted_server("127.0.0.1", body.clone(), |_, _| Answer::Serve);
    let to = format!("{target}/media");
    let (origin, origin_log) =
        spawn_scripted_server("127.0.0.1", body.clone(), move |before, _| match before {
            0 => Answer::Serve,
            _ => Answer::Redirect(to.clone()),
        });

    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(AllowAllGate));
    read_through(&mut source, &body).expect("the redirected chunks read back");

    assert_eq!(
        *target_log.lock().expect("log lock"),
        vec![REDIRECT_CHUNK, 2 * REDIRECT_CHUNK],
        "the second server served every chunk after the first, at the byte asked for"
    );
    assert_eq!(
        *origin_log.lock().expect("log lock"),
        vec![0, REDIRECT_CHUNK, 2 * REDIRECT_CHUNK]
    );
    assert!(source.final_url().as_str().starts_with(&target));
}

/// Loopback is the whole of 127/8, so a second loopback address stands in
/// for the host a gate refuses while the origin stays reachable.
struct OnlyLocalhost;

impl media_io::AddressGate for OnlyLocalhost {
    fn permit(&self, ip: std::net::IpAddr) -> bool {
        ip == std::net::IpAddr::from([127, 0, 0, 1])
    }
}

/// A redirect met mid-file names a host the server chose, exactly as one
/// met at the open does, so it goes through the same gate before anything
/// is sent to it.
#[test]
fn a_redirected_chunk_request_is_vetted() {
    let body = patterned(2 * REDIRECT_CHUNK);
    let (refused, refused_log) =
        spawn_scripted_server("127.0.0.2", body.clone(), |_, _| Answer::Serve);
    let to = format!("{refused}/media");
    let (origin, _) =
        spawn_scripted_server("127.0.0.1", body.clone(), move |before, _| match before {
            0 => Answer::Serve,
            _ => Answer::Redirect(to.clone()),
        });

    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(OnlyLocalhost));
    let err = read_through(&mut source, &body).expect_err("the hop is refused");
    assert!(err.starts_with("Blocked:"), "the gate refused it: {err}");
    assert!(
        refused_log.lock().expect("log lock").is_empty(),
        "nothing was sent to the refused host"
    );

    // The same chain with a gate that allows the hop, so the refusal above
    // is the gate's and not the fixture's.
    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(AllowAllGate));
    read_through(&mut source, &body).expect("an allowed hop reads through");
}

#[test]
fn a_redirect_loop_on_a_chunk_request_stops_at_the_cap() {
    let body = patterned(2 * REDIRECT_CHUNK);
    let (origin, origin_log) =
        spawn_scripted_server("127.0.0.1", body.clone(), |before, _| match before {
            0 => Answer::Serve,
            _ => Answer::Redirect("/media".to_string()),
        });

    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(AllowAllGate));
    let err = read_through(&mut source, &body).expect_err("the loop is given up on");
    assert!(err.starts_with("Redirect:"), "the cap refused it: {err}");

    let hops = IoLimits::default().max_redirects as usize + 1;
    assert_eq!(
        origin_log.lock().expect("log lock").len(),
        1 + hops,
        "one request for the open and one walk's worth for the chunk"
    );
}

/// A redirector that hands out short-lived targets has to be asked again
/// each time: the target a previous walk settled on is the thing that
/// expires first. Each target here serves one request and answers 410 from
/// then on, and the origin names a different one each time it is asked.
#[test]
fn every_chunk_request_walks_from_the_url_that_was_opened() {
    let body = patterned(3 * REDIRECT_CHUNK);
    let one_use = |before: usize, _: usize| match before {
        0 => Answer::Serve,
        _ => Answer::Gone,
    };
    let (first, _) = spawn_scripted_server("127.0.0.1", body.clone(), one_use);
    let (second, _) = spawn_scripted_server("127.0.0.1", body.clone(), one_use);
    let targets = [format!("{first}/media"), format!("{second}/media")];
    let (origin, origin_log) =
        spawn_scripted_server("127.0.0.1", body.clone(), move |before, _| match before {
            0 => Answer::Serve,
            n => Answer::Redirect(targets[(n - 1) % targets.len()].clone()),
        });

    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(AllowAllGate));
    read_through(&mut source, &body).expect("each chunk is asked of the origin again");

    assert_eq!(
        *origin_log.lock().expect("log lock"),
        vec![0, REDIRECT_CHUNK, 2 * REDIRECT_CHUNK],
        "the origin saw every chunk request"
    );
}

/// A proxy that rewrites ranges answers 206 as well, for bytes that start
/// somewhere else. The status cannot tell the two apart, and served as they
/// come those bytes would reach the demuxer as the ones it asked for.
#[test]
fn a_ranged_answer_that_starts_elsewhere_is_refused() {
    let body = patterned(3 * REDIRECT_CHUNK);
    let (origin, _) = spawn_scripted_server("127.0.0.1", body.clone(), |before, _| match before {
        0 => Answer::Serve,
        _ => Answer::Shifted(512),
    });

    let mut source = open_chunked(&format!("{origin}/media"), Arc::new(AllowAllGate));
    let err = read_through(&mut source, &body).expect_err("the shifted part is refused");
    assert!(err.starts_with("Read:"), "a read error: {err}");
    assert!(
        err.contains(&format!("bytes {}-", REDIRECT_CHUNK + 512)),
        "the refusal names the range that was stated: {err}"
    );
}

/// The opening probe asks from byte 0 and is held to it the same way: a
/// part that starts anywhere else would be installed as the head of the
/// file.
#[test]
fn an_opening_answer_that_starts_elsewhere_is_refused() {
    let body = patterned(2 * REDIRECT_CHUNK);
    let (origin, _) = spawn_scripted_server("127.0.0.1", body, |_, _| Answer::Shifted(512));

    let err = HttpSource::open(
        &format!("{origin}/media"),
        IoLimits::default(),
        Arc::new(AllowAllGate),
        CancelToken::new(),
    )
    .map(|_| ())
    .expect_err("the shifted part is refused");
    assert_eq!(err.kind, IoErrorKind::Http);
    assert!(
        err.detail.contains("bytes 512-"),
        "the refusal names the range that was stated: {err}"
    );
}

/// One ranged request as a server saw it: which connection carried it and
/// the inclusive range it asked for.
#[derive(Clone, Copy, Debug)]
struct Asked {
    connection: usize,
    first: u64,
    last: u64,
}

impl Asked {
    fn len(&self) -> u64 {
        self.last - self.first + 1
    }
}

/// A range server that keeps each connection alive and logs every request
/// with the connection it arrived on.
fn spawn_logging_server(body: Vec<u8>) -> (String, Arc<Mutex<Vec<Asked>>>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let log = Arc::new(Mutex::new(Vec::<Asked>::new()));
    let seen = Arc::clone(&log);
    let body = Arc::new(body);
    thread::spawn(move || {
        for (connection, stream) in listener.incoming().enumerate() {
            let Ok(stream) = stream else { break };
            let seen = Arc::clone(&seen);
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let first: Option<u64> = parts.next().and_then(|s| s.parse().ok());
                            let last: Option<u64> = parts.next().and_then(|s| s.parse().ok());
                            range = first.zip(last);
                        }
                    }
                    let Some((first, last)) = range else { return };
                    seen.lock().expect("log lock").push(Asked {
                        connection,
                        first,
                        last,
                    });
                    let stop = (last as usize + 1).min(body.len());
                    let first = first as usize;
                    let mut r = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {first}-{}/{}\r\n\
                         Content-Length: {}\r\n\r\n",
                        stop - 1,
                        body.len(),
                        stop - first
                    )
                    .into_bytes();
                    r.extend_from_slice(&body[first..stop]);
                    if stream.write_all(&r).is_err() {
                        return;
                    }
                }
            });
        }
    });
    (format!("http://127.0.0.1:{port}"), log)
}

const MIB: usize = 1024 * 1024;

fn read_some(source: &mut HttpSource, body: &[u8], at: usize, len: usize) {
    let mut buf = vec![0u8; len];
    let n = source.read_at(at as u64, &mut buf).expect("read");
    assert!(n > 0, "bytes at {at}");
    assert_eq!(&buf[..n], &body[at..at + n], "bytes at {at}");
}

/// A reader that jumps wants the bytes where it lands, not the megabytes
/// after them: a fragment walk reads a header and moves on, and an Ogg
/// seek reads a page per probe. Asked for a whole chunk, the server sends
/// what the reader then walks away from.
#[test]
fn a_jump_asks_for_what_it_reads_not_a_whole_chunk() {
    let body = patterned(8 * MIB);
    let (base, log) = spawn_logging_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    read_some(&mut source, &body, 0, 16);
    read_some(&mut source, &body, 5 * MIB, 16 * 1024);

    let log = log.lock().expect("log lock").clone();
    assert_eq!(log.len(), 2, "the open and the jump: {log:?}");
    assert_eq!(log[1].first, 5 * MIB as u64);
    for asked in &log {
        assert!(
            asked.len() <= 64 * 1024,
            "a request sized to the read, not to a chunk: {asked:?}"
        );
    }
}

/// Reading on from where the last request ended is playback, and playback
/// wants long requests: the size doubles with each one until it reaches the
/// chunk size.
#[test]
fn sequential_reads_grow_the_request_to_a_whole_chunk() {
    let body = patterned(16 * MIB);
    let (base, log) = spawn_logging_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    let mut buf = vec![0u8; 64 * 1024];
    let mut pos = 0usize;
    while pos < 12 * MIB {
        let n = source.read_at(pos as u64, &mut buf).expect("read");
        assert_eq!(&buf[..n], &body[pos..pos + n], "bytes at {pos}");
        pos += n;
    }

    let log = log.lock().expect("log lock").clone();
    let chunk = IoLimits::default().chunk_bytes;
    assert!(
        log[0].len() < chunk,
        "the open starts small and grows: {log:?}"
    );
    for pair in log.windows(2) {
        assert_eq!(pair[1].first, pair[0].last + 1, "contiguous: {log:?}");
        assert_eq!(
            pair[1].len(),
            (pair[0].len() * 2).min(chunk),
            "each request doubles to the chunk size: {log:?}"
        );
    }
    assert_eq!(log.last().map(Asked::len), Some(chunk), "{log:?}");
}

/// A skip a few kilobytes forward lands inside the response already
/// arriving; reading on to it costs less than asking again.
#[test]
fn a_short_forward_jump_rides_the_response_it_has() {
    let body = patterned(8 * MIB);
    let (base, log) = spawn_logging_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    read_some(&mut source, &body, 0, 16);
    read_some(&mut source, &body, 32 * 1024, 16);

    assert_eq!(log.lock().expect("log lock").len(), 1, "no second request");
}

/// A walk of header reads a fragment apart, the shape of opening a
/// fragmented MP4 with no index. Each step asks again, but on the same
/// connection: a response with little left on it is read out so its
/// connection goes back to the pool, where dropping it would cost a new
/// connection, and a TLS handshake, per step.
#[test]
fn a_walk_of_jumps_keeps_one_connection() {
    const STEP: usize = 600 * 1024;
    const STEPS: usize = 30;
    let body = patterned(STEP * STEPS + MIB);
    let (base, log) = spawn_logging_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    for step in 0..STEPS {
        read_some(&mut source, &body, step * STEP, 512);
    }

    let log = log.lock().expect("log lock").clone();
    assert_eq!(log.len(), STEPS, "a request per step: {log:?}");
    assert!(
        log.iter().all(|asked| asked.connection == 0),
        "one connection for the walk: {log:?}"
    );
    let asked: u64 = log.iter().map(Asked::len).sum();
    assert!(
        asked <= (STEPS * 64 * 1024) as u64,
        "{asked} bytes asked for {STEPS} headers"
    );
}

/// A range server that answers every request as a chunked 206: the part
/// is stated in `Content-Range` and nowhere else, with no `Content-Length`.
fn spawn_chunked_range_server(body: Vec<u8>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let body = Arc::new(body);
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let first: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            let last: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            range = first.zip(last);
                        }
                    }
                    let Some((first, last)) = range else { return };
                    let stop = (last + 1).min(body.len());
                    let part = &body[first..stop];
                    let mut r = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {first}-{}/{}\r\n\
                         Transfer-Encoding: chunked\r\n\r\n{:x}\r\n",
                        stop - 1,
                        body.len(),
                        part.len()
                    )
                    .into_bytes();
                    r.extend_from_slice(part);
                    r.extend_from_slice(b"\r\n0\r\n\r\n");
                    if stream.write_all(&r).is_err() {
                        return;
                    }
                }
            });
        }
    });
    format!("http://127.0.0.1:{port}")
}

/// A 206 need not state a `Content-Length`: a chunked one states its part
/// only in `Content-Range`. A skip past the end of that part asks again
/// rather than reading off the end of it, and reading on across the part's
/// end carries on rather than taking it for the end of the file.
#[test]
fn a_chunked_part_is_bounded_by_its_content_range() {
    let body = patterned(2 * MIB);
    let base = spawn_chunked_range_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    read_some(&mut source, &body, 0, 16);
    read_some(&mut source, &body, 96 * 1024, 16);
    read_through(&mut source, &body).expect("the file reads through part by part");
}

/// A range server that never states the file's total (`Content-Range:
/// bytes <first>-<last>/*`) and answers a range starting at or past the end
/// with 416, which is how such a server says the file has ended.
fn spawn_unknown_total_server(body: Vec<u8>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let body = Arc::new(body);
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let body = Arc::clone(&body);
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let first: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            let last: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            range = first.zip(last);
                        }
                    }
                    let Some((first, last)) = range else { return };
                    let r = if first >= body.len() {
                        b"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\n\r\n".to_vec()
                    } else {
                        let stop = (last + 1).min(body.len());
                        let mut r = format!(
                            "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {first}-{}/*\r\n\
                             Content-Length: {}\r\n\r\n",
                            stop - 1,
                            stop - first
                        )
                        .into_bytes();
                        r.extend_from_slice(&body[first..stop]);
                        r
                    };
                    if stream.write_all(&r).is_err() {
                        return;
                    }
                }
            });
        }
    });
    format!("http://127.0.0.1:{port}")
}

/// `bytes <first>-<last>/*` is a valid part of a file whose total the
/// server does not state. The end of one part is not the end of the file:
/// reads carry on part by part, and the 416 that answers a range past the
/// end is where the file ends.
#[test]
fn a_part_with_no_stated_total_is_not_the_end_of_the_file() {
    let body = patterned(MIB + 1000);
    let base = spawn_unknown_total_server(body.clone());
    let mut source = open(&format!("{base}/media")).expect("open");

    read_through(&mut source, &body).expect("the file reads through part by part");
    let mut buf = [0u8; 16];
    assert_eq!(
        source
            .read_at(body.len() as u64, &mut buf)
            .expect("the 416 is the end"),
        0
    );
}

/// A `Content-Range` whose last byte is the largest number a byte offset
/// can hold has no exclusive end to read to. It is refused as a malformed
/// answer, not carried into arithmetic that would overflow on it.
#[test]
fn a_range_ending_at_the_largest_offset_is_refused() {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    thread::spawn(move || {
        let Ok((stream, _)) = listener.accept() else {
            return;
        };
        let mut reader = BufReader::new(stream.try_clone().expect("clone"));
        let mut stream = stream;
        loop {
            let mut line = String::new();
            if reader.read_line(&mut line).unwrap_or(0) == 0 || line.trim_end().is_empty() {
                break;
            }
        }
        let _ = stream.write_all(
            format!(
                "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 0-{}/*\r\n\
                 Content-Length: 16\r\n\r\n",
                u64::MAX
            )
            .as_bytes(),
        );
        let _ = stream.write_all(&[0u8; 16]);
        thread::sleep(Duration::from_secs(1));
    });

    let err = open(&format!("http://127.0.0.1:{port}/media"))
        .map(|_| ())
        .expect_err("the unrepresentable range is refused");
    assert_eq!(err.kind, IoErrorKind::Http, "{err}");
}

/// A short skip reads on through the response it has, and that response
/// can die part-way like any other. The server here promises the opening
/// part, sends 16 KiB of it and closes; a skip to 32 KiB then asks again
/// from where it was going rather than failing.
#[test]
fn a_skip_through_a_dropped_response_asks_again_from_its_target() {
    const SENT: usize = 16 * 1024;
    let body = patterned(MIB);
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let port = listener.local_addr().unwrap().port();
    let starts = Arc::new(Mutex::new(Vec::<usize>::new()));
    let seen = Arc::clone(&starts);
    let served = body.clone();
    thread::spawn(move || {
        for stream in listener.incoming() {
            let Ok(stream) = stream else { break };
            let seen = Arc::clone(&seen);
            let body = served.clone();
            thread::spawn(move || {
                let mut reader = BufReader::new(stream.try_clone().expect("clone"));
                let mut stream = stream;
                loop {
                    let mut range = None;
                    loop {
                        let mut line = String::new();
                        if reader.read_line(&mut line).unwrap_or(0) == 0 {
                            return;
                        }
                        let line = line.trim_end();
                        if line.is_empty() {
                            break;
                        }
                        if let Some(spec) = line.to_ascii_lowercase().strip_prefix("range: bytes=")
                        {
                            let mut parts = spec.split('-');
                            let start: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            let end: Option<usize> = parts.next().and_then(|s| s.parse().ok());
                            range = start.zip(end);
                        }
                    }
                    let Some((start, end)) = range else { return };
                    let first = {
                        let mut seen = seen.lock().expect("starts lock");
                        seen.push(start);
                        seen.len() == 1
                    };
                    let stop = (end + 1).min(body.len());
                    let head = format!(
                        "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes {start}-{}/{}\r\n\
                         Content-Length: {}\r\n\r\n",
                        stop - 1,
                        body.len(),
                        stop - start
                    );
                    let _ = stream.write_all(head.as_bytes());
                    if first {
                        let _ = stream.write_all(&body[start..start + SENT]);
                        let _ = stream.flush();
                        let _ = stream.shutdown(std::net::Shutdown::Both);
                        return;
                    }
                    if stream.write_all(&body[start..stop]).is_err() {
                        return;
                    }
                }
            });
        }
    });

    let mut source = open(&format!("http://127.0.0.1:{port}/media")).expect("open");
    read_some(&mut source, &body, 0, 16);
    read_some(&mut source, &body, 32 * 1024, 16);

    assert_eq!(
        *starts.lock().expect("starts lock"),
        vec![0, 32 * 1024],
        "the skip's failure is answered with a request from its target"
    );
}
