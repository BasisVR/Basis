use std::ffi::{CStr, CString, c_char, c_int, c_uint, c_void};
use std::mem::{offset_of, size_of};
use std::os::fd::{AsRawFd, FromRawFd, OwnedFd};

use libloading::Library;

use crate::h264_bitstream::{
    ASPECT_RATIO_SQUARE, Frame, Gop, MAX_REF_FRAMES, PIC_INIT_QP, Profile, Sps, copy_nv12,
};
use crate::{CaptureError, EncodedVideo, has_parameter_sets, parameter_sets};

type VADisplay = *mut c_void;
type VAStatus = c_int;

const VA_STATUS_SUCCESS: VAStatus = 0;
const VA_INVALID_ID: u32 = 0xffff_ffff;
const VA_ATTRIB_NOT_SUPPORTED: u32 = 0x8000_0000;
const VA_RT_FORMAT_YUV420: u32 = 0x1;
const VA_RC_CBR: u32 = 0x2;
const VA_RC_VBR: u32 = 0x4;
const VA_ENC_PACKED_HEADER_SEQUENCE: u32 = 0x1;
const VA_ENC_PACKED_HEADER_PICTURE: u32 = 0x2;
const VA_ENC_PACKED_HEADER_SLICE: u32 = 0x4;
const VA_PROGRESSIVE: c_int = 0x1;
const VA_FOURCC_NV12: u32 = 0x3231_564e;
const VA_LSB_FIRST: u32 = 1;
const VA_SURFACE_ATTRIB_SETTABLE: u32 = 0x2;
const VA_PICTURE_H264_INVALID: u32 = 0x1;
const VA_PICTURE_H264_SHORT_TERM_REFERENCE: u32 = 0x8;
const VA_CODED_BUF_STATUS_SLICE_OVERFLOW_MASK: u32 = 0x200;
const VA_PROFILE_H264_MAIN: c_int = 6;
const VA_PROFILE_H264_CONSTRAINED_BASELINE: c_int = 13;
const VA_ENTRYPOINT_ENC_SLICE: c_int = 6;
const VA_ENTRYPOINT_ENC_SLICE_LP: c_int = 8;
const VA_CONFIG_ATTRIB_RT_FORMAT: c_int = 0;
const VA_CONFIG_ATTRIB_RATE_CONTROL: c_int = 5;
const VA_CONFIG_ATTRIB_ENC_PACKED_HEADERS: c_int = 10;
const VA_CONFIG_ATTRIB_ENC_MAX_REF_FRAMES: c_int = 13;
const VA_SURFACE_ATTRIB_PIXEL_FORMAT: c_int = 1;
const VA_GENERIC_VALUE_TYPE_INTEGER: c_int = 1;
const VA_ENC_CODED_BUFFER: c_int = 21;
const VA_ENC_SEQUENCE_PARAMETER_BUFFER: c_int = 22;
const VA_ENC_PICTURE_PARAMETER_BUFFER: c_int = 23;
const VA_ENC_SLICE_PARAMETER_BUFFER: c_int = 24;
const VA_ENC_PACKED_HEADER_PARAMETER_BUFFER: c_int = 25;
const VA_ENC_PACKED_HEADER_DATA_BUFFER: c_int = 26;
const VA_ENC_MISC_PARAMETER_BUFFER: c_int = 27;
const VA_ENC_MISC_PARAMETER_TYPE_FRAME_RATE: u32 = 0;
const VA_ENC_MISC_PARAMETER_TYPE_RATE_CONTROL: u32 = 1;
const VA_ENC_MISC_PARAMETER_TYPE_HRD: u32 = 5;
const VA_ENC_PACKED_HEADER_TYPE_SEQUENCE: u32 = 1;
const VA_ENC_PACKED_HEADER_TYPE_PICTURE: u32 = 2;
const VA_ENC_PACKED_HEADER_TYPE_SLICE: u32 = 3;
const INPUT_SURFACES: usize = 2;
const RECON_SURFACES: usize = 2;
const MAX_SEGMENTS: usize = 4096;

#[repr(C)]
#[derive(Clone, Copy)]
struct VAConfigAttrib {
    kind: c_int,
    value: u32,
}

#[repr(C)]
#[derive(Clone, Copy)]
union VAGenericValueUnion {
    i: i32,
    f: f32,
    p: *mut c_void,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct VAGenericValue {
    kind: c_int,
    value: VAGenericValueUnion,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct VASurfaceAttrib {
    kind: c_int,
    flags: u32,
    value: VAGenericValue,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct VAImageFormat {
    fourcc: u32,
    byte_order: u32,
    bits_per_pixel: u32,
    depth: u32,
    red_mask: u32,
    green_mask: u32,
    blue_mask: u32,
    alpha_mask: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
struct VAImage {
    image_id: u32,
    format: VAImageFormat,
    buf: u32,
    width: u16,
    height: u16,
    data_size: u32,
    num_planes: u32,
    pitches: [u32; 3],
    offsets: [u32; 3],
    num_palette_entries: i32,
    entry_bytes: i32,
    component_order: [i8; 4],
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VACodedBufferSegment {
    size: u32,
    bit_offset: u32,
    status: u32,
    reserved: u32,
    buf: *mut c_void,
    next: *mut c_void,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncPackedHeaderParameterBuffer {
    kind: u32,
    bit_length: u32,
    has_emulation_bytes: u8,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncMiscParameterRateControl {
    bits_per_second: u32,
    target_percentage: u32,
    window_size: u32,
    initial_qp: u32,
    min_qp: u32,
    basic_unit_size: u32,
    rc_flags: u32,
    icq_quality_factor: u32,
    max_qp: u32,
    quality_factor: u32,
    target_frame_size: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncMiscParameterHRD {
    initial_buffer_fullness: u32,
    buffer_size: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncMiscParameterFrameRate {
    framerate: u32,
    framerate_flags: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncMiscParameterBuffer<T> {
    kind: u32,
    data: T,
}

#[repr(C)]
#[derive(Clone, Copy)]
struct VAPictureH264 {
    picture_id: u32,
    frame_idx: u32,
    flags: u32,
    top_field_order_cnt: i32,
    bottom_field_order_cnt: i32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncSequenceParameterBufferH264 {
    seq_parameter_set_id: u8,
    level_idc: u8,
    intra_period: u32,
    intra_idr_period: u32,
    ip_period: u32,
    bits_per_second: u32,
    max_num_ref_frames: u32,
    picture_width_in_mbs: u16,
    picture_height_in_mbs: u16,
    seq_fields: u32,
    bit_depth_luma_minus8: u8,
    bit_depth_chroma_minus8: u8,
    num_ref_frames_in_pic_order_cnt_cycle: u8,
    offset_for_non_ref_pic: i32,
    offset_for_top_to_bottom_field: i32,
    offset_for_ref_frame: [i32; 256],
    frame_cropping_flag: u8,
    frame_crop_left_offset: u32,
    frame_crop_right_offset: u32,
    frame_crop_top_offset: u32,
    frame_crop_bottom_offset: u32,
    vui_parameters_present_flag: u8,
    vui_fields: u32,
    aspect_ratio_idc: u8,
    sar_width: u32,
    sar_height: u32,
    num_units_in_tick: u32,
    time_scale: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncPictureParameterBufferH264 {
    curr_pic: VAPictureH264,
    reference_frames: [VAPictureH264; 16],
    coded_buf: u32,
    pic_parameter_set_id: u8,
    seq_parameter_set_id: u8,
    last_picture: u8,
    frame_num: u16,
    pic_init_qp: u8,
    num_ref_idx_l0_active_minus1: u8,
    num_ref_idx_l1_active_minus1: u8,
    chroma_qp_index_offset: i8,
    second_chroma_qp_index_offset: i8,
    pic_fields: u32,
    va_reserved: [u32; 4],
}

#[repr(C)]
struct VAEncSliceParameterBufferH264 {
    macroblock_address: u32,
    num_macroblocks: u32,
    macroblock_info: u32,
    slice_type: u8,
    pic_parameter_set_id: u8,
    idr_pic_id: u16,
    pic_order_cnt_lsb: u16,
    delta_pic_order_cnt_bottom: i32,
    delta_pic_order_cnt: [i32; 2],
    direct_spatial_mv_pred_flag: u8,
    num_ref_idx_active_override_flag: u8,
    num_ref_idx_l0_active_minus1: u8,
    num_ref_idx_l1_active_minus1: u8,
    ref_pic_list0: [VAPictureH264; 32],
    ref_pic_list1: [VAPictureH264; 32],
    luma_log2_weight_denom: u8,
    chroma_log2_weight_denom: u8,
    luma_weight_l0_flag: u8,
    luma_weight_l0: [i16; 32],
    luma_offset_l0: [i16; 32],
    chroma_weight_l0_flag: u8,
    chroma_weight_l0: [[i16; 2]; 32],
    chroma_offset_l0: [[i16; 2]; 32],
    luma_weight_l1_flag: u8,
    luma_weight_l1: [i16; 32],
    luma_offset_l1: [i16; 32],
    chroma_weight_l1_flag: u8,
    chroma_weight_l1: [[i16; 2]; 32],
    chroma_offset_l1: [[i16; 2]; 32],
    cabac_init_idc: u8,
    slice_qp_delta: i8,
    disable_deblocking_filter_idc: u8,
    slice_alpha_c0_offset_div2: i8,
    slice_beta_offset_div2: i8,
    va_reserved: [u32; 4],
}

const _: () = {
    assert!(size_of::<VAConfigAttrib>() == 8 && offset_of!(VAConfigAttrib, value) == 4);
    assert!(size_of::<VAImageFormat>() == 48 && offset_of!(VAImageFormat, va_reserved) == 32);
    assert!(size_of::<VAImage>() == 120);
    assert!(offset_of!(VAImage, format) == 4 && offset_of!(VAImage, buf) == 52);
    assert!(offset_of!(VAImage, width) == 56 && offset_of!(VAImage, height) == 58);
    assert!(offset_of!(VAImage, data_size) == 60 && offset_of!(VAImage, num_planes) == 64);
    assert!(offset_of!(VAImage, pitches) == 68 && offset_of!(VAImage, offsets) == 80);
    assert!(offset_of!(VAImage, num_palette_entries) == 92);
    assert!(offset_of!(VAImage, component_order) == 100);
    assert!(offset_of!(VAImage, va_reserved) == 104);
    assert!(size_of::<VAEncPackedHeaderParameterBuffer>() == 28);
    assert!(offset_of!(VAEncPackedHeaderParameterBuffer, has_emulation_bytes) == 8);
    assert!(offset_of!(VAEncPackedHeaderParameterBuffer, va_reserved) == 12);
    assert!(size_of::<VAEncMiscParameterRateControl>() == 60);
    assert!(offset_of!(VAEncMiscParameterRateControl, rc_flags) == 24);
    assert!(offset_of!(VAEncMiscParameterRateControl, target_frame_size) == 40);
    assert!(size_of::<VAEncMiscParameterHRD>() == 24);
    assert!(size_of::<VAEncMiscParameterFrameRate>() == 24);
    assert!(size_of::<VAEncMiscParameterBuffer<VAEncMiscParameterHRD>>() == 28);
    assert!(size_of::<VAPictureH264>() == 36 && offset_of!(VAPictureH264, va_reserved) == 20);
    assert!(size_of::<VAEncSequenceParameterBufferH264>() == 1132);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, intra_period) == 4);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, picture_width_in_mbs) == 24);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, seq_fields) == 28);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, bit_depth_luma_minus8) == 32);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, offset_for_non_ref_pic) == 36);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, offset_for_ref_frame) == 44);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, frame_cropping_flag) == 1068);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, frame_crop_left_offset) == 1072);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, frame_crop_bottom_offset) == 1084);
    assert!(
        offset_of!(
            VAEncSequenceParameterBufferH264,
            vui_parameters_present_flag
        ) == 1088
    );
    assert!(offset_of!(VAEncSequenceParameterBufferH264, vui_fields) == 1092);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, aspect_ratio_idc) == 1096);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, sar_width) == 1100);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, time_scale) == 1112);
    assert!(offset_of!(VAEncSequenceParameterBufferH264, va_reserved) == 1116);
    assert!(size_of::<VAEncPictureParameterBufferH264>() == 648);
    assert!(offset_of!(VAEncPictureParameterBufferH264, reference_frames) == 36);
    assert!(offset_of!(VAEncPictureParameterBufferH264, coded_buf) == 612);
    assert!(offset_of!(VAEncPictureParameterBufferH264, last_picture) == 618);
    assert!(offset_of!(VAEncPictureParameterBufferH264, frame_num) == 620);
    assert!(offset_of!(VAEncPictureParameterBufferH264, pic_init_qp) == 622);
    assert!(
        offset_of!(
            VAEncPictureParameterBufferH264,
            second_chroma_qp_index_offset
        ) == 626
    );
    assert!(offset_of!(VAEncPictureParameterBufferH264, pic_fields) == 628);
    assert!(offset_of!(VAEncPictureParameterBufferH264, va_reserved) == 632);
    assert!(size_of::<VAEncSliceParameterBufferH264>() == 3140);
    assert!(offset_of!(VAEncSliceParameterBufferH264, slice_type) == 12);
    assert!(offset_of!(VAEncSliceParameterBufferH264, idr_pic_id) == 14);
    assert!(offset_of!(VAEncSliceParameterBufferH264, pic_order_cnt_lsb) == 16);
    assert!(offset_of!(VAEncSliceParameterBufferH264, delta_pic_order_cnt_bottom) == 20);
    assert!(offset_of!(VAEncSliceParameterBufferH264, direct_spatial_mv_pred_flag) == 32);
    assert!(offset_of!(VAEncSliceParameterBufferH264, num_ref_idx_l1_active_minus1) == 35);
    assert!(offset_of!(VAEncSliceParameterBufferH264, ref_pic_list0) == 36);
    assert!(offset_of!(VAEncSliceParameterBufferH264, ref_pic_list1) == 1188);
    assert!(offset_of!(VAEncSliceParameterBufferH264, luma_log2_weight_denom) == 2340);
    assert!(offset_of!(VAEncSliceParameterBufferH264, luma_weight_l0) == 2344);
    assert!(offset_of!(VAEncSliceParameterBufferH264, chroma_weight_l0_flag) == 2472);
    assert!(offset_of!(VAEncSliceParameterBufferH264, chroma_weight_l0) == 2474);
    assert!(offset_of!(VAEncSliceParameterBufferH264, chroma_offset_l0) == 2602);
    assert!(offset_of!(VAEncSliceParameterBufferH264, luma_weight_l1_flag) == 2730);
    assert!(offset_of!(VAEncSliceParameterBufferH264, luma_weight_l1) == 2732);
    assert!(offset_of!(VAEncSliceParameterBufferH264, chroma_weight_l1) == 2862);
    assert!(offset_of!(VAEncSliceParameterBufferH264, chroma_offset_l1) == 2990);
    assert!(offset_of!(VAEncSliceParameterBufferH264, cabac_init_idc) == 3118);
    assert!(offset_of!(VAEncSliceParameterBufferH264, slice_beta_offset_div2) == 3122);
    assert!(offset_of!(VAEncSliceParameterBufferH264, va_reserved) == 3124);
};

#[cfg(target_pointer_width = "64")]
const _: () = {
    assert!(size_of::<VAGenericValue>() == 16 && offset_of!(VAGenericValue, value) == 8);
    assert!(size_of::<VASurfaceAttrib>() == 24 && offset_of!(VASurfaceAttrib, value) == 8);
    assert!(size_of::<VACodedBufferSegment>() == 48);
    assert!(offset_of!(VACodedBufferSegment, buf) == 16);
    assert!(offset_of!(VACodedBufferSegment, next) == 24);
    assert!(offset_of!(VACodedBufferSegment, va_reserved) == 32);
};

const INVALID_PICTURE: VAPictureH264 = VAPictureH264 {
    picture_id: VA_INVALID_ID,
    frame_idx: 0,
    flags: VA_PICTURE_H264_INVALID,
    top_field_order_cnt: 0,
    bottom_field_order_cnt: 0,
    va_reserved: [0; 4],
};

struct Api {
    get_display_drm: unsafe extern "C" fn(c_int) -> VADisplay,
    initialize: unsafe extern "C" fn(VADisplay, *mut c_int, *mut c_int) -> VAStatus,
    terminate: unsafe extern "C" fn(VADisplay) -> VAStatus,
    error_str: unsafe extern "C" fn(VAStatus) -> *const c_char,
    vendor: unsafe extern "C" fn(VADisplay) -> *const c_char,
    max_profiles: unsafe extern "C" fn(VADisplay) -> c_int,
    max_entrypoints: unsafe extern "C" fn(VADisplay) -> c_int,
    query_profiles: unsafe extern "C" fn(VADisplay, *mut c_int, *mut c_int) -> VAStatus,
    query_entrypoints: unsafe extern "C" fn(VADisplay, c_int, *mut c_int, *mut c_int) -> VAStatus,
    get_config_attributes:
        unsafe extern "C" fn(VADisplay, c_int, c_int, *mut VAConfigAttrib, c_int) -> VAStatus,
    create_config: unsafe extern "C" fn(
        VADisplay,
        c_int,
        c_int,
        *mut VAConfigAttrib,
        c_int,
        *mut u32,
    ) -> VAStatus,
    destroy_config: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    create_surfaces: unsafe extern "C" fn(
        VADisplay,
        c_uint,
        c_uint,
        c_uint,
        *mut u32,
        c_uint,
        *mut VASurfaceAttrib,
        c_uint,
    ) -> VAStatus,
    destroy_surfaces: unsafe extern "C" fn(VADisplay, *mut u32, c_int) -> VAStatus,
    create_context: unsafe extern "C" fn(
        VADisplay,
        u32,
        c_int,
        c_int,
        c_int,
        *mut u32,
        c_int,
        *mut u32,
    ) -> VAStatus,
    destroy_context: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    create_buffer: unsafe extern "C" fn(
        VADisplay,
        u32,
        c_int,
        c_uint,
        c_uint,
        *mut c_void,
        *mut u32,
    ) -> VAStatus,
    destroy_buffer: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    map_buffer: unsafe extern "C" fn(VADisplay, u32, *mut *mut c_void) -> VAStatus,
    unmap_buffer: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    begin_picture: unsafe extern "C" fn(VADisplay, u32, u32) -> VAStatus,
    render_picture: unsafe extern "C" fn(VADisplay, u32, *mut u32, c_int) -> VAStatus,
    end_picture: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    sync_surface: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    derive_image: unsafe extern "C" fn(VADisplay, u32, *mut VAImage) -> VAStatus,
    create_image:
        unsafe extern "C" fn(VADisplay, *mut VAImageFormat, c_int, c_int, *mut VAImage) -> VAStatus,
    destroy_image: unsafe extern "C" fn(VADisplay, u32) -> VAStatus,
    put_image: unsafe extern "C" fn(
        VADisplay,
        u32,
        u32,
        c_int,
        c_int,
        c_uint,
        c_uint,
        c_int,
        c_int,
        c_uint,
        c_uint,
    ) -> VAStatus,
    _drm: Library,
    _va: Library,
}

fn library(names: &[&str]) -> Result<Library, CaptureError> {
    let mut last = String::new();
    for name in names {
        // SAFETY: loading the system libva runs only its own initialisers;
        // no symbols are used until they are resolved with their va.h types.
        match unsafe { Library::new(name) } {
            Ok(lib) => return Ok(lib),
            Err(e) => last = format!("{name}: {e}"),
        }
    }
    Err(CaptureError(format!("VAAPI is not installed ({last})")))
}

fn symbol<T: Copy>(lib: &Library, name: &str) -> Result<T, CaptureError> {
    // SAFETY: every caller assigns the result to a field whose function
    // pointer type matches the va.h / va_drm.h (VA-API 1.22) prototype of
    // `name`, and the owning Library is kept alive in the same Api.
    unsafe { lib.get::<T>(name.as_bytes()) }
        .map(|s| *s)
        .map_err(|e| CaptureError(format!("libva is missing {name}: {e}")))
}

impl Api {
    fn load() -> Result<Self, CaptureError> {
        let va = library(&["libva.so.2", "libva.so"])?;
        let drm = library(&["libva-drm.so.2", "libva-drm.so"])?;
        Ok(Self {
            get_display_drm: symbol(&drm, "vaGetDisplayDRM")?,
            initialize: symbol(&va, "vaInitialize")?,
            terminate: symbol(&va, "vaTerminate")?,
            error_str: symbol(&va, "vaErrorStr")?,
            vendor: symbol(&va, "vaQueryVendorString")?,
            max_profiles: symbol(&va, "vaMaxNumProfiles")?,
            max_entrypoints: symbol(&va, "vaMaxNumEntrypoints")?,
            query_profiles: symbol(&va, "vaQueryConfigProfiles")?,
            query_entrypoints: symbol(&va, "vaQueryConfigEntrypoints")?,
            get_config_attributes: symbol(&va, "vaGetConfigAttributes")?,
            create_config: symbol(&va, "vaCreateConfig")?,
            destroy_config: symbol(&va, "vaDestroyConfig")?,
            create_surfaces: symbol(&va, "vaCreateSurfaces")?,
            destroy_surfaces: symbol(&va, "vaDestroySurfaces")?,
            create_context: symbol(&va, "vaCreateContext")?,
            destroy_context: symbol(&va, "vaDestroyContext")?,
            create_buffer: symbol(&va, "vaCreateBuffer")?,
            destroy_buffer: symbol(&va, "vaDestroyBuffer")?,
            map_buffer: symbol(&va, "vaMapBuffer")?,
            unmap_buffer: symbol(&va, "vaUnmapBuffer")?,
            begin_picture: symbol(&va, "vaBeginPicture")?,
            render_picture: symbol(&va, "vaRenderPicture")?,
            end_picture: symbol(&va, "vaEndPicture")?,
            sync_surface: symbol(&va, "vaSyncSurface")?,
            derive_image: symbol(&va, "vaDeriveImage")?,
            create_image: symbol(&va, "vaCreateImage")?,
            destroy_image: symbol(&va, "vaDestroyImage")?,
            put_image: symbol(&va, "vaPutImage")?,
            _drm: drm,
            _va: va,
        })
    }

    fn describe(&self, status: VAStatus) -> String {
        // SAFETY: vaErrorStr returns a pointer to a static string (or null).
        let text = unsafe { (self.error_str)(status) };
        if text.is_null() {
            return format!("VA status {status:#x}");
        }
        // SAFETY: non-null, NUL-terminated and static per libva.
        let text = unsafe { CStr::from_ptr(text) }.to_string_lossy();
        format!("{text} ({status:#x})")
    }

    fn check(&self, status: VAStatus, what: &str) -> Result<(), CaptureError> {
        if status == VA_STATUS_SUCCESS {
            Ok(())
        } else {
            Err(CaptureError(format!("{what}: {}", self.describe(status))))
        }
    }
}

struct Choice {
    profile: Profile,
    va_profile: c_int,
    entrypoint: c_int,
    rc_mode: u32,
    packed: Option<u32>,
}

pub(crate) struct VaapiEncoder {
    api: Api,
    display: VADisplay,
    config: u32,
    context: u32,
    surfaces: Vec<u32>,
    coded: u32,
    put_image: Option<VAImage>,
    choice: Option<Choice>,
    sps: Sps,
    gop: Gop,
    vendor: String,
    width: u32,
    height: u32,
    aligned: (u32, u32),
    bitrate_kbps: u32,
    rc_dirty: bool,
    cur: usize,
    inputs_used: u64,
    last: Option<Frame>,
    headers: Vec<u8>,
    _fd: OwnedFd,
}

impl VaapiEncoder {
    pub(crate) fn open(
        width: u32,
        height: u32,
        fps: u32,
        bitrate_kbps: u32,
        gop_frames: u32,
    ) -> Result<Self, CaptureError> {
        if width < 2
            || height < 2
            || !width.is_multiple_of(2)
            || !height.is_multiple_of(2)
            || width > crate::MAX_DIMENSION
            || height > crate::MAX_DIMENSION
        {
            return Err(CaptureError(format!(
                "VAAPI H.264 cannot encode {width}x{height}"
            )));
        }
        let mut errors = Vec::new();
        for n in 128..=135 {
            let path = format!("/dev/dri/renderD{n}");
            if !std::path::Path::new(&path).exists() {
                continue;
            }
            let driver = std::fs::read_link(format!("/sys/class/drm/renderD{n}/device/driver"))
                .ok()
                .and_then(|p| Some(p.file_name()?.to_string_lossy().into_owned()));
            if driver.as_deref() == Some("nvidia") {
                errors.push(format!("{path}: the NVIDIA driver has no VAAPI encoder"));
                continue;
            }
            match Self::open_node(
                &path,
                width,
                height,
                fps.max(1),
                bitrate_kbps.max(1),
                gop_frames,
            ) {
                Ok(encoder) => return Ok(encoder),
                Err(e) => errors.push(format!("{path}: {}", e.0)),
            }
        }
        if errors.is_empty() {
            return Err(CaptureError(
                "no DRM render node (/dev/dri/renderD128-135) for VAAPI".into(),
            ));
        }
        Err(CaptureError(format!(
            "VAAPI H.264 encoder unavailable: {}",
            errors.join("; ")
        )))
    }

    fn open_node(
        path: &str,
        width: u32,
        height: u32,
        fps: u32,
        bitrate_kbps: u32,
        gop_frames: u32,
    ) -> Result<Self, CaptureError> {
        let api = Api::load()?;
        let c_path = CString::new(path).map_err(|_| CaptureError("bad device path".into()))?;
        // SAFETY: plain open(2) of a NUL-terminated path.
        let raw = unsafe { libc::open(c_path.as_ptr(), libc::O_RDWR | libc::O_CLOEXEC) };
        if raw < 0 {
            return Err(CaptureError(format!(
                "open: {}",
                std::io::Error::last_os_error()
            )));
        }
        // SAFETY: `raw` is a freshly opened descriptor owned by nobody else.
        let fd = unsafe { OwnedFd::from_raw_fd(raw) };
        // SAFETY: the descriptor stays open for the display's lifetime (it is
        // closed only after vaTerminate in Drop).
        let display = unsafe { (api.get_display_drm)(fd.as_raw_fd()) };
        if display.is_null() {
            return Err(CaptureError("vaGetDisplayDRM returned no display".into()));
        }
        let wmbs = width.div_ceil(16);
        let hmbs = height.div_ceil(16);
        let mut encoder = Self {
            api,
            display,
            config: VA_INVALID_ID,
            context: VA_INVALID_ID,
            surfaces: Vec::new(),
            coded: VA_INVALID_ID,
            put_image: None,
            choice: None,
            sps: Sps::new(Profile::Main, width, height, fps, bitrate_kbps),
            gop: Gop::new(gop_frames),
            vendor: String::new(),
            width,
            height,
            aligned: (wmbs * 16, hmbs * 16),
            bitrate_kbps,
            rc_dirty: true,
            cur: 0,
            inputs_used: 0,
            last: None,
            headers: Vec::new(),
            _fd: fd,
        };
        encoder.init()?;
        Ok(encoder)
    }

    fn init(&mut self) -> Result<(), CaptureError> {
        let (mut major, mut minor) = (0, 0);
        // SAFETY: display came from vaGetDisplayDRM; Drop terminates it even
        // when initialisation fails, as libva requires.
        let status = unsafe { (self.api.initialize)(self.display, &mut major, &mut minor) };
        self.api.check(status, "vaInitialize")?;
        // SAFETY: initialised display; the string is owned by libva.
        let vendor = unsafe { (self.api.vendor)(self.display) };
        self.vendor = if vendor.is_null() {
            format!("VA-API {major}.{minor}")
        } else {
            // SAFETY: non-null NUL-terminated string valid while the display lives.
            unsafe { CStr::from_ptr(vendor) }
                .to_string_lossy()
                .trim()
                .to_string()
        };
        let choice = self.choose()?;
        self.sps = Sps::new(
            choice.profile,
            self.width,
            self.height,
            self.sps.fps,
            self.bitrate_kbps,
        );
        let mut attribs = vec![
            VAConfigAttrib {
                kind: VA_CONFIG_ATTRIB_RT_FORMAT,
                value: VA_RT_FORMAT_YUV420,
            },
            VAConfigAttrib {
                kind: VA_CONFIG_ATTRIB_RATE_CONTROL,
                value: choice.rc_mode,
            },
        ];
        if let Some(packed) = choice.packed.filter(|&p| p != 0) {
            attribs.push(VAConfigAttrib {
                kind: VA_CONFIG_ATTRIB_ENC_PACKED_HEADERS,
                value: packed,
            });
        }
        // SAFETY: the attribute array outlives the call; the id is written
        // only on success.
        let status = unsafe {
            (self.api.create_config)(
                self.display,
                choice.va_profile,
                choice.entrypoint,
                attribs.as_mut_ptr(),
                attribs.len() as c_int,
                &mut self.config,
            )
        };
        if status != VA_STATUS_SUCCESS {
            self.config = VA_INVALID_ID;
        }
        self.api.check(status, "vaCreateConfig")?;
        self.choice = Some(choice);
        self.create_surfaces()?;
        let (aw, ah) = self.aligned;
        let mut context = VA_INVALID_ID;
        // SAFETY: config and surfaces are live; the surface list outlives the call.
        let status = unsafe {
            (self.api.create_context)(
                self.display,
                self.config,
                aw as c_int,
                ah as c_int,
                VA_PROGRESSIVE,
                self.surfaces.as_mut_ptr(),
                self.surfaces.len() as c_int,
                &mut context,
            )
        };
        self.api.check(status, "vaCreateContext")?;
        self.context = context;
        let coded_size = aw * ah * 3 / 2 + (1 << 16);
        let mut coded = VA_INVALID_ID;
        // SAFETY: a driver-allocated coded buffer on the live context.
        let status = unsafe {
            (self.api.create_buffer)(
                self.display,
                self.context,
                VA_ENC_CODED_BUFFER,
                coded_size,
                1,
                std::ptr::null_mut(),
                &mut coded,
            )
        };
        self.api.check(status, "vaCreateBuffer (coded)")?;
        self.coded = coded;
        self.choose_upload()?;
        self.headers = self.sps.sps_nal();
        self.headers.extend(self.sps.pps_nal());
        Ok(())
    }

    fn choose(&self) -> Result<Choice, CaptureError> {
        // SAFETY: initialised display.
        let max = unsafe { (self.api.max_profiles)(self.display) }.max(1) as usize;
        let mut profiles = vec![0 as c_int; max];
        let mut count = 0 as c_int;
        // SAFETY: the list holds vaMaxNumProfiles entries as libva requires.
        let status =
            unsafe { (self.api.query_profiles)(self.display, profiles.as_mut_ptr(), &mut count) };
        self.api.check(status, "vaQueryConfigProfiles")?;
        profiles.truncate((count.max(0) as usize).min(max));
        let mut reasons = Vec::new();
        for (profile, va_profile) in [
            (Profile::Main, VA_PROFILE_H264_MAIN),
            (
                Profile::ConstrainedBaseline,
                VA_PROFILE_H264_CONSTRAINED_BASELINE,
            ),
        ] {
            if !profiles.contains(&va_profile) {
                continue;
            }
            // SAFETY: initialised display.
            let max = unsafe { (self.api.max_entrypoints)(self.display) }.max(1) as usize;
            let mut entrypoints = vec![0 as c_int; max];
            let mut count = 0 as c_int;
            // SAFETY: the list holds vaMaxNumEntrypoints entries.
            let status = unsafe {
                (self.api.query_entrypoints)(
                    self.display,
                    va_profile,
                    entrypoints.as_mut_ptr(),
                    &mut count,
                )
            };
            if status != VA_STATUS_SUCCESS {
                continue;
            }
            entrypoints.truncate((count.max(0) as usize).min(max));
            for entrypoint in [VA_ENTRYPOINT_ENC_SLICE, VA_ENTRYPOINT_ENC_SLICE_LP] {
                if !entrypoints.contains(&entrypoint) {
                    continue;
                }
                match self.attributes(va_profile, entrypoint) {
                    Ok((rc_mode, packed)) => {
                        return Ok(Choice {
                            profile,
                            va_profile,
                            entrypoint,
                            rc_mode,
                            packed,
                        });
                    }
                    Err(e) => reasons.push(format!("{} {}", profile.name(), e.0)),
                }
            }
        }
        if reasons.is_empty() {
            return Err(CaptureError(format!(
                "{} has no H.264 Main or Constrained Baseline encoder",
                self.vendor
            )));
        }
        Err(CaptureError(format!(
            "{}: {}",
            self.vendor,
            reasons.join(", ")
        )))
    }

    fn attributes(
        &self,
        va_profile: c_int,
        entrypoint: c_int,
    ) -> Result<(u32, Option<u32>), CaptureError> {
        let mut attribs = [
            VA_CONFIG_ATTRIB_RT_FORMAT,
            VA_CONFIG_ATTRIB_RATE_CONTROL,
            VA_CONFIG_ATTRIB_ENC_PACKED_HEADERS,
            VA_CONFIG_ATTRIB_ENC_MAX_REF_FRAMES,
        ]
        .map(|kind| VAConfigAttrib { kind, value: 0 });
        // SAFETY: the array outlives the call and its length is passed.
        let status = unsafe {
            (self.api.get_config_attributes)(
                self.display,
                va_profile,
                entrypoint,
                attribs.as_mut_ptr(),
                attribs.len() as c_int,
            )
        };
        self.api.check(status, "vaGetConfigAttributes")?;
        let [rt, rc, packed, refs] = attribs.map(|a| a.value);
        if rt == VA_ATTRIB_NOT_SUPPORTED || rt & VA_RT_FORMAT_YUV420 == 0 {
            return Err(CaptureError("has no YUV 4:2:0 input".into()));
        }
        let rc_mode = if rc == VA_ATTRIB_NOT_SUPPORTED {
            0
        } else if rc & VA_RC_CBR != 0 {
            VA_RC_CBR
        } else if rc & VA_RC_VBR != 0 {
            VA_RC_VBR
        } else {
            0
        };
        if rc_mode == 0 {
            return Err(CaptureError("supports neither CBR nor VBR".into()));
        }
        if refs != VA_ATTRIB_NOT_SUPPORTED && refs & 0xffff < MAX_REF_FRAMES {
            return Err(CaptureError("cannot encode P frames".into()));
        }
        let packed = (packed != VA_ATTRIB_NOT_SUPPORTED).then_some(
            packed
                & (VA_ENC_PACKED_HEADER_SEQUENCE
                    | VA_ENC_PACKED_HEADER_PICTURE
                    | VA_ENC_PACKED_HEADER_SLICE),
        );
        Ok((rc_mode, packed))
    }

    fn create_surfaces(&mut self) -> Result<(), CaptureError> {
        let (aw, ah) = self.aligned;
        let count = INPUT_SURFACES + RECON_SURFACES;
        let mut surfaces = vec![VA_INVALID_ID; count];
        let mut attrib = VASurfaceAttrib {
            kind: VA_SURFACE_ATTRIB_PIXEL_FORMAT,
            flags: VA_SURFACE_ATTRIB_SETTABLE,
            value: VAGenericValue {
                kind: VA_GENERIC_VALUE_TYPE_INTEGER,
                value: VAGenericValueUnion {
                    i: VA_FOURCC_NV12 as i32,
                },
            },
        };
        // SAFETY: the surface array holds `count` ids and the attribute
        // outlives the call.
        let mut status = unsafe {
            (self.api.create_surfaces)(
                self.display,
                VA_RT_FORMAT_YUV420,
                aw,
                ah,
                surfaces.as_mut_ptr(),
                count as c_uint,
                &mut attrib,
                1,
            )
        };
        if status != VA_STATUS_SUCCESS {
            surfaces.fill(VA_INVALID_ID);
            // SAFETY: as above, without the optional pixel format hint.
            status = unsafe {
                (self.api.create_surfaces)(
                    self.display,
                    VA_RT_FORMAT_YUV420,
                    aw,
                    ah,
                    surfaces.as_mut_ptr(),
                    count as c_uint,
                    std::ptr::null_mut(),
                    0,
                )
            };
        }
        self.api.check(status, "vaCreateSurfaces")?;
        self.surfaces = surfaces;
        Ok(())
    }

    fn input_surface(&self, n: u64) -> u32 {
        self.surfaces[(n % INPUT_SURFACES as u64) as usize]
    }

    fn recon_surface(&self, n: usize) -> u32 {
        self.surfaces[INPUT_SURFACES + n % RECON_SURFACES]
    }

    fn choose_upload(&mut self) -> Result<(), CaptureError> {
        let mut image = VAImage::default();
        // SAFETY: deriving an image from a live, idle surface.
        let status =
            unsafe { (self.api.derive_image)(self.display, self.input_surface(0), &mut image) };
        if status == VA_STATUS_SUCCESS {
            let usable = image.format.fourcc == VA_FOURCC_NV12 && image.num_planes >= 2;
            // SAFETY: the derived image was created just above.
            unsafe { (self.api.destroy_image)(self.display, image.image_id) };
            if usable {
                return Ok(());
            }
        }
        let (aw, ah) = self.aligned;
        let mut format = VAImageFormat {
            fourcc: VA_FOURCC_NV12,
            byte_order: VA_LSB_FIRST,
            bits_per_pixel: 12,
            ..VAImageFormat::default()
        };
        let mut image = VAImage::default();
        // SAFETY: format and image structs outlive the call.
        let status = unsafe {
            (self.api.create_image)(
                self.display,
                &mut format,
                aw as c_int,
                ah as c_int,
                &mut image,
            )
        };
        self.api.check(status, "vaCreateImage (NV12)")?;
        self.put_image = Some(image);
        Ok(())
    }

    pub(crate) fn name(&self) -> String {
        format!("VAAPI H.264 ({})", self.vendor)
    }

    pub(crate) fn set_bitrate(&mut self, kbps: u32) {
        let kbps = kbps.max(1);
        if kbps != self.bitrate_kbps {
            self.bitrate_kbps = kbps;
            self.rc_dirty = true;
        }
    }

    pub(crate) fn encode(
        &mut self,
        nv12: &[u8],
        pts_us: i64,
        force_key: bool,
    ) -> Result<Option<EncodedVideo>, CaptureError> {
        let (w, h) = (self.width as usize, self.height as usize);
        if nv12.len() < w * h * 3 / 2 {
            return Err(CaptureError(format!(
                "NV12 frame is {} bytes, {}x{} needs {}",
                nv12.len(),
                w,
                h,
                w * h * 3 / 2
            )));
        }
        let frame = self.gop.next(force_key || self.last.is_none());
        let input = self.input_surface(self.inputs_used);
        self.inputs_used = self.inputs_used.wrapping_add(1);
        match self.encode_frame(nv12, input, &frame) {
            Ok((mut data, overflow)) => {
                self.cur ^= 1;
                self.rc_dirty = false;
                self.last = Some(frame);
                if overflow {
                    self.gop.restart();
                }
                if data.is_empty() {
                    self.gop.restart();
                    return Ok(None);
                }
                if frame.idr {
                    if has_parameter_sets(&data) {
                        self.headers = parameter_sets(&data);
                    } else if !self.headers.is_empty() {
                        let mut joined = self.headers.clone();
                        joined.extend_from_slice(&data);
                        data = joined;
                    }
                }
                Ok(Some(EncodedVideo {
                    data,
                    pts_us,
                    key: frame.idr,
                    width: self.width,
                    height: self.height,
                }))
            }
            Err(e) => {
                self.last = None;
                Err(CaptureError(format!("{} ({})", e.0, self.name())))
            }
        }
    }

    fn upload(&self, surface: u32, nv12: &[u8]) -> Result<(), CaptureError> {
        let (image, derived) = match self.put_image {
            Some(image) => (image, false),
            None => {
                let mut image = VAImage::default();
                // SAFETY: deriving an image from a live surface that is idle
                // (every submitted picture is synced before the next).
                let status = unsafe { (self.api.derive_image)(self.display, surface, &mut image) };
                self.api.check(status, "vaDeriveImage")?;
                (image, true)
            }
        };
        let written = self.write_image(&image, nv12);
        let result = written.and_then(|()| {
            if derived {
                return Ok(());
            }
            let (aw, ah) = self.aligned;
            // SAFETY: the image and surface are live and the same size.
            let status = unsafe {
                (self.api.put_image)(
                    self.display,
                    surface,
                    image.image_id,
                    0,
                    0,
                    aw,
                    ah,
                    0,
                    0,
                    aw,
                    ah,
                )
            };
            self.api.check(status, "vaPutImage")
        });
        if derived {
            // SAFETY: the derived image belongs to this call only.
            unsafe { (self.api.destroy_image)(self.display, image.image_id) };
        }
        result
    }

    fn write_image(&self, image: &VAImage, nv12: &[u8]) -> Result<(), CaptureError> {
        let mut ptr = std::ptr::null_mut();
        // SAFETY: mapping the image's backing buffer; unmapped below.
        let status = unsafe { (self.api.map_buffer)(self.display, image.buf, &mut ptr) };
        self.api.check(status, "vaMapBuffer (image)")?;
        if ptr.is_null() {
            // SAFETY: balancing the successful map above.
            unsafe { (self.api.unmap_buffer)(self.display, image.buf) };
            return Err(CaptureError("vaMapBuffer returned null".into()));
        }
        // SAFETY: libva maps data_size bytes of the image buffer for CPU
        // access; the slice is dropped before the unmap.
        let dst =
            unsafe { std::slice::from_raw_parts_mut(ptr.cast::<u8>(), image.data_size as usize) };
        let (aw, ah) = self.aligned;
        let ok = image.num_planes >= 2
            && copy_nv12(
                nv12,
                self.width as usize,
                self.height as usize,
                dst,
                image.offsets[0] as usize,
                image.pitches[0] as usize,
                image.offsets[1] as usize,
                image.pitches[1] as usize,
                (aw as usize).min(usize::from(image.width)),
                (ah as usize).min(usize::from(image.height)),
            );
        // SAFETY: balancing the successful map above.
        let status = unsafe { (self.api.unmap_buffer)(self.display, image.buf) };
        if !ok {
            return Err(CaptureError(format!(
                "VA image layout {}x{} pitches {:?} offsets {:?} size {} does not fit {}x{}",
                image.width,
                image.height,
                image.pitches,
                image.offsets,
                image.data_size,
                self.width,
                self.height
            )));
        }
        self.api.check(status, "vaUnmapBuffer (image)")
    }

    fn buffer<T>(&self, ids: &mut Vec<u32>, kind: c_int, value: &T) -> Result<(), CaptureError> {
        self.raw_buffer(
            ids,
            kind,
            (value as *const T).cast::<c_void>(),
            size_of::<T>(),
        )
    }

    fn raw_buffer(
        &self,
        ids: &mut Vec<u32>,
        kind: c_int,
        data: *const c_void,
        len: usize,
    ) -> Result<(), CaptureError> {
        let mut id = VA_INVALID_ID;
        // SAFETY: `data` points at `len` readable bytes that libva copies
        // into a new buffer; it never writes through the pointer.
        let status = unsafe {
            (self.api.create_buffer)(
                self.display,
                self.context,
                kind,
                len as c_uint,
                1,
                data.cast_mut(),
                &mut id,
            )
        };
        self.api.check(status, "vaCreateBuffer")?;
        ids.push(id);
        Ok(())
    }

    fn packed(
        &self,
        ids: &mut Vec<u32>,
        kind: u32,
        data: &[u8],
        bits: u32,
    ) -> Result<(), CaptureError> {
        let param = VAEncPackedHeaderParameterBuffer {
            kind,
            bit_length: bits,
            has_emulation_bytes: 1,
            va_reserved: [0; 4],
        };
        self.buffer(ids, VA_ENC_PACKED_HEADER_PARAMETER_BUFFER, &param)?;
        self.raw_buffer(
            ids,
            VA_ENC_PACKED_HEADER_DATA_BUFFER,
            data.as_ptr().cast(),
            data.len(),
        )
    }

    fn picture(&self, surface: u32, frame: &Frame, reference: bool) -> VAPictureH264 {
        VAPictureH264 {
            picture_id: surface,
            frame_idx: frame.frame_num,
            flags: if reference {
                VA_PICTURE_H264_SHORT_TERM_REFERENCE
            } else {
                0
            },
            top_field_order_cnt: frame.poc,
            bottom_field_order_cnt: frame.poc,
            va_reserved: [0; 4],
        }
    }

    fn sequence_params(&self) -> VAEncSequenceParameterBufferH264 {
        // SAFETY: an all-integer repr(C) struct; all-zero is a valid value.
        let mut seq: VAEncSequenceParameterBufferH264 = unsafe { std::mem::zeroed() };
        let sps = &self.sps;
        seq.level_idc = sps.level_idc;
        seq.intra_period = self.gop.period();
        seq.intra_idr_period = self.gop.period();
        seq.ip_period = 1;
        seq.bits_per_second = self.bitrate_kbps.saturating_mul(1000);
        seq.max_num_ref_frames = MAX_REF_FRAMES;
        seq.picture_width_in_mbs = sps.width_mbs() as u16;
        seq.picture_height_in_mbs = sps.height_mbs() as u16;
        seq.seq_fields = sps.va_seq_fields();
        seq.frame_cropping_flag = u8::from(sps.cropped());
        seq.frame_crop_right_offset = sps.crop_right();
        seq.frame_crop_bottom_offset = sps.crop_bottom();
        seq.vui_parameters_present_flag = 1;
        seq.vui_fields = sps.va_vui_fields();
        seq.aspect_ratio_idc = ASPECT_RATIO_SQUARE;
        seq.num_units_in_tick = sps.num_units_in_tick();
        seq.time_scale = sps.time_scale();
        seq
    }

    fn rate_control(&self, ids: &mut Vec<u32>) -> Result<(), CaptureError> {
        let bits = self.bitrate_kbps.saturating_mul(1000);
        self.buffer(
            ids,
            VA_ENC_MISC_PARAMETER_BUFFER,
            &VAEncMiscParameterBuffer {
                kind: VA_ENC_MISC_PARAMETER_TYPE_RATE_CONTROL,
                data: VAEncMiscParameterRateControl {
                    bits_per_second: bits,
                    target_percentage: 100,
                    window_size: 1000,
                    initial_qp: 0,
                    min_qp: 0,
                    basic_unit_size: 0,
                    rc_flags: 0,
                    icq_quality_factor: 0,
                    max_qp: 0,
                    quality_factor: 0,
                    target_frame_size: 0,
                    va_reserved: [0; 4],
                },
            },
        )?;
        self.buffer(
            ids,
            VA_ENC_MISC_PARAMETER_BUFFER,
            &VAEncMiscParameterBuffer {
                kind: VA_ENC_MISC_PARAMETER_TYPE_HRD,
                data: VAEncMiscParameterHRD {
                    initial_buffer_fullness: bits / 4 * 3,
                    buffer_size: bits,
                    va_reserved: [0; 4],
                },
            },
        )?;
        self.buffer(
            ids,
            VA_ENC_MISC_PARAMETER_BUFFER,
            &VAEncMiscParameterBuffer {
                kind: VA_ENC_MISC_PARAMETER_TYPE_FRAME_RATE,
                data: VAEncMiscParameterFrameRate {
                    framerate: self.sps.fps,
                    framerate_flags: 0,
                    va_reserved: [0; 4],
                },
            },
        )
    }

    fn encode_frame(
        &mut self,
        nv12: &[u8],
        input: u32,
        frame: &Frame,
    ) -> Result<(Vec<u8>, bool), CaptureError> {
        self.upload(input, nv12)?;
        let mut ids = Vec::with_capacity(12);
        let result = self.submit(&mut ids, input, frame);
        for id in ids {
            // SAFETY: parameter buffers created for this picture only.
            unsafe { (self.api.destroy_buffer)(self.display, id) };
        }
        result?;
        // SAFETY: waits for the picture rendered into the live input surface.
        let status = unsafe { (self.api.sync_surface)(self.display, input) };
        self.api.check(status, "vaSyncSurface")?;
        self.read_coded()
    }

    fn submit(&self, ids: &mut Vec<u32>, input: u32, frame: &Frame) -> Result<(), CaptureError> {
        let packed = self.choice.as_ref().and_then(|c| c.packed).unwrap_or(0);
        let recon = self.recon_surface(self.cur);
        let reference = self.recon_surface(self.cur ^ 1);
        let last = self.last.filter(|_| !frame.idr);
        if frame.idr {
            self.buffer(
                ids,
                VA_ENC_SEQUENCE_PARAMETER_BUFFER,
                &self.sequence_params(),
            )?;
        }
        if frame.idr || self.rc_dirty {
            self.rate_control(ids)?;
        }
        let mut reference_frames = [INVALID_PICTURE; 16];
        if let Some(last) = &last {
            reference_frames[0] = self.picture(reference, last, true);
        }
        let pic = VAEncPictureParameterBufferH264 {
            curr_pic: self.picture(recon, frame, false),
            reference_frames,
            coded_buf: self.coded,
            pic_parameter_set_id: 0,
            seq_parameter_set_id: 0,
            last_picture: 0,
            frame_num: frame.frame_num as u16,
            pic_init_qp: PIC_INIT_QP,
            num_ref_idx_l0_active_minus1: 0,
            num_ref_idx_l1_active_minus1: 0,
            chroma_qp_index_offset: 0,
            second_chroma_qp_index_offset: 0,
            pic_fields: self.sps.va_pic_fields(frame.idr),
            va_reserved: [0; 4],
        };
        self.buffer(ids, VA_ENC_PICTURE_PARAMETER_BUFFER, &pic)?;
        if frame.idr && packed & VA_ENC_PACKED_HEADER_SEQUENCE != 0 {
            let mut data = self.sps.sps_nal();
            if packed & VA_ENC_PACKED_HEADER_PICTURE == 0 {
                data.extend(self.sps.pps_nal());
            }
            let bits = data.len() as u32 * 8;
            self.packed(ids, VA_ENC_PACKED_HEADER_TYPE_SEQUENCE, &data, bits)?;
        }
        if frame.idr && packed & VA_ENC_PACKED_HEADER_PICTURE != 0 {
            let data = self.sps.pps_nal();
            let bits = data.len() as u32 * 8;
            self.packed(ids, VA_ENC_PACKED_HEADER_TYPE_PICTURE, &data, bits)?;
        }
        if packed & VA_ENC_PACKED_HEADER_SLICE != 0 {
            let (data, bits) = self.sps.slice_header(frame);
            self.packed(ids, VA_ENC_PACKED_HEADER_TYPE_SLICE, &data, bits)?;
        }
        // SAFETY: an all-integer repr(C) struct; all-zero is a valid value.
        let mut slice: VAEncSliceParameterBufferH264 = unsafe { std::mem::zeroed() };
        slice.num_macroblocks = self.sps.width_mbs() * self.sps.height_mbs();
        slice.macroblock_info = VA_INVALID_ID;
        slice.slice_type = frame.slice_type();
        slice.idr_pic_id = frame.idr_pic_id;
        slice.ref_pic_list0 = [INVALID_PICTURE; 32];
        slice.ref_pic_list1 = [INVALID_PICTURE; 32];
        if let Some(last) = &last {
            slice.ref_pic_list0[0] = self.picture(reference, last, true);
        }
        self.buffer(ids, VA_ENC_SLICE_PARAMETER_BUFFER, &slice)?;
        // SAFETY: context and input surface are live; the buffer id list
        // outlives vaRenderPicture, which only reads it.
        unsafe {
            self.api.check(
                (self.api.begin_picture)(self.display, self.context, input),
                "vaBeginPicture",
            )?;
            let rendered = self.api.check(
                (self.api.render_picture)(
                    self.display,
                    self.context,
                    ids.as_mut_ptr(),
                    ids.len() as c_int,
                ),
                "vaRenderPicture",
            );
            let ended = self.api.check(
                (self.api.end_picture)(self.display, self.context),
                "vaEndPicture",
            );
            rendered.and(ended)
        }
    }

    fn read_coded(&self) -> Result<(Vec<u8>, bool), CaptureError> {
        let mut ptr = std::ptr::null_mut();
        // SAFETY: mapping the coded buffer after its picture was synced.
        let status = unsafe { (self.api.map_buffer)(self.display, self.coded, &mut ptr) };
        self.api.check(status, "vaMapBuffer (coded)")?;
        let mut out = Vec::new();
        let mut overflow = false;
        let mut segment = ptr.cast::<VACodedBufferSegment>().cast_const();
        let mut count = 0;
        while !segment.is_null() && count < MAX_SEGMENTS {
            // SAFETY: libva hands back a null-terminated chain of segments
            // that stay valid until vaUnmapBuffer.
            let s = unsafe { &*segment };
            if !s.buf.is_null() && s.size > 0 {
                // SAFETY: each segment's buf holds `size` bytes of bitstream.
                out.extend_from_slice(unsafe {
                    std::slice::from_raw_parts(s.buf.cast::<u8>(), s.size as usize)
                });
            }
            overflow |= s.status & VA_CODED_BUF_STATUS_SLICE_OVERFLOW_MASK != 0;
            segment = s.next.cast::<VACodedBufferSegment>().cast_const();
            count += 1;
        }
        // SAFETY: balancing the successful map above.
        let status = unsafe { (self.api.unmap_buffer)(self.display, self.coded) };
        self.api.check(status, "vaUnmapBuffer (coded)")?;
        Ok((out, overflow))
    }
}

impl Drop for VaapiEncoder {
    fn drop(&mut self) {
        // SAFETY: every id below was created on this display and is
        // destroyed exactly once, children before their parents, before the
        // display is terminated; the DRM fd closes afterwards.
        unsafe {
            if let Some(image) = self.put_image.take() {
                (self.api.destroy_image)(self.display, image.image_id);
            }
            if self.coded != VA_INVALID_ID {
                (self.api.destroy_buffer)(self.display, self.coded);
            }
            if self.context != VA_INVALID_ID {
                (self.api.destroy_context)(self.display, self.context);
            }
            if !self.surfaces.is_empty() {
                (self.api.destroy_surfaces)(
                    self.display,
                    self.surfaces.as_mut_ptr(),
                    self.surfaces.len() as c_int,
                );
            }
            if self.config != VA_INVALID_ID {
                (self.api.destroy_config)(self.display, self.config);
            }
            (self.api.terminate)(self.display);
        }
    }
}
