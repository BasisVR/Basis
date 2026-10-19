//! The recorded network-delay fixtures: delivery-gap distributions measured
//! on a live VRCDN stream under network impairment, committed so the depth
//! sizing table is an executable test.
//!
//! Each fixture was derived from a diagnostics capture of the previous C
//! player, named in the fixture's header. A starve of duration D happened
//! after that player's jitter buffer had drained, so the underlying delivery
//! gap is D + buffer. `analytic_stall_fraction` is the sizing model (residual
//! stall assuming the buffer refills between gaps) that the Bank replay is
//! measured against.

use media_clock::MediaTime;

/// One delivery gap on the capture's analysed timeline: delivery halts at
/// `start`, and everything the source emitted during the gap arrives in a
/// recovery burst at `start + dur`.
#[derive(Debug, Clone, Copy)]
pub struct Gap {
    pub start: MediaTime,
    pub dur: MediaTime,
}

#[derive(Debug, Clone)]
pub struct GapCapture {
    pub name: &'static str,
    pub impairment: String,
    /// Length of the analysed steady-state window.
    pub duration: MediaTime,
    pub gaps: Vec<Gap>,
}

impl GapCapture {
    fn parse(name: &'static str, text: &str) -> Self {
        let mut impairment = String::new();
        let mut duration = MediaTime::ZERO;
        let mut gaps = Vec::new();
        for line in text.lines() {
            if let Some(rest) = line.strip_prefix("# impairment:") {
                impairment = rest.trim().to_owned();
            } else if let Some(rest) = line.strip_prefix("# analysed_duration_s:") {
                let secs: f64 = rest.trim().parse().expect("duration");
                duration = MediaTime::from_micros((secs * 1e6) as i64);
            } else if !line.starts_with('#') && line.contains(',') && !line.starts_with("start_s") {
                let (start_s, gap_ms) = line.split_once(',').expect("gap row");
                let start: f64 = start_s.trim().parse().expect("gap start");
                let dur: f64 = gap_ms.trim().parse().expect("gap duration");
                gaps.push(Gap {
                    start: MediaTime::from_micros((start * 1e6) as i64),
                    dur: MediaTime::from_micros((dur * 1e3) as i64),
                });
            }
        }
        assert!(
            duration > MediaTime::ZERO,
            "{name}: missing duration header"
        );
        Self {
            name,
            impairment,
            duration,
            gaps,
        }
    }

    /// Residual stall at a candidate total depth, as a fraction of the run:
    /// `sum(max(0, gap - depth)) / duration`. Assumes the buffer refills
    /// between gaps, which holds in the jitter regime and is optimistic in
    /// the throughput regime.
    pub fn analytic_stall_fraction(&self, depth: MediaTime) -> f64 {
        let residual: i64 = self
            .gaps
            .iter()
            .map(|g| (g.dur - depth).max(MediaTime::ZERO).as_micros())
            .sum();
        residual as f64 / self.duration.as_micros() as f64
    }

    /// The clean baseline: ~18 min across three transports, zero starves.
    pub fn ts_clean() -> Self {
        Self::parse("ts-clean", include_str!("../fixtures/phase0/ts-clean.csv"))
    }

    /// HTTP-TS, +600 ms RTT, no loss: latency alone is nearly harmless.
    pub fn ts_rtt600_loss0() -> Self {
        Self::parse(
            "ts-rtt600-loss0",
            include_str!("../fixtures/phase0/ts-rtt600-loss0.csv"),
        )
    }

    /// HTTP-TS, +300 ms RTT, 0.05% loss: the jitter regime, worst TCP lane.
    pub fn ts_rtt300_loss005() -> Self {
        Self::parse(
            "ts-rtt300-loss005",
            include_str!("../fixtures/phase0/ts-rtt300-loss005.csv"),
        )
    }

    /// RTSP-TCP, +300 ms RTT, 0.05% loss: the jitter regime, milder lane.
    pub fn rtspt_rtt300_loss005() -> Self {
        Self::parse(
            "rtspt-rtt300-loss005",
            include_str!("../fixtures/phase0/rtspt-rtt300-loss005.csv"),
        )
    }

    /// HTTP-TS, +300 ms RTT, 0.5% loss: the throughput regime. No depth
    /// suffices, and the analytic model is optimistic here.
    pub fn ts_rtt300_loss05() -> Self {
        Self::parse(
            "ts-rtt300-loss05",
            include_str!("../fixtures/phase0/ts-rtt300-loss05.csv"),
        )
    }

    pub fn all() -> Vec<Self> {
        vec![
            Self::ts_clean(),
            Self::ts_rtt600_loss0(),
            Self::ts_rtt300_loss005(),
            Self::rtspt_rtt300_loss005(),
            Self::ts_rtt300_loss05(),
        ]
    }
}
