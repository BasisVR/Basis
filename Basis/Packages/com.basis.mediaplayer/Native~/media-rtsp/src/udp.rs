//! UDP transport: retina keeps the RTSP state machine (DESCRIBE/SETUP/
//! PLAY, keepalives, TEARDOWN, socket bind + connect + firewall punch)
//! and hands over the connected sockets and depacketizers; `media-rtp`
//! owns receive: reorder, jitter/loss accounting and RTCP receiver
//! reports, which retina's own UDP path lacks (servers kill RR-less
//! sessions as dead). The SETUP response's `source` address is vetted
//! before any packet goes out: it is server-controlled and need
//! not match the RTSP host.

use std::collections::VecDeque;
use std::net::IpAddr;
use std::num::NonZeroU32;
use std::sync::Arc;
use std::time::Instant;

use futures::StreamExt;
use media_clock::{Generation, MediaTime};
use media_demux::{StreamEvent, TrackId};
use media_diag::diag_log;
use media_rtp::{ReceiverConfig, RtpReceiver, ntp_at_zero, units_to_us};
use retina::client::{PlayOptions, SessionOptions, SetupOptions, Transport, UdpTransportOptions};
use retina::codec::{CodecItem, Depacketizer};
use retina::rtp::ReceivedPacketBuilder;
use tokio::net::UdpSocket;
use tokio::sync::{mpsc, oneshot};
use tokio::task::JoinSet;

use crate::{
    ALIGN_WAIT, CHANNEL_DEPTH, MAX_STREAMS, PendingFrame, REPORT_GRACE, StreamAlign,
    declared_starts_agree, emit, flush_aligned, format_from, frame_format, realign_late,
    reports_complete, select_streams, send_event,
};

/// Peer-address policy for UDP targets, backed by the engine's one
/// address gate.
pub type UdpPeerAllowed = Arc<dyn Fn(IpAddr) -> bool + Send + Sync>;

pub(crate) struct UdpStream {
    index: usize,
    clock_rate: NonZeroU32,
    rtp: Arc<UdpSocket>,
    rtcp: Arc<UdpSocket>,
    receiver: RtpReceiver,
    depacketizer: Depacketizer,
    /// The PLAY response's `rtptime` for this stream, when the server gave
    /// one for every stream.
    play_rtptime: Option<u32>,
    /// Unwrapped RTP timestamp at which stream elapsed is zero, latched at
    /// the first delivered packet: `play_rtptime` where it is usable (see
    /// [`rtptime_zero`]), otherwise that packet's own timestamp.
    ts_start: Option<i64>,
    /// `ts_start` is the first delivered packet rather than `rtptime`.
    zero_is_first_packet: bool,
    announced: bool,
    /// Packets the depacketizer would not take. Loss is expected on this
    /// lane; a count that only ever goes up is a stream nothing can
    /// depacketize, and the two look the same from the emit path.
    refused: u64,
}

pub(crate) struct UdpReady {
    session: retina::client::Session<retina::client::Playing>,
    streams: Vec<UdpStream>,
    video_index: Option<usize>,
    audio_index: Option<usize>,
}

/// Derive a local SSRC for our receiver reports. Uniqueness matters
/// only within the session; a hash of the stream index seeded by the
/// process's `RandomState` is plenty.
fn local_ssrc(index: usize) -> u32 {
    use std::hash::{BuildHasher, Hasher};
    let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
    hasher.write_usize(index);
    (hasher.finish() & 0xFFFF_FFFF) as u32
}

pub(crate) async fn setup_udp_session(
    url: url::Url,
    servers: Vec<std::net::SocketAddr>,
    peer_allowed: UdpPeerAllowed,
) -> Result<UdpReady, String> {
    let options = SessionOptions::default()
        .user_agent("basis-media".into())
        .connect_addrs(servers)
        .udp_peer_validator(Arc::new(move |ip| peer_allowed(ip)));
    let mut session = retina::client::Session::describe(url, options)
        .await
        .map_err(|e| format!("rtsp describe: {e}"))?;

    let (video_index, audio_index) = select_streams(session.streams());
    if video_index.is_none() && audio_index.is_none() {
        return Err("no h264 video or aac audio stream in the SDP".into());
    }

    for index in [video_index, audio_index].into_iter().flatten() {
        session
            .setup(
                index,
                SetupOptions::default()
                    .transport(Transport::Udp(UdpTransportOptions::default()))
                    .frame_format(frame_format()),
            )
            .await
            .map_err(|e| format!("rtsp setup (udp): {e}"))?;
    }

    let mut session = session
        .play(
            PlayOptions::default()
                .initial_timestamp(retina::client::InitialTimestampPolicy::Permissive),
        )
        .await
        .map_err(|e| format!("rtsp play: {e}"))?;

    let mut streams = Vec::new();
    for index in [video_index, audio_index].into_iter().flatten() {
        let (rtp, rtcp) = session
            .take_udp_sockets(index)
            .ok_or("udp sockets missing after play")?;
        let depacketizer = session
            .take_depacketizer(index)
            .ok_or("stream cannot be depacketized")?;
        let clock_rate = NonZeroU32::new(session.streams()[index].clock_rate_hz())
            .ok_or("stream advertises a zero clock rate")?;
        let play_rtptime = session.initial_rtptime(index);
        streams.push(UdpStream {
            index,
            clock_rate,
            rtp: Arc::new(rtp),
            rtcp: Arc::new(rtcp),
            receiver: RtpReceiver::new(ReceiverConfig::new(
                clock_rate.get(),
                local_ssrc(index),
                "basis-media",
            )),
            depacketizer,
            play_rtptime,
            ts_start: None,
            zero_is_first_packet: false,
            announced: false,
            refused: 0,
        });
    }
    Ok(UdpReady {
        session,
        streams,
        video_index,
        audio_index,
    })
}

pub(crate) async fn run_udp_session(
    ready: UdpReady,
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
    mut first_datagram: Option<oneshot::Sender<()>>,
) -> Result<(), String> {
    let UdpReady {
        mut session,
        mut streams,
        video_index,
        audio_index,
    } = ready;
    let needed: Vec<usize> = [video_index, audio_index].into_iter().flatten().collect();

    // Announce formats known from the SDP up front.
    for stream in &mut streams {
        if let Some(parameters) = stream.depacketizer.parameters()
            && let Some(format) = format_from(&parameters)
        {
            send_event(
                tx,
                StreamEvent::Format(TrackId(stream.index as u32), format),
            )
            .await?;
            stream.announced = true;
        }
    }

    // One receive task per socket, funneling into a single channel; the
    // JoinSet aborts them all when the driver ends.
    let (dtx, mut drx) = mpsc::channel::<(usize, bool, Vec<u8>)>(512);
    let mut recv_tasks = JoinSet::new();
    for stream in &streams {
        for (is_rtcp, socket) in [(false, &stream.rtp), (true, &stream.rtcp)] {
            let dtx = dtx.clone();
            let socket = Arc::clone(socket);
            let index = stream.index;
            recv_tasks.spawn(async move {
                let mut buf = vec![0u8; 65_536];
                loop {
                    match socket.recv(&mut buf).await {
                        Ok(n) => {
                            if dtx.send((index, is_rtcp, buf[..n].to_vec())).await.is_err() {
                                break;
                            }
                        }
                        // Windows surfaces ICMP port-unreachable for
                        // previously sent packets (hole punch, RRs) as
                        // a receive error; the socket is fine.
                        Err(e)
                            if matches!(
                                e.kind(),
                                std::io::ErrorKind::ConnectionReset
                                    | std::io::ErrorKind::ConnectionRefused
                            ) => {}
                        Err(_) => break,
                    }
                }
            });
        }
    }
    drop(dtx);

    let epoch = Instant::now();
    let now_of = |epoch: &Instant| MediaTime::from_micros(epoch.elapsed().as_micros() as i64);

    let mut align = [StreamAlign::default(); MAX_STREAMS];
    let mut buffered: VecDeque<PendingFrame> = VecDeque::new();
    let mut aligning = true;
    // As in the TCP loop: realign video once late reports are all in.
    let mut late_reports = true;
    let align_deadline = tokio::time::Instant::now() + ALIGN_WAIT;
    let mut flush_at = align_deadline;

    loop {
        // Next reorder-gap release or receiver report across streams,
        // plus the alignment flush while it is pending.
        let mut wake: Option<MediaTime> = streams
            .iter()
            .filter_map(|s| s.receiver.next_deadline())
            .min();
        let wake_instant = {
            let media_wake = wake.map(|deadline| {
                let now = now_of(&epoch);
                let delta = deadline.saturating_sub(now).as_micros().max(0) as u64;
                tokio::time::Instant::now() + std::time::Duration::from_micros(delta)
            });
            let align_wake = if aligning { Some(flush_at) } else { None };
            match (media_wake, align_wake) {
                (Some(a), Some(b)) => {
                    wake = Some(MediaTime::ZERO); // marker: a wake exists
                    Some(a.min(b))
                }
                (a, b) => {
                    let chosen = a.or(b);
                    if chosen.is_some() {
                        wake = Some(MediaTime::ZERO);
                    }
                    chosen
                }
            }
        };
        let far_future = tokio::time::Instant::now() + std::time::Duration::from_secs(3_600);

        tokio::select! {
            datagram = drx.recv() => {
                let now = now_of(&epoch);
                match datagram {
                    Some((index, is_rtcp, data)) => {
                        if let Some(first) = first_datagram.take() {
                            let _ = first.send(());
                        }
                        let stream = streams
                            .iter_mut()
                            .find(|s| s.index == index)
                            .expect("datagram for unknown stream");
                        if is_rtcp {
                            let _ = stream.receiver.on_rtcp(now, &data);
                            if stream.receiver.bye_received() {
                                // Sender said goodbye: the session is
                                // over (publisher stopped); surface as
                                // source loss so live lanes reconnect.
                                return Ok(());
                            }
                            update_alignment(stream, &mut align);
                            if reports_complete(&align, &needed) && late_reports {
                                late_reports = false;
                                if aligning {
                                    flush_aligned(&mut buffered, &mut align, &needed, generation, tx)
                                        .await?;
                                    aligning = false;
                                } else {
                                    realign_late(&mut align, video_index, audio_index);
                                }
                            }
                        } else {
                            let _ = stream.receiver.on_rtp(now, &data);
                            drain_stream(stream, now, aligning, &mut buffered, &align, generation, tx)
                                .await?;
                        }
                    }
                    None => return Err("udp receive tasks ended".into()),
                }
            }
            item = session.next() => {
                match item {
                    // Control connection closed by the server.
                    None => return Ok(()),
                    Some(Err(e)) => return Err(format!("rtsp session: {e}")),
                    // Stray interleaved items; media flows on our sockets.
                    Some(Ok(_)) => {}
                }
            }
            _ = tokio::time::sleep_until(wake_instant.unwrap_or(far_future)), if wake.is_some() => {
                let now = now_of(&epoch);
                for stream in streams.iter_mut() {
                    drain_stream(stream, now, aligning, &mut buffered, &align, generation, tx)
                        .await?;
                    if let Some(report) = stream.receiver.poll_rtcp_out(now) {
                        let _ = stream.rtcp.send(&report).await;
                    }
                }
                if aligning && tokio::time::Instant::now() >= flush_at {
                    flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
                    aligning = false;
                }
            }
        }

        if aligning && buffered.len() >= CHANNEL_DEPTH {
            flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
            aligning = false;
        }
        let from_rtptime = streams
            .iter()
            .all(|s| s.ts_start.is_some() && !s.zero_is_first_packet);
        if aligning
            && flush_at == align_deadline
            && declared_starts_agree(&buffered, &needed, from_rtptime)
        {
            flush_at = align_deadline.min(tokio::time::Instant::now() + REPORT_GRACE);
        }
    }
}

/// Furthest a PLAY `rtptime` may sit from the stream's first packet and
/// still be taken as its zero. A server's `rtptime` marks the start of
/// the play range, which a live join delivers within a GOP or so; one
/// further out is junk that would put the stream hours from the other.
const RTPTIME_MAX_DISTANCE_SECS: i64 = 10;

/// The stream's zero on the receiver's unwrapped timeline from the PLAY
/// response's `rtptime`: the wrap of it nearest the first packet. `None`
/// when there is no `rtptime` or it is too far from the stream to trust.
fn rtptime_zero(first: i64, rtptime: Option<u32>, clock_rate: NonZeroU32) -> Option<i64> {
    let distance = i64::from(rtptime?.wrapping_sub(first as u32) as i32);
    (distance.abs() <= RTPTIME_MAX_DISTANCE_SECS * i64::from(clock_rate.get()))
        .then_some(first + distance)
}

/// Release everything the reorder buffer will give up at `now` and feed
/// it through the depacketizer into the emit path.
async fn drain_stream(
    stream: &mut UdpStream,
    now: MediaTime,
    aligning: bool,
    buffered: &mut VecDeque<PendingFrame>,
    align: &[StreamAlign; MAX_STREAMS],
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    while let Some(packet) = stream.receiver.poll_packet(now) {
        let start = match stream.ts_start {
            Some(start) => start,
            None => {
                let zero = rtptime_zero(packet.timestamp, stream.play_rtptime, stream.clock_rate);
                stream.zero_is_first_packet = zero.is_none();
                *stream.ts_start.insert(zero.unwrap_or(packet.timestamp))
            }
        };
        // Pre-join edge: a reordered predecessor of the first delivered
        // packet has nowhere to go on a timeline zeroed at that packet.
        if stream.zero_is_first_packet && packet.timestamp < start {
            continue;
        }
        let Some(timestamp) =
            retina::Timestamp::new(packet.timestamp.saturating_sub(start), stream.clock_rate, 0)
        else {
            continue;
        };
        let built = ReceivedPacketBuilder {
            ctx: retina::PacketContext::dummy(),
            stream_id: stream.index,
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
        // unconditional because retina can queue a completed access unit
        // and then reject the same packet, and pushing over unpulled
        // output panics.
        //
        // Refusals are logged on the first and then sparsely, with the
        // running total, so a stream nothing can depacketize is told
        // apart from a burst of loss.
        if let Err(e) = stream.depacketizer.push(built) {
            stream.refused += 1;
            if stream.refused == 1 || stream.refused.is_multiple_of(256) {
                diag_log!(
                    "rtsp stream {}: depacketizer refused {} packet(s), latest: {e}",
                    stream.index,
                    stream.refused
                );
            }
        }
        while let Some(item) = stream.depacketizer.pull() {
            let Ok(item) = item else {
                continue;
            };
            match item {
                CodecItem::VideoFrame(frame) => {
                    if (frame.has_new_parameters() || !stream.announced)
                        && let Some(parameters) = stream.depacketizer.parameters()
                        && let Some(format) = format_from(&parameters)
                    {
                        send_event(
                            tx,
                            StreamEvent::Format(TrackId(stream.index as u32), format),
                        )
                        .await?;
                        stream.announced = true;
                    }
                    let ts = frame.timestamp();
                    let elapsed_us = units_to_us(ts.elapsed(), ts.clock_rate());
                    let pending = PendingFrame {
                        stream_id: stream.index,
                        elapsed_us,
                        key: frame.is_random_access_point(),
                        data: frame.into_data(),
                    };
                    emit(pending, aligning, buffered, align, generation, tx).await?;
                }
                CodecItem::AudioFrame(frame) => {
                    if !stream.announced
                        && let Some(parameters) = stream.depacketizer.parameters()
                        && let Some(format) = format_from(&parameters)
                    {
                        send_event(
                            tx,
                            StreamEvent::Format(TrackId(stream.index as u32), format),
                        )
                        .await?;
                        stream.announced = true;
                    }
                    let ts = frame.timestamp();
                    let elapsed_us = units_to_us(ts.elapsed(), ts.clock_rate());
                    let pending = PendingFrame {
                        stream_id: stream.index,
                        elapsed_us,
                        key: true,
                        data: frame.data().to_vec(),
                    };
                    emit(pending, aligning, buffered, align, generation, tx).await?;
                }
                _ => {}
            }
        }
    }
    Ok(())
}

/// NTP↔RTP alignment from the stream's latest sender report, on the
/// same zero-based timeline as the emitted frames.
fn update_alignment(stream: &mut UdpStream, align: &mut [StreamAlign; MAX_STREAMS]) {
    if stream.index >= MAX_STREAMS || align[stream.index].ntp_at_zero.is_some() {
        return;
    }
    let (Some(sr), Some(start)) = (stream.receiver.sender_info(), stream.ts_start) else {
        return;
    };
    let ext = stream.receiver.extend_report_ts(sr.rtp_timestamp);
    let elapsed_us = units_to_us(ext.saturating_sub(start), stream.clock_rate);
    align[stream.index].ntp_at_zero = Some(ntp_at_zero(sr.ntp, elapsed_us));
}

#[cfg(test)]
mod tests {
    use super::*;

    fn datagram(seq: u16, timestamp: u32, payload: &[u8]) -> Vec<u8> {
        let mut out = vec![0x80, 96];
        out.extend_from_slice(&seq.to_be_bytes());
        out.extend_from_slice(&timestamp.to_be_bytes());
        out.extend_from_slice(&0x1234_5678u32.to_be_bytes());
        out.extend_from_slice(payload);
        out
    }

    /// A refused push can leave a completed access unit queued inside
    /// retina, and the next push over it panics. Three datagrams reach
    /// that state: an FU-A start, then a timestamp change mid-fragment
    /// whose own payload is rejected (F bit set), then anything at all.
    ///
    /// The row proves survival only. The state it builds is a *pending*
    /// fragment, so nothing completes and the buffer is empty at the end
    /// by construction; asserting an access unit came out would mean
    /// completing one, which is not the state that panics.
    #[test]
    fn refused_push_still_drains_before_the_next_one() {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let mut depacketizer =
                Depacketizer::new("video", "h264", 90_000, None, None).expect("h264 depacketizer");
            depacketizer.set_frame_format(frame_format());
            let bind = || async {
                Arc::new(
                    UdpSocket::bind("127.0.0.1:0")
                        .await
                        .expect("loopback socket"),
                )
            };
            let mut stream = UdpStream {
                index: 0,
                clock_rate: NonZeroU32::new(90_000).unwrap(),
                rtp: bind().await,
                rtcp: bind().await,
                receiver: RtpReceiver::new(ReceiverConfig::new(90_000, 1, "basis-media")),
                depacketizer,
                play_rtptime: None,
                ts_start: None,
                zero_is_first_packet: false,
                announced: false,
                refused: 0,
            };

            let fed_at = MediaTime::from_millis(0);
            // Drained past the reorder window: `poll_packet` releasing a
            // sequential run straight away is policy rather than contract,
            // and a change to it should not fail this row.
            let now = fed_at
                + ReceiverConfig::new(90_000, 1, "basis-media").reorder_wait
                + MediaTime::from_millis(1);
            for (seq, timestamp, payload) in [
                (1000u16, 9_000u32, [0x7C, 0x85, 0x42].as_slice()),
                (1001, 12_000, [0x9C, 0x42].as_slice()),
                (1002, 12_000, [0x65, 0x42].as_slice()),
            ] {
                stream
                    .receiver
                    .on_rtp(fed_at, &datagram(seq, timestamp, payload))
                    .expect("staged datagram accepted");
            }

            let (tx, _rx) = mpsc::channel(16);
            let mut buffered = VecDeque::new();
            let align = [StreamAlign::default(); MAX_STREAMS];
            drain_stream(
                &mut stream,
                now,
                true,
                &mut buffered,
                &align,
                Generation::default(),
                &tx,
            )
            .await
            .expect("stream survives a refused push");
            // The drain only pushes what `poll_packet` releases, and a
            // reorder hold would release nothing at the instant the three
            // were fed. `ts_start` is latched off the first released
            // packet, so this is what separates surviving the push from
            // never having made one.
            assert!(
                stream.ts_start.is_some(),
                "no packet reached the depacketizer, so the row proved nothing"
            );
            // And that one of them was refused: the queued-output state
            // this guards is only reachable through a refusal, so a
            // depacketizer that took all three would leave the row
            // passing over a state it never built.
            assert!(
                stream.refused > 0,
                "nothing was refused, so the drain never covered the push that panicked"
            );
        });
    }

    async fn aac_stream(play_rtptime: Option<u32>) -> UdpStream {
        const PARAMS: &str = "streamtype=5;profile-level-id=1;mode=AAC-hbr;sizelength=13;\
                              indexlength=3;indexdeltalength=3;config=1188";
        let mut depacketizer =
            Depacketizer::new("audio", "mpeg4-generic", 48_000, None, Some(PARAMS))
                .expect("aac depacketizer");
        depacketizer.set_frame_format(frame_format());
        let bind = || async {
            Arc::new(
                UdpSocket::bind("127.0.0.1:0")
                    .await
                    .expect("loopback socket"),
            )
        };
        UdpStream {
            index: 1,
            clock_rate: NonZeroU32::new(48_000).unwrap(),
            rtp: bind().await,
            rtcp: bind().await,
            receiver: RtpReceiver::new(ReceiverConfig::new(48_000, 1, "basis-media")),
            depacketizer,
            play_rtptime,
            ts_start: None,
            zero_is_first_packet: false,
            announced: true,
            refused: 0,
        }
    }

    /// One marked AAC-hbr datagram carrying a single access unit.
    fn aac_datagram(seq: u16, timestamp: u32) -> Vec<u8> {
        let mut out = vec![0x80, 0x80 | 97];
        out.extend_from_slice(&seq.to_be_bytes());
        out.extend_from_slice(&timestamp.to_be_bytes());
        out.extend_from_slice(&0x1234_5678u32.to_be_bytes());
        out.extend_from_slice(&[0x00, 0x10, 0x00, 0x20, 1, 2, 3, 4]);
        out
    }

    /// Feed the datagrams, drain past the reorder window, and return each
    /// buffered frame's elapsed microseconds.
    fn elapsed_of(play_rtptime: Option<u32>, datagrams: &[(u16, u32)]) -> Vec<i64> {
        let runtime = tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap();
        runtime.block_on(async {
            let mut stream = aac_stream(play_rtptime).await;
            let fed_at = MediaTime::from_millis(0);
            for &(seq, timestamp) in datagrams {
                stream
                    .receiver
                    .on_rtp(fed_at, &aac_datagram(seq, timestamp))
                    .expect("datagram accepted");
            }
            let now = fed_at
                + ReceiverConfig::new(48_000, 1, "basis-media").reorder_wait
                + MediaTime::from_millis(1);
            let (tx, _rx) = mpsc::channel(16);
            let mut buffered = VecDeque::new();
            let align = [StreamAlign::default(); MAX_STREAMS];
            drain_stream(
                &mut stream,
                now,
                true,
                &mut buffered,
                &align,
                Generation::default(),
                &tx,
            )
            .await
            .expect("drain");
            buffered.iter().map(|f| f.elapsed_us).collect()
        })
    }

    /// With the PLAY response's `rtptime`, a stream's elapsed time counts
    /// from it, not from whichever packet happened to arrive first, so two
    /// streams joined at different points of their GOPs stay aligned.
    #[test]
    fn a_stream_counts_from_the_play_rtptime() {
        // First packet 480 ticks (10 ms at 48 kHz) after rtptime.
        assert_eq!(
            elapsed_of(Some(100_000), &[(7, 100_480), (8, 101_504)]),
            [10_000, 31_333]
        );
        // And 480 ticks before it: elapsed runs negative, as over TCP.
        assert_eq!(elapsed_of(Some(100_480), &[(7, 100_000)]), [-10_000]);
        // Without one, the first packet is zero.
        assert_eq!(elapsed_of(None, &[(7, 100_480), (8, 101_504)]), [0, 21_333]);
    }

    /// `rtptime` is taken at the wrap nearest the first packet, and one
    /// more than 10 s from it is junk: the first packet is zero instead.
    #[test]
    fn the_rtptime_zero_is_bounded_and_wraps() {
        let rate = NonZeroU32::new(90_000).unwrap();
        let first = i64::from(u32::MAX) + 1 + 1_000;
        assert_eq!(
            rtptime_zero(first, Some(u32::MAX - 999), rate),
            Some(first - 2_000),
            "rtptime just before the wrap belongs to the first packet's cycle's predecessor"
        );
        assert_eq!(rtptime_zero(1_000, None, rate), None);
        assert_eq!(
            rtptime_zero(1_000_000, Some(1_900_000), rate),
            Some(1_900_000)
        );
        assert_eq!(rtptime_zero(1_000_000, Some(1_900_001), rate), None);
        assert_eq!(rtptime_zero(1_000_000, Some(100_000), rate), Some(100_000));
        assert_eq!(rtptime_zero(1_000_000, Some(99_999), rate), None);
    }
}
