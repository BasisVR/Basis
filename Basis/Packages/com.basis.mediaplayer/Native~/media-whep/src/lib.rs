#![forbid(unsafe_code)]

//! WHEP receive session as a [`Demuxer`]: the sub-second lane.
//! Hand-rolled WHEP signalling over media-io's vetted-and-pinned HTTP
//! discipline; `str0m` (sans-IO) runs ICE/DTLS/SRTP/RTCP on our socket
//! and hands decrypted RTP to media-rtp's reorder layer, retina's H.264
//! depacketizer / the RFC 7587 Opus mapping, and media-rtsp's shared
//! aligner/emit path. The client obligations from the WHEP draft are
//! built in: both the `201 + answer` and `406 + counter-offer, PATCH
//! answer` flows, full ICE gathering before the POST (host candidates
//! ride in the offer, so PATCH-refusing servers work), `Link
//! rel="ice-server"` parsing, and `DELETE` on teardown.
//!
//! The signalling URLs go through the same gate-vetted pinned
//! connects as every HTTP lane; every media-path address str0m wants to
//! reach (ICE candidates, however learned) is checked against the same
//! gate at the transmit boundary before anything is sent.

mod session;
mod signal;

pub use signal::{AnswerFlow, WhepError, ice_servers_from_links, signalling_url};

use std::future::Future;
use std::net::SocketAddr;
use std::sync::Arc;
use std::time::Duration;

use media_clock::{Generation, MediaTime};
use media_demux::{DemuxError, Demuxer, EosReason, Format, StreamEvent, TrackId};
use media_io::{AddressGate, IoLimits};
use media_rtsp::{CHANNEL_DEPTH, CancelProbe};
use session::{AUDIO_STREAM, Lane, LaneCodec, SessionReady, VIDEO_STREAM};
use str0m::change::{SdpAnswer, SdpOffer};
use str0m::format::Codec;
use str0m::media::{Direction, MediaKind};
use str0m::{Candidate, Rtc, RtcConfig};
use tokio::sync::mpsc;
use url::Url;

/// No frames for this long is a dead session (the transport-loss class;
/// the engine's reconnect path takes it from there).
const FEED_STALL: Duration = Duration::from_secs(10);
/// How often the engine's teardown probe is sampled while signalling is
/// driven on the runtime. The probe is a poll rather than a future, so a
/// close lands within one tick of being asked for.
const CANCEL_POLL: Duration = Duration::from_millis(50);

pub struct WhepDemuxer {
    rx: mpsc::Receiver<Result<StreamEvent, String>>,
    runtime: tokio::runtime::Handle,
    cancelled: CancelProbe,
    video_track: Option<TrackId>,
    audio_track: Option<TrackId>,
    task: tokio::task::JoinHandle<()>,
    /// The session URL for teardown DELETE (fired from drop).
    resource: Url,
    gate: Arc<dyn AddressGate>,
    flow: AnswerFlow,
    ice_servers: Vec<String>,
}

impl WhepDemuxer {
    /// Open and start pulling: signalling (POST/answer, or the
    /// counter-offer PATCH flow) runs synchronously so a dead endpoint
    /// fails the open itself and consumes the engine's reconnect
    /// budget; ICE/DTLS then connect asynchronously on `runtime` and a
    /// media path that never comes up surfaces as a feed stall.
    pub fn open(
        url: &str,
        generation: Generation,
        runtime: tokio::runtime::Handle,
        cancelled: CancelProbe,
        gate: Arc<dyn AddressGate>,
    ) -> Result<Self, DemuxError> {
        // A cancel that lands after the server created the session but
        // before its answer is read leaves that session with no Location
        // to DELETE, so the teardown in `Drop` has nothing to send and
        // the server holds it until its own idle timeout. That is
        // inherent to abandoning a POST in flight (the resource exists
        // and its name arrived nowhere); the server-side timeout is the
        // recovery path.
        //
        // Signalling is driven by blocking this thread on the runtime
        // (see `drive`), which panics on a runtime worker. This is
        // public, so refuse by name here rather than let tokio's own
        // assertion be how a caller finds out.
        if tokio::runtime::Handle::try_current().is_ok() {
            return Err(DemuxError::Source(
                "whep open blocks its thread and cannot run on a runtime worker".into(),
            ));
        }
        let endpoint = signalling_url(url).map_err(whep_err)?;
        let limits = IoLimits::default();

        // The signalling host's address also picks the interface the host
        // candidate advertises, since the media peer is normally on the
        // same route (see `bind_media_socket`).
        //
        // A missing host is refused rather than defaulted to "": the
        // scheme screen should make it unreachable, and resolving "" would
        // report a URL fault as a resolver one.
        let host = endpoint
            .host_str()
            .ok_or_else(|| WhepError::Url("signalling url has no host".into()))
            .map_err(whep_err)?
            .to_string();
        let signal_addr = drive(&runtime, &cancelled, async {
            media_io::resolve_vetted_async(
                &host,
                endpoint.port_or_known_default().unwrap_or(443),
                gate.as_ref(),
            )
            .await
            .map_err(WhepError::Io)
        })
        .map_err(whep_err)?;
        let (socket, local_addr) = bind_media_socket(signal_addr)?;

        // Fully gathered before POST: the one host candidate is in the
        // offer, so servers that reject PATCH still connect.
        let provider = Arc::new(str0m::crypto::from_feature_flags());
        let mut rtc = rtc_config(Arc::clone(&provider)).build(std::time::Instant::now());
        add_host_candidate(&mut rtc, local_addr)?;
        let mut api = rtc.sdp_api();
        let audio_mid = api.add_media(MediaKind::Audio, Direction::RecvOnly, None, None, None);
        let video_mid = api.add_media(MediaKind::Video, Direction::RecvOnly, None, None, None);
        let (offer, pending) = api
            .apply()
            .ok_or_else(|| DemuxError::Parse("no changes to offer".into()))?;
        let _ = (audio_mid, video_mid);

        let outcome = drive(
            &runtime,
            &cancelled,
            signal::post_offer(&endpoint, &offer.to_sdp_string(), &limits, &gate),
        )
        .map_err(whep_err)?;

        // The server has created a session by now, and every failure
        // from here returns before `Self` exists, so `Drop` (which owns
        // the teardown) never runs. The session's name is known, so the
        // DELETE goes out on the way past: the same fire-and-forget call
        // `Drop` makes, with its own bounded timeout.
        let abandon = |resource: &Url, e: DemuxError| -> DemuxError {
            let resource = resource.clone();
            let gate = Arc::clone(&gate);
            runtime.spawn(async move {
                let _ = signal::delete_resource(&resource, &gate).await;
            });
            e
        };

        let (resource, ice_servers, flow) = match outcome {
            signal::PostOutcome::Answer {
                resource,
                ice_servers,
                answer_sdp,
            } => {
                let answer = SdpAnswer::from_sdp_string(&answer_sdp)
                    .map_err(|e| DemuxError::Parse(format!("whep answer sdp: {e}")))
                    .map_err(|e| abandon(&resource, e))?;
                rtc.sdp_api()
                    .accept_answer(pending, answer)
                    .map_err(|e| DemuxError::Parse(format!("whep answer: {e}")))
                    .map_err(|e| abandon(&resource, e))?;
                (resource, ice_servers, AnswerFlow::Direct)
            }
            signal::PostOutcome::CounterOffer {
                resource,
                ice_servers,
                offer_sdp,
            } => {
                // The server offers; answer from a fresh Rtc (ours never
                // completed its own negotiation).
                let server_offer = SdpOffer::from_sdp_string(&offer_sdp)
                    .map_err(|e| DemuxError::Parse(format!("whep counter-offer sdp: {e}")))
                    .map_err(|e| abandon(&resource, e))?;
                let mut fresh = rtc_config(provider).build(std::time::Instant::now());
                add_host_candidate(&mut fresh, local_addr).map_err(|e| abandon(&resource, e))?;
                let answer = fresh
                    .sdp_api()
                    .accept_offer(server_offer)
                    .map_err(|e| DemuxError::Parse(format!("whep counter-offer: {e}")))
                    .map_err(|e| abandon(&resource, e))?;
                drive(
                    &runtime,
                    &cancelled,
                    signal::patch_answer(&resource, &answer.to_sdp_string(), &limits, &gate),
                )
                .map_err(whep_err)
                .map_err(|e| abandon(&resource, e))?;
                rtc = fresh;
                (resource, ice_servers, AnswerFlow::CounterOffer)
            }
        };

        let lanes = build_lanes(&mut rtc)
            .map_err(DemuxError::Parse)
            .map_err(|e| abandon(&resource, e))?;

        let (tx, rx) = mpsc::channel(CHANNEL_DEPTH);
        let ready = SessionReady {
            rtc,
            socket,
            local_addr,
            gate: Arc::clone(&gate),
            lanes,
        };
        let task = runtime.spawn(async move {
            let result = session::run_session(ready, generation, &tx, None).await;
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
            resource,
            gate,
            flow,
            ice_servers,
        })
    }

    /// Which answer flow the negotiation took.
    pub fn answer_flow(&self) -> AnswerFlow {
        self.flow
    }

    /// `Link rel="ice-server"` entries the server advertised. Surfaced as
    /// diagnostics only: gathering offers the one host candidate.
    pub fn ice_servers(&self) -> &[String] {
        &self.ice_servers
    }
}

impl Drop for WhepDemuxer {
    fn drop(&mut self) {
        self.task.abort();
        // Teardown obligation: DELETE the session URL, fire-and-forget
        // with its own bounded timeout.
        let resource = self.resource.clone();
        let gate = Arc::clone(&self.gate);
        self.runtime.spawn(async move {
            let _ = signal::delete_resource(&resource, &gate).await;
        });
    }
}

impl Demuxer for WhepDemuxer {
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
                Ok(None) => return Err(DemuxError::Source("whep session task ended".into())),
                Err(_) => {
                    if std::time::Instant::now() >= stall_deadline {
                        return Err(DemuxError::Source("whep feed stalled".into()));
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
        Err(DemuxError::Unsupported("seek on a WHEP session"))
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

/// RTP mode (str0m hands over packets, our layers own the buffering)
/// offering exactly what this engine decodes: H.264 video and Opus
/// audio. Codecs the decode factory would refuse are refused at
/// negotiation instead, so a server whose stream is VP8 answers with
/// nothing playable rather than feeding undecodable RTP.
fn rtc_config(provider: Arc<str0m::config::CryptoProvider>) -> RtcConfig {
    RtcConfig::new()
        .set_crypto_provider(provider)
        .set_rtp_mode(true)
        .clear_codecs()
        .enable_h264(true)
        .enable_opus(true)
}

/// Drive one signalling exchange on the I/O runtime, abandoning it as
/// soon as the engine's teardown probe goes true.
///
/// Signalling is a chain of server-chosen requests (up to six redirect
/// hops, each at the connect and request timeouts), and the thread
/// running it is one `bm_session_close` joins from the client's main
/// thread. Without this the close waits the whole chain out.
///
/// The `block_on` also constrains where the open may be called from: a
/// runtime worker thread panics on it. `WhepDemuxer::open` is public, so
/// it screens for that itself and refuses by name. Open it from a thread
/// of its own, as the engine's opener does, never from a spawned task.
fn drive<T>(
    runtime: &tokio::runtime::Handle,
    cancelled: &CancelProbe,
    work: impl Future<Output = Result<T, WhepError>>,
) -> Result<T, WhepError> {
    runtime.block_on(async {
        tokio::pin!(work);
        loop {
            if cancelled() {
                return Err(WhepError::Cancelled);
            }
            tokio::select! {
                biased;
                outcome = &mut work => return outcome,
                () = tokio::time::sleep(CANCEL_POLL) => {}
            }
        }
    })
}

fn whep_err(e: WhepError) -> DemuxError {
    match e {
        WhepError::Io(io) => DemuxError::Source(io.into()),
        WhepError::Url(detail) => DemuxError::Parse(format!("whep url: {detail}")),
        other => DemuxError::Source(other.to_string().into()),
    }
}

/// Bind the media socket on the wildcard address: the server's answer
/// may carry candidates on any of its interfaces, and a socket bound to
/// one local IP cannot transmit to destinations off that interface (the
/// nominated pair would die silently). The *advertised* host candidate
/// uses the interface that routes towards the (vetted) signalling
/// address, learned with a connected probe socket. Nonblocking so the
/// session task can adopt it into tokio.
fn bind_media_socket(
    signal_addr: SocketAddr,
) -> Result<(std::net::UdpSocket, SocketAddr), DemuxError> {
    let wildcard: SocketAddr = if signal_addr.is_ipv4() {
        "0.0.0.0:0".parse().expect("literal")
    } else {
        "[::]:0".parse().expect("literal")
    };
    let probe = std::net::UdpSocket::bind(wildcard)
        .map_err(|e| DemuxError::Source(format!("udp bind: {e}").into()))?;
    probe
        .connect(signal_addr)
        .map_err(|e| DemuxError::Source(format!("udp route probe: {e}").into()))?;
    let local_ip = probe
        .local_addr()
        .map_err(|e| DemuxError::Source(format!("udp local addr: {e}").into()))?
        .ip();
    drop(probe);
    let socket = std::net::UdpSocket::bind(wildcard)
        .map_err(|e| DemuxError::Source(format!("udp bind: {e}").into()))?;
    socket
        .set_nonblocking(true)
        .map_err(|e| DemuxError::Source(format!("udp nonblocking: {e}").into()))?;
    let port = socket
        .local_addr()
        .map_err(|e| DemuxError::Source(format!("udp local addr: {e}").into()))?
        .port();
    Ok((socket, SocketAddr::new(local_ip, port)))
}

fn add_host_candidate(rtc: &mut Rtc, local_addr: SocketAddr) -> Result<(), DemuxError> {
    let candidate = Candidate::host(local_addr, "udp")
        .map_err(|e| DemuxError::Parse(format!("host candidate: {e}")))?;
    rtc.add_local_candidate(candidate);
    Ok(())
}

/// Map the negotiated payload types onto receive lanes. H.264 may
/// negotiate several PTs (profiles); they all route to the one video
/// lane. A session with neither H.264 nor Opus has nothing this engine
/// can play; refusing at open makes it read as a codec problem, not a
/// stall.
fn build_lanes(rtc: &mut Rtc) -> Result<Vec<Lane>, String> {
    let mut h264_pts = Vec::new();
    let mut opus = None;
    for params in rtc.codec_config().params() {
        let spec = params.spec();
        match spec.codec {
            Codec::H264 => h264_pts.push(*params.pt()),
            Codec::Opus if opus.is_none() => {
                opus = Some((
                    *params.pt(),
                    spec.clock_rate.get(),
                    spec.channels.unwrap_or(2),
                ));
            }
            _ => {}
        }
    }
    let mut lanes = Vec::new();
    if !h264_pts.is_empty() {
        lanes.push(Lane::new(VIDEO_STREAM, LaneCodec::H264, h264_pts, 90_000)?);
    }
    if let Some((pt, clock_rate, channels)) = opus {
        lanes.push(Lane::new(
            AUDIO_STREAM,
            LaneCodec::Opus { channels },
            vec![pt],
            clock_rate,
        )?);
    }
    if lanes.is_empty() {
        return Err("no h264 video or opus audio negotiated".into());
    }
    Ok(lanes)
}
