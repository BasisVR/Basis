use bytes::Bytes;
use media_clock::MediaTime;
use rtcp::goodbye::Goodbye;
use rtcp::receiver_report::ReceiverReport;
use rtcp::reception_report::ReceptionReport;
use rtcp::sender_report::SenderReport;
use rtcp::source_description::{
    SdesType, SourceDescription, SourceDescriptionChunk, SourceDescriptionItem,
};
use webrtc_util::marshal::Marshal;

use crate::receiver::{PacketRejected, ReceiverConfig};

/// First report goes out shortly after the stream starts flowing —
/// servers that gate UDP liveness on RRs should not wait a full
/// interval for the first one.
const INITIAL_RR_DELAY: MediaTime = MediaTime::from_millis(500);

/// The latest sender report's NTP↔RTP mapping. NTP is the report's
/// 64-bit 32.32 fixed-point value; `received_at` is the local arrival.
#[derive(Debug, Clone, Copy)]
pub struct SenderInfo {
    pub ntp: u64,
    pub rtp_timestamp: u32,
    pub received_at: MediaTime,
}

#[derive(Default)]
pub(crate) struct ReportState {
    last_sr: Option<SenderInfo>,
    next_rr: Option<MediaTime>,
    expected_prior: u64,
    received_prior: u64,
    bye: bool,
}

impl ReportState {
    pub(crate) fn on_rtp_accepted(&mut self, now: MediaTime) {
        if self.next_rr.is_none() {
            self.next_rr = Some(now + INITIAL_RR_DELAY);
        }
    }

    pub(crate) fn on_rtcp(
        &mut self,
        now: MediaTime,
        datagram: &[u8],
        remote_ssrc: Option<u32>,
    ) -> Result<(), PacketRejected> {
        // Same fence as the RTP side: the webrtc-rs parsers have
        // panic paths on hostile length fields.
        let mut buf = datagram;
        let parsed = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            rtcp::packet::unmarshal(&mut buf)
        }));
        let packets = match parsed {
            Ok(Ok(packets)) => packets,
            Ok(Err(e)) => return Err(PacketRejected::Malformed(e.to_string())),
            Err(_) => return Err(PacketRejected::Malformed("rtcp parser panicked".into())),
        };
        for packet in packets {
            let any = packet.as_any();
            if let Some(sr) = any.downcast_ref::<SenderReport>() {
                // One media sender per stream: accept the SR that
                // matches the pinned source, or any SR while unpinned
                // (RTCP can arrive before the first RTP packet).
                if remote_ssrc.is_none_or(|ssrc| ssrc == sr.ssrc) {
                    self.last_sr = Some(SenderInfo {
                        ntp: sr.ntp_time,
                        rtp_timestamp: sr.rtp_time,
                        received_at: now,
                    });
                }
            } else if let Some(bye) = any.downcast_ref::<Goodbye>()
                && remote_ssrc.is_none_or(|ssrc| bye.sources.contains(&ssrc))
            {
                self.bye = true;
            }
        }
        Ok(())
    }

    #[allow(clippy::too_many_arguments)]
    pub(crate) fn poll_rr(
        &mut self,
        now: MediaTime,
        config: &ReceiverConfig,
        remote_ssrc: u32,
        expected: u64,
        received: u64,
        highest_seq: u32,
        jitter: u32,
    ) -> Option<Vec<u8>> {
        let due = self.next_rr?;
        if now < due {
            return None;
        }
        self.next_rr = Some(now + config.rr_interval);

        let expected_interval = expected.saturating_sub(self.expected_prior);
        let received_interval = received.saturating_sub(self.received_prior);
        self.expected_prior = expected;
        self.received_prior = received;
        let lost_interval = expected_interval.saturating_sub(received_interval);
        let fraction_lost = (lost_interval * 256)
            .checked_div(expected_interval)
            .unwrap_or(0)
            .min(255) as u8;
        let total_lost = expected.saturating_sub(received).min(0x00FF_FFFF) as u32;

        let (last_sender_report, delay) = match self.last_sr {
            Some(sr) => {
                let lsr = ((sr.ntp >> 16) & 0xFFFF_FFFF) as u32;
                let elapsed = now.saturating_sub(sr.received_at).as_micros().max(0);
                let dlsr =
                    ((elapsed as u128 * 65_536) / 1_000_000).min(u128::from(u32::MAX)) as u32;
                (lsr, dlsr)
            }
            None => (0, 0),
        };

        let rr = ReceiverReport {
            ssrc: config.local_ssrc,
            reports: vec![ReceptionReport {
                ssrc: remote_ssrc,
                fraction_lost,
                total_lost,
                last_sequence_number: highest_seq,
                jitter,
                last_sender_report,
                delay,
            }],
            profile_extensions: Bytes::new(),
        };
        let sdes = SourceDescription {
            chunks: vec![SourceDescriptionChunk {
                source: config.local_ssrc,
                items: vec![SourceDescriptionItem {
                    sdes_type: SdesType::SdesCname,
                    text: Bytes::from(config.cname.clone()),
                }],
            }],
        };
        let mut out = Vec::new();
        for marshalled in [rr.marshal(), sdes.marshal()] {
            match marshalled {
                Ok(bytes) => out.extend_from_slice(&bytes),
                Err(_) => return None,
            }
        }
        Some(out)
    }

    pub(crate) fn next_rr(&self) -> Option<MediaTime> {
        self.next_rr
    }

    pub(crate) fn sender_info(&self) -> Option<SenderInfo> {
        self.last_sr
    }

    pub(crate) fn bye_received(&self) -> bool {
        self.bye
    }
}
