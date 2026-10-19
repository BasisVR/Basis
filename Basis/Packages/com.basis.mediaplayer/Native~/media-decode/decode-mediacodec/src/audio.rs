//! Audio through MediaCodec: AAC and MP3 decode on the platform decoder.
//! There is no surface; PCM comes back through the codec's output
//! buffers, converted to interleaved f32 for the ring. Float output is
//! requested at configure; the output format's stated encoding decides
//! how each buffer is read, so a codec that ignores the request still
//! decodes correctly through the s16 path.

use std::ffi::CStr;
use std::time::{Duration, Instant};

use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};

use crate::driver::AsyncCodec;
use crate::ffi::*;

/// Drain-tail wait budget, spent in slices (see the video adapter).
const DRAIN_BUDGET: Duration = Duration::from_secs(1);
const DRAIN_SLICE: Duration = Duration::from_millis(20);

/// Bounds on the geometry handed to `AMediaCodec_configure`. The NDK
/// takes `int32_t`, so narrowing the container's `u32` flips the sign
/// above `i32::MAX`, and a stated 3 GHz rate would configure a vendor
/// codec with a negative one. Whether a closed-source codec refuses that
/// or computes buffer geometry from it is unknown, so it is refused here.
/// Both bounds sit above anything a real stream carries.
const MAX_SAMPLE_RATE: u32 = 384_000;
const MAX_CHANNELS: u32 = 64;

#[derive(Debug, Clone, Copy)]
pub enum AudioMime {
    Aac,
    Mp3,
}

impl AudioMime {
    fn as_cstr(self) -> &'static CStr {
        match self {
            AudioMime::Aac => c"audio/mp4a-latm",
            AudioMime::Mp3 => c"audio/mpeg",
        }
    }
}

pub struct McAudioDecoder {
    codec: AsyncCodec,
    /// The format the ring is built for (`output_format()` contract):
    /// as configured; chunks re-state the codec's live values.
    configured: (u32, u32),
    eos_out: bool,
    drain_waited: Duration,
}

impl McAudioDecoder {
    /// `codec_private` is the AAC AudioSpecificConfig (csd-0); MP3 takes
    /// none. The demux layer reconstructs the ASC on raw/TS lanes, so
    /// every AAC lane arrives with one.
    pub fn new(
        mime: AudioMime,
        sample_rate: u32,
        channels: u32,
        codec_private: &[u8],
    ) -> Result<Self, DecodeError> {
        let sample_rate = sample_rate.max(1);
        let channels = channels.max(1);
        if sample_rate > MAX_SAMPLE_RATE || channels > MAX_CHANNELS {
            return Err(DecodeError(format!(
                "implausible audio geometry: {sample_rate} Hz / {channels} channels"
            )));
        }
        // SAFETY: format construction with checked pointers; consumed by
        // AsyncCodec::start.
        unsafe {
            let format = AMediaFormat_new();
            if format.is_null() {
                return Err(DecodeError("AMediaFormat_new failed".into()));
            }
            AMediaFormat_setString(format, c"mime".as_ptr(), mime.as_cstr().as_ptr());
            AMediaFormat_setInt32(format, c"sample-rate".as_ptr(), sample_rate as i32);
            AMediaFormat_setInt32(format, c"channel-count".as_ptr(), channels as i32);
            AMediaFormat_setInt32(format, c"pcm-encoding".as_ptr(), ENCODING_PCM_FLOAT);
            // Some devices' AAC decoders fold multichannel down to stereo
            // unless configured with an output-channel ceiling. Both the
            // generic (API 32+) and the legacy AAC key are set: unknown keys
            // are ignored, and values above the stream's count clamp to it.
            //
            // The ceiling is `MAX_CHANNELS` because a codec honouring a
            // larger request could report a geometry `read_output` refuses.
            // Any value at or above the stream's count defeats the downmix.
            let ceiling = MAX_CHANNELS as i32;
            AMediaFormat_setInt32(format, c"max-output-channel-count".as_ptr(), ceiling);
            AMediaFormat_setInt32(format, c"aac-max-output-channel_count".as_ptr(), ceiling);
            if matches!(mime, AudioMime::Aac) {
                if codec_private.is_empty() {
                    AMediaFormat_delete(format);
                    return Err(DecodeError("AAC without AudioSpecificConfig".into()));
                }
                let codec_private = media_bitstream::strip_inert_sbr(codec_private);
                AMediaFormat_setBuffer(
                    format,
                    c"csd-0".as_ptr(),
                    codec_private.as_ptr().cast(),
                    codec_private.len(),
                );
            }
            let codec = AsyncCodec::start(mime.as_cstr(), format, core::ptr::null_mut())?;
            crate::ffi::alog(&format!("mediacodec audio: {}", codec.name));
            Ok(Self {
                codec,
                configured: (sample_rate, channels),
                eos_out: false,
                drain_waited: Duration::ZERO,
            })
        }
    }

    fn take_ready(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        let Some((index, info)) = self.codec.pop_output(Duration::ZERO) else {
            return Ok(None);
        };
        let read = self.read_output(index, &info);
        // The index goes back whatever the read returned: a codec whose
        // output pool is not returned stalls, which reads as a hang rather
        // than as the error that caused it. A release failure takes
        // precedence over a read failure: it says the codec is in trouble
        // rather than this frame.
        let released = self.codec.release_output(index, false).map_err(|e| {
            let after = read
                .as_ref()
                .err()
                .map(|r| format!("; the read of it failed too ({})", r.0))
                .unwrap_or_default();
            DecodeError(format!("{}{after}", e.0))
        })?;
        if !released {
            // From before the flush: whatever was read belongs to the old
            // timeline.
            return Ok(None);
        }
        if info.flags & BUFFER_FLAG_END_OF_STREAM != 0 {
            self.eos_out = true;
        }
        read
    }

    /// Read one granted output buffer. The caller owns the index and
    /// releases it whatever this returns.
    fn read_output(
        &self,
        index: i32,
        info: &AMediaCodecBufferInfo,
    ) -> Result<Option<PcmChunk>, DecodeError> {
        // The NDK states both of these signed. A negative one cast straight
        // to usize would wrap the addition below into a window that passes
        // the bounds check while sitting before the buffer. Converted ahead
        // of the empty-buffer test so malformed metadata is refused rather
        // than read as "no output this time".
        let (Ok(offset), Ok(len)) = (usize::try_from(info.offset), usize::try_from(info.size))
        else {
            return Err(DecodeError(format!(
                "negative buffer info ({}, {}) on {}",
                info.offset, info.size, self.codec.name
            )));
        };
        if len == 0 {
            return Ok(None);
        }
        // The codec's live output format wins over the configured one
        // (HE-AAC doubles the rate mid-stream via format-changed).
        let format = self.codec.output_format();
        let sample_rate = if format.sample_rate > 0 {
            format.sample_rate as u32
        } else {
            self.configured.0
        };
        let channels = if format.channels > 0 {
            format.channels as u32
        } else {
            self.configured.1
        };
        let float_out = format.seen && format.pcm_encoding == ENCODING_PCM_FLOAT;
        // The live format overrides the configured one, so it gets the
        // same bounds. A working decoder never reports values past them.
        if sample_rate > MAX_SAMPLE_RATE || channels > MAX_CHANNELS {
            return Err(DecodeError(format!(
                "implausible output geometry: {sample_rate} Hz / {channels} channels on {}",
                self.codec.name
            )));
        }

        // Whole interleaved frames only. A sample count short of a frame
        // reaches the ring as a remainder it never accepts, and the chunk
        // then parks until the inert-consumer discard clears it: a stall
        // per malformed buffer. Trimming the torn tail here avoids the
        // stall without failing an otherwise healthy track.
        let bytes_per_frame = if float_out { 4 } else { 2 } * channels.max(1) as usize;
        let len = len - len % bytes_per_frame;
        if len == 0 {
            return Ok(None);
        }

        // SAFETY: `index` is a granted output index; the buffer spans
        // `size` readable bytes and the reads below stay inside
        // `offset..offset + len`, proven by the check.
        let data = unsafe {
            let mut size = 0usize;
            let buf = AMediaCodec_getOutputBuffer(self.codec.raw(), index as usize, &mut size);
            if buf.is_null() || offset.checked_add(len).is_none_or(|end| end > size) {
                return Err(DecodeError(format!(
                    "output buffer {index} out of bounds on {}",
                    self.codec.name
                )));
            }
            let payload = core::slice::from_raw_parts(buf.add(offset), len);
            if float_out {
                payload
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .map(|b| f32::from_le_bytes(*b))
                    .collect::<Vec<f32>>()
            } else {
                payload
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .map(|b| f32::from(i16::from_le_bytes(*b)) / 32768.0)
                    .collect::<Vec<f32>>()
            }
        };
        Ok(Some(PcmChunk {
            sample_rate,
            channels,
            pts_us: info.presentation_time_us,
            data,
        }))
    }
}

impl AudioDecoder for McAudioDecoder {
    fn output_format(&self) -> (u32, u32) {
        let format = self.codec.output_format();
        if format.seen && format.sample_rate > 0 && format.channels > 0 {
            let sample_rate = format.sample_rate as u32;
            let channels = format.channels as u32;
            // The same bounds `read_output` applies. This value sizes the
            // ring and is published as the session's audio geometry before
            // any buffer arrives, so it is checked here too. The configured
            // pair is already bounded.
            if sample_rate <= MAX_SAMPLE_RATE && channels <= MAX_CHANNELS {
                return (sample_rate, channels);
            }
        }
        self.configured
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        self.codec.submit(au, pts_us)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        if let Some(e) = self.codec.take_error() {
            return Err(e);
        }
        loop {
            if let Some(chunk) = self.take_ready()? {
                return Ok(Some(chunk));
            }
            // An empty non-EOS buffer (codec config echo) loops; anything
            // else falls through.
            let state = self.codec.cb.lock();
            if state.output_ready.is_empty() {
                break;
            }
        }
        // One slice per call under the cumulative budget, so the caller
        // stays responsive to flushes while `drain_dry` reports false. Only
        // time spent waiting on a silent codec counts against it.
        if self.codec.draining() && !self.eos_out {
            if self.drain_waited < DRAIN_BUDGET {
                let waiting = Instant::now();
                let entry = self.codec.pop_output(DRAIN_SLICE);
                if entry.is_none() {
                    self.drain_waited += waiting.elapsed();
                }
                if let Some(entry) = entry {
                    let mut state = self.codec.cb.lock();
                    state.output_ready.push_front(entry);
                    drop(state);
                    if let Some(chunk) = self.take_ready()? {
                        return Ok(Some(chunk));
                    }
                }
                if let Some(e) = self.codec.take_error() {
                    return Err(e);
                }
            }
            if !self.eos_out && self.drain_waited >= DRAIN_BUDGET {
                crate::ffi::alog(&format!(
                    "mediacodec audio drain timed out on {}; declaring dry",
                    self.codec.name
                ));
                self.eos_out = true;
            }
        }
        Ok(None)
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.codec.begin_drain()
    }

    fn drain_dry(&self) -> bool {
        self.eos_out
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.codec.reset()?;
        self.eos_out = false;
        self.drain_waited = Duration::ZERO;
        Ok(())
    }
}
