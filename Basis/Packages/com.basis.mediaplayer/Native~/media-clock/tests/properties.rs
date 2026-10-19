//! Property tests for the drift ladder: schedule in → position out.

use media_clock::{ClockConfig, Correction, Generation, Master, MediaClock, MediaTime};
use proptest::prelude::*;

fn clock_at_zero() -> MediaClock {
    let mut c = MediaClock::new(
        ClockConfig::default(),
        MediaTime::ZERO,
        MediaTime::ZERO,
        Generation(0),
    );
    c.set_playing(MediaTime::ZERO, true);
    c.set_master(MediaTime::ZERO, Master::Audio);
    c
}

/// An observation schedule: (wall step µs, master error µs) pairs. Errors are
/// kept inside the snap threshold so the run stays in the slew regime.
fn slew_regime_schedule() -> impl Strategy<Value = Vec<(i64, i64)>> {
    prop::collection::vec((1_000i64..2_000_000, -699_000i64..699_000), 1..64)
}

proptest! {
    /// In the slew regime (no snaps), position never runs backwards.
    #[test]
    fn monotone_under_slew(schedule in slew_regime_schedule()) {
        let mut c = clock_at_zero();
        let mut wall = MediaTime::ZERO;
        let mut last = c.now(wall);
        for (step, err) in schedule {
            // Observe a master sitting `err` away from the current position.
            let target = c.now(wall) + MediaTime::from_micros(err);
            let corr = c.observe_master(wall, target);
            let snapped = matches!(corr, Correction::Snap { .. });
            prop_assert!(!snapped);
            wall += MediaTime::from_micros(step);
            let now = c.now(wall);
            prop_assert!(now >= last, "position went backwards: {last} -> {now}");
            last = now;
        }
    }

    /// The instantaneous rate stays within whichever ceiling is in force:
    /// the wide one for `fast_window` after the master is adopted, the 2%
    /// one from then on. Both halves are asserted by the same row, so a
    /// fast window that failed to close would fail here.
    #[test]
    fn rate_bounded(schedule in slew_regime_schedule()) {
        let cfg = ClockConfig::default();
        let mut c = clock_at_zero();
        let mut wall = MediaTime::ZERO;
        for (step, err) in schedule {
            let target = c.now(wall) + MediaTime::from_micros(err);
            c.observe_master(wall, target);
            // `clock_at_zero` adopts the audio master at wall 0, so the fast
            // window runs from there.
            let cap_ppm = if wall < cfg.fast_window {
                cfg.fast_slew_cap_ppm
            } else {
                cfg.slew_cap_ppm
            };
            let before = c.now(wall);
            wall += MediaTime::from_micros(step);
            let after = c.now(wall);
            let advance = (after - before).as_micros();
            let lo = step + step * -cap_ppm / 1_000_000 - 1;
            let hi = step + step * cap_ppm / 1_000_000 + 1;
            prop_assert!(advance >= lo && advance <= hi,
                "advance {advance} outside [{lo}, {hi}] for step {step} at wall {wall}");
        }
    }

    /// Past the fast window the ceiling is the steady-state cap, whatever
    /// the error.
    #[test]
    fn steady_state_rate_bounded_by_the_slew_cap(schedule in slew_regime_schedule()) {
        let cfg = ClockConfig::default();
        let mut c = clock_at_zero();
        // Past the fast window before the first observation.
        let mut wall = cfg.fast_window + MediaTime::from_millis(1);
        for (step, err) in schedule {
            let target = c.now(wall) + MediaTime::from_micros(err);
            c.observe_master(wall, target);
            let before = c.now(wall);
            wall += MediaTime::from_micros(step);
            let after = c.now(wall);
            let advance = (after - before).as_micros();
            let lo = step + step * -cfg.slew_cap_ppm / 1_000_000 - 1;
            let hi = step + step * cfg.slew_cap_ppm / 1_000_000 + 1;
            prop_assert!(advance >= lo && advance <= hi,
                "advance {advance} outside [{lo}, {hi}] for step {step}");
        }
    }

    /// A steady 1x master with a fixed offset inside the slew band is
    /// converged on: error falls to the dead band and stays there.
    #[test]
    fn converges_on_steady_master(offset_us in 21_000i64..699_000, sign in prop::bool::ANY) {
        let offset = if sign { offset_us } else { -offset_us };
        let cfg = ClockConfig::default();
        let mut c = clock_at_zero();
        // Master runs at exactly 1x, `offset` ahead of (or behind) the clock.
        let master = |wall: MediaTime| wall + MediaTime::from_micros(offset);
        let mut wall = MediaTime::ZERO;
        // At a 2% slew, 699 ms of error needs < 35 s; observe every 100 ms.
        let budget_steps = 400;
        let mut converged_at = None;
        for i in 0..budget_steps {
            let corr = c.observe_master(wall, master(wall));
            if matches!(corr, Correction::None) && i > 0 {
                converged_at = Some(wall);
                break;
            }
            let snapped = matches!(corr, Correction::Snap { .. });
            prop_assert!(!snapped);
            wall += MediaTime::from_millis(100);
        }
        let converged_at = converged_at.expect("never converged");
        // Once converged, it stays converged (no oscillation out of the band).
        for _ in 0..50 {
            wall += MediaTime::from_millis(100);
            let corr = c.observe_master(wall, master(wall));
            prop_assert!(matches!(corr, Correction::None),
                "left the dead band after converging at {converged_at}: {corr:?}");
            let err = (master(wall) - c.now(wall)).abs();
            prop_assert!(err <= cfg.dead_band);
        }
    }

    /// Beyond the snap threshold the clock jumps exactly to the master.
    #[test]
    fn snaps_beyond_threshold(err_us in 700_000i64..30_000_000, sign in prop::bool::ANY) {
        let err = if sign { err_us } else { -err_us };
        let mut c = clock_at_zero();
        let wall = MediaTime::from_secs(5);
        let target = c.now(wall) + MediaTime::from_micros(err);
        let corr = c.observe_master(wall, target);
        let snapped = matches!(corr, Correction::Snap { .. });
        prop_assert!(snapped);
        prop_assert_eq!(c.now(wall), target);
        prop_assert_eq!(c.rate_ppm(), 0);
    }

    /// Pause freezes position; resume continues from the same place.
    #[test]
    fn pause_freezes_position(pause_at in 0i64..10_000_000, pause_for in 0i64..60_000_000) {
        let mut c = clock_at_zero();
        let pause_wall = MediaTime::from_micros(pause_at);
        let frozen = c.now(pause_wall);
        c.set_playing(pause_wall, false);
        let mid = pause_wall + MediaTime::from_micros(pause_for / 2);
        let resume_wall = pause_wall + MediaTime::from_micros(pause_for);
        prop_assert_eq!(c.now(mid), frozen);
        c.set_playing(resume_wall, true);
        prop_assert_eq!(c.now(resume_wall), frozen);
    }

    /// A generation change snaps regardless of error size.
    #[test]
    fn generation_change_snaps(err_us in -100_000i64..100_000) {
        let mut c = clock_at_zero();
        let wall = MediaTime::from_secs(1);
        let target = c.now(wall) + MediaTime::from_micros(err_us);
        let corr = c.advance_generation(wall, Generation(1), target);
        let snapped = matches!(corr, Correction::Snap { .. });
        prop_assert!(snapped);
        prop_assert_eq!(c.now(wall), target);
        prop_assert_eq!(c.generation(), Generation(1));
    }
}

#[test]
fn wall_master_ignores_observations() {
    let mut c = MediaClock::new(
        ClockConfig::default(),
        MediaTime::ZERO,
        MediaTime::ZERO,
        Generation(0),
    );
    c.set_playing(MediaTime::ZERO, true);
    assert_eq!(c.master(), Master::Wall);
    let wall = MediaTime::from_secs(1);
    let corr = c.observe_master(wall, MediaTime::from_secs(30));
    assert_eq!(corr, Correction::None);
    assert_eq!(c.now(wall), MediaTime::from_secs(1));
}

#[test]
fn master_switch_does_not_move_position() {
    let mut c = clock_at_zero();
    let wall = MediaTime::from_secs(2);
    // Get a slew running, then switch to wall pacing mid-slew.
    c.observe_master(wall, c.now(wall) + MediaTime::from_millis(100));
    let wall2 = wall + MediaTime::from_millis(500);
    let before = c.now(wall2);
    c.set_master(wall2, Master::Wall);
    assert_eq!(c.now(wall2), before);
    assert_eq!(c.rate_ppm(), 0);
}

/// The wall-master rung: `slew_wall` never moves `now` at the call
/// instant, runs at 1x + ppm afterwards, clamps to the cap, and is inert
/// under the audio master (there the correction rides the playhead).
#[test]
fn slew_wall_shifts_rate_without_moving_now() {
    let mut c = clock_at_zero();
    c.set_master(MediaTime::ZERO, Master::Wall);
    let t1 = MediaTime::from_millis(1_000);
    let before = c.now(t1);
    c.slew_wall(t1, 20_000);
    assert_eq!(c.now(t1), before, "slew_wall must not move now");
    let t2 = t1 + MediaTime::from_secs(1);
    assert_eq!(
        c.now(t2) - before,
        MediaTime::from_secs(1) + MediaTime::from_millis(20),
        "1 s at +2% is 1.020 s"
    );
    c.slew_wall(t2, 0);
    let settled = c.now(t2);
    assert_eq!(
        c.now(t2 + MediaTime::from_secs(1)) - settled,
        MediaTime::from_secs(1)
    );
}

#[test]
fn slew_wall_clamps_to_the_cap_and_ignores_audio_master() {
    let mut c = clock_at_zero();
    c.set_master(MediaTime::ZERO, Master::Wall);
    c.slew_wall(MediaTime::ZERO, 1_000_000);
    assert_eq!(c.rate_ppm(), 20_000, "clamped to the slew cap");
    c.slew_wall(MediaTime::ZERO, -1_000_000);
    assert_eq!(c.rate_ppm(), -20_000);

    let mut audio = clock_at_zero();
    audio.slew_wall(MediaTime::ZERO, 20_000);
    assert_eq!(audio.rate_ppm(), 0, "audio master ignores slew_wall");
}

/// Convergence from an error the size of a bad live join (690 ms), which the
/// 2% cap alone needs ~34.5 s to close. The bound is 5 s: the fast window
/// (1.2 s at 50% of wall rate) absorbs ~600 ms and the steady cap clears the
/// last ~90 ms. The companion row covers the error a well-placed
/// presentation origin leaves.
#[test]
fn a_join_sized_error_converges_far_faster_than_the_cap_alone() {
    let at = converge_from(MediaTime::from_millis(690));
    // The cap alone would need 690 / 0.02 = 34_500 ms.
    assert!(
        at <= MediaTime::from_millis(5000),
        "converged at {at}, expected within 5 s"
    );
    assert!(
        at < MediaTime::from_millis(34_500),
        "no better than the cap alone"
    );
}

/// The error a well-placed presentation origin leaves: both legs banked at
/// the start point, so the clock begins close to its master. Sub-second.
#[test]
fn a_small_join_error_converges_within_a_second() {
    let at = converge_from(MediaTime::from_millis(100));
    assert!(
        at <= MediaTime::from_millis(1000),
        "converged at {at}, expected within 1 s"
    );
}

/// Drive a 1x master sitting `offset` ahead and return the wall time at which
/// the error first reaches the dead band.
fn converge_from(offset: MediaTime) -> MediaTime {
    let cfg = ClockConfig::default();
    let mut c = clock_at_zero();
    let mut wall = MediaTime::ZERO;
    let mut master = offset;
    let step = MediaTime::from_millis(20);
    for _ in 0..5_000 {
        c.observe_master(wall, master);
        wall += step;
        // A 1x master advances with wall time.
        master += step;
        if (master - c.now(wall)).abs() <= cfg.dead_band {
            return wall;
        }
    }
    panic!("never converged");
}

/// The correction decelerates as the error closes. A fixed-rate law runs at
/// the cap right up to the dead band and then drops to zero, overshooting
/// into a limit cycle between the two bounds. The discriminator is a strict
/// decrease while the rate is still non-zero: a fixed-rate law never has one.
#[test]
fn the_rate_falls_as_the_error_closes() {
    // The error starts inside the proportional region (below
    // `fast_slew_cap_ppm * slew_tau`) and the walk stays inside the fast
    // window: a fixed-rate law would also fall when the fast window expires
    // and the ceiling steps down.
    let cfg = ClockConfig::default();
    let mut c = clock_at_zero();
    let mut wall = MediaTime::ZERO;
    let step = MediaTime::from_millis(20);
    let mut master = MediaTime::from_millis(100);
    let mut previous: Option<i64> = None;
    let mut decreased = false;
    while wall < cfg.fast_window {
        c.observe_master(wall, master);
        let rate = c.rate_ppm();
        if rate == 0 {
            break;
        }
        assert!(
            rate < cfg.fast_slew_cap_ppm,
            "rate {rate} is at the ceiling, so a fall would only be the cap stepping down"
        );
        if let Some(prev) = previous
            && rate < prev
        {
            decreased = true;
        }
        previous = Some(rate);
        wall += step;
        master += step;
    }
    assert!(
        decreased,
        "rate never fell inside the fast window while non-zero; the law is still fixed-rate"
    );
}

/// The fast ceiling must stay under 1x or the clock could run backwards
/// while correcting, which no amount of downstream tolerance survives.
#[test]
fn the_fast_ceiling_cannot_reverse_the_clock() {
    let cfg = ClockConfig::default();
    assert!(
        cfg.fast_slew_cap_ppm < 1_000_000,
        "a ceiling at or beyond 1x can drive position backwards"
    );
    assert!(cfg.slew_cap_ppm <= cfg.fast_slew_cap_ppm);
}

/// A caller-supplied ceiling is data, not a promise. `ClockConfig`'s fields are
/// public, so a negative ceiling must not panic the clamp and a ceiling at or
/// beyond 1x must not let a correction stop or reverse position.
#[test]
fn a_hostile_ceiling_neither_panics_nor_reverses_the_clock() {
    for (fast, steady) in [
        (-1i64, -1i64),
        (i64::MIN, i64::MIN),
        (5_000_000, 5_000_000),
        (i64::MAX, i64::MAX),
    ] {
        let cfg = ClockConfig {
            fast_slew_cap_ppm: fast,
            slew_cap_ppm: steady,
            ..ClockConfig::default()
        };
        let mut c = MediaClock::new(cfg, MediaTime::ZERO, MediaTime::ZERO, Generation(0));
        c.set_playing(MediaTime::ZERO, true);
        c.set_master(MediaTime::ZERO, Master::Audio);
        let mut wall = MediaTime::ZERO;
        let mut last = c.now(wall);
        // Drive both signs of error so the clamp is exercised either way.
        for i in 0..200 {
            let err = if i % 2 == 0 { 400_000 } else { -400_000 };
            let target = c.now(wall) + MediaTime::from_micros(err);
            c.observe_master(wall, target);
            wall += MediaTime::from_millis(20);
            let now = c.now(wall);
            assert!(
                now >= last,
                "position went backwards with ceilings ({fast}, {steady}): {last} -> {now}"
            );
            last = now;
        }
    }
}

/// The fast window is bounded by wall time, but `rate_ppm` persists between
/// observations, so a rate set just inside the window would keep running at
/// the wide ceiling for as long as the master stays quiet. Closing the window
/// must not wait for the next observation.
#[test]
fn an_expired_fast_window_is_closed_without_an_observation() {
    let cfg = ClockConfig::default();
    let mut c = clock_at_zero();
    // One observation just inside the window, with an error big enough to
    // drive the rate to the wide ceiling.
    let wall = cfg.fast_window - MediaTime::from_millis(1);
    let target = c.now(wall) + MediaTime::from_millis(600);
    c.observe_master(wall, target);
    assert!(
        c.rate_ppm() > cfg.slew_cap_ppm,
        "expected the wide ceiling in force, got {}",
        c.rate_ppm()
    );

    // The master then goes quiet well past the window.
    let later = cfg.fast_window + MediaTime::from_secs(2);
    let before = c.now(later);
    c.enforce_slew_ceiling(later);
    assert!(
        c.rate_ppm().abs() <= cfg.slew_cap_ppm,
        "rate {} still above the steady ceiling after the window closed",
        c.rate_ppm()
    );
    // Closing the window must not move the position already reported.
    assert_eq!(before, c.now(later), "closing the window moved `now`");
}

/// A slew starts past the dead band (20 ms) and, once started, runs until
/// the error is inside the release band (5 ms). Both edges pinned.
#[test]
fn a_slew_runs_until_the_release_band() {
    let cfg = ClockConfig {
        smooth_master: false,
        ..ClockConfig::default()
    };
    let mut c = MediaClock::new(cfg, MediaTime::ZERO, MediaTime::ZERO, Generation(0));
    c.set_playing(MediaTime::ZERO, true);
    c.set_master(MediaTime::ZERO, Master::Audio);
    // Past the fast window, so the steady cap applies.
    let mut wall = MediaTime::from_secs(2);
    let mut at = |c: &mut MediaClock, error_us: i64| {
        wall += MediaTime::from_millis(10);
        let master = c.now(wall) + MediaTime::from_micros(error_us);
        c.observe_master(wall, master)
    };
    assert_eq!(
        at(&mut c, 20_000),
        Correction::None,
        "the dead band's edge starts nothing"
    );
    assert!(matches!(at(&mut c, 20_001), Correction::Slew { .. }));
    assert!(
        matches!(at(&mut c, 5_001), Correction::Slew { .. }),
        "a running slew must not stop outside the release band"
    );
    assert_eq!(at(&mut c, 5_000), Correction::None);
    assert_eq!(c.rate_ppm(), 0);
    assert_eq!(
        at(&mut c, 19_000),
        Correction::None,
        "idle again, the dead band applies"
    );
}
