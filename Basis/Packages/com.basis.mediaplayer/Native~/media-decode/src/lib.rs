//! Decode trait + format types shared by the platform adapters.

#![forbid(unsafe_code)]

use std::fmt;

/// YUV→RGB matrix, as stated by the decoder (colour comes from the
/// stream's own reported parameters, never guessed from dimensions).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum YuvMatrix {
    /// The decoder did not state a matrix; converters fall back to BT.601.
    #[default]
    Unspecified,
    Bt601,
    Bt709,
    Bt2020,
}

/// Sample range, as stated by the decoder.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum YuvRange {
    /// The decoder did not state a range; converters fall back to limited.
    #[default]
    Unspecified,
    /// Studio swing: Y in 16..=235, chroma in 16..=240.
    Limited,
    /// Full swing: 0..=255.
    Full,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct ColorInfo {
    pub matrix: YuvMatrix,
    pub range: YuvRange,
}

/// A decoded NV12 frame, tightly packed: `height` rows of Y, then `height / 2`
/// rows of interleaved UV. `width`/`height` are the decoder's coded dimensions,
/// which may exceed the display dimensions (e.g. 640x368 coded for 640x360).
pub struct Nv12Frame {
    pub width: u32,
    pub height: u32,
    pub pts_us: i64,
    pub color: ColorInfo,
    pub data: Vec<u8>,
}

/// Bytes a packed [`Nv12Frame`] of this geometry occupies: a full-width Y
/// plane followed by a half-height interleaved chroma plane. Every route
/// that packs one sizes its destination from here.
///
/// The destination is allocated before the decoder has described the
/// memory it will be copied from, so this is where an implausible frame
/// size is caught: a product that wraps would allocate short and the copy
/// would then write past the end.
///
/// Odd dimensions are refused rather than rounded. NV12's chroma plane is
/// exactly half the luma in each axis, so an odd dimension has no
/// representation: rounding down drops the bottom row of the picture, and
/// rounding up lets a copy write past the buffer.
pub fn packed_nv12_len(tag: &str, width: usize, height: usize) -> Result<usize, DecodeError> {
    if !width.is_multiple_of(2) || !height.is_multiple_of(2) {
        return Err(DecodeError(format!(
            "{tag}: NV12 geometry {width}x{height} is not representable at odd dimensions"
        )));
    }
    width
        .checked_mul(height)
        .and_then(|y| y.checked_add(width.checked_mul(height / 2)?))
        .ok_or_else(|| {
            DecodeError(format!(
                "{tag}: packed NV12 geometry {width}x{height} overflows"
            ))
        })
}

/// A decoded frame that never left the decoder's own memory: an opaque
/// GPU buffer (Android `AHardwareBuffer` under the MediaCodec adapter,
/// a DXVA `IMFSample` texture-array slice under the Windows hardware
/// adapter), presented by importing or copying the buffer GPU-side
/// rather than reading pixels. Dropping the handle returns the buffer to
/// its owner (the adapter's image reader / the MFT's surface pool), so a
/// handle must stay alive until the present pass has consumed it.
pub trait OpaqueImage: Send {
    /// The platform buffer handle (`AHardwareBuffer*` on Android,
    /// `IMFSample*` on Windows). Valid for the lifetime of this object.
    fn hardware_buffer(&self) -> *mut core::ffi::c_void;

    /// Windows DXVA payloads: the decoded NV12 plane as
    /// (`ID3D11Texture2D*`, subresource index). The texture is a slice of
    /// the decoder's array, so never assume slice 0. Both are valid for the
    /// lifetime of this object. `None` on every other platform's payload.
    fn d3d11_slice(&self) -> Option<(*mut core::ffi::c_void, u32)> {
        None
    }

    /// Android payloads: the buffer's allocated size and where the picture
    /// sits in it. `None` where the frame's width and height are the whole
    /// buffer.
    fn geometry(&self) -> Option<BufferGeometry> {
        None
    }
}

/// Where a decoded picture sits in its buffer. Hardware decoders allocate at
/// an aligned size (1920x1088 for 1080p) and report the picture as a crop.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct BufferGeometry {
    pub buffer_width: u32,
    pub buffer_height: u32,
    pub left: u32,
    pub top: u32,
    pub width: u32,
    pub height: u32,
}

impl BufferGeometry {
    /// A picture that fills its buffer.
    pub fn whole(width: u32, height: u32) -> Self {
        Self {
            buffer_width: width,
            buffer_height: height,
            left: 0,
            top: 0,
            width,
            height,
        }
    }

    /// The picture from a crop rectangle (`[left, top, right, bottom]`, right
    /// and bottom exclusive). A crop that is missing, empty or not inside the
    /// buffer falls back to the image size from the top-left corner, clipped
    /// to the buffer. `None` when the buffer or the picture is empty.
    pub fn from_crop(
        buffer_width: u32,
        buffer_height: u32,
        image_width: u32,
        image_height: u32,
        crop: Option<[i32; 4]>,
    ) -> Option<Self> {
        if buffer_width == 0 || buffer_height == 0 {
            return None;
        }
        let fits = |[left, top, right, bottom]: [i32; 4]| {
            left >= 0
                && top >= 0
                && right > left
                && bottom > top
                && right as u32 <= buffer_width
                && bottom as u32 <= buffer_height
        };
        let (left, top, width, height) = match crop.filter(|&c| fits(c)) {
            Some([left, top, right, bottom]) => (
                left as u32,
                top as u32,
                (right - left) as u32,
                (bottom - top) as u32,
            ),
            None => (
                0,
                0,
                image_width.min(buffer_width),
                image_height.min(buffer_height),
            ),
        };
        (width > 0 && height > 0).then_some(Self {
            buffer_width,
            buffer_height,
            left,
            top,
            width,
            height,
        })
    }
}

/// A decoded frame kept in decoder-native GPU memory (opaque
/// end-to-end, never CPU-locked).
pub struct OpaqueFrame {
    pub width: u32,
    pub height: u32,
    pub pts_us: i64,
    pub color: ColorInfo,
    pub image: Box<dyn OpaqueImage>,
}

/// One decoded video frame, in whichever memory the adapter produces.
pub enum VideoFrame {
    /// CPU NV12 (the Windows sync-MFT and software decoders).
    Nv12(Nv12Frame),
    /// Decoder-native GPU buffer (the MediaCodec adapter).
    Opaque(OpaqueFrame),
}

impl VideoFrame {
    pub fn pts_us(&self) -> i64 {
        match self {
            VideoFrame::Nv12(f) => f.pts_us,
            VideoFrame::Opaque(f) => f.pts_us,
        }
    }

    pub fn width(&self) -> u32 {
        match self {
            VideoFrame::Nv12(f) => f.width,
            VideoFrame::Opaque(f) => f.width,
        }
    }

    pub fn height(&self) -> u32 {
        match self {
            VideoFrame::Nv12(f) => f.height,
            VideoFrame::Opaque(f) => f.height,
        }
    }

    pub fn color(&self) -> ColorInfo {
        match self {
            VideoFrame::Nv12(f) => f.color,
            VideoFrame::Opaque(f) => f.color,
        }
    }

    /// The CPU NV12 payload, when this frame has one (test oracles and
    /// the probe tool inspect pixels; the engine matches on the variant).
    pub fn as_nv12(&self) -> Option<&Nv12Frame> {
        match self {
            VideoFrame::Nv12(f) => Some(f),
            VideoFrame::Opaque(_) => None,
        }
    }
}

impl From<Nv12Frame> for VideoFrame {
    fn from(frame: Nv12Frame) -> Self {
        VideoFrame::Nv12(frame)
    }
}

#[derive(Debug)]
pub struct DecodeError(pub String);

impl fmt::Display for DecodeError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "decode error: {}", self.0)
    }
}

impl std::error::Error for DecodeError {}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SubmitOutcome {
    Accepted,
    /// The decoder's input queue is full; drain outputs and resubmit.
    NotAccepting,
}

/// One decoded PCM chunk: interleaved f32, `data.len() == frames * channels`.
/// `sample_rate`/`channels` are the decoder's *output* format, which can
/// change mid-stream (HE-AAC renegotiates when in-band SBR doubles the core
/// rate), so consumers re-check per chunk.
pub struct PcmChunk {
    pub sample_rate: u32,
    pub channels: u32,
    pub pts_us: i64,
    pub data: Vec<f32>,
}

pub trait AudioDecoder {
    /// The output format the decoder will produce, `(sample_rate,
    /// channels)`, as negotiated at construction. This is the format the
    /// ring is built for; chunks re-state it and can diverge mid-stream
    /// (HE-AAC renegotiation).
    fn output_format(&self) -> (u32, u32);

    /// Submit one raw compressed frame with its presentation timestamp.
    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError>;

    /// Pull one decoded chunk if one is ready.
    fn try_output(&mut self) -> Result<Option<PcmChunk>, DecodeError>;

    /// Signal end of stream; keep calling `try_output` until it returns
    /// `None` *and* [`AudioDecoder::drain_dry`] reports true.
    fn begin_drain(&mut self) -> Result<(), DecodeError>;

    /// After `begin_drain`, whether a `None` from `try_output` means the
    /// stream is truly dry. Synchronous adapters drain inline, so `None`
    /// is always dry (the default). An adapter whose tail arrives
    /// asynchronously returns false while output may still come, bounding
    /// its own wait so this eventually reports true. The caller keeps
    /// polling rather than blocking inside one call, so it stays
    /// responsive to flushes.
    fn drain_dry(&self) -> bool {
        true
    }

    /// Flush and restart the stream (loop / seek).
    fn reset(&mut self) -> Result<(), DecodeError>;
}

pub trait VideoDecoder {
    /// After an error: whether this hardware adapter detected the platform
    /// decoder silently falling back to CPU output (the Windows DXVA
    /// no-DXGI-backing signal). The caller reroutes to the software path
    /// and reports `DecodeFallbackHwToSw` instead of failing the session.
    /// Software and well-behaved adapters never report it (the default).
    fn hardware_fell_back(&self) -> bool {
        false
    }

    /// Submit one Annex-B access unit with its presentation timestamp.
    fn submit(&mut self, annexb: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError>;

    /// Pull one decoded frame if one is ready.
    fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError>;

    /// Signal end of stream; keep calling `try_output` until it returns
    /// `None` *and* [`VideoDecoder::drain_dry`] reports true.
    fn begin_drain(&mut self) -> Result<(), DecodeError>;

    /// After `begin_drain`, whether a `None` from `try_output` means the
    /// stream is truly dry. Synchronous adapters drain inline, so `None`
    /// is always dry (the default). An adapter whose tail arrives
    /// asynchronously returns false while output may still come, bounding
    /// its own wait so this eventually reports true. The caller keeps
    /// polling rather than blocking inside one call, so it stays
    /// responsive to flushes.
    fn drain_dry(&self) -> bool {
        true
    }

    /// Flush and restart the stream (loop / seek).
    fn reset(&mut self) -> Result<(), DecodeError>;
}

#[cfg(test)]
mod tests {
    use super::BufferGeometry;

    fn geometry(left: u32, top: u32, width: u32, height: u32) -> BufferGeometry {
        BufferGeometry {
            buffer_width: 1920,
            buffer_height: 1088,
            left,
            top,
            width,
            height,
        }
    }

    #[test]
    fn a_crop_inside_the_buffer_is_the_picture() {
        let crop = Some([0, 0, 1920, 1080]);
        assert_eq!(
            BufferGeometry::from_crop(1920, 1088, 1920, 1080, crop),
            Some(geometry(0, 0, 1920, 1080))
        );
        let offset = Some([8, 4, 1912, 1084]);
        assert_eq!(
            BufferGeometry::from_crop(1920, 1088, 1904, 1080, offset),
            Some(geometry(8, 4, 1904, 1080))
        );
    }

    #[test]
    fn a_crop_reaching_the_buffer_edge_is_kept_and_one_past_it_is_not() {
        let edge = Some([0, 8, 1920, 1088]);
        assert_eq!(
            BufferGeometry::from_crop(1920, 1088, 1920, 1080, edge),
            Some(geometry(0, 8, 1920, 1080))
        );
        let past = Some([0, 9, 1920, 1089]);
        assert_eq!(
            BufferGeometry::from_crop(1920, 1088, 1920, 1080, past),
            Some(geometry(0, 0, 1920, 1080))
        );
    }

    #[test]
    fn a_missing_empty_or_negative_crop_falls_back_to_the_image_size() {
        for crop in [
            None,
            Some([0, 0, 0, 0]),
            Some([10, 10, 10, 20]),
            Some([-1, 0, 1919, 1080]),
            Some([100, 0, 50, 1080]),
        ] {
            assert_eq!(
                BufferGeometry::from_crop(1920, 1088, 1920, 1080, crop),
                Some(geometry(0, 0, 1920, 1080)),
                "{crop:?}"
            );
        }
    }

    #[test]
    fn the_fallback_never_runs_past_the_buffer() {
        assert_eq!(
            BufferGeometry::from_crop(1920, 1088, 4096, 2160, None),
            Some(geometry(0, 0, 1920, 1088))
        );
    }

    #[test]
    fn an_empty_buffer_or_picture_has_no_geometry() {
        assert_eq!(BufferGeometry::from_crop(0, 1088, 1920, 1080, None), None);
        assert_eq!(BufferGeometry::from_crop(1920, 0, 1920, 1080, None), None);
        assert_eq!(BufferGeometry::from_crop(1920, 1088, 0, 1080, None), None);
    }
}
