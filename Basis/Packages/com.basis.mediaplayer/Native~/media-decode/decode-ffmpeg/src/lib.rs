//! Linux decode on the system's FFmpeg (libavcodec + libavutil), loaded at
//! runtime with dlopen. Nothing is linked or shipped, so the patented
//! codecs are the distribution's, the way Firefox plays H.264 on Linux.
//! FFmpeg 4.4 through 9.0 (libavcodec 58 to 63) are accepted; a library
//! pair whose major versions disagree is skipped.
//!
//! Struct fields are read at fixed offsets. The ones used here were
//! checked against every accepted release's headers and have not moved:
//! AVFrame data/linesize/width/height/nb_samples/format/pts, AVPacket
//! pts/dts/data/size and the head of AVCodecParameters. Everything else
//! goes through AVOptions, which are stable by name.

use std::collections::VecDeque;
use std::ffi::{CStr, c_char, c_int, c_void};
use std::ptr::null_mut;
use std::sync::OnceLock;

use libloading::Library;
use media_decode::{
    AudioDecoder, ColorInfo, DecodeError, Nv12Frame, PcmChunk, SubmitOutcome, VideoDecoder,
    VideoFrame, YuvMatrix, YuvRange, packed_nv12_len,
};

const FRAME_DATA: usize = 0;
const FRAME_LINESIZE: usize = 64;
const FRAME_WIDTH: usize = 104;
const FRAME_HEIGHT: usize = 108;
const FRAME_NB_SAMPLES: usize = 112;
const FRAME_FORMAT: usize = 116;
const FRAME_PTS: usize = 136;
const PACKET_PTS: usize = 8;
const PACKET_DTS: usize = 16;
const PACKET_DATA: usize = 24;
const PACKET_SIZE: usize = 32;
const CODEC_ID: usize = 20;
const PAR_TYPE: usize = 0;
const PAR_ID: usize = 4;
const PAR_EXTRADATA: usize = 16;
const PAR_EXTRADATA_SIZE: usize = 24;

const NOPTS: i64 = i64::MIN;
const PADDING: usize = 64;
const EAGAIN: c_int = -11;
const EOF: c_int = -0x2046_4F45;
const INVALID_DATA: c_int = -0x4144_4E49;
const MEDIA_VIDEO: c_int = 0;
const MEDIA_AUDIO: c_int = 1;
const LOG_QUIET: c_int = -8;
const READY_CAP: usize = 64;

const PIX_YUV420P: c_int = 0;
const PIX_YUVJ420P: c_int = 12;
const PIX_NV12: c_int = 23;

/// The codecs this adapter is asked for. Decoders are found by name, since
/// FFmpeg 9 renumbered codec ids; the id comes from the decoder found.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FfCodec {
    H264,
    H265,
    Vp9,
    Vp8,
    Aac,
    Mp3,
}

impl FfCodec {
    fn decoders(self) -> &'static [&'static CStr] {
        match self {
            FfCodec::H264 => &[c"h264", c"libopenh264"],
            FfCodec::H265 => &[c"hevc"],
            FfCodec::Vp8 => &[c"vp8", c"libvpx"],
            FfCodec::Vp9 => &[c"vp9", c"libvpx-vp9"],
            FfCodec::Aac => &[c"aac", c"aac_fixed", c"libfdk_aac"],
            FfCodec::Mp3 => &[c"mp3float", c"mp3"],
        }
    }

    fn name(self) -> &'static str {
        match self {
            FfCodec::H264 => "H.264",
            FfCodec::H265 => "H.265",
            FfCodec::Vp9 => "VP9",
            FfCodec::Vp8 => "VP8",
            FfCodec::Aac => "AAC",
            FfCodec::Mp3 => "MP3",
        }
    }
}

type Raw = *mut c_void;

struct Api {
    _avcodec: Library,
    _avutil: Library,
    label: String,
    find_decoder: unsafe extern "C" fn(*const c_char) -> Raw,
    alloc_context: unsafe extern "C" fn(Raw) -> Raw,
    free_context: unsafe extern "C" fn(*mut Raw),
    parameters_alloc: unsafe extern "C" fn() -> Raw,
    parameters_free: unsafe extern "C" fn(*mut Raw),
    parameters_to_context: unsafe extern "C" fn(Raw, Raw) -> c_int,
    open: unsafe extern "C" fn(Raw, Raw, Raw) -> c_int,
    send_packet: unsafe extern "C" fn(Raw, Raw) -> c_int,
    receive_frame: unsafe extern "C" fn(Raw, Raw) -> c_int,
    flush_buffers: unsafe extern "C" fn(Raw),
    packet_alloc: unsafe extern "C" fn() -> Raw,
    packet_free: unsafe extern "C" fn(*mut Raw),
    frame_alloc: unsafe extern "C" fn() -> Raw,
    frame_free: unsafe extern "C" fn(*mut Raw),
    frame_unref: unsafe extern "C" fn(Raw),
    mallocz: unsafe extern "C" fn(usize) -> Raw,
    opt_set: unsafe extern "C" fn(Raw, *const c_char, *const c_char, c_int) -> c_int,
    opt_get_int: unsafe extern "C" fn(Raw, *const c_char, c_int, *mut i64) -> c_int,
    strerror: unsafe extern "C" fn(c_int, *mut c_char, usize) -> c_int,
}

#[cfg(not(windows))]
const PAIRS: [(&str, &str, u32, u32); 6] = [
    ("libavcodec.so.63", "libavutil.so.61", 63, 61),
    ("libavcodec.so.62", "libavutil.so.60", 62, 60),
    ("libavcodec.so.61", "libavutil.so.59", 61, 59),
    ("libavcodec.so.60", "libavutil.so.58", 60, 58),
    ("libavcodec.so.59", "libavutil.so.57", 59, 57),
    ("libavcodec.so.58", "libavutil.so.56", 58, 56),
];

/// Windows names, so the adapter's own tests run against a shared FFmpeg
/// build on the development machine (same x64 struct layout).
#[cfg(windows)]
const PAIRS: [(&str, &str, u32, u32); 6] = [
    ("avcodec-63.dll", "avutil-61.dll", 63, 61),
    ("avcodec-62.dll", "avutil-60.dll", 62, 60),
    ("avcodec-61.dll", "avutil-59.dll", 61, 59),
    ("avcodec-60.dll", "avutil-58.dll", 60, 58),
    ("avcodec-59.dll", "avutil-57.dll", 59, 57),
    ("avcodec-58.dll", "avutil-56.dll", 58, 56),
];

fn sym<T: Copy>(lib: &Library, name: &str) -> Result<T, String> {
    // SAFETY: each caller names the C function whose signature T restates.
    unsafe { lib.get::<T>(name.as_bytes()) }
        .map(|s| *s)
        .map_err(|e| format!("{name}: {e}"))
}

fn load_pair(codec: &str, util: &str, codec_major: u32, util_major: u32) -> Result<Api, String> {
    // SAFETY: loading a system library runs its initialisers; libavcodec
    // and libavutil have no load-time side effects beyond their own state.
    let avutil = unsafe { Library::new(util) }.map_err(|e| e.to_string())?;
    // SAFETY: as above.
    let avcodec = unsafe { Library::new(codec) }.map_err(|e| e.to_string())?;
    let codec_version: unsafe extern "C" fn() -> u32 = sym(&avcodec, "avcodec_version")?;
    let util_version: unsafe extern "C" fn() -> u32 = sym(&avutil, "avutil_version")?;
    // SAFETY: both take no arguments and return a packed version.
    let (cv, uv) = unsafe { (codec_version(), util_version()) };
    if cv >> 16 != codec_major || uv >> 16 != util_major {
        return Err(format!(
            "{codec} reports {}.{}, {util} reports {}.{}",
            cv >> 16,
            (cv >> 8) & 0xff,
            uv >> 16,
            (uv >> 8) & 0xff
        ));
    }
    let log_level: unsafe extern "C" fn(c_int) = sym(&avutil, "av_log_set_level")?;
    // SAFETY: sets a process-wide integer; FFmpeg's default prints every
    // concealed slice to stderr.
    unsafe { log_level(LOG_QUIET) };
    Ok(Api {
        label: format!(
            "FFmpeg (libavcodec {}.{}.{})",
            cv >> 16,
            (cv >> 8) & 0xff,
            cv & 0xff
        ),
        find_decoder: sym(&avcodec, "avcodec_find_decoder_by_name")?,
        alloc_context: sym(&avcodec, "avcodec_alloc_context3")?,
        free_context: sym(&avcodec, "avcodec_free_context")?,
        parameters_alloc: sym(&avcodec, "avcodec_parameters_alloc")?,
        parameters_free: sym(&avcodec, "avcodec_parameters_free")?,
        parameters_to_context: sym(&avcodec, "avcodec_parameters_to_context")?,
        open: sym(&avcodec, "avcodec_open2")?,
        send_packet: sym(&avcodec, "avcodec_send_packet")?,
        receive_frame: sym(&avcodec, "avcodec_receive_frame")?,
        flush_buffers: sym(&avcodec, "avcodec_flush_buffers")?,
        packet_alloc: sym(&avcodec, "av_packet_alloc")?,
        packet_free: sym(&avcodec, "av_packet_free")?,
        frame_alloc: sym(&avutil, "av_frame_alloc")?,
        frame_free: sym(&avutil, "av_frame_free")?,
        frame_unref: sym(&avutil, "av_frame_unref")?,
        mallocz: sym(&avutil, "av_mallocz")?,
        opt_set: sym(&avutil, "av_opt_set")?,
        opt_get_int: sym(&avutil, "av_opt_get_int")?,
        strerror: sym(&avutil, "av_strerror")?,
        _avcodec: avcodec,
        _avutil: avutil,
    })
}

fn api() -> Result<&'static Api, DecodeError> {
    static API: OnceLock<Result<Api, String>> = OnceLock::new();
    API.get_or_init(|| {
        let mut tried = Vec::new();
        for (codec, util, cm, um) in PAIRS {
            match load_pair(codec, util, cm, um) {
                Ok(api) => return Ok(api),
                Err(e) => tried.push(e),
            }
        }
        Err(format!(
            "system FFmpeg not found (install the ffmpeg / libavcodec package): {}",
            tried.join("; ")
        ))
    })
    .as_ref()
    .map_err(|e| DecodeError(e.clone()))
}

/// The loaded library's label, or why none loaded.
pub fn library() -> Result<&'static str, DecodeError> {
    api().map(|a| a.label.as_str())
}

fn find(api: &Api, codec: FfCodec) -> Option<(Raw, c_int)> {
    codec.decoders().iter().find_map(|name| {
        // SAFETY: the lookup only reads FFmpeg's static codec table; a
        // found AVCodec's public head (name, long_name, type, id) is fixed.
        unsafe {
            let decoder = (api.find_decoder)(name.as_ptr());
            (!decoder.is_null()).then(|| (decoder, read::<c_int>(decoder, CODEC_ID)))
        }
    })
}

/// Whether the system FFmpeg has a decoder for this codec.
pub fn has_decoder(codec: FfCodec) -> bool {
    api().is_ok_and(|a| find(a, codec).is_some())
}

/// # Safety
/// `base` points to a live FFmpeg struct with a field of type T at `offset`.
unsafe fn read<T: Copy>(base: Raw, offset: usize) -> T {
    // SAFETY: caller contract.
    unsafe { (base as *const u8).add(offset).cast::<T>().read_unaligned() }
}

/// # Safety
/// As [`read`], writable.
unsafe fn write<T>(base: Raw, offset: usize, value: T) {
    // SAFETY: caller contract.
    unsafe {
        (base as *mut u8)
            .add(offset)
            .cast::<T>()
            .write_unaligned(value)
    }
}

fn error(api: &Api, what: &str, code: c_int) -> DecodeError {
    let mut buf = [0 as c_char; 128];
    // SAFETY: av_strerror writes at most buf.len() bytes including the NUL.
    let text = if unsafe { (api.strerror)(code, buf.as_mut_ptr(), buf.len()) } == 0 {
        // SAFETY: NUL-terminated by av_strerror.
        unsafe { CStr::from_ptr(buf.as_ptr()) }
            .to_string_lossy()
            .into_owned()
    } else {
        format!("error {code}")
    };
    DecodeError(format!("FFmpeg {what}: {text}"))
}

/// One open libavcodec decoder: context, reusable packet and frame.
struct Session {
    api: &'static Api,
    ctx: Raw,
    packet: Raw,
    frame: Raw,
    input: Vec<u8>,
    draining: bool,
}

// SAFETY: the FFmpeg objects are owned by this value and used from one
// thread at a time (the engine's decode thread).
unsafe impl Send for Session {}

impl Session {
    fn open(
        codec: FfCodec,
        extradata: &[u8],
        options: &[(&CStr, &CStr)],
    ) -> Result<Self, DecodeError> {
        let api = api()?;
        // SAFETY: every pointer below is checked before use and released
        // on each failure path by Drop (the struct is built first).
        unsafe {
            let Some((decoder, id)) = find(api, codec) else {
                return Err(DecodeError(format!(
                    "{} has no {} decoder (the distribution's FFmpeg build leaves it out)",
                    api.label,
                    codec.name()
                )));
            };
            let session = Session {
                api,
                ctx: (api.alloc_context)(decoder),
                packet: (api.packet_alloc)(),
                frame: (api.frame_alloc)(),
                input: Vec::new(),
                draining: false,
            };
            if session.ctx.is_null() || session.packet.is_null() || session.frame.is_null() {
                return Err(DecodeError("FFmpeg allocation failed".into()));
            }
            let mut par = (api.parameters_alloc)();
            if par.is_null() {
                return Err(DecodeError("FFmpeg allocation failed".into()));
            }
            let video = !matches!(codec, FfCodec::Aac | FfCodec::Mp3);
            write::<c_int>(par, PAR_TYPE, if video { MEDIA_VIDEO } else { MEDIA_AUDIO });
            write::<c_int>(par, PAR_ID, id);
            if !extradata.is_empty() {
                let copy = (api.mallocz)(extradata.len() + PADDING);
                if copy.is_null() {
                    (api.parameters_free)(&mut par);
                    return Err(DecodeError("FFmpeg allocation failed".into()));
                }
                std::ptr::copy_nonoverlapping(extradata.as_ptr(), copy.cast(), extradata.len());
                write::<Raw>(par, PAR_EXTRADATA, copy);
                write::<c_int>(par, PAR_EXTRADATA_SIZE, extradata.len() as c_int);
            }
            let copied = (api.parameters_to_context)(session.ctx, par);
            (api.parameters_free)(&mut par);
            if copied < 0 {
                return Err(error(api, "parameters", copied));
            }
            for (name, value) in options {
                (api.opt_set)(session.ctx, name.as_ptr(), value.as_ptr(), 0);
            }
            let opened = (api.open)(session.ctx, decoder, null_mut());
            if opened < 0 {
                return Err(error(api, "open", opened));
            }
            Ok(session)
        }
    }

    fn submit(&mut self, data: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if data.is_empty() {
            return Ok(SubmitOutcome::Accepted);
        }
        self.input.clear();
        self.input.extend_from_slice(data);
        self.input.resize(data.len() + PADDING, 0);
        // SAFETY: the packet is ours and not refcounted (buf stays null),
        // so avcodec_send_packet copies the padded payload before
        // returning; the fields are cleared again before input is reused.
        unsafe {
            write::<i64>(self.packet, PACKET_PTS, pts_us);
            write::<i64>(self.packet, PACKET_DTS, NOPTS);
            write::<*mut u8>(self.packet, PACKET_DATA, self.input.as_mut_ptr());
            write::<c_int>(self.packet, PACKET_SIZE, data.len() as c_int);
            let sent = (self.api.send_packet)(self.ctx, self.packet);
            write::<*mut u8>(self.packet, PACKET_DATA, null_mut());
            write::<c_int>(self.packet, PACKET_SIZE, 0);
            match sent {
                0 => Ok(SubmitOutcome::Accepted),
                EAGAIN => Ok(SubmitOutcome::NotAccepting),
                EOF => Ok(SubmitOutcome::Accepted),
                INVALID_DATA => Ok(SubmitOutcome::Accepted),
                code => Err(error(self.api, "send", code)),
            }
        }
    }

    /// Ok(true) leaves a decoded frame in `self.frame` until the next call.
    fn receive(&mut self) -> Result<bool, DecodeError> {
        // SAFETY: context and frame are live; receive_frame unrefs the
        // frame before filling it.
        unsafe {
            (self.api.frame_unref)(self.frame);
            match (self.api.receive_frame)(self.ctx, self.frame) {
                0 => Ok(true),
                EAGAIN | EOF => Ok(false),
                code => Err(error(self.api, "decode", code)),
            }
        }
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        if self.draining {
            return Ok(());
        }
        self.draining = true;
        // SAFETY: a null packet enters draining mode.
        let sent = unsafe { (self.api.send_packet)(self.ctx, null_mut()) };
        if sent < 0 && sent != EOF {
            return Err(error(self.api, "drain", sent));
        }
        Ok(())
    }

    fn reset(&mut self) {
        self.draining = false;
        // SAFETY: the context is open; flushing also leaves draining mode.
        unsafe {
            (self.api.frame_unref)(self.frame);
            (self.api.flush_buffers)(self.ctx);
        }
    }

    fn option(&self, name: &CStr) -> Option<i64> {
        let mut out = 0i64;
        // SAFETY: the context is a live AVOptions object; out is a local.
        (unsafe { (self.api.opt_get_int)(self.ctx, name.as_ptr(), 0, &mut out) } >= 0)
            .then_some(out)
    }

    /// # Safety
    /// A frame from a successful [`Session::receive`] is in `self.frame`.
    unsafe fn plane(&self, index: usize) -> (*const u8, isize) {
        // SAFETY: caller contract; data and linesize are fixed-size arrays
        // at the head of AVFrame.
        unsafe {
            (
                read::<*const u8>(self.frame, FRAME_DATA + index * 8),
                read::<c_int>(self.frame, FRAME_LINESIZE + index * 4) as isize,
            )
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        // SAFETY: each free takes a pointer to the owner field, tolerates
        // null and nulls it.
        unsafe {
            (self.api.frame_free)(&mut self.frame);
            (self.api.packet_free)(&mut self.packet);
            (self.api.free_context)(&mut self.ctx);
        }
    }
}

pub struct FfVideoDecoder {
    session: Session,
    last_pts: i64,
}

impl FfVideoDecoder {
    /// `live` trades frame threading (several frames of latency) for slice
    /// threading and low-delay output.
    pub fn new(codec: FfCodec, live: bool) -> Result<Self, DecodeError> {
        let options: &[(&CStr, &CStr)] = if live {
            &[
                (c"threads", c"auto"),
                (c"thread_type", c"slice"),
                (c"flags", c"+low_delay"),
            ]
        } else {
            &[(c"threads", c"auto")]
        };
        Ok(Self {
            session: Session::open(codec, &[], options)?,
            last_pts: 0,
        })
    }

    pub fn label(&self) -> &'static str {
        let api: &'static Api = self.session.api;
        &api.label
    }

    fn color(&self, format: c_int) -> ColorInfo {
        let matrix = match self.session.option(c"colorspace") {
            Some(1) => YuvMatrix::Bt709,
            Some(5 | 6) => YuvMatrix::Bt601,
            Some(9 | 10) => YuvMatrix::Bt2020,
            _ => YuvMatrix::Unspecified,
        };
        let range = match self.session.option(c"color_range") {
            _ if format == PIX_YUVJ420P => YuvRange::Full,
            Some(1) => YuvRange::Limited,
            Some(2) => YuvRange::Full,
            _ => YuvRange::Unspecified,
        };
        ColorInfo { matrix, range }
    }

    fn convert(&mut self) -> Result<Nv12Frame, DecodeError> {
        // SAFETY: called right after a successful receive; every plane read
        // stays inside rows the frame states, with strides checked to cover
        // the copied width.
        unsafe {
            let frame = self.session.frame;
            let format = read::<c_int>(frame, FRAME_FORMAT);
            let w = read::<c_int>(frame, FRAME_WIDTH);
            let h = read::<c_int>(frame, FRAME_HEIGHT);
            if w < 2 || h < 2 {
                return Err(DecodeError(format!("FFmpeg frame {w}x{h} is empty")));
            }
            let (width, height) = ((w & !1) as usize, (h & !1) as usize);
            let pts = match read::<i64>(frame, FRAME_PTS) {
                NOPTS => self.last_pts,
                pts => pts,
            };
            self.last_pts = pts;
            let color = self.color(format);
            let mut data = vec![0u8; packed_nv12_len("FFmpeg frame", width, height)?];
            let (y, y_stride) = self.session.plane(0);
            if y.is_null() || y_stride < width as isize {
                return Err(DecodeError(
                    "FFmpeg frame without a usable luma plane".into(),
                ));
            }
            for row in 0..height {
                std::ptr::copy_nonoverlapping(
                    y.offset(row as isize * y_stride),
                    data.as_mut_ptr().add(row * width),
                    width,
                );
            }
            let uv_out = data.as_mut_ptr().add(width * height);
            match format {
                PIX_NV12 => {
                    let (uv, stride) = self.session.plane(1);
                    if uv.is_null() || stride < width as isize {
                        return Err(DecodeError("FFmpeg NV12 frame without chroma".into()));
                    }
                    for row in 0..height / 2 {
                        std::ptr::copy_nonoverlapping(
                            uv.offset(row as isize * stride),
                            uv_out.add(row * width),
                            width,
                        );
                    }
                }
                PIX_YUV420P | PIX_YUVJ420P => {
                    let (u, us) = self.session.plane(1);
                    let (v, vs) = self.session.plane(2);
                    let half = (width / 2) as isize;
                    if u.is_null() || v.is_null() || us < half || vs < half {
                        return Err(DecodeError("FFmpeg 4:2:0 frame without chroma".into()));
                    }
                    for row in 0..height / 2 {
                        let u_row = u.offset(row as isize * us);
                        let v_row = v.offset(row as isize * vs);
                        let out = uv_out.add(row * width);
                        for x in 0..width / 2 {
                            *out.add(2 * x) = *u_row.add(x);
                            *out.add(2 * x + 1) = *v_row.add(x);
                        }
                    }
                }
                other => {
                    return Err(DecodeError(format!(
                        "FFmpeg pixel format {other} unsupported (8-bit 4:2:0 only)"
                    )));
                }
            }
            Ok(Nv12Frame {
                width: width as u32,
                height: height as u32,
                pts_us: pts,
                color,
                data,
            })
        }
    }
}

impl VideoDecoder for FfVideoDecoder {
    fn submit(&mut self, annexb: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        self.session.submit(annexb, pts_us)
    }

    fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
        if !self.session.receive()? {
            return Ok(None);
        }
        self.convert().map(|f| Some(VideoFrame::Nv12(f)))
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.session.begin_drain()
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.session.reset();
        Ok(())
    }
}

pub struct FfAudioDecoder {
    session: Session,
    sample_rate: u32,
    channels: u32,
    ready: VecDeque<PcmChunk>,
}

impl FfAudioDecoder {
    /// `config` is the AudioSpecificConfig for AAC, empty for MP3.
    pub fn new(
        codec: FfCodec,
        sample_rate: u32,
        channels: u32,
        config: &[u8],
    ) -> Result<Self, DecodeError> {
        Ok(Self {
            session: Session::open(codec, config, &[])?,
            sample_rate,
            channels: channels.max(1),
            ready: VecDeque::new(),
        })
    }

    fn convert(&self) -> Result<PcmChunk, DecodeError> {
        // SAFETY: called right after a successful receive. Planar formats
        // hold one plane per channel in data[], packed formats one
        // interleaved plane; each holds nb_samples samples per channel.
        unsafe {
            let frame = self.session.frame;
            let format = read::<c_int>(frame, FRAME_FORMAT);
            let samples = read::<c_int>(frame, FRAME_NB_SAMPLES).max(0) as usize;
            let pts = read::<i64>(frame, FRAME_PTS);
            let rate = self
                .session
                .option(c"ar")
                .filter(|r| *r > 0)
                .map_or(self.sample_rate, |r| r as u32);
            let planar = matches!(format, 5..=9);
            let channels = if planar {
                (0..8)
                    .take_while(|&i| !self.session.plane(i).0.is_null())
                    .count()
                    .max(1)
            } else {
                self.session
                    .option(c"ac")
                    .filter(|c| (1..=8).contains(c))
                    .map_or(self.channels as usize, |c| c as usize)
            };
            let mut data = vec![0f32; samples * channels];
            let sample = |plane: *const u8, index: usize| -> f32 {
                match format {
                    0 | 5 => (f32::from(*plane.add(index)) - 128.0) / 128.0,
                    1 | 6 => f32::from(plane.cast::<i16>().add(index).read_unaligned()) / 32768.0,
                    2 | 7 => {
                        plane.cast::<i32>().add(index).read_unaligned() as f32 / 2_147_483_648.0
                    }
                    3 | 8 => plane.cast::<f32>().add(index).read_unaligned(),
                    _ => plane.cast::<f64>().add(index).read_unaligned() as f32,
                }
            };
            if !(0..=9).contains(&format) {
                return Err(DecodeError(format!(
                    "FFmpeg sample format {format} unsupported"
                )));
            }
            if planar {
                for c in 0..channels {
                    let plane = self.session.plane(c).0;
                    for s in 0..samples {
                        data[s * channels + c] = sample(plane, s);
                    }
                }
            } else {
                let plane = self.session.plane(0).0;
                if plane.is_null() {
                    return Err(DecodeError("FFmpeg audio frame without samples".into()));
                }
                for (i, out) in data.iter_mut().enumerate() {
                    *out = sample(plane, i);
                }
            }
            Ok(PcmChunk {
                sample_rate: rate,
                channels: channels as u32,
                pts_us: if pts == NOPTS { 0 } else { pts },
                data,
            })
        }
    }

    fn collect(&mut self) -> Result<(), DecodeError> {
        while self.ready.len() < READY_CAP && self.session.receive()? {
            let chunk = self.convert()?;
            if !chunk.data.is_empty() {
                self.ready.push_back(chunk);
            }
        }
        Ok(())
    }
}

impl AudioDecoder for FfAudioDecoder {
    fn output_format(&self) -> (u32, u32) {
        (self.sample_rate, self.channels)
    }

    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if self.ready.len() >= READY_CAP {
            return Ok(SubmitOutcome::NotAccepting);
        }
        let outcome = self.session.submit(au, pts_us)?;
        self.collect()?;
        Ok(outcome)
    }

    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError> {
        if self.ready.is_empty() {
            self.collect()?;
        }
        Ok(self.ready.pop_front())
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.session.begin_drain()
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.ready.clear();
        self.session.reset();
        Ok(())
    }
}
