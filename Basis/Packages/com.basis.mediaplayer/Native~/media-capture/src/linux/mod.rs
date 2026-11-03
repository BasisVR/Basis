mod pipewire;
mod portal;
mod vaapi;

use std::sync::atomic::Ordering;
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use crate::convert::{Order, Source, to_nv12};
use crate::pack::Packer;
use crate::{AudioSource, CaptureError, CaptureState, Shared, SourceInfo, SourceKind, fit_size};
use pipewire::{AudioStream, PixelFormat, VideoFrame, VideoStream};
use vaapi::VaapiEncoder;

pub(crate) const PORTAL_SOURCE: &str = "portal";
const REPEAT: Duration = Duration::from_millis(500);

pub fn sources() -> Vec<SourceInfo> {
    if !portal::available() {
        return Vec::new();
    }
    vec![SourceInfo {
        id: PORTAL_SOURCE.into(),
        kind: SourceKind::Monitor,
        name: "Screen or window".into(),
        width: 0,
        height: 0,
        primary: true,
    }]
}

pub fn supported() -> bool {
    portal::available()
}

pub fn open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    if !portal::available() {
        return Err(CaptureError(
            "screen sharing needs xdg-desktop-portal with ScreenCast support".into(),
        ));
    }
    let mut threads = Vec::new();
    let video_shared = Arc::clone(shared);
    threads.push(
        std::thread::Builder::new()
            .name("bm-capture-video".into())
            .spawn(move || run_video(video_shared))
            .map_err(|e| CaptureError(format!("spawn capture thread: {e}")))?,
    );
    if shared.config.audio {
        let audio_shared = Arc::clone(shared);
        threads.push(
            std::thread::Builder::new()
                .name("bm-capture-audio".into())
                .spawn(move || run_audio(audio_shared))
                .map_err(|e| CaptureError(format!("spawn audio thread: {e}")))?,
        );
    }
    Ok(threads)
}

#[derive(Default)]
struct Latest {
    nv12: Vec<u8>,
    size: (u32, u32),
    fresh: bool,
}

fn run_video(shared: Arc<Shared>) {
    match capture_video(&shared) {
        Ok(()) => shared.update(|s| {
            if s.state != CaptureState::Error {
                s.state = CaptureState::Ended;
            }
        }),
        Err(e) => shared.fail(e.0),
    }
}

fn capture_video(shared: &Arc<Shared>) -> Result<(), CaptureError> {
    let config = shared.config.clone();
    let stop = {
        let shared = Arc::clone(shared);
        move || shared.stopped()
    };
    let cast = portal::request_screencast(config.cursor, &stop)?;
    if shared.stopped() {
        return Ok(());
    }
    let slot = Arc::new((Mutex::new(Latest::default()), Condvar::new()));
    let interval = Duration::from_secs_f64(1.0 / f64::from(config.fps.max(1)));
    let stream = {
        let slot = Arc::clone(&slot);
        let shared = Arc::clone(shared);
        let mut last_convert: Option<Instant> = None;
        let mut out_size = (0u32, 0u32);
        VideoStream::open(
            &cast,
            config.fps,
            Box::new(move |frame: VideoFrame<'_>| {
                shared.captured.fetch_add(1, Ordering::Relaxed);
                if last_convert.is_some_and(|t| t.elapsed() < interval)
                    && !shared.keyframe.load(Ordering::Acquire)
                {
                    return;
                }
                if out_size == (0, 0) {
                    out_size = fit_size(
                        frame.width,
                        frame.height,
                        config.max_width,
                        config.max_height,
                    );
                }
                let order = match frame.format {
                    PixelFormat::Bgrx | PixelFormat::Bgra => Order::Bgr,
                    PixelFormat::Rgbx | PixelFormat::Rgba => Order::Rgb,
                };
                let source = Source {
                    width: frame.width,
                    height: frame.height,
                    stride: frame.stride,
                    order,
                    data: frame.data,
                };
                let (lock, wake) = &*slot;
                let mut latest = lock.lock().unwrap_or_else(|e| e.into_inner());
                if to_nv12(&source, out_size.0, out_size.1, &mut latest.nv12) {
                    latest.size = out_size;
                    latest.fresh = true;
                    last_convert = Some(Instant::now());
                    wake.notify_one();
                }
            }),
        )?
    };
    let mut encoder: Option<VaapiEncoder> = None;
    let mut bitrate = shared.bitrate_kbps.load(Ordering::Acquire);
    let mut nv12 = Vec::new();
    let mut size = (0u32, 0u32);
    let mut last_encode: Option<Instant> = None;
    let mut last_pts = -1i64;
    while !shared.stopped() {
        let fresh = {
            let (lock, wake) = &*slot;
            let mut latest = lock.lock().unwrap_or_else(|e| e.into_inner());
            if !latest.fresh {
                latest = wake
                    .wait_timeout(latest, Duration::from_millis(50))
                    .unwrap_or_else(|e| e.into_inner())
                    .0;
            }
            if latest.fresh {
                std::mem::swap(&mut nv12, &mut latest.nv12);
                size = latest.size;
                latest.fresh = false;
                true
            } else {
                false
            }
        };
        if size == (0, 0) {
            continue;
        }
        let repeat_due = last_encode.is_some_and(|t| t.elapsed() >= REPEAT);
        if !fresh && !repeat_due {
            continue;
        }
        if encoder.is_none() {
            let created = VaapiEncoder::open(
                size.0,
                size.1,
                config.fps,
                bitrate,
                (config.fps * config.keyframe_interval_ms / 1000).max(1),
            )?;
            let name = created.name();
            shared.update(|s| {
                s.state = CaptureState::Running;
                s.width = size.0;
                s.height = size.1;
                s.encoder = name;
                s.hardware = true;
            });
            encoder = Some(created);
        }
        let Some(active) = encoder.as_mut() else {
            continue;
        };
        let requested = shared.bitrate_kbps.load(Ordering::Acquire);
        if requested != bitrate {
            active.set_bitrate(requested);
            bitrate = requested;
        }
        let mut pts = shared.now_us();
        if pts <= last_pts {
            pts = last_pts + 1;
        }
        let key = shared.keyframe.swap(false, Ordering::AcqRel);
        match active.encode(&nv12, pts, key) {
            Ok(Some(frame)) => {
                shared.encoded.fetch_add(1, Ordering::Relaxed);
                shared.push_video(frame);
            }
            Ok(None) => {}
            Err(e) => {
                drop(stream);
                return Err(e);
            }
        }
        last_encode = Some(Instant::now());
        last_pts = pts;
    }
    drop(stream);
    drop(cast);
    Ok(())
}

fn run_audio(shared: Arc<Shared>) {
    if shared.config.audio_source != AudioSource::System {
        shared.update(|s| {
            s.audio = false;
            s.audio_error = "on Linux only Whole Computer audio can be shared, and it includes Basis's own sound".into();
        });
        return;
    }
    if let Err(e) = capture_audio(&shared) {
        media_diag::diag_warn!("capture audio: {}", e.0);
        shared.update(|s| {
            s.audio = false;
            s.audio_error = e.0.clone();
        });
    }
}

fn capture_audio(shared: &Arc<Shared>) -> Result<(), CaptureError> {
    let packer = Arc::new(Mutex::new(Packer::new(shared)?));
    let stream = {
        let packer = Arc::clone(&packer);
        let shared = Arc::clone(shared);
        AudioStream::open(Box::new(move |samples: &[f32]| {
            let mut packer = packer.lock().unwrap_or_else(|e| e.into_inner());
            if let Err(e) = packer.push(&shared, samples) {
                media_diag::diag_warn!("capture audio: {}", e.0);
            }
        }))?
    };
    shared.update(|s| s.audio = true);
    while !shared.stopped() {
        std::thread::sleep(Duration::from_millis(20));
        packer
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .fill_silence(shared)?;
    }
    drop(stream);
    Ok(())
}
