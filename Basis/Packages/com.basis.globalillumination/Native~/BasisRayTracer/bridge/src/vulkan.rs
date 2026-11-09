use crate::host::{self, Handle, Result};
use ash::vk;
use basis_rt_protocol::*;
use std::ffi::c_void;

pub type Resolve = Box<dyn Fn(*mut c_void) -> Option<(vk::Image, vk::ImageLayout)> + Send>;

pub struct Imported { pub image: vk::Image, pub memory: vk::DeviceMemory, pub width: u32, pub height: u32 }

pub struct Vulkan {
    pub instance: ash::Instance,
    pub physical: vk::PhysicalDevice,
    pub device: ash::Device,
    pub queue: vk::Queue,
    pub family: u32,
    #[cfg(windows)]
    memory_win32: ash::khr::external_memory_win32::Device,
    #[cfg(windows)]
    semaphore_win32: ash::khr::external_semaphore_win32::Device,
    #[cfg(unix)]
    memory_fd: ash::khr::external_memory_fd::Device,
    #[cfg(unix)]
    semaphore_fd: ash::khr::external_semaphore_fd::Device,
    memory_properties: vk::PhysicalDeviceMemoryProperties,
    pool: vk::CommandPool,
    commands: Vec<(vk::CommandBuffer, vk::Fence)>,
    cursor: usize,
    pub resolve: Resolve,
}
unsafe impl Send for Vulkan {}

fn err(context: &'static str) -> impl Fn(vk::Result) -> String { move |e| format!("{context}: {e}") }

impl Vulkan {
    pub unsafe fn new(instance: ash::Instance, physical: vk::PhysicalDevice, device: ash::Device, queue: vk::Queue, family: u32, resolve: Resolve) -> Result<Vulkan> {
        unsafe {
            let pool = device.create_command_pool(&vk::CommandPoolCreateInfo::default().queue_family_index(family).flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER), None).map_err(err("create command pool"))?;
            let buffers = device.allocate_command_buffers(&vk::CommandBufferAllocateInfo::default().command_pool(pool).level(vk::CommandBufferLevel::PRIMARY).command_buffer_count(8)).map_err(err("allocate command buffers"))?;
            let mut commands = Vec::new();
            for b in buffers { commands.push((b, device.create_fence(&vk::FenceCreateInfo::default().flags(vk::FenceCreateFlags::SIGNALED), None).map_err(err("create fence"))?)); }
            Ok(Vulkan {
                #[cfg(windows)]
                memory_win32: ash::khr::external_memory_win32::Device::new(&instance, &device),
                #[cfg(windows)]
                semaphore_win32: ash::khr::external_semaphore_win32::Device::new(&instance, &device),
                #[cfg(unix)]
                memory_fd: ash::khr::external_memory_fd::Device::new(&instance, &device),
                #[cfg(unix)]
                semaphore_fd: ash::khr::external_semaphore_fd::Device::new(&instance, &device),
                memory_properties: instance.get_physical_device_memory_properties(physical),
                instance, physical, device, queue, family, pool, commands, cursor: 0, resolve,
            })
        }
    }

    pub fn identity(&self) -> ([u8; 16], Option<[u8; 8]>) {
        let mut id = vk::PhysicalDeviceIDProperties::default();
        let mut props = vk::PhysicalDeviceProperties2::default().push_next(&mut id);
        unsafe { self.instance.get_physical_device_properties2(self.physical, &mut props); }
        (id.device_uuid, if id.device_luid_valid == vk::TRUE { Some(id.device_luid) } else { None })
    }

    fn memory_kind(interop: u32) -> vk::ExternalMemoryHandleTypeFlags {
        match interop { INTEROP_D3D12 => vk::ExternalMemoryHandleTypeFlags::D3D12_RESOURCE, INTEROP_VULKAN_WIN32 => vk::ExternalMemoryHandleTypeFlags::OPAQUE_WIN32, _ => vk::ExternalMemoryHandleTypeFlags::OPAQUE_FD }
    }

    fn semaphore_kind(interop: u32) -> vk::ExternalSemaphoreHandleTypeFlags {
        match interop { INTEROP_D3D12 => vk::ExternalSemaphoreHandleTypeFlags::D3D12_FENCE, INTEROP_VULKAN_WIN32 => vk::ExternalSemaphoreHandleTypeFlags::OPAQUE_WIN32, _ => vk::ExternalSemaphoreHandleTypeFlags::OPAQUE_FD }
    }

    pub fn import_image(&self, handle: Handle, interop: u32, width: u32, height: u32, format: i32) -> Result<Imported> {
        let kind = Self::memory_kind(interop);
        unsafe {
            let mut external = vk::ExternalMemoryImageCreateInfo::default().handle_types(kind);
            let info = vk::ImageCreateInfo::default().image_type(vk::ImageType::TYPE_2D).format(vk::Format::from_raw(format)).extent(vk::Extent3D { width: width.max(1), height: height.max(1), depth: 1 })
                .mip_levels(1).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL).usage(vk::ImageUsageFlags::from_raw(SHARED_USAGE)).sharing_mode(vk::SharingMode::EXCLUSIVE).initial_layout(vk::ImageLayout::UNDEFINED).push_next(&mut external);
            let image = match self.device.create_image(&info, None) { Ok(i) => i, Err(e) => { host::Host::close_handle(handle); return Err(format!("create imported image: {e}")); } };
            let requirements = self.device.get_image_memory_requirements(image);
            #[cfg(windows)]
            let bits = {
                let mut properties = vk::MemoryWin32HandlePropertiesKHR::default();
                if interop == INTEROP_D3D12 { self.memory_win32.get_memory_win32_handle_properties(kind, handle.0 as isize, &mut properties).map_err(err("query shared texture"))?; properties.memory_type_bits } else { u32::MAX }
            };
            #[cfg(unix)]
            let bits = {
                let mut properties = vk::MemoryFdPropertiesKHR::default();
                match self.memory_fd.get_memory_fd_properties(kind, handle, &mut properties) { Ok(_) => properties.memory_type_bits, Err(_) => u32::MAX }
            };
            let bits = requirements.memory_type_bits & bits;
            let index = (0..self.memory_properties.memory_type_count).find(|&i| bits & (1 << i) != 0 && self.memory_properties.memory_types[i as usize].property_flags.contains(vk::MemoryPropertyFlags::DEVICE_LOCAL))
                .or_else(|| (0..self.memory_properties.memory_type_count).find(|&i| bits & (1 << i) != 0)).ok_or("no memory type can hold the shared texture")?;
            let mut dedicated = vk::MemoryDedicatedAllocateInfo::default().image(image);
            #[cfg(windows)]
            let mut import = vk::ImportMemoryWin32HandleInfoKHR::default().handle_type(kind).handle(handle.0 as isize);
            #[cfg(unix)]
            let mut import = vk::ImportMemoryFdInfoKHR::default().handle_type(kind).fd(handle);
            let allocate = vk::MemoryAllocateInfo::default().allocation_size(requirements.size).memory_type_index(index).push_next(&mut import).push_next(&mut dedicated);
            let memory = match self.device.allocate_memory(&allocate, None) {
                Ok(m) => m,
                Err(e) => { self.device.destroy_image(image, None); if cfg!(unix) { host::Host::close_handle(handle); } return Err(format!("import shared texture: {e}")); }
            };
            self.device.bind_image_memory(image, memory, 0).map_err(err("bind shared texture"))?;
            Ok(Imported { image, memory, width, height })
        }
    }

    pub fn import_semaphore(&self, handle: Handle, interop: u32) -> Result<vk::Semaphore> {
        let kind = Self::semaphore_kind(interop);
        unsafe {
            let mut timeline = vk::SemaphoreTypeCreateInfo::default().semaphore_type(vk::SemaphoreType::TIMELINE).initial_value(0);
            let semaphore = self.device.create_semaphore(&vk::SemaphoreCreateInfo::default().push_next(&mut timeline), None).map_err(err("create timeline semaphore (Unity's Vulkan device needs timeline semaphores)"))?;
            #[cfg(windows)]
            let result = self.semaphore_win32.import_semaphore_win32_handle(&vk::ImportSemaphoreWin32HandleInfoKHR::default().semaphore(semaphore).handle_type(kind).handle(handle.0 as isize));
            #[cfg(unix)]
            let result = self.semaphore_fd.import_semaphore_fd(&vk::ImportSemaphoreFdInfoKHR::default().semaphore(semaphore).handle_type(kind).fd(handle));
            if let Err(e) = result { self.device.destroy_semaphore(semaphore, None); if cfg!(unix) { host::Host::close_handle(handle); } return Err(format!("import shared fence: {e}")); }
            Ok(semaphore)
        }
    }

    pub fn release_image(&self, image: Imported) { unsafe { self.device.destroy_image(image.image, None); self.device.free_memory(image.memory, None); } }

    pub fn release_semaphore(&self, semaphore: vk::Semaphore) { unsafe { self.device.destroy_semaphore(semaphore, None); } }

    pub fn run(&mut self, wait: Option<(vk::Semaphore, u64)>, signal: Option<(vk::Semaphore, u64)>, body: impl FnOnce(&ash::Device, vk::CommandBuffer)) -> Result<()> {
        let (cmd, fence) = self.commands[self.cursor];
        self.cursor = (self.cursor + 1) % self.commands.len();
        unsafe {
            self.device.wait_for_fences(&[fence], true, 1_000_000_000).map_err(err("wait for bridge commands"))?;
            self.device.reset_fences(&[fence]).map_err(err("reset fence"))?;
            self.device.reset_command_buffer(cmd, vk::CommandBufferResetFlags::empty()).map_err(err("reset command buffer"))?;
            self.device.begin_command_buffer(cmd, &vk::CommandBufferBeginInfo::default().flags(vk::CommandBufferUsageFlags::ONE_TIME_SUBMIT)).map_err(err("begin command buffer"))?;
            body(&self.device, cmd);
            self.device.end_command_buffer(cmd).map_err(err("end command buffer"))?;
            let wait_semaphores: Vec<vk::Semaphore> = wait.iter().map(|w| w.0).collect();
            let wait_values: Vec<u64> = wait.iter().map(|w| w.1).collect();
            let wait_stages: Vec<vk::PipelineStageFlags> = wait.iter().map(|_| vk::PipelineStageFlags::ALL_COMMANDS).collect();
            let signal_semaphores: Vec<vk::Semaphore> = signal.iter().map(|s| s.0).collect();
            let signal_values: Vec<u64> = signal.iter().map(|s| s.1).collect();
            let mut timeline = vk::TimelineSemaphoreSubmitInfo::default().wait_semaphore_values(&wait_values).signal_semaphore_values(&signal_values);
            let cmds = [cmd];
            let submit = vk::SubmitInfo::default().wait_semaphores(&wait_semaphores).wait_dst_stage_mask(&wait_stages).command_buffers(&cmds).signal_semaphores(&signal_semaphores).push_next(&mut timeline);
            self.device.queue_submit(self.queue, &[submit], fence).map_err(err("submit bridge copy"))?;
        }
        Ok(())
    }
}

fn layout_access(layout: vk::ImageLayout) -> vk::AccessFlags {
    match layout {
        vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL => vk::AccessFlags::COLOR_ATTACHMENT_WRITE | vk::AccessFlags::COLOR_ATTACHMENT_READ,
        vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL => vk::AccessFlags::SHADER_READ,
        vk::ImageLayout::TRANSFER_SRC_OPTIMAL => vk::AccessFlags::TRANSFER_READ,
        vk::ImageLayout::TRANSFER_DST_OPTIMAL => vk::AccessFlags::TRANSFER_WRITE,
        _ => vk::AccessFlags::MEMORY_READ | vk::AccessFlags::MEMORY_WRITE,
    }
}

fn barrier(device: &ash::Device, cmd: vk::CommandBuffer, image: vk::Image, old: vk::ImageLayout, new: vk::ImageLayout, src_family: u32, dst_family: u32) {
    let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: 1, base_array_layer: 0, layer_count: 1 };
    let b = vk::ImageMemoryBarrier::default().src_access_mask(layout_access(old) | vk::AccessFlags::MEMORY_WRITE).dst_access_mask(layout_access(new) | vk::AccessFlags::MEMORY_READ)
        .old_layout(old).new_layout(new).src_queue_family_index(src_family).dst_queue_family_index(dst_family).image(image).subresource_range(range);
    unsafe { device.cmd_pipeline_barrier(cmd, vk::PipelineStageFlags::ALL_COMMANDS, vk::PipelineStageFlags::ALL_COMMANDS, vk::DependencyFlags::empty(), &[], &[], &[b]); }
}

pub fn copy(device: &ash::Device, cmd: vk::CommandBuffer, family: u32, src: (vk::Image, vk::ImageLayout, bool), dst: (vk::Image, vk::ImageLayout, bool), width: u32, height: u32) {
    let shared_src = src.2;
    let shared_dst = dst.2;
    let external = vk::QUEUE_FAMILY_EXTERNAL;
    let ignored = vk::QUEUE_FAMILY_IGNORED;
    let g = vk::ImageLayout::GENERAL;
    let src_layout = if shared_src { g } else { vk::ImageLayout::TRANSFER_SRC_OPTIMAL };
    let dst_layout = if shared_dst { g } else { vk::ImageLayout::TRANSFER_DST_OPTIMAL };
    if shared_src { barrier(device, cmd, src.0, src.1, g, external, family); } else { barrier(device, cmd, src.0, src.1, src_layout, ignored, ignored); }
    if shared_dst { barrier(device, cmd, dst.0, dst.1, g, external, family); } else { barrier(device, cmd, dst.0, if dst.1 == vk::ImageLayout::UNDEFINED { vk::ImageLayout::UNDEFINED } else { dst.1 }, dst_layout, ignored, ignored); }
    let layers = vk::ImageSubresourceLayers { aspect_mask: vk::ImageAspectFlags::COLOR, mip_level: 0, base_array_layer: 0, layer_count: 1 };
    let region = vk::ImageCopy { src_subresource: layers, src_offset: vk::Offset3D::default(), dst_subresource: layers, dst_offset: vk::Offset3D::default(), extent: vk::Extent3D { width, height, depth: 1 } };
    unsafe { device.cmd_copy_image(cmd, src.0, src_layout, dst.0, dst_layout, &[region]); }
    if shared_src { barrier(device, cmd, src.0, g, g, family, external); } else { barrier(device, cmd, src.0, src_layout, src.1, ignored, ignored); }
    if shared_dst { barrier(device, cmd, dst.0, g, g, family, external); } else { barrier(device, cmd, dst.0, dst_layout, if dst.1 == vk::ImageLayout::UNDEFINED { vk::ImageLayout::GENERAL } else { dst.1 }, ignored, ignored); }
}
