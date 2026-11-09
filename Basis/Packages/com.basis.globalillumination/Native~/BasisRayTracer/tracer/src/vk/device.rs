use crate::Fallible;
use ash::vk;
use std::ffi::{CStr, c_char};

pub fn err(context: &str) -> impl Fn(vk::Result) -> String + '_ { move |e| format!("{context}: {e}") }

pub struct Block { pub memory: vk::DeviceMemory, pub size: u64, pub free: Vec<(u64, u64)>, pub mapped: *mut u8 }

#[derive(Clone, Copy, Default)]
pub struct Allocation { pub memory: vk::DeviceMemory, pub offset: u64, pub size: u64, pub block: usize, pub pool: usize, pub dedicated: bool }

pub struct Pool { pub memory_type: u32, pub images: bool, pub blocks: Vec<Block> }

const BLOCK_BYTES: u64 = 64 * 1024 * 1024;

pub struct Gpu {
    pub _entry: ash::Entry,
    pub instance: ash::Instance,
    pub physical: vk::PhysicalDevice,
    pub device: ash::Device,
    pub queue: vk::Queue,
    pub family: u32,
    pub accel: ash::khr::acceleration_structure::Device,
    pub rt: ash::khr::ray_tracing_pipeline::Device,
    #[cfg(windows)]
    pub memory_win32: ash::khr::external_memory_win32::Device,
    #[cfg(windows)]
    pub semaphore_win32: ash::khr::external_semaphore_win32::Device,
    #[cfg(unix)]
    pub memory_fd: ash::khr::external_memory_fd::Device,
    #[cfg(unix)]
    pub semaphore_fd: ash::khr::external_semaphore_fd::Device,
    pub memory_properties: vk::PhysicalDeviceMemoryProperties,
    pub handle_size: u32,
    pub handle_alignment: u32,
    pub group_base_alignment: u32,
    pub scratch_alignment: u64,
    pub timestamp_period: f32,
    pub luid: Option<[u8; 8]>,
    pub timeline: vk::Semaphore,
    pub timeline_value: u64,
    pub pools: Vec<Pool>,
    pub command_pool: vk::CommandPool,
}

fn has(extensions: &[vk::ExtensionProperties], name: &CStr) -> bool { extensions.iter().any(|e| e.extension_name_as_c_str().is_ok_and(|n| n == name)) }

impl Gpu {
    pub fn new(uuid: [u8; 16], luid: [u8; 8]) -> Fallible<Gpu> {
        unsafe {
            let entry = ash::Entry::load().map_err(|e| format!("load the Vulkan loader: {e}"))?;
            let app = vk::ApplicationInfo::default().application_name(c"BasisRayTracer").api_version(vk::API_VERSION_1_3);
            let mut layers: Vec<*const c_char> = Vec::new();
            if std::env::var_os("BASIS_RT_DEBUG").is_some() { layers.push(c"VK_LAYER_KHRONOS_validation".as_ptr()); }
            let instance = entry.create_instance(&vk::InstanceCreateInfo::default().application_info(&app).enabled_layer_names(&layers), None).map_err(err("create Vulkan instance"))?;
            let mut required: Vec<&CStr> = vec![ash::khr::acceleration_structure::NAME, ash::khr::ray_tracing_pipeline::NAME, ash::khr::ray_query::NAME, ash::khr::deferred_host_operations::NAME, ash::ext::mutable_descriptor_type::NAME];
            if cfg!(windows) { required.extend([ash::khr::external_memory_win32::NAME, ash::khr::external_semaphore_win32::NAME]); } else { required.extend([ash::khr::external_memory_fd::NAME, ash::khr::external_semaphore_fd::NAME]); }
            let mut chosen = None;
            let mut fallback = None;
            let mut fallback_discrete = false;
            let mut reason = String::from("no Vulkan device was found");
            for physical in instance.enumerate_physical_devices().map_err(err("enumerate Vulkan devices"))? {
                let mut id = vk::PhysicalDeviceIDProperties::default();
                let mut props2 = vk::PhysicalDeviceProperties2::default().push_next(&mut id);
                instance.get_physical_device_properties2(physical, &mut props2);
                let props = props2.properties;
                let extensions = instance.enumerate_device_extension_properties(physical).unwrap_or_default();
                let missing: Vec<&CStr> = required.iter().copied().filter(|n| !has(&extensions, n)).collect();
                if !missing.is_empty() || props.api_version < vk::API_VERSION_1_3 {
                    reason = format!("{} does not support Vulkan ray tracing (missing {:?})", props.device_name_as_c_str().unwrap_or(c"GPU").to_string_lossy(), missing);
                    continue;
                }
                let device_luid = if id.device_luid_valid == vk::TRUE { Some(id.device_luid) } else { None };
                let matched = if uuid != [0; 16] { id.device_uuid == uuid } else if luid != [0; 8] { device_luid == Some(luid) } else { false };
                if matched { chosen = Some((physical, device_luid)); break; }
                if fallback.is_none() || (!fallback_discrete && props.device_type == vk::PhysicalDeviceType::DISCRETE_GPU) { fallback = Some((physical, device_luid)); fallback_discrete = props.device_type == vk::PhysicalDeviceType::DISCRETE_GPU; }
            }
            if chosen.is_none() && uuid == [0; 16] && luid == [0; 8] { chosen = fallback; }
            let Some((physical, device_luid)) = chosen else {
                return Err(if fallback.is_some() { "the GPU Unity renders on does not support Vulkan ray tracing".into() } else { reason });
            };
            let family = instance.get_physical_device_queue_family_properties(physical).iter().position(|q| q.queue_flags.contains(vk::QueueFlags::GRAPHICS | vk::QueueFlags::COMPUTE) && q.timestamp_valid_bits > 0).ok_or("no graphics queue with timestamps")? as u32;
            let priorities = [1.0f32];
            let queues = [vk::DeviceQueueCreateInfo::default().queue_family_index(family).queue_priorities(&priorities)];
            let names: Vec<*const c_char> = required.iter().map(|n| n.as_ptr()).collect();
            let mut f12 = vk::PhysicalDeviceVulkan12Features::default().timeline_semaphore(true).buffer_device_address(true).descriptor_indexing(true).runtime_descriptor_array(true)
                .descriptor_binding_partially_bound(true).descriptor_binding_update_unused_while_pending(true).descriptor_binding_storage_buffer_update_after_bind(true).descriptor_binding_sampled_image_update_after_bind(true)
                .descriptor_binding_storage_image_update_after_bind(true).shader_sampled_image_array_non_uniform_indexing(true).shader_storage_buffer_array_non_uniform_indexing(true).shader_storage_image_array_non_uniform_indexing(true)
                .scalar_block_layout(true).host_query_reset(true);
            let mut f13 = vk::PhysicalDeviceVulkan13Features::default().synchronization2(true).maintenance4(true);
            let mut f11 = vk::PhysicalDeviceVulkan11Features::default();
            let mut accel = vk::PhysicalDeviceAccelerationStructureFeaturesKHR::default().acceleration_structure(true);
            let mut rt = vk::PhysicalDeviceRayTracingPipelineFeaturesKHR::default().ray_tracing_pipeline(true);
            let mut query = vk::PhysicalDeviceRayQueryFeaturesKHR::default().ray_query(true);
            let mut mutable = vk::PhysicalDeviceMutableDescriptorTypeFeaturesEXT::default().mutable_descriptor_type(true);
            let features = vk::PhysicalDeviceFeatures::default().sampler_anisotropy(true).shader_storage_image_read_without_format(true).shader_storage_image_write_without_format(true);
            let mut features2 = vk::PhysicalDeviceFeatures2::default().features(features).push_next(&mut f11).push_next(&mut f12).push_next(&mut f13).push_next(&mut accel).push_next(&mut rt).push_next(&mut query).push_next(&mut mutable);
            let info = vk::DeviceCreateInfo::default().queue_create_infos(&queues).enabled_extension_names(&names).push_next(&mut features2);
            let device = instance.create_device(physical, &info, None).map_err(err("create Vulkan device"))?;
            let queue = device.get_device_queue(family, 0);
            let mut rt_props = vk::PhysicalDeviceRayTracingPipelinePropertiesKHR::default();
            let mut as_props = vk::PhysicalDeviceAccelerationStructurePropertiesKHR::default();
            let mut props2 = vk::PhysicalDeviceProperties2::default().push_next(&mut rt_props).push_next(&mut as_props);
            instance.get_physical_device_properties2(physical, &mut props2);
            let limits = props2.properties.limits;
            let mut timeline_type = vk::SemaphoreTypeCreateInfo::default().semaphore_type(vk::SemaphoreType::TIMELINE).initial_value(0);
            let timeline = device.create_semaphore(&vk::SemaphoreCreateInfo::default().push_next(&mut timeline_type), None).map_err(err("create timeline"))?;
            let command_pool = device.create_command_pool(&vk::CommandPoolCreateInfo::default().queue_family_index(family).flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER), None).map_err(err("create command pool"))?;
            Ok(Gpu {
                accel: ash::khr::acceleration_structure::Device::new(&instance, &device),
                rt: ash::khr::ray_tracing_pipeline::Device::new(&instance, &device),
                #[cfg(windows)]
                memory_win32: ash::khr::external_memory_win32::Device::new(&instance, &device),
                #[cfg(windows)]
                semaphore_win32: ash::khr::external_semaphore_win32::Device::new(&instance, &device),
                #[cfg(unix)]
                memory_fd: ash::khr::external_memory_fd::Device::new(&instance, &device),
                #[cfg(unix)]
                semaphore_fd: ash::khr::external_semaphore_fd::Device::new(&instance, &device),
                memory_properties: instance.get_physical_device_memory_properties(physical),
                handle_size: rt_props.shader_group_handle_size,
                handle_alignment: rt_props.shader_group_handle_alignment,
                group_base_alignment: rt_props.shader_group_base_alignment,
                scratch_alignment: (as_props.min_acceleration_structure_scratch_offset_alignment as u64).max(256),
                timestamp_period: limits.timestamp_period,
                luid: device_luid,
                _entry: entry, instance, physical, device, queue, family, timeline, timeline_value: 0, pools: Vec::new(), command_pool,
            })
        }
    }

    pub fn memory_type(&self, bits: u32, flags: vk::MemoryPropertyFlags) -> Fallible<u32> {
        (0..self.memory_properties.memory_type_count).find(|&i| bits & (1 << i) != 0 && self.memory_properties.memory_types[i as usize].property_flags.contains(flags)).ok_or_else(|| format!("no memory type for {flags:?}"))
    }

    pub fn allocate(&mut self, requirements: vk::MemoryRequirements, flags: vk::MemoryPropertyFlags, images: bool) -> Fallible<Allocation> {
        let memory_type = self.memory_type(requirements.memory_type_bits, flags)?;
        if requirements.size > BLOCK_BYTES / 2 {
            let memory = self.raw_allocate(requirements.size, memory_type)?;
            return Ok(Allocation { memory, offset: 0, size: requirements.size, block: 0, pool: usize::MAX, dedicated: true });
        }
        let pool = match self.pools.iter().position(|p| p.memory_type == memory_type && p.images == images) { Some(p) => p, None => { self.pools.push(Pool { memory_type, images, blocks: Vec::new() }); self.pools.len() - 1 } };
        let align = requirements.alignment.max(256);
        for b in 0..self.pools[pool].blocks.len() {
            if let Some(offset) = take(&mut self.pools[pool].blocks[b].free, requirements.size, align) {
                let memory = self.pools[pool].blocks[b].memory;
                return Ok(Allocation { memory, offset, size: requirements.size, block: b, pool, dedicated: false });
            }
        }
        let memory = self.raw_allocate(BLOCK_BYTES, memory_type)?;
        let mut free = vec![(0, BLOCK_BYTES)];
        let offset = take(&mut free, requirements.size, align).unwrap();
        self.pools[pool].blocks.push(Block { memory, size: BLOCK_BYTES, free, mapped: std::ptr::null_mut() });
        Ok(Allocation { memory, offset, size: requirements.size, block: self.pools[pool].blocks.len() - 1, pool, dedicated: false })
    }

    fn raw_allocate(&self, size: u64, memory_type: u32) -> Fallible<vk::DeviceMemory> {
        let mut flags = vk::MemoryAllocateFlagsInfo::default().flags(vk::MemoryAllocateFlags::DEVICE_ADDRESS);
        let info = vk::MemoryAllocateInfo::default().allocation_size(size).memory_type_index(memory_type).push_next(&mut flags);
        unsafe { self.device.allocate_memory(&info, None).map_err(err("allocate GPU memory")) }
    }

    pub fn free(&mut self, a: Allocation) {
        if a.memory == vk::DeviceMemory::null() { return; }
        if a.dedicated { unsafe { self.device.free_memory(a.memory, None); } return; }
        let block = &mut self.pools[a.pool].blocks[a.block];
        give(&mut block.free, a.offset, a.size);
        let _ = block.size;
    }

    pub fn buffer(&mut self, size: u64, usage: vk::BufferUsageFlags, flags: vk::MemoryPropertyFlags) -> Fallible<Buffer> {
        unsafe {
            let usage = usage | vk::BufferUsageFlags::SHADER_DEVICE_ADDRESS | vk::BufferUsageFlags::TRANSFER_DST | vk::BufferUsageFlags::TRANSFER_SRC;
            let buffer = self.device.create_buffer(&vk::BufferCreateInfo::default().size(size.max(256)).usage(usage), None).map_err(err("create buffer"))?;
            let requirements = self.device.get_buffer_memory_requirements(buffer);
            let host = flags.contains(vk::MemoryPropertyFlags::HOST_VISIBLE);
            let allocation = if host {
                let memory_type = self.memory_type(requirements.memory_type_bits, flags)?;
                Allocation { memory: self.raw_allocate(requirements.size, memory_type)?, offset: 0, size: requirements.size, block: 0, pool: usize::MAX, dedicated: true }
            } else { self.allocate(requirements, flags, false)? };
            self.device.bind_buffer_memory(buffer, allocation.memory, allocation.offset).map_err(err("bind buffer"))?;
            let mapped = if host { self.device.map_memory(allocation.memory, 0, vk::WHOLE_SIZE, vk::MemoryMapFlags::empty()).map_err(err("map buffer"))? as *mut u8 } else { std::ptr::null_mut() };
            let address = self.device.get_buffer_device_address(&vk::BufferDeviceAddressInfo::default().buffer(buffer));
            Ok(Buffer { buffer, allocation, address, size: size.max(256), mapped })
        }
    }

    pub fn destroy_buffer(&mut self, b: Buffer) {
        unsafe { self.device.destroy_buffer(b.buffer, None); }
        self.free(b.allocation);
    }

    pub fn image(&mut self, info: &vk::ImageCreateInfo) -> Fallible<(vk::Image, Allocation)> {
        unsafe {
            let image = self.device.create_image(info, None).map_err(err("create image"))?;
            let requirements = self.device.get_image_memory_requirements(image);
            let allocation = self.allocate(requirements, vk::MemoryPropertyFlags::DEVICE_LOCAL, true)?;
            self.device.bind_image_memory(image, allocation.memory, allocation.offset).map_err(err("bind image"))?;
            Ok((image, allocation))
        }
    }

    pub fn view(&self, image: vk::Image, format: vk::Format, base_mip: u32, mips: u32, usage: vk::ImageUsageFlags) -> Fallible<vk::ImageView> {
        let mut usage_info = vk::ImageViewUsageCreateInfo::default().usage(usage);
        let info = vk::ImageViewCreateInfo::default().image(image).view_type(vk::ImageViewType::TYPE_2D).format(format).subresource_range(vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: base_mip, level_count: mips, base_array_layer: 0, layer_count: 1 }).push_next(&mut usage_info);
        unsafe { self.device.create_image_view(&info, None).map_err(err("create image view")) }
    }

    pub fn begin(&self) -> Fallible<vk::CommandBuffer> {
        unsafe {
            let cmd = self.device.allocate_command_buffers(&vk::CommandBufferAllocateInfo::default().command_pool(self.command_pool).level(vk::CommandBufferLevel::PRIMARY).command_buffer_count(1)).map_err(err("allocate command buffer"))?[0];
            self.device.begin_command_buffer(cmd, &vk::CommandBufferBeginInfo::default().flags(vk::CommandBufferUsageFlags::ONE_TIME_SUBMIT)).map_err(err("begin command buffer"))?;
            Ok(cmd)
        }
    }

    pub fn finish_now(&mut self, cmd: vk::CommandBuffer) -> Fallible<()> {
        unsafe {
            self.device.end_command_buffer(cmd).map_err(err("end command buffer"))?;
            let value = self.signal_value();
            let cmds = [vk::CommandBufferSubmitInfo::default().command_buffer(cmd)];
            let signals = [vk::SemaphoreSubmitInfo::default().semaphore(self.timeline).value(value).stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS)];
            self.device.queue_submit2(self.queue, &[vk::SubmitInfo2::default().command_buffer_infos(&cmds).signal_semaphore_infos(&signals)], vk::Fence::null()).map_err(err("submit"))?;
            self.wait(value)?;
            self.device.free_command_buffers(self.command_pool, &[cmd]);
        }
        Ok(())
    }

    pub fn signal_value(&mut self) -> u64 { self.timeline_value += 1; self.timeline_value }

    pub fn completed(&self) -> u64 { unsafe { self.device.get_semaphore_counter_value(self.timeline).unwrap_or(0) } }

    pub fn wait(&self, value: u64) -> Fallible<()> {
        if value == 0 || self.completed() >= value { return Ok(()); }
        let semaphores = [self.timeline];
        let values = [value];
        unsafe { self.device.wait_semaphores(&vk::SemaphoreWaitInfo::default().semaphores(&semaphores).values(&values), u64::MAX).map_err(err("wait for GPU")) }
    }

    pub fn flush(&self) -> Fallible<()> { unsafe { self.device.queue_wait_idle(self.queue).map_err(err("wait for queue")) } }
}

pub fn take(free: &mut Vec<(u64, u64)>, size: u64, align: u64) -> Option<u64> {
    for i in 0..free.len() {
        let (start, len) = free[i];
        let aligned = start.div_ceil(align) * align;
        let pad = aligned - start;
        if len >= size + pad {
            let end = start + len;
            free.remove(i);
            if pad > 0 { free.insert(i, (start, pad)); }
            let rest = end - (aligned + size);
            if rest > 0 { free.push((aligned + size, rest)); free.sort_unstable_by_key(|r| r.0); }
            return Some(aligned);
        }
    }
    None
}

pub fn give(free: &mut Vec<(u64, u64)>, start: u64, size: u64) {
    if size == 0 { return; }
    free.push((start, size));
    free.sort_unstable_by_key(|r| r.0);
    let mut merged: Vec<(u64, u64)> = Vec::with_capacity(free.len());
    for &(s, l) in free.iter() {
        if let Some(last) = merged.last_mut() { if last.0 + last.1 == s { last.1 += l; continue; } }
        merged.push((s, l));
    }
    *free = merged;
}

pub struct Buffer { pub buffer: vk::Buffer, pub allocation: Allocation, pub address: u64, pub size: u64, pub mapped: *mut u8 }

pub fn barrier(gpu: &Gpu, cmd: vk::CommandBuffer) {
    let b = [vk::MemoryBarrier2::default().src_stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS).src_access_mask(vk::AccessFlags2::MEMORY_WRITE).dst_stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS).dst_access_mask(vk::AccessFlags2::MEMORY_READ | vk::AccessFlags2::MEMORY_WRITE)];
    unsafe { gpu.device.cmd_pipeline_barrier2(cmd, &vk::DependencyInfo::default().memory_barriers(&b)); }
}

pub fn image_barrier(gpu: &Gpu, cmd: vk::CommandBuffer, images: &[(vk::Image, vk::ImageLayout, vk::ImageLayout, u32, u32)]) {
    let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: vk::REMAINING_MIP_LEVELS, base_array_layer: 0, layer_count: 1 };
    let barriers: Vec<vk::ImageMemoryBarrier2> = images.iter().map(|&(image, old, new, src_family, dst_family)| vk::ImageMemoryBarrier2::default()
        .src_stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS).src_access_mask(vk::AccessFlags2::MEMORY_WRITE | vk::AccessFlags2::MEMORY_READ)
        .dst_stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS).dst_access_mask(vk::AccessFlags2::MEMORY_READ | vk::AccessFlags2::MEMORY_WRITE)
        .old_layout(old).new_layout(new).src_queue_family_index(src_family).dst_queue_family_index(dst_family).image(image).subresource_range(range)).collect();
    unsafe { gpu.device.cmd_pipeline_barrier2(cmd, &vk::DependencyInfo::default().image_memory_barriers(&barriers)); }
}
