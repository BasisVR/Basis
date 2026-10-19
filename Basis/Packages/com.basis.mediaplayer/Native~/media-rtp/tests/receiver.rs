//! Table-driven receive rows over injectable schedules: no sleeps, no
//! sockets, and `now` handed in per call.

use std::num::NonZeroU32;

use bytes::Bytes;
use media_clock::MediaTime;
use media_rtp::{PacketRejected, ReceiverConfig, RtpReceiver, ntp_at_zero, units_to_us};
use rtcp::sender_report::SenderReport;
use webrtc_util::marshal::Marshal;

const SSRC: u32 = 0x1234_5678;

fn config() -> ReceiverConfig {
    ReceiverConfig::new(90_000, 0xBA51_50DA, "basis-media-test")
}

fn rtp_datagram(seq: u16, timestamp: u32, ssrc: u32, payload: &[u8]) -> Vec<u8> {
    let packet = rtp::packet::Packet {
        header: rtp::header::Header {
            version: 2,
            payload_type: 96,
            sequence_number: seq,
            timestamp,
            ssrc,
            ..Default::default()
        },
        payload: Bytes::copy_from_slice(payload),
    };
    packet.marshal().expect("marshal").to_vec()
}

fn ms(n: i64) -> MediaTime {
    MediaTime::from_millis(n)
}

/// Feed a datagram and drain everything releasable at `now`.
fn feed(rx: &mut RtpReceiver, now: MediaTime, datagram: &[u8]) -> Vec<u64> {
    let _ = rx.on_rtp(now, datagram);
    drain(rx, now)
}

fn drain(rx: &mut RtpReceiver, now: MediaTime) -> Vec<u64> {
    let mut seqs = Vec::new();
    while let Some(packet) = rx.poll_packet(now) {
        seqs.push(packet.seq);
    }
    seqs
}

#[test]
fn in_order_passthrough() {
    let mut rx = RtpReceiver::new(config());
    for seq in 0..5u16 {
        let released = feed(
            &mut rx,
            ms(i64::from(seq) * 10),
            &rtp_datagram(100 + seq, 3000 * u32::from(seq), SSRC, b"payload"),
        );
        assert_eq!(released, vec![u64::from(100 + seq)]);
    }
    let stats = rx.stats();
    assert_eq!(stats.received, 5);
    assert_eq!(stats.lost, 0);
    assert_eq!(stats.reordered, 0);
}

#[test]
fn single_swap_reorders() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(10, 0, SSRC, b"a")), [10]);
    // 12 arrives before 11: held, then both release when 11 lands.
    assert!(feed(&mut rx, ms(10), &rtp_datagram(12, 6000, SSRC, b"c")).is_empty());
    assert_eq!(
        feed(&mut rx, ms(20), &rtp_datagram(11, 3000, SSRC, b"b")),
        [11, 12]
    );
    let stats = rx.stats();
    assert_eq!(stats.reordered, 1);
    assert_eq!(stats.lost, 0);
}

#[test]
fn gap_releases_after_wait_and_late_arrival_drops() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(10, 0, SSRC, b"a")), [10]);
    assert!(feed(&mut rx, ms(10), &rtp_datagram(12, 6000, SSRC, b"c")).is_empty());
    // Deadline reflects the open gap.
    assert_eq!(rx.next_deadline(), Some(ms(10) + ms(100)));
    assert!(rx.poll_packet(ms(109)).is_none());
    let released = rx.poll_packet(ms(110)).expect("released");
    assert_eq!(released.seq, 12);
    assert_eq!(released.loss, 1);
    assert_eq!(rx.stats().lost, 1);
    // The missing packet arriving now is late, not a re-delivery.
    assert_eq!(
        rx.on_rtp(ms(120), &rtp_datagram(11, 3000, SSRC, b"b")),
        Err(PacketRejected::Late)
    );
    assert_eq!(rx.stats().late_dropped, 1);
}

#[test]
fn depth_overflow_forces_release() {
    let mut rx = RtpReceiver::new(ReceiverConfig {
        reorder_depth: 4,
        ..config()
    });
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(0, 0, SSRC, b"a")), [0]);
    // Sequence 1 never arrives; 2..=6 pile up past the depth of 4.
    for seq in 2..=5u16 {
        assert!(feed(&mut rx, ms(1), &rtp_datagram(seq, 0, SSRC, b"x")).is_empty());
    }
    // The fifth insert exceeds depth: gap released without waiting.
    assert_eq!(
        feed(&mut rx, ms(2), &rtp_datagram(6, 0, SSRC, b"x")),
        [2, 3, 4, 5, 6]
    );
    assert_eq!(rx.stats().lost, 1);
}

#[test]
fn duplicate_and_foreign_ssrc_reject() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(5, 0, SSRC, b"a")), [5]);
    assert!(feed(&mut rx, ms(1), &rtp_datagram(7, 0, SSRC, b"c")).is_empty());
    assert_eq!(
        rx.on_rtp(ms(2), &rtp_datagram(7, 0, SSRC, b"c")),
        Err(PacketRejected::Duplicate)
    );
    assert_eq!(
        rx.on_rtp(ms(3), &rtp_datagram(8, 0, 0xDEAD_BEEF, b"zz")),
        Err(PacketRejected::ForeignSsrc)
    );
    let stats = rx.stats();
    assert_eq!(stats.duplicates, 1);
    assert_eq!(stats.foreign_ssrc, 1);
    assert_eq!(rx.remote_ssrc(), Some(SSRC));
}

#[test]
fn sequence_wrap_extends() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(
        feed(&mut rx, ms(0), &rtp_datagram(65_534, 0, SSRC, b"a")),
        [65_534]
    );
    assert_eq!(
        feed(&mut rx, ms(10), &rtp_datagram(65_535, 0, SSRC, b"b")),
        [65_535]
    );
    assert_eq!(
        feed(&mut rx, ms(20), &rtp_datagram(0, 0, SSRC, b"c")),
        [65_536]
    );
    assert_eq!(
        feed(&mut rx, ms(30), &rtp_datagram(1, 0, SSRC, b"d")),
        [65_537]
    );
    assert_eq!(rx.stats().lost, 0);
}

#[test]
fn timestamp_wrap_unwraps_monotonically() {
    let mut rx = RtpReceiver::new(config());
    let _ = rx.on_rtp(ms(0), &rtp_datagram(1, u32::MAX - 1500, SSRC, b"a"));
    let first = rx.poll_packet(ms(0)).expect("first");
    let _ = rx.on_rtp(ms(33), &rtp_datagram(2, 1500, SSRC, b"b"));
    let second = rx.poll_packet(ms(33)).expect("second");
    assert_eq!(second.timestamp - first.timestamp, 3001);
}

#[test]
fn jitter_matches_hand_computed_rfc_value() {
    // 90 kHz clock; packets 33 ms apart in RTP time (2970 units) but the
    // second arrives 10 ms late: transit delta = 900 units.
    let mut rx = RtpReceiver::new(config());
    let _ = rx.on_rtp(ms(0), &rtp_datagram(1, 0, SSRC, b"a"));
    drain(&mut rx, ms(0));
    let _ = rx.on_rtp(ms(43), &rtp_datagram(2, 2970, SSRC, b"b"));
    drain(&mut rx, ms(43));
    // J = 0 + (|D| - 0) / 16 = 900/16
    let jitter = rx.stats().jitter;
    assert!((jitter - 900.0 / 16.0).abs() < 1.0, "jitter {jitter}");
}

#[test]
fn receiver_report_cadence_and_fields() {
    let mut rx = RtpReceiver::new(config());
    // No RTP yet: nothing to report.
    assert!(rx.poll_rtcp_out(ms(0)).is_none());
    assert_eq!(
        feed(&mut rx, ms(0), &rtp_datagram(100, 0, SSRC, b"a")),
        [100]
    );
    // First report due 500 ms after the first packet.
    assert!(rx.poll_rtcp_out(ms(499)).is_none());

    // Lose 101 (released via wait), receive 102.
    assert!(feed(&mut rx, ms(10), &rtp_datagram(102, 0, SSRC, b"c")).is_empty());
    assert_eq!(drain(&mut rx, ms(200)), [102]);

    let compound = rx.poll_rtcp_out(ms(500)).expect("rr due");
    let mut buf = &compound[..];
    let packets = rtcp::packet::unmarshal(&mut buf).expect("parse compound");
    assert_eq!(packets.len(), 2, "RR + SDES");
    let rr = packets[0]
        .as_any()
        .downcast_ref::<rtcp::receiver_report::ReceiverReport>()
        .expect("receiver report first");
    assert_eq!(rr.ssrc, 0xBA51_50DA);
    let report = &rr.reports[0];
    assert_eq!(report.ssrc, SSRC);
    assert_eq!(report.total_lost, 1);
    assert_eq!(report.last_sequence_number, 102);
    // expected 3, received 2 since start: fraction = 1*256/3.
    assert_eq!(report.fraction_lost, 85);
    // Next report a full interval later.
    assert!(rx.poll_rtcp_out(ms(5_499)).is_none());
    assert!(rx.poll_rtcp_out(ms(5_500)).is_some());
}

#[test]
fn sender_report_feeds_alignment_and_lsr_dlsr() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(1, 0, SSRC, b"a")), [1]);

    let sr = SenderReport {
        ssrc: SSRC,
        ntp_time: 0xABCD_EF01_2345_6789,
        rtp_time: 4_000,
        packet_count: 1,
        octet_count: 7,
        ..Default::default()
    };
    let datagram = sr.marshal().expect("marshal sr");
    rx.on_rtcp(ms(100), &datagram).expect("sr accepted");
    let info = rx.sender_info().expect("sender info");
    assert_eq!(info.ntp, 0xABCD_EF01_2345_6789);
    assert_eq!(info.rtp_timestamp, 4_000);
    assert_eq!(info.received_at, ms(100));

    let compound = rx.poll_rtcp_out(ms(600)).expect("rr due");
    let mut buf = &compound[..];
    let packets = rtcp::packet::unmarshal(&mut buf).expect("parse");
    let rr = packets[0]
        .as_any()
        .downcast_ref::<rtcp::receiver_report::ReceiverReport>()
        .expect("rr");
    let report = &rr.reports[0];
    assert_eq!(report.last_sender_report, 0xEF01_2345);
    // 500 ms since the SR arrived, in 1/65536 s units.
    assert_eq!(report.delay, 65_536 / 2);
}

#[test]
fn foreign_sender_report_ignored_and_bye_latches() {
    let mut rx = RtpReceiver::new(config());
    assert_eq!(feed(&mut rx, ms(0), &rtp_datagram(1, 0, SSRC, b"a")), [1]);

    let foreign = SenderReport {
        ssrc: 0x9999_9999,
        ntp_time: 42,
        ..Default::default()
    };
    rx.on_rtcp(ms(10), &foreign.marshal().expect("marshal"))
        .expect("parsed fine");
    assert!(rx.sender_info().is_none(), "foreign SR must not align us");

    assert!(!rx.bye_received());
    let bye = rtcp::goodbye::Goodbye {
        sources: vec![SSRC],
        reason: Bytes::from_static(b"done"),
    };
    rx.on_rtcp(ms(20), &bye.marshal().expect("marshal"))
        .expect("bye parsed");
    assert!(rx.bye_received());
}

#[test]
fn parsed_fields_entry_matches_datagram_entry() {
    // The WHEP lane feeds already-parsed packets (str0m terminates
    // SRTP); the fields entry must behave exactly like the wire entry —
    // same pinning, reorder and extension.
    use media_rtp::RtpFields;
    let fields = |seq: u16, timestamp: u32, ssrc: u32| RtpFields {
        seq,
        timestamp,
        ssrc,
        payload_type: 96,
        marker: false,
    };
    let mut rx = RtpReceiver::new(config());
    rx.on_packet(ms(0), fields(100, 0, SSRC), Bytes::from_static(b"a"))
        .expect("first packet pins");
    assert_eq!(drain(&mut rx, ms(0)), vec![100]);

    // Reordered arrival releases in order once the gap fills.
    rx.on_packet(ms(10), fields(102, 6000, SSRC), Bytes::from_static(b"c"))
        .expect("gap parks");
    assert_eq!(drain(&mut rx, ms(10)), Vec::<u64>::new());
    rx.on_packet(ms(20), fields(101, 3000, SSRC), Bytes::from_static(b"b"))
        .expect("gap fills");
    assert_eq!(drain(&mut rx, ms(20)), vec![101, 102]);

    // Foreign SSRC still rejected on the parsed path.
    assert!(matches!(
        rx.on_packet(ms(30), fields(103, 9000, 0xDEAD_BEEF), Bytes::new()),
        Err(PacketRejected::ForeignSsrc)
    ));
    assert_eq!(rx.stats().foreign_ssrc, 1);
}

#[test]
fn malformed_datagrams_are_counted_not_fatal() {
    let mut rx = RtpReceiver::new(config());
    assert!(matches!(
        rx.on_rtp(ms(0), &[0x80, 0x60]),
        Err(PacketRejected::Malformed(_))
    ));
    assert!(matches!(
        rx.on_rtcp(ms(0), &[0x00]),
        Err(PacketRejected::Malformed(_))
    ));
    assert_eq!(rx.stats().malformed, 1);
    // A good packet still flows afterwards.
    assert_eq!(feed(&mut rx, ms(1), &rtp_datagram(1, 0, SSRC, b"ok")), [1]);
}

/// A sender picks its own RTP timestamps and each packet can move the
/// unwrapped total by up to 2^31, so the span handed to the µs conversion
/// has no ceiling. Scaled in i64 it would wrap a few thousand hostile
/// packets in, silently in release builds, leaving an arbitrary value as
/// a frame's presentation timestamp or a stream's alignment anchor.
#[test]
fn a_hostile_timestamp_span_saturates_rather_than_wrapping() {
    let rate = NonZeroU32::new(90_000).expect("nonzero");
    assert_eq!(units_to_us(i64::MAX, rate), i64::MAX);
    assert_eq!(units_to_us(i64::MIN, rate), i64::MIN);
    // The first span past what an i64 multiply survives: the conversion
    // still lands on the exact answer, which only fits once widened.
    let overflows = i64::MAX / 1_000_000 + 1;
    assert!(overflows.checked_mul(1_000_000).is_none());
    let exact = (i128::from(overflows) * 1_000_000 / 90_000) as i64;
    assert_eq!(units_to_us(overflows, rate), exact);

    // Ordinary spans are unchanged, sign included.
    assert_eq!(units_to_us(90_000, rate), 1_000_000);
    assert_eq!(units_to_us(-90_000, rate), -1_000_000);
    assert_eq!(units_to_us(0, rate), 0);
    let opus = NonZeroU32::new(48_000).expect("nonzero");
    assert_eq!(units_to_us(960, opus), 20_000);
}

/// The alignment anchor is a sender report's NTP shifted back by how far
/// into the stream it was taken, in 32.32 fixed point. The shift has to
/// survive what `units_to_us` hands it, whose saturated ends are exactly
/// the values a float conversion rounds off and an `as u64` truncates.
#[test]
fn the_alignment_anchor_survives_a_saturated_elapsed() {
    const NTP: u64 = 0xE0FF_1234_5678_9ABC;
    // One second in, the anchor sits exactly 2^32 ticks earlier.
    assert_eq!(ntp_at_zero(NTP, 1_000_000), NTP - (1 << 32));
    // A report at elapsed zero is the anchor.
    assert_eq!(ntp_at_zero(NTP, 0), NTP);
    // Negative elapsed moves it the other way by the same magnitude.
    assert_eq!(ntp_at_zero(NTP, -1_000_000), NTP + (1 << 32));

    // Saturated ends clamp the offset rather than truncating it, and stay
    // on the correct side. i64::MIN is the edge that negating a magnitude
    // would overflow on.
    assert_eq!(ntp_at_zero(NTP, i64::MAX), NTP.wrapping_sub(u64::MAX));
    assert_eq!(ntp_at_zero(NTP, i64::MIN), NTP.wrapping_add(u64::MAX));

    // Exact at magnitudes where scaling through an f64 no longer is. The
    // divergence is sub-nanosecond and only past ~11 days of elapsed, so
    // this pins the helper's contract rather than a visible defect. Only
    // the exact value is asserted: the two paths differ by about one ULP
    // here, and asserting that they differ would depend on how the
    // compiler treats the intermediate arithmetic.
    let long_us = 1_000_000_000_001_i64;
    assert_eq!(
        ntp_at_zero(NTP, long_us),
        NTP - ((u128::from(long_us as u64) << 32) / 1_000_000) as u64
    );
}
