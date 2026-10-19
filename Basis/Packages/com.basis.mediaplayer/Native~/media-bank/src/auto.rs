//! Auto depth: the debt bound grows actual lag to what the link
//! demonstrates; decay shrinks it back towards this estimator's target: a
//! quantile over a histogram of recent delivery delays with exponential
//! forgetting (WebRTC NetEQ's shape: 20 ms buckets, 0.95 quantile, 0.983
//! forget factor). Sizing comes from measured delay variance, never RTT.

use media_clock::MediaTime;

#[derive(Debug, Clone)]
pub struct AutoConfig {
    /// Total depth assumed before the link has demonstrated anything. Auto
    /// starts modest and grows on evidence.
    pub cold_start_depth: MediaTime,
    /// Floor for the target depth.
    pub min_depth: MediaTime,
    pub bucket: MediaTime,
    pub forget_factor: f64,
    pub quantile: f64,
}

impl Default for AutoConfig {
    fn default() -> Self {
        Self {
            cold_start_depth: MediaTime::from_millis(500),
            min_depth: MediaTime::from_millis(500),
            bucket: MediaTime::from_millis(20),
            forget_factor: 0.983,
            quantile: 0.95,
        }
    }
}

/// Weight of the cold-start seed sample: heavy enough to govern the target
/// for the first few seconds of arrivals, light enough that measured delays
/// wash it out.
const SEED_WEIGHT: f64 = 20.0;

#[derive(Debug)]
pub(crate) struct AutoDepth {
    cfg: AutoConfig,
    cushion: MediaTime,
    lag_cap: MediaTime,
    counts: Vec<f64>,
    total: f64,
}

impl AutoDepth {
    pub fn new(cfg: AutoConfig, cushion: MediaTime, lag_cap: MediaTime) -> Self {
        let span = lag_cap + cushion;
        let buckets = (span.as_micros() / cfg.bucket.as_micros().max(1)) as usize + 1;
        let mut this = Self {
            cfg,
            cushion,
            lag_cap,
            counts: vec![0.0; buckets],
            total: 0.0,
        };
        let seed = this.bucket_of(this.cfg.cold_start_depth);
        this.counts[seed] = SEED_WEIGHT;
        this.total = SEED_WEIGHT;
        this
    }

    fn bucket_of(&self, t: MediaTime) -> usize {
        let idx =
            (t.max(MediaTime::ZERO).as_micros() / self.cfg.bucket.as_micros().max(1)) as usize;
        idx.min(self.counts.len() - 1)
    }

    /// Feed one arrival's delivery delay relative to the 1x schedule. A
    /// delay of `d` needs `d` of total depth to absorb.
    pub fn observe_delay(&mut self, delay: MediaTime) {
        let f = self.cfg.forget_factor;
        for c in &mut self.counts {
            *c *= f;
        }
        self.total = self.total * f + 1.0;
        let idx = self.bucket_of(delay);
        self.counts[idx] += 1.0;
    }

    fn target_depth(&self) -> MediaTime {
        if self.total <= 0.0 {
            return self.cfg.cold_start_depth;
        }
        let want = self.total * self.cfg.quantile;
        let mut acc = 0.0;
        let mut idx = self.counts.len() - 1;
        for (i, c) in self.counts.iter().enumerate() {
            acc += c;
            if acc >= want {
                idx = i;
                break;
            }
        }
        // Upper edge of the quantile bucket.
        let depth = MediaTime::from_micros((idx as i64 + 1) * self.cfg.bucket.as_micros());
        depth.clamp(self.cfg.min_depth, self.lag_cap + self.cushion)
    }

    /// The lag decay steers towards.
    pub fn target_lag(&self) -> MediaTime {
        (self.target_depth() - self.cushion).clamp(MediaTime::ZERO, self.lag_cap)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cold_start_governs_until_evidence() {
        let mut auto = AutoDepth::new(
            AutoConfig {
                cold_start_depth: MediaTime::from_millis(1500),
                ..AutoConfig::default()
            },
            MediaTime::from_millis(500),
            MediaTime::from_secs(10),
        );
        // Target reads the seed bucket's upper edge: within one bucket.
        let lag = auto.target_lag();
        assert!(
            lag >= MediaTime::from_millis(1000) && lag <= MediaTime::from_millis(1020),
            "cold-start lag {lag} not near 1000ms"
        );
        // A clean link (zero delays) washes the seed out and the target
        // falls to the floor.
        for _ in 0..600 {
            auto.observe_delay(MediaTime::ZERO);
        }
        assert_eq!(auto.target_lag(), MediaTime::ZERO);
    }

    #[test]
    fn sustained_delays_raise_the_target() {
        let mut auto = AutoDepth::new(
            AutoConfig::default(),
            MediaTime::from_millis(500),
            MediaTime::from_secs(10),
        );
        for _ in 0..40 {
            for _ in 0..9 {
                auto.observe_delay(MediaTime::ZERO);
            }
            auto.observe_delay(MediaTime::from_millis(2000));
        }
        // 10% of arrivals delayed ~2 s: the 0.95 quantile must cover them.
        assert!(auto.target_lag() >= MediaTime::from_millis(1400));
    }
}
