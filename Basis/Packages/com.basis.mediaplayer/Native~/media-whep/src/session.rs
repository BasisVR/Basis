//! The WebRTC receive session driver: one UDP socket and one sans-IO
//! [`Rtc`] state machine on the shared I/O runtime. str0m owns ICE,
//! DTLS, SRTP and RTCP (receiver reports, NACK); decrypted RTP hands off
//! to media-rtp's reorder/jitter layer, retina's depacketizers (H.264)
//! or the trivial Opus mapping (RFC 7587: one packet, one frame), and
//! media-rtsp's shared aligner/emit path, so WHEP frames enter the engine
//! exactly where RTSP frames do.
//!
//! Address policy: every datagram str0m wants to send passes the engine's
//! address gate first. Remote ICE candidates are server-controlled (SDP
//! and trickle alike), so the check sits at the transmit boundary where
//! it cannot be bypassed, and a blocked candidate's connectivity check is
//! never sent. Inbound traffic is safe to parse from anywhere: ICE
//! consent requires message integrity with session credentials.

use media_diag::{diag_log, diag_warn};

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::num::NonZeroU32;
use std::sync::Arc;
use std::time::{Instant, SystemTime};

use bytes::Bytes;
use media_clock::{Generation, MediaTime};
use media_demux::{AudioCodec, Format, StreamEvent, TrackId};
use media_io::AddressGate;
use media_rtp::{ReceiverConfig, RtpFields, RtpReceiver, ntp_at_zero, units_to_us};
use media_rtsp::{
    ALIGN_WAIT, CHANNEL_DEPTH, MAX_STREAMS, PendingFrame, StreamAlign, emit, flush_aligned,
    format_from, frame_format, send_event,
};
use retina::codec::{CodecItem, Depacketizer};
use retina::rtp::ReceivedPacketBuilder;
use str0m::media::MediaTime as StrMediaTime;
use str0m::net::{Protocol, Receive};
use str0m::rtp::rtcp::SenderInfo;
use str0m::{Event, IceConnectionState, Input, Output, Rtc};
use tokio::net::UdpSocket;

/// How many distinct blocked destinations a session will hold and name.
/// The signalling body cap already bounds the remote's candidate list;
/// this makes the bound explicit where the list is kept.
const MAX_BLOCKED_PEERS: usize = 64;

/// Record a refused destination the first time it is seen, while the
/// bound allows. `Some(addr)` means this one has not been named before
/// and should be; `None` means it has, or that the bound is reached and
/// no further address will be recorded or named.
fn note_blocked_peer(
    seen: &mut Vec<std::net::IpAddr>,
    addr: std::net::IpAddr,
) -> Option<std::net::IpAddr> {
    if seen.len() >= MAX_BLOCKED_PEERS || seen.contains(&addr) {
        return None;
    }
    seen.push(addr);
    Some(addr)
}
use tokio::sync::mpsc;

/// Stable stream ids on the emit path (WebRTC mids are strings; the
/// engine wants small track indices).
pub(crate) const VIDEO_STREAM: usize = 0;
pub(crate) const AUDIO_STREAM: usize = 1;

#[derive(Debug, Clone, Copy)]
pub(crate) enum LaneCodec {
    H264,
    Opus { channels: u8 },
}

/// One receive lane: media-rtp reorder + depacketisation state.
pub(crate) struct Lane {
    pub(crate) stream_id: usize,
    pub(crate) codec: LaneCodec,
    /// Negotiated payload types routing to this lane (a codec can
    /// negotiate several, e.g. H.264 profiles).
    pub(crate) pts: Vec<u8>,
    pub(crate) clock_rate: NonZeroU32,
    pub(crate) receiver: RtpReceiver,
    /// H.264 goes through retina's depacketizer; Opus needs none.
    pub(crate) depacketizer: Option<Depacketizer>,
    /// First delivered packet's unwrapped RTP timestamp; stream elapsed
    /// starts at zero there.
    pub(crate) ts_start: Option<i64>,
    pub(crate) last_sr: Option<SenderInfo>,
    pub(crate) announced: bool,
    /// Packets the depacketizer would not take. Loss is expected here;
    /// a count that only ever goes up is a lane nothing can
    /// depacketize, and the two look the same from the emit path.
    pub(crate) refused: u64,
}

impl Lane {
    pub(crate) fn new(
        stream_id: usize,
        codec: LaneCodec,
        pts: Vec<u8>,
        clock_rate: u32,
    ) -> Result<Self, String> {
        let clock_rate =
            NonZeroU32::new(clock_rate).ok_or("negotiated codec with zero clock rate")?;
        let depacketizer = match codec {
            LaneCodec::H264 => {
                let mut depacketizer =
                    Depacketizer::new("video", "h264", clock_rate.get(), None, None)
                        .map_err(|e| format!("h264 depacketizer: {e}"))?;
                depacketizer.set_frame_format(frame_format());
                Some(depacketizer)
            }
            LaneCodec::Opus { .. } => None,
        };
        Ok(Self {
            stream_id,
            codec,
            pts,
            clock_rate,
            receiver: RtpReceiver::new(ReceiverConfig::new(
                clock_rate.get(),
                // Reports are never sent (str0m owns RTCP); the local
                // SSRC only labels internal state.
                stream_id as u32 + 1,
                "basis-media",
            )),
            depacketizer,
            ts_start: None,
            last_sr: None,
            refused: 0,
            announced: false,
        })
    }
}

/// A negotiated session ready for the run loop.
pub(crate) struct SessionReady {
    pub(crate) rtc: Rtc,
    pub(crate) socket: std::net::UdpSocket,
    pub(crate) local_addr: SocketAddr,
    pub(crate) gate: Arc<dyn AddressGate>,
    pub(crate) lanes: Vec<Lane>,
}

/// Unix-epoch-based NTP 32.32. The aligner only uses differences
/// between streams, so the constant NTP-era offset cancels.
fn ntp64(t: SystemTime) -> u64 {
    let d = t.duration_since(SystemTime::UNIX_EPOCH).unwrap_or_default();
    (d.as_secs() << 32) | (u64::from(d.subsec_nanos()) * (1 << 32) / 1_000_000_000)
}

pub(crate) async fn run_session(
    ready: SessionReady,
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
    mut first_frame: Option<tokio::sync::oneshot::Sender<()>>,
) -> Result<(), String> {
    let SessionReady {
        mut rtc,
        socket,
        local_addr,
        gate,
        mut lanes,
    } = ready;
    let socket = UdpSocket::from_std(socket).map_err(|e| format!("udp socket: {e}"))?;
    let needed: Vec<usize> = lanes.iter().map(|l| l.stream_id).collect();

    let epoch = Instant::now();
    let now_media = |epoch: &Instant| MediaTime::from_micros(epoch.elapsed().as_micros() as i64);

    let mut align = [StreamAlign::default(); MAX_STREAMS];
    let mut buffered: VecDeque<PendingFrame> = VecDeque::new();
    let mut aligning = true;
    let align_deadline = tokio::time::Instant::now() + ALIGN_WAIT;
    // One note per blocked address, up to a bound; a hostile candidate
    // list must not flood stderr.
    let mut blocked_peers: Vec<std::net::IpAddr> = Vec::new();

    let mut buf = vec![0u8; 65_536];
    loop {
        // Drive str0m until it reports a timeout, sending datagrams and
        // handling events as they surface.
        let str0m_deadline = loop {
            match rtc.poll_output().map_err(|e| format!("webrtc: {e}"))? {
                Output::Timeout(t) => break t,
                Output::Transmit(t) => {
                    if gate.permit(t.destination.ip()) {
                        let _ = socket.send_to(&t.contents, t.destination).await;
                    } else if let Some(named) =
                        note_blocked_peer(&mut blocked_peers, t.destination.ip())
                    {
                        diag_warn!("whep: blocked candidate address {named} (gate)");
                        if blocked_peers.len() == MAX_BLOCKED_PEERS {
                            diag_log!(
                                "whep: {MAX_BLOCKED_PEERS} blocked candidate addresses named; \
                                 no more will be named"
                            );
                        }
                    }
                }
                Output::Event(event) => match event {
                    Event::RtpPacket(packet) => {
                        let now = now_media(&epoch);
                        if let Some(lane) = lanes
                            .iter_mut()
                            .find(|l| lane_matches(l, *packet.header.payload_type))
                        {
                            if lane.last_sr.is_none() {
                                lane.last_sr = packet.last_sender_info;
                            }
                            let _ = lane.receiver.on_packet(
                                now,
                                RtpFields {
                                    seq: packet.header.sequence_number,
                                    timestamp: packet.header.timestamp,
                                    ssrc: *packet.header.ssrc,
                                    payload_type: *packet.header.payload_type,
                                    marker: packet.header.marker,
                                },
                                Bytes::copy_from_slice(&packet.payload),
                            );
                            drain_lane(lane, now, aligning, &mut buffered, &align, generation, tx)
                                .await?;
                            if let Some(first) = first_frame.take() {
                                let _ = first.send(());
                            }
                            update_alignment(lane, &mut align);
                            if aligning && needed.iter().all(|&i| align[i].ntp_at_zero.is_some()) {
                                flush_aligned(&mut buffered, &mut align, &needed, generation, tx)
                                    .await?;
                                aligning = false;
                            }
                        }
                    }
                    Event::IceConnectionStateChange(state) => {
                        diag_log!("whep ice: {state:?}");
                        if state == IceConnectionState::Disconnected {
                            // The media path died (publisher gone, NAT
                            // rebind): surface as source loss so the
                            // engine's reconnect path re-signals.
                            return Ok(());
                        }
                    }
                    Event::Connected => {
                        diag_log!("whep: connected");
                    }
                    _ => {}
                },
            }
        };

        // Reorder-gap releases wake independently of network traffic.
        let rtp_deadline = lanes
            .iter()
            .filter_map(|l| l.receiver.next_deadline())
            .min()
            .map(|deadline| {
                let now = now_media(&epoch);
                let delta = deadline.saturating_sub(now).as_micros().max(0) as u64;
                tokio::time::Instant::now() + std::time::Duration::from_micros(delta)
            });
        let mut wake = tokio::time::Instant::from_std(str0m_deadline);
        if let Some(deadline) = rtp_deadline {
            wake = wake.min(deadline);
        }
        if aligning {
            wake = wake.min(align_deadline);
        }

        tokio::select! {
            received = socket.recv_from(&mut buf) => {
                match received {
                    Ok((n, source)) => {
                        if let Ok(contents) = buf[..n].try_into() {
                            let input = Input::Receive(
                                Instant::now(),
                                Receive {
                                    proto: Protocol::Udp,
                                    source,
                                    destination: local_addr,
                                    contents,
                                },
                            );
                            let _ = rtc.handle_input(input);
                        }
                    }
                    // Windows surfaces ICMP port-unreachable for earlier
                    // sends as a receive error; the socket is fine.
                    Err(e) if matches!(
                        e.kind(),
                        std::io::ErrorKind::ConnectionReset | std::io::ErrorKind::ConnectionRefused
                    ) => {}
                    Err(e) => return Err(format!("udp receive: {e}")),
                }
            }
            _ = tokio::time::sleep_until(wake) => {
                let _ = rtc.handle_input(Input::Timeout(Instant::now()));
                let now = now_media(&epoch);
                for lane in lanes.iter_mut() {
                    drain_lane(lane, now, aligning, &mut buffered, &align, generation, tx).await?;
                }
                if aligning && tokio::time::Instant::now() >= align_deadline {
                    flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
                    aligning = false;
                }
            }
        }

        if aligning && buffered.len() >= CHANNEL_DEPTH {
            flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
            aligning = false;
        }
    }
}

fn lane_matches(lane: &Lane, pt: u8) -> bool {
    lane.pts.contains(&pt)
}

/// Release everything the reorder buffer will give up at `now` and feed
/// it through depacketisation into the shared emit path.
async fn drain_lane(
    lane: &mut Lane,
    now: MediaTime,
    aligning: bool,
    buffered: &mut VecDeque<PendingFrame>,
    align: &[StreamAlign; MAX_STREAMS],
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    while let Some(packet) = lane.receiver.poll_packet(now) {
        let start = *lane.ts_start.get_or_insert(packet.timestamp);
        // Pre-join edge: a reordered predecessor of the first delivered
        // packet has nowhere to go on a zero-based timeline.
        if packet.timestamp < start {
            continue;
        }
        match &mut lane.depacketizer {
            None => {
                // Opus (RFC 7587): one RTP packet is one frame.
                let LaneCodec::Opus { channels } = lane.codec else {
                    unreachable!("only opus lanes run without a depacketizer");
                };
                if !lane.announced {
                    send_event(
                        tx,
                        StreamEvent::Format(
                            TrackId(lane.stream_id as u32),
                            Format::Audio {
                                codec: AudioCodec::Opus,
                                sample_rate: lane.clock_rate.get(),
                                channels: u32::from(channels),
                                codec_private: opus_head(channels, lane.clock_rate.get()),
                            },
                        ),
                    )
                    .await?;
                    lane.announced = true;
                }
                let elapsed_us =
                    units_to_us(packet.timestamp.saturating_sub(start), lane.clock_rate);
                let pending = PendingFrame {
                    stream_id: lane.stream_id,
                    elapsed_us,
                    key: true,
                    data: packet.payload.to_vec(),
                };
                emit(pending, aligning, buffered, align, generation, tx).await?;
            }
            Some(depacketizer) => {
                let Some(timestamp) = retina::Timestamp::new(
                    packet.timestamp.saturating_sub(start),
                    lane.clock_rate,
                    0,
                ) else {
                    continue;
                };
                let built = ReceivedPacketBuilder {
                    ctx: retina::PacketContext::dummy(),
                    stream_id: lane.stream_id,
                    sequence_number: (packet.seq & 0xFFFF) as u16,
                    timestamp,
                    payload_type: packet.payload_type,
                    ssrc: packet.ssrc,
                    mark: packet.marker,
                    loss: packet.loss,
                }
                .build(packet.payload)
                .map_err(|e| format!("rebuild rtp packet: {e}"))?;

                // Per-packet depacketizer refusals are loss, not session
                // failure; this lane tolerates loss. The drain below is
                // unconditional because retina can queue a completed
                // access unit and then reject the same packet, and pushing
                // over unpulled output panics.
                //
                // Refusals are logged on the first and then sparsely, with
                // the running total, so a lane nothing can depacketize is
                // told apart from a burst of loss.
                if let Err(e) = depacketizer.push(built) {
                    lane.refused += 1;
                    if lane.refused == 1 || lane.refused.is_multiple_of(256) {
                        diag_log!(
                            "whep lane {}: depacketizer refused {} packet(s), latest: {e}",
                            lane.stream_id,
                            lane.refused
                        );
                    }
                }
                while let Some(item) = depacketizer.pull() {
                    let Ok(item) = item else {
                        continue;
                    };
                    if let CodecItem::VideoFrame(frame) = item {
                        if (frame.has_new_parameters() || !lane.announced)
                            && let Some(parameters) = depacketizer.parameters()
                            && let Some(format) = format_from(&parameters)
                        {
                            send_event(
                                tx,
                                StreamEvent::Format(TrackId(lane.stream_id as u32), format),
                            )
                            .await?;
                            lane.announced = true;
                        }
                        let ts = frame.timestamp();
                        let elapsed_us = units_to_us(ts.elapsed(), ts.clock_rate());
                        let pending = PendingFrame {
                            stream_id: lane.stream_id,
                            elapsed_us,
                            key: frame.is_random_access_point(),
                            data: frame.into_data(),
                        };
                        emit(pending, aligning, buffered, align, generation, tx).await?;
                    }
                }
            }
        }
    }
    Ok(())
}

/// Minimal OpusHead (the decode adapter's codec_private contract):
/// WebRTC Opus carries no container header, so state the negotiated
/// channel count with zero pre-skip.
fn opus_head(channels: u8, input_rate: u32) -> Vec<u8> {
    let mut head = Vec::with_capacity(19);
    head.extend_from_slice(b"OpusHead");
    head.push(1); // version
    head.push(channels);
    head.extend_from_slice(&0u16.to_le_bytes()); // pre-skip
    head.extend_from_slice(&input_rate.to_le_bytes());
    head.extend_from_slice(&0i16.to_le_bytes()); // output gain
    head.push(0); // mapping family 0 (mono/stereo)
    head
}

/// NTP↔RTP alignment from the stream's latest sender report, on the
/// same zero-based timeline as the emitted frames. str0m surfaces the
/// SR with each packet; the raw 32-bit RTP time is extended against the
/// receiver's own timeline exactly as the RTSP-UDP driver does.
fn update_alignment(lane: &mut Lane, align: &mut [StreamAlign; MAX_STREAMS]) {
    if lane.stream_id >= MAX_STREAMS || align[lane.stream_id].ntp_at_zero.is_some() {
        return;
    }
    let (Some(sr), Some(start)) = (lane.last_sr.as_ref(), lane.ts_start) else {
        return;
    };
    let raw = sr_rtp_raw(sr.rtp_time);
    let ext = lane.receiver.extend_report_ts(raw);
    let elapsed_us = units_to_us(ext.saturating_sub(start), lane.clock_rate);
    align[lane.stream_id].ntp_at_zero = Some(ntp_at_zero(ntp64(sr.ntp_time), elapsed_us));
}

/// The low 32 bits of str0m's extended SR RTP time are the wire value.
fn sr_rtp_raw(t: StrMediaTime) -> u32 {
    t.numer() as u32
}

#[cfg(test)]
mod tests {
    use super::*;

    fn feed(lane: &mut Lane, now: MediaTime, seq: u16, timestamp: u32, payload: &'static [u8]) {
        lane.receiver
            .on_packet(
                now,
                RtpFields {
                    seq,
                    timestamp,
                    ssrc: 0x1234_5678,
                    payload_type: 96,
                    marker: false,
                },
                Bytes::from_static(payload),
            )
            .expect("staged packet accepted");
    }

    /// A refused push can leave a completed access unit queued inside
    /// retina, and the next push over it panics. Three packets reach
    /// that state: an FU-A start, then a timestamp change mid-fragment
    /// whose own payload is rejected (F bit set), then anything at all.
    #[test]
    fn refused_push_still_drains_before_the_next_one() {
        let mut lane = Lane::new(VIDEO_STREAM, LaneCodec::H264, vec![96], 90_000).unwrap();
        let fed_at = MediaTime::from_millis(0);
        feed(&mut lane, fed_at, 1000, 9_000, &[0x7C, 0x85, 0x42]);
        feed(&mut lane, fed_at, 1001, 12_000, &[0x9C, 0x42]);
        feed(&mut lane, fed_at, 1002, 12_000, &[0x65, 0x42]);
        // Drained past the reorder window, as the RTSP twin is: releasing
        // a sequential run straight away is policy rather than contract,
        // and a change to it should not fail this row.
        let now = fed_at
            + ReceiverConfig::new(90_000, 1, "basis-media").reorder_wait
            + MediaTime::from_millis(1);

        let (tx, _rx) = mpsc::channel(16);
        let mut buffered = VecDeque::new();
        let align = [StreamAlign::default(); MAX_STREAMS];
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
            .block_on(drain_lane(
                &mut lane,
                now,
                true,
                &mut buffered,
                &align,
                Generation::default(),
                &tx,
            ))
            .expect("lane survives a refused push");
        // The queued-output state this guards is only reachable through
        // a refusal, so a depacketizer that took all three packets would
        // leave the row passing over a state it never built.
        assert!(
            lane.refused > 0,
            "nothing was refused, so the drain never covered the push that panicked"
        );
    }

    #[test]
    fn blocked_peers_are_named_once_each_and_bounded() {
        use std::net::{IpAddr, Ipv4Addr};

        let mut seen = Vec::new();
        let addr = |n: u32| IpAddr::V4(Ipv4Addr::from(n));

        assert_eq!(note_blocked_peer(&mut seen, addr(1)), Some(addr(1)));
        assert_eq!(
            note_blocked_peer(&mut seen, addr(1)),
            None,
            "an address already named is not named twice"
        );

        for n in 2..=MAX_BLOCKED_PEERS as u32 {
            assert_eq!(note_blocked_peer(&mut seen, addr(n)), Some(addr(n)));
        }
        assert_eq!(seen.len(), MAX_BLOCKED_PEERS);

        // Past the bound nothing is recorded, so the list stops growing and
        // the scan over it stops lengthening.
        for n in 1_000..2_000 {
            assert_eq!(note_blocked_peer(&mut seen, addr(n)), None);
        }
        assert_eq!(seen.len(), MAX_BLOCKED_PEERS, "the list stopped growing");
    }
}
