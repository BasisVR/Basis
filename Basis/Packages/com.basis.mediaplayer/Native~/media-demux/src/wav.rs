//! RIFF/WAVE demuxer: walks the chunk list for `fmt ` and `data`,
//! announces the PCM format, then serves the data chunk as ~20 ms blocks of
//! whole interleaved frames with a sample-counted timeline. Conversion to
//! the ring's float samples is the PCM adapter's job, as it is for the
//! Blu-ray LPCM lane.
//!
//! Supported: `WAVE_FORMAT_PCM`, `WAVE_FORMAT_IEEE_FLOAT` and
//! `WAVE_FORMAT_EXTENSIBLE` wrapping either; 8-, 16-, 24- or 32-bit
//! integer, 32- or 64-bit float, 1-8 channels, 8-192 kHz. Anything else
//! refuses typed: a WAV is audio-only, so unplayable audio is an
//! unplayable file.
//!
//! The block server is shared with the AIFF demuxer, which differs only in
//! how its header states the same layout.

use media_clock::{Generation, MediaTime};

use crate::source::SeqReader;
use crate::{
    Au, AudioCodec, ByteSource, DemuxError, DemuxLimits, Demuxer, EosReason, Format, StreamEvent,
    TrackId,
};

const TRACK: TrackId = TrackId(1);
/// `WAVE_FORMAT_EXTENSIBLE`; the real tag lives in the SubFormat GUID.
const WAVE_FORMAT_EXTENSIBLE: u16 = 0xFFFE;
const WAVE_FORMAT_PCM: u16 = 1;
const WAVE_FORMAT_IEEE_FLOAT: u16 = 3;

/// The PCM adapter's sample-type codes (decode-sw `pcm.rs`): 1 and 3 are
/// the Blu-ray 16- and 24-bit codes, the rest the file lanes' own.
pub(crate) const PCM_16: u8 = 1;
pub(crate) const PCM_24: u8 = 3;
pub(crate) const PCM_32: u8 = 4;
pub(crate) const PCM_U8: u8 = 5;
pub(crate) const PCM_S8: u8 = 6;
pub(crate) const PCM_F32: u8 = 7;
pub(crate) const PCM_F64: u8 = 8;

/// The rates the file lanes accept.
pub(crate) const PCM_RATES: std::ops::RangeInclusive<u32> = 8_000..=192_000;

fn le16(p: &[u8]) -> u16 {
    u16::from(p[0]) | (u16::from(p[1]) << 8)
}

fn le32(p: &[u8]) -> u32 {
    u32::from(p[0]) | (u32::from(p[1]) << 8) | (u32::from(p[2]) << 16) | (u32::from(p[3]) << 24)
}

/// Interleaved PCM as a file header states it.
pub(crate) struct PcmLayout {
    pub sample_rate: u32,
    pub channels: u32,
    /// One of the `PCM_*` codes.
    pub code: u8,
    pub little_endian: bool,
    /// Bytes per interleaved frame.
    pub frame_bytes: usize,
}

pub struct WavDemuxer {
    reader: SeqReader,
    generation: Generation,
    sample_rate: u32,
    channels: u32,
    code: u8,
    little_endian: bool,
    /// Bytes per interleaved frame; equals the `fmt ` block alignment.
    frame_bytes: usize,
    byte_rate: u32,
    /// Absolute offset of the first PCM byte.
    data_start: u64,
    /// Stated data length, when the header states a usable one. A streaming
    /// capture writes 0 or 0xFFFFFFFF, and a truncated file overstates it,
    /// so this bounds the read but EOF still ends the stream.
    data_len: Option<u64>,
    /// Whether the source can serve the backwards reads a seek needs.
    seekable: bool,
    frames_sent: u64,
    /// Bytes of the data chunk already served.
    consumed: u64,
    announced: bool,
    ended: bool,
}

impl WavDemuxer {
    pub fn open(
        mut src: Box<dyn ByteSource>,
        limits: DemuxLimits,
        generation: Generation,
    ) -> Result<Self, DemuxError> {
        // A size answer is also the seek gate: the sequential live source
        // serves forward reads only, and a duration would put a seek bar on
        // a stream that refuses it.
        let seekable = src.size().map_err(DemuxError::Source)?.is_some();
        let mut reader = SeqReader::new(src);

        let head = reader.peek(12).map_err(DemuxError::Source)?;
        if head.len() < 12 || &head[..4] != b"RIFF" || &head[8..12] != b"WAVE" {
            return Err(DemuxError::Parse("not a RIFF/WAVE stream".into()));
        }
        reader.consume(12);

        let mut fmt: Option<(u16, u32, u32, u16, u16)> = None;
        let mut metadata = 0u64;
        let (data_len, layout) = loop {
            // Header bytes count too, or a chain of zero-length chunks
            // would walk forever without moving the budget.
            metadata = metadata.saturating_add(8);
            if metadata > limits.max_metadata_bytes {
                return Err(DemuxError::Cap("WAV chunk walk"));
            }
            let header = reader.peek(8).map_err(DemuxError::Source)?;
            if header.len() < 8 {
                return Err(DemuxError::Parse("WAV ended before its data chunk".into()));
            }
            let id: [u8; 4] = header[..4].try_into().expect("sliced 4");
            let size = u64::from(le32(&header[4..8]));
            reader.consume(8);
            // RIFF pads odd-sized chunks to a two-byte boundary.
            let padded = size + (size & 1);

            if &id == b"fmt " {
                let take = size.min(40) as usize;
                let body = reader.peek(take).map_err(DemuxError::Source)?;
                if body.len() < take || take < 16 {
                    return Err(DemuxError::Parse("WAV fmt chunk truncated".into()));
                }
                let f = &body[..take];
                let mut tag = le16(f);
                let channels = u32::from(le16(&f[2..]));
                let sample_rate = le32(&f[4..]);
                let block_align = le16(&f[12..]);
                // The container width: samples are left-justified in it, so
                // a narrower valid-bits field in WAVE_FORMAT_EXTENSIBLE
                // changes nothing about how they are read. The channel mask
                // at f[20..24] needs no remap either: WAVE order *is* the
                // mask's canonical bit order, which is what the ring
                // already expects.
                let bits = le16(&f[14..]);
                if tag == WAVE_FORMAT_EXTENSIBLE && take >= 40 {
                    tag = le16(&f[24..]);
                }
                fmt = Some((tag, channels, sample_rate, block_align, bits));
                let past = reader.pos() + padded;
                reader.seek_to(past).map_err(DemuxError::Source)?;
                continue;
            }

            if &id != b"data" {
                // A hostile file can chain chunk headers forever; the same
                // metadata budget the box walks answer to bounds it.
                metadata = metadata.saturating_add(padded);
                if metadata > limits.max_metadata_bytes {
                    return Err(DemuxError::Cap("WAV chunk walk"));
                }
                let past = reader.pos() + padded;
                reader.seek_to(past).map_err(DemuxError::Source)?;
                continue;
            }

            let Some((tag, channels, sample_rate, block_align, bits)) = fmt else {
                return Err(DemuxError::Parse("WAV data chunk before fmt".into()));
            };
            let code = match (tag, bits) {
                (WAVE_FORMAT_PCM, 8) => PCM_U8,
                (WAVE_FORMAT_PCM, 16) => PCM_16,
                (WAVE_FORMAT_PCM, 24) => PCM_24,
                (WAVE_FORMAT_PCM, 32) => PCM_32,
                (WAVE_FORMAT_IEEE_FLOAT, 32) => PCM_F32,
                (WAVE_FORMAT_IEEE_FLOAT, 64) => PCM_F64,
                (WAVE_FORMAT_PCM | WAVE_FORMAT_IEEE_FLOAT, _) => {
                    return Err(DemuxError::Unsupported(
                        "WAV sample width unsupported (need 8/16/24/32-bit integer or 32/64-bit float)",
                    ));
                }
                _ => {
                    return Err(DemuxError::Unsupported(
                        "WAV: only uncompressed integer or float PCM is supported",
                    ));
                }
            };
            if !(1..=8).contains(&channels)
                || !PCM_RATES.contains(&sample_rate)
                || u32::from(block_align) != channels * u32::from(bits / 8)
            {
                return Err(DemuxError::Unsupported(
                    "WAV format unsupported (need 1-8 channels at 8-192 kHz)",
                ));
            }
            // 0 and 0xFFFFFFFF both mean "unknown" from a streaming writer.
            let bounded = size != 0 && size != u64::from(u32::MAX);
            break (
                bounded.then_some(size),
                PcmLayout {
                    sample_rate,
                    channels,
                    code,
                    little_endian: true,
                    frame_bytes: usize::from(block_align),
                },
            );
        };

        Ok(Self::serve(reader, generation, layout, data_len, seekable))
    }

    /// Serve interleaved PCM from where `reader` stands, which is the
    /// first sample byte.
    pub(crate) fn serve(
        reader: SeqReader,
        generation: Generation,
        layout: PcmLayout,
        data_len: Option<u64>,
        seekable: bool,
    ) -> Self {
        Self {
            data_start: reader.pos(),
            reader,
            generation,
            sample_rate: layout.sample_rate,
            channels: layout.channels,
            code: layout.code,
            little_endian: layout.little_endian,
            frame_bytes: layout.frame_bytes,
            // Derived rather than read: a header's stated byte rate is
            // advisory and sometimes wrong, and the seek maths need it right.
            byte_rate: layout.sample_rate.saturating_mul(layout.frame_bytes as u32),
            data_len,
            seekable,
            frames_sent: 0,
            consumed: 0,
            announced: false,
            ended: false,
        }
    }

    fn pts_of(&self, frames: u64) -> MediaTime {
        MediaTime::from_micros(
            (frames as i64).saturating_mul(1_000_000) / i64::from(self.sample_rate),
        )
    }
}

impl Demuxer for WavDemuxer {
    fn next_event(&mut self) -> Result<StreamEvent, DemuxError> {
        if !self.announced {
            self.announced = true;
            return Ok(StreamEvent::Format(
                TRACK,
                Format::Audio {
                    codec: AudioCodec::Pcm,
                    sample_rate: self.sample_rate,
                    channels: self.channels,
                    // Channel assignment 0 = already in WAVE order; the
                    // sample-type code; flags bit 0 marks the samples
                    // little-endian.
                    codec_private: vec![0, self.code, u8::from(self.little_endian)],
                },
            ));
        }
        if self.ended {
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }

        // ~20 ms of whole frames per AU.
        let chunk_frames = (self.sample_rate as usize / 50).max(1);
        let mut want = chunk_frames * self.frame_bytes;
        if let Some(len) = self.data_len {
            want = want.min((len - self.consumed) as usize);
        }
        if want == 0 {
            self.ended = true;
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }

        let data = self.reader.peek(want).map_err(DemuxError::Source)?;
        // Whole interleaved frames only; a torn tail is not a frame.
        let got = data.len().min(want) / self.frame_bytes * self.frame_bytes;
        if got == 0 {
            self.ended = true;
            return Ok(StreamEvent::Eos(EosReason::Natural));
        }
        let data = data[..got].to_vec();
        self.reader.consume(got);

        let pts = self.pts_of(self.frames_sent);
        self.frames_sent += (got / self.frame_bytes) as u64;
        self.consumed += got as u64;
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
        if !self.seekable || self.byte_rate == 0 {
            return Err(DemuxError::Unsupported("seek on a streaming WAV source"));
        }
        let micros = target.as_micros().max(0);
        let mut offset = (i128::from(micros) * i128::from(self.byte_rate) / 1_000_000) as u64;
        if let Some(len) = self.data_len {
            offset = offset.min(len);
        }
        offset -= offset % self.frame_bytes as u64;

        self.reader.reposition(self.data_start + offset);
        self.generation = generation;
        self.frames_sent = offset / self.frame_bytes as u64;
        self.consumed = offset;
        self.ended = false;
        Ok(self.pts_of(self.frames_sent))
    }

    fn duration(&self) -> Option<MediaTime> {
        // A duration implies a working seek bar, so it is gated on the same
        // answer the seek is.
        let len = self.data_len?;
        (self.seekable && self.byte_rate > 0).then(|| {
            MediaTime::from_micros(
                (len as i64).saturating_mul(1_000_000) / i64::from(self.byte_rate),
            )
        })
    }

    fn audio_track(&self) -> Option<TrackId> {
        Some(TRACK)
    }
}
