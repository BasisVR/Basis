//! MP3 decode through the in-box Media Foundation decoder, which needs no
//! bundled code. Same sync-MFT driving as AAC; the input type is just
//! MAJOR=Audio, SUBTYPE=MP3 plus the container-stated rate and channels.
//! Everything else is in the frame headers.

use media_decode::{AudioDecoder, DecodeError, PcmChunk, SubmitOutcome};
use windows::Win32::Media::MediaFoundation::{
    CLSID_MP3DecMediaObject, IMFMediaType, IMFTransform, MF_MT_AUDIO_NUM_CHANNELS,
    MF_MT_AUDIO_SAMPLES_PER_SECOND, MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE, MFAudioFormat_MP3,
    MFCreateMediaType, MFMediaType_Audio,
};
use windows::Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance};

use crate::audio_mft::AudioMft;
use crate::{mf, mf_startup};

pub struct Mp3Decoder {
    mft: AudioMft,
}

impl Mp3Decoder {
    /// `sample_rate`/`channels` describe the input as the container (or the
    /// first frame header) states it.
    pub fn new(sample_rate: u32, channels: u32) -> Result<Self, DecodeError> {
        // SAFETY: COM calls through owned wrappers after mf_startup; no raw
        // pointers cross the boundary.
        unsafe {
            mf_startup()?;

            let mft: IMFTransform = mf(
                CoCreateInstance(&CLSID_MP3DecMediaObject, None, CLSCTX_INPROC_SERVER),
                "create MP3 decoder MFT",
            )?;

            let input: IMFMediaType = mf(MFCreateMediaType(), "MFCreateMediaType (mp3 in)")?;
            mf(
                input.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Audio),
                "set input major type",
            )?;
            mf(
                input.SetGUID(&MF_MT_SUBTYPE, &MFAudioFormat_MP3),
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
            mf(mft.SetInputType(0, &input, 0), "SetInputType (mp3)")?;

            Ok(Self {
                mft: AudioMft::start(mft, "mp3", sample_rate, channels)?,
            })
        }
    }
}

impl AudioDecoder for Mp3Decoder {
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
