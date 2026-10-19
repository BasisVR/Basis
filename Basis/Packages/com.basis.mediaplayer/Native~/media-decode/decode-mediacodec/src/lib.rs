//! Android MediaCodec decode adapters, async-callback driven.
//!
//! Video decodes into an `AImageReader` surface (format PRIVATE,
//! GPU_SAMPLED_IMAGE usage) and surfaces frames as opaque
//! `AHardwareBuffer` handles. Decoder output stays in the vendor's layout
//! and is never CPU-locked; the Vulkan present pass imports the buffer on
//! the render thread. Audio decodes to PCM through the codec's output
//! buffers.
//!
//! The whole crate is Android-only; on every other platform it compiles
//! to nothing (the engine's routing is target-gated to match).

#![cfg(target_os = "android")]

mod audio;
mod capability;
mod driver;
mod ffi;
mod video;

pub use audio::{AudioMime, McAudioDecoder};
pub use capability::{CodecProbe, probe_video_decoder, set_java_vm};
pub use video::{McVideoDecoder, VideoMime};
