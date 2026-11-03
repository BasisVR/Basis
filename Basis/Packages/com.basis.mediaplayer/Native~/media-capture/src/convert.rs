#![cfg_attr(not(target_os = "linux"), allow(dead_code))]

use crate::letterbox;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Order {
    Bgr,
    Rgb,
}

pub(crate) struct Source<'a> {
    pub width: u32,
    pub height: u32,
    pub stride: u32,
    pub order: Order,
    pub data: &'a [u8],
}

const BLACK_Y: u8 = 16;
const NEUTRAL_C: u8 = 128;

fn luma(r: i32, g: i32, b: i32) -> u8 {
    ((47 * r + 157 * g + 16 * b + 128) / 256 + 16).clamp(16, 235) as u8
}

fn chroma(r: i32, g: i32, b: i32) -> (u8, u8) {
    let u = (-26 * r - 87 * g + 112 * b + 128) / 256 + 128;
    let v = (112 * r - 102 * g - 10 * b + 128) / 256 + 128;
    (u.clamp(16, 240) as u8, v.clamp(16, 240) as u8)
}

fn sample(src: &Source<'_>, x: u32, y: u32) -> (i32, i32, i32) {
    let i = y as usize * src.stride as usize + x as usize * 4;
    let p = &src.data[i..i + 4];
    match src.order {
        Order::Bgr => (i32::from(p[2]), i32::from(p[1]), i32::from(p[0])),
        Order::Rgb => (i32::from(p[0]), i32::from(p[1]), i32::from(p[2])),
    }
}

fn valid(src: &Source<'_>) -> bool {
    src.width > 0
        && src.height > 0
        && src.stride >= src.width * 4
        && src.data.len()
            >= (src.height as usize - 1) * src.stride as usize + src.width as usize * 4
}

pub(crate) fn to_nv12(src: &Source<'_>, out_w: u32, out_h: u32, out: &mut Vec<u8>) -> bool {
    if !valid(src) || out_w < 2 || out_h < 2 || !out_w.is_multiple_of(2) || !out_h.is_multiple_of(2)
    {
        return false;
    }
    let (w, h) = (out_w as usize, out_h as usize);
    out.clear();
    out.resize(w * h * 3 / 2, 0);
    let (x0, y0, x1, y1) = letterbox(src.width, src.height, out_w, out_h);
    let (x0, y0, x1, y1) = (
        x0 as usize & !1,
        y0 as usize & !1,
        (x1 as usize + 1) & !1,
        (y1 as usize + 1) & !1,
    );
    let (dw, dh) = ((x1 - x0).max(2), (y1 - y0).max(2));
    let map_x: Vec<u32> = (0..dw)
        .map(|x| {
            (((x as u64 * 2 + 1) * u64::from(src.width)) / (dw as u64 * 2))
                .min(u64::from(src.width - 1)) as u32
        })
        .collect();
    let map_y: Vec<u32> = (0..dh)
        .map(|y| {
            (((y as u64 * 2 + 1) * u64::from(src.height)) / (dh as u64 * 2))
                .min(u64::from(src.height - 1)) as u32
        })
        .collect();
    let (y_plane, uv_plane) = out.split_at_mut(w * h);
    let pair_rows = h / 2;
    let bands = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1)
        .clamp(1, 8)
        .min(pair_rows);
    let rows_per_band = pair_rows.div_ceil(bands.max(1));
    std::thread::scope(|scope| {
        for (band, (ys, uvs)) in y_plane
            .chunks_mut(rows_per_band * 2 * w)
            .zip(uv_plane.chunks_mut(rows_per_band * w))
            .enumerate()
        {
            let (map_x, map_y) = (&map_x, &map_y);
            scope.spawn(move || {
                let first_pair = band * rows_per_band;
                for (pair, (yrows, uvrow)) in
                    ys.chunks_mut(2 * w).zip(uvs.chunks_mut(w)).enumerate()
                {
                    let oy = (first_pair + pair) * 2;
                    for (dy, yrow) in yrows.chunks_mut(w).enumerate() {
                        let row = oy + dy;
                        yrow.fill(BLACK_Y);
                        if row < y0 || row >= y0 + dh {
                            continue;
                        }
                        let sy = map_y[row - y0];
                        for (dx, &sx) in map_x.iter().enumerate() {
                            let (r, g, b) = sample(src, sx, sy);
                            yrow[x0 + dx] = luma(r, g, b);
                        }
                    }
                    uvrow.fill(NEUTRAL_C);
                    if oy < y0 || oy >= y0 + dh {
                        continue;
                    }
                    let sy0 = map_y[oy - y0];
                    let sy1 = map_y[(oy + 1 - y0).min(dh - 1)];
                    for cx in (0..dw).step_by(2) {
                        let (sx0, sx1) = (map_x[cx], map_x[(cx + 1).min(dw - 1)]);
                        let mut acc = (0, 0, 0);
                        for (sx, sy) in [(sx0, sy0), (sx1, sy0), (sx0, sy1), (sx1, sy1)] {
                            let (r, g, b) = sample(src, sx, sy);
                            acc = (acc.0 + r, acc.1 + g, acc.2 + b);
                        }
                        let (u, v) = chroma(acc.0 / 4, acc.1 / 4, acc.2 / 4);
                        uvrow[x0 + cx] = u;
                        uvrow[x0 + cx + 1] = v;
                    }
                }
            });
        }
    });
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    fn solid(w: u32, h: u32, rgb: (u8, u8, u8), order: Order, pad: u32) -> Vec<u8> {
        let stride = (w * 4 + pad) as usize;
        let mut data = vec![0u8; stride * h as usize];
        for y in 0..h as usize {
            for x in 0..w as usize {
                let p = &mut data[y * stride + x * 4..y * stride + x * 4 + 4];
                match order {
                    Order::Bgr => p.copy_from_slice(&[rgb.2, rgb.1, rgb.0, 255]),
                    Order::Rgb => p.copy_from_slice(&[rgb.0, rgb.1, rgb.2, 255]),
                }
            }
        }
        data
    }

    #[test]
    fn white_and_black_map_to_studio_range() {
        let mut out = Vec::new();
        let data = solid(64, 36, (255, 255, 255), Order::Bgr, 0);
        assert!(to_nv12(
            &Source {
                width: 64,
                height: 36,
                stride: 256,
                order: Order::Bgr,
                data: &data
            },
            64,
            36,
            &mut out
        ));
        assert_eq!(out[0], 235);
        assert_eq!(out[64 * 36], 128);
        let data = solid(64, 36, (0, 0, 0), Order::Rgb, 0);
        assert!(to_nv12(
            &Source {
                width: 64,
                height: 36,
                stride: 256,
                order: Order::Rgb,
                data: &data
            },
            64,
            36,
            &mut out
        ));
        assert_eq!(out[0], 16);
    }

    #[test]
    fn red_has_high_v_and_order_is_honoured() {
        let mut bgr = Vec::new();
        let mut rgb = Vec::new();
        let a = solid(32, 32, (255, 0, 0), Order::Bgr, 8);
        let b = solid(32, 32, (255, 0, 0), Order::Rgb, 8);
        assert!(to_nv12(
            &Source {
                width: 32,
                height: 32,
                stride: 136,
                order: Order::Bgr,
                data: &a
            },
            32,
            32,
            &mut bgr
        ));
        assert!(to_nv12(
            &Source {
                width: 32,
                height: 32,
                stride: 136,
                order: Order::Rgb,
                data: &b
            },
            32,
            32,
            &mut rgb
        ));
        assert_eq!(bgr, rgb);
        let v = bgr[32 * 32 + 1];
        assert!(v > 230, "v was {v}");
    }

    #[test]
    fn letterboxes_a_square_source_into_16_by_9() {
        let mut out = Vec::new();
        let data = solid(100, 100, (255, 255, 255), Order::Bgr, 0);
        assert!(to_nv12(
            &Source {
                width: 100,
                height: 100,
                stride: 400,
                order: Order::Bgr,
                data: &data
            },
            160,
            90,
            &mut out
        ));
        assert_eq!(out[0], 16, "left bar is black");
        assert_eq!(out[45 * 160 + 80], 235, "centre is the picture");
    }

    #[test]
    fn rejects_short_buffers_and_odd_sizes() {
        let mut out = Vec::new();
        let data = vec![0u8; 10];
        assert!(!to_nv12(
            &Source {
                width: 64,
                height: 36,
                stride: 256,
                order: Order::Bgr,
                data: &data
            },
            64,
            36,
            &mut out
        ));
        let data = solid(64, 36, (0, 0, 0), Order::Bgr, 0);
        assert!(!to_nv12(
            &Source {
                width: 64,
                height: 36,
                stride: 256,
                order: Order::Bgr,
                data: &data
            },
            63,
            36,
            &mut out
        ));
    }
}
