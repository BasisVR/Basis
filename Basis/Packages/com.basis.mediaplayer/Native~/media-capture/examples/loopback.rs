use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

use media_capture::{Capture, CaptureConfig};
use media_engine::{OpenRequest, PushOutcome, Session};

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let seconds: u64 = args.get(1).and_then(|s| s.parse().ok()).unwrap_or(5);
    let software = args.iter().any(|a| a == "--software");
    let audio = args.iter().any(|a| a == "--audio");
    for source in media_capture::sources().iter().take(8) {
        println!(
            "source {:?} {} {}x{} {}",
            source.kind, source.name, source.width, source.height, source.id
        );
    }
    let config = CaptureConfig {
        fps: 30,
        bitrate_kbps: 6000,
        audio,
        encoder: if software {
            media_capture::EncoderPreference::SoftwareOnly
        } else {
            media_capture::EncoderPreference::HardwareWithFallback
        },
        ..CaptureConfig::default()
    };
    let capture = Capture::open(config).expect("capture open");
    let request = OpenRequest::new("basis-push://loopback");
    let (session, feed) = Session::open_push(request);
    session.play();
    let start = Instant::now();
    let (mut video, mut audio_packets, mut keys, mut bytes, mut need_key) =
        (0u64, 0u64, 0u64, 0u64, 0u64);
    while start.elapsed() < Duration::from_secs(seconds) {
        while let Some(frame) = capture.take_video() {
            video += 1;
            keys += u64::from(frame.key);
            bytes += frame.data.len() as u64;
            match feed.push_video(
                &frame.data,
                frame.pts_us,
                frame.key,
                frame.width,
                frame.height,
            ) {
                PushOutcome::Accepted => {}
                PushOutcome::NeedKeyframe => {
                    need_key += 1;
                    capture.request_keyframe();
                }
                other => println!("push video: {other:?}"),
            }
        }
        while let Some(packet) = capture.take_audio() {
            audio_packets += 1;
            let _ = feed.push_audio(&packet.data, packet.pts_us, 48_000, 2);
        }
        std::thread::sleep(Duration::from_millis(5));
    }
    let status = capture.status();
    let shared = session.shared();
    println!(
        "status {}",
        serde_json::to_string(&status).unwrap_or_default()
    );
    println!(
        "encoded {video} frames ({keys} key, {need_key} waits), {:.0} kbps, audio packets {audio_packets}",
        bytes as f64 * 8.0 / 1000.0 / seconds as f64
    );
    println!(
        "engine state {} decoded {} size {}x{} audio {} Hz error {}",
        shared.state.load(Ordering::Relaxed),
        shared.frames_decoded.load(Ordering::Relaxed),
        shared.width.load(Ordering::Relaxed),
        shared.height.load(Ordering::Relaxed),
        shared.audio_rate.load(Ordering::Relaxed),
        shared.last_error.load(Ordering::Relaxed)
    );
    for record in media_diag::drain_log(64) {
        println!("log {:?}", record);
    }
    drop(capture);
}
