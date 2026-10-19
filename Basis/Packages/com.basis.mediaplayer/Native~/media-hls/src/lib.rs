#![forbid(unsafe_code)]

//! HLS: playlist-driven segment chaining over the TS and fMP4
//! demuxers. `m3u8-rs` parses playlist bytes; the scheduling is ours:
//! variant choice, the live window cursor, refresh cadence, join point,
//! discontinuity splices and seek-to-segment.
//!
//! TS segments feed one continuous `TsDemuxer` through a chaining source
//! (segments may legally continue PES/GOP state across boundaries), which
//! rebuilds only across stated discontinuities. fMP4 segments parse
//! per-segment as `init + segment`; `tfdt` keeps their timestamps
//! absolute, so no cross-segment correction is needed.
//!
//! Low-Latency HLS (a live playlist listing parts and offering blocking
//! reloads) joins `PART-HOLD-BACK` behind the end on an independent part.
//! Complete segments are read whole; parts only from the segment still
//! being written, or to finish one joined part way through. Reloads carry
//! `_HLS_msn`/`_HLS_part`, which the server answers as soon as the next
//! part exists. Parts flow through the same TS chain or per-fragment fMP4
//! parse as segments.
//!
//! A variant whose audio is a separate rendition (`EXT-X-MEDIA
//! TYPE=AUDIO` with a URI) names that rendition's playlist
//! ([`HlsDemuxer::audio_rendition`]); the engine opens it as a second
//! demuxer ([`HlsDemuxer::open_rendition`]), joined at the same point on
//! the timeline as the variant.

use std::collections::{HashMap, VecDeque};

use std::sync::{Arc, Mutex};
use std::time::Duration;

use media_clock::{Generation, MediaTime};

use media_demux::{
    ByteSource, DemuxError, DemuxLimits, Demuxer, DiscontinuityReason, EosReason, Format,
    Mp4Demuxer, SourceError, StreamEvent, TrackId, TsDemuxer, push_note,
};

/// Whole-resource fetch plus a pacing seam. `media-io` implements the
/// network version; tests drive a virtual one. `wait` and `now` keep the
/// refresh cadence schedulable without the demuxer owning a clock.
pub trait SegmentFetcher: Send {
    /// Fetch an entire resource, refusing past `cap` bytes.
    fn fetch(&mut self, url: &str, cap: u64) -> Result<Vec<u8>, SourceError>;

    /// Sleep (or advance virtual time) between live playlist refreshes.
    fn wait(&mut self, duration: Duration);

    /// Monotonic time, for judging how long a live playlist has gone
    /// without advancing, blocking reloads held by the server included.
    fn now(&self) -> Duration {
        static EPOCH: std::sync::OnceLock<std::time::Instant> = std::sync::OnceLock::new();
        EPOCH.get_or_init(std::time::Instant::now).elapsed()
    }
}

/// Parse-time caps: enforced here, not around the demuxer.
const PLAYLIST_CAP: u64 = 4 * 1024 * 1024;
const SEGMENT_CAP: u64 = 64 * 1024 * 1024;
const MAX_SEGMENTS: usize = 65_536;
const MAX_PARTS: usize = 65_536;
/// Stated durations beyond this are hostile numbers, not media: with the
/// segment-count cap this keeps every cumulative-duration fold far from
/// i64 microseconds (fuzz-found overflow).
const MAX_SEGMENT_SECONDS: f64 = 3600.0;
/// Attempts per resource before the failure propagates (live segments are
/// skipped instead, since the window moves on without them).
const RESOURCE_ATTEMPTS: u32 = 3;
/// Target durations of reloading with no window progress before the lane
/// reads as dead, whatever the reload cadence.
const STALE_TARGET_DURATIONS: u32 = 20;
/// `HOLD-BACK` defaults to three target durations (RFC 8216bis
/// §4.4.3.8); a playlist missing the `PART-HOLD-BACK` it requires gets
/// three part targets.
const HOLD_BACK_TARGETS: i64 = 3;
/// Floor on the wait between reloads of a low-latency playlist.
const MIN_PART_RELOAD: Duration = Duration::from_millis(100);

/// `#EXTM3U` leads the playlist (BOM/whitespace tolerated). The router's
/// sniff for HLS lanes.
pub fn looks_like_playlist(head: &[u8]) -> bool {
    let head = head.strip_prefix(&[0xEF, 0xBB, 0xBF]).unwrap_or(head);
    let mut idx = 0;
    while idx < head.len() && (head[idx] == b' ' || head[idx] == b'\r' || head[idx] == b'\n') {
        idx += 1;
    }
    head[idx..].starts_with(b"#EXTM3U")
}

/// A leading `c:`, which names a Windows drive and is absolute to that
/// OS with or without a separator after it. Recognised by shape rather
/// than by `Path`, which does not see it at all off Windows.
fn has_drive_prefix(rel: &str) -> bool {
    let bytes = rel.as_bytes();
    bytes.len() >= 2 && bytes[0].is_ascii_alphabetic() && bytes[1] == b':'
}

/// A URI the engine can fetch over the network. The playlist lane has
/// exactly one transport, so this is the whole allowlist.
fn is_fetchable_scheme(scheme: &str) -> bool {
    matches!(scheme, "http" | "https")
}

/// Resolve a possibly relative playlist URI against its playlist's URL
/// (or filesystem path, so local fixtures play without a server).
///
/// A playlist may not change what kind of thing its resources are. One
/// served over the network resolves everything through `Url::join`,
/// which handles an absolute URI correctly on its own, and the result
/// must be fetchable or it is refused: otherwise a `c:` or `file:` URI
/// resolves to something the fetcher would open as a path. A playlist on
/// disk may still name network resources (the address gate vets those),
/// but anything else must sit beside it.
fn resolve(base: &str, rel: &str) -> Result<String, DemuxError> {
    match url::Url::parse(base) {
        // A single-letter "scheme" is a Windows drive letter, not a URL.
        Ok(base_url) if base_url.scheme().len() > 1 && !base_url.cannot_be_a_base() => {
            let joined = base_url
                .join(rel)
                .map_err(|e| DemuxError::Parse(format!("bad segment URI {rel:?}: {e}")))?;
            if !is_fetchable_scheme(joined.scheme()) {
                return Err(DemuxError::Parse(format!(
                    "playlist URI resolves to an unfetchable {} scheme: {rel:?}",
                    joined.scheme()
                )));
            }
            Ok(joined.to_string())
        }
        _ => {
            // An absolute URI from a playlist on disk is fine when it is
            // one the fetcher can actually go and get, and the gate vets
            // it there. Any other scheme would land on the fetcher's
            // filesystem arm, which is the shape this refuses.
            if let Ok(absolute) = url::Url::parse(rel) {
                if is_fetchable_scheme(absolute.scheme()) {
                    return Ok(absolute.to_string());
                }
                return Err(DemuxError::Parse(format!(
                    "playlist URI outside the playlist's directory: {rel:?}"
                )));
            }
            // A playlist on disk names its resources beside itself, so
            // the only URI shape that resolves is a plain relative one.
            // Path::join drops the base entirely for an absolute rel and
            // walks out of it for a `..`, so screen the URI here rather
            // than build a path and rely on the fetcher to disown it.
            //
            // The Windows shapes are screened as text, before the
            // components are walked, because `Path` only parses the
            // syntax of the platform it was compiled for: a Unix build
            // reads `C:\dir\clip.ts` as one ordinary filename and would
            // accept a playlist written to attack a Windows client. The
            // same rule applies on every host. A drive-shaped string
            // already parses as a URL with a one-letter scheme and is
            // refused above; the drive check is defence in depth for that
            // arm.
            if rel.contains('\\') || has_drive_prefix(rel) {
                return Err(DemuxError::Parse(format!(
                    "playlist URI outside the playlist's directory: {rel:?}"
                )));
            }
            let candidate = std::path::Path::new(rel);
            if candidate.components().any(|c| {
                !matches!(
                    c,
                    std::path::Component::Normal(_) | std::path::Component::CurDir
                )
            }) {
                return Err(DemuxError::Parse(format!(
                    "playlist URI outside the playlist's directory: {rel:?}"
                )));
            }
            // A bare filename has no parent, and joining onto the empty
            // path yields a bare name back. The fetcher is confined to a
            // directory and judges what it is handed against it, so both
            // sides have to spell the current directory the same way or
            // every segment of such a playlist resolves to something the
            // fetcher then disowns.
            let path = std::path::Path::new(base);
            let parent = match path.parent() {
                Some(parent) if !parent.as_os_str().is_empty() => parent,
                _ => std::path::Path::new("."),
            };
            Ok(parent.join(rel).to_string_lossy().into_owned())
        }
    }
}

#[derive(Debug, Clone)]
pub struct PlaylistSegment {
    pub url: String,
    pub duration: MediaTime,
    pub discontinuity: bool,
    pub map_url: Option<String>,
    /// Its partial segments, when the playlist's parts are in use.
    pub parts: Vec<PlaylistPart>,
    /// `EXT-X-PROGRAM-DATE-TIME`, milliseconds since the Unix epoch.
    pub program_time_ms: Option<i64>,
}

/// One Low-Latency HLS partial segment (`EXT-X-PART`).
#[derive(Debug, Clone)]
pub struct PlaylistPart {
    pub url: String,
    pub duration: MediaTime,
    /// `INDEPENDENT=YES`, or the in-progress segment's first part: a
    /// decoder can start here.
    pub independent: bool,
    /// `GAP=YES`: the server has no media for it.
    pub gap: bool,
}

/// The segment still being written at the end of a low-latency
/// playlist: its parts so far, and no URI of its own yet.
#[derive(Debug, Clone, Default)]
pub struct InProgressSegment {
    pub parts: Vec<PlaylistPart>,
    pub discontinuity: bool,
    pub map_url: Option<String>,
}

/// Low-Latency HLS as a live playlist declares it: parts listed, and
/// reloads that can block until the next part exists.
#[derive(Debug, Clone)]
pub struct LowLatency {
    pub part_target: MediaTime,
    /// How far behind the end a client joins when riding parts.
    pub part_hold_back: MediaTime,
    pub in_progress: InProgressSegment,
}

/// One parsed media-playlist window.
#[derive(Debug, Clone)]
pub struct PlaylistWindow {
    pub target_duration: MediaTime,
    pub first_sequence: u64,
    pub segments: Vec<PlaylistSegment>,
    pub ended: bool,
    /// `CAN-BLOCK-RELOAD=YES`: a reload may ask the server to hold it
    /// until the next media exists.
    pub can_block_reload: bool,
    /// `HOLD-BACK`: how far behind the end a client joins on whole
    /// segments.
    pub hold_back: Option<MediaTime>,
    /// `EXT-X-START` `TIME-OFFSET`; negative counts back from the end.
    pub start_offset: Option<MediaTime>,
    pub low_latency: Option<LowLatency>,
    /// Why a playlist that lists parts is played on whole segments.
    pub low_latency_declined: Option<&'static str>,
}

impl PlaylistWindow {
    /// The in-progress segment's sequence number: one past the last
    /// complete segment.
    fn in_progress_sequence(&self) -> u64 {
        self.first_sequence + self.segments.len() as u64
    }

    /// How far the playlist reaches, for telling a refresh that brought
    /// new media from one that did not.
    fn reach(&self) -> (u64, usize) {
        let parts = self
            .low_latency
            .as_ref()
            .map_or(0, |ll| ll.in_progress.parts.len());
        (self.in_progress_sequence(), parts)
    }
}

/// Seconds as a playlist states them, refused past the per-segment cap.
fn stated_seconds(value: &str, what: &'static str) -> Result<MediaTime, DemuxError> {
    let seconds: f64 = value
        .trim()
        .parse()
        .map_err(|_| DemuxError::Parse(format!("bad {what}: {value:?}")))?;
    if !seconds.is_finite() || !(0.0..=MAX_SEGMENT_SECONDS).contains(&seconds) {
        return Err(DemuxError::Cap(what));
    }
    Ok(MediaTime::from_micros((seconds * 1e6) as i64))
}

/// `KEY=value` pairs of an attribute list, quotes removed. A quoted
/// value may hold commas.
fn attributes(list: &str) -> Vec<(&str, &str)> {
    let mut out = Vec::new();
    let mut rest = list;
    while !rest.is_empty() {
        let Some(eq) = rest.find('=') else { break };
        let key = rest[..eq].trim();
        let after = &rest[eq + 1..];
        let (value, next) = if let Some(quoted) = after.strip_prefix('"') {
            match quoted.find('"') {
                Some(end) => (&quoted[..end], &quoted[end + 1..]),
                None => (quoted, ""),
            }
        } else {
            match after.find(',') {
                Some(end) => (&after[..end], &after[end..]),
                None => (after, ""),
            }
        };
        out.push((key, value));
        rest = next.trim_start_matches([',', ' ']);
    }
    out
}

/// The Low-Latency HLS tags, which `m3u8-rs` does not model: it files
/// them as unknown and drops whatever follows the last segment URI, which
/// is where the in-progress segment's parts sit. Read line by line, with
/// parts grouped by the segment URI that closes them.
#[derive(Default)]
struct LowLatencyTags {
    can_block_reload: bool,
    hold_back: Option<MediaTime>,
    part_hold_back: Option<MediaTime>,
    part_target: Option<MediaTime>,
    /// One entry per segment URI, then the in-progress segment's.
    parts: Vec<Vec<PlaylistPart>>,
    in_progress_discontinuity: bool,
    in_progress_map: Option<String>,
    byte_range_parts: bool,
    /// A part or map tag that could not be read; the playlist plays on
    /// whole segments rather than failing.
    malformed: bool,
}

fn scan_low_latency(text: &str, base_url: &str) -> LowLatencyTags {
    let mut tags = LowLatencyTags::default();
    let mut current = Vec::new();
    let mut total_parts = 0usize;
    let mut discontinuity = false;
    let mut map: Option<String> = None;
    for line in text.lines() {
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        if !line.starts_with('#') {
            tags.parts.push(std::mem::take(&mut current));
            discontinuity = false;
            continue;
        }
        if let Some(list) = line.strip_prefix("#EXT-X-SERVER-CONTROL:") {
            for (key, value) in attributes(list) {
                match key {
                    "CAN-BLOCK-RELOAD" => tags.can_block_reload = value == "YES",
                    "HOLD-BACK" => tags.hold_back = stated_seconds(value, "HOLD-BACK").ok(),
                    "PART-HOLD-BACK" => {
                        tags.part_hold_back = stated_seconds(value, "PART-HOLD-BACK").ok();
                    }
                    _ => {}
                }
            }
        } else if let Some(list) = line.strip_prefix("#EXT-X-PART-INF:") {
            for (key, value) in attributes(list) {
                if key == "PART-TARGET" {
                    tags.part_target = stated_seconds(value, "PART-TARGET").ok();
                }
            }
        } else if let Some(list) = line.strip_prefix("#EXT-X-PART:") {
            total_parts += 1;
            if total_parts > MAX_PARTS {
                tags.malformed = true;
                continue;
            }
            let mut uri = None;
            let mut duration = None;
            let mut independent = false;
            let mut gap = false;
            for (key, value) in attributes(list) {
                match key {
                    "URI" => uri = Some(value),
                    "DURATION" => duration = stated_seconds(value, "part duration").ok(),
                    "INDEPENDENT" => independent = value == "YES",
                    "GAP" => gap = value == "YES",
                    "BYTERANGE" => tags.byte_range_parts = true,
                    _ => {}
                }
            }
            let (Some(Ok(url)), Some(duration)) = (uri.map(|uri| resolve(base_url, uri)), duration)
            else {
                tags.malformed = true;
                continue;
            };
            current.push(PlaylistPart {
                url,
                duration,
                independent,
                gap,
            });
        } else if line == "#EXT-X-DISCONTINUITY" {
            discontinuity = true;
        } else if let Some(list) = line.strip_prefix("#EXT-X-MAP:") {
            for (key, value) in attributes(list) {
                match key {
                    // The in-progress segment's map comes only from here.
                    "URI" => match resolve(base_url, value) {
                        Ok(url) => map = Some(url),
                        Err(_) => tags.malformed = true,
                    },
                    "BYTERANGE" => tags.byte_range_parts = true,
                    _ => {}
                }
            }
        }
    }
    // The in-progress segment's parts are listed from its first, which
    // starts where the segment does. A complete segment may have lost its
    // leading parts; there only a stated INDEPENDENT counts.
    if let Some(first) = current.first_mut() {
        first.independent = true;
    }
    tags.parts.push(current);
    tags.in_progress_discontinuity = discontinuity;
    tags.in_progress_map = map;
    tags
}

/// One variant of a master playlist.
#[derive(Debug, Clone)]
pub struct Variant {
    pub bandwidth: u64,
    pub url: String,
    /// The playlist of its audio rendition: of the `EXT-X-MEDIA
    /// TYPE=AUDIO` entries in its `AUDIO` group, the `DEFAULT=YES` one,
    /// else the first `AUTOSELECT=YES`, else the first. `None` when that
    /// entry has no URI, since its audio is muxed in the variant (RFC 8216
    /// §4.3.4.1); `Err` when its URI was refused, with the reason.
    pub audio_url: Option<Result<String, String>>,
}

/// A parsed playlist of either kind; also the fuzz target's surface.
pub enum ParsedPlaylist {
    /// Its variants, best candidate first.
    Master(Vec<Variant>),
    Media(Box<PlaylistWindow>),
}

/// Parse playlist bytes, refusing the features we do not carry (encrypted
/// media, byte-range segments, I-frame playlists) as typed errors.
pub fn parse_playlist(bytes: &[u8], base_url: &str) -> Result<ParsedPlaylist, DemuxError> {
    let playlist = m3u8_rs::parse_playlist_res(bytes)
        .map_err(|_| DemuxError::Parse("not a valid m3u8 playlist".into()))?;
    match playlist {
        m3u8_rs::Playlist::MasterPlaylist(master) => {
            let mut variants: Vec<Variant> = Vec::new();
            for variant in &master.variants {
                if variant.is_i_frame {
                    continue;
                }
                let group: Vec<&m3u8_rs::AlternativeMedia> = master
                    .alternatives
                    .iter()
                    .filter(|media| {
                        media.media_type == m3u8_rs::AlternativeMediaType::Audio
                            && Some(&media.group_id) == variant.audio.as_ref()
                    })
                    .collect();
                let chosen = group
                    .iter()
                    .find(|media| media.default)
                    .or_else(|| group.iter().find(|media| media.autoselect))
                    .or(group.first());
                // A refused rendition costs the audio leg, not the variant.
                let audio_url = chosen
                    .and_then(|media| media.uri.as_deref())
                    .map(|uri| resolve(base_url, uri).map_err(|e| e.to_string()));
                variants.push(Variant {
                    bandwidth: variant.bandwidth,
                    url: resolve(base_url, &variant.uri)?,
                    audio_url,
                });
            }
            if variants.is_empty() {
                return Err(DemuxError::Unsupported(
                    "master playlist with no usable variant",
                ));
            }
            variants.sort_by_key(|v| std::cmp::Reverse(v.bandwidth));
            Ok(ParsedPlaylist::Master(variants))
        }
        m3u8_rs::Playlist::MediaPlaylist(media) => {
            if media.i_frames_only {
                return Err(DemuxError::Unsupported("I-frame-only playlist"));
            }
            if media.segments.len() > MAX_SEGMENTS {
                return Err(DemuxError::Cap("playlist segment count"));
            }
            let mut segments = Vec::with_capacity(media.segments.len());
            // EXT-X-MAP applies to every segment after it (RFC 8216
            // §4.3.2.5); carry it forward.
            let mut current_map: Option<String> = None;
            for segment in &media.segments {
                if segment.key.is_some() {
                    return Err(DemuxError::Unsupported("encrypted HLS (EXT-X-KEY)"));
                }
                if segment.byte_range.is_some() {
                    return Err(DemuxError::Unsupported(
                        "byte-range segments (EXT-X-BYTERANGE)",
                    ));
                }
                match &segment.map {
                    Some(map) if map.byte_range.is_some() => {
                        return Err(DemuxError::Unsupported(
                            "byte-range init segments (EXT-X-MAP BYTERANGE)",
                        ));
                    }
                    Some(map) => current_map = Some(resolve(base_url, &map.uri)?),
                    None => {}
                }
                let duration = f64::from(segment.duration);
                if !duration.is_finite() || !(0.0..=MAX_SEGMENT_SECONDS).contains(&duration) {
                    return Err(DemuxError::Cap("segment duration"));
                }
                segments.push(PlaylistSegment {
                    url: resolve(base_url, &segment.uri)?,
                    duration: MediaTime::from_micros((duration * 1e6) as i64),
                    discontinuity: segment.discontinuity,
                    map_url: current_map.clone(),
                    parts: Vec::new(),
                    program_time_ms: segment
                        .program_date_time
                        .map(|time| time.timestamp_millis()),
                });
            }

            let tags = match std::str::from_utf8(bytes) {
                Ok(text) => scan_low_latency(text, base_url),
                Err(_) => LowLatencyTags::default(),
            };
            let start_offset = media
                .start
                .as_ref()
                .map(|start| start.time_offset)
                .filter(|offset| offset.is_finite() && offset.abs() <= MAX_SEGMENT_SECONDS * 24.0)
                .map(|offset| MediaTime::from_micros((offset * 1e6) as i64));
            let mut window = PlaylistWindow {
                target_duration: MediaTime::from_secs(
                    media.target_duration.clamp(1, MAX_SEGMENT_SECONDS as u64) as i64,
                ),
                first_sequence: media.media_sequence,
                segments,
                ended: media.end_list,
                can_block_reload: tags.can_block_reload,
                hold_back: tags.hold_back,
                start_offset,
                low_latency: None,
                low_latency_declined: None,
            };

            let lists_parts = tags.parts.iter().any(|parts| !parts.is_empty());
            let declined = if !lists_parts {
                None
            } else if !tags.can_block_reload {
                Some("parts listed without CAN-BLOCK-RELOAD")
            } else if tags.part_target.is_none() {
                Some("parts listed without a readable PART-TARGET")
            } else if tags.malformed {
                Some("a low-latency tag could not be read")
            } else if tags.byte_range_parts {
                Some("byte-range parts")
            } else if tags.parts.len() != window.segments.len() + 1 {
                Some("parts could not be matched to their segments")
            } else {
                None
            };
            window.low_latency_declined = declined;
            // Parts stay on the segments once the playlist ends: a
            // segment joined part way through is finished on them.
            if let (true, None, Some(part_target)) = (lists_parts, declined, tags.part_target) {
                let mut parts = tags.parts;
                let in_progress = parts.pop().unwrap_or_default();
                for (segment, parts) in window.segments.iter_mut().zip(parts) {
                    segment.parts = parts;
                }
                window.low_latency = (!window.ended).then(|| LowLatency {
                    part_target,
                    part_hold_back: tags.part_hold_back.unwrap_or(MediaTime::from_micros(
                        part_target.as_micros() * HOLD_BACK_TARGETS,
                    )),
                    in_progress: InProgressSegment {
                        parts: in_progress,
                        discontinuity: tags.in_progress_discontinuity,
                        map_url: tags.in_progress_map,
                    },
                });
            }
            Ok(ParsedPlaylist::Media(Box::new(window)))
        }
    }
}

/// A fetched segment ready to demux.
struct FetchedSegment {
    data: Vec<u8>,
    /// A stated splice precedes this segment (EXT-X-DISCONTINUITY, a
    /// window fall-out jump, or a skipped-segment gap).
    discontinuity: DiscontinuityKind,
    map_url: Option<String>,
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum DiscontinuityKind {
    None,
    /// The playlist stated it.
    Stated,
    /// The scheduler jumped (fell out of the window, or skipped a dead
    /// segment).
    Jump,
}

/// Where the scheduler reads next.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct Cursor {
    sequence: u64,
    /// `None` reads the whole segment, `Some(n)` its part `n`.
    part: Option<usize>,
}

impl Cursor {
    fn segment(sequence: u64) -> Self {
        Self {
            sequence,
            part: None,
        }
    }
}

/// A point in the window a decoder can start from.
#[derive(Debug, Clone, Copy)]
struct JoinPoint {
    /// From the start of the window.
    at: MediaTime,
    /// Program time here, milliseconds since the Unix epoch, when the
    /// playlist states one.
    program_ms: Option<i64>,
    cursor: Cursor,
}

/// Where a live playlist joined, so that a rendition of the same
/// presentation can join at the same point on the timeline.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct JoinAnchor {
    pub sequence: u64,
    /// `None` for a whole segment.
    pub part: Option<usize>,
    /// Program time at the join, milliseconds since the Unix epoch, when
    /// the playlist states one.
    pub program_time_ms: Option<i64>,
}

/// What the cursor points at in the current window.
enum Located {
    Fetch {
        url: String,
        map_url: Option<String>,
        stated: bool,
        then: Cursor,
    },
    /// Nothing to fetch here; move on. `jump` names a skip that breaks
    /// continuity.
    Advance { then: Cursor, jump: Option<String> },
    /// Not in the window yet.
    Missing,
}

/// `resync` passes over parts a decoder cannot start from.
fn part_located(
    part: &PlaylistPart,
    index: usize,
    sequence: u64,
    discontinuity: bool,
    map_url: Option<String>,
    resync: bool,
) -> Located {
    let next = Cursor {
        sequence,
        part: Some(index + 1),
    };
    if part.gap {
        return Located::Advance {
            then: next,
            jump: Some(format!(
                "part {index} of sequence {sequence} is a gap; skipping to its next independent part"
            )),
        };
    }
    if resync && !part.independent {
        return Located::Advance {
            then: next,
            jump: None,
        };
    }
    Located::Fetch {
        url: part.url.clone(),
        map_url,
        stated: index == 0 && discontinuity,
        then: Cursor {
            sequence,
            part: Some(index + 1),
        },
    }
}

/// Where each listed part starts within its segment. A complete segment's
/// parts end where it does, which places them even when its leading parts
/// are no longer listed; an in-progress segment's parts start at its own
/// start.
fn part_starts(parts: &[PlaylistPart], complete: Option<MediaTime>) -> Vec<MediaTime> {
    let listed = parts
        .iter()
        .fold(MediaTime::ZERO, |sum, part| sum + part.duration);
    let mut at = complete.map_or(MediaTime::ZERO, |duration| {
        (duration - listed).max(MediaTime::ZERO)
    });
    parts
        .iter()
        .map(|part| {
            let start = at;
            at += part.duration;
            start
        })
        .collect()
}

/// The scheduler: playlist window cursor + refresh cadence + fetch policy.
struct Scheduler {
    fetcher: Box<dyn SegmentFetcher>,
    playlist_url: String,
    window: PlaylistWindow,
    live: bool,
    cursor: Cursor,
    pending_jump: bool,
    /// Set after a lost part: the parts after it are passed over until one
    /// a decoder can start from.
    resync: bool,
    /// Set once the server refuses a blocking reload: the lane reloads
    /// plainly from then on.
    blocking_refused: bool,
    notes: Vec<String>,
}

impl Scheduler {
    fn fetch_with_retries(&mut self, url: &str, cap: u64) -> Result<Vec<u8>, DemuxError> {
        let mut last_error = None;
        for attempt in 0..RESOURCE_ATTEMPTS {
            if attempt > 0 {
                self.fetcher.wait(Duration::from_millis(250 << attempt));
            }
            match self.fetcher.fetch(url, cap) {
                Ok(bytes) => return Ok(bytes),
                Err(e) => last_error = Some(e),
            }
        }
        Err(DemuxError::Source(
            last_error.expect("at least one attempt ran"),
        ))
    }

    /// The segment for `sequence`, if the current window still carries it.
    fn segment_at(&self, sequence: u64) -> Option<&PlaylistSegment> {
        let index = usize::try_from(sequence.checked_sub(self.window.first_sequence)?).ok()?;
        self.window.segments.get(index)
    }

    /// A complete segment is read whole unless the cursor joined it part
    /// way through; parts are read only from the segment still being
    /// written, or to finish one begun on parts.
    fn locate(&self) -> Located {
        let Cursor { sequence, part } = self.cursor;
        if let Some(segment) = self.segment_at(sequence) {
            let next = Cursor::segment(sequence + 1);
            return match part {
                None => Located::Fetch {
                    url: segment.url.clone(),
                    map_url: segment.map_url.clone(),
                    stated: segment.discontinuity,
                    then: next,
                },
                Some(index) => match segment.parts.get(index) {
                    Some(p) => part_located(
                        p,
                        index,
                        sequence,
                        segment.discontinuity,
                        segment.map_url.clone(),
                        self.resync,
                    ),
                    None if index == 0 => Located::Fetch {
                        url: segment.url.clone(),
                        map_url: segment.map_url.clone(),
                        stated: segment.discontinuity,
                        then: next,
                    },
                    None if index == segment.parts.len() => Located::Advance {
                        then: next,
                        jump: None,
                    },
                    None => Located::Advance {
                        then: next,
                        jump: Some(format!(
                            "parts of sequence {sequence} left the playlist; skipping to {}",
                            sequence + 1
                        )),
                    },
                },
            };
        }
        if let Some(ll) = &self.window.low_latency
            && sequence == self.window.in_progress_sequence()
        {
            let index = part.unwrap_or(0);
            if let Some(p) = ll.in_progress.parts.get(index) {
                return part_located(
                    p,
                    index,
                    sequence,
                    ll.in_progress.discontinuity,
                    ll.in_progress.map_url.clone(),
                    self.resync,
                );
            }
        }
        Located::Missing
    }

    /// Blocking: the next segment or part to demux, `None` at a VOD end.
    /// Live windows refresh until the cursor's media appears, the
    /// playlist ends, or the lane reads as dead.
    fn next_segment(&mut self) -> Result<Option<FetchedSegment>, DemuxError> {
        loop {
            // Fell out of the window: jump to the live join point and say so.
            if self.live && self.cursor.sequence < self.window.first_sequence {
                let jump_to = self.live_join().0.cursor;
                let from = self.cursor.sequence;
                push_note(&mut self.notes, || {
                    format!(
                        "window advanced past sequence {from}; jumping to {}",
                        jump_to.sequence
                    )
                });
                self.cursor = jump_to;
                self.pending_jump = true;
            }

            match self.locate() {
                Located::Fetch {
                    url,
                    map_url,
                    stated,
                    then,
                } => match self.fetch_with_retries(&url, SEGMENT_CAP) {
                    Ok(data) => {
                        self.cursor = then;
                        let discontinuity = if stated {
                            DiscontinuityKind::Stated
                        } else if self.pending_jump {
                            DiscontinuityKind::Jump
                        } else {
                            DiscontinuityKind::None
                        };
                        self.pending_jump = false;
                        self.resync = false;
                        return Ok(Some(FetchedSegment {
                            data,
                            discontinuity,
                            map_url,
                        }));
                    }
                    // A dead live segment or part is a gap to skip, not a
                    // session failure; VOD has no window racing away, so it
                    // fails.
                    Err(e) if self.live => {
                        let Cursor { sequence, part } = self.cursor;
                        push_note(&mut self.notes, || match part {
                            None => format!("segment {sequence} unfetchable ({e}); skipping"),
                            Some(index) => format!(
                                "part {index} of sequence {sequence} unfetchable ({e}); skipping to its next independent part"
                            ),
                        });
                        self.cursor = match part {
                            None => Cursor::segment(sequence + 1),
                            Some(index) => Cursor {
                                sequence,
                                part: Some(index + 1),
                            },
                        };
                        self.pending_jump = true;
                        self.resync = true;
                        continue;
                    }
                    Err(e) => return Err(e),
                },
                Located::Advance { then, jump } => {
                    if let Some(note) = jump {
                        push_note(&mut self.notes, || note);
                        self.pending_jump = true;
                        self.resync = true;
                    }
                    self.cursor = then;
                }
                Located::Missing if self.window.ended => return Ok(None),
                Located::Missing if self.live => self.refresh_until_progress()?,
                // A VOD playlist that neither carries the cursor nor has
                // ended is malformed.
                Located::Missing => {
                    return Err(DemuxError::Parse(
                        "playlist window ended without EXT-X-ENDLIST".into(),
                    ));
                }
            }
        }
    }

    /// Every point in the window a decoder can start from, in playlist
    /// order, and where the window ends. Program time runs on from the
    /// last `EXT-X-PROGRAM-DATE-TIME` stated at or before a point.
    fn join_points(&self) -> (Vec<JoinPoint>, MediaTime) {
        let window = &self.window;
        let low_latency = window.low_latency.as_ref();
        let mut points = Vec::new();
        let mut at = MediaTime::ZERO;
        let mut program_ms: Option<i64> = None;
        for (offset, segment) in window.segments.iter().enumerate() {
            let sequence = window.first_sequence + offset as u64;
            if segment.program_time_ms.is_some() {
                program_ms = segment.program_time_ms;
            }
            let start_ms = program_ms;
            points.push(JoinPoint {
                at,
                program_ms: start_ms,
                cursor: Cursor::segment(sequence),
            });
            if low_latency.is_some() {
                let starts = part_starts(&segment.parts, Some(segment.duration));
                for (index, (part, start)) in segment.parts.iter().zip(starts).enumerate() {
                    // A part at the segment's own start is the segment.
                    if start > MediaTime::ZERO && part.independent && !part.gap {
                        points.push(JoinPoint {
                            at: at + start,
                            program_ms: start_ms.map(|ms| ms + start.as_millis()),
                            cursor: Cursor {
                                sequence,
                                part: Some(index),
                            },
                        });
                    }
                }
            }
            at += segment.duration;
            program_ms = start_ms.map(|ms| ms + segment.duration.as_millis());
        }
        if let Some(ll) = low_latency {
            let sequence = window.in_progress_sequence();
            let start = at;
            for (index, part) in ll.in_progress.parts.iter().enumerate() {
                if part.independent && !part.gap {
                    points.push(JoinPoint {
                        at,
                        program_ms: program_ms.map(|ms| ms + (at - start).as_millis()),
                        cursor: Cursor {
                            sequence,
                            part: Some(index),
                        },
                    });
                }
                at += part.duration;
            }
        }
        (points, at)
    }

    /// Where a live session starts, and how far that is behind the end
    /// (RFC 8216bis §6.3.3): the playlist's `EXT-X-START`, else its hold
    /// back behind the end (`PART-HOLD-BACK` when riding parts, three part
    /// targets when unstated; `HOLD-BACK` otherwise, three target durations
    /// when unstated), snapped back to the nearest point a decoder can
    /// start from.
    fn live_join(&self) -> (JoinPoint, MediaTime) {
        let window = &self.window;
        let (points, end) = self.join_points();
        let target = match (window.start_offset, window.low_latency.as_ref()) {
            (Some(offset), _) if offset < MediaTime::ZERO => end + offset,
            (Some(offset), _) => offset,
            (None, Some(ll)) => end - ll.part_hold_back,
            (None, None) => {
                end - window.hold_back.unwrap_or(MediaTime::from_micros(
                    window.target_duration.as_micros() * HOLD_BACK_TARGETS,
                ))
            }
        }
        .clamp(MediaTime::ZERO, end);
        let point = points
            .iter()
            .rev()
            .find(|point| point.at <= target)
            .or(points.first())
            .copied()
            .unwrap_or(JoinPoint {
                at: MediaTime::ZERO,
                program_ms: None,
                cursor: Cursor::segment(window.in_progress_sequence()),
            });
        (point, end - point.at)
    }

    /// Where a rendition joins to meet another playlist's join on the
    /// timeline, as hls.js aligns renditions: by program time when both
    /// playlists state one (the last point at or before it, within a target
    /// duration), else by the same sequence number, on the same part when
    /// this window lists it as a start point. `None` when neither lands in
    /// this window.
    fn aligned_join(&self, anchor: JoinAnchor) -> Option<(Cursor, &'static str)> {
        let (points, _) = self.join_points();
        if let Some(target) = anchor.program_time_ms
            && let Some(point) = points
                .iter()
                .rev()
                .find(|point| point.program_ms.is_some_and(|ms| ms <= target))
            && point
                .program_ms
                .is_some_and(|ms| target - ms <= self.window.target_duration.as_millis())
        {
            return Some((point.cursor, "program time"));
        }
        let wanted = Cursor {
            sequence: anchor.sequence,
            part: anchor.part,
        };
        points
            .iter()
            .find(|point| point.cursor == wanted)
            .or_else(|| {
                points
                    .iter()
                    .find(|point| point.cursor.sequence == anchor.sequence)
            })
            .map(|point| (point.cursor, "sequence number"))
    }

    /// The playlist URL with the blocking-reload directives (RFC 8216bis
    /// §6.2.5.2) for the media the cursor waits on, when the server takes
    /// them.
    fn blocking_reload_url(&self) -> Option<String> {
        if !self.window.can_block_reload || self.blocking_refused {
            return None;
        }
        let mut url = url::Url::parse(&self.playlist_url).ok()?;
        // A drive path parses with a one-letter scheme.
        if !is_fetchable_scheme(url.scheme()) {
            return None;
        }
        {
            let mut query = url.query_pairs_mut();
            query.append_pair("_HLS_msn", &self.cursor.sequence.to_string());
            if self.window.low_latency.is_some() {
                query.append_pair("_HLS_part", &self.cursor.part.unwrap_or(0).to_string());
            }
        }
        Some(url.into())
    }

    /// The parts listed for `sequence` and, for a complete segment, its
    /// duration.
    fn parts_of(&self, sequence: u64) -> Option<(&[PlaylistPart], Option<MediaTime>)> {
        if let Some(segment) = self.segment_at(sequence) {
            return Some((&segment.parts, Some(segment.duration)));
        }
        let ll = self.window.low_latency.as_ref()?;
        (sequence == self.window.in_progress_sequence()).then_some((&ll.in_progress.parts, None))
    }

    /// Where the part cursor sits within its segment, in media time.
    fn cursor_offset(&self) -> Option<(u64, MediaTime)> {
        let index = self.cursor.part?;
        let (parts, complete) = self.parts_of(self.cursor.sequence)?;
        let starts = part_starts(parts, complete);
        let at = match starts.get(index) {
            Some(start) => *start,
            None if index == parts.len() => starts
                .last()
                .zip(parts.last())
                .map_or(MediaTime::ZERO, |(start, part)| *start + part.duration),
            None => return None,
        };
        Some((self.cursor.sequence, at))
    }

    /// After a reload, put the part cursor back on the part that starts
    /// where it was. A server removes old parts, leading ones first, and a
    /// list position does not survive that. A cursor on a complete
    /// segment it has taken nothing from reads the segment whole; one
    /// whose next part is no longer listed jumps to the next segment.
    fn remap_cursor(&mut self, offset: Option<(u64, MediaTime)>) {
        let Some((sequence, at)) = offset else { return };
        if self.cursor.sequence != sequence {
            return;
        }
        let Some((parts, complete)) = self.parts_of(sequence) else {
            return;
        };
        if at == MediaTime::ZERO && complete.is_some() {
            self.cursor.part = None;
            return;
        }
        let starts = part_starts(parts, complete);
        let tolerance = parts
            .iter()
            .map(|part| part.duration.as_micros() / 2)
            .min()
            .map_or(MediaTime::ZERO, MediaTime::from_micros);
        let end = starts
            .last()
            .zip(parts.last())
            .map(|(start, part)| *start + part.duration);
        let found = starts
            .iter()
            .position(|start| (*start - at).abs() <= tolerance)
            .or_else(|| {
                end.filter(|end| (*end - at).abs() <= tolerance)
                    .map(|_| parts.len())
            });
        match found {
            Some(index) => self.cursor.part = Some(index),
            None => {
                push_note(&mut self.notes, || {
                    format!(
                        "parts of sequence {sequence} left the playlist; skipping to {}",
                        sequence + 1
                    )
                });
                self.cursor = Cursor::segment(sequence + 1);
                self.pending_jump = true;
                self.resync = true;
            }
        }
    }

    /// Between reloads that are not held by the server: half a target
    /// duration (RFC 8216 cadence), or a part target when riding parts.
    fn reload_interval(&self) -> Duration {
        match &self.window.low_latency {
            Some(ll) => {
                Duration::from_micros(ll.part_target.as_micros().max(0) as u64).max(MIN_PART_RELOAD)
            }
            None => Duration::from_micros((self.window.target_duration.as_micros() / 2) as u64)
                .max(Duration::from_millis(500)),
        }
    }

    /// Refresh the playlist until the cursor's media is visible or the
    /// playlist ends. While the server takes blocking reloads, a reload
    /// goes out at once and the server holds it until the media exists; a
    /// plain reload, or any reload after one that brought nothing, waits
    /// the reload interval first. A server that refuses a blocking reload
    /// gets plain ones from then on. A window that stops advancing for
    /// `STALE_TARGET_DURATIONS` is a dead lane.
    fn refresh_until_progress(&mut self) -> Result<(), DemuxError> {
        let mut stalled_since: Option<Duration> = None;
        let stale_limit =
            Duration::from_micros(self.window.target_duration.as_micros().max(0) as u64)
                * STALE_TARGET_DURATIONS;
        let mut wait_first = !self.window.can_block_reload;
        loop {
            let started = self.fetcher.now();
            let before = self.window.reach();
            let interval = self.reload_interval();
            let blocking = self.blocking_reload_url();
            if wait_first || blocking.is_none() {
                self.fetcher.wait(interval);
            }

            let url = self.playlist_url.clone();
            let bytes = match blocking {
                Some(blocking) => match self.fetcher.fetch(&blocking, PLAYLIST_CAP) {
                    Ok(bytes) => bytes,
                    // Any failed blocking reload (a server past its limit
                    // answers 503) falls back to plain reloads for the rest
                    // of the session.
                    Err(e) => {
                        push_note(&mut self.notes, || {
                            format!("blocking reload failed ({e}); reloading without it")
                        });
                        self.blocking_refused = true;
                        self.fetcher.wait(interval);
                        self.fetch_with_retries(&url, PLAYLIST_CAP)?
                    }
                },
                None => self.fetch_with_retries(&url, PLAYLIST_CAP)?,
            };
            let window = match parse_playlist(&bytes, &url)? {
                ParsedPlaylist::Media(window) => *window,
                ParsedPlaylist::Master(_) => {
                    return Err(DemuxError::Parse(
                        "media playlist URL started returning a master playlist".into(),
                    ));
                }
            };

            let progressed = window.reach() > before || window.ended;
            let offset = self.cursor_offset();
            self.window = window;
            self.remap_cursor(offset);
            if self.window.ended
                || !matches!(self.locate(), Located::Missing)
                || self.cursor.sequence < self.window.first_sequence
            {
                return Ok(());
            }
            if progressed {
                stalled_since = None;
            } else {
                let since = *stalled_since.get_or_insert(started);
                if self.fetcher.now().saturating_sub(since) >= stale_limit {
                    return Err(DemuxError::Source("live playlist stopped advancing".into()));
                }
            }
            wait_first = !progressed;
        }
    }
}

/// Byte window the chaining TS source serves from.
struct ChainState {
    buf: Vec<u8>,
    /// Absolute offset of `buf[0]` on the demuxer's timeline.
    base: u64,
    ended: bool,
    /// Segment stashed when its discontinuity flag fired. The source
    /// serves end-of-stream so the old demuxer flushes its trailing PES
    /// cleanly; the wrapper then rebuilds on this segment.
    pending: Option<FetchedSegment>,
}

/// Sequential TS bytes across segments; fetches inside `read_at` exactly
/// like a live transport source blocks on its socket.
struct TsChainSource {
    state: Arc<Mutex<ChainState>>,
    scheduler: Arc<Mutex<Scheduler>>,
}

impl ByteSource for TsChainSource {
    fn size(&mut self) -> Result<Option<u64>, SourceError> {
        Ok(None)
    }

    fn read_at(&mut self, offset: u64, out: &mut [u8]) -> Result<usize, SourceError> {
        loop {
            {
                let mut state = self.state.lock().expect("chain lock");
                if offset < state.base {
                    return Err("TS chain read below the retained window".into());
                }
                let rel = (offset - state.base) as usize;
                if rel < state.buf.len() {
                    let n = out.len().min(state.buf.len() - rel);
                    out[..n].copy_from_slice(&state.buf[rel..rel + n]);
                    // The demuxer reads strictly forward; keep a little
                    // slack and drop the rest.
                    let keep_from = rel.saturating_sub(64 * 1024);
                    if keep_from > 0 {
                        state.buf.drain(..keep_from);
                        state.base += keep_from as u64;
                    }
                    return Ok(n);
                }
                if state.ended || state.pending.is_some() {
                    return Ok(0);
                }
            }
            let next = self
                .scheduler
                .lock()
                .expect("scheduler lock")
                .next_segment()
                .map_err(|e| Box::new(e) as SourceError)?;
            let mut state = self.state.lock().expect("chain lock");
            match next {
                Some(segment) if segment.discontinuity != DiscontinuityKind::None => {
                    state.pending = Some(segment);
                }
                Some(segment) => state.buf.extend_from_slice(&segment.data),
                None => state.ended = true,
            }
        }
    }
}

/// Downstream-facing state shared by both modes: format dedup, track
/// identity, the timeline origin.
#[derive(Default)]
struct Adapter {
    announced: Vec<(TrackId, Format)>,
    video_track: Option<TrackId>,
    audio_track: Option<TrackId>,
    timeline_origin: Option<MediaTime>,
}

impl Adapter {
    /// Suppress duplicate Format re-announcements from later segments;
    /// learn track identity and the timeline origin.
    fn adapt(&mut self, event: StreamEvent) -> Option<StreamEvent> {
        match event {
            StreamEvent::Format(track, format) => {
                match &format {
                    Format::Video { .. } => self.video_track = Some(track),
                    Format::Audio { .. } => self.audio_track = Some(track),
                }
                if self
                    .announced
                    .iter()
                    .any(|(t, f)| *t == track && *f == format)
                {
                    return None;
                }
                self.announced.retain(|(t, _)| *t != track);
                self.announced.push((track, format.clone()));
                Some(StreamEvent::Format(track, format))
            }
            StreamEvent::Au(au) => {
                if self.timeline_origin.is_none() {
                    self.timeline_origin = Some(au.pts);
                }
                Some(StreamEvent::Au(au))
            }
            other => Some(other),
        }
    }

    fn splice_event(&self, kind: DiscontinuityKind) -> StreamEvent {
        let track = self.video_track.unwrap_or(TrackId(0));
        let reason = match kind {
            DiscontinuityKind::Stated => DiscontinuityReason::AdSplice,
            _ => DiscontinuityReason::Reconnect,
        };
        StreamEvent::Discontinuity(track, reason)
    }
}

/// Fetch (or reuse) the init segment and parse `init + segment`; `tfdt`
/// keeps the timestamps absolute across segments.
fn build_fmp4_inner(
    scheduler: &Arc<Mutex<Scheduler>>,
    limits: &DemuxLimits,
    generation: Generation,
    segment: &FetchedSegment,
    init_cache: &mut HashMap<String, Vec<u8>>,
) -> Result<Mp4Demuxer, DemuxError> {
    let mut bytes = Vec::new();
    if let Some(map_url) = &segment.map_url {
        if !init_cache.contains_key(map_url) {
            let init = scheduler
                .lock()
                .expect("scheduler lock")
                .fetch_with_retries(map_url, SEGMENT_CAP)?;
            init_cache.insert(map_url.clone(), init);
        }
        bytes.extend_from_slice(&init_cache[map_url]);
    }
    bytes.extend_from_slice(&segment.data);
    Mp4Demuxer::open(
        Box::new(media_demux::MemSource(bytes)),
        limits.clone(),
        generation,
    )
}

enum Mode {
    /// First pull decides TS vs fMP4 from the first segment.
    Undecided,
    Ts {
        demuxer: Box<TsDemuxer>,
        chain: Arc<Mutex<ChainState>>,
    },
    Fmp4 {
        inner: Option<Box<Mp4Demuxer>>,
        init_cache: HashMap<String, Vec<u8>>,
    },
}

pub struct HlsDemuxer {
    scheduler: Arc<Mutex<Scheduler>>,
    limits: DemuxLimits,
    generation: Generation,
    mode: Mode,
    adapter: Adapter,
    duration: Option<MediaTime>,
    pending: VecDeque<StreamEvent>,
    ended: bool,
    notes: Vec<String>,
    audio_rendition: Option<Result<String, String>>,
    join_anchor: Option<JoinAnchor>,
}

impl HlsDemuxer {
    /// Open from already-fetched playlist bytes (the router sniffed them).
    /// A master playlist resolves to its highest-bandwidth variant.
    pub fn open(
        playlist_url: &str,
        playlist_bytes: Vec<u8>,
        fetcher: Box<dyn SegmentFetcher>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        Self::open_with(
            playlist_url,
            playlist_bytes,
            fetcher,
            limits,
            generation,
            None,
        )
    }

    /// Open an audio rendition's media playlist, as named by
    /// [`HlsDemuxer::audio_rendition`]. On a live playlist it joins where
    /// `anchor` (the variant's [`HlsDemuxer::join_anchor`]) sits on the
    /// timeline, or on its own hold-back when nothing lines up.
    pub fn open_rendition(
        playlist_url: &str,
        playlist_bytes: Vec<u8>,
        fetcher: Box<dyn SegmentFetcher>,
        limits: DemuxLimits,
        generation: Generation,
        anchor: Option<JoinAnchor>,
    ) -> Result<Self, DemuxError> {
        if let ParsedPlaylist::Master(_) = parse_playlist(&playlist_bytes, playlist_url)? {
            return Err(DemuxError::Unsupported(
                "audio rendition is a master playlist",
            ));
        }
        Self::open_with(
            playlist_url,
            playlist_bytes,
            fetcher,
            limits,
            generation,
            Some(anchor),
        )
    }

    /// `rendition` is `None` for a variant or a single playlist, and
    /// `Some` of the variant's join (if it had one) for an audio rendition.
    fn open_with(
        playlist_url: &str,
        playlist_bytes: Vec<u8>,
        mut fetcher: Box<dyn SegmentFetcher>,
        limits: DemuxLimits,
        generation: Generation,
        rendition: Option<Option<JoinAnchor>>,
    ) -> Result<Self, DemuxError> {
        let mut url = playlist_url.to_string();
        let mut notes = Vec::new();
        let mut audio_rendition = None;
        let window = match parse_playlist(&playlist_bytes, &url)? {
            ParsedPlaylist::Media(window) => *window,
            ParsedPlaylist::Master(variants) => {
                let chosen = variants[0].clone();
                push_note(&mut notes, || {
                    format!(
                        "master playlist: picked {} bps of {} variants",
                        chosen.bandwidth,
                        variants.len()
                    )
                });
                let bytes = fetcher
                    .fetch(&chosen.url, PLAYLIST_CAP)
                    .map_err(DemuxError::Source)?;
                url = chosen.url;
                audio_rendition = chosen.audio_url;
                match parse_playlist(&bytes, &url)? {
                    ParsedPlaylist::Media(window) => *window,
                    ParsedPlaylist::Master(_) => {
                        return Err(DemuxError::Unsupported(
                            "master playlist pointing at master playlists",
                        ));
                    }
                }
            }
        };

        let live = !window.ended;
        let duration = if window.ended {
            Some(
                window
                    .segments
                    .iter()
                    .fold(MediaTime::ZERO, |acc, s| acc + s.duration),
            )
        } else {
            None
        };
        let first_sequence = window.first_sequence;
        let mut scheduler = Scheduler {
            fetcher,
            playlist_url: url,
            window,
            live,
            cursor: Cursor::segment(first_sequence),
            pending_jump: false,
            resync: false,
            blocking_refused: false,
            notes,
        };
        let mut join_anchor = None;
        if live {
            let (point, behind) = scheduler.live_join();
            let aligned = rendition
                .flatten()
                .and_then(|anchor| scheduler.aligned_join(anchor));
            let joined = aligned.map_or(point.cursor, |(cursor, _)| cursor);
            scheduler.cursor = joined;
            join_anchor = Some(JoinAnchor {
                sequence: joined.sequence,
                part: joined.part,
                program_time_ms: scheduler
                    .join_points()
                    .0
                    .iter()
                    .find(|p| p.cursor == joined)
                    .and_then(|p| p.program_ms),
            });
            if let Some(why) = scheduler.window.low_latency_declined {
                push_note(&mut scheduler.notes, || {
                    format!("low-latency parts not used ({why}); playing whole segments")
                });
            }
            let join = scheduler.cursor;
            let at = match join.part {
                Some(part) => format!("sequence {} part {part}", join.sequence),
                None => format!("sequence {}", join.sequence),
            };
            match (rendition, aligned) {
                (Some(_), Some((_, by))) => push_note(&mut scheduler.notes, || {
                    format!("audio rendition: joining {at}, aligned with the variant by {by}")
                }),
                (Some(_), None) => push_note(&mut scheduler.notes, || {
                    format!(
                        "audio rendition: joining {at}, {} ms behind the end (nothing lines up with the variant's join)",
                        behind.as_millis()
                    )
                }),
                (None, _) if scheduler.window.low_latency.is_some() => {
                    push_note(&mut scheduler.notes, || {
                        format!(
                            "low-latency HLS: joining {at}, {} ms behind the end",
                            behind.as_millis()
                        )
                    });
                }
                (None, _) => {}
            }
        }

        Ok(Self {
            scheduler: Arc::new(Mutex::new(scheduler)),
            limits,
            generation,
            mode: Mode::Undecided,
            adapter: Adapter::default(),
            duration,
            pending: VecDeque::new(),
            ended: false,
            notes: Vec::new(),
            audio_rendition,
            join_anchor,
        })
    }

    /// The playlist of the chosen variant's audio rendition, when its
    /// audio is carried apart from the variant.
    pub fn audio_rendition(&self) -> Option<Result<&str, &str>> {
        self.audio_rendition
            .as_ref()
            .map(|rendition| rendition.as_deref().map_err(String::as_str))
    }

    /// Where a live playlist joined, for opening a rendition at the same
    /// point. `None` on an on-demand playlist, which starts at the top.
    pub fn join_anchor(&self) -> Option<JoinAnchor> {
        self.join_anchor
    }

    /// Liveness as the playlist itself states it (`EXT-X-ENDLIST` ⇒ VOD).
    /// The engine aligns the Bank mode with it.
    pub fn is_live(&self) -> bool {
        self.scheduler.lock().expect("scheduler lock").live
    }

    /// Decide TS vs fMP4 from the first fetched segment and build the
    /// inner demuxer state around it.
    fn decide_mode(&mut self) -> Result<(), DemuxError> {
        let first = self
            .scheduler
            .lock()
            .expect("scheduler lock")
            .next_segment()?;
        let Some(segment) = first else {
            self.ended = true;
            return Ok(());
        };
        let is_ts = segment.map_url.is_none()
            && (segment.data.first() == Some(&0x47)
                || (segment.data.get(4) == Some(&0x47) && segment.data.len() >= 192));
        if is_ts {
            let chain = Arc::new(Mutex::new(ChainState {
                buf: segment.data,
                base: 0,
                ended: false,
                pending: None,
            }));
            let source = TsChainSource {
                state: Arc::clone(&chain),
                scheduler: Arc::clone(&self.scheduler),
            };
            let demuxer = TsDemuxer::open(Box::new(source), self.limits.clone(), self.generation)?;
            self.mode = Mode::Ts {
                demuxer: Box::new(demuxer),
                chain,
            };
        } else {
            let mut init_cache = HashMap::new();
            let inner = build_fmp4_inner(
                &self.scheduler,
                &self.limits,
                self.generation,
                &segment,
                &mut init_cache,
            )?;
            self.mode = Mode::Fmp4 {
                inner: Some(Box::new(inner)),
                init_cache,
            };
        }
        Ok(())
    }
}

impl Demuxer for HlsDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        loop {
            if let Some(event) = self.pending.pop_front() {
                return Ok(event);
            }
            if self.ended {
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }
            if matches!(self.mode, Mode::Undecided) {
                self.decide_mode()?;
                continue;
            }
            match &mut self.mode {
                Mode::Undecided => unreachable!("decided above"),
                Mode::Ts { demuxer, chain } => match demuxer.next_event() {
                    Ok(StreamEvent::Eos(reason)) => {
                        // The chain serves EOS at a splice so the old
                        // demuxer flushes its trailing PES; a stashed
                        // segment means rebuild, not end.
                        let kind = {
                            let mut state = chain.lock().expect("chain lock");
                            state.pending.take().map(|segment| {
                                let kind = segment.discontinuity;
                                state.buf = segment.data;
                                state.base = 0;
                                state.ended = false;
                                kind
                            })
                        };
                        match kind {
                            Some(kind) => {
                                let source = TsChainSource {
                                    state: Arc::clone(chain),
                                    scheduler: Arc::clone(&self.scheduler),
                                };
                                **demuxer = TsDemuxer::open(
                                    Box::new(source),
                                    self.limits.clone(),
                                    self.generation,
                                )?;
                                self.pending.push_back(self.adapter.splice_event(kind));
                            }
                            None => {
                                self.ended = true;
                                return Ok(StreamEvent::Eos(reason));
                            }
                        }
                    }
                    Ok(event) => {
                        if let Some(out) = self.adapter.adapt(event) {
                            return Ok(out);
                        }
                    }
                    Err(e) => return Err(e),
                },
                Mode::Fmp4 { inner, init_cache } => {
                    if inner.is_none() {
                        let next = self
                            .scheduler
                            .lock()
                            .expect("scheduler lock")
                            .next_segment()?;
                        match next {
                            None => {
                                self.ended = true;
                                return Ok(StreamEvent::Eos(EosReason::Natural));
                            }
                            Some(segment) => {
                                if segment.discontinuity != DiscontinuityKind::None {
                                    self.pending.push_back(
                                        self.adapter.splice_event(segment.discontinuity),
                                    );
                                }
                                let built = build_fmp4_inner(
                                    &self.scheduler,
                                    &self.limits,
                                    self.generation,
                                    &segment,
                                    init_cache,
                                )?;
                                *inner = Some(Box::new(built));
                            }
                        }
                        continue;
                    }
                    match inner.as_mut().expect("ensured above").next_event() {
                        // Per-segment EOS just advances the chain.
                        Ok(StreamEvent::Eos(_)) => {
                            *inner = None;
                        }
                        Ok(event) => {
                            if let Some(out) = self.adapter.adapt(event) {
                                return Ok(out);
                            }
                        }
                        Err(e) => return Err(e),
                    }
                }
            }
        }
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        if self.is_live() {
            return Err(DemuxError::Unsupported("seek on a live HLS lane"));
        }
        let origin = self.adapter.timeline_origin.unwrap_or(MediaTime::ZERO);
        let rel = (target - origin).max(MediaTime::ZERO);
        let mut scheduler = self.scheduler.lock().expect("scheduler lock");
        let mut cumulative = MediaTime::ZERO;
        let mut index = 0usize;
        let mut landed = MediaTime::ZERO;
        for (i, segment) in scheduler.window.segments.iter().enumerate() {
            index = i;
            landed = cumulative;
            if cumulative + segment.duration > rel {
                break;
            }
            cumulative += segment.duration;
        }
        scheduler.cursor = Cursor::segment(scheduler.window.first_sequence + index as u64);
        scheduler.pending_jump = false;
        drop(scheduler);

        self.generation = generation;
        self.pending.clear();
        self.ended = false;
        // The inner demuxer rebuilds on the target segment (well-formed
        // VOD segments open on keyframes).
        self.mode = Mode::Undecided;
        Ok(origin + landed)
    }

    fn duration(&self) -> Option<MediaTime> {
        self.duration
    }

    fn video_track(&self) -> Option<TrackId> {
        self.adapter.video_track
    }

    fn audio_track(&self) -> Option<TrackId> {
        self.adapter.audio_track
    }

    fn take_notes(&mut self) -> Vec<String> {
        let mut notes = std::mem::take(&mut self.notes);
        notes.extend(std::mem::take(
            &mut self.scheduler.lock().expect("scheduler lock").notes,
        ));
        notes
    }
}
