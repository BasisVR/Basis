//! HttpLiveSource against an in-process HTTP/1.1 server: sequential
//! streaming, the head cache, per-read stall detection, cancellable
//! connects and the address gate. No external network anywhere.

use std::io::{BufRead, BufReader, Write};
use std::net::{TcpListener, TcpStream};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use media_clock::Generation;
use media_demux::{ByteSource, DemuxLimits, StreamEvent};
use media_io::{
    AllowAllGate, CancelToken, HttpLiveSource, IoError, IoErrorKind, IoLimits, PublicAddressGate,
};

#[derive(Clone, Copy, PartialEq)]
enum Mode {
    /// Stream the body in pieces, then close (EOS).
    Stream,
    /// Send the first 4 KiB, then hold the connection open silently.
    StallAfterHead,
    /// Accept, read the request, never answer.
    NeverAnswer,
}

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
    let mut request_line = String::new();
    if reader.read_line(&mut request_line).unwrap_or(0) == 0 {
        return;
    }
    let path = request_line
        .split_whitespace()
        .nth(1)
        .unwrap_or("/")
        .to_string();
    loop {
        let mut line = String::new();
        if reader.read_line(&mut line).unwrap_or(0) == 0 {
            return;
        }
        if line.trim_end().is_empty() {
            break;
        }
    }

    if path == "/hop" {
        let _ = stream
            .write_all(b"HTTP/1.1 302 Found\r\nLocation: /media\r\nContent-Length: 0\r\n\r\n");
        // Fall through to serving on the next request over a new connection.
        return;
    }
    if mode == Mode::NeverAnswer {
        // Hold the socket open, sending nothing.
        thread::sleep(Duration::from_secs(60));
        return;
    }

    // A live edge has no length: connection-close delimited body.
    if stream
        .write_all(b"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n")
        .is_err()
    {
        return;
    }
    match mode {
        Mode::Stream => {
            for piece in body.chunks(16 * 1024) {
                if stream.write_all(piece).is_err() {
                    return;
                }
            }
        }
        Mode::StallAfterHead => {
            let head = &body[..body.len().min(4096)];
            let _ = stream.write_all(head);
            let _ = stream.flush();
            thread::sleep(Duration::from_secs(60));
        }
        Mode::NeverAnswer => unreachable!(),
    }
}

fn fixture(name: &str) -> Vec<u8> {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../fixtures")
        .join(name);
    std::fs::read(path).expect("fixture readable")
}

fn open(url: &str, limits: IoLimits) -> Result<HttpLiveSource, IoError> {
    HttpLiveSource::open(url, limits, Arc::new(AllowAllGate), CancelToken::new())
}

#[test]
fn streams_ts_through_the_demuxer() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.ts"), Mode::Stream);
    let mut source = open(&format!("{base}/media"), IoLimits::default()).expect("open");
    assert_eq!(source.size().expect("size"), None);

    let mut demux = media_demux::open_auto(Box::new(source), DemuxLimits::default(), Generation(0))
        .expect("router sniffs TS off the live stream");
    let (mut video, mut audio) = (0u32, 0u32);
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Au(au) => {
                if Some(au.track) == demux.video_track() {
                    video += 1;
                } else {
                    audio += 1;
                }
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    assert_eq!((video, audio), (180, 283));
}

#[test]
fn head_cache_serves_rereads_and_seeks_are_refused() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.ts"), Mode::Stream);
    let mut source = open(&format!("{base}/media"), IoLimits::default()).expect("open");

    let mut first = [0u8; 1024];
    source.read_exact_at(0, &mut first).expect("head read");
    let mut again = [0u8; 1024];
    source.read_exact_at(0, &mut again).expect("head re-read");
    assert_eq!(first, again);
    assert_eq!(first[0], 0x47);

    // Sequential resume past the served head works…
    let mut next = [0u8; 512];
    source.read_exact_at(1024, &mut next).expect("resume");
    // …but a forward skip is a typed refusal.
    let err = source
        .read_at(1_000_000, &mut next)
        .expect_err("gap refused");
    assert_eq!(
        err.downcast_ref::<IoError>().expect("io error").kind,
        IoErrorKind::Read
    );
}

#[test]
fn stall_surfaces_as_a_typed_read_error() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.ts"), Mode::StallAfterHead);
    let limits = IoLimits {
        read_stall: Duration::from_millis(300),
        ..IoLimits::default()
    };
    let mut source = open(&format!("{base}/media"), limits).expect("open");

    let started = Instant::now();
    let mut buf = [0u8; 64 * 1024];
    let mut offset = 0u64;
    let error = loop {
        match source.read_at(offset, &mut buf) {
            Ok(n) if n > 0 => offset += n as u64,
            Ok(0) => panic!("stalled stream must error, not end cleanly"),
            Ok(_) => unreachable!(),
            Err(e) => break e,
        }
    };
    assert_eq!(
        error.downcast_ref::<IoError>().expect("io error").kind,
        IoErrorKind::Read
    );
    assert!(
        started.elapsed() < Duration::from_secs(5),
        "stall must surface promptly, took {:?}",
        started.elapsed()
    );
}

#[test]
fn connect_is_cancellable() {
    let base = spawn_server(Vec::new(), Mode::NeverAnswer);
    let cancel = CancelToken::new();
    let canceller = cancel.clone();
    thread::spawn(move || {
        thread::sleep(Duration::from_millis(150));
        canceller.cancel();
    });

    let started = Instant::now();
    let err = HttpLiveSource::open(
        &format!("{base}/media"),
        IoLimits::default(),
        Arc::new(AllowAllGate),
        cancel,
    )
    .expect_err("cancelled open must fail");
    assert_eq!(err.kind, IoErrorKind::Connect);
    assert!(
        started.elapsed() < Duration::from_secs(3),
        "cancel must interrupt the open, took {:?}",
        started.elapsed()
    );
}

#[test]
fn redirects_re_vet_and_resolve() {
    let base = spawn_server(fixture("h264-aac-640x360-30fps.ts"), Mode::Stream);
    let source = open(&format!("{base}/hop"), IoLimits::default()).expect("redirect resolves");
    assert!(source.final_url().path().ends_with("/media"));
}

#[test]
fn public_gate_blocks_loopback() {
    let base = spawn_server(Vec::new(), Mode::Stream);
    let err = HttpLiveSource::open(
        &format!("{base}/media"),
        IoLimits::default(),
        Arc::new(PublicAddressGate),
        CancelToken::new(),
    )
    .expect_err("loopback must be blocked");
    assert_eq!(err.kind, IoErrorKind::Blocked);
}
