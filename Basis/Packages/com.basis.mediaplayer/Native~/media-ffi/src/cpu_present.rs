use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::Ordering;

use media_decode::{Nv12Frame, VideoFrame, YuvMatrix, YuvRange};

use crate::{BM_ERR_INVALID_ARG, BM_ERR_INVALID_HANDLE, BM_ERR_PANIC, lookup};

pub const BM_ERR_SIZE: i32 = -6;

struct Coefficients {
    y: i32,
    rv: i32,
    gu: i32,
    gv: i32,
    bu: i32,
    y_offset: i32,
}

fn coefficients(matrix: YuvMatrix, range: YuvRange) -> Coefficients {
    let full = range == YuvRange::Full;
    let (kr, kb) = match matrix {
        YuvMatrix::Bt709 => (0.2126, 0.0722),
        YuvMatrix::Bt2020 => (0.2627, 0.0593),
        YuvMatrix::Bt601 | YuvMatrix::Unspecified => (0.299, 0.114),
    };
    let kg = 1.0 - kr - kb;
    let (y_scale, c_scale) = if full {
        (1.0, 1.0)
    } else {
        (255.0 / 219.0, 255.0 / 224.0)
    };
    let q = |v: f64| (v * 65536.0).round() as i32;
    Coefficients {
        y: q(y_scale),
        rv: q(2.0 * (1.0 - kr) * c_scale),
        gu: q(2.0 * (1.0 - kb) * kb / kg * c_scale),
        gv: q(2.0 * (1.0 - kr) * kr / kg * c_scale),
        bu: q(2.0 * (1.0 - kb) * c_scale),
        y_offset: if full { 0 } else { 16 },
    }
}

fn clamp(v: i32) -> u8 {
    ((v + 32768) >> 16).clamp(0, 255) as u8
}

fn convert_rows(frame: &Nv12Frame, c: &Coefficients, first_row: usize, out: &mut [u8]) {
    let w = frame.width as usize;
    let h = frame.height as usize;
    let (y_plane, uv_plane) = frame.data.split_at(w * h);
    for (i, row) in out.chunks_exact_mut(w * 4).enumerate() {
        let y = first_row + i;
        let y_row = &y_plane[y * w..y * w + w];
        let uv_row = &uv_plane[(y / 2) * w..(y / 2) * w + w];
        for x in 0..w {
            let luma = (i32::from(y_row[x]) - c.y_offset) * c.y;
            let u = i32::from(uv_row[x & !1]) - 128;
            let v = i32::from(uv_row[(x & !1) + 1]) - 128;
            let px = &mut row[x * 4..x * 4 + 4];
            px[0] = clamp(luma + c.bu * u);
            px[1] = clamp(luma - c.gu * u - c.gv * v);
            px[2] = clamp(luma + c.rv * v);
            px[3] = 255;
        }
    }
}

pub fn nv12_to_bgra(frame: &Nv12Frame, out: &mut [u8]) -> bool {
    let w = frame.width as usize;
    let h = frame.height as usize;
    if w == 0 || h == 0 || out.len() < w * h * 4 || frame.data.len() < w * h + w * h.div_ceil(2) {
        return false;
    }
    let c = coefficients(frame.color.matrix, frame.color.range);
    let out = &mut out[..w * h * 4];
    let bands = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1)
        .clamp(1, 8)
        .min(h.div_ceil(2));
    let rows_per_band = h.div_ceil(bands).next_multiple_of(2);
    if bands <= 1 || h < 64 {
        convert_rows(frame, &c, 0, out);
        return true;
    }
    std::thread::scope(|scope| {
        for (band, chunk) in out.chunks_mut(rows_per_band * w * 4).enumerate() {
            let c = &c;
            scope.spawn(move || convert_rows(frame, c, band * rows_per_band, chunk));
        }
    });
    true
}

/// # Safety
/// `dst` must point to `cap` writable bytes; `width` and `height` must be
/// writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_take_bgra(
    handle: u64,
    dst: *mut u8,
    cap: usize,
    width: *mut u32,
    height: *mut u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if dst.is_null() || width.is_null() || height.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Some(VideoFrame::Nv12(frame)) = media_engine::render_take(&entry.pipeline) else {
            return 0;
        };
        // SAFETY: caller contract: both out-params are writable.
        unsafe {
            *width = frame.width;
            *height = frame.height;
        }
        let needed = frame.width as usize * frame.height as usize * 4;
        if cap < needed {
            return BM_ERR_SIZE;
        }
        // SAFETY: caller contract: dst holds cap bytes, checked to cover the
        // converted frame.
        let out = unsafe { std::slice::from_raw_parts_mut(dst, needed) };
        if !nv12_to_bgra(&frame, out) {
            return BM_ERR_SIZE;
        }
        entry
            .shared
            .frames_presented
            .fetch_add(1, Ordering::Relaxed);
        1
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[cfg(test)]
mod tests {
    use super::*;
    use media_decode::ColorInfo;

    fn frame(w: u32, h: u32, y: u8, u: u8, v: u8, matrix: YuvMatrix, range: YuvRange) -> Nv12Frame {
        let mut data = vec![y; (w * h) as usize];
        for _ in 0..(w * h / 4) {
            data.push(u);
            data.push(v);
        }
        Nv12Frame {
            width: w,
            height: h,
            pts_us: 0,
            color: ColorInfo { matrix, range },
            data,
        }
    }

    #[test]
    fn limited_range_black_white_and_red() {
        let mut out = vec![0u8; 16 * 16 * 4];
        assert!(nv12_to_bgra(
            &frame(16, 16, 16, 128, 128, YuvMatrix::Bt709, YuvRange::Limited),
            &mut out
        ));
        assert_eq!(&out[..4], &[0, 0, 0, 255]);
        assert!(nv12_to_bgra(
            &frame(16, 16, 235, 128, 128, YuvMatrix::Bt709, YuvRange::Limited),
            &mut out
        ));
        assert_eq!(&out[..4], &[255, 255, 255, 255]);
        assert!(nv12_to_bgra(
            &frame(16, 16, 63, 102, 240, YuvMatrix::Bt709, YuvRange::Limited),
            &mut out
        ));
        let (b, g, r) = (out[0], out[1], out[2]);
        assert!(
            r > 240 && g < 20 && b < 20,
            "bt709 red came out as {r},{g},{b}"
        );
    }

    #[test]
    fn banded_conversion_matches_single_pass() {
        let mut f = frame(256, 144, 0, 0, 0, YuvMatrix::Bt601, YuvRange::Full);
        for (i, b) in f.data.iter_mut().enumerate() {
            *b = (i * 7 % 251) as u8;
        }
        let mut banded = vec![0u8; 256 * 144 * 4];
        let mut single = vec![0u8; 256 * 144 * 4];
        assert!(nv12_to_bgra(&f, &mut banded));
        convert_rows(
            &f,
            &coefficients(YuvMatrix::Bt601, YuvRange::Full),
            0,
            &mut single,
        );
        assert_eq!(banded, single);
    }

    #[test]
    fn refuses_a_short_destination() {
        let mut out = vec![0u8; 10];
        assert!(!nv12_to_bgra(
            &frame(16, 16, 16, 128, 128, YuvMatrix::Bt709, YuvRange::Limited),
            &mut out
        ));
    }
}
