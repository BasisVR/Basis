#[cfg(windows)]
fn main() {
    use std::io::Write;
    use std::time::{Duration, Instant};

    use media_capture::{AudioSource, Capture, CaptureConfig, CaptureState};

    let mut args = std::env::args().skip(1);
    let out = args.next().unwrap_or_else(|| "wine_probe".into());
    let seconds: u64 = args.next().and_then(|s| s.parse().ok()).unwrap_or(10);
    let mut log = std::fs::File::create(format!("{out}.log")).expect("create log");
    let mut say = |line: String| {
        println!("{line}");
        let _ = writeln!(log, "{line}");
    };
    say(format!("supported {}", media_capture::supported()));
    say(format!(
        "sources {}",
        serde_json::to_string(&media_capture::sources()).unwrap_or_default()
    ));
    let mut capture = match Capture::open(CaptureConfig {
        source_id: "portal".into(),
        max_width: 1280,
        max_height: 720,
        fps: 30,
        bitrate_kbps: 3000,
        audio: true,
        audio_source: AudioSource::System,
        ..CaptureConfig::default()
    }) {
        Ok(capture) => capture,
        Err(e) => {
            say(format!("FAIL open: {e}"));
            std::process::exit(1);
        }
    };
    let mut video = std::fs::File::create(format!("{out}.h264")).expect("create h264");
    let started = Instant::now();
    let mut next_report = started;
    let (mut frames, mut keys, mut packets, mut bytes) = (0u64, 0u64, 0u64, 0u64);
    let mut keyframe_asked = false;
    let mut bitrate_asked = false;
    let mut window_bytes = [0u64; 2];
    let mut state = CaptureState::Starting;
    while started.elapsed() < Duration::from_secs(seconds) {
        capture.shared().wait(Duration::from_millis(50));
        while let Some(frame) = capture.take_video() {
            frames += 1;
            keys += u64::from(frame.key);
            bytes += frame.data.len() as u64;
            let half = usize::from(started.elapsed() >= Duration::from_secs(seconds / 2));
            window_bytes[half] += frame.data.len() as u64;
            video.write_all(&frame.data).expect("write h264");
        }
        while capture.take_audio().is_some() {
            packets += 1;
        }
        let elapsed = started.elapsed();
        if !keyframe_asked && frames > 0 && elapsed >= Duration::from_secs(seconds / 3) {
            keyframe_asked = true;
            capture.request_keyframe();
            say("asked for a keyframe".into());
        }
        if !bitrate_asked && elapsed >= Duration::from_secs(seconds / 2) {
            bitrate_asked = true;
            capture.set_bitrate(600);
            say("set bitrate 600 kb/s".into());
        }
        if Instant::now() >= next_report {
            next_report += Duration::from_secs(1);
            let status = capture.status();
            state = status.state;
            say(format!(
                "t={:.1}s frames={frames} keys={keys} audio={packets} bytes={bytes} status={}",
                elapsed.as_secs_f32(),
                serde_json::to_string(&status).unwrap_or_default()
            ));
            if matches!(state, CaptureState::Error | CaptureState::Ended) {
                break;
            }
        }
    }
    let status = capture.status();
    capture.stop();
    say(format!(
        "RESULT frames={frames} keys={keys} audio={packets} first_half_bytes={} second_half_bytes={} encoder={} hardware={} state={:?} error={} audio_error={}",
        window_bytes[0],
        window_bytes[1],
        status.encoder,
        status.hardware,
        state,
        status.error,
        status.audio_error
    ));
    let ok = frames >= 30 && keys >= 2 && state != CaptureState::Error;
    say(if ok { "PASS".into() } else { "FAIL".into() });
    std::process::exit(if ok { 0 } else { 1 });
}

#[cfg(not(windows))]
fn main() {
    eprintln!("wine_probe is a Windows program meant to run under Wine or Proton");
}
