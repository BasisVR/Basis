use std::ffi::c_void;

use core_foundation_sys::base::{Boolean, CFAllocatorRef, CFTypeRef, OSStatus};
use core_foundation_sys::dictionary::CFDictionaryRef;
use core_foundation_sys::string::CFStringRef;

pub(crate) type CMFormatDescriptionRef = *mut c_void;
pub(crate) type CMBlockBufferRef = *mut c_void;
pub(crate) type CMSampleBufferRef = *mut c_void;
pub(crate) type CVImageBufferRef = *mut c_void;
pub(crate) type VTDecompressionSessionRef = *mut c_void;
pub(crate) type AudioConverterRef = *mut c_void;

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CMTime {
    pub value: i64,
    pub timescale: i32,
    pub flags: u32,
    pub epoch: i64,
}

impl CMTime {
    pub(crate) fn micros(us: i64) -> Self {
        Self {
            value: us,
            timescale: 1_000_000,
            flags: TIME_VALID,
            epoch: 0,
        }
    }

    pub(crate) fn as_micros(&self) -> Option<i64> {
        (self.flags & TIME_VALID != 0 && self.timescale > 0)
            .then(|| (i128::from(self.value) * 1_000_000 / i128::from(self.timescale)) as i64)
    }
}

#[repr(C)]
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct CMSampleTimingInfo {
    pub duration: CMTime,
    pub presentation: CMTime,
    pub decode: CMTime,
}

pub(crate) type DecompressionOutput = extern "C" fn(
    refcon: *mut c_void,
    source_refcon: *mut c_void,
    status: OSStatus,
    flags: u32,
    image: CVImageBufferRef,
    pts: CMTime,
    duration: CMTime,
);

#[repr(C)]
pub(crate) struct DecompressionOutputRecord {
    pub callback: DecompressionOutput,
    pub refcon: *mut c_void,
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
#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct AudioStreamPacketDescription {
    pub start_offset: i64,
    pub variable_frames: u32,
    pub data_byte_size: u32,
}

#[repr(C)]
pub(crate) struct AudioBuffer {
    pub channels: u32,
    pub data_byte_size: u32,
    pub data: *mut c_void,
}

#[repr(C)]
pub(crate) struct AudioBufferList {
    pub count: u32,
    pub buffers: [AudioBuffer; 1],
}

#[repr(C)]
pub(crate) struct AudioFormatInfo {
    pub asbd: AudioStreamBasicDescription,
    pub cookie: *const c_void,
    pub cookie_size: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub(crate) struct AudioFormatListItem {
    pub asbd: AudioStreamBasicDescription,
    pub layout_tag: u32,
}

#[repr(C)]
pub(crate) struct AudioChannelLayout {
    pub tag: u32,
    pub bitmap: u32,
    pub descriptions: u32,
    pub first: [u32; 5],
}

pub(crate) type ComplexInput = extern "C" fn(
    converter: AudioConverterRef,
    packets: *mut u32,
    data: *mut AudioBufferList,
    description: *mut *mut AudioStreamPacketDescription,
    user: *mut c_void,
) -> OSStatus;

pub(crate) const TIME_VALID: u32 = 1;
pub(crate) const CODEC_H264: u32 = u32::from_be_bytes(*b"avc1");
pub(crate) const CODEC_HEVC: u32 = u32::from_be_bytes(*b"hvc1");
pub(crate) const PIXEL_420V: u32 = u32::from_be_bytes(*b"420v");
pub(crate) const PIXEL_420F: u32 = u32::from_be_bytes(*b"420f");
pub(crate) const BLOCK_ASSURE_MEMORY_NOW: u32 = 1;
pub(crate) const LOCK_READ_ONLY: u64 = 1;
pub(crate) const DECODE_TEMPORAL_PROCESSING: u32 = 1 << 3;
pub(crate) const DECODE_INFO_FRAME_DROPPED: u32 = 1 << 1;
pub(crate) const ERR_INVALID_SESSION: OSStatus = -12903;
pub(crate) const ERR_BAD_DATA: OSStatus = -12909;
pub(crate) const ERR_MALFUNCTION: OSStatus = -12911;
pub(crate) const FORMAT_AAC: u32 = u32::from_be_bytes(*b"aac ");
pub(crate) const FORMAT_MP3: u32 = u32::from_be_bytes(*b".mp3");
pub(crate) const FORMAT_LPCM: u32 = u32::from_be_bytes(*b"lpcm");
pub(crate) const FORMAT_FLAG_FLOAT: u32 = 1;
pub(crate) const FORMAT_FLAG_PACKED: u32 = 8;
pub(crate) const PROPERTY_FORMAT_INFO: u32 = u32::from_be_bytes(*b"fmti");
pub(crate) const PROPERTY_FORMAT_LIST: u32 = u32::from_be_bytes(*b"flst");
pub(crate) const CONVERTER_MAGIC_COOKIE: u32 = u32::from_be_bytes(*b"dmgc");
pub(crate) const CONVERTER_OUTPUT_LAYOUT: u32 = u32::from_be_bytes(*b"ocl ");

#[link(name = "CoreMedia", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn CMVideoFormatDescriptionCreateFromH264ParameterSets(
        allocator: CFAllocatorRef,
        count: usize,
        pointers: *const *const u8,
        sizes: *const usize,
        nal_header_length: i32,
        out: *mut CMFormatDescriptionRef,
    ) -> OSStatus;
    pub(crate) fn CMVideoFormatDescriptionCreateFromHEVCParameterSets(
        allocator: CFAllocatorRef,
        count: usize,
        pointers: *const *const u8,
        sizes: *const usize,
        nal_header_length: i32,
        extensions: CFDictionaryRef,
        out: *mut CMFormatDescriptionRef,
    ) -> OSStatus;
    pub(crate) fn CMFormatDescriptionGetExtension(
        description: CMFormatDescriptionRef,
        key: CFStringRef,
    ) -> CFTypeRef;
    pub(crate) fn CMBlockBufferCreateWithMemoryBlock(
        allocator: CFAllocatorRef,
        memory: *mut c_void,
        length: usize,
        block_allocator: CFAllocatorRef,
        custom_source: *const c_void,
        offset: usize,
        data_length: usize,
        flags: u32,
        out: *mut CMBlockBufferRef,
    ) -> OSStatus;
    pub(crate) fn CMBlockBufferReplaceDataBytes(
        source: *const c_void,
        destination: CMBlockBufferRef,
        offset: usize,
        length: usize,
    ) -> OSStatus;
    pub(crate) fn CMSampleBufferCreateReady(
        allocator: CFAllocatorRef,
        data: CMBlockBufferRef,
        format: CMFormatDescriptionRef,
        samples: isize,
        timing_entries: isize,
        timing: *const CMSampleTimingInfo,
        size_entries: isize,
        sizes: *const usize,
        out: *mut CMSampleBufferRef,
    ) -> OSStatus;
    #[link_name = "kCMFormatDescriptionExtension_FullRangeVideo"]
    pub(crate) static EXTENSION_FULL_RANGE: CFStringRef;
    #[link_name = "kCMFormatDescriptionExtension_YCbCrMatrix"]
    pub(crate) static EXTENSION_MATRIX: CFStringRef;
}

#[link(name = "CoreVideo", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn CVPixelBufferLockBaseAddress(buffer: CVImageBufferRef, flags: u64) -> i32;
    pub(crate) fn CVPixelBufferUnlockBaseAddress(buffer: CVImageBufferRef, flags: u64) -> i32;
    pub(crate) fn CVPixelBufferGetPixelFormatType(buffer: CVImageBufferRef) -> u32;
    pub(crate) fn CVPixelBufferGetPlaneCount(buffer: CVImageBufferRef) -> usize;
    pub(crate) fn CVPixelBufferGetWidthOfPlane(buffer: CVImageBufferRef, plane: usize) -> usize;
    pub(crate) fn CVPixelBufferGetHeightOfPlane(buffer: CVImageBufferRef, plane: usize) -> usize;
    pub(crate) fn CVPixelBufferGetBaseAddressOfPlane(
        buffer: CVImageBufferRef,
        plane: usize,
    ) -> *mut c_void;
    pub(crate) fn CVPixelBufferGetBytesPerRowOfPlane(
        buffer: CVImageBufferRef,
        plane: usize,
    ) -> usize;
    pub(crate) fn CVBufferGetAttachment(
        buffer: CVImageBufferRef,
        key: CFStringRef,
        mode: *mut u32,
    ) -> CFTypeRef;
    #[link_name = "kCVPixelBufferPixelFormatTypeKey"]
    pub(crate) static PIXEL_FORMAT_KEY: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrixKey"]
    pub(crate) static MATRIX_KEY: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrix_ITU_R_709_2"]
    pub(crate) static MATRIX_709: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrix_ITU_R_601_4"]
    pub(crate) static MATRIX_601: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrix_SMPTE_240M_1995"]
    pub(crate) static MATRIX_240M: CFStringRef;
    #[link_name = "kCVImageBufferYCbCrMatrix_ITU_R_2020"]
    pub(crate) static MATRIX_2020: CFStringRef;
}

#[link(name = "VideoToolbox", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn VTDecompressionSessionCreate(
        allocator: CFAllocatorRef,
        format: CMFormatDescriptionRef,
        decoder_specification: CFDictionaryRef,
        image_attributes: CFDictionaryRef,
        output: *const DecompressionOutputRecord,
        out: *mut VTDecompressionSessionRef,
    ) -> OSStatus;
    pub(crate) fn VTDecompressionSessionDecodeFrame(
        session: VTDecompressionSessionRef,
        sample: CMSampleBufferRef,
        flags: u32,
        source_refcon: *mut c_void,
        info: *mut u32,
    ) -> OSStatus;
    pub(crate) fn VTDecompressionSessionFinishDelayedFrames(
        session: VTDecompressionSessionRef,
    ) -> OSStatus;
    pub(crate) fn VTDecompressionSessionWaitForAsynchronousFrames(
        session: VTDecompressionSessionRef,
    ) -> OSStatus;
    pub(crate) fn VTDecompressionSessionCanAcceptFormatDescription(
        session: VTDecompressionSessionRef,
        format: CMFormatDescriptionRef,
    ) -> Boolean;
    pub(crate) fn VTDecompressionSessionInvalidate(session: VTDecompressionSessionRef);
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
    pub(crate) fn VTIsHardwareDecodeSupported(codec: u32) -> Boolean;
    #[link_name = "kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder"]
    pub(crate) static SPEC_ENABLE_HARDWARE: CFStringRef;
    #[link_name = "kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder"]
    pub(crate) static SPEC_REQUIRE_HARDWARE: CFStringRef;
    #[link_name = "kVTDecompressionPropertyKey_UsingHardwareAcceleratedVideoDecoder"]
    pub(crate) static PROPERTY_USING_HARDWARE: CFStringRef;
    #[link_name = "kVTDecompressionPropertyKey_RealTime"]
    pub(crate) static PROPERTY_REAL_TIME: CFStringRef;
}

#[link(name = "AudioToolbox", kind = "framework")]
unsafe extern "C" {
    pub(crate) fn AudioConverterNew(
        source: *const AudioStreamBasicDescription,
        destination: *const AudioStreamBasicDescription,
        out: *mut AudioConverterRef,
    ) -> OSStatus;
    pub(crate) fn AudioConverterDispose(converter: AudioConverterRef) -> OSStatus;
    pub(crate) fn AudioConverterReset(converter: AudioConverterRef) -> OSStatus;
    pub(crate) fn AudioConverterSetProperty(
        converter: AudioConverterRef,
        id: u32,
        size: u32,
        data: *const c_void,
    ) -> OSStatus;
    pub(crate) fn AudioConverterFillComplexBuffer(
        converter: AudioConverterRef,
        input: ComplexInput,
        user: *mut c_void,
        packets: *mut u32,
        output: *mut AudioBufferList,
        descriptions: *mut AudioStreamPacketDescription,
    ) -> OSStatus;
    pub(crate) fn AudioFormatGetPropertyInfo(
        id: u32,
        specifier_size: u32,
        specifier: *const c_void,
        size: *mut u32,
    ) -> OSStatus;
    pub(crate) fn AudioFormatGetProperty(
        id: u32,
        specifier_size: u32,
        specifier: *const c_void,
        size: *mut u32,
        out: *mut c_void,
    ) -> OSStatus;
}
