//! The byte-source seam between the I/O domain and the demuxers.
//!
//! Demuxers pull; sources serve positioned reads. Network implementations
//! live in `media-io`; this crate defines the trait plus the in-memory
//! source the tests and fuzz targets drive.

use std::io::{Read, Seek, SeekFrom};

/// Errors cross the seam as boxed source errors; the engine downcasts to
/// the concrete I/O error for categorisation.
pub type SourceError = Box<dyn std::error::Error + Send + Sync>;

/// A positioned byte source. Implementations are expected to serve
/// mostly-sequential reads efficiently (an HTTP source keeps one streaming
/// response alive and only re-requests on a real seek).
pub trait ByteSource: Send {
    /// Total size in bytes, when the source knows it (VOD). Progressive MP4
    /// demuxing requires a known length; live sources, fragmented MP4
    /// included, are read sequentially.
    fn size(&mut self) -> Result<Option<u64>, SourceError>;

    /// Read up to `buf.len()` bytes at `offset`. A return of 0 means end of
    /// source.
    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError>;

    /// Fill `buf` exactly from `offset`, or fail.
    fn read_exact_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<(), SourceError> {
        let mut filled = 0usize;
        while filled < buf.len() {
            let n = self.read_at(offset + filled as u64, &mut buf[filled..])?;
            if n == 0 {
                return Err(format!(
                    "source ended at {} inside a read of {} at {}",
                    offset + filled as u64,
                    buf.len(),
                    offset
                )
                .into());
            }
            filled += n;
        }
        Ok(())
    }
}

impl ByteSource for Box<dyn ByteSource> {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        (**self).size()
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        (**self).read_at(offset, buf)
    }
}

/// In-memory source for tests and fuzz targets.
pub struct MemSource(pub Vec<u8>);

impl ByteSource for MemSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(Some(self.0.len() as u64))
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        let Ok(offset) = usize::try_from(offset) else {
            return Ok(0);
        };
        if offset >= self.0.len() {
            return Ok(0);
        }
        let n = buf.len().min(self.0.len() - offset);
        buf[..n].copy_from_slice(&self.0[offset..offset + n]);
        Ok(n)
    }
}

/// A two-block read cache over a [`ByteSource`] for demuxers whose read
/// pattern alternates between file regions (fragmented MP4 interleaves
/// per-track sample runs; metadata walks revisit headers). Over a ranged
/// HTTP source every position jump otherwise costs a fresh request; two
/// cached blocks cover the ping-pong.
pub(crate) struct CachedSource {
    src: Box<dyn ByteSource>,
    blocks: [(u64, Vec<u8>); 2],
}

const CACHE_SOURCE_BLOCK: u64 = 256 * 1024;

impl CachedSource {
    pub fn new(src: Box<dyn ByteSource>) -> Self {
        Self {
            src,
            blocks: [(u64::MAX, Vec::new()), (u64::MAX, Vec::new())],
        }
    }

    /// The source under the cache, for a reader that sizes its own
    /// fetches. A walk whose every step lands in a fresh region gets no
    /// reuse out of the blocks and pays a whole one per step.
    pub fn inner_mut(&mut self) -> &mut dyn ByteSource {
        self.src.as_mut()
    }
}

impl ByteSource for CachedSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        self.src.size()
    }

    fn read_at(&mut self, offset: u64, buf: &mut [u8]) -> Result<usize, SourceError> {
        let block_start = offset - offset % CACHE_SOURCE_BLOCK;
        let hit = self
            .blocks
            .iter()
            .position(|(start, data)| *start == block_start && !data.is_empty());
        let index = match hit {
            Some(index) => index,
            None => {
                let mut block = vec![0u8; CACHE_SOURCE_BLOCK as usize];
                let mut filled = 0usize;
                while filled < block.len() {
                    let n = self
                        .src
                        .read_at(block_start + filled as u64, &mut block[filled..])?;
                    if n == 0 {
                        break;
                    }
                    filled += n;
                }
                block.truncate(filled);
                if block.is_empty() {
                    return Ok(0);
                }
                self.blocks.swap(0, 1);
                self.blocks[0] = (block_start, block);
                0
            }
        };
        let (start, data) = &self.blocks[index];
        let off = (offset - start) as usize;
        if off >= data.len() {
            // A short cached block only happens at end of source.
            return Ok(0);
        }
        let n = buf.len().min(data.len() - off);
        buf[..n].copy_from_slice(&data[off..off + n]);
        Ok(n)
    }
}

/// Buffered sequential reader over a [`ByteSource`] for the raw-audio
/// frame walkers: cheap `peek`/`consume` over a sliding window, no
/// backwards seeks.
pub(crate) struct SeqReader {
    src: Box<dyn ByteSource>,
    /// Absolute offset of `buf[0]`.
    start: u64,
    buf: Vec<u8>,
    /// Consumed bytes within `buf`.
    off: usize,
    eof: bool,
}

const SEQ_CHUNK: usize = 64 * 1024;

impl SeqReader {
    pub fn new(src: Box<dyn ByteSource>) -> Self {
        Self {
            src,
            start: 0,
            buf: Vec::new(),
            off: 0,
            eof: false,
        }
    }

    /// Make at least `n` bytes visible (fewer only at end of source) and
    /// return everything buffered from the current position.
    pub fn peek(&mut self, n: usize) -> Result<&[u8], SourceError> {
        // Compact before the buffer grows past the window.
        if self.off >= SEQ_CHUNK {
            self.buf.drain(..self.off);
            self.start += self.off as u64;
            self.off = 0;
        }
        while self.buf.len() - self.off < n && !self.eof {
            let read_at = self.start + self.buf.len() as u64;
            let mut chunk = [0u8; SEQ_CHUNK];
            let got = self.src.read_at(read_at, &mut chunk)?;
            if got == 0 {
                self.eof = true;
                break;
            }
            self.buf.extend_from_slice(&chunk[..got]);
        }
        Ok(&self.buf[self.off..])
    }

    pub fn consume(&mut self, n: usize) {
        self.off = (self.off + n).min(self.buf.len());
    }

    /// Absolute offset of the next unread byte.
    pub fn pos(&self) -> u64 {
        self.start + self.off as u64
    }

    /// Move to an absolute position, discarding the window. Unlike
    /// [`Self::seek_to`] this accepts backwards jumps of any distance, so
    /// the source itself decides whether it can serve one: a sequential
    /// live source fails the following read rather than here.
    pub fn reposition(&mut self, pos: u64) {
        self.buf.clear();
        self.off = 0;
        self.start = pos;
        self.eof = false;
    }

    /// Jump forward to an absolute position (tag skips). A backwards jump
    /// is honoured within the buffer and refused before it.
    pub fn seek_to(&mut self, pos: u64) -> Result<(), SourceError> {
        if pos >= self.start && pos <= self.start + self.buf.len() as u64 {
            self.off = (pos - self.start) as usize;
            return Ok(());
        }
        if pos < self.start {
            return Err("backwards seek out of the buffered window".into());
        }
        self.buf.clear();
        self.off = 0;
        self.start = pos;
        self.eof = false;
        Ok(())
    }
}

/// `Read + Seek` adapter over a [`ByteSource`] for the metadata parse, with
/// a read cache sized so a box walk costs few positioned reads, and a byte
/// budget so a hostile header cannot pull unbounded metadata.
pub(crate) struct SourceReader<'a> {
    src: &'a mut dyn ByteSource,
    len: u64,
    pos: u64,
    cache: Vec<u8>,
    cache_start: u64,
    /// Bytes fetched on a cache miss, and charged to the budget. Fixed at
    /// [`CACHE_CHUNK`] over a [`CachedSource`], whose own block is that
    /// size; when the reader fetches from the source itself it starts at
    /// [`SPARSE_WINDOW`] after a jump and doubles while reads stay
    /// contiguous, so a walk that touches a header per region pays for
    /// the header rather than for a block.
    window: usize,
    min_window: usize,
    /// Metadata bytes remaining before the budget trips.
    budget: u64,
    /// Total bytes *served* remaining. The fetch budget only counts fresh
    /// chunks, so a parser looping over already-cached bytes would spin
    /// forever without touching it; a legitimate parse serves each byte a
    /// few times (header re-reads across a box walk), so a small multiple
    /// of the fetch budget turns a non-progressing loop into a typed
    /// error (fuzz-found: a hostile box layout can hang the box walk
    /// otherwise).
    serve_budget: u64,
}

pub(crate) const CACHE_CHUNK: usize = 256 * 1024;
/// First fetch after a non-contiguous jump in sparse mode. A fragment
/// header is a few hundred bytes, so a fragmented file's walk pays this
/// per fragment.
const SPARSE_WINDOW: usize = 16 * 1024;

impl<'a> SourceReader<'a> {
    /// Reads through a [`CachedSource`], whose block size the fetch
    /// window matches.
    pub fn new(src: &'a mut dyn ByteSource, len: u64, budget: u64) -> Self {
        Self::with_window(src, len, budget, CACHE_CHUNK)
    }

    /// Reads from an uncached source, sizing each fetch to what the parse
    /// is using.
    pub fn new_sparse(src: &'a mut dyn ByteSource, len: u64, budget: u64) -> Self {
        Self::with_window(src, len, budget, SPARSE_WINDOW)
    }

    fn with_window(src: &'a mut dyn ByteSource, len: u64, budget: u64, window: usize) -> Self {
        Self {
            src,
            len,
            pos: 0,
            cache: Vec::new(),
            cache_start: u64::MAX,
            window,
            min_window: window,
            budget,
            serve_budget: budget.saturating_mul(8),
        }
    }

    /// Budget left, so a second reader over the same parse continues the
    /// bound rather than restarting it.
    pub fn remaining_budget(&self) -> u64 {
        self.budget
    }
}

impl Read for SourceReader<'_> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        if self.pos >= self.len || buf.is_empty() {
            return Ok(0);
        }
        if self.serve_budget == 0 {
            return Err(std::io::Error::other(
                "metadata parse exceeded its serve budget (non-progressing parser loop)",
            ));
        }
        let in_cache =
            self.pos >= self.cache_start && self.pos < self.cache_start + self.cache.len() as u64;
        if !in_cache {
            let contiguous = self.pos == self.cache_start.saturating_add(self.cache.len() as u64);
            self.window = if contiguous {
                self.window.saturating_mul(2).min(CACHE_CHUNK)
            } else {
                self.min_window
            };
            let want = self.window.min((self.len - self.pos) as usize);
            if (want as u64) > self.budget {
                return Err(std::io::Error::other("metadata byte budget exceeded"));
            }
            self.budget -= want as u64;
            let mut chunk = vec![0u8; want];
            let mut filled = 0usize;
            while filled < want {
                let n = self
                    .src
                    .read_at(self.pos + filled as u64, &mut chunk[filled..])
                    .map_err(std::io::Error::other)?;
                if n == 0 {
                    break;
                }
                filled += n;
            }
            chunk.truncate(filled);
            if chunk.is_empty() {
                return Ok(0);
            }
            self.cache_start = self.pos;
            self.cache = chunk;
        }
        let off = (self.pos - self.cache_start) as usize;
        let n = buf.len().min(self.cache.len() - off);
        buf[..n].copy_from_slice(&self.cache[off..off + n]);
        self.pos += n as u64;
        self.serve_budget = self.serve_budget.saturating_sub(n as u64);
        Ok(n)
    }
}

impl Seek for SourceReader<'_> {
    fn seek(&mut self, pos: SeekFrom) -> std::io::Result<u64> {
        let target = match pos {
            SeekFrom::Start(o) => o as i64,
            SeekFrom::Current(d) => self.pos as i64 + d,
            SeekFrom::End(d) => self.len as i64 + d,
        };
        if target < 0 {
            return Err(std::io::Error::other("seek before start"));
        }
        self.pos = target as u64;
        Ok(self.pos)
    }
}
