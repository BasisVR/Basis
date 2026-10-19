use std::collections::BTreeMap;

use bytes::Bytes;
use media_clock::MediaTime;
use rtp::packet::Packet as RtpPacket;
use webrtc_util::marshal::Unmarshal;

use crate::reports::{ReportState, SenderInfo};

/// Per-stream receive configuration. `clock_rate` is the RTP clock from
/// the SDP/rtpmap; `local_ssrc` identifies our reports (caller-chosen —
/// this crate has no randomness source).
#[derive(Debug, Clone)]
pub struct ReceiverConfig {
    pub clock_rate: u32,
    pub local_ssrc: u32,
    pub cname: String,
    /// Reorder buffer packet cap; exceeding it releases the oldest gap
    /// immediately. Bounds memory to roughly `reorder_depth` MTUs.
    pub reorder_depth: usize,
    /// How long a sequence gap holds delivery before the missing
    /// packets are declared lost. Covers reordering only; jitter
    /// absorption belongs to the buffering layer downstream.
    pub reorder_wait: MediaTime,
    /// Receiver-report cadence once the stream is flowing.
    pub rr_interval: MediaTime,
}

impl ReceiverConfig {
    pub fn new(clock_rate: u32, local_ssrc: u32, cname: impl Into<String>) -> Self {
        Self {
            clock_rate,
            local_ssrc,
            cname: cname.into(),
            reorder_depth: 256,
            reorder_wait: MediaTime::from_millis(100),
            rr_interval: MediaTime::from_secs(5),
        }
    }
}

/// The header fields of one RTP packet, for callers whose transport has
/// already parsed (and possibly decrypted) the wire format — the WHEP
/// lane, where str0m terminates SRTP and hands over parsed packets. The
/// datagram entry ([`RtpReceiver::on_rtp`]) parses into exactly this.
#[derive(Debug, Clone, Copy)]
pub struct RtpFields {
    /// Wire (16-bit) sequence number; the receiver runs its own cycle
    /// extension.
    pub seq: u16,
    /// Wire (32-bit) RTP timestamp; unwrapped internally.
    pub timestamp: u32,
    pub ssrc: u32,
    pub payload_type: u8,
    pub marker: bool,
}

/// An RTP packet released in sequence order.
#[derive(Debug, Clone)]
pub struct OrderedPacket {
    /// Extended (unwrapped) sequence number; low 16 bits are the wire
    /// sequence, upper bits count cycles from the first packet.
    pub seq: u64,
    /// Unwrapped RTP timestamp: the first packet's 32-bit value
    /// continued into i64 across wraps.
    pub timestamp: i64,
    pub ssrc: u32,
    pub payload_type: u8,
    pub marker: bool,
    /// Packets declared lost immediately before this one (a gap
    /// released after `reorder_wait`).
    pub loss: u16,
    pub payload: Bytes,
}

/// Why `on_rtp` did not accept a datagram. Rejections are counted in
/// [`ReceiverStats`]; callers usually just continue.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PacketRejected {
    Malformed(String),
    /// Valid RTP from an SSRC other than the pinned source.
    ForeignSsrc,
    /// Arrived after its gap was already released as lost.
    Late,
    Duplicate,
}

#[derive(Debug, Default, Clone, Copy)]
pub struct ReceiverStats {
    /// Valid packets accepted from the pinned source (includes
    /// duplicates and late arrivals, per RFC 3550 A.3).
    pub received: u64,
    /// Declared lost by gap release.
    pub lost: u64,
    /// Arrived out of sequence order (behind the highest seen).
    pub reordered: u64,
    pub duplicates: u64,
    pub late_dropped: u64,
    pub malformed: u64,
    pub foreign_ssrc: u64,
    /// RFC 3550 interarrival jitter, in clock-rate units.
    pub jitter: f64,
}

struct Pending {
    arrival: MediaTime,
    packet: OrderedPacket,
}

/// RFC 3550 header geometry: fixed header, CSRC list, and (when the X
/// bit is set) the extension header plus its stated word count must all
/// fit inside the datagram.
fn header_geometry_ok(datagram: &[u8]) -> Result<(), PacketRejected> {
    let too_short = || PacketRejected::Malformed("rtp header truncated".into());
    if datagram.len() < 12 {
        return Err(too_short());
    }
    let base = 12 + 4 * usize::from(datagram[0] & 0x0F);
    if datagram.len() < base {
        return Err(too_short());
    }
    if datagram[0] & 0x10 != 0 {
        if datagram.len() < base + 4 {
            return Err(too_short());
        }
        let words = usize::from(u16::from_be_bytes([datagram[base + 2], datagram[base + 3]]));
        if datagram.len() < base + 4 + 4 * words {
            return Err(too_short());
        }
    }
    Ok(())
}

pub struct RtpReceiver {
    config: ReceiverConfig,
    remote_ssrc: Option<u32>,
    /// Next extended sequence expected for in-order delivery.
    next_seq: Option<u64>,
    /// Highest extended sequence seen (RR's extended-highest field).
    highest_ext: Option<u64>,
    /// First extended sequence seen (base for expected-count).
    base_ext: u64,
    pending: BTreeMap<u64, Pending>,
    last_ts: Option<(u32, i64)>,
    last_transit: Option<i64>,
    stats: ReceiverStats,
    pub(crate) reports: ReportState,
}

impl RtpReceiver {
    pub fn new(config: ReceiverConfig) -> Self {
        Self {
            config,
            remote_ssrc: None,
            next_seq: None,
            highest_ext: None,
            base_ext: 0,
            pending: BTreeMap::new(),
            last_ts: None,
            last_transit: None,
            stats: ReceiverStats::default(),
            reports: ReportState::default(),
        }
    }

    /// Feed one RTP datagram. Call [`Self::poll_packet`] until it
    /// returns `None` after each acceptance; the reorder buffer is
    /// bounded on that contract.
    pub fn on_rtp(&mut self, now: MediaTime, datagram: &[u8]) -> Result<(), PacketRejected> {
        // The webrtc-rs parser advances by the header-extension length
        // field before checking the remaining buffer (rtp 0.17.2
        // header.rs, `bytes` "advance out of bounds" panic; fuzz-found,
        // input pinned in tests/data/rtp-panics/). Screen the header
        // geometry first; the unmarshal fence below covers whatever
        // this screen does not model.
        header_geometry_ok(datagram).inspect_err(|_| {
            self.stats.malformed += 1;
        })?;
        let mut buf = datagram;
        let parsed = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            RtpPacket::unmarshal(&mut buf)
        }));
        let packet = match parsed {
            Ok(Ok(packet)) => packet,
            Ok(Err(e)) => {
                self.stats.malformed += 1;
                return Err(PacketRejected::Malformed(e.to_string()));
            }
            Err(_) => {
                self.stats.malformed += 1;
                return Err(PacketRejected::Malformed("rtp parser panicked".into()));
            }
        };
        let header = &packet.header;
        self.on_packet(
            now,
            RtpFields {
                seq: header.sequence_number,
                timestamp: header.timestamp,
                ssrc: header.ssrc,
                payload_type: header.payload_type,
                marker: header.marker,
            },
            packet.payload,
        )
    }

    /// Feed one already-parsed RTP packet (the transport owns the wire
    /// format — SRTP lanes). Same contract as [`Self::on_rtp`]: poll
    /// [`Self::poll_packet`] until `None` after each acceptance.
    pub fn on_packet(
        &mut self,
        now: MediaTime,
        fields: RtpFields,
        payload: Bytes,
    ) -> Result<(), PacketRejected> {
        match self.remote_ssrc {
            None => self.remote_ssrc = Some(fields.ssrc),
            Some(ssrc) if ssrc != fields.ssrc => {
                self.stats.foreign_ssrc += 1;
                return Err(PacketRejected::ForeignSsrc);
            }
            Some(_) => {}
        }
        self.stats.received += 1;
        self.reports.on_rtp_accepted(now);

        let ext = match self.extend_seq(fields.seq) {
            Some(ext) => ext,
            None => {
                // Predecessor of the very first packet: no slot exists
                // before extension 0.
                self.stats.late_dropped += 1;
                return Err(PacketRejected::Late);
            }
        };
        match self.highest_ext {
            Some(highest) if ext <= highest => self.stats.reordered += 1,
            Some(_) | None => self.highest_ext = Some(ext),
        }

        let timestamp = self.extend_ts(fields.timestamp);
        self.update_jitter(now, timestamp);

        if let Some(next) = self.next_seq
            && ext < next
        {
            self.stats.late_dropped += 1;
            return Err(PacketRejected::Late);
        }
        if self.pending.contains_key(&ext) {
            self.stats.duplicates += 1;
            return Err(PacketRejected::Duplicate);
        }
        self.pending.insert(
            ext,
            Pending {
                arrival: now,
                packet: OrderedPacket {
                    seq: ext,
                    timestamp,
                    ssrc: fields.ssrc,
                    payload_type: fields.payload_type,
                    marker: fields.marker,
                    loss: 0,
                    payload,
                },
            },
        );
        Ok(())
    }

    /// Release the next in-order packet, or a gap-fronted packet whose
    /// wait has expired (its `loss` field counts the released gap).
    pub fn poll_packet(&mut self, now: MediaTime) -> Option<OrderedPacket> {
        let (&front, _) = self.pending.first_key_value()?;
        let deliver = match self.next_seq {
            None => true,
            Some(next) if front == next => true,
            Some(next) => {
                debug_assert!(front > next, "late packets are rejected before insert");
                self.pending.len() > self.config.reorder_depth
                    || now.saturating_sub(self.blocked_since()) >= self.config.reorder_wait
            }
        };
        if !deliver {
            return None;
        }
        let mut packet = self.pending.remove(&front).expect("front exists").packet;
        if let Some(next) = self.next_seq {
            let gap = front - next;
            self.stats.lost += gap;
            packet.loss = gap.min(u64::from(u16::MAX)) as u16;
        }
        self.next_seq = Some(front + 1);
        Some(packet)
    }

    /// When the caller should poll again with a fresh `now` even if no
    /// datagram arrives: a pending gap's release or a due report.
    pub fn next_deadline(&self) -> Option<MediaTime> {
        let gap = match (self.pending.first_key_value(), self.next_seq) {
            (Some((&front, _)), Some(next)) if front > next => {
                Some(self.blocked_since() + self.config.reorder_wait)
            }
            _ => None,
        };
        match (gap, self.reports.next_rr()) {
            (Some(a), Some(b)) => Some(a.min(b)),
            (a, b) => a.or(b),
        }
    }

    /// Feed one RTCP datagram (sender reports advance the NTP↔RTP
    /// mapping; BYE is latched).
    pub fn on_rtcp(&mut self, now: MediaTime, datagram: &[u8]) -> Result<(), PacketRejected> {
        self.reports.on_rtcp(now, datagram, self.remote_ssrc)
    }

    /// A due receiver report (RR + SDES CNAME compound), serialized for
    /// the wire. `None` until the interval elapses or before any RTP
    /// packet has been accepted.
    pub fn poll_rtcp_out(&mut self, now: MediaTime) -> Option<Vec<u8>> {
        let remote_ssrc = self.remote_ssrc?;
        let highest = self.highest_ext?;
        let expected = highest - self.base_ext + 1;
        self.reports.poll_rr(
            now,
            &self.config,
            remote_ssrc,
            expected,
            self.stats.received,
            highest as u32,
            self.stats.jitter as u32,
        )
    }

    /// NTP↔RTP mapping from the latest sender report, for cross-stream
    /// alignment.
    pub fn sender_info(&self) -> Option<SenderInfo> {
        self.reports.sender_info()
    }

    /// The remote sender said goodbye; the session is over.
    pub fn bye_received(&self) -> bool {
        self.reports.bye_received()
    }

    pub fn remote_ssrc(&self) -> Option<u32> {
        self.remote_ssrc
    }

    pub fn stats(&self) -> ReceiverStats {
        self.stats
    }

    /// Unwrap the RTP timestamp of a sender report against the same
    /// timeline as the media packets, for alignment maths.
    pub fn extend_report_ts(&self, raw: u32) -> i64 {
        self.extend_ts_readonly(raw)
    }

    /// Extend a 16-bit sequence to the cycle nearest the highest seen.
    /// The first packet defines cycle zero; `None` means the value
    /// precedes it.
    fn extend_seq(&mut self, seq: u16) -> Option<u64> {
        let highest = match self.highest_ext {
            None => {
                self.base_ext = u64::from(seq);
                return Some(u64::from(seq));
            }
            Some(h) => h as i64,
        };
        let cycle_base = highest & !0xFFFF_i64;
        let candidate = [-0x1_0000_i64, 0, 0x1_0000]
            .iter()
            .map(|offset| cycle_base + offset + i64::from(seq))
            .min_by_key(|c| (c - highest).abs())
            .expect("non-empty");
        u64::try_from(candidate).ok()
    }

    fn extend_ts(&mut self, raw: u32) -> i64 {
        let ext = self.extend_ts_readonly(raw);
        self.last_ts = Some((raw, ext));
        ext
    }

    fn extend_ts_readonly(&self, raw: u32) -> i64 {
        match self.last_ts {
            None => i64::from(raw),
            Some((last_raw, last_ext)) => {
                let delta = i64::from((raw.wrapping_sub(last_raw)) as i32);
                last_ext + delta
            }
        }
    }

    /// Earliest arrival among buffered packets: any buffered packet
    /// implies the gap ahead of it has been open since it arrived.
    fn blocked_since(&self) -> MediaTime {
        self.pending
            .values()
            .map(|p| p.arrival)
            .min()
            .unwrap_or(MediaTime::ZERO)
    }

    /// RFC 3550 A.8, computed in arrival order (pre-reorder).
    fn update_jitter(&mut self, now: MediaTime, timestamp: i64) {
        let arrival_units =
            (i128::from(now.as_micros()) * i128::from(self.config.clock_rate) / 1_000_000) as i64;
        let transit = arrival_units - timestamp;
        if let Some(last) = self.last_transit {
            let d = (transit - last).abs() as f64;
            self.stats.jitter += (d - self.stats.jitter) / 16.0;
        }
        self.last_transit = Some(transit);
    }
}
