//! Software decoders: permissively licensed, in-process, one behaviour on
//! every platform.
//!
//! The rav1d AV1 decoder is not built on Android: the crates.io package
//! cannot build its arm64 assembly (`src/arm/asm-offsets.h` is missing from
//! the published crate), and the Vulkan present path has no CPU-frame
//! upload. Android AV1 is the platform decoder or a typed refusal.

#[cfg(not(target_os = "android"))]
mod av1;
mod flac;
mod opus;
mod pcm;

pub use self::opus::{OpusDecoder, OpusHead};
#[cfg(not(target_os = "android"))]
pub use av1::SwAv1Decoder;
pub use flac::FlacDecoder;
pub use pcm::PcmDecoder;
