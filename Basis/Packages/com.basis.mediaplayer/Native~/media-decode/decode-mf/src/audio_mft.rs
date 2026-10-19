//! The shared sync-MFT driver for the in-box audio decoders. Output
//! negotiation, sample plumbing and the drain/flush protocol are the same
//! for every codec; only the input type the adapter configures differs.

use media_decode::{DecodeError, PcmChunk, SubmitOutcome};
use windows::Win32::Media::MediaFoundation::{
    IMFMediaType, IMFSample, IMFTransform, MF_E_NOTACCEPTING, MF_E_TRANSFORM_NEED_MORE_INPUT,
    MF_E_TRANSFORM_STREAM_CHANGE, MF_MT_AUDIO_BITS_PER_SAMPLE, MF_MT_AUDIO_NUM_CHANNELS,
    MF_MT_AUDIO_SAMPLES_PER_SECOND, MF_MT_SUBTYPE, MFAudioFormat_Float, MFAudioFormat_PCM,
    MFCreateMemoryBuffer, MFCreateSample, MFT_MESSAGE_COMMAND_DRAIN, MFT_MESSAGE_COMMAND_FLUSH,
    MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, MFT_MESSAGE_NOTIFY_END_OF_STREAM,
    MFT_MESSAGE_NOTIFY_START_OF_STREAM, MFT_OUTPUT_DATA_BUFFER, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES,
};

use crate::mf;
use std::mem::ManuallyDrop;

pub(crate) struct AudioMft {
    mft: IMFTransform,
    tag: &'static str,
    output_provides_samples: bool,
    output_buffer_size: u32,
    out_rate: u32,
    out_channels: u32,
    out_bits: u32,
    /// Sample-counted fallback timeline for outputs that come back without
    /// a time (the decoders normally propagate input times).
    pts_fallback_us: i64,
}

impl AudioMft {
    /// Wrap a created MFT whose input type is already set; negotiates the
    /// output and starts streaming. `rate`/`channels` seed the output
    /// format until negotiation refines them.
    pub(crate) fn start(
        mft: IMFTransform,
        tag: &'static str,
        rate: u32,
        channels: u32,
    ) -> Result<Self, DecodeError> {
        let mut this = Self {
            mft,
            tag,
            output_provides_samples: false,
            output_buffer_size: 0,
            out_rate: rate,
            out_channels: channels,
            out_bits: 32,
            pts_fallback_us: 0,
        };
        this.negotiate_output()?;
        // SAFETY: message-only COM calls on the owned MFT; no pointers cross.
        unsafe {
            mf(
                this.mft
                    .ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0),
                "BEGIN_STREAMING (audio)",
            )?;
            mf(
                this.mft
                    .ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0),
                "START_OF_STREAM (audio)",
            )?;
        }
        Ok(this)
    }

    pub(crate) fn output_format(&self) -> (u32, u32) {
        (self.out_rate, self.out_channels)
    }

    /// Pick among the offered output types: prefer a channel count matching
    /// the input (keeps discrete surround), then the stereo fold-down, then
    /// IEEE float over 16-bit PCM.
    fn negotiate_output(&mut self) -> Result<(), DecodeError> {
        // SAFETY: COM calls on the owned MFT; offered media types are owned
        // wrappers queried before use.
        unsafe {
            let target = self.out_channels;
            let mut best: Option<(i64, IMFMediaType, u32, u32, u32)> = None;
            let mut index = 0;
            loop {
                let Ok(ty) = self.mft.GetOutputAvailableType(0, index) else {
                    break;
                };
                index += 1;
                let Ok(subtype) = ty.GetGUID(&MF_MT_SUBTYPE) else {
                    continue;
                };
                let is_float = subtype == MFAudioFormat_Float;
                let is_pcm = subtype == MFAudioFormat_PCM;
                if !is_float && !is_pcm {
                    continue;
                }
                let channels = ty.GetUINT32(&MF_MT_AUDIO_NUM_CHANNELS).unwrap_or(0);
                let rate = ty.GetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND).unwrap_or(0);
                let bits = ty.GetUINT32(&MF_MT_AUDIO_BITS_PER_SAMPLE).unwrap_or(0);
                // Types wider than 8 channels never rank: the managed
                // splitter maps at most 8 lanes.
                if channels == 0 || channels > 8 {
                    continue;
                }
                let rank = i64::from(channels == target) * 10_000
                    + i64::from(channels == 2) * 1_000
                    + i64::from(is_float) * 100
                    + i64::from(channels);
                if best.as_ref().is_none_or(|(r, ..)| rank > *r) {
                    let bits = if is_float { 32 } else { bits.max(16) };
                    best = Some((rank, ty, rate, channels, bits));
                }
            }
            let (_, ty, rate, channels, bits) = best.ok_or_else(|| {
                DecodeError(format!("{} decoder offered no PCM output", self.tag))
            })?;
            mf(self.mft.SetOutputType(0, &ty, 0), "SetOutputType (audio)")?;
            if rate != 0 {
                self.out_rate = rate;
            }
            if channels != 0 {
                self.out_channels = channels;
            }
            self.out_bits = bits;

            let info = mf(
                self.mft.GetOutputStreamInfo(0),
                "GetOutputStreamInfo (audio)",
            )?;
            self.output_provides_samples =
                info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32 != 0;
            self.output_buffer_size = if info.cbSize != 0 { info.cbSize } else { 65536 };
            Ok(())
        }
    }

    fn caller_sample(&mut self) -> Result<Option<IMFSample>, DecodeError> {
        if self.output_provides_samples {
            return Ok(None);
        }
        // SAFETY: COM object creation with no raw pointers; fresh sample and
        // buffer per call, as on the video path.
        unsafe {
            let sample = mf(MFCreateSample(), "MFCreateSample (audio out)")?;
            let buffer = mf(
                MFCreateMemoryBuffer(self.output_buffer_size),
                "MFCreateMemoryBuffer (audio out)",
            )?;
            mf(sample.AddBuffer(&buffer), "AddBuffer (audio out)")?;
            Ok(Some(sample))
        }
    }

    fn copy_chunk(&mut self, sample: &IMFSample) -> Result<PcmChunk, DecodeError> {
        // SAFETY: Lock exposes `len` readable bytes until Unlock; the slice is
        // constructed with exactly that length and only read before Unlock.
        unsafe {
            let pts_us = sample
                .GetSampleTime()
                .map(|t| t / 10)
                .unwrap_or(self.pts_fallback_us);

            let buffer = mf(sample.GetBufferByIndex(0), "GetBufferByIndex (audio)")?;
            let mut ptr = std::ptr::null_mut();
            let mut len = 0u32;
            mf(
                buffer.Lock(&mut ptr, None, Some(&mut len)),
                "buffer Lock (audio)",
            )?;
            let bytes = std::slice::from_raw_parts(ptr, len as usize);
            let data: Vec<f32> = if self.out_bits == 16 {
                bytes
                    .as_chunks::<2>()
                    .0
                    .iter()
                    .map(|c| f32::from(i16::from_le_bytes(*c)) / 32768.0)
                    .collect()
            } else {
                bytes
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .map(|c| f32::from_le_bytes(*c))
                    .collect()
            };
            mf(buffer.Unlock(), "buffer Unlock (audio)")?;

            let channels = self.out_channels.max(1);
            let frames = data.len() as i64 / i64::from(channels);
            self.pts_fallback_us = pts_us + frames * 1_000_000 / i64::from(self.out_rate.max(1));

            Ok(PcmChunk {
                sample_rate: self.out_rate,
                channels,
                pts_us,
                data,
            })
        }
    }

    pub(crate) fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        // SAFETY: the input buffer is created with au.len() bytes and locked
        // before the copy_nonoverlapping of exactly au.len() bytes; all
        // interface pointers are owned wrappers.
        unsafe {
            let sample = mf(MFCreateSample(), "MFCreateSample (audio in)")?;
            let buffer = mf(
                MFCreateMemoryBuffer(au.len() as u32),
                "MFCreateMemoryBuffer (audio in)",
            )?;
            let mut ptr = std::ptr::null_mut();
            mf(buffer.Lock(&mut ptr, None, None), "input Lock (audio)")?;
            std::ptr::copy_nonoverlapping(au.as_ptr(), ptr, au.len());
            mf(buffer.Unlock(), "input Unlock (audio)")?;
            mf(
                buffer.SetCurrentLength(au.len() as u32),
                "SetCurrentLength (audio)",
            )?;
            mf(sample.AddBuffer(&buffer), "AddBuffer (audio in)")?;
            mf(sample.SetSampleTime(pts_us * 10), "SetSampleTime (audio)")?;

            match self.mft.ProcessInput(0, &sample, 0) {
                Ok(()) => Ok(SubmitOutcome::Accepted),
                Err(e) if e.code() == MF_E_NOTACCEPTING => Ok(SubmitOutcome::NotAccepting),
                Err(e) => Err(DecodeError(format!("ProcessInput ({}): {e}", self.tag))),
            }
        }
    }

    pub(crate) fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        // SAFETY: as on the video path, the MFT_OUTPUT_DATA_BUFFER's
        // ManuallyDrop COM pointers are reclaimed on every path after
        // ProcessOutput.
        unsafe {
            loop {
                let sample = self.caller_sample()?;
                let mut out = MFT_OUTPUT_DATA_BUFFER {
                    dwStreamID: 0,
                    pSample: ManuallyDrop::new(sample),
                    dwStatus: 0,
                    pEvents: ManuallyDrop::new(None),
                };
                let mut status = 0u32;
                let result = self
                    .mft
                    .ProcessOutput(0, std::slice::from_mut(&mut out), &mut status);
                let sample = ManuallyDrop::take(&mut out.pSample);
                drop(ManuallyDrop::take(&mut out.pEvents));

                match result {
                    Ok(()) => {
                        let sample = sample.ok_or_else(|| {
                            DecodeError(format!("ProcessOutput ({}) returned no sample", self.tag))
                        })?;
                        return Ok(Some(self.copy_chunk(&sample)?));
                    }
                    Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => return Ok(None),
                    Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                        // HE-AAC renegotiates mid-stream when in-band SBR
                        // doubles the rate past what configure saw. Repick
                        // and keep draining; giving up here mutes audio
                        // for good.
                        self.negotiate_output()?;
                        continue;
                    }
                    Err(e) => {
                        return Err(DecodeError(format!("ProcessOutput ({}): {e}", self.tag)));
                    }
                }
            }
        }
    }

    pub(crate) fn begin_drain(&mut self) -> Result<(), DecodeError> {
        // SAFETY: message-only COM calls on the owned MFT; no pointers cross.
        unsafe {
            mf(
                self.mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0),
                "END_OF_STREAM (audio)",
            )?;
            mf(
                self.mft.ProcessMessage(MFT_MESSAGE_COMMAND_DRAIN, 0),
                "COMMAND_DRAIN (audio)",
            )?;
            Ok(())
        }
    }

    pub(crate) fn reset(&mut self) -> Result<(), DecodeError> {
        // SAFETY: message-only COM calls on the owned MFT; no pointers cross.
        unsafe {
            mf(
                self.mft.ProcessMessage(MFT_MESSAGE_COMMAND_FLUSH, 0),
                "COMMAND_FLUSH (audio)",
            )?;
            mf(
                self.mft
                    .ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0),
                "START_OF_STREAM (audio)",
            )?;
            Ok(())
        }
    }
}
