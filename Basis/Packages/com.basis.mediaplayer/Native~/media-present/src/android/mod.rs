//! Android / Vulkan present path. Vulkan-init interception guarantees the
//! device extensions and YCbCr feature; the decoder's `AHardwareBuffer`
//! imports into Unity's own `VkDevice`; one fullscreen draw converts into
//! Unity's RGBA RenderTexture on the render thread.
//!
//! Managed-side requirements: the output texture is a Unity RGBA32
//! RenderTexture with the default read/write, so sRGB in a Linear
//! colour-space project and UNORM in a Gamma one, created at
//! the snapshot's display size and registered via
//! `bm_session_set_output_texture(GetNativeTexturePtr())`. The plugin must
//! be preloaded (`PluginImporter.isPreloaded`) so the interception
//! registers before graphics initialisation. Render events and teardown
//! order are the same as on D3D11.

mod fns;
mod intercept;
mod renderer;
mod unity;

pub use renderer::{SessionRenderer, drain_graveyard};

/// Forward of `UnityPluginLoad`.
///
/// # Safety
/// `interfaces` must be the live `IUnityInterfaces*` Unity passed.
pub unsafe fn unity_plugin_load(interfaces: *mut core::ffi::c_void) {
    // SAFETY: caller contract forwarded.
    unsafe { intercept::plugin_load(interfaces) }
}

/// Write a logcat line under the `basis-media` tag. stderr goes nowhere on
/// Android, and the engine's own eprintln diagnostics still go there, so
/// anything that matters should reach here too.
pub fn log(line: &str) {
    unity::log(line);
}
