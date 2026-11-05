mod convert;
mod h264_bitstream;
#[cfg(target_os = "linux")]
mod linux;
mod opus;
#[cfg(any(windows, target_os = "linux"))]
mod pack;
mod queue;
mod spa_pod;
#[cfg(windows)]
mod win;

use std::sync::Arc;
use std::sync::Mutex;
use std::sync::atomic::{AtomicBool, AtomicU32, AtomicU64, Ordering};
use std::thread::JoinHandle;

use serde::{Deserialize, Serialize};

pub use opus::OpusEncoder;
pub use queue::Queue;

pub const AUDIO_RATE: u32 = 48_000;
pub const AUDIO_CHANNELS: u32 = 2;
pub const AUDIO_FRAME: usize = 960;
pub const MAX_DIMENSION: u32 = 4096;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum SourceKind {
    Monitor,
    Window,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourceInfo {
    pub id: String,
    pub kind: SourceKind,
    pub name: String,
    pub width: u32,
    pub height: u32,
    pub primary: bool,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum AudioSource {
    #[default]
    Auto,
    System,
    Application,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Default, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum EncoderPreference {
    #[default]
    HardwareWithFallback,
    HardwareOnly,
    SoftwareOnly,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(default)]
pub struct CaptureConfig {
    pub source_id: String,
    pub max_width: u32,
    pub max_height: u32,
    pub fps: u32,
    pub bitrate_kbps: u32,
    pub keyframe_interval_ms: u32,
    pub cursor: bool,
    pub audio: bool,
    pub audio_bitrate_kbps: u32,
    pub encoder: EncoderPreference,
    pub pts_base_us: i64,
    pub audio_source: AudioSource,
}

impl Default for CaptureConfig {
    fn default() -> Self {
        Self {
            source_id: String::new(),
            max_width: 1920,
            max_height: 1080,
            fps: 30,
            bitrate_kbps: 4000,
            keyframe_interval_ms: 10_000,
            cursor: true,
            audio: false,
            audio_bitrate_kbps: 128,
            encoder: EncoderPreference::HardwareWithFallback,
            pts_base_us: 0,
            audio_source: AudioSource::Auto,
        }
    }
}

impl CaptureConfig {
    pub fn sanitized(mut self) -> Self {
        self.max_width = self.max_width.clamp(160, MAX_DIMENSION);
        self.max_height = self.max_height.clamp(90, MAX_DIMENSION);
        self.fps = self.fps.clamp(1, 120);
        self.bitrate_kbps = self.bitrate_kbps.clamp(100, 100_000);
        self.keyframe_interval_ms = self.keyframe_interval_ms.clamp(1000, 60_000);
        self.audio_bitrate_kbps = self.audio_bitrate_kbps.clamp(16, 510);
        self
    }
}

#[derive(Debug, Clone)]
pub struct EncodedVideo {
    pub data: Vec<u8>,
    pub pts_us: i64,
    pub key: bool,
    pub width: u32,
    pub height: u32,
}

#[derive(Debug, Clone)]
pub struct EncodedAudio {
    pub data: Vec<u8>,
    pub pts_us: i64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "lowercase")]
pub enum CaptureState {
    Starting,
    Running,
    Ended,
    Error,
}

#[derive(Debug, Clone, Serialize)]
pub struct CaptureStatus {
    pub state: CaptureState,
    pub error: String,
    pub width: u32,
    pub height: u32,
    pub encoder: String,
    pub hardware: bool,
    pub captured: u64,
    pub encoded: u64,
    pub dropped: u64,
    pub bitrate_kbps: u32,
    pub audio: bool,
    pub audio_error: String,
}

#[derive(Debug)]
pub struct CaptureError(pub String);

impl std::fmt::Display for CaptureError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for CaptureError {}

pub struct Shared {
    pub config: CaptureConfig,
    pub video: Queue<EncodedVideo>,
    pub audio: Queue<EncodedAudio>,
    pub stop: AtomicBool,
    pub keyframe: AtomicBool,
    pub bitrate_kbps: AtomicU32,
    pub captured: AtomicU64,
    pub encoded: AtomicU64,
    pub dropped: AtomicU64,
    pub status: Mutex<CaptureStatus>,
    pub origin: std::time::Instant,
    ready: Mutex<bool>,
    wake: std::sync::Condvar,
}

impl Shared {
    fn new(config: CaptureConfig) -> Self {
        let bitrate = config.bitrate_kbps;
        let audio = config.audio;
        Self {
            config,
            video: Queue::new(240),
            audio: Queue::new(500),
            stop: AtomicBool::new(false),
            keyframe: AtomicBool::new(true),
            bitrate_kbps: AtomicU32::new(bitrate),
            captured: AtomicU64::new(0),
            encoded: AtomicU64::new(0),
            dropped: AtomicU64::new(0),
            status: Mutex::new(CaptureStatus {
                state: CaptureState::Starting,
                error: String::new(),
                width: 0,
                height: 0,
                encoder: String::new(),
                hardware: false,
                captured: 0,
                encoded: 0,
                dropped: 0,
                bitrate_kbps: bitrate,
                audio,
                audio_error: String::new(),
            }),
            origin: std::time::Instant::now(),
            ready: Mutex::new(false),
            wake: std::sync::Condvar::new(),
        }
    }

    pub fn stopped(&self) -> bool {
        self.stop.load(Ordering::Acquire)
    }

    pub fn now_us(&self) -> i64 {
        self.config.pts_base_us.max(0) + self.origin.elapsed().as_micros() as i64
    }

    fn signal(&self) {
        *self.ready.lock().unwrap_or_else(|e| e.into_inner()) = true;
        self.wake.notify_all();
    }

    pub fn wait(&self, timeout: std::time::Duration) -> bool {
        let mut ready = self.ready.lock().unwrap_or_else(|e| e.into_inner());
        if !*ready && self.video.is_empty() && self.audio.is_empty() && !self.stopped() {
            ready = self
                .wake
                .wait_timeout(ready, timeout)
                .unwrap_or_else(|e| e.into_inner())
                .0;
        }
        *ready = false;
        !self.video.is_empty() || !self.audio.is_empty()
    }

    pub fn push_audio(&self, packet: EncodedAudio) {
        if !self.audio.push(packet) {
            self.audio.clear();
        }
        self.signal();
    }

    pub fn update(&self, f: impl FnOnce(&mut CaptureStatus)) {
        let mut status = self.status.lock().unwrap_or_else(|e| e.into_inner());
        f(&mut status);
    }

    pub fn fail(&self, detail: impl Into<String>) {
        let detail = detail.into();
        media_diag::diag_warn!("capture: {detail}");
        self.update(|s| {
            s.state = CaptureState::Error;
            s.error = detail;
        });
        self.stop.store(true, Ordering::Release);
    }

    pub fn push_video(&self, frame: EncodedVideo) {
        if !self.video.push(frame) {
            self.dropped.fetch_add(1, Ordering::Relaxed);
            self.video.clear();
            self.keyframe.store(true, Ordering::Release);
        }
        self.signal();
    }
}

pub struct Capture {
    shared: Arc<Shared>,
    threads: Vec<JoinHandle<()>>,
}

impl Capture {
    pub fn open(config: CaptureConfig) -> Result<Self, CaptureError> {
        let shared = Arc::new(Shared::new(config.sanitized()));
        let threads = platform_open(&shared)?;
        Ok(Self { shared, threads })
    }

    pub fn shared(&self) -> Arc<Shared> {
        Arc::clone(&self.shared)
    }

    pub fn take_video(&self) -> Option<EncodedVideo> {
        self.shared.video.pop()
    }

    pub fn take_audio(&self) -> Option<EncodedAudio> {
        self.shared.audio.pop()
    }

    pub fn request_keyframe(&self) {
        self.shared.keyframe.store(true, Ordering::Release);
    }

    pub fn set_bitrate(&self, kbps: u32) {
        self.shared
            .bitrate_kbps
            .store(kbps.clamp(100, 100_000), Ordering::Release);
    }

    pub fn status(&self) -> CaptureStatus {
        let mut status = self
            .shared
            .status
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone();
        status.captured = self.shared.captured.load(Ordering::Relaxed);
        status.encoded = self.shared.encoded.load(Ordering::Relaxed);
        status.dropped = self.shared.dropped.load(Ordering::Relaxed);
        status.bitrate_kbps = self.shared.bitrate_kbps.load(Ordering::Relaxed);
        status
    }

    pub fn stop(&mut self) {
        self.shared.stop.store(true, Ordering::Release);
        self.shared.signal();
        for thread in self.threads.drain(..) {
            let _ = thread.join();
        }
    }
}

impl Drop for Capture {
    fn drop(&mut self) {
        self.stop();
    }
}

pub fn sources() -> Vec<SourceInfo> {
    #[cfg(windows)]
    {
        win::sources()
    }
    #[cfg(target_os = "linux")]
    {
        linux::sources()
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        Vec::new()
    }
}

pub fn supported() -> bool {
    #[cfg(windows)]
    {
        win::supported()
    }
    #[cfg(target_os = "linux")]
    {
        linux::supported()
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        false
    }
}

fn platform_open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    #[cfg(windows)]
    {
        win::open(shared)
    }
    #[cfg(target_os = "linux")]
    {
        linux::open(shared)
    }
    #[cfg(not(any(windows, target_os = "linux")))]
    {
        let _ = shared;
        Err(CaptureError(
            "screen capture is not available on this platform".into(),
        ))
    }
}

pub fn fit_size(source_w: u32, source_h: u32, max_w: u32, max_h: u32) -> (u32, u32) {
    if source_w == 0 || source_h == 0 {
        return (max_w & !1, max_h & !1);
    }
    let scale = (f64::from(max_w) / f64::from(source_w))
        .min(f64::from(max_h) / f64::from(source_h))
        .min(1.0);
    let w = ((f64::from(source_w) * scale).round() as u32).max(2) & !1;
    let h = ((f64::from(source_h) * scale).round() as u32).max(2) & !1;
    (w, h)
}

pub fn letterbox(source_w: u32, source_h: u32, out_w: u32, out_h: u32) -> (u32, u32, u32, u32) {
    if source_w == 0 || source_h == 0 {
        return (0, 0, out_w, out_h);
    }
    let scale =
        (f64::from(out_w) / f64::from(source_w)).min(f64::from(out_h) / f64::from(source_h));
    let w = ((f64::from(source_w) * scale).round() as u32).clamp(2, out_w);
    let h = ((f64::from(source_h) * scale).round() as u32).clamp(2, out_h);
    let x = (out_w - w) / 2;
    let y = (out_h - h) / 2;
    (x, y, x + w, y + h)
}

pub fn has_parameter_sets(annexb: &[u8]) -> bool {
    nal_types(annexb).any(|t| t == 7)
}

pub fn nal_types(annexb: &[u8]) -> impl Iterator<Item = u8> + '_ {
    let mut i = 0usize;
    std::iter::from_fn(move || {
        while i + 3 < annexb.len() {
            if annexb[i] == 0 && annexb[i + 1] == 0 && annexb[i + 2] == 1 {
                let t = annexb[i + 3] & 0x1f;
                i += 3;
                return Some(t);
            }
            i += 1;
        }
        None
    })
}

pub fn parameter_sets(annexb: &[u8]) -> Vec<u8> {
    let mut out = Vec::new();
    let mut starts = Vec::new();
    let mut i = 0usize;
    while i + 3 <= annexb.len() {
        if annexb[i] == 0 && annexb[i + 1] == 0 && annexb[i + 2] == 1 {
            starts.push(i);
            i += 3;
        } else {
            i += 1;
        }
    }
    for (n, &start) in starts.iter().enumerate() {
        let end = starts.get(n + 1).copied().unwrap_or(annexb.len());
        let mut end = end;
        while end > start + 3 && annexb[end - 1] == 0 {
            end -= 1;
        }
        if let Some(&header) = annexb.get(start + 3)
            && matches!(header & 0x1f, 7 | 8)
        {
            out.extend_from_slice(&[0, 0, 0, 1]);
            out.extend_from_slice(&annexb[start + 3..end]);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fit_keeps_aspect_and_even_sizes() {
        assert_eq!(fit_size(3840, 2160, 1920, 1080), (1920, 1080));
        assert_eq!(fit_size(1280, 720, 1920, 1080), (1280, 720));
        assert_eq!(fit_size(1001, 1001, 1920, 1080), (1000, 1000));
        assert_eq!(fit_size(2001, 2001, 1920, 1080), (1080, 1080));
        assert_eq!(fit_size(3440, 1440, 1920, 1080), (1920, 804));
    }

    #[test]
    fn letterbox_centres() {
        assert_eq!(letterbox(1920, 1080, 1920, 1080), (0, 0, 1920, 1080));
        assert_eq!(letterbox(1080, 1080, 1920, 1080), (420, 0, 1500, 1080));
    }

    #[test]
    fn parameter_sets_are_found_and_extracted() {
        let au = [
            0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9, 9,
        ];
        assert!(has_parameter_sets(&au));
        assert_eq!(
            parameter_sets(&au),
            vec![0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3]
        );
        assert!(!has_parameter_sets(&[0, 0, 1, 0x41, 5]));
    }

    #[test]
    fn config_is_clamped() {
        let config = CaptureConfig {
            fps: 0,
            bitrate_kbps: 1,
            max_width: 99_999,
            ..CaptureConfig::default()
        }
        .sanitized();
        assert_eq!(config.fps, 1);
        assert_eq!(config.bitrate_kbps, 100);
        assert_eq!(config.max_width, MAX_DIMENSION);
    }
}
