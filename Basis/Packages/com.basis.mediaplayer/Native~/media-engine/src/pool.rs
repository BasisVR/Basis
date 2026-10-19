//! The leased FramePool: a small fixed pool of decoder-format frames
//! between decode and present. A full pool is backpressure on the decode
//! side. The present side never blocks: it takes the newest due frame or
//! nothing. Waiting only ever runs one way, so the backpressure cannot
//! deadlock.
//!
//! Slots carry owned [`VideoFrame`]s, so an opaque (decoder-native GPU)
//! frame moves through the pool like a CPU frame. Recycling a slot drops
//! the payload, which for an opaque frame returns the buffer to the
//! adapter's image reader. The pool depth is therefore part of the
//! adapter's outstanding-image budget.

use std::sync::{Arc, Condvar, Mutex};

use media_clock::MediaTime;
use media_decode::VideoFrame;

pub const POOL_SLOTS: usize = 4;

/// How far behind the audio-led clock a frame may be when it is chosen and
/// still be shown. Past it the frame is recycled and the picture already
/// on screen stays. A held picture under correct sound is preferred to a
/// moving one out of step with it.
///
/// A late picture puts the sound ahead of it, the direction people notice
/// first. EBU R37 limits end-to-end error that way to 40 ms (60 ms the
/// other way); ITU-R BT.1359-1 puts detectability at 45 ms and
/// acceptability at 90 ms (125 and 185 ms the other way). ExoPlayer drops
/// an output buffer 30 ms late, VLC a picture one frame period late.
/// Healthy playback presents a frame a few milliseconds after its time and
/// crosses 40 ms for under one frame in a thousand. The limit is a fixed
/// figure because a frame period at 60 fps would sit inside the display's
/// own quantisation. It is measured from the clock, not from the render
/// path's lookahead target.
pub const MAX_PRESENT_LATE: MediaTime = MediaTime::from_millis(40);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum SlotState {
    Free,
    Ready,
    /// Taken by the present side; frees on release.
    Leased,
}

struct Slot {
    state: SlotState,
    pts: MediaTime,
    frame: Option<VideoFrame>,
    /// The timeline the frame was decoded on. A flush clears the pool, but
    /// not before the render thread can take a lease published under the
    /// previous generation, so the frame carries its own answer.
    generation: u64,
}

struct PoolState {
    slots: Vec<Slot>,
    /// Monotonic publish counter so "newest" is well-defined.
    published: u64,
    /// Publish sequence per slot (parallel to `slots`).
    seq: Vec<u64>,
    dropped: u64,
}

pub struct FramePool {
    state: Mutex<PoolState>,
    freed: Condvar,
}

/// A filled frame currently owned by the present side.
pub struct Lease {
    pub pts: MediaTime,
    /// The generation this frame was published under; see [`Slot`].
    pub generation: u64,
    frame: Option<VideoFrame>,
    slot: usize,
}

impl Lease {
    pub fn frame(&self) -> Option<&VideoFrame> {
        self.frame.as_ref()
    }

    /// Move the frame out (the Android sink hands it to the render
    /// event); the lease still frees its slot on release.
    pub fn take_frame(&mut self) -> Option<VideoFrame> {
        self.frame.take()
    }
}

impl FramePool {
    pub fn new() -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::new(PoolState {
                slots: (0..POOL_SLOTS)
                    .map(|_| Slot {
                        state: SlotState::Free,
                        pts: MediaTime::ZERO,
                        frame: None,
                        generation: 0,
                    })
                    .collect(),
                published: 0,
                seq: vec![0; POOL_SLOTS],
                dropped: 0,
            }),
            freed: Condvar::new(),
        })
    }

    /// Decode side: publish one frame if a slot is free. A full pool hands
    /// the frame back as backpressure and the caller holds it. This never
    /// blocks, because the publishing thread may be the presenter that
    /// frees the slots.
    pub fn try_publish(&self, frame: VideoFrame, generation: u64) -> Result<(), VideoFrame> {
        let mut state = self.state.lock().expect("pool lock");
        let Some(slot_index) = state.slots.iter().position(|s| s.state == SlotState::Free) else {
            return Err(frame);
        };

        let seq = state.published + 1;
        state.published = seq;
        state.seq[slot_index] = seq;
        let slot = &mut state.slots[slot_index];
        slot.state = SlotState::Ready;
        slot.pts = MediaTime::from_micros(frame.pts_us());
        slot.frame = Some(frame);
        slot.generation = generation;
        Ok(())
    }

    /// Present side: take the newest Ready frame due at `now` (pts <= now),
    /// discarding older due frames (counted as drops). A frame more than
    /// [`MAX_PRESENT_LATE`] behind `clock` is discarded too, so nothing is
    /// returned where every due frame is that late. `clock` is the clock's
    /// own reading; `now` may run ahead of it by a selection lookahead.
    /// Never blocks.
    pub fn take_due(&self, now: MediaTime, clock: MediaTime) -> Option<Lease> {
        let state = self.state.lock().expect("pool lock");
        self.take_due_locked(state, now, clock)
    }

    /// `take_due` for the render thread. It try-locks, so a publish in
    /// flight on the video thread costs a re-present, never a wait.
    pub fn try_take_due(&self, now: MediaTime, clock: MediaTime) -> Option<Lease> {
        let state = self.state.try_lock().ok()?;
        self.take_due_locked(state, now, clock)
    }

    fn take_due_locked(
        &self,
        mut state: std::sync::MutexGuard<'_, PoolState>,
        now: MediaTime,
        clock: MediaTime,
    ) -> Option<Lease> {
        // On the stack: this runs on the render thread once per event.
        let mut found = [0usize; POOL_SLOTS];
        let mut count = 0;
        for (i, slot) in state.slots.iter().enumerate() {
            if slot.state == SlotState::Ready && slot.pts <= now {
                found[count] = i;
                count += 1;
            }
        }
        let due = &mut found[..count];
        due.sort_unstable_by_key(|&i| state.seq[i]);
        // The newest due frame is shown unless it is too late to be in
        // step with the sound, in which case every due frame is.
        let (newest, due) = match due.split_last() {
            Some((&last, older)) if clock - state.slots[last].pts <= MAX_PRESENT_LATE => {
                (Some(last), older)
            }
            _ => (None, &*due),
        };
        for &stale in due {
            state.slots[stale].state = SlotState::Free;
            state.slots[stale].frame = None;
            state.dropped += 1;
        }
        let Some(newest) = newest else {
            if !due.is_empty() {
                self.freed.notify_all();
            }
            return None;
        };
        let slot = &mut state.slots[newest];
        slot.state = SlotState::Leased;
        let lease = Lease {
            pts: slot.pts,
            generation: slot.generation,
            frame: slot.frame.take(),
            slot: newest,
        };
        if !due.is_empty() {
            self.freed.notify_all();
        }
        Some(lease)
    }

    /// The pts of the oldest Ready frame, if any: the restart point for a
    /// parked clock.
    pub fn first_ready_pts(&self) -> Option<MediaTime> {
        let state = self.state.lock().expect("pool lock");
        (0..state.slots.len())
            .filter(|&i| state.slots[i].state == SlotState::Ready)
            .min_by_key(|&i| state.seq[i])
            .map(|i| state.slots[i].pts)
    }

    /// How many frames are waiting.
    pub fn ready_count(&self) -> usize {
        let state = self.state.lock().expect("pool lock");
        state
            .slots
            .iter()
            .filter(|s| s.state == SlotState::Ready)
            .count()
    }

    pub fn dropped(&self) -> u64 {
        self.state.lock().expect("pool lock").dropped
    }

    /// Free a lease's slot. The frame itself is the caller's to keep or
    /// drop: an opaque frame may need to outlive the lease until the render
    /// thread has consumed it.
    pub fn release(&self, lease: Lease) {
        let mut state = self.state.lock().expect("pool lock");
        state.slots[lease.slot].state = SlotState::Free;
        drop(state);
        self.freed.notify_all();
    }

    /// Flush every waiting frame (seek/teardown). A leased slot stays
    /// leased until its holder releases it.
    pub fn clear(&self) {
        let mut state = self.state.lock().expect("pool lock");
        for slot in &mut state.slots {
            if slot.state == SlotState::Ready {
                slot.state = SlotState::Free;
                slot.frame = None;
            }
        }
        drop(state);
        self.freed.notify_all();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use media_decode::{ColorInfo, Nv12Frame};

    fn frame(pts_ms: i64) -> VideoFrame {
        VideoFrame::Nv12(Nv12Frame {
            width: 2,
            height: 2,
            pts_us: MediaTime::from_millis(pts_ms).as_micros(),
            color: ColorInfo::default(),
            data: vec![pts_ms as u8; 8],
        })
    }

    #[test]
    fn newest_due_frame_wins_and_older_are_recycled() {
        let pool = FramePool::new();
        for ms in [0, 33, 66] {
            assert!(pool.try_publish(frame(ms), 0).is_ok());
        }
        // At t=50ms, frames 0 and 33 are due; 33 wins, 0 is dropped.
        let at = MediaTime::from_millis(50);
        let lease = pool.take_due(at, at).expect("due");
        assert_eq!(lease.pts, MediaTime::from_millis(33));
        assert_eq!(pool.dropped(), 1);
        // 66 is not due yet.
        assert!(pool.take_due(at, at).is_none());
        pool.release(lease);
        let at = MediaTime::from_millis(70);
        let lease = pool.take_due(at, at).expect("66 due");
        assert_eq!(lease.pts, MediaTime::from_millis(66));
        pool.release(lease);
    }

    #[test]
    fn a_frame_too_late_to_be_in_step_is_recycled_not_shown() {
        let pool = FramePool::new();
        for ms in [0, 33] {
            assert!(pool.try_publish(frame(ms), 0).is_ok());
        }
        // The newest due frame is 41 ms behind the clock: nothing is shown,
        // and both slots come back to the decoder.
        let at = MediaTime::from_millis(33) + MAX_PRESENT_LATE + MediaTime::from_millis(1);
        assert!(pool.take_due(at, at).is_none());
        assert_eq!(pool.dropped(), 2);
        assert_eq!(pool.ready_count(), 0);
    }

    #[test]
    fn a_frame_at_the_limit_is_shown() {
        let pool = FramePool::new();
        assert!(pool.try_publish(frame(33), 0).is_ok());
        let at = MediaTime::from_millis(33) + MAX_PRESENT_LATE;
        let lease = pool.take_due(at, at).expect("at the limit");
        assert_eq!(lease.pts, MediaTime::from_millis(33));
        pool.release(lease);
    }

    #[test]
    fn lateness_is_measured_from_the_clock_not_the_lookahead() {
        let pool = FramePool::new();
        assert!(pool.try_publish(frame(33), 0).is_ok());
        // Selected a vsync ahead of a clock the frame is 30 ms behind:
        // measured from the target it would read 47 ms late and be lost.
        let clock = MediaTime::from_millis(63);
        let target = clock + MediaTime::from_millis(17);
        let lease = pool
            .take_due(target, clock)
            .expect("in step with the clock");
        assert_eq!(lease.pts, MediaTime::from_millis(33));
        pool.release(lease);
    }

    #[test]
    fn full_pool_is_backpressure_not_a_drop() {
        let pool = FramePool::new();
        for ms in 0..POOL_SLOTS as i64 {
            assert!(pool.try_publish(frame(ms * 33), 0).is_ok());
        }
        assert!(pool.try_publish(frame(999), 0).is_err());
        assert_eq!(pool.dropped(), 0);
        // Present frees slots (all four due: newest wins, three recycled);
        // the publish then lands.
        let at = MediaTime::from_millis((POOL_SLOTS as i64 - 1) * 33 + 10);
        let lease = pool.take_due(at, at).expect("due");
        assert_eq!(
            lease.pts,
            MediaTime::from_millis((POOL_SLOTS as i64 - 1) * 33)
        );
        assert_eq!(pool.dropped(), 3);
        pool.release(lease);
        assert!(pool.try_publish(frame(999), 0).is_ok());
    }
}
