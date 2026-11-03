use std::ffi::{CStr, c_char, c_int, c_void};
use std::os::fd::{IntoRawFd, OwnedFd};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr::{null, null_mut};
use std::sync::atomic::{AtomicI32, AtomicPtr, AtomicU32, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

use libloading::Library;

use super::portal::PortalCast;
use crate::CaptureError;
use crate::spa_pod::{
    self, AUDIO_CHANNEL_FL, AUDIO_CHANNEL_FR, CHUNK_FLAG_CORRUPTED, DATA_MEM_FD, DATA_MEM_PTR,
    PARAM_FORMAT, VIDEO_FORMAT_BGRA, VIDEO_FORMAT_BGRX, VIDEO_FORMAT_RGBA, VIDEO_FORMAT_RGBX,
    VideoInfo,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum PixelFormat {
    Bgrx,
    Rgbx,
    Bgra,
    Rgba,
}

pub(crate) struct VideoFrame<'a> {
    pub width: u32,
    pub height: u32,
    pub stride: u32,
    pub format: PixelFormat,
    pub data: &'a [u8],
}

const PW_ID_ANY: u32 = u32::MAX;
const PW_DIRECTION_INPUT: c_int = 0;
const PW_STREAM_FLAG_AUTOCONNECT: c_int = 1 << 0;
const PW_STREAM_FLAG_MAP_BUFFERS: c_int = 1 << 2;
const PW_STREAM_STATE_ERROR: c_int = -1;
const PW_STREAM_STATE_UNCONNECTED: c_int = 0;
const PW_STREAM_STATE_PAUSED: c_int = 2;
const PW_VERSION_STREAM_EVENTS: u32 = 2;
const CONNECT_WAIT: Duration = Duration::from_secs(5);

#[repr(C)]
struct SpaPod {
    size: u32,
    type_: u32,
}

#[repr(C)]
struct SpaList {
    next: *mut SpaList,
    prev: *mut SpaList,
}

#[repr(C)]
struct SpaHook {
    link: SpaList,
    funcs: *const c_void,
    data: *mut c_void,
    removed: Option<unsafe extern "C" fn(*mut SpaHook)>,
    private: *mut c_void,
}

#[repr(C)]
struct SpaChunk {
    offset: u32,
    size: u32,
    stride: i32,
    flags: i32,
}

#[repr(C)]
struct SpaData {
    type_: u32,
    flags: u32,
    fd: i64,
    mapoffset: u32,
    maxsize: u32,
    data: *mut c_void,
    chunk: *mut SpaChunk,
}

#[repr(C)]
struct SpaBuffer {
    n_metas: u32,
    n_datas: u32,
    metas: *mut c_void,
    datas: *mut SpaData,
}

#[repr(C)]
struct PwBuffer {
    buffer: *mut SpaBuffer,
    user_data: *mut c_void,
    size: u64,
    requested: u64,
}

#[repr(C)]
struct PwStreamEvents {
    version: u32,
    destroy: Option<unsafe extern "C" fn(*mut c_void)>,
    state_changed: Option<unsafe extern "C" fn(*mut c_void, c_int, c_int, *const c_char)>,
    control_info: Option<unsafe extern "C" fn(*mut c_void, u32, *const c_void)>,
    io_changed: Option<unsafe extern "C" fn(*mut c_void, u32, *mut c_void, u32)>,
    param_changed: Option<unsafe extern "C" fn(*mut c_void, u32, *const SpaPod)>,
    add_buffer: Option<unsafe extern "C" fn(*mut c_void, *mut PwBuffer)>,
    remove_buffer: Option<unsafe extern "C" fn(*mut c_void, *mut PwBuffer)>,
    process: Option<unsafe extern "C" fn(*mut c_void)>,
    drained: Option<unsafe extern "C" fn(*mut c_void)>,
    command: Option<unsafe extern "C" fn(*mut c_void, *const c_void)>,
    trigger_done: Option<unsafe extern "C" fn(*mut c_void)>,
}

#[cfg(target_pointer_width = "64")]
const _: () = {
    assert!(size_of::<SpaPod>() == 8);
    assert!(size_of::<SpaHook>() == 48);
    assert!(size_of::<SpaChunk>() == 16);
    assert!(size_of::<SpaData>() == 40);
    assert!(std::mem::offset_of!(SpaData, fd) == 8);
    assert!(std::mem::offset_of!(SpaData, mapoffset) == 16);
    assert!(std::mem::offset_of!(SpaData, maxsize) == 20);
    assert!(std::mem::offset_of!(SpaData, data) == 24);
    assert!(std::mem::offset_of!(SpaData, chunk) == 32);
    assert!(size_of::<SpaBuffer>() == 24);
    assert!(std::mem::offset_of!(SpaBuffer, datas) == 16);
    assert!(std::mem::offset_of!(PwBuffer, buffer) == 0);
    assert!(size_of::<PwStreamEvents>() == 96);
    assert!(std::mem::offset_of!(PwStreamEvents, param_changed) == 40);
    assert!(std::mem::offset_of!(PwStreamEvents, process) == 64);
    assert!(std::mem::offset_of!(PwStreamEvents, trigger_done) == 88);
};

type Raw = *mut c_void;
type FrameSink = Box<dyn FnMut(VideoFrame<'_>) + Send>;
type SampleSink = Box<dyn FnMut(&[f32]) + Send>;

struct Api {
    init: unsafe extern "C" fn(*mut c_int, *mut *mut *mut c_char),
    thread_loop_new: unsafe extern "C" fn(*const c_char, *const c_void) -> Raw,
    thread_loop_get_loop: unsafe extern "C" fn(Raw) -> Raw,
    thread_loop_start: unsafe extern "C" fn(Raw) -> c_int,
    thread_loop_stop: unsafe extern "C" fn(Raw),
    thread_loop_destroy: unsafe extern "C" fn(Raw),
    thread_loop_lock: unsafe extern "C" fn(Raw),
    thread_loop_unlock: unsafe extern "C" fn(Raw),
    context_new: unsafe extern "C" fn(Raw, Raw, usize) -> Raw,
    context_destroy: unsafe extern "C" fn(Raw),
    context_connect: unsafe extern "C" fn(Raw, Raw, usize) -> Raw,
    context_connect_fd: unsafe extern "C" fn(Raw, c_int, Raw, usize) -> Raw,
    core_disconnect: unsafe extern "C" fn(Raw) -> c_int,
    properties_new: unsafe extern "C" fn(*const c_char, ...) -> Raw,
    properties_set: unsafe extern "C" fn(Raw, *const c_char, *const c_char) -> c_int,
    stream_new: unsafe extern "C" fn(Raw, *const c_char, Raw) -> Raw,
    stream_add_listener: unsafe extern "C" fn(Raw, *mut SpaHook, *const PwStreamEvents, Raw),
    stream_connect: unsafe extern "C" fn(Raw, c_int, u32, c_int, *mut *const SpaPod, u32) -> c_int,
    stream_disconnect: unsafe extern "C" fn(Raw) -> c_int,
    stream_destroy: unsafe extern "C" fn(Raw),
    stream_update_params: unsafe extern "C" fn(Raw, *mut *const SpaPod, u32) -> c_int,
    stream_dequeue_buffer: unsafe extern "C" fn(Raw) -> *mut PwBuffer,
    stream_queue_buffer: unsafe extern "C" fn(Raw, *mut PwBuffer) -> c_int,
    _lib: Library,
}

unsafe fn sym<T: Copy>(lib: &Library, name: &str) -> Result<T, String> {
    let symbol = format!("{name}\0");
    // SAFETY: the caller names T as the C signature of `name` in pipewire 0.3's headers.
    unsafe { lib.get::<T>(symbol.as_bytes()) }
        .map(|s| *s)
        .map_err(|e| format!("libpipewire-0.3 is missing {name}: {e}"))
}

fn load() -> Result<Api, String> {
    // SAFETY: loading libpipewire runs only its own library constructors.
    let lib = unsafe { Library::new("libpipewire-0.3.so.0") }
        .map_err(|e| format!("PipeWire is not available (libpipewire-0.3.so.0): {e}"))?;
    // SAFETY: every signature below is copied from pipewire 0.3's public headers, and
    // the library stays loaded for as long as the Api, which lives in a static.
    let api = unsafe {
        Api {
            init: sym(&lib, "pw_init")?,
            thread_loop_new: sym(&lib, "pw_thread_loop_new")?,
            thread_loop_get_loop: sym(&lib, "pw_thread_loop_get_loop")?,
            thread_loop_start: sym(&lib, "pw_thread_loop_start")?,
            thread_loop_stop: sym(&lib, "pw_thread_loop_stop")?,
            thread_loop_destroy: sym(&lib, "pw_thread_loop_destroy")?,
            thread_loop_lock: sym(&lib, "pw_thread_loop_lock")?,
            thread_loop_unlock: sym(&lib, "pw_thread_loop_unlock")?,
            context_new: sym(&lib, "pw_context_new")?,
            context_destroy: sym(&lib, "pw_context_destroy")?,
            context_connect: sym(&lib, "pw_context_connect")?,
            context_connect_fd: sym(&lib, "pw_context_connect_fd")?,
            core_disconnect: sym(&lib, "pw_core_disconnect")?,
            properties_new: sym(&lib, "pw_properties_new")?,
            properties_set: sym(&lib, "pw_properties_set")?,
            stream_new: sym(&lib, "pw_stream_new")?,
            stream_add_listener: sym(&lib, "pw_stream_add_listener")?,
            stream_connect: sym(&lib, "pw_stream_connect")?,
            stream_disconnect: sym(&lib, "pw_stream_disconnect")?,
            stream_destroy: sym(&lib, "pw_stream_destroy")?,
            stream_update_params: sym(&lib, "pw_stream_update_params")?,
            stream_dequeue_buffer: sym(&lib, "pw_stream_dequeue_buffer")?,
            stream_queue_buffer: sym(&lib, "pw_stream_queue_buffer")?,
            _lib: lib,
        }
    };
    // SAFETY: pw_init accepts null argc/argv and is reference counted, so one call is enough.
    unsafe { (api.init)(null_mut(), null_mut()) };
    Ok(api)
}

fn api() -> Result<&'static Api, CaptureError> {
    static API: OnceLock<Result<Api, String>> = OnceLock::new();
    API.get_or_init(load)
        .as_ref()
        .map_err(|e| CaptureError(e.clone()))
}

fn aligned(bytes: &[u8]) -> Vec<u64> {
    bytes
        .chunks(8)
        .map(|c| {
            let mut word = [0u8; 8];
            word[..c.len()].copy_from_slice(c);
            u64::from_ne_bytes(word)
        })
        .collect()
}

fn pixel_format(raw: u32) -> Option<PixelFormat> {
    match raw {
        VIDEO_FORMAT_BGRX => Some(PixelFormat::Bgrx),
        VIDEO_FORMAT_RGBX => Some(PixelFormat::Rgbx),
        VIDEO_FORMAT_BGRA => Some(PixelFormat::Bgra),
        VIDEO_FORMAT_RGBA => Some(PixelFormat::Rgba),
        _ => None,
    }
}

struct Common {
    api: &'static Api,
    stream: AtomicPtr<c_void>,
    state: AtomicI32,
    error: Mutex<String>,
}

impl Common {
    fn new(api: &'static Api) -> Self {
        Self {
            api,
            stream: AtomicPtr::new(null_mut()),
            state: AtomicI32::new(PW_STREAM_STATE_UNCONNECTED),
            error: Mutex::new(String::new()),
        }
    }
}

trait Listener: Send + 'static {
    fn common(&self) -> &Common;
}

unsafe extern "C" fn on_state_changed<T: Listener>(
    data: *mut c_void,
    _old: c_int,
    state: c_int,
    error: *const c_char,
) {
    // SAFETY: data is the listener registered with this stream, freed only after the stream.
    let common = unsafe { &*data.cast::<T>() }.common();
    if state == PW_STREAM_STATE_ERROR {
        let message = if error.is_null() {
            "unknown error".to_string()
        } else {
            // SAFETY: PipeWire passes a NUL-terminated message valid for this call.
            unsafe { CStr::from_ptr(error) }
                .to_string_lossy()
                .into_owned()
        };
        *common.error.lock().unwrap_or_else(|e| e.into_inner()) = message;
    }
    common.state.store(state, Ordering::Release);
}

unsafe fn pod_bytes<'a>(pod: *const SpaPod) -> &'a [u8] {
    // SAFETY: the caller passes a valid pod, whose header and `size` body bytes are readable.
    unsafe { std::slice::from_raw_parts(pod.cast::<u8>(), 8 + (*pod).size as usize) }
}

unsafe fn chunk_bytes<'a>(buffer: *mut PwBuffer) -> Option<(&'a [u8], i32)> {
    // SAFETY: the caller passes a dequeued pw_buffer; PipeWire keeps its spa_buffer, data
    // and chunk alive and mapped (MAP_BUFFERS) until the buffer is queued again.
    unsafe {
        let spa = (*buffer).buffer;
        if spa.is_null() || (*spa).n_datas == 0 || (*spa).datas.is_null() {
            return None;
        }
        let data = &*(*spa).datas;
        if data.data.is_null() || data.chunk.is_null() {
            return None;
        }
        let chunk = &*data.chunk;
        if chunk.flags & CHUNK_FLAG_CORRUPTED != 0 {
            return None;
        }
        let (offset, size) = spa_pod::chunk_region(data.maxsize, chunk.offset, chunk.size)?;
        let bytes = std::slice::from_raw_parts(data.data.cast::<u8>().add(offset), size);
        Some((bytes, chunk.stride))
    }
}

struct Connection<T: Listener> {
    api: &'static Api,
    thread_loop: Raw,
    context: Raw,
    core: Raw,
    stream: Raw,
    hook: *mut SpaHook,
    data: *mut T,
}

struct Setup<'a> {
    fd: Option<OwnedFd>,
    name: &'a CStr,
    props: &'a [(&'a CStr, &'a CStr)],
    events: &'static PwStreamEvents,
    target: u32,
    params: &'a [Vec<u64>],
}

impl<T: Listener> Connection<T> {
    fn open(api: &'static Api, data: T, setup: Setup<'_>) -> Result<Self, CaptureError> {
        let hook = Box::new(SpaHook {
            link: SpaList {
                next: null_mut(),
                prev: null_mut(),
            },
            funcs: null(),
            data: null_mut(),
            removed: None,
            private: null_mut(),
        });
        let mut conn = Self {
            api,
            thread_loop: null_mut(),
            context: null_mut(),
            core: null_mut(),
            stream: null_mut(),
            hook: Box::into_raw(hook),
            data: Box::into_raw(Box::new(data)),
        };
        // SAFETY: thread_loop_new copies the name; a null property dict is allowed.
        conn.thread_loop = unsafe { (api.thread_loop_new)(c"bm-pipewire".as_ptr(), null()) };
        if conn.thread_loop.is_null() {
            return Err(CaptureError(
                "PipeWire thread loop could not be created".into(),
            ));
        }
        // SAFETY: the loop belongs to the live thread loop; null properties are allowed.
        conn.context = unsafe {
            (api.context_new)((api.thread_loop_get_loop)(conn.thread_loop), null_mut(), 0)
        };
        if conn.context.is_null() {
            return Err(CaptureError("PipeWire context could not be created".into()));
        }
        // SAFETY: the thread loop is valid and not yet running.
        if unsafe { (api.thread_loop_start)(conn.thread_loop) } < 0 {
            return Err(CaptureError("PipeWire thread loop could not start".into()));
        }
        // SAFETY: the thread loop is running; the lock is released below on every path.
        unsafe { (api.thread_loop_lock)(conn.thread_loop) };
        let result = conn.connect_locked(setup);
        // SAFETY: matches the lock taken above.
        unsafe { (api.thread_loop_unlock)(conn.thread_loop) };
        result?;
        let common = conn.data().common();
        let deadline = Instant::now() + CONNECT_WAIT;
        loop {
            let state = common.state.load(Ordering::Acquire);
            if state == PW_STREAM_STATE_ERROR {
                let error = common.error.lock().unwrap_or_else(|e| e.into_inner());
                return Err(CaptureError(format!("PipeWire stream failed: {error}")));
            }
            if state >= PW_STREAM_STATE_PAUSED || Instant::now() >= deadline {
                return Ok(conn);
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }

    fn connect_locked(&mut self, setup: Setup<'_>) -> Result<(), CaptureError> {
        let api = self.api;
        self.core = match setup.fd {
            // SAFETY: the context is valid and the loop lock is held; connect_fd takes
            // ownership of the duplicated descriptor.
            Some(fd) => unsafe {
                (api.context_connect_fd)(self.context, fd.into_raw_fd(), null_mut(), 0)
            },
            // SAFETY: the context is valid and the loop lock is held.
            None => unsafe { (api.context_connect)(self.context, null_mut(), 0) },
        };
        if self.core.is_null() {
            return Err(CaptureError(format!(
                "could not connect to PipeWire: {}",
                std::io::Error::last_os_error()
            )));
        }
        // SAFETY: a null first key ends the variadic list, giving empty properties.
        let props = unsafe { (api.properties_new)(null()) };
        if props.is_null() {
            return Err(CaptureError(
                "PipeWire properties could not be created".into(),
            ));
        }
        for (key, value) in setup.props {
            // SAFETY: props is valid and the strings are NUL-terminated; PipeWire copies them.
            unsafe { (api.properties_set)(props, key.as_ptr(), value.as_ptr()) };
        }
        // SAFETY: core is connected and the lock is held; stream_new takes ownership of props.
        self.stream = unsafe { (api.stream_new)(self.core, setup.name.as_ptr(), props) };
        if self.stream.is_null() {
            return Err(CaptureError("PipeWire stream could not be created".into()));
        }
        // SAFETY: data was leaked from a Box and is freed only in Drop, after the stream.
        unsafe { &*self.data }
            .common()
            .stream
            .store(self.stream, Ordering::Release);
        // SAFETY: the hook, events and data all outlive the stream, which Drop destroys first.
        unsafe {
            (api.stream_add_listener)(self.stream, self.hook, setup.events, self.data.cast())
        };
        let mut pods: Vec<*const SpaPod> = setup
            .params
            .iter()
            .map(|p| p.as_ptr().cast::<SpaPod>())
            .collect();
        // SAFETY: each pod is a complete, 8-byte aligned SPA pod that PipeWire copies here.
        let res = unsafe {
            (api.stream_connect)(
                self.stream,
                PW_DIRECTION_INPUT,
                setup.target,
                PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
                pods.as_mut_ptr(),
                pods.len() as u32,
            )
        };
        if res < 0 {
            return Err(CaptureError(format!(
                "PipeWire stream could not connect: {}",
                std::io::Error::from_raw_os_error(-res)
            )));
        }
        Ok(())
    }

    fn data(&self) -> &T {
        // SAFETY: data stays allocated until Drop and is only ever shared immutably.
        unsafe { &*self.data }
    }
}

impl<T: Listener> Drop for Connection<T> {
    fn drop(&mut self) {
        let api = self.api;
        // SAFETY: each handle is either null or still owned here; the stream is destroyed under
        // the loop lock so no callback can run, the loop thread is joined before the core and
        // context go, and the hook and data are freed last because the stream referenced them.
        unsafe {
            if !self.thread_loop.is_null() {
                (api.thread_loop_lock)(self.thread_loop);
                if !self.stream.is_null() {
                    (api.stream_disconnect)(self.stream);
                    (api.stream_destroy)(self.stream);
                }
                (api.thread_loop_unlock)(self.thread_loop);
                (api.thread_loop_stop)(self.thread_loop);
                if !self.core.is_null() {
                    (api.core_disconnect)(self.core);
                }
                if !self.context.is_null() {
                    (api.context_destroy)(self.context);
                }
                (api.thread_loop_destroy)(self.thread_loop);
            }
            drop(Box::from_raw(self.hook));
            drop(Box::from_raw(self.data));
        }
    }
}

struct VideoData {
    common: Common,
    format: Mutex<Option<(VideoInfo, PixelFormat)>>,
    on_frame: Mutex<FrameSink>,
}

impl Listener for VideoData {
    fn common(&self) -> &Common {
        &self.common
    }
}

unsafe extern "C" fn video_param_changed(data: *mut c_void, id: u32, param: *const SpaPod) {
    if id != PARAM_FORMAT {
        return;
    }
    // SAFETY: data is the VideoData registered with this stream.
    let video = unsafe { &*data.cast::<VideoData>() };
    let info = if param.is_null() {
        None
    } else {
        // SAFETY: PipeWire passes a valid pod for the duration of the callback.
        spa_pod::parse_video_format(unsafe { pod_bytes(param) })
    };
    let info = info.and_then(|i| Some((i, pixel_format(i.format)?)));
    *video.format.lock().unwrap_or_else(|e| e.into_inner()) = info;
    if info.is_none() {
        return;
    }
    let buffers = aligned(&spa_pod::video_buffers(
        (1 << DATA_MEM_PTR) | (1 << DATA_MEM_FD),
    ));
    let mut params = [buffers.as_ptr().cast::<SpaPod>()];
    let stream = video.common.stream.load(Ordering::Acquire);
    // SAFETY: called on the loop thread for this live stream; the pod is copied by PipeWire.
    unsafe { (video.common.api.stream_update_params)(stream, params.as_mut_ptr(), 1) };
}

unsafe extern "C" fn video_process(data: *mut c_void) {
    // SAFETY: data is the VideoData registered with this stream.
    let video = unsafe { &*data.cast::<VideoData>() };
    let api = video.common.api;
    let stream = video.common.stream.load(Ordering::Acquire);
    // SAFETY: process runs on the loop thread while the stream is alive.
    let mut buffer = unsafe { (api.stream_dequeue_buffer)(stream) };
    if buffer.is_null() {
        return;
    }
    loop {
        // SAFETY: as above; older buffers go back so only the newest frame is delivered.
        let next = unsafe { (api.stream_dequeue_buffer)(stream) };
        if next.is_null() {
            break;
        }
        // SAFETY: buffer was dequeued from this stream and is returned exactly once.
        unsafe { (api.stream_queue_buffer)(stream, buffer) };
        buffer = next;
    }
    let format = *video.format.lock().unwrap_or_else(|e| e.into_inner());
    // SAFETY: buffer is dequeued and stays valid until queued below.
    if let (Some((info, format)), Some((bytes, stride))) = (format, unsafe { chunk_bytes(buffer) })
        && let Some(stride) = spa_pod::frame_stride(info.width, info.height, stride, bytes.len())
    {
        let frame = VideoFrame {
            width: info.width,
            height: info.height,
            stride,
            format,
            data: bytes,
        };
        let mut on_frame = video.on_frame.lock().unwrap_or_else(|e| e.into_inner());
        let _ = catch_unwind(AssertUnwindSafe(|| on_frame(frame)));
    }
    // SAFETY: buffer was dequeued from this stream and is returned exactly once.
    unsafe { (api.stream_queue_buffer)(stream, buffer) };
}

static VIDEO_EVENTS: PwStreamEvents = PwStreamEvents {
    version: PW_VERSION_STREAM_EVENTS,
    destroy: None,
    state_changed: Some(on_state_changed::<VideoData>),
    control_info: None,
    io_changed: None,
    param_changed: Some(video_param_changed),
    add_buffer: None,
    remove_buffer: None,
    process: Some(video_process),
    drained: None,
    command: None,
    trigger_done: None,
};

pub(crate) struct VideoStream {
    _conn: Connection<VideoData>,
}

// SAFETY: the raw PipeWire handles are only touched in Drop, under the thread loop lock or
// after the loop thread has stopped, and the listener data is Send.
unsafe impl Send for VideoStream {}

impl VideoStream {
    pub(crate) fn open(
        cast: &PortalCast,
        fps: u32,
        on_frame: Box<dyn FnMut(VideoFrame<'_>) + Send>,
    ) -> Result<Self, CaptureError> {
        let api = api()?;
        let fd = cast
            .fd
            .try_clone()
            .map_err(|e| CaptureError(format!("duplicate the PipeWire remote: {e}")))?;
        let params = [aligned(&spa_pod::video_enum_format(
            &[
                VIDEO_FORMAT_BGRX,
                VIDEO_FORMAT_RGBX,
                VIDEO_FORMAT_BGRA,
                VIDEO_FORMAT_RGBA,
            ],
            (cast.width, cast.height),
            fps,
        ))];
        let data = VideoData {
            common: Common::new(api),
            format: Mutex::new(None),
            on_frame: Mutex::new(on_frame),
        };
        let conn = Connection::open(
            api,
            data,
            Setup {
                fd: Some(fd),
                name: c"basis-screen-capture",
                props: &[
                    (c"media.type", c"Video"),
                    (c"media.category", c"Capture"),
                    (c"media.role", c"Screen"),
                ],
                events: &VIDEO_EVENTS,
                target: cast.node_id,
                params: &params,
            },
        )?;
        Ok(Self { _conn: conn })
    }
}

struct AudioData {
    common: Common,
    channels: AtomicU32,
    scratch: Mutex<Vec<f32>>,
    on_samples: Mutex<SampleSink>,
}

impl Listener for AudioData {
    fn common(&self) -> &Common {
        &self.common
    }
}

unsafe extern "C" fn audio_param_changed(data: *mut c_void, id: u32, param: *const SpaPod) {
    if id != PARAM_FORMAT || param.is_null() {
        return;
    }
    // SAFETY: data is the AudioData registered with this stream.
    let audio = unsafe { &*data.cast::<AudioData>() };
    // SAFETY: PipeWire passes a valid pod for the duration of the callback.
    if let Some(info) = spa_pod::parse_audio_format(unsafe { pod_bytes(param) })
        && info.channels > 0
    {
        audio.channels.store(info.channels, Ordering::Release);
    }
}

unsafe extern "C" fn audio_process(data: *mut c_void) {
    // SAFETY: data is the AudioData registered with this stream.
    let audio = unsafe { &*data.cast::<AudioData>() };
    let api = audio.common.api;
    let stream = audio.common.stream.load(Ordering::Acquire);
    let channels = audio.channels.load(Ordering::Acquire).max(1) as usize;
    loop {
        // SAFETY: process runs on the loop thread while the stream is alive.
        let buffer = unsafe { (api.stream_dequeue_buffer)(stream) };
        if buffer.is_null() {
            return;
        }
        // SAFETY: buffer is dequeued and stays valid until queued below.
        if let Some((bytes, _)) = unsafe { chunk_bytes(buffer) } {
            let mut scratch = audio.scratch.lock().unwrap_or_else(|e| e.into_inner());
            scratch.clear();
            scratch.extend(
                bytes
                    .as_chunks::<4>()
                    .0
                    .iter()
                    .map(|b| f32::from_ne_bytes(*b)),
            );
            let whole = scratch.len() - scratch.len() % channels;
            if whole > 0 {
                let mut on_samples = audio.on_samples.lock().unwrap_or_else(|e| e.into_inner());
                let _ = catch_unwind(AssertUnwindSafe(|| on_samples(&scratch[..whole])));
            }
        }
        // SAFETY: buffer was dequeued from this stream and is returned exactly once.
        unsafe { (api.stream_queue_buffer)(stream, buffer) };
    }
}

static AUDIO_EVENTS: PwStreamEvents = PwStreamEvents {
    version: PW_VERSION_STREAM_EVENTS,
    destroy: None,
    state_changed: Some(on_state_changed::<AudioData>),
    control_info: None,
    io_changed: None,
    param_changed: Some(audio_param_changed),
    add_buffer: None,
    remove_buffer: None,
    process: Some(audio_process),
    drained: None,
    command: None,
    trigger_done: None,
};

pub(crate) struct AudioStream {
    _conn: Connection<AudioData>,
}

// SAFETY: the raw PipeWire handles are only touched in Drop, under the thread loop lock or
// after the loop thread has stopped, and the listener data is Send.
unsafe impl Send for AudioStream {}

impl AudioStream {
    pub(crate) fn open(on_samples: SampleSink) -> Result<Self, CaptureError> {
        let api = api()?;
        let params = [aligned(&spa_pod::audio_enum_format(
            crate::AUDIO_RATE,
            &[AUDIO_CHANNEL_FL, AUDIO_CHANNEL_FR],
        ))];
        let data = AudioData {
            common: Common::new(api),
            channels: AtomicU32::new(crate::AUDIO_CHANNELS),
            scratch: Mutex::new(Vec::new()),
            on_samples: Mutex::new(on_samples),
        };
        let conn = Connection::open(
            api,
            data,
            Setup {
                fd: None,
                name: c"basis-audio-capture",
                props: &[
                    (c"media.type", c"Audio"),
                    (c"media.category", c"Capture"),
                    (c"media.role", c"Music"),
                    (c"stream.capture.sink", c"true"),
                ],
                events: &AUDIO_EVENTS,
                target: PW_ID_ANY,
                params: &params,
            },
        )?;
        Ok(Self { _conn: conn })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn aligned_pods_keep_their_bytes() {
        let pod = spa_pod::video_buffers(6);
        let words = aligned(&pod);
        assert_eq!(words.len() * 8, pod.len());
        let back: Vec<u8> = words.iter().flat_map(|w| w.to_ne_bytes()).collect();
        assert_eq!(back, pod);
        assert_eq!(aligned(&[1, 2, 3]).len(), 1);
    }

    #[test]
    fn pixel_formats_map() {
        assert_eq!(pixel_format(VIDEO_FORMAT_BGRX), Some(PixelFormat::Bgrx));
        assert_eq!(pixel_format(VIDEO_FORMAT_RGBA), Some(PixelFormat::Rgba));
        assert_eq!(pixel_format(2), None);
    }
}
