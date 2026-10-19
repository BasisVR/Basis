//! Fragmented MP4 from a source with no length, such as a live stream
//! served over HTTP as one response: `moov`, then `moof`/`mdat` pairs for
//! as long as the stream runs. The source only moves forward, and each box
//! is read once, in order. `moov` is held and parsed as a file's is; after
//! it, each fragment's header is read and the media data following it held
//! until its samples have been served. Whatever sits between fragments
//! (`styp`, a per-fragment `sidx`, `prft`, `emsg`) is read past.

use std::collections::BTreeMap;
use std::io::{Cursor, Seek, SeekFrom};
use std::panic::{AssertUnwindSafe, catch_unwind};

use media_clock::{Generation, MediaTime};
use re_mp4::{MoofBox, ReadBox as _};

use super::{
    Loaded, MAX_FRAGMENT_BYTES, MAX_FRAGMENT_SAMPLES, MAX_HELD_SAMPLES, MAX_PREFIX_BOXES,
    Mp4Demuxer, STYP, Samples, audio_configs, read_metadata, to_ref,
};
use crate::DemuxError;
use crate::demuxer::{DemuxLimits, DemuxOptions};
use crate::mp4_fragment::{Cursors, TrackDefaults};
use crate::source::{ByteSource, CachedSource, MemSource};

const MOOV: u32 = u32::from_be_bytes(*b"moov");
const MOOF: u32 = u32::from_be_bytes(*b"moof");
const MDAT: u32 = u32::from_be_bytes(*b"mdat");

/// How much one track may have queued, first sample to last, while the
/// other has nothing before reading ahead stops. A stream may declare a
/// track in `moov` and never send it; this bounds the delay that adds.
const MAX_LIVE_WAIT: MediaTime = MediaTime::from_millis(500);
/// Ceiling on one fragment's media data, which is held whole.
const MAX_LIVE_MEDIA_BYTES: u64 = 64 * 1024 * 1024;
/// Media data held for queued samples before reading ahead stops. What is
/// held peaks at this plus [`MAX_LIVE_MEDIA_BYTES`], since the fragment
/// being read when it is reached still arrives.
const MAX_LIVE_HELD_BYTES: u64 = 64 * 1024 * 1024;
/// The step a box body is read in, whether it is held or skipped.
const SKIP_CHUNK: usize = 64 * 1024;

pub(super) struct Live {
    /// Where the next top-level box starts.
    pos: u64,
    defaults: BTreeMap<u32, TrackDefaults>,
    cursors: Cursors,
    /// The stream has ended, or was cut part-way through a box.
    ended: bool,
}

/// A top-level box header as read off the stream.
struct Header {
    kind: u32,
    start: u64,
    body: u64,
    end: u64,
    raw: [u8; 16],
}

impl Header {
    fn raw(&self) -> &[u8] {
        &self.raw[..(self.body - self.start) as usize]
    }
}

/// The bound tracks' ids and edit-list shifts.
#[derive(Clone, Copy)]
struct Bound {
    video: Option<(u32, i64)>,
    audio: Option<(u32, i64)>,
}

/// One fragment's samples on the bound tracks and the data they lie in.
struct Arrived {
    loaded: Loaded,
    start: u64,
    data: Vec<u8>,
}

impl Mp4Demuxer {
    pub(super) fn open_live(
        mut src: CachedSource,
        limits: DemuxLimits,
        generation: Generation,
        options: &DemuxOptions,
    ) -> Result<Self, DemuxError> {
        // Everything up to the end of `moov`, kept at the stream's offsets
        // for the file parse to read as a file's head.
        let mut prefix = Vec::new();
        let mut moov_end = None;
        for _ in 0..MAX_PREFIX_BOXES {
            let Some(header) = read_header(src.inner_mut(), prefix.len() as u64)? else {
                break;
            };
            match header.kind {
                MOOF | STYP => {
                    return Err(DemuxError::Unsupported(
                        "a movie fragment comes before the moov that describes it",
                    ));
                }
                MDAT => {
                    return Err(DemuxError::Unsupported(
                        "progressive MP4 needs a source with a known length",
                    ));
                }
                _ => {}
            }
            if header.end > limits.max_metadata_bytes {
                return Err(DemuxError::Cap(
                    "live MP4 header above the metadata ceiling",
                ));
            }
            prefix.extend_from_slice(header.raw());
            if !read_body(src.inner_mut(), &header, &mut prefix)? {
                break;
            }
            if header.kind == MOOV {
                moov_end = Some(header.end);
                break;
            }
        }
        let moov_end = moov_end.ok_or_else(|| {
            DemuxError::Parse("mp4: the live stream has no moov ahead of its media".into())
        })?;

        let mut head = CachedSource::new(Box::new(MemSource(prefix)));
        let mut budget = limits.max_metadata_bytes;
        let mp4 = read_metadata(&mut head, moov_end, &mut budget, false)?;
        // Only a fragmented file can be read without going back: a
        // progressive one's samples are wherever its tables say.
        if mp4.moov.mvex.is_none() {
            return Err(DemuxError::Unsupported(
                "progressive MP4 needs a source with a known length",
            ));
        }
        if mp4.tracks().values().any(|t| !t.samples.is_empty()) {
            return Err(DemuxError::Unsupported(
                "a live MP4 stream whose moov holds samples of its own",
            ));
        }
        let configs = audio_configs(&mp4, &mut head, moov_end, &mut budget);

        let mut this = Self::new(src, limits, generation, Vec::new(), &mp4);
        this.live = Some(Live {
            pos: moov_end,
            defaults: crate::mp4_fragment::track_defaults(&mp4.moov),
            cursors: Cursors::new(),
            ended: false,
        });
        this.bind(&mp4, options, &configs, None)?;
        // A live stream has no length, whatever `moov` states.
        this.duration = None;
        Ok(this)
    }

    /// Read fragments until both bound tracks have a sample to choose
    /// between, one has waited long enough for the other, or a read-ahead
    /// limit is reached.
    pub(super) fn fill_live(&mut self) -> Result<(), DemuxError> {
        while self.live_wants_more() {
            let bound = Bound {
                video: self.video.as_ref().map(|v| (v.id.0, v.shift)),
                audio: self.audio.as_ref().map(|a| (a.id.0, a.shift)),
            };
            let Self {
                src, live, limits, ..
            } = self;
            let live = live.as_mut().expect("only a live stream fills this way");
            let Some(arrived) = live.next_fragment(src.inner_mut(), bound, limits)? else {
                continue;
            };
            let count = arrived.loaded.video.len() + arrived.loaded.audio.len();
            self.runs.adopt(arrived.start, arrived.data, count);
            if let Some(Samples::Held(held)) = self.video.as_mut().map(|v| &mut v.samples) {
                held.extend(arrived.loaded.video);
            }
            if let Some(Samples::Held(held)) = self.audio.as_mut().map(|a| &mut a.samples) {
                held.extend(arrived.loaded.audio);
            }
        }
        Ok(())
    }

    fn live_wants_more(&self) -> bool {
        if self.live.as_ref().is_none_or(|live| live.ended)
            || self.held_samples() >= MAX_HELD_SAMPLES
            || self.runs.held() >= MAX_LIVE_HELD_BYTES
        {
            return false;
        }
        let queues = [
            self.video.as_ref().map(|v| &v.samples),
            self.audio.as_ref().map(|a| &a.samples),
        ];
        let starved = queues.iter().flatten().any(|s| s.peek().is_none());
        let waited = queues
            .iter()
            .flatten()
            .any(|s| s.held_span() >= MAX_LIVE_WAIT);
        starved && !waited
    }
}

impl Live {
    /// Read up to and including the next fragment's media data. `None`
    /// when the stream ended first, or when the fragment held nothing on
    /// the bound tracks.
    fn next_fragment(
        &mut self,
        src: &mut dyn ByteSource,
        bound: Bound,
        limits: &DemuxLimits,
    ) -> Result<Option<Arrived>, DemuxError> {
        let ceiling = MAX_FRAGMENT_BYTES.min(limits.max_metadata_bytes);
        let mut moof: Option<MoofBox> = None;
        loop {
            let Some(header) = read_header(src, self.pos)? else {
                self.ended = true;
                return Ok(None);
            };
            match (header.kind, moof.take()) {
                (MOOF, None) => {
                    if header.end - header.start > ceiling {
                        return Err(DemuxError::Cap("movie fragment above the header ceiling"));
                    }
                    let mut bytes = header.raw().to_vec();
                    if !read_body(src, &header, &mut bytes)? {
                        self.ended = true;
                        return Ok(None);
                    }
                    moof = Some(parse_moof(bytes, &header)?);
                }
                (MOOF, Some(_)) => {
                    return Err(DemuxError::Parse(
                        "mp4: a movie fragment in the live stream has no media data".into(),
                    ));
                }
                (MDAT, Some(fragment)) => {
                    if header.end - header.body > MAX_LIVE_MEDIA_BYTES {
                        return Err(DemuxError::Cap(
                            "live fragment media data above the ceiling",
                        ));
                    }
                    let loaded = self.samples(&fragment, &header, bound, limits)?;
                    if loaded.video.is_empty() && loaded.audio.is_empty() {
                        if !skip(src, &header)? {
                            self.ended = true;
                        }
                        self.pos = header.end;
                        return Ok(None);
                    }
                    let mut data = Vec::new();
                    if !read_body(src, &header, &mut data)? {
                        self.ended = true;
                        return Ok(None);
                    }
                    self.pos = header.end;
                    return Ok(Some(Arrived {
                        loaded,
                        start: header.body,
                        data,
                    }));
                }
                // Between fragments, and media data no fragment describes.
                (_, pending) => {
                    moof = pending;
                    if !skip(src, &header)? {
                        self.ended = true;
                        return Ok(None);
                    }
                }
            }
            self.pos = header.end;
        }
    }

    /// The fragment's samples on the bound tracks, each of which must lie
    /// in the media data that follows it: nothing earlier can be read
    /// again.
    fn samples(
        &mut self,
        moof: &MoofBox,
        mdat: &Header,
        bound: Bound,
        limits: &DemuxLimits,
    ) -> Result<Loaded, DemuxError> {
        let mut budget = MAX_FRAGMENT_SAMPLES;
        let built =
            crate::mp4_fragment::build(moof, &self.defaults, &mut self.cursors, &mut budget)
                .map_err(|why| DemuxError::Parse(format!("mp4: {why}")))?;
        let mut loaded = Loaded::default();
        for (track_id, samples) in built {
            let (out, shift) = match (bound.video, bound.audio) {
                (Some((id, shift)), _) if id == track_id => (&mut loaded.video, shift),
                (_, Some((id, shift))) if id == track_id => (&mut loaded.audio, shift),
                _ => continue,
            };
            let timescale = self
                .defaults
                .get(&track_id)
                .map_or(1, |d| d.timescale.max(1));
            for sample in samples {
                let inside = sample.offset >= mdat.body
                    && sample
                        .offset
                        .checked_add(u64::from(sample.size))
                        .is_some_and(|end| end <= mdat.end);
                if !inside {
                    return Err(DemuxError::Parse(
                        "mp4: a live fragment's samples lie outside the media data after it".into(),
                    ));
                }
                out.push(to_ref(sample, timescale, shift, limits)?);
            }
        }
        Ok(loaded)
    }
}

/// The box header at `at`, or `None` where the stream ends first.
fn read_header(src: &mut dyn ByteSource, at: u64) -> Result<Option<Header>, DemuxError> {
    let mut raw = [0u8; 16];
    if !read_fully(src, at, &mut raw[..8])? {
        return Ok(None);
    }
    let kind = u32::from_be_bytes([raw[4], raw[5], raw[6], raw[7]]);
    let (size, header_len) = match u32::from_be_bytes([raw[0], raw[1], raw[2], raw[3]]) {
        0 => {
            return Err(DemuxError::Parse(
                "mp4: a box in the live stream states no size".into(),
            ));
        }
        1 => {
            if !read_fully(src, at + 8, &mut raw[8..16])? {
                return Ok(None);
            }
            let size = u64::from_be_bytes(raw[8..16].try_into().expect("eight bytes"));
            (size, 16)
        }
        size => (u64::from(size), 8),
    };
    if size < header_len {
        return Err(DemuxError::Parse(
            "mp4: a box in the live stream is shorter than its header".into(),
        ));
    }
    let end = at.checked_add(size).ok_or_else(|| {
        DemuxError::Parse("mp4: a box in the live stream states a size past any end".into())
    })?;
    Ok(Some(Header {
        kind,
        start: at,
        body: at + header_len,
        end,
        raw,
    }))
}

/// Parse a movie fragment held whole, header included, placing it where
/// the stream has it so its data offsets resolve.
fn parse_moof(bytes: Vec<u8>, header: &Header) -> Result<MoofBox, DemuxError> {
    let header_len = header.body - header.start;
    // As the box parser states sizes: a 64-bit header's is eight short.
    let size = header.end - header.start - (header_len - 8);
    let mut cursor = Cursor::new(bytes);
    cursor
        .seek(SeekFrom::Start(header_len))
        .map_err(DemuxError::Io)?;
    let mut moof = match catch_unwind(AssertUnwindSafe(|| MoofBox::read_box(&mut cursor, size))) {
        Ok(Ok(moof)) => moof,
        Ok(Err(e)) => return Err(DemuxError::Parse(format!("mp4: movie fragment: {e}"))),
        Err(_) => {
            return Err(DemuxError::Parse(
                "mp4 parser panicked on a movie fragment".into(),
            ));
        }
    };
    moof.start = header.start;
    Ok(moof)
}

/// Append the box's body to `into`, growing it as the bytes arrive rather
/// than by what the header claims. `false` where the stream ends first.
fn read_body(
    src: &mut dyn ByteSource,
    header: &Header,
    into: &mut Vec<u8>,
) -> Result<bool, DemuxError> {
    let mut at = header.body;
    while at < header.end {
        let chunk = (header.end - at).min(SKIP_CHUNK as u64) as usize;
        let from = into.len();
        into.resize(from + chunk, 0);
        if !read_fully(src, at, &mut into[from..])? {
            return Ok(false);
        }
        at += chunk as u64;
    }
    Ok(true)
}

/// Read past the box's body. `false` where the stream ends first.
fn skip(src: &mut dyn ByteSource, header: &Header) -> Result<bool, DemuxError> {
    let mut scratch = vec![0u8; SKIP_CHUNK];
    let mut at = header.body;
    while at < header.end {
        let chunk = (header.end - at).min(SKIP_CHUNK as u64) as usize;
        if !read_fully(src, at, &mut scratch[..chunk])? {
            return Ok(false);
        }
        at += chunk as u64;
    }
    Ok(true)
}

/// Fill `buf` from `at`. `false` where the stream ends first.
fn read_fully(src: &mut dyn ByteSource, at: u64, buf: &mut [u8]) -> Result<bool, DemuxError> {
    let mut filled = 0usize;
    while filled < buf.len() {
        let n = src
            .read_at(at + filled as u64, &mut buf[filled..])
            .map_err(DemuxError::Source)?;
        if n == 0 {
            return Ok(false);
        }
        filled += n;
    }
    Ok(true)
}
