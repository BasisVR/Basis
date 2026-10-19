//! The conversion-pass maths, shared between the GPU pass (which uploads
//! [`coefficients`] as its constant buffer) and the CPU reference converter
//! the validation tests hold the GPU output against.

use media_decode::{ColorInfo, YuvMatrix, YuvRange};

/// Row-vector coefficients for `dot((y, u, v, 1), row)` per output channel
/// (R, G, B), with y/u/v normalised to `[0, 1]`. Unspecified matrix/range
/// resolve to BT.601 limited.
pub fn coefficients(color: ColorInfo) -> [[f32; 4]; 3] {
    let (kr, kb) = match color.matrix {
        YuvMatrix::Bt709 => (0.2126, 0.0722),
        YuvMatrix::Bt2020 => (0.2627, 0.0593),
        YuvMatrix::Bt601 | YuvMatrix::Unspecified => (0.299, 0.114),
    };
    let kg = 1.0 - kr - kb;
    // (scale, offset) pairs mapping normalised code values to Y' in [0,1]
    // and Pb/Pr in [-0.5, 0.5].
    let (ys, yo, cs, co) = match color.range {
        YuvRange::Full => (1.0f32, 0.0f32, 1.0f32, -128.0 / 255.0),
        YuvRange::Limited | YuvRange::Unspecified => {
            (255.0 / 219.0, -16.0 / 219.0, 255.0 / 224.0, -128.0 / 224.0)
        }
    };
    let rv = 2.0 * (1.0 - kr);
    let gu = -2.0 * (1.0 - kb) * kb / kg;
    let gv = -2.0 * (1.0 - kr) * kr / kg;
    let bu = 2.0 * (1.0 - kb);
    [
        [ys, 0.0, rv * cs, yo + rv * co],
        [ys, gu * cs, gv * cs, yo + (gu + gv) * co],
        [ys, bu * cs, 0.0, yo + bu * co],
    ]
}

/// CPU reference for the GPU pass: identical maths (same [`coefficients`],
/// point-sampled top-left co-sited chroma), pixel-exact up to float
/// rounding. Clamps the copy region like the pass's viewport does and
/// leaves pixels outside the frame black.
pub fn nv12_to_bgra(
    frame_width: u32,
    frame_height: u32,
    data: &[u8],
    color: ColorInfo,
    out_width: u32,
    out_height: u32,
    out: &mut Vec<u8>,
) {
    let fw = frame_width as usize;
    let w = out_width.min(frame_width) as usize;
    let h = out_height.min(frame_height) as usize;
    out.clear();
    out.resize(out_width as usize * out_height as usize * 4, 0);
    if data.len() < fw * frame_height as usize * 3 / 2 {
        return;
    }
    let coef = coefficients(color);
    let y_plane = &data[..fw * frame_height as usize];
    let uv_plane = &data[fw * frame_height as usize..];

    for row in 0..h {
        let y_row = &y_plane[row * fw..row * fw + w];
        let uv_row = &uv_plane[(row / 2) * fw..(row / 2) * fw + w];
        let out_row = &mut out[row * out_width as usize * 4..][..w * 4];
        for col in 0..w {
            let yuv1 = [
                y_row[col] as f32 / 255.0,
                uv_row[col & !1] as f32 / 255.0,
                uv_row[col | 1] as f32 / 255.0,
                1.0,
            ];
            let channel = |c: [f32; 4]| {
                let v = c[0] * yuv1[0] + c[1] * yuv1[1] + c[2] * yuv1[2] + c[3];
                (v.clamp(0.0, 1.0) * 255.0).round() as u8
            };
            let px = &mut out_row[col * 4..col * 4 + 4];
            px[0] = channel(coef[2]);
            px[1] = channel(coef[1]);
            px[2] = channel(coef[0]);
            px[3] = 255;
        }
    }
}
