use ash::vk;
use ash::vk::Handle;
use std::ffi::{CStr, c_char, c_void};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};

#[repr(C)]
pub struct UnityInterfaces {
    pub get_interface: unsafe extern "system" fn(u128) -> *mut c_void,
    pub register_interface: unsafe extern "system" fn(u128, *mut c_void),
    pub get_interface_split: unsafe extern "system" fn(u64, u64) -> *mut c_void,
    pub register_interface_split: unsafe extern "system" fn(u64, u64, *mut c_void),
}

#[repr(C)]
pub struct UnityGraphics {
    pub get_renderer: unsafe extern "system" fn() -> i32,
    pub register_device_event_callback: unsafe extern "system" fn(unsafe extern "system" fn(i32)),
    pub unregister_device_event_callback: unsafe extern "system" fn(unsafe extern "system" fn(i32)),
    pub reserve_event_id_range: unsafe extern "system" fn(i32) -> i32,
}

#[repr(C)]
pub struct UnityVulkanInstance {
    pub pipeline_cache: vk::PipelineCache,
    pub instance: vk::Instance,
    pub physical: vk::PhysicalDevice,
    pub device: vk::Device,
    pub queue: vk::Queue,
    pub get_instance_proc_addr: vk::PFN_vkGetInstanceProcAddr,
    pub family: u32,
    pub reserved: [*mut c_void; 8],
}

#[repr(C)]
pub struct UnityVulkanMemory { pub memory: vk::DeviceMemory, pub offset: u64, pub size: u64, pub mapped: *mut c_void, pub flags: u32, pub memory_type_index: u32, pub reserved: [*mut c_void; 4] }

#[repr(C)]
pub struct UnityVulkanImage {
    pub memory: UnityVulkanMemory,
    pub image: vk::Image,
    pub layout: vk::ImageLayout,
    pub aspect: u32,
    pub usage: u32,
    pub format: vk::Format,
    pub extent: vk::Extent3D,
    pub tiling: i32,
    pub image_type: i32,
    pub samples: u32,
    pub layers: i32,
    pub mip_count: i32,
    pub reserved: [*mut c_void; 4],
}

#[repr(C)]
pub struct UnityVulkanPluginEventConfig { pub render_pass_precondition: i32, pub graphics_queue_access: i32, pub flags: u32 }

pub type UnityVulkanInitCallback = unsafe extern "system" fn(vk::PFN_vkGetInstanceProcAddr, *mut c_void) -> vk::PFN_vkGetInstanceProcAddr;

#[repr(C)]
pub struct UnityGraphicsVulkan {
    pub intercept_initialization: unsafe extern "system" fn(UnityVulkanInitCallback, *mut c_void) -> bool,
    pub intercept_vulkan_api: unsafe extern "system" fn(*const c_char, vk::PFN_vkVoidFunction) -> vk::PFN_vkVoidFunction,
    pub configure_event: unsafe extern "system" fn(i32, *const UnityVulkanPluginEventConfig),
    pub instance: unsafe extern "system" fn() -> UnityVulkanInstance,
    pub command_recording_state: unsafe extern "system" fn(*mut c_void, i32) -> bool,
    pub access_texture: unsafe extern "system" fn(*mut c_void, *const c_void, vk::ImageLayout, u32, u32, i32, *mut UnityVulkanImage) -> bool,
}

pub const GUID_GRAPHICS: (u64, u64) = (0x7CBA0A9CA4DDB544, 0x8C5AD4926EB17B11);
pub const GUID_D3D11: (u64, u64) = (0xAAB37EF87A87D748, 0xBF76967F07EFB177);
pub const GUID_D3D12V7: (u64, u64) = (0x4624B0DA41B64AAC, 0x915AABCB9BC3F0D3);
pub const GUID_VULKAN: (u64, u64) = (0x95355348d4ef4e11, 0x9789313dfcffcc87);
pub const RENDERER_D3D11: i32 = 2;
pub const RENDERER_D3D12: i32 = 18;
pub const RENDERER_VULKAN: i32 = 21;

static ORIGINAL: AtomicUsize = AtomicUsize::new(0);
static INSTANCE: AtomicUsize = AtomicUsize::new(0);
pub static HOOKED: AtomicBool = AtomicBool::new(false);

const WANTED: &[&CStr] = &[
    c"VK_KHR_external_memory",
    c"VK_KHR_external_semaphore",
    c"VK_KHR_dedicated_allocation",
    c"VK_KHR_get_memory_requirements2",
    c"VK_KHR_timeline_semaphore",
    #[cfg(windows)]
    c"VK_KHR_external_memory_win32",
    #[cfg(windows)]
    c"VK_KHR_external_semaphore_win32",
    #[cfg(unix)]
    c"VK_KHR_external_memory_fd",
    #[cfg(unix)]
    c"VK_KHR_external_semaphore_fd",
];

unsafe fn original(instance: vk::Instance, name: &CStr) -> vk::PFN_vkVoidFunction {
    let gipa: vk::PFN_vkGetInstanceProcAddr = unsafe { std::mem::transmute(ORIGINAL.load(Ordering::Acquire)) };
    unsafe { gipa(instance, name.as_ptr()) }
}

unsafe extern "system" fn hooked_create_device(physical: vk::PhysicalDevice, info: *const vk::DeviceCreateInfo<'_>, allocator: *const vk::AllocationCallbacks<'_>, out: *mut vk::Device) -> vk::Result {
    unsafe {
        let instance = vk::Instance::from_raw(INSTANCE.load(Ordering::Acquire) as u64);
        let Some(create) = original(instance, c"vkCreateDevice") else { return vk::Result::ERROR_INITIALIZATION_FAILED; };
        let create: vk::PFN_vkCreateDevice = std::mem::transmute(create);
        let mut extensions: Vec<vk::ExtensionProperties> = Vec::new();
        if let Some(enumerate) = original(instance, c"vkEnumerateDeviceExtensionProperties") {
            let enumerate: vk::PFN_vkEnumerateDeviceExtensionProperties = std::mem::transmute(enumerate);
            let mut count = 0u32;
            let _ = enumerate(physical, std::ptr::null(), &mut count, std::ptr::null_mut());
            extensions.resize(count as usize, vk::ExtensionProperties::default());
            let _ = enumerate(physical, std::ptr::null(), &mut count, extensions.as_mut_ptr());
            extensions.truncate(count as usize);
        }
        let mut copy = *info;
        let existing: Vec<*const c_char> = if copy.enabled_extension_count > 0 { std::slice::from_raw_parts(copy.pp_enabled_extension_names, copy.enabled_extension_count as usize).to_vec() } else { Vec::new() };
        let mut names = existing.clone();
        for want in WANTED {
            let supported = extensions.iter().any(|e| e.extension_name_as_c_str().is_ok_and(|n| n == *want));
            let present = existing.iter().any(|p| CStr::from_ptr(*p) == *want);
            if supported && !present { names.push(want.as_ptr()); }
        }
        copy.enabled_extension_count = names.len() as u32;
        copy.pp_enabled_extension_names = names.as_ptr();
        let mut found = false;
        let mut next = copy.p_next as *mut vk::BaseOutStructure<'_>;
        while !next.is_null() {
            match (*next).s_type {
                vk::StructureType::PHYSICAL_DEVICE_VULKAN_1_2_FEATURES => { (*(next as *mut vk::PhysicalDeviceVulkan12Features<'_>)).timeline_semaphore = vk::TRUE; found = true; }
                vk::StructureType::PHYSICAL_DEVICE_TIMELINE_SEMAPHORE_FEATURES => { (*(next as *mut vk::PhysicalDeviceTimelineSemaphoreFeatures<'_>)).timeline_semaphore = vk::TRUE; found = true; }
                _ => {}
            }
            next = (*next).p_next;
        }
        let mut timeline = vk::PhysicalDeviceTimelineSemaphoreFeatures::default().timeline_semaphore(true);
        if !found { timeline.p_next = copy.p_next as *mut c_void; copy.p_next = &timeline as *const _ as *const c_void; }
        let result = create(physical, &copy, allocator, out);
        if result == vk::Result::SUCCESS { HOOKED.store(true, Ordering::Release); }
        result
    }
}

unsafe extern "system" fn hooked_get_instance_proc_addr(instance: vk::Instance, name: *const c_char) -> vk::PFN_vkVoidFunction {
    unsafe {
        if !name.is_null() && CStr::from_ptr(name) == c"vkCreateDevice" {
            INSTANCE.store(instance.as_raw() as usize, Ordering::Release);
            return std::mem::transmute::<*const c_void, vk::PFN_vkVoidFunction>(hooked_create_device as *const c_void);
        }
        original(instance, CStr::from_ptr(name))
    }
}

unsafe extern "system" fn intercept(gipa: vk::PFN_vkGetInstanceProcAddr, _user: *mut c_void) -> vk::PFN_vkGetInstanceProcAddr {
    ORIGINAL.store(gipa as usize, Ordering::Release);
    hooked_get_instance_proc_addr
}

pub unsafe fn install_vulkan_hook(interfaces: *const UnityInterfaces) {
    unsafe {
        let v = ((*interfaces).get_interface_split)(GUID_VULKAN.0, GUID_VULKAN.1) as *const UnityGraphicsVulkan;
        if !v.is_null() { ((*v).intercept_initialization)(intercept, std::ptr::null_mut()); }
    }
}
