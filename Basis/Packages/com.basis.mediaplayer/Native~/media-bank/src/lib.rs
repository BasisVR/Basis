//! The AU bank + pacer: the live buffer, the VOD pacer, the HLS
//! live margin and the RTSP jitter absorber as one component.
//!
//! The Bank sits between demux and decode for every transport. It is a
//! bounded queue of `StreamEvent`s with a time cap and a byte cap; depth is
//! held upstream as compressed AUs. Live banking works by lagging release
//! behind arrival; VOD banking works by reading ahead of 1x release; both
//! share one release path that feeds no consumer faster than 1x + lead.
//!
//! The Bank is deterministic and platform-free: every method takes the wall
//! clock as a `MediaTime` argument, so tests and capture replays drive it
//! with synthetic schedules.

#![forbid(unsafe_code)]

mod auto;

use std::collections::VecDeque;
use std::fmt;

use media_clock::{Generation, MediaTime};
use media_demux::{StreamEvent, TrackId};

pub use auto::AutoConfig;
use auto::AutoDepth;

/// One configured depth per viewer; everything else derives from it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum BufferDepth {
    /// Self-sizing: the debt bound grows depth to what the link
    /// demonstrates; decay shrinks it back towards the delay-percentile
    /// target (see [`AutoConfig`]).
    Auto,
    /// Total configured depth, milliseconds, decoder cushion included.
    Millis(u32),
}

/// Stated by the resolver, never inferred.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Liveness {
    /// 1x source: depth is held by lagging release behind arrival, and the
    /// debt bound keeps the schedule maintained across delivery gaps.
    Live,
    /// Seekable source read ahead of 1x: the bank backpressures at the
    /// configured depth, and late delivery flows at decode speed (a seek
    /// preroll relies on it).
    Vod,
}

#[derive(Debug, Clone)]
pub struct BankConfig {
    pub depth: BufferDepth,
    pub liveness: Liveness,
    /// Fixed, small, covering decode/present jitter only. The depth beyond
    /// it is the delivery lag, held upstream in compressed form.
    pub decoder_cushion: MediaTime,
    /// How far ahead of the 1x schedule release may run when no lag is
    /// held: with no upstream cushion this is the decoder's only
    /// protection against scheduling jitter.
    pub pace_lead: MediaTime,
    /// The lead once a lag exists: with seconds banked upstream, a large
    /// lead earns nothing and manufactures edge jitter the cushion then has
    /// to absorb.
    pub pace_lead_with_lag: MediaTime,
    /// Ceiling on accumulated lag: the furthest behind the live edge a
    /// viewer may sit. Past it the Bank trades stutter for staying near
    /// the edge.
    pub lag_cap: MediaTime,
    /// A high-bitrate source must not blow the memory budget (16 MiB
    /// default).
    pub byte_cap: usize,
    /// Cap on banked media duration.
    pub time_cap: MediaTime,
    /// Startup holds release until the target depth is banked or this much
    /// wall time passes, so a join reads as one buffering moment.
    pub startup_timeout: MediaTime,
    /// How fast decay returns surplus lag, ppm of wall time. Matches the
    /// present clock's slew cap so the give-back can be presented smoothly
    /// (both halves or neither).
    pub decay_rate_ppm: i64,
    /// The decoder-priming allowance at every anchor, in both fill modes.
    ///
    /// VOD: the release anchor starts this far in the past, so the
    /// schedule runs a constant `startup_burst` early for the whole
    /// generation. The rate stays 1x. The early phase fills the decoder's
    /// first-output input depth at submit speed and keeps it filled; the
    /// decoder's own appetite (NotAccepting plus channel backpressure)
    /// bounds what is actually in flight.
    ///
    /// Live: during the startup hold, release runs ahead of the 1x line
    /// from the first arrival by at most this much, so the decoder primes
    /// while the hold fills and a join costs roughly max(hold, decoder
    /// priming) rather than the sum. Presentation stays gated behind the
    /// hold, and once it starts the schedule re-anchors
    /// presentation-relative (see [`Bank::presentation_started`]), so the
    /// released-ahead span counts as in-flight depth, not spent depth.
    /// Zero disables the overlap: hold, then 1x.
    pub startup_burst: MediaTime,
    pub auto: AutoConfig,
}

impl Default for BankConfig {
    fn default() -> Self {
        Self {
            depth: BufferDepth::Auto,
            liveness: Liveness::Live,
            decoder_cushion: MediaTime::from_millis(500),
            pace_lead: MediaTime::from_millis(400),
            pace_lead_with_lag: MediaTime::from_millis(100),
            lag_cap: MediaTime::from_secs(10),
            byte_cap: 16 * 1024 * 1024,
            time_cap: MediaTime::from_secs(30),
            startup_timeout: MediaTime::from_secs(6),
            decay_rate_ppm: 20_000,
            startup_burst: MediaTime::from_millis(2000),
            auto: AutoConfig::default(),
        }
    }
}

/// An unsatisfiable derivation is a reported error, never a silent clamp.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum BankConfigError {
    /// The requested depth sits below the fixed decoder cushion; there is
    /// nothing left to hold upstream and honouring it would mean shrinking
    /// the cushion silently.
    DepthBelowCushion {
        depth: MediaTime,
        cushion: MediaTime,
    },
    /// The requested depth exceeds what the lag ceiling permits.
    DepthBeyondLagCap { depth: MediaTime, max: MediaTime },
    /// The pace lead must sit strictly inside the decoder cushion, or the
    /// gate's own release sawtooth starves the decoder.
    LeadNotBelowCushion { lead: MediaTime, cushion: MediaTime },
}

impl fmt::Display for BankConfigError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::DepthBelowCushion { depth, cushion } => {
                write!(f, "depth {depth} below decoder cushion {cushion}")
            }
            Self::DepthBeyondLagCap { depth, max } => {
                write!(f, "depth {depth} beyond lag cap allowance {max}")
            }
            Self::LeadNotBelowCushion { lead, cushion } => {
                write!(f, "pace lead {lead} not below decoder cushion {cushion}")
            }
        }
    }
}

impl std::error::Error for BankConfigError {}

#[derive(Debug)]
pub enum PushOutcome {
    Accepted,
    /// A cap would be exceeded (or, on VOD, the read-ahead target is met).
    /// The event comes back so the pusher can retry after release drains,
    /// without cloning AU payloads.
    Full(StreamEvent),
    /// Stale-generation event, dropped on sight.
    StaleGeneration,
}

#[derive(Debug, Clone, Copy, Default)]
pub struct BankMetrics {
    /// Compressed bytes currently banked.
    pub banked_bytes: usize,
    /// Media duration currently banked ahead of the release point.
    pub banked: MediaTime,
    /// Accumulated delivery lag (how far behind the live edge release sits).
    pub lag: MediaTime,
    /// Current target lag (authored, or Auto's estimate).
    pub target_lag: MediaTime,
    /// Debt-bound anchor shifts.
    pub reanchors: u64,
    /// Total schedule deferral from anchor shifts.
    pub reanchor_total: MediaTime,
    /// The viewer-visible half of the deferrals: shift excess beyond what
    /// the decoder cushion absorbs.
    pub stall_total: MediaTime,
    pub released_aus: u64,
    pub dropped_stale: u64,
    /// True until the startup hold has released.
    pub holding: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Hold {
    /// Waiting for the target to bank (or the timeout). `since` is the wall
    /// time of the first banked AU.
    Filling {
        since: Option<MediaTime>,
    },
    /// Live priming joins only: the hold has lifted (presentation may
    /// start) but the schedule is not anchored yet. Release keeps running
    /// on the priming line until the engine reports the first
    /// presentation, which fixes the anchor presentation-relative.
    Primed {
        since: MediaTime,
    },
    Released,
}

#[derive(Debug)]
struct QueuedEvent {
    event: StreamEvent,
    /// Position on the bank's internal spliced timeline (discontinuities are
    /// stitched so accounting stays monotone; the event keeps its original
    /// timestamps for downstream).
    internal_dts: Option<MediaTime>,
}

/// One track's span this generation, internal timeline. Live tracks can
/// sit apart at arrival (a relay may open a join with a cached keyframe
/// stamped seconds before the live frames after it), so how far behind the
/// edge release sits is read from the lead track: the lowest newest stamp
/// among tracks still arriving. A track stamped ahead of the lead waits on
/// the schedule for it.
#[derive(Debug, Clone, Copy)]
struct TrackSpan {
    track: TrackId,
    first: MediaTime,
    newest: MediaTime,
    released: Option<MediaTime>,
    /// Wall time of the newest arrival.
    arrived_at: MediaTime,
}

impl TrackSpan {
    fn banked(&self) -> MediaTime {
        (self.newest - self.released.unwrap_or(self.first)).max(MediaTime::ZERO)
    }

    fn pending(&self) -> bool {
        self.released.is_none_or(|released| released < self.newest)
    }
}

/// A track that has delivered nothing for this long, while another has,
/// no longer decides the lead: a track that stops must not hold lag and
/// decay at its own drained span.
const TRACK_QUIET: MediaTime = MediaTime::from_secs(1);

/// The track an event keeps its place in. Metadata and captions have no
/// consumer past the Bank and keep none.
fn track_of(event: &StreamEvent) -> Option<TrackId> {
    match event {
        StreamEvent::Au(au) => Some(au.track),
        StreamEvent::Format(track, _) | StreamEvent::Discontinuity(track, _) => Some(*track),
        StreamEvent::Metadata(_) | StreamEvent::Caption(_) | StreamEvent::Eos(_) => None,
    }
}

#[derive(Debug)]
pub struct Bank {
    cfg: BankConfig,
    fixed_target_lag: MediaTime,
    auto_depth: Option<AutoDepth>,
    generation: Generation,

    queue: VecDeque<QueuedEvent>,
    queued_bytes: usize,

    /// First AU's dts of this generation (internal timeline origin).
    base_dts: Option<MediaTime>,
    /// Newest banked AU, internal timeline.
    newest_dts: Option<MediaTime>,
    /// Furthest released AU, internal timeline. Never moves back: a source
    /// with no decode timestamps (RTP) reports dts = pts, which runs out of
    /// order under B-frames.
    release_dts: Option<MediaTime>,
    /// Offset applied to incoming dts to splice across discontinuities.
    splice_offset: MediaTime,
    /// A discontinuity arrived; the next AU re-bases the splice offset.
    splice_pending: bool,
    tracks: Vec<TrackSpan>,

    /// Wall origin of the release schedule: an AU at internal position `rel`
    /// is due at `anchor + rel`.
    anchor: Option<MediaTime>,
    /// The schedule's offset behind the live edge. Tracks the lead track's
    /// banked span, less the cushion, upwards after the anchor and is what
    /// decay returns; see [`Bank::set_downstream_parked`] for why decay is
    /// not always free to run.
    lag: MediaTime,
    hold: Hold,
    last_decay: Option<MediaTime>,
    /// The release thread's report that a consumer channel is full. While
    /// it is, the cursor is held by downstream appetite rather than by the
    /// schedule, and decay waits.
    downstream_parked: bool,

    reanchors: u64,
    reanchor_total: MediaTime,
    stall_total: MediaTime,
    released_aus: u64,
    dropped_stale: u64,
}

impl Bank {
    pub fn new(cfg: BankConfig, generation: Generation) -> Result<Self, BankConfigError> {
        let lead = cfg.pace_lead.max(cfg.pace_lead_with_lag);
        if lead >= cfg.decoder_cushion {
            return Err(BankConfigError::LeadNotBelowCushion {
                lead,
                cushion: cfg.decoder_cushion,
            });
        }
        let fixed_target_lag = match cfg.depth {
            BufferDepth::Auto => MediaTime::ZERO,
            BufferDepth::Millis(ms) => {
                let depth = MediaTime::from_millis(ms as i64);
                if depth < cfg.decoder_cushion {
                    return Err(BankConfigError::DepthBelowCushion {
                        depth,
                        cushion: cfg.decoder_cushion,
                    });
                }
                let max = cfg.lag_cap + cfg.decoder_cushion;
                if depth > max {
                    return Err(BankConfigError::DepthBeyondLagCap { depth, max });
                }
                depth - cfg.decoder_cushion
            }
        };
        let auto_depth = match cfg.depth {
            BufferDepth::Auto => Some(AutoDepth::new(
                cfg.auto.clone(),
                cfg.decoder_cushion,
                cfg.lag_cap,
            )),
            BufferDepth::Millis(_) => None,
        };
        Ok(Self {
            cfg,
            fixed_target_lag,
            auto_depth,
            generation,
            queue: VecDeque::new(),
            queued_bytes: 0,
            base_dts: None,
            newest_dts: None,
            release_dts: None,
            splice_offset: MediaTime::ZERO,
            splice_pending: false,
            tracks: Vec::new(),
            anchor: None,
            lag: MediaTime::ZERO,
            hold: Hold::Filling { since: None },
            last_decay: None,
            downstream_parked: false,
            reanchors: 0,
            reanchor_total: MediaTime::ZERO,
            stall_total: MediaTime::ZERO,
            released_aus: 0,
            dropped_stale: 0,
        })
    }

    pub fn config(&self) -> &BankConfig {
        &self.cfg
    }

    pub fn generation(&self) -> Generation {
        self.generation
    }

    /// The lag the Bank is currently steering towards.
    pub fn target_lag(&self) -> MediaTime {
        match &self.auto_depth {
            Some(auto) => auto.target_lag(),
            None => self.fixed_target_lag,
        }
    }

    fn pace_lead(&self) -> MediaTime {
        // The reduced lead exists because a *live* delivery lag already
        // cushions the decoder upstream. VOD's target is read-ahead, not
        // lag: the decoder's only runway there is the full lead.
        if self.cfg.liveness == Liveness::Live
            && (self.target_lag() > MediaTime::ZERO || self.lag > MediaTime::ZERO)
        {
            self.cfg.pace_lead_with_lag
        } else {
            self.cfg.pace_lead
        }
    }

    /// Media banked ahead of the release point, internal timeline.
    fn banked(&self) -> MediaTime {
        match (self.newest_dts, self.release_cursor()) {
            (Some(newest), Some(cursor)) => (newest - cursor).max(MediaTime::ZERO),
            _ => MediaTime::ZERO,
        }
    }

    /// Media span arrived this generation, released or not. During a
    /// priming join this is the viewer's protection at first frame, since
    /// released-ahead media is still in flight in the decoder.
    fn arrived(&self) -> MediaTime {
        match (self.newest_dts, self.base_dts) {
            (Some(newest), Some(base)) => (newest - base).max(MediaTime::ZERO),
            _ => MediaTime::ZERO,
        }
    }

    /// The lead track (see [`TrackSpan`]): the lowest newest stamp among
    /// tracks still arriving. What a track stamped ahead of it holds is
    /// the gap between the tracks, not distance from the live edge. `None`
    /// on VOD.
    fn lead_span(&self) -> Option<&TrackSpan> {
        if self.cfg.liveness != Liveness::Live {
            return None;
        }
        let latest = self.tracks.iter().map(|span| span.arrived_at).max()?;
        self.tracks
            .iter()
            .filter(|span| latest - span.arrived_at < TRACK_QUIET)
            .min_by_key(|span| span.newest)
    }

    /// Where release stands for the whole queue: the head's cursor, or on
    /// a live source the earliest unreleased point of any track with media
    /// still queued (tracks release independently there, so the head's
    /// cursor can follow a track stamped ahead of the rest).
    fn release_cursor(&self) -> Option<MediaTime> {
        if self.cfg.liveness == Liveness::Live && !self.tracks.is_empty() {
            return self
                .tracks
                .iter()
                .filter(|span| span.pending())
                .map(|span| span.released.unwrap_or(span.first))
                .min()
                .or(self.newest_dts);
        }
        self.release_dts.or(self.base_dts)
    }

    /// What lag and decay read: the lead track's banked span on a live
    /// source, the whole queue's otherwise.
    fn lead_banked(&self) -> MediaTime {
        self.lead_span()
            .map_or_else(|| self.banked(), TrackSpan::banked)
    }

    /// When an AU at `internal` falls due, where the hold state gives each
    /// AU its own time: a priming join (filling or primed) and a released
    /// schedule. `None` while a strict startup hold decides from the head.
    fn au_due(&self, internal: MediaTime) -> Option<MediaTime> {
        let base = self.base_dts?;
        match self.hold {
            Hold::Filling { since: Some(since) } if self.priming() => {
                Some(self.priming_line_due(since, internal - base))
            }
            Hold::Filling { .. } => None,
            Hold::Primed { since } => Some(self.priming_line_due(since, internal - base)),
            Hold::Released => Some(self.anchor? + (internal - base) - self.pace_lead()),
        }
    }

    /// Whether this configuration primes the decoder during the startup
    /// hold (live with a non-zero burst allowance).
    fn priming(&self) -> bool {
        self.cfg.liveness == Liveness::Live && self.cfg.startup_burst > MediaTime::ZERO
    }

    /// What must have arrived for the hold to lift. Presentation starts at
    /// hold-lift on a priming join, so the arrived span is the depth the
    /// viewer joins with. An explicitly configured depth therefore holds
    /// for lag plus cushion, or the join would silently shed the cushion.
    /// Auto holds to the estimator's lag only. A cold Auto target sits a
    /// hair above zero, and adding the cushion would cost every Auto live
    /// join about 500 ms. A zero target lifts immediately.
    fn hold_target(&self) -> MediaTime {
        let target = self.target_lag();
        if self.priming()
            && target > MediaTime::ZERO
            && matches!(self.cfg.depth, BufferDepth::Millis(_))
        {
            target + self.cfg.decoder_cushion
        } else {
            target
        }
    }

    /// Span already released this generation (the priming run-ahead).
    fn released_span(&self) -> MediaTime {
        match (self.release_dts, self.base_dts) {
            (Some(release), Some(base)) => (release - base).max(MediaTime::ZERO),
            _ => MediaTime::ZERO,
        }
    }

    pub fn metrics(&self) -> BankMetrics {
        BankMetrics {
            banked_bytes: self.queued_bytes,
            banked: self.banked(),
            lag: self.lag,
            target_lag: self.target_lag(),
            reanchors: self.reanchors,
            reanchor_total: self.reanchor_total,
            stall_total: self.stall_total,
            released_aus: self.released_aus,
            dropped_stale: self.dropped_stale,
            holding: matches!(self.hold, Hold::Filling { .. }),
        }
    }

    /// Seek/reconnect: adopt the new generation, drop everything banked from
    /// the old one, restart the startup hold. Auto's delay history survives,
    /// because a seek does not change the link. A track's Format is kept:
    /// it describes the track, not a place on the timeline, and a demuxer
    /// announces each track once, so one not yet released when the seek
    /// lands would leave that track with no decoder for good.
    pub fn advance_generation(&mut self, generation: Generation) {
        self.generation = generation;
        self.queue
            .retain(|entry| matches!(entry.event, StreamEvent::Format(..)));
        self.queued_bytes = self
            .queue
            .iter()
            .map(|entry| entry.event.payload_bytes())
            .sum();
        self.base_dts = None;
        self.newest_dts = None;
        self.release_dts = None;
        self.splice_offset = MediaTime::ZERO;
        self.splice_pending = false;
        self.tracks.clear();
        self.anchor = None;
        self.lag = MediaTime::ZERO;
        self.hold = Hold::Filling { since: None };
        self.last_decay = None;
    }

    /// Offer one event to the bank. `wall` is the arrival time.
    pub fn push(&mut self, wall: MediaTime, event: StreamEvent) -> PushOutcome {
        if let Some(generation) = event.generation()
            && generation != self.generation
        {
            self.dropped_stale += 1;
            return PushOutcome::StaleGeneration;
        }

        let StreamEvent::Au(ref au) = event else {
            if matches!(event, StreamEvent::Discontinuity(..)) {
                self.splice_pending = true;
            }
            self.queued_bytes += event.payload_bytes();
            self.queue.push_back(QueuedEvent {
                event,
                internal_dts: None,
            });
            return PushOutcome::Accepted;
        };

        if self.queued_bytes + au.data.len() > self.cfg.byte_cap {
            return PushOutcome::Full(event);
        }

        let internal = if self.splice_pending {
            // Stitch the new timeline on immediately after the newest banked
            // media so accounting and pacing stay continuous across the
            // break; downstream still sees the original timestamps plus the
            // Discontinuity event.
            self.newest_dts.unwrap_or(au.dts)
        } else {
            au.dts + self.splice_offset
        };
        if let Some(cursor) = self.release_cursor() {
            let span = internal - cursor;
            if span > self.cfg.time_cap {
                return PushOutcome::Full(event);
            }
            if self.cfg.liveness == Liveness::Vod {
                let target_depth = self.target_lag() + self.cfg.decoder_cushion;
                if self.banked() >= target_depth.max(self.cfg.decoder_cushion) {
                    return PushOutcome::Full(event);
                }
            }
        }
        if self.splice_pending {
            self.splice_pending = false;
            self.splice_offset = internal - au.dts;
        }

        if self.base_dts.is_none() {
            self.base_dts = Some(internal);
        }
        self.newest_dts = Some(self.newest_dts.map_or(internal, |n| n.max(internal)));
        match self.tracks.iter_mut().find(|span| span.track == au.track) {
            Some(span) => {
                span.newest = span.newest.max(internal);
                span.arrived_at = wall;
            }
            None => self.tracks.push(TrackSpan {
                track: au.track,
                first: internal,
                newest: internal,
                released: None,
                arrived_at: wall,
            }),
        }
        if let Hold::Filling { since: None } = self.hold {
            self.hold = Hold::Filling { since: Some(wall) };
        }

        // The debt bound (live only): if delivery has fallen further behind
        // the release schedule than the decoder cushion absorbs, shift the
        // anchor by the excess. The burst behind this AU is then metered
        // back to 1x and the surplus deepens the bank.
        if self.cfg.liveness == Liveness::Live
            && let Some(anchor) = self.anchor
            && let Some(base) = self.base_dts
        {
            let rel = internal - base;
            let sched = wall - anchor;
            let behind = sched - rel;
            if let Some(auto) = &mut self.auto_depth {
                auto.observe_delay((behind + self.lag).max(MediaTime::ZERO));
            }
            if behind > self.cfg.decoder_cushion {
                let shift = behind - self.cfg.decoder_cushion;
                let headroom = (self.cfg.lag_cap - self.lag).max(MediaTime::ZERO);
                let applied = shift.min(headroom);
                self.anchor = Some(anchor + applied);
                self.lag += applied;
                self.reanchors += 1;
                self.reanchor_total += applied;
                // What the viewer saw: everything the cushion could not hide,
                // whether or not the lag cap let the schedule absorb it.
                self.stall_total += shift;
            }
            // Media arriving ahead of the schedule (the rest of a join's
            // opening burst) deepens the bank with no anchor shift. `lag`
            // follows the lead track's banked span upwards, less the
            // cushion: within the cushion a high span is arrival jitter,
            // and tracking it would ratchet the schedule earlier with each
            // early burst. Downwards `lag` is left alone, and a delivery
            // stall reads to the estimator as a delay.
            let surplus = (self.lead_banked() - self.cfg.decoder_cushion).max(MediaTime::ZERO);
            self.lag = self.lag.max(surplus).min(self.cfg.lag_cap);
        }

        self.queued_bytes += au.data.len();
        self.queue.push_back(QueuedEvent {
            event,
            internal_dts: Some(internal),
        });
        // The hold condition is arrival-driven; advancing it here keeps
        // the holding/awaiting state honest the moment the target arrives.
        self.advance_hold(wall);
        PushOutcome::Accepted
    }

    /// Lift the hold once the target has arrived or the timeout passes.
    /// Priming joins move to [`Hold::Primed`] here (the anchor waits for
    /// the presentation signal); the strict startup anchors inside
    /// `pop_due`, where the head position is to hand.
    fn advance_hold(&mut self, wall: MediaTime) {
        if !self.priming() {
            return;
        }
        if let Hold::Filling { since: Some(since) } = self.hold {
            let timed_out = wall - since >= self.cfg.startup_timeout;
            if self.arrived() >= self.hold_target() || timed_out {
                self.hold = Hold::Primed { since };
            }
        }
    }

    /// The wall time the priming line admits the head AU: release may run
    /// ahead of the 1x line from the first arrival by the burst allowance.
    fn priming_line_due(&self, since: MediaTime, rel: MediaTime) -> MediaTime {
        since + rel - self.cfg.startup_burst
    }

    /// When the head event becomes releasable, as a wall deadline for the
    /// release thread's condvar. `None` means "wait for a push".
    pub fn next_due(&mut self, wall: MediaTime) -> Option<MediaTime> {
        self.next_due_gated(wall, &|_| false)
    }

    /// [`Bank::next_due`] under a release gate: the deadline for the first
    /// event the gate admits, or on a live source the soonest of each
    /// track's first. `None` means "wait for a push or an unblock": an Eos
    /// barrier behind skipped events has no wall deadline of its own.
    pub fn next_due_gated(
        &mut self,
        wall: MediaTime,
        blocked: &dyn Fn(&StreamEvent) -> bool,
    ) -> Option<MediaTime> {
        self.tick(wall);
        self.advance_hold(wall);
        let per_track = self.cfg.liveness == Liveness::Live;
        let mut waiting: Vec<TrackId> = Vec::new();
        let mut soonest: Option<MediaTime> = None;
        for (index, entry) in self.queue.iter().enumerate() {
            let track = track_of(&entry.event);
            if blocked(&entry.event) || track.is_some_and(|track| waiting.contains(&track)) {
                continue;
            }
            if matches!(entry.event, StreamEvent::Eos(_)) && index > 0 {
                break;
            }
            let Some(internal) = entry.internal_dts else {
                return Some(wall);
            };
            let due = self.head_due(wall, internal);
            if !per_track || self.au_due(internal).is_none() {
                return due;
            }
            soonest = match (soonest, due) {
                (Some(a), Some(b)) => Some(a.min(b)),
                (a, b) => a.or(b),
            };
            waiting.extend(track);
        }
        soonest
    }

    /// When the AU at `internal` becomes releasable under the hold state,
    /// as a wall deadline.
    fn head_due(&self, wall: MediaTime, internal: MediaTime) -> Option<MediaTime> {
        match self.hold {
            Hold::Filling { since } => {
                let since = since?;
                let timeout_at = since + self.cfg.startup_timeout;
                if self.priming() {
                    let base = self.base_dts?;
                    let line = self.priming_line_due(since, internal - base);
                    // The timeout lifts the hold whatever the line says.
                    Some(line.max(wall).min(timeout_at.max(wall)))
                } else if self.banked() >= self.hold_target() {
                    Some(wall)
                } else {
                    Some(timeout_at)
                }
            }
            Hold::Primed { since } => {
                let base = self.base_dts?;
                Some(self.priming_line_due(since, internal - base).max(wall))
            }
            Hold::Released => {
                let anchor = self.anchor?;
                let base = self.base_dts?;
                Some(anchor + (internal - base) - self.pace_lead())
            }
        }
    }

    /// Release the head event if it is due. No consumer is fed faster than
    /// 1x plus the pace lead. On VOD the schedule's phase sits
    /// `startup_burst` early (a constant offset; the rate is still 1x and
    /// the decode channel bounds what is actually in flight).
    pub fn pop_due(&mut self, wall: MediaTime) -> Option<StreamEvent> {
        self.pop_due_gated(wall, &|_| false)
    }

    /// [`Bank::pop_due`] under a release gate (per-track routing).
    /// Events the gate blocks are skipped and left queued in order, so one
    /// track's full decode chain never wedges the other track's release.
    /// Only whole tracks may be blocked (the gate sees every event), which
    /// keeps per-track order exact. Eos is a barrier: it never overtakes a
    /// skipped event, so a blocked track's AUs reach their decoder before
    /// its drain begins. On a live source a track whose next AU is not yet
    /// due is skipped the same way, so a track stamped ahead of the others
    /// waits on the schedule without holding them. The caps measure from
    /// the earliest unreleased point still queued, lag and decay from the
    /// lead track (see [`TrackSpan`]).
    pub fn pop_due_gated(
        &mut self,
        wall: MediaTime,
        blocked: &dyn Fn(&StreamEvent) -> bool,
    ) -> Option<StreamEvent> {
        self.tick(wall);
        self.advance_hold(wall);
        let per_track = self.cfg.liveness == Liveness::Live;
        let mut waiting: Vec<TrackId> = Vec::new();
        let mut index = 0;
        let internal = loop {
            let entry = self.queue.get(index)?;
            let track = track_of(&entry.event);
            if blocked(&entry.event) || track.is_some_and(|track| waiting.contains(&track)) {
                index += 1;
                continue;
            }
            if matches!(entry.event, StreamEvent::Eos(_)) && index > 0 {
                return None;
            }
            let Some(internal) = entry.internal_dts else {
                // Format/metadata/discontinuity flow with their queue
                // position among the admitted; only AUs gate on the
                // schedule.
                let entry = self.queue.remove(index).expect("entry exists");
                self.pop_non_au_bytes(&entry.event);
                return Some(entry.event);
            };
            if per_track
                && let Some(due) = self.au_due(internal)
                && wall < due
            {
                waiting.extend(track);
                index += 1;
                continue;
            }
            break internal;
        };

        match self.hold {
            Hold::Filling { since } => {
                let since = since?;
                if self.priming() {
                    // Priming release: run ahead of 1x into the decoder
                    // while the hold fills; the decode channels and the
                    // decoder's own appetite bound what is in flight, and
                    // presentation stays gated behind the hold.
                    let base = self.base_dts.expect("AU banked");
                    if wall < self.priming_line_due(since, internal - base) {
                        return None;
                    }
                } else {
                    let target_banked = self.banked() >= self.hold_target();
                    let timed_out = wall - since >= self.cfg.startup_timeout;
                    if !target_banked && !timed_out {
                        return None;
                    }
                    // The join's one buffering moment ends here: anchor the
                    // 1x schedule now, with everything banked as the working
                    // lag. VOD anchors `startup_burst` in the past, the
                    // phase that keeps the decoder's input depth filled from
                    // the first frame.
                    self.hold = Hold::Released;
                    let burst = if self.cfg.liveness == Liveness::Vod {
                        self.cfg.startup_burst
                    } else {
                        MediaTime::ZERO
                    };
                    self.anchor = Some(
                        wall - (internal - self.base_dts.expect("anchored with base")) - burst,
                    );
                    self.lag = self.lead_banked();
                }
            }
            Hold::Primed { since } => {
                let base = self.base_dts.expect("AU banked");
                if wall < self.priming_line_due(since, internal - base) {
                    return None;
                }
            }
            Hold::Released => {}
        }

        if self.hold == Hold::Released {
            let anchor = self.anchor?;
            let base = self.base_dts?;
            let rel = internal - base;
            if wall < anchor + rel - self.pace_lead() {
                return None;
            }
        }

        let entry = self.queue.remove(index).expect("entry exists");
        if let StreamEvent::Au(au) = &entry.event {
            self.queued_bytes -= au.data.len();
            if index == 0 {
                self.release_dts = Some(self.release_dts.map_or(internal, |r| r.max(internal)));
            }
            if let Some(span) = self.tracks.iter_mut().find(|span| span.track == au.track) {
                span.released = Some(span.released.map_or(internal, |r| r.max(internal)));
            }
            self.released_aus += 1;
        }
        Some(entry.event)
    }

    fn pop_non_au_bytes(&mut self, event: &StreamEvent) {
        self.queued_bytes = self.queued_bytes.saturating_sub(event.payload_bytes());
    }

    /// The engine's presentation signal, ending a priming join: the first
    /// frame is reaching the viewer at `wall`. The 1x schedule is fixed
    /// presentation-relative, phased at the point the lead track's release
    /// has reached, and resumes without pausing.
    /// Released-ahead media is in-flight depth held downstream; what the
    /// lead track still has banked is the lag. A later anchor would pause
    /// the schedule, and since one anchor governs every track it would
    /// starve the audio ring as well as the frame pool. No-op outside a
    /// priming join.
    ///
    /// The Auto estimator is unaffected: it observes `behind + lag`, and
    /// moving the anchor earlier grows `behind` by exactly what it takes
    /// off `lag`.
    pub fn presentation_started(&mut self, wall: MediaTime) {
        let Hold::Primed { .. } = self.hold else {
            return;
        };
        self.hold = Hold::Released;
        // The lead may be stamped before the origin (the first AU pushed),
        // so its position can be negative.
        let sched_now = match (self.lead_span(), self.base_dts) {
            (Some(span), Some(base)) => span.released.unwrap_or(span.first) - base,
            _ => self.released_span(),
        };
        self.anchor = Some(wall - sched_now);
        self.lag = match self.lead_span() {
            Some(span) => span.banked(),
            None => (self.arrived() - sched_now).max(MediaTime::ZERO),
        }
        .min(self.cfg.lag_cap);
    }

    /// The release thread's report of downstream appetite: `true` while a
    /// consumer channel is full and a released message is parked on it.
    /// Decay hands media to consumers of finite capacity (the audio ring,
    /// the decode channel, the frame pool), and while one is full the
    /// cursor stops for a reason that is not surplus. Left running, decay
    /// would keep moving the anchor earlier against a consumer that cannot
    /// take it, until arrivals read as more than a cushion late and the
    /// debt bound shifts the anchor back, booking a stall that never
    /// happened. So decay waits for a tick on which nothing is parked.
    pub fn set_downstream_parked(&mut self, parked: bool) {
        self.downstream_parked = parked;
    }

    /// True while a priming join waits for the engine's presentation
    /// signal ([`Bank::presentation_started`]).
    pub fn awaiting_presentation(&self) -> bool {
        matches!(self.hold, Hold::Primed { .. })
    }

    /// Whether the startup hold still gates presentation, advancing the
    /// hold state first. The presentation gate must ask the Bank
    /// directly: during a priming join the release thread can sit
    /// blocked on a full decode channel that only presentation drains, so
    /// a gate fed by release-thread stores would deadlock the join.
    pub fn holding(&mut self, wall: MediaTime) -> bool {
        self.advance_hold(wall);
        matches!(self.hold, Hold::Filling { .. })
    }

    /// Decay: return surplus lag in bounded steps towards the target, at a
    /// rate the present clock's slew can track. Runs only while the bank
    /// actually holds more than the target, since giving lag back during a
    /// drought would deepen the next stall.
    fn tick(&mut self, wall: MediaTime) {
        let last = self.last_decay.replace(wall);
        if self.hold != Hold::Released || self.downstream_parked {
            return;
        }
        let Some(last) = last else { return };
        let dt = (wall - last).max(MediaTime::ZERO);
        let target = self.target_lag();
        let banked = self.lead_banked();
        if self.lag > target && banked > target {
            let step = dt
                .scale_ppm(self.cfg.decay_rate_ppm)
                .min(self.lag - target)
                .min((banked - target).max(MediaTime::ZERO));
            if step > MediaTime::ZERO
                && let Some(anchor) = self.anchor
            {
                self.anchor = Some(anchor - step);
                self.lag -= step;
            }
        }
    }
}
