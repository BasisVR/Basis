//! Fuzz-found parser panic inputs, replayed through the same
//! tag/length-framed walk as the `rtp_session` fuzz target. The
//! webrtc-rs parsers have panic paths on hostile length fields (rtp
//! 0.17.2 advances by the header-extension length before checking the
//! remaining buffer); the receiver screens header geometry and fences
//! both unmarshal calls, so every pinned input must come out as typed
//! rejections — never a panic.

use media_clock::MediaTime;
use media_rtp::{ReceiverConfig, RtpReceiver};

fn replay(data: &[u8]) {
    let mut receiver = RtpReceiver::new(ReceiverConfig::new(90_000, 0x42, "fuzz"));
    let mut now = MediaTime::ZERO;
    let mut rest = data;
    while rest.len() >= 3 {
        let tag = rest[0];
        let len = usize::from(u16::from_be_bytes([rest[1], rest[2]]) & 0x3FF);
        rest = &rest[3..];
        let take = len.min(rest.len());
        let (chunk, tail) = rest.split_at(take);
        rest = tail;
        now += MediaTime::from_micros(i64::from(tag & 0x3F) * 1_000);
        if tag & 1 == 0 {
            let _ = receiver.on_rtp(now, chunk);
            while receiver.poll_packet(now).is_some() {}
        } else {
            let _ = receiver.on_rtcp(now, chunk);
        }
        let _ = receiver.poll_rtcp_out(now);
        let _ = receiver.next_deadline();
    }
    let end = now + MediaTime::from_secs(60);
    while receiver.poll_packet(end).is_some() {}
    let _ = receiver.poll_rtcp_out(end);
}

#[test]
fn pinned_inputs_reject_without_panicking() {
    let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/tests/data/rtp-panics");
    let mut seen = 0;
    for entry in std::fs::read_dir(dir).expect("pinned dir") {
        let path = entry.expect("dir entry").path();
        replay(&std::fs::read(&path).expect("read pinned input"));
        seen += 1;
    }
    assert!(seen > 0, "no pinned inputs found");
}

#[test]
fn hostile_extension_length_is_a_typed_rejection() {
    // Minimal form of the pinned crash: X bit set, extension word
    // count far past the datagram end.
    let mut datagram = vec![0x90, 0x60, 0x00, 0x01, 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78];
    datagram.extend_from_slice(&[0xBE, 0xDE, 0xFF, 0xFF]);
    let mut receiver = RtpReceiver::new(ReceiverConfig::new(90_000, 1, "test"));
    assert!(receiver.on_rtp(MediaTime::ZERO, &datagram).is_err());
    assert_eq!(receiver.stats().malformed, 1);
}
