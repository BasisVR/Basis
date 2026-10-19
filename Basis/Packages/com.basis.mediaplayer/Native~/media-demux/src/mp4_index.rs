//! The `sidx` segment index (ISO/IEC 14496-12 8.16.3): where each of a
//! fragmented file's subsegments begins, how long it is, and what span of
//! media it holds. A file that carries one says in its first hundred
//! kilobytes everything an opener needs to start anywhere in it, however
//! many hours long it is.
//!
//! An index is believed only when it accounts for the whole file: every
//! reference is to media rather than to another index, and the end of the
//! box plus `first_offset` plus the sizes lands exactly on the end of the
//! media. A short or shifted index would otherwise put a seek in the
//! middle of a box, and hostile input is the normal case for a player
//! that opens arbitrary URLs.
//!
//! A file with no `sidx` often ends in an `mfra` instead (8.8.9), whose
//! `tfra` lists the fragments holding each sync sample. That builds the
//! same index, held to the same test.

/// One subsegment: a `moof` and the `mdat` it indexes.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub(crate) struct IndexEntry {
    /// Absolute offset of the subsegment's first byte.
    pub offset: u64,
    pub size: u32,
    /// Start time, in the index's timescale.
    pub time: u64,
    pub duration: u32,
}

#[derive(Debug, Clone)]
pub(crate) struct SegmentIndex {
    /// The track whose presentation times the index states. A muxed file
    /// written with per-track indexes carries one of these per track.
    pub reference_id: u32,
    pub timescale: u32,
    pub entries: Vec<IndexEntry>,
    /// One past the last byte any reference covers.
    pub end: u64,
}

impl SegmentIndex {
    /// Whether the index covers the file's media to the byte. `media_end`
    /// is the end of the source, or the start of a trailing `mfra`.
    pub fn tiles(&self, media_end: u64) -> bool {
        self.end == media_end
    }

    /// Whether the first reference is the first fragment the box walk
    /// found, which pins the index to this file's layout rather than to a
    /// copy it was written for.
    pub fn starts_at(&self, first_fragment: u64) -> bool {
        self.entries
            .first()
            .is_some_and(|entry| entry.offset == first_fragment)
    }

    /// The last subsegment beginning at or before `time`, in the index's
    /// timescale; the first when `time` precedes the index.
    pub fn floor(&self, time: u64) -> usize {
        self.entries
            .partition_point(|entry| entry.time <= time)
            .saturating_sub(1)
    }

    /// Media the index spans, in its own timescale.
    pub fn span(&self) -> u64 {
        self.entries
            .last()
            .map_or(0, |last| last.time.saturating_add(u64::from(last.duration)))
            .saturating_sub(self.entries.first().map_or(0, |first| first.time))
    }
}

/// Parse a `sidx` body (the box contents past its eight-byte header)
/// whose last byte is at `after_box - 1`.
pub(crate) fn parse(body: &[u8], after_box: u64) -> Result<SegmentIndex, &'static str> {
    let Some(&version) = body.first() else {
        return Err("sidx has no body");
    };
    // The version sizes `earliest_presentation_time` and `first_offset`;
    // the reference table follows them.
    let head = match version {
        0 => 20usize,
        1 => 28usize,
        _ => return Err("sidx version is not 0 or 1"),
    };
    if body.len() < head + 4 {
        return Err("sidx shorter than its reference count");
    }
    let reference_id = be32(&body[4..8]);
    let timescale = be32(&body[8..12]);
    if timescale == 0 {
        return Err("sidx timescale is zero");
    }
    let (earliest, first_offset) = if head == 20 {
        (
            u64::from(be32(&body[12..16])),
            u64::from(be32(&body[16..20])),
        )
    } else {
        (be64(&body[12..20]), be64(&body[20..28]))
    };

    let count = usize::from(u16::from_be_bytes([body[head + 2], body[head + 3]]));
    if count == 0 {
        return Err("sidx references nothing");
    }
    let table = &body[head + 4..];
    if table.len() < count * 12 {
        return Err("sidx reference count overruns the box");
    }

    let mut offset = after_box
        .checked_add(first_offset)
        .ok_or("sidx first offset runs past the file")?;
    let mut time = earliest;
    let mut entries = Vec::with_capacity(count);
    for reference in table.as_chunks::<12>().0.iter().take(count) {
        let word = be32(&reference[0..4]);
        if word >> 31 == 1 {
            return Err("sidx references another index");
        }
        let size = word & 0x7FFF_FFFF;
        if size == 0 {
            return Err("sidx reference is empty");
        }
        let duration = be32(&reference[4..8]);
        entries.push(IndexEntry {
            offset,
            size,
            time,
            duration,
        });
        offset = offset
            .checked_add(u64::from(size))
            .ok_or("sidx sizes run past the file")?;
        time = time
            .checked_add(u64::from(duration))
            .ok_or("sidx durations run past the end of time")?;
    }

    Ok(SegmentIndex {
        reference_id,
        timescale,
        entries,
        end: offset,
    })
}

/// One track's `tfra` (8.8.10): the time of each sync sample it lists and
/// the offset of the movie fragment that holds it.
pub(crate) struct RandomAccess {
    pub track_id: u32,
    pub points: Vec<(u64, u64)>,
}

/// Parse an `mfra` body (the box contents past its eight-byte header) into
/// the `tfra` it holds, one per track.
pub(crate) fn parse_mfra(body: &[u8]) -> Result<Vec<RandomAccess>, &'static str> {
    let mut tables = Vec::new();
    let mut rest = body;
    while rest.len() >= 8 {
        let size = be32(&rest[0..4]) as usize;
        if size < 8 || size > rest.len() {
            return Err("mfra child box overruns the mfra");
        }
        if &rest[4..8] == b"tfra" {
            tables.push(parse_tfra(&rest[8..size])?);
        }
        rest = &rest[size..];
    }
    Ok(tables)
}

fn parse_tfra(body: &[u8]) -> Result<RandomAccess, &'static str> {
    if body.len() < 16 {
        return Err("tfra shorter than its header");
    }
    let wide = match body[0] {
        0 => 4usize,
        1 => 8usize,
        _ => return Err("tfra version is not 0 or 1"),
    };
    let track_id = be32(&body[4..8]);
    let sizes = be32(&body[8..12]);
    // The traf, trun and sample numbers that follow each time and offset
    // are one to four bytes each, as the low six bits say.
    let numbers = (((sizes >> 4) & 3) + ((sizes >> 2) & 3) + (sizes & 3) + 3) as usize;
    let count = be32(&body[12..16]) as usize;
    let stride = 2 * wide + numbers;
    let table = &body[16..];
    if count
        .checked_mul(stride)
        .is_none_or(|need| need > table.len())
    {
        return Err("tfra entry count overruns the box");
    }
    let read = |bytes: &[u8]| {
        if wide == 8 {
            be64(bytes)
        } else {
            u64::from(be32(bytes))
        }
    };
    let points = table
        .chunks_exact(stride)
        .take(count)
        .map(|entry| (read(&entry[..wide]), read(&entry[wide..2 * wide])))
        .collect();
    Ok(RandomAccess { track_id, points })
}

/// A segment index from one track's random-access points: a subsegment
/// from each fragment the points name to the next, the last running to
/// `media_end`. The last subsegment's duration is left at zero, because a
/// `tfra` does not say where the media ends; the opener reads that out of
/// the last fragment itself.
pub(crate) fn from_random_access(
    table: &RandomAccess,
    timescale: u32,
    media_end: u64,
) -> Result<SegmentIndex, &'static str> {
    if timescale == 0 {
        return Err("tfra track has no timescale");
    }
    // A fragment holding several sync samples is listed once per sample;
    // its first is where it starts. The times are presentation times, so
    // with reordered frames one can sit a frame before the last: it is
    // held at the last, since a seek steps to its landing from wherever
    // the index puts it. Fragments going backwards is another file.
    let mut starts: Vec<(u64, u64)> = Vec::with_capacity(table.points.len());
    for &(time, offset) in &table.points {
        match starts.last() {
            Some(&(_, last)) if offset == last => {}
            Some(&(_, last)) if offset < last => return Err("tfra fragments go backwards"),
            Some(&(last_time, _)) => starts.push((time.max(last_time), offset)),
            None => starts.push((time, offset)),
        }
    }
    if starts.is_empty() {
        return Err("tfra lists no fragment");
    }

    let mut entries = Vec::with_capacity(starts.len());
    for (i, &(time, offset)) in starts.iter().enumerate() {
        let (next_time, next_offset) = starts.get(i + 1).copied().unwrap_or((time, media_end));
        if next_offset <= offset {
            return Err("a tfra fragment lies past the media");
        }
        let size = u32::try_from(next_offset - offset).map_err(|_| "tfra fragment above 4 GiB")?;
        let duration =
            u32::try_from(next_time - time).map_err(|_| "tfra fragment too long to index")?;
        entries.push(IndexEntry {
            offset,
            size,
            time,
            duration,
        });
    }
    Ok(SegmentIndex {
        reference_id: table.track_id,
        timescale,
        entries,
        end: media_end,
    })
}

fn be32(bytes: &[u8]) -> u32 {
    u32::from_be_bytes(bytes.try_into().expect("four bytes"))
}

fn be64(bytes: &[u8]) -> u64 {
    u64::from_be_bytes(bytes.try_into().expect("eight bytes"))
}

#[cfg(test)]
mod tests {
    use super::*;

    /// A `sidx` body with `references` as `(size, duration)` pairs, every
    /// reference to media and starting with a SAP.
    fn body(version: u8, first_offset: u64, references: &[(u32, u32)]) -> Vec<u8> {
        let mut out = vec![version, 0, 0, 0];
        out.extend_from_slice(&1u32.to_be_bytes()); // reference_ID
        out.extend_from_slice(&1000u32.to_be_bytes()); // timescale
        if version == 0 {
            out.extend_from_slice(&0u32.to_be_bytes()); // earliest pts
            out.extend_from_slice(&(first_offset as u32).to_be_bytes());
        } else {
            out.extend_from_slice(&0u64.to_be_bytes());
            out.extend_from_slice(&first_offset.to_be_bytes());
        }
        out.extend_from_slice(&0u16.to_be_bytes()); // reserved
        out.extend_from_slice(&(references.len() as u16).to_be_bytes());
        for (size, duration) in references {
            out.extend_from_slice(&size.to_be_bytes());
            out.extend_from_slice(&duration.to_be_bytes());
            out.extend_from_slice(&0x9000_0000u32.to_be_bytes()); // SAP type 1
        }
        out
    }

    #[test]
    fn both_versions_describe_the_same_segments() {
        for version in [0u8, 1u8] {
            let index = parse(&body(version, 0, &[(100, 10), (200, 20)]), 1000)
                .expect("a well-formed index parses");
            assert_eq!(index.reference_id, 1);
            assert_eq!(index.timescale, 1000);
            assert_eq!(
                index.entries,
                vec![
                    IndexEntry {
                        offset: 1000,
                        size: 100,
                        time: 0,
                        duration: 10
                    },
                    IndexEntry {
                        offset: 1100,
                        size: 200,
                        time: 10,
                        duration: 20
                    },
                ],
                "version {version}"
            );
            assert_eq!(index.end, 1300);
            assert_eq!(index.span(), 30);
        }
    }

    /// ffmpeg's `+global_sidx` gives the first of two indexes an offset
    /// that steps over the second, so a non-zero one is ordinary.
    #[test]
    fn a_first_offset_steps_over_what_follows_the_box() {
        let index = parse(&body(0, 40, &[(100, 10)]), 1000).expect("parses");
        assert_eq!(index.entries[0].offset, 1040);
        assert!(index.tiles(1140));
        assert!(index.starts_at(1040));
        assert!(!index.starts_at(1000));
    }

    #[test]
    fn an_index_short_of_the_media_is_not_trusted() {
        let index = parse(&body(0, 0, &[(100, 10), (200, 20)]), 1000).expect("parses");
        assert!(index.tiles(1300));
        assert!(!index.tiles(1301), "a fragment past the index");
        assert!(!index.tiles(1299));
    }

    #[test]
    fn the_floor_is_the_subsegment_holding_the_time() {
        let index = parse(&body(0, 0, &[(10, 100), (10, 100), (10, 100)]), 0).expect("parses");
        assert_eq!(index.floor(0), 0);
        assert_eq!(index.floor(99), 0);
        assert_eq!(index.floor(100), 1);
        assert_eq!(index.floor(250), 2);
        assert_eq!(index.floor(u64::MAX), 2);
    }

    #[test]
    fn hostile_indexes_are_refused() {
        assert!(parse(&[], 0).is_err(), "no body at all");

        let short = &body(0, 0, &[(100, 10)])[..8];
        assert!(parse(short, 0).is_err(), "a truncated box");

        let mut version = body(0, 0, &[(100, 10)]);
        version[0] = 2;
        assert!(parse(&version, 0).is_err(), "an unknown version");

        let mut timescale = body(0, 0, &[(100, 10)]);
        timescale[8..12].copy_from_slice(&0u32.to_be_bytes());
        assert!(parse(&timescale, 0).is_err(), "a zero timescale");

        let mut count = body(0, 0, &[(100, 10)]);
        count[22..24].copy_from_slice(&64u16.to_be_bytes());
        assert!(parse(&count, 0).is_err(), "a count past the box");

        let mut none = body(0, 0, &[(100, 10)]);
        none[22..24].copy_from_slice(&0u16.to_be_bytes());
        assert!(parse(&none, 0).is_err(), "no references at all");

        let mut hierarchical = body(0, 0, &[(100, 10)]);
        hierarchical[24..28].copy_from_slice(&0x8000_0064u32.to_be_bytes());
        assert!(parse(&hierarchical, 0).is_err(), "a reference to an index");

        let empty = body(0, 0, &[(0, 10)]);
        assert!(parse(&empty, 0).is_err(), "a zero-length subsegment");

        let huge = body(1, u64::MAX, &[(100, 10)]);
        assert!(parse(&huge, 1000).is_err(), "a first offset that wraps");

        let wrapping = body(0, 0, &[(0x7FFF_FFFF, 10); 3]);
        assert!(
            parse(&wrapping, u64::MAX - 1).is_err(),
            "sizes that wrap the file"
        );
    }

    /// A version 1 `tfra` for `track`, with one-byte traf, trun and sample
    /// numbers after each time and offset.
    fn tfra(track: u32, points: &[(u64, u64)]) -> Vec<u8> {
        let mut b = vec![1, 0, 0, 0];
        b.extend_from_slice(&track.to_be_bytes());
        b.extend_from_slice(&0u32.to_be_bytes());
        b.extend_from_slice(&(points.len() as u32).to_be_bytes());
        for (time, offset) in points {
            b.extend_from_slice(&time.to_be_bytes());
            b.extend_from_slice(&offset.to_be_bytes());
            b.extend_from_slice(&[1, 1, 1]);
        }
        let mut boxed = ((b.len() + 8) as u32).to_be_bytes().to_vec();
        boxed.extend_from_slice(b"tfra");
        boxed.extend_from_slice(&b);
        boxed
    }

    #[test]
    fn a_tfra_indexes_its_fragments_to_the_media_end() {
        let body = [
            tfra(2, &[(0, 100)]),
            tfra(1, &[(0, 100), (0, 100), (90, 600), (80, 900)]),
        ]
        .concat();
        let tables = parse_mfra(&body).expect("parses");
        assert_eq!(tables.len(), 2);
        let video = tables.iter().find(|t| t.track_id == 1).expect("track 1");
        let index = from_random_access(video, 30, 1000).expect("indexes");
        let entries: Vec<_> = index
            .entries
            .iter()
            .map(|e| (e.offset, e.size, e.time, e.duration))
            .collect();
        // One entry per fragment; a time a frame early is held at the last.
        assert_eq!(
            entries,
            vec![(100, 500, 0, 90), (600, 300, 90, 0), (900, 100, 90, 0)]
        );
        assert!(index.starts_at(100) && index.tiles(1000));
    }

    #[test]
    fn hostile_tfras_are_refused() {
        let backwards = parse_mfra(&tfra(1, &[(0, 600), (10, 100)])).expect("parses");
        assert!(
            from_random_access(&backwards[0], 30, 1000).is_err(),
            "offsets going back"
        );

        let past = parse_mfra(&tfra(1, &[(0, 100), (10, 1000)])).expect("parses");
        assert!(
            from_random_access(&past[0], 30, 1000).is_err(),
            "a fragment past the media"
        );

        let huge = parse_mfra(&tfra(1, &[(0, 0), (10, 1 << 33)])).expect("parses");
        assert!(
            from_random_access(&huge[0], 30, (1 << 33) + 10).is_err(),
            "a 4 GiB fragment"
        );

        let empty = parse_mfra(&tfra(1, &[])).expect("parses");
        assert!(
            from_random_access(&empty[0], 30, 1000).is_err(),
            "no fragments"
        );

        let mut overcount = tfra(1, &[(0, 100)]);
        overcount[20..24].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(parse_mfra(&overcount).is_err(), "a count past the box");

        let mut overrun = tfra(1, &[(0, 100)]);
        overrun[0..4].copy_from_slice(&u32::MAX.to_be_bytes());
        assert!(parse_mfra(&overrun).is_err(), "a child past the mfra");

        let untimed = parse_mfra(&tfra(1, &[(0, 100)])).expect("parses");
        assert!(
            from_random_access(&untimed[0], 0, 1000).is_err(),
            "no timescale"
        );
    }
}
