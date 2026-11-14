use std::ffi::{CStr, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr::null_mut;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex, OnceLock};
use std::time::Duration;

use block2::RcBlock;
use core_foundation_sys::base::{CFRelease, CFRetain};
use core_foundation_sys::string::{
    CFStringGetCString, CFStringGetLength, CFStringGetMaximumSizeForEncoding, CFStringRef,
    kCFStringEncodingUTF8,
};
use objc2::rc::{Allocated, Retained};
use objc2::runtime::{AnyClass, AnyObject, AnyProtocol, Bool, ClassBuilder, Sel};
use objc2::{msg_send, sel};

use super::ffi::*;
use crate::{CaptureError, SourceInfo, SourceKind};

const OUTPUT_SCREEN: isize = 0;
const OUTPUT_AUDIO: isize = 1;
const ERROR_USER_DECLINED: isize = -3801;
const PERMISSION: &str = "screenShare.error.macPermission";
pub(crate) const AUDIO_VERSION: &str = "screenShare.error.macAudioVersion";
const WAIT: Duration = Duration::from_secs(10);
const FRAMEWORK: &str = "/System/Library/Frameworks/ScreenCaptureKit.framework/ScreenCaptureKit";

fn class(name: &CStr) -> Result<&'static AnyClass, CaptureError> {
    AnyClass::get(name).ok_or_else(|| {
        CaptureError(format!(
            "{} is missing (screen sharing needs macOS 12.3 or later)",
            name.to_string_lossy()
        ))
    })
}

/// ScreenCaptureKit is loaded on first use rather than linked, so the
/// plugin still loads on a macOS without it.
pub(crate) fn available() -> bool {
    static LOADED: OnceLock<bool> = OnceLock::new();
    *LOADED.get_or_init(|| {
        // SAFETY: loading a system framework only registers its classes.
        let loaded = unsafe { libloading::Library::new(FRAMEWORK) }.map(std::mem::forget);
        loaded.is_ok()
            && [
                c"SCStream",
                c"SCShareableContent",
                c"SCContentFilter",
                c"SCStreamConfiguration",
            ]
            .iter()
            .all(|n| AnyClass::get(n).is_some())
    })
}

/// Whether streams can capture audio (macOS 13 and later).
pub(crate) fn audio_available() -> bool {
    class(c"SCStreamConfiguration")
        .is_ok_and(|c| c.responds_to(sel!(setExcludesCurrentProcessAudio:)))
}

/// Whether Basis may record the screen, asking once if it has never been
/// asked. A grant only takes effect after the app restarts.
pub(crate) fn permitted() -> bool {
    // SAFETY: plain CoreGraphics queries; the request shows the system prompt
    // at most once per app.
    unsafe { CGPreflightScreenCaptureAccess() || CGRequestScreenCaptureAccess() }
}

pub(crate) fn string(value: *mut AnyObject) -> String {
    if value.is_null() {
        return String::new();
    }
    let cf = value as CFStringRef;
    // SAFETY: NSString is toll-free bridged to CFString; the buffer is sized
    // for the longest UTF-8 form of the string plus its terminator.
    unsafe {
        let size =
            CFStringGetMaximumSizeForEncoding(CFStringGetLength(cf), kCFStringEncodingUTF8) + 1;
        let mut buffer = vec![0 as c_char; size.max(1) as usize];
        if CFStringGetCString(
            cf,
            buffer.as_mut_ptr(),
            buffer.len() as isize,
            kCFStringEncodingUTF8,
        ) == 0
        {
            return String::new();
        }
        CStr::from_ptr(buffer.as_ptr())
            .to_string_lossy()
            .into_owned()
    }
}

fn describe(error: *mut AnyObject) -> String {
    if error.is_null() {
        return "ScreenCaptureKit failed without a reason".into();
    }
    // SAFETY: error is an NSError.
    let (code, text) = unsafe {
        let code: isize = msg_send![error, code];
        let text: *mut AnyObject = msg_send![error, localizedDescription];
        (code, string(text))
    };
    if code == ERROR_USER_DECLINED {
        PERMISSION.into()
    } else {
        format!("{text} ({code})")
    }
}

fn items(array: *mut AnyObject) -> Vec<*mut AnyObject> {
    if array.is_null() {
        return Vec::new();
    }
    // SAFETY: array is an NSArray; indices stay below its count.
    unsafe {
        let count: usize = msg_send![array, count];
        (0..count)
            .map(|i| msg_send![array, objectAtIndex: i])
            .collect()
    }
}

struct SendObject(Retained<AnyObject>);

// SAFETY: the objects passed through here (shareable content, NSError) are
// immutable once handed over.
unsafe impl Send for SendObject {}

type Slot<T> = Arc<(Mutex<Option<T>>, Condvar)>;

fn wait<T>(slot: &Slot<T>, what: &str) -> Result<T, CaptureError> {
    let (lock, wake) = &**slot;
    let guard = lock.lock().unwrap_or_else(|e| e.into_inner());
    let (mut guard, _) = wake
        .wait_timeout_while(guard, WAIT, |v| v.is_none())
        .unwrap_or_else(|e| e.into_inner());
    guard
        .take()
        .ok_or_else(|| CaptureError(format!("ScreenCaptureKit did not answer ({what})")))
}

fn fill<T>(slot: &Slot<T>, value: T) {
    let (lock, wake) = &**slot;
    *lock.lock().unwrap_or_else(|e| e.into_inner()) = Some(value);
    wake.notify_all();
}

fn display_pixels(id: u32, points: (u32, u32)) -> (u32, u32) {
    // SAFETY: CoreGraphics queries on a display id; the mode is released.
    unsafe {
        let mode = CGDisplayCopyDisplayMode(id);
        if mode.is_null() {
            return points;
        }
        let size = (
            CGDisplayModeGetPixelWidth(mode) as u32,
            CGDisplayModeGetPixelHeight(mode) as u32,
        );
        CGDisplayModeRelease(mode);
        if size.0 == 0 || size.1 == 0 {
            points
        } else {
            size
        }
    }
}

fn main_scale() -> f64 {
    // SAFETY: CoreGraphics queries on the main display.
    let (main, points) = unsafe { (CGMainDisplayID(), CGDisplayPixelsWide(CGMainDisplayID())) };
    let (pixels, _) = display_pixels(main, (points as u32, 0));
    if points == 0 {
        1.0
    } else {
        (f64::from(pixels) / points as f64).clamp(1.0, 4.0)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum Target {
    Display(u32),
    Window(u32),
}

impl Target {
    pub(crate) fn parse(id: &str) -> Option<Self> {
        if let Some(rest) = id.strip_prefix("monitor:") {
            rest.parse().ok().map(Self::Display)
        } else if let Some(rest) = id.strip_prefix("window:") {
            rest.parse().ok().map(Self::Window)
        } else {
            None
        }
    }
}

pub(crate) struct Source {
    pub filter: Retained<AnyObject>,
    pub width: u32,
    pub height: u32,
}

pub(crate) struct Content(Retained<AnyObject>);

impl Content {
    pub(crate) fn fetch() -> Result<Self, CaptureError> {
        let shareable = class(c"SCShareableContent")?;
        let slot: Slot<Result<SendObject, String>> = Arc::default();
        let answer = Arc::clone(&slot);
        let block = RcBlock::new(move |content: *mut AnyObject, error: *mut AnyObject| {
            // SAFETY: content is the handler's SCShareableContent or nil.
            let result = match unsafe { Retained::retain(content) } {
                Some(c) => Ok(SendObject(c)),
                None => Err(describe(error)),
            };
            fill(&answer, result);
        });
        // SAFETY: the class method takes a completion block, copied by the callee.
        let _: () =
            unsafe { msg_send![shareable, getShareableContentWithCompletionHandler: &*block] };
        match wait(&slot, "shareable content")? {
            Ok(content) => Ok(Self(content.0)),
            Err(e) => Err(CaptureError(e)),
        }
    }

    fn displays(&self) -> Vec<*mut AnyObject> {
        // SAFETY: displays is an NSArray property of the content.
        items(unsafe { msg_send![&*self.0, displays] })
    }

    fn windows(&self) -> Vec<*mut AnyObject> {
        // SAFETY: windows is an NSArray property of the content.
        items(unsafe { msg_send![&*self.0, windows] })
    }

    fn display_info(display: *mut AnyObject) -> (u32, u32, u32) {
        // SAFETY: display is an SCDisplay.
        let (id, width, height) = unsafe {
            let id: u32 = msg_send![display, displayID];
            let width: isize = msg_send![display, width];
            let height: isize = msg_send![display, height];
            (id, width.max(0) as u32, height.max(0) as u32)
        };
        let (w, h) = display_pixels(id, (width, height));
        (id, w, h)
    }

    /// A window worth offering: on screen, at the normal level, titled, of
    /// a useful size.
    fn window_info(window: *mut AnyObject, scale: f64) -> Option<(u32, String, u32, u32)> {
        // SAFETY: window is an SCWindow.
        unsafe {
            let on_screen: Bool = msg_send![window, isOnScreen];
            let layer: isize = msg_send![window, windowLayer];
            let frame: CGRect = msg_send![window, frame];
            if !on_screen.as_bool() || layer != 0 {
                return None;
            }
            if frame.size.width < 64.0 || frame.size.height < 64.0 {
                return None;
            }
            let title = string(msg_send![window, title]);
            if title.trim().is_empty() {
                return None;
            }
            let id: u32 = msg_send![window, windowID];
            Some((
                id,
                title,
                (frame.size.width * scale).round() as u32,
                (frame.size.height * scale).round() as u32,
            ))
        }
    }

    pub(crate) fn sources(&self) -> Vec<SourceInfo> {
        // SAFETY: a CoreGraphics query.
        let main = unsafe { CGMainDisplayID() };
        let mut list: Vec<SourceInfo> = self
            .displays()
            .into_iter()
            .enumerate()
            .map(|(index, display)| {
                let (id, width, height) = Self::display_info(display);
                SourceInfo {
                    id: format!("monitor:{id}"),
                    kind: SourceKind::Monitor,
                    name: format!("Display {}", index + 1),
                    width,
                    height,
                    primary: id == main,
                }
            })
            .collect();
        let scale = main_scale();
        list.extend(self.windows().into_iter().filter_map(|window| {
            Self::window_info(window, scale).map(|(id, title, width, height)| SourceInfo {
                id: format!("window:{id}"),
                kind: SourceKind::Window,
                name: title,
                width,
                height,
                primary: false,
            })
        }));
        list
    }

    fn find_window(&self, id: u32) -> Option<*mut AnyObject> {
        self.windows().into_iter().find(|&w| {
            // SAFETY: w is an SCWindow.
            let window_id: u32 = unsafe { msg_send![w, windowID] };
            window_id == id
        })
    }

    fn empty_array() -> Result<*mut AnyObject, CaptureError> {
        // SAFETY: +[NSArray array] returns an autoreleased empty array.
        Ok(unsafe { msg_send![class(c"NSArray")?, array] })
    }

    fn filter_alloc() -> Result<Allocated<AnyObject>, CaptureError> {
        // SAFETY: allocating an SCContentFilter for one of the initialisers below.
        Ok(unsafe { msg_send![class(c"SCContentFilter")?, alloc] })
    }

    /// The video filter and source size in pixels for a capture target.
    pub(crate) fn source(&self, target: Target) -> Result<Option<Source>, CaptureError> {
        match target {
            Target::Display(id) => {
                let Some(display) = self
                    .displays()
                    .into_iter()
                    .find(|&d| Self::display_info(d).0 == id)
                else {
                    return Ok(None);
                };
                let (_, width, height) = Self::display_info(display);
                let empty = Self::empty_array()?;
                let alloc = Self::filter_alloc()?;
                // SAFETY: display and the empty array are live for the call.
                let filter: Option<Retained<AnyObject>> =
                    unsafe { msg_send![alloc, initWithDisplay: display, excludingWindows: empty] };
                Ok(filter.map(|filter| Source {
                    filter,
                    width,
                    height,
                }))
            }
            Target::Window(id) => {
                let Some(window) = self.find_window(id) else {
                    return Ok(None);
                };
                // SAFETY: window is an SCWindow.
                let frame: CGRect = unsafe { msg_send![window, frame] };
                let scale = main_scale();
                let alloc = Self::filter_alloc()?;
                // SAFETY: window is live for the call.
                let filter: Option<Retained<AnyObject>> =
                    unsafe { msg_send![alloc, initWithDesktopIndependentWindow: window] };
                Ok(filter.map(|filter| Source {
                    filter,
                    width: (frame.size.width * scale).round().max(2.0) as u32,
                    height: (frame.size.height * scale).round().max(2.0) as u32,
                }))
            }
        }
    }

    /// The audio filter: the window's application alone when `application`
    /// and the target is a window, otherwise everything on the system (the
    /// stream configuration leaves Basis's own sound out).
    pub(crate) fn audio_filter(
        &self,
        target: Target,
        application: bool,
    ) -> Result<Retained<AnyObject>, CaptureError> {
        let display = *self
            .displays()
            .first()
            .ok_or_else(|| CaptureError("no display to capture audio through".into()))?;
        let empty = Self::empty_array()?;
        let app = match target {
            Target::Window(id) if application => self.find_window(id).and_then(|w| {
                // SAFETY: w is an SCWindow; its application may be nil.
                let app: *mut AnyObject = unsafe { msg_send![w, owningApplication] };
                (!app.is_null()).then_some(app)
            }),
            _ => None,
        };
        let alloc = Self::filter_alloc()?;
        // SAFETY: every argument is live for the call.
        let filter: Option<Retained<AnyObject>> = unsafe {
            match app {
                Some(app) => {
                    let apps: *mut AnyObject = msg_send![class(c"NSArray")?, arrayWithObject: app];
                    msg_send![alloc, initWithDisplay: display, includingApplications: apps, exceptingWindows: empty]
                }
                None => msg_send![alloc, initWithDisplay: display, excludingWindows: empty],
            }
        };
        filter.ok_or_else(|| CaptureError("ScreenCaptureKit refused the audio filter".into()))
    }
}

pub(crate) struct Settings {
    pub width: u32,
    pub height: u32,
    pub fps: u32,
    pub cursor: bool,
    pub audio: bool,
}

fn configuration(settings: &Settings) -> Result<Retained<AnyObject>, CaptureError> {
    // SAFETY: setters on a fresh SCStreamConfiguration with the argument
    // types its properties declare.
    unsafe {
        let config: Option<Retained<AnyObject>> = msg_send![class(c"SCStreamConfiguration")?, new];
        let config = config.ok_or_else(|| CaptureError("SCStreamConfiguration failed".into()))?;
        let _: () = msg_send![&*config, setWidth: settings.width.max(2) as usize];
        let _: () = msg_send![&*config, setHeight: settings.height.max(2) as usize];
        let _: () = msg_send![&*config, setPixelFormat: PIXEL_420V];
        let interval = CMTime {
            value: 1,
            timescale: settings.fps.max(1) as i32,
            flags: 1,
            epoch: 0,
        };
        let _: () = msg_send![&*config, setMinimumFrameInterval: interval];
        let _: () = msg_send![&*config, setShowsCursor: Bool::new(settings.cursor)];
        let _: () = msg_send![&*config, setQueueDepth: 6isize];
        let _: () = msg_send![&*config, setColorMatrix: MATRIX_709 as *const OpaqueString];
        if settings.audio {
            let _: () = msg_send![&*config, setCapturesAudio: Bool::YES];
            let _: () = msg_send![&*config, setExcludesCurrentProcessAudio: Bool::YES];
            let _: () = msg_send![&*config, setSampleRate: crate::AUDIO_RATE as isize];
            let _: () = msg_send![&*config, setChannelCount: crate::AUDIO_CHANNELS as isize];
        }
        Ok(config)
    }
}

type Handler = Box<dyn FnMut(CMSampleBufferRef) + Send>;

#[derive(Default)]
struct Handlers {
    video: Mutex<Option<Handler>>,
    audio: Mutex<Option<Handler>>,
    stopped: Mutex<Option<String>>,
    closed: AtomicBool,
}

const IVAR: &CStr = c"bmHandlers";

/// # Safety
/// `this` is an instance of the delegate class.
unsafe fn handlers_of(this: &AnyObject) -> *const Handlers {
    let Some(ivar) = this.class().instance_variable(IVAR) else {
        return std::ptr::null();
    };
    // SAFETY: caller contract; the ivar holds a pointer or null.
    unsafe { *ivar.load::<*const c_void>(this) as *const Handlers }
}

unsafe extern "C-unwind" fn on_sample(
    this: &AnyObject,
    _cmd: Sel,
    _stream: *mut AnyObject,
    sample: CMSampleBufferRef,
    kind: isize,
) {
    // SAFETY: the pointer stays valid until the stream has stopped and this
    // queue has drained (see Stream's Drop).
    let Some(handlers) = (unsafe { handlers_of(this).as_ref() }) else {
        return;
    };
    if handlers.closed.load(Ordering::Acquire) || sample.is_null() {
        return;
    }
    let slot = if kind == OUTPUT_AUDIO {
        &handlers.audio
    } else {
        &handlers.video
    };
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if let Some(handler) = slot.lock().unwrap_or_else(|e| e.into_inner()).as_mut() {
            handler(sample);
        }
    }));
}

unsafe extern "C-unwind" fn on_stop(
    this: &AnyObject,
    _cmd: Sel,
    _stream: *mut AnyObject,
    error: *mut AnyObject,
) {
    // SAFETY: as on_sample.
    if let Some(handlers) = unsafe { handlers_of(this).as_ref() } {
        *handlers.stopped.lock().unwrap_or_else(|e| e.into_inner()) = Some(describe(error));
    }
}

fn delegate_class() -> Result<&'static AnyClass, CaptureError> {
    static CLASS: OnceLock<Option<&'static AnyClass>> = OnceLock::new();
    CLASS
        .get_or_init(|| {
            let name = c"BasisMediaCaptureOutput";
            let Some(mut builder) = ClassBuilder::new(name, AnyClass::get(c"NSObject")?) else {
                return AnyClass::get(name);
            };
            builder.add_ivar::<*const c_void>(IVAR);
            // SAFETY: the signatures restate SCStreamOutput's and
            // SCStreamDelegate's methods.
            unsafe {
                builder.add_method(
                    sel!(stream:didOutputSampleBuffer:ofType:),
                    on_sample as unsafe extern "C-unwind" fn(_, _, _, _, _),
                );
                builder.add_method(
                    sel!(stream:didStopWithError:),
                    on_stop as unsafe extern "C-unwind" fn(_, _, _, _),
                );
            }
            for protocol in [c"SCStreamOutput", c"SCStreamDelegate"] {
                if let Some(p) = AnyProtocol::get(protocol) {
                    builder.add_protocol(p);
                }
            }
            Some(builder.register())
        })
        .ok_or_else(|| CaptureError("could not define the capture delegate".into()))
}

extern "C" fn drained(_: *mut c_void) {}

pub(crate) struct Stream {
    stream: Retained<AnyObject>,
    delegate: Retained<AnyObject>,
    handlers: Arc<Handlers>,
    queue: DispatchQueue,
}

impl Stream {
    pub(crate) fn start(
        filter: &AnyObject,
        settings: &Settings,
        video: Option<Handler>,
        audio: Option<Handler>,
    ) -> Result<Self, CaptureError> {
        let config = configuration(settings)?;
        let handlers = Arc::new(Handlers {
            video: Mutex::new(video),
            audio: Mutex::new(audio),
            ..Handlers::default()
        });
        let delegate_class = delegate_class()?;
        // SAFETY: a fresh instance of the class defined above.
        let delegate: Option<Retained<AnyObject>> = unsafe { msg_send![delegate_class, new] };
        let delegate = delegate.ok_or_else(|| CaptureError("capture delegate failed".into()))?;
        let stream_class = class(c"SCStream")?;
        // SAFETY: allocating and initialising an SCStream with live arguments.
        let stream: Option<Retained<AnyObject>> = unsafe {
            let alloc: Allocated<AnyObject> = msg_send![stream_class, alloc];
            msg_send![alloc, initWithFilter: filter, configuration: &*config, delegate: &*delegate]
        };
        let stream = stream.ok_or_else(|| CaptureError("SCStream could not be created".into()))?;
        let this = Self {
            stream,
            delegate,
            handlers,
            // SAFETY: a serial queue owned by this stream, released in Drop.
            queue: unsafe { dispatch_queue_create(c"basis.media.capture".as_ptr(), null_mut()) },
        };
        if let Some(ivar) = delegate_class.instance_variable(IVAR) {
            // SAFETY: the ivar is a pointer slot on the delegate; it takes one
            // strong count of the handlers, given back in Drop.
            unsafe {
                *ivar.load_ptr::<*const c_void>(&this.delegate) =
                    Arc::into_raw(Arc::clone(&this.handlers)).cast();
            }
        }
        let mut kinds = vec![OUTPUT_SCREEN];
        if settings.audio {
            kinds.push(OUTPUT_AUDIO);
        }
        for kind in kinds {
            let mut error: *mut AnyObject = null_mut();
            // SAFETY: the delegate implements the output method; the queue is live.
            let added: Bool = unsafe {
                msg_send![&*this.stream, addStreamOutput: &*this.delegate, type: kind, sampleHandlerQueue: this.queue as *mut AnyObject, error: &mut error]
            };
            if !added.as_bool() {
                this.handlers.closed.store(true, Ordering::Release);
                return Err(CaptureError(describe(error)));
            }
        }
        let slot: Slot<Option<String>> = Arc::default();
        let answer = Arc::clone(&slot);
        let block = RcBlock::new(move |error: *mut AnyObject| {
            fill(&answer, (!error.is_null()).then(|| describe(error)));
        });
        // SAFETY: the completion block is copied by the callee.
        let _: () = unsafe { msg_send![&*this.stream, startCaptureWithCompletionHandler: &*block] };
        if let Some(error) = wait(&slot, "start")? {
            this.handlers.closed.store(true, Ordering::Release);
            return Err(CaptureError(error));
        }
        Ok(this)
    }

    /// Why the stream stopped on its own, if it has.
    pub(crate) fn stopped(&self) -> Option<String> {
        self.handlers
            .stopped
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .clone()
    }
}

impl Drop for Stream {
    fn drop(&mut self) {
        let slot: Slot<()> = Arc::default();
        let answer = Arc::clone(&slot);
        let block = RcBlock::new(move |_error: *mut AnyObject| fill(&answer, ()));
        if !self.handlers.closed.swap(true, Ordering::AcqRel) {
            // SAFETY: the completion block is copied by the callee.
            let _: () =
                unsafe { msg_send![&*self.stream, stopCaptureWithCompletionHandler: &*block] };
            let _ = wait(&slot, "stop");
        }
        *self
            .handlers
            .video
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = None;
        *self
            .handlers
            .audio
            .lock()
            .unwrap_or_else(|e| e.into_inner()) = None;
        // SAFETY: the ivar is cleared before the queue drains, so no callback
        // can read the pointer once its count is given back; the queue is ours.
        unsafe {
            let class = self.delegate.class();
            if let Some(ivar) = class.instance_variable(IVAR) {
                let raw = std::mem::replace(
                    &mut *ivar.load_ptr::<*const c_void>(&self.delegate),
                    std::ptr::null(),
                );
                dispatch_sync_f(self.queue, null_mut(), drained);
                if !raw.is_null() {
                    drop(Arc::from_raw(raw as *const Handlers));
                }
            }
            dispatch_release(self.queue);
        }
    }
}

/// A pixel buffer held past its callback.
pub(crate) struct Pixel(pub CVPixelBufferRef);

// SAFETY: CVPixelBuffers are reference counted and safe to hand between
// threads; this one is only read.
unsafe impl Send for Pixel {}

impl Pixel {
    /// # Safety
    /// `sample` is a live sample buffer.
    pub(crate) unsafe fn from_sample(sample: CMSampleBufferRef) -> Option<Self> {
        // SAFETY: caller contract; the image buffer is retained here.
        unsafe {
            let image = CMSampleBufferGetImageBuffer(sample);
            (!image.is_null()).then(|| Self(CFRetain(image.cast()) as CVPixelBufferRef))
        }
    }

    pub(crate) fn size(&self) -> (u32, u32) {
        // SAFETY: the buffer is retained by self.
        unsafe {
            (
                CVPixelBufferGetWidth(self.0) as u32,
                CVPixelBufferGetHeight(self.0) as u32,
            )
        }
    }
}

impl Drop for Pixel {
    fn drop(&mut self) {
        // SAFETY: retained in from_sample and released once.
        unsafe { CFRelease(self.0.cast()) };
    }
}

/// The sample buffer's audio as interleaved stereo f32.
///
/// # Safety
/// `sample` is a live audio sample buffer.
pub(crate) unsafe fn stereo(sample: CMSampleBufferRef) -> Option<Vec<f32>> {
    // SAFETY: caller contract; the buffer list is sized from CoreMedia's own
    // answer and every sample read stays inside the byte sizes it states.
    unsafe {
        let format = CMSampleBufferGetFormatDescription(sample);
        let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(format).as_ref()?;
        if asbd.format_id != FORMAT_LPCM
            || asbd.format_flags & FLAG_FLOAT == 0
            || asbd.bits_per_channel != 32
        {
            return None;
        }
        let frames = CMSampleBufferGetNumSamples(sample).max(0) as usize;
        let mut needed = 0usize;
        CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sample,
            &mut needed,
            null_mut(),
            0,
            std::ptr::null(),
            std::ptr::null(),
            0,
            null_mut(),
        );
        if needed < 8 + size_of::<AudioBuffer>() {
            return None;
        }
        let mut storage = vec![0u64; needed.div_ceil(8)];
        let mut block: CMBlockBufferRef = null_mut();
        if CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
            sample,
            null_mut(),
            storage.as_mut_ptr().cast(),
            needed,
            std::ptr::null(),
            std::ptr::null(),
            AUDIO_LIST_ALIGN_16,
            &mut block,
        ) != 0
        {
            return None;
        }
        let base = storage.as_ptr().cast::<u8>();
        let count =
            (base.cast::<u32>().read() as usize).min((needed - 8) / size_of::<AudioBuffer>());
        let buffers: Vec<AudioBuffer> = (0..count)
            .map(|i| {
                base.add(8 + i * size_of::<AudioBuffer>())
                    .cast::<AudioBuffer>()
                    .read()
            })
            .collect();
        let channel = |c: usize, frame: usize| -> f32 {
            if asbd.format_flags & FLAG_NON_INTERLEAVED != 0 {
                let b = &buffers[c.min(buffers.len() - 1)];
                let n = b.data_byte_size as usize / 4;
                if frame < n && !b.data.is_null() {
                    b.data.cast::<f32>().add(frame).read()
                } else {
                    0.0
                }
            } else {
                let b = &buffers[0];
                let channels = b.channels.max(1) as usize;
                let at = frame * channels + c.min(channels - 1);
                if at < b.data_byte_size as usize / 4 && !b.data.is_null() {
                    b.data.cast::<f32>().add(at).read()
                } else {
                    0.0
                }
            }
        };
        let mut out = Vec::with_capacity(frames * 2);
        if !buffers.is_empty() {
            for frame in 0..frames {
                out.push(channel(0, frame));
                out.push(channel(1, frame));
            }
        }
        if !block.is_null() {
            CFRelease(block.cast());
        }
        Some(out)
    }
}
