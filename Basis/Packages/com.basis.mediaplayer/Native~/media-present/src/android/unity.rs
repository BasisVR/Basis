//! Unity native plugin API declarations (ported from IUnityInterface.h /
//! IUnityGraphics.h / IUnityGraphicsVulkan.h; `UNITY_INTERFACE_API` is
//! empty on Android, so everything is plain `extern "C"`). Struct layouts
//! mirror the headers field for field: the V2 Vulkan interface is a
//! vtable and every slot must sit at its exact offset.

#![allow(non_snake_case)]

use core::ffi::{c_char, c_int, c_void};

use ash::vk;

/// Every function slot Unity fills in is declared `Option`: the tables and
/// the structs returned from them are foreign values, and a bare `fn`
/// pointer in Rust may not be null even before it is called, so a Unity
/// build that leaves a slot empty would make the value itself invalid.
/// `Option<fn(..)>` has the same size and ABI through the null niche.
#[repr(C)]
pub struct IUnityInterfaces {
    pub get_interface: Option<unsafe extern "C" fn(guid: UnityInterfaceGUID) -> *mut c_void>,
    pub register_interface:
        Option<unsafe extern "C" fn(guid: UnityInterfaceGUID, ptr: *mut c_void)>,
    pub get_interface_split: Option<unsafe extern "C" fn(high: u64, low: u64) -> *mut c_void>,
    pub register_interface_split:
        Option<unsafe extern "C" fn(high: u64, low: u64, ptr: *mut c_void)>,
}

#[repr(C)]
pub struct UnityInterfaceGUID {
    pub high: u64,
    pub low: u64,
}

#[repr(C)]
pub struct IUnityGraphics {
    pub get_renderer: Option<unsafe extern "C" fn() -> c_int>,
    pub register_device_event_callback:
        Option<unsafe extern "C" fn(cb: unsafe extern "C" fn(c_int))>,
    pub unregister_device_event_callback:
        Option<unsafe extern "C" fn(cb: unsafe extern "C" fn(c_int))>,
    pub reserve_event_id_range: Option<unsafe extern "C" fn(count: c_int) -> c_int>,
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct UnityVulkanInstance {
    pub pipeline_cache: u64,
    pub instance: vk::Instance,
    pub physical_device: vk::PhysicalDevice,
    pub device: vk::Device,
    pub graphics_queue: vk::Queue,
    pub get_instance_proc_addr: Option<vk::PFN_vkGetInstanceProcAddr>,
    pub queue_family_index: u32,
    pub reserved: [*mut c_void; 8],
}

#[repr(C)]
pub struct UnityVulkanMemory {
    pub memory: vk::DeviceMemory,
    pub offset: vk::DeviceSize,
    pub size: vk::DeviceSize,
    pub mapped: *mut c_void,
    pub flags: vk::MemoryPropertyFlags,
    pub memory_type_index: u32,
    pub reserved: [*mut c_void; 4],
}

#[repr(C)]
pub struct UnityVulkanImage {
    pub memory: UnityVulkanMemory,
    pub image: vk::Image,
    pub layout: vk::ImageLayout,
    pub aspect: vk::ImageAspectFlags,
    pub usage: vk::ImageUsageFlags,
    pub format: vk::Format,
    pub extent: vk::Extent3D,
    pub tiling: vk::ImageTiling,
    pub r#type: vk::ImageType,
    pub samples: vk::SampleCountFlags,
    pub layers: c_int,
    pub mip_count: c_int,
    pub reserved: [*mut c_void; 4],
}

#[repr(C)]
pub struct UnityVulkanRecordingState {
    pub command_buffer: vk::CommandBuffer,
    pub command_buffer_level: c_int,
    pub render_pass: vk::RenderPass,
    pub framebuffer: vk::Framebuffer,
    pub sub_pass_index: c_int,
    pub current_frame_number: u64,
    pub safe_frame_number: u64,
    pub reserved: [*mut c_void; 4],
}

pub type UnityVulkanResourceAccessMode = c_int;
pub const ACCESS_PIPELINE_BARRIER: UnityVulkanResourceAccessMode = 1;

pub type UnityVulkanGraphicsQueueAccess = c_int;
pub const QUEUE_ACCESS_DONT_CARE: UnityVulkanGraphicsQueueAccess = 0;

pub type UnityVulkanInitCallback = unsafe extern "C" fn(
    get_instance_proc_addr: Option<vk::PFN_vkGetInstanceProcAddr>,
    userdata: *mut c_void,
) -> Option<vk::PFN_vkGetInstanceProcAddr>;

#[repr(C)]
pub struct IUnityGraphicsVulkanV2 {
    pub intercept_initialization:
        Option<unsafe extern "C" fn(func: UnityVulkanInitCallback, userdata: *mut c_void) -> bool>,
    pub intercept_vulkan_api: *mut c_void,
    pub configure_event: *mut c_void,
    pub instance: Option<unsafe extern "C" fn() -> UnityVulkanInstance>,
    pub command_recording_state: Option<
        unsafe extern "C" fn(
            out: *mut UnityVulkanRecordingState,
            queue_access: UnityVulkanGraphicsQueueAccess,
        ) -> bool,
    >,
    pub access_texture: Option<
        unsafe extern "C" fn(
            native_texture: *mut c_void,
            sub_resource: *const vk::ImageSubresource,
            layout: vk::ImageLayout,
            pipeline_stage_flags: vk::PipelineStageFlags,
            access_flags: vk::AccessFlags,
            access_mode: UnityVulkanResourceAccessMode,
            out_image: *mut UnityVulkanImage,
        ) -> bool,
    >,
    pub access_render_buffer_texture: *mut c_void,
    pub access_render_buffer_resolve_texture: *mut c_void,
    pub access_buffer: *mut c_void,
    pub ensure_outside_render_pass: Option<unsafe extern "C" fn()>,
    pub ensure_inside_render_pass: *mut c_void,
    pub access_queue: *mut c_void,
    pub configure_swapchain: *mut c_void,
    pub access_texture_by_id: *mut c_void,
    pub add_intercept_initialization: Option<
        unsafe extern "C" fn(
            func: UnityVulkanInitCallback,
            userdata: *mut c_void,
            priority: i32,
        ) -> bool,
    >,
    pub remove_intercept_initialization:
        Option<unsafe extern "C" fn(func: UnityVulkanInitCallback) -> bool>,
}

pub const GUID_VULKAN_V2: (u64, u64) = (0x329334C09DCA4787, 0xB347DD92A0097FFC);
pub const GUID_GRAPHICS: (u64, u64) = (0x7CBA0A9CA4DDB544, 0x8C5AD4926EB17B11);

/// `kUnityGfxRendererVulkan`.
pub const RENDERER_VULKAN: c_int = 21;
/// `kUnityGfxDeviceEventInitialize`.
pub const DEVICE_EVENT_INITIALIZE: c_int = 0;

// logcat: the only place stderr-style diagnostics land on Android.
#[link(name = "log")]
unsafe extern "C" {
    fn __android_log_write(prio: c_int, tag: *const c_char, text: *const c_char) -> c_int;
}

pub fn log(line: &str) {
    let Ok(text) = std::ffi::CString::new(line.replace('\0', "?")) else {
        return;
    };
    // SAFETY: both pointers are NUL-terminated strings live for the call.
    unsafe {
        __android_log_write(4 /* INFO */, c"basis-media".as_ptr(), text.as_ptr());
    }
}

macro_rules! logf {
    ($($arg:tt)*) => { $crate::android::unity::log(&format!($($arg)*)) };
}
pub(crate) use logf;
