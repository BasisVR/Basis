use std::ffi::{CStr, CString, c_char, c_void};
use std::hash::{BuildHasher, Hasher};
use std::io::{BufWriter, Read};
use std::net::{Shutdown, TcpListener, TcpStream};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::atomic::Ordering;
use std::sync::{Arc, OnceLock};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress};
use windows::Win32::System::Memory::{GetProcessHeap, HEAP_FLAGS, HeapFree};
use windows::core::{PCSTR, PCWSTR, w};

use crate::relay::{self, TOKEN_LEN};
use crate::{CaptureError, CaptureState, Shared, SourceInfo, SourceKind};

const HELPER: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/capture-helper.bin"));
const LOADER: &str = "/lib64/ld-linux-x86-64.so.2";
const CONNECT: Duration = Duration::from_secs(15);
const POLL: Duration = Duration::from_millis(20);
const PORTAL_SOURCE: &str = "portal";

type UnixName = unsafe extern "C" fn(*const u16) -> *mut c_char;
type DosName = unsafe extern "C" fn(*const c_char) -> *mut u16;
type Spawn = unsafe extern "C" fn(*const *const c_char, i32) -> i32;
type Export = unsafe extern "system" fn() -> isize;

fn export(module: PCWSTR, name: &CStr) -> Option<Export> {
    // SAFETY: module and name are valid NUL-terminated strings.
    unsafe {
        let handle = GetModuleHandleW(module).ok()?;
        GetProcAddress(handle, PCSTR(name.as_ptr().cast()))
    }
}

pub fn detected() -> bool {
    static WINE: OnceLock<bool> = OnceLock::new();
    *WINE.get_or_init(|| export(w!("ntdll.dll"), c"wine_get_version").is_some())
}

pub fn supported() -> bool {
    !HELPER.is_empty()
}

pub fn sources() -> Vec<SourceInfo> {
    if !supported() {
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

fn free(ptr: *mut c_void) {
    // SAFETY: ptr came from Wine's file name exports, which allocate on the process heap.
    unsafe {
        if let Ok(heap) = GetProcessHeap() {
            let _ = HeapFree(heap, HEAP_FLAGS(0), Some(ptr));
        }
    }
}

fn unix_name(path: &Path) -> Option<String> {
    let f = export(w!("kernel32.dll"), c"wine_get_unix_file_name")?;
    // SAFETY: the export has the UnixName signature in every Wine release.
    let f: UnixName = unsafe { std::mem::transmute::<Export, UnixName>(f) };
    let wide: Vec<u16> = path
        .as_os_str()
        .encode_wide()
        .chain(std::iter::once(0))
        .collect();
    // SAFETY: wide is NUL-terminated and outlives the call.
    let out = unsafe { f(wide.as_ptr()) };
    if out.is_null() {
        return None;
    }
    // SAFETY: out is a NUL-terminated string owned by us until freed.
    let name = unsafe { CStr::from_ptr(out) }
        .to_string_lossy()
        .into_owned();
    free(out.cast());
    Some(name)
}

fn dos_name(unix: &str) -> Option<PathBuf> {
    let f = export(w!("kernel32.dll"), c"wine_get_dos_file_name")?;
    // SAFETY: the export has the DosName signature in every Wine release.
    let f: DosName = unsafe { std::mem::transmute::<Export, DosName>(f) };
    let unix = CString::new(unix).ok()?;
    // SAFETY: unix is NUL-terminated and outlives the call.
    let out = unsafe { f(unix.as_ptr()) };
    if out.is_null() {
        return None;
    }
    let mut len = 0usize;
    // SAFETY: out is a NUL-terminated UTF-16 string owned by us until freed.
    let name = unsafe {
        while *out.add(len) != 0 {
            len += 1;
        }
        String::from_utf16_lossy(std::slice::from_raw_parts(out, len))
    };
    free(out.cast());
    Some(PathBuf::from(name))
}

fn fingerprint(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325u64;
    for &b in bytes {
        hash = (hash ^ u64::from(b)).wrapping_mul(0x0100_0000_01b3);
    }
    hash
}

fn stage_helper() -> Result<PathBuf, CaptureError> {
    let path = std::env::temp_dir().join(format!(
        "basis-capture-helper-{:016x}.bin",
        fingerprint(HELPER)
    ));
    let current = std::fs::metadata(&path).is_ok_and(|m| m.len() == HELPER.len() as u64);
    if !current {
        let staging = path.with_extension(format!("{}.tmp", std::process::id()));
        std::fs::write(&staging, HELPER)
            .and_then(|()| std::fs::rename(&staging, &path))
            .map_err(|e| CaptureError(format!("could not stage the Linux capture helper: {e}")))?;
    }
    Ok(path)
}

fn token() -> [u8; TOKEN_LEN] {
    let mut out = [0u8; TOKEN_LEN];
    let seed = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map_or(0, |d| d.as_nanos());
    for (i, chunk) in out.chunks_mut(8).enumerate() {
        let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
        hasher.write_u128(seed);
        hasher.write_usize(i);
        hasher.write_u32(std::process::id());
        chunk.copy_from_slice(&hasher.finish().to_le_bytes());
    }
    out
}

fn launch(helper_unix: &str, port: u16, token: &str) -> Result<(), CaptureError> {
    let port = port.to_string();
    if let Some(f) = export(w!("ntdll.dll"), c"__wine_unix_spawnvp") {
        // SAFETY: the export has the Spawn signature in every Wine release that ships it.
        let f: Spawn = unsafe { std::mem::transmute::<Export, Spawn>(f) };
        let args: Vec<CString> = [LOADER, helper_unix, &port, token]
            .iter()
            .filter_map(|a| CString::new(*a).ok())
            .collect();
        let mut argv: Vec<*const c_char> = args.iter().map(|a| a.as_ptr()).collect();
        argv.push(std::ptr::null());
        // SAFETY: argv is a NULL-terminated array of NUL-terminated strings that outlive the call.
        if args.len() == 4 && unsafe { f(argv.as_ptr(), 0) } == 0 {
            return Ok(());
        }
    }
    let loader =
        dos_name(LOADER).unwrap_or_else(|| PathBuf::from(r"Z:\lib64\ld-linux-x86-64.so.2"));
    std::process::Command::new(loader)
        .args([helper_unix, &port, token])
        .stdin(std::process::Stdio::null())
        .stdout(std::process::Stdio::null())
        .stderr(std::process::Stdio::null())
        .spawn()
        .map(drop)
        .map_err(|e| CaptureError(format!("could not start the Linux capture helper: {e}")))
}

pub fn open(shared: &Arc<Shared>) -> Result<Vec<JoinHandle<()>>, CaptureError> {
    if !supported() {
        return Err(CaptureError(
            "this build has no Linux capture helper for Proton".into(),
        ));
    }
    let helper = stage_helper()?;
    let helper_unix = unix_name(&helper)
        .ok_or_else(|| CaptureError("could not resolve the capture helper's Linux path".into()))?;
    let listener = TcpListener::bind(("127.0.0.1", 0))
        .map_err(|e| CaptureError(format!("capture helper socket: {e}")))?;
    let port = listener
        .local_addr()
        .map_err(|e| CaptureError(format!("capture helper socket: {e}")))?
        .port();
    let secret = token();
    launch(&helper_unix, port, &relay::token_hex(&secret))?;
    let host_shared = Arc::clone(shared);
    let thread = std::thread::Builder::new()
        .name("bm-capture-relay".into())
        .spawn(move || {
            if let Err(e) = host(&host_shared, &listener, &secret) {
                host_shared.fail(e.0);
            }
        })
        .map_err(|e| CaptureError(format!("spawn relay thread: {e}")))?;
    Ok(vec![thread])
}

fn accept(
    shared: &Shared,
    listener: &TcpListener,
    secret: &[u8; TOKEN_LEN],
) -> Result<Option<TcpStream>, CaptureError> {
    listener
        .set_nonblocking(true)
        .map_err(|e| CaptureError(format!("capture helper socket: {e}")))?;
    let started = Instant::now();
    while !shared.stopped() {
        match listener.accept() {
            Ok((mut stream, _)) => {
                let _ = stream.set_nonblocking(false);
                let _ = stream.set_read_timeout(Some(Duration::from_secs(5)));
                let mut hello = [0u8; relay::HELLO_LEN];
                if stream.read_exact(&mut hello).is_ok() && hello == relay::hello(secret) {
                    let _ = stream.set_read_timeout(None);
                    let _ = stream.set_nodelay(true);
                    return Ok(Some(stream));
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {}
            Err(e) => return Err(CaptureError(format!("capture helper socket: {e}"))),
        }
        if started.elapsed() >= CONNECT {
            return Err(CaptureError(
                "the Linux capture helper did not start".into(),
            ));
        }
        std::thread::sleep(POLL);
    }
    Ok(None)
}

fn host(
    shared: &Arc<Shared>,
    listener: &TcpListener,
    secret: &[u8; TOKEN_LEN],
) -> Result<(), CaptureError> {
    let Some(stream) = accept(shared, listener, secret)? else {
        return Ok(());
    };
    let io = |e: std::io::Error| CaptureError(format!("capture helper connection: {e}"));
    let reader_stream = stream.try_clone().map_err(io)?;
    let mut writer = BufWriter::new(stream.try_clone().map_err(io)?);
    let config = serde_json::to_vec(&shared.config).map_err(|e| CaptureError(e.to_string()))?;
    relay::write_message(&mut writer, relay::CONFIG, &[&config]).map_err(io)?;
    let reader_shared = Arc::clone(shared);
    let reader = std::thread::Builder::new()
        .name("bm-capture-relay-read".into())
        .spawn(move || read_helper(&reader_shared, reader_stream))
        .map_err(|e| CaptureError(format!("spawn relay thread: {e}")))?;
    let mut bitrate = shared.bitrate_kbps.load(Ordering::Acquire);
    let mut result = Ok(());
    while !shared.stopped() && !reader.is_finished() {
        std::thread::sleep(POLL);
        if shared.keyframe.swap(false, Ordering::AcqRel)
            && let Err(e) = relay::write_message(&mut writer, relay::KEYFRAME, &[])
        {
            result = Err(io(e));
            break;
        }
        let requested = shared.bitrate_kbps.load(Ordering::Acquire);
        if requested != bitrate {
            bitrate = requested;
            if let Err(e) =
                relay::write_message(&mut writer, relay::BITRATE, &[&requested.to_le_bytes()])
            {
                result = Err(io(e));
                break;
            }
        }
    }
    let _ = stream.shutdown(Shutdown::Both);
    let _ = reader.join();
    result
}

fn read_helper(shared: &Shared, mut stream: TcpStream) {
    let mut body = Vec::new();
    let mut finished = false;
    while let Ok(kind) = relay::read_message(&mut stream, &mut body) {
        match kind {
            relay::VIDEO => {
                if let Some(frame) = relay::parse_video(&body) {
                    shared.push_video(frame);
                }
            }
            relay::AUDIO => {
                if let Some(packet) = relay::parse_audio(&body) {
                    shared.push_audio(packet);
                }
            }
            relay::STATUS => {
                let Some(status) = relay::parse_status(&body) else {
                    continue;
                };
                shared.captured.store(status.captured, Ordering::Relaxed);
                shared.encoded.store(status.encoded, Ordering::Relaxed);
                shared.dropped.store(status.dropped, Ordering::Relaxed);
                finished = matches!(status.state, CaptureState::Ended | CaptureState::Error);
                if status.state == CaptureState::Error {
                    shared.fail(status.error);
                    continue;
                }
                shared.update(|s| {
                    let bitrate = s.bitrate_kbps;
                    *s = status;
                    s.bitrate_kbps = bitrate;
                });
            }
            relay::LOG => {
                media_diag::diag_log!("capture helper: {}", String::from_utf8_lossy(&body));
            }
            _ => {}
        }
    }
    if !finished && !shared.stopped() {
        shared.fail("the Linux capture helper stopped");
    }
    shared.signal();
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{CaptureConfig, CaptureStatus, EncodedAudio, EncodedVideo};
    use std::io::Write;

    fn status(state: CaptureState) -> CaptureStatus {
        CaptureStatus {
            state,
            error: String::new(),
            width: 1280,
            height: 720,
            encoder: "OpenH264".into(),
            hardware: false,
            captured: 5,
            encoded: 4,
            dropped: 0,
            bitrate_kbps: 1,
            audio: true,
            audio_error: String::new(),
        }
    }

    fn start(secret: [u8; TOKEN_LEN]) -> (Arc<Shared>, u16, JoinHandle<Result<(), CaptureError>>) {
        let shared = Arc::new(Shared::new(CaptureConfig {
            source_id: PORTAL_SOURCE.into(),
            bitrate_kbps: 3000,
            ..CaptureConfig::default()
        }));
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let host_shared = Arc::clone(&shared);
        let thread = std::thread::spawn(move || host(&host_shared, &listener, &secret));
        (shared, port, thread)
    }

    fn until(what: impl Fn() -> bool) {
        let started = Instant::now();
        while !what() {
            assert!(started.elapsed() < Duration::from_secs(5), "timed out");
            std::thread::sleep(Duration::from_millis(5));
        }
    }

    #[test]
    fn helper_stream_reaches_shared_and_commands_reach_helper() {
        let secret = token();
        let (shared, port, thread) = start(secret);
        let mut helper = TcpStream::connect(("127.0.0.1", port)).unwrap();
        helper.write_all(&relay::hello(&secret)).unwrap();
        let mut body = Vec::new();
        assert_eq!(
            relay::read_message(&mut helper, &mut body).unwrap(),
            relay::CONFIG
        );
        let config: CaptureConfig = serde_json::from_slice(&body).unwrap();
        assert_eq!(
            (config.source_id.as_str(), config.bitrate_kbps),
            (PORTAL_SOURCE, 3000)
        );
        assert_eq!(
            relay::read_message(&mut helper, &mut body).unwrap(),
            relay::KEYFRAME
        );
        relay::write_status(&mut helper, &status(CaptureState::Running)).unwrap();
        relay::write_video(
            &mut helper,
            &EncodedVideo {
                data: vec![0, 0, 0, 1, 0x65],
                pts_us: 40,
                key: true,
                width: 1280,
                height: 720,
            },
        )
        .unwrap();
        relay::write_audio(
            &mut helper,
            &EncodedAudio {
                data: vec![1, 2],
                pts_us: 41,
            },
        )
        .unwrap();
        until(|| !shared.video.is_empty() && !shared.audio.is_empty());
        let frame = shared.video.pop().unwrap();
        assert_eq!((frame.pts_us, frame.key, frame.width), (40, true, 1280));
        assert_eq!(shared.audio.pop().unwrap().data, vec![1, 2]);
        until(|| shared.status.lock().unwrap().state == CaptureState::Running);
        {
            let s = shared.status.lock().unwrap();
            assert_eq!(
                (s.width, s.encoder.as_str(), s.bitrate_kbps),
                (1280, "OpenH264", 3000)
            );
        }
        assert_eq!(shared.encoded.load(Ordering::Relaxed), 4);
        shared.keyframe.store(true, Ordering::Release);
        assert_eq!(
            relay::read_message(&mut helper, &mut body).unwrap(),
            relay::KEYFRAME
        );
        shared.bitrate_kbps.store(1500, Ordering::Release);
        assert_eq!(
            relay::read_message(&mut helper, &mut body).unwrap(),
            relay::BITRATE
        );
        assert_eq!(body, 1500u32.to_le_bytes());
        relay::write_status(&mut helper, &status(CaptureState::Ended)).unwrap();
        drop(helper);
        thread.join().unwrap().unwrap();
        assert_eq!(shared.status.lock().unwrap().state, CaptureState::Ended);
        assert!(!shared.stopped());
    }

    #[test]
    fn helper_error_fails_the_capture() {
        let secret = token();
        let (shared, port, thread) = start(secret);
        let mut helper = TcpStream::connect(("127.0.0.1", port)).unwrap();
        helper.write_all(&relay::hello(&secret)).unwrap();
        let mut body = Vec::new();
        relay::read_message(&mut helper, &mut body).unwrap();
        let mut failed = status(CaptureState::Error);
        failed.error = "screen sharing needs xdg-desktop-portal".into();
        relay::write_status(&mut helper, &failed).unwrap();
        drop(helper);
        thread.join().unwrap().unwrap();
        let s = shared.status.lock().unwrap();
        assert_eq!(
            (s.state, s.error.as_str()),
            (
                CaptureState::Error,
                "screen sharing needs xdg-desktop-portal"
            )
        );
        assert!(shared.stopped());
    }

    #[test]
    fn wrong_token_is_ignored_and_stop_ends_the_wait() {
        let secret = token();
        let (shared, port, thread) = start(secret);
        let mut intruder = TcpStream::connect(("127.0.0.1", port)).unwrap();
        intruder.write_all(&relay::hello(&[0; TOKEN_LEN])).unwrap();
        std::thread::sleep(Duration::from_millis(100));
        shared.stop.store(true, Ordering::Release);
        thread.join().unwrap().unwrap();
        let mut body = Vec::new();
        assert!(relay::read_message(&mut intruder, &mut body).is_err());
    }

    #[test]
    fn helper_disconnect_fails_the_capture() {
        let secret = token();
        let (shared, port, thread) = start(secret);
        let mut helper = TcpStream::connect(("127.0.0.1", port)).unwrap();
        helper.write_all(&relay::hello(&secret)).unwrap();
        let mut body = Vec::new();
        relay::read_message(&mut helper, &mut body).unwrap();
        drop(helper);
        thread.join().unwrap().unwrap();
        assert_eq!(shared.status.lock().unwrap().state, CaptureState::Error);
    }

    #[test]
    fn tokens_differ() {
        assert_ne!(token(), token());
    }
}
