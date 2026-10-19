//! Windows Media Foundation decode adapters, driven as sync MFTs
//! (ProcessInput/ProcessOutput, no COM callback objects).
//!
//! The whole crate is Windows-only; on every other platform it compiles
//! to nothing (the engine's routing is target-gated to match).

#![cfg(windows)]

mod aac;
mod audio_mft;
mod dxva;
mod mp3;
mod video_mft;

pub use aac::AacDecoder;
pub use dxva::{
    DISABLE_HW_DECODE_ENV, HwCodec, HwVideoDecoder, probe_hardware, probe_hardware_ceiling,
    read_back_nv12,
};
pub use mp3::Mp3Decoder;

use media_decode::{DecodeError, SubmitOutcome, VideoDecoder, VideoFrame};
use windows::Win32::Media::MediaFoundation::{
    CLSID_MSH264DecoderMFT, IMFMediaType, IMFTransform, MF_MT_FRAME_SIZE, MF_MT_MAJOR_TYPE,
    MF_MT_SUBTYPE, MFCreateMediaType, MFMediaType_Video, MFSTARTUP_FULL, MFStartup,
    MFT_ENUM_FLAG_SORTANDFILTER, MFT_ENUM_FLAG_SYNCMFT, MFVideoFormat_AV1, MFVideoFormat_H264,
    MFVideoFormat_VP90,
};
use windows::Win32::System::Com::{
    CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx,
};

use video_mft::{VideoMft, create_decoder_for, request_low_latency};

// MFStartup version: MF_SDK_VERSION (0x0002) << 16 | MF_API_VERSION (0x0070).
const MF_VERSION: u32 = 0x0002_0070;

pub(crate) fn mf<T>(r: windows::core::Result<T>, what: &str) -> Result<T, DecodeError> {
    r.map_err(|e| DecodeError(format!("{what}: {e}")))
}

/// Idempotent on repeat calls from the same thread; the session never
/// shuts MF down for the process lifetime.
pub(crate) fn mf_startup() -> Result<(), DecodeError> {
    // SAFETY: plain FFI with no pointer arguments. CoInitializeEx tolerates
    // repeat/mismatched-mode calls (the failure HRESULT is ignored by design)
    // and MFStartup is balanced by never calling MFShutdown for the process
    // lifetime, so no COM object outlives its runtime.
    unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        mf(MFStartup(MF_VERSION, MFSTARTUP_FULL), "MFStartup")
    }
}

/// Build the video input type every MF video decoder wants: major, subtype
/// and, where the caller knows it, the coded frame size. H.264 carries its
/// dimensions in-band; the VP9 and AV1 decoders want them stated.
fn video_input_type(
    subtype: &windows::core::GUID,
    size: Option<(u32, u32)>,
) -> Result<IMFMediaType, DecodeError> {
    // SAFETY: COM calls through owned wrappers; no raw pointers cross.
    unsafe {
        let input: IMFMediaType = mf(MFCreateMediaType(), "MFCreateMediaType")?;
        mf(
            input.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video),
            "set input major type",
        )?;
        mf(input.SetGUID(&MF_MT_SUBTYPE, subtype), "set input subtype")?;
        if let Some((width, height)) = size {
            mf(
                input.SetUINT64(
                    &MF_MT_FRAME_SIZE,
                    (u64::from(width) << 32) | u64::from(height),
                ),
                "set input frame size",
            )?;
        }
        Ok(input)
    }
}

macro_rules! delegate_video_decoder {
    ($ty:ty) => {
        impl VideoDecoder for $ty {
            fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
                self.mft.submit(au, pts_us)
            }

            fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
                Ok(self.mft.try_output()?.map(VideoFrame::from))
            }

            fn begin_drain(&mut self) -> Result<(), DecodeError> {
                self.mft.begin_drain()
            }

            fn reset(&mut self) -> Result<(), DecodeError> {
                self.mft.reset()
            }
        }
    };
}

/// Capability probe: whether the platform VP9 decoder MFT (the Store "VP9
/// Video Extensions") both enumerates and activates. This is the path
/// `Vp9Decoder::new` takes, so `true` means that route will decode.
pub fn probe_vp9() -> bool {
    mf_startup().is_ok()
        && create_decoder_for(
            &MFVideoFormat_VP90,
            "VP9",
            MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
        )
        .is_ok()
}

/// H.264 through the fixed in-box decoder CLSID, fed Annex B with SPS/PPS
/// on keyframes, which is what it requires.
pub struct H264Decoder {
    mft: VideoMft,
}

impl H264Decoder {
    /// `live` puts the decoder in low-latency mode.
    pub fn new(live: bool) -> Result<Self, DecodeError> {
        mf_startup()?;
        // SAFETY: COM calls through owned wrappers after mf_startup.
        unsafe {
            let mft: IMFTransform = mf(
                CoCreateInstance(&CLSID_MSH264DecoderMFT, None, CLSCTX_INPROC_SERVER),
                "create H.264 decoder MFT",
            )?;
            if live {
                request_low_latency(&mft, "h264");
            }
            let input = video_input_type(&MFVideoFormat_H264, None)?;
            mf(mft.SetInputType(0, &input, 0), "SetInputType")?;
            Ok(Self {
                mft: VideoMft::start(mft, "h264")?,
            })
        }
    }

    pub fn output_size(&self) -> (u32, u32) {
        self.mft.output_size()
    }
}

delegate_video_decoder!(H264Decoder);

/// VP9 through the platform decoder (the Store "VP9 Video Extensions"
/// MFT), found by probe. A missing extension is a typed error the engine
/// reports rather than a silent failure.
pub struct Vp9Decoder {
    mft: VideoMft,
}

impl Vp9Decoder {
    /// `width`/`height` are the container-stated coded dimensions; VP9
    /// input types want them declared up front.
    pub fn new(width: u32, height: u32) -> Result<Self, DecodeError> {
        mf_startup()?;
        // SAFETY: COM calls through owned wrappers after mf_startup.
        unsafe {
            let mft = create_decoder_for(
                &MFVideoFormat_VP90,
                "VP9",
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            )?;
            let input = video_input_type(&MFVideoFormat_VP90, Some((width, height)))?;
            mf(mft.SetInputType(0, &input, 0), "SetInputType (vp9)")?;
            Ok(Self {
                mft: VideoMft::start(mft, "vp9")?,
            })
        }
    }

    pub fn output_size(&self) -> (u32, u32) {
        self.mft.output_size()
    }
}

delegate_video_decoder!(Vp9Decoder);

/// AV1 through the platform decoder (the Store "AV1 Video Extension"
/// MFT), found by probe like VP9.
pub struct Av1Decoder {
    mft: VideoMft,
}

impl Av1Decoder {
    pub fn new(width: u32, height: u32) -> Result<Self, DecodeError> {
        mf_startup()?;
        // SAFETY: COM calls through owned wrappers after mf_startup.
        unsafe {
            let mft = create_decoder_for(
                &MFVideoFormat_AV1,
                "AV1",
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            )?;
            let input = video_input_type(&MFVideoFormat_AV1, Some((width, height)))?;
            mf(mft.SetInputType(0, &input, 0), "SetInputType (av1)")?;
            Ok(Self {
                mft: VideoMft::start(mft, "av1")?,
            })
        }
    }

    pub fn output_size(&self) -> (u32, u32) {
        self.mft.output_size()
    }
}

delegate_video_decoder!(Av1Decoder);
