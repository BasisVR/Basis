//! Video through MediaCodec into an `AImageReader` surface: output stays
//! in the decoder's opaque layout (UBWC on Adreno) and frames surface as
//! `AHardwareBuffer` handles for the Vulkan present pass to import. A
//! codec output buffer is only rendered to the surface when the reader has
//! a slot for it, and every acquired image keeps the reader alive until
//! the handle drops.

use std::ffi::CStr;
use std::sync::Arc;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::{Duration, Instant};

use media_decode::{
    BufferGeometry, ColorInfo, DecodeError, OpaqueFrame, OpaqueImage, SubmitOutcome, VideoDecoder,
    VideoFrame, YuvMatrix, YuvRange,
};

use crate::driver::AsyncCodec;
use crate::ffi::*;

/// Reader slots: enough for the engine's whole decoded-frame budget
/// (FramePool slots + the parked frame + the render event's current and
/// retired frames) plus acquire headroom.
const MAX_IMAGES: i32 = 10;

/// Cumulative wait budget for the drain tail: once EOS is queued, output
/// is awaited (in slices) up to this long before the stream is declared
/// dry, so a broken codec cannot wedge the session's end.
const DRAIN_BUDGET: Duration = Duration::from_secs(2);
const DRAIN_SLICE: Duration = Duration::from_millis(20);

/// How long frames counted as on their way to the reader may go without one
/// arriving before the count is written off. A rendered frame reaches the
/// reader within a frame interval; a frame the BufferQueue dropped never
/// does, and some devices drop with `allow-frame-drop` off regardless.
const SURFACE_STALL: Duration = Duration::from_millis(500);

#[derive(Debug, Clone, Copy)]
pub enum VideoMime {
    H264,
    H265,
    Vp8,
    Vp9,
    Av1,
}

impl VideoMime {
    pub(crate) fn as_cstr(self) -> &'static CStr {
        match self {
            VideoMime::H264 => c"video/avc",
            VideoMime::H265 => c"video/hevc",
            VideoMime::Vp8 => c"video/x-vnd.on2.vp8",
            VideoMime::Vp9 => c"video/x-vnd.on2.vp9",
            VideoMime::Av1 => c"video/av01",
        }
    }
}

/// Owns the `AImageReader`; deleted only once the decoder and every
/// acquired image are gone (deleting the reader invalidates outstanding
/// images, so the images themselves hold the Arc).
struct ReaderHandle {
    reader: *mut AImageReader,
}

// SAFETY: the raw reader pointer is only used behind the Arc by the
// decode thread and image drops; libmediandk reader calls are internally
// synchronised.
unsafe impl Send for ReaderHandle {}
// SAFETY: as above: shared only through Arc, calls internally
// synchronised by libmediandk.
unsafe impl Sync for ReaderHandle {}

impl Drop for ReaderHandle {
    fn drop(&mut self) {
        // SAFETY: last owner. No codec writes into the surface any more
        // (the codec drops before the decoder's Arc) and no acquired
        // image survives (each held an Arc).
        unsafe { AImageReader_delete(self.reader) };
    }
}

/// One acquired frame, alive until the present pass is done with it.
struct McImage {
    image: *mut AImage,
    buffer: *mut AHardwareBuffer,
    geometry: BufferGeometry,
    reader: Arc<ReaderHandle>,
    alive: Arc<AtomicUsize>,
}

// SAFETY: the image is owned by this handle alone; deletion from another
// thread (the render side retiring frames) is the AImage lifecycle's
// supported shape.
unsafe impl Send for McImage {}

impl OpaqueImage for McImage {
    fn hardware_buffer(&self) -> *mut core::ffi::c_void {
        self.buffer.cast()
    }

    fn geometry(&self) -> Option<BufferGeometry> {
        Some(self.geometry)
    }
}

impl Drop for McImage {
    fn drop(&mut self) {
        // SAFETY: image acquired from the reader this handle keeps alive
        // (`reader` Arc drops after this), deleted exactly once.
        unsafe { AImage_delete(self.image) };
        self.alive.fetch_sub(1, Ordering::AcqRel);
        let _ = &self.reader;
    }
}

pub struct McVideoDecoder {
    // Declaration order is drop order: the codec stops before the reader
    // handle can release (images may outlive both via their own Arcs).
    codec: AsyncCodec,
    reader: Arc<ReaderHandle>,
    /// Output buffers rendered to the surface but not yet acquired.
    surface_pending: usize,
    /// When the reader last took a frame, or the pending count last rose
    /// from zero.
    surface_progress: Instant,
    /// Acquired images still alive anywhere in the pipeline.
    alive: Arc<AtomicUsize>,
    /// Output side saw the EOS-flagged buffer.
    eos_out: bool,
    drain_waited: Duration,
    /// Configuration carried out of band (AV1: the config OBUs from `av1C`),
    /// prepended to the first AU of each timeline. Not every decoder reads
    /// `csd-0`; every one reads the stream.
    config: Vec<u8>,
    config_due: bool,
    /// A live H.264 session, where each new SPS decides low-latency output.
    low_latency_live: bool,
    low_latency_on: bool,
    /// The SPS that made the last decision, so a repeat on every keyframe
    /// is not parsed again.
    last_sps: Vec<u8>,
}

impl McVideoDecoder {
    /// `live` H.264 opts into the codec's low-latency output while the
    /// stream's SPS shows it never reorders frames (see `submit`).
    pub fn new(
        mime: VideoMime,
        coded_width: u32,
        coded_height: u32,
        live: bool,
        codec_private: &[u8],
    ) -> Result<Self, DecodeError> {
        let width = coded_width.max(2) as i32;
        let height = coded_height.max(2) as i32;
        // SAFETY: reader creation + window query with checked status; the
        // format object is owned until AsyncCodec::start consumes it.
        unsafe {
            let mut reader: *mut AImageReader = core::ptr::null_mut();
            let status = AImageReader_newWithUsage(
                width,
                height,
                AIMAGE_FORMAT_PRIVATE,
                AHARDWAREBUFFER_USAGE_GPU_SAMPLED_IMAGE,
                MAX_IMAGES,
                &mut reader,
            );
            if status != AMEDIA_OK || reader.is_null() {
                return Err(DecodeError(format!("AImageReader_newWithUsage: {status}")));
            }
            let reader = Arc::new(ReaderHandle { reader });

            let mut window: *mut ANativeWindow = core::ptr::null_mut();
            let status = AImageReader_getWindow(reader.reader, &mut window);
            if status != AMEDIA_OK || window.is_null() {
                return Err(DecodeError(format!("AImageReader_getWindow: {status}")));
            }

            let format = AMediaFormat_new();
            if format.is_null() {
                return Err(DecodeError("AMediaFormat_new failed".into()));
            }
            AMediaFormat_setString(format, c"mime".as_ptr(), mime.as_cstr().as_ptr());
            AMediaFormat_setInt32(format, c"width".as_ptr(), width);
            AMediaFormat_setInt32(format, c"height".as_ptr(), height);
            // Every rendered frame has to reach the reader, or the pending
            // count below never comes down. The NDK constant is API 34; the
            // key itself is read since Android 10.
            AMediaFormat_setInt32(format, c"allow-frame-drop".as_ptr(), 0);

            let codec = AsyncCodec::start(mime.as_cstr(), format, window)?;
            crate::ffi::alog(&format!("mediacodec video: {}", codec.name));
            Ok(Self {
                codec,
                reader,
                surface_pending: 0,
                surface_progress: Instant::now(),
                alive: Arc::new(AtomicUsize::new(0)),
                eos_out: false,
                drain_waited: Duration::ZERO,
                config: codec_private.to_vec(),
                config_due: !codec_private.is_empty(),
                low_latency_live: live && matches!(mime, VideoMime::H264),
                low_latency_on: false,
                last_sps: Vec::new(),
            })
        }
    }

    /// Room in the reader for one more rendered buffer, with one slot of
    /// acquire headroom kept back.
    fn reader_has_room(&self) -> bool {
        self.alive.load(Ordering::Acquire) + self.surface_pending < (MAX_IMAGES as usize) - 1
    }

    /// Move ready codec outputs to the surface while the reader has room.
    fn render_ready_outputs(&mut self) -> Result<(), DecodeError> {
        while self.reader_has_room() {
            let Some((index, info)) = self.codec.pop_output(Duration::ZERO) else {
                return Ok(());
            };
            let render = info.size > 0;
            if !self.codec.release_output(index, render)? {
                continue;
            }
            if info.flags & BUFFER_FLAG_END_OF_STREAM != 0 {
                self.eos_out = true;
            }
            if render {
                if self.surface_pending == 0 {
                    self.surface_progress = Instant::now();
                }
                self.surface_pending += 1;
            }
        }
        Ok(())
    }

    fn acquire_image(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
        if self.surface_pending == 0 {
            return Ok(None);
        }
        // SAFETY: reader is live; out-params are locals checked before use.
        unsafe {
            let mut image: *mut AImage = core::ptr::null_mut();
            let mut fence_fd: i32 = -1;
            let status =
                AImageReader_acquireNextImageAsync(self.reader.reader, &mut image, &mut fence_fd);
            if status == AMEDIA_IMGREADER_NO_BUFFER_AVAILABLE {
                if self.surface_progress.elapsed() >= SURFACE_STALL {
                    crate::ffi::alog(&format!(
                        "mediacodec: {} rendered frames never reached the reader, written off ({})",
                        self.surface_pending, self.codec.name
                    ));
                    self.surface_pending = 0;
                }
                return Ok(None);
            }
            if status != AMEDIA_OK || image.is_null() {
                return Err(DecodeError(format!("acquireNextImageAsync: {status}")));
            }
            self.surface_pending = self.surface_pending.saturating_sub(1);
            self.surface_progress = Instant::now();

            // The producer's release fence: wait CPU-side (bounded) so the
            // buffer is safe to sample by the time the present pass sees
            // it. Decode has almost always finished by acquisition; the
            // poll is a correctness backstop, not a steady-state wait.
            if fence_fd >= 0 {
                let mut pfd = libc::pollfd {
                    fd: fence_fd,
                    events: libc::POLLIN,
                    revents: 0,
                };
                let _ = libc::poll(&mut pfd, 1, 100);
                libc::close(fence_fd);
            }

            let mut buffer: *mut AHardwareBuffer = core::ptr::null_mut();
            let status = AImage_getHardwareBuffer(image, &mut buffer);
            if status != AMEDIA_OK || buffer.is_null() {
                AImage_delete(image);
                return Err(DecodeError(format!("AImage_getHardwareBuffer: {status}")));
            }
            let mut timestamp_ns = 0i64;
            let status = AImage_getTimestamp(image, &mut timestamp_ns);
            if status != AMEDIA_OK {
                AImage_delete(image);
                return Err(DecodeError(format!("AImage_getTimestamp: {status}")));
            }
            let (mut width, mut height) = (0i32, 0i32);
            let status = AImage_getWidth(image, &mut width);
            if status != AMEDIA_OK {
                AImage_delete(image);
                return Err(DecodeError(format!("AImage_getWidth: {status}")));
            }
            let status = AImage_getHeight(image, &mut height);
            if status != AMEDIA_OK {
                AImage_delete(image);
                return Err(DecodeError(format!("AImage_getHeight: {status}")));
            }
            // The Vulkan import has to match the buffer's own size, which
            // the decoder pads to an aligned one (1080 rows in a 1088-row
            // buffer); the image size is the crop. A failed query leaves
            // zeros, and the frame is refused rather than sampled from a
            // made-up rectangle.
            let mut desc = AHardwareBuffer_Desc::default();
            AHardwareBuffer_describe(buffer, &mut desc);
            let mut rect = AImageCropRect::default();
            let crop = (AImage_getCropRect(image, &mut rect) == AMEDIA_OK).then_some([
                rect.left,
                rect.top,
                rect.right,
                rect.bottom,
            ]);
            let Some(geometry) = BufferGeometry::from_crop(
                desc.width,
                desc.height,
                width.max(0) as u32,
                height.max(0) as u32,
                crop,
            ) else {
                AImage_delete(image);
                return Err(DecodeError(format!(
                    "image geometry {width}x{height} in a {}x{} buffer",
                    desc.width, desc.height
                )));
            };

            self.alive.fetch_add(1, Ordering::AcqRel);
            let handle = McImage {
                image,
                buffer,
                geometry,
                reader: Arc::clone(&self.reader),
                alive: Arc::clone(&self.alive),
            };
            Ok(Some(VideoFrame::Opaque(OpaqueFrame {
                width: geometry.width,
                height: geometry.height,
                // Surface timestamps are the queued presentationTimeUs in
                // nanoseconds.
                pts_us: timestamp_ns / 1_000,
                color: self.color_info(),
                image: Box::new(handle),
            })))
        }
    }

    /// Advisory colour info from the codec's output format; the Vulkan
    /// import reads the driver's per-buffer suggestion at draw time and
    /// that is what the conversion actually uses.
    fn color_info(&self) -> ColorInfo {
        let format = self.codec.output_format();
        let matrix = match format.color_standard {
            COLOR_STANDARD_BT709 => YuvMatrix::Bt709,
            COLOR_STANDARD_BT601_PAL | COLOR_STANDARD_BT601_NTSC => YuvMatrix::Bt601,
            COLOR_STANDARD_BT2020 => YuvMatrix::Bt2020,
            _ => YuvMatrix::Unspecified,
        };
        let range = match format.color_range {
            COLOR_RANGE_FULL => YuvRange::Full,
            COLOR_RANGE_LIMITED => YuvRange::Limited,
            _ => YuvRange::Unspecified,
        };
        ColorInfo { matrix, range }
    }

    /// Low-latency output on live H.264, decided by each new SPS and set
    /// before the AU carrying it is queued. Only a stream that cannot reorder
    /// frames gets it: with B-frames, Qualcomm's low-latency decode is not
    /// trusted to keep presentation order. An encoder restart can bring
    /// B-frames in without a format change, so a later SPS can turn it off
    /// again. Off is only sent to undo an earlier on: Chromium found some
    /// devices break on the key at 0.
    fn decide_low_latency(&mut self, annexb: &[u8]) {
        let Some(sps) = media_bitstream::nal_units(annexb)
            .find(|nal| !nal.is_empty() && media_bitstream::h264_nal_type(nal[0]) == 7)
        else {
            return;
        };
        if sps == self.last_sps.as_slice() {
            return;
        }
        self.last_sps = sps.to_vec();
        let reorder_free = media_bitstream::sps_reorder_free(sps);
        let wanted = reorder_free == Some(true);
        if wanted == self.low_latency_on {
            if !wanted {
                crate::ffi::alog(&format!(
                    "mediacodec: low-latency left off, stream may reorder frames ({reorder_free:?}, {})",
                    self.codec.name
                ));
            }
            return;
        }
        match self.codec.set_low_latency(wanted) {
            Ok(()) => {
                self.low_latency_on = wanted;
                crate::ffi::alog(&format!(
                    "mediacodec: low-latency {}, stream {} ({reorder_free:?}, {})",
                    if wanted { "on" } else { "off" },
                    if wanted {
                        "never reorders"
                    } else {
                        "may reorder frames"
                    },
                    self.codec.name
                ));
            }
            Err(e) => {
                // Retry on the next repeat of this SPS.
                self.last_sps.clear();
                crate::ffi::alog(&e.0);
            }
        }
    }

    /// Drop every image still queued in the reader (seek/flush: stale
    /// timeline).
    fn drain_reader(&mut self) {
        loop {
            // SAFETY: reader is live; a failed acquire ends the loop.
            unsafe {
                let mut image: *mut AImage = core::ptr::null_mut();
                let mut fence_fd: i32 = -1;
                let status = AImageReader_acquireNextImageAsync(
                    self.reader.reader,
                    &mut image,
                    &mut fence_fd,
                );
                if status != AMEDIA_OK || image.is_null() {
                    break;
                }
                if fence_fd >= 0 {
                    libc::close(fence_fd);
                }
                AImage_delete(image);
            }
        }
        self.surface_pending = 0;
    }
}

impl VideoDecoder for McVideoDecoder {
    fn submit(&mut self, annexb: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        // Keep outputs flowing towards the surface so input buffers recycle.
        self.render_ready_outputs()?;
        if self.low_latency_live {
            self.decide_low_latency(annexb);
        }
        if !self.config_due {
            return self.codec.submit(annexb, pts_us);
        }
        let mut joined = Vec::with_capacity(self.config.len() + annexb.len());
        joined.extend_from_slice(&self.config);
        joined.extend_from_slice(annexb);
        let outcome = self.codec.submit(&joined, pts_us)?;
        // A refused submit is retried with the same AU, which has to carry the
        // configuration again.
        if outcome == SubmitOutcome::Accepted {
            self.config_due = false;
        }
        Ok(outcome)
    }

    fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
        if let Some(e) = self.codec.take_error() {
            return Err(e);
        }
        self.render_ready_outputs()?;
        if let Some(frame) = self.acquire_image()? {
            return Ok(Some(frame));
        }
        // Drain tail: EOS is queued but the pipeline still holds frames.
        // Wait one slice per call under the cumulative budget, so a
        // transient gap between outputs is not mistaken for dry while the
        // caller stays responsive between calls. A seek's flush must not
        // wait out a codec that never flags EOS (the OMX avc decoder on
        // Quest never does). `drain_dry` reports false until the tail is
        // in or the budget is spent; the engine keeps polling. Only time
        // spent waiting on a silent codec counts: an output held back by a
        // full reader is the tail still arriving.
        if self.codec.draining() && !self.eos_out {
            if self.drain_waited < DRAIN_BUDGET {
                let waiting = Instant::now();
                match self.codec.pop_output(DRAIN_SLICE) {
                    // Put it back through the normal path.
                    Some(entry) => self.codec.cb.lock().output_ready.push_front(entry),
                    None => self.drain_waited += waiting.elapsed(),
                }
                self.render_ready_outputs()?;
                if let Some(frame) = self.acquire_image()? {
                    return Ok(Some(frame));
                }
                if let Some(e) = self.codec.take_error() {
                    return Err(e);
                }
            }
            if !self.eos_out && self.drain_waited >= DRAIN_BUDGET {
                crate::ffi::alog(&format!(
                    "mediacodec drain timed out on {}; declaring dry",
                    self.codec.name
                ));
                self.eos_out = true;
            }
        }
        // After EOS the surface may still hold the last frames.
        if self.eos_out
            && let Some(frame) = self.acquire_image()?
        {
            return Ok(Some(frame));
        }
        Ok(None)
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        self.render_ready_outputs()?;
        self.codec.begin_drain()
    }

    fn drain_dry(&self) -> bool {
        self.eos_out
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        self.codec.reset()?;
        self.drain_reader();
        self.eos_out = false;
        self.drain_waited = Duration::ZERO;
        self.config_due = !self.config.is_empty();
        Ok(())
    }
}
