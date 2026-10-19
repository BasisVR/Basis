//! The sample data of the movie fragments whose samples are queued.
//!
//! A fragment keeps each track's samples in one run, and decode order
//! alternates between the runs: read a sample at a time from a ranged
//! source, that is a request per cache block. Held here, each run is read
//! in file order instead, the shorter runs when the fragment is queued and
//! the longest on one request as its samples are served. The first sample
//! then waits only for its own bytes. A small fragment is one run, read
//! whole when queued. A run is let go once every sample
//! queued in it has been served.

use std::collections::VecDeque;

use crate::source::{ByteSource, SourceError};

/// A run past this is not held; its samples are read one at a time.
const MAX_RUN_BYTES: u64 = 16 * 1024 * 1024;
/// Nor is a run that would take what is held past this.
const MAX_HELD_BYTES: u64 = 48 * 1024 * 1024;
/// A fragment whose data spans no more than this is read whole when it is
/// queued: a request per track would cost more than waiting for the rest.
/// Past it, the wait for the whole of it would delay the first sample and
/// every seek by seconds on a slow link.
const WHOLE_BYTES: u64 = 4 * 1024 * 1024;

struct Run {
    start: u64,
    bytes: Vec<u8>,
    /// How much of `bytes` has been read.
    filled: usize,
    /// Samples queued in the run and not yet served.
    remaining: usize,
}

impl Run {
    fn end(&self) -> u64 {
        self.start + self.bytes.len() as u64
    }

    fn fill_to(&mut self, src: &mut dyn ByteSource, to: usize) -> Result<(), SourceError> {
        while self.filled < to {
            let n = src.read_at(
                self.start + self.filled as u64,
                &mut self.bytes[self.filled..],
            )?;
            if n == 0 {
                return Err("the source ended inside a movie fragment".into());
            }
            self.filled += n;
        }
        Ok(())
    }
}

#[derive(Default)]
pub(crate) struct HeldRuns {
    runs: VecDeque<Run>,
    held: u64,
}

impl HeldRuns {
    /// Hold the data one fragment's queued samples lie in, given as each
    /// track's `(offset, size)` list: a run per track, merged where two
    /// overlap, or one run across them all for a small fragment. Every run
    /// but the longest of a large fragment is read now.
    pub fn hold(&mut self, src: &mut dyn ByteSource, tracks: &[Vec<(u64, u32)>]) {
        let mut extents: Vec<(u64, u64, usize)> = tracks
            .iter()
            .filter_map(|samples| {
                let first = samples.iter().map(|s| s.0).min()?;
                let end = samples
                    .iter()
                    .map(|s| s.0.saturating_add(u64::from(s.1)))
                    .max()?;
                Some((first, end, samples.len()))
            })
            .collect();
        extents.sort_unstable();
        let mut merged: Vec<(u64, u64, usize)> = Vec::with_capacity(extents.len());
        for (start, end, count) in extents {
            match merged.last_mut() {
                Some(last) if start < last.1 => {
                    last.1 = last.1.max(end);
                    last.2 += count;
                }
                _ => merged.push((start, end, count)),
            }
        }

        let (Some(first), Some(last)) = (merged.first(), merged.last()) else {
            return;
        };
        let whole = last.1 - first.0 <= WHOLE_BYTES;
        if whole {
            let count = merged.iter().map(|m| m.2).sum();
            merged = vec![(first.0, last.1.max(first.1), count)];
        }
        let longest = merged.iter().map(|m| m.1 - m.0).max().unwrap_or(0);
        let mut deferred = whole;
        for (start, end, remaining) in merged {
            let len = end - start;
            if len == 0 || len > MAX_RUN_BYTES || self.held + len > MAX_HELD_BYTES {
                continue;
            }
            let mut run = Run {
                start,
                bytes: vec![0; len as usize],
                filled: 0,
                remaining,
            };
            if !deferred && len == longest {
                deferred = true;
            } else if run.fill_to(src, len as usize).is_err() {
                continue;
            }
            self.held += len;
            self.runs.push_back(run);
        }
    }

    /// A held sample's bytes, reading its run up to them first; `None` for
    /// a sample no run holds. A failed read gives the run up.
    pub fn serve(
        &mut self,
        src: &mut dyn ByteSource,
        offset: u64,
        size: u32,
    ) -> Option<Result<Vec<u8>, SourceError>> {
        let end = offset.checked_add(u64::from(size))?;
        let at = self
            .runs
            .iter()
            .position(|run| run.start <= offset && end <= run.end())?;
        let run = &mut self.runs[at];
        let from = (offset - run.start) as usize;
        let to = from + size as usize;
        let served = run.fill_to(src, to).map(|()| run.bytes[from..to].to_vec());
        run.remaining = run.remaining.saturating_sub(1);
        if served.is_err() || run.remaining == 0 {
            let gone = self.runs.remove(at).expect("the run was just found");
            self.held -= gone.bytes.len() as u64;
        }
        Some(served)
    }

    /// Read every run to its end before the source is used for anything
    /// else. A run still being read is the source's open response: reading
    /// elsewhere first would drop it, and the rest of the run would cost a
    /// request, and over HTTPS a connection, of its own.
    pub fn finish(&mut self, src: &mut dyn ByteSource) {
        let mut at = 0;
        while at < self.runs.len() {
            let run = &mut self.runs[at];
            let len = run.bytes.len();
            if run.fill_to(src, len).is_err() {
                let gone = self.runs.remove(at).expect("the run is there");
                self.held -= gone.bytes.len() as u64;
                continue;
            }
            at += 1;
        }
    }

    /// Hold data already read, for the `remaining` samples queued in it: a
    /// live stream's media data, which a forward-only source serves once.
    pub fn adopt(&mut self, start: u64, bytes: Vec<u8>, remaining: usize) {
        self.held += bytes.len() as u64;
        self.runs.push_back(Run {
            start,
            filled: bytes.len(),
            bytes,
            remaining,
        });
    }

    pub fn clear(&mut self) {
        self.runs.clear();
        self.held = 0;
    }

    /// Bytes held across every run.
    pub fn held(&self) -> u64 {
        self.held
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A source that hands back at most `chunk` bytes a read, as a network
    /// body does, and counts what it was asked for.
    struct Chunked {
        data: Vec<u8>,
        chunk: usize,
        bytes: u64,
    }

    impl ByteSource for Chunked {
        fn size(&mut self) -> Result<Option<u64>, SourceError> {
            Ok(Some(self.data.len() as u64))
        }

        fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
            let from = offset as usize;
            let n = buf.len().min(self.chunk).min(self.data.len() - from);
            buf[..n].copy_from_slice(&self.data[from..from + n]);
            self.bytes += n as u64;
            Ok(n)
        }
    }

    fn source(len: usize) -> Chunked {
        Chunked {
            data: (0..len).map(|i| (i % 251) as u8).collect(),
            chunk: 16 * 1024,
            bytes: 0,
        }
    }

    /// A fragment of `video` bytes of video samples of `each` bytes, then
    /// `audio` bytes of audio samples of the same size, from `at`.
    fn fragment(at: u64, video: u64, audio: u64, each: u32) -> Vec<Vec<(u64, u32)>> {
        let samples = |from: u64, len: u64| {
            (0..len / u64::from(each))
                .map(|i| (from + i * u64::from(each), each))
                .collect::<Vec<_>>()
        };
        vec![samples(at, video), samples(at + video, audio)]
    }

    #[test]
    fn the_first_sample_waits_for_its_own_bytes_not_the_fragments() {
        let mut src = source(8 << 20);
        let mut runs = HeldRuns::default();
        runs.hold(&mut src, &fragment(0, 6 << 20, 64 << 10, 4096));
        assert_eq!(src.bytes, 64 << 10, "the audio run is read when queued");

        let first = runs.serve(&mut src, 0, 4096).expect("held").expect("read");
        assert_eq!(first, src.data[..4096]);
        assert!(
            src.bytes <= (64 << 10) + (16 << 10),
            "the first video sample read {} bytes",
            src.bytes
        );
    }

    #[test]
    fn a_run_with_samples_unserved_is_kept_however_many_are_queued_after_it() {
        let mut src = source(8 << 20);
        let mut runs = HeldRuns::default();
        let fragments: Vec<_> = (0..4)
            .map(|k| fragment(k * (2 << 20), 1 << 20, 64 << 10, 4096))
            .collect();
        for fragment in &fragments {
            runs.hold(&mut src, fragment);
        }
        for fragment in &fragments {
            for &(offset, size) in fragment.iter().flatten() {
                let bytes = runs
                    .serve(&mut src, offset, size)
                    .expect("held")
                    .expect("read");
                assert_eq!(
                    bytes,
                    src.data[offset as usize..offset as usize + size as usize]
                );
            }
        }
        assert_eq!(
            src.bytes,
            4 * ((1 << 20) + (64 << 10)),
            "every byte is read once"
        );
        assert_eq!(runs.held(), 0, "served runs are let go");
    }

    #[test]
    fn a_run_being_read_is_finished_before_the_source_is_used_elsewhere() {
        let mut src = source(8 << 20);
        let mut runs = HeldRuns::default();
        let first = fragment(0, 6 << 20, 64 << 10, 4096);
        runs.hold(&mut src, &first);
        runs.serve(&mut src, 0, 4096).expect("held").expect("read");
        runs.finish(&mut src);
        assert_eq!(
            src.bytes,
            (6 << 20) + (64 << 10),
            "the video run is read to its end"
        );
        for &(offset, size) in first.iter().flatten().skip(1) {
            runs.serve(&mut src, offset, size)
                .expect("held")
                .expect("read");
        }
        assert_eq!(
            src.bytes,
            (6 << 20) + (64 << 10),
            "and nothing is read twice"
        );
    }

    #[test]
    fn a_small_fragment_is_read_whole_when_queued() {
        let mut src = source(1 << 20);
        let mut runs = HeldRuns::default();
        let small = fragment(0, 96 << 10, 16 << 10, 4096);
        runs.hold(&mut src, &small);
        assert_eq!(src.bytes, 112 << 10, "read whole when queued");
        for &(offset, size) in small.iter().flatten() {
            runs.serve(&mut src, offset, size)
                .expect("held")
                .expect("read");
        }
        assert_eq!(src.bytes, 112 << 10, "and served without reading again");
    }

    #[test]
    fn a_run_past_the_ceiling_is_left_to_be_read_a_sample_at_a_time() {
        let mut src = source(1024);
        let mut runs = HeldRuns::default();
        runs.hold(&mut src, &[vec![(0, 16), (MAX_RUN_BYTES, 16)]]);
        assert!(runs.serve(&mut src, 0, 16).is_none());
        assert_eq!(src.bytes, 0);
    }

    #[test]
    fn a_seek_lets_every_run_go() {
        let mut src = source(1 << 20);
        let mut runs = HeldRuns::default();
        runs.hold(&mut src, &fragment(0, 512 << 10, 64 << 10, 4096));
        runs.clear();
        assert_eq!(runs.held(), 0);
        assert!(runs.serve(&mut src, 0, 4096).is_none());
    }
}
