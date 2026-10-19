//! The media clock: one `MediaTime` type, master selection, and the drift
//! correction ladder (dead band / bounded slew / snap).
//!
//! One clock per session. Position as reported anywhere is always
//! clock-derived: `MediaClock::now` is the position, there is no other
//! position source.

#![forbid(unsafe_code)]

use std::fmt;
use std::ops::{Add, AddAssign, Neg, Sub, SubAssign};

/// The one time type: signed microseconds. Used for media positions,
/// durations, and monotonic wall-clock readings alike. Timestamp wrap
/// handling (33-bit PCR, 32-bit RTP) happens in the demux/RTP crates;
/// nothing holding a `MediaTime` ever sees a wrapped value.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct MediaTime(i64);

impl MediaTime {
    pub const ZERO: MediaTime = MediaTime(0);
    pub const MAX: MediaTime = MediaTime(i64::MAX);

    pub const fn from_micros(us: i64) -> Self {
        Self(us)
    }

    pub const fn from_millis(ms: i64) -> Self {
        Self(ms * 1_000)
    }

    pub const fn from_secs(s: i64) -> Self {
        Self(s * 1_000_000)
    }

    pub const fn as_micros(self) -> i64 {
        self.0
    }

    pub const fn as_millis(self) -> i64 {
        self.0 / 1_000
    }

    pub const fn abs(self) -> Self {
        Self(self.0.abs())
    }

    pub const fn saturating_add(self, rhs: Self) -> Self {
        Self(self.0.saturating_add(rhs.0))
    }

    pub const fn saturating_sub(self, rhs: Self) -> Self {
        Self(self.0.saturating_sub(rhs.0))
    }

    pub fn clamp(self, min: Self, max: Self) -> Self {
        Self(self.0.clamp(min.0, max.0))
    }

    pub const fn min(self, rhs: Self) -> Self {
        if self.0 <= rhs.0 { self } else { rhs }
    }

    pub const fn max(self, rhs: Self) -> Self {
        if self.0 >= rhs.0 { self } else { rhs }
    }

    /// Scale by a parts-per-million factor, rounding towards zero.
    pub fn scale_ppm(self, ppm: i64) -> Self {
        Self((self.0 as i128 * ppm as i128 / 1_000_000) as i64)
    }
}

impl Add for MediaTime {
    type Output = MediaTime;
    fn add(self, rhs: Self) -> Self {
        Self(self.0 + rhs.0)
    }
}

impl AddAssign for MediaTime {
    fn add_assign(&mut self, rhs: Self) {
        self.0 += rhs.0;
    }
}

impl Sub for MediaTime {
    type Output = MediaTime;
    fn sub(self, rhs: Self) -> Self {
        Self(self.0 - rhs.0)
    }
}

impl SubAssign for MediaTime {
    fn sub_assign(&mut self, rhs: Self) {
        self.0 -= rhs.0;
    }
}

impl Neg for MediaTime {
    type Output = MediaTime;
    fn neg(self) -> Self {
        Self(-self.0)
    }
}

impl fmt::Display for MediaTime {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}us", self.0)
    }
}

/// Seek/reconnect generation. Every stage drops stale-generation events on
/// sight; the clock snaps across a generation change.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub struct Generation(pub u64);

impl Generation {
    pub const fn next(self) -> Self {
        Self(self.0 + 1)
    }
}

/// Which source the clock chases. Explicit state, switchable at runtime.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Master {
    /// Audio playhead is master while an audio track is playing.
    Audio,
    /// Wall-clock paced: the clock free-runs at 1x and ignores observations.
    Wall,
}

/// The widest a slew ceiling may be. `ClockConfig`'s fields are public, so a
/// ceiling is caller-supplied: a negative one inverts the correction, and one
/// at or beyond 1x lets a negative correction stop or reverse `now()`, which
/// nothing downstream of a monotonic clock survives.
const MAX_SLEW_PPM: i64 = 999_999;

/// Master offsets averaged by `ClockConfig::smooth_master`.
pub const SMOOTHING_SAMPLES: usize = 10;
/// The least wall time between two averaged offsets, so the window spans at
/// least `SMOOTHING_SAMPLES` × this (300 ms) however often the master is
/// observed.
pub const SMOOTHING_INTERVAL: MediaTime = MediaTime::from_millis(30);

/// Ladder parameters. Defaults: 20 ms dead band (Media3's figure) with a 5 ms
/// release, 2% slew cap (well under the ~6% at which a rate change becomes
/// noticeable), and a 700 ms snap threshold (the previous C player's live
/// resync figure). The correction is proportional with the cap as a
/// ceiling, and a wider cap applies for a short window after a snap or a
/// master adoption, so a join converges in seconds rather than tens of
/// seconds.
#[derive(Debug, Clone)]
pub struct ClockConfig {
    pub dead_band: MediaTime,
    /// Once a slew has started, it runs until the error is inside this,
    /// not merely back inside `dead_band`. The correction is only
    /// proportional below `slew_tau` × cap (5 ms at the defaults); a slew
    /// released at the dead band's edge would park the clock there, where a
    /// few ms of jitter starts the next slew. Clamped to `dead_band`.
    pub release_band: MediaTime,
    pub snap_threshold: MediaTime,
    pub slew_cap_ppm: i64,
    /// Time constant of the proportional correction: the rate offset is the
    /// error divided by this, so an uncapped correction closes the error
    /// exponentially with this constant rather than arriving at full rate and
    /// overshooting. 0.25 s matches the previous C player's live correction.
    pub slew_tau: MediaTime,
    /// The cap in force for `fast_window` after a snap or a master adoption,
    /// the two moments where the clock is knowingly far from its master and a
    /// steady-state cap would take tens of seconds to close the gap. Audio is
    /// master and is never rate-adjusted, so the whole correction lands on the
    /// picture: a brief catch-up in the video and nothing in the sound.
    pub fast_slew_cap_ppm: i64,
    /// How long `fast_slew_cap_ppm` stays in force.
    pub fast_window: MediaTime,
    /// Smooth the master position before the dead-band/slew rungs act on
    /// it. The audio playhead is timed from the host's pulls, whose jitter
    /// (Unity on Windows ±25 ms, Android ±40 ms) is wider than the dead band.
    /// The smoothed master is the wall clock plus the mean of the last
    /// `SMOOTHING_SAMPLES` playhead-minus-wall offsets, sampled at least
    /// `SMOOTHING_INTERVAL` apart (Media3's `AudioTrackPositionTracker`
    /// scheme). Leaving the clock's own corrections out of the average stops
    /// a catch-up being carried past its target. The snap rung always acts
    /// on the raw position.
    pub smooth_master: bool,
}

impl Default for ClockConfig {
    fn default() -> Self {
        Self {
            dead_band: MediaTime::from_millis(20),
            release_band: MediaTime::from_millis(5),
            snap_threshold: MediaTime::from_millis(700),
            slew_cap_ppm: 20_000,
            slew_tau: MediaTime::from_millis(250),
            fast_slew_cap_ppm: 500_000,
            fast_window: MediaTime::from_millis(1200),
            smooth_master: true,
        }
    }
}

/// What an observation did, for the diagnostics event log (slew/seek
/// corrections are default-verbosity events).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Correction {
    /// Error inside the dead band, or observation ignored (wall master,
    /// paused, stale generation).
    None,
    /// Presentation rate slewed towards the master; the offset applied, ppm.
    Slew { rate_ppm: i64 },
    /// Error beyond the snap threshold, a discontinuity, or a generation
    /// change: the clock jumped to the master position.
    Snap { error: MediaTime },
}

#[derive(Debug)]
pub struct MediaClock {
    cfg: ClockConfig,
    /// Wall time of the current linear segment's origin.
    anchor_wall: MediaTime,
    /// Media position at the segment origin.
    anchor_media: MediaTime,
    /// Rate offset from 1x in ppm, clamped to the ceiling in force (`cap_ppm`).
    rate_ppm: i64,
    master: Master,
    playing: bool,
    generation: Generation,
    /// The master's recent offsets from the wall clock (`smooth_master`).
    /// Cleared on snap, master switch and play/pause, the moments the
    /// offset legitimately jumps, so the next observation seeds it afresh.
    offsets: MasterOffsets,
    /// Wall time the fast cap stops applying. Opened by a snap and by
    /// adopting the audio master; `None` outside a fast window.
    fast_until: Option<MediaTime>,
}

impl MediaClock {
    pub fn new(
        cfg: ClockConfig,
        wall: MediaTime,
        origin: MediaTime,
        generation: Generation,
    ) -> Self {
        Self {
            cfg,
            anchor_wall: wall,
            anchor_media: origin,
            rate_ppm: 0,
            master: Master::Wall,
            playing: false,
            generation,
            offsets: MasterOffsets::default(),
            fast_until: None,
        }
    }

    /// The session position. Clock-derived, always.
    pub fn now(&self, wall: MediaTime) -> MediaTime {
        if !self.playing {
            return self.anchor_media;
        }
        let elapsed = wall - self.anchor_wall;
        self.anchor_media + elapsed + elapsed.scale_ppm(self.rate_ppm)
    }

    pub fn rate_ppm(&self) -> i64 {
        self.rate_ppm
    }

    pub fn master(&self) -> Master {
        self.master
    }

    pub fn is_playing(&self) -> bool {
        self.playing
    }

    pub fn generation(&self) -> Generation {
        self.generation
    }

    /// Re-anchor the linear segment at the current position so a rate or
    /// state change never moves `now`.
    fn rebase(&mut self, wall: MediaTime) {
        self.anchor_media = self.now(wall);
        self.anchor_wall = wall;
    }

    pub fn set_playing(&mut self, wall: MediaTime, playing: bool) {
        if self.playing != playing {
            self.rebase(wall);
            self.playing = playing;
            // The master stands still while paused and the wall does not,
            // so offsets from before a pause read the pause as an error.
            self.offsets.clear();
        }
    }

    /// Switch master selection. Switching never moves `now`; switching to
    /// `Wall` also clears any running slew.
    pub fn set_master(&mut self, wall: MediaTime, master: Master) {
        self.rebase(wall);
        self.master = master;
        self.offsets.clear();
        match master {
            Master::Wall => {
                self.rate_ppm = 0;
                self.fast_until = None;
            }
            // Adopting audio is a join: the clock has been running on wall
            // time and the first real master report can be a long way off.
            Master::Audio => self.fast_until = Some(wall + self.cfg.fast_window),
        }
    }

    /// Feed a master position report (the audio playhead). Applies the
    /// ladder: dead band → nothing (a running slew ends at `release_band`),
    /// slew band → rate offset capped at the ceiling in force (`cap_ppm`),
    /// beyond `snap_threshold` → snap. With `smooth_master` set, only the
    /// snap rung sees the raw position.
    pub fn observe_master(&mut self, wall: MediaTime, master_pos: MediaTime) -> Correction {
        if self.master != Master::Audio || !self.playing {
            return Correction::None;
        }
        self.enforce_slew_ceiling(wall);
        let raw = master_pos - self.now(wall);
        if raw.abs() >= self.cfg.snap_threshold {
            self.snap(wall, master_pos);
            return Correction::Snap { error: raw };
        }
        let error = if self.cfg.smooth_master {
            wall + self.offsets.observe(wall, master_pos - wall) - self.now(wall)
        } else {
            raw
        };
        self.rebase(wall);
        let band = if self.rate_ppm != 0 {
            self.cfg.release_band.min(self.cfg.dead_band)
        } else {
            self.cfg.dead_band
        };
        if error.abs() <= band {
            self.rate_ppm = 0;
            return Correction::None;
        }
        // Proportional catch-up, capped. The rate that closes `error` with
        // time constant `slew_tau`; the cap is a ceiling, not the operating
        // point, so the correction decelerates as the error closes instead of
        // arriving at full rate and overshooting into a limit cycle.
        let tau = self.cfg.slew_tau.as_micros().max(1);
        let ppm = error.as_micros().saturating_mul(1_000_000) / tau;
        let cap = self.cap_ppm(wall);
        self.rate_ppm = ppm.clamp(-cap, cap);
        Correction::Slew {
            rate_ppm: self.rate_ppm,
        }
    }

    /// External soft-target slew under the wall master (the sync ladder on
    /// masterless lanes): rebase so `now` never jumps, then run at
    /// 1x + `ppm`, clamped to the slew cap. Ignored under the audio
    /// master, where the correction rides the audio playhead and
    /// `observe_master` carries the clock along.
    pub fn slew_wall(&mut self, wall: MediaTime, ppm: i64) {
        if self.master != Master::Wall {
            return;
        }
        self.rebase(wall);
        self.rate_ppm = ppm.clamp(-self.cfg.slew_cap_ppm, self.cfg.slew_cap_ppm);
    }

    /// A timeline break (PCR wrap, ad splice, decoder reset): snap, never
    /// slew.
    pub fn discontinuity(&mut self, wall: MediaTime, new_pos: MediaTime) -> Correction {
        let error = new_pos - self.now(wall);
        self.snap(wall, new_pos);
        Correction::Snap { error }
    }

    /// Seek/reconnect: adopt the new generation and snap to its position.
    pub fn advance_generation(
        &mut self,
        wall: MediaTime,
        generation: Generation,
        new_pos: MediaTime,
    ) -> Correction {
        self.generation = generation;
        let error = new_pos - self.now(wall);
        self.snap(wall, new_pos);
        Correction::Snap { error }
    }

    /// The slew ceiling in force. `fast_slew_cap_ppm` for `fast_window`
    /// after a snap or a master adoption, `slew_cap_ppm` otherwise.
    fn cap_ppm(&self, wall: MediaTime) -> i64 {
        let cap = match self.fast_until {
            Some(until) if wall < until => self.cfg.fast_slew_cap_ppm,
            _ => self.cfg.slew_cap_ppm,
        };
        // Caller-supplied, so bounded here rather than trusted: a negative
        // ceiling would make the clamp below panic on `min > max`, and one at
        // or beyond 1x could stop or reverse `now()`.
        cap.clamp(0, MAX_SLEW_PPM)
    }

    /// Close out an expired fast window. `rate_ppm` persists between
    /// observations, so a rate set just inside the window keeps running at the
    /// wide ceiling until the next observation arrives, indefinitely if the
    /// master goes quiet. Rebasing first means the position already reported
    /// at the old rate stands, and only the rate from here changes.
    ///
    /// Idempotent and cheap; call it wherever the clock is already held.
    pub fn enforce_slew_ceiling(&mut self, wall: MediaTime) {
        let Some(until) = self.fast_until else {
            return;
        };
        if wall < until {
            return;
        }
        self.fast_until = None;
        let cap = self.cfg.slew_cap_ppm.clamp(0, MAX_SLEW_PPM);
        if self.rate_ppm.abs() > cap {
            self.rebase(wall);
            self.rate_ppm = self.rate_ppm.clamp(-cap, cap);
        }
    }

    fn snap(&mut self, wall: MediaTime, pos: MediaTime) {
        self.anchor_wall = wall;
        self.anchor_media = pos;
        self.rate_ppm = 0;
        self.offsets.clear();
        // A snap lands the clock on the master but says nothing about the
        // rate it should run at from here; the window lets the first
        // corrections after it converge rather than crawl.
        self.fast_until = Some(wall + self.cfg.fast_window);
    }
}

/// The master's recent offsets from the wall clock, for `smooth_master`.
#[derive(Debug, Default)]
struct MasterOffsets {
    samples: [i64; SMOOTHING_SAMPLES],
    count: usize,
    next: usize,
    last_sample: Option<MediaTime>,
}

impl MasterOffsets {
    fn clear(&mut self) {
        *self = Self::default();
    }

    /// Take `offset` into the average if the last sample is at least
    /// `SMOOTHING_INTERVAL` old, and return the mean. The first observation
    /// after a clear stands alone, so a fresh timeline is not held back.
    fn observe(&mut self, wall: MediaTime, offset: MediaTime) -> MediaTime {
        let due = self
            .last_sample
            .is_none_or(|last| wall - last >= SMOOTHING_INTERVAL);
        if due {
            self.samples[self.next] = offset.as_micros();
            self.next = (self.next + 1) % SMOOTHING_SAMPLES;
            self.count = (self.count + 1).min(SMOOTHING_SAMPLES);
            self.last_sample = Some(wall);
        }
        let sum: i128 = self.samples[..self.count].iter().map(|&s| s as i128).sum();
        MediaTime::from_micros((sum / self.count as i128) as i64)
    }
}
