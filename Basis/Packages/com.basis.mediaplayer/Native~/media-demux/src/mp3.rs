//! Raw MP3 file demuxer: ID3v2 skip, then an MPEG audio frame walk.
//! Each AU is one Layer III frame, its length computed from its header and
//! its pts from accumulated samples. A leading Xing/Info/VBRI frame is metadata,
//! not audio: it is parsed for the duration and seek table, then dropped.
//!
//! Seeking an MP3 is approximate, as it is in every player: frames carry
//! no index, so the target maps to a byte offset through the Xing table
//! where there is one and a constant-bitrate estimate where there is not,
//! and playback resumes at the first frame header found from there.

use media_clock::{Generation, MediaTime};

use crate::artwork;
use crate::source::SeqReader;
use crate::{
    Artwork, Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format,
    StreamEvent, TrackId,
};

const TRACK: TrackId = TrackId(1);

/// Bytes of garbage tolerated while hunting a frame sync (tags between
/// frames, truncated tails).
const RESYNC_CAP: u64 = 256 * 1024;

#[derive(Clone, Copy, PartialEq, Eq, Debug)]
enum Version {
    Mpeg1,
    Mpeg2,
    Mpeg25,
}

#[derive(Clone, Copy)]
struct FrameInfo {
    version: Version,
    sample_rate: u32,
    channels: u32,
    /// Whole frame length in bytes, header included.
    frame_len: usize,
    /// 2 when the frame carries a CRC, which shifts the side-info block.
    crc_len: usize,
    /// Inter-channel samples per frame at this version.
    samples: u32,
}

const BITRATES_V1_L3: [u32; 15] = [
    0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320,
];
const BITRATES_V2_L3: [u32; 15] = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

/// Parse a 4-byte MPEG audio frame header; Layer III only (the decode
/// route is the MP3 decoder). `None` = not a Layer III header here.
fn parse_frame_header(h: &[u8]) -> Option<FrameInfo> {
    if h.len() < 4 || h[0] != 0xFF || h[1] & 0xE0 != 0xE0 {
        return None;
    }
    let version = match (h[1] >> 3) & 0x03 {
        0b00 => Version::Mpeg25,
        0b10 => Version::Mpeg2,
        0b11 => Version::Mpeg1,
        _ => return None,
    };
    // Layer bits: 01 = Layer III.
    if (h[1] >> 1) & 0x03 != 0b01 {
        return None;
    }
    let bitrate_idx = (h[2] >> 4) as usize;
    let rate_idx = ((h[2] >> 2) & 0x03) as usize;
    if bitrate_idx == 0 || bitrate_idx == 15 || rate_idx == 3 {
        // Free-format (index 0) is refused: length is not derivable.
        return None;
    }
    let base_rate = [44_100u32, 48_000, 32_000][rate_idx];
    let sample_rate = match version {
        Version::Mpeg1 => base_rate,
        Version::Mpeg2 => base_rate / 2,
        Version::Mpeg25 => base_rate / 4,
    };
    let bitrate = match version {
        Version::Mpeg1 => BITRATES_V1_L3[bitrate_idx],
        _ => BITRATES_V2_L3[bitrate_idx],
    };
    let padding = usize::from((h[2] >> 1) & 1);
    let (samples, factor) = match version {
        Version::Mpeg1 => (1152u32, 144_000),
        _ => (576, 72_000),
    };
    let frame_len = (factor * bitrate / sample_rate) as usize + padding;
    if frame_len <= 4 {
        return None;
    }
    let channels = if (h[3] >> 6) & 0x03 == 0b11 { 1 } else { 2 };
    // Protection bit clear means a 2-byte CRC follows the header.
    let crc_len = if h[1] & 1 == 0 { 2 } else { 0 };
    Some(FrameInfo {
        version,
        sample_rate,
        channels,
        frame_len,
        crc_len,
        samples,
    })
}

/// Sample rate and channel count from a Layer III frame header.
pub(crate) fn frame_format(h: &[u8]) -> Option<(u32, u32)> {
    parse_frame_header(h).map(|info| (info.sample_rate, info.channels))
}

/// The encoder's leading metadata frame (Xing/Info from LAME and friends,
/// VBRI from Fraunhofer): total counts and, for Xing, a 100-entry seek
/// table mapping time percent to byte percent. Decodes as silence, so it
/// is dropped from the AU stream.
#[derive(Default)]
struct VbrHeader {
    /// Audio frames in the file, excluding this one. 0 = unstated.
    frames: u32,
    /// Bytes of MPEG data from this frame onwards. 0 = unstated.
    bytes: u32,
    /// Xing seek table: byte-percent (of `bytes`, scaled by 256) at each
    /// whole percent of the duration.
    toc: Option<[u8; 100]>,
}

fn be32(p: &[u8]) -> u32 {
    u32::from_be_bytes([p[0], p[1], p[2], p[3]])
}

/// Parse the leading metadata frame, if this is one. Xing/Info sit past
/// the side-info block (whose size depends on version and channel count,
/// and which follows the optional CRC); VBRI is always at byte 36.
fn parse_vbr_header(frame: &[u8], info: &FrameInfo) -> Option<VbrHeader> {
    let side_info = match (info.version, info.channels) {
        (Version::Mpeg1, 1) => 17,
        (Version::Mpeg1, _) => 32,
        (_, 1) => 9,
        (_, _) => 17,
    };
    let at = 4 + info.crc_len + side_info;
    if matches!(frame.get(at..at + 4), Some(b"Xing") | Some(b"Info")) {
        let mut header = VbrHeader::default();
        let flags = be32(frame.get(at + 4..at + 8)?);
        let mut q = at + 8;
        if flags & 0x1 != 0 {
            header.frames = be32(frame.get(q..q + 4)?);
            q += 4;
        }
        if flags & 0x2 != 0 {
            header.bytes = be32(frame.get(q..q + 4)?);
            q += 4;
        }
        if flags & 0x4 != 0
            && let Some(toc) = frame.get(q..q + 100)
        {
            header.toc = Some(toc.try_into().expect("sliced 100"));
        }
        return Some(header);
    }
    if frame.get(36..40) == Some(b"VBRI") {
        // Counts only; its table is a different shape and the estimate
        // covers what it would buy.
        return Some(VbrHeader {
            bytes: be32(frame.get(46..50)?),
            frames: be32(frame.get(50..54)?),
            toc: None,
        });
    }
    None
}

pub struct Mp3Demuxer {
    reader: SeqReader,
    generation: Generation,
    version: Version,
    sample_rate: u32,
    channels: u32,
    announced: bool,
    /// Inter-channel samples emitted so far: the exact pts base.
    samples_out: u64,
    notes: Vec<String>,
    ended: bool,
    /// Absolute offset of the first MPEG frame, which is the metadata one
    /// where the file has it. Xing byte counts are measured from here.
    mpeg_start: u64,
    /// Absolute offset of the first *audio* frame.
    audio_start: u64,
    /// Bytes per second at the first frame's bitrate: the estimate that
    /// carries a file with no seek table.
    cbr_bps: u64,
    vbr: Option<VbrHeader>,
    duration: Option<MediaTime>,
    /// Whether the source can serve the backwards reads a seek needs.
    seekable: bool,
    /// Cover art from the ID3v2 tag the frame walk skips over.
    artwork: Option<Artwork>,
}

impl Mp3Demuxer {
    pub fn open(
        mut src: Box<dyn ByteSource>,
        _limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        // A size answer is the seek gate: a sequential live source serves
        // forward reads only, and a duration implies a working seek bar.
        let seekable = src.size().map_err(DemuxError::Source)?.is_some();
        let mut reader = SeqReader::new(src);

        // ID3v2: "ID3", version (2), flags (1), syncsafe size (4).
        let mut artwork = None;
        let head = reader.peek(10).map_err(DemuxError::Source)?;
        if head.starts_with(b"ID3") && head.len() >= 10 {
            let major = head[3];
            let flags = head[5];
            let size = (u64::from(head[6] & 0x7F) << 21)
                | (u64::from(head[7] & 0x7F) << 14)
                | (u64::from(head[8] & 0x7F) << 7)
                | u64::from(head[9] & 0x7F);
            let footer = if flags & 0x10 != 0 { 10 } else { 0 };
            // The tag is skipped either way; read it first only when it is
            // small enough to be a tag rather than a hostile length, since
            // cover art is the one thing in it worth carrying.
            if size <= artwork::MAX_ARTWORK_BYTES as u64 {
                let want = 10 + size as usize;
                let tag = reader.peek(want).map_err(DemuxError::Source)?;
                if let Some(body) = tag.get(10..want) {
                    artwork = artwork::from_id3v2(major, flags, body);
                }
            }
            reader
                .seek_to(10 + size + footer)
                .map_err(DemuxError::Source)?;
        }

        // Find the first frame: a valid header whose length lands on
        // another valid header (or end of source), the standard defence
        // against payload false syncs.
        let mut skipped = 0u64;
        let info = loop {
            let data = reader.peek(4).map_err(DemuxError::Source)?;
            if data.len() < 4 {
                return Err(DemuxError::Unsupported("no MPEG audio frames found"));
            }
            if let Some(info) = parse_frame_header(data) {
                let data = reader
                    .peek(info.frame_len + 4)
                    .map_err(DemuxError::Source)?;
                let confirmed = match data.get(info.frame_len..info.frame_len + 4) {
                    Some(next) => parse_frame_header(next).is_some(),
                    None => data.len() >= 4, // sole frame before EOF
                };
                if confirmed {
                    break info;
                }
            }
            reader.consume(1);
            skipped += 1;
            if skipped > RESYNC_CAP {
                return Err(DemuxError::Unsupported("no MPEG audio frames found"));
            }
        };

        // The leading metadata frame carries the counts and the seek
        // table. Consume it here rather than mid-walk so the byte offsets
        // either side of it are known before the first AU is served.
        let mpeg_start = reader.pos();
        let mut vbr = None;
        let frame = reader
            .peek(info.frame_len)
            .map_err(DemuxError::Source)?
            .to_vec();
        if frame.len() >= info.frame_len
            && let Some(header) = parse_vbr_header(&frame[..info.frame_len], &info)
        {
            reader.consume(info.frame_len);
            vbr = Some(header);
        }
        let audio_start = reader.pos();

        let cbr_bps = if info.samples > 0 {
            u64::from(info.frame_len as u32) * u64::from(info.sample_rate) / u64::from(info.samples)
        } else {
            0
        };
        let duration = match &vbr {
            Some(header) if header.frames > 0 => Some(MediaTime::from_micros(
                i64::from(header.frames) * i64::from(info.samples) * 1_000_000
                    / i64::from(info.sample_rate),
            )),
            _ => None,
        };

        Ok(Self {
            reader,
            generation,
            version: info.version,
            sample_rate: info.sample_rate,
            channels: info.channels,
            announced: false,
            samples_out: 0,
            notes: Vec::new(),
            ended: false,
            mpeg_start,
            audio_start,
            cbr_bps,
            vbr,
            // A duration is only offered where the seek it implies works.
            duration: if seekable { duration } else { None },
            seekable,
            artwork,
        })
    }

    /// Byte offset for a target instant: the Xing table where the file has
    /// one, interpolated between its whole-percent entries; a proportional
    /// split of the stated byte count where it states frames but no table;
    /// and a constant-bitrate estimate otherwise.
    fn offset_for(&self, target: MediaTime) -> u64 {
        let micros = target.as_micros().max(0);
        let payload = self
            .vbr
            .as_ref()
            .map(|v| u64::from(v.bytes))
            .unwrap_or(0)
            .saturating_sub(self.audio_start - self.mpeg_start);
        if let (Some(vbr), Some(duration)) = (self.vbr.as_ref(), self.duration)
            && duration.as_micros() > 0
            && payload > 0
        {
            let mut frac = micros as f64 / duration.as_micros() as f64;
            frac = frac.clamp(0.0, 1.0);
            if let Some(toc) = &vbr.toc {
                let percent = frac * 100.0;
                let a = (percent as usize).min(99);
                let fa = f64::from(toc[a]);
                let fb = if a < 99 { f64::from(toc[a + 1]) } else { 256.0 };
                frac = (fa + (fb - fa) * (percent - a as f64)) / 256.0;
            }
            return self.audio_start + (frac * payload as f64) as u64;
        }
        self.audio_start + (micros as u64).saturating_mul(self.cbr_bps) / 1_000_000
    }
}

impl Demuxer for Mp3Demuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if !self.announced {
            self.announced = true;
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec: AudioCodec::Mp3,
                    sample_rate: self.sample_rate,
                    channels: self.channels,
                    codec_private: Vec::new(),
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }

        let mut skipped = 0u64;
        loop {
            let data = self.reader.peek(4).map_err(DemuxError::Source)?;
            if data.len() < 4 {
                // Tail bytes that cannot hold a frame (ID3v1 remnants).
                self.ended = true;
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }
            // A header must match the stream's fixed identity; anything
            // else is a false sync inside garbage, so resync.
            let info = match parse_frame_header(data) {
                Some(info)
                    if info.version == self.version
                        && info.sample_rate == self.sample_rate
                        && info.channels == self.channels =>
                {
                    info
                }
                _ => {
                    self.reader.consume(1);
                    skipped += 1;
                    if skipped > RESYNC_CAP {
                        return Err(DemuxError::Cap("MP3 resync distance"));
                    }
                    continue;
                }
            };
            let data = self
                .reader
                .peek(info.frame_len)
                .map_err(DemuxError::Source)?;
            if data.len() < info.frame_len {
                // Truncated final frame: not decodable, drop it.
                self.notes
                    .push("dropped a truncated final MP3 frame".into());
                self.ended = true;
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }
            let frame = data[..info.frame_len].to_vec();
            self.reader.consume(info.frame_len);
            let pts = MediaTime::from_micros(
                (self.samples_out as i64).saturating_mul(1_000_000) / i64::from(self.sample_rate),
            );
            self.samples_out += u64::from(info.samples);
            return Ok(StreamEvent::Au(Au {
                track: TRACK,
                data: frame,
                pts,
                dts: pts,
                key: true,
                generation: self.generation,
            }));
        }
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        if !self.seekable {
            return Err(DemuxError::Unsupported("seek on a streaming MP3 source"));
        }
        self.reader.reposition(self.offset_for(target));
        self.generation = generation;
        // The landing is a byte estimate, so the timeline is re-anchored
        // on the request: the next frame found from there is treated as
        // the target instant. Without this the pts would keep counting
        // from the samples emitted before the seek and run backwards.
        self.samples_out = (target.as_micros().max(0) as u64)
            .saturating_mul(u64::from(self.sample_rate))
            / 1_000_000;
        self.ended = false;
        Ok(MediaTime::from_micros(
            (self.samples_out as i64).saturating_mul(1_000_000) / i64::from(self.sample_rate),
        ))
    }

    fn duration(&self) -> Option<MediaTime> {
        self.duration
    }

    fn audio_track(&self) -> Option<TrackId> {
        Some(TRACK)
    }

    fn artwork(&self) -> Option<&Artwork> {
        self.artwork.as_ref()
    }

    fn take_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notes)
    }
}
