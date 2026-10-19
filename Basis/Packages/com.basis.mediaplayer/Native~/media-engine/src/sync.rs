//! Shared-playback soft sync target. Receivers feed in the owner's
//! extrapolated position and the engine runs the correction ladder: a dead
//! band with no action, then a bounded slew, then a seek past a large
//! threshold.
//!
//! How the slew is applied depends on the clock master:
//!
//! - **Audio master:** the engine publishes the wanted rate offset
//!   (`PipelineShared::sync_rate_ppm`, surfaced in the ABI snapshot) and
//!   the managed audio pull consumes source frames at `1x + offset`
//!   through its resampler. The audio playhead moves faster or slower, the
//!   clock follows it and video follows the clock, so A/V stays aligned
//!   during the correction. A consumer that ignores the rate lets the
//!   error grow until the seek rung takes it.
//! - **Wall master** (no audio track): the engine slews the clock
//!   directly ([`media_clock::MediaClock::slew_wall`]).
//!
//! Live sessions ignore sync targets. The stream clock is authoritative
//! and depth is per-viewer latency; divergence is bounded by the Bank's
//! lag cap (`OpenRequest::max_divergence_ms`) instead.

use std::sync::Mutex;
use std::sync::atomic::Ordering;

use media_clock::MediaTime;

use crate::State;
use crate::pipeline::PipelineShared;

/// Errors inside the band are left alone: owner heartbeat extrapolation
/// carries network jitter well beyond the clock's 20 ms A/V dead band,
/// and chasing it would keep the rate oscillating.
pub const SYNC_DEAD_BAND: MediaTime = MediaTime::from_millis(150);
/// Beyond this the slew would take too long to converge and the viewer
/// is visibly elsewhere, so seek.
pub const SYNC_SEEK_THRESHOLD: MediaTime = MediaTime::from_secs(2);
/// Slew magnitude, ppm of 1x. Matches the clock's slew cap so an
/// audio-master correction can be followed at full rate (2%,
/// well under the ~6% audibility bound).
pub const SYNC_SLEW_PPM: i64 = 20_000;

/// The last reported target: owner position at `wall`, extrapolated at
/// 1x between reports.
#[derive(Debug, Clone, Copy)]
pub(crate) struct SyncTarget {
    pub pos: MediaTime,
    pub wall: MediaTime,
}

#[derive(Default)]
pub(crate) struct SyncShared {
    pub target: Mutex<Option<SyncTarget>>,
}

/// What one evaluation decided. Pure, so it is unit-testable without a
/// session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SyncAction {
    None,
    Slew { rate_ppm: i64 },
    Seek { to: MediaTime },
}

/// The ladder on a raw error (target − position).
pub fn ladder(error: MediaTime) -> SyncAction {
    if error.abs() >= SYNC_SEEK_THRESHOLD {
        SyncAction::Seek {
            to: MediaTime::ZERO, // caller substitutes the extrapolated target
        }
    } else if error.abs() <= SYNC_DEAD_BAND {
        SyncAction::None
    } else if error > MediaTime::ZERO {
        SyncAction::Slew {
            rate_ppm: SYNC_SLEW_PPM,
        }
    } else {
        SyncAction::Slew {
            rate_ppm: -SYNC_SLEW_PPM,
        }
    }
}

/// Set (or clear, `position_us < 0`) the sync target and run one
/// evaluation. Cheap enough to call every host frame with the caller's
/// freshly extrapolated owner position; the engine extrapolates at 1x
/// between calls anyway, so a sparser cadence (the 5 s heartbeat itself)
/// also converges.
pub(crate) fn set_sync_target(px: &PipelineShared, position_us: i64) {
    if position_us < 0 {
        *px.sync.target.lock().expect("sync lock") = None;
        clear_rate(px);
        return;
    }
    let wall = px.wall.now();
    *px.sync.target.lock().expect("sync lock") = Some(SyncTarget {
        pos: MediaTime::from_micros(position_us),
        wall,
    });
    evaluate(px, wall);
}

fn clear_rate(px: &PipelineShared) {
    px.sync_rate_ppm.store(0, Ordering::Relaxed);
    let wall = px.wall.now();
    px.clock.lock().expect("clock lock").slew_wall(wall, 0);
}

/// One ladder pass against the current position. Corrections apply only
/// to a playing on-demand session: live sessions never chase, and a
/// buffering, paused or seeking session is left to settle first.
pub(crate) fn evaluate(px: &PipelineShared, wall: MediaTime) {
    let Some(target) = *px.sync.target.lock().expect("sync lock") else {
        return;
    };
    if px.state() != State::Playing as u32
        || !px.clock_playing.load(Ordering::Relaxed)
        || px.live.load(Ordering::Relaxed)
    {
        clear_rate(px);
        return;
    }
    let target_now = target.pos + (wall - target.wall).max(MediaTime::ZERO);
    let position = {
        let clock = px.clock.lock().expect("clock lock");
        clock.now(wall)
    };
    let error = target_now - position;
    let action = match ladder(error) {
        SyncAction::Seek { .. } => SyncAction::Seek { to: target_now },
        other => other,
    };
    match action {
        SyncAction::None => {
            if px.sync_rate_ppm.swap(0, Ordering::Relaxed) != 0 {
                px.clock.lock().expect("clock lock").slew_wall(wall, 0);
                px.diag.event(
                    wall,
                    media_diag::EventCode::SyncSlew,
                    media_diag::Stage::Clock,
                    format!("released at error {error}"),
                );
            }
        }
        SyncAction::Slew { rate_ppm } => {
            if px.sync_rate_ppm.swap(rate_ppm, Ordering::Relaxed) != rate_ppm {
                px.diag.event(
                    wall,
                    media_diag::EventCode::SyncSlew,
                    media_diag::Stage::Clock,
                    format!("error {error}, rate {rate_ppm} ppm"),
                );
            }
            px.clock
                .lock()
                .expect("clock lock")
                .slew_wall(wall, rate_ppm);
        }
        SyncAction::Seek { to } => {
            px.sync_rate_ppm.store(0, Ordering::Relaxed);
            px.diag.event(
                wall,
                media_diag::EventCode::SyncSeek,
                media_diag::Stage::Clock,
                format!("error {error}, seeking to {to}"),
            );
            crate::seek_px(px, to);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn dead_band_holds() {
        assert_eq!(ladder(MediaTime::from_millis(0)), SyncAction::None);
        assert_eq!(ladder(MediaTime::from_millis(150)), SyncAction::None);
        assert_eq!(ladder(MediaTime::from_millis(-150)), SyncAction::None);
    }

    #[test]
    fn slew_band_signs() {
        assert_eq!(
            ladder(MediaTime::from_millis(151)),
            SyncAction::Slew {
                rate_ppm: SYNC_SLEW_PPM
            }
        );
        assert_eq!(
            ladder(MediaTime::from_millis(-500)),
            SyncAction::Slew {
                rate_ppm: -SYNC_SLEW_PPM
            }
        );
        assert_eq!(
            ladder(MediaTime::from_millis(1999)),
            SyncAction::Slew {
                rate_ppm: SYNC_SLEW_PPM
            }
        );
    }

    #[test]
    fn seek_is_the_last_rung() {
        assert!(matches!(
            ladder(MediaTime::from_secs(2)),
            SyncAction::Seek { .. }
        ));
        assert!(matches!(
            ladder(MediaTime::from_secs(-30)),
            SyncAction::Seek { .. }
        ));
    }
}
