use std::ffi::{c_char, c_void};

use core_foundation_sys::array::CFArrayRef;
use core_foundation_sys::base::{Boolean, CFAllocatorRef, CFTypeRef, OSStatus};
use core_foundation_sys::dictionary::CFDictionaryRef;
use core_foundation_sys::string::CFStringRef;
use objc2::encode::{Encode, Encoding, RefEncode};

pub(crate) type CMSampleBufferRef = *mut OpaqueSampleBuffer;
pub(crate) type CMFormatDescriptionRef = *mut c_void;
pub(crate) type CMBlockBufferRef = *mut c_void;
pub(crate) type CVPixelBufferRef = *mut c_void;
pub(crate) type VTCompressionSessionRef = *mut c_void;
pub(crate) type DispatchQueue = *mut c_void;

#[repr(C)]
pub(crate) struct OpaqueSampleBuffer {
    _private: [u8; 0],
}

// SAFETY: CMSampleBufferRef is `^{opaqueCMSampleBuffer=}` in the Objective-C
// method signatures that carry it.
unsafe impl RefEncode for OpaqueSampleBuffer {
    const ENCODING_REF: Encoding =
        Encoding::Pointer(&Encoding::Struct("opaqueCMSampleBuffer", &[]));
}

#[repr(C)]
pub(crate) struct OpaqueString {
    _private: [u8; 0],
}

// SAFETY: CFStringRef is `^{__CFString=}` in Objective-C signatures.
unsafe impl RefEncode for OpaqueString {
    const ENCODING_REF: Encoding = Encoding::Pointer(&Encoding::Struct("__CFString", &[]));
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CMTime {
    pub value: i64,
    pub timescale: i32,
    pub flags: u32,
    pub epoch: i64,
}

// SAFETY: CMTime is the anonymous struct `{?=qiIq}`.
unsafe impl Encode for CMTime {
    const ENCODING: Encoding = Encoding::Struct(
        "?",
        &[i64::ENCODING, i32::ENCODING, u32::ENCODING, i64::ENCODING],
    );
}

impl CMTime {
    pub(crate) fn micros(us: i64) -> Self {
        Self {
            value: us,
            timescale: 1_000_000,
            flags: 1,
            epoch: 0,
        }
    }

    pub(crate) fn as_micros(&self) -> Option<i64> {
        (self.flags & 1 != 0 && self.timescale > 0)
            .then(|| (i128::from(self.value) * 1_000_000 / i128::from(self.timescale)) as i64)
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CGPoint {
    pub x: f64,
    pub y: f64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CGSize {
    pub width: f64,
    pub height: f64,
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CGRect {
    pub origin: CGPoint,
    pub size: CGSize,
}

// SAFETY: matches CoreGraphics' `{CGRect={CGPoint=dd}{CGSize=dd}}`.
unsafe impl Encode for CGRect {
    const ENCODING: Encoding = Encoding::Struct(
        "CGRect",
        &[
            Encoding::Struct("CGPoint", &[f64::ENCODING, f64::ENCODING]),
            Encoding::Struct("CGSize", &[f64::ENCODING, f64::ENCODING]),
        ],
    );
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct AudioStreamBasicDescription {
    pub sample_rate: f64,
    pub format_id: u32,
    pub format_flags: u32,
    pub bytes_per_packet: u32,
    pub frames_per_packet: u32,
    pub bytes_per_frame: u32,
    pub channels_per_frame: u32,
    pub bits_per_channel: u32,
    pub reserved: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub(crate) struct AudioBuffer {
    pub channels: u32,
    pub data_byte_size: u32,
    pub data: *mut c_void,
}

pub(crate) type CompressionOutput = extern "C" fn(
    refcon: *mut c_void,
    source_refcon: *mut c_void,
    status: OSStatus,
    flags: u32,
    sample: CMSampleBufferRef,
);

pub(crate) const PIXEL_420V: u32 = u32::from_be_bytes(*b"420v");
pub(crate) const CODEC_H264: u32 = u32::from_be_bytes(*b"avc1");
pub(crate) const FORMAT_LPCM: u32 = u32::from_be_bytes(*b"lpcm");
pub(crate) const FLAG_FLOAT: u32 = 1;
pub(crate) const FLAG_NON_INTERLEAVED: u32 = 1 << 5;
pub(crate) const AUDIO_LIST_ALIGN_16: u32 = 1;
pub(crate) const ENCODE_INFO_FRAME_DROPPED: u32 = 1 << 1;

#[link(name = "CoreMedia", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn CMSampleBufferGetImageBuffer(sample: CMSampleBufferRef) -> CVPixelBufferRef;
    pub(crate) fn CMSampleBufferGetDataBuffer(sample: CMSampleBufferRef) -> CMBlockBufferRef;
    pub(crate) fn CMSampleBufferGetFormatDescription(
        sample: CMSampleBufferRef,
    ) -> CMFormatDescriptionRef;
    pub(crate) fn CMSampleBufferGetPresentationTimeStamp(sample: CMSampleBufferRef) -> CMTime;
    pub(crate) fn CMSampleBufferGetNumSamples(sample: CMSampleBufferRef) -> isize;
    pub(crate) fn CMSampleBufferGetSampleAttachmentsArray(
        sample: CMSampleBufferRef,
        create: Boolean,
    ) -> CFArrayRef;
    pub(crate) fn CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sample: CMSampleBufferRef,
        size_needed: *mut usize,
        list: *mut c_void,
        list_size: usize,
        structure_allocator: CFAllocatorRef,
        block_allocator: CFAllocatorRef,
        flags: u32,
        block: *mut CMBlockBufferRef,
    ) -> OSStatus;
    pub(crate) fn CMAudioFormatDescriptionGetStreamBasicDescription(
        description: CMFormatDescriptionRef,
    ) -> *const AudioStreamBasicDescription;
    pub(crate) fn CMBlockBufferGetDataLength(block: CMBlockBufferRef) -> usize;
    pub(crate) fn CMBlockBufferCopyDataBytes(
        block: CMBlockBufferRef,
        offset: usize,
        length: usize,
        destination: *mut c_void,
    ) -> OSStatus;
    pub(crate) fn CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
        description: CMFormatDescriptionRef,
        index: usize,
        pointer: *mut *const u8,
        size: *mut usize,
        count: *mut usize,
        nal_header_length: *mut i32,
    ) -> OSStatus;
    #[link_name = "kCMSampleAttachmentKey_NotSync"]
    pub(crate) static ATTACHMENT_NOT_SYNC: CFStringRef;
}

#[link(name = "CoreVideo", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn CVPixelBufferGetWidth(buffer: CVPixelBufferRef) -> usize;
    pub(crate) fn CVPixelBufferGetHeight(buffer: CVPixelBufferRef) -> usize;
    #[link_name = "kCVImageBufferColorPrimaries_ITU_R_709_2"]
    pub(crate) static PRIMARIES_709: CFStringRef;
    #[link_name = "kCVImageBufferTransferFunction_ITU_R_709_2"]
    pub(crate) static TRANSFER_709: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrix_ITU_R_709_2"]
    pub(crate) static MATRIX_709: CFStringRef;
}

#[link(name = "VideoToolbox", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn VTCompressionSessionCreate(
        allocator: CFAllocatorRef,
        width: i32,
        height: i32,
        codec: u32,
        encoder_specification: CFDictionaryRef,
        source_attributes: CFDictionaryRef,
        compressed_allocator: CFAllocatorRef,
        output: CompressionOutput,
        refcon: *mut c_void,
        out: *mut VTCompressionSessionRef,
    ) -> OSStatus;
    pub(crate) fn VTCompressionSessionPrepareToEncodeFrames(
        session: VTCompressionSessionRef,
    ) -> OSStatus;
    pub(crate) fn VTCompressionSessionEncodeFrame(
        session: VTCompressionSessionRef,
        image: CVPixelBufferRef,
        pts: CMTime,
        duration: CMTime,
        frame_properties: CFDictionaryRef,
        source_refcon: *mut c_void,
        info: *mut u32,
    ) -> OSStatus;
    pub(crate) fn VTCompressionSessionCompleteFrames(
        session: VTCompressionSessionRef,
        until: CMTime,
    ) -> OSStatus;
    pub(crate) fn VTCompressionSessionInvalidate(session: VTCompressionSessionRef);
    pub(crate) fn VTSessionSetProperty(
        session: *mut c_void,
        key: CFStringRef,
        value: CFTypeRef,
    ) -> OSStatus;
    pub(crate) fn VTSessionCopyProperty(
        session: *mut c_void,
        key: CFStringRef,
        allocator: CFAllocatorRef,
        out: *mut c_void,
    ) -> OSStatus;
    #[link_name = "kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder"]
    pub(crate) static SPEC_ENABLE_HARDWARE: CFStringRef;
    #[link_name = "kVTVideoEncoderSpecification_RequireHardwareAcceleratedVideoEncoder"]
    pub(crate) static SPEC_REQUIRE_HARDWARE: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_UsingHardwareAcceleratedVideoEncoder"]
    pub(crate) static PROPERTY_USING_HARDWARE: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_RealTime"]
    pub(crate) static PROPERTY_REAL_TIME: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_AllowFrameReordering"]
    pub(crate) static PROPERTY_FRAME_REORDERING: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_ProfileLevel"]
    pub(crate) static PROPERTY_PROFILE_LEVEL: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_AverageBitRate"]
    pub(crate) static PROPERTY_AVERAGE_BIT_RATE: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_DataRateLimits"]
    pub(crate) static PROPERTY_DATA_RATE_LIMITS: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_MaxKeyFrameInterval"]
    pub(crate) static PROPERTY_MAX_KEY_INTERVAL: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration"]
    pub(crate) static PROPERTY_MAX_KEY_DURATION: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_ExpectedFrameRate"]
    pub(crate) static PROPERTY_EXPECTED_FRAME_RATE: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_ColorPrimaries"]
    pub(crate) static PROPERTY_COLOR_PRIMARIES: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_TransferFunction"]
    pub(crate) static PROPERTY_TRANSFER_FUNCTION: CFStringRef;
    #[link_name = "kVTCompressionPropertyKey_YCbCrMatrix"]
    pub(crate) static PROPERTY_MATRIX: CFStringRef;
    #[link_name = "kVTProfileLevel_H264_High_AutoLevel"]
    pub(crate) static PROFILE_HIGH: CFStringRef;
    #[link_name = "kVTProfileLevel_H264_Main_AutoLevel"]
    pub(crate) static PROFILE_MAIN: CFStringRef;
    #[link_name = "kVTEncodeFrameOptionKey_ForceKeyFrame"]
    pub(crate) static OPTION_FORCE_KEY_FRAME: CFStringRef;
}

pub(crate) type CGDisplayModeRef = *mut c_void;

#[link(name = "CoreGraphics", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn CGPreflightScreenCaptureAccess() -> bool;
    pub(crate) fn CGRequestScreenCaptureAccess() -> bool;
    pub(crate) fn CGMainDisplayID() -> u32;
    pub(crate) fn CGDisplayPixelsWide(display: u32) -> usize;
    pub(crate) fn CGDisplayCopyDisplayMode(display: u32) -> CGDisplayModeRef;
    pub(crate) fn CGDisplayModeGetPixelWidth(mode: CGDisplayModeRef) -> usize;
    pub(crate) fn CGDisplayModeGetPixelHeight(mode: CGDisplayModeRef) -> usize;
    pub(crate) fn CGDisplayModeRelease(mode: CGDisplayModeRef);
}

unsafe extern "C" {
    pub(crate) fn dispatch_queue_create(
        label: *const c_char,
        attributes: *mut c_void,
    ) -> DispatchQueue;
    pub(crate) fn dispatch_sync_f(
        queue: DispatchQueue,
        context: *mut c_void,
        work: extern "C" fn(*mut c_void),
    );
    pub(crate) fn dispatch_release(object: DispatchQueue);
}
