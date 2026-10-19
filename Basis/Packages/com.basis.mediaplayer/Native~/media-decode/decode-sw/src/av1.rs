//! AV1 software decode on rav1d (the pure-Rust dav1d port, driven through
//! its dav1d-compatible C API). 8-bit 4:2:0 only: the present layer takes
//! NV12 and has no P010 path, so higher depths are a typed refusal.

use std::ptr::NonNull;

use media_decode::{
    ColorInfo, DecodeError, Nv12Frame, SubmitOutcome, VideoDecoder, VideoFrame, YuvMatrix,
    YuvRange, packed_nv12_len,
};
use rav1d::include::dav1d::data::Dav1dData;
use rav1d::include::dav1d::dav1d::{Dav1dContext, Dav1dSettings};
use rav1d::include::dav1d::headers::{
    DAV1D_MC_BT470BG, DAV1D_MC_BT601, DAV1D_MC_BT709, DAV1D_MC_BT2020_CL, DAV1D_MC_BT2020_NCL,
    DAV1D_PIXEL_LAYOUT_I420,
};
use rav1d::include::dav1d::picture::Dav1dPicture;
use rav1d::src::lib::{
    dav1d_close, dav1d_data_create, dav1d_data_unref, dav1d_default_settings, dav1d_flush,
    dav1d_get_picture, dav1d_open, dav1d_picture_unref, dav1d_send_data,
};

const AGAIN: i32 = -libc::EAGAIN;

pub struct SwAv1Decoder {
    ctx: Dav1dContext,
    /// A packet dav1d has partially consumed and still owns bytes of;
    /// flushed before anything new is accepted.
    pending: Option<Dav1dData>,
}

// SAFETY: the Dav1dContext is an owned handle used from one thread at a
// time (the engine's video thread); rav1d's own internals are Sync.
unsafe impl Send for SwAv1Decoder {}

impl SwAv1Decoder {
    /// `max_pixels` bounds every frame the bitstream states, which the
    /// container's dimensions do not: a frame header past it fails the
    /// decode before rav1d allocates the picture. Zero is refused: rav1d
    /// reads it as no limit.
    pub fn new(max_pixels: u32) -> Result<Self, DecodeError> {
        if max_pixels == 0 {
            return Err(DecodeError(
                "rav1d frame size limit must be above zero".into(),
            ));
        }
        // SAFETY: out-params are locals; settings is fully initialised by
        // dav1d_default_settings before dav1d_open reads it.
        unsafe {
            let mut settings = std::mem::MaybeUninit::<Dav1dSettings>::uninit();
            dav1d_default_settings(NonNull::new_unchecked(settings.as_mut_ptr()));
            let mut settings = settings.assume_init();
            settings.frame_size_limit = max_pixels;
            let mut ctx: Option<Dav1dContext> = None;
            let result = dav1d_open(
                Some(NonNull::from(&mut ctx)),
                Some(NonNull::from(&mut settings)),
            );
            if result.0 != 0 {
                return Err(DecodeError(format!("rav1d open failed ({})", result.0)));
            }
            let ctx = ctx.ok_or_else(|| DecodeError("rav1d open returned no context".into()))?;
            Ok(Self { ctx, pending: None })
        }
    }

    /// Try to hand a queued packet to the decoder. Ok(true) = fully
    /// consumed.
    fn flush_pending(&mut self) -> Result<bool, DecodeError> {
        let Some(mut data) = self.pending.take() else {
            return Ok(true);
        };
        // SAFETY: `data` came from dav1d_data_create and has not been
        // unref'd; the context is live.
        unsafe {
            let result = dav1d_send_data(Some(self.ctx), Some(NonNull::from(&mut data)));
            if result.0 == 0 && data.sz == 0 {
                dav1d_data_unref(Some(NonNull::from(&mut data)));
                return Ok(true);
            }
            if result.0 == 0 || result.0 == AGAIN {
                // Partially consumed or refused whole: keep the remainder.
                self.pending = Some(data);
                return Ok(false);
            }
            dav1d_data_unref(Some(NonNull::from(&mut data)));
            Err(DecodeError(format!("rav1d send failed ({})", result.0)))
        }
    }

    fn convert(&self, pic: &Dav1dPicture) -> Result<Nv12Frame, DecodeError> {
        if pic.p.layout != DAV1D_PIXEL_LAYOUT_I420 || pic.p.bpc != 8 {
            return Err(DecodeError(format!(
                "AV1 output {} bpc layout {} unsupported (8-bit 4:2:0 only)",
                pic.p.bpc, pic.p.layout
            )));
        }
        let width = pic.p.w as usize;
        let height = pic.p.h as usize;
        // An AV1 frame size is `frame_width_minus_1 + 1`, so it may be odd,
        // and NV12 cannot carry that: its chroma plane is exactly half the
        // luma in each axis. `packed_nv12_len` refuses it too; this check
        // stays for the clearer message.
        if !width.is_multiple_of(2) || !height.is_multiple_of(2) {
            return Err(DecodeError(format!(
                "AV1 picture {width}x{height} has no NV12 representation"
            )));
        }
        let (y, u, v) = match (pic.data[0], pic.data[1], pic.data[2]) {
            (Some(y), Some(u), Some(v)) => (y, u, v),
            _ => return Err(DecodeError("AV1 picture without planes".into())),
        };
        let Some((y_stride, uv_stride)) = checked_strides(pic.stride[0], pic.stride[1], width)
        else {
            return Err(DecodeError(format!(
                "AV1 picture strides {}/{} cannot cover {width}x{height}",
                pic.stride[0], pic.stride[1]
            )));
        };
        let mut data = vec![0u8; packed_nv12_len("AV1 picture", width, height)?];
        // SAFETY: the picture's planes are valid for its stated geometry
        // until dav1d_picture_unref; the strides are checked forwards and
        // at least as wide as the rows they carry above, so every row read
        // is within the plane, and the destination is sized for the
        // packed frame up front.
        unsafe {
            let y = y.as_ptr() as *const u8;
            for row in 0..height {
                std::ptr::copy_nonoverlapping(
                    y.add(row * y_stride),
                    data.as_mut_ptr().add(row * width),
                    width,
                );
            }
            let u = u.as_ptr() as *const u8;
            let v = v.as_ptr() as *const u8;
            let uv_out = data.as_mut_ptr().add(width * height);
            for row in 0..height / 2 {
                let u_row = u.add(row * uv_stride);
                let v_row = v.add(row * uv_stride);
                let out_row = uv_out.add(row * width);
                for x in 0..width / 2 {
                    *out_row.add(2 * x) = *u_row.add(x);
                    *out_row.add(2 * x + 1) = *v_row.add(x);
                }
            }
        }

        // SAFETY: seq_hdr is valid while the picture is (unref'd after
        // this call returns to the caller).
        let color = unsafe {
            pic.seq_hdr.map_or(ColorInfo::default(), |hdr| {
                let hdr = hdr.as_ref();
                ColorInfo {
                    matrix: match hdr.mtrx {
                        m if m == DAV1D_MC_BT709 => YuvMatrix::Bt709,
                        m if m == DAV1D_MC_BT601 || m == DAV1D_MC_BT470BG => YuvMatrix::Bt601,
                        m if m == DAV1D_MC_BT2020_NCL || m == DAV1D_MC_BT2020_CL => {
                            YuvMatrix::Bt2020
                        }
                        _ => YuvMatrix::Unspecified,
                    },
                    range: if hdr.color_range != 0 {
                        YuvRange::Full
                    } else {
                        YuvRange::Limited
                    },
                }
            })
        };

        Ok(Nv12Frame {
            width: width as u32,
            height: height as u32,
            pts_us: pic.m.timestamp,
            color,
            data,
        })
    }
}

impl VideoDecoder for SwAv1Decoder {
    fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if !self.flush_pending()? {
            return Ok(SubmitOutcome::NotAccepting);
        }
        // SAFETY: dav1d_data_create returns a writable buffer of exactly
        // au.len() bytes (or null on failure); the copy fills it fully.
        // Accepted here means this adapter owns the bytes: a partial or
        // refused send parks the Dav1dData in `pending`.
        unsafe {
            let mut data = Dav1dData::default();
            let buf = dav1d_data_create(Some(NonNull::from(&mut data)), au.len());
            if buf.is_null() {
                return Err(DecodeError("rav1d data alloc failed".into()));
            }
            std::ptr::copy_nonoverlapping(au.as_ptr(), buf, au.len());
            data.m.timestamp = pts_us;
            self.pending = Some(data);
        }
        self.flush_pending()?;
        Ok(SubmitOutcome::Accepted)
    }

    fn try_output(&mut self) -> Result<Option<VideoFrame>, DecodeError> {
        // SAFETY: the context is live; the picture out-param starts
        // zeroed/default and is unref'd on every path after a successful
        // return.
        unsafe {
            let mut pic = Dav1dPicture::default();
            let result = dav1d_get_picture(Some(self.ctx), Some(NonNull::from(&mut pic)));
            if result.0 == AGAIN {
                return Ok(None);
            }
            if result.0 != 0 {
                return Err(DecodeError(format!(
                    "rav1d get_picture failed ({})",
                    result.0
                )));
            }
            let frame = self.convert(&pic);
            dav1d_picture_unref(Some(NonNull::from(&mut pic)));
            frame.map(|f| Some(VideoFrame::from(f)))
        }
    }

    fn begin_drain(&mut self) -> Result<(), DecodeError> {
        // dav1d drains by pulling: get_picture without new sends flushes
        // the reorder queue until it reports EAGAIN.
        let _ = self.flush_pending()?;
        Ok(())
    }

    fn reset(&mut self) -> Result<(), DecodeError> {
        // SAFETY: the context is live; a parked packet is released before
        // the flush.
        unsafe {
            if let Some(mut data) = self.pending.take() {
                dav1d_data_unref(Some(NonNull::from(&mut data)));
            }
            dav1d_flush(self.ctx);
        }
        Ok(())
    }
}

impl Drop for SwAv1Decoder {
    fn drop(&mut self) {
        // SAFETY: releases what this adapter owns, exactly once: the
        // parked packet if any, then the context.
        unsafe {
            if let Some(mut data) = self.pending.take() {
                dav1d_data_unref(Some(NonNull::from(&mut data)));
            }
            let mut ctx = Some(self.ctx);
            dav1d_close(Some(NonNull::from(&mut ctx)));
        }
    }
}

/// The decoder states signed strides per picture. `None` for a negative
/// one (a bottom-up plane the forward row reads in `convert` cannot
/// follow) or one shorter than the row it carries.
fn checked_strides(y: isize, uv: isize, width: usize) -> Option<(usize, usize)> {
    let y = usize::try_from(y).ok()?;
    let uv = usize::try_from(uv).ok()?;
    (y >= width && uv >= width.div_ceil(2)).then_some((y, uv))
}

#[cfg(test)]
mod tests {
    use super::checked_strides;

    #[test]
    fn a_picture_stride_that_cannot_cover_its_rows_is_refused() {
        assert_eq!(checked_strides(1920, 960, 1920), Some((1920, 960)));
        assert_eq!(checked_strides(2048, 1024, 1920), Some((2048, 1024)));
        // Bottom-up planes, either one.
        assert_eq!(checked_strides(-1920, 960, 1920), None);
        assert_eq!(checked_strides(1920, -960, 1920), None);
        // Shorter than the row it carries, either plane.
        assert_eq!(checked_strides(1919, 960, 1920), None);
        assert_eq!(checked_strides(1920, 959, 1920), None);
    }
}
