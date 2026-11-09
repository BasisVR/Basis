use std::collections::HashMap;
use std::ffi::c_void;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::atomic::{AtomicI64, AtomicPtr, Ordering};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock, Weak};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use jni::objects::{GlobalRef, JByteBuffer, JClass, JFloatArray, JString, JValue};
use jni::sys::{jboolean, jint, jlong};
use jni::{JNIEnv, JavaVM, NativeMethod};

use crate::pack::Packer;
use crate::{
    CaptureError, CaptureState, EncodedVideo, Shared, SourceInfo, SourceKind, has_parameter_sets,
    parameter_sets,
};

const CLASS: &str = "com/basis/screenshare/BasisAndroidCapture";
const SOURCE: &str = "portal";
const ANSWER: Duration = Duration::from_secs(120);
const FIRST_FRAME: Duration = Duration::from_secs(15);
const FLAG_KEY: jint = 1;
const FLAG_CONFIG: jint = 2;
const DRIFT_US: i64 = 1_000_000;
pub(crate) const UNSUPPORTED: &str = "screenShare.error.unsupportedAndroid";
const NO_ANSWER: &str = "screenShare.error.androidNoAnswer";
const NO_FRAMES: &str = "screenShare.error.androidNoFrames";
const STOPPED: &str = "screenShare.error.androidStopped";

static JAVA_VM: AtomicPtr<c_void> = AtomicPtr::new(std::ptr::null_mut());
static CLASS_REF: Mutex<Option<GlobalRef>> = Mutex::new(None);
static NEXT: AtomicI64 = AtomicI64::new(1);

/// # Safety
/// `vm` must be the process's live `JavaVM*`, as the runtime hands it to
/// `JNI_OnLoad`; it is revived with `JavaVM::from_raw` on every thread that
/// starts a capture.
pub unsafe fn set_java_vm(vm: *mut c_void) {
    JAVA_VM.store(vm, Ordering::Release);
}

struct Session {
    shared: Weak<Shared>,
    opened: Instant,
    started: Option<Instant>,
    headers: Vec<u8>,
    offset: Option<i64>,
    last_pts: i64,
    size: (u32, u32),
    frames: u64,
    packer: Option<Packer>,
    audio_failed: bool,
}

type Entry = Arc<Mutex<Session>>;

fn sessions() -> MutexGuard<'static, HashMap<i64, Entry>> {
    static SESSIONS: OnceLock<Mutex<HashMap<i64, Entry>>> = OnceLock::new();
    SESSIONS
        .get_or_init(|| Mutex::new(HashMap::new()))
        .lock()
        .unwrap_or_else(|e| e.into_inner())
}

fn lock(entry: &Entry) -> MutexGuard<'_, Session> {
    entry.lock().unwrap_or_else(|e| e.into_inner())
}

fn lookup(token: jlong) -> Option<(Entry, Arc<Shared>)> {
    let entry = sessions().get(&token).cloned()?;
    let shared = lock(&entry).shared.upgrade()?;
    Some((entry, shared))
}

fn vm() -> Option<JavaVM> {
    let ptr = JAVA_VM.load(Ordering::Acquire);
    if ptr.is_null() {
        return None;
    }
    // SAFETY: `set_java_vm`'s contract admits only the process's live
    // JavaVM*, so a non-null value here is one.
    unsafe { JavaVM::from_raw(ptr.cast()) }.ok()
}

fn load_with_context<'local>(env: &mut JNIEnv<'local>) -> jni::errors::Result<JClass<'local>> {
    let thread = env
        .call_static_method(
            "java/lang/Thread",
            "currentThread",
            "()Ljava/lang/Thread;",
            &[],
        )?
        .l()?;
    let loader = env
        .call_method(
            &thread,
            "getContextClassLoader",
            "()Ljava/lang/ClassLoader;",
            &[],
        )?
        .l()?;
    let name = env.new_string(CLASS.replace('/', "."))?;
    Ok(env
        .call_method(
            &loader,
            "loadClass",
            "(Ljava/lang/String;)Ljava/lang/Class;",
            &[JValue::Object(&name)],
        )?
        .l()?
        .into())
}

fn natives() -> Vec<NativeMethod> {
    let method = |name: &str, sig: &str, fn_ptr: *mut c_void| NativeMethod {
        name: name.into(),
        sig: sig.into(),
        fn_ptr,
    };
    vec![
        method("nativeControl", "(J)J", native_control as *mut c_void),
        method(
            "nativeStarted",
            "(JIILjava/lang/String;Z)V",
            native_started as *mut c_void,
        ),
        method(
            "nativeFrame",
            "(JLjava/nio/ByteBuffer;IIJI)V",
            native_frame as *mut c_void,
        ),
        method("nativeAudio", "(J[FI)V", native_audio as *mut c_void),
        method(
            "nativeAudioError",
            "(JLjava/lang/String;)V",
            native_audio_error as *mut c_void,
        ),
        method(
            "nativeEnded",
            "(JLjava/lang/String;)V",
            native_ended as *mut c_void,
        ),
    ]
}

fn class(env: &mut JNIEnv<'_>) -> Option<GlobalRef> {
    let mut slot = CLASS_REF.lock().unwrap_or_else(|e| e.into_inner());
    if let Some(class) = slot.as_ref() {
        return Some(class.clone());
    }
    let found = match env.find_class(CLASS) {
        Ok(class) => Ok(class),
        Err(_) => {
            let _ = env.exception_clear();
            load_with_context(env)
        }
    };
    let bound = found.and_then(|class| {
        let global = env.new_global_ref(&class)?;
        env.register_native_methods(&class, &natives())?;
        Ok(global)
    });
    match bound {
        Ok(global) => {
            *slot = Some(global.clone());
            Some(global)
        }
        Err(e) => {
            let _ = env.exception_clear();
            media_diag::diag_warn!("capture: {CLASS} unavailable: {e}");
            None
        }
    }
}

fn with_class<T>(
    f: impl FnOnce(&mut JNIEnv<'_>, &GlobalRef) -> jni::errors::Result<T>,
) -> Option<T> {
    let vm = vm()?;
    let mut env = vm.attach_current_thread().ok()?;
    let class = class(&mut env)?;
    let result = env.with_local_frame(8, |env| f(env, &class));
    if let Err(e) = &result {
        let _ = env.exception_clear();
        media_diag::diag_warn!("capture: java call failed: {e}");
    }
    result.ok()
}

pub fn supported() -> bool {
    with_class(|env, class| env.call_static_method(class, "supported", "()Z", &[])?.z())
        .unwrap_or(false)
}

pub fn sources() -> Vec<SourceInfo> {
    if !supported() {
        return Vec::new();
    }
    let size = with_class(|env, class| {
        env.call_static_method(class, "displaySize", "()J", &[])?
            .j()
    })
    .unwrap_or(0);
    vec![SourceInfo {
        id: SOURCE.into(),
        kind: SourceKind::Monitor,
        name: "Screen".into(),
        width: (size >> 32) as u32,
        height: size as u32,
        primary: true,
    }]
}

pub fn open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    let token = NEXT.fetch_add(1, Ordering::Relaxed);
    let entry = Arc::new(Mutex::new(Session {
        shared: Arc::downgrade(shared),
        opened: Instant::now(),
        started: None,
        headers: Vec::new(),
        offset: None,
        last_pts: -1,
        size: (0, 0),
        frames: 0,
        packer: None,
        audio_failed: false,
    }));
    sessions().insert(token, Arc::clone(&entry));
    let config = &shared.config;
    let int = |v: u32| JValue::Int(v.min(i32::MAX as u32) as jint);
    let started = with_class(|env, class| {
        env.call_static_method(
            class,
            "start",
            "(JIIIIIZ)Z",
            &[
                JValue::Long(token),
                int(config.max_width),
                int(config.max_height),
                int(config.fps),
                int(config.bitrate_kbps),
                int(config.keyframe_interval_ms),
                JValue::Bool(jboolean::from(config.audio)),
            ],
        )?
        .z()
    });
    if started != Some(true) {
        sessions().remove(&token);
        return Err(CaptureError(UNSUPPORTED.into()));
    }
    let watch_shared = Arc::clone(shared);
    std::thread::Builder::new()
        .name("bm-capture-watch".into())
        .spawn(move || watch(watch_shared, token, entry))
        .map(|thread| vec![thread])
        .map_err(|e| {
            sessions().remove(&token);
            CaptureError(format!("spawn capture thread: {e}"))
        })
}

fn watch(shared: Arc<Shared>, token: i64, entry: Entry) {
    let result = catch_unwind(AssertUnwindSafe(|| {
        while !shared.stopped() {
            std::thread::sleep(Duration::from_millis(20));
            let mut session = lock(&entry);
            let failure = match session.started {
                None if session.opened.elapsed() >= ANSWER => Some(NO_ANSWER),
                Some(at) if session.frames == 0 && at.elapsed() >= FIRST_FRAME => Some(NO_FRAMES),
                _ => None,
            };
            if let Some(failure) = failure {
                drop(session);
                shared.fail(failure);
                break;
            }
            if let Some(packer) = session.packer.as_mut()
                && let Err(e) = packer.fill_silence(&shared)
            {
                audio_failed(&mut session, &shared, e.0);
            }
        }
    }));
    sessions().remove(&token);
    if result.is_err() {
        shared.fail("screen capture stopped unexpectedly");
    }
}

fn audio_failed(session: &mut Session, shared: &Shared, detail: String) {
    media_diag::diag_warn!("capture audio: {detail}");
    session.packer = None;
    session.audio_failed = true;
    shared.update(|s| {
        s.audio = false;
        s.audio_error = detail;
    });
}

fn text(env: &mut JNIEnv<'_>, value: &JString<'_>) -> String {
    if value.is_null() {
        return String::new();
    }
    env.get_string(value).map(Into::into).unwrap_or_default()
}

fn guarded<T>(env: &mut JNIEnv<'_>, fallback: T, f: impl FnOnce(&mut JNIEnv<'_>) -> T) -> T {
    let result = catch_unwind(AssertUnwindSafe(|| f(env))).unwrap_or(fallback);
    if env.exception_check().unwrap_or(false) {
        let _ = env.exception_clear();
    }
    result
}

extern "system" fn native_control(mut env: JNIEnv<'_>, _class: JClass<'_>, token: jlong) -> jlong {
    guarded(&mut env, -1, |_| {
        let Some((_, shared)) = lookup(token) else {
            return -1;
        };
        if shared.stopped() {
            return -1;
        }
        let key = shared.keyframe.swap(false, Ordering::AcqRel);
        i64::from(shared.bitrate_kbps.load(Ordering::Acquire)) | (i64::from(key) << 32)
    })
}

extern "system" fn native_started(
    mut env: JNIEnv<'_>,
    _class: JClass<'_>,
    token: jlong,
    width: jint,
    height: jint,
    encoder: JString<'_>,
    hardware: jboolean,
) {
    guarded(&mut env, (), |env| {
        let Some((entry, shared)) = lookup(token) else {
            return;
        };
        let name = text(env, &encoder);
        let size = (width.max(0) as u32, height.max(0) as u32);
        let mut session = lock(&entry);
        session.started = Some(Instant::now());
        session.size = size;
        if shared.config.audio && session.packer.is_none() && !session.audio_failed {
            match Packer::new(&shared) {
                Ok(packer) => session.packer = Some(packer),
                Err(e) => audio_failed(&mut session, &shared, e.0),
            }
        }
        drop(session);
        shared.update(|s| {
            s.state = CaptureState::Running;
            s.width = size.0;
            s.height = size.1;
            s.encoder = name;
            s.hardware = hardware != 0;
        });
    });
}

#[allow(clippy::too_many_arguments)]
extern "system" fn native_frame(
    mut env: JNIEnv<'_>,
    _class: JClass<'_>,
    token: jlong,
    buffer: JByteBuffer<'_>,
    offset: jint,
    size: jint,
    pts_us: jlong,
    flags: jint,
) {
    guarded(&mut env, (), |env| {
        let Some((entry, shared)) = lookup(token) else {
            return;
        };
        if buffer.is_null() || offset < 0 || size <= 0 {
            return;
        }
        let (Ok(address), Ok(capacity)) = (
            env.get_direct_buffer_address(&buffer),
            env.get_direct_buffer_capacity(&buffer),
        ) else {
            return;
        };
        let (offset, size) = (offset as usize, size as usize);
        if address.is_null() || offset.saturating_add(size) > capacity {
            return;
        }
        // SAFETY: the direct buffer's address and capacity come from the VM,
        // the range was checked against that capacity, and Java holds the
        // codec's output buffer until this call returns.
        let bytes = unsafe { std::slice::from_raw_parts(address.add(offset), size) };
        let mut session = lock(&entry);
        if flags & FLAG_CONFIG != 0 {
            session.headers = parameter_sets(bytes);
            return;
        }
        let key = flags & FLAG_KEY != 0;
        let data = if key && !has_parameter_sets(bytes) && !session.headers.is_empty() {
            [session.headers.as_slice(), bytes].concat()
        } else {
            bytes.to_vec()
        };
        let now = shared.now_us();
        let mut shift = *session.offset.get_or_insert(now - pts_us);
        if (pts_us + shift - now).abs() > DRIFT_US {
            shift = now - pts_us;
            session.offset = Some(shift);
        }
        let mut pts = pts_us + shift;
        if pts <= session.last_pts {
            pts = session.last_pts + 1;
        }
        session.last_pts = pts;
        session.frames += 1;
        let (width, height) = session.size;
        drop(session);
        shared.captured.fetch_add(1, Ordering::Relaxed);
        shared.encoded.fetch_add(1, Ordering::Relaxed);
        shared.push_video(EncodedVideo {
            data,
            pts_us: pts,
            key,
            width,
            height,
        });
    });
}

extern "system" fn native_audio(
    mut env: JNIEnv<'_>,
    _class: JClass<'_>,
    token: jlong,
    samples: JFloatArray<'_>,
    count: jint,
) {
    guarded(&mut env, (), |env| {
        let Some((entry, shared)) = lookup(token) else {
            return;
        };
        if samples.is_null() || count <= 0 {
            return;
        }
        let length = env.get_array_length(&samples).unwrap_or(0);
        let count = count.min(length).max(0) as usize & !1;
        let mut pcm = vec![0f32; count];
        if count == 0 || env.get_float_array_region(&samples, 0, &mut pcm).is_err() {
            return;
        }
        let mut session = lock(&entry);
        if session.audio_failed || shared.stopped() {
            return;
        }
        if let Some(packer) = session.packer.as_mut()
            && let Err(e) = packer.push(&shared, &pcm)
        {
            audio_failed(&mut session, &shared, e.0);
        }
    });
}

extern "system" fn native_audio_error(
    mut env: JNIEnv<'_>,
    _class: JClass<'_>,
    token: jlong,
    detail: JString<'_>,
) {
    guarded(&mut env, (), |env| {
        let Some((entry, shared)) = lookup(token) else {
            return;
        };
        let detail = text(env, &detail);
        audio_failed(&mut lock(&entry), &shared, detail);
    });
}

extern "system" fn native_ended(
    mut env: JNIEnv<'_>,
    _class: JClass<'_>,
    token: jlong,
    detail: JString<'_>,
) {
    guarded(&mut env, (), |env| {
        let Some((_, shared)) = lookup(token) else {
            return;
        };
        let detail = text(env, &detail);
        if !shared.stopped() {
            shared.fail(if detail.is_empty() {
                STOPPED.to_string()
            } else {
                detail
            });
        }
    });
}
