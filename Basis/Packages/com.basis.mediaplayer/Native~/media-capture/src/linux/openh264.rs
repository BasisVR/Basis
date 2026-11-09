use std::ffi::{c_int, c_void};
use std::mem::{offset_of, size_of};
use std::ptr::null_mut;

use libloading::Library;

use crate::{CaptureError, EncodedVideo};

const USAGE_SCREEN_CONTENT_REAL_TIME: c_int = 1;
const RC_BITRATE_MODE: c_int = 1;
const VIDEO_FORMAT_I420: c_int = 23;
const OPTION_IDR_INTERVAL: c_int = 1;
const OPTION_BITRATE: c_int = 5;
const SPATIAL_LAYER_ALL: c_int = 4;
const FRAME_TYPE_IDR: c_int = 1;
const MAX_LAYERS: usize = 128;
const LAYER_SIZE: usize = 40;
const LAYER_SIZE_PSNR: usize = 56;
const LAYER_NAL_COUNT: usize = 16;
const LAYER_NAL_LENGTHS: usize = 24;
const LAYER_BITSTREAM: usize = 32;
const FRAME_LAYERS: usize = 8;
const FRAME_INFO_WORDS: usize = (FRAME_LAYERS + MAX_LAYERS * LAYER_SIZE_PSNR + 16) / 8;

#[repr(C)]
struct EncParamBase {
    usage: c_int,
    width: c_int,
    height: c_int,
    bitrate: c_int,
    rc_mode: c_int,
    max_fps: f32,
}

#[repr(C)]
struct SourcePicture {
    color_format: c_int,
    stride: [c_int; 4],
    data: [*mut u8; 4],
    width: c_int,
    height: c_int,
    timestamp: i64,
    psnr: [bool; 3],
}

#[repr(C)]
struct BitrateInfo {
    layer: c_int,
    bitrate: c_int,
}

#[repr(C)]
#[derive(Default)]
struct Version {
    major: u32,
    minor: u32,
    revision: u32,
    reserved: u32,
}

type Encoder = *const Vtbl;

#[repr(C)]
struct Vtbl {
    initialize: unsafe extern "C" fn(*mut Encoder, *const EncParamBase) -> c_int,
    initialize_ext: *const c_void,
    get_default_params: *const c_void,
    uninitialize: unsafe extern "C" fn(*mut Encoder) -> c_int,
    encode_frame: unsafe extern "C" fn(*mut Encoder, *const SourcePicture, *mut c_void) -> c_int,
    encode_parameter_sets: *const c_void,
    force_intra_frame: unsafe extern "C" fn(*mut Encoder, bool, c_int) -> c_int,
    set_option: unsafe extern "C" fn(*mut Encoder, c_int, *mut c_void) -> c_int,
    get_option: *const c_void,
}

#[cfg(target_pointer_width = "64")]
const _: () = {
    assert!(size_of::<EncParamBase>() == 24);
    assert!(offset_of!(SourcePicture, data) == 24 && offset_of!(SourcePicture, width) == 56);
    assert!(offset_of!(SourcePicture, timestamp) == 64);
    assert!(offset_of!(Vtbl, encode_frame) == 32 && offset_of!(Vtbl, set_option) == 56);
};

struct Api {
    create: unsafe extern "C" fn(*mut *mut Encoder) -> c_int,
    destroy: unsafe extern "C" fn(*mut Encoder),
    layer_size: usize,
    _lib: Library,
}

fn load() -> Result<Api, CaptureError> {
    let mut last = String::new();
    #[cfg(not(windows))]
    const NAMES: [&str; 5] = [
        "libopenh264.so.8",
        "libopenh264.so.7",
        "libopenh264.so.6",
        "libopenh264.so.5",
        "libopenh264.so",
    ];
    #[cfg(windows)]
    const NAMES: [&str; 1] = ["openh264-2.6.0-win64.dll"];
    for name in NAMES {
        // SAFETY: loading the system OpenH264 runs only its own initialisers.
        let lib = match unsafe { Library::new(name) } {
            Ok(lib) => lib,
            Err(e) => {
                last = format!("{name}: {e}");
                continue;
            }
        };
        // SAFETY: the types match codec_api.h, and the Library is kept in the same Api.
        let create = unsafe {
            lib.get::<unsafe extern "C" fn(*mut *mut Encoder) -> c_int>(b"WelsCreateSVCEncoder\0")
        }
        .map(|s| *s);
        // SAFETY: as above.
        let destroy =
            unsafe { lib.get::<unsafe extern "C" fn(*mut Encoder)>(b"WelsDestroySVCEncoder\0") }
                .map(|s| *s);
        let (Ok(create), Ok(destroy)) = (create, destroy) else {
            last = format!("{name} has no H.264 encoder entry points");
            continue;
        };
        let mut version = Version::default();
        // SAFETY: WelsGetCodecVersionEx only fills the four-field struct passed to it.
        let get =
            unsafe { lib.get::<unsafe extern "C" fn(*mut Version)>(b"WelsGetCodecVersionEx\0") };
        if let Ok(get) = get {
            // SAFETY: as above.
            unsafe { get(&mut version) };
        }
        let layer_size = if (version.major, version.minor) >= (2, 6) {
            LAYER_SIZE_PSNR
        } else {
            LAYER_SIZE
        };
        return Ok(Api {
            create,
            destroy,
            layer_size,
            _lib: lib,
        });
    }
    Err(CaptureError(format!("OpenH264 is not installed ({last})")))
}

pub(crate) struct OpenH264Encoder {
    api: Api,
    encoder: *mut Encoder,
    width: u32,
    height: u32,
    bitrate_kbps: u32,
    i420: Vec<u8>,
    info: Vec<u64>,
}

impl OpenH264Encoder {
    pub(crate) fn open(
        width: u32,
        height: u32,
        fps: u32,
        bitrate_kbps: u32,
        gop_frames: u32,
    ) -> Result<Self, CaptureError> {
        if width < 2 || height < 2 || !width.is_multiple_of(2) || !height.is_multiple_of(2) {
            return Err(CaptureError(format!(
                "OpenH264 cannot encode {width}x{height}"
            )));
        }
        let api = load()?;
        let mut encoder = null_mut();
        // SAFETY: WelsCreateSVCEncoder writes a new encoder handle on success.
        if unsafe { (api.create)(&mut encoder) } != 0 || encoder.is_null() {
            return Err(CaptureError("OpenH264 encoder could not be created".into()));
        }
        let this = Self {
            api,
            encoder,
            width,
            height,
            bitrate_kbps: bitrate_kbps.max(1),
            i420: Vec::new(),
            info: vec![0; FRAME_INFO_WORDS],
        };
        let param = EncParamBase {
            usage: USAGE_SCREEN_CONTENT_REAL_TIME,
            width: width as c_int,
            height: height as c_int,
            bitrate: this.bits(),
            rc_mode: RC_BITRATE_MODE,
            max_fps: fps.max(1) as f32,
        };
        // SAFETY: the encoder is live and the parameter struct matches SEncParamBase.
        let status = unsafe { ((*this.vtbl()).initialize)(this.encoder, &param) };
        if status != 0 {
            return Err(CaptureError(format!(
                "OpenH264 rejected {width}x{height} at {fps} fps ({status})"
            )));
        }
        let mut interval = gop_frames.max(1) as c_int;
        // SAFETY: IDR_INTERVAL takes a pointer to an int.
        unsafe {
            ((*this.vtbl()).set_option)(
                this.encoder,
                OPTION_IDR_INTERVAL,
                (&raw mut interval).cast(),
            )
        };
        Ok(this)
    }

    fn vtbl(&self) -> *const Vtbl {
        // SAFETY: the handle from WelsCreateSVCEncoder points at its vtable pointer.
        unsafe { *self.encoder }
    }

    fn bits(&self) -> c_int {
        self.bitrate_kbps.saturating_mul(1000).min(i32::MAX as u32) as c_int
    }

    pub(crate) fn name(&self) -> String {
        "OpenH264 (software)".into()
    }

    pub(crate) fn set_bitrate(&mut self, kbps: u32) {
        let kbps = kbps.max(1);
        if kbps == self.bitrate_kbps {
            return;
        }
        self.bitrate_kbps = kbps;
        let mut info = BitrateInfo {
            layer: SPATIAL_LAYER_ALL,
            bitrate: self.bits(),
        };
        // SAFETY: BITRATE takes a pointer to an SBitrateInfo.
        unsafe {
            ((*self.vtbl()).set_option)(self.encoder, OPTION_BITRATE, (&raw mut info).cast())
        };
    }

    pub(crate) fn encode(
        &mut self,
        nv12: &[u8],
        pts_us: i64,
        force_key: bool,
    ) -> Result<Option<EncodedVideo>, CaptureError> {
        let (w, h) = (self.width as usize, self.height as usize);
        let luma = w * h;
        if nv12.len() < luma * 3 / 2 {
            return Err(CaptureError(format!(
                "NV12 frame is {} bytes, {w}x{h} needs {}",
                nv12.len(),
                luma * 3 / 2
            )));
        }
        let quarter = luma / 4;
        self.i420.resize(luma + quarter * 2, 0);
        let (y, chroma) = self.i420.split_at_mut(luma);
        let (u, v) = chroma.split_at_mut(quarter);
        y.copy_from_slice(&nv12[..luma]);
        for ((pair, u), v) in nv12[luma..luma + quarter * 2]
            .as_chunks::<2>()
            .0
            .iter()
            .zip(u.iter_mut())
            .zip(v.iter_mut())
        {
            *u = pair[0];
            *v = pair[1];
        }
        if force_key {
            // SAFETY: the encoder is live.
            unsafe { ((*self.vtbl()).force_intra_frame)(self.encoder, true, -1) };
        }
        let base = self.i420.as_mut_ptr();
        let picture = SourcePicture {
            color_format: VIDEO_FORMAT_I420,
            stride: [w as c_int, (w / 2) as c_int, (w / 2) as c_int, 0],
            // SAFETY: both offsets lie inside the I420 buffer sized above.
            data: unsafe { [base, base.add(luma), base.add(luma + quarter), null_mut()] },
            width: w as c_int,
            height: h as c_int,
            timestamp: pts_us / 1000,
            psnr: [false; 3],
        };
        self.info.fill(0);
        // SAFETY: the picture planes stay alive for the call and the info buffer is large
        // enough for SFrameBSInfo in every OpenH264 release.
        let status = unsafe {
            ((*self.vtbl()).encode_frame)(self.encoder, &picture, self.info.as_mut_ptr().cast())
        };
        if status != 0 {
            return Err(CaptureError(format!(
                "OpenH264 EncodeFrame failed ({status})"
            )));
        }
        let bytes = self.info.as_ptr().cast::<u8>();
        let read = |at: usize| -> c_int {
            // SAFETY: every offset read here lies inside the zeroed info buffer.
            unsafe { bytes.add(at).cast::<c_int>().read_unaligned() }
        };
        let layers = (read(0).max(0) as usize).min(MAX_LAYERS);
        let frame_type = read(FRAME_LAYERS + MAX_LAYERS * self.api.layer_size);
        let mut data = Vec::new();
        for layer in 0..layers {
            let at = FRAME_LAYERS + layer * self.api.layer_size;
            let nals = read(at + LAYER_NAL_COUNT).max(0) as usize;
            // SAFETY: as above; the two pointer fields are 8-byte aligned in the struct.
            let (lengths, stream) = unsafe {
                (
                    bytes
                        .add(at + LAYER_NAL_LENGTHS)
                        .cast::<*const c_int>()
                        .read_unaligned(),
                    bytes
                        .add(at + LAYER_BITSTREAM)
                        .cast::<*const u8>()
                        .read_unaligned(),
                )
            };
            if nals == 0 || lengths.is_null() || stream.is_null() {
                continue;
            }
            // SAFETY: OpenH264 fills iNalCount lengths for the layer.
            let total: usize = unsafe { std::slice::from_raw_parts(lengths, nals) }
                .iter()
                .map(|&n| n.max(0) as usize)
                .sum();
            // SAFETY: the layer's NALs lie back to back in pBsBuf.
            data.extend_from_slice(unsafe { std::slice::from_raw_parts(stream, total) });
        }
        if data.is_empty() {
            return Ok(None);
        }
        Ok(Some(EncodedVideo {
            data,
            pts_us,
            key: frame_type == FRAME_TYPE_IDR,
            width: self.width,
            height: self.height,
        }))
    }
}

impl Drop for OpenH264Encoder {
    fn drop(&mut self) {
        // SAFETY: the encoder was created by this Api and is destroyed exactly once.
        unsafe {
            ((*self.vtbl()).uninitialize)(self.encoder);
            (self.api.destroy)(self.encoder);
        }
    }
}
