//! Property rows: schedule in → release schedule out. Arbitrary
//! bounded-displacement reorder with loss and duplicates must come out
//! strictly ordered with the accounting identity intact.

use bytes::Bytes;
use media_clock::MediaTime;
use media_rtp::{ReceiverConfig, RtpReceiver};
use proptest::prelude::*;
use webrtc_util::marshal::Marshal;

const SSRC: u32 = 0xC0FF_EE00;

fn rtp_datagram(seq: u16, timestamp: u32) -> Vec<u8> {
    let packet = rtp::packet::Packet {
        header: rtp::header::Header {
            version: 2,
            payload_type: 96,
            sequence_number: seq,
            timestamp,
            ssrc: SSRC,
            ..Default::default()
        },
        payload: Bytes::from_static(b"p"),
    };
    packet.marshal().expect("marshal").to_vec()
}

proptest! {
    #[test]
    fn reordered_lossy_schedule_releases_in_order(
        start_seq in 0u16..=u16::MAX,
        displacements in prop::collection::vec(-4i64..=4, 40),
        sent_mask in prop::collection::vec(prop::bool::weighted(0.9), 40),
        dup_mask in prop::collection::vec(prop::bool::weighted(0.1), 40),
    ) {
        // Build the arrival order: packet i departs at slot i, arrives at
        // slot i + displacement, ties broken by index.
        let mut arrivals: Vec<(i64, usize, bool)> = Vec::new();
        for i in 0..40usize {
            if !sent_mask[i] {
                continue;
            }
            arrivals.push((i as i64 + displacements[i], i, false));
            if dup_mask[i] {
                arrivals.push((i as i64 + displacements[i] + 1, i, true));
            }
        }
        arrivals.sort();

        let mut rx = RtpReceiver::new(ReceiverConfig::new(90_000, 1, "prop"));
        let mut delivered = Vec::new();
        for (slot, &(order, i, _dup)) in arrivals.iter().enumerate() {
            let now = MediaTime::from_millis(order.max(0) * 5 + slot as i64);
            let seq = start_seq.wrapping_add(i as u16);
            let _ = rx.on_rtp(now, &rtp_datagram(seq, (i as u32) * 3000));
            while let Some(packet) = rx.poll_packet(now) {
                delivered.push(packet);
            }
        }
        // Final drain well past every deadline.
        let end = MediaTime::from_secs(3600);
        while let Some(packet) = rx.poll_packet(end) {
            delivered.push(packet);
        }

        // Strictly increasing extended sequence, no duplicates.
        for pair in delivered.windows(2) {
            prop_assert!(pair[0].seq < pair[1].seq);
        }
        // Accounting identity: every accepted packet is delivered exactly
        // once; rejections are counted, never silently dropped.
        let stats = rx.stats();
        prop_assert_eq!(
            delivered.len() as u64,
            stats.received - stats.late_dropped - stats.duplicates
        );
        // Nothing invented: every delivered sequence maps back to a sent
        // packet's wire sequence.
        for packet in &delivered {
            let wire = (packet.seq & 0xFFFF) as u16;
            let i = wire.wrapping_sub(start_seq) as usize;
            prop_assert!(i < 40 && sent_mask[i], "unknown seq {}", packet.seq);
        }
    }
}
