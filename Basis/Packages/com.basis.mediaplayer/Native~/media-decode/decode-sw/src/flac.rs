//! FLAC decode on claxon, in-process. The platform route is avoided
//! because the Media Foundation FLAC MFT hangs.
//!
//! Each submitted AU holds whole FLAC frames, normally one (Matroska
//! stores frames; the raw-file demuxer emits whole frames), decoded
//! synchronously into a small output queue. With no codec latency, drain
//! is a no-op.

use std::collections::VecDeque;
use std::io::Cursor;

use claxon::frame::FrameReader;
use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

/// Decoded chunks the queue holds before `submit` pushes back; the release
/// schedule bounds arrivals well below this in practice. An AU with more
/// FLAC frames than it holds is refused; one that fits only once the queue
/// drains is pushed back.
const READY_CAP: usize = 64;
/// Sample frames one AU may decode to: four blocks of the largest size
/// FLAC allows. A frame of constant subframes is a few dozen bytes and
/// decodes to a full block, so the AU's size bounds nothing.
const MAX_AU_FRAMES: u32 = 4 * 65_535;

pub struct FlacDecoder {
    sample_rate: u32,
    channels: u32,
    /// Normalisation for the stated bit depth: samples arrive as integers
    /// scaled to `bits_per_sample`.
    scale: f32,
    ready: VecDeque<PcmChunk>,
    /// claxon's decode buffer, recycled across frames.
    buffer: Vec<i32>,
}

impl FlacDecoder {
    /// `codec_private` is the FLAC stream header (the `fLaC` marker plus
    /// metadata blocks) as Matroska stores it and the raw demuxer forwards
    /// it; STREAMINFO inside it is authoritative for format and bit depth.
    pub fn new(codec_private: &[u8]) -> Result<Self, DecodeError> {
        let reader = claxon::FlacReader::new(Cursor::new(codec_private))
            .map_err(|e| DecodeError(format!("FLAC stream header: {e}")))?;
        let info = reader.streaminfo();
        if info.channels == 0 || info.channels > 8 {
            return Err(DecodeError(format!(
                "FLAC channel count {} unsupported",
                info.channels
            )));
        }
        if info.sample_rate == 0 {
            return Err(DecodeError("FLAC sample rate 0".into()));
        }
        if info.bits_per_sample == 0 || info.bits_per_sample > 32 {
            return Err(DecodeError(format!(
                "FLAC bit depth {} unsupported",
                info.bits_per_sample
            )));
        }
        Ok(Self {
            sample_rate: info.sample_rate,
            channels: info.channels,
            scale: 1.0 / (1i64 << (info.bits_per_sample - 1)) as f32,
            ready: VecDeque::new(),
            buffer: Vec::new(),
        })
    }
}

impl AudioDecoder for FlacDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.sample_rate, self.channels)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        let mut frames = FrameReader::new(Cursor::new(au));
        let mut pts_us = pts_us;
        let mut decoded = 0u32;
        // Held until the whole AU decodes, so a refused one leaves nothing
        // queued.
        let mut staged = Vec::new();
        loop {
            match frames.read_next_or_eof(std::mem::take(&mut self.buffer)) {
                Ok(Some(block)) => {
                    let frames_n = block.duration();
                    let channels = block.channels();
                    if channels != self.channels {
                        return Err(DecodeError(format!(
                            "FLAC frame channel count {channels} != stream {}",
                            self.channels
                        )));
                    }
                    decoded = decoded.saturating_add(frames_n);
                    if decoded > MAX_AU_FRAMES {
                        return Err(DecodeError(format!(
                            "FLAC access unit decodes to more than {MAX_AU_FRAMES} sample frames"
                        )));
                    }
                    if staged.len() >= READY_CAP {
                        return Err(DecodeError(format!(
                            "FLAC access unit holds more frames than the {READY_CAP}-chunk queue"
                        )));
                    }
                    if self.ready.len() + staged.len() >= READY_CAP {
                        self.buffer = block.into_buffer();
                        return Ok(SubmitOutcome::NotAccepting);
                    }
                    let mut data = Vec::with_capacity((frames_n * channels) as usize);
                    for i in 0..frames_n {
                        for ch in 0..channels {
                            data.push(block.channel(ch)[i as usize] as f32 * self.scale);
                        }
                    }
                    staged.push(PcmChunk {
                        sample_rate: self.sample_rate,
                        channels,
                        pts_us,
                        data,
                    });
                    pts_us += i64::from(frames_n) * 1_000_000 / i64::from(self.sample_rate);
                    self.buffer = block.into_buffer();
                }
                Ok(None) => {
                    self.ready.extend(staged);
                    return Ok(SubmitOutcome::Accepted);
                }
                Err(e) => return Err(DecodeError(format!("FLAC frame: {e}"))),
            }
        }
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        Ok(self.ready.pop_front())
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.ready.clear();
        Ok(())
    }
}
