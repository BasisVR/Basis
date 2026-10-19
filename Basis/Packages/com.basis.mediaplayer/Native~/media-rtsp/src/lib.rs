#![forbid(unsafe_code)]

//! RTSP session as a [`Demuxer`]: `retina` owns the RTSP
//! state machine. `rtsp://` negotiates UDP first and falls back to
//! TCP-interleaved when UDP cannot be set up or no datagrams flow. The
//! UDP path runs media-rtp's reorder/RTCP-RR layer under retina's
//! signalling (`udp` module), because retina's own UDP path has no
//! reorder buffer and sends no receiver reports, and real servers kill
//! sessions over missing reports. `rtspt://` pins TCP-interleaved; the
//! Bank downstream absorbs jitter either way.
//!
//! A/V alignment: RTP timestamps are per-stream, so cross-stream offsets
//! come from RTCP sender reports (NTP ↔ RTP mappings). Frames buffer
//! briefly at start until every stream has a sender report or a bounded
//! wait expires, then flow with aligned timestamps. Without reports, each
//! stream counts from the PLAY response's RTP-Info `rtptime` where the
//! server gave one for every stream and it agrees with the first frames,
//! and from its first frame otherwise. When the `rtptime` starts already
//! agree, the wait is cut to a short grace after the first frames: a
//! server that sends reports promptly still lands inside it, and one that
//! sends none does not hold every join for the whole wait. Reports that
//! arrive later move the video onto them once; the audio keeps its
//! timeline.

use std::collections::VecDeque;
use std::net::SocketAddr;
use std::time::Duration;

use futures::StreamExt;
use media_clock::{Generation, MediaTime};
use media_demux::{
    Au, AudioCodec, DemuxError, Demuxer, EosReason, Format, StreamEvent, TrackId, VideoCodec,
};
use media_diag::diag_log;
use media_rtp::{ntp_at_zero, units_to_us};
use retina::client::{PlayOptions, SessionOptions, SetupOptions, Transport};
use retina::codec::{CodecItem, FrameFormat, ParameterSetInsertion, ParametersRef, aac, h26x};
use tokio::sync::mpsc;

mod udp;

pub use udp::UdpPeerAllowed;

/// Channel depth between the session task and the pulling demux thread,
/// and the cap on frames buffered while waiting for sender reports.
/// Public with the shared emit path below: the WHEP lane runs the same
/// session-task → demux-thread shape.
pub const CHANNEL_DEPTH: usize = 512;
/// How long the aligner waits for sender reports before aligning without
/// them, from the PLAY `rtptime` or the first frames.
pub const ALIGN_WAIT: Duration = Duration::from_secs(2);
/// How long the aligner still waits for sender reports once every
/// stream's first frame is in and the PLAY response's `rtptime` starts
/// agree (see [`declared_starts_agree`]). Servers that send reports at
/// once deliver them within tens of milliseconds of the first frames.
pub const REPORT_GRACE: Duration = Duration::from_millis(300);
/// Widest spread between the streams' first frames that the PLAY
/// response's `rtptime` may put them at and still be believed, when no
/// sender report has arrived. A live join delivers both streams' first
/// frames within about a frame of the same moment; some servers' start
/// times drift seconds from the streams they describe.
pub const RTPTIME_AGREEMENT: Duration = Duration::from_millis(250);
/// Largest correction applied when sender reports arrive after the start.
/// Past it the reports disagree with the stream by more than any join
/// skew, and the start's alignment is kept.
pub const REALIGN_MAX: Duration = Duration::from_secs(3);
/// No frames for this long is a dead session (the transport-loss class;
/// the engine's reconnect path takes it from there).
const FEED_STALL: Duration = Duration::from_secs(10);
/// How long a played UDP session may stay silent (no RTP or RTCP on any
/// socket) before the open falls back to TCP-interleaved: the
/// firewall/NAT-blackhole case, invisible at SETUP time.
const UDP_PROBE: Duration = Duration::from_secs(5);

/// Longest one transport's DESCRIBE/SETUP/PLAY exchange may take. The
/// client sets no deadline of its own, so a server that accepts the
/// connection and never answers would otherwise hold the open for good.
const OPEN_DEADLINE: Duration = Duration::from_secs(15);
/// How often a blocked open samples the cancel probe.
const CANCEL_POLL: Duration = Duration::from_millis(50);

/// The engine's teardown probe: sampled between blocking receives so a
/// closing session never waits out the stall timeout.
pub type CancelProbe = Box<dyn Fn() -> bool + Send>;

enum Interrupted {
    Cancelled,
    TimedOut,
}

/// Runs `work` until it finishes, `cancelled` reports true, or `limit`
/// passes. The thread running an open is one `bm_session_close` joins from
/// the client's main thread, so every wait in it has to see the cancel.
fn drive<T>(
    runtime: &tokio::runtime::Handle,
    cancelled: &CancelProbe,
    limit: Duration,
    work: impl Future<Output = T>,
) -> Result<T, Interrupted> {
    runtime.block_on(async {
        let deadline = tokio::time::Instant::now() + limit;
        tokio::pin!(work);
        loop {
            if cancelled() {
                return Err(Interrupted::Cancelled);
            }
            tokio::select! {
                biased;
                outcome = &mut work => return Ok(outcome),
                () = tokio::time::sleep_until(deadline) => return Err(Interrupted::TimedOut),
                () = tokio::time::sleep(CANCEL_POLL) => {}
            }
        }
    })
}

fn open_cancelled() -> DemuxError {
    DemuxError::Source("rtsp open cancelled".into())
}

/// Which transport the open negotiated (transport choices are
/// diagnosable, never silent).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RtspTransport {
    Udp,
    TcpInterleaved,
}

impl std::fmt::Display for RtspTransport {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Udp => write!(f, "udp"),
            Self::TcpInterleaved => write!(f, "tcp-interleaved"),
        }
    }
}

pub struct RtspDemuxer {
    rx: mpsc::Receiver<Result<StreamEvent, String>>,
    runtime: tokio::runtime::Handle,
    cancelled: CancelProbe,
    video_track: Option<TrackId>,
    audio_track: Option<TrackId>,
    task: tokio::task::JoinHandle<()>,
    transport: RtspTransport,
    fallback: Option<String>,
}

impl RtspDemuxer {
    /// Open and start pulling. `runtime` hosts the async session (the
    /// shared bm-io runtime); `cancelled` is polled during blocking
    /// waits. `rtsp://` negotiates UDP first and falls back to
    /// TCP-interleaved; `rtspt://` pins TCP-interleaved. `servers` are the
    /// addresses to dial, tried in order, resolved from the URL's host and
    /// vetted by the caller; the host is never looked up again.
    /// `udp_peer_allowed` vets the UDP peer address from the SETUP response
    /// before any packet is sent to it.
    pub fn open(
        url: &str,
        servers: Vec<SocketAddr>,
        generation: Generation,
        runtime: tokio::runtime::Handle,
        cancelled: CancelProbe,
        udp_peer_allowed: UdpPeerAllowed,
    ) -> Result<Self, DemuxError> {
        let want_udp = url.starts_with("rtsp://");
        let parsed = url::Url::parse(&url.replacen("rtspt://", "rtsp://", 1))
            .map_err(|e| DemuxError::Parse(format!("rtsp url: {e}")))?;

        let mut fallback = None;
        if want_udp {
            match drive(
                &runtime,
                &cancelled,
                OPEN_DEADLINE,
                udp::setup_udp_session(parsed.clone(), servers.clone(), udp_peer_allowed),
            ) {
                Ok(Ok(ready)) => {
                    let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
                    let (first_tx, first_rx) = tokio::sync::oneshot::channel();
                    let task = runtime.spawn(async move {
                        let result =
                            udp::run_udp_session(ready, generation, &tx, Some(first_tx)).await;
                        let _ = tx
                            .send(match result {
                                Ok(()) => Ok(StreamEvent::Eos(EosReason::SourceLost)),
                                Err(e) => Err(e),
                            })
                            .await;
                    });
                    // A set-up session that never delivers a datagram is
                    // the UDP-blackhole case; only arrival proves the
                    // path works.
                    match drive(&runtime, &cancelled, UDP_PROBE, first_rx) {
                        Ok(Ok(())) => {
                            return Ok(Self {
                                rx,
                                runtime,
                                cancelled,
                                video_track: None,
                                audio_track: None,
                                task,
                                transport: RtspTransport::Udp,
                                fallback: None,
                            });
                        }
                        Err(Interrupted::Cancelled) => {
                            task.abort();
                            return Err(open_cancelled());
                        }
                        _ => {
                            task.abort();
                            fallback = Some(format!("no UDP datagrams within {UDP_PROBE:?}"));
                        }
                    }
                }
                Ok(Err(detail)) => fallback = Some(format!("UDP setup failed: {detail}")),
                Err(Interrupted::Cancelled) => return Err(open_cancelled()),
                Err(Interrupted::TimedOut) => {
                    fallback = Some(format!("UDP setup unanswered within {OPEN_DEADLINE:?}"));
                }
            }
        }

        // Describe/setup/play happen synchronously so an unreachable or
        // 404 path fails the open itself, and the engine's reconnect
        // budget counts it rather than seeing a session that dies on
        // first pull.
        let ready = match drive(
            &runtime,
            &cancelled,
            OPEN_DEADLINE,
            setup_session(parsed, servers),
        ) {
            Ok(ready) => ready.map_err(|detail| DemuxError::Source(detail.into()))?,
            Err(Interrupted::Cancelled) => return Err(open_cancelled()),
            Err(Interrupted::TimedOut) => {
                return Err(DemuxError::Source(
                    format!("rtsp setup unanswered within {OPEN_DEADLINE:?}").into(),
                ));
            }
        };
        let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
        let task = runtime.spawn(async move {
            let result = run_session(ready, generation, &tx).await;
            let _ = tx
                .send(match result {
                    Ok(()) => Ok(StreamEvent::Eos(EosReason::SourceLost)),
                    Err(e) => Err(e),
                })
                .await;
        });
        Ok(Self {
            rx,
            runtime,
            cancelled,
            video_track: None,
            audio_track: None,
            task,
            transport: RtspTransport::TcpInterleaved,
            fallback,
        })
    }

    pub fn transport(&self) -> RtspTransport {
        self.transport
    }

    /// Why a `rtsp://` open ended up on TCP-interleaved, when it did.
    pub fn fallback_reason(&self) -> Option<&str> {
        self.fallback.as_deref()
    }
}

impl Drop for RtspDemuxer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

impl Demuxer for RtspDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        let stall_deadline = std::time::Instant::now() + FEED_STALL;
        loop {
            if (self.cancelled)() {
                return Ok(StreamEvent::Eos(EosReason::SourceLost));
            }
            let received = self.runtime.block_on(async {
                tokio::time::timeout(Duration::from_millis(100), self.rx.recv()).await
            });
            match received {
                Ok(Some(Ok(event))) => {
                    if let StreamEvent::Format(track, format) = &event {
                        match format {
                            Format::Video { .. } => self.video_track = Some(*track),
                            Format::Audio { .. } => self.audio_track = Some(*track),
                        }
                    }
                    return Ok(event);
                }
                Ok(Some(Err(detail))) => return Err(DemuxError::Source(detail.into())),
                Ok(None) => return Err(DemuxError::Source("rtsp session task ended".into())),
                Err(_) => {
                    if std::time::Instant::now() >= stall_deadline {
                        return Err(DemuxError::Source("rtsp feed stalled".into()));
                    }
                }
            }
        }
    }

    fn seek(
        &mut self,
        _target: MediaTime,
        _generation: Generation,
    ) -> Result<MediaTime, DemuxError> {
        Err(DemuxError::Unsupported("seek on an RTSP session"))
    }

    fn duration(&self) -> Option<MediaTime> {
        None
    }

    fn video_track(&self) -> Option<TrackId> {
        self.video_track
    }

    fn audio_track(&self) -> Option<TrackId> {
        self.audio_track
    }
}

/// One depacketised frame awaiting emission. Part of the shared
/// RTP-session emit path: the RTSP drivers and the WHEP lane all feed
/// frames through [`emit`]/[`flush_aligned`] so A/V alignment and the
/// `StreamEvent` conversion exist exactly once.
pub struct PendingFrame {
    pub stream_id: usize,
    pub data: Vec<u8>,
    pub elapsed_us: i64,
    pub key: bool,
}

/// Per-stream alignment: NTP time (32.32) at stream elapsed 0, learned
/// from a sender report, and the microsecond offset derived from it.
#[derive(Default, Clone, Copy)]
pub struct StreamAlign {
    pub ntp_at_zero: Option<u64>,
    pub offset_us: i64,
}

pub const MAX_STREAMS: usize = 8;

/// First H.264 video stream and first AAC audio stream; breadth grows
/// with the decode table.
pub(crate) fn select_streams(streams: &[retina::client::Stream]) -> (Option<usize>, Option<usize>) {
    let mut video_index = None;
    let mut audio_index = None;
    for (index, stream) in streams.iter().enumerate() {
        match (stream.media(), stream.encoding_name()) {
            ("video", "h264") if video_index.is_none() && index < MAX_STREAMS => {
                video_index = Some(index);
            }
            ("audio", "mpeg4-generic") if audio_index.is_none() && index < MAX_STREAMS => {
                audio_index = Some(index);
            }
            _ => {}
        }
    }
    (video_index, audio_index)
}

/// Annex-B with parameter sets on every keyframe, raw AAC with the
/// AudioSpecificConfig out of band — the decode adapters' contracts.
pub fn frame_format() -> FrameFormat {
    FrameFormat {
        h26x_framing: h26x::Framing::AnnexB,
        parameter_set_insertion: ParameterSetInsertion::EachKeyFrame,
        aac_framing: aac::Framing::Raw,
    }
}

/// How far apart the set-up streams' first buffered frames sit on their
/// timelines; `None` until every stream has one. Streams counted from
/// their first packets read zero; streams counted from the PLAY
/// response's `rtptime` read how far that start is from the frames.
fn start_spread(buffered: &VecDeque<PendingFrame>, needed: &[usize]) -> Option<i64> {
    let mut firsts = Vec::with_capacity(needed.len());
    for &index in needed {
        firsts.push(buffered.iter().find(|f| f.stream_id == index)?.elapsed_us);
    }
    Some(firsts.iter().max()? - firsts.iter().min()?)
}

/// Whether the start can already be aligned without sender reports: every
/// set-up stream has a frame buffered, each is counted from the PLAY
/// response's `rtptime` (`from_rtptime`), and those starts put the first
/// frames within [`RTPTIME_AGREEMENT`] of one another. The aligner then
/// waits only [`REPORT_GRACE`] more for reports.
pub fn declared_starts_agree(
    buffered: &VecDeque<PendingFrame>,
    needed: &[usize],
    from_rtptime: bool,
) -> bool {
    from_rtptime
        && start_spread(buffered, needed)
            .is_some_and(|spread| spread <= RTPTIME_AGREEMENT.as_micros() as i64)
}

/// Every set-up stream has a sender report.
pub fn reports_complete(align: &[StreamAlign; MAX_STREAMS], needed: &[usize]) -> bool {
    needed.iter().all(|&i| align[i].ntp_at_zero.is_some())
}

/// What [`realign_video`] did.
#[derive(Debug, PartialEq, Eq)]
pub enum Realign {
    /// The video offset moved by this many microseconds.
    Moved(i64),
    /// The reports asked for this many microseconds, past [`REALIGN_MAX`].
    Refused(i64),
    /// Already aligned, or a stream has no report.
    Unchanged,
}

/// Align video to audio from sender reports that arrived after frames
/// were already flowing. Audio keeps its timeline, since downstream it is
/// the clock master and never gives way; the video offset takes the whole
/// correction, which the picture shows as one hold or skip.
pub fn realign_video(
    align: &mut [StreamAlign; MAX_STREAMS],
    video: usize,
    audio: usize,
) -> Realign {
    let (Some(video_zero), Some(audio_zero)) = (align[video].ntp_at_zero, align[audio].ntp_at_zero)
    else {
        return Realign::Unchanged;
    };
    let wanted = if video_zero >= audio_zero {
        ntp_delta_us(video_zero - audio_zero)
    } else {
        -ntp_delta_us(audio_zero - video_zero)
    };
    let step = wanted - (align[video].offset_us - align[audio].offset_us);
    if step == 0 {
        return Realign::Unchanged;
    }
    if step.unsigned_abs() > REALIGN_MAX.as_micros() as u64 {
        return Realign::Refused(step);
    }
    align[video].offset_us += step;
    Realign::Moved(step)
}

/// Apply late sender reports once all are in, and say what changed.
pub(crate) fn realign_late(
    align: &mut [StreamAlign; MAX_STREAMS],
    video: Option<usize>,
    audio: Option<usize>,
) {
    let (Some(video), Some(audio)) = (video, audio) else {
        return;
    };
    match realign_video(align, video, audio) {
        Realign::Moved(step) => diag_log!(
            "rtsp: sender reports arrived after the start; video moved {:.1} ms to match audio",
            step as f64 / 1000.0
        ),
        Realign::Refused(step) => diag_log!(
            "rtsp: sender reports arrived after the start asking video to move {:.1} ms; kept the start's alignment",
            step as f64 / 1000.0
        ),
        Realign::Unchanged => {}
    }
}

pub fn ntp_delta_us(ntp: u64) -> i64 {
    // 32.32 fixed → microseconds. Relative use only; the epoch cancels.
    let secs = (ntp >> 32) as i64;
    let frac = (ntp & 0xFFFF_FFFF) as i64;
    secs.saturating_mul(1_000_000) + ((frac * 1_000_000) >> 32)
}

/// A session set up and playing, ready for the pull loop.
struct ReadySession {
    session: retina::client::Session<retina::client::Playing>,
    video_index: Option<usize>,
    audio_index: Option<usize>,
}

async fn setup_session(url: url::Url, servers: Vec<SocketAddr>) -> Result<ReadySession, String> {
    let options = SessionOptions::default()
        .user_agent("basis-media".into())
        .connect_addrs(servers);
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
                    .transport(Transport::Tcp(Default::default()))
                    .frame_format(frame_format()),
            )
            .await
            .map_err(|e| format!("rtsp setup: {e}"))?;
    }

    // Permissive: servers legally omit `rtptime` from RTP-Info for
    // streams that have not seen data yet (mediamtx does on quiet paths).
    let session = session
        .play(
            PlayOptions::default()
                .initial_timestamp(retina::client::InitialTimestampPolicy::Permissive),
        )
        .await
        .map_err(|e| format!("rtsp play: {e}"))?;
    Ok(ReadySession {
        session,
        video_index,
        audio_index,
    })
}

async fn run_session(
    ready: ReadySession,
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    let ReadySession {
        session,
        video_index,
        audio_index,
    } = ready;
    let needed: Vec<usize> = [video_index, audio_index].into_iter().flatten().collect();

    // Announce formats known from the SDP up front.
    let mut announced = [false; MAX_STREAMS];
    for index in [video_index, audio_index].into_iter().flatten() {
        if let Some(parameters) = session.streams()[index].parameters()
            && let Some(format) = format_from(&parameters)
        {
            send_event(tx, StreamEvent::Format(TrackId(index as u32), format)).await?;
            announced[index] = true;
        }
    }

    // Retina counts every stream from the PLAY response's `rtptime` only
    // when all of them have one; otherwise from their first packets.
    let from_rtptime = needed
        .iter()
        .all(|&index| session.initial_rtptime(index).is_some());
    let mut demuxed = session.demuxed().map_err(|e| format!("rtsp demux: {e}"))?;

    // Alignment: buffer until every set-up stream has a sender report or
    // the wait expires, then flush with per-stream offsets applied.
    let mut align = [StreamAlign::default(); MAX_STREAMS];
    let mut buffered: VecDeque<PendingFrame> = VecDeque::new();
    let mut aligning = true;
    // Cleared once every stream has a sender report. If that happens after
    // the start has flushed, video is realigned to them once.
    let mut late_reports = true;
    let align_deadline = tokio::time::Instant::now() + ALIGN_WAIT;
    let mut flush_at = align_deadline;

    loop {
        let item = if aligning {
            match tokio::time::timeout_at(flush_at, demuxed.next()).await {
                Ok(item) => item,
                Err(_) => {
                    flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
                    aligning = false;
                    continue;
                }
            }
        } else {
            demuxed.next().await
        };
        let Some(item) = item else {
            return Ok(());
        };
        let item = item.map_err(|e| format!("rtsp stream: {e}"))?;

        match item {
            CodecItem::VideoFrame(frame) => {
                let stream_id = frame.stream_id();
                if (frame.has_new_parameters()
                    || !announced.get(stream_id).copied().unwrap_or(true))
                    && let Some(parameters) = demuxed.streams()[stream_id].parameters()
                    && let Some(format) = format_from(&parameters)
                {
                    send_event(tx, StreamEvent::Format(TrackId(stream_id as u32), format)).await?;
                    if let Some(flag) = announced.get_mut(stream_id) {
                        *flag = true;
                    }
                }
                let timestamp = frame.timestamp();
                let elapsed_us = units_to_us(timestamp.elapsed(), timestamp.clock_rate());
                let pending = PendingFrame {
                    stream_id,
                    elapsed_us,
                    key: frame.is_random_access_point(),
                    data: frame.into_data(),
                };
                emit(pending, aligning, &mut buffered, &align, generation, tx).await?;
            }
            CodecItem::AudioFrame(frame) => {
                let stream_id = frame.stream_id();
                if !announced.get(stream_id).copied().unwrap_or(true)
                    && let Some(parameters) = demuxed.streams()[stream_id].parameters()
                    && let Some(format) = format_from(&parameters)
                {
                    send_event(tx, StreamEvent::Format(TrackId(stream_id as u32), format)).await?;
                    if let Some(flag) = announced.get_mut(stream_id) {
                        *flag = true;
                    }
                }
                let timestamp = frame.timestamp();
                let elapsed_us = units_to_us(timestamp.elapsed(), timestamp.clock_rate());
                let pending = PendingFrame {
                    stream_id,
                    elapsed_us,
                    key: true,
                    data: frame.data().to_vec(),
                };
                emit(pending, aligning, &mut buffered, &align, generation, tx).await?;
            }
            CodecItem::Rtcp(rtcp) => {
                let stream_id = rtcp.stream_id();
                if stream_id < MAX_STREAMS
                    && align[stream_id].ntp_at_zero.is_none()
                    && let Some(rtp_timestamp) = rtcp.rtp_timestamp()
                {
                    let elapsed_us =
                        units_to_us(rtp_timestamp.elapsed(), rtp_timestamp.clock_rate());
                    // The first report in the compound packet, not the last
                    // one: a sender is free to carry several, and letting
                    // each overwrite the one before makes the anchor depend
                    // on how the peer packed them.
                    for packet in rtcp.pkts() {
                        if let Ok(Some(sr)) = packet.as_sender_report() {
                            align[stream_id].ntp_at_zero =
                                Some(ntp_at_zero(sr.ntp_timestamp().0, elapsed_us));
                            break;
                        }
                    }
                }
                if reports_complete(&align, &needed) && late_reports {
                    late_reports = false;
                    if aligning {
                        flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
                        aligning = false;
                    } else {
                        realign_late(&mut align, video_index, audio_index);
                    }
                }
            }
            _ => {}
        }

        if aligning && buffered.len() >= CHANNEL_DEPTH {
            flush_aligned(&mut buffered, &mut align, &needed, generation, tx).await?;
            aligning = false;
        }
        if aligning
            && flush_at == align_deadline
            && declared_starts_agree(&buffered, &needed, from_rtptime)
        {
            flush_at = align_deadline.min(tokio::time::Instant::now() + REPORT_GRACE);
        }
    }
}

/// Send one event to the pulling demux thread; `Err` when it hung up.
pub async fn send_event(
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
    event: StreamEvent,
) -> Result<(), String> {
    tx.send(Ok(event))
        .await
        .map_err(|_| "engine hung up".into())
}

/// Compute per-stream offsets from the collected sender reports, or from
/// the first frames when the streams' start times disagree, and flush the
/// buffered frames in arrival order.
pub async fn flush_aligned(
    buffered: &mut VecDeque<PendingFrame>,
    align: &mut [StreamAlign; MAX_STREAMS],
    needed: &[usize],
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    let ntp_zeroes: Vec<(usize, u64)> = needed
        .iter()
        .filter_map(|&index| align[index].ntp_at_zero.map(|ntp| (index, ntp)))
        .collect();
    if ntp_zeroes.len() == needed.len() && !ntp_zeroes.is_empty() {
        // Common origin: the earliest stream start carries offset 0,
        // later streams positive offsets.
        let min_ntp = ntp_zeroes
            .iter()
            .map(|&(_, ntp)| ntp)
            .min()
            .expect("non-empty");
        for &(index, ntp) in &ntp_zeroes {
            align[index].offset_us = ntp_delta_us(ntp.wrapping_sub(min_ntp));
        }
    } else if let Some(spread) = start_spread(buffered, needed)
        && spread > RTPTIME_AGREEMENT.as_micros() as i64
    {
        // The start times the server gave put the streams' first frames
        // further apart than a live join delivers them. Count each stream
        // from its first frame instead.
        for &index in needed {
            if let Some(first) = buffered.iter().find(|f| f.stream_id == index) {
                align[index].offset_us = -first.elapsed_us;
            }
        }
        diag_log!(
            "rtsp: the server's start times put the streams {} ms apart; counting each from its first frame",
            spread / 1000
        );
    }
    for frame in std::mem::take(buffered) {
        emit_aligned(frame, align, generation, tx).await?;
    }
    Ok(())
}

/// Route one frame: buffered while the aligner is collecting sender
/// reports, emitted with per-stream offsets applied once it settles.
pub async fn emit(
    frame: PendingFrame,
    aligning: bool,
    buffered: &mut VecDeque<PendingFrame>,
    align: &[StreamAlign; MAX_STREAMS],
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    if aligning {
        buffered.push_back(frame);
        return Ok(());
    }
    emit_aligned(frame, align, generation, tx).await
}

async fn emit_aligned(
    frame: PendingFrame,
    align: &[StreamAlign; MAX_STREAMS],
    generation: Generation,
    tx: &mpsc::Sender<Result<StreamEvent, String>>,
) -> Result<(), String> {
    let offset = align.get(frame.stream_id).map(|a| a.offset_us).unwrap_or(0);
    // RTP carries presentation time only, so dts is pts and runs out of
    // order under B-frames. Arrival order is decode order, and nothing
    // downstream needs dts to be monotonic.
    let pts = MediaTime::from_micros(frame.elapsed_us.saturating_add(offset));
    let au = Au {
        track: TrackId(frame.stream_id as u32),
        data: frame.data,
        pts,
        dts: pts,
        key: frame.key,
        generation,
    };
    tx.send(Ok(StreamEvent::Au(au)))
        .await
        .map_err(|_| "engine hung up".into())
}

/// [`Format`] announce from retina stream parameters — the depacketizer
/// side of the shared emit path.
pub fn format_from(parameters: &ParametersRef<'_>) -> Option<Format> {
    match parameters {
        ParametersRef::Video(video) => {
            let (width, height) = video.pixel_dimensions();
            Some(Format::Video {
                codec: VideoCodec::H264,
                coded_width: width,
                coded_height: height,
                display_width: width,
                display_height: height,
                codec_private: Vec::new(),
            })
        }
        ParametersRef::Audio(audio) => Some(Format::Audio {
            codec: AudioCodec::Aac,
            sample_rate: audio.clock_rate(),
            channels: u32::from(audio.channels().get()),
            codec_private: audio.extra_data().to_vec(),
        }),
        ParametersRef::Message(_) => None,
    }
}
