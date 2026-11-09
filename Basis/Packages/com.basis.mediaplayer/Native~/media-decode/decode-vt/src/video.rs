use std::collections::VecDeque;
use std::ffi::c_void;
use std::ptr::{null, null_mut};
use std::sync::Mutex;

use core_foundation_sys::base::{CFRelease, CFTypeRef, OSStatus, kCFAllocatorDefault};
use core_foundation_sys::number::{
    CFBooleanGetValue, CFBooleanRef, kCFBooleanFalse, kCFBooleanTrue,
};
use media_decode::{
    ColorInfo, DecodeError, Nv12Frame, SubmitOutcome, VideoDecoder, VideoFrame, YuvMatrix,
    YuvRange, packed_nv12_len,
};

use crate::ffi::*;
use crate::{cf_equal, dictionary, number};

const READY_CAP: usize = 8;
const MAX_REORDER: usize = 4;

/// The codecs VideoToolbox decodes here, both fed as Annex B.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VtCodec {
    H264,
    H265,
}

impl VtCodec {
    fn fourcc(self) -> u32 {
        match self {
            VtCodec::H264 => CODEC_H264,
            VtCodec::H265 => CODEC_HEVC,
        }
    }
}

/// How the session is asked to use the media engine: hardware where the
/// Mac has it, hardware or nothing, or Apple's software decoder.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum VtAcceleration {
    Prefer,
    Require,
    Software,
}

/// Whether this Mac decodes the codec in hardware.
pub fn hardware_decode_supported(codec: VtCodec) -> bool {
    // SAFETY: a pure query on a codec FourCC.
    unsafe { VTIsHardwareDecodeSupported(codec.fourcc()) != 0 }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Nal {
    Vps,
    Sps,
    Pps,
    Skip,
    Slice { key: bool },
    Other,
}

fn classify(codec: VtCodec, header: u8) -> Nal {
    match codec {
        VtCodec::H264 => match header & 0x1f {
            7 => Nal::Sps,
            8 => Nal::Pps,
            9..=13 => Nal::Skip,
            5 => Nal::Slice { key: true },
            1..=4 => Nal::Slice { key: false },
            _ => Nal::Other,
        },
        VtCodec::H265 => match (header >> 1) & 0x3f {
            32 => Nal::Vps,
            33 => Nal::Sps,
            34 => Nal::Pps,
            35..=38 => Nal::Skip,
            16..=23 => Nal::Slice { key: true },
            0..=9 => Nal::Slice { key: false },
            _ => Nal::Other,
        },
    }
}

/// The NAL units of an Annex B access unit, start codes and trailing zero
/// bytes removed.
pub(crate) fn nal_units(data: &[u8]) -> impl Iterator<Item = &[u8]> + '_ {
    let mut starts = Vec::new();
    let mut i = 0usize;
    while i + 3 <= data.len() {
        if data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1 {
            starts.push(i + 3);
            i += 3;
        } else {
            i += 1;
        }
    }
    let ends: Vec<usize> = starts
        .iter()
        .skip(1)
        .map(|&s| s - 3)
        .chain(std::iter::once(data.len()))
        .collect();
    starts.into_iter().zip(ends).filter_map(move |(s, mut e)| {
        while e > s && data[e - 1] == 0 {
            e -= 1;
        }
        (e > s).then(|| &data[s..e])
    })
}

#[derive(Clone, Default, PartialEq, Eq)]
struct ParameterSets {
    vps: Vec<Vec<u8>>,
    sps: Vec<Vec<u8>>,
    pps: Vec<Vec<u8>>,
}

impl ParameterSets {
    fn complete(&self, codec: VtCodec) -> bool {
        !self.sps.is_empty()
            && !self.pps.is_empty()
            && (codec == VtCodec::H264 || !self.vps.is_empty())
    }

    fn merge(&mut self, update: ParameterSets) -> bool {
        let mut next = self.clone();
        for (into, from) in [
            (&mut next.vps, update.vps),
            (&mut next.sps, update.sps),
            (&mut next.pps, update.pps),
        ] {
            if !from.is_empty() {
                *into = from;
            }
        }
        let changed = next != *self;
        *self = next;
        changed
    }

    fn all(&self) -> impl Iterator<Item = &Vec<u8>> {
        self.vps.iter().chain(&self.sps).chain(&self.pps)
    }
}

fn push_unique(list: &mut Vec<Vec<u8>>, nal: &[u8]) {
    if !list.iter().any(|n| n == nal) {
        list.push(nal.to_vec());
    }
}

/// Decoded frames waiting for the engine, put back into presentation order.
/// Sessions fed in decode order can hand B-frames back early; the depth
/// grows the first time that happens and is kept for the decoder's life.
#[derive(Default)]
struct Outputs {
    ready: VecDeque<Nv12Frame>,
    held: Vec<Nv12Frame>,
    depth: usize,
    released: Option<i64>,
    error: Option<String>,
    lost: bool,
    matrix: YuvMatrix,
}

impl Outputs {
    fn push(&mut self, frame: Nv12Frame) {
        if self.released.is_some_and(|last| frame.pts_us < last) {
            self.depth = (self.depth + 1).min(MAX_REORDER);
            if self.ready.back().is_some_and(|f| f.pts_us > frame.pts_us) {
                let at = self.ready.partition_point(|f| f.pts_us < frame.pts_us);
                self.ready.insert(at, frame);
            }
            return;
        }
        let at = self.held.partition_point(|f| f.pts_us <= frame.pts_us);
        self.held.insert(at, frame);
        while self.held.len() > self.depth {
            self.release_one();
        }
    }

    fn release_one(&mut self) {
        let frame = self.held.remove(0);
        self.released = Some(frame.pts_us);
        self.ready.push_back(frame);
    }

    fn flush(&mut self) {
        while !self.held.is_empty() {
            self.release_one();
        }
    }

    fn clear(&mut self) {
        self.ready.clear();
        self.held.clear();
        self.released = None;
    }
}

fn lock(outputs: &Mutex<Outputs>) -> std::sync::MutexGuard<'_, Outputs> {
    outputs.lock().unwrap_or_else(|e| e.into_inner())
}

extern "C" fn on_output(
    refcon: *mut c_void,
    _source: *mut c_void,
    status: OSStatus,
    flags: u32,
    image: CVImageBufferRef,
    pts: CMTime,
    _duration: CMTime,
) {
    // SAFETY: refcon is the decoder's boxed Outputs, which outlives the
    // session (the session is invalidated before the box is dropped).
    let outputs = unsafe { &*(refcon as *const Mutex<Outputs>) };
    if status == ERR_INVALID_SESSION || status == ERR_MALFUNCTION {
        lock(outputs).lost = true;
        return;
    }
    if status != 0 || flags & DECODE_INFO_FRAME_DROPPED != 0 || image.is_null() {
        return;
    }
    // SAFETY: VideoToolbox hands a live pixel buffer for the callback's duration.
    let frame = unsafe { copy_out(image, pts.as_micros().unwrap_or(0)) };
    let mut out = lock(outputs);
    let frame = frame.map(|mut f| {
        if f.color.matrix == YuvMatrix::Unspecified {
            f.color.matrix = out.matrix;
        }
        f
    });
    match frame {
        Ok(frame) => out.push(frame),
        Err(e) => out.error = Some(e.0),
    }
}

struct Locked(CVImageBufferRef);

impl Drop for Locked {
    fn drop(&mut self) {
        // SAFETY: the buffer was locked read-only by the constructor below.
        unsafe { CVPixelBufferUnlockBaseAddress(self.0, LOCK_READ_ONLY) };
    }
}

fn matrix_of(value: CFTypeRef) -> YuvMatrix {
    if value.is_null() {
        return YuvMatrix::Unspecified;
    }
    // SAFETY: the statics are CoreVideo's constant strings.
    let (m709, m601, m240, m2020) = unsafe { (MATRIX_709, MATRIX_601, MATRIX_240M, MATRIX_2020) };
    if cf_equal(value, m709) {
        YuvMatrix::Bt709
    } else if cf_equal(value, m601) || cf_equal(value, m240) {
        YuvMatrix::Bt601
    } else if cf_equal(value, m2020) {
        YuvMatrix::Bt2020
    } else {
        YuvMatrix::Unspecified
    }
}

/// # Safety
/// `image` is a live CVPixelBuffer.
unsafe fn copy_out(image: CVImageBufferRef, pts_us: i64) -> Result<Nv12Frame, DecodeError> {
    // SAFETY: caller contract; every plane read stays inside the rows and
    // strides the buffer states, checked against the copied width.
    unsafe {
        let range = match CVPixelBufferGetPixelFormatType(image) {
            PIXEL_420V => YuvRange::Limited,
            PIXEL_420F => YuvRange::Full,
            other => {
                return Err(DecodeError(format!(
                    "VideoToolbox returned pixel format {:?}, not NV12",
                    other.to_be_bytes().map(char::from)
                )));
            }
        };
        if CVPixelBufferGetPlaneCount(image) != 2 {
            return Err(DecodeError("VideoToolbox frame is not biplanar".into()));
        }
        if CVPixelBufferLockBaseAddress(image, LOCK_READ_ONLY) != 0 {
            return Err(DecodeError("VideoToolbox frame could not be locked".into()));
        }
        let _guard = Locked(image);
        let width = CVPixelBufferGetWidthOfPlane(image, 0) & !1;
        let height = CVPixelBufferGetHeightOfPlane(image, 0) & !1;
        if width < 2 || height < 2 {
            return Err(DecodeError(format!(
                "VideoToolbox frame {width}x{height} is empty"
            )));
        }
        let mut data = vec![0u8; packed_nv12_len("VideoToolbox frame", width, height)?];
        let y = CVPixelBufferGetBaseAddressOfPlane(image, 0) as *const u8;
        let y_stride = CVPixelBufferGetBytesPerRowOfPlane(image, 0);
        let uv = CVPixelBufferGetBaseAddressOfPlane(image, 1) as *const u8;
        let uv_stride = CVPixelBufferGetBytesPerRowOfPlane(image, 1);
        if y.is_null()
            || uv.is_null()
            || y_stride < width
            || uv_stride < width
            || CVPixelBufferGetHeightOfPlane(image, 1) < height / 2
        {
            return Err(DecodeError("VideoToolbox frame planes are unusable".into()));
        }
        for row in 0..height {
            std::ptr::copy_nonoverlapping(
                y.add(row * y_stride),
                data.as_mut_ptr().add(row * width),
                width,
            );
        }
        let uv_out = data.as_mut_ptr().add(width * height);
        for row in 0..height / 2 {
            std::ptr::copy_nonoverlapping(uv.add(row * uv_stride), uv_out.add(row * width), width);
        }
        let matrix = matrix_of(CVBufferGetAttachment(image, MATRIX_KEY, null_mut()));
        Ok(Nv12Frame {
            width: width as u32,
            height: height as u32,
            pts_us,
            color: ColorInfo { matrix, range },
            data,
        })
    }
}

pub struct VtVideoDecoder {
    codec: VtCodec,
    acceleration: VtAcceleration,
    live: bool,
    outputs: Box<Mutex<Outputs>>,
    session: VTDecompressionSessionRef,
    format: CMFormatDescriptionRef,
    full_range: bool,
    params: ParameterSets,
    need_key: bool,
    hardware: bool,
    sample: Vec<u8>,
}

// SAFETY: the session and format description are owned by this value and
// driven from one thread at a time; the output callback only touches the
// mutex-guarded queue.
unsafe impl Send for VtVideoDecoder {}

impl VtVideoDecoder {
    /// `live` decodes in arrival order with the real-time hint, reordering
    /// only as far as the stream turns out to need; otherwise VideoToolbox
    /// holds frames back itself and returns them in presentation order.
    pub fn new(
        codec: VtCodec,
        acceleration: VtAcceleration,
        live: bool,
    ) -> Result<Self, DecodeError> {
        let hardware = acceleration != VtAcceleration::Software && hardware_decode_supported(codec);
        if acceleration == VtAcceleration::Require && !hardware {
            return Err(DecodeError(format!(
                "this Mac has no hardware {} decoder",
                Self::codec_name(codec)
            )));
        }
        Ok(Self {
            codec,
            acceleration,
            live,
            outputs: Box::new(Mutex::new(Outputs {
                depth: if live { 0 } else { MAX_REORDER },
                ..Outputs::default()
            })),
            session: null_mut(),
            format: null_mut(),
            full_range: false,
            params: ParameterSets::default(),
            need_key: true,
            hardware,
            sample: Vec::new(),
        })
    }

    fn codec_name(codec: VtCodec) -> &'static str {
        match codec {
            VtCodec::H264 => "H.264",
            VtCodec::H265 => "HEVC",
        }
    }

    /// Whether the session decodes on the media engine. Before the first
    /// keyframe this is the platform's answer for the codec; after it, the
    /// session's own.
    pub fn hardware(&self) -> bool {
        self.hardware
    }

    pub fn label(&self) -> &'static str {
        match (self.codec, self.hardware) {
            (VtCodec::H264, true) => "VideoToolbox H.264",
            (VtCodec::H264, false) => "VideoToolbox H.264 (software)",
            (VtCodec::H265, true) => "VideoToolbox HEVC",
            (VtCodec::H265, false) => "VideoToolbox HEVC (software)",
        }
    }

    fn configure(&mut self) -> Result<(), DecodeError> {
        let sets: Vec<&Vec<u8>> = self.params.all().collect();
        let pointers: Vec<*const u8> = sets.iter().map(|s| s.as_ptr()).collect();
        let sizes: Vec<usize> = sets.iter().map(|s| s.len()).collect();
        let mut format: CMFormatDescriptionRef = null_mut();
        // SAFETY: pointers and sizes describe parameter sets that outlive the
        // call; CoreMedia copies them into the description.
        let status = unsafe {
            match self.codec {
                VtCodec::H264 => CMVideoFormatDescriptionCreateFromH264ParameterSets(
                    kCFAllocatorDefault,
                    sets.len(),
                    pointers.as_ptr(),
                    sizes.as_ptr(),
                    4,
                    &mut format,
                ),
                VtCodec::H265 => CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                    kCFAllocatorDefault,
                    sets.len(),
                    pointers.as_ptr(),
                    sizes.as_ptr(),
                    4,
                    null(),
                    &mut format,
                ),
            }
        };
        if status != 0 || format.is_null() {
            return Err(DecodeError(format!(
                "VideoToolbox refused the {} parameter sets ({status})",
                Self::codec_name(self.codec)
            )));
        }
        // SAFETY: format is a live description; the extension is a borrowed
        // CFBoolean or null.
        let (full_range, matrix) = unsafe {
            let value = CMFormatDescriptionGetExtension(format, EXTENSION_FULL_RANGE);
            (
                !value.is_null() && CFBooleanGetValue(value as CFBooleanRef),
                matrix_of(CMFormatDescriptionGetExtension(format, EXTENSION_MATRIX)),
            )
        };
        lock(&self.outputs).matrix = matrix;
        if !self.session.is_null() {
            // SAFETY: both are live.
            let accepts =
                unsafe { VTDecompressionSessionCanAcceptFormatDescription(self.session, format) }
                    != 0;
            if !accepts || full_range != self.full_range {
                self.close_session();
                self.need_key = true;
            }
        }
        self.release_format();
        self.format = format;
        self.full_range = full_range;
        Ok(())
    }

    fn open_session(&mut self) -> Result<(), DecodeError> {
        // SAFETY: every key and value is a CoreFoundation constant or a value
        // created here and released after the session copies it.
        unsafe {
            let (key, value) = match self.acceleration {
                VtAcceleration::Prefer => (SPEC_ENABLE_HARDWARE, kCFBooleanTrue),
                VtAcceleration::Require => (SPEC_REQUIRE_HARDWARE, kCFBooleanTrue),
                VtAcceleration::Software => (SPEC_ENABLE_HARDWARE, kCFBooleanFalse),
            };
            let specification = dictionary(&[(key as CFTypeRef, value as CFTypeRef)]);
            let pixel = number(if self.full_range {
                PIXEL_420F
            } else {
                PIXEL_420V
            } as i32);
            let attributes = dictionary(&[(PIXEL_FORMAT_KEY as CFTypeRef, pixel)]);
            CFRelease(pixel);
            let record = DecompressionOutputRecord {
                callback: on_output,
                refcon: (&raw const *self.outputs) as *mut c_void,
            };
            let mut session: VTDecompressionSessionRef = null_mut();
            let status = VTDecompressionSessionCreate(
                kCFAllocatorDefault,
                self.format,
                specification,
                attributes,
                &record,
                &mut session,
            );
            CFRelease(specification as CFTypeRef);
            CFRelease(attributes as CFTypeRef);
            if status != 0 || session.is_null() {
                return Err(DecodeError(format!(
                    "VideoToolbox could not open a {} decoder ({status}){}",
                    Self::codec_name(self.codec),
                    if self.acceleration == VtAcceleration::Require {
                        ", hardware required"
                    } else {
                        ""
                    }
                )));
            }
            if self.live {
                VTSessionSetProperty(session, PROPERTY_REAL_TIME, kCFBooleanTrue as CFTypeRef);
            }
            let mut using: CFTypeRef = null();
            if VTSessionCopyProperty(
                session,
                PROPERTY_USING_HARDWARE,
                kCFAllocatorDefault,
                (&raw mut using).cast(),
            ) == 0
                && !using.is_null()
            {
                self.hardware = CFBooleanGetValue(using as CFBooleanRef);
                CFRelease(using);
            }
            self.session = session;
        }
        Ok(())
    }

    fn settle(&mut self) {
        if self.session.is_null() {
            return;
        }
        // SAFETY: the session is live; both calls block until every pending
        // frame has gone through the callback.
        unsafe {
            VTDecompressionSessionFinishDelayedFrames(self.session);
            VTDecompressionSessionWaitForAsynchronousFrames(self.session);
        }
    }

    fn close_session(&mut self) {
        if self.session.is_null() {
            return;
        }
        self.settle();
        // SAFETY: the session is live and released exactly once.
        unsafe {
            VTDecompressionSessionInvalidate(self.session);
            CFRelease(self.session as CFTypeRef);
        }
        self.session = null_mut();
    }

    fn release_format(&mut self) {
        if !self.format.is_null() {
            // SAFETY: the description is ours and released exactly once.
            unsafe { CFRelease(self.format as CFTypeRef) };
            self.format = null_mut();
        }
    }

    fn decode(&mut self, pts_us: i64) -> Result<OSStatus, DecodeError> {
        // SAFETY: the block buffer is allocated by CoreMedia and filled from
        // the sample bytes; every object created here is released here.
        unsafe {
            let mut block: CMBlockBufferRef = null_mut();
            let status = CMBlockBufferCreateWithMemoryBlock(
                kCFAllocatorDefault,
                null_mut(),
                self.sample.len(),
                kCFAllocatorDefault,
                null(),
                0,
                self.sample.len(),
                BLOCK_ASSURE_MEMORY_NOW,
                &mut block,
            );
            if status != 0 || block.is_null() {
                return Err(DecodeError(format!(
                    "CoreMedia block buffer failed ({status})"
                )));
            }
            CMBlockBufferReplaceDataBytes(self.sample.as_ptr().cast(), block, 0, self.sample.len());
            let timing = CMSampleTimingInfo {
                presentation: CMTime::micros(pts_us),
                ..CMSampleTimingInfo::default()
            };
            let size = self.sample.len();
            let mut sample: CMSampleBufferRef = null_mut();
            let status = CMSampleBufferCreateReady(
                kCFAllocatorDefault,
                block,
                self.format,
                1,
                1,
                &timing,
                1,
                &size,
                &mut sample,
            );
            CFRelease(block as CFTypeRef);
            if status != 0 || sample.is_null() {
                return Err(DecodeError(format!(
                    "CoreMedia sample buffer failed ({status})"
                )));
            }
            let flags = if self.live {
                0
            } else {
                DECODE_TEMPORAL_PROCESSING
            };
            let mut info = 0u32;
            let status = VTDecompressionSessionDecodeFrame(
                self.session,
                sample,
                flags,
                null_mut(),
                &mut info,
            );
            CFRelease(sample as CFTypeRef);
            Ok(status)
        }
    }
}

impl VideoDecoder for VtVideoDecoder {
    fn submit(&mut self, annexb: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        {
            let mut out = lock(&self.outputs);
            if out.ready.len() >= READY_CAP {
                return Ok(SubmitOutcome::NotAccepting);
            }
            if std::mem::take(&mut out.lost) {
                drop(out);
                self.close_session();
                self.need_key = true;
            }
        }
        let mut update = ParameterSets::default();
        let mut key = false;
        self.sample.clear();
        for nal in nal_units(annexb) {
            match classify(self.codec, nal[0]) {
                Nal::Vps => push_unique(&mut update.vps, nal),
                Nal::Sps => push_unique(&mut update.sps, nal),
                Nal::Pps => push_unique(&mut update.pps, nal),
                Nal::Skip => {}
                kind => {
                    key |= kind == Nal::Slice { key: true };
                    self.sample
                        .extend_from_slice(&(nal.len() as u32).to_be_bytes());
                    self.sample.extend_from_slice(nal);
                }
            }
        }
        if self.params.merge(update) && self.params.complete(self.codec) {
            self.configure()?;
        }
        if self.sample.is_empty() || self.format.is_null() || (self.need_key && !key) {
            return Ok(SubmitOutcome::Accepted);
        }
        if self.session.is_null() {
            self.open_session()?;
        }
        self.need_key = false;
        match self.decode(pts_us)? {
            0 | ERR_BAD_DATA => Ok(SubmitOutcome::Accepted),
            ERR_INVALID_SESSION | ERR_MALFUNCTION => {
                self.close_session();
                self.need_key = true;
                Ok(SubmitOutcome::Accepted)
            }
            status => Err(DecodeError(format!(
                "VideoToolbox {} decode failed ({status})",
                Self::codec_name(self.codec)
            ))),
        }
    }

    fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
        let mut out = lock(&self.outputs);
        if let Some(error) = out.error.take() {
            return Err(DecodeError(error));
        }
        Ok(out.ready.pop_front().map(VideoFrame::Nv12))
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.settle();
        lock(&self.outputs).flush();
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.settle();
        let mut out = lock(&self.outputs);
        out.clear();
        out.error = None;
        drop(out);
        self.need_key = true;
        Ok(())
    }
}

impl Drop for VtVideoDecoder {
    fn drop(&mut self) {
        self.close_session();
        self.release_format();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn nal_units_split_on_three_and_four_byte_codes() {
        let au = [
            0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 0, 0, 1, 0x65, 9, 9,
        ];
        let nals: Vec<&[u8]> = nal_units(&au).collect();
        assert_eq!(
            nals,
            vec![&[0x67, 1, 2][..], &[0x68, 3][..], &[0x65, 9, 9][..]]
        );
        assert_eq!(nal_units(&[]).count(), 0);
        assert_eq!(nal_units(&[0, 0, 1]).count(), 0);
    }

    #[test]
    fn hevc_and_h264_headers_classify() {
        assert!(classify(VtCodec::H264, 0x65) == Nal::Slice { key: true });
        assert!(classify(VtCodec::H264, 0x41) == Nal::Slice { key: false });
        assert!(classify(VtCodec::H264, 0x09) == Nal::Skip);
        assert!(classify(VtCodec::H265, 0x40) == Nal::Vps);
        assert!(classify(VtCodec::H265, 0x42) == Nal::Sps);
        assert!(classify(VtCodec::H265, 0x44) == Nal::Pps);
        assert!(classify(VtCodec::H265, 0x26) == Nal::Slice { key: true });
        assert!(classify(VtCodec::H265, 0x02) == Nal::Slice { key: false });
    }

    fn frame(pts_us: i64) -> Nv12Frame {
        Nv12Frame {
            width: 2,
            height: 2,
            pts_us,
            color: ColorInfo::default(),
            data: vec![0; 6],
        }
    }

    #[test]
    fn decode_order_output_is_put_back_in_presentation_order() {
        let mut out = Outputs::default();
        for pts in [0, 3, 1, 2, 6, 4, 5] {
            out.push(frame(pts));
        }
        out.flush();
        let order: Vec<i64> = out.ready.iter().map(|f| f.pts_us).collect();
        assert_eq!(order, vec![0, 1, 2, 3, 4, 5, 6]);
        assert_eq!(out.depth, 2);
    }

    #[test]
    fn full_depth_keeps_late_frames_the_consumer_already_drained_past() {
        let mut out = Outputs {
            depth: MAX_REORDER,
            ..Outputs::default()
        };
        let mut order = Vec::new();
        for pts in [0, 4, 2, 1, 3, 8, 6, 5, 7] {
            out.push(frame(pts));
            order.extend(out.ready.drain(..).map(|f| f.pts_us));
        }
        out.flush();
        order.extend(out.ready.drain(..).map(|f| f.pts_us));
        assert_eq!(order, (0..9).collect::<Vec<i64>>());
    }

    #[test]
    fn parameter_set_updates_keep_the_parts_not_resent() {
        let mut sets = ParameterSets::default();
        assert!(sets.merge(ParameterSets {
            sps: vec![vec![0x67]],
            pps: vec![vec![0x68]],
            ..ParameterSets::default()
        }));
        assert!(sets.complete(VtCodec::H264));
        assert!(!sets.complete(VtCodec::H265));
        assert!(!sets.merge(ParameterSets {
            sps: vec![vec![0x67]],
            ..ParameterSets::default()
        }));
        assert!(sets.merge(ParameterSets {
            pps: vec![vec![0x68, 1]],
            ..ParameterSets::default()
        }));
        assert_eq!(sets.sps, vec![vec![0x67]]);
    }
}
