//! macOS decode on Apple's own frameworks: VideoToolbox for H.264 and HEVC,
//! AudioToolbox for AAC and MP3. Nothing is bundled; the codecs are the
//! system's, the way Media Foundation and MediaCodec are on Windows and
//! Android. Video comes back as CPU NV12 copied out of the decoder's
//! pixel buffers, in the range and matrix the stream states.
//!
//! The whole crate is macOS-only; on every other platform it compiles to
//! nothing (the engine's routing is target-gated to match).

#![cfg(target_os = "macos")]

mod audio;
mod ffi;
mod video;

pub use audio::{AtAudioDecoder, AtCodec};
pub use video::{VtAcceleration, VtCodec, VtVideoDecoder, hardware_decode_supported};

use core_foundation_sys::base::{CFEqual, CFTypeRef, kCFAllocatorDefault};
use core_foundation_sys::dictionary::{
    CFDictionaryCreate, CFDictionaryRef, kCFTypeDictionaryKeyCallBacks,
    kCFTypeDictionaryValueCallBacks,
};
use core_foundation_sys::number::{CFNumberCreate, kCFNumberSInt32Type};

fn cf_equal<T>(a: CFTypeRef, b: *const T) -> bool {
    // SAFETY: both are live CoreFoundation objects or null, which CFEqual
    // is never given.
    !a.is_null() && !b.is_null() && unsafe { CFEqual(a, b.cast()) } != 0
}

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
