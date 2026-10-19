//! Master smoothing and the slew release: host pull jitter of the kind
//! measured on Quest Pro and in the Unity Editor on Windows must not reach
//! frame due-times, while genuine offsets, discontinuities and generation
//! changes correct as on the unsmoothed ladder, and a start-up catch-up is
//! not carried past its target.

use media_clock::{ClockConfig, Correction, Generation, Master, MediaClock, MediaTime};

fn clock(smooth_master: bool) -> MediaClock {
    clock_with(ClockConfig {
        smooth_master,
        ..ClockConfig::default()
    })
}

fn clock_with(cfg: ClockConfig) -> MediaClock {
    let mut c = MediaClock::new(cfg, MediaTime::ZERO, MediaTime::ZERO, Generation(0));
    c.set_playing(MediaTime::ZERO, true);
    c.set_master(MediaTime::ZERO, Master::Audio);
    c
}

/// One delivery of PCM to the consumer: wall time and frames pulled.
struct Delivery {
    wall_us: i64,
    frames: i64,
}

/// The callback pattern captured on Quest Pro playing a stereo RTSP-over-TCP
/// stream: 512-sample buffers at 24 kHz (21.3 ms nominal). Jitter comes in
/// multi-second episodes, separated by quiet phases of uniform cadence.
/// Within an episode callbacks alternate ~14/28 ms (double-buffer bursts)
/// and every 15th slot is missed outright (a ~42 ms gap, ~320 ms period),
/// with the next pull catching up.
fn quest_delivery_schedule(duration_us: i64) -> Vec<Delivery> {
    const SLOT_US: i64 = 21_333;
    const JITTER_US: i64 = 7_000;
    const PHASE_US: i64 = 3_000_000;
    let mut deliveries = Vec::new();
    let mut carried = 0i64;
    let mut slot = 0i64;
    loop {
        let nominal = slot * SLOT_US;
        if nominal > duration_us {
            return deliveries;
        }
        // Quiet phase / episode phase, alternating every 3 s.
        let episode = (nominal / PHASE_US) % 2 == 1;
        let jitter = match (episode, slot % 2 == 0) {
            (false, _) => 0,
            (true, true) => -JITTER_US,
            (true, false) => JITTER_US,
        };
        if episode && slot % 15 == 14 {
            // Missed slot: its samples arrive with the next pull.
            carried += 512;
        } else {
            deliveries.push(Delivery {
                wall_us: (nominal + jitter).max(0),
                frames: 512 + carried,
            });
            carried = 0;
        }
        slot += 1;
    }
}

/// The engine's measured playhead over a schedule: consumed frames plus
/// wall-since-last-pull extrapolation capped at 40 ms (the
/// `AudioShared::playhead` model).
fn measured_playhead(deliveries: &[Delivery], rate: i64, wall_us: i64) -> Option<i64> {
    let mut consumed = 0i64;
    let mut last_pull = None;
    for d in deliveries {
        if d.wall_us > wall_us {
            break;
        }
        consumed += d.frames;
        last_pull = Some(d.wall_us);
    }
    let last_pull = last_pull?;
    let since = (wall_us - last_pull).clamp(0, 40_000);
    Some(consumed * 1_000_000 / rate + since)
}

/// Run a schedule, observing every 4 ms (the audio thread's cadence).
/// Returns the clock's post-settle wander (how far `now(wall) - wall` moved
/// over the measured window), the snap count and the number of slews
/// started.
fn run_pattern(
    c: &mut MediaClock,
    deliveries: &[Delivery],
    rate: i64,
    duration_us: i64,
    settle_us: i64,
) -> (i64, usize, usize) {
    let mut snaps = 0;
    let mut slews = 0;
    let mut slewing = false;
    let (mut lo, mut hi) = (i64::MAX, i64::MIN);
    let mut wall_us = 0i64;
    while wall_us <= duration_us {
        if let Some(playhead) = measured_playhead(deliveries, rate, wall_us) {
            let correction = c.observe_master(
                MediaTime::from_micros(wall_us),
                MediaTime::from_micros(playhead),
            );
            if wall_us >= settle_us {
                match correction {
                    Correction::Snap { .. } => snaps += 1,
                    Correction::Slew { .. } if !slewing => slews += 1,
                    _ => {}
                }
                let err = (c.now(MediaTime::from_micros(wall_us))
                    - MediaTime::from_micros(wall_us))
                .as_micros();
                lo = lo.min(err);
                hi = hi.max(err);
            }
            slewing = matches!(correction, Correction::Slew { .. });
        }
        wall_us += 4_000;
    }
    (hi - lo, snaps, slews)
}

/// Smoothed ladder on the Quest trace: once settled, the jitter moves the
/// clock (and every frame's due time) by under 3 ms, less than half a
/// 72 Hz vsync (6.9 ms). Presentation holds its cadence.
#[test]
fn smoothing_holds_due_times_through_quest_callback_jitter() {
    let mut c = clock(true);
    let deliveries = quest_delivery_schedule(15_000_000);
    let (wander, snaps, slews) = run_pattern(&mut c, &deliveries, 24_000, 15_000_000, 2_500_000);
    assert_eq!(snaps, 0, "jitter must never snap");
    assert_eq!(slews, 0, "the callback jitter must not start a slew");
    assert!(
        wander < 3_000,
        "smoothed clock wandered {wander} µs against 1x"
    );
}

/// The audio playhead traced in the Unity Editor on Windows (D3D11, 48 kHz
/// output, 1024-frame DSP buffer, a 44.1 kHz source): wall time and
/// playhead, both from the session's start, one row per audio-thread tick.
/// Pulls land on a ~20 ms grid with missed slots, and the playhead wanders
/// about ±23 ms around 1x.
const WINDOWS_EDITOR_PLAYHEAD: &str = include_str!("data/windows-editor-playhead.csv");

/// Replay the Windows trace. Returns the post-settle wander, snaps and
/// slews started, as `run_pattern`.
fn run_windows_trace(c: &mut MediaClock, settle_us: i64) -> (i64, usize, usize) {
    let mut snaps = 0;
    let mut slews = 0;
    let mut slewing = false;
    let (mut lo, mut hi) = (i64::MAX, i64::MIN);
    for line in WINDOWS_EDITOR_PLAYHEAD.lines().filter(|l| !l.is_empty()) {
        let (wall_us, playhead_us) = line.split_once(',').expect("wall,playhead");
        let wall = MediaTime::from_micros(wall_us.trim().parse().expect("wall"));
        let playhead = MediaTime::from_micros(playhead_us.trim().parse().expect("playhead"));
        let correction = c.observe_master(wall, playhead);
        if wall.as_micros() >= settle_us {
            match correction {
                Correction::Snap { .. } => snaps += 1,
                Correction::Slew { .. } if !slewing => slews += 1,
                _ => {}
            }
            let err = (c.now(wall) - wall).as_micros();
            lo = lo.min(err);
            hi = hi.max(err);
        }
        slewing = matches!(correction, Correction::Slew { .. });
    }
    (hi - lo, snaps, slews)
}

/// The Windows trace: no slew starts once settled, and the clock runs at
/// 1x within a couple of ms.
#[test]
fn smoothing_holds_the_clock_through_the_windows_pull_jitter() {
    let mut c = clock(true);
    let (wander, snaps, slews) = run_windows_trace(&mut c, 2_500_000);
    assert_eq!(snaps, 0);
    assert_eq!(slews, 0, "the pull jitter must not start a slew");
    assert!(wander < 3_000, "clock wandered {wander} µs against 1x");
}

/// Without smoothing the same trace keeps starting slews. Pinned so the
/// comparison stays visible.
#[test]
fn without_smoothing_the_windows_pull_jitter_hunts() {
    let mut c = clock(false);
    let (_, snaps, slews) = run_windows_trace(&mut c, 2_500_000);
    assert_eq!(snaps, 0);
    assert!(
        slews > 10,
        "expected the raw ladder to hunt, saw {slews} slews"
    );
}

/// Smoothed but released at the dead band's edge, the clock parks there and
/// the residual jitter still starts slews. Pinned so the release's part in
/// the result stays visible.
#[test]
fn without_the_release_band_the_clock_parks_at_the_edge() {
    let mut c = clock_with(ClockConfig {
        release_band: MediaTime::from_millis(20),
        ..ClockConfig::default()
    });
    let (_, snaps, slews) = run_windows_trace(&mut c, 2_500_000);
    assert_eq!(snaps, 0);
    assert!(slews > 0, "expected slews at the band edge, saw none");
}

/// A start with the master's first reading 100 ms behind the clock: the
/// fast window closes the gap within 500 ms, and the average must not carry
/// the catch-up past its target.
#[test]
fn a_start_up_catch_up_is_not_carried_past_its_target() {
    let mut c = clock(true);
    let behind = MediaTime::from_millis(100);
    let mut wall = MediaTime::ZERO;
    let mut inside_at = None;
    let mut overshoot = MediaTime::ZERO;
    while wall <= MediaTime::from_secs(3) {
        let master = wall - behind;
        c.observe_master(wall, master);
        let error = master - c.now(wall);
        if inside_at.is_none() && error.abs() <= MediaTime::from_millis(20) {
            inside_at = Some(wall);
        }
        overshoot = overshoot.max(error);
        wall += MediaTime::from_millis(4);
    }
    let inside_at = inside_at.expect("never closed the start-up gap");
    assert!(
        inside_at <= MediaTime::from_millis(500),
        "start-up gap closed only after {inside_at}"
    );
    assert!(
        overshoot <= MediaTime::from_millis(5),
        "the catch-up overshot by {overshoot}"
    );
}

/// A genuine standing offset still converges through the smoothing and
/// then stays inside the dead band.
#[test]
fn smoothing_converges_on_genuine_offset() {
    let mut c = clock(true);
    let offset = MediaTime::from_millis(300);
    let mut wall = MediaTime::ZERO;
    let mut converged_at = None;
    for _ in 0..400 {
        let correction = c.observe_master(wall, wall + offset);
        assert!(!matches!(correction, Correction::Snap { .. }));
        if converged_at.is_none()
            && matches!(correction, Correction::None)
            && wall > MediaTime::ZERO
        {
            converged_at = Some(wall);
        }
        wall += MediaTime::from_millis(100);
    }
    let converged_at = converged_at.expect("never converged");
    assert!(converged_at < MediaTime::from_secs(18));
    for _ in 0..50 {
        wall += MediaTime::from_millis(100);
        let correction = c.observe_master(wall, wall + offset);
        assert!(
            matches!(correction, Correction::None),
            "left the dead band after converging: {correction:?}"
        );
    }
}

/// The snap rung acts on the raw position: a real discontinuity must never
/// be averaged away.
#[test]
fn snap_acts_on_the_raw_position() {
    let mut c = clock(true);
    let mut wall = MediaTime::ZERO;
    for _ in 0..100 {
        c.observe_master(wall, wall);
        wall += MediaTime::from_millis(10);
    }
    let target = wall + MediaTime::from_secs(2);
    let correction = c.observe_master(wall, target);
    assert!(matches!(correction, Correction::Snap { .. }));
    assert_eq!(c.now(wall), target);
}

/// A generation change (seek) clears the average: the first observation on
/// the new timeline seeds fresh instead of blending with the old one.
#[test]
fn generation_change_resets_the_average() {
    let mut c = clock(true);
    let mut wall = MediaTime::ZERO;
    for _ in 0..200 {
        c.observe_master(wall, wall + MediaTime::from_millis(600));
        wall += MediaTime::from_millis(10);
    }
    c.advance_generation(wall, Generation(1), MediaTime::from_secs(60));
    for _ in 0..10 {
        wall += MediaTime::from_millis(10);
        let now = c.now(wall);
        let correction = c.observe_master(wall, now);
        assert!(
            matches!(correction, Correction::None),
            "stale offsets survived the generation change: {correction:?}"
        );
    }
    assert_eq!(c.rate_ppm(), 0);
}

/// The master stands still through a pause while the wall runs on, so the
/// offsets from before it must not be read as an error on resume.
#[test]
fn a_pause_is_not_read_as_an_error() {
    let mut c = clock(true);
    let mut wall = MediaTime::ZERO;
    for _ in 0..100 {
        let now = c.now(wall);
        c.observe_master(wall, now);
        wall += MediaTime::from_millis(10);
    }
    c.set_playing(wall, false);
    wall += MediaTime::from_secs(5);
    c.set_playing(wall, true);
    for _ in 0..10 {
        let now = c.now(wall);
        let correction = c.observe_master(wall, now);
        assert!(
            matches!(correction, Correction::None),
            "the pause was read as an error: {correction:?}"
        );
        wall += MediaTime::from_millis(10);
    }
}
