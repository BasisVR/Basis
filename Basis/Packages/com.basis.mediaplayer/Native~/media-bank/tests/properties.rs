//! Property tests: arrival schedule in → release schedule out.

use media_bank::{Bank, BankConfig, BufferDepth, Liveness, PushOutcome};
use media_clock::{Generation, MediaTime};
use media_demux::{Au, StreamEvent, TrackId};
use proptest::prelude::*;

fn au(dts_us: i64, bytes: usize, generation: Generation) -> StreamEvent {
    StreamEvent::Au(Au {
        track: TrackId(0),
        data: vec![0u8; bytes],
        pts: MediaTime::from_micros(dts_us),
        dts: MediaTime::from_micros(dts_us),
        key: false,
        generation,
    })
}

fn fixed_cfg(depth_ms: u32) -> BankConfig {
    BankConfig {
        depth: BufferDepth::Millis(depth_ms),
        liveness: Liveness::Live,
        // The pacing properties below describe the steady-state schedule;
        // the deliberate startup-window exception has its own tests.
        startup_burst: MediaTime::ZERO,
        ..BankConfig::default()
    }
}

/// A synthetic live arrival timeline: AUs at a fixed cadence, with delivery
/// gaps injected (arrival deferred to the gap end), driven against pops.
#[derive(Debug, Clone)]
struct Run {
    depth_ms: u32,
    au_interval_us: i64,
    /// (index of AU where a gap starts, gap length in AU intervals)
    gaps: Vec<(usize, usize)>,
    n_aus: usize,
}

fn runs() -> impl Strategy<Value = Run> {
    (
        prop_oneof![Just(500u32), Just(1500), Just(3000)],
        20_000i64..40_000,
        prop::collection::vec((0usize..200, 1usize..90), 0..6),
        50usize..200,
    )
        .prop_map(|(depth_ms, au_interval_us, gaps, n_aus)| Run {
            depth_ms,
            au_interval_us,
            gaps,
            n_aus,
        })
}

/// Drive a run: returns (release wall, dts) per released AU plus the bank.
fn drive(run: &Run) -> (Vec<(MediaTime, MediaTime)>, Bank) {
    let mut bank = Bank::new(fixed_cfg(run.depth_ms), Generation(0)).unwrap();
    let interval = MediaTime::from_micros(run.au_interval_us);

    // Build arrival times: 1x, deferred by gaps.
    let mut arrivals = Vec::with_capacity(run.n_aus);
    for i in 0..run.n_aus {
        let dts = MediaTime::from_micros(i as i64 * run.au_interval_us);
        let mut arrival = dts;
        for &(gap_at, gap_aus) in &run.gaps {
            let start = MediaTime::from_micros(gap_at as i64 * run.au_interval_us);
            let end = start + MediaTime::from_micros(gap_aus as i64 * run.au_interval_us);
            if arrival >= start && arrival < end {
                arrival = end;
            }
        }
        arrivals.push((arrival, dts));
    }
    arrivals.sort();

    let mut released = Vec::new();
    let step = interval.min(MediaTime::from_millis(10));
    let mut wall = MediaTime::ZERO;
    let end_wall = MediaTime::from_micros(
        (run.n_aus as i64 + run.gaps.iter().map(|g| g.1).sum::<usize>() as i64 + 400)
            * run.au_interval_us,
    );
    let mut next_arrival = 0usize;
    while wall <= end_wall {
        while next_arrival < arrivals.len() && arrivals[next_arrival].0 <= wall {
            let (_, dts) = arrivals[next_arrival];
            match bank.push(wall, au(dts.as_micros(), 100, Generation(0))) {
                PushOutcome::Accepted => next_arrival += 1,
                PushOutcome::Full(_) => break,
                PushOutcome::StaleGeneration => unreachable!(),
            }
        }
        while let Some(ev) = bank.pop_due(wall) {
            if let StreamEvent::Au(au) = ev {
                released.push((wall, au.dts));
            }
        }
        wall += step;
    }
    (released, bank)
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(64))]

    /// Everything pushed is released exactly once, in order.
    #[test]
    fn conservation_and_order(run in runs()) {
        let (released, _) = drive(&run);
        prop_assert_eq!(released.len(), run.n_aus, "release count mismatch");
        for pair in released.windows(2) {
            prop_assert!(pair[1].1 > pair[0].1, "dts order violated");
            prop_assert!(pair[1].0 >= pair[0].0, "wall order violated");
        }
    }

    /// Release never feeds a consumer faster than 1x plus the lead, allowing
    /// for decay running the schedule at most `decay_rate` fast.
    #[test]
    fn release_never_beats_one_x_plus_lead(run in runs()) {
        let cfg = fixed_cfg(run.depth_ms);
        let (released, _) = drive(&run);
        let Some(&(first_wall, first_dts)) = released.first() else { return Ok(()) };
        for &(wall, dts) in &released[1..] {
            let media = dts - first_dts;
            let elapsed = wall - first_wall;
            let allowance = elapsed
                + elapsed.scale_ppm(cfg.decay_rate_ppm)
                + cfg.pace_lead.max(cfg.pace_lead_with_lag)
                // one drive step of scheduling slack
                + MediaTime::from_millis(10);
            prop_assert!(
                media <= allowance,
                "released {media} of media in {elapsed} (allowance {allowance})"
            );
        }
    }

    /// The byte cap is never exceeded, and lag never passes the cap.
    #[test]
    fn caps_hold(run in runs()) {
        let (_, bank) = drive(&run);
        let cfg = fixed_cfg(run.depth_ms);
        let m = bank.metrics();
        prop_assert!(m.banked_bytes <= cfg.byte_cap);
        prop_assert!(m.lag <= cfg.lag_cap);
    }

    /// Startup: nothing is released until the target depth is banked (or
    /// the hold times out).
    #[test]
    fn startup_holds_until_banked(run in runs()) {
        let cfg = fixed_cfg(run.depth_ms);
        let target_lag = MediaTime::from_millis(run.depth_ms as i64) - cfg.decoder_cushion;
        let (released, _) = drive(&run);
        let Some(&(first_wall, _)) = released.first() else { return Ok(()) };
        // Release began either once ~target was banked (1x fill ⇒ wall ≈
        // target lag) or at the startup timeout, whichever came first.
        let earliest = target_lag.min(cfg.startup_timeout) - MediaTime::from_millis(10);
        prop_assert!(
            first_wall >= earliest,
            "released at {first_wall}, before target banked or timeout ({earliest})"
        );
    }
}

#[test]
fn stale_generation_dropped_on_sight() {
    let mut bank = Bank::new(fixed_cfg(500), Generation(1)).unwrap();
    assert!(matches!(
        bank.push(MediaTime::ZERO, au(0, 10, Generation(0))),
        PushOutcome::StaleGeneration
    ));
    assert!(matches!(
        bank.push(MediaTime::ZERO, au(0, 10, Generation(1))),
        PushOutcome::Accepted
    ));
    assert_eq!(bank.metrics().dropped_stale, 1);
}

#[test]
fn unsatisfiable_depth_is_an_error_not_a_clamp() {
    use media_bank::BankConfigError;
    let cfg = BankConfig {
        depth: BufferDepth::Millis(100),
        ..BankConfig::default()
    };
    match Bank::new(cfg, Generation(0)) {
        Err(BankConfigError::DepthBelowCushion { .. }) => {}
        other => panic!("expected DepthBelowCushion, got {other:?}"),
    }

    let cfg = BankConfig {
        depth: BufferDepth::Millis(60_000),
        ..BankConfig::default()
    };
    match Bank::new(cfg, Generation(0)) {
        Err(BankConfigError::DepthBeyondLagCap { .. }) => {}
        other => panic!("expected DepthBeyondLagCap, got {other:?}"),
    }

    let cfg = BankConfig {
        pace_lead: MediaTime::from_millis(600),
        ..BankConfig::default()
    };
    match Bank::new(cfg, Generation(0)) {
        Err(BankConfigError::LeadNotBelowCushion { .. }) => {}
        other => panic!("expected LeadNotBelowCushion, got {other:?}"),
    }
}

#[test]
fn vod_backpressures_at_depth() {
    let cfg = BankConfig {
        depth: BufferDepth::Millis(1500),
        liveness: Liveness::Vod,
        ..BankConfig::default()
    };
    let mut bank = Bank::new(cfg, Generation(0)).unwrap();
    // A VOD source delivers far faster than 1x; the bank must refuse once
    // the read-ahead target is banked.
    let mut accepted = 0i64;
    for i in 0..1000 {
        match bank.push(MediaTime::ZERO, au(i * 33_000, 100, Generation(0))) {
            PushOutcome::Accepted => accepted += 1,
            PushOutcome::Full(_) => break,
            PushOutcome::StaleGeneration => unreachable!(),
        }
    }
    let banked_ms = accepted * 33;
    assert!(
        (1400..=1700).contains(&banked_ms),
        "VOD read-ahead stopped at {banked_ms}ms, wanted ~1500ms"
    );
}

#[test]
fn discontinuity_splices_the_timeline() {
    use media_demux::DiscontinuityReason;
    let mut bank = Bank::new(fixed_cfg(500), Generation(0)).unwrap();
    let interval = 33_000i64;
    let mut wall = MediaTime::ZERO;
    let mut released = 0usize;
    // 1x delivery; a PCR-wrap-style dts reset mid-stream.
    for i in 0..60i64 {
        let dts = if i < 30 {
            90_000_000_000 + i * interval
        } else {
            (i - 30) * interval
        };
        if i == 30 {
            bank.push(
                wall,
                StreamEvent::Discontinuity(TrackId(0), DiscontinuityReason::PcrWrap),
            );
        }
        assert!(matches!(
            bank.push(wall, au(dts, 100, Generation(0))),
            PushOutcome::Accepted
        ));
        while let Some(ev) = bank.pop_due(wall) {
            if matches!(ev, StreamEvent::Au(_)) {
                released += 1;
            }
        }
        wall += MediaTime::from_micros(interval);
    }
    // Run the clock on: everything must drain, paced, without a stall from
    // the wrap itself.
    for _ in 0..120 {
        wall += MediaTime::from_micros(interval);
        while let Some(ev) = bank.pop_due(wall) {
            if matches!(ev, StreamEvent::Au(_)) {
                released += 1;
            }
        }
    }
    assert_eq!(released, 60);
    assert_eq!(bank.metrics().stall_total, MediaTime::ZERO);
}

/// VOD: the startup burst is an anchor phase shift. Everything inside
/// `burst + lead` of the join point is due immediately, and the schedule
/// keeps running exactly that far ahead (still 1x) further out.
#[test]
fn vod_startup_burst_shifts_the_release_phase() {
    let cfg = BankConfig {
        depth: BufferDepth::Millis(1000),
        liveness: Liveness::Vod,
        startup_burst: MediaTime::from_millis(2000),
        ..BankConfig::default()
    };
    let lead = cfg.pace_lead;
    let mut bank = Bank::new(cfg, Generation(0)).unwrap();
    let interval = 33_000i64;

    // Feed like a read-ahead source: push when the bank accepts, pop what
    // is due at a frozen wall instant just after the hold lifts.
    let wall = MediaTime::from_millis(1);
    let mut pushed = 0i64;
    let mut released = 0i64;
    while pushed < 200 {
        match bank.push(wall, au(pushed * interval, 100, Generation(0))) {
            PushOutcome::Accepted => pushed += 1,
            // Read-ahead target reached with nothing more due at this
            // instant: the phase window is spent.
            PushOutcome::Full(_) => break,
            PushOutcome::StaleGeneration => unreachable!(),
        }
        while bank.pop_due(wall).is_some() {
            released += 1;
        }
    }
    // Due-now window at the anchor: burst + lead (2.4 s = ~72 AUs).
    let released_ms = released * 33;
    let expected_ms = 2000 + lead.as_micros() / 1000;
    assert!(
        (released_ms - expected_ms).abs() <= 100,
        "burst phase released {released_ms}ms at the anchor, wanted ~{expected_ms}ms"
    );

    // Beyond the phase window the 1x schedule holds.
    assert!(bank.pop_due(wall).is_none());
    let next = bank.next_due(wall).expect("queue not empty");
    assert!(next > wall, "post-burst release must stay paced");
    // One more AU interval of wall time releases about one more AU.
    let later = wall + MediaTime::from_micros(2 * interval);
    let mut trailing = 0;
    while bank.pop_due(later).is_some() {
        trailing += 1;
    }
    assert!(
        (1..=3).contains(&trailing),
        "expected ~2 AUs per 2 intervals of wall time, got {trailing}"
    );
}

// The live startup with a non-zero burst is the priming join, pinned in
// tests/priming.rs. The properties here pin the strict burst-zero startup
// and the steady-state schedule.

/// The VOD phase shift re-applies across generations (seeks).
#[test]
fn vod_startup_burst_rearms_across_generations() {
    let cfg = BankConfig {
        depth: BufferDepth::Millis(1000),
        liveness: Liveness::Vod,
        startup_burst: MediaTime::from_millis(2000),
        ..BankConfig::default()
    };
    let mut bank = Bank::new(cfg, Generation(0)).unwrap();
    let interval = 33_000i64;
    let wall = MediaTime::from_millis(1);
    for i in 0..40i64 {
        let _ = bank.push(wall, au(i * interval, 100, Generation(0)));
    }
    let mut first = 0;
    while bank.pop_due(wall).is_some() {
        first += 1;
    }
    assert!(first >= 30, "burst phase should release the banked window");

    bank.advance_generation(Generation(1));
    let seek_wall = MediaTime::from_secs(5);
    for i in 0..40i64 {
        let _ = bank.push(seek_wall, au(60_000_000 + i * interval, 100, Generation(1)));
    }
    let mut second = 0;
    while bank
        .pop_due(seek_wall + MediaTime::from_millis(1))
        .is_some()
    {
        second += 1;
    }
    assert!(
        second >= 30,
        "post-seek burst phase released only {second} AUs"
    );
}
