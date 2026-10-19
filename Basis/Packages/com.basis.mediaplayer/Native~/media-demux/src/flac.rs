//! Raw FLAC file demuxer: parses the stream header, then walks
//! frames by header validation (sync pattern, structural fields, CRC-8,
//! and the expected frame/sample number) so each AU is one complete FLAC
//! frame with an exact sample-derived pts. Decoding stays in decode-sw;
//! the whole header region travels as codec private data.
//!
//! Every frame header states its own sample number, so a seek is exact
//! once the byte position is found. A SEEKTABLE states that position
//! outright; without one the position is bisected over confirmed frame
//! headers, each probe reading back the sample number that narrows the
//! interval.

use media_clock::{Generation, MediaTime};

use crate::artwork;
use crate::source::SeqReader;
use crate::{
    Artwork, Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format,
    StreamEvent, TrackId,
};

const TRACK: TrackId = TrackId(1);
const SCAN_CHUNK: usize = 64 * 1024;
/// A header candidate needs this much visibility before a failed parse is
/// trusted (headers are at most ~16 bytes; 64 leaves margin).
const HEADER_VISIBILITY: usize = 64;
/// The sample rates a frame header codes directly (bits 1..=11). 0 defers
/// to STREAMINFO and 12..=14 state the rate past the header.
const FRAME_RATES: [u32; 11] = [
    88_200, 176_400, 192_000, 8_000, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 96_000,
];
/// Seek points kept from a SEEKTABLE, strided evenly across a longer one
/// so a large table costs bounded memory without losing its reach.
const MAX_SEEK_POINTS: usize = 4096;
/// Probes one seek may spend narrowing its interval. Each cuts the window
/// by at least a quarter, so this sits far past what a file needs; it is
/// here so a hostile layout still terminates.
const SEEK_PROBE_CAP: u32 = 32;
/// Bytes one probe scans for a frame start before treating the region as
/// unusable. Generous against any real frame.
const SEEK_SCAN_CAP: u64 = 1024 * 1024;

/// CRC-8 over the frame header, polynomial 0x07 (the FLAC one).
fn crc8(data: &[u8]) -> u8 {
    let mut crc = 0u8;
    for &b in data {
        crc ^= b;
        for _ in 0..8 {
            crc = if crc & 0x80 != 0 {
                (crc << 1) ^ 0x07
            } else {
                crc << 1
            };
        }
    }
    crc
}

/// A parsed and CRC-validated frame header.
struct FrameHeader {
    /// Total header bytes including the CRC-8.
    len: usize,
    /// Frame number (fixed block size) or first sample number (variable).
    coded_number: u64,
    variable_block_size: bool,
    /// Block size in inter-channel samples.
    block_size: u32,
    /// Sample-rate code, which a stream states per frame.
    sr_bits: u8,
    /// Channel-assignment code: 0..=7 is (channel count − 1), 8..=10 the
    /// stereo decorrelations.
    chan_bits: u8,
}

/// Parse a frame header at the start of `data`, validating everything the
/// format pins down plus the CRC-8. `None` = not a frame header here.
fn parse_frame_header(data: &[u8]) -> Option<FrameHeader> {
    if data.len() < 6 || data[0] != 0xFF || data[1] & 0xFE != 0xF8 {
        return None;
    }
    let variable_block_size = data[1] & 0x01 != 0;
    let bs_bits = data[2] >> 4;
    let sr_bits = data[2] & 0x0F;
    let chan_bits = data[3] >> 4;
    let size_bits = (data[3] >> 1) & 0x07;
    if bs_bits == 0 || sr_bits == 15 || chan_bits > 10 || size_bits == 3 || data[3] & 1 != 0 {
        return None;
    }

    // UTF-8-coded frame/sample number: up to 7 bytes.
    let mut idx = 4;
    let first = *data.get(idx)?;
    let extra = match first {
        0x00..=0x7F => 0,
        0xC0..=0xDF => 1,
        0xE0..=0xEF => 2,
        0xF0..=0xF7 => 3,
        0xF8..=0xFB => 4,
        0xFC..=0xFD => 5,
        0xFE => 6,
        _ => return None,
    };
    let mut coded_number = if extra == 0 {
        u64::from(first)
    } else {
        u64::from(first & (0x7F >> (extra + 1)))
    };
    idx += 1;
    for _ in 0..extra {
        let b = *data.get(idx)?;
        if b & 0xC0 != 0x80 {
            return None;
        }
        coded_number = (coded_number << 6) | u64::from(b & 0x3F);
        idx += 1;
    }

    let block_size = match bs_bits {
        1 => 192,
        2..=5 => 576 << (bs_bits - 2),
        6 => {
            let b = *data.get(idx)?;
            idx += 1;
            u32::from(b) + 1
        }
        7 => {
            let hi = *data.get(idx)?;
            let lo = *data.get(idx + 1)?;
            idx += 2;
            (u32::from(hi) << 8 | u32::from(lo)) + 1
        }
        _ => 256 << (bs_bits - 8),
    };
    match sr_bits {
        12 => idx += 1,
        13 | 14 => idx += 2,
        _ => {}
    }
    if crc8(data.get(..idx)?) != *data.get(idx)? {
        return None;
    }
    Some(FrameHeader {
        len: idx + 1,
        coded_number,
        variable_block_size,
        block_size,
        sr_bits,
        chan_bits,
    })
}

/// Screen a candidate against the identity STREAMINFO announced. The
/// CRC-8 alone leaves roughly one false sync per 256 structurally valid
/// candidates, which a walk that starts mid-file meets far more often
/// than one that starts at the first frame.
fn matches_stream(header: &FrameHeader, sample_rate: u32, channels: u32) -> bool {
    let rate_ok = match header.sr_bits {
        0 | 12..=14 => true,
        bits => FRAME_RATES[usize::from(bits) - 1] == sample_rate,
    };
    let coded_channels = if header.chan_bits >= 8 {
        2
    } else {
        u32::from(header.chan_bits) + 1
    };
    rate_ok && coded_channels == channels
}

pub struct FlacDemuxer {
    reader: SeqReader,
    limits: DemuxLimits,
    generation: Generation,
    sample_rate: u32,
    /// STREAMINFO max block size: the sample multiplier for fixed-blocksize
    /// frame numbers.
    max_block_size: u32,
    duration: Option<MediaTime>,
    /// The stream header region, announced as codec private data.
    header: Option<Vec<u8>>,
    channels: u32,
    ended: bool,
    /// Absolute offset of the first frame: where the seek interval starts
    /// and what the SEEKTABLE's offsets are measured from.
    first_frame: u64,
    /// Total size where the source states one. Its absence is the seek
    /// gate: a sequential live source serves forward reads only.
    stream_len: Option<u64>,
    /// STREAMINFO's total sample count, 0 when unstated.
    total_samples: u64,
    /// (sample, absolute offset) from the SEEKTABLE, ascending.
    seek_points: Vec<(u64, u64)>,
    /// Cover art from a PICTURE block. The bytes already travel inside the
    /// codec private data with the rest of the header region, but nothing
    /// downstream can find them in there.
    artwork: Option<Artwork>,
}

impl FlacDemuxer {
    pub fn open(
        mut src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        let stream_len = src.size().map_err(DemuxError::Source)?;
        let mut reader = SeqReader::new(src);
        let head = reader.peek(4).map_err(DemuxError::Source)?;
        if !head.starts_with(b"fLaC") {
            return Err(DemuxError::Unsupported("not a FLAC stream"));
        }

        // Walk the metadata blocks to find where frames start; keep the
        // whole region as codec private data.
        let mut header_len = 4usize;
        let mut streaminfo: Option<(u32, u32, u32, u64)> = None;
        let mut seek_points: Vec<(u64, u64)> = Vec::new();
        let mut picture: Option<(u32, Artwork)> = None;
        loop {
            if header_len as u64 + 4 > limits.max_metadata_bytes {
                return Err(DemuxError::Cap("FLAC metadata size"));
            }
            let data = reader.peek(header_len + 4).map_err(DemuxError::Source)?;
            let block: [u8; 4] = data
                .get(header_len..header_len + 4)
                .ok_or_else(|| DemuxError::Parse("FLAC header truncated".into()))?
                .try_into()
                .expect("sliced 4");
            let last = block[0] & 0x80 != 0;
            let kind = block[0] & 0x7F;
            let len =
                usize::from(block[1]) << 16 | usize::from(block[2]) << 8 | usize::from(block[3]);
            let body_start = header_len + 4;
            if body_start as u64 + len as u64 > limits.max_metadata_bytes {
                return Err(DemuxError::Cap("FLAC metadata size"));
            }
            if kind == 0 {
                let data = reader.peek(body_start + len).map_err(DemuxError::Source)?;
                let body = data
                    .get(body_start..body_start + len)
                    .ok_or_else(|| DemuxError::Parse("FLAC STREAMINFO truncated".into()))?;
                if body.len() < 34 {
                    return Err(DemuxError::Parse("FLAC STREAMINFO short".into()));
                }
                let max_block = u32::from(body[2]) << 8 | u32::from(body[3]);
                let rate =
                    u32::from(body[10]) << 12 | u32::from(body[11]) << 4 | u32::from(body[12]) >> 4;
                let channels = ((u32::from(body[12]) >> 1) & 0x07) + 1;
                let total = (u64::from(body[13]) & 0x0F) << 32
                    | u64::from(body[14]) << 24
                    | u64::from(body[15]) << 16
                    | u64::from(body[16]) << 8
                    | u64::from(body[17]);
                streaminfo = Some((rate, channels, max_block, total));
            } else if kind == 3 {
                let data = reader.peek(body_start + len).map_err(DemuxError::Source)?;
                let body = data
                    .get(body_start..body_start + len)
                    .ok_or_else(|| DemuxError::Parse("FLAC SEEKTABLE truncated".into()))?;
                // 18 bytes per point: sample number, byte offset from the
                // first frame, frame sample count. A placeholder point
                // states u64::MAX and carries no position.
                let stride = (body.len() / 18).div_ceil(MAX_SEEK_POINTS).max(1);
                for point in body.as_chunks::<18>().0.iter().step_by(stride) {
                    let sample = u64::from_be_bytes(point[..8].try_into().expect("sliced 8"));
                    let offset = u64::from_be_bytes(point[8..16].try_into().expect("sliced 8"));
                    if sample != u64::MAX {
                        seek_points.push((sample, offset));
                    }
                }
            } else if kind == 6 {
                let data = reader.peek(body_start + len).map_err(DemuxError::Source)?;
                if let Some(body) = data.get(body_start..body_start + len)
                    && let Some(found) = artwork::parse_picture_block(body)
                {
                    artwork::prefer(&mut picture, found);
                }
            }
            header_len = body_start + len;
            if last {
                break;
            }
        }
        let Some((sample_rate, channels, max_block_size, total_samples)) = streaminfo else {
            return Err(DemuxError::Parse("FLAC header without STREAMINFO".into()));
        };
        if sample_rate == 0 {
            return Err(DemuxError::Parse("FLAC sample rate 0".into()));
        }
        let data = reader.peek(header_len).map_err(DemuxError::Source)?;
        let header = data
            .get(..header_len)
            .ok_or_else(|| DemuxError::Parse("FLAC header truncated".into()))?
            .to_vec();
        reader.consume(header_len);

        // The table's offsets are relative to the first frame; a point
        // that lands outside the stream is a broken table, not a bound.
        let first_frame = header_len as u64;
        seek_points.retain_mut(|(sample, offset)| {
            *offset = offset.saturating_add(first_frame);
            stream_len.is_none_or(|len| *offset < len)
                && (total_samples == 0 || *sample < total_samples)
        });
        seek_points.sort_unstable();
        seek_points.dedup_by_key(|(sample, _)| *sample);

        let duration = (total_samples > 0).then(|| {
            MediaTime::from_micros(
                (total_samples as i64).saturating_mul(1_000_000) / i64::from(sample_rate),
            )
        });
        Ok(Self {
            reader,
            limits,
            generation,
            sample_rate,
            max_block_size: max_block_size.max(1),
            // A duration implies a working seek bar, so it is gated on the
            // same answer the seek is.
            duration: duration.filter(|_| stream_len.is_some()),
            header: Some(header),
            channels,
            ended: false,
            first_frame,
            stream_len,
            total_samples,
            seek_points,
            artwork: picture.map(|(_, art)| art),
        })
    }

    /// Inter-channel sample position a header states. Fixed-blocksize
    /// streams number frames rather than samples.
    fn sample_of(&self, header: &FrameHeader) -> u64 {
        if header.variable_block_size {
            header.coded_number
        } else {
            header
                .coded_number
                .saturating_mul(u64::from(self.max_block_size))
        }
    }

    fn time_of(&self, samples: u64) -> MediaTime {
        MediaTime::from_micros(
            (samples as i64).saturating_mul(1_000_000) / i64::from(self.sample_rate),
        )
    }

    /// Length of the frame whose header this is, found by scanning for the
    /// next header carrying the number this frame implies. Together with
    /// the CRC-8 that rules out payload false syncs. The flag says
    /// whether such a header was found: end of source terminates the last
    /// frame with nothing to confirm it against. Peeks only, so the read
    /// position is unchanged.
    fn frame_end(&mut self, header: &FrameHeader) -> Result<(usize, bool), DemuxError> {
        let expected = if header.variable_block_size {
            header.coded_number + u64::from(header.block_size)
        } else {
            header.coded_number + 1
        };
        let mut end = header.len;
        loop {
            let want = end + SCAN_CHUNK;
            let data = self.reader.peek(want).map_err(DemuxError::Source)?;
            let at_eof = data.len() < want;
            let scan_limit = if at_eof {
                data.len()
            } else {
                data.len() - HEADER_VISIBILITY
            };
            let mut i = end;
            while i < scan_limit {
                if data[i] == 0xFF
                    && let Some(candidate) = parse_frame_header(&data[i..])
                    && candidate.variable_block_size == header.variable_block_size
                    && candidate.coded_number == expected
                {
                    return Ok((i, true));
                }
                i += 1;
            }
            if at_eof {
                return Ok((data.len(), false));
            }
            end = scan_limit;
            if end as u64 > self.limits.max_au_bytes {
                return Err(DemuxError::Cap("FLAC frame size"));
            }
        }
    }

    /// The first frame at or after `from` whose header matches the stream
    /// and is confirmed by the frame following it. A seek landing needs
    /// that standard because it starts reading mid-file, where the forward
    /// walk never does. Leaves the read position on the frame it
    /// found; `None` = none before the source ended.
    fn confirmed_frame_at_or_after(
        &mut self,
        from: u64,
    ) -> Result<Option<(u64, FrameHeader)>, DemuxError> {
        self.reader.reposition(from);
        let mut skipped = 0u64;
        loop {
            let at = self.reader.pos();
            let candidate = {
                let data = self
                    .reader
                    .peek(HEADER_VISIBILITY)
                    .map_err(DemuxError::Source)?;
                if data.is_empty() {
                    return Ok(None);
                }
                parse_frame_header(data)
            };
            if let Some(header) = candidate
                && matches_stream(&header, self.sample_rate, self.channels)
                && self.frame_end(&header)?.1
            {
                return Ok(Some((at, header)));
            }
            self.reader.consume(1);
            skipped += 1;
            if skipped > SEEK_SCAN_CAP {
                return Ok(None);
            }
        }
    }
}

impl Demuxer for FlacDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if let Some(private) = self.header.take() {
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec: AudioCodec::Flac,
                    sample_rate: self.sample_rate,
                    channels: self.channels,
                    codec_private: private,
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }

        // The next frame should start here; resync over garbage rather
        // than fail (padding, tags), bounded by the AU cap.
        let mut skipped = 0u64;
        let header = loop {
            let candidate = {
                let data = self
                    .reader
                    .peek(HEADER_VISIBILITY)
                    .map_err(DemuxError::Source)?;
                if data.is_empty() {
                    self.ended = true;
                    return Ok(StreamEvent::Eos(EosReason::Natural));
                }
                parse_frame_header(data)
            };
            if let Some(header) = candidate
                && matches_stream(&header, self.sample_rate, self.channels)
            {
                break header;
            }
            self.reader.consume(1);
            skipped += 1;
            if skipped > self.limits.max_au_bytes {
                return Err(DemuxError::Cap("FLAC resync distance"));
            }
        };
        let pts = self.time_of(self.sample_of(&header));

        let (end, _) = self.frame_end(&header)?;
        let data = self.reader.peek(end).map_err(DemuxError::Source)?[..end].to_vec();
        self.reader.consume(end);
        Ok(StreamEvent::Au(Au {
            track: TRACK,
            data,
            pts,
            dts: pts,
            key: true,
            generation: self.generation,
        }))
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        let Some(stream_len) = self.stream_len else {
            return Err(DemuxError::Unsupported("seek on a streaming FLAC source"));
        };
        let mut goal = (target.as_micros().max(0) as u64)
            .saturating_mul(u64::from(self.sample_rate))
            / 1_000_000;
        if self.total_samples > 0 {
            goal = goal.min(self.total_samples - 1);
        }

        // Best known frame at or before the goal. The first frame is the
        // floor; a SEEKTABLE point beats it, and a probe beats that. A point
        // is the file's word rather than something read back, so the landing
        // is confirmed before it is reported.
        let mut lo = (self.first_frame, 0u64, self.max_block_size);
        let mut lo_confirmed = false;
        let mut hi = (
            stream_len,
            (self.total_samples > 0).then_some(self.total_samples),
        );
        for &(sample, offset) in &self.seek_points {
            if sample <= goal {
                lo = (offset, sample, self.max_block_size);
            } else {
                hi = (offset, Some(sample));
                break;
            }
        }

        for _ in 0..SEEK_PROBE_CAP {
            // The frame at `lo` covers the goal: nothing left to narrow.
            if goal < lo.1.saturating_add(u64::from(lo.2)) || hi.0 <= lo.0 {
                break;
            }
            // Interpolate where the sample numbers allow it, clamped to
            // the middle half so a mis-estimate still cuts the window by a
            // quarter and the search cannot stall.
            let span = hi.0 - lo.0;
            let probe = match hi.1 {
                Some(hi_sample) if hi_sample > lo.1 => {
                    let frac = (goal - lo.1) as f64 / (hi_sample - lo.1) as f64;
                    lo.0 + (frac * span as f64) as u64
                }
                _ => lo.0 + span / 2,
            }
            .clamp(lo.0 + span / 4, hi.0 - span / 4);

            let Some((offset, header)) = self.confirmed_frame_at_or_after(probe)? else {
                // No frame between the probe and the end of the region:
                // the goal is behind it.
                hi = (probe, hi.1);
                continue;
            };
            let sample = self.sample_of(&header);
            if sample > goal || offset >= hi.0 {
                // Nothing starts between the probe and this frame, so the
                // frame carrying the goal starts before the probe. The
                // sample only bounds the interval when it is past the
                // goal; an offset out of bounds says the bounds were the
                // file's word and wrong.
                hi = (probe, (sample > goal).then_some(sample));
            } else if offset > lo.0 {
                lo = (offset, sample, header.block_size);
                lo_confirmed = true;
            } else {
                break;
            }
        }

        if !lo_confirmed && let Some((offset, header)) = self.confirmed_frame_at_or_after(lo.0)? {
            lo = (offset, self.sample_of(&header), header.block_size);
        }

        // The scans leave the reader on the frame they found, so only a
        // landing settled by an earlier probe needs the window discarded
        // (over a ranged source that discard costs a fetch).
        if self.reader.pos() != lo.0 {
            self.reader.reposition(lo.0);
        }
        self.generation = generation;
        self.ended = false;
        Ok(self.time_of(lo.1))
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
}
