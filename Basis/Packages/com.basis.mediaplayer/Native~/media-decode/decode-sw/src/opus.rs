//! Opus decode on libopus's multistream decoder, in-process on every
//! platform. Mapping families 0 (mono/stereo), 1 (up to 7.1, in Vorbis
//! order) and 255 (discrete channels) are decoded; RFC 7845 §5.1.1.
//!
//! Pre-skip is a timestamp concern resolved before the ring: the
//! demuxer shifts pts by the codec delay so priming samples arrive with
//! negative pts and the engine's origin drop removes them. The decoder
//! itself is stateless about it.

use std::collections::VecDeque;
use std::ffi::{CStr, c_int};
use std::ptr::NonNull;

use audiopus_sys::{
    OPUS_OK, OPUS_RESET_STATE, OPUS_SET_GAIN_REQUEST, OpusMSDecoder, opus_multistream_decode_float,
    opus_multistream_decoder_create, opus_multistream_decoder_ctl,
    opus_multistream_decoder_destroy, opus_strerror,
};
use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

/// Opus always decodes at 48 kHz; the OpusHead input rate is informational.
const OPUS_RATE: u32 = 48_000;
/// Longest packet libopus will hand back: 120 ms at 48 kHz.
const MAX_FRAMES: usize = 5760;
const READY_CAP: usize = 64;
/// Family 1 defines layouts up to 7.1; family 255 is held to the same.
const MAX_CHANNELS: u32 = 8;

/// For each WAVE output channel, its index in family 1's Vorbis order,
/// by channel count (RFC 7845 §5.1.1.2). Mono, stereo and quad already
/// coincide.
const VORBIS_TO_WAVE: [&[usize]; 9] = [
    &[],
    &[0],
    &[0, 1],
    &[0, 2, 1],
    &[0, 1, 2, 3],
    &[0, 2, 1, 3, 4],
    &[0, 2, 1, 5, 3, 4],
    &[0, 2, 1, 6, 5, 3, 4],
    &[0, 2, 1, 7, 5, 6, 3, 4],
];

/// The identification header carried as codec private data (RFC 7845 §5.1).
pub struct OpusHead {
    pub channels: u32,
    pub pre_skip: u16,
    /// Q7.8 dB, applied by the decoder.
    pub output_gain: i16,
    pub mapping_family: u8,
    pub streams: u8,
    pub coupled_streams: u8,
    /// Output channel to decoded stream channel, already in WAVE order.
    pub mapping: Vec<u8>,
}

impl OpusHead {
    pub fn parse(private: &[u8]) -> Result<Self, DecodeError> {
        if private.len() < 19 || &private[..8] != b"OpusHead" {
            return Err(DecodeError("missing OpusHead".into()));
        }
        let channels = u32::from(private[9]);
        let mapping_family = private[18];
        let (streams, coupled_streams, mapping) = match mapping_family {
            0 => {
                if !(1..=2).contains(&channels) {
                    return Err(DecodeError(format!(
                        "Opus mapping family 0 with {channels} channels (mono/stereo only)"
                    )));
                }
                (1, private[9] - 1, (0..private[9]).collect())
            }
            1 | 255 => {
                if !(1..=MAX_CHANNELS).contains(&channels) {
                    return Err(DecodeError(format!(
                        "Opus mapping family {mapping_family} with {channels} channels unsupported (up to {MAX_CHANNELS})"
                    )));
                }
                let table = private.get(21..21 + channels as usize).ok_or_else(|| {
                    DecodeError("OpusHead channel mapping table truncated".into())
                })?;
                let mapping = if mapping_family == 1 {
                    VORBIS_TO_WAVE[channels as usize]
                        .iter()
                        .map(|&vorbis| table[vorbis])
                        .collect()
                } else {
                    table.to_vec()
                };
                (private[19], private[20], mapping)
            }
            family => {
                return Err(DecodeError(format!(
                    "Opus mapping family {family} unsupported"
                )));
            }
        };
        Ok(Self {
            channels,
            pre_skip: u16::from_le_bytes([private[10], private[11]]),
            output_gain: i16::from_le_bytes([private[16], private[17]]),
            mapping_family,
            streams,
            coupled_streams,
            mapping,
        })
    }

    /// Pre-skip as a duration at the 48 kHz decode rate.
    pub fn pre_skip_us(&self) -> i64 {
        i64::from(self.pre_skip) * 1_000_000 / i64::from(OPUS_RATE)
    }
}

fn opus_error(context: &str, code: c_int) -> DecodeError {
    // SAFETY: opus_strerror returns a static NUL-terminated string for
    // any code.
    let text = unsafe { CStr::from_ptr(opus_strerror(code)) };
    DecodeError(format!("{context}: {}", text.to_string_lossy()))
}

pub struct OpusDecoder {
    inner: NonNull<OpusMSDecoder>,
    channels: u32,
    ready: VecDeque<PcmChunk>,
}

impl OpusDecoder {
    /// `codec_private` is the OpusHead as Matroska/Ogg store it.
    pub fn new(codec_private: &[u8]) -> Result<Self, DecodeError> {
        let head = OpusHead::parse(codec_private)?;
        let mut error: c_int = OPUS_OK;
        // SAFETY: `mapping` holds exactly `channels` entries and outlives
        // the call; libopus validates the layout against the stream counts
        // and copies it.
        let raw = unsafe {
            opus_multistream_decoder_create(
                OPUS_RATE as i32,
                head.channels as c_int,
                c_int::from(head.streams),
                c_int::from(head.coupled_streams),
                head.mapping.as_ptr(),
                &mut error,
            )
        };
        let inner = match NonNull::new(raw) {
            Some(inner) if error == OPUS_OK => inner,
            _ => return Err(opus_error("libopus decoder", error)),
        };
        let decoder = Self {
            inner,
            channels: head.channels,
            ready: VecDeque::new(),
        };
        if head.output_gain != 0 {
            // SAFETY: the decoder is live; OPUS_SET_GAIN takes one int.
            let result = unsafe {
                opus_multistream_decoder_ctl(
                    decoder.inner.as_ptr(),
                    OPUS_SET_GAIN_REQUEST,
                    c_int::from(head.output_gain),
                )
            };
            if result != OPUS_OK {
                return Err(opus_error("Opus output gain", result));
            }
        }
        Ok(decoder)
    }
}

impl Drop for OpusDecoder {
    fn drop(&mut self) {
        // SAFETY: created by opus_multistream_decoder_create, destroyed once.
        unsafe { opus_multistream_decoder_destroy(self.inner.as_ptr()) }
    }
}

impl AudioDecoder for OpusDecoder {
    fn output_format(&self) -> (u32, u32) {
        (OPUS_RATE, self.channels)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        let len = i32::try_from(au.len())
            .map_err(|_| DecodeError(format!("Opus packet of {} bytes", au.len())))?;
        let mut data = vec![0.0f32; MAX_FRAMES * self.channels as usize];
        // SAFETY: `data` has room for MAX_FRAMES frames of every channel;
        // `au` is valid for `len` bytes (libopus treats an empty packet as
        // loss and conceals it).
        let frames = unsafe {
            opus_multistream_decode_float(
                self.inner.as_ptr(),
                au.as_ptr(),
                len,
                data.as_mut_ptr(),
                MAX_FRAMES as c_int,
                0,
            )
        };
        if frames < 0 {
            return Err(opus_error("Opus decode", frames));
        }
        data.truncate(frames as usize * self.channels as usize);
        self.ready.push_back(PcmChunk {
            sample_rate: OPUS_RATE,
            channels: self.channels,
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
        // SAFETY: the decoder is live; OPUS_RESET_STATE takes no argument.
        let result = unsafe { opus_multistream_decoder_ctl(self.inner.as_ptr(), OPUS_RESET_STATE) };
        if result == OPUS_OK {
            Ok(())
        } else {
            Err(opus_error("Opus reset", result))
        }
    }
}
