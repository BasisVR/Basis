//! Sessions, the state machine and pipeline assembly. One session is one
//! pipeline: media-clock is the only position source, the Bank sits
//! between demux and decode, every stage exports counters, and the leased
//! FramePool carries decoded frames to the present pass.

mod audio;
mod capabilities;
mod pipeline;
mod playable;
mod pool;
mod present;
mod route;
mod sink;
mod sync;

pub use audio::{AudioConsumer, frames_before_origin};
pub use capabilities::{
    AudioCap, CAPABILITIES_VERSION, CapabilitySet, Route, TransportCap, VideoCap, capabilities,
};
pub use media_bitstream::CaptionCue;
pub use pipeline::PipelineShared;
pub use pool::FramePool;
#[cfg(windows)]
pub use present::render_present;
#[cfg(target_os = "android")]
pub use present::render_take;
pub use route::SLOW_VIDEO_DECODE_ENV;
pub use sync::{SYNC_DEAD_BAND, SYNC_SEEK_THRESHOLD, SYNC_SLEW_PPM, SyncAction, ladder};

use std::sync::atomic::{AtomicBool, AtomicI32, AtomicI64, AtomicU32, AtomicU64, Ordering};
use std::sync::mpsc::sync_channel;
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;

use media_bank::{Bank, BankConfig, BufferDepth, Liveness};
use media_clock::{ClockConfig, Generation, MediaClock, MediaTime};
use media_demux::{ByteSource, DemuxError, DemuxLimits, DemuxOptions, SourceError};
use media_diag::{EventCode, SessionDiag, Stage, diag_log, diag_warn};
use media_io::{AllowAllGate, FileSource, HttpSource, IoError, IoLimits, PublicAddressGate};

// Decode-channel depths. The audio side stays shallow: its decoders and
// the ring drain fast, so depth is only latency. The video side must take
// the whole startup burst without the release thread blocking on it. A
// video decoder with a shallow input queue (the MF AV1 extension accepts
// ~12 before MF_E_NOTACCEPTING, MF H.264 ~30) would otherwise wedge the
// single release thread mid-burst, starving audio and dragging the
// audio-master clock backwards. Compressed-AU memory here is bounded by
// the burst window, not the slot count.
const AUDIO_CHANNEL_DEPTH: usize = 8;
const VIDEO_CHANNEL_DEPTH: usize = 256;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum State {
    Idle = 0,
    Opening = 1,
    Buffering = 2,
    Playing = 3,
    Paused = 4,
    Ended = 5,
    Error = 6,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum ErrorCategory {
    None = 0,
    Io = 1,
    Demux = 2,
    Decode = 3,
    Present = 4,
    Config = 5,
    Internal = 6,
}

/// Structured error: stable code, category, human detail. The code
/// scheme is category * 100 + a per-category sub-code.
#[derive(Debug)]
pub struct EngineError {
    pub code: i32,
    pub category: ErrorCategory,
    pub stage: Stage,
    pub detail: String,
}

impl EngineError {
    pub fn io(e: IoError) -> Self {
        Self {
            code: 100 + e.kind as i32,
            category: ErrorCategory::Io,
            stage: Stage::Source,
            detail: e.to_string(),
        }
    }

    pub fn demux(e: DemuxError) -> Self {
        // A source failure surfacing through the demuxer is an I/O error
        // for the field report, not a parse error.
        if let DemuxError::Source(source) = &e
            && let Some(io) = source.downcast_ref::<IoError>()
        {
            return Self {
                code: 100 + io.kind as i32,
                category: ErrorCategory::Io,
                stage: Stage::Source,
                detail: io.to_string(),
            };
        }
        let sub = match &e {
            DemuxError::Io(_) => 1,
            DemuxError::Source(_) => 2,
            DemuxError::Parse(_) => 3,
            DemuxError::Unsupported(_) => 4,
            DemuxError::Cap(_) => 5,
            DemuxError::Refused(_) => 6,
        };
        Self {
            code: 200 + sub,
            category: ErrorCategory::Demux,
            stage: Stage::Demux,
            detail: e.to_string(),
        }
    }

    pub fn decode(e: media_decode::DecodeError) -> Self {
        Self {
            code: 301,
            category: ErrorCategory::Decode,
            stage: Stage::Decode,
            detail: e.to_string(),
        }
    }

    /// Every track the source carries was refused, so nothing can play.
    /// The detail is the refusals' reasons.
    pub fn refused(detail: impl Into<String>) -> Self {
        Self {
            code: 302,
            category: ErrorCategory::Decode,
            stage: Stage::Decode,
            detail: detail.into(),
        }
    }

    pub fn present(e: media_present::PresentError) -> Self {
        Self {
            code: 401,
            category: ErrorCategory::Present,
            stage: Stage::Present,
            detail: e.to_string(),
        }
    }

    pub fn config(detail: impl Into<String>) -> Self {
        Self {
            code: 501,
            category: ErrorCategory::Config,
            stage: Stage::Source,
            detail: detail.into(),
        }
    }

    /// The two sources of a split pair measure their timelines from points
    /// too far apart to play against one another. This is a property of
    /// the requested pair, so it shares the descriptor's category. It has
    /// its own sub-code because the fix is to pick a different rendition,
    /// not to correct a URL.
    pub fn split_origin_mismatch(detail: impl Into<String>) -> Self {
        Self {
            code: 502,
            category: ErrorCategory::Config,
            stage: Stage::Demux,
            detail: detail.into(),
        }
    }
}

/// Cross-thread session state, written by the pipeline, read by pollers.
#[derive(Default)]
pub struct SessionShared {
    pub state: AtomicU32,
    pub width: AtomicU32,
    pub height: AtomicU32,
    pub position_us: AtomicI64,
    /// Presented video pts minus the audio playhead, µs: the engine's own
    /// account of its A/V alignment, sampled where both are known at one
    /// wall reading. `i32::MIN` until an audio playhead and a presented
    /// frame both exist. Diagnostic only: nothing steers on it. The clock's
    /// own correction acts on clock-versus-playhead, which says nothing
    /// about the picture.
    pub av_offset_us: AtomicI32,
    pub duration_us: AtomicI64,
    pub frames_decoded: AtomicU64,
    /// Render-side copies, incremented by the FFI render event.
    pub frames_presented: AtomicU64,
    pub last_error: AtomicI32,
    pub last_error_category: AtomicU32,
    pub shared_texture_handle: AtomicU64,
    pub banked_us: AtomicI64,
    pub bank_holding: AtomicBool,
    pub audio_rate: AtomicU32,
    pub audio_channels: AtomicU32,
    /// Current seek generation (the Bank's, mirrored lock-free). The
    /// decode threads compare it against the generation of the last Flush
    /// they took, to drop stale-timeline work instead of parking on it. A
    /// parked stale AU would block the channel that delivers the Flush,
    /// with the decoder full and presentation parked, and nothing else
    /// would free it.
    pub generation: AtomicU64,
    pub(crate) stop: AtomicBool,
}

/// Liveness. Every transport except plain http(s) settles this itself:
/// RTSP, WHEP and RIST force Live, HLS reads it from the playlist, and a
/// resolver states it. The descriptor field covers the case left over, a
/// plain HTTP URL that is not a playlist.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum SourceLiveness {
    /// Force the live path: lag the edge, never read ahead.
    Live,
    /// Force the on-demand path: read ahead and seek.
    Vod,
    /// Work it out from the source's answer: finite and rangeable is
    /// on-demand, anything else is a live edge. The default, and correct
    /// unless a server's headers are wrong.
    #[default]
    Auto,
}

/// Decode-route preference: a per-user machine setting, never
/// world-authored. Enforced in one place, the route factory. A route the
/// platform does not have is a typed refusal.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum DecodePreference {
    /// Hardware first; the software path carries a `DecodeFallbackHwToSw`
    /// diagnostic when it engages. The default.
    #[default]
    HardwareWithFallback,
    /// Hardware or a typed refusal.
    HardwareOnly,
    /// Software only, for CPU comparisons and as a workaround for driver
    /// faults. Subject to the software-route performance cap.
    SoftwareOnly,
}

/// What the engine needs to open a source.
#[derive(Debug, Clone)]
pub struct OpenRequest {
    /// `http(s)://` URL or a local file path.
    pub url: String,
    /// A separate audio-only source to play against `url`, which is then
    /// treated as video-only. This is how adaptive ladders serve anything
    /// above their muxed fallback rung. The two legs are cuts of the same
    /// content, so their timelines already agree and the Bank meters them
    /// as one. On-demand HTTP(S) and local files only, since every live
    /// transport carries both tracks in one stream. `None` = one source
    /// carrying everything.
    pub audio_url: Option<String>,
    /// Explicit opt-out from the public-address gate, for local fixtures
    /// and test servers. Never set from world content.
    pub allow_local_addresses: bool,
    /// `None` = Auto (the sizing model's default).
    pub buffer_depth_ms: Option<u32>,
    pub liveness: SourceLiveness,
    /// Which of the container's audio tracks to bind, as an index into
    /// the offered list. Switching track re-opens the session at the
    /// current position, so this is only read at open. Out of range falls
    /// back to the first track with a note rather than failing, so an index
    /// remembered across a source change cannot break playback.
    pub audio_track: usize,
    /// Write the capture-recorder CSV here on close, sampled every 100 ms
    /// by an engine-owned thread. For hosts that cannot drive the recorder
    /// themselves (the managed ABI; bm-probe polls it in-process instead).
    /// `None` = off.
    pub diag_csv: Option<std::path::PathBuf>,
    /// Append each session's capture to `diag_csv` instead of replacing it.
    /// Otherwise a player that goes dormant and wakes repeatedly leaves
    /// only its last session behind.
    pub diag_csv_append: bool,
    /// Shared-playback divergence bound on live sessions: the furthest
    /// behind the live edge this viewer may sit, applied as a ceiling on
    /// the Bank's lag cap (and so on Auto's depth growth). Live position is
    /// never hard-synced between peers, so this is the world author's only
    /// control. `None` keeps the default lag cap.
    pub max_divergence_ms: Option<u32>,
    /// Decode-route preference, from the user's client-side setting.
    /// Absent in the descriptor = the default.
    pub decode_preference: DecodePreference,
}

impl OpenRequest {
    pub fn new(url: impl Into<String>) -> Self {
        Self {
            url: url.into(),
            audio_url: None,
            allow_local_addresses: false,
            buffer_depth_ms: None,
            liveness: SourceLiveness::default(),
            audio_track: 0,
            diag_csv: None,
            diag_csv_append: false,
            max_divergence_ms: None,
            decode_preference: DecodePreference::default(),
        }
    }
}

/// Byte-source wrapper feeding the Source stage counters.
struct CountedSource<S: ByteSource> {
    inner: S,
    diag: Arc<SessionDiag>,
}

impl<S: ByteSource> ByteSource for CountedSource<S> {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        self.inner.size()
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        let n = self.inner.read_at(offset, buf)?;
        let stage = self.diag.stage(Stage::Source);
        stage.out_count.fetch_add(1, Ordering::Relaxed);
        stage.out_bytes.fetch_add(n as u64, Ordering::Relaxed);
        Ok(n)
    }
}

pub struct Session {
    px: Arc<PipelineShared>,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
}

impl Session {
    /// Open a session. Returns immediately in `Opening`; all I/O happens on
    /// the pipeline's threads and progress is polled through the snapshot.
    pub fn open(request: OpenRequest) -> Self {
        Self::open_internal(request, None)
    }

    /// Open over a caller-built byte source instead of one derived from the
    /// URL, as the impairment harness does. The request's URL is
    /// display-only here.
    pub fn open_with_source(request: OpenRequest, source: Box<dyn ByteSource>) -> Self {
        Self::open_internal(request, Some(source))
    }

    fn open_internal(mut request: OpenRequest, source: Option<Box<dyn ByteSource>>) -> Self {
        let shared = Arc::new(SessionShared {
            av_offset_us: AtomicI32::new(i32::MIN),
            ..SessionShared::default()
        });
        let diag = Arc::new(SessionDiag::default());
        let wall = Arc::new(pipeline::EngineWall::new());

        let bank_cfg = BankConfig {
            liveness: match request.liveness {
                SourceLiveness::Live => Liveness::Live,
                // Auto starts on-demand and is re-decided once the source
                // has answered, as HLS re-decides from its playlist.
                SourceLiveness::Vod | SourceLiveness::Auto => Liveness::Vod,
            },
            depth: match request.buffer_depth_ms {
                Some(ms) => BufferDepth::Millis(ms),
                None => BufferDepth::Auto,
            },
            ..BankConfig::default()
        };
        // The divergence bound rides the lag cap, which also clamps
        // Auto's depth growth. An explicit depth beyond it fails typed
        // through the Bank's own validation.
        let bank_cfg = match request.max_divergence_ms {
            Some(ms) => BankConfig {
                lag_cap: bank_cfg.lag_cap.min(MediaTime::from_millis(i64::from(ms))),
                ..bank_cfg
            },
            None => bank_cfg,
        };

        let clock = MediaClock::new(
            ClockConfig::default(),
            wall.now(),
            MediaTime::ZERO,
            Generation(0),
        );

        let px = Arc::new(PipelineShared {
            shared: Arc::clone(&shared),
            diag,
            wall,
            clock: Arc::new(Mutex::new(clock)),
            bank: Arc::new(pipeline::BankShared {
                // Replaced by the opener once config validates; a default
                // Bank keeps the type simple.
                bank: Mutex::new(
                    Bank::new(BankConfig::default(), Generation(0)).expect("default bank config"),
                ),
                changed: std::sync::Condvar::new(),
            }),
            pool: FramePool::new(),
            commands: Mutex::new(Vec::new()),
            audio_consumer: Mutex::new(None),
            audio_shared: audio::new_audio_shared(),
            io_cancel: media_io::CancelToken::new(),
            video_active: AtomicBool::new(false),
            audio_active: AtomicBool::new(false),
            playable: playable::Playable::default(),
            audio_tail_out: AtomicU64::new(u64::MAX),
            clock_playing: AtomicBool::new(false),
            audio_ring_generation: AtomicU64::new(pipeline::NO_GENERATION),
            decode_preference: request.decode_preference,
            presented_generation: AtomicU64::new(pipeline::NO_GENERATION),
            presented_pts_us: AtomicI64::new(i64::MIN),
            shown_generation: AtomicU64::new(pipeline::NO_GENERATION),
            transport: Mutex::new(()),
            timeline: Mutex::new(()),
            pause_wanted: AtomicBool::new(false),
            seeks_pending: AtomicU32::new(0),
            seek_floor_us: AtomicI64::new(pipeline::NO_FLOOR),
            audio_start_us: AtomicI64::new(0),
            video_end_us: AtomicI64::new(i64::MAX),
            audio_end_us: AtomicI64::new(i64::MAX),
            generation_start_us: AtomicI64::new(0),
            seek_fed: AtomicU64::new(0),
            seek_taken: AtomicU64::new(0),
            captions: Mutex::new(std::collections::VecDeque::new()),
            user_data: Mutex::new(pipeline::UserDataRing::default()),
            audio_tracks: Mutex::new(Vec::new()),
            artwork: Mutex::new(None),
            present: present::PresentShared::new(),
            sync: sync::SyncShared::default(),
            sync_rate_ppm: AtomicI64::new(0),
            live: AtomicBool::new(false),
            // Set by the opener, and only when it really is spawning two
            // demux threads, so nothing waits on a leg that never runs.
            split: std::sync::OnceLock::new(),
            #[cfg(windows)]
            presenter: Mutex::new(None),
        });
        px.set_state(State::Opening);

        let threads = Arc::new(Mutex::new(Vec::new()));
        if let Some(path) = request.diag_csv.take() {
            let sampler_px = Arc::clone(&px);
            let append = request.diag_csv_append;
            let sampler = std::thread::Builder::new()
                .name("bm-diag".into())
                .spawn(move || run_diag_sampler(sampler_px, path, append))
                .expect("spawn diag sampler thread");
            threads
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .push(sampler);
        }
        let opener_px = Arc::clone(&px);
        let opener_threads = Arc::clone(&threads);
        let opener = std::thread::Builder::new()
            .name("bm-open".into())
            .spawn(move || open_and_run(opener_px, request, source, bank_cfg, opener_threads))
            .expect("spawn opener thread");
        threads
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(opener);

        Self { px, threads }
    }

    pub fn pipeline(&self) -> &Arc<PipelineShared> {
        &self.px
    }

    pub fn shared(&self) -> &Arc<SessionShared> {
        &self.px.shared
    }

    pub fn diag(&self) -> &Arc<SessionDiag> {
        &self.px.diag
    }

    pub fn play(&self) {
        // Buffering auto-plays when the first frame is ready; play only
        // reverses an explicit pause, including one still waiting on a
        // seek to land.
        let _transport = self.px.transport.lock().expect("transport lock");
        self.px.pause_wanted.store(false, Ordering::Relaxed);
        if self.px.state() == State::Paused as u32 {
            self.px.wall.resume();
            let wall = self.px.wall.now();
            {
                let mut clock = self.px.clock.lock().expect("clock lock");
                clock.set_playing(wall, true);
                self.px.clock_playing.store(true, Ordering::Relaxed);
                self.px.present.mirror_clock(wall, clock.now(wall), true);
            }
            // Only a failure can leave Paused without `transport`, and a
            // failed session keeps its Error.
            self.px.claim_state(&[State::Paused], State::Playing);
            self.px.bank.changed.notify_all();
        }
    }

    /// Pause an on-demand session. A live source is not pausable: the wall
    /// clock stops but delivery does not, so the Bank fills to its byte cap,
    /// the demuxer blocks, a UDP source loses everything after that, and on
    /// resume the session sits the pause's length behind the edge for good.
    /// On a live session the request is ignored with a line on the process
    /// log, and the state stays where it was.
    ///
    /// A pause holds across a seek: the session lands the new position,
    /// shows it, and stays paused there until `play`.
    pub fn pause(&self) {
        let _transport = self.px.transport.lock().expect("transport lock");
        let state = self.px.state();
        if state != State::Playing as u32 && state != State::Buffering as u32 {
            return;
        }
        // Read after the state: the opener publishes the liveness flag and
        // then Buffering, and `state()` is an acquire against that store,
        // so a request that sees Buffering sees the flag. Read first, the
        // flag could still be the pre-open default.
        if self.px.live.load(Ordering::Acquire) {
            diag_warn!("pause ignored: a live source is not pausable, reload it instead");
            return;
        }
        // A buffering session, or one with a seek still queued, pauses once
        // its position is showing: freezing the wall now would stop the
        // release schedule that delivers that picture. The decode threads
        // complete the pause.
        self.px.pause_wanted.store(true, Ordering::Relaxed);
        if state == State::Playing as u32 && self.px.seeks_pending.load(Ordering::Acquire) == 0 {
            self.px.park_paused();
        }
    }

    pub fn seek(&self, to: MediaTime) {
        seek_px(&self.px, to);
    }

    /// Feed the owner's extrapolated position as a soft sync target. The
    /// engine answers with a dead band, then a slew, then a seek. Negative
    /// clears the target (the local user took control, or the owner left).
    /// How the slew is applied depends on the clock master; see the
    /// snapshot's `sync_rate_ppm` for the audio consumer's part.
    pub fn set_sync_target(px: &PipelineShared, position_us: i64) {
        sync::set_sync_target(px, position_us);
    }

    /// The audio tracks the source offers instead of the bound one, in
    /// container order. Empty where there is nothing to choose between.
    /// Switching is a re-open with `OpenRequest::audio_track` set, so this
    /// is stable for the life of the session.
    pub fn audio_tracks(&self) -> Vec<media_demux::AudioTrackInfo> {
        self.pipeline()
            .audio_tracks
            .lock()
            .expect("audio tracks lock")
            .clone()
    }

    /// Cover art the container carried, where it carried one. The bytes
    /// are compressed exactly as stored; the caller decodes them.
    pub fn artwork(&self) -> Option<media_demux::Artwork> {
        self.pipeline()
            .artwork
            .lock()
            .expect("artwork lock")
            .clone()
    }

    /// Drain up to `max` pending caption cues (in-band CEA-608). Each is
    /// the full displayed text as of its PTS (empty = display cleared).
    /// Cues are surfaced on arrival; the consumer schedules display
    /// against the session position.
    pub fn drain_captions(px: &PipelineShared, max: usize) -> Vec<media_bitstream::CaptionCue> {
        let mut ring = px.captions.lock().expect("captions lock");
        let n = ring.len().min(max);
        ring.drain(..n).collect()
    }

    /// Drain pending SEI user-data messages: up to `max` of them, and
    /// no more than `max_bytes` of payload between them, so a caller
    /// copying into a fixed buffer takes exactly what fits and the rest
    /// waits. A message that could never fit on its own (payload above
    /// `max_bytes`) is dropped rather than left blocking the head.
    /// Messages are surfaced on arrival; the consumer schedules delivery
    /// against the session position.
    pub fn drain_user_data(
        px: &PipelineShared,
        max: usize,
        max_bytes: usize,
    ) -> Vec<media_bitstream::SeiUserData> {
        px.user_data
            .lock()
            .expect("user data lock")
            .drain(max, max_bytes)
    }

    /// Report the managed sink's estimated output latency (µs): the chain
    /// between the audio pull and the speaker (DSP buffers + HAL). The
    /// playhead the clock masters on is shifted back by it, so video paces
    /// to the audible position. Clamped to 0..=500 ms; 0 (the default)
    /// applies no shift.
    ///
    /// A running clock moves by the change, since it was started from the
    /// latency then in force. An audio-only session's clock starts the
    /// moment the ring fills, usually before the host has reported any.
    /// A parked clock stays where it is, so a paused position does not jump
    /// on screen: a landing restarts it from the new value, and after a
    /// resume the audio master brings it round.
    pub fn set_audio_latency(px: &PipelineShared, latency_us: i64) {
        let latency = latency_us.clamp(0, 500_000);
        // Under the clock lock, which both clock starts hold while reading
        // the latency: a start reads either the old value and is moved
        // here, or the new one and is not.
        let mut clock = px.clock.lock().expect("clock lock");
        let old = px
            .audio_shared
            .output_latency_us
            .swap(latency, Ordering::Relaxed);
        if old == latency || !clock.is_playing() {
            return;
        }
        let wall = px.wall.now();
        let moved = clock.now(wall) - MediaTime::from_micros(latency - old);
        clock.discontinuity(wall, moved);
        px.present.mirror_clock(wall, moved, true);
    }

    /// Lock-free audio pull for the Unity audio thread. Fills `out`
    /// (interleaved f32) and returns frames written. Serves silence while
    /// the clock is parked, outside the timeline in force (see
    /// `audio_serves`), or under contention.
    pub fn read_audio(px: &PipelineShared, out: &mut [f32]) -> usize {
        // A parked clock means presentation has not reached this timeline
        // yet, and the state alone races (a present in flight can flip a
        // seeking session back to Playing).
        if !pipeline::audio_serves(
            px.state(),
            px.clock_playing.load(Ordering::Relaxed),
            px.pause_wanted.load(Ordering::Relaxed),
            px.seeks_pending.load(Ordering::Acquire),
            px.audio_ring_generation.load(Ordering::Acquire),
            px.shared.generation.load(Ordering::Relaxed),
        ) {
            out.fill(0.0);
            return 0;
        }
        let Ok(mut slot) = px.audio_consumer.try_lock() else {
            out.fill(0.0);
            return 0;
        };
        match slot.as_mut() {
            Some(consumer) => consumer.pull(out, px.wall.now().as_micros()),
            None => {
                out.fill(0.0);
                0
            }
        }
    }

    pub fn close(&mut self) {
        self.px.shared.stop.store(true, Ordering::Relaxed);
        self.px.io_cancel.cancel();
        self.px.bank.changed.notify_all();
        // Drop runs this, where a panic would abort the process rather than
        // reach the ABI's fences, so every site on this lock recovers from
        // poisoning. The handles stay valid across one.
        //
        // Drained in a loop because the opener registers the pipeline's
        // threads part-way through its own run. Closing mid-open finds only
        // the opener in the list, and returning after joining it would leave
        // the threads it spawned running on state the caller believes is
        // released. The opener is the only registrar, so the drain after it
        // is joined is the last.
        loop {
            let handles: Vec<_> =
                std::mem::take(&mut *self.threads.lock().unwrap_or_else(|e| e.into_inner()));
            if handles.is_empty() {
                break;
            }
            for handle in handles {
                let _ = handle.join();
            }
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.close();
    }
}

/// Seek, from a session handle or the sync ladder. A paused or ended
/// session resumes into buffering at the new position; the demux thread
/// parks the clock and the video thread restarts it at the first
/// post-seek frame (the audio thread, on a source with no video). A paused
/// session resumes only to land the seek, and pauses again once the new
/// position is showing. A failed or closing
/// session has no demux thread left to take the seek, and stays as it is.
pub(crate) fn seek_px(px: &PipelineShared, to: MediaTime) {
    let _transport = px.transport.lock().expect("transport lock");
    if px.stopping() {
        return;
    }
    px.seeks_pending.fetch_add(1, Ordering::Relaxed);
    px.wall.resume();
    px.set_state(State::Buffering);
    px.commands
        .lock()
        .expect("commands lock")
        .push(pipeline::Command::Seek(to));
    px.bank.changed.notify_all();
}

/// The engine-owned capture-recorder loop behind `OpenRequest::diag_csv`:
/// sample every 100 ms until the session stops, then write the CSV.
fn run_diag_sampler(px: Arc<PipelineShared>, path: std::path::PathBuf, append: bool) {
    let mut recorder = media_diag::CaptureRecorder::default();
    while !px.shared.stop.load(Ordering::Relaxed) {
        recorder.sample(px.wall.now(), &px.diag);
        std::thread::sleep(std::time::Duration::from_millis(100));
    }
    recorder.sample(px.wall.now(), &px.diag);
    // Only the first capture into a file carries the header. An append to a
    // file that is missing or empty still needs one, so ask the filesystem.
    let existing = std::fs::metadata(&path).map(|m| m.len()).unwrap_or(0);
    let header = !append || existing == 0;
    let written = std::fs::OpenOptions::new()
        .write(true)
        .create(true)
        .append(append)
        .truncate(!append)
        .open(&path)
        .and_then(|file| recorder.write_csv_rows(std::io::BufWriter::new(file), header));
    if let Err(e) = written {
        diag_warn!("diag csv {}: {e}", path.display());
    }
}

/// Fill `head` from offset 0; a short fill means a short source.
fn read_head(source: &mut dyn ByteSource, head: &mut [u8]) -> Result<usize, SourceError> {
    let mut filled = 0usize;
    while filled < head.len() {
        let n = source.read_at(filled as u64, &mut head[filled..])?;
        if n == 0 {
            break;
        }
        filled += n;
    }
    Ok(filled)
}

/// Read a whole (small) resource through a `ByteSource`, capped.
fn read_all(source: &mut dyn ByteSource, cap: u64) -> Result<Vec<u8>, SourceError> {
    let mut bytes = Vec::new();
    let mut buf = vec![0u8; 64 * 1024];
    loop {
        let n = source.read_at(bytes.len() as u64, &mut buf)?;
        if n == 0 {
            return Ok(bytes);
        }
        if bytes.len() as u64 + n as u64 > cap {
            return Err("playlist exceeds the size cap".into());
        }
        bytes.extend_from_slice(&buf[..n]);
    }
}

const PLAYLIST_CAP: u64 = 4 * 1024 * 1024;

/// Where a playlist came from, which fixes what its URIs may reach for the
/// life of the session. A playlist read off the network can name only more
/// network. One opened from disk can name either, since the address gate
/// already covers reaching the network.
#[derive(Clone, Copy)]
enum PlaylistOrigin {
    Network,
    Disk,
}

/// HLS: hand the URL to the HLS demuxer with a per-resource fetcher built
/// for where the playlist came from. The playlist states its own liveness
/// (EXT-X-ENDLIST) and the Bank mode follows it. The HLS scheduler handles
/// reconnects itself, so no engine reconnect factory is passed. A variant
/// whose audio is a separate rendition gets that rendition as the audio
/// leg of a split pair.
fn open_hls(
    px: Arc<PipelineShared>,
    url: &str,
    playlist: Vec<u8>,
    origin: PlaylistOrigin,
    allow_local: bool,
    mut bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    let fetcher = match hls_fetcher(&px, url, origin, allow_local) {
        Ok(fetcher) => fetcher,
        Err(e) => {
            px.fail(EngineError::io(e));
            return;
        }
    };
    let demuxer = match media_hls::HlsDemuxer::open(
        url,
        playlist,
        Box::new(fetcher),
        DemuxLimits::default(),
        Generation(0),
    ) {
        Ok(demuxer) => demuxer,
        Err(e) => {
            px.fail(EngineError::demux(e));
            return;
        }
    };
    bank_cfg.liveness = if demuxer.is_live() {
        Liveness::Live
    } else {
        Liveness::Vod
    };
    let audio_leg = demuxer.audio_rendition().and_then(|rendition| {
        let opened = rendition.map_err(str::to_owned).and_then(|audio_url| {
            open_hls_rendition(
                &px,
                url,
                audio_url,
                origin,
                allow_local,
                demuxer.join_anchor(),
            )
        });
        match opened {
            Ok(leg) => Some(Box::new(leg) as Box<dyn media_demux::Demuxer>),
            // The picture plays on; the reason goes where any other
            // track left out is shown.
            Err(detail) => {
                let reason = format!("audio rendition not played: {detail}");
                px.playable.reason(&reason);
                px.diag
                    .event(px.wall.now(), EventCode::CodecRefused, Stage::Demux, reason);
                None
            }
        }
    });
    finish_open_split(px, Box::new(demuxer), audio_leg, bank_cfg, None, threads);
}

/// The fetcher for a playlist lane: network-only for a playlist that came
/// off the network, confined to the playlist's directory for one on disk.
fn hls_fetcher(
    px: &Arc<PipelineShared>,
    url: &str,
    origin: PlaylistOrigin,
    allow_local: bool,
) -> Result<media_io::ResourceFetcher, media_io::IoError> {
    let gate: Arc<dyn media_io::AddressGate> = if allow_local {
        Arc::new(AllowAllGate)
    } else {
        Arc::new(PublicAddressGate)
    };
    let limits = IoLimits::default();
    match origin {
        PlaylistOrigin::Network => Ok(media_io::ResourceFetcher::remote(
            limits,
            gate,
            px.io_cancel.clone(),
        )),
        PlaylistOrigin::Disk => {
            // Segments resolve beside the playlist, so its own directory
            // is the root. A bare filename has none; that is the cwd.
            let dir = std::path::Path::new(url)
                .parent()
                .unwrap_or(std::path::Path::new(""));
            let dir = if dir.as_os_str().is_empty() {
                std::path::Path::new(".")
            } else {
                dir
            };
            media_io::ResourceFetcher::local(dir, limits, gate, px.io_cancel.clone())
        }
    }
}

/// An audio rendition's demuxer, fetched and joined beside its variant.
/// The fetcher is rooted where the master playlist's was, since the
/// rendition's URI was resolved against it.
fn open_hls_rendition(
    px: &Arc<PipelineShared>,
    master_url: &str,
    audio_url: &str,
    origin: PlaylistOrigin,
    allow_local: bool,
    anchor: Option<media_hls::JoinAnchor>,
) -> Result<media_hls::HlsDemuxer, String> {
    let mut fetcher =
        hls_fetcher(px, master_url, origin, allow_local).map_err(|e| e.to_string())?;
    let playlist = media_hls::SegmentFetcher::fetch(&mut fetcher, audio_url, PLAYLIST_CAP)
        .map_err(|e| e.to_string())?;
    media_hls::HlsDemuxer::open_rendition(
        audio_url,
        playlist,
        Box::new(fetcher),
        DemuxLimits::default(),
        Generation(0),
        anchor,
    )
    .map_err(|e| e.to_string())
}

/// Live HTTP: build the streaming source once and sniff it. An HLS
/// playlist goes to `open_hls`; anything else gets a factory that rebuilds
/// source and demuxer when the demux thread sees transport loss.
fn open_http_live(
    px: Arc<PipelineShared>,
    url: &str,
    allow_local: bool,
    bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
    options: DemuxOptions,
) {
    let gate: Arc<dyn media_io::AddressGate> = if allow_local {
        Arc::new(AllowAllGate)
    } else {
        Arc::new(PublicAddressGate)
    };
    let source = match media_io::HttpLiveSource::open(
        url,
        IoLimits::default(),
        gate,
        px.io_cancel.clone(),
    ) {
        Ok(source) => source,
        Err(e) => {
            px.fail(EngineError::io(e));
            return;
        }
    };
    open_http_live_with(px, url, source, allow_local, bank_cfg, threads, options);
}

/// Live HTTP from an already-open source, so the Auto probe can hand over
/// the body it opened instead of spending a second connection.
fn open_http_live_with(
    px: Arc<PipelineShared>,
    url: &str,
    source: media_io::HttpLiveSource,
    allow_local: bool,
    mut bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
    options: DemuxOptions,
) {
    // An Auto request that the probe settled as live arrives with the
    // on-demand default, and would otherwise run the whole session that way:
    // read-ahead cap, no priming, no debt bound, sync targets honoured,
    // pause allowed.
    bank_cfg.liveness = Liveness::Live;
    let mut counted = CountedSource {
        inner: source,
        diag: Arc::clone(&px.diag),
    };
    let mut head = [0u8; 1024];
    let filled = match read_head(&mut counted, &mut head) {
        Ok(filled) => filled,
        Err(e) => {
            px.fail(EngineError::demux(DemuxError::Source(e)));
            return;
        }
    };
    if media_hls::looks_like_playlist(&head[..filled]) {
        match read_all(&mut counted, PLAYLIST_CAP) {
            Ok(playlist) => {
                // Only ever reached for an http(s) URL.
                open_hls(
                    px,
                    url,
                    playlist,
                    PlaylistOrigin::Network,
                    allow_local,
                    bank_cfg,
                    threads,
                );
            }
            Err(e) => px.fail(EngineError::demux(DemuxError::Source(e))),
        }
        return;
    }
    let demuxer = match media_demux::open_auto_with(
        Box::new(counted),
        DemuxLimits::default(),
        Generation(0),
        &options,
    ) {
        Ok(demuxer) => demuxer,
        Err(e) => {
            px.fail(EngineError::demux(e));
            return;
        }
    };
    let reconnect_factory: pipeline::DemuxFactory = {
        let px = Arc::clone(&px);
        let url = url.to_string();
        let options = options.clone();
        Box::new(move || {
            let gate: Arc<dyn media_io::AddressGate> = if allow_local {
                Arc::new(AllowAllGate)
            } else {
                Arc::new(PublicAddressGate)
            };
            let source = media_io::HttpLiveSource::open(
                &url,
                IoLimits::default(),
                gate,
                px.io_cancel.clone(),
            )
            .map_err(EngineError::io)?;
            let counted = CountedSource {
                inner: source,
                diag: Arc::clone(&px.diag),
            };
            let generation = px.bank.bank.lock().expect("bank lock").generation();
            media_demux::open_auto_with(
                Box::new(counted),
                DemuxLimits::default(),
                generation,
                &options,
            )
            .map_err(EngineError::demux)
        })
    };
    finish_open(px, demuxer, bank_cfg, Some(reconnect_factory), threads);
}

/// RTSP: always live. `rtsp://` tries UDP first (media-rtp under retina)
/// and falls back to TCP-interleaved; `rtspt://` is pinned to TCP. The
/// Bank absorbs jitter either way, and the reconnect factory rebuilds the
/// whole session on transport loss. The host is resolved and vetted
/// against the address gate here, and the client dials those addresses in
/// turn; the same gate vets the SETUP response's UDP peer address.
fn open_rtsp(
    px: Arc<PipelineShared>,
    url: &str,
    allow_local: bool,
    mut bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    bank_cfg.liveness = Liveness::Live;
    let build = {
        let px = Arc::clone(&px);
        let url = url.to_string();
        move |generation: Generation| -> Result<Box<dyn media_demux::Demuxer>, EngineError> {
            let gate: Arc<dyn media_io::AddressGate> = if allow_local {
                Arc::new(AllowAllGate)
            } else {
                Arc::new(PublicAddressGate)
            };
            let parsed = url::Url::parse(&url.replacen("rtspt://", "rtsp://", 1))
                .map_err(|e| EngineError::config(format!("rtsp url: {e}")))?;
            let host = parsed
                .host_str()
                .ok_or_else(|| EngineError::config("rtsp url without host"))?;
            let servers =
                media_io::resolve_vetted_all(host, parsed.port().unwrap_or(554), gate.as_ref())
                    .map_err(EngineError::io)?;
            let cancel = px.io_cancel.clone();
            let peer_gate = Arc::clone(&gate);
            let demuxer = media_rtsp::RtspDemuxer::open(
                &url,
                servers,
                generation,
                media_io::io_runtime_handle(),
                Box::new(move || cancel.is_cancelled()),
                Arc::new(move |ip| peer_gate.permit(ip)),
            )
            .map_err(EngineError::demux)?;
            if let Some(reason) = demuxer.fallback_reason() {
                px.diag.event(
                    px.wall.now(),
                    EventCode::TransportFallback,
                    Stage::Source,
                    reason.to_string(),
                );
                diag_warn!("rtsp transport fallback: {reason}");
            }
            diag_log!("rtsp transport: {}", demuxer.transport());
            Ok(Box::new(demuxer))
        }
    };
    let demuxer = match build(Generation(0)) {
        Ok(demuxer) => demuxer,
        Err(e) => {
            px.fail(e);
            return;
        }
    };
    let factory: pipeline::DemuxFactory = {
        let px = Arc::clone(&px);
        Box::new(move || {
            let generation = px.bank.bank.lock().expect("bank lock").generation();
            build(generation)
        })
    };
    finish_open(px, demuxer, bank_cfg, Some(factory), threads);
}

/// WHEP: always live, with the Bank at its floor (depth equals the decoder
/// cushion, so the lag target is zero). str0m's NACK recovery and
/// media-rtp's bounded reorder absorb network jitter upstream, and a deep
/// Bank on top would only add latency. An explicit depth request still
/// wins. Reconnect re-runs the whole signalling exchange. The signalling
/// host is vetted and pinned inside the crate, and every media-path
/// address passes the same gate at the transmit boundary.
fn open_whep(
    px: Arc<PipelineShared>,
    url: &str,
    request: &OpenRequest,
    mut bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    bank_cfg.liveness = Liveness::Live;
    if request.buffer_depth_ms.is_none() {
        bank_cfg.depth = BufferDepth::Millis(bank_cfg.decoder_cushion.as_micros() as u32 / 1000);
    }
    let allow_local = request.allow_local_addresses;
    let build = {
        let px = Arc::clone(&px);
        let url = url.to_string();
        move |generation: Generation| -> Result<Box<dyn media_demux::Demuxer>, EngineError> {
            let gate: Arc<dyn media_io::AddressGate> = if allow_local {
                Arc::new(AllowAllGate)
            } else {
                Arc::new(PublicAddressGate)
            };
            let cancel = px.io_cancel.clone();
            let demuxer = media_whep::WhepDemuxer::open(
                &url,
                generation,
                media_io::io_runtime_handle(),
                Box::new(move || cancel.is_cancelled()),
                gate,
            )
            .map_err(EngineError::demux)?;
            diag_log!(
                "whep negotiated: {} flow{}",
                demuxer.answer_flow(),
                if demuxer.ice_servers().is_empty() {
                    String::new()
                } else {
                    format!(", ice-servers {:?}", demuxer.ice_servers())
                }
            );
            Ok(Box::new(demuxer))
        }
    };
    let demuxer = match build(Generation(0)) {
        Ok(demuxer) => demuxer,
        Err(e) => {
            px.fail(e);
            return;
        }
    };
    let factory: pipeline::DemuxFactory = {
        let px = Arc::clone(&px);
        Box::new(move || {
            let generation = px.bank.bank.lock().expect("bank lock").generation();
            build(generation)
        })
    };
    finish_open(px, demuxer, bank_cfg, Some(factory), threads);
}

/// RIST: always live. librist owns the sockets, ARQ, jitter buffer and
/// PSK-AES and serves recovered TS as a sequential byte source. It keeps
/// the flow alive itself, so no engine reconnect factory is passed. The
/// host is resolved and vetted against the address gate here, and librist
/// is pinned to the vetted literal.
fn open_rist(
    px: Arc<PipelineShared>,
    url: &str,
    allow_local: bool,
    mut bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
    options: DemuxOptions,
) {
    bank_cfg.liveness = Liveness::Live;
    let gate: Arc<dyn media_io::AddressGate> = if allow_local {
        Arc::new(AllowAllGate)
    } else {
        Arc::new(PublicAddressGate)
    };
    let parsed = match url::Url::parse(url) {
        Ok(parsed) => parsed,
        Err(e) => {
            px.fail(EngineError::config(format!("rist url: {e}")));
            return;
        }
    };
    let host = match parsed.host_str() {
        Some(host) => host,
        None => {
            px.fail(EngineError::config("rist url without host"));
            return;
        }
    };
    let port = match parsed.port() {
        Some(port) => port,
        None => {
            px.fail(EngineError::config("rist url requires an explicit port"));
            return;
        }
    };
    let vetted = match media_io::resolve_vetted(host, port, gate.as_ref()) {
        Ok(vetted) => vetted,
        Err(e) => {
            px.fail(EngineError::io(e));
            return;
        }
    };
    let cancel = px.io_cancel.clone();
    let source =
        match media_rist::RistSource::open(url, vetted, Box::new(move || cancel.is_cancelled())) {
            Ok(source) => source,
            Err(e) => {
                px.fail(EngineError::config(e.to_string()));
                return;
            }
        };
    diag_log!(
        "rist transport: librist {} main profile",
        media_rist::RistSource::library_version()
    );
    let counted = CountedSource {
        inner: Box::new(source) as Box<dyn ByteSource>,
        diag: Arc::clone(&px.diag),
    };
    match media_demux::open_auto_with(
        Box::new(counted),
        DemuxLimits::default(),
        Generation(0),
        &options,
    ) {
        Ok(demuxer) => finish_open(px, demuxer, bank_cfg, None, threads),
        Err(e) => px.fail(EngineError::demux(e)),
    }
}

/// What a session's URL names. Decided once, from the URL parser's
/// normalised scheme rather than raw prefixes, because schemes are
/// case-insensitive (RFC 3986 §3.1) and the managed classifier compares
/// them that way. A local path is its own case, not the fallthrough: a
/// string that matches nothing known is refused, not opened off disk.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SourceKind {
    Http,
    Rtsp,
    Whep,
    Rist,
    File,
    /// The caller brought the bytes, so the request's URL is a label
    /// rather than a location and names nothing to open.
    Supplied,
}

impl SourceKind {
    /// Every transport that carries both tracks in one stream, so a
    /// separate audio leg cannot apply to it.
    fn is_live_transport(self) -> bool {
        matches!(self, SourceKind::Rtsp | SourceKind::Whep | SourceKind::Rist)
    }
}

/// A path that can only name a place on this machine: relative, rooted,
/// drive-absolute, or the extended-length spelling of a drive path. Any
/// other shape may reach a host. Windows resolves `\\host`, `\??\UNC\host`,
/// `\\.\UNC\host` and the object-namespace routes to the UNC provider
/// through the SMB redirector, so opening one makes a network connection
/// that never passes the address gate. Judged by the shapes allowed, since
/// the spellings that reach a host are many.
///
/// Matched as text on every host: `Path` recognises only the syntax of the
/// platform it was compiled for, and the answer should not depend on that.
fn is_a_local_path(url: &str) -> bool {
    let separator = |b: u8| b == b'\\' || b == b'/';
    match url.as_bytes() {
        // The long spelling of a drive path, used past MAX_PATH. Backslashes
        // only: `\\?\` reaches the object manager as written, while every
        // other device prefix is normalised first with the device namespace
        // as its root, so `..` climbs out of the drive (`\\.\C:\..\UNC\host`).
        [b'\\', b'\\', b'?', b'\\', drive, b':', rest @ ..] => {
            drive.is_ascii_alphabetic() && rest.first().is_none_or(|&b| b == b'\\')
        }
        // Windows takes either separator in either position, so all four
        // pairings open the same namespace.
        [a, b, ..] if separator(*a) && separator(*b) => false,
        // `\??\` is handed to the object manager unnormalised, where
        // `\??\UNC\host` is a share.
        [a, b'?', b'?', rest @ ..]
            if separator(*a) && rest.first().is_none_or(|&b| separator(b)) =>
        {
            false
        }
        _ => true,
    }
}

/// The URL with its scheme in the parser's normalised form and every other
/// byte untouched. The transports match their schemes as text, so this
/// normalises once instead of teaching each that `RTSP://` is `rtsp://`.
///
/// Spliced only where the leading bytes really are that scheme in another
/// case. The parser skips leading control characters and spaces before
/// reading a scheme, so its scheme does not always start at byte zero, and
/// splicing on length alone would build a string that is neither the
/// input nor what was parsed. Where they disagree the URL is left as it
/// came in.
fn with_normalised_scheme(url: &str, scheme: &str) -> String {
    match url.get(..scheme.len()) {
        Some(prefix) if prefix.eq_ignore_ascii_case(scheme) && prefix != scheme => {
            format!("{scheme}{}", &url[scheme.len()..])
        }
        _ => url.to_owned(),
    }
}

/// Classify `url` and return the spelling the openers should use. An
/// unknown scheme is a typed refusal.
fn classify(url: &str) -> Result<(SourceKind, String), EngineError> {
    let Ok(parsed) = url::Url::parse(url) else {
        return Ok((SourceKind::File, url.to_owned()));
    };
    // A single-letter "scheme" is a Windows drive letter, not a URL.
    let scheme = parsed.scheme();
    if scheme.len() <= 1 {
        return Ok((SourceKind::File, url.to_owned()));
    }
    let kind = match scheme {
        "http" | "https" => SourceKind::Http,
        "rtsp" | "rtspt" => SourceKind::Rtsp,
        "whep" | "wheps" => SourceKind::Whep,
        "rist" => SourceKind::Rist,
        other => {
            return Err(EngineError::config(format!(
                "unsupported source scheme: {other}"
            )));
        }
    };
    Ok((kind, with_normalised_scheme(url, scheme)))
}

/// The opener: source and demuxer construction (blocking I/O), then the
/// pipeline threads.
fn open_and_run(
    px: Arc<PipelineShared>,
    request: OpenRequest,
    source_override: Option<Box<dyn ByteSource>>,
    bank_cfg: BankConfig,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    let diag = Arc::clone(&px.diag);
    let (kind, url) = match classify(&request.url) {
        // With `open_with_source` the URL is display-only, whatever it
        // parses as. Refusing it would break such a caller, and reading it
        // as a location is worse: "case 4" parses as a relative path, which
        // would let a caller-supplied playlist fetch files from the working
        // directory. Every transport branch below already requires no
        // override.
        Ok((_, url)) if source_override.is_some() => (SourceKind::Supplied, url),
        Err(_) if source_override.is_some() => (SourceKind::Supplied, request.url.clone()),
        Ok(classified) => classified,
        Err(e) => {
            px.fail(e);
            return;
        }
    };
    let is_http = kind == SourceKind::Http;
    let options = DemuxOptions {
        audio_track: request.audio_track,
    };

    // A separate audio leg only makes sense where the primary is an
    // on-demand byte stream. Every live transport carries both tracks in
    // one stream, and a caller-supplied source is a single stream. Refuse
    // rather than silently play no audio.
    if request.audio_url.is_some()
        && (kind.is_live_transport()
            || source_override.is_some()
            || request.liveness == SourceLiveness::Live)
    {
        px.fail(EngineError::config(
            "audio_url applies to on-demand HTTP(S) and file sources only",
        ));
        return;
    }

    if source_override.is_none() && kind == SourceKind::Rtsp {
        open_rtsp(px, &url, request.allow_local_addresses, bank_cfg, threads);
        return;
    }

    if source_override.is_none() && kind == SourceKind::Whep {
        open_whep(px, &url, &request, bank_cfg, threads);
        return;
    }

    if source_override.is_none() && kind == SourceKind::Rist {
        open_rist(
            px,
            &url,
            request.allow_local_addresses,
            bank_cfg,
            threads,
            options,
        );
        return;
    }

    // Live HTTP: a streaming source plus a factory that rebuilds source and
    // demuxer on transport loss.
    if source_override.is_none() && is_http && request.liveness == SourceLiveness::Live {
        open_http_live(
            px,
            &url,
            request.allow_local_addresses,
            bank_cfg,
            threads,
            options,
        );
        return;
    }

    let source: Box<dyn ByteSource> = if let Some(source) = source_override {
        Box::new(CountedSource {
            inner: source,
            diag: Arc::clone(&diag),
        })
    } else if is_http {
        // The ranged on-demand source, which is also the probe that
        // settles Auto.
        let gate: Arc<dyn media_io::AddressGate> = if request.allow_local_addresses {
            Arc::new(AllowAllGate)
        } else {
            Arc::new(PublicAddressGate)
        };
        match HttpSource::open(&url, IoLimits::default(), gate, px.io_cancel.clone()) {
            Ok(source) => {
                // A split session is always on-demand (the resolver states
                // liveness for those), so it is never inferred live.
                if request.liveness == SourceLiveness::Auto
                    && request.audio_url.is_none()
                    && !source.is_seekable()
                {
                    px.diag.event(
                        px.wall.now(),
                        EventCode::CapabilityProbe,
                        Stage::Source,
                        format!(
                            "liveness inferred Live for {url}: the source states neither a usable length nor byte ranges"
                        ),
                    );
                    // The probe's body is the live stream, so adopt it.
                    // Connecting again costs a handshake, rejoins the stream
                    // later than the probe left it, and fails outright on an
                    // origin that serves one client at a time.
                    match media_io::HttpLiveSource::adopt(
                        source,
                        &IoLimits::default(),
                        px.io_cancel.clone(),
                    ) {
                        Ok(live) => open_http_live_with(
                            px,
                            &url,
                            live,
                            request.allow_local_addresses,
                            bank_cfg,
                            threads,
                            options,
                        ),
                        Err(_) => open_http_live(
                            px,
                            &url,
                            request.allow_local_addresses,
                            bank_cfg,
                            threads,
                            options,
                        ),
                    }
                    return;
                }
                Box::new(CountedSource {
                    inner: source,
                    diag: Arc::clone(&diag),
                })
            }
            Err(e) => {
                px.fail(EngineError::io(e));
                return;
            }
        }
    } else {
        if !is_a_local_path(&url) && !request.allow_local_addresses {
            px.fail(EngineError::config(format!(
                "only relative, rooted and drive paths are opened unless local addresses are permitted: {url}"
            )));
            return;
        }
        match FileSource::open(std::path::Path::new(&url)) {
            Ok(source) => Box::new(CountedSource {
                inner: source,
                diag: Arc::clone(&diag),
            }),
            Err(e) => {
                px.fail(EngineError::io(e));
                return;
            }
        }
    };

    // The container is sniffed, not taken from the extension. A playlist
    // head goes to `open_hls`.
    let mut source = source;
    let mut head = [0u8; 1024];
    match read_head(&mut source, &mut head) {
        Ok(filled) if media_hls::looks_like_playlist(&head[..filled]) => {
            if request.audio_url.is_some() {
                px.fail(EngineError::config(
                    "audio_url applies to on-demand HTTP(S) and file sources only",
                ));
                return;
            }
            match read_all(&mut source, PLAYLIST_CAP) {
                Ok(playlist) => {
                    drop(source);
                    // Only a playlist opened off the disk gets a fetcher
                    // that can reach the disk. Everything else, including a
                    // caller-supplied source, stays on the network side.
                    let origin = if kind == SourceKind::File {
                        PlaylistOrigin::Disk
                    } else {
                        PlaylistOrigin::Network
                    };
                    open_hls(
                        px,
                        &url,
                        playlist,
                        origin,
                        request.allow_local_addresses,
                        bank_cfg,
                        threads,
                    );
                }
                Err(e) => px.fail(EngineError::demux(DemuxError::Source(e))),
            }
            return;
        }
        Ok(_) => {}
        Err(e) => {
            px.fail(EngineError::demux(DemuxError::Source(e)));
            return;
        }
    }
    let demuxer = match media_demux::open_auto_with(
        source,
        DemuxLimits::default(),
        Generation(0),
        &options,
    ) {
        Ok(demuxer) => demuxer,
        Err(e) => {
            px.fail(EngineError::demux(e));
            return;
        }
    };
    let audio_leg = match request.audio_url.as_deref() {
        Some(audio_url) => {
            match open_audio_leg(&px, audio_url, request.allow_local_addresses, &diag) {
                Some(leg) => Some(leg),
                // open_audio_leg has already failed the session.
                None => return,
            }
        }
        None => None,
    };
    finish_open_split(px, demuxer, audio_leg, bank_cfg, None, threads);
}

/// Builds the demuxer for a split session's audio leg: one plain on-demand
/// byte stream, HTTP(S) or a local file. Fails the session and returns
/// `None` if it cannot be opened: a split source without its audio is not
/// worth playing silently.
fn open_audio_leg(
    px: &Arc<PipelineShared>,
    url: &str,
    allow_local: bool,
    diag: &Arc<SessionDiag>,
) -> Option<Box<dyn media_demux::Demuxer>> {
    let (kind, url) = match classify(url) {
        Ok(classified) => classified,
        Err(e) => {
            px.fail(e);
            return None;
        }
    };
    if kind.is_live_transport() {
        px.fail(EngineError::config(
            "audio_url applies to on-demand HTTP(S) and file sources only",
        ));
        return None;
    }
    let url = url.as_str();
    let source: Box<dyn ByteSource> = if kind == SourceKind::Http {
        let gate: Arc<dyn media_io::AddressGate> = if allow_local {
            Arc::new(AllowAllGate)
        } else {
            Arc::new(PublicAddressGate)
        };
        match HttpSource::open(url, IoLimits::default(), gate, px.io_cancel.clone()) {
            Ok(source) => Box::new(CountedSource {
                inner: source,
                diag: Arc::clone(diag),
            }),
            Err(e) => {
                px.fail(EngineError::io(e));
                return None;
            }
        }
    } else {
        if !is_a_local_path(url) && !allow_local {
            px.fail(EngineError::config(format!(
                "only relative, rooted and drive paths are opened unless local addresses are permitted: {url}"
            )));
            return None;
        }
        match FileSource::open(std::path::Path::new(url)) {
            Ok(source) => Box::new(CountedSource {
                inner: source,
                diag: Arc::clone(diag),
            }),
            Err(e) => {
                px.fail(EngineError::io(e));
                return None;
            }
        }
    };
    match media_demux::open_auto(source, DemuxLimits::default(), Generation(0)) {
        Ok(demuxer) => Some(demuxer),
        Err(e) => {
            px.fail(EngineError::demux(e));
            None
        }
    }
}

/// Common tail of the open: swap in the configured Bank, then spawn the
/// pipeline threads around the demuxer and the reconnect factory, if any.
fn finish_open(
    px: Arc<PipelineShared>,
    demuxer: Box<dyn media_demux::Demuxer>,
    bank_cfg: BankConfig,
    reconnect_factory: Option<pipeline::DemuxFactory>,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    finish_open_split(px, demuxer, None, bank_cfg, reconnect_factory, threads);
}

/// As [`finish_open`], with an optional second demuxer carrying audio only
/// (`OpenRequest::audio_url`). Both feed the one Bank, so the buffering
/// model, the clock and the release schedule are the session's, not each
/// leg's.
fn finish_open_split(
    px: Arc<PipelineShared>,
    mut demuxer: Box<dyn media_demux::Demuxer>,
    mut audio_leg: Option<Box<dyn media_demux::Demuxer>>,
    bank_cfg: BankConfig,
    reconnect_factory: Option<pipeline::DemuxFactory>,
    threads: Arc<Mutex<Vec<JoinHandle<()>>>>,
) {
    let leg_notes = audio_leg
        .as_mut()
        .map(|leg| leg.take_notes())
        .unwrap_or_default();
    for note in demuxer.take_notes().into_iter().chain(leg_notes) {
        px.diag.event(
            px.wall.now(),
            EventCode::CapabilityProbe,
            Stage::Demux,
            note,
        );
    }
    // Kept as reasons too: if what the demuxer did keep is refused as
    // well, the failure names everything the source was refused for.
    let mut refusals = demuxer.take_refusals();
    if let Some(leg) = audio_leg.as_mut() {
        refusals.extend(leg.take_refusals());
    }
    for refusal in refusals {
        px.playable.reason(&refusal);
        px.diag.event(
            px.wall.now(),
            EventCode::CodecRefused,
            Stage::Demux,
            refusal,
        );
    }
    // What a picker can offer instead of the bound track. Empty unless the
    // container carries more than one audio track.
    *px.audio_tracks.lock().expect("audio tracks lock") = demuxer.audio_tracks();
    *px.artwork.lock().expect("artwork lock") = demuxer.artwork().cloned();
    let audio_start = audio_leg
        .as_ref()
        .map_or_else(|| demuxer.audio_start(), |leg| leg.audio_start());
    px.audio_start_us
        .store(audio_start.as_micros(), Ordering::Relaxed);
    let end_us = |end: Option<MediaTime>| end.map_or(i64::MAX, MediaTime::as_micros);
    px.video_end_us
        .store(end_us(demuxer.video_end()), Ordering::Relaxed);
    let audio_end = audio_leg
        .as_ref()
        .map_or_else(|| demuxer.audio_end(), |leg| leg.audio_end());
    px.audio_end_us.store(end_us(audio_end), Ordering::Relaxed);
    if let Some(duration) = demuxer.duration() {
        px.shared
            .duration_us
            .store(duration.as_micros(), Ordering::Relaxed);
    }

    // The real Bank for this session's config. An unsatisfiable config is
    // a reported error, never a clamp. The liveness flag is published
    // before the state leaves Opening and `set_state` releases it, so a
    // caller that observes Buffering observes this store.
    px.live
        .store(bank_cfg.liveness == Liveness::Live, Ordering::Release);
    match Bank::new(bank_cfg, Generation(0)) {
        Ok(bank) => *px.bank.bank.lock().expect("bank lock") = bank,
        Err(e) => {
            px.fail(EngineError::config(e.to_string()));
            return;
        }
    }

    // Auto-play, but the clock stays parked until the first frame is
    // ready: starting it at open would turn the startup wait into instant
    // lateness. The decode threads start it.
    px.set_state(State::Buffering);

    let (video_tx, video_rx) = sync_channel(VIDEO_CHANNEL_DEPTH);
    let (audio_tx, audio_rx) = sync_channel(AUDIO_CHANNEL_DEPTH);

    // Set here, where the second thread is actually spawned, so nothing
    // ever waits on a leg that never runs.
    let leg = if audio_leg.is_some() {
        let _ = px.split.set(pipeline::SplitLegs::new());
        pipeline::Leg::Video
    } else {
        pipeline::Leg::Single
    };

    let mut handles = Vec::new();
    {
        let px = Arc::clone(&px);
        let video_tx = video_tx.clone();
        let audio_tx = audio_tx.clone();
        handles.push(
            std::thread::Builder::new()
                .name("bm-demux".into())
                .spawn(move || {
                    pipeline::run_demux_leg(
                        &px,
                        demuxer,
                        &video_tx,
                        &audio_tx,
                        reconnect_factory,
                        leg,
                    );
                })
                .expect("spawn demux thread"),
        );
    }
    if let Some(audio_leg) = audio_leg {
        let px = Arc::clone(&px);
        let video_tx = video_tx.clone();
        let audio_tx = audio_tx.clone();
        handles.push(
            std::thread::Builder::new()
                .name("bm-demux-audio".into())
                .spawn(move || {
                    pipeline::run_demux_leg(
                        &px,
                        audio_leg,
                        &video_tx,
                        &audio_tx,
                        None,
                        pipeline::Leg::Audio,
                    );
                })
                .expect("spawn audio demux thread"),
        );
    }
    {
        let px = Arc::clone(&px);
        handles.push(
            std::thread::Builder::new()
                .name("bm-release".into())
                .spawn(move || pipeline::run_release(&px, &video_tx, &audio_tx))
                .expect("spawn release thread"),
        );
    }
    {
        let px = Arc::clone(&px);
        handles.push(
            std::thread::Builder::new()
                .name("bm-video".into())
                .spawn(move || pipeline::run_video(&px, &video_rx))
                .expect("spawn video thread"),
        );
    }
    {
        let px = Arc::clone(&px);
        handles.push(
            std::thread::Builder::new()
                .name("bm-audio".into())
                .spawn(move || pipeline::run_audio(&px, &audio_rx))
                .expect("spawn audio thread"),
        );
    }
    threads
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .extend(handles);
}

#[cfg(test)]
mod classify_tests {
    use super::{SourceKind, classify, is_a_local_path};

    fn kind(url: &str) -> SourceKind {
        classify(url)
            .unwrap_or_else(|e| panic!("{url:?} refused: {}", e.detail))
            .0
    }

    fn spelling(url: &str) -> String {
        classify(url).expect("classified").1
    }

    #[test]
    fn schemes_classify_case_insensitively() {
        for (url, want) in [
            ("http://h/x", SourceKind::Http),
            ("HTTPS://h/x", SourceKind::Http),
            ("HtTp://h/x", SourceKind::Http),
            ("rtsp://h/x", SourceKind::Rtsp),
            ("RTSPT://h/x", SourceKind::Rtsp),
            ("WHEP://h/x", SourceKind::Whep),
            ("RIST://h:1234", SourceKind::Rist),
        ] {
            assert_eq!(kind(url), want, "{url:?}");
        }
    }

    #[test]
    fn paths_are_files_and_unknown_schemes_are_refused() {
        for url in [
            r"C:\clips\x.mp4",
            "c:/clips/x.mp4",
            "/srv/clips/x.mp4",
            "clips/x.mp4",
            "x.mp4",
            r"\\host\share\x.mp4",
        ] {
            assert_eq!(kind(url), SourceKind::File, "{url:?}");
        }
        for url in [
            "ftp://h/x",
            "file:///x",
            "gopher://h/x",
            "data:text/plain,x",
        ] {
            assert!(
                classify(url).is_err(),
                "{url:?} must refuse, not open as a path"
            );
        }
    }

    /// The scheme is lowercased for the transports, which match it as
    /// text. Every other byte, case included, is left as written.
    #[test]
    fn only_the_scheme_is_normalised() {
        assert_eq!(spelling("RTSP://Host/Path?Q=V"), "rtsp://Host/Path?Q=V");
        assert_eq!(spelling("RTSPT://Host/Path"), "rtspt://Host/Path");
        assert_eq!(spelling("HTTPS://Host/Path"), "https://Host/Path");
        assert_eq!(spelling("https://Host/Path"), "https://Host/Path");
        // A path is not a URL and is never rewritten.
        assert_eq!(spelling(r"C:\Clips\X.mp4"), r"C:\Clips\X.mp4");
    }

    /// The parser skips leading control characters and spaces before it
    /// reads a scheme, so its scheme does not always begin at byte zero.
    /// Splicing on length alone would turn " http://h/x" into
    /// "httpp://h/x". Where the leading bytes are not that scheme, the
    /// input is passed through untouched.
    #[test]
    fn a_scheme_the_parser_found_past_the_start_is_not_spliced() {
        for url in [
            " http://h/x",
            "\thttp://h/x",
            "\nhttp://h/x",
            "\u{1}http://h/x",
            "  HTTP://h/x",
        ] {
            assert_eq!(spelling(url), url, "{url:?} was rewritten");
        }
    }

    /// A string that does not parse as a URL is a path, so the
    /// unknown-scheme refusal never sees it. That is why `open_and_run`
    /// overrides the kind whenever the caller brought the bytes: a label
    /// like `"case 4"` reads as a relative path here, and would otherwise
    /// let a caller-supplied playlist fetch from the working directory.
    #[test]
    fn a_label_that_does_not_parse_as_a_url_reads_as_a_path() {
        for label in ["case 4", "burst run", "the third one"] {
            assert!(
                classify(label).is_ok(),
                "{label:?} must not reach the scheme refusal"
            );
            assert_eq!(kind(label), SourceKind::File, "{label:?}");
        }
    }

    /// Paths that name this machine whatever the current directory. The
    /// long spelling is what a caller uses past MAX_PATH, so refusing it
    /// would refuse ordinary local content.
    #[test]
    fn plain_paths_are_local() {
        for url in [
            r"C:\clips\x.ts",
            "c:/clips/x.ts",
            "C:x.ts",
            "/srv/clips/x.ts",
            r"\clips\x.ts",
            "clips/x.ts",
            "x.ts",
            r"\\?\C:\clips\x.ts",
            r"\\?\c:\clips\x.ts",
            r"\\?\C:",
            // `??` as a name further in is only a folder.
            r"\clips\??\UNC\x.ts",
            r"\???\x.ts",
        ] {
            assert!(is_a_local_path(url), "{url:?}");
        }
    }

    /// Every spelling here reaches another host, or a device, on Windows.
    #[test]
    fn a_path_that_can_reach_a_host_is_not_local() {
        for url in [
            r"\\host\share\x.ts",
            "//host/share/x.ts",
            // Windows takes either separator in either position.
            r"\/host/share/x.ts",
            r"/\host\share\x.ts",
            r"\\?\UNC\host\share\x.ts",
            r"\\?\unc\host\share\x.ts",
            r"\\.\UNC\host\share\x.ts",
            // `\\.\` is normalised, so a forward slash is a separator.
            r"\\.\UNC/host/share/x.ts",
            // The NT spelling, handed through unnormalised.
            r"\??\UNC\host\share\x.ts",
            r"\??\unc\host\share\x.ts",
            "/??/UNC/host/share/x.ts",
            // The object namespace, straight to the UNC provider.
            r"\\?\GLOBALROOT\Device\Mup\host\share\x.ts",
            r"\\.\GLOBALROOT\Device\Mup\host\share\x.ts",
            r"\\?\GLOBALROOT\??\UNC\host\share\x.ts",
            // Normalised against the device root, `..` leaves the drive.
            r"\\.\C:\..\UNC\host\share\x.ts",
            r"//?/C:/../UNC/host/share/x.ts",
            // A verbatim drive path has a backslash after its colon, and a
            // drive is one letter.
            r"\\?\C:/x.ts",
            r"\\?\CC:\x.ts",
            // Other devices are not files to play.
            r"\\.\pipe\x",
            r"\\?\Volume{00000000-0000-0000-0000-000000000000}\x.ts",
            r"\\.\C:\clips\x.ts",
            // Non-ASCII after the prefix is judged by byte, never sliced.
            "\\\\?\\A𐀀\\x.ts",
            "\\\\?\\𐀀:\\x.ts",
        ] {
            assert!(
                url.starts_with('\\') || url.starts_with('/'),
                "the row's own input lost its leading separator: {url:?}"
            );
            assert!(!is_a_local_path(url), "{url:?}");
        }
    }
}

#[cfg(test)]
mod close_tests {
    use super::*;

    /// A whole-file source that stalls its first read, so a close issued
    /// straight after open is still inside the opener's run.
    struct StalledSource {
        data: Vec<u8>,
        stalled: bool,
    }

    impl ByteSource for StalledSource {
        fn size(&mut self) -> Result<Option<u64>, SourceError> {
            Ok(Some(self.data.len() as u64))
        }

        fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
            if !self.stalled {
                self.stalled = true;
                std::thread::sleep(std::time::Duration::from_millis(150));
            }
            // Bounded as u64 before narrowing: narrowing first would drop
            // the high bits of an offset past `usize` and read from
            // wherever the remainder landed.
            if offset >= self.data.len() as u64 {
                return Ok(0);
            }
            let offset = offset as usize;
            let n = buf.len().min(self.data.len() - offset);
            buf[..n].copy_from_slice(&self.data[offset..offset + n]);
            Ok(n)
        }
    }

    /// The opener registers the pipeline's threads part-way through its own
    /// run, so a close arriving mid-open finds only the opener in the list.
    /// The threads it spawned meanwhile are in the list once that join
    /// returns, and close must drain it again rather than leave them
    /// running.
    #[test]
    fn close_mid_open_joins_the_threads_the_opener_registered() {
        let path = concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/../fixtures/h264-aac-640x360-30fps.mp4"
        );
        let source = StalledSource {
            data: std::fs::read(path).expect("fixture readable"),
            stalled: false,
        };
        let mut session =
            Session::open_with_source(OpenRequest::new("test://stalled"), Box::new(source));
        session.close();

        assert!(
            session
                .threads
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .is_empty(),
            "close returned leaving threads the opener registered while it ran"
        );
    }
}
