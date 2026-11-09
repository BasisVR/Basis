use super::device::*;
use crate::{Fallible, os};
use ash::vk;
use basis_rt_protocol::*;

pub struct SharedImage {
    pub image: vk::Image,
    pub memory: vk::DeviceMemory,
    pub view: vk::ImageView,
    pub remote: u64,
    #[cfg(windows)]
    _resource: Option<windows::Win32::Graphics::Direct3D12::ID3D12Resource>,
}

pub struct SharedSemaphore {
    pub semaphore: vk::Semaphore,
    pub remote: u64,
    #[cfg(windows)]
    _fence: Option<windows::Win32::Graphics::Direct3D12::ID3D12Fence>,
}

pub struct Interop {
    pub mode: u32,
    #[cfg(windows)]
    d3d: Option<windows::Win32::Graphics::Direct3D12::ID3D12Device>,
}

#[cfg(windows)]
fn export_type() -> (vk::ExternalMemoryHandleTypeFlags, vk::ExternalSemaphoreHandleTypeFlags) { (vk::ExternalMemoryHandleTypeFlags::OPAQUE_WIN32, vk::ExternalSemaphoreHandleTypeFlags::OPAQUE_WIN32) }
#[cfg(unix)]
fn export_type() -> (vk::ExternalMemoryHandleTypeFlags, vk::ExternalSemaphoreHandleTypeFlags) { (vk::ExternalMemoryHandleTypeFlags::OPAQUE_FD, vk::ExternalSemaphoreHandleTypeFlags::OPAQUE_FD) }

pub fn image_info(width: u32, height: u32, format: i32) -> vk::ImageCreateInfo<'static> {
    vk::ImageCreateInfo::default().image_type(vk::ImageType::TYPE_2D).format(vk::Format::from_raw(format)).extent(vk::Extent3D { width: width.max(1), height: height.max(1), depth: 1 })
        .mip_levels(1).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL).usage(vk::ImageUsageFlags::from_raw(SHARED_USAGE)).sharing_mode(vk::SharingMode::EXCLUSIVE).initial_layout(vk::ImageLayout::UNDEFINED)
}

impl Interop {
    pub fn new(gpu: &Gpu, header: &Header) -> Fallible<Interop> {
        #[cfg(windows)]
        {
            if header.api != API_VULKAN {
                use windows::Win32::Foundation::LUID;
                use windows::Win32::Graphics::Direct3D::*;
                use windows::Win32::Graphics::Direct3D12::*;
                use windows::Win32::Graphics::Dxgi::*;
                let luid = gpu.luid.ok_or("the Vulkan device has no adapter LUID, so it cannot share textures with Direct3D")?;
                let luid = LUID { LowPart: u32::from_le_bytes(luid[0..4].try_into().unwrap()), HighPart: i32::from_le_bytes(luid[4..8].try_into().unwrap()) };
                unsafe {
                    let factory: IDXGIFactory4 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0)).map_err(|e| format!("create DXGI factory: {e}"))?;
                    let adapter: IDXGIAdapter1 = factory.EnumAdapterByLuid(luid).map_err(|e| format!("find the Direct3D adapter: {e}"))?;
                    let mut device: Option<ID3D12Device> = None;
                    D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_11_0, &mut device).map_err(|e| format!("create the Direct3D 12 sharing device: {e}"))?;
                    return Ok(Interop { mode: INTEROP_D3D12, d3d: device });
                }
            }
            let _ = header;
            Ok(Interop { mode: INTEROP_VULKAN_WIN32, d3d: None })
        }
        #[cfg(unix)]
        {
            let _ = (gpu, header);
            Ok(Interop { mode: INTEROP_VULKAN_FD })
        }
    }

    pub fn image(&self, gpu: &mut Gpu, link: &os::Link, width: u32, height: u32, format: i32) -> Fallible<SharedImage> {
        #[cfg(windows)]
        if let Some(d3d) = &self.d3d { return self.image_d3d12(d3d, gpu, link, width, height, format); }
        unsafe {
            let (memory_type, _) = export_type();
            let mut external = vk::ExternalMemoryImageCreateInfo::default().handle_types(memory_type);
            let info = image_info(width, height, format).push_next(&mut external);
            let image = gpu.device.create_image(&info, None).map_err(err("create shared image"))?;
            let requirements = gpu.device.get_image_memory_requirements(image);
            let index = gpu.memory_type(requirements.memory_type_bits, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
            let mut export = vk::ExportMemoryAllocateInfo::default().handle_types(memory_type);
            let mut dedicated = vk::MemoryDedicatedAllocateInfo::default().image(image);
            let allocate = vk::MemoryAllocateInfo::default().allocation_size(requirements.size).memory_type_index(index).push_next(&mut export).push_next(&mut dedicated);
            let memory = gpu.device.allocate_memory(&allocate, None).map_err(err("allocate shared image"))?;
            gpu.device.bind_image_memory(image, memory, 0).map_err(err("bind shared image"))?;
            #[cfg(windows)]
            let remote = {
                let handle = gpu.memory_win32.get_memory_win32_handle(&vk::MemoryGetWin32HandleInfoKHR::default().memory(memory).handle_type(memory_type)).map_err(err("export shared image"))?;
                let handle = windows::Win32::Foundation::HANDLE(handle as *mut core::ffi::c_void);
                let r = link.share_handle(handle).map_err(|e| e.to_string());
                let _ = windows::Win32::Foundation::CloseHandle(handle);
                r?
            };
            #[cfg(unix)]
            let remote = {
                let fd = gpu.memory_fd.get_memory_fd(&vk::MemoryGetFdInfoKHR::default().memory(memory).handle_type(memory_type)).map_err(err("export shared image"))?;
                link.share_fd(fd)?
            };
            let view = gpu.view(image, vk::Format::from_raw(format), 0, 1, vk::ImageUsageFlags::SAMPLED)?;
            Ok(SharedImage { image, memory, view, remote, #[cfg(windows)] _resource: None })
        }
    }

    #[cfg(windows)]
    fn image_d3d12(&self, d3d: &windows::Win32::Graphics::Direct3D12::ID3D12Device, gpu: &mut Gpu, link: &os::Link, width: u32, height: u32, format: i32) -> Fallible<SharedImage> {
        use windows::Win32::Foundation::*;
        use windows::Win32::Graphics::Direct3D12::*;
        use windows::Win32::Graphics::Dxgi::Common::*;
        use windows::core::PCWSTR;
        unsafe {
            let dxgi = if format == SHARED_FORMAT_UINT { DXGI_FORMAT_R16G16B16A16_UINT } else { DXGI_FORMAT_R16G16B16A16_FLOAT };
            let desc = crate::gpu::texture_desc(width, height, 1, dxgi, D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET);
            let mut resource: Option<ID3D12Resource> = None;
            d3d.CreateCommittedResource(&crate::gpu::heap_props(D3D12_HEAP_TYPE_DEFAULT), D3D12_HEAP_FLAG_SHARED, &desc, D3D12_RESOURCE_STATE_COMMON, None, &mut resource).map_err(|e| format!("create shared texture: {e}"))?;
            let resource = resource.ok_or("create shared texture")?;
            let handle = d3d.CreateSharedHandle(&resource, None, GENERIC_ALL.0, PCWSTR::null()).map_err(|e| format!("share texture: {e}"))?;
            let result = (|| -> Fallible<SharedImage> {
                let remote = link.share_handle(handle).map_err(|e| e.to_string())?;
                let kind = vk::ExternalMemoryHandleTypeFlags::D3D12_RESOURCE;
                let mut external = vk::ExternalMemoryImageCreateInfo::default().handle_types(kind);
                let info = image_info(width, height, format).push_next(&mut external);
                let image = gpu.device.create_image(&info, None).map_err(err("create imported image"))?;
                let requirements = gpu.device.get_image_memory_requirements(image);
                let mut properties = vk::MemoryWin32HandlePropertiesKHR::default();
                gpu.memory_win32.get_memory_win32_handle_properties(kind, handle.0 as isize, &mut properties).map_err(err("query imported texture"))?;
                let bits = requirements.memory_type_bits & properties.memory_type_bits;
                let index = gpu.memory_type(bits, vk::MemoryPropertyFlags::DEVICE_LOCAL).or_else(|_| gpu.memory_type(bits, vk::MemoryPropertyFlags::empty()))?;
                let mut import = vk::ImportMemoryWin32HandleInfoKHR::default().handle_type(kind).handle(handle.0 as isize);
                let mut dedicated = vk::MemoryDedicatedAllocateInfo::default().image(image);
                let allocate = vk::MemoryAllocateInfo::default().allocation_size(requirements.size).memory_type_index(index).push_next(&mut import).push_next(&mut dedicated);
                let memory = gpu.device.allocate_memory(&allocate, None).map_err(err("import shared texture"))?;
                gpu.device.bind_image_memory(image, memory, 0).map_err(err("bind imported texture"))?;
                let view = gpu.view(image, vk::Format::from_raw(format), 0, 1, vk::ImageUsageFlags::SAMPLED)?;
                Ok(SharedImage { image, memory, view, remote, _resource: Some(resource) })
            })();
            let _ = CloseHandle(handle);
            result
        }
    }

    pub fn semaphore(&self, gpu: &mut Gpu, link: &os::Link) -> Fallible<SharedSemaphore> {
        unsafe {
            #[cfg(windows)]
            if let Some(d3d) = &self.d3d {
                use windows::Win32::Foundation::*;
                use windows::Win32::Graphics::Direct3D12::*;
                use windows::core::PCWSTR;
                let fence: ID3D12Fence = d3d.CreateFence(0, D3D12_FENCE_FLAG_SHARED).map_err(|e| format!("create shared fence: {e}"))?;
                let handle = d3d.CreateSharedHandle(&fence, None, GENERIC_ALL.0, PCWSTR::null()).map_err(|e| format!("share fence: {e}"))?;
                let result = (|| -> Fallible<SharedSemaphore> {
                    let remote = link.share_handle(handle).map_err(|e| e.to_string())?;
                    let mut kind = vk::SemaphoreTypeCreateInfo::default().semaphore_type(vk::SemaphoreType::TIMELINE).initial_value(0);
                    let semaphore = gpu.device.create_semaphore(&vk::SemaphoreCreateInfo::default().push_next(&mut kind), None).map_err(err("create semaphore"))?;
                    let import = vk::ImportSemaphoreWin32HandleInfoKHR::default().semaphore(semaphore).handle_type(vk::ExternalSemaphoreHandleTypeFlags::D3D12_FENCE).handle(handle.0 as isize);
                    gpu.semaphore_win32.import_semaphore_win32_handle(&import).map_err(err("import shared fence"))?;
                    Ok(SharedSemaphore { semaphore, remote, _fence: Some(fence) })
                })();
                let _ = CloseHandle(handle);
                return result;
            }
            let (_, semaphore_type) = export_type();
            let mut kind = vk::SemaphoreTypeCreateInfo::default().semaphore_type(vk::SemaphoreType::TIMELINE).initial_value(0);
            let mut export = vk::ExportSemaphoreCreateInfo::default().handle_types(semaphore_type);
            let semaphore = gpu.device.create_semaphore(&vk::SemaphoreCreateInfo::default().push_next(&mut kind).push_next(&mut export), None).map_err(err("create shared semaphore"))?;
            #[cfg(windows)]
            let remote = {
                let handle = gpu.semaphore_win32.get_semaphore_win32_handle(&vk::SemaphoreGetWin32HandleInfoKHR::default().semaphore(semaphore).handle_type(semaphore_type)).map_err(err("export semaphore"))?;
                let handle = windows::Win32::Foundation::HANDLE(handle as *mut core::ffi::c_void);
                let r = link.share_handle(handle).map_err(|e| e.to_string());
                let _ = windows::Win32::Foundation::CloseHandle(handle);
                r?
            };
            #[cfg(unix)]
            let remote = {
                let fd = gpu.semaphore_fd.get_semaphore_fd(&vk::SemaphoreGetFdInfoKHR::default().semaphore(semaphore).handle_type(semaphore_type)).map_err(err("export semaphore"))?;
                link.share_fd(fd)?
            };
            Ok(SharedSemaphore { semaphore, remote, #[cfg(windows)] _fence: None })
        }
    }

    pub fn destroy_image(gpu: &Gpu, image: SharedImage) {
        unsafe {
            gpu.device.destroy_image_view(image.view, None);
            gpu.device.destroy_image(image.image, None);
            gpu.device.free_memory(image.memory, None);
        }
    }
}
