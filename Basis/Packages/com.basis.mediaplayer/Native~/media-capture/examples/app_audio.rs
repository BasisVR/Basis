use std::time::Duration;

use media_capture::{AudioSource, Capture, CaptureConfig, SourceKind, sources};

fn main() {
    let Some(window) = sources().into_iter().find(|s| s.kind == SourceKind::Window) else {
        println!("no window to capture");
        return;
    };
    let config = CaptureConfig {
        source_id: window.id.clone(),
        audio: true,
        audio_source: AudioSource::Application,
        ..CaptureConfig::default()
    };
    let capture = Capture::open(config).expect("open");
    std::thread::sleep(Duration::from_secs(2));
    let mut packets = 0;
    while capture.take_audio().is_some() {
        packets += 1;
    }
    let status = capture.status();
    println!(
        "window '{}' audio active {} error '{}' packets {packets}",
        window.name.chars().take(40).collect::<String>(),
        status.audio,
        status.audio_error
    );
}
