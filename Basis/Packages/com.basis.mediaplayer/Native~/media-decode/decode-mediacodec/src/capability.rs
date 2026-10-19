//! Capability probes. Presence and route come from the NDK: a decoder
//! that `createDecoderByType` actually instantiates is the one the engine
//! would use, and the codec name separates hardware from the
//! `c2.android`/`OMX.google` software implementations. Ceilings need
//! `MediaCodecList`'s `CodecCapabilities`, which has no NDK surface, so
//! they go through JNI when the host process has handed over its `JavaVM`
//! (Unity does, at `JNI_OnLoad`). Without one, ceilings stay 0 (unstated,
//! ranked conservatively).

use std::ffi::c_void;
use std::sync::atomic::{AtomicPtr, Ordering};

use crate::driver::codec_name;
use crate::ffi::{AMediaCodec_createDecoderByType, AMediaCodec_delete};
use crate::video::VideoMime;

static JAVA_VM: AtomicPtr<c_void> = AtomicPtr::new(core::ptr::null_mut());

/// Record the process's `JavaVM` (from `JNI_OnLoad`). Idempotent.
///
/// # Safety
/// `vm` must be the process's live `JavaVM*`, as the runtime hands it to
/// `JNI_OnLoad`. It is kept for the process lifetime and revived with
/// `JavaVM::from_raw` on whichever thread runs the ceiling probe, and
/// every JNI call then dispatches through the invocation table read out of
/// it, so any other address becomes an indirect call through unrelated
/// memory. Nothing downstream can check this; the load site only rejects
/// null.
pub unsafe fn set_java_vm(vm: *mut c_void) {
    JAVA_VM.store(vm, Ordering::Release);
}

#[derive(Debug, Clone)]
pub struct CodecProbe {
    /// The instantiated decoder's name (e.g. `c2.qti.avc.decoder`).
    pub name: String,
    /// Name-derived: not one of the Android software implementations.
    pub hardware: bool,
    /// `MediaCodecList` ceilings; 0 = unstated (no JVM to ask).
    pub max_width: u32,
    pub max_height: u32,
    pub max_fps: u32,
}

/// Probe the decoder the engine would open for `mime`: instantiate,
/// name, release. Returns `None` when no decoder exists (the typed
/// refusal the routing would produce).
pub fn probe_video_decoder(mime: VideoMime) -> Option<CodecProbe> {
    let mime_c = mime.as_cstr();
    // SAFETY: create/name/delete on a codec this probe owns.
    let name = unsafe {
        let codec = AMediaCodec_createDecoderByType(mime_c.as_ptr());
        if codec.is_null() {
            return None;
        }
        let name = codec_name(codec);
        AMediaCodec_delete(codec);
        name?
    };
    let lowered = name.to_ascii_lowercase();
    let hardware = !(lowered.starts_with("c2.android.")
        || lowered.starts_with("omx.google.")
        || lowered.contains(".sw."));
    let (max_width, max_height, max_fps) =
        jni_video_ceilings(&name, &mime_c.to_string_lossy()).unwrap_or((0, 0, 0));
    Some(CodecProbe {
        name,
        hardware,
        max_width,
        max_height,
        max_fps,
    })
}

/// `MediaCodecList` → the named codec's `VideoCapabilities`: upper
/// supported width/height, and the achievable frame rate at that size.
/// Any JNI failure degrades to `None`; the probe never fails the caller.
fn jni_video_ceilings(codec_name: &str, mime: &str) -> Option<(u32, u32, u32)> {
    use jni::JavaVM;
    use jni::objects::{JObject, JObjectArray, JValue};

    let vm_ptr = JAVA_VM.load(Ordering::Acquire);
    if vm_ptr.is_null() {
        return None;
    }
    // SAFETY: `set_java_vm` is unsafe and its contract admits only the
    // process's live JavaVM*, so a non-null value here is one.
    let vm = unsafe { JavaVM::from_raw(vm_ptr.cast()) }.ok()?;
    let mut env = vm.attach_current_thread().ok()?;

    // On a thread Unity keeps attached (the main thread) local references
    // are only freed at detach, so the probe runs in its own frame and drops
    // the references of each codec it skips as it goes.
    let result = env.with_local_frame(16, |env| -> jni::errors::Result<Option<(u32, u32, u32)>> {
        // new MediaCodecList(MediaCodecList.REGULAR_CODECS = 0)
        let list = env.new_object("android/media/MediaCodecList", "(I)V", &[JValue::Int(0)])?;
        let infos: JObjectArray<'_> = env
            .call_method(
                &list,
                "getCodecInfos",
                "()[Landroid/media/MediaCodecInfo;",
                &[],
            )?
            .l()?
            .into();
        let count = env.get_array_length(&infos)?;
        for i in 0..count {
            let info = env.get_object_array_element(&infos, i)?;
            let name_obj = env
                .call_method(&info, "getName", "()Ljava/lang/String;", &[])?
                .l()?;
            let name: String = env.get_string((&name_obj).into())?.into();
            if name != codec_name {
                env.delete_local_ref(name_obj)?;
                env.delete_local_ref(info)?;
                continue;
            }
            let mime_j = env.new_string(mime)?;
            let caps = env.call_method(
                &info,
                "getCapabilitiesForType",
                "(Ljava/lang/String;)Landroid/media/MediaCodecInfo$CodecCapabilities;",
                &[JValue::Object(&mime_j)],
            );
            let caps = match caps {
                Ok(v) => v.l()?,
                Err(_) => {
                    let _ = env.exception_clear();
                    return Ok(None);
                }
            };
            let video = env
                .call_method(
                    &caps,
                    "getVideoCapabilities",
                    "()Landroid/media/MediaCodecInfo$VideoCapabilities;",
                    &[],
                )?
                .l()?;
            if video.is_null() {
                return Ok(None);
            }
            let upper_int = |env: &mut jni::JNIEnv<'_>, range: &JObject<'_>| {
                let upper = env
                    .call_method(range, "getUpper", "()Ljava/lang/Comparable;", &[])?
                    .l()?;
                env.call_method(&upper, "intValue", "()I", &[])?.i()
            };
            let widths = env
                .call_method(&video, "getSupportedWidths", "()Landroid/util/Range;", &[])?
                .l()?;
            let heights = env
                .call_method(&video, "getSupportedHeights", "()Landroid/util/Range;", &[])?
                .l()?;
            let max_width = upper_int(env, &widths)?.max(0) as u32;
            let max_height = upper_int(env, &heights)?.max(0) as u32;

            // The achievable rate at a real size beats the codec-wide
            // declared range (whose upper is a nominal 960). The two axis
            // uppers need not be jointly supported (8192x8192 throws on
            // c2.qti), so fall back through common ceilings.
            let mut fps = 0i32;
            for (w, h) in [
                (max_width as i32, max_height as i32),
                (3840, 2160),
                (1920, 1080),
            ] {
                let attempt = (|| -> jni::errors::Result<i32> {
                    let rates = env
                        .call_method(
                            &video,
                            "getSupportedFrameRatesFor",
                            "(II)Landroid/util/Range;",
                            &[JValue::Int(w), JValue::Int(h)],
                        )?
                        .l()?;
                    let upper = env
                        .call_method(&rates, "getUpper", "()Ljava/lang/Comparable;", &[])?
                        .l()?;
                    let value = env.call_method(&upper, "doubleValue", "()D", &[])?.d()?;
                    Ok(value.floor() as i32)
                })();
                match attempt {
                    Ok(value) => {
                        fps = value;
                        break;
                    }
                    Err(_) => {
                        let _ = env.exception_clear();
                    }
                }
            }
            return Ok(Some((max_width, max_height, fps.max(0) as u32)));
        }
        Ok(None)
    });

    match result {
        Ok(v) => v,
        Err(_) => {
            let _ = env.exception_clear();
            None
        }
    }
}
