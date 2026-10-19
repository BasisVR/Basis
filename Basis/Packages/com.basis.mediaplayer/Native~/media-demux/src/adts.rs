//! Raw ADTS (.aac) file demuxer: walks ADTS frames, strips the
//! 7/9-byte headers and reconstructs the 2-byte AudioSpecificConfig so the
//! platform AAC decoder gets exactly what the MP4 lane feeds it (raw
//! frames, payload type 0). The same channel screen applies: explicitly
//! signalled 1..=6 channel configurations only.
//!
//! ADTS states neither a length nor a frame count anywhere, and frames
//! carry no timestamp, so both the duration and the seek are keyed on the
//! byte rate averaged over the leading frames. Landing is approximate, as
//! it is in every player: the target maps to a byte offset, playback
//! resumes at the first confirmed frame from there, and the timeline is
//! re-anchored on the request.

use media_clock::{Generation, MediaTime};

use crate::source::SeqReader;
use crate::{
    Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format, StreamEvent,
    TrackId,
};

const TRACK: TrackId = TrackId(1);
const RESYNC_CAP: u64 = 256 * 1024;
/// Leading frames averaged for the byte rate. ~2.7 s at 48 kHz, which is
/// enough for the estimate to settle without holding much of the file.
const RATE_SAMPLE_FRAMES: usize = 128;

const SAMPLE_RATES: [u32; 13] = [
    96_000, 88_200, 64_000, 48_000, 44_100, 32_000, 24_000, 22_050, 16_000, 12_000, 11_025, 8_000,
    7_350,
];

#[derive(Clone, Copy)]
struct FrameInfo {
    /// AOT − 1 as ADTS codes it (0 = Main, 1 = LC, 2 = SSR).
    profile: u8,
    sf_index: u8,
    sample_rate: u32,
    channel_config: u8,
    header_len: usize,
    /// Whole frame length in bytes, header included.
    frame_len: usize,
}

/// Parse an ADTS frame header. `None` = not an ADTS header here.
fn parse_frame_header(h: &[u8]) -> Option<FrameInfo> {
    if h.len() < 7 || h[0] != 0xFF || h[1] & 0xF6 != 0xF0 {
        // Sync 0xFFF + layer bits 00; the protection bit may be either.
        return None;
    }
    let protection_absent = h[1] & 0x01 != 0;
    let profile = h[2] >> 6;
    let sf_index = (h[2] >> 2) & 0x0F;
    if usize::from(sf_index) >= SAMPLE_RATES.len() {
        return None;
    }
    let channel_config = (h[2] & 0x01) << 2 | h[3] >> 6;
    let frame_len =
        usize::from(h[3] & 0x03) << 11 | usize::from(h[4]) << 3 | usize::from(h[5]) >> 5;
    let header_len = if protection_absent { 7 } else { 9 };
    if frame_len <= header_len {
        return None;
    }
    Some(FrameInfo {
        profile,
        sf_index,
        sample_rate: SAMPLE_RATES[usize::from(sf_index)],
        channel_config,
        header_len,
        frame_len,
    })
}

pub struct AdtsDemuxer {
    reader: SeqReader,
    generation: Generation,
    first: FrameInfo,
    announced: bool,
    /// AAC frames emitted so far; each is 1024 samples at the core rate.
    frames_out: u64,
    notes: Vec<String>,
    ended: bool,
    /// Absolute offset of the first frame: what the byte estimate is
    /// measured from.
    first_frame: u64,
    /// Total size where the source states one. Its absence is the seek
    /// gate: a sequential live source serves forward reads only.
    stream_len: Option<u64>,
    /// Bytes per second over the leading frames, 0 where it could not be
    /// measured.
    byte_rate: u64,
}

impl AdtsDemuxer {
    pub fn open(
        mut src: Box<dyn ByteSource>,
        _limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        let stream_len = src.size().map_err(DemuxError::Source)?;
        let mut reader = SeqReader::new(src);
        let mut skipped = 0u64;
        let first = loop {
            let data = reader.peek(9).map_err(DemuxError::Source)?;
            if data.len() < 7 {
                return Err(DemuxError::Unsupported("no ADTS frames found"));
            }
            if let Some(info) = parse_frame_header(data) {
                // Confirm on the next header to defeat false syncs.
                let data = reader
                    .peek(info.frame_len + 9)
                    .map_err(DemuxError::Source)?;
                let confirmed = match data.get(info.frame_len..) {
                    Some(next) if next.len() >= 7 => parse_frame_header(next).is_some(),
                    _ => data.len() >= info.frame_len, // sole frame before EOF
                };
                if confirmed {
                    break info;
                }
            }
            reader.consume(1);
            skipped += 1;
            if skipped > RESYNC_CAP {
                return Err(DemuxError::Unsupported("no ADTS frames found"));
            }
        };
        if first.channel_config == 0 || first.channel_config > 6 {
            // 0 = layout in an in-band PCE; >6 is wider than the managed
            // splitter maps. Same screen as the MP4 lane.
            return Err(DemuxError::Unsupported(
                "ADTS channel configuration outside 1..=6",
            ));
        }
        // number_of_raw_data_blocks_in_frame: nonzero packs several AAC
        // frames into one ADTS frame and breaks per-AU timing; encoders
        // do not emit it in practice.
        let head = reader.peek(7).map_err(DemuxError::Source)?;
        if head.len() >= 7 && head[6] & 0x03 != 0 {
            return Err(DemuxError::Unsupported(
                "ADTS with multiple raw data blocks per frame",
            ));
        }
        // Average the leading frames rather than trust the first: a VBR
        // encoder's opening frame is not the file's rate, and both the
        // duration and the seek rest on this one number.
        let mut measured = 0usize;
        let mut bytes = 0u64;
        let mut at = 0usize;
        while measured < RATE_SAMPLE_FRAMES {
            let data = reader.peek(at + 9).map_err(DemuxError::Source)?;
            let Some(info) = data.get(at..).and_then(parse_frame_header) else {
                break;
            };
            if info.sf_index != first.sf_index || info.channel_config != first.channel_config {
                break;
            }
            at += info.frame_len;
            bytes += info.frame_len as u64;
            measured += 1;
        }
        let byte_rate = if measured > 0 {
            bytes * u64::from(first.sample_rate) / (measured as u64 * 1_024)
        } else {
            0
        };

        Ok(Self {
            first_frame: reader.pos(),
            reader,
            generation,
            first,
            announced: false,
            frames_out: 0,
            notes: Vec::new(),
            ended: false,
            stream_len,
            byte_rate,
        })
    }

    fn pts_of(&self, frames: u64) -> MediaTime {
        MediaTime::from_micros(
            (frames as i64).saturating_mul(1_024 * 1_000_000) / i64::from(self.first.sample_rate),
        )
    }

    /// The first frame at or after `from` that matches the stream and is
    /// confirmed by the header the next frame starts with. A seek landing
    /// needs that standard because it starts reading mid-file, where the
    /// forward walk never does. Leaves the read position on the frame
    /// it found; `None` = none before the source ended.
    fn confirmed_frame_at_or_after(&mut self, from: u64) -> Result<Option<u64>, DemuxError> {
        self.reader.reposition(from);
        let mut skipped = 0u64;
        loop {
            let at = self.reader.pos();
            let candidate = {
                let data = self.reader.peek(9).map_err(DemuxError::Source)?;
                if data.len() < 7 {
                    return Ok(None);
                }
                parse_frame_header(data)
            };
            if let Some(info) = candidate
                && info.sf_index == self.first.sf_index
                && info.channel_config == self.first.channel_config
            {
                let data = self
                    .reader
                    .peek(info.frame_len + 9)
                    .map_err(DemuxError::Source)?;
                let confirmed = match data.get(info.frame_len..) {
                    Some(next) if next.len() >= 7 => parse_frame_header(next).is_some(),
                    _ => data.len() >= info.frame_len, // final frame before EOF
                };
                if confirmed {
                    return Ok(Some(at));
                }
            }
            self.reader.consume(1);
            skipped += 1;
            if skipped > RESYNC_CAP {
                return Ok(None);
            }
        }
    }

    /// The 2-byte explicit AudioSpecificConfig the MF adapter contract
    /// wants: AOT, frequency index, channel configuration.
    fn asc(&self) -> Vec<u8> {
        let aot = self.first.profile + 1;
        vec![
            aot << 3 | self.first.sf_index >> 1,
            (self.first.sf_index & 1) << 7 | self.first.channel_config << 3,
        ]
    }
}

impl Demuxer for AdtsDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if !self.announced {
            self.announced = true;
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec: AudioCodec::Aac,
                    sample_rate: self.first.sample_rate,
                    channels: u32::from(self.first.channel_config),
                    codec_private: self.asc(),
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }

        let mut skipped = 0u64;
        loop {
            let data = self.reader.peek(9).map_err(DemuxError::Source)?;
            if data.len() < 7 {
                self.ended = true;
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }
            let info = match parse_frame_header(data) {
                Some(info)
                    if info.sf_index == self.first.sf_index
                        && info.channel_config == self.first.channel_config =>
                {
                    info
                }
                _ => {
                    self.reader.consume(1);
                    skipped += 1;
                    if skipped > RESYNC_CAP {
                        return Err(DemuxError::Cap("ADTS resync distance"));
                    }
                    continue;
                }
            };
            let data = self
                .reader
                .peek(info.frame_len)
                .map_err(DemuxError::Source)?;
            if data.len() < info.frame_len {
                self.notes
                    .push("dropped a truncated final ADTS frame".into());
                self.ended = true;
                return Ok(StreamEvent::Eos(EosReason::Natural));
            }
            let payload = data[info.header_len..info.frame_len].to_vec();
            self.reader.consume(info.frame_len);
            let pts = self.pts_of(self.frames_out);
            self.frames_out += 1;
            return Ok(StreamEvent::Au(Au {
                track: TRACK,
                data: payload,
                pts,
                dts: pts,
                key: true,
                generation: self.generation,
            }));
        }
    }

    fn seek(&mut self, target: MediaTime, generation: Generation) -> Result<MediaTime, DemuxError> {
        let Some(stream_len) = self.stream_len.filter(|_| self.byte_rate > 0) else {
            return Err(DemuxError::Unsupported("seek on a streaming ADTS source"));
        };
        let micros = target.as_micros().max(0) as u64;
        let offset =
            (self.first_frame + micros.saturating_mul(self.byte_rate) / 1_000_000).min(stream_len);

        // The estimate lands anywhere inside a frame, and a payload false
        // sync taken for a frame start desynchronises everything after it,
        // so the walk restarts on a confirmed header rather than the first
        // plausible one. Past the end there is none, and the walk ends.
        let landing = self.confirmed_frame_at_or_after(offset)?.unwrap_or(offset);
        if self.reader.pos() != landing {
            self.reader.reposition(landing);
        }
        self.generation = generation;
        // Frames carry no timestamp of their own, so the timeline is
        // re-anchored on the request. Without this the pts would keep
        // counting from the frames emitted before the seek and run
        // backwards.
        self.frames_out =
            micros.saturating_mul(u64::from(self.first.sample_rate)) / (1_024 * 1_000_000);
        self.ended = false;
        Ok(self.pts_of(self.frames_out))
    }

    fn duration(&self) -> Option<MediaTime> {
        // Estimated from the byte rate, and gated on the same answer the
        // seek is: a duration implies a working seek bar.
        let len = self.stream_len?;
        (self.byte_rate > 0).then(|| {
            MediaTime::from_micros(
                (len.saturating_sub(self.first_frame) as i64).saturating_mul(1_000_000)
                    / self.byte_rate as i64,
            )
        })
    }

    fn audio_track(&self) -> Option<TrackId> {
        Some(TRACK)
    }

    fn take_notes(&mut self) -> Vec<String> {
        std::mem::take(&mut self.notes)
    }
}
