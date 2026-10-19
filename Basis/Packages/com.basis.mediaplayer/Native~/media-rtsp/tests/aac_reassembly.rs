//! AAC access units spread over several RTP packets by a sender that
//! sizes each fragment's AU-header to that fragment alone and sets the
//! marker bit only on the last packet.
//!
//! RFC 3640's fragment form states the whole access unit's size in every
//! fragment's header, which is what the depacketizer's size rung keys on.
//! Under this shape a header states only what its own packet carries, so
//! that rung cannot fire and each fragment looks like a complete access
//! unit. Emitting them separately hands the decoder one access unit in
//! pieces, which decodes to loud broadband noise on multichannel content
//! (the only content whose access units outgrow a packet). mediamtx's
//! RTSP output does this on both UDP and TCP-interleaved.

use std::num::NonZeroU32;

use media_rtsp::frame_format;
use retina::PacketContext;
use retina::codec::{CodecItem, Depacketizer};
use retina::rtp::ReceivedPacketBuilder;

const CLOCK_RATE: u32 = 48_000;
/// Mono 48 kHz AudioSpecificConfig. The channel count is immaterial to
/// reassembly; the fragmentation it provokes in the field is not.
const PARAMS: &str = "streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;\
                      indexlength=3;indexdeltalength=3;config=1188";

fn depacketizer() -> Depacketizer {
    let mut d = Depacketizer::new("audio", "mpeg4-generic", CLOCK_RATE, None, Some(PARAMS))
        .expect("aac depacketizer");
    d.set_frame_format(frame_format());
    d
}

/// One AAC-hbr packet carrying a single AU-header that states `data`'s
/// own length.
fn packet(seq: u16, ts: i64, mark: bool, loss: u16, data: &[u8]) -> retina::rtp::ReceivedPacket {
    let size = u16::try_from(data.len()).expect("fits an AU-size field") << 3;
    let mut payload = vec![0x00, 0x10];
    payload.extend_from_slice(&size.to_be_bytes());
    payload.extend_from_slice(data);
    ReceivedPacketBuilder {
        ctx: PacketContext::dummy(),
        stream_id: 0,
        sequence_number: seq,
        timestamp: retina::Timestamp::new(ts, NonZeroU32::new(CLOCK_RATE).unwrap(), 0)
            .expect("timestamp"),
        payload_type: 0,
        ssrc: 0,
        mark,
        loss,
    }
    .build(payload)
    .expect("build packet")
}

/// Push one packet and drain, the way both receive loops do.
fn feed(d: &mut Depacketizer, pkt: retina::rtp::ReceivedPacket) -> Vec<Vec<u8>> {
    d.push(pkt).expect("push accepted");
    let mut out = Vec::new();
    while let Some(item) = d.pull() {
        match item {
            Ok(CodecItem::AudioFrame(a)) => out.push(a.data().to_vec()),
            other => panic!("unexpected item: {other:?}"),
        }
    }
    out
}

/// The fragments arrive as one access unit, not as one per packet.
#[test]
fn marker_terminated_fragments_reassemble() {
    let mut d = depacketizer();
    // A complete, marked access unit first: until the stream has been seen
    // to set the bit, a clear bit says nothing.
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);
    assert!(
        feed(&mut d, packet(1, 1024, false, 0, b"frag-one")).is_empty(),
        "emitted a fragment before its marker bit"
    );
    assert_eq!(
        feed(&mut d, packet(2, 1024, true, 0, b"frag-two")),
        [b"frag-onefrag-two".to_vec()]
    );
}

/// Three packets reassemble as readily as two: the end signal is the
/// marker bit, not a packet count.
#[test]
fn reassembly_spans_more_than_two_packets() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);
    assert!(feed(&mut d, packet(1, 1024, false, 0, b"aaa")).is_empty());
    assert!(feed(&mut d, packet(2, 1024, false, 0, b"bbb")).is_empty());
    assert_eq!(
        feed(&mut d, packet(3, 1024, true, 0, b"ccc")),
        [b"aaabbbccc".to_vec()]
    );
}

/// A sender that never sets the marker bit still gets an access unit per
/// packet: a bit that is always clear carries no end-of-AU signal to wait
/// for, and waiting would stall such a stream outright.
#[test]
fn unmarked_stream_delivers_one_au_per_packet() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, false, 0, b"first")), [b"first"]);
    assert_eq!(
        feed(&mut d, packet(1, 1024, false, 0, b"second")),
        [b"second"]
    );
}

/// A complete access unit that carries the marker bit is still emitted on
/// its own packet, with no wait for anything further.
#[test]
fn marked_complete_access_units_pass_straight_through() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"one")), [b"one"]);
    assert_eq!(feed(&mut d, packet(1, 1024, true, 0, b"two")), [b"two"]);
}

/// Loss mid-reassembly drops the prefix rather than splicing across the
/// gap: the bytes either side belong to one access unit, and joining them
/// would be silent corruption.
#[test]
fn loss_discards_an_in_progress_reassembly() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);
    assert!(feed(&mut d, packet(1, 1024, false, 0, b"prefix")).is_empty());
    for au in feed(&mut d, packet(3, 1024, true, 1, b"suffix")) {
        assert!(
            !au.starts_with(b"prefix"),
            "spliced across loss: {:?}",
            String::from_utf8_lossy(&au)
        );
    }
}

/// Reassembly is bounded. With no total stated up front, a sender that
/// withholds the marker bit indefinitely would otherwise grow the buffer
/// without limit.
#[test]
fn reassembly_is_bounded() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);
    let chunk = vec![b'x'; 1400];
    let mut refused = false;
    for seq in 1..17u16 {
        if d.push(packet(seq, 1024, false, 0, &chunk)).is_err() {
            refused = true;
            break;
        }
        while d.pull().is_some() {}
    }
    assert!(refused, "unbounded reassembly accepted sixteen packets");
}

/// A refused packet takes the prefix with it. The UDP receive loop treats a
/// refusal as loss and keeps feeding the same depacketizer, so a retained
/// prefix would have the next marked packet appended to it and leave as a
/// truncated access unit carrying bytes the depacketizer had already
/// rejected.
#[test]
fn a_refused_fragment_does_not_survive_into_the_next_access_unit() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);

    // Overrun the accumulation ceiling; the prefix is all 'x'.
    let chunk = vec![b'x'; 1400];
    let mut seq = 1u16;
    loop {
        if d.push(packet(seq, 1024, false, 0, &chunk)).is_err() {
            break;
        }
        while d.pull().is_some() {}
        seq += 1;
        assert!(seq < 32, "the ceiling never refused a packet");
    }

    // The very next packet carries the marker bit and the fragment's own
    // timestamp, which is what an unreset state machine appends to and
    // emits. It must not carry the rejected prefix, and because a prefix
    // was dropped the unit completing at this marker is not trustworthy
    // either.
    seq += 1;
    let after = feed(&mut d, packet(seq, 1024, true, 0, b"tail"));
    for au in &after {
        assert!(
            !au.contains(&b'x'),
            "the rejected prefix was emitted as a truncated access unit: {au:?}"
        );
    }

    // And the stream recovers on the units that follow.
    let mut out = Vec::new();
    for (i, tail) in [b"aaaa".as_slice(), b"bbbb".as_slice(), b"cccc".as_slice()]
        .into_iter()
        .enumerate()
    {
        seq += 1;
        out.extend(feed(
            &mut d,
            packet(seq, 2048 + (i as i64) * 1024, true, 0, tail),
        ));
    }
    for au in &out {
        assert!(
            !au.contains(&b'x'),
            "a rejected prefix reached a later access unit: {au:?}"
        );
    }
    assert!(
        out.iter().any(|au| au == b"cccc"),
        "the stream never recovered after the refusal: {out:?}"
    );
}

/// The same contract on a different refusal: a mid-fragment timestamp change
/// is refused, and the prefix it was accumulating must not reach the output
/// either. The discard happens at the push boundary rather than at each
/// refusal, so this rides the same guarantee.
#[test]
fn a_prefix_refused_for_a_timestamp_change_is_also_discarded() {
    let mut d = depacketizer();
    assert_eq!(feed(&mut d, packet(0, 0, true, 0, b"whole")), [b"whole"]);
    assert!(d.push(packet(1, 1024, false, 0, b"xxxxxxxx")).is_ok());
    while d.pull().is_some() {}
    assert!(
        d.push(packet(2, 9999, false, 0, b"yyyy")).is_err(),
        "a timestamp change mid-fragment must be refused"
    );
    let mut out = Vec::new();
    for (i, tail) in [b"aaaa".as_slice(), b"cccc".as_slice()]
        .into_iter()
        .enumerate()
    {
        out.extend(feed(
            &mut d,
            packet(10 + i as u16, 20480 + (i as i64) * 1024, true, 0, tail),
        ));
    }
    for au in &out {
        assert!(
            !au.contains(&b'x'),
            "a rejected prefix reached the output: {au:?}"
        );
    }
}

/// Overrun the accumulation ceiling and return the next sequence number.
/// The marked packet first is load-bearing: reassembly only engages once the
/// stream has been seen to set the marker bit, so without it these packets
/// leave as one access unit each and nothing ever accumulates.
fn overrun(d: &mut Depacketizer, mut seq: u16, ts: i64) -> u16 {
    assert_eq!(feed(d, packet(seq, ts, true, 0, b"prime")), [b"prime"]);
    seq += 1;
    let chunk = vec![b'x'; 1400];
    while d.push(packet(seq, ts, false, 0, &chunk)).is_ok() {
        while d.pull().is_some() {}
        seq += 1;
        assert!(seq < 64, "the ceiling never refused a packet");
    }
    seq + 1
}

/// What the discard leaves behind: the reset raises the damaged flag, and a
/// reassembly completing on the next marker is dropped rather than trusted.
/// The one after it is trusted again, so the flag is spent, not sticky.
#[test]
fn the_damaged_flag_drops_the_next_reassembly() {
    let mut d = depacketizer();
    let mut seq = overrun(&mut d, 1, 1024);

    assert!(d.push(packet(seq, 8192, false, 0, b"aaaa")).is_ok());
    while d.pull().is_some() {}
    seq += 1;
    assert!(
        feed(&mut d, packet(seq, 8192, true, 0, b"bbbb")).is_empty(),
        "the first reassembly after a discard must not be trusted"
    );

    seq += 1;
    assert!(d.push(packet(seq, 12288, false, 0, b"cccc")).is_ok());
    while d.pull().is_some() {}
    seq += 1;
    assert_eq!(
        feed(&mut d, packet(seq, 12288, true, 0, b"dddd")),
        [b"ccccdddd".to_vec()],
        "the flag should clear once spent"
    );
}

/// The flag is spent by the next marker whatever carries it: a complete
/// access unit arriving in one marked packet is kept, and clears it, so a
/// reassembly after that one is trusted.
#[test]
fn a_complete_access_unit_spends_the_damaged_flag_and_survives() {
    let mut d = depacketizer();
    let mut seq = overrun(&mut d, 1, 1024);

    assert_eq!(
        feed(&mut d, packet(seq, 8192, true, 0, b"solo")),
        [b"solo"],
        "a complete access unit is not caught by the damaged flag"
    );

    seq += 1;
    assert!(d.push(packet(seq, 12288, false, 0, b"eeee")).is_ok());
    while d.pull().is_some() {}
    seq += 1;
    assert_eq!(
        feed(&mut d, packet(seq, 12288, true, 0, b"ffff")),
        [b"eeeeffff".to_vec()],
        "the marker in between should already have spent the flag"
    );
}
