use std::mem::ManuallyDrop;

use windows::Win32::Foundation::LUID;
use windows::Win32::Graphics::Direct3D11::{
    D3D11_CPU_ACCESS_READ, D3D11_MAP_READ, D3D11_MAPPED_SUBRESOURCE, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_STAGING, ID3D11Device, ID3D11DeviceContext, ID3D11Texture2D,
};
use windows::Win32::Graphics::Dxgi::IDXGIDevice;
use windows::Win32::Media::MediaFoundation::{
    CLSID_MSH264EncoderMFT, CODECAPI_AVEncCommonMeanBitRate, CODECAPI_AVEncCommonRateControlMode,
    CODECAPI_AVEncMPVDefaultBPictureCount, CODECAPI_AVEncMPVGOPSize,
    CODECAPI_AVEncVideoForceKeyFrame, CODECAPI_AVLowLatencyMode, ICodecAPI, IMF2DBuffer,
    IMFActivate, IMFDXGIDeviceManager, IMFMediaEventGenerator, IMFMediaType, IMFSample,
    IMFTransform, METransformHaveOutput, METransformNeedInput, MF_E_NO_EVENTS_AVAILABLE,
    MF_E_NOTACCEPTING, MF_E_TRANSFORM_NEED_MORE_INPUT, MF_E_TRANSFORM_STREAM_CHANGE,
    MF_EVENT_FLAG_NO_WAIT, MF_LOW_LATENCY, MF_MT_AVG_BITRATE, MF_MT_FRAME_RATE, MF_MT_FRAME_SIZE,
    MF_MT_INTERLACE_MODE, MF_MT_MAJOR_TYPE, MF_MT_MPEG_SEQUENCE_HEADER, MF_MT_MPEG2_PROFILE,
    MF_MT_PIXEL_ASPECT_RATIO, MF_MT_SUBTYPE, MF_MT_VIDEO_NOMINAL_RANGE, MF_MT_YUV_MATRIX,
    MF_TRANSFORM_ASYNC, MF_TRANSFORM_ASYNC_UNLOCK, MFCreateDXGIDeviceManager,
    MFCreateDXGISurfaceBuffer, MFCreateMediaType, MFCreateMemoryBuffer, MFCreateSample,
    MFMediaType_Video, MFNominalRange_16_235, MFSampleExtension_CleanPoint,
    MFT_CATEGORY_VIDEO_ENCODER, MFT_ENUM_ADAPTER_LUID, MFT_ENUM_FLAG_HARDWARE,
    MFT_ENUM_FLAG_SORTANDFILTER, MFT_FRIENDLY_NAME_Attribute, MFT_MESSAGE_NOTIFY_BEGIN_STREAMING,
    MFT_MESSAGE_NOTIFY_END_OF_STREAM, MFT_MESSAGE_NOTIFY_START_OF_STREAM,
    MFT_MESSAGE_SET_D3D_MANAGER, MFT_OUTPUT_DATA_BUFFER, MFT_OUTPUT_STREAM_PROVIDES_SAMPLES,
    MFT_REGISTER_TYPE_INFO, MFTEnumEx, MFVideoFormat_H264, MFVideoFormat_NV12,
    MFVideoInterlace_Progressive, MFVideoTransferMatrix_BT709, eAVEncCommonRateControlMode_CBR,
    eAVEncH264VProfile_High, eAVEncH264VProfile_Main,
};
use windows::Win32::System::Com::{CLSCTX_INPROC_SERVER, CoCreateInstance, CoTaskMemFree};
use windows::Win32::System::Variant::VARIANT;
use windows::core::{Interface, PWSTR};

use super::{hr, ok};
use crate::{
    CaptureError, EncodedVideo, EncoderPreference, has_parameter_sets, nal_types, parameter_sets,
};

pub(crate) struct Settings {
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub bitrate_kbps: u32,
    pub gop_frames: u32,
}

pub(crate) struct Encoder {
    mft: IMFTransform,
    codec: Option<ICodecAPI>,
    events: Option<IMFMediaEventGenerator>,
    _manager: Option<IMFDXGIDeviceManager>,
    staging: Option<ID3D11Texture2D>,
    pub name: String,
    pub hardware: bool,
    width: u32,
    height: u32,
    duration_100ns: i64,
    credits: u32,
    provides_samples: bool,
    out_size: u32,
    seq_header: Vec<u8>,
    bitrate_kbps: u32,
    in_flight: u32,
}

fn adapter_luid(device: &ID3D11Device) -> Option<LUID> {
    // SAFETY: COM queries on a live device; the adapter description is a
    // plain struct copy.
    unsafe {
        let dxgi: IDXGIDevice = device.cast().ok()?;
        let adapter = dxgi.GetAdapter().ok()?;
        Some(adapter.GetDesc().ok()?.AdapterLuid)
    }
}

fn friendly_name(activate: &IMFActivate) -> String {
    let mut text = PWSTR::null();
    let mut len = 0u32;
    // SAFETY: GetAllocatedString hands back a CoTaskMem string that is
    // copied and freed here.
    unsafe {
        if activate
            .GetAllocatedString(&MFT_FRIENDLY_NAME_Attribute, &mut text, &mut len)
            .is_err()
            || text.is_null()
        {
            return "hardware H.264 encoder".into();
        }
        let name = text.to_string().unwrap_or_default();
        CoTaskMemFree(Some(text.0 as *const _));
        name
    }
}

fn activate_luid(activate: &IMFActivate) -> Option<LUID> {
    let mut bytes = [0u8; 8];
    // SAFETY: the blob read is bounded by the slice passed.
    if unsafe { activate.GetBlob(&MFT_ENUM_ADAPTER_LUID, &mut bytes, None) }.is_ok() {
        return Some(LUID {
            LowPart: u32::from_le_bytes([bytes[0], bytes[1], bytes[2], bytes[3]]),
            HighPart: i32::from_le_bytes([bytes[4], bytes[5], bytes[6], bytes[7]]),
        });
    }
    // SAFETY: attribute read on a live activate.
    let value = unsafe { activate.GetUINT64(&MFT_ENUM_ADAPTER_LUID) }.ok()?;
    Some(LUID {
        LowPart: value as u32,
        HighPart: (value >> 32) as i32,
    })
}

fn hardware_candidates(device: &ID3D11Device) -> Vec<(IMFActivate, String)> {
    let input = MFT_REGISTER_TYPE_INFO {
        guidMajorType: MFMediaType_Video,
        guidSubtype: MFVideoFormat_NV12,
    };
    let output = MFT_REGISTER_TYPE_INFO {
        guidMajorType: MFMediaType_Video,
        guidSubtype: MFVideoFormat_H264,
    };
    let mut activates: *mut Option<IMFActivate> = std::ptr::null_mut();
    let mut count = 0u32;
    // SAFETY: MFTEnumEx returns a CoTaskMem array of activates; every slot is
    // taken (so its reference drops) and the array is freed below.
    unsafe {
        if MFTEnumEx(
            MFT_CATEGORY_VIDEO_ENCODER,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
            Some(&input),
            Some(&output),
            &mut activates,
            &mut count,
        )
        .is_err()
            || activates.is_null()
        {
            return Vec::new();
        }
        let wanted = adapter_luid(device);
        let mut out = Vec::new();
        for slot in std::slice::from_raw_parts_mut(activates, count as usize) {
            let Some(activate) = slot.take() else {
                continue;
            };
            let matches = match (wanted, activate_luid(&activate)) {
                (Some(a), Some(b)) => a.LowPart == b.LowPart && a.HighPart == b.HighPart,
                _ => true,
            };
            if matches {
                let name = friendly_name(&activate);
                out.push((activate, name));
            }
        }
        CoTaskMemFree(Some(activates as *const _));
        out
    }
}

fn set_u32(codec: &ICodecAPI, api: &windows::core::GUID, value: u32) -> bool {
    let variant = VARIANT::from(value);
    // SAFETY: the variant lives across the call and the GUID is a constant.
    unsafe { codec.SetValue(api, &variant) }.is_ok()
}

fn set_bool(codec: &ICodecAPI, api: &windows::core::GUID, value: bool) -> bool {
    let variant = VARIANT::from(value);
    // SAFETY: as for set_u32.
    unsafe { codec.SetValue(api, &variant) }.is_ok()
}

fn video_type(
    subtype: &windows::core::GUID,
    s: &Settings,
    profile: Option<i32>,
) -> Result<IMFMediaType, CaptureError> {
    // SAFETY: attribute writes on a freshly created media type.
    unsafe {
        let t = ok(MFCreateMediaType(), "MFCreateMediaType")?;
        hr(
            t.SetGUID(&MF_MT_MAJOR_TYPE, &MFMediaType_Video),
            "set major type",
        )?;
        hr(t.SetGUID(&MF_MT_SUBTYPE, subtype), "set subtype")?;
        hr(
            t.SetUINT64(
                &MF_MT_FRAME_SIZE,
                (u64::from(s.width) << 32) | u64::from(s.height),
            ),
            "set frame size",
        )?;
        hr(
            t.SetUINT64(&MF_MT_FRAME_RATE, (u64::from(s.fps) << 32) | 1),
            "set frame rate",
        )?;
        hr(
            t.SetUINT64(&MF_MT_PIXEL_ASPECT_RATIO, (1u64 << 32) | 1),
            "set pixel aspect",
        )?;
        hr(
            t.SetUINT32(&MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive.0 as u32),
            "set interlace",
        )?;
        if let Some(profile) = profile {
            hr(
                t.SetUINT32(&MF_MT_AVG_BITRATE, s.bitrate_kbps * 1000),
                "set bitrate",
            )?;
            hr(
                t.SetUINT32(&MF_MT_MPEG2_PROFILE, profile as u32),
                "set profile",
            )?;
        } else {
            let _ = t.SetUINT32(&MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709.0 as u32);
            let _ = t.SetUINT32(&MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235.0 as u32);
        }
        Ok(t)
    }
}

impl Encoder {
    pub fn create(
        device: &ID3D11Device,
        settings: &Settings,
        preference: EncoderPreference,
    ) -> Result<Self, CaptureError> {
        let mut failures = Vec::new();
        if preference != EncoderPreference::SoftwareOnly {
            for (activate, name) in hardware_candidates(device) {
                // SAFETY: activation of an enumerated MFT; the activate is
                // shut down if configuration fails.
                let mft = match unsafe { activate.ActivateObject::<IMFTransform>() } {
                    Ok(mft) => mft,
                    Err(e) => {
                        failures.push(format!("{name}: {e}"));
                        continue;
                    }
                };
                match Self::configure(mft, device, settings, name.clone(), true) {
                    Ok(encoder) => return Ok(encoder),
                    Err(e) => {
                        failures.push(format!("{name}: {e}"));
                        // SAFETY: releases the activated object we gave up on.
                        let _ = unsafe { activate.ShutdownObject() };
                    }
                }
            }
            if preference == EncoderPreference::HardwareOnly {
                return Err(CaptureError(if failures.is_empty() {
                    "no hardware H.264 encoder".into()
                } else {
                    failures.join("; ")
                }));
            }
        }
        // SAFETY: plain in-process COM creation of the Microsoft encoder.
        let mft: IMFTransform = ok(
            unsafe { CoCreateInstance(&CLSID_MSH264EncoderMFT, None, CLSCTX_INPROC_SERVER) },
            "create software H.264 encoder",
        )?;
        let encoder = Self::configure(
            mft,
            device,
            settings,
            "Microsoft H.264 (software)".into(),
            false,
        );
        if let Err(e) = &encoder
            && !failures.is_empty()
        {
            return Err(CaptureError(format!("{e}; {}", failures.join("; "))));
        }
        encoder
    }

    fn configure(
        mft: IMFTransform,
        device: &ID3D11Device,
        s: &Settings,
        name: String,
        hardware: bool,
    ) -> Result<Self, CaptureError> {
        // SAFETY: COM configuration of an owned MFT. The device manager is
        // kept alive in the returned struct for as long as the MFT may use it.
        unsafe {
            let attributes = ok(mft.GetAttributes(), "GetAttributes")?;
            let is_async = attributes.GetUINT32(&MF_TRANSFORM_ASYNC).unwrap_or(0) != 0;
            if is_async {
                hr(
                    attributes.SetUINT32(&MF_TRANSFORM_ASYNC_UNLOCK, 1),
                    "unlock async MFT",
                )?;
            }
            let _ = attributes.SetUINT32(&MF_LOW_LATENCY, 1);
            let mut manager = None;
            if hardware {
                let mut token = 0u32;
                let mut created: Option<IMFDXGIDeviceManager> = None;
                hr(
                    MFCreateDXGIDeviceManager(&mut token, &mut created),
                    "MFCreateDXGIDeviceManager",
                )?;
                let created =
                    created.ok_or_else(|| CaptureError("no DXGI device manager".into()))?;
                hr(created.ResetDevice(device, token), "ResetDevice")?;
                hr(
                    mft.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, created.as_raw() as usize),
                    "SET_D3D_MANAGER",
                )?;
                manager = Some(created);
            }
            let codec: Option<ICodecAPI> = mft.cast().ok();
            if let Some(codec) = &codec {
                set_u32(
                    codec,
                    &CODECAPI_AVEncCommonRateControlMode,
                    eAVEncCommonRateControlMode_CBR.0 as u32,
                );
                set_u32(
                    codec,
                    &CODECAPI_AVEncCommonMeanBitRate,
                    s.bitrate_kbps * 1000,
                );
                set_bool(codec, &CODECAPI_AVLowLatencyMode, true);
                set_u32(codec, &CODECAPI_AVEncMPVDefaultBPictureCount, 0);
                set_u32(codec, &CODECAPI_AVEncMPVGOPSize, s.gop_frames);
            }
            let mut set = Err(CaptureError("no profile accepted".into()));
            for profile in [eAVEncH264VProfile_High.0, eAVEncH264VProfile_Main.0] {
                let output = video_type(&MFVideoFormat_H264, s, Some(profile))?;
                set = hr(mft.SetOutputType(0, &output, 0), "SetOutputType");
                if set.is_ok() {
                    break;
                }
            }
            set?;
            let input = video_type(&MFVideoFormat_NV12, s, None)?;
            if mft.SetInputType(0, &input, 0).is_err() {
                let mut index = 0;
                let mut accepted = false;
                while let Ok(candidate) = mft.GetInputAvailableType(0, index) {
                    index += 1;
                    if candidate.GetGUID(&MF_MT_SUBTYPE).ok() != Some(MFVideoFormat_NV12) {
                        continue;
                    }
                    let _ = candidate.SetUINT64(
                        &MF_MT_FRAME_SIZE,
                        (u64::from(s.width) << 32) | u64::from(s.height),
                    );
                    let _ = candidate.SetUINT64(&MF_MT_FRAME_RATE, (u64::from(s.fps) << 32) | 1);
                    if mft.SetInputType(0, &candidate, 0).is_ok() {
                        accepted = true;
                        break;
                    }
                }
                if !accepted {
                    return Err(CaptureError(format!(
                        "{}x{} NV12 input refused",
                        s.width, s.height
                    )));
                }
            }
            let mut seq_header = Vec::new();
            if let Ok(current) = mft.GetOutputCurrentType(0)
                && let Ok(len) = current.GetBlobSize(&MF_MT_MPEG_SEQUENCE_HEADER)
                && len > 0
            {
                let mut blob = vec![0u8; len as usize];
                if current
                    .GetBlob(&MF_MT_MPEG_SEQUENCE_HEADER, &mut blob, None)
                    .is_ok()
                {
                    seq_header = parameter_sets(&blob);
                }
            }
            let info = ok(mft.GetOutputStreamInfo(0), "GetOutputStreamInfo")?;
            let provides_samples = info.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES.0 as u32 != 0;
            hr(
                mft.ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0),
                "BEGIN_STREAMING",
            )?;
            hr(
                mft.ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0),
                "START_OF_STREAM",
            )?;
            let events = if is_async {
                Some(ok(
                    mft.cast::<IMFMediaEventGenerator>(),
                    "async MFT events",
                )?)
            } else {
                None
            };
            let staging = if hardware {
                None
            } else {
                let desc = D3D11_TEXTURE2D_DESC {
                    Width: s.width,
                    Height: s.height,
                    MipLevels: 1,
                    ArraySize: 1,
                    Format: windows::Win32::Graphics::Dxgi::Common::DXGI_FORMAT_NV12,
                    SampleDesc: windows::Win32::Graphics::Dxgi::Common::DXGI_SAMPLE_DESC {
                        Count: 1,
                        Quality: 0,
                    },
                    Usage: D3D11_USAGE_STAGING,
                    BindFlags: 0,
                    CPUAccessFlags: D3D11_CPU_ACCESS_READ.0 as u32,
                    MiscFlags: 0,
                };
                let mut texture = None;
                hr(
                    device.CreateTexture2D(&desc, None, Some(&mut texture)),
                    "create NV12 staging texture",
                )?;
                texture
            };
            Ok(Self {
                mft,
                codec,
                events,
                _manager: manager,
                staging,
                name,
                hardware,
                width: s.width,
                height: s.height,
                duration_100ns: 10_000_000 / i64::from(s.fps.max(1)),
                credits: 0,
                provides_samples,
                out_size: info.cbSize.max(s.width * s.height * 3 / 2),
                seq_header,
                bitrate_kbps: s.bitrate_kbps,
                in_flight: 0,
            })
        }
    }

    pub fn busy(&self) -> bool {
        self.in_flight > 0
    }

    pub fn set_bitrate(&mut self, kbps: u32) {
        if kbps == self.bitrate_kbps {
            return;
        }
        if let Some(codec) = &self.codec
            && set_u32(codec, &CODECAPI_AVEncCommonMeanBitRate, kbps * 1000)
        {
            self.bitrate_kbps = kbps;
        }
    }

    fn sample_from_texture(
        &self,
        context: &ID3D11DeviceContext,
        texture: &ID3D11Texture2D,
        pts_us: i64,
    ) -> Result<IMFSample, CaptureError> {
        // SAFETY: COM wrapping of a live texture, or a mapped staging copy
        // whose rows are read within the stated pitch and height.
        unsafe {
            let sample = ok(MFCreateSample(), "MFCreateSample")?;
            if let Some(staging) = &self.staging {
                context.CopyResource(staging, texture);
                let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
                hr(
                    context.Map(staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)),
                    "map NV12 staging",
                )?;
                let (w, h) = (self.width as usize, self.height as usize);
                let len = w * h * 3 / 2;
                let buffer = ok(MFCreateMemoryBuffer(len as u32), "MFCreateMemoryBuffer");
                let buffer = match buffer {
                    Ok(b) => b,
                    Err(e) => {
                        context.Unmap(staging, 0);
                        return Err(e);
                    }
                };
                let mut dst = std::ptr::null_mut();
                if buffer.Lock(&mut dst, None, None).is_ok() {
                    let src = mapped.pData as *const u8;
                    let pitch = mapped.RowPitch as usize;
                    for row in 0..h + h / 2 {
                        std::ptr::copy_nonoverlapping(src.add(row * pitch), dst.add(row * w), w);
                    }
                    let _ = buffer.Unlock();
                    let _ = buffer.SetCurrentLength(len as u32);
                }
                context.Unmap(staging, 0);
                hr(sample.AddBuffer(&buffer), "AddBuffer")?;
            } else {
                let buffer = ok(
                    MFCreateDXGISurfaceBuffer(&ID3D11Texture2D::IID, texture, 0, false),
                    "MFCreateDXGISurfaceBuffer",
                )?;
                if let Ok(two_d) = buffer.cast::<IMF2DBuffer>()
                    && let Ok(len) = two_d.GetContiguousLength()
                {
                    let _ = buffer.SetCurrentLength(len);
                }
                hr(sample.AddBuffer(&buffer), "AddBuffer")?;
            }
            hr(sample.SetSampleTime(pts_us * 10), "SetSampleTime")?;
            hr(
                sample.SetSampleDuration(self.duration_100ns),
                "SetSampleDuration",
            )?;
            Ok(sample)
        }
    }

    pub fn encode(
        &mut self,
        context: &ID3D11DeviceContext,
        texture: &ID3D11Texture2D,
        pts_us: i64,
        key: bool,
        out: &mut Vec<EncodedVideo>,
    ) -> Result<bool, CaptureError> {
        self.poll(out)?;
        if self.events.is_some() && self.credits == 0 {
            return Ok(false);
        }
        if key && let Some(codec) = &self.codec {
            set_u32(codec, &CODECAPI_AVEncVideoForceKeyFrame, 1);
        }
        let sample = self.sample_from_texture(context, texture, pts_us)?;
        // SAFETY: ProcessInput on the owned MFT with a sample built above.
        let mut result = unsafe { self.mft.ProcessInput(0, &sample, 0) };
        if self.events.is_none() && matches!(&result, Err(e) if e.code() == MF_E_NOTACCEPTING) {
            self.drain_sync(out)?;
            // SAFETY: as above, retried once after draining.
            result = unsafe { self.mft.ProcessInput(0, &sample, 0) };
        }
        match result {
            Ok(()) => {}
            Err(e) if e.code() == MF_E_NOTACCEPTING => return Ok(false),
            Err(e) => return Err(CaptureError(format!("ProcessInput ({}): {e}", self.name))),
        }
        self.in_flight += 1;
        if self.events.is_some() {
            self.credits -= 1;
        } else {
            self.drain_sync(out)?;
        }
        Ok(true)
    }

    pub fn poll(&mut self, out: &mut Vec<EncodedVideo>) -> Result<(), CaptureError> {
        let Some(events) = self.events.clone() else {
            return Ok(());
        };
        loop {
            // SAFETY: non-blocking event query on the MFT's own generator.
            let event = match unsafe { events.GetEvent(MF_EVENT_FLAG_NO_WAIT) } {
                Ok(event) => event,
                Err(e) if e.code() == MF_E_NO_EVENTS_AVAILABLE => return Ok(()),
                Err(e) => return Err(CaptureError(format!("encoder event ({}): {e}", self.name))),
            };
            // SAFETY: reading the type of an event we own.
            let kind = unsafe { event.GetType() }.unwrap_or(0);
            if kind == METransformNeedInput.0 as u32 {
                self.credits += 1;
            } else if kind == METransformHaveOutput.0 as u32 {
                self.take_output(out)?;
            }
        }
    }

    fn drain_sync(&mut self, out: &mut Vec<EncodedVideo>) -> Result<(), CaptureError> {
        while self.take_output(out)? {}
        Ok(())
    }

    fn take_output(&mut self, out: &mut Vec<EncodedVideo>) -> Result<bool, CaptureError> {
        // SAFETY: the output buffer struct is built and torn down here; the
        // sample and event collection it may carry are taken out of their
        // ManuallyDrop wrappers exactly once.
        unsafe {
            let caller = if self.provides_samples {
                None
            } else {
                let sample = ok(MFCreateSample(), "MFCreateSample (output)")?;
                let buffer = ok(
                    MFCreateMemoryBuffer(self.out_size),
                    "MFCreateMemoryBuffer (output)",
                )?;
                hr(sample.AddBuffer(&buffer), "AddBuffer (output)")?;
                Some(sample)
            };
            let mut data = MFT_OUTPUT_DATA_BUFFER {
                dwStreamID: 0,
                pSample: ManuallyDrop::new(caller),
                dwStatus: 0,
                pEvents: ManuallyDrop::new(None),
            };
            let mut status = 0u32;
            let result = self
                .mft
                .ProcessOutput(0, std::slice::from_mut(&mut data), &mut status);
            let sample = ManuallyDrop::take(&mut data.pSample);
            drop(ManuallyDrop::take(&mut data.pEvents));
            match result {
                Ok(()) => {}
                Err(e) if e.code() == MF_E_TRANSFORM_NEED_MORE_INPUT => return Ok(false),
                Err(e) if e.code() == MF_E_TRANSFORM_STREAM_CHANGE => {
                    if let Ok(t) = self.mft.GetOutputAvailableType(0, 0) {
                        let _ = self.mft.SetOutputType(0, &t, 0);
                    }
                    return Ok(true);
                }
                Err(e) => return Err(CaptureError(format!("ProcessOutput ({}): {e}", self.name))),
            }
            let Some(sample) = sample else {
                return Ok(true);
            };
            let buffer = ok(
                sample.ConvertToContiguousBuffer(),
                "ConvertToContiguousBuffer",
            )?;
            let mut ptr = std::ptr::null_mut();
            let mut len = 0u32;
            hr(buffer.Lock(&mut ptr, None, Some(&mut len)), "lock output")?;
            let mut bytes = std::slice::from_raw_parts(ptr, len as usize).to_vec();
            let _ = buffer.Unlock();
            if bytes.is_empty() {
                return Ok(true);
            }
            let pts_us = sample.GetSampleTime().unwrap_or(0) / 10;
            let key = sample.GetUINT32(&MFSampleExtension_CleanPoint).unwrap_or(0) != 0
                || nal_types(&bytes).any(|t| t == 5);
            if key {
                if has_parameter_sets(&bytes) {
                    self.seq_header = parameter_sets(&bytes);
                } else if !self.seq_header.is_empty() {
                    let mut joined = self.seq_header.clone();
                    joined.extend_from_slice(&bytes);
                    bytes = joined;
                }
            }
            self.in_flight = self.in_flight.saturating_sub(1);
            out.push(EncodedVideo {
                data: bytes,
                pts_us,
                key,
                width: self.width,
                height: self.height,
            });
            Ok(true)
        }
    }
}

impl Drop for Encoder {
    fn drop(&mut self) {
        // SAFETY: end-of-stream notification on the owned MFT before release.
        unsafe {
            let _ = self.mft.ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
            let _ = self.mft.ProcessMessage(MFT_MESSAGE_SET_D3D_MANAGER, 0);
        }
    }
}
