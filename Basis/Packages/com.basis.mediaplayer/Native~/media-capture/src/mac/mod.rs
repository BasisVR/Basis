mod encoder;
mod ffi;
mod sck;

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::Ordering;
use std::sync::{Arc, Condvar, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use core_foundation_sys::base::{CFTypeRef, kCFAllocatorDefault};
use core_foundation_sys::dictionary::{
    CFDictionaryCreate, CFDictionaryRef, kCFTypeDictionaryKeyCallBacks,
    kCFTypeDictionaryValueCallBacks,
};
use core_foundation_sys::number::{CFNumberCreate, kCFNumberSInt32Type};
use objc2::rc::autoreleasepool;

use crate::pack::Packer;
use crate::{AudioSource, CaptureError, CaptureState, Shared, SourceInfo, fit_size};
use encoder::VtEncoder;
use sck::{Content, Pixel, Settings, Stream, Target};

const REPEAT: Duration = Duration::from_millis(500);
const FIRST_FRAME: Duration = Duration::from_secs(15);

/// # Safety
/// Every key and value is a live CoreFoundation object.
unsafe fn dictionary(pairs: &[(CFTypeRef, CFTypeRef)]) -> CFDictionaryRef {
    let keys: Vec<CFTypeRef> = pairs.iter().map(|p| p.0).collect();
    let values: Vec<CFTypeRef> = pairs.iter().map(|p| p.1).collect();
    // SAFETY: caller contract; the dictionary retains what it holds.
    unsafe {
        CFDictionaryCreate(
            kCFAllocatorDefault,
            keys.as_ptr(),
            values.as_ptr(),
            pairs.len() as isize,
            &kCFTypeDictionaryKeyCallBacks,
            &kCFTypeDictionaryValueCallBacks,
        )
    }
}

fn number(value: i32) -> CFTypeRef {
    // SAFETY: CFNumberCreate copies the value it is pointed at.
    unsafe {
        CFNumberCreate(
            kCFAllocatorDefault,
            kCFNumberSInt32Type,
            (&raw const value).cast(),
        )
        .cast()
    }
}

pub fn sources() -> Vec<SourceInfo> {
    if !sck::available() || !sck::permitted() {
        return Vec::new();
    }
    autoreleasepool(|_| match Content::fetch() {
        Ok(content) => content.sources(),
        Err(e) => {
            media_diag::diag_warn!("capture: {}", e.0);
            Vec::new()
        }
    })
}

pub fn supported() -> bool {
    sck::available()
}

pub fn open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    if !sck::available() {
        return Err(CaptureError(
            "screen sharing needs macOS 12.3 or later (ScreenCaptureKit)".into(),
        ));
    }
    let target = Target::parse(&shared.config.source_id).ok_or_else(|| {
        CaptureError(format!(
            "capture source {} is gone",
            shared.config.source_id
        ))
    })?;
    let mut threads = Vec::new();
    let video_shared = Arc::clone(shared);
    threads.push(
        std::thread::Builder::new()
            .name("bm-capture-video".into())
            .spawn(move || run_video(video_shared, target))
            .map_err(|e| CaptureError(format!("spawn capture thread: {e}")))?,
    );
    if shared.config.audio {
        let audio_shared = Arc::clone(shared);
        threads.push(
            std::thread::Builder::new()
                .name("bm-capture-audio".into())
                .spawn(move || run_audio(audio_shared, target))
                .map_err(|e| CaptureError(format!("spawn audio thread: {e}")))?,
        );
    }
    Ok(threads)
}

#[derive(Default)]
struct Latest {
    image: Option<Pixel>,
    fresh: bool,
}

fn run_video(shared: Arc<Shared>, target: Target) {
    let result = catch_unwind(AssertUnwindSafe(|| {
        autoreleasepool(|_| capture_video(&shared, target))
    }))
    .unwrap_or_else(|_| Err(CaptureError("screen capture stopped unexpectedly".into())));
    match result {
        Ok(()) => shared.update(|s| {
            if s.state != CaptureState::Error {
                s.state = CaptureState::Ended;
            }
        }),
        Err(e) => shared.fail(e.0),
    }
}

fn capture_video(shared: &Arc<Shared>, target: Target) -> Result<(), CaptureError> {
    let config = shared.config.clone();
    let content = Content::fetch()?;
    let source = content
        .source(target)?
        .ok_or_else(|| CaptureError(format!("capture source {} is gone", config.source_id)))?;
    let size = fit_size(
        source.width,
        source.height,
        config.max_width,
        config.max_height,
    );
    let slot = Arc::new((Mutex::new(Latest::default()), Condvar::new()));
    let stream = {
        let slot = Arc::clone(&slot);
        let shared = Arc::clone(shared);
        Stream::start(
            &source.filter,
            &Settings {
                width: size.0,
                height: size.1,
                fps: config.fps,
                cursor: config.cursor,
                audio: false,
            },
            Some(Box::new(move |sample| {
                // SAFETY: ScreenCaptureKit hands a live sample buffer.
                let Some(image) = (unsafe { Pixel::from_sample(sample) }) else {
                    return;
                };
                shared.captured.fetch_add(1, Ordering::Relaxed);
                let (lock, wake) = &*slot;
                let mut latest = lock.lock().unwrap_or_else(|e| e.into_inner());
                latest.image = Some(image);
                latest.fresh = true;
                wake.notify_one();
            })),
            None,
        )?
    };
    drop(content);
    let opened = Instant::now();
    let mut encoder: Option<VtEncoder> = None;
    let mut bitrate = shared.bitrate_kbps.load(Ordering::Acquire);
    let mut current: Option<Pixel> = None;
    let mut last_encode: Option<Instant> = None;
    let mut last_pts = -1i64;
    let interval = Duration::from_secs_f64(1.0 / f64::from(config.fps.max(1)));
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
                current = latest.image.take();
                latest.fresh = false;
                true
            } else {
                false
            }
        };
        if let Some(reason) = stream.stopped() {
            return if shared.captured.load(Ordering::Relaxed) > 0 {
                Ok(())
            } else {
                Err(CaptureError(reason))
            };
        }
        let Some(image) = current.as_ref() else {
            if opened.elapsed() >= FIRST_FRAME {
                return Err(CaptureError(
                    "the screen capture sent no frames (check the Screen Recording permission)"
                        .into(),
                ));
            }
            continue;
        };
        let repeat_due = last_encode.is_some_and(|t| t.elapsed() >= REPEAT);
        let paced = last_encode.is_none_or(|t| t.elapsed() >= interval)
            || shared.keyframe.load(Ordering::Acquire);
        if !(fresh && paced) && !repeat_due {
            continue;
        }
        if encoder.is_none() {
            let created = VtEncoder::open(
                config.encoder,
                size,
                config.fps,
                bitrate,
                (config.fps * config.keyframe_interval_ms / 1000).max(1),
            )?;
            let (name, hardware) = (created.name(), created.hardware());
            let (width, height) = image.size();
            shared.update(|s| {
                s.state = CaptureState::Running;
                s.width = width;
                s.height = height;
                s.encoder = name;
                s.hardware = hardware;
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
        for frame in active.encode(image.0, pts, key)? {
            shared.encoded.fetch_add(1, Ordering::Relaxed);
            shared.push_video(frame);
        }
        last_encode = Some(Instant::now());
        last_pts = pts;
    }
    drop(encoder);
    drop(stream);
    Ok(())
}

fn run_audio(shared: Arc<Shared>, target: Target) {
    let result = catch_unwind(AssertUnwindSafe(|| {
        autoreleasepool(|_| capture_audio(&shared, target))
    }))
    .unwrap_or_else(|_| Err(CaptureError("audio capture stopped unexpectedly".into())));
    if let Err(e) = result {
        media_diag::diag_warn!("capture audio: {}", e.0);
        shared.update(|s| {
            s.audio = false;
            s.audio_error = e.0.clone();
        });
    }
}

fn capture_audio(shared: &Arc<Shared>, target: Target) -> Result<(), CaptureError> {
    if !sck::audio_available() {
        return Err(CaptureError(sck::AUDIO_VERSION.into()));
    }
    let content = Content::fetch()?;
    let filter = content.audio_filter(target, shared.config.audio_source != AudioSource::System)?;
    let packer = Arc::new(Mutex::new(Packer::new(shared)?));
    let stream = {
        let packer = Arc::clone(&packer);
        let shared = Arc::clone(shared);
        Stream::start(
            &filter,
            &Settings {
                width: 64,
                height: 64,
                fps: 1,
                cursor: false,
                audio: true,
            },
            None,
            Some(Box::new(move |sample| {
                // SAFETY: ScreenCaptureKit hands a live audio sample buffer.
                let Some(samples) = (unsafe { sck::stereo(sample) }) else {
                    return;
                };
                let mut packer = packer.lock().unwrap_or_else(|e| e.into_inner());
                if let Err(e) = packer.push(&shared, &samples) {
                    media_diag::diag_warn!("capture audio: {}", e.0);
                }
            })),
        )?
    };
    drop(content);
    shared.update(|s| s.audio = true);
    while !shared.stopped() {
        std::thread::sleep(Duration::from_millis(20));
        if let Some(reason) = stream.stopped() {
            return Err(CaptureError(reason));
        }
        packer
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .fill_silence(shared)?;
    }
    Ok(())
}
