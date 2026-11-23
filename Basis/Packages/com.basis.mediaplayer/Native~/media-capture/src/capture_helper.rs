#[cfg(target_os = "linux")]
mod helper {
    use std::io::{BufWriter, Write};
    use std::net::TcpStream;
    use std::sync::atomic::Ordering;
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};

    use media_capture::relay;
    use media_capture::{Capture, CaptureConfig, CaptureState, CaptureStatus, Shared};

    const STATUS_INTERVAL: Duration = Duration::from_millis(250);

    static LOGS: Mutex<Vec<String>> = Mutex::new(Vec::new());

    fn sink(line: &str) {
        let mut logs = LOGS.lock().unwrap_or_else(|e| e.into_inner());
        if logs.len() < 256 {
            logs.push(line.to_string());
        }
    }

    fn error_status(detail: String) -> CaptureStatus {
        CaptureStatus {
            state: CaptureState::Error,
            error: detail,
            width: 0,
            height: 0,
            encoder: String::new(),
            hardware: false,
            captured: 0,
            encoded: 0,
            dropped: 0,
            bitrate_kbps: 0,
            audio: false,
            audio_error: String::new(),
        }
    }

    fn commands(shared: &Shared, mut stream: TcpStream) {
        let mut body = Vec::new();
        while let Ok(kind) = relay::read_message(&mut stream, &mut body) {
            match kind {
                relay::KEYFRAME => shared.keyframe.store(true, Ordering::Release),
                relay::BITRATE => {
                    if let Some(bytes) = body.get(..4).and_then(|b| <[u8; 4]>::try_from(b).ok()) {
                        shared.bitrate_kbps.store(
                            u32::from_le_bytes(bytes).clamp(100, 100_000),
                            Ordering::Release,
                        );
                    }
                }
                _ => {}
            }
        }
        shared.stop.store(true, Ordering::Release);
        shared.signal();
    }

    fn flush_logs(writer: &mut impl Write) -> std::io::Result<()> {
        let lines = std::mem::take(&mut *LOGS.lock().unwrap_or_else(|e| e.into_inner()));
        for line in lines {
            relay::write_message(writer, relay::LOG, &[line.as_bytes()])?;
        }
        Ok(())
    }

    pub fn run() -> Result<(), String> {
        let mut args = std::env::args().skip(1);
        let port: u16 = args
            .next()
            .and_then(|p| p.parse().ok())
            .ok_or("usage: basis-capture-helper <port> <token>")?;
        let token = args
            .next()
            .and_then(|t| relay::parse_token(&t))
            .ok_or("usage: basis-capture-helper <port> <token>")?;
        media_diag::set_log_sink(sink);
        let stream = TcpStream::connect(("127.0.0.1", port)).map_err(|e| e.to_string())?;
        let _ = stream.set_nodelay(true);
        let mut reader = stream.try_clone().map_err(|e| e.to_string())?;
        let mut writer = BufWriter::new(stream);
        writer
            .write_all(&relay::hello(&token))
            .and_then(|()| writer.flush())
            .map_err(|e| e.to_string())?;
        let mut body = Vec::new();
        let kind = relay::read_message(&mut reader, &mut body).map_err(|e| e.to_string())?;
        if kind != relay::CONFIG {
            return Err("expected a capture config".into());
        }
        let config: CaptureConfig = serde_json::from_slice(&body).map_err(|e| e.to_string())?;
        let mut capture = match Capture::open(config) {
            Ok(capture) => capture,
            Err(e) => {
                let _ = flush_logs(&mut writer);
                let _ = relay::write_status(&mut writer, &error_status(e.0.clone()));
                return Err(e.0);
            }
        };
        let shared: Arc<Shared> = capture.shared();
        let command_shared = Arc::clone(&shared);
        std::thread::Builder::new()
            .name("bm-capture-helper-commands".into())
            .spawn(move || commands(&command_shared, reader))
            .map_err(|e| e.to_string())?;
        let mut last_status = String::new();
        let mut next_status = Instant::now();
        let result = loop {
            shared.wait(Duration::from_millis(50));
            let mut sent = Ok(());
            while sent.is_ok()
                && let Some(frame) = capture.take_video()
            {
                sent = relay::write_video(&mut writer, &frame);
            }
            while sent.is_ok()
                && let Some(packet) = capture.take_audio()
            {
                sent = relay::write_audio(&mut writer, &packet);
            }
            if sent.is_ok() {
                sent = flush_logs(&mut writer);
            }
            if let Err(e) = sent {
                break Err(e.to_string());
            }
            let stopped = shared.stopped();
            if stopped || Instant::now() >= next_status {
                next_status = Instant::now() + STATUS_INTERVAL;
                let status = capture.status();
                let done = matches!(status.state, CaptureState::Ended | CaptureState::Error);
                let json = serde_json::to_string(&status).unwrap_or_default();
                if json != last_status {
                    if let Err(e) = relay::write_status(&mut writer, &status) {
                        break Err(e.to_string());
                    }
                    last_status = json;
                }
                if done || stopped {
                    break Ok(());
                }
            }
        };
        capture.stop();
        result
    }
}

#[cfg(target_os = "linux")]
fn main() {
    if let Err(e) = helper::run() {
        eprintln!("basis-capture-helper: {e}");
        std::process::exit(1);
    }
}

#[cfg(not(target_os = "linux"))]
fn main() {
    eprintln!("basis-capture-helper only runs on Linux");
    std::process::exit(2);
}
