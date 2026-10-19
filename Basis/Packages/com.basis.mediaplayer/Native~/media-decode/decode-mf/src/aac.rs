//! AAC decode through the in-box Media Foundation decoder, driven as a
//! sync MFT like the video path. The decoder wants: the fixed CLSID, raw
//! AAC frames (payload type 0), and `MF_MT_USER_DATA` set to 12 zero bytes
//! (the HEAACWAVEINFO fields after WAVEFORMATEX) followed by the
//! AudioSpecificConfig.
//!
//! Callers screen the channel layout before construction (≤ 6 explicitly
//! signalled channels). Fed anything wider, the in-box decoder accepts the
//! input type and then takes an access violation decoding the first frame
//! rather than returning an error.

use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};
use windows::Win32::Media::MediaFoundation::{
    CLSID_MSAACDecMFT, IMFMediaType, IMFTransform, MF_MT_AAC_PAYLOAD_TYPE,
    MF_MT_AUDIO_NUM_CHANNELS, MF_MT_AUDIO_SAMPLES_PER_SECOND, MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE,
    MF_MT_USER_DATA, MFAudioFormat_AAC, MFCreateMediaType, MFMediaType_Audio,
};
use windows::Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance};

use crate::audio_mft::AudioMft;
use crate::{mf, mf_startup};

pub struct AacDecoder {
    mft: AudioMft,
}

impl AacDecoder {
    /// `sample_rate`/`channels` describe the input as the container states
    /// it; `asc` is the AudioSpecificConfig.
    pub fn new(sample_rate: u32, channels: u32, asc: &[u8]) -> Result<Self, DecodeError> {
        // SAFETY: COM calls through owned wrappers after mf_startup. The one
        // raw-ish argument is the MF_MT_USER_DATA blob, passed as a bounded
        // slice the wrapper copies.
        unsafe {
            mf_startup()?;

            let mft: IMFTransform = mf(
                CoCreateInstance(&CLSID_MSAACDecMFT, None, CLSCTX_INPROC_SERVER),
                "create AAC decoder MFT",
            )?;

            let input: IMFMediaType = mf(MFCreateMediaType(), "MFCreateMediaType (aac in)")?;
            mf(
                input.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio),
                "set input major type",
            )?;
            mf(
                input.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_AAC),
                "set input subtype",
            )?;
            mf(
                input.SetUINT32(&MF_MT_AUDIO_SAMPLES_PER_SECOND, sample_rate),
                "set input rate",
            )?;
            mf(
                input.SetUINT32(&MF_MT_AUDIO_NUM_CHANNELS, channels),
                "set input channels",
            )?;
            // 0 = raw AAC frames (not ADTS).
            mf(
                input.SetUINT32(&MF_MT_AAC_PAYLOAD_TYPE, 0),
                "set payload type",
            )?;
            let mut blob = vec![0u8; 12 + asc.len()];
            blob[12..].copy_from_slice(asc);
            mf(input.SetBlob(&MF_MT_USER_DATA, &blob), "set user data")?;
            mf(mft.SetInputType(0, &input, 0), "SetInputType (aac)")?;

            Ok(Self {
                mft: AudioMft::start(mft, "aac", sample_rate, channels)?,
            })
        }
    }
}

impl AudioDecoder for AacDecoder {
    fn output_format(&self) -> (u32, u32) {
        self.mft.output_format()
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        self.mft.submit(au, pts_us)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        self.mft.try_output()
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.mft.begin_drain()
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.mft.reset()
    }
}
