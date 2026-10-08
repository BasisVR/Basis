//! Pipeline assembly: demux, release and decode threads around the Bank,
//! with media-clock as the one position source, condvars instead of
//! sleep-polls, and a generation per seek.
//!
//! Threads per session:
//!   demux    pulls the Demuxer, pushes into the Bank, executes seeks
//!   release  drains the Bank on the 1x schedule, routes AUs to decoders
//!   video    decode → FramePool
//!   audio    decode → priming drop → PcmRing
//! Unity's render thread selects the due frame and runs the conversion and
//! the copy into Unity's texture. Unity's audio thread only runs the
//! lock-free ring pull.

use std::sync::atomic::Ordering;
use std::sync::mpsc::{Receiver, RecvTimeoutError, SyncSender};
use std::sync::{Arc, Condvar, Mutex};
use std::time::{Duration, Instant};

use media_bank::{Bank, PushOutcome};
use media_clock::{Correction, Generation, Master, MediaClock, MediaTime};
use media_decode::{AudioDecoder, PcmChunk, SubmitOutcome, VideoDecoder, VideoFrame};
use media_demux::{Au, Demuxer, Format, StreamEvent};
use media_diag::{BankReadings, EventCode, SessionDiag, Stage, diag_err, diag_log, diag_warn};

use crate::audio::{
    AudioConsumer, AudioFormatInfo, AudioProducer, frames_before_end, frames_before_origin,
    install_audio_generation,
};
use crate::playable::{Playable, TrackKind};
use crate::pool::FramePool;
use crate::present::PresentShared;
use crate::route::{open_audio_decoder, open_video_decoder};
use crate::sink::VideoSink;
use crate::{EngineError, SessionShared, State};

/// How long a fully banked pipeline waits before rechecking for commands.
const IDLE_WAIT: Duration = Duration::from_millis(50);
/// Decode-thread receive granularity: bounds presentation-timing error.
const DECODE_TICK: Duration = Duration::from_millis(4);

/// A consumer pull within this window keeps audio as the clock master.
const AUDIO_LIVENESS: MediaTime = MediaTime::from_millis(500);

/// A video access unit this far behind the playing clock when the decoder
/// takes it cannot be shown in time, nor can anything that depends on it,
/// so video is discarded from there to the next keyframe. Access units
/// reach the decoder ahead of the clock by the release lead, so a late one
/// means the decoder has been too slow for long enough to spend that lead;
/// a brief stall that a healthy decoder recovers from never gets here.
/// Left alone, the backlog fills the decode channel, the gated track closes
/// the Bank to both tracks and audio starves. ExoPlayer discards source
/// video to the next keyframe at the same figure.
const LATE_VIDEO_SKIP: MediaTime = MediaTime::from_millis(500);
/// Decoded frames in a row, each too late to show and later than the one
/// before, that mark a decoder losing ground. See [`FallingBehind`].
const LATE_FRAMES_BEFORE_SKIP: u32 = 5;
/// A skip is reported at most this often.
const LATE_VIDEO_LOG_EVERY: Duration = Duration::from_secs(5);

/// Whether the decoder is losing ground, judged from what comes out of it.
///
/// The access-unit test above is slow to fire on a decoder only a little
/// short of its stream, which spends seconds of the release lead decoding
/// frames already too late to show. The output frames show it sooner. One
/// late frame means nothing, since a decoder recovering from a stall is
/// late while it catches up; a run of them, each later than the last,
/// means it will not catch up.
#[derive(Default)]
struct FallingBehind {
    last: Option<MediaTime>,
    run: u32,
}

impl FallingBehind {
    /// Note how far behind the clock a decoded frame came out. True once
    /// the run is long enough to act on.
    fn observe(&mut self, late: MediaTime) -> bool {
        let losing =
            late > crate::pool::MAX_PRESENT_LATE && self.last.is_none_or(|last| late > last);
        self.run = if losing { self.run + 1 } else { 0 };
        self.last = Some(late);
        self.run >= LATE_FRAMES_BEFORE_SKIP
    }

    fn clear(&mut self) {
        *self = Self::default();
    }
}

/// How far behind the clock `pts` is, measured only against a playing
/// clock of this generation. A parked clock is a pause or a seek landing,
/// where the span ahead of the floor is decoded late on purpose.
fn behind_the_clock(
    px: &PipelineShared,
    generation: Generation,
    pts: MediaTime,
) -> Option<MediaTime> {
    let clock = px.clock.lock().expect("clock lock");
    (clock.is_playing() && clock.generation() == generation).then(|| clock.now(px.wall.now()) - pts)
}

/// The longest a last frame is held past its pts. A duration or a frame gap
/// beyond it is a gap in the media, not the frame's own display time.
const LAST_FRAME_HOLD_MAX: MediaTime = MediaTime::from_secs(1);

/// The newest two frames a video thread has handed to the pool this
/// generation.
#[derive(Default)]
struct Published {
    last: Option<MediaTime>,
    previous: Option<MediaTime>,
}

impl Published {
    fn push(&mut self, pts: MediaTime) {
        self.previous = self.last;
        self.last = Some(pts);
    }
}

/// When a last frame at `pts` stops showing: at the duration where that
/// falls within the hold, else after the gap from the frame before it, as
/// FFmpeg guesses a final frame's length. With neither, at its pts.
fn last_frame_end(pts: MediaTime, previous: Option<MediaTime>, duration_us: i64) -> MediaTime {
    let duration = MediaTime::from_micros(duration_us);
    if duration > pts && duration - pts <= LAST_FRAME_HOLD_MAX {
        return duration;
    }
    match previous.map(|previous| pts - previous) {
        Some(gap) if gap > MediaTime::ZERO && gap <= LAST_FRAME_HOLD_MAX => pts + gap,
        _ => pts,
    }
}

/// The frame, unless the container stops presenting the track before it.
/// Checked ahead of the unseen span, so a seek past the end lands on the
/// last frame that is shown, not on one past it.
fn before_end(px: &PipelineShared, frame: VideoFrame) -> Option<VideoFrame> {
    (frame.pts_us() < px.video_end_us.load(Ordering::Relaxed)).then_some(frame)
}

fn report_late_video(
    px: &PipelineShared,
    reported: &mut Option<std::time::Instant>,
    behind: MediaTime,
) {
    if reported.is_none_or(|at| at.elapsed() >= LATE_VIDEO_LOG_EVERY) {
        *reported = Some(std::time::Instant::now());
        px.diag.event(
            px.wall.now(),
            EventCode::LateVideoSkip,
            Stage::Decode,
            format!("{behind} behind the clock, discarding to the next keyframe"),
        );
    }
}

/// `seek_floor_us` when the generation presents from wherever it starts.
pub(crate) const NO_FLOOR: i64 = i64::MIN;
/// The most frames a seek will decode forward from its keyframe to reach
/// the target exactly. Everything in that span is decoded before anything
/// shows, so this bounds how long a seek can sit in Buffering, and decode
/// time goes by frames, not media time: twelve seconds at 60 fps, minutes
/// of a still picture at 1 fps. Past it the seek presents from the
/// keyframe and logs that.
const ACCURATE_SEEK_FRAMES: i64 = 720;
/// The span a seek always decodes forward, and all it has before the video
/// track's frame rate is known.
const ACCURATE_SEEK_MIN: MediaTime = MediaTime::from_secs(12);
/// Audio kept ahead of the floor so the decoder's overlap state is warm by
/// the first sample that is heard. The ring trims it off again.
const SEEK_AUDIO_LEAD_IN: MediaTime = MediaTime::from_millis(100);
/// How long the demux thread waits on a full decode channel while it
/// feeds the span ahead of the floor.
const SEEK_FEED_WAIT: Duration = Duration::from_millis(2);

/// Where a generation starts presenting when that is not where its seek
/// landed: the target, if the demuxer stopped short of it by no more than
/// [`ACCURATE_SEEK_FRAMES`] of `frame_step` (the video track's typical
/// step between frames), and never less than [`ACCURATE_SEEK_MIN`].
fn presentation_floor(
    target: MediaTime,
    landed: MediaTime,
    frame_step: Option<MediaTime>,
) -> Option<MediaTime> {
    let bound = frame_step.map_or(ACCURATE_SEEK_MIN, |step| {
        MediaTime::from_micros(step.as_micros().saturating_mul(ACCURATE_SEEK_FRAMES))
            .max(ACCURATE_SEEK_MIN)
    });
    (landed < target && target - landed <= bound).then_some(target)
}

/// How many of the latest steps between video frames [`FrameStep`] keeps.
const FRAME_STEPS_KEPT: usize = 15;

/// The video track's typical step between consecutive decode timestamps,
/// which sizes how far a seek decodes forward: the median of the latest
/// steps, so an odd pair of frames close together does not set it. A new
/// video Format starts it again, since a replacement track or rendition can
/// run at another rate.
#[derive(Default)]
struct FrameStep {
    steps: [i64; FRAME_STEPS_KEPT],
    count: usize,
    next: usize,
    last_dts: Option<MediaTime>,
}

impl FrameStep {
    fn step(&self) -> Option<MediaTime> {
        if self.count == 0 {
            return None;
        }
        let mut kept = self.steps;
        let kept = &mut kept[..self.count];
        kept.sort_unstable();
        Some(MediaTime::from_micros(kept[self.count / 2]))
    }

    fn observe(&mut self, dts: MediaTime) {
        if let Some(last) = self.last_dts
            && dts > last
        {
            self.steps[self.next] = dts.saturating_sub(last).as_micros();
            self.next = (self.next + 1) % FRAME_STEPS_KEPT;
            self.count = (self.count + 1).min(FRAME_STEPS_KEPT);
        }
        self.last_dts = Some(dts);
    }

    /// A seek's jump is not a step between frames.
    fn seeked(&mut self) {
        self.last_dts = None;
    }

    fn new_format(&mut self) {
        *self = Self::default();
    }
}

/// The frames a seek decodes to build its target's picture. None of them
/// is shown. Output is in display order, so the first frame to reach the
/// floor ends the span.
#[derive(Default)]
struct UnseenSpan {
    before_us: Option<i64>,
    dropped: u32,
    /// The last frame dropped, kept in case the stream ends inside the
    /// span: a target past the final picture has nothing at or after it
    /// to show, and that frame is then the one to land on.
    held: Option<VideoFrame>,
}

/// What [`UnseenSpan::admit`] makes of one decoded frame.
#[derive(Debug, PartialEq, Eq)]
enum Admit {
    /// Ahead of the floor: decoded, never shown.
    Unseen,
    /// The first frame at or past the floor, and how many were dropped to
    /// get to it.
    Reached { dropped: u32 },
    /// No span is open.
    Show,
}

impl UnseenSpan {
    /// Open the span a Flush brings with it, or none.
    fn arm(&mut self, floor_us: i64) {
        self.before_us = (floor_us != NO_FLOOR).then_some(floor_us);
        self.dropped = 0;
        self.held = None;
    }

    fn admit(&mut self, pts_us: i64) -> Admit {
        match self.before_us {
            None => Admit::Show,
            Some(at) if pts_us < at => {
                self.dropped += 1;
                Admit::Unseen
            }
            Some(_) => {
                self.before_us = None;
                Admit::Reached {
                    dropped: self.dropped,
                }
            }
        }
    }

    /// The frame, unless it belongs to the span. Every site that takes a
    /// frame from the decoder goes through here, including the
    /// end-of-stream drain: a seek landing close to the end reaches Eos
    /// with frames ahead of the floor still inside the decoder.
    fn filter(&mut self, px: &PipelineShared, frame: VideoFrame) -> Option<VideoFrame> {
        match self.admit(frame.pts_us()) {
            Admit::Unseen => {
                self.held = Some(frame);
                None
            }
            Admit::Show => Some(frame),
            Admit::Reached { dropped } => {
                self.held = None;
                px.diag.event(
                    px.wall.now(),
                    EventCode::Seek,
                    Stage::Decode,
                    format!(
                        "reached the target at {}us after {dropped} frames decoded unseen",
                        frame.pts_us()
                    ),
                );
                Some(frame)
            }
        }
    }

    /// The decoder has drained dry, so a span still open will never be
    /// reached: the target lies past the last picture (at the very end of
    /// a clip, or wherever audio outlasts video). Nothing would reach the
    /// pool, the parked clock would never start and the session would sit
    /// in Buffering, so the span closes on the last frame it dropped.
    fn give_up(&mut self, px: &PipelineShared) -> Option<VideoFrame> {
        let at = self.before_us.take()?;
        let frame = self.held.take()?;
        px.diag.event(
            px.wall.now(),
            EventCode::Seek,
            Stage::Decode,
            format!(
                "the stream ended ahead of the target at {at}us; landing on its last frame at {}us",
                frame.pts_us()
            ),
        );
        Some(frame)
    }
}

/// Presented video pts minus the audio playhead, or the unknown sentinel.
///
/// Both terms must belong to the same generation, hence
/// `presented_this_generation` rather than a cumulative presented count. A
/// stale video position against a fresh audio playhead gives a
/// plausible-looking number, and in a capture column that is worse than a
/// gap.
fn av_offset_us(
    playhead: Option<MediaTime>,
    presented_this_generation: bool,
    presented_pts_us: i64,
) -> i32 {
    match playhead {
        Some(ph) if presented_this_generation && presented_pts_us != i64::MIN => {
            (presented_pts_us - ph.as_micros()).clamp(i64::from(i32::MIN) + 1, i64::from(i32::MAX))
                as i32
        }
        _ => i32::MIN,
    }
}

/// No timeline has presented yet. Generations count up from zero, so this
/// can never collide with a real one.
pub(crate) const NO_GENERATION: u64 = u64::MAX;

/// Record that `generation` has presented a frame, for the A/V offset's
/// gate. The clock's start cannot answer that: when audio starts the clock,
/// the video thread never reaches its clock-start branch.
///
/// The value written names the timeline that presented. A render event
/// that raced a seek writes a retired generation, which
/// [`presented_this_generation`] does not match, and nothing needs clearing
/// at a flush.
pub(crate) fn note_presented(px: &PipelineShared, generation: u64) {
    px.presented_generation.store(generation, Ordering::Relaxed);
}

/// Whether the timeline in force has presented a frame: the A/V offset's
/// gate. Split out so a test can assert it without a `PipelineShared`.
fn presented_this_generation(presented: u64, current: u64) -> bool {
    presented == current
}

/// Whether a presentation ends Buffering: only the timeline in force
/// counts, not while a seek is queued or still in the demuxer (until it
/// returns, the timeline in force is the one being left), and not while a
/// pause is waiting to complete on it. Split out as above.
fn buffering_ends(presented: u64, current: u64, seeks_pending: u32, pause_wanted: bool) -> bool {
    presented == current && seeks_pending == 0 && !pause_wanted
}

/// Whether the audio pull may serve the ring: in Playing, or in Buffering
/// once the clock runs (a start anchors the clock an output latency before
/// the first picture, and sound pulled from then is heard as it shows).
/// Never while the clock is parked, and only for the timeline in force: not
/// with a seek queued (the clock still runs on the timeline being left),
/// not before the ring is this timeline's, and not with a pause waiting on
/// the landing. Split out as above.
pub(crate) fn audio_serves(
    state: u32,
    clock_playing: bool,
    pause_wanted: bool,
    seeks_pending: u32,
    ring_generation: u64,
    current: u64,
) -> bool {
    clock_playing
        && (state == State::Playing as u32 || state == State::Buffering as u32)
        && !pause_wanted
        && seeks_pending == 0
        && ring_generation == current
}

/// Whether a thread that reached the end of the `ended` timeline may end
/// the session: not while a seek is in flight, and not once the timeline
/// has moved on. Split out as above.
fn end_is_current(seeks_pending: u32, current: u64, ended: u64) -> bool {
    seeks_pending == 0 && current == ended
}

/// The one read-modify-write behind [`PipelineShared::claim_state`], split
/// out so a row can publish the competing state first.
fn claim(state: &std::sync::atomic::AtomicU32, from: &[State], to: State) -> bool {
    state
        .fetch_update(Ordering::AcqRel, Ordering::Acquire, |s| {
            from.iter().any(|f| *f as u32 == s).then_some(to as u32)
        })
        .is_ok()
}

/// Whether the audio consumer is still pulling, as of `wall`.
///
/// `i64::MIN` is `last_pull_wall_us`'s never-pulled sentinel and would
/// overflow the subtraction, so it is checked first. A consumer that never
/// arrived is not live.
fn consumer_live(wall: MediaTime, last_pull_us: i64) -> bool {
    last_pull_us != i64::MIN && wall - MediaTime::from_micros(last_pull_us) <= AUDIO_LIVENESS
}

/// The engine wall clock: monotonic µs that freeze while paused, so the
/// Bank's release schedule and the media clock pause together without
/// either component knowing about pause.
pub struct EngineWall {
    origin: Instant,
    state: Mutex<WallState>,
}

struct WallState {
    paused_accum: Duration,
    paused_since: Option<Instant>,
}

impl EngineWall {
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
            state: Mutex::new(WallState {
                paused_accum: Duration::ZERO,
                paused_since: None,
            }),
        }
    }

    pub fn now(&self) -> MediaTime {
        let state = self.state.lock().expect("wall lock");
        let paused = state.paused_accum
            + state
                .paused_since
                .map(|since| since.elapsed())
                .unwrap_or(Duration::ZERO);
        MediaTime::from_micros((self.origin.elapsed() - paused).as_micros() as i64)
    }

    pub fn pause(&self) {
        let mut state = self.state.lock().expect("wall lock");
        if state.paused_since.is_none() {
            state.paused_since = Some(Instant::now());
        }
    }

    pub fn resume(&self) {
        let mut state = self.state.lock().expect("wall lock");
        if let Some(since) = state.paused_since.take() {
            state.paused_accum += since.elapsed();
        }
    }
}

/// The Bank and its condvar: pushed by demux, drained by release, both
/// signal.
pub struct BankShared {
    pub bank: Mutex<Bank>,
    pub changed: Condvar,
}

pub enum Command {
    Seek(MediaTime),
}

/// Everything the pipeline threads share.
pub struct PipelineShared {
    pub shared: Arc<SessionShared>,
    pub diag: Arc<SessionDiag>,
    pub wall: Arc<EngineWall>,
    pub clock: Arc<Mutex<MediaClock>>,
    pub bank: Arc<BankShared>,
    pub pool: Arc<FramePool>,
    pub commands: Mutex<Vec<Command>>,
    pub audio_consumer: Mutex<Option<AudioConsumer>>,
    pub audio_shared: Arc<crate::audio::AudioShared>,
    /// Cancels in-flight connects and reads on teardown.
    pub io_cancel: media_io::CancelToken,
    /// Track presence, set by the release thread as Formats route. Decides
    /// which decode thread declares Ended.
    pub video_active: std::sync::atomic::AtomicBool,
    pub audio_active: std::sync::atomic::AtomicBool,
    /// Whether anything is left to play once tracks are refused.
    pub playable: Playable,
    /// The generation the audio thread has nothing further to play out for:
    /// its post-EOS drain finished and the ring is empty, or the consumer
    /// stopped pulling, or no decoder was ever built for the track. The video
    /// thread waits on it before declaring Ended, so a session with both
    /// kinds of track does not end on the last *picture* while the ring still
    /// holds sound.
    ///
    /// It carries the generation, not a bare flag, because the two decode
    /// threads observe a seek independently and the audio thread can publish
    /// for the generation it is leaving after the advance. A stale generation
    /// simply fails to match. `u64::MAX` = nothing published.
    pub audio_tail_out: std::sync::atomic::AtomicU64,
    /// Lock-free mirror of whether the clock is playing, for the audio pull
    /// path: the ring serves silence while the clock is parked (startup,
    /// seeks), so post-seek audio never plays against a parked clock.
    /// Written beside every `set_playing` call. The `State` gate alone is
    /// not enough, since a present in flight can race a seek back to
    /// Playing.
    pub clock_playing: std::sync::atomic::AtomicBool,
    /// The generation whose ring the audio consumer slot holds, or
    /// [`NO_GENERATION`]. Stored after each install, so a pull that reads
    /// the current generation here finds that generation's ring.
    pub audio_ring_generation: std::sync::atomic::AtomicU64,
    /// Decode-route preference from the descriptor, read by the video
    /// thread's route resolution.
    pub decode_preference: crate::DecodePreference,
    /// The generation that has presented a frame, or [`NO_GENERATION`].
    /// Read only by the A/V offset's gate. Holding the generation means a
    /// write from a retired timeline cannot be mistaken for the current
    /// one's.
    pub presented_generation: std::sync::atomic::AtomicU64,
    /// The pts of the frame last presented, µs: what is on screen, as
    /// distinct from the session position, which is the clock's. The A/V
    /// offset is this against the audio playhead.
    pub presented_pts_us: std::sync::atomic::AtomicI64,
    /// The generation whose frame last reached the output, or
    /// [`NO_GENERATION`]. Unlike `presented_generation` it is never armed
    /// by a clock start, so it answers "is the landed picture on screen".
    pub shown_generation: std::sync::atomic::AtomicU64,
    /// Serialises the transport requests (play, pause, seek) against the
    /// decode threads completing a pause or an end, and the audio thread's
    /// position update against the end. Never taken on the render thread.
    pub transport: Mutex<()>,
    /// Held by the demux thread while a seek advances the generation and
    /// publishes Buffering, and tried (never waited on) by a presentation
    /// leaving Buffering, so the generation it checks is the one its
    /// Playing lands on. A presentation that finds it held leaves the
    /// state alone; the next one retries.
    pub timeline: Mutex<()>,
    /// A pause is asked for and `play` has not withdrawn it. It outlives a
    /// seek: the seek runs unpaused so the pipeline can land it, and the
    /// pause completes once the landed position is showing.
    pub pause_wanted: std::sync::atomic::AtomicBool,
    /// Seeks requested and not yet executed by the demux thread. A pause
    /// completes only at zero: freezing the wall under a seek in flight
    /// would land it against a stopped release schedule.
    pub seeks_pending: std::sync::atomic::AtomicU32,
    /// Where the current generation starts presenting, or [`NO_FLOOR`]. A
    /// demuxer lands a seek on the keyframe at or before the target. When
    /// that is early the target is the floor, and everything decoded ahead
    /// of it only builds the target's picture and is never shown or heard.
    /// Written by the demux thread after it advances the generation and
    /// before the Flush goes out, so a decode thread only reads it for the
    /// generation it has adopted.
    pub seek_floor_us: std::sync::atomic::AtomicI64,
    /// Where the audio source's sound begins ([`Demuxer::audio_start`]).
    /// No generation's audio starts before it. Written once at open, before
    /// any pipeline thread is spawned.
    pub audio_start_us: std::sync::atomic::AtomicI64,
    /// Where the video source's pictures end ([`Demuxer::video_end`]), or
    /// `i64::MAX`. A decoded picture at or after it is not shown. Written
    /// once at open, before any pipeline thread is spawned.
    pub video_end_us: std::sync::atomic::AtomicI64,
    /// Where the audio source's sound ends ([`Demuxer::audio_end`]), or
    /// `i64::MAX`. Sound at or after it is not played. Written once at open.
    pub audio_end_us: std::sync::atomic::AtomicI64,
    /// Where the current generation's timeline starts: zero at open, and
    /// after a seek where the clock was snapped to. Written with
    /// `seek_floor_us`.
    pub generation_start_us: std::sync::atomic::AtomicI64,
    /// Access units the demux thread has sent straight to the video
    /// decoder for the span ahead of the floor, and how many of this
    /// generation's the video thread has taken off its channel. The channel
    /// holds a whole span, so feeding finishes long before decoding does,
    /// and the Bank's release schedule starts at its first push. Pushing
    /// then would release audio for as long as the decode takes, into a
    /// two-second ring with no consumer until the seek lands, so the first
    /// push waits for the two counts to meet.
    pub seek_fed: std::sync::atomic::AtomicU64,
    pub seek_taken: std::sync::atomic::AtomicU64,
    /// Caption cues scanned from the video AUs' SEI on the demux thread,
    /// surfaced on arrival with their due PTS. Captions bypass the Bank's
    /// release schedule so the consumer gets the full pre-roll.
    /// Drop-oldest at [`CAPTION_RING`].
    pub captions: Mutex<std::collections::VecDeque<media_bitstream::CaptionCue>>,
    /// SEI user data (type 5) scanned from the video AUs on the demux
    /// thread, surfaced on arrival with its PTS and left unparsed. Same
    /// path as captions.
    pub user_data: Mutex<UserDataRing>,
    /// Audio tracks the container offers instead of the bound one, filled
    /// once the demuxer is open. Empty where there is no choice to make.
    /// Nothing on a hot path touches it.
    pub audio_tracks: Mutex<Vec<media_demux::AudioTrackInfo>>,
    /// Cover art the container carried, read once at open. A property of
    /// the file rather than the stream, like the duration.
    pub artwork: Mutex<Option<media_demux::Artwork>>,
    /// Render-event selection state: the render thread's clock mirror, its
    /// vsync estimate, and the liveness stamp that hands frame selection
    /// between it and the video thread.
    pub present: PresentShared,
    /// Shared-playback soft sync target: the last reported owner
    /// position, extrapolated at 1x between reports.
    pub(crate) sync: crate::sync::SyncShared,
    /// The sync ladder's wanted rate offset from 1x, ppm. With an audio
    /// master the managed audio pull applies it through its resampler (the
    /// snapshot surfaces it). With a wall master the engine has already
    /// applied it to the clock and this mirrors what it did.
    pub sync_rate_ppm: std::sync::atomic::AtomicI64,
    /// Bank liveness, mirrored lock-free once the opener installs the
    /// session's real Bank (a playlist can override the request's stated
    /// liveness). Live sessions ignore sync targets.
    pub live: std::sync::atomic::AtomicBool,
    /// Split-source coordination (`OpenRequest::audio_url`). Absent on a
    /// one-source session, and every split-only branch requires it.
    pub split: std::sync::OnceLock<SplitLegs>,
    /// The Windows shared-texture presenter, shared between the render
    /// event (selection and conversion at display cadence) and the video
    /// thread (configure on Format; fallback presents while no render
    /// consumer is live). Neither holder does GPU-external work under other
    /// media-path locks, and the render event only try-locks it.
    #[cfg(windows)]
    pub presenter: Mutex<Option<media_present::SharedTexturePresenter>>,
}

/// Caption cue ring depth.
const CAPTION_RING: usize = 64;
/// A per-frame stream sends one message per AU, and an on-demand open
/// banks up to the Bank's 30 s time cap before the consumer's first drain:
/// 900 messages at 30 fps. The ring must outlast that burst or a recording
/// loses its opening seconds of data. Live delivery is paced and never
/// comes near it.
const USER_DATA_RING: usize = 1024;
/// Memory bound on the ring as a whole, drop-oldest like the count.
const USER_DATA_RING_BYTES: usize = 16 * 1024 * 1024;
/// Per-message ceiling. The largest real payload measured is ~10 KiB. A
/// larger message is refused at the ring so a stream cannot size it.
pub const USER_DATA_PAYLOAD_CAP: usize = 64 * 1024;

/// Pending SEI user-data messages, oldest first, with the payload total
/// kept alongside so both bounds are O(1) to hold.
#[derive(Default)]
pub struct UserDataRing {
    items: std::collections::VecDeque<media_bitstream::SeiUserData>,
    bytes: usize,
}

impl UserDataRing {
    fn push(&mut self, message: media_bitstream::SeiUserData) {
        if message.payload.len() > USER_DATA_PAYLOAD_CAP {
            return;
        }
        self.bytes += message.payload.len();
        self.items.push_back(message);
        while self.items.len() > USER_DATA_RING || self.bytes > USER_DATA_RING_BYTES {
            self.pop_front();
        }
    }

    fn pop_front(&mut self) -> Option<media_bitstream::SeiUserData> {
        let m = self.items.pop_front()?;
        self.bytes -= m.payload.len();
        Some(m)
    }

    pub fn clear(&mut self) {
        self.items.clear();
        self.bytes = 0;
    }

    /// Take up to `max` messages whose payloads total no more than
    /// `max_bytes`. A head message that could never fit on its own is
    /// dropped rather than left blocking everything behind it.
    pub fn drain(&mut self, max: usize, max_bytes: usize) -> Vec<media_bitstream::SeiUserData> {
        let mut out = Vec::new();
        let mut bytes = 0usize;
        while out.len() < max {
            let Some(head) = self.items.front() else {
                break;
            };
            let len = head.payload.len();
            if len > max_bytes {
                self.pop_front();
                continue;
            }
            if bytes + len > max_bytes {
                break;
            }
            bytes += len;
            out.push(self.pop_front().expect("front was present"));
        }
        out
    }

    pub fn len(&self) -> usize {
        self.items.len()
    }

    pub fn is_empty(&self) -> bool {
        self.items.is_empty()
    }
}

/// Which source a demux thread is reading, in a session that has more than
/// one. Adaptive ladders serve high rungs as a video-only and an audio-only
/// stream that must be played together. Both are cuts of the same content,
/// so a single Bank meters them against one timeline.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Leg {
    /// The only source. Carries whatever tracks it carries.
    Single,
    /// The video leg of a split pair. Owns seek: it advances the
    /// generation, snaps the clock and flushes the decode threads, as the
    /// single-source path does.
    Video,
    /// The audio leg of a split pair. Follows the video leg's seeks rather
    /// than taking commands of its own.
    Audio,
}

impl Leg {
    /// Whether this leg's tracks of the other kind are dropped before the
    /// Bank sees them. A video leg from an adaptive ladder is video-only in
    /// practice, but a caller can pass anything.
    fn wants(self, format: &Format) -> bool {
        match self {
            Self::Single => true,
            Self::Video => matches!(format, Format::Video { .. }),
            Self::Audio => matches!(format, Format::Audio { .. }),
        }
    }
}

/// Where the audio leg's track ids start. Each leg numbers its tracks from
/// its own base, so the two cannot meet in the Bank whatever the containers
/// number them.
const AUDIO_LEG_TRACK_BIT: u32 = 0x8000_0000;

/// A split leg's view of its demuxer's track ids.
#[derive(Default)]
struct LegTracks {
    /// Tracks of the kind this leg does not carry, learned from its own
    /// Format events, so their AUs can be dropped before the Bank sees
    /// them.
    foreign: std::collections::HashSet<media_demux::TrackId>,
    /// The id each carried track reaches the Bank under, numbered in the
    /// order first seen. A container may number a track anywhere in the
    /// u32 range, so no arithmetic on its own id keeps two apart.
    ids: std::collections::HashMap<media_demux::TrackId, media_demux::TrackId>,
}

impl LegTracks {
    /// The id `track` reaches the Bank under, or `None` once this leg has
    /// used up its half of the id range.
    fn remap(&mut self, leg: Leg, track: media_demux::TrackId) -> Option<media_demux::TrackId> {
        if let Some(id) = self.ids.get(&track) {
            return Some(*id);
        }
        let next = u32::try_from(self.ids.len())
            .ok()
            .filter(|n| *n < AUDIO_LEG_TRACK_BIT)?;
        let base = if leg == Leg::Audio {
            AUDIO_LEG_TRACK_BIT
        } else {
            0
        };
        let id = media_demux::TrackId(base | next);
        self.ids.insert(track, id);
        Some(id)
    }
}

/// What the two demux threads of a split session share.
pub struct SplitLegs {
    /// The seek the video leg last performed: its generation and where it
    /// landed. The audio leg watches this and follows.
    pub seek: Mutex<Option<(Generation, MediaTime)>>,
    /// Whether each leg has reached the end of its own source, video
    /// first. The session's Eos reaches the Bank only once both have, so a
    /// leg that runs a little short cannot end the other one early.
    pub eos: [std::sync::atomic::AtomicBool; 2],
    /// Set by whichever leg carried the Eos through, so two legs finishing
    /// together still bank exactly one.
    eos_carried: std::sync::atomic::AtomicBool,
    /// The dts each leg has most recently banked, video first, or `UNSET`
    /// before its first. Two producers share one bounded Bank, so without
    /// this the faster-reading leg (a small audio file against a large
    /// video one) fills it alone and locks the other out. That deadlocks:
    /// the clock will not start until the video leg lands a frame, release
    /// will not drain until the clock starts, and the Bank will not take the
    /// video leg's frame until release drains.
    banked_dts_us: [std::sync::atomic::AtomicI64; 2],
    /// Where each leg is measured from, video first, or `UNSET` until it
    /// offers its first dts. Container timelines start anywhere (an
    /// arbitrary 33-bit clock on MPEG-TS, a `baseMediaDecodeTime` on fMP4,
    /// the first cluster timestamp on Matroska), and the two legs need not
    /// agree on where zero is or where a landed seek sits. The cap meters how
    /// far each leg has come from its own baseline. A seek moves both
    /// baselines: each leg clears its own as its demuxer moves.
    origin_us: [std::sync::atomic::AtomicI64; 2],
}

/// `banked_dts_us` / `origin_us` before a leg has banked or seen anything.
const UNSET: i64 = i64::MIN;

/// How far ahead of the other leg a leg may bank before it waits.
///
/// Deliberately tight. On an on-demand source the Bank releases its queue
/// in arrival order on a dts-derived schedule: an event that arrives early
/// but is due late sits at the head and holds up everything behind it,
/// including the other leg's frames that are due now. Keeping the legs
/// within a fraction of a second keeps arrival order close to timeline
/// order and bounds that wait well inside the decoder's cushion. It must
/// also stay well under the Bank's read-ahead depth, or one leg fills the
/// Bank before the cap applies and the other cannot get in.
const SPLIT_LEAD_CAP_US: i64 = 100_000;

impl SplitLegs {
    pub fn new() -> Self {
        Self {
            seek: Mutex::new(None),
            eos: [
                std::sync::atomic::AtomicBool::new(false),
                std::sync::atomic::AtomicBool::new(false),
            ],
            eos_carried: std::sync::atomic::AtomicBool::new(false),
            banked_dts_us: [
                std::sync::atomic::AtomicI64::new(UNSET),
                std::sync::atomic::AtomicI64::new(UNSET),
            ],
            origin_us: [
                std::sync::atomic::AtomicI64::new(UNSET),
                std::sync::atomic::AtomicI64::new(UNSET),
            ],
        }
    }

    /// Latch this leg's baseline from the first dts it offers after a
    /// start or a seek. Only that leg's own demux thread writes its slot.
    fn note_origin(&self, leg: Leg, dts_us: i64) {
        let slot = &self.origin_us[Self::index(leg)];
        if slot.load(Ordering::Relaxed) == UNSET && dts_us != UNSET {
            slot.store(dts_us, Ordering::Relaxed);
        }
    }

    /// Whether this leg has to wait for the other one to catch up before
    /// banking `dts`. A leg that has reached the end of its source never
    /// holds the other back, since it has nothing left to catch up with.
    ///
    /// The distance is between how far each leg has come from its own
    /// origin, never between absolute dts. A leg that has banked nothing
    /// has come no distance, so a timeline origin above the cap cannot read
    /// as both legs being ahead of each other.
    fn must_wait_for_other(&self, leg: Leg, dts_us: i64) -> bool {
        let me = Self::index(leg);
        let other = 1 - me;
        if self.eos[other].load(Ordering::Relaxed) {
            return false;
        }
        let my_origin = self.origin_us[me].load(Ordering::Relaxed);
        if my_origin == UNSET {
            return false;
        }
        let their_origin = self.origin_us[other].load(Ordering::Relaxed);
        let their_banked = self.banked_dts_us[other].load(Ordering::Relaxed);
        let their_progress = if their_origin == UNSET || their_banked == UNSET {
            0
        } else {
            their_banked.saturating_sub(their_origin)
        };
        // Saturating: both terms are container dts, scaled by a timescale
        // the container also states, so their distance is unbounded. A wrap
        // would invert the decision and hold a leg out of the Bank for good.
        dts_us
            .saturating_sub(my_origin)
            .saturating_sub(their_progress)
            > SPLIT_LEAD_CAP_US
    }

    /// How far apart the two legs measure their timelines from, once
    /// both have latched a baseline. `None` until then.
    ///
    /// Saturating for the same reason as the lead cap: both terms are
    /// container dts, so their difference is unbounded.
    fn origin_gap_us(&self) -> Option<i64> {
        let video = self.origin_us[0].load(Ordering::Relaxed);
        let audio = self.origin_us[1].load(Ordering::Relaxed);
        if video == UNSET || audio == UNSET {
            return None;
        }
        Some(video.saturating_sub(audio).saturating_abs())
    }

    fn note_banked(&self, leg: Leg, dts_us: i64) {
        self.banked_dts_us[Self::index(leg)].store(dts_us, Ordering::Relaxed);
    }

    /// Neither leg has come any distance from a seek it has only just
    /// landed, so what either banked on the old timeline says nothing
    /// about the distance between them now.
    fn rebase(&self) {
        for slot in &self.banked_dts_us {
            slot.store(UNSET, Ordering::Relaxed);
        }
    }

    /// Forget this leg's baseline so its first dts after a seek
    /// re-establishes it. Each leg calls this for itself once its own
    /// demuxer has moved. The landed position is absolute and the legs need
    /// not agree on where it falls on their own timelines, so it cannot be
    /// handed to either as a baseline. Doing it on the leg's own thread also
    /// stops a pre-seek AU still in hand from latching the new baseline at
    /// the position the leg is leaving.
    fn reset_origin(&self, leg: Leg) {
        let slot = Self::index(leg);
        self.origin_us[slot].store(UNSET, Ordering::Relaxed);
        // What this leg banked was measured from the baseline it is
        // forgetting, so it goes too. The video leg rebases both slots as it
        // lands the seek, and the audio leg can bank one more pre-seek AU
        // before it observes that seek. The other leg's progress would then
        // span two timelines and hold it out of the Bank until the audio leg
        // banks again.
        self.banked_dts_us[slot].store(UNSET, Ordering::Relaxed);
    }

    fn index(leg: Leg) -> usize {
        match leg {
            Leg::Audio => 1,
            _ => 0,
        }
    }

    /// Records this leg as having reached the end of its own source.
    fn mark_eos(&self, leg: Leg) {
        self.eos[Self::index(leg)].store(true, Ordering::SeqCst);
    }

    fn both_reached_eos(&self) -> bool {
        self.eos.iter().all(|f| f.load(Ordering::SeqCst))
    }

    /// Claims the job of banking the session's Eos, for exactly one leg.
    fn claim_carrier(&self) -> bool {
        !self.eos_carried.swap(true, Ordering::SeqCst)
    }

    fn clear_eos(&self) {
        self.eos_carried.store(false, Ordering::SeqCst);
        for flag in &self.eos {
            flag.store(false, Ordering::SeqCst);
        }
    }
}

/// Namespaces each leg's track ids and drops the tracks a leg does not
/// carry, so two demuxers can feed one Bank (each numbers its own tracks,
/// and the Bank and release thread route on the id alone). Returns `None`
/// for an event this leg should not contribute.
fn adapt_leg_event(leg: Leg, tracks: &mut LegTracks, event: StreamEvent) -> Option<StreamEvent> {
    if leg == Leg::Single {
        return Some(event);
    }
    match event {
        StreamEvent::Format(track, format) => {
            if !leg.wants(&format) {
                tracks.foreign.insert(track);
                return None;
            }
            Some(StreamEvent::Format(tracks.remap(leg, track)?, format))
        }
        StreamEvent::Au(mut au) => {
            if tracks.foreign.contains(&au.track) {
                return None;
            }
            au.track = tracks.remap(leg, au.track)?;
            Some(StreamEvent::Au(au))
        }
        StreamEvent::Discontinuity(track, reason) => {
            if tracks.foreign.contains(&track) {
                return None;
            }
            Some(StreamEvent::Discontinuity(
                tracks.remap(leg, track)?,
                reason,
            ))
        }
        // Captions ride the video bitstream, so an audio leg has none to
        // contribute. Metadata and Eos carry no track.
        StreamEvent::Caption(_) if leg == Leg::Audio => None,
        other => Some(other),
    }
}

impl Default for SplitLegs {
    fn default() -> Self {
        Self::new()
    }
}

impl PipelineShared {
    /// Release: everything published before a state change is visible to
    /// a caller that acquires the new state through [`Self::state`]. Error
    /// is final: the pipeline threads have stopped, and nothing but a fresh
    /// open may report the session as anything else.
    pub fn set_state(&self, state: State) {
        let stored = self
            .shared
            .state
            .fetch_update(Ordering::AcqRel, Ordering::Acquire, |s| {
                (s != State::Error as u32).then_some(state as u32)
            });
        if stored.is_ok_and(|previous| previous != state as u32) {
            self.state_event(state);
        }
    }

    /// Move to `to` only from one of `from`, for a thread that decided on the
    /// transition from a state it read earlier: whatever another thread has
    /// published since then stays.
    pub(crate) fn claim_state(&self, from: &[State], to: State) -> bool {
        let claimed = claim(&self.shared.state, from, to);
        if claimed {
            self.state_event(to);
        }
        claimed
    }

    /// End the session for a decode thread that reached the end of
    /// `generation`'s timeline. An end of stream queued ahead of a seek's
    /// Flush belongs to the timeline the seek left, so the end is refused
    /// while a seek is in flight or once the generation has moved on; the
    /// caller asks again until it succeeds or a Flush retires the end.
    /// `transport` keeps a seek from starting in between.
    pub(crate) fn end_timeline(&self, generation: Generation, from: &[State]) -> bool {
        let _transport = self.transport.lock().expect("transport lock");
        if !end_is_current(
            self.seeks_pending.load(Ordering::Acquire),
            self.shared.generation.load(Ordering::Relaxed),
            generation.0,
        ) {
            return false;
        }
        // Ended freezes position, and the audio thread's last reading can
        // trail the clock by a tick, so the clock's own reading is stored,
        // ahead of the claim so it is visible before Ended is. A failure or
        // pause can still take the state (`fail` does not take `transport`);
        // the previous reading goes back then.
        let before = self.shared.position_us.load(Ordering::Relaxed);
        let at_end = self.clock_position();
        self.shared.position_us.store(at_end, Ordering::Relaxed);
        if self.claim_state(from, State::Ended) {
            return true;
        }
        self.shared.position_us.store(before, Ordering::Relaxed);
        false
    }

    /// The clock's reading as a session position, clamped to the duration
    /// where one is known.
    fn clock_position(&self) -> i64 {
        let now = self
            .clock
            .lock()
            .expect("clock lock")
            .now(self.wall.now())
            .as_micros();
        let duration = self.shared.duration_us.load(Ordering::Relaxed);
        if duration > 0 { now.min(duration) } else { now }
    }

    fn state_event(&self, state: State) {
        self.diag.event(
            self.wall.now(),
            EventCode::StateChange,
            Stage::Clock,
            format!("{state:?}"),
        );
    }

    pub fn state(&self) -> u32 {
        self.shared.state.load(Ordering::Acquire)
    }

    /// Buffering → Playing at a presentation of the timeline in force,
    /// unless a pause is waiting on it, in which case the state stays
    /// Buffering for [`Self::settle_pause`]. `generation` is the presented
    /// frame's: between a seek advancing the generation and the video
    /// thread clearing the pool, a render event can still present the old
    /// timeline's due frame, and that must not end the new one's Buffering.
    pub(crate) fn leave_buffering(&self, generation: u64) {
        // Under the gate the generation cannot advance between the check
        // and the store. A seek mid-flight holds it; this presentation then
        // changes nothing and the next one asks again.
        let Ok(_timeline) = self.timeline.try_lock() else {
            return;
        };
        // The counter before the generation, as in `settle_pause`: reading
        // zero guarantees the generation read is the last seek's.
        let seeks_pending = self.seeks_pending.load(Ordering::Acquire);
        if !buffering_ends(
            generation,
            self.shared.generation.load(Ordering::Relaxed),
            seeks_pending,
            self.pause_wanted.load(Ordering::Relaxed),
        ) {
            return;
        }
        // Claimed rather than stored: a Paused or Ended that landed since
        // the caller presented stays.
        self.claim_state(&[State::Buffering], State::Playing);
    }

    /// Publish Paused, then park the clock and freeze the wall. The caller
    /// holds `transport` and has seen Playing or Buffering; an end or a
    /// failure published since then stays, and the clock is left running.
    pub(crate) fn park_paused(&self) {
        if !self.claim_state(&[State::Playing, State::Buffering], State::Paused) {
            return;
        }
        let wall = self.wall.now();
        self.clock
            .lock()
            .expect("clock lock")
            .set_playing(wall, false);
        self.clock_playing.store(false, Ordering::Relaxed);
        self.present.mirror_clock(wall, MediaTime::ZERO, false);
        self.wall.pause();
    }

    /// Complete a wanted pause from a decode thread. A buffering session
    /// has landed once its generation's picture is on the output or, from
    /// the audio thread on a session with no video, once the ring is ready;
    /// `ring` is then the generation that ring belongs to. A Playing
    /// session is parked too: the render thread reads the flag without the
    /// lock, so its Buffering → Playing can slip past a pause request by a
    /// tick.
    pub(crate) fn settle_pause(&self, ring: Option<Generation>) {
        let _transport = self.transport.lock().expect("transport lock");
        // The counter first, as an acquire against the demux thread's
        // release: reading zero guarantees the generation read below is the
        // one the last seek advanced to. In the other order, a seek
        // completing in between would let the old timeline's picture pass
        // for the landing.
        if !self.pause_wanted.load(Ordering::Relaxed)
            || self.seeks_pending.load(Ordering::Acquire) != 0
        {
            return;
        }
        let current = self.shared.generation.load(Ordering::Relaxed);
        let landed = match ring {
            Some(generation) => generation.0 == current,
            None => self.shown_generation.load(Ordering::Relaxed) == current,
        };
        // Playing needs the landing too: a render event that read the flag
        // before the request can publish Playing after the seek, with the
        // old timeline's picture still showing.
        let state = self.state();
        if landed && (state == State::Playing as u32 || state == State::Buffering as u32) {
            self.park_paused();
        }
    }

    /// End the session in Error. Once the session is stopping, a failure is
    /// what the stop's cancellation looks like from the thread it cut short
    /// (a read cancelled, a container cut off mid-box), so it is not
    /// reported, and the first failure stays the one reported.
    pub fn fail(&self, error: EngineError) {
        if self.shared.stop.swap(true, Ordering::AcqRel) {
            return;
        }
        self.shared.last_error.store(error.code, Ordering::Relaxed);
        self.shared
            .last_error_category
            .store(error.category as u32, Ordering::Relaxed);
        self.diag.event(
            self.wall.now(),
            EventCode::Error,
            error.stage,
            error.detail.clone(),
        );
        diag_err!("session error: {}", error.detail);
        self.set_state(State::Error);
        self.bank.changed.notify_all();
    }

    /// A decoder refused its track. The reason goes out as a diagnostic,
    /// and the session fails with it if nothing else is left to play.
    pub fn refuse(&self, kind: TrackKind, reason: String) {
        self.diag.event(
            self.wall.now(),
            EventCode::CodecRefused,
            Stage::Decode,
            reason.clone(),
        );
        if let Some(detail) = self.playable.refused(kind, &reason) {
            self.fail(EngineError::refused(detail));
        }
    }

    /// A demux thread knows every track it will announce.
    fn settle(&self, leg: Leg) {
        if let Some(detail) = self.playable.settled(leg) {
            self.fail(EngineError::refused(detail));
        }
    }

    pub fn stopping(&self) -> bool {
        self.shared.stop.load(Ordering::Relaxed)
    }

    fn push_caption(&self, cue: media_bitstream::CaptionCue) {
        let mut ring = self.captions.lock().expect("captions lock");
        if ring.len() >= CAPTION_RING {
            ring.pop_front();
        }
        ring.push_back(cue);
    }
}

pub enum MediaMsg {
    Format(Format),
    Au(Au),
    /// Seek/flush marker: drop decoder state, adopt the generation.
    Flush {
        generation: Generation,
    },
    Eos,
}

/// Rebuilds the source and demuxer after transport loss. Sessions that
/// cannot reconnect (on-demand, injected sources) pass `None`, and a
/// failure fails the session.
pub type DemuxFactory = Box<dyn FnMut() -> Result<Box<dyn Demuxer>, EngineError> + Send>;

/// Reconnect attempts and backoff.
const RECONNECT_ATTEMPTS: u32 = 6;
const RECONNECT_BASE: Duration = Duration::from_millis(500);
const RECONNECT_CAP: Duration = Duration::from_secs(8);

/// Whether a demux-thread failure is transport loss (worth a reconnect)
/// rather than a parse refusal, which is a property of the stream and would
/// loop forever if retried.
fn is_transport_loss(error: &media_demux::DemuxError) -> bool {
    match error {
        media_demux::DemuxError::Source(source) => {
            source.downcast_ref::<media_io::IoError>().is_none_or(|io| {
                matches!(
                    io.kind,
                    media_io::IoErrorKind::Read
                        | media_io::IoErrorKind::Connect
                        | media_io::IoErrorKind::Http
                        | media_io::IoErrorKind::Resolve
                )
            })
        }
        media_demux::DemuxError::Io(_) => true,
        _ => false,
    }
}

/// Demux thread: pull events, push into the Bank with backpressure, run
/// seeks and, on live sessions, rebuild the transport when it dies. The
/// generation does not advance across a reconnect, since it is not a seek
/// and the banked depth keeps playing through the outage. The clock's snap
/// absorbs the timeline jump when post-reconnect frames arrive.
///
/// One of these runs per source. A split pair runs two, and everything
/// specific to that requires [`PipelineShared::split`] to be set.
pub fn run_demux_leg(
    px: &Arc<PipelineShared>,
    mut demuxer: Box<dyn Demuxer>,
    video_tx: &SyncSender<MediaMsg>,
    audio_tx: &SyncSender<MediaMsg>,
    mut factory: Option<DemuxFactory>,
    leg: Leg,
) {
    let mut eos_reached = false;
    let mut pending: Option<StreamEvent> = None;
    // Empty on a single-source session, which keeps everything.
    let mut leg_tracks = LegTracks::default();
    // The seek the audio leg has already followed.
    let mut followed_seek: Option<Generation> = None;
    // Whether this leg has been picked to carry the pair's Eos. Held across
    // a Bank-full retry: the pick is made once, and asking again would hand
    // the Eos to nobody and hang the session.
    let mut carries_eos = false;
    // The Eos this leg pulled but has not banked yet.
    let mut held_eos: Option<StreamEvent> = None;
    // In-band CEA-608: every H.264 AU is scanned for caption SEI here, in
    // decode order on arrival, because the 608 pair stream is stateful and
    // follows decode order. Display against the due PTS happens at the
    // consumer. One scanner per session; seeks reset it.
    let mut caption_scanner = media_bitstream::CaptionScanner::new();
    let mut caption_track: Option<media_demux::TrackId> = None;
    // SEI user data travels in the same AUs (H.264 and H.265 both carry
    // it) and is scanned here for the same reason.
    let mut user_data_scanner = media_bitstream::UserDataScanner::new();
    let mut user_data_track: Option<(media_demux::TrackId, bool)> = None;
    // Where the generation starts presenting when a seek landed ahead of
    // its target, and the track whose access units must reach the decoder
    // to get there.
    let mut floor: Option<MediaTime> = None;
    let mut video_track: Option<media_demux::TrackId> = None;
    let mut frame_step = FrameStep::default();
    // The kinds this leg has announced, and whether it has announced all it
    // will: a track the demuxer names is announced before its first access
    // unit, except where a transport stream holds its format back until the
    // stream has said enough.
    let mut announced_video = false;
    let mut announced_audio = false;
    let mut settled = false;
    // What the caption display holds once the span ahead of the floor has
    // been decoded: the text of the last cue in it, empty for a clear.
    let mut caption_at_floor: Option<String> = None;
    // Set once an access unit at or past the floor has been scanned. From
    // there cues go out in decode order as usual: a reordered frame that
    // displays just ahead of the floor still follows the one that reached
    // it.
    let mut captions_live = true;
    // The minimum media the Bank holds before it refuses a push. Read once,
    // since a session's Bank config is settled at open.
    let decoder_cushion_us = px
        .bank
        .bank
        .lock()
        .expect("bank lock")
        .config()
        .decoder_cushion
        .as_micros();
    loop {
        if px.stopping() {
            return;
        }

        // The audio leg of a split pair takes no commands of its own. It
        // goes where the video leg's seek landed, so both resume from the
        // same point on the same generation.
        if leg == Leg::Audio
            && let Some(split) = px.split.get()
        {
            let wanted = *split.seek.lock().expect("split seek lock");
            if let Some((generation, landed)) = wanted
                && followed_seek != Some(generation)
            {
                followed_seek = Some(generation);
                // The pair shares one floor: the video leg set it before it
                // published the seek this leg is following.
                let shared_floor = px.seek_floor_us.load(Ordering::Relaxed);
                floor = (shared_floor != NO_FLOOR).then(|| MediaTime::from_micros(shared_floor));

                match demuxer.seek(landed, generation) {
                    Ok(_) => {}
                    // An unseekable audio leg plays on; the video leg has
                    // already reported the refusal.
                    Err(media_demux::DemuxError::Unsupported(_)) => {}
                    Err(e) => {
                        px.fail(EngineError::demux(e));
                        return;
                    }
                }
                split.reset_origin(leg);
                pending = None;
                eos_reached = false;
                carries_eos = false;
                held_eos = None;
                continue;
            }
        }

        // Seeks run here, where both the demuxer and the Bank are owned.
        // The newest seek queued supersedes any queued before it.
        let (command, taken) = if leg == Leg::Audio {
            (None, 0)
        } else {
            let mut commands = px.commands.lock().expect("commands lock");
            let taken = commands.len() as u32;
            let newest = commands.pop();
            commands.clear();
            (newest, taken)
        };
        if let Some(Command::Seek(target)) = command {
            let generation = px.bank.bank.lock().expect("bank lock").generation().next();
            match demuxer.seek(target, generation) {
                Ok(landed) => {
                    // A demuxer lands on the keyframe at or before the
                    // target. Within the bound the generation starts at the
                    // target and the span before it is decoded unseen.
                    // Otherwise, or if the demuxer landed at or after the
                    // target, it starts where the demuxer stopped.
                    floor = presentation_floor(target, landed, frame_step.step());
                    frame_step.seeked();
                    caption_at_floor = None;
                    captions_live = floor.is_none();
                    let start = floor.unwrap_or(landed);
                    px.bank
                        .bank
                        .lock()
                        .expect("bank lock")
                        .advance_generation(generation);
                    // Held until Buffering is published below, so no
                    // presentation of the old timeline can end the new
                    // one's Buffering in between.
                    let timeline = px.timeline.lock().expect("timeline lock");
                    px.shared.generation.store(generation.0, Ordering::Relaxed);
                    px.seek_floor_us.store(
                        floor.map_or(NO_FLOOR, MediaTime::as_micros),
                        Ordering::Relaxed,
                    );
                    px.generation_start_us
                        .store(start.as_micros(), Ordering::Relaxed);
                    px.seek_fed.store(0, Ordering::Relaxed);
                    px.seek_taken.store(0, Ordering::Relaxed);
                    {
                        // Snap to where the generation starts and park the
                        // clock. It restarts when the first post-seek frame
                        // is ready, so decode latency never reads as
                        // lateness.
                        let wall = px.wall.now();
                        let mut clock = px.clock.lock().expect("clock lock");
                        clock.advance_generation(wall, generation, start);
                        clock.set_playing(wall, false);
                        px.clock_playing.store(false, Ordering::Relaxed);
                        px.present.mirror_clock(wall, MediaTime::ZERO, false);
                    }
                    // Re-assert Buffering: a present that raced the seek
                    // command may have flipped the state back to Playing
                    // between the session call and the clock parking here.
                    px.set_state(State::Buffering);
                    drop(timeline);
                    let _ = video_tx.send(MediaMsg::Flush { generation });
                    let _ = audio_tx.send(MediaMsg::Flush { generation });
                    px.diag.event(
                        px.wall.now(),
                        EventCode::Seek,
                        Stage::Demux,
                        if floor.is_some() {
                            format!("to {target}, landed {landed}, presenting from {start}")
                        } else {
                            format!("to {target}, landed {landed}")
                        },
                    );
                    px.shared
                        .position_us
                        .store(start.as_micros(), Ordering::Relaxed);
                    px.presented_pts_us.store(i64::MIN, Ordering::Relaxed);
                    pending = None;
                    eos_reached = false;
                    carries_eos = false;
                    held_eos = None;
                    // Captions from the old position must not survive the
                    // jump: reset the scanner, drop queued cues and clear
                    // the display where the generation starts.
                    caption_scanner.reset();
                    px.captions.lock().expect("captions lock").clear();
                    user_data_scanner.reset();
                    px.user_data.lock().expect("user data lock").clear();
                    px.push_caption(media_bitstream::CaptionCue {
                        pts_us: start.as_micros(),
                        text: String::new(),
                    });
                    // Hand the landing to the audio leg, and let both legs
                    // reach the end again on the new timeline.
                    if let Some(split) = px.split.get() {
                        split.clear_eos();
                        split.rebase();
                        split.reset_origin(leg);
                        *split.seek.lock().expect("split seek lock") = Some((generation, start));
                    }
                    px.bank.changed.notify_all();
                }
                // An unseekable (live) source refuses the seek and plays
                // on. That is a property of the source, not a failure.
                Err(media_demux::DemuxError::Unsupported(what)) => {
                    px.diag.event(
                        px.wall.now(),
                        EventCode::Seek,
                        Stage::Demux,
                        format!("to {target}, refused: {what}"),
                    );
                }
                Err(e) => px.fail(EngineError::demux(e)),
            }
            // After the generation advance, and a release, so a pause that
            // reads zero also reads the new generation and cannot take the
            // old timeline's picture for its own.
            px.seeks_pending.fetch_sub(taken, Ordering::Release);
            continue;
        }

        if eos_reached && pending.is_none() {
            // A held Eos is re-offered every tick, not decided once when it
            // arrived. The two legs finish independently and a seek can
            // reset the pair mid-handshake, so a single edge can go missing.
            // Re-checking means the session always ends, at worst a tick
            // late.
            if let Some(split) = px.split.get()
                && !carries_eos
                && held_eos.is_some()
                && split.both_reached_eos()
                && split.claim_carrier()
            {
                carries_eos = true;
                eos_reached = false;
                pending = held_eos.take();
                continue;
            }
            // Nothing to pull; idle until a seek or stop.
            std::thread::park_timeout(IDLE_WAIT);
            continue;
        }

        let event = match pending.take() {
            Some(event) => event,
            None => match demuxer.next_event() {
                Ok(event) => {
                    px.diag
                        .stage(Stage::Demux)
                        .out_count
                        .fetch_add(1, Ordering::Relaxed);
                    px.diag
                        .stage(Stage::Demux)
                        .out_bytes
                        .fetch_add(event.payload_bytes() as u64, Ordering::Relaxed);
                    // SEI is scanned on first pull only, since a Bank-full
                    // retry must not re-feed the stateful 608 decoder. It
                    // is also skipped on the audio leg: its source may carry
                    // a picture of its own (dropped by adapt_leg_event
                    // below), and scanning it would mix a second timeline
                    // into rings the video leg is filling.
                    match &event {
                        _ if leg == Leg::Audio => {}
                        StreamEvent::Format(track, Format::Video { codec, .. }) => {
                            caption_track =
                                (*codec == media_demux::VideoCodec::H264).then_some(*track);
                            user_data_track = match codec {
                                media_demux::VideoCodec::H264 => Some((*track, false)),
                                media_demux::VideoCodec::H265 => Some((*track, true)),
                                _ => None,
                            };
                        }
                        // Nothing ahead of the floor is shown, so its cues
                        // and user data are not the new timeline's. The
                        // caption decoder is stateful, though: a caption
                        // that went up ahead of the floor and is still up at
                        // it is only known by decoding that span. Its cues
                        // are held back, and the last one is what the
                        // display holds at the floor. User data has no state
                        // to rebuild and is skipped.
                        StreamEvent::Au(au)
                            if !captions_live && floor.is_some_and(|floor| au.pts < floor) =>
                        {
                            if Some(au.track) == caption_track
                                && let Some(cue) =
                                    caption_scanner.scan_au(&au.data, false, au.pts.as_micros())
                            {
                                caption_at_floor = Some(cue.text);
                            }
                        }
                        StreamEvent::Au(au) => {
                            if Some(au.track) == caption_track {
                                // The floor is reached here, before this
                                // access unit's own scan. What was already
                                // up goes out first, or the older text would
                                // overwrite a cue this unit carries. A clear
                                // needs nothing: the seek cleared the
                                // display at the floor.
                                if !captions_live {
                                    captions_live = true;
                                    if let (Some(at), Some(text)) = (floor, caption_at_floor.take())
                                        && !text.is_empty()
                                    {
                                        px.push_caption(media_bitstream::CaptionCue {
                                            pts_us: at.as_micros(),
                                            text,
                                        });
                                    }
                                }
                                if let Some(cue) =
                                    caption_scanner.scan_au(&au.data, false, au.pts.as_micros())
                                {
                                    px.push_caption(cue);
                                }
                            }
                            if let Some((track, hevc)) = user_data_track
                                && au.track == track
                            {
                                // Collected first: a backwards jump means the
                                // timeline restarted and everything queued
                                // before this AU belongs to the old one. A
                                // loop lands back among the old pass's first
                                // timestamps, so no pts comparison can tell
                                // them apart.
                                let mut messages = Vec::new();
                                let new_epoch = user_data_scanner.scan_au(
                                    &au.data,
                                    hevc,
                                    au.pts.as_micros(),
                                    |m| messages.push(m),
                                );
                                let mut ring = px.user_data.lock().expect("user data lock");
                                if new_epoch {
                                    ring.clear();
                                }
                                for m in messages {
                                    ring.push(m);
                                }
                            }
                        }
                        _ => {}
                    }
                    match adapt_leg_event(leg, &mut leg_tracks, event) {
                        Some(event) => {
                            match &event {
                                // The seek floor tests adapted access units,
                                // so it learns the id a split leg gives them.
                                StreamEvent::Format(track, Format::Video { .. }) => {
                                    video_track = Some(*track);
                                    frame_step.new_format();
                                    announced_video = true;
                                    px.playable.announced(TrackKind::Video);
                                }
                                StreamEvent::Format(_, Format::Audio { .. }) => {
                                    announced_audio = true;
                                    px.playable.announced(TrackKind::Audio);
                                }
                                StreamEvent::Au(_) if !settled => {
                                    let wants_video = leg != Leg::Audio;
                                    let wants_audio = leg != Leg::Video;
                                    settled = (!wants_video
                                        || announced_video
                                        || demuxer.video_track().is_none())
                                        && (!wants_audio
                                            || announced_audio
                                            || demuxer.audio_track().is_none());
                                    if settled {
                                        px.settle(leg);
                                    }
                                }
                                StreamEvent::Eos(_) if !settled => {
                                    settled = true;
                                    px.settle(leg);
                                }
                                _ => {}
                            }
                            event
                        }
                        // A track this leg does not contribute.
                        None => continue,
                    }
                }
                Err(e) => {
                    if let Some(rebuild) = factory.as_mut()
                        && is_transport_loss(&e)
                    {
                        match reconnect(px, rebuild, &e) {
                            Some(rebuilt) => {
                                demuxer = rebuilt;
                                continue;
                            }
                            None => {
                                if !px.stopping() {
                                    px.fail(EngineError::demux(e));
                                }
                                return;
                            }
                        }
                    }
                    let refused = matches!(e, media_demux::DemuxError::Refused(_));
                    let mut error = EngineError::demux(e);
                    // Named beside whatever else was left out, such as an
                    // audio rendition that could not be opened.
                    if refused {
                        error.detail = px.playable.all_reasons_with(&error.detail);
                    }
                    px.fail(error);
                    return;
                }
            },
        };
        // EOF on a live transport is treated as loss: a dropped TCP
        // connection with a connection-close body looks exactly like a
        // finished stream, so try to rejoin. Only exhausted attempts let the
        // Eos through, as Ended rather than Error, since the broadcaster may
        // really have stopped.
        if matches!(event, StreamEvent::Eos(_))
            && let Some(rebuild) = factory.as_mut()
        {
            let cause = media_demux::DemuxError::Unsupported("live source delivered EOF");
            match reconnect(px, rebuild, &cause) {
                Some(rebuilt) => {
                    demuxer = rebuilt;
                    continue;
                }
                None => {
                    if px.stopping() {
                        return;
                    }
                    // Fall through: the Eos flows to the Bank as usual.
                }
            }
        }
        let is_eos = matches!(event, StreamEvent::Eos(_));

        if let StreamEvent::Au(au) = &event
            && Some(au.track) == video_track
        {
            frame_step.observe(au.dts);
        }

        // Media ahead of the floor never enters the Bank, which would pace
        // it at 1x. Video goes straight to the decoder, on the channel and
        // from the thread that carried the Flush, so it decodes as fast as
        // the decoder takes it and the Bank's first event of the generation
        // is the first at the floor. Decode order decides: a frame that
        // displays after the floor can still decode before it. Audio has no
        // such dependency and is dropped, apart from a short lead-in. The
        // first access unit to reach the floor ends the span. Nothing is
        // pushed until the video thread has taken what was fed, so the Bank
        // starts its schedule with the decoder at the floor, not a span
        // behind it.
        if let (Some(at), StreamEvent::Au(au)) = (floor, &event) {
            let ahead = if Some(au.track) == video_track {
                au.dts < at
            } else {
                au.pts + SEEK_AUDIO_LEAD_IN < at
            };
            if !ahead
                && px.seek_taken.load(Ordering::Relaxed) < px.seek_fed.load(Ordering::Relaxed)
                && px.state() != State::Error as u32
            {
                pending = Some(event);
                std::thread::park_timeout(SEEK_FEED_WAIT);
                continue;
            }
            if Some(au.track) == video_track {
                if au.dts < at {
                    let StreamEvent::Au(au) = event else {
                        unreachable!("matched an access unit above")
                    };
                    match video_tx.try_send(MediaMsg::Au(au)) {
                        Ok(()) => {
                            px.seek_fed.fetch_add(1, Ordering::Relaxed);
                        }
                        Err(std::sync::mpsc::TrySendError::Disconnected(_)) => {}
                        Err(std::sync::mpsc::TrySendError::Full(MediaMsg::Au(au))) => {
                            pending = Some(StreamEvent::Au(au));
                            std::thread::park_timeout(SEEK_FEED_WAIT);
                        }
                        Err(std::sync::mpsc::TrySendError::Full(_)) => {
                            unreachable!("an access unit was sent")
                        }
                    }
                    continue;
                }
                floor = None;
            } else if au.pts + SEEK_AUDIO_LEAD_IN < at {
                continue;
            } else if video_track.is_none() {
                floor = None;
            }
        }

        // On a split pair the session ends only once both sources have. The
        // legs are cuts of the same content but rarely exactly the same
        // length, so the shorter must not end the other. The pick is
        // remembered because a full Bank sends this Eos round again, and
        // asking twice would give it to neither leg.
        if is_eos
            && let Some(split) = px.split.get()
            && !carries_eos
        {
            split.mark_eos(leg);
            if split.both_reached_eos() && split.claim_carrier() {
                carries_eos = true;
            } else {
                // Hold it: the other leg is still going, or got there first.
                // The idle branch re-checks, so whichever leg ends up
                // holding an unbanked Eos carries it.
                eos_reached = true;
                held_eos = Some(event);
                continue;
            }
        }

        // Keep the pair in step. One Bank serves both legs, so a leg that
        // reads far ahead of the other takes the room the other one needs.
        let banked_dts_us = match (&event, px.split.get()) {
            (StreamEvent::Au(au), Some(split)) => {
                let dts_us = au.dts.as_micros();
                split.note_origin(leg, dts_us);
                // Two separately muxed sources need not agree on where their
                // timelines start, and the Bank measures what it holds as one
                // span across both legs: a gap between the origins counts as
                // held media. Past the cushion the Bank reads as full from
                // the trailing leg's first access unit and the session would
                // sit in Buffering until closed. The pair is refused. Only
                // before the first seek: a landed seek re-latches both
                // baselines wherever each demuxer stopped.
                if px.shared.generation.load(Ordering::Relaxed) == 0
                    && let Some(gap_us) = split.origin_gap_us()
                    && gap_us > decoder_cushion_us
                {
                    px.fail(EngineError::split_origin_mismatch(format!(
                        "the two sources start {} ms apart on their own timelines, past the {} ms of media the buffer can hold",
                        gap_us / 1000,
                        decoder_cushion_us / 1000
                    )));
                    return;
                }
                if split.must_wait_for_other(leg, dts_us) {
                    pending = Some(event);
                    let bank = px.bank.bank.lock().expect("bank lock");
                    let _ = px.bank.changed.wait_timeout(bank, IDLE_WAIT);
                    continue;
                }
                Some(dts_us)
            }
            _ => None,
        };

        let wall = px.wall.now();
        let mut bank = px.bank.bank.lock().expect("bank lock");
        match bank.push(wall, event) {
            PushOutcome::Accepted => {
                px.diag
                    .stage(Stage::Bank)
                    .in_count
                    .fetch_add(1, Ordering::Relaxed);
                if let (Some(dts_us), Some(split)) = (banked_dts_us, px.split.get()) {
                    split.note_banked(leg, dts_us);
                }
                px.bank.changed.notify_all();
                if is_eos {
                    eos_reached = true;
                }
            }
            PushOutcome::StaleGeneration => {
                px.diag
                    .stage(Stage::Bank)
                    .drops
                    .fetch_add(1, Ordering::Relaxed);
            }
            PushOutcome::Full(event) => {
                pending = Some(event);
                // Backpressure: wait for release to drain (or a command).
                let _ = px.bank.changed.wait_timeout(bank, IDLE_WAIT);
            }
        }
    }
}

/// The reconnect loop: bounded attempts with jittered exponential backoff,
/// each attempt logged as a diagnostics event. Returns `None` when attempts
/// are exhausted or the session is stopping.
fn reconnect(
    px: &Arc<PipelineShared>,
    factory: &mut DemuxFactory,
    cause: &media_demux::DemuxError,
) -> Option<Box<dyn Demuxer>> {
    for attempt in 1..=RECONNECT_ATTEMPTS {
        let backoff = RECONNECT_BASE
            .saturating_mul(1 << (attempt - 1).min(4))
            .min(RECONNECT_CAP);
        // ±25% jitter so a room of viewers does not reconnect to the origin
        // in lockstep. The wall clock is entropy enough.
        let jitter_ppm = (px.wall.now().as_micros() % 500_000) - 250_000;
        let backoff = Duration::from_micros(
            (backoff.as_micros() as i64 * (1_000_000 + jitter_ppm) / 1_000_000) as u64,
        );
        px.diag.event(
            px.wall.now(),
            EventCode::Reconnect,
            Stage::Source,
            format!("attempt {attempt}/{RECONNECT_ATTEMPTS} in {backoff:?} after: {cause}"),
        );
        diag_warn!(
            "transport lost ({cause}); reconnect attempt {attempt}/{RECONNECT_ATTEMPTS} in {backoff:?}"
        );

        let deadline = Instant::now() + backoff;
        while Instant::now() < deadline {
            if px.stopping() {
                return None;
            }
            std::thread::sleep(Duration::from_millis(50));
        }

        match factory() {
            Ok(demuxer) => {
                px.diag.event(
                    px.wall.now(),
                    EventCode::Reconnect,
                    Stage::Source,
                    format!("reconnected on attempt {attempt}"),
                );
                diag_log!("reconnected on attempt {attempt}");
                return Some(demuxer);
            }
            Err(e) => {
                px.diag.event(
                    px.wall.now(),
                    EventCode::Reconnect,
                    Stage::Source,
                    format!("attempt {attempt} failed: {}", e.detail),
                );
            }
        }
    }
    None
}

/// How often a parked message retries its channel: no notification exists
/// for a `SyncSender` slot freeing, so a blocked target is polled.
const PARKED_POLL: Duration = Duration::from_millis(4);

/// One decode channel's parked tail: messages the channel had no room for,
/// delivered in order before anything newer is popped for this target.
/// While non-empty the target's whole track is gated in the Bank, so
/// per-track order is exact, and the other track keeps routing.
#[derive(Default)]
struct ParkedTarget {
    msgs: std::collections::VecDeque<MediaMsg>,
}

impl ParkedTarget {
    /// Deliver as much of the tail as the channel accepts.
    fn flush(&mut self, tx: &SyncSender<MediaMsg>) {
        while let Some(msg) = self.msgs.pop_front() {
            match tx.try_send(msg) {
                Ok(()) => {}
                Err(std::sync::mpsc::TrySendError::Full(msg)) => {
                    self.msgs.push_front(msg);
                    return;
                }
                // Teardown: the receiver is gone, nothing left to order.
                Err(std::sync::mpsc::TrySendError::Disconnected(_)) => {}
            }
        }
    }

    fn send(&mut self, tx: &SyncSender<MediaMsg>, msg: MediaMsg) {
        if !self.msgs.is_empty() {
            self.msgs.push_back(msg);
            return;
        }
        match tx.try_send(msg) {
            Ok(()) | Err(std::sync::mpsc::TrySendError::Disconnected(_)) => {}
            Err(std::sync::mpsc::TrySendError::Full(msg)) => self.msgs.push_back(msg),
        }
    }

    /// A seek happened while messages were parked. Stale AUs and a stale
    /// Eos must not cross into the new generation (Eos carries no
    /// generation, so a late one would end the fresh timeline). Formats are
    /// timeline-free decoder config and are not re-announced after a seek,
    /// so they survive.
    fn drop_stale(&mut self) {
        self.msgs.retain(|msg| matches!(msg, MediaMsg::Format(_)));
    }
}

/// Release thread: drain the Bank on the 1x schedule and route events.
/// Track identity is learned from the Format events flowing through the
/// Bank (a TS demuxer only names its PIDs once the PMT arrives), so
/// routing needs no demuxer-specific knowledge at spawn time. Sends never
/// block: a full decode channel parks that target's messages and gates its
/// track in the Bank while the other track keeps releasing, so one track's
/// capacity cannot wedge the other's release.
pub fn run_release(
    px: &Arc<PipelineShared>,
    video_tx: &SyncSender<MediaMsg>,
    audio_tx: &SyncSender<MediaMsg>,
) {
    let mut video_track: Option<media_demux::TrackId> = None;
    let mut audio_track: Option<media_demux::TrackId> = None;
    let mut parked_video = ParkedTarget::default();
    let mut parked_audio = ParkedTarget::default();
    let mut seen_generation: Option<Generation> = None;
    loop {
        if px.stopping() {
            return;
        }
        parked_video.flush(video_tx);
        parked_audio.flush(audio_tx);

        let wall = px.wall.now();
        let mut bank = px.bank.bank.lock().expect("bank lock");
        let generation = bank.generation();
        if seen_generation != Some(generation) {
            seen_generation = Some(generation);
            parked_video.drop_stale();
            parked_audio.drop_stale();
        }
        let blocked = |event: &StreamEvent| -> bool {
            match event {
                StreamEvent::Au(au) => {
                    (!parked_video.msgs.is_empty() && Some(au.track) == video_track)
                        || (!parked_audio.msgs.is_empty() && Some(au.track) == audio_track)
                }
                StreamEvent::Format(_, Format::Video { .. }) => !parked_video.msgs.is_empty(),
                StreamEvent::Format(_, Format::Audio { .. }) => !parked_audio.msgs.is_empty(),
                _ => false,
            }
        };
        bank.set_downstream_parked(!parked_video.msgs.is_empty() || !parked_audio.msgs.is_empty());
        let popped = bank.pop_due_gated(wall, &blocked);
        let metrics = bank.metrics();
        let awaiting_presentation = bank.awaiting_presentation();
        let next_due = if popped.is_none() {
            bank.next_due_gated(wall, &blocked)
        } else {
            None
        };
        drop(bank);

        // A priming join anchors its schedule to presentation. Once the
        // clock has started (on the video thread or the audio-only path),
        // tell the Bank, so the 1x schedule absorbs the decoder's
        // input-to-output depth instead of starving the primed decoder or
        // spending the banked lag.
        if awaiting_presentation {
            let playing = px.clock.lock().expect("clock lock").is_playing();
            if playing {
                px.bank
                    .bank
                    .lock()
                    .expect("bank lock")
                    .presentation_started(px.wall.now());
            }
        }

        px.shared
            .banked_us
            .store(metrics.banked.as_micros(), Ordering::Relaxed);
        px.shared
            .bank_holding
            .store(metrics.holding, Ordering::Relaxed);
        let diag_bank = px.diag.stage(Stage::Bank);
        diag_bank
            .occupancy
            .store(metrics.banked.as_millis() as u64, Ordering::Relaxed);
        diag_bank
            .occupancy_bytes
            .store(metrics.banked_bytes as u64, Ordering::Relaxed);
        px.diag.set_bank(BankReadings {
            lag: metrics.lag,
            target_lag: metrics.target_lag,
            reanchors: metrics.reanchors,
            reanchor_total: metrics.reanchor_total,
            stall_total: metrics.stall_total,
        });

        match popped {
            Some(event) => {
                diag_bank.out_count.fetch_add(1, Ordering::Relaxed);
                px.bank.changed.notify_all();
                match event {
                    StreamEvent::Format(track, format) => {
                        let (target, tx) = match &format {
                            Format::Video { .. } => {
                                video_track = Some(track);
                                px.video_active.store(true, Ordering::Relaxed);
                                (&mut parked_video, video_tx)
                            }
                            Format::Audio { .. } => {
                                audio_track = Some(track);
                                px.audio_active.store(true, Ordering::Relaxed);
                                (&mut parked_audio, audio_tx)
                            }
                        };
                        target.send(tx, MediaMsg::Format(format));
                    }
                    StreamEvent::Au(au) => {
                        let (target, tx) = if Some(au.track) == video_track {
                            (&mut parked_video, video_tx)
                        } else if Some(au.track) == audio_track {
                            (&mut parked_audio, audio_tx)
                        } else {
                            // No format announced for this track yet:
                            // nothing downstream could decode it.
                            diag_bank.drops.fetch_add(1, Ordering::Relaxed);
                            continue;
                        };
                        // A full channel parks the AU. The channel depth
                        // still bounds the decoder's intake without
                        // holding up the other track.
                        target.send(tx, MediaMsg::Au(au));
                    }
                    StreamEvent::Eos(_) => {
                        parked_video.send(video_tx, MediaMsg::Eos);
                        parked_audio.send(audio_tx, MediaMsg::Eos);
                    }
                    StreamEvent::Metadata(_)
                    | StreamEvent::Caption(_)
                    | StreamEvent::Discontinuity(..) => {}
                }
            }
            None => {
                let parked = !parked_video.msgs.is_empty() || !parked_audio.msgs.is_empty();
                let bank = px.bank.bank.lock().expect("bank lock");
                let wait = match next_due {
                    Some(due) if due > wall => {
                        Duration::from_micros((due - wall).as_micros().min(50_000) as u64)
                    }
                    Some(_) => Duration::from_millis(1),
                    None => IDLE_WAIT,
                };
                // A parked message has no wake-up when its channel frees:
                // poll it instead of sleeping the full schedule wait.
                let wait = if parked { wait.min(PARKED_POLL) } else { wait };
                let _ = px.bank.changed.wait_timeout(bank, wait);
            }
        }
    }
}

/// A hardware decoder detected the platform silently falling back to CPU
/// output mid-stream (a probe false positive, seen as output with no DXGI
/// backing). Reroute to the software route and report
/// `DecodeFallbackHwToSw`. A software refusal (including the performance
/// cap) is a CodecRefused: video mutes and audio plays on. Returns the
/// replacement decoder, or `None` when video is now muted. Frames between
/// the fallback point and the next keyframe are lost.
fn reroute_hw_fallback(
    px: &Arc<PipelineShared>,
    current_coded: Option<(media_demux::VideoCodec, u32, u32)>,
    codec_private: &[u8],
    live: bool,
    error: &media_decode::DecodeError,
) -> Option<Box<dyn VideoDecoder>> {
    let Some((codec, width, height)) = current_coded else {
        px.video_active.store(false, Ordering::Relaxed);
        return None;
    };
    match crate::route::open_video_decoder(
        codec,
        width,
        height,
        live,
        crate::DecodePreference::SoftwareOnly,
        codec_private,
    ) {
        Ok(route) => {
            px.diag.event(
                px.wall.now(),
                EventCode::DecodeFallbackHwToSw,
                Stage::Decode,
                format!("{error}; decoding {codec:?} on {}", route.label),
            );
            diag_log!("{error}; decoding {codec:?} on {}", route.label);
            Some(route.decoder)
        }
        Err(refused) => {
            px.video_active.store(false, Ordering::Relaxed);
            px.refuse(
                TrackKind::Video,
                format!("{error}; software route refused {codec:?}: {refused}"),
            );
            None
        }
    }
}

/// Video thread: decode into the FramePool and present due frames on the
/// clock's schedule. Nothing here blocks: a full pool parks the decoded
/// frame in `pending_frame`, a refusing decoder parks the AU in
/// `pending_au`, and both retry each tick while presentation keeps running.
pub fn run_video(px: &Arc<PipelineShared>, rx: &Receiver<MediaMsg>) {
    let mut decoder: Option<Box<dyn VideoDecoder>> = None;
    let mut sink = VideoSink::new();
    let mut generation = {
        let bank = px.bank.bank.lock().expect("bank lock");
        bank.generation()
    };
    let live = {
        let bank = px.bank.bank.lock().expect("bank lock");
        bank.config().liveness == media_bank::Liveness::Live
    };
    let mut draining = false;
    let mut pending_frame: Option<VideoFrame> = None;
    // Opened by a Flush whose seek landed ahead of its target.
    let mut unseen = UnseenSpan::default();
    let mut pending_au: Option<Au> = None;
    let mut eos_after_drain = false;
    let mut eos_undecoded = false;
    // The render thread takes a frame before it is due, so an empty pool
    // does not mean the last frame has had its time on screen: the end
    // waits for the clock too.
    let mut published = Published::default();
    let mut current_coded: Option<(media_demux::VideoCodec, u32, u32)> = None;
    let mut current_private: Vec<u8> = Vec::new();
    // Discarding late video up to the next keyframe.
    let mut skipping_late = false;
    let mut falling_behind = FallingBehind::default();
    let mut rejoin_pending = false;
    let mut late_reported: Option<std::time::Instant> = None;

    loop {
        if px.stopping() {
            return;
        }

        // A seek has advanced the session generation and this thread's
        // Flush is still in the channel, so everything held here is stale.
        // Drop it rather than park on it. A parked AU against a full decoder
        // would block the intake that delivers the Flush (presentation is
        // parked across the seek, so nothing frees the decoder), and decode
        // pulls would only churn frames the Flush is about to clear.
        let flush_pending = Generation(px.shared.generation.load(Ordering::Relaxed)) != generation;
        if flush_pending {
            pending_au = None;
            pending_frame = None;
        }

        // 1. Move a parked frame into the pool before pulling more output.
        if let Some(frame) = pending_frame.take() {
            let pts = MediaTime::from_micros(frame.pts_us());
            match px.pool.try_publish(frame, generation.0) {
                Ok(()) => {
                    published.push(pts);
                    px.diag
                        .stage(Stage::Decode)
                        .out_count
                        .fetch_add(1, Ordering::Relaxed);
                }
                Err(frame) => pending_frame = Some(frame),
            }
        }
        if !flush_pending && pending_frame.is_none() && decoder.is_some() {
            match decoder.as_mut().expect("decoder checked").try_output() {
                Ok(Some(frame)) => {
                    if let Some(frame) = before_end(px, frame).and_then(|f| unseen.filter(px, f)) {
                        px.shared.frames_decoded.fetch_add(1, Ordering::Relaxed);
                        let pts = MediaTime::from_micros(frame.pts_us());
                        match behind_the_clock(px, generation, pts) {
                            Some(late) if !skipping_late && !draining => {
                                if falling_behind.observe(late) {
                                    falling_behind.clear();
                                    report_late_video(px, &mut late_reported, late);
                                    // A keyframe already waiting is where
                                    // video rejoins; anything else goes.
                                    if pending_au.as_ref().is_some_and(|au| !au.key) {
                                        pending_au = None;
                                        px.diag
                                            .stage(Stage::Decode)
                                            .drops
                                            .fetch_add(1, Ordering::Relaxed);
                                    }
                                    skipping_late = pending_au.is_none();
                                    rejoin_pending = pending_au.is_some();
                                }
                            }
                            _ => falling_behind.clear(),
                        }
                        match px.pool.try_publish(frame, generation.0) {
                            Ok(()) => {
                                published.push(pts);
                                px.diag
                                    .stage(Stage::Decode)
                                    .out_count
                                    .fetch_add(1, Ordering::Relaxed);
                            }
                            Err(frame) => pending_frame = Some(frame),
                        }
                    }
                }
                Ok(None) => {}
                Err(e) => {
                    if decoder.as_ref().is_some_and(|d| d.hardware_fell_back()) {
                        decoder =
                            reroute_hw_fallback(px, current_coded, &current_private, live, &e);
                        if draining && let Some(rerouted) = decoder.as_mut() {
                            let _ = rerouted.begin_drain();
                        }
                    } else {
                        px.fail(EngineError::decode(e));
                        return;
                    }
                }
            }
        }
        // Video rejoins at a keyframe with an empty decoder. A decoder is a
        // queue: what went in before the skip would come out ahead of the
        // keyframe, even later than before, and hold the keyframe's output
        // back until it drained.
        if rejoin_pending {
            rejoin_pending = false;
            pending_frame = None;
            if let Some(active) = decoder.as_mut()
                && let Err(e) = active.reset()
            {
                px.fail(EngineError::decode(e));
                return;
            }
        }

        // 2. Present the newest due frame. A parked clock (startup, or the
        //    frames after a seek) starts at the first ready frame's pts so
        //    buffering time never turns into lateness. The gate is whether
        //    the clock is parked, not the Buffering state: a stale pre-flush
        //    present can race the seek back to Playing. While the Bank
        //    holds, frames may already exist (a priming join decodes during
        //    the hold), and presentation stays gated so the join delivers
        //    its configured depth. The gate asks the Bank directly: during a
        //    priming join the release thread can sit blocked on a decode
        //    channel that only presentation drains, so a stored flag would
        //    deadlock the join.
        let wall = px.wall.now();
        let state = px.state();
        if state != State::Paused as u32
            && state != State::Error as u32
            && let Some(first_pts) = px.pool.first_ready_pts()
        {
            let parked = !px.clock.lock().expect("clock lock").is_playing();
            let gated = parked && px.bank.bank.lock().expect("bank lock").holding(wall);
            // The Bank's hold is the whole start condition here. On a live
            // session the ring has normally started the clock already; this
            // branch starts it where nothing else has (an on-demand source
            // with video, or a session with no audio).
            if !gated {
                let mut clock = px.clock.lock().expect("clock lock");
                // The generations must agree. After a seek parks the clock,
                // pre-flush frames sit in the pool until the Flush is
                // processed, and restarting from one would resume the old
                // timeline. The clock adopts the new generation when the
                // demux thread parks it and this thread adopts it at the
                // Flush; between the two, stay parked.
                if !clock.is_playing() && clock.generation() == generation {
                    // Anchor at the audible position: the master playhead
                    // reads sink latency behind the pull, so starting the
                    // clock that far back puts the standing error at zero
                    // instead of leaving it to converge by slew.
                    let latency = MediaTime::from_micros(
                        px.audio_shared.output_latency_us.load(Ordering::Relaxed),
                    );
                    clock.discontinuity(wall, first_pts - latency);
                    clock.set_playing(wall, true);
                    px.clock_playing.store(true, Ordering::Relaxed);
                    px.present.mirror_clock(wall, clock.now(wall), true);
                    note_presented(px, generation.0);
                }
            }
        }
        let (now, playing) = {
            let clock = px.clock.lock().expect("clock lock");
            (clock.now(wall), clock.is_playing())
        };
        // Refresh the render thread's clock mirror every tick. At the
        // normal 2% slew cap a mirror one 4 ms tick stale is under 0.1 ms
        // out.
        px.present.mirror_clock(wall, now, playing);
        // While a render consumer is live, the render event owns frame
        // selection. This thread presents only for consumers that issue no
        // render events (headless sessions, a non-rendering app).
        if playing
            && !px.present.consumer_live(wall)
            && let Some(mut lease) = px.pool.take_due(now, now)
        {
            if sink.ready() {
                match sink.present(px, &mut lease) {
                    Ok(_fresh) => {
                        px.diag
                            .stage(Stage::Present)
                            .out_count
                            .fetch_add(1, Ordering::Relaxed);
                        px.presented_pts_us
                            .store(lease.pts.as_micros(), Ordering::Relaxed);
                        note_presented(px, lease.generation);
                        px.shown_generation
                            .store(lease.generation, Ordering::Relaxed);
                        px.leave_buffering(lease.generation);
                    }
                    Err(e) => {
                        px.fail(EngineError::present(e));
                        return;
                    }
                }
            }
            px.pool.release(lease);
        }
        if px.pause_wanted.load(Ordering::Relaxed) {
            px.settle_pause(None);
        }
        px.diag
            .stage(Stage::Pool)
            .occupancy
            .store(px.pool.ready_count() as u64, Ordering::Relaxed);
        px.diag
            .stage(Stage::Pool)
            .drops
            .store(px.pool.dropped(), Ordering::Relaxed);

        // 3. Retry a parked AU before taking anything new.
        if pending_au.is_some() && decoder.is_some() {
            let au = pending_au.take().expect("pending_au checked");
            match decoder
                .as_mut()
                .expect("decoder checked")
                .submit(&au.data, au.pts.as_micros())
            {
                Ok(SubmitOutcome::Accepted) => {}
                Ok(SubmitOutcome::NotAccepting) => pending_au = Some(au),
                Err(e) => {
                    if decoder.as_ref().is_some_and(|d| d.hardware_fell_back()) {
                        decoder =
                            reroute_hw_fallback(px, current_coded, &current_private, live, &e);
                        // The replacement decoder picks up from this AU and
                        // joins cleanly at the next keyframe.
                        if decoder.is_some() {
                            pending_au = Some(au);
                        }
                    } else {
                        px.fail(EngineError::decode(e));
                        return;
                    }
                }
            }
        }

        // 4. Take the next message only when nothing is parked upstream.
        if pending_au.is_some() {
            std::thread::sleep(DECODE_TICK);
            continue;
        }
        match rx.recv_timeout(DECODE_TICK) {
            Ok(MediaMsg::Format(Format::Video {
                codec,
                coded_width,
                coded_height,
                display_width,
                display_height,
                codec_private,
            })) => {
                // A re-announce with unchanged geometry (a reconnect on the
                // same rendition) keeps the decoder and the shared texture:
                // the managed side holds the texture handle and must not
                // see it change mid-session.
                if decoder.is_some()
                    && sink.ready()
                    && current_coded == Some((codec, coded_width, coded_height))
                {
                    continue;
                }
                current_coded = Some((codec, coded_width, coded_height));
                current_private = codec_private;
                let decode_device;
                match open_video_decoder(
                    codec,
                    coded_width,
                    coded_height,
                    live,
                    px.decode_preference,
                    &current_private,
                ) {
                    Ok(route) => {
                        if let Some(reason) = &route.fallback {
                            px.diag.event(
                                px.wall.now(),
                                EventCode::DecodeFallbackHwToSw,
                                Stage::Decode,
                                format!("{reason}; decoding {codec:?} on {}", route.label),
                            );
                            diag_log!("{reason}; decoding {codec:?} on {}", route.label);
                        } else {
                            diag_log!("decoding {codec:?} on {}", route.label);
                        }
                        decode_device = route.decode_device;
                        decoder = Some(route.decoder);
                        px.playable.decoding(TrackKind::Video);
                    }
                    Err(e) => {
                        // Refused video mutes the picture, audio plays on,
                        // and Ended becomes the audio thread's call. With
                        // no audio either, the session fails.
                        px.video_active.store(false, Ordering::Relaxed);
                        decoder = None;
                        px.refuse(TrackKind::Video, e.0);
                        continue;
                    }
                }
                px.shared.width.store(display_width, Ordering::Relaxed);
                px.shared.height.store(display_height, Ordering::Relaxed);
                // The output target carries the coded size; the consumer
                // crops to display size when it samples.
                // SAFETY: `decode_device` is the hardware route's own
                // device pointer. The decoder that owns it was moved into
                // `decoder` above, which outlives this call.
                let configured =
                    unsafe { sink.configure(px, coded_width, coded_height, decode_device) };
                if let Err(e) = configured {
                    px.fail(EngineError::present(e));
                    return;
                }
            }
            Ok(MediaMsg::Format(_)) => {}
            Ok(MediaMsg::Au(au)) => {
                if au.generation != generation || flush_pending {
                    continue;
                }
                // Counted on arrival, decoder or not: a message is only taken
                // once the one before it has been accepted, and a track
                // nothing can decode must not hold a seek open.
                px.seek_taken.fetch_add(1, Ordering::Relaxed);
                px.diag
                    .stage(Stage::Decode)
                    .in_count
                    .fetch_add(1, Ordering::Relaxed);
                if au.key {
                    rejoin_pending = skipping_late;
                    skipping_late = false;
                } else if !skipping_late
                    && let Some(behind) = behind_the_clock(px, generation, au.pts)
                    && behind > LATE_VIDEO_SKIP
                {
                    skipping_late = true;
                    falling_behind.clear();
                    report_late_video(px, &mut late_reported, behind);
                }
                if skipping_late {
                    px.diag
                        .stage(Stage::Decode)
                        .drops
                        .fetch_add(1, Ordering::Relaxed);
                    continue;
                }
                if decoder.is_some() {
                    pending_au = Some(au);
                }
            }
            Ok(MediaMsg::Flush { generation: new }) => {
                generation = new;
                skipping_late = false;
                rejoin_pending = false;
                falling_behind.clear();
                draining = false;
                eos_after_drain = false;
                eos_undecoded = false;
                published = Published::default();
                pending_au = None;
                pending_frame = None;
                unseen.arm(px.seek_floor_us.load(Ordering::Relaxed));
                if let Some(active) = decoder.as_mut()
                    && let Err(e) = active.reset()
                {
                    px.fail(EngineError::decode(e));
                    return;
                }
                px.pool.clear();
            }
            Ok(MediaMsg::Eos) => {
                if let Some(active) = decoder.as_mut() {
                    if let Err(e) = active.begin_drain() {
                        px.fail(EngineError::decode(e));
                        return;
                    }
                    draining = true;
                } else if !px.audio_active.load(Ordering::Relaxed) {
                    // No track reached either decode thread, so nothing will
                    // ever present: end below. An audio-only session ends on
                    // the audio thread once its ring drains.
                    eos_undecoded = true;
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return,
        }

        // 5. Drained dry after EOS with nothing left due: the session ends
        //    once the last frame has been presented. An async adapter's
        //    `None` is only dry when `drain_dry` says so. Until then keep
        //    polling (the adapter bounds its own wait), so a flush arriving
        //    during the drain is still picked up within a tick.
        if !flush_pending && draining && pending_frame.is_none() && decoder.is_some() {
            match decoder.as_mut().expect("decoder checked").try_output() {
                Ok(Some(frame)) => {
                    if let Some(frame) = before_end(px, frame).and_then(|f| unseen.filter(px, f)) {
                        px.shared.frames_decoded.fetch_add(1, Ordering::Relaxed);
                        pending_frame = Some(frame);
                    }
                }
                Ok(None) => {
                    if decoder.as_ref().expect("decoder checked").drain_dry() {
                        draining = false;
                        eos_after_drain = true;
                        if let Some(frame) = unseen.give_up(px) {
                            px.shared.frames_decoded.fetch_add(1, Ordering::Relaxed);
                            pending_frame = Some(frame);
                        }
                    }
                }
                Err(e) => {
                    if decoder.as_ref().is_some_and(|d| d.hardware_fell_back()) {
                        decoder =
                            reroute_hw_fallback(px, current_coded, &current_private, live, &e);
                        match decoder.as_mut() {
                            // The replacement has no queued input, so its
                            // drain goes dry at once and the session ends on
                            // whatever was already presented.
                            Some(rerouted) => {
                                let _ = rerouted.begin_drain();
                            }
                            None => {
                                draining = false;
                                eos_after_drain = true;
                            }
                        }
                    } else {
                        px.fail(EngineError::decode(e));
                        return;
                    }
                }
            }
        }
        // A pause or seek published after this pass read Playing keeps the
        // session; a pause is asked again after the next play.
        if eos_after_drain
            && pending_frame.is_none()
            && px.pool.ready_count() == 0
            && published.last.is_none_or(|pts| {
                let end = last_frame_end(
                    pts,
                    published.previous,
                    px.shared.duration_us.load(Ordering::Relaxed),
                );
                behind_the_clock(px, generation, end)
                    .is_some_and(|behind| behind >= MediaTime::ZERO)
            })
            && px.state() == State::Playing as u32
            && (!px.audio_active.load(Ordering::Relaxed)
                || px.audio_tail_out.load(Ordering::Relaxed)
                    == px.shared.generation.load(Ordering::Relaxed))
            && px.end_timeline(generation, &[State::Playing])
        {
            eos_after_drain = false;
        }
        if eos_undecoded && px.end_timeline(generation, &[State::Buffering, State::Playing]) {
            eos_undecoded = false;
        }
    }
}

/// PCM waiting for ring space: (pts of the *next* unwritten frame, data).
struct Pending {
    pts_us: i64,
    data: Vec<f32>,
    offset: usize,
    /// Silence standing in for a gap before the sound, which the origin
    /// drop leaves alone.
    silence: bool,
}

impl Pending {
    fn sound(pts_us: i64, data: Vec<f32>) -> Self {
        Self {
            pts_us,
            data,
            offset: 0,
            silence: false,
        }
    }
}

/// The silence still to place ahead of a generation's first sound, a
/// block at a time so a long gap costs no more memory than a short one,
/// and the sound that follows it.
struct Lead {
    from_us: i64,
    rate: u32,
    channels: usize,
    placed: u64,
    total: u64,
    then: Option<Pending>,
}

/// The longest block of lead-in silence built at once, in seconds.
const LEAD_BLOCK_SECONDS: u64 = 1;

impl Lead {
    /// The next block of silence, or the sound once the gap is filled.
    fn next(&mut self) -> Pending {
        if self.placed < self.total {
            let frames = (self.total - self.placed).min(u64::from(self.rate) * LEAD_BLOCK_SECONDS);
            let pts_us = self.from_us.saturating_add(
                i64::try_from(self.placed * 1_000_000 / u64::from(self.rate)).unwrap_or(i64::MAX),
            );
            self.placed += frames;
            return Pending {
                pts_us,
                data: vec![0.0; frames as usize * self.channels],
                offset: 0,
                silence: true,
            };
        }
        self.then
            .take()
            .expect("the sound follows its lead-in once")
    }

    fn placed(&self) -> bool {
        self.placed >= self.total && self.then.is_none()
    }
}

/// Where a generation's lead-in silence starts: the generation's own start,
/// when the source's sound begins after it. Only a source that states such
/// a gap has one; for any other the audio clock starts at the first sound.
fn lead_start(audio_start_us: i64, generation_start_us: i64) -> Option<i64> {
    (audio_start_us > 0 && generation_start_us < audio_start_us).then_some(generation_start_us)
}

/// Take one decoded chunk on the audio thread: count it, publish its
/// geometry, and start placing it.
///
/// The ring was sized from what the container announced. A decoder can
/// output something else (implicit SBR doubles the rate, some fold surround
/// to stereo), and MediaCodec only says so once it has decoded, so the ring
/// is rebuilt at the chunk's geometry before anything of it is placed.
fn accept_chunk(
    px: &PipelineShared,
    producer: &mut Option<AudioProducer>,
    generation: Generation,
    chunk: PcmChunk,
    lead_from: Option<(i64, i64)>,
    lead: &mut Option<Lead>,
) -> Pending {
    px.diag
        .stage(Stage::Decode)
        .out_bytes
        .fetch_add((chunk.data.len() * 4) as u64, Ordering::Relaxed);
    px.shared
        .audio_rate
        .store(chunk.sample_rate, Ordering::Relaxed);
    px.shared
        .audio_channels
        .store(chunk.channels, Ordering::Relaxed);
    if chunk.sample_rate > 0
        && chunk.channels > 0
        && producer.as_ref().is_some_and(|out| {
            out.sample_rate() != chunk.sample_rate || out.channels() != chunk.channels
        })
    {
        diag_log!(
            "audio ring resized to the decoder's output: {} Hz, {} channels",
            chunk.sample_rate,
            chunk.channels
        );
        *producer = Some(install_audio_generation(
            &px.audio_consumer,
            AudioFormatInfo {
                sample_rate: chunk.sample_rate,
                channels: chunk.channels,
            },
            Arc::clone(&px.audio_shared),
        ));
        px.audio_ring_generation
            .store(generation.0, Ordering::Release);
    }
    begin_chunk(
        Pending::sound(chunk.pts_us, chunk.data),
        chunk.sample_rate,
        chunk.channels,
        lead_from,
        lead,
    )
}

/// The first chunk of a generation, or silence ahead of it when the
/// generation has a gap to fill. The gap runs to the chunk, or to the
/// origin where the chunk opens with priming that will be dropped.
fn begin_chunk(
    chunk: Pending,
    rate: u32,
    channels: u32,
    lead_from: Option<(i64, i64)>,
    lead: &mut Option<Lead>,
) -> Pending {
    let Some((from_us, origin_us)) = lead_from else {
        return chunk;
    };
    let until_us = chunk.pts_us.max(origin_us);
    if until_us <= from_us || rate == 0 || channels == 0 {
        return chunk;
    }
    let gap = u128::from(until_us.abs_diff(from_us));
    let total = u64::try_from((gap * u128::from(rate) + 500_000) / 1_000_000).unwrap_or(u64::MAX);
    // A gap shorter than half a frame has no silence to place.
    if total == 0 {
        return chunk;
    }
    let mut filling = Lead {
        from_us,
        rate,
        channels: channels as usize,
        placed: 0,
        total,
        then: Some(chunk),
    };
    let first = filling.next();
    *lead = Some(filling);
    first
}

/// Audio thread: decode, drop priming, feed the ring, drive the clock.
pub fn run_audio(px: &Arc<PipelineShared>, rx: &Receiver<MediaMsg>) {
    let mut decoder: Option<Box<dyn AudioDecoder>> = None;
    let mut producer: Option<AudioProducer> = None;
    let mut pending: Option<Pending> = None;
    // Where this generation's audio starts: where the source's sound
    // begins, which removes encoder priming, or the floor of a seek that
    // landed ahead of its target, which removes the lead-in kept to warm
    // the decoder.
    let audio_start_us = px.audio_start_us.load(Ordering::Relaxed).max(0);
    let audio_end_us = px.audio_end_us.load(Ordering::Relaxed);
    let mut origin_us = audio_start_us;
    // Where this generation's timeline starts, while the source's sound
    // begins after it and nothing has been placed yet. The gap goes into
    // the ring as silence, so the audio clock starts with the timeline
    // rather than at the sound, and a picture already showing is not
    // hurried to catch up with it.
    let mut lead_from = lead_start(audio_start_us, 0);
    let mut lead: Option<Lead> = None;
    let mut generation = {
        let bank = px.bank.bank.lock().expect("bank lock");
        bank.generation()
    };
    let live = {
        let bank = px.bank.bank.lock().expect("bank lock");
        bank.config().liveness == media_bank::Liveness::Live
    };
    let mut last_master = Master::Wall;
    let mut last_correction = Correction::None;
    let mut current_audio: Option<(media_demux::AudioCodec, u32, u32, Vec<u8>)> = None;
    let mut pending_au: Option<Au> = None;
    // Wall time a chunk first stuck against a full ring with no consumer
    // progress; the discard grace window measures from here.
    let mut park_since: Option<MediaTime> = None;
    // Trims counted at the last AudioTrim event, and when it fired. The
    // event is rate-limited, since steady trimming would otherwise flood
    // the bounded event queue.
    let mut trimmed_reported = 0u64;
    let mut last_trim_event = MediaTime::from_secs(-3600);
    let mut draining = false;
    let mut decoder_dry = false;
    let mut eos_undecoded = false;

    loop {
        if px.stopping() {
            return;
        }

        // Stale-timeline work is dropped while a seek's Flush is still
        // queued (see run_video). A parked AU here would block the intake
        // that delivers the Flush, and once the clock restarts on the new
        // timeline a ring not yet swapped would briefly play the old audio.
        let flush_pending = Generation(px.shared.generation.load(Ordering::Relaxed)) != generation;
        if flush_pending {
            pending = None;
            lead = None;
            pending_au = None;
        }

        // 1. Move pending PCM into the ring. A full ring is backpressure
        //    while the consumer pulls, and briefly at startup before its
        //    first pull. A chunk stuck against one is discarded once the
        //    consumer has been inert for the liveness window, so it cannot
        //    stall the pipeline and a headless session cannot deadlock
        //    behind a consumer that never pulls. The rule applies in every
        //    state and is the same one the Ended logic uses.
        if let (Some(chunk), Some(out)) = (pending.as_mut(), producer.as_mut()) {
            let rate = out.sample_rate().max(1);
            let channels = out.channels().max(1) as usize;
            // Sound past where the track stops (encoder padding, or a tail
            // the container trims) is not played.
            let frames = (chunk.data.len() - chunk.offset) / channels;
            let kept = frames_before_end(chunk.pts_us, audio_end_us, frames, rate);
            chunk.data.truncate(chunk.offset + kept * channels);
            let remaining = &chunk.data[chunk.offset..];
            if !remaining.is_empty() {
                let frames_left = remaining.len() / channels;
                // Drop what still precedes the origin: encoder priming,
                // which carries a negative pts, and after a seek the audio
                // ahead of where the generation starts. Silence standing in
                // for a gap before the sound is placed as it is.
                let drop_frames = if chunk.silence {
                    0
                } else {
                    frames_before_origin(chunk.pts_us.saturating_sub(origin_us), frames_left, rate)
                };
                if drop_frames > 0 {
                    chunk.offset += drop_frames * channels;
                    chunk.pts_us += drop_frames as i64 * 1_000_000 / i64::from(rate);
                } else {
                    let written = out.push(chunk.pts_us, remaining);
                    chunk.offset += written;
                    chunk.pts_us += (written / channels) as i64 * 1_000_000 / i64::from(rate);
                    if written == 0 {
                        let wall = px.wall.now();
                        let live_consumer = consumer_live(
                            wall,
                            px.audio_shared.last_pull_wall_us.load(Ordering::Relaxed),
                        );
                        if live_consumer {
                            park_since = None;
                        } else if wall - *park_since.get_or_insert(wall) > AUDIO_LIVENESS {
                            px.diag
                                .stage(Stage::AudioRing)
                                .drops
                                .fetch_add(1, Ordering::Relaxed);
                            pending = None;
                            lead = None;
                        }
                    } else {
                        park_since = None;
                    }
                }
            }
            if pending.as_ref().is_some_and(|c| c.offset >= c.data.len()) {
                pending = lead.as_mut().map(Lead::next);
                if lead.as_ref().is_some_and(Lead::placed) {
                    lead = None;
                }
            }
        }

        // 2. Pull decoder output when the previous chunk is fully placed.
        if !flush_pending
            && pending.is_none()
            && let Some(active) = decoder.as_mut()
        {
            match active.try_output() {
                Ok(Some(chunk)) => {
                    pending = Some(accept_chunk(
                        px,
                        &mut producer,
                        generation,
                        chunk,
                        lead_from.take().map(|from| (from, origin_us)),
                        &mut lead,
                    ));
                }
                Ok(None) => {}
                Err(e) => {
                    px.fail(EngineError::decode(e));
                    return;
                }
            }
        }

        // 2b. Audio-only sessions and live sessions start the parked clock
        //     at the first banked PCM's pts.
        if !px.video_active.load(Ordering::Relaxed) || live {
            let ringing = producer.as_ref().is_some_and(|p| !p.is_drained());
            let state = px.state();
            if ringing
                && state == State::Buffering as u32
                && !px
                    .bank
                    .bank
                    .lock()
                    .expect("bank lock")
                    .holding(px.wall.now())
            {
                let wall = px.wall.now();
                let mut clock = px.clock.lock().expect("clock lock");
                // Generation gate as in run_video's restart. Across a seek
                // the old ring and its base pts are stale until this thread
                // processes the Flush, so a parked clock of another
                // generation stays parked and the state stays Buffering.
                let start = !clock.is_playing() && clock.generation() == generation;
                let hold = px.pause_wanted.load(Ordering::Relaxed);
                if start {
                    let base = px.audio_shared.base_pts_us.load(Ordering::Relaxed);
                    // Latency-back anchor as in run_video's restart.
                    let latency = MediaTime::from_micros(
                        px.audio_shared.output_latency_us.load(Ordering::Relaxed),
                    );
                    clock.discontinuity(wall, MediaTime::from_micros(base) - latency);
                    if !hold {
                        clock.set_playing(wall, true);
                        px.clock_playing.store(true, Ordering::Relaxed);
                        px.present.mirror_clock(wall, clock.now(wall), true);
                    }
                }
                let playing = clock.is_playing();
                drop(clock);
                if hold {
                    // With nothing presenting, the ring standing ready at
                    // the landed position is the landing. This runs on
                    // every tick, not only the one that anchors the clock,
                    // because a pause arriving after `hold` was read finds
                    // the clock running.
                    px.settle_pause(Some(generation));
                } else if playing {
                    px.leave_buffering(generation.0);
                }
            } else if state == State::Playing as u32
                && !px.video_active.load(Ordering::Relaxed)
                && px.pause_wanted.load(Ordering::Relaxed)
            {
                // This thread's own Buffering → Playing can slip past a
                // pause request, and with no picture to land on, only the
                // ring can vouch for the generation.
                px.settle_pause(Some(generation));
            }
        }

        // 3. Clock: audio is master while the consumer is pulling.
        {
            let wall = px.wall.now();
            let playhead = px.audio_shared.playhead(wall);
            // Diagnostic A/V offset. Both terms are read on one tick, so the
            // figure is a real difference, not two samples a frame apart. It
            // uses the presented pts, never the session position, which is
            // the clock's and would just repeat the clock's own error.
            px.shared.av_offset_us.store(
                av_offset_us(
                    playhead,
                    // Per generation: after a seek the old video position
                    // must never pair with the new audio playhead.
                    presented_this_generation(
                        px.presented_generation.load(Ordering::Relaxed),
                        px.shared.generation.load(Ordering::Relaxed),
                    ),
                    px.presented_pts_us.load(Ordering::Relaxed),
                ),
                Ordering::Relaxed,
            );
            // The capture takes the same value as the ABI snapshot.
            px.diag
                .set_av_offset(px.shared.av_offset_us.load(Ordering::Relaxed));
            let pulling = consumer_live(
                wall,
                px.audio_shared.last_pull_wall_us.load(Ordering::Relaxed),
            );
            let consumer_live = playhead.is_some() && pulling;
            let mut clock = px.clock.lock().expect("clock lock");
            // Close an expired fast-slew window here rather than waiting for
            // the next master observation. The rate persists between them, so
            // a master that goes quiet just after a join would otherwise keep
            // the wide ceiling indefinitely.
            clock.enforce_slew_ceiling(wall);
            // Mirror the clock for the pull path's serve trim, since the pull
            // must never take this lock. MIN while parked disables the trim
            // across startup, seeks and join holds.
            if clock.is_playing() {
                px.audio_shared
                    .clock_now_us
                    .store(clock.now(wall).as_micros(), Ordering::Relaxed);
                px.audio_shared
                    .clock_wall_us
                    .store(wall.as_micros(), Ordering::Relaxed);
            } else {
                px.audio_shared
                    .clock_now_us
                    .store(i64::MIN, Ordering::Relaxed);
            }
            let master = if consumer_live {
                Master::Audio
            } else {
                Master::Wall
            };
            if master != last_master {
                clock.set_master(wall, master);
                last_master = master;
            }
            if master == Master::Audio
                && let Some(playhead) = playhead
            {
                let correction = clock.observe_master(wall, playhead);
                drop(clock);
                match correction {
                    Correction::Slew { rate_ppm }
                        if !matches!(last_correction, Correction::Slew { .. }) =>
                    {
                        px.diag.event(
                            wall,
                            EventCode::SlewCorrection,
                            Stage::Clock,
                            format!("{rate_ppm} ppm towards audio master"),
                        );
                    }
                    Correction::Snap { error } => {
                        px.diag.event(
                            wall,
                            EventCode::SnapCorrection,
                            Stage::Clock,
                            format!("snap {error} to audio master"),
                        );
                    }
                    _ => {}
                }
                last_correction = correction;
            }
        }

        // Position is the clock's, whatever is on screen: captions, SEI user
        // data and shared playback are timed against it, and a stalled
        // picture must not stall them. A parked clock reads where it was
        // parked, so a pause or a seek landing holds position there. Once
        // the session has ended it keeps its last reading. Under `transport`,
        // as `end_timeline` is: a reading taken before the end must not land
        // after it.
        {
            let _transport = px.transport.lock().expect("transport lock");
            let state = px.state();
            if state != State::Ended as u32 && state != State::Error as u32 {
                px.shared
                    .position_us
                    .store(px.clock_position(), Ordering::Relaxed);
            }
        }

        // 4. Occupancy and flow counters for diagnostics, and the serve-trim
        // event: trims run on the pull path, which must not touch the event
        // lock, so this thread reports them.
        {
            let ring_stage = px.diag.stage(Stage::AudioRing);
            ring_stage.occupancy.store(
                producer
                    .as_ref()
                    .map(|p| p.free_frames() as u64)
                    .unwrap_or(0),
                Ordering::Relaxed,
            );
            ring_stage.in_count.store(
                px.audio_shared.pushed_frames.load(Ordering::Relaxed),
                Ordering::Relaxed,
            );
            ring_stage.out_count.store(
                px.audio_shared.consumed_frames.load(Ordering::Relaxed),
                Ordering::Relaxed,
            );
            // The session total, not the generation's. A seek resets the
            // per-generation counter, which would make the capture column fall,
            // and `trimmed_reported` is a high-water mark kept across
            // generations, so a fallen counter would also silence the event.
            let trimmed = px.audio_shared.trimmed_frames_total.load(Ordering::Relaxed);
            px.diag.set_audio_trimmed(trimmed);
            let wall = px.wall.now();
            if trimmed > trimmed_reported && wall - last_trim_event >= MediaTime::from_secs(5) {
                px.diag.event(
                    wall,
                    EventCode::AudioTrim,
                    Stage::AudioRing,
                    format!("serve trimmed {trimmed} frames (source outruns its pts timeline)"),
                );
                trimmed_reported = trimmed;
                last_trim_event = wall;
            }
        }

        // 4b. Retry a parked AU before taking anything new. While one is
        //     parked the decoder is full and steps 1–2 make space, so
        //     nothing new comes off the channel (as on the video thread).
        if let Some(au) = pending_au.take()
            && let Some(active) = decoder.as_mut()
        {
            match active.submit(&au.data, au.pts.as_micros()) {
                Ok(SubmitOutcome::Accepted) => {}
                Ok(SubmitOutcome::NotAccepting) => pending_au = Some(au),
                Err(e) => {
                    px.fail(EngineError::decode(e));
                    return;
                }
            }
        }
        if pending_au.is_some() {
            std::thread::sleep(DECODE_TICK);
            continue;
        }

        // If the ring is full and a chunk is stuck, wait a beat off the
        // channel so the consumer can drain.
        let wait = if pending.is_some() {
            Duration::from_millis(10)
        } else {
            DECODE_TICK
        };

        // 5. Take the next message.
        match rx.recv_timeout(wait) {
            Ok(MediaMsg::Format(Format::Audio {
                codec,
                sample_rate,
                channels,
                codec_private,
            })) => {
                let announced = (codec, sample_rate, channels, codec_private);
                if decoder.is_some() && current_audio.as_ref() == Some(&announced) {
                    // Unchanged format on a live reconnect: keep the ring.
                    continue;
                }
                let (codec, sample_rate, channels, codec_private) = announced;
                current_audio = Some((codec, sample_rate, channels, codec_private.clone()));
                match open_audio_decoder(codec, sample_rate, channels, &codec_private) {
                    Ok(d) => {
                        let (out_rate, out_channels) = d.output_format();
                        decoder = Some(d);
                        px.playable.decoding(TrackKind::Audio);
                        producer = Some(install_audio_generation(
                            &px.audio_consumer,
                            AudioFormatInfo {
                                sample_rate: out_rate,
                                channels: out_channels,
                            },
                            Arc::clone(&px.audio_shared),
                        ));
                        px.audio_ring_generation
                            .store(generation.0, Ordering::Release);
                        px.shared.audio_rate.store(out_rate, Ordering::Relaxed);
                        px.shared
                            .audio_channels
                            .store(out_channels, Ordering::Relaxed);
                    }
                    Err(e) => {
                        // Refused audio mutes and video is unaffected. With
                        // no video either, the session fails.
                        px.refuse(
                            TrackKind::Audio,
                            format!("{codec:?} audio refused: {}", e.0),
                        );
                        decoder = None;
                        // The flush arm installs a fresh ring only where
                        // there is a decoder to size it from, so a producer
                        // left over from the previous format would outlive
                        // its timeline.
                        producer = None;
                        // The consumer half goes too, as in the flush arm's
                        // no-decoder case. Left installed it would serve the
                        // retired format's tail while the drain check, seeing
                        // no producer, reads the track as finished.
                        *px.audio_consumer.lock().unwrap_or_else(|e| e.into_inner()) = None;
                    }
                }
            }
            Ok(MediaMsg::Format(_)) => {}
            Ok(MediaMsg::Au(au)) => {
                if au.generation != generation || flush_pending {
                    continue;
                }
                px.diag
                    .stage(Stage::Decode)
                    .in_count
                    .fetch_add(1, Ordering::Relaxed);
                if decoder.is_some() {
                    pending_au = Some(au);
                }
            }
            Ok(MediaMsg::Flush { generation: new }) => {
                generation = new;
                origin_us = px.seek_floor_us.load(Ordering::Relaxed).max(audio_start_us);
                lead_from = lead_start(
                    audio_start_us,
                    px.generation_start_us.load(Ordering::Relaxed),
                );
                lead = None;
                pending = None;
                pending_au = None;
                park_since = None;
                draining = false;
                decoder_dry = false;
                eos_undecoded = false;
                if let Some(active) = decoder.as_mut()
                    && let Err(e) = active.reset()
                {
                    px.fail(EngineError::decode(e));
                    return;
                }
                // Fresh ring for the new timeline, reset and swapped under
                // the slot lock so a pull in flight cannot undo it.
                if let Some(active) = decoder.as_ref() {
                    let (out_rate, out_channels) = active.output_format();
                    producer = Some(install_audio_generation(
                        &px.audio_consumer,
                        AudioFormatInfo {
                            sample_rate: out_rate,
                            channels: out_channels,
                        },
                        Arc::clone(&px.audio_shared),
                    ));
                    px.audio_ring_generation.store(new.0, Ordering::Release);
                } else {
                    // No decoder to size a ring from, so no swap retires the
                    // old consumer and it would keep serving samples and a
                    // base pts from the timeline just left, against a clock
                    // about to restart on the new one. Retire it here. The
                    // track is muted, so nothing audible is lost.
                    *px.audio_consumer.lock().unwrap_or_else(|e| e.into_inner()) = None;
                }
            }
            Ok(MediaMsg::Eos) => {
                match decoder.as_mut() {
                    Some(active) => {
                        if let Err(e) = active.begin_drain() {
                            px.fail(EngineError::decode(e));
                            return;
                        }
                        draining = true;
                    }
                    // No decoder was built for the track, so there is no
                    // tail to wait for. Publishing that stops a refused
                    // audio track holding a video session open for ever.
                    None => {
                        px.audio_tail_out.store(generation.0, Ordering::Relaxed);
                        eos_undecoded = !px.video_active.load(Ordering::Relaxed);
                    }
                }
            }
            Err(RecvTimeoutError::Timeout) => {}
            Err(RecvTimeoutError::Disconnected) => return,
        }

        // Drained dry after EOS with the ring consumed: an audio-only
        // session ends here, where the last sample's consumption is
        // visible. Ending on the video thread's EOS would cut up to the
        // ring's full depth. A consumer that stopped pulling does not hold
        // the session open.
        if !flush_pending
            && draining
            && pending.is_none()
            && let Some(active) = decoder.as_mut()
        {
            match active.try_output() {
                Ok(Some(chunk)) => {
                    pending = Some(accept_chunk(
                        px,
                        &mut producer,
                        generation,
                        chunk,
                        lead_from.take().map(|from| (from, origin_us)),
                        &mut lead,
                    ));
                }
                Ok(None) => {
                    // Async adapters bound their own drain wait; keep
                    // polling until they report dry (see run_video).
                    if active.drain_dry() {
                        draining = false;
                        decoder_dry = true;
                    }
                }
                Err(e) => {
                    px.fail(EngineError::decode(e));
                    return;
                }
            }
        }
        if decoder_dry && pending.is_none() && px.state() == State::Playing as u32 {
            let wall = px.wall.now();
            let consumer_live = consumer_live(
                wall,
                px.audio_shared.last_pull_wall_us.load(Ordering::Relaxed),
            );
            let ring_drained = producer.as_ref().is_none_or(|p| p.is_drained());
            if ring_drained || !consumer_live {
                // Published before ending, so the video thread's own end
                // condition and this one agree on what "played out" means.
                px.audio_tail_out.store(generation.0, Ordering::Relaxed);
                // As on the video thread: a pause or seek since the check
                // above keeps the session, and the end is asked again.
                if px.video_active.load(Ordering::Relaxed)
                    || px.end_timeline(generation, &[State::Playing])
                {
                    decoder_dry = false;
                }
            }
        }
        if eos_undecoded && px.end_timeline(generation, &[State::Buffering, State::Playing]) {
            eos_undecoded = false;
        }
    }
}

#[cfg(test)]
mod user_data_ring_tests {
    use super::*;
    use media_bitstream::SeiUserData;

    fn message(pts_us: i64, len: usize) -> SeiUserData {
        SeiUserData {
            pts_us,
            uuid: [0; 16],
            payload: vec![0xAB; len],
        }
    }

    #[test]
    fn drain_takes_whole_messages_that_fit_and_leaves_the_rest() {
        let mut ring = UserDataRing::default();
        for i in 0..4 {
            ring.push(message(i, 100));
        }
        let first = ring.drain(10, 250);
        assert_eq!(first.len(), 2, "a third would pass the byte bound");
        assert_eq!(ring.len(), 2);
        let second = ring.drain(1, 1000);
        assert_eq!(second.len(), 1, "the count bound holds too");
        assert_eq!(second[0].pts_us, 2);
        assert!(ring.drain(10, 1000).len() == 1 && ring.is_empty());
    }

    #[test]
    fn a_message_that_can_never_fit_is_dropped_not_left_blocking() {
        let mut ring = UserDataRing::default();
        ring.push(message(0, 5000));
        ring.push(message(1, 10));
        let got = ring.drain(10, 1000);
        assert_eq!(got.len(), 1);
        assert_eq!(got[0].pts_us, 1);
        assert!(ring.is_empty());
    }

    #[test]
    fn both_ring_bounds_drop_oldest() {
        let mut ring = UserDataRing::default();
        for i in 0..(USER_DATA_RING as i64 + 5) {
            ring.push(message(i, 1));
        }
        assert_eq!(ring.len(), USER_DATA_RING);
        assert_eq!(ring.drain(1, 1).remove(0).pts_us, 5);

        let mut ring = UserDataRing::default();
        let per = USER_DATA_PAYLOAD_CAP;
        let fits = USER_DATA_RING_BYTES / per;
        for i in 0..(fits as i64 + 2) {
            ring.push(message(i, per));
        }
        assert_eq!(ring.len(), fits);
        assert_eq!(ring.bytes, fits * per);
        assert_eq!(ring.drain(1, per).remove(0).pts_us, 2);
        // Over the per-message cap is refused outright.
        ring.push(message(99, per + 1));
        assert_eq!(ring.len(), fits - 1);
    }

    #[test]
    fn a_new_epoch_clears_the_old_pass_even_where_timestamps_overlap() {
        // The old pass's opening messages sit at the same timestamps a
        // loop restarts into, so the clear is unconditional rather than
        // a pts comparison.
        let mut ring = UserDataRing::default();
        ring.push(message(0, 100));
        ring.push(message(500_000, 100));
        ring.push(message(10_000_000, 100));
        let restart = message(0, 7);
        ring.clear();
        ring.push(restart);
        assert_eq!(ring.len(), 1);
        assert_eq!(ring.bytes, 7);
        assert_eq!(ring.drain(1, 7)[0].pts_us, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_last_frame_ends_at_the_duration_or_after_its_gap() {
        let us = MediaTime::from_micros;
        // 30 fps, 12 s: the last frame starts at 11.966667 s.
        let last = us(11_966_667);
        let previous = Some(us(11_933_333));
        assert_eq!(last_frame_end(last, previous, 12_000_000), us(12_000_000));
        assert_eq!(last_frame_end(last, None, 12_000_000), us(12_000_000));
        // No duration (a TS file), or one that ends the frame no later than
        // it starts: the gap to the frame before.
        assert_eq!(last_frame_end(last, previous, 0), us(12_000_001));
        assert_eq!(last_frame_end(last, previous, 11_966_667), us(12_000_001));
        // The hold, exact on both sides, for the duration and for the gap.
        assert_eq!(
            last_frame_end(us(5_000_000), None, 6_000_000),
            us(6_000_000)
        );
        assert_eq!(
            last_frame_end(us(5_000_000), None, 6_000_001),
            us(5_000_000)
        );
        assert_eq!(
            last_frame_end(us(5_000_000), Some(us(4_000_000)), 0),
            us(6_000_000)
        );
        assert_eq!(
            last_frame_end(us(5_000_000), Some(us(3_999_999)), 0),
            us(5_000_000)
        );
        // A duration past the hold falls back to the gap.
        assert_eq!(last_frame_end(last, previous, 14_000_000), us(12_000_001));
        // One frame and no duration, or frames out of order: its own pts.
        assert_eq!(last_frame_end(last, None, 0), last);
        assert_eq!(last_frame_end(last, Some(us(12_000_000)), 0), last);
    }

    /// A lead-in places the gap as silence a block at a time, then the
    /// sound, and is spent once the sound has gone out. A gap that rounds
    /// to no frames places nothing and leaves no lead-in behind, since one
    /// with no sound left to hand over cannot be asked for its next chunk.
    #[test]
    fn a_lead_in_places_the_gap_then_the_sound() {
        let sound = || Pending::sound(1_500_000, vec![1.0; 4]);
        let mut lead = None;
        // 1.5 s at 48 kHz stereo: a second of silence, half a second, the
        // sound.
        let first = begin_chunk(sound(), 48_000, 2, Some((0, 1_500_000)), &mut lead);
        assert!(first.silence);
        assert_eq!((first.pts_us, first.data.len()), (0, 96_000));
        let filling = lead.as_mut().expect("a gap to fill");
        let second = filling.next();
        assert!(second.silence);
        assert_eq!((second.pts_us, second.data.len()), (1_000_000, 48_000));
        let third = filling.next();
        assert!(!third.silence);
        assert_eq!(third.pts_us, 1_500_000);
        assert!(filling.placed());

        // 10 us is under half a frame at 48 kHz.
        let mut lead = None;
        let chunk = begin_chunk(sound(), 48_000, 2, Some((1_499_990, 1_500_000)), &mut lead);
        assert!(!chunk.silence);
        assert!(lead.is_none(), "a lead-in with nothing to place was kept");
    }

    /// Every frame the decoder hands back meets the same gate, whichever
    /// site took it.
    #[test]
    fn the_unseen_span_ends_at_the_first_frame_to_reach_the_floor() {
        let mut span = UnseenSpan::default();
        assert_eq!(span.admit(0), Admit::Show, "no span is open by default");

        span.arm(3_200_000);
        assert_eq!(span.admit(2_000_000), Admit::Unseen);
        assert_eq!(span.admit(3_166_666), Admit::Unseen);
        assert_eq!(span.admit(3_200_000), Admit::Reached { dropped: 2 });
        assert_eq!(
            span.admit(3_100_000),
            Admit::Show,
            "once reached the span is over, whatever follows"
        );

        span.arm(5_000_000);
        assert_eq!(span.admit(4_000_000), Admit::Unseen);
        span.arm(NO_FLOOR);
        assert_eq!(
            span.admit(4_000_000),
            Admit::Show,
            "a seek that takes no floor closes the span the last one left open"
        );
    }

    /// Frames each later than the last are a decoder losing ground, and a
    /// run of them is acted on.
    #[test]
    fn a_decoder_losing_ground_is_noticed_after_a_run_of_late_frames() {
        let mut falling = FallingBehind::default();
        let mut noticed = None;
        for n in 0..10 {
            if falling.observe(MediaTime::from_millis(60 + 22 * n)) {
                noticed = Some(n + 1);
                break;
            }
        }
        assert_eq!(noticed, Some(i64::from(LATE_FRAMES_BEFORE_SKIP)));
    }

    /// A decoder recovering from a stall is late but catching up, and is
    /// left alone however late it starts.
    #[test]
    fn a_decoder_catching_up_is_left_alone() {
        let mut falling = FallingBehind::default();
        for n in 0..30 {
            assert!(!falling.observe(MediaTime::from_millis(900 - 30 * n)));
        }
    }

    /// Lateness inside what may still be shown is not falling behind,
    /// whichever way it is heading, and it breaks a run.
    #[test]
    fn frames_that_can_still_be_shown_are_not_falling_behind() {
        let mut falling = FallingBehind::default();
        for n in 0..30 {
            assert!(!falling.observe(MediaTime::from_millis(n)));
        }
        let mut falling = FallingBehind::default();
        for n in 0..i64::from(LATE_FRAMES_BEFORE_SKIP) - 1 {
            assert!(!falling.observe(MediaTime::from_millis(60 + 22 * n)));
        }
        assert!(!falling.observe(MediaTime::from_millis(10)));
        assert!(!falling.observe(MediaTime::from_millis(200)));
    }

    /// A seek decodes forward to its target only while that is cheap
    /// enough to do unseen.
    #[test]
    fn the_floor_is_the_target_only_within_the_bound() {
        let at = MediaTime::from_millis;
        assert_eq!(
            presentation_floor(at(3_200), at(2_000), None),
            Some(at(3_200))
        );
        assert_eq!(
            presentation_floor(at(14_000), at(2_000), None),
            Some(at(14_000)),
            "the bound itself is inside it"
        );
        assert_eq!(
            presentation_floor(at(14_001), at(2_000), None),
            None,
            "past the bound the seek presents from its keyframe"
        );
        assert_eq!(presentation_floor(at(4_000), at(4_000), None), None);
        assert_eq!(
            presentation_floor(at(4_000), at(4_500), None),
            None,
            "a demuxer that lands late has nothing ahead of the target to skip"
        );
    }

    /// The step is the typical one between consecutive frames, not across a
    /// seek's jump, and a new video Format forgets the old track's rate.
    #[test]
    fn the_frame_step_follows_the_current_track() {
        let at = MediaTime::from_millis;
        let mut steps = FrameStep::default();
        assert_eq!(steps.step(), None);
        for n in 0..3 {
            steps.observe(at(n * 1_000));
        }
        assert_eq!(steps.step(), Some(at(1_000)), "a 1 fps track");
        steps.seeked();
        steps.observe(at(200));
        assert_eq!(steps.step(), Some(at(1_000)), "a jump back is no step");
        steps.seeked();
        steps.observe(at(90_000));
        assert_eq!(steps.step(), Some(at(1_000)), "nor is a jump forward");
        steps.new_format();
        assert_eq!(
            steps.step(),
            None,
            "a replacement track does not inherit the old rate"
        );
        steps.observe(at(90_000));
        steps.observe(MediaTime::from_micros(90_033_333));
        assert_eq!(steps.step(), Some(MediaTime::from_micros(33_333)));
    }

    /// One pair of frames close together in a 4 fps track does not set the
    /// step, and the step follows the track's latest cadence.
    #[test]
    fn an_odd_short_step_does_not_set_the_frame_step() {
        let at = MediaTime::from_millis;
        let mut steps = FrameStep::default();
        for n in 0..8 {
            steps.observe(at(n * 250));
        }
        steps.observe(at(7 * 250 + 1));
        for n in 9..16 {
            steps.observe(at(n * 250));
        }
        assert_eq!(steps.step(), Some(at(250)));
        for n in 0..=FRAME_STEPS_KEPT as i64 {
            steps.observe(at(4_000 + n * 40));
        }
        assert_eq!(steps.step(), Some(at(40)), "the track changed pace");
    }

    /// The bound is 720 frames of the track's own rate and never under 12 s,
    /// so a low-rate file decodes forward much further than a 60 fps one.
    #[test]
    fn the_bound_counts_frames() {
        let at = MediaTime::from_millis;
        let eight_fps = Some(at(125));
        assert_eq!(
            presentation_floor(at(90_000), at(0), eight_fps),
            Some(at(90_000)),
            "720 frames at 8 fps is 90 s"
        );
        assert_eq!(presentation_floor(at(90_001), at(0), eight_fps), None);
        let sixty_fps = Some(MediaTime::from_micros(16_667));
        assert_eq!(
            presentation_floor(at(12_000), at(0), sixty_fps),
            Some(at(12_000))
        );
        assert_eq!(
            presentation_floor(MediaTime::from_micros(12_000_241), at(0), sixty_fps),
            None,
            "720 frames at 60 fps is 12.000240 s"
        );
        assert_eq!(
            presentation_floor(at(12_000), at(0), Some(MediaTime::from_micros(1))),
            Some(at(12_000)),
            "a tiny step never shrinks the bound below 12 s"
        );
        assert_eq!(
            presentation_floor(at(12_001), at(0), Some(MediaTime::from_micros(1))),
            None
        );
    }

    /// The never-pulled sentinel is `i64::MIN`, and `MediaTime`'s `Sub` is a
    /// plain subtraction, so subtracting it from any positive wall clock
    /// would panic the audio thread.
    #[test]
    fn a_consumer_that_never_pulled_is_not_live_and_does_not_overflow() {
        let wall = MediaTime::from_millis(10_000);
        assert!(!consumer_live(wall, i64::MIN), "never pulled is not live");
    }

    /// The offset is a difference between two terms of the same generation,
    /// or unknown. The window after a flush must not export the old video
    /// position against the new audio playhead as a measurement.
    #[test]
    fn the_av_offset_is_unknown_until_this_generation_presents() {
        let ph = Some(MediaTime::from_millis(1_000));
        assert_eq!(
            av_offset_us(ph, false, 5_000_000),
            i32::MIN,
            "nothing presented this generation: the offset is not knowable"
        );
        assert_eq!(av_offset_us(None, true, 5_000_000), i32::MIN, "no playhead");
        assert_eq!(av_offset_us(ph, true, 1_020_000), 20_000);
    }

    /// The gate answers for the timeline in force, and only that one.
    ///
    /// A render event in flight across a flush still carries a lease from
    /// the retired timeline. The recorded value is the generation that
    /// presented, so such a write names the old timeline and the gate does
    /// not match it.
    #[test]
    fn the_gate_answers_only_for_the_timeline_in_force() {
        assert!(
            !presented_this_generation(NO_GENERATION, 0),
            "nothing has presented yet"
        );
        assert!(presented_this_generation(7, 7), "this timeline presented");
        assert!(
            !presented_this_generation(7, 8),
            "a frame from the retired timeline armed the new one"
        );
        assert!(
            !presented_this_generation(8, 7),
            "a generation that has not been reached yet cannot answer either"
        );
    }

    /// Buffering ends at a presentation of the timeline in force only. A
    /// seek advances the generation before the video thread clears the
    /// pool, so a render event can still present the retired timeline's
    /// due frame; that frame reports the old picture, not the landing.
    #[test]
    fn buffering_ends_only_for_the_timeline_in_force() {
        assert!(buffering_ends(7, 7, 0, false), "this timeline presented");
        assert!(
            !buffering_ends(7, 8, 0, false),
            "a frame from the retired timeline ended the new one's Buffering"
        );
        assert!(
            !buffering_ends(7, 7, 1, false),
            "a seek still in the demuxer let the timeline being left end Buffering"
        );
        assert!(
            !buffering_ends(7, 7, 0, true),
            "a pause waiting on the landing keeps Buffering for settle_pause"
        );
    }

    /// The pull serves in Playing, and in Buffering once the clock runs,
    /// only on the timeline in force with nothing waiting to land on it.
    #[test]
    fn audio_serves_only_for_the_running_timeline() {
        let playing = State::Playing as u32;
        let buffering = State::Buffering as u32;
        assert!(audio_serves(playing, true, false, 0, 7, 7), "playing");
        assert!(
            audio_serves(buffering, true, false, 0, 7, 7),
            "the clock runs ahead of the first picture"
        );
        assert!(
            !audio_serves(State::Paused as u32, true, false, 0, 7, 7),
            "paused"
        );
        for (state, name) in [(playing, "Playing"), (buffering, "Buffering")] {
            assert!(
                !audio_serves(state, false, false, 0, 7, 7),
                "{name}: a parked clock serves nothing"
            );
            assert!(
                !audio_serves(state, true, true, 0, 7, 7),
                "{name}: a pause waiting on the landing is heard before it lands"
            );
            assert!(
                !audio_serves(state, true, false, 1, 7, 7),
                "{name}: a queued seek's clock still runs on the timeline being left"
            );
            assert!(
                !audio_serves(state, true, false, 0, 7, 8),
                "{name}: the retired timeline's ring served the new one"
            );
        }
    }

    /// A thread that decided on a transition from an earlier read loses to
    /// whatever another thread has published since. Each case stores the
    /// competing state first, standing in for the thread that got there
    /// between the read and the claim.
    #[test]
    fn a_state_published_since_the_read_wins_over_the_claim() {
        use std::sync::atomic::AtomicU32;
        let cases = [
            (
                State::Paused,
                &[State::Playing][..],
                State::Ended,
                "a pause lost to the end",
            ),
            (
                State::Buffering,
                &[State::Playing],
                State::Ended,
                "a seek lost to the end",
            ),
            (
                State::Error,
                &[State::Playing],
                State::Ended,
                "a failure lost to the end",
            ),
            (
                State::Error,
                &[State::Buffering, State::Playing],
                State::Ended,
                "a failure lost to an end with no track",
            ),
            (
                State::Error,
                &[State::Paused],
                State::Playing,
                "a failure lost to a play",
            ),
            (
                State::Ended,
                &[State::Playing, State::Buffering],
                State::Paused,
                "an end lost to a pause",
            ),
            (
                State::Error,
                &[State::Playing, State::Buffering],
                State::Paused,
                "a failure lost to a pause",
            ),
        ];
        for (published, from, to, what) in cases {
            let state = AtomicU32::new(published as u32);
            assert!(!claim(&state, from, to), "{what}");
            assert_eq!(state.load(Ordering::Relaxed), published as u32, "{what}");
        }

        let state = AtomicU32::new(State::Playing as u32);
        assert!(claim(&state, &[State::Playing], State::Ended));
        assert_eq!(state.load(Ordering::Relaxed), State::Ended as u32);
        let state = AtomicU32::new(State::Buffering as u32);
        assert!(claim(
            &state,
            &[State::Playing, State::Buffering],
            State::Paused
        ));
        assert_eq!(state.load(Ordering::Relaxed), State::Paused as u32);
    }

    /// An end of stream queued ahead of a seek's Flush reaches its decode
    /// thread after the seek has published Buffering. It belongs to the
    /// timeline the seek left, and must not end the new one.
    #[test]
    fn an_end_of_stream_from_the_timeline_a_seek_left_does_not_end_the_session() {
        assert!(
            !end_is_current(1, 4, 4),
            "a seek not yet taken by the demux thread was ended"
        );
        assert!(
            !end_is_current(0, 5, 4),
            "a seek whose Flush had not arrived was ended"
        );
        assert!(
            end_is_current(0, 4, 4),
            "the timeline in force reached its end"
        );
    }

    /// A flush needs no clearing step: the new generation has no matching
    /// write until it presents one of its own, and the frame that arms it
    /// is the one that names it.
    #[test]
    fn a_new_generation_starts_unarmed_and_arms_itself() {
        let presented = std::sync::atomic::AtomicU64::new(NO_GENERATION);
        let arm = |g: u64| presented.store(g, Ordering::Relaxed);
        let gate =
            |current: u64| presented_this_generation(presented.load(Ordering::Relaxed), current);
        assert!(!gate(0));
        arm(0);
        assert!(gate(0), "the first frame of generation 0 arms it");
        // A seek advances the generation. Nothing was cleared, and the gate
        // is false regardless.
        assert!(!gate(1), "the new generation inherited the old answer");
        // A late render from generation 0 lands after the seek.
        arm(0);
        assert!(!gate(1), "a stale render armed the new generation");
        arm(1);
        assert!(gate(1), "the new timeline's own frame arms it");
    }

    /// The sentinel stays reachable: a real value is clamped clear of it, so
    /// "unknown" and "a very large negative offset" never collide.
    #[test]
    fn a_real_av_offset_never_collides_with_the_sentinel() {
        let ph = Some(MediaTime::from_micros(i64::MAX / 2));
        assert!(av_offset_us(ph, true, i64::MIN / 2) > i32::MIN);
    }

    #[test]
    fn consumer_liveness_tracks_the_last_pull() {
        let wall = MediaTime::from_millis(10_000);
        let at = |ms: i64| MediaTime::from_millis(ms).as_micros();
        assert!(consumer_live(wall, at(10_000)), "pulled now");
        assert!(
            consumer_live(wall, at(9_500)),
            "exactly AUDIO_LIVENESS ago still counts — the bound is inclusive"
        );
        assert!(!consumer_live(wall, at(9_499)), "past the bound");
    }

    /// Both dts are the container's, scaled by a timescale the container
    /// also states, so their distance is unbounded. Wrapping would invert
    /// the comparison and hold a leg out of the Bank permanently.
    #[test]
    fn an_out_of_range_dts_cannot_wrap_the_cap() {
        let split = SplitLegs::new();
        split.note_origin(Leg::Video, i64::MAX / 2);
        split.note_banked(Leg::Video, i64::MAX / 2);
        split.note_origin(Leg::Audio, i64::MAX / 2);

        // Far enough ahead to wait, and far enough behind not to, both out
        // of range of the subtraction that decides it.
        assert!(split.must_wait_for_other(Leg::Audio, i64::MAX));
        assert!(!split.must_wait_for_other(Leg::Audio, i64::MIN + 1));
    }

    /// Whether a pair can play turns on how far apart its two sources'
    /// timeline origins are. That must be a distance, since either source
    /// may be the later one, and must survive origins far enough apart to
    /// overflow the subtraction, where a wrap would report a wide pair as
    /// a close one and let it wedge the Bank.
    #[test]
    fn the_origin_gap_is_a_distance_and_cannot_wrap() {
        const GAP: i64 = 1_470_000;

        let split = SplitLegs::new();
        assert_eq!(split.origin_gap_us(), None, "neither leg has a baseline");
        split.note_origin(Leg::Video, GAP);
        assert_eq!(split.origin_gap_us(), None, "only one leg has one");
        split.note_origin(Leg::Audio, 0);
        assert_eq!(split.origin_gap_us(), Some(GAP));

        let reversed = SplitLegs::new();
        reversed.note_origin(Leg::Video, 0);
        reversed.note_origin(Leg::Audio, GAP);
        assert_eq!(
            reversed.origin_gap_us(),
            Some(GAP),
            "the later leg is the audio one here"
        );

        let extreme = SplitLegs::new();
        extreme.note_origin(Leg::Video, i64::MAX);
        extreme.note_origin(Leg::Audio, i64::MIN + 1);
        assert_eq!(extreme.origin_gap_us(), Some(i64::MAX));
    }

    /// The legs observe a seek at their own pace, so the audio leg can
    /// bank one more access unit from the timeline it is leaving *after*
    /// the video leg landed the seek and rebased. Pairing that with the
    /// baseline latched at the landing makes the video leg's view of its
    /// progress span two timelines, which holds the video leg out of the
    /// Bank until the audio leg banks again.
    ///
    /// Seeking forwards is the direction that fails: the stale dts sits
    /// below the new baseline, so the progress reads negative and is
    /// subtracted from the video leg's own lead.
    #[test]
    fn a_leg_forgetting_its_origin_forgets_what_it_banked_on_it() {
        const STALE: i64 = 500_000;
        const LANDED: i64 = 30_000_000;

        let split = SplitLegs::new();
        for leg in [Leg::Video, Leg::Audio] {
            split.note_origin(leg, 0);
            split.note_banked(leg, 0);
        }

        // The video leg lands the seek and hands it over.
        split.rebase();
        split.reset_origin(Leg::Video);
        // The audio leg is still on the old timeline for one more AU,
        // and only then observes the seek.
        split.note_banked(Leg::Audio, STALE);
        split.reset_origin(Leg::Audio);

        // Both resume at the landing.
        for leg in [Leg::Video, Leg::Audio] {
            split.note_origin(leg, LANDED);
        }
        assert!(
            !split.must_wait_for_other(Leg::Video, LANDED),
            "the video leg was held by an audio dts banked on the timeline it left"
        );
    }

    /// The lead cap measures how far each leg has come from its own
    /// origin, so a seek must re-establish both origins. The two legs need
    /// not agree on where the landing sits on their own timelines (an
    /// adaptive ladder's renditions are separately muxed), and the
    /// difference would otherwise read as one leg being permanently that
    /// far ahead, holding the video leg out of the Bank while the audio
    /// leg fills it.
    #[test]
    fn a_seek_rebaselines_legs_whose_timelines_disagree() {
        const VIDEO_ORIGIN: i64 = 0;
        const AUDIO_ORIGIN: i64 = 1_470_000;
        const LANDED: i64 = 5_000_000;

        let split = SplitLegs::new();
        for (leg, origin) in [(Leg::Video, VIDEO_ORIGIN), (Leg::Audio, AUDIO_ORIGIN)] {
            split.note_origin(leg, origin);
            split.note_banked(leg, origin);
        }

        // The seek. Each leg forgets its own origin as its own demuxer
        // moves, so no pre-seek AU can latch the new baseline.
        split.rebase();
        split.reset_origin(Leg::Video);
        split.reset_origin(Leg::Audio);

        // Both resume at the landing, each on its own timeline.
        for leg in [Leg::Video, Leg::Audio] {
            split.note_origin(leg, LANDED);
            assert!(
                !split.must_wait_for_other(leg, LANDED),
                "{leg:?} was held out of the Bank at the landing it just seeked to"
            );
            split.note_banked(leg, LANDED);
        }

        // The cap applies again, measured from the landing.
        assert!(!split.must_wait_for_other(Leg::Video, LANDED + SPLIT_LEAD_CAP_US));
        assert!(split.must_wait_for_other(Leg::Video, LANDED + SPLIT_LEAD_CAP_US + 1));
    }

    /// A container may number a track anywhere in the u32 range, and an
    /// HLS leg can announce a new track when a later segment's container
    /// numbers it differently. Every track of the pair must still reach
    /// the Bank under its own id, or the release thread routes one track's
    /// AUs to another's decoder.
    #[test]
    fn split_legs_give_every_track_its_own_id_whatever_the_container_numbers() {
        let video = Format::Video {
            codec: media_demux::VideoCodec::H264,
            coded_width: 64,
            coded_height: 64,
            display_width: 64,
            display_height: 64,
            codec_private: Vec::new(),
        };
        let audio = Format::Audio {
            codec: media_demux::AudioCodec::Aac,
            sample_rate: 48_000,
            channels: 2,
            codec_private: Vec::new(),
        };
        let banked_id = |leg, tracks: &mut LegTracks, track, format: &Format| {
            let event = StreamEvent::Format(media_demux::TrackId(track), format.clone());
            match adapt_leg_event(leg, tracks, event) {
                Some(StreamEvent::Format(id, _)) => id,
                other => panic!("{leg:?} dropped its own track: {other:?}"),
            }
        };

        let mut video_leg = LegTracks::default();
        let mut audio_leg = LegTracks::default();
        let ids = [
            banked_id(Leg::Video, &mut video_leg, 1, &video),
            banked_id(Leg::Video, &mut video_leg, 0x8000_0001, &video),
            banked_id(Leg::Audio, &mut audio_leg, 1, &audio),
            banked_id(Leg::Audio, &mut audio_leg, 0x8000_0001, &audio),
        ];
        for (i, a) in ids.iter().enumerate() {
            for b in &ids[i + 1..] {
                assert_ne!(a, b, "two tracks of the pair share an id: {ids:?}");
            }
        }
        assert_eq!(
            banked_id(Leg::Video, &mut video_leg, 0x8000_0001, &video),
            ids[1],
            "a re-announced track keeps the id it was given"
        );
    }
}
