//! The shared async-callback plumbing under both adapters. MediaCodec's
//! callbacks land on the codec's own internal thread and only push indices
//! into this state and notify; the engine's decode thread consumes through
//! the trait surface. Queue depths are the codec's own (input buffers
//! granted by `onAsyncInputAvailable`, output buffers by
//! `onAsyncOutputAvailable`), and a dry input queue surfaces as
//! `NotAccepting` for the caller to retry.
//!
//! The callback thread belongs to libmediandk and cannot unwind, so every
//! trampoline below is fenced and no acquisition of this state panics on a
//! poisoned lock. The queues and the format stay structurally valid across
//! a poisoning.

use std::collections::VecDeque;
use std::ffi::{CStr, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

use media_decode::{DecodeError, SubmitOutcome};

use crate::ffi::*;

#[derive(Default)]
pub(crate) struct CbState {
    pub input_free: VecDeque<i32>,
    pub output_ready: VecDeque<(i32, AMediaCodecBufferInfo)>,
    /// Set by a flush, cleared when the next input is queued. No output of
    /// the new timeline can exist before then, so anything arriving in
    /// between was posted before the flush.
    pub stale_outputs: bool,
    /// First asynchronous codec error; fails the session on next use.
    pub error: Option<String>,
    /// Latest output-format colour/geometry, parsed in the format-changed
    /// callback (the format object itself stays framework-owned).
    pub format: OutputFormat,
}

#[derive(Debug, Clone, Copy, Default)]
pub(crate) struct OutputFormat {
    pub color_standard: i32,
    pub color_range: i32,
    pub sample_rate: i32,
    pub channels: i32,
    pub pcm_encoding: i32,
    pub seen: bool,
}

pub(crate) struct Callbacks {
    state: Mutex<CbState>,
    pub changed: Condvar,
}

impl Callbacks {
    fn new() -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::new(CbState::default()),
            changed: Condvar::new(),
        })
    }

    /// The only way to the state, recovering from poison. `state` is
    /// private so nothing takes it with a plain `unwrap`, which would panic
    /// on the codec's own thread, which cannot unwind.
    pub(crate) fn lock(&self) -> std::sync::MutexGuard<'_, CbState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }
}

/// Longest decoder-supplied error detail read. Bounds the scan as well as
/// the copy: the string's length is the codec's choice, and a vendor blob
/// without a terminator would otherwise read off the end of the buffer.
const DETAIL_CAP: usize = 256;

/// Note a panic one of the callback fences caught.
///
/// Swallowing the panic outright would leave the codec holding a granted
/// index nobody collects and the session waiting on a buffer that never
/// arrives. Recording it turns that silent stall into an ordinary failed
/// session on the next use.
///
/// Re-locking here is sound: the guard the panicking frame held was
/// dropped as the stack unwound, and the lock is taken through poison
/// recovery.
///
/// # Safety
/// `userdata` is the `Arc<Callbacks>` raw pointer this driver registered,
/// as in `on_input`.
unsafe fn note_callback_panic(userdata: *mut c_void) {
    // SAFETY: caller contract, as documented above.
    let cb = unsafe { &*(userdata as *const Callbacks) };
    let mut state = cb.lock();
    state
        .error
        .get_or_insert_with(|| "a codec callback panicked".to_string());
    cb.changed.notify_all();
}

unsafe extern "C" fn on_input(_codec: *mut AMediaCodec, userdata: *mut c_void, index: i32) {
    if catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: userdata is the Arc<Callbacks> raw pointer this driver
        // registered and keeps alive until after AMediaCodec_delete returns
        // (delete joins the callback thread, so no callback outlives it).
        let cb = unsafe { &*(userdata as *const Callbacks) };
        let mut state = cb.lock();
        // A grant posted before a flush can arrive after it, alongside the
        // fresh grant of the same index.
        if !state.input_free.contains(&index) {
            state.input_free.push_back(index);
        }
        cb.changed.notify_all();
    }))
    .is_err()
    {
        // SAFETY: userdata is this driver's registered pointer, as above.
        unsafe { note_callback_panic(userdata) };
    }
}

unsafe extern "C" fn on_output(
    _codec: *mut AMediaCodec,
    userdata: *mut c_void,
    index: i32,
    info: *mut AMediaCodecBufferInfo,
) {
    if catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: userdata as in `on_input`; `info` is valid for the duration
        // of the callback per the NDK contract and is copied out here.
        let (cb, info) = unsafe { (&*(userdata as *const Callbacks), *info) };
        let mut state = cb.lock();
        if !state.stale_outputs {
            // The codec cannot grant an index the client still holds, so an
            // entry already queued under this index is from before a flush.
            state.output_ready.retain(|(queued, _)| *queued != index);
            state.output_ready.push_back((index, info));
        }
        cb.changed.notify_all();
    }))
    .is_err()
    {
        // SAFETY: userdata is this driver's registered pointer, as above.
        unsafe { note_callback_panic(userdata) };
    }
}

unsafe extern "C" fn on_format(
    _codec: *mut AMediaCodec,
    userdata: *mut c_void,
    format: *mut AMediaFormat,
) {
    if catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: userdata as in `on_input`; the format object is only read
        // during the callback (framework-owned, valid for its duration).
        let cb = unsafe { &*(userdata as *const Callbacks) };
        let get = |name: &CStr| -> i32 {
            let mut value = 0i32;
            // SAFETY: live format pointer, NUL-terminated key, out-param is a
            // local. A missing key leaves `value` untouched (getInt32 returns
            // false).
            unsafe { AMediaFormat_getInt32(format, name.as_ptr(), &mut value) };
            value
        };
        let parsed = OutputFormat {
            color_standard: get(c"color-standard"),
            color_range: get(c"color-range"),
            sample_rate: get(c"sample-rate"),
            channels: get(c"channel-count"),
            pcm_encoding: get(c"pcm-encoding"),
            seen: true,
        };
        let mut state = cb.lock();
        state.format = parsed;
        cb.changed.notify_all();
    }))
    .is_err()
    {
        // SAFETY: userdata is this driver's registered pointer, as above.
        unsafe { note_callback_panic(userdata) };
    }
}

unsafe extern "C" fn on_error(
    _codec: *mut AMediaCodec,
    userdata: *mut c_void,
    error: media_status_t,
    action_code: i32,
    detail: *const core::ffi::c_char,
) {
    if catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: userdata as in `on_input`; detail is a NUL-terminated C
        // string (or null) valid for the callback's duration.
        let cb = unsafe { &*(userdata as *const Callbacks) };
        let detail = if detail.is_null() {
            String::new()
        } else {
            // SAFETY: non-null per the check above. `strnlen` stops at the
            // NUL the NDK contract promises or at DETAIL_CAP, whichever
            // comes first, so the slice below spans bytes it just read.
            unsafe {
                let len = libc::strnlen(detail, DETAIL_CAP);
                String::from_utf8_lossy(std::slice::from_raw_parts(detail.cast::<u8>(), len))
                    .into_owned()
            }
        };
        let mut state = cb.lock();
        state.error.get_or_insert(format!(
            "codec error {error} (action {action_code}): {detail}"
        ));
        cb.changed.notify_all();
    }))
    .is_err()
    {
        // SAFETY: userdata is this driver's registered pointer, as above.
        unsafe { note_callback_panic(userdata) };
    }
}

/// One async-driven `AMediaCodec` plus the callback state it feeds.
pub(crate) struct AsyncCodec {
    codec: *mut AMediaCodec,
    pub cb: Arc<Callbacks>,
    /// Raw Arc handed to the callbacks; released after delete.
    userdata: *const Callbacks,
    pub name: String,
    /// EOS submitted to the input side this timeline.
    eos_queued: bool,
    /// A drain was requested before an input buffer was free.
    eos_pending: bool,
    /// A flush has happened and no output has been released since.
    flush_unsettled: bool,
}

// SAFETY: the codec handle is driven from one engine thread at a time;
// libmediandk's own callback thread only touches the Mutex-guarded state.
unsafe impl Send for AsyncCodec {}

impl AsyncCodec {
    /// Create a decoder for `mime`, configure it with `format` (consumed)
    /// and the optional output surface, register callbacks, start it.
    ///
    /// # Safety
    /// `format` must be a live `AMediaFormat*` the caller owns; it is
    /// consumed here and deleted before this returns, so the caller must
    /// not touch or free it afterwards. `surface` must be null or a live
    /// `ANativeWindow*` that outlives the codec, since MediaCodec renders
    /// into it for as long as the decoder runs.
    pub unsafe fn start(
        mime: &CStr,
        format: *mut AMediaFormat,
        surface: *mut ANativeWindow,
    ) -> Result<Self, DecodeError> {
        // SAFETY: create/configure/start sequence per the NDK state
        // machine, all pointers checked; the format object is ours until
        // deleted below, and the callback userdata Arc outlives the codec.
        unsafe {
            let codec = AMediaCodec_createDecoderByType(mime.as_ptr());
            if codec.is_null() {
                AMediaFormat_delete(format);
                return Err(DecodeError(format!(
                    "no MediaCodec decoder for {}",
                    mime.to_string_lossy()
                )));
            }
            let name = codec_name(codec).unwrap_or_else(|| "<unnamed>".into());

            let cb = Callbacks::new();
            let userdata = Arc::into_raw(Arc::clone(&cb));
            let callbacks = AMediaCodecOnAsyncNotifyCallback {
                on_async_input_available: on_input,
                on_async_output_available: on_output,
                on_async_format_changed: on_format,
                on_async_error: on_error,
            };
            let status =
                AMediaCodec_setAsyncNotifyCallback(codec, callbacks, userdata as *mut c_void);
            if status != AMEDIA_OK {
                AMediaFormat_delete(format);
                AMediaCodec_delete(codec);
                drop(Arc::from_raw(userdata));
                return Err(DecodeError(format!(
                    "setAsyncNotifyCallback failed ({status}) on {name}"
                )));
            }
            let status = AMediaCodec_configure(codec, format, surface, core::ptr::null_mut(), 0);
            AMediaFormat_delete(format);
            if status != AMEDIA_OK {
                AMediaCodec_delete(codec);
                drop(Arc::from_raw(userdata));
                return Err(DecodeError(format!(
                    "configure failed ({status}) on {name}"
                )));
            }
            let status = AMediaCodec_start(codec);
            if status != AMEDIA_OK {
                AMediaCodec_delete(codec);
                drop(Arc::from_raw(userdata));
                return Err(DecodeError(format!("start failed ({status}) on {name}")));
            }
            Ok(Self {
                codec,
                cb,
                userdata,
                name,
                eos_queued: false,
                eos_pending: false,
                flush_unsettled: false,
            })
        }
    }

    pub fn raw(&self) -> *mut AMediaCodec {
        self.codec
    }

    pub fn take_error(&self) -> Option<DecodeError> {
        let state = self.cb.lock();
        state
            .error
            .as_ref()
            .map(|e| DecodeError(format!("{} ({})", e, self.name)))
    }

    /// Copy one compressed AU into a free input buffer. A dry input queue
    /// is `NotAccepting`; the codec grants indices as it consumes.
    pub fn submit(&mut self, au: &[u8], pts_us: i64) -> Result<SubmitOutcome, DecodeError> {
        if let Some(e) = self.take_error() {
            return Err(e);
        }
        if self.eos_queued {
            // Post-drain submissions wait for the reset.
            return Ok(SubmitOutcome::NotAccepting);
        }
        self.flush_eos_if_pending()?;
        if self.eos_pending {
            // The parked EOS owns the next free index.
            return Ok(SubmitOutcome::NotAccepting);
        }
        let Some((index, buf, size)) = self.pop_input() else {
            return Ok(SubmitOutcome::NotAccepting);
        };
        // SAFETY: `pop_input` returned a buffer the codec has granted, which
        // spans `size` writable bytes; the copy is bounds-checked against it.
        unsafe {
            if size < au.len() {
                return Err(DecodeError(format!(
                    "input buffer {index} too small ({size} < {}) on {}",
                    au.len(),
                    self.name
                )));
            }
            core::ptr::copy_nonoverlapping(au.as_ptr(), buf, au.len());
            // Negative pts (encoder priming) round-trips through the
            // unsigned parameter: the framework's internal timestamp is
            // int64, so the two's-complement value comes back intact.
            let status = AMediaCodec_queueInputBuffer(
                self.codec,
                index as usize,
                0,
                au.len(),
                pts_us as u64,
                0,
            );
            if status != AMEDIA_OK {
                return Err(DecodeError(format!(
                    "queueInputBuffer failed ({status}) on {}",
                    self.name
                )));
            }
        }
        self.cb.lock().stale_outputs = false;
        Ok(SubmitOutcome::Accepted)
    }

    /// The next free input index with its buffer. An index whose buffer the
    /// codec will not hand over is a grant from before a flush that the
    /// codec has since taken back, and is skipped.
    fn pop_input(&mut self) -> Option<(i32, *mut u8, usize)> {
        loop {
            let index = self.cb.lock().input_free.pop_front()?;
            let mut size = 0usize;
            // SAFETY: live codec; a non-granted index returns null rather
            // than a buffer.
            let buf = unsafe { AMediaCodec_getInputBuffer(self.codec, index as usize, &mut size) };
            if !buf.is_null() {
                return Some((index, buf, size));
            }
            crate::ffi::alog(&format!(
                "mediacodec: input index {index} no longer granted, skipped ({})",
                self.name
            ));
        }
    }

    /// Return an output index to the codec. `Ok(false)` when it was not
    /// released: before the first release after a flush, a refusal means
    /// the index was granted before the flush and is skipped. After that a
    /// refusal is the codec failing.
    pub fn release_output(&mut self, index: i32, render: bool) -> Result<bool, DecodeError> {
        // SAFETY: live codec; the index came from onAsyncOutputAvailable and
        // is released at most once.
        let status = unsafe { AMediaCodec_releaseOutputBuffer(self.codec, index as usize, render) };
        if status == AMEDIA_OK {
            self.flush_unsettled = false;
            return Ok(true);
        }
        if self.flush_unsettled {
            crate::ffi::alog(&format!(
                "mediacodec: output index {index} from before the flush, skipped ({status}, {})",
                self.name
            ));
            return Ok(false);
        }
        Err(DecodeError(format!(
            "releaseOutputBuffer failed ({status}) on {}",
            self.name
        )))
    }

    /// Queue the end-of-stream marker, parking it if no input buffer is
    /// free yet (the codec grants one as it drains).
    pub fn begin_drain(&mut self) -> Result<(), DecodeError> {
        if self.eos_queued {
            return Ok(());
        }
        self.eos_pending = true;
        self.flush_eos_if_pending()
    }

    fn flush_eos_if_pending(&mut self) -> Result<(), DecodeError> {
        if !self.eos_pending || self.eos_queued {
            return Ok(());
        }
        let Some((index, _, _)) = self.pop_input() else {
            return Ok(());
        };
        // SAFETY: a granted input index; an empty EOS buffer needs no
        // payload write.
        let status = unsafe {
            AMediaCodec_queueInputBuffer(
                self.codec,
                index as usize,
                0,
                0,
                0,
                BUFFER_FLAG_END_OF_STREAM,
            )
        };
        if status != AMEDIA_OK {
            return Err(DecodeError(format!(
                "queueInputBuffer(EOS) failed ({status}) on {}",
                self.name
            )));
        }
        self.eos_pending = false;
        self.eos_queued = true;
        self.cb.lock().stale_outputs = false;
        Ok(())
    }

    pub fn draining(&self) -> bool {
        self.eos_queued || self.eos_pending
    }

    /// Pop the next ready output index, optionally waiting up to `wait`
    /// for one (the drain path waits; steady-state polling does not).
    pub fn pop_output(&self, wait: Duration) -> Option<(i32, AMediaCodecBufferInfo)> {
        let mut state = self.cb.lock();
        if let Some(entry) = state.output_ready.pop_front() {
            return Some(entry);
        }
        if wait.is_zero() {
            return None;
        }
        let (mut state, _timeout) = self
            .cb
            .changed
            .wait_timeout(state, wait)
            .unwrap_or_else(|e| e.into_inner());
        state.output_ready.pop_front()
    }

    pub fn output_format(&self) -> OutputFormat {
        self.cb.lock().format
    }

    /// Turn a running decoder's low-latency output on or off: `low-latency`
    /// (`MediaCodec.PARAMETER_KEY_LOW_LATENCY`, a runtime parameter since
    /// API 30) and Qualcomm's vendor key. A decoder ignores keys it does not
    /// know, so the only failure reported is the call itself.
    pub fn set_low_latency(&self, enabled: bool) -> Result<(), DecodeError> {
        // SAFETY: a format object owned here and deleted before returning,
        // NUL-terminated keys, and a live codec.
        unsafe {
            let params = AMediaFormat_new();
            if params.is_null() {
                return Err(DecodeError("AMediaFormat_new failed".into()));
            }
            let value = i32::from(enabled);
            AMediaFormat_setInt32(params, c"low-latency".as_ptr(), value);
            AMediaFormat_setInt32(
                params,
                c"vendor.qti-ext-dec-low-latency.enable".as_ptr(),
                value,
            );
            let status = AMediaCodec_setParameters(self.codec, params);
            AMediaFormat_delete(params);
            if status != AMEDIA_OK {
                return Err(DecodeError(format!(
                    "setParameters(low-latency) failed ({status}) on {}",
                    self.name
                )));
            }
        }
        Ok(())
    }

    /// Flush for a seek/loop. This invalidates every granted index, so both
    /// queues are cleared. Async codecs must be restarted after a flush.
    pub fn reset(&mut self) -> Result<(), DecodeError> {
        // SAFETY: flush/start on a live codec in the Executing state.
        unsafe {
            let status = AMediaCodec_flush(self.codec);
            if status != AMEDIA_OK {
                return Err(DecodeError(format!(
                    "flush failed ({status}) on {}",
                    self.name
                )));
            }
            // Callbacks posted before the flush can still arrive after this
            // clear: the NDK delivers them on its own thread with no flush
            // check, and offers no way to queue a marker behind them. Outputs
            // are refused until the next input is queued; stale input grants
            // are weeded out by `pop_input`, and a stale output that still
            // gets through by `release_output`.
            {
                let mut state = self.cb.lock();
                state.input_free.clear();
                state.output_ready.clear();
                state.stale_outputs = true;
            }
            let status = AMediaCodec_start(self.codec);
            if status != AMEDIA_OK {
                return Err(DecodeError(format!(
                    "start-after-flush failed ({status}) on {}",
                    self.name
                )));
            }
        }
        self.eos_queued = false;
        self.eos_pending = false;
        self.flush_unsettled = true;
        Ok(())
    }
}

impl Drop for AsyncCodec {
    fn drop(&mut self) {
        // SAFETY: stop/delete join the codec's callback thread, so the
        // userdata Arc is only released once no callback can run again.
        unsafe {
            AMediaCodec_stop(self.codec);
            AMediaCodec_delete(self.codec);
            drop(Arc::from_raw(self.userdata));
        }
    }
}

/// The codec's component name (e.g. `c2.qti.avc.decoder`).
///
/// # Safety
/// `codec` must be a live `AMediaCodec*`; `getName` reads through it.
pub(crate) unsafe fn codec_name(codec: *mut AMediaCodec) -> Option<String> {
    // SAFETY: getName allocates a NUL-terminated string released via
    // releaseName; the contents are copied before release.
    unsafe {
        let mut name: *mut core::ffi::c_char = core::ptr::null_mut();
        if AMediaCodec_getName(codec, &mut name) != AMEDIA_OK || name.is_null() {
            return None;
        }
        let owned = CStr::from_ptr(name).to_string_lossy().into_owned();
        AMediaCodec_releaseName(codec, name);
        Some(owned)
    }
}
