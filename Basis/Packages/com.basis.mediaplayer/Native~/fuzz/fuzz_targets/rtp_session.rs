//! Fuzz the media-rtp receive path end to end: the webrtc-rs rtp/rtcp
//! parsers plus our sequence/timestamp unwrap, reorder buffer, jitter
//! accounting and receiver-report composition. The input is a stream of
//! tagged length-prefixed chunks fed alternately as RTP and RTCP
//! datagrams under an advancing synthetic clock. Arbitrary bytes must
//! produce typed rejections or ordered packets, with no panics and no
//! unbounded buffer growth (the poll-after-feed contract bounds the
//! reorder buffer).

#![no_main]

use libfuzzer_sys::fuzz_target;
use media_clock::MediaTime;
use media_rtp::{ReceiverConfig, RtpReceiver};

fuzz_target!(|data: &[u8]| {
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
    let _ = receiver.stats();
});
