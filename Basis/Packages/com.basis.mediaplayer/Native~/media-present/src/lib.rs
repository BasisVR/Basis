//! Frame handoff into Unity, per graphics API:
//!
//! - **Windows / D3D11** (`win`): the decode thread converts NV12 into a
//!   shared BGRA texture with one GPU pass; the Unity render thread opens
//!   the shared handle and copies, under a keyed mutex.
//! - **Windows / D3D12** (`win_d3d12`): the same conversion pass, its
//!   output copied into shared slots published on a shared fence; the
//!   render event records the copy into Unity's texture on Unity's own
//!   command list.
//! - **Android / Vulkan** (`android`): the decoder's `AHardwareBuffer`
//!   is imported into Unity's own `VkDevice` (external memory, dedicated
//!   allocation, driver-suggested YCbCr conversion) and one compute pass
//!   converts into Unity's RGBA RenderTexture, recorded on Unity's
//!   current command buffer inside the render event.

use std::fmt;

#[derive(Debug)]
pub struct PresentError(pub String);

impl fmt::Display for PresentError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "present error: {}", self.0)
    }
}

impl std::error::Error for PresentError {}

#[cfg(windows)]
mod gpu;
#[cfg(windows)]
pub mod reference;
#[cfg(windows)]
mod win;
#[cfg(windows)]
mod win_d3d12;
#[cfg(windows)]
pub mod win_unity;
#[cfg(windows)]
pub use win::{SharedTextureConsumer, SharedTexturePresenter, TestConsumerTarget};
#[cfg(windows)]
pub use win_d3d12::{
    D3d12Consumer, D3d12Host, Handoff, TestD3d12Host, collect_retired, retired_count,
};

#[cfg(target_os = "android")]
pub mod android;
