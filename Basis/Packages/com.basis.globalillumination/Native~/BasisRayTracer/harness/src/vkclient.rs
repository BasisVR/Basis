use BasisRayTracingBridge::*;
use ash::vk;
use basis_rt_protocol::*;
use std::ffi::{CStr, c_char};

pub struct Image { pub image: vk::Image, _memory: vk::DeviceMemory, pub width: u32, pub height: u32, pub handle: Box<(vk::Image, vk::ImageLayout)> }

impl Image { pub fn pointer(&self) -> *mut std::ffi::c_void { &*self.handle as *const (vk::Image, vk::ImageLayout) as *mut std::ffi::c_void } }

pub struct Staging { pub buffer: vk::Buffer, _memory: vk::DeviceMemory, pub mapped: *mut u8 }

pub struct Client {
    pub _instance: ash::Instance,
    pub device: ash::Device,
    pub queue: vk::Queue,
    cmd: vk::CommandBuffer,
    pub gbuffer: Image,
    pub upload: Image,
    pub result: Image,
    pub second: Image,
    pub gstage: Staging,
    pub ustage: Staging,
    pub readback: Staging,
    rpitch: u32,
}

fn memory_type(props: &vk::PhysicalDeviceMemoryProperties, bits: u32, flags: vk::MemoryPropertyFlags) -> u32 {
    (0..props.memory_type_count).find(|&i| bits & (1 << i) != 0 && props.memory_types[i as usize].property_flags.contains(flags)).expect("memory type")
}

impl Client {
    pub unsafe fn new(w: u32, h: u32, rpitch: u32) -> (Client, Backend, Adapter) {
        unsafe {
            let entry = ash::Entry::load().expect("Vulkan loader");
            let app = vk::ApplicationInfo::default().application_name(c"basis-rt-harness").api_version(vk::API_VERSION_1_2);
            let instance = entry.create_instance(&vk::InstanceCreateInfo::default().application_info(&app), None).expect("instance");
            let physical = instance.enumerate_physical_devices().unwrap().into_iter().max_by_key(|p| (instance.get_physical_device_properties(*p).device_type == vk::PhysicalDeviceType::DISCRETE_GPU) as u32).expect("device");
            let family = instance.get_physical_device_queue_family_properties(physical).iter().position(|q| q.queue_flags.contains(vk::QueueFlags::GRAPHICS)).unwrap() as u32;
            let names: Vec<&CStr> = if cfg!(windows) { vec![c"VK_KHR_external_memory_win32", c"VK_KHR_external_semaphore_win32"] } else { vec![c"VK_KHR_external_memory_fd", c"VK_KHR_external_semaphore_fd"] };
            let names: Vec<*const c_char> = names.iter().map(|n| n.as_ptr()).collect();
            let priorities = [1.0f32];
            let queues = [vk::DeviceQueueCreateInfo::default().queue_family_index(family).queue_priorities(&priorities)];
            let mut timeline = vk::PhysicalDeviceTimelineSemaphoreFeatures::default().timeline_semaphore(true);
            let device = instance.create_device(physical, &vk::DeviceCreateInfo::default().queue_create_infos(&queues).enabled_extension_names(&names).push_next(&mut timeline), None).expect("device");
            let queue = device.get_device_queue(family, 0);
            let props = instance.get_physical_device_memory_properties(physical);
            let image = |width: u32, height: u32, format: vk::Format| -> Image {
                let info = vk::ImageCreateInfo::default().image_type(vk::ImageType::TYPE_2D).format(format).extent(vk::Extent3D { width, height, depth: 1 }).mip_levels(1).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL)
                    .usage(vk::ImageUsageFlags::TRANSFER_SRC | vk::ImageUsageFlags::TRANSFER_DST | vk::ImageUsageFlags::SAMPLED | vk::ImageUsageFlags::COLOR_ATTACHMENT);
                let image = device.create_image(&info, None).unwrap();
                let req = device.get_image_memory_requirements(image);
                let memory = device.allocate_memory(&vk::MemoryAllocateInfo::default().allocation_size(req.size).memory_type_index(memory_type(&props, req.memory_type_bits, vk::MemoryPropertyFlags::DEVICE_LOCAL)), None).unwrap();
                device.bind_image_memory(image, memory, 0).unwrap();
                Image { image, _memory: memory, width, height, handle: Box::new((image, vk::ImageLayout::GENERAL)) }
            };
            let staging = |size: u64| -> Staging {
                let buffer = device.create_buffer(&vk::BufferCreateInfo::default().size(size).usage(vk::BufferUsageFlags::TRANSFER_SRC | vk::BufferUsageFlags::TRANSFER_DST), None).unwrap();
                let req = device.get_buffer_memory_requirements(buffer);
                let memory = device.allocate_memory(&vk::MemoryAllocateInfo::default().allocation_size(req.size).memory_type_index(memory_type(&props, req.memory_type_bits, vk::MemoryPropertyFlags::HOST_VISIBLE | vk::MemoryPropertyFlags::HOST_COHERENT)), None).unwrap();
                device.bind_buffer_memory(buffer, memory, 0).unwrap();
                let mapped = device.map_memory(memory, 0, vk::WHOLE_SIZE, vk::MemoryMapFlags::empty()).unwrap() as *mut u8;
                Staging { buffer, _memory: memory, mapped }
            };
            let pool = device.create_command_pool(&vk::CommandPoolCreateInfo::default().queue_family_index(family).flags(vk::CommandPoolCreateFlags::RESET_COMMAND_BUFFER), None).unwrap();
            let cmd = device.allocate_command_buffers(&vk::CommandBufferAllocateInfo::default().command_pool(pool).level(vk::CommandBufferLevel::PRIMARY).command_buffer_count(1)).unwrap()[0];
            let client = Client {
                gbuffer: image(w * 2, h * 2, vk::Format::R16G16B16A16_UINT), upload: image(UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, vk::Format::R16G16B16A16_UINT), result: image(w, h, vk::Format::R16G16B16A16_SFLOAT), second: image(w, h, vk::Format::R16G16B16A16_SFLOAT),
                gstage: staging((w * 2 * 8) as u64 * (h * 2) as u64), ustage: staging(UPLOAD_WIDTH as u64 * 8 * UPLOAD_PHYSICAL_HEIGHT as u64), readback: staging(rpitch as u64 * h as u64),
                _instance: instance.clone(), device: device.clone(), queue, cmd, rpitch,
            };
            client.run(|d, cmd| for i in [&client.gbuffer, &client.upload, &client.result, &client.second] { layout(d, cmd, i.image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL); });
            let resolve: BasisRayTracingBridge::vulkan::Resolve = Box::new(|p| Some(*(p as *const (vk::Image, vk::ImageLayout))));
            let (backend, adapter) = backend_vulkan(instance, physical, device, queue, family, resolve).unwrap();
            (client, backend, adapter)
        }
    }

    fn run(&self, body: impl FnOnce(&ash::Device, vk::CommandBuffer)) {
        unsafe {
            self.device.reset_command_buffer(self.cmd, vk::CommandBufferResetFlags::empty()).unwrap();
            self.device.begin_command_buffer(self.cmd, &vk::CommandBufferBeginInfo::default()).unwrap();
            body(&self.device, self.cmd);
            self.device.end_command_buffer(self.cmd).unwrap();
            let cmds = [self.cmd];
            self.device.queue_submit(self.queue, &[vk::SubmitInfo::default().command_buffers(&cmds)], vk::Fence::null()).unwrap();
            self.device.queue_wait_idle(self.queue).unwrap();
        }
    }

    pub fn upload(&self, rows: u32) {
        self.run(|d, cmd| unsafe {
            let layers = vk::ImageSubresourceLayers { aspect_mask: vk::ImageAspectFlags::COLOR, mip_level: 0, base_array_layer: 0, layer_count: 1 };
            layout(d, cmd, self.gbuffer.image, vk::ImageLayout::GENERAL, vk::ImageLayout::TRANSFER_DST_OPTIMAL);
            d.cmd_copy_buffer_to_image(cmd, self.gstage.buffer, self.gbuffer.image, vk::ImageLayout::TRANSFER_DST_OPTIMAL, &[vk::BufferImageCopy { buffer_offset: 0, buffer_row_length: 0, buffer_image_height: 0, image_subresource: layers, image_offset: vk::Offset3D::default(), image_extent: vk::Extent3D { width: self.gbuffer.width, height: self.gbuffer.height, depth: 1 } }]);
            layout(d, cmd, self.gbuffer.image, vk::ImageLayout::TRANSFER_DST_OPTIMAL, vk::ImageLayout::GENERAL);
            if rows > 0 {
                layout(d, cmd, self.upload.image, vk::ImageLayout::GENERAL, vk::ImageLayout::TRANSFER_DST_OPTIMAL);
                d.cmd_copy_buffer_to_image(cmd, self.ustage.buffer, self.upload.image, vk::ImageLayout::TRANSFER_DST_OPTIMAL, &[vk::BufferImageCopy { buffer_offset: 0, buffer_row_length: 0, buffer_image_height: 0, image_subresource: layers, image_offset: vk::Offset3D::default(), image_extent: vk::Extent3D { width: UPLOAD_WIDTH, height: rows * 2, depth: 1 } }]);
                layout(d, cmd, self.upload.image, vk::ImageLayout::TRANSFER_DST_OPTIMAL, vk::ImageLayout::GENERAL);
            }
        });
    }

    pub fn read(&self) {
        self.run(|d, cmd| unsafe {
            let layers = vk::ImageSubresourceLayers { aspect_mask: vk::ImageAspectFlags::COLOR, mip_level: 0, base_array_layer: 0, layer_count: 1 };
            d.cmd_copy_image_to_buffer(cmd, self.result.image, vk::ImageLayout::GENERAL, self.readback.buffer, &[vk::BufferImageCopy { buffer_offset: 0, buffer_row_length: self.rpitch / 8, buffer_image_height: 0, image_subresource: layers, image_offset: vk::Offset3D::default(), image_extent: vk::Extent3D { width: self.result.width, height: self.result.height, depth: 1 } }]);
        });
    }
}

fn layout(d: &ash::Device, cmd: vk::CommandBuffer, image: vk::Image, old: vk::ImageLayout, new: vk::ImageLayout) {
    let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: 1, base_array_layer: 0, layer_count: 1 };
    let b = vk::ImageMemoryBarrier::default().src_access_mask(vk::AccessFlags::MEMORY_WRITE).dst_access_mask(vk::AccessFlags::MEMORY_READ | vk::AccessFlags::MEMORY_WRITE).old_layout(old).new_layout(new)
        .src_queue_family_index(vk::QUEUE_FAMILY_IGNORED).dst_queue_family_index(vk::QUEUE_FAMILY_IGNORED).image(image).subresource_range(range);
    unsafe { d.cmd_pipeline_barrier(cmd, vk::PipelineStageFlags::ALL_COMMANDS, vk::PipelineStageFlags::ALL_COMMANDS, vk::DependencyFlags::empty(), &[], &[], &[b]); }
}
