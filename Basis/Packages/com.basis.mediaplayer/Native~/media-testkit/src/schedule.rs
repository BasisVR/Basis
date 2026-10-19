//! Synthetic arrival schedules: turn a recorded gap distribution into the
//! per-AU arrival timeline a live TCP source would have produced: 1x
//! delivery, halted during each gap, with the accumulated media arriving as
//! a recovery burst when the gap ends.

use crate::phase0::GapCapture;
use media_clock::MediaTime;

/// One access unit's worth of arrival: when it landed off the network, its
/// decode timestamp, and its size.
#[derive(Debug, Clone, Copy)]
pub struct ArrivalAu {
    pub arrival: MediaTime,
    pub dts: MediaTime,
    pub bytes: usize,
}

#[derive(Debug, Clone)]
pub struct ArrivalSchedule {
    pub aus: Vec<ArrivalAu>,
}

impl ArrivalSchedule {
    /// Replay a capture as a constant-cadence AU stream. `au_interval` is the
    /// media duration per AU (frame interval), `au_bytes` its compressed
    /// size. An AU whose 1x arrival falls inside a gap window arrives at the
    /// window's end instead.
    ///
    /// Each reconstructed gap includes the drain lead-in before its starve
    /// became visible, so clustered gaps' windows can overlap on the
    /// recorded timeline even though the recording shows delivery resuming
    /// between them. They are laid out sequentially here, separated by at
    /// least one AU interval so each recovery burst lands (and refills the
    /// bank) before the next outage begins. Every gap stays a distinct
    /// outage of its reconstructed duration, matching the analytic model's
    /// refill-between-gaps assumption.
    pub fn from_capture(capture: &GapCapture, au_interval: MediaTime, au_bytes: usize) -> Self {
        let mut gaps = capture.gaps.clone();
        gaps.sort_by_key(|g| g.start);
        let mut windows: Vec<(MediaTime, MediaTime)> = Vec::with_capacity(gaps.len());
        let mut cursor = MediaTime::ZERO;
        for g in gaps {
            let start = g.start.max(cursor);
            let end = start + g.dur;
            windows.push((start, end));
            cursor = end + au_interval;
        }

        let mut aus = Vec::new();
        let mut dts = MediaTime::ZERO;
        while dts < capture.duration {
            let mut arrival = dts;
            // Windows are sorted and disjoint; a deferral to one window's
            // end may land exactly on the next window's start, so keep
            // scanning forward.
            for &(start, end) in &windows {
                if arrival >= start && arrival < end {
                    arrival = end;
                }
            }
            aus.push(ArrivalAu {
                arrival,
                dts,
                bytes: au_bytes,
            });
            dts += au_interval;
        }
        Self { aus }
    }
}
