//! The RTSP client dials the addresses its caller resolved and vetted, not
//! whatever the URL's host resolves to when the client gets round to it.
//! A second lookup is a second answer, which a rebinding name can point
//! inside the network after the first passed the address gate. A host with
//! several vetted addresses still opens while any of them answers.

use std::io::{Read, Write};
use std::net::{SocketAddr, TcpListener};
use std::sync::mpsc;
use std::time::Duration;

use retina::client::{Session, SessionOptions};

/// Answers one request with a 404 and reports the request head it saw.
fn server() -> (SocketAddr, mpsc::Receiver<String>) {
    let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
    let addr = listener.local_addr().expect("addr");
    let (seen_tx, seen_rx) = mpsc::channel();
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
        let _ = seen_tx.send(String::from_utf8_lossy(&head).into_owned());
        let _ = stream.write_all(b"RTSP/1.0 404 Not Found\r\nCSeq: 1\r\n\r\n");
    });
    (addr, seen_rx)
}

/// Describes `rtsp://camera.invalid:8554/stream` through `addrs` and
/// returns the request the server saw. `.invalid` never resolves, so
/// reaching the server at all means the client did not look the host up.
fn describe_via(addrs: Vec<SocketAddr>, seen: mpsc::Receiver<String>) -> String {
    let url = url::Url::parse("rtsp://camera.invalid:8554/stream").expect("url");
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .expect("runtime");
    let result = runtime.block_on(async {
        tokio::time::timeout(
            Duration::from_secs(10),
            Session::describe(url, SessionOptions::default().connect_addrs(addrs)),
        )
        .await
        .expect("describe settles")
    });
    let err = result.err().expect("the server answers 404").to_string();
    seen.recv_timeout(Duration::from_secs(1))
        .unwrap_or_else(|_| panic!("the pinned server saw no request: {err}"))
}

#[test]
fn describe_dials_the_pinned_address_and_names_the_host_in_the_request() {
    let (addr, seen) = server();
    let request = describe_via(vec![addr], seen);
    assert!(
        request.starts_with("DESCRIBE rtsp://camera.invalid:8554/stream RTSP/1.0\r\n"),
        "{request}"
    );
}

#[test]
fn describe_moves_on_when_the_first_pinned_address_refuses() {
    let refused = {
        let listener = TcpListener::bind("127.0.0.1:0").expect("bind");
        listener.local_addr().expect("addr")
    };
    let (addr, seen) = server();
    let request = describe_via(vec![refused, addr], seen);
    assert!(request.starts_with("DESCRIBE "), "{request}");
}

#[test]
fn describe_reaches_a_later_pinned_address_behind_silent_ones() {
    // TEST-NET-1 is reserved for documentation and routed nowhere, so these
    // connection attempts are dropped rather than refused.
    let mut addrs: Vec<SocketAddr> = ["192.0.2.1:554", "192.0.2.2:554", "192.0.2.3:554"]
        .iter()
        .map(|a| a.parse().expect("addr"))
        .collect();
    let (addr, seen) = server();
    addrs.push(addr);
    let started = std::time::Instant::now();
    let request = describe_via(addrs, seen);
    assert!(request.starts_with("DESCRIBE "), "{request}");
    assert!(
        started.elapsed() < Duration::from_secs(5),
        "took {:?} to reach the fourth address",
        started.elapsed()
    );
}
