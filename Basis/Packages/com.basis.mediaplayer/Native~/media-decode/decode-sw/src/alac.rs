//! Apple Lossless decode on the `alac` crate, in-process: Media
//! Foundation and AudioToolbox carry it, MediaCodec does not, so one
//! software decoder keeps every platform the same.
//!
//! Codec private data is the magic cookie (the ALACSpecificConfig, with or
//! without the `frma`/`alac` atoms older encoders wrap it in). Each AU is
//! one ALAC packet. The crate is built without its `mp4parse` feature,
//! which is MPL licensed; the MP4 demuxer finds the cookie itself.
//!
//! The crate asserts on its own invariants; a hostile packet that trips
//! one becomes a typed error here rather than a panic on the decode
//! thread.

use std::collections::VecDeque;
use std::panic::{AssertUnwindSafe, catch_unwind};

use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

const READY_CAP: usize = 64;
const MAX_CHANNELS: u8 = 8;
/// Frames per packet the decoder will allocate for. Apple's encoder uses
/// 4096; the field is 32 bits, so a hostile cookie could otherwise ask for
/// gigabytes.
const MAX_FRAME_LENGTH: u32 = 65_536;

/// ALAC element order to WAVE order, by channel count: for each decoded
/// channel, its WAVE output slot. ALAC leads with the centre for three
/// channels and up (Apple's ALAC channel layout tags).
const ALAC_TO_WAVE: [&[usize]; 9] = [
    &[],
    &[0],
    &[0, 1],
    &[2, 0, 1],
    &[2, 0, 1, 3],
    &[2, 0, 1, 3, 4],
    &[2, 0, 1, 4, 5, 3],
    &[2, 0, 1, 4, 5, 6, 3],
    &[2, 6, 7, 0, 1, 4, 5, 3],
];

pub struct AlacDecoder {
    decoder: alac::Decoder,
    sample_rate: u32,
    channels: usize,
    buffer: Vec<i32>,
    ready: VecDeque<PcmChunk>,
}

impl AlacDecoder {
    pub fn new(codec_private: &[u8]) -> Result<Self, DecodeError> {
        let info = alac::StreamInfo::from_cookie(codec_private)
            .map_err(|e| DecodeError(format!("ALAC magic cookie: {e}")))?;
        let channels = info.channels();
        if channels == 0 || channels > MAX_CHANNELS {
            return Err(DecodeError(format!(
                "ALAC channel count {channels} unsupported"
            )));
        }
        if !matches!(info.bit_depth(), 16 | 20 | 24 | 32) {
            return Err(DecodeError(format!(
                "ALAC bit depth {} unsupported",
                info.bit_depth()
            )));
        }
        if info.sample_rate() == 0 {
            return Err(DecodeError("ALAC sample rate 0".into()));
        }
        let frame_length = info.max_frames_per_packet();
        if frame_length == 0 || frame_length > MAX_FRAME_LENGTH {
            return Err(DecodeError(format!(
                "ALAC frame length {frame_length} unsupported"
            )));
        }
        Ok(Self {
            sample_rate: info.sample_rate(),
            channels: usize::from(channels),
            buffer: vec![0; info.max_samples_per_packet() as usize],
            decoder: alac::Decoder::new(info),
            ready: VecDeque::new(),
        })
    }
}

impl AudioDecoder for AlacDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.sample_rate, self.channels as u32)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        let (decoder, buffer) = (&mut self.decoder, &mut self.buffer);
        let decoded = catch_unwind(AssertUnwindSafe(|| {
            decoder.decode_packet::<i32>(au, buffer).map(<[i32]>::len)
        }));
        let len = match decoded {
            Ok(Ok(len)) => len,
            Ok(Err(e)) => return Err(DecodeError(format!("ALAC packet: {e}"))),
            Err(_) => return Err(DecodeError("ALAC packet is malformed".into())),
        };
        let channels = self.channels;
        if len == 0 || len % channels != 0 {
            return Ok(SubmitOutcome::Accepted);
        }
        // Samples come left-aligned in 32 bits whatever the coded depth.
        const SCALE: f32 = 1.0 / 2_147_483_648.0;
        let map = ALAC_TO_WAVE[channels];
        let mut data = vec![0f32; len];
        for (src, dst) in self.buffer[..len]
            .chunks_exact(channels)
            .zip(data.chunks_exact_mut(channels))
        {
            for (c, &slot) in map.iter().enumerate() {
                dst[slot] = src[c] as f32 * SCALE;
            }
        }
        self.ready.push_back(PcmChunk {
            sample_rate: self.sample_rate,
            channels: channels as u32,
            pts_us,
            data,
        });
        Ok(SubmitOutcome::Accepted)
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

#[cfg(test)]
mod tests {
    use super::*;

    fn cookie(frame_length: u32, bits: u8, channels: u8, rate: u32) -> Vec<u8> {
        let mut c = frame_length.to_be_bytes().to_vec();
        c.extend_from_slice(&[0, bits, 40, 10, 14, channels]);
        c.extend_from_slice(&255u16.to_be_bytes());
        c.extend_from_slice(&0u32.to_be_bytes());
        c.extend_from_slice(&0u32.to_be_bytes());
        c.extend_from_slice(&rate.to_be_bytes());
        c
    }

    #[test]
    fn cookies_it_cannot_serve_are_refused() {
        assert!(AlacDecoder::new(&cookie(4096, 16, 2, 44_100)).is_ok());
        assert!(AlacDecoder::new(&cookie(4096, 16, 9, 44_100)).is_err());
        assert!(AlacDecoder::new(&cookie(4096, 12, 2, 44_100)).is_err());
        assert!(AlacDecoder::new(&cookie(u32::MAX, 16, 2, 44_100)).is_err());
        assert!(AlacDecoder::new(&cookie(4096, 16, 2, 0)).is_err());
        assert!(AlacDecoder::new(&[0; 10]).is_err());
    }

    /// An uncompressed (escape) stereo packet of two frames: the element
    /// header, then the samples verbatim. Exercises the decode and the
    /// scale without an encoder.
    #[test]
    fn decodes_an_escape_packet() {
        let mut d = AlacDecoder::new(&cookie(4096, 16, 2, 44_100)).expect("decoder");
        // Bits: tag CPE (3) = 001, element instance (4) = 0000, unused
        // (12) = 0, partial-frame flag (1) = 1, shift (2) = 00, escape
        // (1) = 1, then a 32-bit frame count of 2, then 16-bit samples
        // L R L R, then END (3) = 111.
        let mut bits = String::new();
        bits += "001";
        bits += "0000";
        bits += &"0".repeat(12);
        bits += "1";
        bits += "00";
        bits += "1";
        bits += &format!("{:032b}", 2u32);
        for s in [16_384i16, -16_384, 0, 32_767] {
            bits += &format!("{:016b}", s as u16);
        }
        bits += "111";
        while !bits.len().is_multiple_of(8) {
            bits += "0";
        }
        let packet: Vec<u8> = bits
            .as_bytes()
            .chunks(8)
            .map(|b| u8::from_str_radix(std::str::from_utf8(b).unwrap(), 2).unwrap())
            .collect();
        assert_eq!(
            d.submit(&packet, 1234).expect("submit"),
            SubmitOutcome::Accepted
        );
        let chunk = d.try_output().expect("output").expect("a chunk");
        assert_eq!(chunk.pts_us, 1234);
        assert_eq!(chunk.data, vec![0.5, -0.5, 0.0, 32_767.0 / 32_768.0]);
    }

    #[test]
    fn rubbish_packets_never_panic() {
        let mut d = AlacDecoder::new(&cookie(4096, 24, 6, 48_000)).expect("decoder");
        for len in 0..256usize {
            let bytes: Vec<u8> = (0..len).map(|i| (i * 37 + len) as u8).collect();
            let _ = d.submit(&bytes, 0);
            let _ = d.try_output();
        }
    }
}
