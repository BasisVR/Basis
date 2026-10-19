//! Diagnostics and observability.
//!
//! The rule: a pipeline stage is not done until its input rate, its
//! occupancy and its output rate are exported. Every stage publishes
//! counters into a per-session block; a structured event log runs at
//! default verbosity with stable codes (no failure path hides behind a
//! verbose flag); and the capture recorder dumps the timeline as CSV with a
//! stable column contract for the analysis tooling.

#![forbid(unsafe_code)]

use std::collections::VecDeque;
use std::fmt::Write as _;
use std::io::Write as _;
use std::sync::atomic::{AtomicI32, AtomicI64, AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::Instant;

use media_clock::MediaTime;

/// Where a textual diagnostic goes. `None` means stderr, which is the
/// console a desktop run or `bm-probe` has. A host whose platform gives
/// the process no usable stderr installs its own as it loads: Android
/// discards a native process's stderr outright, so without one nothing
/// the engine says about a session survives it.
static SINK: Mutex<Option<fn(&str)>> = Mutex::new(None);

/// Installs the sink [`log`] routes to. A host calls this once as it
/// loads, before opening anything; the last writer wins.
pub fn set_log_sink(sink: fn(&str)) {
    *SINK.lock().unwrap_or_else(|e| e.into_inner()) = Some(sink);
}

/// How much a diagnostic matters. Ordered most severe first, so "at least
/// this severe" is a comparison rather than a match.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord)]
#[repr(u32)]
pub enum Level {
    Error = 0,
    Warn = 1,
    Info = 2,
}

/// Microseconds since the engine was first asked for a diagnostic in this
/// process. The process log outlives every session, so it cannot borrow a
/// session's clock; a reader lining a line up against a session's events
/// is comparing two different origins and the drain says so.
fn process_wall_us() -> i64 {
    static ORIGIN: OnceLock<Instant> = OnceLock::new();
    ORIGIN.get_or_init(Instant::now).elapsed().as_micros() as i64
}

/// One line in the process log: a session's structured event or a free-text
/// diagnostic, in the one shape. `session` is 0 for a line that belongs to
/// no session, which covers many of the most useful ones: the failures that
/// cost most to diagnose happen before a handle exists or after it closes.
#[derive(Debug, Clone)]
pub struct LogRecord {
    pub wall_us: i64,
    pub session: u64,
    pub level: Level,
    pub code: EventCode,
    pub stage: Stage,
    pub detail: String,
}

/// Lines held for a host that drains on its own tick. A headless run
/// drains nothing at all, so the ring must stay useful having been full
/// for the whole session: it drops its **oldest** line, where the session
/// event log refuses its newest. Refusing here would freeze the process
/// log at whatever the first half-second happened to contain.
const LOG_RING_CAP: usize = 512;

static LOG_RING: Mutex<VecDeque<LogRecord>> = Mutex::new(VecDeque::new());
static LOG_DROPPED: AtomicU64 = AtomicU64::new(0);

fn push_log_record(record: LogRecord) {
    let mut ring = LOG_RING.lock().unwrap_or_else(|e| e.into_inner());
    while ring.len() >= LOG_RING_CAP {
        ring.pop_front();
        LOG_DROPPED.fetch_add(1, Ordering::Relaxed);
    }
    ring.push_back(record);
}

/// Drain up to `max` lines from the process log, oldest first, leaving the
/// rest queued.
pub fn drain_log(max: usize) -> Vec<LogRecord> {
    let mut ring = LOG_RING.lock().unwrap_or_else(|e| e.into_inner());
    let take = max.min(ring.len());
    ring.drain(..take).collect()
}

/// Lines the ring dropped to make room, cumulative. Non-zero means the
/// drained sequence has holes at the *start* of what it covers.
pub fn log_dropped() -> u64 {
    LOG_DROPPED.load(Ordering::Relaxed)
}

/// Emits one diagnostic line. The default stderr sink names the source,
/// since it shares a console with whatever else the host writes; an
/// installed sink is not given the prefix, because a platform log that
/// needs one carries it as a tag instead.
pub fn log(line: &str) {
    log_at(Level::Info, line);
}

/// [`log`] at a stated severity. The line goes to the platform sink and
/// into the process log, so a host with no console still has the free
/// text; on a user's machine the sink alone is unreachable.
pub fn log_at(level: Level, line: &str) {
    push_log_record(LogRecord {
        wall_us: process_wall_us(),
        session: 0,
        level,
        code: EventCode::Log,
        stage: Stage::Source,
        detail: line.to_string(),
    });
    // Copied out so the sink runs with the lock released: a sink is
    // host-supplied and may log, and re-entering here would deadlock.
    let sink = *SINK.lock().unwrap_or_else(|e| e.into_inner());
    match sink {
        Some(sink) => sink(line),
        None => stderr_sink(line),
    }
}

/// The default sink: the line to stderr, tagged. A failed write is
/// dropped, not a panic: stderr can be a pipe whose reader has gone, and
/// this runs on whichever thread is logging, some of them called from C.
pub fn stderr_sink(line: &str) {
    let _ = writeln!(std::io::stderr(), "[basis-media] {line}");
}

/// [`log`] with `format!` arguments, at `Info`.
#[macro_export]
macro_rules! diag_log {
    ($($arg:tt)*) => { $crate::log(&format!($($arg)*)) };
}

/// [`diag_log`] at `Warn`: a fallback, a refusal or a cap the viewer could
/// notice.
#[macro_export]
macro_rules! diag_warn {
    ($($arg:tt)*) => { $crate::log_at($crate::Level::Warn, &format!($($arg)*)) };
}

/// [`diag_log`] at `Error`: the session is not going to do what was asked.
#[macro_export]
macro_rules! diag_err {
    ($($arg:tt)*) => { $crate::log_at($crate::Level::Error, &format!($($arg)*)) };
}

/// Every stage of the one pipeline shape.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(usize)]
pub enum Stage {
    Source = 0,
    Demux = 1,
    Bank = 2,
    Decode = 3,
    Pool = 4,
    Present = 5,
    AudioRing = 6,
    Clock = 7,
}

pub const STAGE_COUNT: usize = 8;

pub const STAGES: [Stage; STAGE_COUNT] = [
    Stage::Source,
    Stage::Demux,
    Stage::Bank,
    Stage::Decode,
    Stage::Pool,
    Stage::Present,
    Stage::AudioRing,
    Stage::Clock,
];

impl Stage {
    pub fn name(self) -> &'static str {
        match self {
            Stage::Source => "source",
            Stage::Demux => "demux",
            Stage::Bank => "bank",
            Stage::Decode => "decode",
            Stage::Pool => "pool",
            Stage::Present => "present",
            Stage::AudioRing => "audio_ring",
            Stage::Clock => "clock",
        }
    }
}

/// In rate, occupancy and out rate, plus the drop and error counters every
/// stage carries. All relaxed atomics: single-writer per counter, readers take
/// snapshots.
#[derive(Debug, Default)]
pub struct StageCounters {
    pub in_count: AtomicU64,
    pub in_bytes: AtomicU64,
    pub out_count: AtomicU64,
    pub out_bytes: AtomicU64,
    pub occupancy: AtomicU64,
    pub occupancy_bytes: AtomicU64,
    pub drops: AtomicU64,
    pub errors: AtomicU64,
}

#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct StageSnapshot {
    pub in_count: u64,
    pub in_bytes: u64,
    pub out_count: u64,
    pub out_bytes: u64,
    pub occupancy: u64,
    pub occupancy_bytes: u64,
    pub drops: u64,
    pub errors: u64,
}

impl StageCounters {
    pub fn snapshot(&self) -> StageSnapshot {
        StageSnapshot {
            in_count: self.in_count.load(Ordering::Relaxed),
            in_bytes: self.in_bytes.load(Ordering::Relaxed),
            out_count: self.out_count.load(Ordering::Relaxed),
            out_bytes: self.out_bytes.load(Ordering::Relaxed),
            occupancy: self.occupancy.load(Ordering::Relaxed),
            occupancy_bytes: self.occupancy_bytes.load(Ordering::Relaxed),
            drops: self.drops.load(Ordering::Relaxed),
            errors: self.errors.load(Ordering::Relaxed),
        }
    }
}

/// Structured event codes, stable across platforms and releases. Silent
/// fallbacks are a contradiction in terms: every fallback, refusal, cap hit
/// and correction is one of these.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u32)]
pub enum EventCode {
    StateChange = 1,
    Reconnect = 2,
    DecodeFallbackHwToSw = 3,
    TransportFallback = 4,
    CodecRefused = 5,
    CapHit = 6,
    UrlBlocked = 7,
    SlewCorrection = 8,
    SnapCorrection = 9,
    Seek = 10,
    Discontinuity = 11,
    CapabilityProbe = 12,
    Error = 13,
    // 14 is retired. Codes carry explicit discriminants and are stable,
    // so a retired value is never reused.
    /// The ring's serve-side trim engaged: banked audio crossed the
    /// high-water mark and excess frames were discarded to keep the
    /// serve on the media timeline (a source delivering more samples
    /// than its pts timeline claims). Detail carries the
    /// cumulative trimmed count.
    AudioTrim = 15,
    /// Shared-playback soft target: the slew rung engaged or
    /// released. Detail carries the target error and the applied rate.
    SyncSlew = 16,
    /// Shared-playback soft target: the error crossed the seek
    /// threshold (the last rung, never the first). Detail carries the
    /// error and the landed target.
    SyncSeek = 17,
    /// A free-text diagnostic ([`log`]). Detail is the whole line: this
    /// is the channel that works before a session exists and after it
    /// closes, so it carries no more structure than the words.
    Log = 18,
    /// The video decoder has fallen too far behind the clock for what it
    /// is decoding to be shown in time, and video is being discarded up to
    /// the next keyframe so that it can rejoin the sound. Sound is
    /// unaffected. Detail carries how far behind it was. Raised once per
    /// episode and rate-limited, so a decoder that stays too slow does not
    /// flood the log: it is the signal that this source is more than the
    /// device can decode.
    LateVideoSkip = 19,
}

impl EventCode {
    /// How much this code matters. A code's severity is a property of the
    /// code, so it is stated once here rather than at each of the twenty
    /// sites that raise one; a free-text line states its own.
    pub fn level(self) -> Level {
        match self {
            EventCode::Error => Level::Error,
            EventCode::Reconnect
            | EventCode::DecodeFallbackHwToSw
            | EventCode::TransportFallback
            | EventCode::CodecRefused
            | EventCode::CapHit
            | EventCode::UrlBlocked
            | EventCode::Discontinuity
            | EventCode::SnapCorrection
            | EventCode::AudioTrim
            | EventCode::LateVideoSkip => Level::Warn,
            EventCode::StateChange
            | EventCode::SlewCorrection
            | EventCode::Seek
            | EventCode::CapabilityProbe
            | EventCode::SyncSlew
            | EventCode::SyncSeek
            | EventCode::Log => Level::Info,
        }
    }
}

#[derive(Debug, Clone)]
pub struct DiagEvent {
    pub wall: MediaTime,
    pub level: Level,
    pub code: EventCode,
    pub stage: Stage,
    pub detail: String,
}

/// The Bank's schedule readings, as the release thread last saw them.
/// These are what decide whether the Bank hands its lag back downstream
/// (decay runs only while `lag` exceeds `target_lag`) and whether the
/// debt bound has been deferring the schedule; none of them can be
/// reconstructed from the stage counters, and without them a capture can
/// show *that* the Bank held a lag for a whole session but not why.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct BankReadings {
    /// Accumulated delivery lag: how far behind the live edge release sits.
    pub lag: MediaTime,
    /// The lag the Bank is steering towards (authored, or Auto's estimate).
    pub target_lag: MediaTime,
    /// Debt-bound anchor shifts so far this session.
    pub reanchors: u64,
    /// Total schedule deferral from those shifts.
    pub reanchor_total: MediaTime,
    /// The viewer-visible half of the deferrals: what the decoder cushion
    /// could not hide.
    pub stall_total: MediaTime,
}

/// Per-session diagnostics block: one set of stage counters, the audio
/// serve trim's running total, the Bank's schedule readings and the
/// bounded event log. Shared by `Arc` across the session's threads.
#[derive(Debug)]
pub struct SessionDiag {
    stages: [StageCounters; STAGE_COUNT],
    events: Mutex<Vec<DiagEvent>>,
    event_cap: usize,
    /// Events lost to the cap, counted so the loss is visible.
    events_dropped: AtomicU64,
    /// Frames discarded by the audio ring's serve-side trim. Session
    /// level rather than a field on `Stage::AudioRing`, whose `drops`
    /// already counts whole chunks dropped for an inert consumer: one
    /// column cannot carry both units.
    audio_trimmed_frames: AtomicU64,
    /// Presented video pts minus the audio playhead, microseconds, as the
    /// engine measures it. `i32::MIN` while either term is missing; real
    /// values are clamped clear of the sentinel so it stays unambiguous.
    /// Session-level rather than a stage field: it is a relationship between
    /// two stages, not a property of either.
    av_offset_us: AtomicI32,
    /// [`BankReadings`], one atomic each so the release thread publishes
    /// them without a lock and the recorder reads them the same way. Not
    /// torn as a set: each is a monotone or slowly-moving figure, and a
    /// row mixing two adjacent ticks is still an honest row.
    bank_lag_us: AtomicI64,
    bank_target_lag_us: AtomicI64,
    bank_reanchors: AtomicU64,
    bank_reanchor_total_us: AtomicI64,
    bank_stall_total_us: AtomicI64,
}

impl SessionDiag {
    pub fn new(event_cap: usize) -> Self {
        Self {
            stages: Default::default(),
            events: Mutex::new(Vec::new()),
            event_cap,
            events_dropped: AtomicU64::new(0),
            audio_trimmed_frames: AtomicU64::new(0),
            av_offset_us: AtomicI32::new(i32::MIN),
            bank_lag_us: AtomicI64::new(0),
            bank_target_lag_us: AtomicI64::new(0),
            bank_reanchors: AtomicU64::new(0),
            bank_reanchor_total_us: AtomicI64::new(0),
            bank_stall_total_us: AtomicI64::new(0),
        }
    }

    pub fn stage(&self, stage: Stage) -> &StageCounters {
        &self.stages[stage as usize]
    }

    pub fn event(&self, wall: MediaTime, code: EventCode, stage: Stage, detail: impl Into<String>) {
        let mut events = self.events.lock().expect("diag event lock");
        if events.len() >= self.event_cap {
            self.events_dropped.fetch_add(1, Ordering::Relaxed);
            return;
        }
        events.push(DiagEvent {
            wall,
            level: code.level(),
            code,
            stage,
            detail: detail.into(),
        });
    }

    /// Drain the pending events (the ABI/event-surface consumer).
    pub fn take_events(&self) -> Vec<DiagEvent> {
        std::mem::take(&mut *self.events.lock().expect("diag event lock"))
    }

    /// Drain up to `max` pending events, oldest first, leaving the rest
    /// queued. What a consumer cannot carry in one call it must be able
    /// to come back for: a burst that outruns its buffer is a backlog,
    /// not a loss.
    pub fn take_events_up_to(&self, max: usize) -> Vec<DiagEvent> {
        let mut events = self.events.lock().expect("diag event lock");
        let take = max.min(events.len());
        events.drain(..take).collect()
    }

    pub fn events_dropped(&self) -> u64 {
        self.events_dropped.load(Ordering::Relaxed)
    }

    /// Publish the audio ring's cumulative serve-trim total, in frames.
    /// The trim itself runs on the pull path, which must not touch the
    /// diag lock, so the audio thread carries the figure across.
    pub fn set_audio_trimmed(&self, frames: u64) {
        self.audio_trimmed_frames.store(frames, Ordering::Relaxed);
    }

    /// Publish the A/V offset for the capture. The engine already computes it
    /// for the ABI snapshot; this is the same value, so the capture and the
    /// snapshot can never disagree.
    pub fn set_av_offset(&self, offset_us: i32) {
        self.av_offset_us.store(offset_us, Ordering::Relaxed);
    }

    pub fn audio_trimmed(&self) -> u64 {
        self.audio_trimmed_frames.load(Ordering::Relaxed)
    }

    pub fn av_offset(&self) -> i32 {
        self.av_offset_us.load(Ordering::Relaxed)
    }

    /// Publish the Bank's schedule readings. The release thread already
    /// takes the Bank's metrics every iteration for the occupancy column;
    /// this carries the rest of that same struct across.
    pub fn set_bank(&self, readings: BankReadings) {
        self.bank_lag_us
            .store(readings.lag.as_micros(), Ordering::Relaxed);
        self.bank_target_lag_us
            .store(readings.target_lag.as_micros(), Ordering::Relaxed);
        self.bank_reanchors
            .store(readings.reanchors, Ordering::Relaxed);
        self.bank_reanchor_total_us
            .store(readings.reanchor_total.as_micros(), Ordering::Relaxed);
        self.bank_stall_total_us
            .store(readings.stall_total.as_micros(), Ordering::Relaxed);
    }

    pub fn bank(&self) -> BankReadings {
        BankReadings {
            lag: MediaTime::from_micros(self.bank_lag_us.load(Ordering::Relaxed)),
            target_lag: MediaTime::from_micros(self.bank_target_lag_us.load(Ordering::Relaxed)),
            reanchors: self.bank_reanchors.load(Ordering::Relaxed),
            reanchor_total: MediaTime::from_micros(
                self.bank_reanchor_total_us.load(Ordering::Relaxed),
            ),
            stall_total: MediaTime::from_micros(self.bank_stall_total_us.load(Ordering::Relaxed)),
        }
    }

    pub fn snapshot(&self) -> [StageSnapshot; STAGE_COUNT] {
        STAGES.map(|s| self.stage(s).snapshot())
    }
}

impl Default for SessionDiag {
    fn default() -> Self {
        Self::new(1024)
    }
}

/// The Bank's schedule block, in column order. Appended after
/// `av_offset_us`; the column contract is append-only.
pub const BANK_COLUMNS: [&str; 5] = [
    "bank_lag_us",
    "bank_target_lag_us",
    "bank_reanchors",
    "bank_reanchor_total_us",
    "bank_stall_total_us",
];

/// The capture recorder: the per-session timeline as CSV rows with a stable
/// column contract, so the analysis tooling keeps working unchanged across
/// platforms and releases. Columns only ever get appended; existing names
/// and order are the contract.
#[derive(Debug, Default)]
pub struct CaptureRecorder {
    rows: Vec<String>,
}

impl CaptureRecorder {
    /// The column contract. Serialisation is size-aware end to end (growable
    /// buffers, no fixed-size truncation).
    pub fn header() -> String {
        let mut h = String::from("wall_us");
        for stage in STAGES {
            let name = stage.name();
            for col in [
                "in_count",
                "in_bytes",
                "out_count",
                "out_bytes",
                "occupancy",
                "occupancy_bytes",
                "drops",
                "errors",
            ] {
                let _ = write!(h, ",{name}_{col}");
            }
        }
        // Appended after the stage block: the serve trim is the audio
        // path's only loss channel with no stage field of its own, and
        // reconstructing it from in/out/occupancy is error-prone.
        let _ = write!(h, ",audio_trimmed_frames");
        // Also appended after the stage block: the A/V offset is a
        // relationship between the present and audio-ring stages rather
        // than a counter belonging to either. It is here so a headless run
        // can see the offset's shape over time, which neither the ABI
        // snapshot nor the managed frame capture gives it.
        let _ = write!(h, ",av_offset_us");
        // The Bank's schedule block, appended last. The stage counters say
        // how much the Bank holds; these say whether it is meant to be
        // holding it, and whether the schedule has been deferred to get
        // there. A capture without them can show a lag that never decays
        // and cannot say which of the decay's own gates was closed.
        for name in BANK_COLUMNS {
            let _ = write!(h, ",{name}");
        }
        h
    }

    pub fn sample(&mut self, wall: MediaTime, diag: &SessionDiag) {
        let mut row = String::with_capacity(256);
        let _ = write!(row, "{}", wall.as_micros());
        for snap in diag.snapshot() {
            let _ = write!(
                row,
                ",{},{},{},{},{},{},{},{}",
                snap.in_count,
                snap.in_bytes,
                snap.out_count,
                snap.out_bytes,
                snap.occupancy,
                snap.occupancy_bytes,
                snap.drops,
                snap.errors
            );
        }
        let _ = write!(row, ",{}", diag.audio_trimmed());
        let _ = write!(row, ",{}", diag.av_offset());
        let bank = diag.bank();
        let _ = write!(
            row,
            ",{},{},{},{},{}",
            bank.lag.as_micros(),
            bank.target_lag.as_micros(),
            bank.reanchors,
            bank.reanchor_total.as_micros(),
            bank.stall_total.as_micros()
        );
        self.rows.push(row);
    }

    pub fn rows(&self) -> &[String] {
        &self.rows
    }

    /// Writes the capture and flushes it. The sink is taken by value, so
    /// returning without flushing would leave a buffered writer's tail to
    /// its `Drop`, which discards the error by design: an `Ok` here has to
    /// mean the rows reached the sink, not that they reached its buffer.
    pub fn write_csv<W: std::io::Write>(&self, w: W) -> std::io::Result<()> {
        self.write_csv_rows(w, true)
    }

    /// As [`Self::write_csv`], with the header suppressed. Appending a
    /// second capture to a file that already has one would otherwise put a
    /// header row in the middle of the data, which every reader of this
    /// format would take as a row.
    pub fn write_csv_rows<W: std::io::Write>(&self, mut w: W, header: bool) -> std::io::Result<()> {
        if header {
            writeln!(w, "{}", Self::header())?;
        }
        for row in &self.rows {
            writeln!(w, "{row}")?;
        }
        w.flush()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// The column contract is stable: this failing means downstream
    /// analysis tooling breaks. Append columns; never rename or reorder.
    #[test]
    fn column_contract_is_stable() {
        let header = CaptureRecorder::header();
        assert!(header.starts_with("wall_us,source_in_count,"));
        // wall_us + the stage block + the two appended session columns +
        // the Bank's schedule block.
        assert_eq!(
            header.split(',').count(),
            1 + STAGE_COUNT * 8 + 2 + BANK_COLUMNS.len()
        );
        assert!(header.contains(",bank_occupancy_bytes,"));
        assert!(header.contains(",clock_errors,"));
        assert!(header.contains(",audio_trimmed_frames,av_offset_us,bank_lag_us,"));
        assert!(header.ends_with(",bank_reanchor_total_us,bank_stall_total_us"));
    }

    fn column(header: &str, name: &str) -> usize {
        header
            .split(',')
            .position(|c| c == name)
            .unwrap_or_else(|| panic!("no column {name}"))
    }

    /// Both appended session columns reach the capture, in the order the
    /// header states. Asserted by position rather than by suffix: a suffix
    /// check silently becomes a check of whichever column was appended last.
    #[test]
    fn the_appended_session_columns_reach_the_capture() {
        let diag = SessionDiag::default();
        diag.set_audio_trimmed(21_535);
        diag.set_av_offset(-27_475);
        let mut rec = CaptureRecorder::default();
        rec.sample(MediaTime::from_millis(16), &diag);
        let fields: Vec<&str> = rec.rows()[0].split(',').collect();
        let header_line = CaptureRecorder::header();
        assert_eq!(fields.len(), header_line.split(',').count());
        assert_eq!(
            fields[column(&header_line, "audio_trimmed_frames")],
            "21535"
        );
        assert_eq!(fields[column(&header_line, "av_offset_us")], "-27475");
    }

    /// Each Bank reading lands under its own header, with the unit the
    /// name states. Asserted by name so the row cannot pass with the
    /// block's fields transposed.
    #[test]
    fn the_bank_readings_reach_the_capture_under_their_names() {
        let diag = SessionDiag::default();
        diag.set_bank(BankReadings {
            lag: MediaTime::from_millis(1_470),
            target_lag: MediaTime::from_millis(1_461),
            reanchors: 3,
            reanchor_total: MediaTime::from_millis(910),
            stall_total: MediaTime::from_millis(640),
        });
        assert_eq!(diag.bank().reanchors, 3);
        let mut rec = CaptureRecorder::default();
        rec.sample(MediaTime::from_millis(16), &diag);
        let fields: Vec<&str> = rec.rows()[0].split(',').collect();
        let header = CaptureRecorder::header();
        assert_eq!(fields[column(&header, "bank_lag_us")], "1470000");
        assert_eq!(fields[column(&header, "bank_target_lag_us")], "1461000");
        assert_eq!(fields[column(&header, "bank_reanchors")], "3");
        assert_eq!(fields[column(&header, "bank_reanchor_total_us")], "910000");
        assert_eq!(fields[column(&header, "bank_stall_total_us")], "640000");
    }

    /// The unknown sentinel survives the round trip: a capture taken before
    /// audio and a presented frame both exist must read as "no value", not as
    /// an offset of zero.
    #[test]
    fn an_unmeasurable_av_offset_reaches_the_capture_as_the_sentinel() {
        let diag = SessionDiag::default();
        let mut rec = CaptureRecorder::default();
        rec.sample(MediaTime::from_millis(16), &diag);
        let fields: Vec<&str> = rec.rows()[0].split(',').collect();
        let header = CaptureRecorder::header();
        assert_eq!(
            fields[column(&header, "av_offset_us")],
            i32::MIN.to_string(),
            "default must be the sentinel"
        );
    }

    /// The free-text channel is the one that works with no session open,
    /// which is where the failures that cost the most to diagnose happen.
    /// It reaches the process log with a level rather than only reaching a
    /// platform sink a user's machine has no way to read.
    #[test]
    fn a_free_text_line_reaches_the_process_log() {
        let _globals = GLOBALS.lock().unwrap_or_else(|e| e.into_inner());
        let _ = drain_log(usize::MAX);
        set_log_sink(swallow);

        log("plain info");
        crate::diag_warn!("warned {}", 7);
        crate::diag_err!("failed");

        let lines = drain_log(64);
        *SINK.lock().unwrap_or_else(|e| e.into_inner()) = None;

        assert_eq!(lines.len(), 3);
        assert!(
            lines
                .iter()
                .all(|r| r.code == EventCode::Log && r.session == 0)
        );
        assert_eq!(
            (lines[0].level, &*lines[0].detail),
            (Level::Info, "plain info")
        );
        assert_eq!(
            (lines[1].level, &*lines[1].detail),
            (Level::Warn, "warned 7")
        );
        assert_eq!(
            (lines[2].level, &*lines[2].detail),
            (Level::Error, "failed")
        );
        assert!(drain_log(64).is_empty(), "a drained log is empty");
    }

    /// A headless run drains nothing, so the ring spends the whole session
    /// full. Refusing new lines there would freeze the process log at
    /// whatever the first half-second contained; it drops its oldest
    /// instead, which is the opposite of the session event log's policy.
    #[test]
    fn a_full_process_log_drops_its_oldest_line() {
        let _globals = GLOBALS.lock().unwrap_or_else(|e| e.into_inner());
        let _ = drain_log(usize::MAX);
        let dropped_before = log_dropped();

        for i in 0..LOG_RING_CAP + 3 {
            push_log_record(LogRecord {
                wall_us: i as i64,
                session: 0,
                level: Level::Info,
                code: EventCode::Log,
                stage: Stage::Source,
                detail: format!("line {i}"),
            });
        }

        let lines = drain_log(usize::MAX);
        assert_eq!(lines.len(), LOG_RING_CAP);
        assert_eq!(lines[0].detail, "line 3");
        assert_eq!(
            lines[LOG_RING_CAP - 1].detail,
            format!("line {}", LOG_RING_CAP + 2)
        );
        assert_eq!(log_dropped() - dropped_before, 3);
    }

    /// Severity belongs to the code rather than to each of the twenty
    /// sites that raise one, so the mapping is worth pinning: an `Error`
    /// event that reported as `Info` would be filtered out by exactly the
    /// setting a user turns on to cut the noise.
    #[test]
    fn an_events_level_follows_its_code() {
        let diag = SessionDiag::default();
        for code in [EventCode::Error, EventCode::AudioTrim, EventCode::Seek] {
            diag.event(MediaTime::ZERO, code, Stage::Bank, "detail");
        }
        let levels: Vec<Level> = diag.take_events().iter().map(|e| e.level).collect();
        assert_eq!(levels, [Level::Error, Level::Warn, Level::Info]);
    }

    #[test]
    fn rows_match_header_width() {
        let diag = SessionDiag::default();
        diag.stage(Stage::Bank)
            .in_count
            .fetch_add(3, Ordering::Relaxed);
        let mut rec = CaptureRecorder::default();
        rec.sample(MediaTime::from_millis(16), &diag);
        let header_cols = CaptureRecorder::header().split(',').count();
        assert_eq!(rec.rows()[0].split(',').count(), header_cols);
    }

    #[test]
    fn event_log_caps_visibly() {
        let diag = SessionDiag::new(2);
        for i in 0..3 {
            diag.event(
                MediaTime::from_millis(i),
                EventCode::CapHit,
                Stage::Bank,
                "byte cap",
            );
        }
        assert_eq!(diag.take_events().len(), 2);
        assert_eq!(diag.events_dropped(), 1);
    }

    #[test]
    fn a_bounded_drain_leaves_the_rest_queued() {
        let diag = SessionDiag::default();
        for i in 0..5 {
            diag.event(
                MediaTime::from_millis(i),
                EventCode::CapHit,
                Stage::Bank,
                "byte cap",
            );
        }

        let first = diag.take_events_up_to(2);
        assert_eq!(first.len(), 2);
        assert_eq!(first[0].wall, MediaTime::from_millis(0));
        assert_eq!(first[1].wall, MediaTime::from_millis(1));

        // The three the buffer could not carry are still there, in order,
        // rather than having gone with the ones that were taken.
        let rest = diag.take_events_up_to(64);
        assert_eq!(rest.len(), 3);
        assert_eq!(rest[0].wall, MediaTime::from_millis(2));
        assert_eq!(rest[2].wall, MediaTime::from_millis(4));

        assert!(diag.take_events_up_to(64).is_empty());
        assert_eq!(diag.events_dropped(), 0);
    }

    /// Accepts every byte and fails only when asked to flush: the shape a
    /// file presents when the volume fills while its buffer is still held.
    struct FlushFails;

    impl std::io::Write for FlushFails {
        fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
            Ok(buf.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Err(std::io::Error::other("no space left on device"))
        }
    }

    static CAPTURED: Mutex<Vec<String>> = Mutex::new(Vec::new());

    /// The sink and the capture behind it are process-wide and the harness
    /// runs rows in parallel, so a row touching either takes this first, or
    /// one row's lines land in another row's expectations.
    static GLOBALS: Mutex<()> = Mutex::new(());

    fn capture(line: &str) {
        CAPTURED
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(line.to_string());
    }

    /// For a row that cares about the ring and not the sink: the default
    /// stderr branch would put its lines in the harness's output.
    fn swallow(_line: &str) {}

    /// A launcher that piped the player's stderr and then exited leaves
    /// every later write failing. Logging must carry on regardless. The
    /// child waits on stdin until the parent has closed the read end, so
    /// every line meets a closed pipe rather than a pipe buffer.
    #[test]
    fn a_line_to_a_stderr_nobody_reads_is_dropped_quietly() {
        const CHILD: &str = "MEDIA_DIAG_CLOSED_STDERR_CHILD";
        if std::env::var_os(CHILD).is_some() {
            let mut release = [0u8; 1];
            std::io::Read::read_exact(&mut std::io::stdin().lock(), &mut release).unwrap();
            for i in 0..10_000 {
                log(&format!("line {i} to a closed pipe"));
            }
            return;
        }
        let mut child = std::process::Command::new(std::env::current_exe().unwrap())
            .args([
                "tests::a_line_to_a_stderr_nobody_reads_is_dropped_quietly",
                "--exact",
                "--nocapture",
            ])
            .env(CHILD, "1")
            .stdin(std::process::Stdio::piped())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::piped())
            .spawn()
            .unwrap();
        drop(child.stderr.take());
        let mut release = child.stdin.take().unwrap();
        std::io::Write::write_all(&mut release, &[1]).unwrap();
        drop(release);
        let status = child.wait().unwrap();
        assert!(status.success(), "the logging child failed: {status}");
    }

    fn capture_marked(line: &str) {
        CAPTURED
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push(format!("replaced: {line}"));
    }

    /// One row rather than three because the sink is process-wide state,
    /// so separate rows would race each other under the test harness.
    /// Covers delivery, the macro's formatting and replacement; the
    /// default stderr path is the one branch no in-process row can read.
    #[test]
    fn an_installed_sink_takes_every_line() {
        let _globals = GLOBALS.lock().unwrap_or_else(|e| e.into_inner());
        set_log_sink(capture);
        log("plain");
        crate::diag_log!("formatted {} {}", 1, "two");
        set_log_sink(capture_marked);
        crate::diag_log!("after replacing");
        let lines = CAPTURED.lock().unwrap_or_else(|e| e.into_inner()).clone();
        // Put the default back before asserting: the sink is process-wide,
        // the harness runs rows in parallel, and a row that panicked here
        // would leave the capture installed for whatever ran next.
        *SINK.lock().unwrap_or_else(|e| e.into_inner()) = None;
        assert_eq!(
            lines,
            ["plain", "formatted 1 two", "replaced: after replacing"]
        );
    }

    /// Fails every write. Behind a buffer larger than the capture, the
    /// first write it ever sees is the one the flush makes.
    struct WriteFails;

    impl std::io::Write for WriteFails {
        fn write(&mut self, _buf: &[u8]) -> std::io::Result<usize> {
            Err(std::io::Error::other("no space left on device"))
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    fn one_row() -> CaptureRecorder {
        let diag = SessionDiag::default();
        let mut rec = CaptureRecorder::default();
        rec.sample(MediaTime::from_millis(16), &diag);
        rec
    }

    /// The capture is the post-mortem channel for a failed session, so a
    /// write that never reached the file must not return Ok. The sink is
    /// taken by value and dropped inside the call, and a buffered writer's
    /// Drop flushes with the error discarded, so the flush has to be made
    /// and reported here or nowhere.
    #[test]
    fn a_failing_flush_is_reported() {
        let rec = one_row();
        assert!(rec.write_csv(FlushFails).is_err());
    }

    /// Nothing was written, so only the flush can fail, and it still has to
    /// be made and reported.
    #[test]
    fn an_empty_capture_still_flushes() {
        let rec = CaptureRecorder::default();
        assert!(rec.write_csv_rows(FlushFails, false).is_err());
    }

    /// The call sites all pass a `BufWriter`. Sized past the whole capture
    /// so no row can spill early, which is what makes the tail flush the
    /// only write and this row the shipped failure mode rather than an
    /// overflow the `?` on `writeln!` would have caught anyway.
    #[test]
    fn a_buffered_tail_that_never_lands_is_reported() {
        let rec = one_row();
        let bytes = CaptureRecorder::header().len() + rec.rows()[0].len() + 2;
        let w = std::io::BufWriter::with_capacity(bytes * 4, WriteFails);
        assert!(rec.write_csv(w).is_err());
    }
}
