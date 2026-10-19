//! Render-event frame selection. Which frame is due is decided in the
//! Unity render event, which runs at the display cadence, with one vsync
//! of lookahead. Selection and display then share one clock, so a beat
//! between a decode tick and vsync cannot push isolated frames a vsync
//! late or early. The video thread keeps a tick-paced fallback for
//! consumers that issue no render events (headless sessions, a
//! non-rendering app): selection hands over on the first stamped event and
//! hands back after [`PRESENT_LIVENESS`] without one.
//!
//! The render thread reads session time from a lock-free clock mirror: a
//! single `clock_now − wall` offset atomic, written beside every
//! `set_playing` call, refreshed each 4 ms decode-thread tick and
//! `i64::MIN` while the clock is parked. One atomic cannot tear, and
//! staleness costs at most the slew cap over one tick (under 0.1 ms at the
//! normal 2% cap).

use std::sync::atomic::{AtomicI64, Ordering};

use media_clock::MediaTime;
#[cfg(any(windows, target_os = "android"))]
use media_diag::Stage;

#[cfg(any(windows, target_os = "android"))]
use crate::State;
#[cfg(any(windows, target_os = "android"))]
use crate::pipeline::PipelineShared;
#[cfg(any(windows, target_os = "android", test))]
use crate::pool::FramePool;
#[cfg(any(windows, target_os = "android", test))]
use crate::pool::Lease;

/// A render event within this window marks the render consumer live, and
/// the video thread's fallback selection stands down. Same figure as the
/// audio consumer's liveness window.
pub const PRESENT_LIVENESS: MediaTime = MediaTime::from_millis(500);

/// Lookahead clamp: one vsync at any sane display rate (500 Hz .. 25 Hz).
const LOOKAHEAD_MIN_US: i64 = 2_000;
const LOOKAHEAD_MAX_US: i64 = 40_000;
/// Inter-event deltas outside this range are app hitches or double
/// events, not display cadence, and are left out of the estimate.
const DELTA_MIN_US: i64 = 1_000;
const DELTA_MAX_US: i64 = 250_000;
/// Seed until two events have been observed (a 60 Hz-ish guess; the EMA
/// converges within a handful of frames).
const INTERVAL_SEED_US: i64 = 16_667;

/// Render-event state shared between the render thread (stamps, selects)
/// and the video thread (liveness gate, clock mirror refresh).
pub struct PresentShared {
    /// Wall µs of the last render event; `i64::MIN` = none ever.
    pub last_event_wall_us: AtomicI64,
    /// Smoothed inter-event interval µs: the vsync estimate.
    pub interval_us: AtomicI64,
    /// Lock-free clock mirror: `clock.now(wall) − wall` µs, `i64::MIN`
    /// while the clock is parked (which disables selection).
    pub clock_offset_us: AtomicI64,
    /// Render events between selecting a frame and its copy reaching the
    /// host's texture, plus one. 1 where the copy lands in the selecting
    /// event (D3D11, Vulkan); 2 on Direct3D 12, where it lands in the next.
    pub lookahead_events: AtomicI64,
}

impl PresentShared {
    pub fn new() -> Self {
        Self {
            last_event_wall_us: AtomicI64::new(i64::MIN),
            interval_us: AtomicI64::new(INTERVAL_SEED_US),
            clock_offset_us: AtomicI64::new(i64::MIN),
            lookahead_events: AtomicI64::new(1),
        }
    }

    /// Stamp a render event: consumer liveness plus the interval EMA.
    /// Called once per event, before selection.
    pub fn note_event(&self, wall: MediaTime) {
        let wall_us = wall.as_micros();
        let last = self.last_event_wall_us.swap(wall_us, Ordering::Relaxed);
        if last == i64::MIN {
            return;
        }
        let delta = wall_us - last;
        if !(DELTA_MIN_US..=DELTA_MAX_US).contains(&delta) {
            return;
        }
        let old = self.interval_us.load(Ordering::Relaxed);
        self.interval_us
            .store(old + (delta - old) / 8, Ordering::Relaxed);
    }

    /// The render consumer is live: an event stamped within the window.
    pub fn consumer_live(&self, wall: MediaTime) -> bool {
        let last = self.last_event_wall_us.load(Ordering::Relaxed);
        last != i64::MIN && wall - MediaTime::from_micros(last) <= PRESENT_LIVENESS
    }

    /// Session time the selection grades against: mirrored clock plus one
    /// vsync of lookahead, because the frame chosen now should be on screen
    /// during the *upcoming* refresh. Returned beside the clock's own
    /// reading, which lateness is measured from. Where the copy lands
    /// events later (`lookahead_events`), both move on by those events, so
    /// the frame is chosen and judged for when it is seen. `None` while
    /// parked.
    pub fn selection_target(&self, wall: MediaTime) -> Option<(MediaTime, MediaTime)> {
        let offset = self.clock_offset_us.load(Ordering::Relaxed);
        if offset == i64::MIN {
            return None;
        }
        let vsync = self
            .interval_us
            .load(Ordering::Relaxed)
            .clamp(LOOKAHEAD_MIN_US, LOOKAHEAD_MAX_US);
        let delay = vsync * (self.lookahead_events.load(Ordering::Relaxed) - 1).max(0);
        let clock = wall + MediaTime::from_micros(offset);
        Some((
            clock + MediaTime::from_micros(delay + vsync),
            clock + MediaTime::from_micros(delay),
        ))
    }

    /// Mirror the clock for the render thread. Call under the clock lock
    /// (or from a site that just read the clock consistently).
    pub fn mirror_clock(&self, wall: MediaTime, now: MediaTime, playing: bool) {
        let offset = if playing {
            (now - wall).as_micros()
        } else {
            i64::MIN
        };
        self.clock_offset_us.store(offset, Ordering::Relaxed);
    }
}

impl Default for PresentShared {
    fn default() -> Self {
        Self::new()
    }
}

/// One render-event selection: the newest due frame against the mirrored
/// clock with a vsync of lookahead. `None` = parked clock, nothing due,
/// or momentary pool contention (re-present the previous output).
#[cfg(any(windows, target_os = "android", test))]
pub fn select_for_render(
    shared: &PresentShared,
    pool: &FramePool,
    wall: MediaTime,
) -> Option<Lease> {
    let (target, clock) = shared.selection_target(wall)?;
    pool.try_take_due(target, clock)
}

/// Presentation bookkeeping, wherever selection ran: presented pts, the
/// Present out-count and the Buffering to Playing transition.
#[cfg(any(windows, target_os = "android"))]
fn presented(px: &PipelineShared, pts: MediaTime, generation: u64) {
    px.diag
        .stage(Stage::Present)
        .out_count
        .fetch_add(1, Ordering::Relaxed);
    px.presented_pts_us
        .store(pts.as_micros(), Ordering::Relaxed);
    crate::pipeline::note_presented(px, generation);
    px.shown_generation.store(generation, Ordering::Relaxed);
    px.leave_buffering(generation);
}

/// The Windows render event's engine half: stamp consumer liveness,
/// select against the mirrored clock, run the conversion pass into the
/// shared texture, and do the presentation bookkeeping. Returns true when
/// a fresh frame was converted (the caller then runs its keyed-mutex
/// copy). Never blocks: the pool take and the presenter are try-locks,
/// and a contended tick just re-presents the previous output.
#[cfg(windows)]
pub fn render_present(px: &PipelineShared) -> bool {
    let wall = px.wall.now();
    px.present.note_event(wall);
    let state = px.state();
    if state == State::Paused as u32 || state == State::Error as u32 {
        return false;
    }
    let Some(lease) = select_for_render(&px.present, &px.pool, wall) else {
        return false;
    };
    let result = {
        let Ok(mut slot) = px.presenter.try_lock() else {
            // Configure in flight on the video thread: skip this vsync.
            px.pool.release(lease);
            return false;
        };
        match slot.as_mut() {
            Some(presenter) => crate::sink::present_lease_frame(presenter, &lease),
            None => Ok(false),
        }
    };
    let fresh = match result {
        Ok(fresh) => fresh,
        Err(e) => {
            px.pool.release(lease);
            px.fail(crate::EngineError::present(e));
            return false;
        }
    };
    if fresh {
        presented(px, lease.pts, lease.generation);
    }
    px.pool.release(lease);
    fresh
}

/// The Android render event's engine half: stamp, select, do the
/// bookkeeping and hand the frame to the Vulkan conversion pass, whose GPU
/// lifetime the caller's renderer manages. `None` = nothing new this vsync.
#[cfg(target_os = "android")]
pub fn render_take(px: &PipelineShared) -> Option<media_decode::VideoFrame> {
    let wall = px.wall.now();
    px.present.note_event(wall);
    let state = px.state();
    if state == State::Paused as u32 || state == State::Error as u32 {
        return None;
    }
    let mut lease = select_for_render(&px.present, &px.pool, wall)?;
    let frame = lease.take_frame();
    if frame.is_some() {
        presented(px, lease.pts, lease.generation);
    }
    px.pool.release(lease);
    frame
}

#[cfg(test)]
mod tests {
    use super::*;
    use media_decode::{ColorInfo, Nv12Frame, VideoFrame};

    fn frame(pts_us: i64) -> VideoFrame {
        VideoFrame::Nv12(Nv12Frame {
            width: 2,
            height: 2,
            pts_us,
            color: ColorInfo::default(),
            data: vec![0u8; 8],
        })
    }

    #[test]
    fn parked_clock_selects_nothing() {
        let shared = PresentShared::new();
        let pool = FramePool::new();
        assert!(pool.try_publish(frame(0), 0).is_ok());
        assert!(select_for_render(&shared, &pool, MediaTime::from_millis(100)).is_none());
        shared.mirror_clock(MediaTime::from_millis(100), MediaTime::ZERO, true);
        assert!(select_for_render(&shared, &pool, MediaTime::from_millis(100)).is_some());
    }

    /// Where the copy lands a render event after selection, the frame is
    /// chosen for, and its lateness judged at, the refresh it is seen on.
    #[test]
    fn a_copy_one_event_late_moves_selection_and_lateness_on_a_refresh() {
        let shared = PresentShared::new();
        shared.mirror_clock(MediaTime::ZERO, MediaTime::ZERO, true);
        let wall = MediaTime::from_secs(1);
        let (target, clock) = shared.selection_target(wall).expect("running");
        assert_eq!(target.as_micros(), 1_000_000 + INTERVAL_SEED_US);
        assert_eq!(clock.as_micros(), 1_000_000);

        shared.lookahead_events.store(2, Ordering::Relaxed);
        let (target, clock) = shared.selection_target(wall).expect("running");
        assert_eq!(target.as_micros(), 1_000_000 + 2 * INTERVAL_SEED_US);
        assert_eq!(clock.as_micros(), 1_000_000 + INTERVAL_SEED_US);
    }

    #[test]
    fn interval_ema_tracks_cadence_and_rejects_hitches() {
        let shared = PresentShared::new();
        let mut wall = 0i64;
        for _ in 0..200 {
            wall += 13_889; // 72 Hz
            shared.note_event(MediaTime::from_micros(wall));
        }
        let est = shared.interval_us.load(Ordering::Relaxed);
        assert!((est - 13_889).abs() < 200, "EMA should converge: {est}");
        // A 2 s app hitch must not pollute the estimate.
        wall += 2_000_000;
        shared.note_event(MediaTime::from_micros(wall));
        assert_eq!(shared.interval_us.load(Ordering::Relaxed), est);
    }

    #[test]
    fn liveness_window_bounds_the_handover() {
        let shared = PresentShared::new();
        assert!(!shared.consumer_live(MediaTime::from_secs(10)));
        shared.note_event(MediaTime::from_secs(10));
        assert!(shared.consumer_live(MediaTime::from_secs(10)));
        assert!(shared.consumer_live(MediaTime::from_micros(10_500_000)));
        assert!(!shared.consumer_live(MediaTime::from_micros(10_500_001)));
    }

    /// 24 fps content selected by 72 Hz render events. Whatever the phase
    /// between the content grid and the event grid, every frame is selected
    /// exactly once, exactly three events apart, with no 4-then-2 or
    /// 2-then-4 pairs when the due phase sits near a vsync boundary.
    #[test]
    fn steady_grid_selects_every_frame_at_the_ideal_hold() {
        const VSYNC_US: i64 = 13_889;
        const FRAME_US: i64 = 41_667;
        for phase_us in (0..VSYNC_US).step_by(500) {
            let shared = PresentShared::new();
            let pool = FramePool::new();
            // Clock runs 1:1 with wall from 0.
            shared.mirror_clock(MediaTime::ZERO, MediaTime::ZERO, true);
            // Warm the interval estimate as a running session would have.
            let mut wall = phase_us - 200 * VSYNC_US;
            for _ in 0..200 {
                shared.note_event(MediaTime::from_micros(wall));
                wall += VSYNC_US;
            }
            assert_eq!(wall, phase_us);

            let mut next_pts = 0i64;
            let mut selections: Vec<(i64, i64)> = Vec::new(); // (event index, pts)
            for event in 0..360 {
                let now = MediaTime::from_micros(wall);
                // Decode runs ahead: keep the pool topped up with the next
                // couple of frames, as the pipeline's cushion does.
                while pool.try_publish(frame(next_pts), 0).is_ok() {
                    next_pts += FRAME_US;
                }
                shared.note_event(now);
                if let Some(lease) = select_for_render(&shared, &pool, now) {
                    selections.push((event, lease.pts.as_micros()));
                    pool.release(lease);
                }
                wall += VSYNC_US;
            }

            // Every frame exactly once, in order.
            let pts: Vec<i64> = selections.iter().map(|&(_, p)| p).collect();
            let expected: Vec<i64> = (0..pts.len() as i64).map(|i| i * FRAME_US).collect();
            assert_eq!(
                pts, expected,
                "phase {phase_us}: frames must not skip or repeat"
            );
            assert_eq!(pool.dropped(), 0, "phase {phase_us}: no newest-wins drops");
            // Every hold is exactly 3 events. The first may run short (the
            // first frame clamps to the loop's first event), so it is
            // skipped.
            let holds: Vec<i64> = selections.windows(2).map(|w| w[1].0 - w[0].0).collect();
            assert!(
                holds.iter().skip(1).all(|&h| h == 3),
                "phase {phase_us}: non-ideal holds {holds:?}"
            );
        }
    }

    /// Event-time jitter within the lookahead margin must not destabilise
    /// selection: the same grid with ±1.5 ms of deterministic wobble on
    /// each event still selects every frame once, and holds never pair a
    /// long with a short more than one apart (a 4 must not be followed by
    /// a 2).
    #[test]
    fn event_jitter_inside_the_margin_keeps_holds_stable() {
        const VSYNC_US: i64 = 13_889;
        const FRAME_US: i64 = 41_667;
        for phase_us in (0..VSYNC_US).step_by(1_000) {
            let shared = PresentShared::new();
            let pool = FramePool::new();
            shared.mirror_clock(MediaTime::ZERO, MediaTime::ZERO, true);
            let jitter = |k: i64| ((k * 5) % 7) * 500 - 1_500; // −1.5..+1.5 ms
            let mut wall = phase_us - 200 * VSYNC_US;
            for k in 0..200 {
                shared.note_event(MediaTime::from_micros(wall + jitter(k)));
                wall += VSYNC_US;
            }

            let mut next_pts = 0i64;
            let mut selections: Vec<(i64, i64)> = Vec::new();
            for event in 0..360 {
                let now = MediaTime::from_micros(wall + jitter(event + 200));
                while pool.try_publish(frame(next_pts), 0).is_ok() {
                    next_pts += FRAME_US;
                }
                shared.note_event(now);
                if let Some(lease) = select_for_render(&shared, &pool, now) {
                    selections.push((event, lease.pts.as_micros()));
                    pool.release(lease);
                }
                wall += VSYNC_US;
            }
            let pts: Vec<i64> = selections.iter().map(|&(_, p)| p).collect();
            let expected: Vec<i64> = (0..pts.len() as i64).map(|i| i * FRAME_US).collect();
            assert_eq!(
                pts, expected,
                "phase {phase_us}: frames must not skip or repeat"
            );
            let holds: Vec<i64> = selections[1..]
                .windows(2)
                .map(|w| w[1].0 - w[0].0)
                .collect();
            for pair in holds.windows(2) {
                assert!(
                    (pair[0] - 3).abs() + (pair[1] - 3).abs() <= 2,
                    "phase {phase_us}: beat pair {pair:?} in {holds:?}"
                );
            }
        }
    }
}
