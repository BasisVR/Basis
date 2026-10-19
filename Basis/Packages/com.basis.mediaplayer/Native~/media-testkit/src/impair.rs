//! The deterministic impairment source: seeded delay/gap schedules
//! wrapped around any [`ByteSource`]. A wrapped source stalls its reads
//! inside each gap window and delivers normally between them; composition
//! with [`PacedSource`] turns a local file into a 1x live edge with a
//! recorded impairment on top.
//!
//! Time comes through [`WallClock`] so unit tests replay schedules
//! virtually; `bm-probe impair` wires [`RealClock`] into a real session, so
//! the stalls happen on the demux thread where a slow network would put
//! them.

use std::sync::Arc;
use std::time::{Duration, Instant};

use media_clock::MediaTime;
use media_demux::{ByteSource, SourceError};

use crate::phase0::GapCapture;
use crate::rng::Xorshift64Star;

/// Wall time for impairment scheduling: injectable so tests are virtual.
pub trait WallClock: Send + Sync {
    fn now(&self) -> MediaTime;
    fn sleep_until(&self, deadline: MediaTime);
}

pub struct RealClock {
    origin: Instant,
}

impl RealClock {
    #[allow(clippy::new_without_default)]
    pub fn new() -> Self {
        Self {
            origin: Instant::now(),
        }
    }
}

impl WallClock for RealClock {
    fn now(&self) -> MediaTime {
        MediaTime::from_micros(self.origin.elapsed().as_micros() as i64)
    }

    fn sleep_until(&self, deadline: MediaTime) {
        let now = self.now();
        if deadline > now {
            std::thread::sleep(Duration::from_micros((deadline - now).as_micros() as u64));
        }
    }
}

/// A delivery-gap schedule on the wall timeline: delivery halts at each
/// window's start and resumes (with the backlog arriving as a burst, since
/// the wrapped source kept producing) at its end.
#[derive(Debug, Clone)]
pub struct ImpairProfile {
    pub name: String,
    pub windows: Vec<(MediaTime, MediaTime)>,
}

impl ImpairProfile {
    /// A recorded capture's gaps as wall windows. Reconstructed gaps carry
    /// their drain lead-in, so clustered gaps can overlap on the recorded
    /// timeline even though delivery resumed between them; laid out
    /// verbatim they would fuse into longer outages than the recording
    /// shows. They are laid out sequentially instead, separated by
    /// `separation` so each recovery burst lands before the next outage
    /// begins, as in [`crate::ArrivalSchedule::from_capture`].
    pub fn from_capture(capture: &GapCapture, separation: MediaTime) -> Self {
        let mut gaps = capture.gaps.clone();
        gaps.sort_by_key(|g| g.start);
        let mut windows: Vec<(MediaTime, MediaTime)> = Vec::with_capacity(gaps.len());
        let mut cursor = MediaTime::ZERO;
        for g in gaps {
            let start = g.start.max(cursor);
            let end = start + g.dur;
            windows.push((start, end));
            cursor = end + separation;
        }
        Self {
            name: capture.name.to_owned(),
            windows,
        }
    }

    /// The analytic residual stall for this profile's windows inside
    /// `[0, window)` at a candidate total depth: the sizing model restricted
    /// to a bounded run.
    pub fn analytic_stall_fraction(&self, depth: MediaTime, window: MediaTime) -> f64 {
        let residual: i64 = self
            .windows
            .iter()
            .filter(|(start, _)| *start < window)
            .map(|&(start, end)| ((end - start) - depth).max(MediaTime::ZERO).as_micros())
            .sum();
        residual as f64 / window.as_micros().max(1) as f64
    }

    /// Synthetic schedule: gaps of `gap` duration at mean `interval`,
    /// exponentially distributed from the seed. Deterministic per seed.
    pub fn synthetic(
        name: impl Into<String>,
        seed: u64,
        duration: MediaTime,
        interval: MediaTime,
        gap: MediaTime,
    ) -> Self {
        let mut rng = Xorshift64Star::new(seed);
        let mut windows = Vec::new();
        let mut at = MediaTime::ZERO;
        loop {
            // Exponential inter-arrival via inverse transform.
            let u = rng.next_f64().max(1e-12);
            let wait = MediaTime::from_micros((-u.ln() * interval.as_micros() as f64) as i64);
            at += wait;
            if at >= duration {
                break;
            }
            windows.push((at, at + gap));
            at += gap;
        }
        Self {
            name: name.into(),
            windows,
        }
    }

    /// The window containing `now`, if any.
    fn active_window(&self, now: MediaTime) -> Option<(MediaTime, MediaTime)> {
        self.windows
            .iter()
            .copied()
            .find(|&(start, end)| now >= start && now < end)
    }
}

/// [`ByteSource`] wrapper that stalls reads inside the profile's windows.
pub struct ImpairedSource {
    inner: Box<dyn ByteSource>,
    profile: ImpairProfile,
    clock: Arc<dyn WallClock>,
}

impl ImpairedSource {
    pub fn new(
        inner: Box<dyn ByteSource>,
        profile: ImpairProfile,
        clock: Arc<dyn WallClock>,
    ) -> Self {
        Self {
            inner,
            profile,
            clock,
        }
    }
}

impl ByteSource for ImpairedSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        self.inner.size()
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        if let Some((_, end)) = self.profile.active_window(self.clock.now()) {
            self.clock.sleep_until(end);
        }
        self.inner.read_at(offset, buf)
    }
}

/// Serves the wrapped source at a fixed byte rate from its own origin, so
/// a local file becomes a 1x live edge. Reads block until the requested
/// range has "arrived".
pub struct PacedSource {
    inner: Box<dyn ByteSource>,
    bytes_per_sec: u64,
    clock: Arc<dyn WallClock>,
}

impl PacedSource {
    pub fn new(inner: Box<dyn ByteSource>, bytes_per_sec: u64, clock: Arc<dyn WallClock>) -> Self {
        Self {
            inner,
            bytes_per_sec: bytes_per_sec.max(1),
            clock,
        }
    }
}

impl ByteSource for PacedSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        // A live edge does not state a length; the demux layer treats the
        // pace as arrival.
        Ok(None)
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        // Block until at least one byte past `offset` has arrived; a return
        // of 0 must mean the *inner* source ended, never "not yet".
        loop {
            let arrived =
                (self.clock.now().as_micros().max(0) as u64) * self.bytes_per_sec / 1_000_000;
            if arrived > offset {
                let window = (arrived - offset).min(buf.len() as u64) as usize;
                return self.inner.read_at(offset, &mut buf[..window]);
            }
            // When byte offset+1 will have arrived, rounded up.
            let due = (offset + 1)
                .saturating_mul(1_000_000)
                .div_ceil(self.bytes_per_sec);
            let now = self.clock.now();
            let deadline = MediaTime::from_micros(due as i64).max(now + MediaTime::from_micros(1));
            self.clock.sleep_until(deadline);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use media_demux::MemSource;
    use std::sync::Mutex;

    /// Virtual clock: `sleep_until` advances time instantly.
    struct VirtualClock {
        now: Mutex<MediaTime>,
    }

    impl WallClock for VirtualClock {
        fn now(&self) -> MediaTime {
            *self.now.lock().unwrap()
        }

        fn sleep_until(&self, deadline: MediaTime) {
            let mut now = self.now.lock().unwrap();
            if deadline > *now {
                *now = deadline;
            }
        }
    }

    #[test]
    fn synthetic_profiles_are_deterministic() {
        let a = ImpairProfile::synthetic(
            "syn",
            7,
            MediaTime::from_secs(60),
            MediaTime::from_secs(5),
            MediaTime::from_millis(800),
        );
        let b = ImpairProfile::synthetic(
            "syn",
            7,
            MediaTime::from_secs(60),
            MediaTime::from_secs(5),
            MediaTime::from_millis(800),
        );
        assert!(!a.windows.is_empty());
        assert_eq!(a.windows.len(), b.windows.len());
        assert_eq!(a.windows.first(), b.windows.first());
    }

    #[test]
    fn reads_defer_to_the_window_end() {
        let clock = Arc::new(VirtualClock {
            now: Mutex::new(MediaTime::ZERO),
        });
        let profile = ImpairProfile {
            name: "one-gap".into(),
            windows: vec![(MediaTime::from_secs(1), MediaTime::from_secs(3))],
        };
        let mut source = ImpairedSource::new(
            Box::new(MemSource(vec![0xAA; 1024])),
            profile,
            Arc::clone(&clock) as Arc<dyn WallClock>,
        );

        let mut buf = [0u8; 16];
        // Before the window: instant.
        source.read_at(0, &mut buf).unwrap();
        assert_eq!(clock.now(), MediaTime::ZERO);
        // Inside the window: deferred to its end.
        clock.sleep_until(MediaTime::from_millis(1500));
        source.read_at(16, &mut buf).unwrap();
        assert_eq!(clock.now(), MediaTime::from_secs(3));
    }

    #[test]
    fn paced_source_serves_at_the_configured_rate() {
        let clock = Arc::new(VirtualClock {
            now: Mutex::new(MediaTime::ZERO),
        });
        let mut source = PacedSource::new(
            Box::new(MemSource((0..=255u8).cycle().take(10_000).collect())),
            1000, // 1000 B/s
            Arc::clone(&clock) as Arc<dyn WallClock>,
        );
        assert_eq!(source.size().unwrap(), None);

        let mut buf = [0u8; 500];
        let n = source.read_at(0, &mut buf).unwrap();
        assert!(n > 0);
        // The first byte arrives essentially immediately; byte 2000 is due
        // at t=2s on a 1000 B/s pace.
        let mut far = [0u8; 100];
        source.read_at(2000, &mut far).unwrap();
        assert!(clock.now() >= MediaTime::from_secs(2));
        assert!(clock.now() < MediaTime::from_millis(2200));
    }

    #[test]
    fn capture_profiles_lay_out_sequentially() {
        let capture = GapCapture::ts_rtt300_loss005();
        let profile = ImpairProfile::from_capture(&capture, MediaTime::from_millis(100));
        assert_eq!(profile.windows.len(), capture.gaps.len());
        // No overlap after layout: every window starts after the previous
        // one ends.
        for pair in profile.windows.windows(2) {
            assert!(pair[1].0 > pair[0].1);
        }
        // Each window keeps its reconstructed duration.
        for ((start, end), gap) in profile.windows.iter().zip({
            let mut gaps = capture.gaps.clone();
            gaps.sort_by_key(|g| g.start);
            gaps
        }) {
            assert_eq!(*end - *start, gap.dur);
        }
    }
}
