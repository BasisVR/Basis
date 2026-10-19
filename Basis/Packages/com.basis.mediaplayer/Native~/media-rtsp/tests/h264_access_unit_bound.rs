//! How much one H.264 access unit may gather over RTP. The depacketizer
//! closes an access unit on the marker bit or a new timestamp, and a sender
//! that sends neither would otherwise grow it for as long as it keeps
//! sending. Both the RTSP lanes and WHEP feed this depacketizer. The limits
//! are written out here rather than read from the depacketizer, so a change
//! to them has to come through these rows.

use std::num::NonZeroU32;

use media_rtsp::frame_format;
use retina::PacketContext;
use retina::codec::Depacketizer;
use retina::rtp::ReceivedPacketBuilder;

const CLOCK_RATE: u32 = 90_000;
const FRAGMENT: usize = 1400;

fn depacketizer() -> Depacketizer {
    let mut d = Depacketizer::new("video", "h264", CLOCK_RATE, None, None).expect("h264");
    d.set_frame_format(frame_format());
    d
}

fn packet(seq: u16, ts: i64, mark: bool, payload: Vec<u8>) -> retina::rtp::ReceivedPacket {
    ReceivedPacketBuilder {
        ctx: PacketContext::dummy(),
        stream_id: 0,
        sequence_number: seq,
        timestamp: retina::Timestamp::new(ts, NonZeroU32::new(CLOCK_RATE).unwrap(), 0)
            .expect("timestamp"),
        payload_type: 96,
        ssrc: 0,
        mark,
        loss: 0,
    }
    .build(payload)
    .expect("build packet")
}

/// An IDR slice fragment (FU-A), the first one flagged as the start.
fn fu_a(start: bool) -> Vec<u8> {
    let mut payload = vec![0x7C, if start { 0x85 } else { 0x05 }];
    payload.resize(2 + FRAGMENT, 0x11);
    payload
}

/// Pushes `payload(n)` for the `n`th packet, at one timestamp with no
/// marker, until the depacketizer refuses. Returns the depacketizer and how
/// many packets it took, the refused one included. Panics past `limit`.
fn packets_until_refused(
    limit: usize,
    payload: impl Fn(usize) -> Vec<u8>,
) -> (Depacketizer, usize) {
    let mut d = depacketizer();
    for n in 0..limit {
        if d.push(packet(n as u16, 0, false, payload(n))).is_err() {
            return (d, n + 1);
        }
        while d.pull().is_some() {}
    }
    panic!("still gathering one access unit after {limit} packets");
}

/// One access unit may gather 16 MiB of RTP payload: every packet up to it
/// is accepted, and the first past it refused.
#[test]
fn an_access_unit_is_refused_at_the_first_packet_past_16_mib() {
    const LIMIT: usize = 16 << 20;
    let (mut d, refused) = packets_until_refused(2 * LIMIT / FRAGMENT, |n| fu_a(n == 0));
    let packet_len = FRAGMENT + 2;
    assert!(
        (refused - 1) * packet_len <= LIMIT && refused * packet_len > LIMIT,
        "refused at packet {refused}, {} bytes in",
        refused * packet_len
    );

    // The refusal leaves the depacketizer ready for the next access unit.
    d.push(packet(refused as u16, 3000, false, vec![0x65, 0x11, 0x11]))
        .expect("a fresh access unit after the refusal");
}

/// One access unit may hold 65,536 NAL units, clear of a 4K picture's
/// 36,864 single-macroblock slices and its parameter sets; the next one is
/// refused.
#[test]
fn an_access_unit_is_refused_at_its_65537th_nal_unit() {
    let (_, refused) = packets_until_refused(100_000, |_| vec![0x65]);
    assert_eq!(refused, 65_537);
}

/// One access unit may hold 65,536 payload pieces; one-byte fragments reach
/// that long before the payload ceiling, and the next one is refused.
#[test]
fn an_access_unit_is_refused_at_its_65537th_fragment() {
    let (_, refused) = packets_until_refused(100_000, |n| {
        vec![0x7C, if n == 0 { 0x85 } else { 0x05 }, 0x11]
    });
    assert_eq!(refused, 65_537);
}
