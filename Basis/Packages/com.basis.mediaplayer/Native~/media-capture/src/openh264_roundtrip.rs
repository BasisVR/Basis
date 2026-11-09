//! The Linux share path end to end on the development machine: a BGRx desktop
//! frame through the PipeWire-side NV12 conversion, the OpenH264 fallback
//! encoder and the FFmpeg decoder a Linux viewer uses. Skips unless Cisco's
//! `openh264-2.6.0-win64.dll` and a shared FFmpeg build are on PATH.

use crate::convert::{Order, Source, to_nv12};
use crate::openh264::OpenH264Encoder;
use decode_ffmpeg::{FfCodec, FfVideoDecoder, has_decoder};
use media_decode::{SubmitOutcome, VideoDecoder};

const SRC_W: u32 = 1920;
const SRC_H: u32 = 1080;
const OUT_W: u32 = 1280;
const OUT_H: u32 = 720;

fn desktop(frame: u32) -> Vec<u8> {
    let mut px = vec![0u8; (SRC_W * SRC_H * 4) as usize];
    for y in 0..SRC_H {
        for x in 0..SRC_W {
            let i = ((y * SRC_W + x) * 4) as usize;
            let window = (x / 240 + y / 135 + frame / 10).is_multiple_of(3);
            let shade = ((x + frame * 8) % 256) as u8;
            px[i] = if window { 230 } else { shade };
            px[i + 1] = if window { 230 } else { (y % 256) as u8 };
            px[i + 2] = if window { 230 } else { 255 - shade };
            px[i + 3] = 0;
        }
    }
    px
}

fn psnr(a: &[u8], b: &[u8]) -> f64 {
    let mse = a
        .iter()
        .zip(b)
        .map(|(&x, &y)| (f64::from(x) - f64::from(y)).powi(2))
        .sum::<f64>()
        / a.len() as f64;
    if mse == 0.0 {
        99.0
    } else {
        10.0 * (255.0 * 255.0 / mse).log10()
    }
}

#[test]
fn openh264_share_decodes_on_the_linux_viewer_path() {
    let mut encoder = match OpenH264Encoder::open(OUT_W, OUT_H, 30, 6000, 300) {
        Ok(e) => e,
        Err(e) => {
            eprintln!("skipped: {}", e.0);
            return;
        }
    };
    if !has_decoder(FfCodec::H264) {
        eprintln!("skipped: no FFmpeg H.264 decoder");
        return;
    }
    let mut decoder = FfVideoDecoder::new(FfCodec::H264, true).expect("decoder");
    let mut sent = Vec::new();
    let mut keys = Vec::new();
    let mut decoded = Vec::new();
    let mut nv12 = Vec::new();
    for frame in 0..60u32 {
        let px = desktop(frame);
        let source = Source {
            width: SRC_W,
            height: SRC_H,
            stride: SRC_W * 4,
            order: Order::Bgr,
            data: &px,
        };
        assert!(to_nv12(&source, OUT_W, OUT_H, &mut nv12));
        if frame == 40 {
            encoder.set_bitrate(3000);
        }
        let pts = i64::from(frame) * 33_333;
        let Some(out) = encoder.encode(&nv12, pts, frame == 30).expect("encode") else {
            continue;
        };
        assert_eq!((out.width, out.height), (OUT_W, OUT_H));
        if out.key {
            keys.push(frame);
        }
        sent.push((pts, nv12.clone()));
        assert_eq!(
            decoder.submit(&out.data, out.pts_us).expect("submit"),
            SubmitOutcome::Accepted
        );
        while let Some(f) = decoder.try_output().expect("decode") {
            decoded.push(f);
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(f) = decoder.try_output().expect("decode") {
        decoded.push(f);
    }
    eprintln!(
        "{} encoded, {} decoded, keys {keys:?}",
        sent.len(),
        decoded.len()
    );
    assert_eq!(keys.first(), Some(&0), "first frame must be IDR");
    assert!(keys.contains(&30), "forced keyframe missing: {keys:?}");
    assert!(
        sent.len() >= 55,
        "encoder dropped {} frames",
        60 - sent.len()
    );
    assert_eq!(decoded.len(), sent.len());
    let luma = (OUT_W * OUT_H) as usize;
    let mut worst = f64::MAX;
    for (frame, (pts, original)) in decoded.iter().zip(&sent) {
        let f = frame.as_nv12().expect("cpu frame");
        assert_eq!((f.width, f.height, f.pts_us), (OUT_W, OUT_H, *pts));
        worst = worst.min(psnr(&f.data[..luma], &original[..luma]));
    }
    eprintln!("worst luma PSNR {worst:.1} dB");
    assert!(worst > 30.0, "worst luma PSNR {worst:.1} dB");
}
