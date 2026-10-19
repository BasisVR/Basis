//! What one RTSP reply may make the client hold. A server names its own
//! `Content-Length` and chooses when a line ends, so neither may decide how
//! much the client buffers: a reply past the ceiling fails the request, and
//! the connection's memory stays bounded while it does.

use std::io::{Read, Write};
use std::net::TcpListener;
use std::time::Duration;

use retina::client::{Session, SessionOptions};

/// Well past any reply the client should accept, well short of a test
/// machine's memory.
const OVERSIZED: usize = 2 << 20;

/// Serves one connection: reads the request head, writes `reply`, then
/// holds the connection open until the client drops it, so a client still
/// waiting for the rest shows as a hang rather than an EOF error.
fn serve_once(reply: Vec<u8>) -> String {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let addr = listener.local_addr().expect("addr");
    std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().expect("accept");
        let mut head = Vec::new();
        let mut byte = [0u8; 1];
        while !head.ends_with(b"\r\n\r\n") {
            if stream.read(&mut byte).unwrap_or(0) == 0 {
                return;
            }
            head.push(byte[0]);
        }
        if stream.write_all(&reply).is_err() {
            return;
        }
        let mut sink = [0u8; 1024];
        while stream.read(&mut sink).unwrap_or(0) > 0 {}
    });
    format!("rtsp://{addr}/stream")
}

fn describe(url: &str) -> Result<(), String> {
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .expect("runtime");
    let url = url::Url::parse(url).expect("url");
    runtime.block_on(async {
        match tokio::time::timeout(
            Duration::from_secs(10),
            Session::describe(url, SessionOptions::default()),
        )
        .await
        {
            Ok(Ok(_)) => Ok(()),
            Ok(Err(e)) => Err(e.to_string()),
            Err(_) => panic!("describe still waiting after 10 s"),
        }
    })
}

#[test]
fn a_reply_claiming_a_terabyte_body_fails_the_describe() {
    let url = serve_once(
        b"RTSP/1.0 200 OK\r\nCSeq: 1\r\nContent-Type: application/sdp\r\n\
          Content-Length: 1099511627776\r\n\r\n"
            .to_vec(),
    );
    let err = describe(&url).expect_err("an oversized body is refused");
    assert!(err.contains("message-too-large"), "{err}");
}

#[test]
fn a_header_line_that_never_ends_fails_the_describe() {
    let mut reply = b"RTSP/1.0 200 OK\r\nCSeq: 1\r\nX-Pad: ".to_vec();
    reply.resize(reply.len() + OVERSIZED, b'a');
    let url = serve_once(reply);
    let err = describe(&url).expect_err("an unterminated line is refused");
    assert!(err.contains("unterminated line"), "{err}");
}

#[test]
fn a_head_of_endless_header_lines_fails_the_describe() {
    let mut reply = b"RTSP/1.0 200 OK\r\nCSeq: 1\r\n".to_vec();
    while reply.len() < OVERSIZED {
        reply.extend_from_slice(
            b"X-Pad: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n",
        );
    }
    let url = serve_once(reply);
    let err = describe(&url).expect_err("an oversized head is refused");
    assert!(err.contains("message-too-large"), "{err}");
}

#[test]
fn an_unfinished_line_after_most_of_the_head_fails_the_describe() {
    let mut reply = b"RTSP/1.0 200 OK\r\nCSeq: 1\r\n".to_vec();
    while reply.len() < 900 << 10 {
        reply.extend_from_slice(
            b"X-Pad: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n",
        );
    }
    reply.extend_from_slice(b"X-Tail: ");
    reply.resize(reply.len() + (200 << 10), b'a');
    let url = serve_once(reply);
    let err = describe(&url).expect_err("an oversized head is refused");
    assert!(err.contains("unterminated line"), "{err}");
}
