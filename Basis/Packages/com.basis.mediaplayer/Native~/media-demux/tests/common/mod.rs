//! Shared pieces for the fragmented-MP4 rows.
//!
//! A real long video puts megabytes between one fragment header and the
//! next, which is what makes the open-time walk expensive; the committed
//! fixtures put kilobytes between them so a single cache block covers
//! dozens. [`inflate`] restores the spacing by padding every `mdat` with
//! bytes no sample points at, and [`SparseSource`] serves those bytes as
//! zeros so the file costs its real size in memory rather than its
//! declared one.

use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};

use media_demux::{ByteSource, SourceError};

/// Bytes added inside every `mdat`. Past the demuxer's 256 KiB cache
/// block, so no two fragment headers share one.
pub const FRAGMENT_PAD: u64 = 320 * 1024;

/// What a source was asked for, readable after the demuxer has taken it.
#[derive(Clone, Default)]
pub struct Counters {
    pub reads: Arc<AtomicU64>,
    pub bytes: Arc<AtomicU64>,
    /// Reads that did not start where the one before ended: over HTTP,
    /// each is a request of its own and a round trip.
    pub jumps: Arc<AtomicU64>,
}

impl Counters {
    pub fn bytes(&self) -> u64 {
        self.bytes.load(Ordering::Relaxed)
    }

    pub fn jumps(&self) -> u64 {
        self.jumps.load(Ordering::Relaxed)
    }
}

/// A source of real byte runs separated by virtual zeros.
pub struct SparseSource {
    /// `(start, bytes)` in ascending, non-overlapping order.
    runs: Vec<(u64, Vec<u8>)>,
    len: u64,
    counters: Counters,
    /// Where the last read ended.
    reached: Option<u64>,
}

impl SparseSource {
    pub fn new(runs: Vec<(u64, Vec<u8>)>, len: u64) -> Self {
        Self {
            runs,
            len,
            counters: Counters::default(),
            reached: None,
        }
    }

    pub fn len(&self) -> u64 {
        self.len
    }

    pub fn counters(&self) -> Counters {
        self.counters.clone()
    }
}

impl ByteSource for SparseSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(Some(self.len))
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        if offset >= self.len || buf.is_empty() {
            return Ok(0);
        }
        // The run at or before `offset`, and the one after it: inside the
        // first the bytes are real, between them they are zeros.
        let next = self.runs.partition_point(|(start, _)| *start <= offset);
        let available = match next.checked_sub(1).map(|i| &self.runs[i]) {
            Some((start, bytes)) if offset < start + bytes.len() as u64 => {
                let from = (offset - start) as usize;
                let n = buf.len().min(bytes.len() - from);
                buf[..n].copy_from_slice(&bytes[from..from + n]);
                n
            }
            _ => {
                let until = self.runs.get(next).map_or(self.len, |(start, _)| *start);
                let n = buf.len().min((until - offset) as usize);
                buf[..n].fill(0);
                n
            }
        };
        self.counters.reads.fetch_add(1, Ordering::Relaxed);
        if self.reached != Some(offset) {
            self.counters.jumps.fetch_add(1, Ordering::Relaxed);
        }
        self.reached = Some(offset + available as u64);
        self.counters
            .bytes
            .fetch_add(available as u64, Ordering::Relaxed);
        Ok(available)
    }
}

/// A progressive file whose `moov` is bigger than the demuxer's cache
/// block, by giving it a `free` child of `pad` bytes. The padding is
/// virtual, so the file costs its real size in memory; the sample
/// offsets past `moov` are left where they were and are wrong
/// afterwards, which is why this serves rows about what open *fetches*
/// rather than about what it plays.
pub fn pad_moov(data: &[u8], pad: u64) -> SparseSource {
    let mut pos = 0usize;
    while pos + 8 <= data.len() {
        let size = u32::from_be_bytes(data[pos..pos + 4].try_into().expect("four bytes")) as usize;
        assert!(size >= 8 && pos + size <= data.len(), "box at {pos}");
        if &data[pos + 4..pos + 8] == b"moov" {
            let grown = u32::try_from(size as u64 + 8 + pad).expect("moov stays 32-bit");
            let mut head = data[..pos + size].to_vec();
            head[pos..pos + 4].copy_from_slice(&grown.to_be_bytes());
            // The `free` box's header is real; its body is the padding.
            head.extend_from_slice(
                &u32::try_from(8 + pad)
                    .expect("free stays 32-bit")
                    .to_be_bytes(),
            );
            head.extend_from_slice(b"free");
            let after = head.len() as u64 + pad;
            let tail = data[pos + size..].to_vec();
            let len = after + tail.len() as u64;
            return SparseSource::new(vec![(0, head), (after, tail)], len);
        }
        pos += size;
    }
    panic!("no moov in the fixture");
}

/// Spread a fragmented fixture's fragments [`FRAGMENT_PAD`] further apart
/// without changing a sample. The padding goes inside each `mdat` past
/// the samples, which `default-base-is-moof` offsets do not reach; each
/// `sidx` reference grows by the same amount so an index still tiles the
/// file; and the `mfra`, whose offsets are absolute, is dropped.
pub fn inflate(data: &[u8]) -> SparseSource {
    inflate_with(data, Indexes::Sidx)
}

/// As [`inflate`], leaving any index behind describing the file it was
/// written for rather than the one that comes out: every reference is
/// then short of its subsegment and the index covers none of the file.
pub fn inflate_untiled(data: &[u8]) -> SparseSource {
    inflate_with(data, Indexes::StaleSidx)
}

/// As [`inflate`], with the `mfra` as the only index: any `sidx` is
/// dropped, and every `tfra` entry is moved to where its fragment now is.
pub fn inflate_mfra(data: &[u8]) -> SparseSource {
    inflate_with(data, Indexes::Mfra)
}

/// As [`inflate_mfra`], leaving the `mfra` pointing at the fragments where
/// they were before the file was spread out.
pub fn inflate_stale_mfra(data: &[u8]) -> SparseSource {
    inflate_with(data, Indexes::StaleMfra)
}

#[derive(Clone, Copy, PartialEq)]
enum Indexes {
    Sidx,
    StaleSidx,
    Mfra,
    StaleMfra,
}

fn inflate_with(data: &[u8], indexes: Indexes) -> SparseSource {
    let mut moved: std::collections::BTreeMap<u64, u64> = std::collections::BTreeMap::new();
    let mut runs: Vec<(u64, Vec<u8>)> = Vec::new();
    let mut run: Vec<u8> = Vec::new();
    let mut run_start = 0u64;
    let mut out = 0u64;
    let mut pos = 0usize;

    while pos + 8 <= data.len() {
        let size = u32::from_be_bytes(data[pos..pos + 4].try_into().expect("four bytes")) as usize;
        assert!(
            size >= 8 && pos + size <= data.len(),
            "box at {pos} declares {size} bytes"
        );
        let kind: [u8; 4] = data[pos + 4..pos + 8].try_into().expect("four bytes");
        let mut boxed = data[pos..pos + size].to_vec();
        if &kind == b"moof" {
            moved.insert(pos as u64, out);
        }
        pos += size;

        match (&kind, indexes) {
            (b"mfra", Indexes::Sidx | Indexes::StaleSidx) => continue,
            (b"mfra", Indexes::Mfra) => move_fragment_offsets(&mut boxed, &moved),
            (b"sidx", Indexes::Sidx) => pad_index(&mut boxed),
            (b"sidx", Indexes::Mfra | Indexes::StaleMfra) => continue,
            _ => {}
        }
        if &kind == b"mdat" {
            let grown =
                u32::try_from(size as u64 + FRAGMENT_PAD).expect("padded mdat stays 32-bit");
            boxed[..4].copy_from_slice(&grown.to_be_bytes());
        }
        run.extend_from_slice(&boxed);
        out += size as u64;
        if &kind == b"mdat" {
            runs.push((run_start, std::mem::take(&mut run)));
            out += FRAGMENT_PAD;
            run_start = out;
        }
    }
    assert_eq!(pos, data.len(), "boxes do not tile the fixture");
    if !run.is_empty() {
        runs.push((run_start, run));
    }
    SparseSource::new(runs, out)
}

/// Point every `tfra` entry in an `mfra` at the offset its fragment moved to.
fn move_fragment_offsets(boxed: &mut [u8], moved: &std::collections::BTreeMap<u64, u64>) {
    let mut child = 8;
    while child + 8 <= boxed.len() {
        let size =
            u32::from_be_bytes(boxed[child..child + 4].try_into().expect("four bytes")) as usize;
        if &boxed[child + 4..child + 8] == b"tfra" {
            let version = boxed[child + 8];
            let sizes = u32::from_be_bytes(boxed[child + 16..child + 20].try_into().expect("four"));
            let count = u32::from_be_bytes(boxed[child + 20..child + 24].try_into().expect("four"))
                as usize;
            let wide = if version == 1 { 8 } else { 4 };
            let numbers = ((sizes >> 4) & 3) + ((sizes >> 2) & 3) + (sizes & 3) + 3;
            let mut p = child + 24;
            for _ in 0..count {
                let at = p + wide;
                let old = if wide == 8 {
                    u64::from_be_bytes(boxed[at..at + 8].try_into().expect("eight"))
                } else {
                    u64::from(u32::from_be_bytes(
                        boxed[at..at + 4].try_into().expect("four"),
                    ))
                };
                let new = *moved.get(&old).expect("a tfra entry names a fragment");
                if wide == 8 {
                    boxed[at..at + 8].copy_from_slice(&new.to_be_bytes());
                } else {
                    let new = u32::try_from(new).expect("moved offset stays 32-bit");
                    boxed[at..at + 4].copy_from_slice(&new.to_be_bytes());
                }
                p += 2 * wide + numbers as usize;
            }
        }
        child += size;
    }
}

/// Grow every reference in a `sidx` by the padding its subsegment gained.
fn pad_index(boxed: &mut [u8]) {
    let version = boxed[8];
    let mut p = if version == 0 { 28 } else { 36 };
    let count = usize::from(u16::from_be_bytes([boxed[p + 2], boxed[p + 3]]));
    p += 4;
    for _ in 0..count {
        let word = u32::from_be_bytes(boxed[p..p + 4].try_into().expect("four bytes"));
        assert_eq!(word >> 31, 0, "hierarchical reference in a fixture index");
        let grown = (word & 0x7FFF_FFFF) + FRAGMENT_PAD as u32;
        boxed[p..p + 4].copy_from_slice(&grown.to_be_bytes());
        p += 12;
    }
}

/// A live stream's source: no length, and bytes served once, in order.
/// Re-reads are served from a copy of the first [`LIVE_HEAD`] bytes, for
/// the container sniff and a demuxer starting again at zero; any other
/// read that does not continue from the last one fails.
pub struct LiveSource {
    bytes: Vec<u8>,
    /// Where the stream has been read to, readable after the demuxer has
    /// taken the source.
    served: Arc<AtomicU64>,
}

pub const LIVE_HEAD: usize = 64 * 1024;

impl LiveSource {
    pub fn new(bytes: Vec<u8>) -> Self {
        Self {
            bytes,
            served: Arc::default(),
        }
    }

    pub fn served(&self) -> Arc<AtomicU64> {
        Arc::clone(&self.served)
    }
}

impl ByteSource for LiveSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(None)
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        let served = self.served.load(Ordering::Relaxed);
        let Ok(at) = usize::try_from(offset) else {
            return Err("offset past memory".into());
        };
        if offset < served {
            if at >= LIVE_HEAD {
                return Err(format!("re-read at {offset} with the stream at {served}").into());
            }
            // The copy holds only what has been served.
            let head = &self.bytes[..LIVE_HEAD.min(served as usize)];
            let n = buf.len().min(head.len() - at);
            buf[..n].copy_from_slice(&head[at..at + n]);
            return Ok(n);
        }
        if offset > served {
            return Err(format!("skip to {offset} with the stream at {served}").into());
        }
        let n = buf.len().min(self.bytes.len().saturating_sub(at));
        buf[..n].copy_from_slice(&self.bytes[at..at + n]);
        self.served.fetch_add(n as u64, Ordering::Relaxed);
        Ok(n)
    }
}
