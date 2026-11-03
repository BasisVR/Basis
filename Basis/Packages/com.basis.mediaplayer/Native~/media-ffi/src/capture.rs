use std::collections::HashMap;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock};

use media_capture::{Capture, CaptureConfig, EncodedVideo, Shared};
use media_engine::PushOutcome;

use crate::{BM_ERR_INVALID_ARG, BM_ERR_INVALID_HANDLE, BM_ERR_PANIC, BM_OK, host_log, lookup};

pub const BM_ERR_TOO_SMALL: i32 = -4;
pub const BM_ERR_CAPTURE: i32 = -5;

#[repr(C)]
#[derive(Default)]
pub struct BmEncodedFrame {
    pub pts_us: i64,
    pub len: u32,
    pub key: u32,
    pub width: u32,
    pub height: u32,
}

struct CaptureEntry {
    capture: Mutex<Option<Capture>>,
    shared: Arc<Shared>,
    pending: Mutex<Option<EncodedVideo>>,
    preview: AtomicU64,
}

fn captures() -> &'static Mutex<HashMap<u64, Arc<CaptureEntry>>> {
    static CAPTURES: OnceLock<Mutex<HashMap<u64, Arc<CaptureEntry>>>> = OnceLock::new();
    CAPTURES.get_or_init(|| Mutex::new(HashMap::new()))
}

fn capture_entry(handle: u64) -> Option<Arc<CaptureEntry>> {
    captures().lock().ok()?.get(&handle).cloned()
}

fn write_json(json: &str, out_buf: *mut u8, cap: usize) -> i32 {
    let bytes = json.as_bytes();
    if !out_buf.is_null() && cap >= bytes.len() {
        // SAFETY: caller contract: out_buf points to cap writable bytes and
        // cap covers the blob.
        unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, bytes.len()) };
    }
    bytes.len() as i32
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_push_version() -> u32 {
    media_engine::PUSH_VERSION
}

/// # Safety
/// `data` must point to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_push_video(
    handle: u64,
    data: *const u8,
    len: usize,
    pts_us: i64,
    key: u32,
    width: u32,
    height: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if data.is_null() || len == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Some(feed) = entry.push.as_ref() else {
            return BM_ERR_INVALID_HANDLE;
        };
        // SAFETY: caller contract: data points to len readable bytes for the
        // duration of this call; the feed copies them.
        let bytes = unsafe { std::slice::from_raw_parts(data, len) };
        feed.push_video(bytes, pts_us, key != 0, width, height) as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// `data` must point to `len` readable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_session_push_audio(
    handle: u64,
    data: *const u8,
    len: usize,
    pts_us: i64,
    sample_rate: u32,
    channels: u32,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if data.is_null() || len == 0 {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = lookup(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let Some(feed) = entry.push.as_ref() else {
            return BM_ERR_INVALID_HANDLE;
        };
        // SAFETY: as for bm_session_push_video.
        let bytes = unsafe { std::slice::from_raw_parts(data, len) };
        feed.push_audio(bytes, pts_us, sample_rate, channels) as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_supported() -> u32 {
    catch_unwind(media_capture::supported)
        .map(u32::from)
        .unwrap_or(0)
}

/// # Safety
/// `out_buf` must be `NULL` or point to `cap` writable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capture_sources(out_buf: *mut u8, cap: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        let json = serde_json::to_string(&media_capture::sources()).unwrap_or_else(|_| "[]".into());
        write_json(&json, out_buf, cap)
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// `desc_ptr` must point to `desc_len` readable bytes and `out_handle` must
/// be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capture_open(
    desc_ptr: *const u8,
    desc_len: usize,
    out_handle: *mut u64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        host_log::install();
        if desc_ptr.is_null() || out_handle.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        // SAFETY: caller contract: desc_ptr points to desc_len bytes.
        let bytes = unsafe { std::slice::from_raw_parts(desc_ptr, desc_len) };
        let Ok(config) = serde_json::from_slice::<CaptureConfig>(bytes) else {
            return BM_ERR_INVALID_ARG;
        };
        let capture = match Capture::open(config) {
            Ok(capture) => capture,
            Err(e) => {
                media_diag::diag_warn!("capture open: {e}");
                return BM_ERR_CAPTURE;
            }
        };
        static NEXT: AtomicU64 = AtomicU64::new(1);
        let handle = NEXT.fetch_add(1, Ordering::Relaxed);
        let entry = Arc::new(CaptureEntry {
            shared: capture.shared(),
            capture: Mutex::new(Some(capture)),
            pending: Mutex::new(None),
            preview: AtomicU64::new(0),
        });
        captures()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .insert(handle, entry);
        // SAFETY: caller contract: out_handle is writable.
        unsafe { *out_handle = handle };
        BM_OK
    }))
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_close(handle: u64) -> i32 {
    catch_unwind(|| {
        let Some(entry) = captures()
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .remove(&handle)
        else {
            return BM_ERR_INVALID_HANDLE;
        };
        if let Some(mut capture) = entry
            .capture
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .take()
        {
            capture.stop();
        }
        BM_OK
    })
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// `out_buf` must be `NULL` or point to `cap` writable bytes.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capture_status(handle: u64, out_buf: *mut u8, cap: usize) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        let Some(entry) = capture_entry(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let guard = entry.capture.lock().unwrap_or_else(|e| e.into_inner());
        let Some(capture) = guard.as_ref() else {
            return BM_ERR_INVALID_HANDLE;
        };
        let json = serde_json::to_string(&capture.status()).unwrap_or_else(|_| "{}".into());
        write_json(&json, out_buf, cap)
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// `out_buf` must point to `cap` writable bytes and `info` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capture_read_video(
    handle: u64,
    out_buf: *mut u8,
    cap: usize,
    info: *mut BmEncodedFrame,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if info.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = capture_entry(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let mut pending = entry.pending.lock().unwrap_or_else(|e| e.into_inner());
        if pending.is_none() {
            let guard = entry.capture.lock().unwrap_or_else(|e| e.into_inner());
            *pending = guard.as_ref().and_then(Capture::take_video);
            if let (Some(frame), Some(capture)) = (pending.as_ref(), guard.as_ref()) {
                tee_preview(&entry, capture, frame);
            }
        }
        let Some(frame) = pending.as_ref() else {
            return 0;
        };
        let out = BmEncodedFrame {
            pts_us: frame.pts_us,
            len: frame.data.len() as u32,
            key: u32::from(frame.key),
            width: frame.width,
            height: frame.height,
        };
        if out_buf.is_null() || cap < frame.data.len() {
            // SAFETY: caller contract: info is writable.
            unsafe { *info = out };
            return BM_ERR_TOO_SMALL;
        }
        // SAFETY: caller contract: out_buf holds cap bytes, checked above to
        // cover the frame, and info is writable.
        unsafe {
            std::ptr::copy_nonoverlapping(frame.data.as_ptr(), out_buf, frame.data.len());
            *info = out;
        }
        let len = frame.data.len() as i32;
        *pending = None;
        len
    }))
    .unwrap_or(BM_ERR_PANIC)
}

/// # Safety
/// `out_buf` must point to `cap` writable bytes and `pts_us` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn bm_capture_read_audio(
    handle: u64,
    out_buf: *mut u8,
    cap: usize,
    pts_us: *mut i64,
) -> i32 {
    catch_unwind(AssertUnwindSafe(|| {
        if out_buf.is_null() || pts_us.is_null() {
            return BM_ERR_INVALID_ARG;
        }
        let Some(entry) = capture_entry(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        let guard = entry.capture.lock().unwrap_or_else(|e| e.into_inner());
        let Some(packet) = guard.as_ref().and_then(Capture::take_audio) else {
            return 0;
        };
        if packet.data.len() > cap {
            return BM_ERR_TOO_SMALL;
        }
        // SAFETY: caller contract, with the length checked against cap.
        unsafe {
            std::ptr::copy_nonoverlapping(packet.data.as_ptr(), out_buf, packet.data.len());
            *pts_us = packet.pts_us;
        }
        packet.data.len() as i32
    }))
    .unwrap_or(BM_ERR_PANIC)
}

fn tee_preview(entry: &CaptureEntry, capture: &Capture, frame: &EncodedVideo) {
    let session = entry.preview.load(Ordering::Acquire);
    if session == 0 {
        return;
    }
    let Some(target) = lookup(session) else {
        entry.preview.store(0, Ordering::Release);
        return;
    };
    let Some(feed) = target.push.as_ref() else {
        return;
    };
    match feed.push_video(
        &frame.data,
        frame.pts_us,
        frame.key,
        frame.width,
        frame.height,
    ) {
        PushOutcome::NeedKeyframe | PushOutcome::Full => capture.request_keyframe(),
        PushOutcome::Closed => entry.preview.store(0, Ordering::Release),
        PushOutcome::Accepted | PushOutcome::Refused => {}
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_set_preview(handle: u64, session: u64) -> i32 {
    catch_unwind(|| {
        let Some(entry) = capture_entry(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        entry.preview.store(session, Ordering::Release);
        BM_OK
    })
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_wait(handle: u64, timeout_ms: u32) -> i32 {
    catch_unwind(|| {
        let Some(entry) = capture_entry(handle) else {
            return BM_ERR_INVALID_HANDLE;
        };
        if entry
            .pending
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .is_some()
        {
            return 1;
        }
        i32::from(
            entry
                .shared
                .wait(std::time::Duration::from_millis(u64::from(
                    timeout_ms.min(1000),
                ))),
        )
    })
    .unwrap_or(BM_ERR_PANIC)
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_request_keyframe(handle: u64) -> i32 {
    let Some(entry) = capture_entry(handle) else {
        return BM_ERR_INVALID_HANDLE;
    };
    match entry
        .capture
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .as_ref()
    {
        Some(capture) => {
            capture.request_keyframe();
            BM_OK
        }
        None => BM_ERR_INVALID_HANDLE,
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn bm_capture_set_bitrate(handle: u64, kbps: u32) -> i32 {
    let Some(entry) = capture_entry(handle) else {
        return BM_ERR_INVALID_HANDLE;
    };
    match entry
        .capture
        .lock()
        .unwrap_or_else(|e| e.into_inner())
        .as_ref()
    {
        Some(capture) => {
            capture.set_bitrate(kbps);
            BM_OK
        }
        None => BM_ERR_INVALID_HANDLE,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn push_outcomes_keep_their_wire_values() {
        assert_eq!(PushOutcome::Accepted as i32, 0);
        assert_eq!(PushOutcome::NeedKeyframe as i32, 1);
        assert_eq!(PushOutcome::Full as i32, 2);
        assert_eq!(PushOutcome::Closed as i32, 3);
        assert_eq!(PushOutcome::Refused as i32, 4);
    }

    #[test]
    fn unknown_handles_are_refused() {
        assert_eq!(bm_capture_close(u64::MAX), BM_ERR_INVALID_HANDLE);
        assert_eq!(bm_capture_request_keyframe(u64::MAX), BM_ERR_INVALID_HANDLE);
        let mut info = BmEncodedFrame::default();
        let mut buf = [0u8; 4];
        // SAFETY: valid buffers; the handle is unknown so nothing is read.
        let read =
            unsafe { bm_capture_read_video(u64::MAX, buf.as_mut_ptr(), buf.len(), &mut info) };
        assert_eq!(read, BM_ERR_INVALID_HANDLE);
        // SAFETY: as above.
        let pushed =
            unsafe { bm_session_push_video(u64::MAX, buf.as_ptr(), buf.len(), 0, 1, 2, 2) };
        assert_eq!(pushed, BM_ERR_INVALID_HANDLE);
        // SAFETY: as above, sizing call with no buffer.
        assert!(unsafe { bm_capture_sources(std::ptr::null_mut(), 0) } >= 2);
    }
}
