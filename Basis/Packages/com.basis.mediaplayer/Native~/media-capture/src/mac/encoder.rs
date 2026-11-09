use std::collections::VecDeque;
use std::ffi::c_void;
use std::ptr::{null, null_mut};
use std::sync::Mutex;

use core_foundation_sys::array::{
    CFArrayCreate, CFArrayGetCount, CFArrayGetValueAtIndex, kCFTypeArrayCallBacks,
};
use core_foundation_sys::base::{CFRelease, CFTypeRef, OSStatus, kCFAllocatorDefault};
use core_foundation_sys::dictionary::{CFDictionaryGetValue, CFDictionaryRef};
use core_foundation_sys::number::{
    CFBooleanGetValue, CFBooleanRef, kCFBooleanFalse, kCFBooleanTrue,
};
use core_foundation_sys::string::CFStringRef;

use super::ffi::*;
use super::{dictionary, number};
use crate::{CaptureError, EncodedVideo, EncoderPreference};

#[derive(Default)]
struct Output {
    frames: VecDeque<EncodedVideo>,
    error: Option<String>,
    width: u32,
    height: u32,
}

fn lock(output: &Mutex<Output>) -> std::sync::MutexGuard<'_, Output> {
    output.lock().unwrap_or_else(|e| e.into_inner())
}

extern "C" fn on_encoded(
    refcon: *mut c_void,
    _source: *mut c_void,
    status: OSStatus,
    flags: u32,
    sample: CMSampleBufferRef,
) {
    // SAFETY: refcon is the encoder's boxed Output, alive until the session is invalidated.
    let output = unsafe { &*(refcon as *const Mutex<Output>) };
    if status != 0 {
        lock(output).error = Some(format!("VideoToolbox H.264 encode failed ({status})"));
        return;
    }
    if flags & ENCODE_INFO_FRAME_DROPPED != 0 || sample.is_null() {
        return;
    }
    // SAFETY: VideoToolbox hands a live sample buffer for the callback's duration.
    let frame = unsafe { annexb(sample) };
    let mut out = lock(output);
    if let Some((data, pts_us, key)) = frame {
        let (width, height) = (out.width, out.height);
        out.frames.push_back(EncodedVideo {
            data,
            pts_us,
            key,
            width,
            height,
        });
    }
}

/// # Safety
/// `sample` is a live encoded sample buffer.
unsafe fn annexb(sample: CMSampleBufferRef) -> Option<(Vec<u8>, i64, bool)> {
    // SAFETY: caller contract; every CoreMedia getter here returns borrowed
    // references valid while the sample is.
    unsafe {
        let block = CMSampleBufferGetDataBuffer(sample);
        if block.is_null() {
            return None;
        }
        let len = CMBlockBufferGetDataLength(block);
        let mut avcc = vec![0u8; len];
        if len == 0 || CMBlockBufferCopyDataBytes(block, 0, len, avcc.as_mut_ptr().cast()) != 0 {
            return None;
        }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, 0);
        let key = attachments.is_null() || CFArrayGetCount(attachments) == 0 || {
            let first = CFArrayGetValueAtIndex(attachments, 0) as CFDictionaryRef;
            let not_sync = CFDictionaryGetValue(first, ATTACHMENT_NOT_SYNC.cast());
            not_sync.is_null() || !CFBooleanGetValue(not_sync as CFBooleanRef)
        };
        let mut data = Vec::with_capacity(len + 64);
        if key {
            let format = CMSampleBufferGetFormatDescription(sample);
            let mut count = 0usize;
            let mut index = 0usize;
            loop {
                let (mut pointer, mut size, mut header) = (null(), 0usize, 0i32);
                if format.is_null()
                    || CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                        format,
                        index,
                        &mut pointer,
                        &mut size,
                        &mut count,
                        &mut header,
                    ) != 0
                    || pointer.is_null()
                {
                    break;
                }
                data.extend_from_slice(&[0, 0, 0, 1]);
                data.extend_from_slice(std::slice::from_raw_parts(pointer, size));
                index += 1;
                if index >= count {
                    break;
                }
            }
        }
        let mut at = 0usize;
        while at + 4 <= avcc.len() {
            let n =
                u32::from_be_bytes([avcc[at], avcc[at + 1], avcc[at + 2], avcc[at + 3]]) as usize;
            at += 4;
            if n == 0 || at + n > avcc.len() {
                break;
            }
            data.extend_from_slice(&[0, 0, 0, 1]);
            data.extend_from_slice(&avcc[at..at + n]);
            at += n;
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sample).as_micros()?;
        (!data.is_empty()).then_some((data, pts, key))
    }
}

pub(crate) struct VtEncoder {
    session: VTCompressionSessionRef,
    output: Box<Mutex<Output>>,
    bitrate_kbps: u32,
    hardware: bool,
    force_key: CFDictionaryRef,
}

// SAFETY: the session is owned by this value and driven from the capture
// thread only; the output callback touches the mutex-guarded queue.
unsafe impl Send for VtEncoder {}

/// The low-latency rate control key exists from macOS 11.3, so it is looked
/// up at run time rather than linked.
fn low_latency_key() -> Option<CFStringRef> {
    let this = libloading::os::unix::Library::this();
    // SAFETY: the symbol, when present, is VideoToolbox's constant CFStringRef.
    unsafe {
        this.get::<*const CFStringRef>(b"kVTVideoEncoderSpecification_EnableLowLatencyRateControl")
            .ok()
            .map(|s| **s)
            .filter(|s| !s.is_null())
    }
}

impl VtEncoder {
    pub(crate) fn open(
        preference: EncoderPreference,
        size: (u32, u32),
        fps: u32,
        bitrate_kbps: u32,
        gop: u32,
    ) -> Result<Self, CaptureError> {
        let attempts: &[(bool, bool)] = match preference {
            EncoderPreference::HardwareOnly => &[(true, true), (true, false)],
            EncoderPreference::SoftwareOnly => &[(false, false)],
            EncoderPreference::HardwareWithFallback => {
                &[(true, true), (true, false), (false, false)]
            }
        };
        let require = preference == EncoderPreference::HardwareOnly;
        let mut last = String::new();
        for &(hardware, low_latency) in attempts {
            match Self::create(size, hardware, require, low_latency) {
                Ok(mut encoder) => {
                    encoder.configure(fps, bitrate_kbps, gop);
                    return Ok(encoder);
                }
                Err(e) => last = e,
            }
        }
        Err(CaptureError(format!(
            "no VideoToolbox H.264 encoder for {}x{}: {last}",
            size.0, size.1
        )))
    }

    fn create(
        size: (u32, u32),
        hardware: bool,
        require: bool,
        low_latency: bool,
    ) -> Result<Self, String> {
        let output: Box<Mutex<Output>> = Box::new(Mutex::new(Output {
            width: size.0,
            height: size.1,
            ..Output::default()
        }));
        let latency_key = if low_latency {
            Some(low_latency_key().ok_or("low-latency rate control unavailable")?)
        } else {
            None
        };
        // SAFETY: the keys are VideoToolbox constants; the dictionaries are
        // released after the session copies them; refcon outlives the session.
        unsafe {
            let mut pairs: Vec<(CFTypeRef, CFTypeRef)> = vec![(
                if require {
                    SPEC_REQUIRE_HARDWARE
                } else {
                    SPEC_ENABLE_HARDWARE
                }
                .cast(),
                if hardware {
                    kCFBooleanTrue
                } else {
                    kCFBooleanFalse
                }
                .cast(),
            )];
            if let Some(key) = latency_key {
                pairs.push((key.cast(), kCFBooleanTrue.cast()));
            }
            let specification = dictionary(&pairs);
            let mut session: VTCompressionSessionRef = null_mut();
            let status = VTCompressionSessionCreate(
                kCFAllocatorDefault,
                size.0 as i32,
                size.1 as i32,
                CODEC_H264,
                specification,
                null(),
                kCFAllocatorDefault,
                on_encoded,
                (&raw const *output) as *mut c_void,
                &mut session,
            );
            CFRelease(specification.cast());
            if status != 0 || session.is_null() {
                return Err(format!("VTCompressionSessionCreate failed ({status})"));
            }
            let mut using: CFTypeRef = null();
            let mut on_hardware = hardware;
            if VTSessionCopyProperty(
                session,
                PROPERTY_USING_HARDWARE,
                kCFAllocatorDefault,
                (&raw mut using).cast(),
            ) == 0
                && !using.is_null()
            {
                on_hardware = CFBooleanGetValue(using as CFBooleanRef);
                CFRelease(using);
            }
            if require && !on_hardware {
                VTCompressionSessionInvalidate(session);
                CFRelease(session.cast());
                return Err("no hardware H.264 encoder on this Mac".into());
            }
            Ok(Self {
                session,
                output,
                bitrate_kbps: 0,
                hardware: on_hardware,
                force_key: dictionary(&[(OPTION_FORCE_KEY_FRAME.cast(), kCFBooleanTrue.cast())]),
            })
        }
    }

    fn set(&self, key: CFStringRef, value: CFTypeRef) -> bool {
        // SAFETY: the session is live and the value a CoreFoundation object.
        unsafe { VTSessionSetProperty(self.session, key, value) == 0 }
    }

    fn set_number(&self, key: CFStringRef, value: i32) {
        let n = number(value);
        self.set(key, n);
        // SAFETY: created above and released once.
        unsafe { CFRelease(n) };
    }

    fn configure(&mut self, fps: u32, bitrate_kbps: u32, gop: u32) {
        // SAFETY: reading VideoToolbox and CoreVideo constant strings.
        unsafe {
            self.set(PROPERTY_REAL_TIME, kCFBooleanTrue.cast());
            self.set(PROPERTY_FRAME_REORDERING, kCFBooleanFalse.cast());
            if !self.set(PROPERTY_PROFILE_LEVEL, PROFILE_HIGH.cast()) {
                self.set(PROPERTY_PROFILE_LEVEL, PROFILE_MAIN.cast());
            }
            self.set(PROPERTY_COLOR_PRIMARIES, PRIMARIES_709.cast());
            self.set(PROPERTY_TRANSFER_FUNCTION, TRANSFER_709.cast());
            self.set(PROPERTY_MATRIX, MATRIX_709.cast());
        }
        // SAFETY: reading VideoToolbox constant strings.
        let (rate, interval, duration) = unsafe {
            (
                PROPERTY_EXPECTED_FRAME_RATE,
                PROPERTY_MAX_KEY_INTERVAL,
                PROPERTY_MAX_KEY_DURATION,
            )
        };
        self.set_number(rate, fps.max(1) as i32);
        self.set_number(interval, gop.max(1) as i32);
        self.set_number(duration, gop.max(1).div_ceil(fps.max(1)) as i32);
        self.set_bitrate(bitrate_kbps);
        // SAFETY: the session is live.
        unsafe { VTCompressionSessionPrepareToEncodeFrames(self.session) };
    }

    pub(crate) fn name(&self) -> String {
        if self.hardware {
            "VideoToolbox H.264".into()
        } else {
            "VideoToolbox H.264 (software)".into()
        }
    }

    pub(crate) fn hardware(&self) -> bool {
        self.hardware
    }

    pub(crate) fn set_bitrate(&mut self, kbps: u32) {
        let kbps = kbps.max(1);
        if kbps == self.bitrate_kbps {
            return;
        }
        self.bitrate_kbps = kbps;
        let bits = kbps.saturating_mul(1000).min(i32::MAX as u32) as i32;
        // SAFETY: reading a VideoToolbox constant string.
        self.set_number(unsafe { PROPERTY_AVERAGE_BIT_RATE }, bits);
        let bytes = number((bits / 8).saturating_mul(3) / 2);
        let seconds = number(1);
        let values = [bytes, seconds];
        // SAFETY: the array retains both numbers, which are released after.
        unsafe {
            let limits = CFArrayCreate(
                kCFAllocatorDefault,
                values.as_ptr(),
                2,
                &kCFTypeArrayCallBacks,
            );
            self.set(PROPERTY_DATA_RATE_LIMITS, limits.cast());
            CFRelease(limits.cast());
            CFRelease(bytes);
            CFRelease(seconds);
        }
    }

    /// Encode one captured frame and hand back whatever the session has
    /// finished, keyframes with SPS and PPS in front.
    pub(crate) fn encode(
        &mut self,
        image: CVPixelBufferRef,
        pts_us: i64,
        key: bool,
    ) -> Result<Vec<EncodedVideo>, CaptureError> {
        let mut info = 0u32;
        // SAFETY: the session and the pixel buffer are live; the session
        // retains the buffer for as long as it needs it.
        let status = unsafe {
            VTCompressionSessionEncodeFrame(
                self.session,
                image,
                CMTime::micros(pts_us),
                CMTime::default(),
                if key { self.force_key } else { null() },
                null_mut(),
                &mut info,
            )
        };
        if status != 0 {
            return Err(CaptureError(format!(
                "VideoToolbox H.264 encode failed ({status})"
            )));
        }
        let mut out = lock(&self.output);
        if let Some(error) = out.error.take() {
            return Err(CaptureError(error));
        }
        Ok(out.frames.drain(..).collect())
    }
}

impl Drop for VtEncoder {
    fn drop(&mut self) {
        // SAFETY: the session and dictionary are ours and released exactly once,
        // the session after every pending frame has come back.
        unsafe {
            VTCompressionSessionCompleteFrames(self.session, CMTime::default());
            VTCompressionSessionInvalidate(self.session);
            CFRelease(self.session.cast());
            CFRelease(self.force_key.cast());
        }
    }
}
