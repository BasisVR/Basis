//! Per-session PCM output: a lock-free SPSC ring of interleaved f32 frames,
//! written by the audio decode thread and drained by the Unity audio thread
//! through the ABI, with pts markers at chunk granularity. The pull path
//! takes no lock.
//!
//! The playhead comes from the media timeline (chunk pts markers
//! interpolated by frames removed) and is the session clock's master when
//! audio is present, so a source whose sample count drifts against its pts
//! cannot drag the clock. For the same kind of source the serve trims a
//! head that runs late against the clock: it delivers more samples than
//! its timeline claims, and without the trim the surplus fills the ring
//! and stalls the track upstream.

use std::sync::atomic::{AtomicI64, AtomicU32, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use media_clock::MediaTime;
use media_diag::diag_warn;

/// Ring capacity in frames, sized in seconds of audio.
const RING_SECONDS: u32 = 2;
/// Hard ceiling on the ring, in samples. The geometry that sizes it is
/// announced by the container or by a platform decoder, so neither factor
/// is trusted to name an allocation on its own. Above two seconds of
/// 384 kHz 7.1, which is past anything the codec table carries.
const MAX_RING_SAMPLES: usize = 6 * 1024 * 1024;
/// How late (µs) the ring head's pts may run against the session clock
/// before the serve trims it. Depth alone is normal: the on-demand startup
/// burst fills the ring, and live joins bank audio. A head that stays late
/// against the clock means the source delivers more samples than its
/// timeline claims. Sized above the clock's practical wobble and the pull
/// cadence, below anything a viewer would read as drift.
const TRIM_LATE_US: i64 = 300_000;
/// Per-pull trim bound, frames. The master playhead jumps by at most
/// ~21 ms at a time, which the clock absorbs without a snap.
const TRIM_MAX_FRAMES: usize = 1024;
/// Chunk pts markers buffered between producer and consumer. Only chunks
/// whose pts the consumer could not already work out take a slot, so this
/// bounds discontinuities in flight, not chunks.
const PTS_MARKERS: usize = 1024;

#[derive(Clone, Copy)]
struct PtsMarker {
    /// Absolute frame index (frames pushed before this chunk) the pts
    /// applies to.
    index: u64,
    pts_us: i64,
}

pub struct AudioFormatInfo {
    pub sample_rate: u32,
    pub channels: u32,
}

/// State shared between the producer, the consumer and pollers.
pub struct AudioShared {
    pub sample_rate: AtomicU32,
    pub channels: AtomicU32,
    /// Frames handed to the consumer since the current generation's origin.
    pub consumed_frames: AtomicU64,
    /// Media time of the first frame written this generation, µs.
    pub base_pts_us: AtomicI64,
    /// Wall time (engine µs) of the last consumer pull that made progress.
    pub last_pull_wall_us: AtomicI64,
    /// Managed sink output latency (µs): the chain after the pull (DSP
    /// buffers + HAL). Subtracted from the playhead so the clock master is
    /// the *audible* position and video paces to match. 0 = uncompensated.
    pub output_latency_us: AtomicI64,
    /// Media time of the ring head as of the last pull (µs), derived from
    /// the chunk pts markers. Once set it is what the playhead reads.
    /// `i64::MIN` until the first marker is consumed.
    pub playhead_pts_us: AtomicI64,
    /// Lock-free mirror of the session clock for the pull path, which must
    /// never take the clock lock: position and the wall it was read at,
    /// written by the audio thread each tick while the clock plays.
    /// `i64::MIN` while parked, which disables the serve trim, since
    /// startup, seeks and join backlogs legitimately hold depth.
    pub clock_now_us: AtomicI64,
    pub clock_wall_us: AtomicI64,
    /// Interleaved frames pushed this generation (production counter).
    pub pushed_frames: AtomicU64,
    /// Frames discarded by the serve-side lateness trim, this generation.
    /// Reset with the rest of the block, because the ring's invariant
    /// (served plus trimmed equals pushed) only holds inside one
    /// generation.
    pub trimmed_frames: AtomicU64,
    /// The same trim, counted for the life of the session and never reset.
    ///
    /// Diagnostics need a figure that only climbs. A capture column that
    /// drops after a seek loses the earlier trims, and the trim event is
    /// rate-limited against a high-water mark that a reset would leave above
    /// the counter, silencing it until the new generation passed the old
    /// total.
    pub trimmed_frames_total: AtomicU64,
}

impl AudioShared {
    fn new() -> Self {
        Self {
            sample_rate: AtomicU32::new(0),
            channels: AtomicU32::new(0),
            consumed_frames: AtomicU64::new(0),
            base_pts_us: AtomicI64::new(0),
            last_pull_wall_us: AtomicI64::new(i64::MIN),
            output_latency_us: AtomicI64::new(0),
            playhead_pts_us: AtomicI64::new(i64::MIN),
            clock_now_us: AtomicI64::new(i64::MIN),
            clock_wall_us: AtomicI64::new(i64::MIN),
            pushed_frames: AtomicU64::new(0),
            trimmed_frames: AtomicU64::new(0),
            trimmed_frames_total: AtomicU64::new(0),
        }
    }

    /// The audio playhead: the ring head's media time as of the last pull
    /// (pts markers interpolated by frames removed, so the pts timeline
    /// wins over the sample count), extrapolated by the time since that
    /// pull. The consumer drains in DSP-buffer quanta, so without the
    /// extrapolation the position would stair-step a buffer behind real
    /// time. `None` until a format is set and consumption has begun.
    pub fn playhead(&self, wall: MediaTime) -> Option<MediaTime> {
        let rate = self.sample_rate.load(Ordering::Relaxed);
        if rate == 0 {
            return None;
        }
        let consumed = self.consumed_frames.load(Ordering::Relaxed);
        if consumed == 0 {
            return None;
        }
        let since_pull = (wall
            - MediaTime::from_micros(self.last_pull_wall_us.load(Ordering::Relaxed)))
        .clamp(MediaTime::ZERO, MediaTime::from_millis(40));
        let latency = MediaTime::from_micros(self.output_latency_us.load(Ordering::Relaxed));
        let position = match self.playhead_pts_us.load(Ordering::Relaxed) {
            i64::MIN => {
                // No marker consumed yet: count samples. This matches the
                // pts form until the first chunk's marker lands, which the
                // first progressing pull consumes.
                let base = self.base_pts_us.load(Ordering::Relaxed);
                base + (consumed as i64) * 1_000_000 / i64::from(rate)
            }
            pts => pts,
        };
        Some(MediaTime::from_micros(position) + since_pull - latency)
    }
}

/// Decode-thread half.
pub struct AudioProducer {
    ring: rtrb::Producer<f32>,
    markers: rtrb::Producer<PtsMarker>,
    shared: Arc<AudioShared>,
    channels: u32,
    sample_rate: u32,
    base_set: bool,
    pushed_frames: u64,
    /// The newest marker written, which is the base the consumer will
    /// extrapolate from once it reaches it.
    last_marker: Option<PtsMarker>,
}

impl AudioProducer {
    /// Push presentable interleaved samples stamped with the first frame's
    /// pts. Frames before the media origin (negative pts from encoder
    /// priming) must already be dropped by the caller. Returns the number
    /// of *samples* written. The rest is backpressure: the caller retries
    /// after a wait, with the pts advanced past what was written.
    pub fn push(&mut self, pts_us: i64, samples: &[f32]) -> usize {
        if !self.base_set {
            self.shared.base_pts_us.store(pts_us, Ordering::Relaxed);
            self.base_set = true;
        }
        let free = self.ring.slots();
        // Whole frames only, so the interleave never shears.
        let channels = self.channels.max(1) as usize;
        let take = (free / channels * channels).min(samples.len() / channels * channels);
        if take == 0 {
            return 0;
        }
        // Marker before the samples: the consumer drains markers up to
        // its removed count, so a marker must be visible before any frame
        // it describes can be consumed.
        if let Some(marker) = self.marker_for(pts_us) {
            if self.markers.push(marker).is_err() {
                // Only the chunk's own pts says where it sits. Losing it
                // would leave the consumer extrapolating an older chunk's
                // timeline over this one, and the lateness trim would then
                // discard audio on an arbitrary error. Take the
                // backpressure as a full sample ring does: the caller
                // retries once the consumer has drained.
                return 0;
            }
            self.last_marker = Some(marker);
        }
        for &s in &samples[..take] {
            let _ = self.ring.push(s);
        }
        self.pushed_frames += (take / channels) as u64;
        self.shared
            .pushed_frames
            .store(self.pushed_frames, Ordering::Relaxed);
        take
    }

    /// The marker this chunk needs, or `None` when the consumer would
    /// already put the chunk where it belongs.
    ///
    /// Chunk cadence is the stream's to choose and the marker ring is
    /// fixed, so a stream of minimum-size access units (FLAC blocks go down
    /// to sixteen samples) would otherwise exhaust it. A contiguous chunk
    /// tells the consumer nothing new: interpolating from its current
    /// marker by frames removed already gives this chunk's pts. Comparing
    /// against the retained marker, not the previous chunk, keeps the error
    /// within the tolerance instead of letting it accrue.
    fn marker_for(&self, pts_us: i64) -> Option<PtsMarker> {
        let marker = PtsMarker {
            index: self.pushed_frames,
            pts_us,
        };
        let rate = i64::from(self.sample_rate.max(1));
        // One sample period, the resolution the consumer interpolates at.
        let tolerance = (1_000_000 / rate).max(1);
        let redundant = self.last_marker.is_some_and(|last| {
            // Saturating: the pts comes from the stream, and a hostile one
            // must cost a marker, not a panic.
            let ahead = i64::try_from(marker.index.saturating_sub(last.index)).unwrap_or(i64::MAX);
            let predicted = last
                .pts_us
                .saturating_add(ahead.saturating_mul(1_000_000) / rate);
            pts_us.saturating_sub(predicted).saturating_abs() <= tolerance
        });
        (!redundant).then_some(marker)
    }

    pub fn free_frames(&self) -> usize {
        self.ring.slots() / self.channels.max(1) as usize
    }

    /// True once the consumer has pulled everything pushed so far.
    pub fn is_drained(&self) -> bool {
        self.ring.slots() == self.ring.buffer().capacity()
    }

    pub fn sample_rate(&self) -> u32 {
        self.sample_rate
    }

    pub fn channels(&self) -> u32 {
        self.channels
    }
}

/// Unity-audio-thread half. Lock-free pull.
pub struct AudioConsumer {
    ring: rtrb::Consumer<f32>,
    markers: rtrb::Consumer<PtsMarker>,
    shared: Arc<AudioShared>,
    channels: u32,
    sample_rate: u32,
    /// Frames removed from the ring (served + trimmed): the index space
    /// the pts markers map onto.
    removed_frames: u64,
    /// The newest marker at or before `removed_frames`.
    anchor: Option<PtsMarker>,
}

impl AudioConsumer {
    /// Advance the pts anchor to the newest marker at or before the ring
    /// head, and return the head's media time (µs) when a marker has been
    /// seen.
    fn head_pts(&mut self) -> Option<i64> {
        loop {
            let due = self
                .markers
                .peek()
                .map(|m| m.index <= self.removed_frames)
                .unwrap_or(false);
            if !due {
                break;
            }
            if let Ok(marker) = self.markers.pop() {
                self.anchor = Some(marker);
            }
        }
        self.anchor.map(|anchor| {
            let ahead = (self.removed_frames - anchor.index) as i64;
            anchor.pts_us + ahead * 1_000_000 / i64::from(self.sample_rate.max(1))
        })
    }

    /// Fill `out` with as many whole frames as are ready and zero the
    /// remainder. Returns frames written. `wall_us` stamps consumer
    /// liveness, which master selection reads.
    pub fn pull(&mut self, out: &mut [f32], wall_us: i64) -> usize {
        let channels = self.channels.max(1) as usize;
        let rate = i64::from(self.sample_rate.max(1));

        // Serve-side trim (see `TRIM_LATE_US`). The late span has no place
        // on the timeline, so it is discarded in bounded steps. A parked
        // clock disables the check.
        let clock_now = self.shared.clock_now_us.load(Ordering::Relaxed);
        if clock_now != i64::MIN
            && let Some(head) = self.head_pts()
        {
            let since =
                (wall_us - self.shared.clock_wall_us.load(Ordering::Relaxed)).clamp(0, 100_000);
            let late = (clock_now + since) - head - TRIM_LATE_US;
            if late > 0 {
                let trim = ((late * rate / 1_000_000) as usize)
                    .min(TRIM_MAX_FRAMES)
                    .min(self.ring.slots() / channels);
                for _ in 0..trim * channels {
                    let _ = self.ring.pop();
                }
                self.removed_frames += trim as u64;
                self.shared
                    .trimmed_frames
                    .fetch_add(trim as u64, Ordering::Relaxed);
                // Both counters are bumped here. Deriving the session figure
                // from the per-generation one would need the generation
                // install to snapshot it first, and the install resets the
                // block from inside.
                self.shared
                    .trimmed_frames_total
                    .fetch_add(trim as u64, Ordering::Relaxed);
            }
        }

        let want = out.len() / channels * channels;
        let ready = self.ring.slots() / channels * channels;
        let take = want.min(ready);
        for slot in out[..take].iter_mut() {
            *slot = self.ring.pop().unwrap_or(0.0);
        }
        out[take..].fill(0.0);
        if take > 0 {
            self.removed_frames += (take / channels) as u64;
            self.shared
                .consumed_frames
                .fetch_add((take / channels) as u64, Ordering::Relaxed);
            self.shared
                .last_pull_wall_us
                .store(wall_us, Ordering::Relaxed);
            if let Some(head) = self.head_pts() {
                self.shared.playhead_pts_us.store(head, Ordering::Relaxed);
            }
        }
        take / channels
    }
}

/// Install a fresh ring for one generation's format (a seek's flush or a
/// mid-stream format change) and return the producer half.
///
/// The slot's lock is taken here, not by the caller, because the shared
/// block's reset and the swap must be one critical section. A pull holds
/// the slot for its whole duration, so the reset cannot land underneath
/// one in flight. If it could, that pull's stores (the retired
/// generation's playhead, consumed count and pull wall) would land on top
/// of the reset, and the clock, mastered on the playhead, would see an
/// error the size of the seek and snap to a position nothing has decoded.
/// The pull path serves silence on a failed `try_lock`, so the added
/// contention costs one block.
pub fn install_audio_generation(
    slot: &Mutex<Option<AudioConsumer>>,
    format: AudioFormatInfo,
    shared: Arc<AudioShared>,
) -> AudioProducer {
    // A pull that panicked while holding the slot poisons it. The contents
    // are replaced wholesale here, so nothing depends on the previous
    // holder having finished. Panicking instead would take the opener
    // thread down on every later seek and format change, where `read_audio`
    // only serves silence.
    let mut slot = slot.lock().unwrap_or_else(|e| e.into_inner());
    let (producer, consumer) = audio_pair(format, shared);
    *slot = Some(consumer);
    producer
}

/// Create the pair for one generation's format, resetting the shared
/// block to that generation's origin. Private because the reset is only
/// safe with the consumer slot held: go through `install_audio_generation`.
fn audio_pair(format: AudioFormatInfo, shared: Arc<AudioShared>) -> (AudioProducer, AudioConsumer) {
    // Saturating: an announced rate near u32::MAX would wrap a plain
    // multiply, silently in a release build. The announced values still go
    // to the shared state verbatim; this bounds the allocation, not the
    // timeline.
    let frames = format.sample_rate.max(8000).saturating_mul(RING_SECONDS);
    // The cap counts samples but both ends work in whole frames, so it is
    // rounded down to a whole frame. Otherwise a channel count that does
    // not divide it would leave slots no push or pull can use.
    //
    // A single frame wider than the cap cannot work either way: nothing can
    // ever be pushed. The flat cap keeps the allocation bounded and the log
    // line says the track is inert, so it does not look like a stall. No
    // decoder can reach this, since every adapter limits its channel count
    // far below the cap (8 on the MF and software routes, 64 on
    // MediaCodec).
    let channels = format.channels.max(1) as usize;
    let cap = match MAX_RING_SAMPLES / channels * channels {
        0 => {
            diag_warn!(
                "audio ring: one frame of {channels} channels exceeds the {MAX_RING_SAMPLES}-sample cap, so no audio can be banked"
            );
            MAX_RING_SAMPLES
        }
        whole => whole,
    };
    let capacity = (frames.saturating_mul(format.channels.max(1)) as usize).min(cap);
    let (producer, consumer) = rtrb::RingBuffer::new(capacity);
    let (marker_tx, marker_rx) = rtrb::RingBuffer::new(PTS_MARKERS);
    shared
        .sample_rate
        .store(format.sample_rate, Ordering::Relaxed);
    shared.channels.store(format.channels, Ordering::Relaxed);
    shared.consumed_frames.store(0, Ordering::Relaxed);
    shared.base_pts_us.store(0, Ordering::Relaxed);
    shared.playhead_pts_us.store(i64::MIN, Ordering::Relaxed);
    shared.clock_now_us.store(i64::MIN, Ordering::Relaxed);
    shared.pushed_frames.store(0, Ordering::Relaxed);
    shared.trimmed_frames.store(0, Ordering::Relaxed);
    // trimmed_frames_total is deliberately not reset: it is a session
    // total.
    (
        AudioProducer {
            ring: producer,
            markers: marker_tx,
            shared: Arc::clone(&shared),
            channels: format.channels,
            sample_rate: format.sample_rate,
            base_set: false,
            pushed_frames: 0,
            last_marker: None,
        },
        AudioConsumer {
            ring: consumer,
            markers: marker_rx,
            shared,
            channels: format.channels,
            sample_rate: format.sample_rate,
            removed_frames: 0,
            anchor: None,
        },
    )
}

pub fn new_audio_shared() -> Arc<AudioShared> {
    Arc::new(AudioShared::new())
}

/// Given a chunk starting at `pts_us` with `frames` frames at `rate`, how
/// many leading frames precede the media origin and must be dropped.
/// Rounds up, so a partially primed frame is dropped rather than
/// half-played.
pub fn frames_before_origin(pts_us: i64, frames: usize, rate: u32) -> usize {
    if pts_us >= 0 || frames == 0 || rate == 0 {
        return 0;
    }
    if pts_us == i64::MIN {
        return frames;
    }
    let drop = ((-pts_us) * i64::from(rate) + 999_999) / 1_000_000;
    usize::try_from(drop).map_or(frames, |d| d.min(frames))
}

/// Given a chunk starting at `pts_us` with `frames` frames at `rate`, how
/// many leading frames come before `end_us`, where the track stops. Rounds
/// to the nearest frame: a pts held in whole microseconds is a fraction of
/// a frame off, and the container counts whole frames.
pub fn frames_before_end(pts_us: i64, end_us: i64, frames: usize, rate: u32) -> usize {
    if end_us == i64::MAX {
        return frames;
    }
    if pts_us >= end_us || rate == 0 {
        return 0;
    }
    let kept = (i128::from(end_us) - i128::from(pts_us)) * i128::from(rate);
    usize::try_from((kept + 500_000) / 1_000_000).map_or(frames, |k| k.min(frames))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_tracks_end_keeps_the_frames_before_it() {
        // 6 s at 48 kHz: the last whole AAC frame starts at 286976 / 48000,
        // a pts of 5_978_666 us once truncated, and ends on the 288000th.
        assert_eq!(frames_before_end(5_978_666, 6_000_000, 1024, 48000), 1024);
        assert_eq!(frames_before_end(5_978_667, 6_000_000, 2048, 48000), 1024);
        // The padding frame after it, and anything later, goes whole.
        assert_eq!(frames_before_end(6_000_000, 6_000_000, 1024, 48000), 0);
        assert_eq!(frames_before_end(6_021_333, 6_000_000, 1024, 48000), 0);
        // A chunk ending before the end keeps everything.
        assert_eq!(frames_before_end(0, 6_000_000, 1024, 48000), 1024);
        // No end stated.
        assert_eq!(frames_before_end(9_000_000, i64::MAX, 1024, 48000), 1024);
        // Exact on both sides of a frame boundary: 10 frames at 1 kHz.
        assert_eq!(frames_before_end(0, 10_000, 64, 1000), 10);
        assert_eq!(frames_before_end(0, 10_499, 64, 1000), 10);
        assert_eq!(frames_before_end(0, 10_500, 64, 1000), 11);
    }

    #[test]
    fn priming_frames_are_counted_conservatively() {
        assert_eq!(frames_before_origin(0, 1024, 48000), 0);
        assert_eq!(frames_before_origin(100, 1024, 48000), 0);
        // One full AAC frame ahead of the origin.
        assert_eq!(frames_before_origin(-21333, 1024, 48000), 1024);
        // Half a frame rounds up.
        assert_eq!(frames_before_origin(-10667, 1024, 48000), 513);
        assert_eq!(frames_before_origin(i64::MIN, 7, 48000), 7);
    }

    /// The sizing factors are announced by the stream, so the ring must
    /// stay bounded whatever they say. Unbounded, a saturated rate would
    /// wrap the frame count (silently, in a release build) and request
    /// gigabytes. The shared state still carries the announced values,
    /// which the playhead maths reads verbatim.
    #[test]
    fn a_hostile_geometry_cannot_size_the_ring() {
        for (sample_rate, channels) in [
            (u32::MAX, u32::MAX),
            (u32::MAX, 2),
            (5_000_000_000u64 as u32, 8),
            // Counts that do not divide the cap, so the clamp has to round
            // rather than land on it.
            (u32::MAX, 3),
            (u32::MAX, 5),
            (u32::MAX, 7),
        ] {
            let shared = new_audio_shared();
            let (producer, _consumer) = audio_pair(
                AudioFormatInfo {
                    sample_rate,
                    channels,
                },
                Arc::clone(&shared),
            );
            assert!(
                producer.ring.slots() <= MAX_RING_SAMPLES,
                "{sample_rate} Hz / {channels} ch sized {} slots",
                producer.ring.slots()
            );
            // Whole frames only: the ring is pushed and pulled a frame at
            // a time, so a remainder is capacity nothing can reach.
            let slots = producer.ring.slots();
            if slots >= channels as usize {
                assert_eq!(
                    slots % channels as usize,
                    0,
                    "{sample_rate} Hz / {channels} ch left {} slots over",
                    slots % channels as usize
                );
            }
            assert_eq!(shared.sample_rate.load(Ordering::Relaxed), sample_rate);
            assert_eq!(shared.channels.load(Ordering::Relaxed), channels);
        }
        // A real geometry is unaffected.
        let shared = new_audio_shared();
        let (producer, _consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48_000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        assert_eq!(producer.ring.slots(), 48_000 * 2 * 2);
    }

    /// The marker ring is fixed and chunk cadence is the stream's to
    /// choose, so a source can offer more gapped chunks than there are
    /// slots. Dropping the overflow would leave the consumer extrapolating
    /// a stale chunk's timeline over the ones that followed, and the
    /// lateness trim would discard audio on the resulting error.
    #[test]
    fn a_gapped_stream_cannot_outrun_the_pts_marker_ring() {
        const FRAMES: usize = 16;
        const RATE: i64 = 48_000;
        // A whole chunk of silence between each: every chunk's pts is a
        // discontinuity, so no marker can be worked out from another.
        const STEP_US: i64 = 2 * FRAMES as i64 * 1_000_000 / RATE;

        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: RATE as u32,
                channels: 1,
            },
            Arc::clone(&shared),
        );

        let chunk = vec![0.0f32; FRAMES];
        let mut pushed: Vec<i64> = Vec::new();
        let mut pts = 0i64;
        while pushed.len() < PTS_MARKERS * 4 && producer.push(pts, &chunk) == FRAMES {
            pushed.push(pts);
            pts += STEP_US;
        }
        // Drain a chunk at a time. The playhead names the chunk at the
        // ring head, which is the one after the chunk just served.
        let mut out = vec![0.0f32; FRAMES];
        for (i, next) in pushed.iter().skip(1).enumerate() {
            consumer.pull(&mut out, 1_000 + i as i64);
            assert_eq!(
                shared.playhead_pts_us.load(Ordering::Relaxed),
                *next,
                "playhead lost the timeline at chunk {}",
                i + 1
            );
        }
        assert_eq!(
            pushed.len(),
            PTS_MARKERS,
            "the marker ring, not the sample ring, has to be what stops this"
        );
        assert!(
            producer.free_frames() > FRAMES,
            "the sample ring still had room, so the stop was back-pressure"
        );
    }

    /// Back-pressure on the marker ring must not cost depth on an ordinary
    /// stream: a contiguous chunk tells the consumer nothing it cannot
    /// already interpolate, so however small the chunks are the ring still
    /// fills to its two seconds.
    #[test]
    fn a_contiguous_stream_of_tiny_chunks_still_fills_the_ring() {
        const FRAMES: usize = 16;
        const RATE: i64 = 48_000;

        let shared = new_audio_shared();
        let (mut producer, _consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: RATE as u32,
                channels: 1,
            },
            Arc::clone(&shared),
        );

        let chunk = vec![0.0f32; FRAMES];
        let mut chunks = 0u64;
        loop {
            let pts = (chunks * FRAMES as u64) as i64 * 1_000_000 / RATE;
            if producer.push(pts, &chunk) != FRAMES {
                break;
            }
            chunks += 1;
        }
        assert!(
            chunks > PTS_MARKERS as u64 * 4,
            "only {chunks} chunks of {FRAMES} frames fit — the marker budget is capping depth"
        );
        assert!(
            producer.free_frames() < FRAMES,
            "the sample ring is what filled, not the marker ring"
        );
    }

    /// A generation swap (a seek's flush, or a mid-stream format change)
    /// rebuilds the ring and resets the shared block. A pull already in
    /// flight must not land the retired generation's playhead, consumed
    /// count and pull wall on top of that reset: the clock masters on the
    /// playhead, and the error would be the whole seek distance.
    #[test]
    fn a_generation_swap_cannot_be_undone_by_a_pull_in_flight() {
        let format = || AudioFormatInfo {
            sample_rate: 48_000,
            channels: 2,
        };
        let shared = new_audio_shared();
        let slot: Arc<Mutex<Option<AudioConsumer>>> = Arc::new(Mutex::new(None));
        let mut producer = install_audio_generation(&slot, format(), Arc::clone(&shared));

        // The retired generation, mid-file: pushed and part-consumed, so
        // the shared block carries a playhead of its own.
        producer.push(5_000_000, &vec![0.0f32; 4096]);
        let mut out = vec![0.0f32; 2048];
        slot.lock()
            .expect("slot")
            .as_mut()
            .expect("consumer")
            .pull(&mut out, 1_000);
        assert_ne!(shared.playhead_pts_us.load(Ordering::Relaxed), i64::MIN);

        // Hold the slot the way a pull in flight does, then swap.
        let in_flight = slot.lock().expect("slot");
        let at_the_swap = Arc::new(std::sync::Barrier::new(2));
        let swapping = {
            let slot = Arc::clone(&slot);
            let shared = Arc::clone(&shared);
            let at_the_swap = Arc::clone(&at_the_swap);
            std::thread::spawn(move || {
                at_the_swap.wait();
                install_audio_generation(&slot, format(), shared)
            })
        };
        // The barrier says the swap is under way. The slot guard, not
        // timing, orders it behind the pull, so a loaded machine cannot
        // change the outcome.
        at_the_swap.wait();
        let mut in_flight = in_flight;
        in_flight.as_mut().expect("consumer").pull(&mut out, 2_000);
        drop(in_flight);
        drop(swapping.join().expect("swap thread"));

        assert_eq!(
            shared.playhead_pts_us.load(Ordering::Relaxed),
            i64::MIN,
            "the retired generation's playhead survived the swap"
        );
        assert_eq!(
            shared.consumed_frames.load(Ordering::Relaxed),
            0,
            "the retired generation's consumed count survived the swap"
        );
    }

    #[test]
    fn playhead_subtracts_output_latency() {
        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        let chunk = vec![0.0f32; 2048];
        assert_eq!(producer.push(0, &chunk), 2048);
        let mut out = vec![0.0f32; 512];
        consumer.pull(&mut out, 1_000);

        let wall = MediaTime::from_micros(1_000);
        let uncompensated = shared.playhead(wall).unwrap();
        shared.output_latency_us.store(60_000, Ordering::Relaxed);
        assert_eq!(
            shared.playhead(wall).unwrap(),
            uncompensated - MediaTime::from_millis(60)
        );
    }

    /// A sink latency that shifts the master mid-play while the clock itself
    /// is not moved is slewed out, never snapped, and the clock settles
    /// within the dead band of the shifted master.
    #[test]
    fn ladder_inherits_the_latency_offset() {
        use media_clock::{ClockConfig, Correction, Generation, Master, MediaClock};

        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        let cfg = ClockConfig::default();
        let dead_band = cfg.dead_band;
        let mut clock = MediaClock::new(cfg, MediaTime::ZERO, MediaTime::ZERO, Generation(0));
        clock.set_playing(MediaTime::ZERO, true);
        clock.set_master(MediaTime::ZERO, Master::Audio);

        // The consumer pulls one 20 ms DSP block per tick, exactly at 1x,
        // from a source whose pts timeline is sample-exact.
        let block = vec![0.0f32; 960 * 2];
        let mut out = vec![0.0f32; 960 * 2];
        let mut push_pts = 0i64;
        let mut pull = |wall_us: i64| {
            producer.push(push_pts, &block);
            push_pts += 960 * 1_000_000 / 48_000;
            consumer.pull(&mut out, wall_us);
        };

        // Converged at zero latency: observations inside the dead band.
        let mut wall_us = 0;
        for _ in 0..10 {
            wall_us += 20_000;
            pull(wall_us);
            let wall = MediaTime::from_micros(wall_us);
            clock.observe_master(wall, shared.playhead(wall).unwrap());
        }
        let wall = MediaTime::from_micros(wall_us);
        let before = clock.now(wall) - shared.playhead(wall).unwrap();
        assert!(before.abs() <= dead_band, "unconverged baseline: {before}");

        // Sink latency arrives and the master shifts back 100 ms. That is
        // under the snap threshold, so the clock must slew until it sits
        // within the dead band of the shifted master.
        shared.output_latency_us.store(100_000, Ordering::Relaxed);
        let mut slewed = false;
        for _ in 0..600 {
            wall_us += 20_000;
            pull(wall_us);
            let wall = MediaTime::from_micros(wall_us);
            match clock.observe_master(wall, shared.playhead(wall).unwrap()) {
                Correction::Slew { .. } => slewed = true,
                Correction::Snap { error } => panic!("latency must never snap: {error}"),
                Correction::None => {}
            }
        }
        assert!(slewed, "a 100 ms offset must engage the slew rung");
        let wall = MediaTime::from_micros(wall_us);
        let error = clock.now(wall) - shared.playhead(wall).unwrap();
        assert!(
            error.abs() <= dead_band,
            "clock should settle onto the compensated master, error {error}"
        );
        // The settled position is about the latency behind the raw pull
        // playhead, so video due-times follow the audible timeline.
        let raw = shared.playhead(wall).unwrap() + MediaTime::from_millis(100);
        let shift = raw - clock.now(wall);
        assert!(
            (shift - MediaTime::from_millis(100)).abs() <= dead_band,
            "expected ~100 ms shift, got {shift}"
        );
    }

    /// The pts timeline, not the sample count, drives the playhead. A
    /// source stamping 1024-sample chunks ~1010 samples of pts apart (as
    /// ms-quantised passthrough sources do) must not drag the master ahead
    /// of its own timeline.
    #[test]
    fn pts_timeline_owns_the_playhead() {
        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        let chunk = vec![0.0f32; 1024 * 2];
        let mut out = vec![0.0f32; 1024 * 2];
        let mut pts_us = 0i64;
        for i in 0..40 {
            assert_eq!(producer.push(pts_us, &chunk), 1024 * 2);
            pts_us += 1010 * 1_000_000 / 48_000;
            consumer.pull(&mut out, (i + 1) * 21_000);
        }
        // 40 chunks consumed: sample count says 40*1024 frames = 853 ms;
        // the timeline says the head sits at chunk 40's pts.
        let playhead = shared
            .playhead(MediaTime::from_micros(40 * 21_000))
            .unwrap();
        let sample_counted = MediaTime::from_micros(40 * 1024 * 1_000_000 / 48_000);
        // The drained head extrapolates from the newest marker by sample
        // count, so the playhead sits within one chunk of the timeline.
        assert!(
            (playhead - MediaTime::from_micros(pts_us)).abs() < MediaTime::from_millis(2),
            "playhead must sit on the pts timeline, got {playhead} vs {pts_us}us"
        );
        assert!(
            sample_counted - playhead > MediaTime::from_millis(10),
            "the drift this guards against must be visible in the fixture"
        );
    }

    /// A head that runs late against the session clock trims in bounded
    /// steps. The clock mirror is driven at wall rate while the fixture's
    /// pts advance at half the sample count (an exaggerated ms-quantised
    /// passthrough source).
    #[test]
    fn serve_trims_a_head_late_against_the_clock() {
        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        let chunk = vec![0.0f32; 1024 * 2];
        let mut pts_us = 0i64;
        let mut pushed = 0usize;
        loop {
            let wrote = producer.push(pts_us, &chunk);
            pushed += wrote / 2;
            if wrote < chunk.len() {
                break;
            }
            pts_us += 512 * 1_000_000 / 48_000;
        }

        let mut out = vec![0.0f32; 512 * 2];
        let mut served = 0u64;
        let mut wall = 0i64;
        let mut last_trimmed = 0u64;
        for _ in 0..1000 {
            wall += 10_000;
            shared.clock_now_us.store(wall, Ordering::Relaxed);
            shared.clock_wall_us.store(wall, Ordering::Relaxed);
            served += consumer.pull(&mut out, wall) as u64;
            let trimmed = shared.trimmed_frames.load(Ordering::Relaxed);
            assert!(
                trimmed - last_trimmed <= 1024,
                "trims must step in bounded increments"
            );
            last_trimmed = trimmed;
        }
        let trimmed = shared.trimmed_frames.load(Ordering::Relaxed);
        assert!(trimmed > 0, "a chronically late head must trim");
        assert_eq!(
            served + trimmed,
            pushed as u64,
            "served + trimmed must account for every pushed frame"
        );
    }

    /// The session trim total survives a generation change; the
    /// per-generation counter does not.
    ///
    /// A seek reinstalls the audio generation, which resets the shared
    /// block. If that reset reached the total, the capture column would fall
    /// after a seek and the rate-limited trim event would go quiet until the
    /// new generation passed the old total.
    #[test]
    fn the_session_trim_total_survives_a_generation_change() {
        fn trim_some(shared: &Arc<AudioShared>) {
            let (mut producer, mut consumer) = audio_pair(
                AudioFormatInfo {
                    sample_rate: 48000,
                    channels: 2,
                },
                Arc::clone(shared),
            );
            let chunk = vec![0.0f32; 1024 * 2];
            let mut pts_us = 0i64;
            loop {
                let wrote = producer.push(pts_us, &chunk);
                if wrote < chunk.len() {
                    break;
                }
                // Half the sample count, so the head runs late against the
                // clock and the serve trim engages.
                pts_us += 512 * 1_000_000 / 48_000;
            }
            let mut out = vec![0.0f32; 512 * 2];
            let mut wall = 0i64;
            for _ in 0..200 {
                wall += 10_000;
                shared.clock_now_us.store(wall, Ordering::Relaxed);
                shared.clock_wall_us.store(wall, Ordering::Relaxed);
                consumer.pull(&mut out, wall);
            }
        }

        let shared = new_audio_shared();

        trim_some(&shared);
        let first_generation = shared.trimmed_frames.load(Ordering::Relaxed);
        let first_total = shared.trimmed_frames_total.load(Ordering::Relaxed);
        assert!(first_generation > 0, "the fixture must actually trim");
        assert_eq!(first_generation, first_total, "one generation, one figure");

        // The seek. audio_pair is what install_audio_generation calls, and the
        // reset lives there.
        trim_some(&shared);
        let second_generation = shared.trimmed_frames.load(Ordering::Relaxed);
        let second_total = shared.trimmed_frames_total.load(Ordering::Relaxed);

        assert!(
            second_total > first_total,
            "the session total only climbs: {second_total} against {first_total}"
        );
        // Both checks are needed: the per-generation counter restarted and
        // carries only this generation's share, while the total carries both.
        // Either alone passes with the reset reaching the wrong counter.
        assert_eq!(
            second_total,
            first_total + second_generation,
            "the total is every generation's trim summed"
        );
    }

    /// A full ring on an honest timeline (the on-demand startup burst) must
    /// never trim: the trigger is lateness, not depth.
    #[test]
    fn full_ring_on_an_honest_timeline_never_trims() {
        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        let chunk = vec![0.0f32; 1024 * 2];
        let mut pts_us = 0i64;
        let mut pushed = 0usize;
        loop {
            let wrote = producer.push(pts_us, &chunk);
            pushed += wrote / 2;
            pts_us += (wrote / 2) as i64 * 1_000_000 / 48_000;
            if wrote < chunk.len() {
                break;
            }
        }
        let mut out = vec![0.0f32; 512 * 2];
        let mut served = 0u64;
        let mut wall = 0i64;
        for _ in 0..1000 {
            wall += 10_000;
            shared.clock_now_us.store(wall, Ordering::Relaxed);
            shared.clock_wall_us.store(wall, Ordering::Relaxed);
            served += consumer.pull(&mut out, wall) as u64;
        }
        assert_eq!(shared.trimmed_frames.load(Ordering::Relaxed), 0);
        assert_eq!(served, pushed as u64, "every frame plays");
    }

    #[test]
    fn ring_round_trips_and_tracks_playhead() {
        let shared = new_audio_shared();
        let (mut producer, mut consumer) = audio_pair(
            AudioFormatInfo {
                sample_rate: 48000,
                channels: 2,
            },
            Arc::clone(&shared),
        );
        assert!(
            shared.playhead(MediaTime::ZERO).is_none(),
            "no consumption yet"
        );

        let chunk: Vec<f32> = (0..2048).map(|i| i as f32).collect();
        assert_eq!(producer.push(0, &chunk), 2048);

        let mut out = vec![0.0f32; 512];
        assert_eq!(consumer.pull(&mut out, 1_000), 256);
        assert_eq!(out[0], 0.0);
        assert_eq!(out[511], 511.0);
        // 256 frames at 48 kHz = 5333 µs.
        assert_eq!(
            shared.playhead(MediaTime::from_micros(1_000)),
            Some(MediaTime::from_micros(5333))
        );

        // Drain past what is banked: remainder must be silence.
        let mut out = vec![1.0f32; 4096];
        let frames = consumer.pull(&mut out, 2_000);
        assert_eq!(frames, (2048 - 512) / 2);
        assert_eq!(out[2048 - 512], 0.0);
    }
}
