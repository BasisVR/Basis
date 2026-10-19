//! The depth sizing table as executable tests: the recorded network-delay
//! fixtures in `media-testkit` (gap distributions captured from a VRCDN
//! stream under impairment) drive the Bank at each candidate depth, and the
//! residual stall is held to the analytic figures.
//!
//! Two layers:
//! - `media-testkit` pins the fixtures to the analytic table
//!   (`sum(max(0, gap - depth))`, refill assumed between gaps).
//! - Here, the Bank replays the same schedules behaviourally. The debt
//!   bound refills between gaps and its growth persists until decay gives
//!   it back, so the Bank may only do better than the constant-depth
//!   analytic model. That is asserted as `bank ≤ analytic + quantisation`,
//!   and the table's fully absorbed cells must be stall-free.

use media_bank::{Bank, BankConfig, BufferDepth, Liveness, PushOutcome};
use media_clock::{Generation, MediaTime};
use media_demux::{Au, StreamEvent, TrackId};
use media_testkit::{ArrivalSchedule, GapCapture};

/// 30 fps, ~2.62 Mbit/s: the measured VRCDN stream the captures recorded.
const AU_INTERVAL: MediaTime = MediaTime::from_micros(33_366);
const AU_BYTES: usize = 10_900;

/// The decoded-side cushion of the player the captures were recorded with;
/// using it keeps "total depth" directly comparable with the analytic table.
const CUSHION: MediaTime = MediaTime::from_millis(460);

fn replay_cfg(depth_ms: u32) -> BankConfig {
    BankConfig {
        depth: BufferDepth::Millis(depth_ms),
        liveness: Liveness::Live,
        decoder_cushion: CUSHION,
        pace_lead: MediaTime::from_millis(400),
        pace_lead_with_lag: MediaTime::from_millis(100),
        // The table grades the steady-state schedule against the analytic
        // model, so the replay uses the strict hold-then-1x startup. The
        // priming join (burst > 0) needs the engine's presentation signal
        // and channel backpressure to mean anything, so its startup is
        // graded by the `bm-probe impair` rows instead.
        startup_burst: MediaTime::ZERO,
        ..BankConfig::default()
    }
}

/// Replay a capture through the Bank at the given config; returns the
/// viewer-visible stall fraction of the run.
fn replay(capture: &GapCapture, cfg: BankConfig) -> (f64, media_bank::BankMetrics) {
    let schedule = ArrivalSchedule::from_capture(capture, AU_INTERVAL, AU_BYTES);
    let mut bank = Bank::new(cfg, Generation(0)).unwrap();
    let step = MediaTime::from_millis(5);
    let mut wall = MediaTime::ZERO;
    let end_wall = capture.duration + MediaTime::from_secs(30);
    let mut next = 0usize;
    while wall <= end_wall {
        while next < schedule.aus.len() && schedule.aus[next].arrival <= wall {
            let a = schedule.aus[next];
            let ev = StreamEvent::Au(Au {
                track: TrackId(0),
                data: vec![0u8; a.bytes],
                pts: a.dts,
                dts: a.dts,
                key: false,
                generation: Generation(0),
            });
            match bank.push(wall, ev) {
                PushOutcome::Accepted => next += 1,
                PushOutcome::Full(_) => break,
                PushOutcome::StaleGeneration => unreachable!(),
            }
        }
        while bank.pop_due(wall).is_some() {}
        wall += step;
    }
    let m = bank.metrics();
    let stall = m.stall_total.as_micros() as f64 / capture.duration.as_micros() as f64;
    (stall, m)
}

/// One sizing-table lane: (capture, per-depth analytic stall %).
fn jitter_lanes() -> Vec<(GapCapture, Vec<(u32, f64)>)> {
    vec![
        (
            GapCapture::ts_rtt600_loss0(),
            vec![
                (460, 2.90),
                (1000, 0.28),
                (1500, 0.08),
                (2000, 0.00),
                (3000, 0.00),
                (5000, 0.00),
            ],
        ),
        (
            GapCapture::ts_rtt300_loss005(),
            vec![
                (460, 22.64),
                (1000, 7.96),
                (1500, 3.56),
                (2000, 1.24),
                (3000, 0.00),
                (5000, 0.00),
            ],
        ),
        (
            GapCapture::rtspt_rtt300_loss005(),
            vec![
                (460, 7.26),
                (1000, 0.47),
                (1500, 0.09),
                (2000, 0.00),
                (3000, 0.00),
                (5000, 0.00),
            ],
        ),
    ]
}

#[test]
fn bank_meets_the_sizing_table_on_the_jitter_lanes() {
    // AU-cadence quantisation: gap boundaries land on 33 ms arrival ticks.
    let quantisation_pp = 2.0;
    for (capture, rows) in jitter_lanes() {
        let mut prev_stall = f64::INFINITY;
        for (depth_ms, published_pct) in rows {
            let (stall, m) = replay(&capture, replay_cfg(depth_ms));
            let stall_pct = stall * 100.0;
            assert!(
                stall_pct <= published_pct + quantisation_pp,
                "{} at {depth_ms}ms: bank stalled {stall_pct:.2}% vs table {published_pct:.2}% \
                 (reanchors {}, lag {})",
                capture.name,
                m.reanchors,
                m.lag,
            );
            // Depth must never make things worse (small slack for AU
            // quantisation between adjacent depths).
            assert!(
                stall_pct <= prev_stall + 0.25,
                "{}: stall rose with depth ({prev_stall:.2}% -> {stall_pct:.2}% at {depth_ms}ms)",
                capture.name,
            );
            prev_stall = stall_pct;
            // The table's headline: cells it calls fully absorbed are
            // genuinely stall-free in the replay.
            if published_pct == 0.0 {
                assert_eq!(
                    m.stall_total,
                    MediaTime::ZERO,
                    "{} at {depth_ms}ms: table says fully absorbed, bank stalled",
                    capture.name,
                );
            }
        }
    }
}

#[test]
fn three_seconds_absorbs_the_jitter_regime_outright() {
    // Three seconds of depth fully absorbs both impaired TCP lanes.
    for capture in [
        GapCapture::ts_rtt300_loss005(),
        GapCapture::rtspt_rtt300_loss005(),
    ] {
        let (_, m) = replay(&capture, replay_cfg(3000));
        assert_eq!(
            m.stall_total,
            MediaTime::ZERO,
            "{} stalled at 3s depth",
            capture.name
        );
    }
}

#[test]
fn clean_baseline_never_stalls_or_reanchors() {
    for depth_ms in [460, 1500, 3000] {
        let (_, m) = replay(&GapCapture::ts_clean(), replay_cfg(depth_ms));
        assert_eq!(m.stall_total, MediaTime::ZERO);
        assert_eq!(m.reanchors, 0);
    }
}

#[test]
fn auto_self_tunes_depth_to_a_bad_link() {
    // The throughput-regime capture: no depth suffices, but Auto must grow
    // the bank towards what the link demonstrates, up to the 10 s cap.
    let capture = GapCapture::ts_rtt300_loss05();
    let cfg = BankConfig {
        depth: BufferDepth::Auto,
        liveness: Liveness::Live,
        decoder_cushion: CUSHION,
        pace_lead: MediaTime::from_millis(400),
        pace_lead_with_lag: MediaTime::from_millis(100),
        startup_burst: MediaTime::ZERO,
        ..BankConfig::default()
    };
    let (_, m) = replay(&capture, cfg);
    assert!(
        m.target_lag >= MediaTime::from_secs(3),
        "Auto target stayed at {} on a link needing many seconds",
        m.target_lag,
    );
    assert!(m.reanchors > 0);
}

#[test]
fn auto_stays_modest_on_a_clean_link() {
    let cfg = BankConfig {
        depth: BufferDepth::Auto,
        liveness: Liveness::Live,
        decoder_cushion: CUSHION,
        pace_lead: MediaTime::from_millis(400),
        pace_lead_with_lag: MediaTime::from_millis(100),
        startup_burst: MediaTime::ZERO,
        ..BankConfig::default()
    };
    let cold_start_lag = cfg.auto.cold_start_depth - CUSHION;
    let (_, m) = replay(&GapCapture::ts_clean(), cfg);
    assert!(
        m.target_lag <= cold_start_lag.max(MediaTime::from_millis(60)),
        "Auto grew to {} on a clean link",
        m.target_lag,
    );
    assert_eq!(m.stall_total, MediaTime::ZERO);
}
