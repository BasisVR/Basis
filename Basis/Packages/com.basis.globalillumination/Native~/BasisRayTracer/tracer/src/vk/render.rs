use super::device::*;
use super::interop::*;
use super::scene::*;
use crate::{Fallible, os};
use ash::vk;
use basis_rt_protocol::*;

const TRACE: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/trace.spv"));
const COMPUTE: [(&str, &[u8]); 11] = [
    ("CopyVertices", include_bytes!(concat!(env!("OUT_DIR"), "/CopyVertices.spv"))),
    ("CopyIndices", include_bytes!(concat!(env!("OUT_DIR"), "/CopyIndices.spv"))),
    ("CopyPositions", include_bytes!(concat!(env!("OUT_DIR"), "/CopyPositions.spv"))),
    ("TexturePixels", include_bytes!(concat!(env!("OUT_DIR"), "/TexturePixels.spv"))),
    ("Downsample", include_bytes!(concat!(env!("OUT_DIR"), "/Downsample.spv"))),
    ("Temporal", include_bytes!(concat!(env!("OUT_DIR"), "/Temporal.spv"))),
    ("Wavelet", include_bytes!(concat!(env!("OUT_DIR"), "/Wavelet.spv"))),
    ("CacheResolve", include_bytes!(concat!(env!("OUT_DIR"), "/CacheResolve.spv"))),
    ("ProbeResolve", include_bytes!(concat!(env!("OUT_DIR"), "/ProbeResolve.spv"))),
    ("SubmeshBounds", include_bytes!(concat!(env!("OUT_DIR"), "/SubmeshBounds.spv"))),
    ("SubmeshBoundsClear", include_bytes!(concat!(env!("OUT_DIR"), "/SubmeshBoundsClear.spv"))),
];
const P_COPY_VERTICES: usize = 0;
const P_COPY_INDICES: usize = 1;
const P_COPY_POSITIONS: usize = 2;
const P_TEXTURE_PIXELS: usize = 3;
const P_DOWNSAMPLE: usize = 4;
const P_TEMPORAL: usize = 5;
const P_WAVELET: usize = 6;
const P_RESOLVE: usize = 7;
const P_PROBE_RESOLVE: usize = 8;
const P_SUBMESH_BOUNDS: usize = 9;
const P_SUBMESH_BOUNDS_CLEAR: usize = 10;

pub const SCRATCH_PER_SLOT: u32 = 4096;
pub const HEAP_SIZE: u32 = SCRATCH_DESCRIPTORS + SCRATCH_PER_SLOT * RING as u32;
pub const SCRATCH_DESCRIPTORS: u32 = 1024 + 4096 * 2;
pub const HEAP_UPLOAD: u32 = 7;
pub const HEAP_RING: u32 = 9;
pub const HEAP_INSTANCES: u32 = 16;
pub const HEAP_MATERIALS: u32 = 24;
pub const HEAP_LIGHTS: u32 = 32;
pub const HEAP_EMITTERS: u32 = 40;
pub const HEAP_CACHE: u32 = 48;
pub const HEAP_DIRTY: u32 = 56;
pub const CACHE_SIZE: u64 = 1 << 20;
pub const CTX_GBUFFER: u32 = 0;
pub const CTX_RADIANCE_UAV: u32 = 2;
pub const CTX_HISTORY_UAV: u32 = 3;
pub const CTX_MOMENTS_UAV: u32 = 5;
pub const CTX_GEOMETRY_UAV: u32 = 7;
pub const CTX_FILTER_UAV: u32 = 9;
pub const CTX_PROBES: u32 = 11;
pub const CTX_PROBE_RAYS: u32 = 13;
pub const CTX_RESERVOIRS: u32 = 14;
pub const CTX_RESERVOIR_SCRATCH: u32 = 16;
pub const PROBE_RAYS: u32 = 16;
pub const PROBE_STRIDE: u64 = 96;
pub const TIMESTAMPS: u32 = 8;
pub fn context_base(context: usize) -> u32 { 64 + context as u32 * 32 }

const fn align(x: u64, to: u64) -> u64 { x.div_ceil(to) * to }
const CONSTANTS_BYTES: u64 = 1024;
const INSTANCES_OFFSET: u64 = align(CONSTANTS_BYTES, 768);
const MATERIALS_OFFSET: u64 = align(INSTANCES_OFFSET + (MAX_INSTANCES * 96) as u64, 768);
const LIGHTS_OFFSET: u64 = align(MATERIALS_OFFSET + (MAX_MATERIALS * 96) as u64, 256);
const EMITTERS_OFFSET: u64 = align(LIGHTS_OFFSET + (MAX_LIGHTS * 64) as u64, 256);
const DIRTY_OFFSET: u64 = align(EMITTERS_OFFSET + (MAX_EMITTERS * 16) as u64, 256);
const TLAS_OFFSET: u64 = align(DIRTY_OFFSET + (MAX_DIRTY * 16) as u64, 256);
const TABLES_OFFSET: u64 = TLAS_OFFSET + (MAX_INSTANCES * 64) as u64;
const BOUNDS_LIST_OFFSET: u64 = TABLES_OFFSET + MAX_MESHES as u64 * 16 + SUBMESH_CAPACITY * 16;
const SLOT_STRIDE: u64 = align(BOUNDS_LIST_OFFSET + MAX_EMITTERS as u64 * 16, 65536);
const _: () = assert!(SLOT_STRIDE <= u32::MAX as u64 / RING as u64);

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Extra { pub slot: u32, pub gbuffer_index: u32, pub history_read: u32, pub history_write: u32, pub delta_time: f32, pub debug_view: u32, pub frame_index: u32, pub context_base: u32 }

#[repr(C)]
#[derive(Clone, Copy)]
struct InstanceDesc { transform: [f32; 12], index_mask: u32, sbt_flags: u32, reference: u64 }
const _: () = assert!(std::mem::size_of::<InstanceDesc>() == 64);

fn tiles(width: u32, height: u32, views: u32) -> (u32, u32) { (width.div_ceil(16) * views, height.div_ceil(16)) }

pub struct Texture { pub image: vk::Image, pub allocation: Allocation, pub view: vk::ImageView }

pub struct Context {
    pub gbuffer: [SharedImage; 2],
    pub output: [SharedImage; 2],
    pub textures: Vec<Texture>,
    pub width: u32,
    pub height: u32,
    pub views: u32,
    pub base: u32,
    pub history_index: u32,
    pub history_valid: bool,
    pub probes: [Buffer; 2],
    pub probe_rays: Buffer,
    pub reservoirs: [Buffer; 2],
    pub reservoir_scratch: Buffer,
    pub last_time: f32,
}

pub struct Renderer {
    pub layout: vk::PipelineLayout,
    pub set_layouts: [vk::DescriptorSetLayout; 2],
    pub sets: [vk::DescriptorSet; 2],
    pub pools: [vk::DescriptorPool; 2],
    pub samplers: [vk::Sampler; 3],
    pub rt: vk::Pipeline,
    pub sbt: Buffer,
    pub regions: [vk::StridedDeviceAddressRegionKHR; 4],
    pub compute: Vec<vk::Pipeline>,
    pub cache: Vec<Buffer>,
    pub queries: vk::QueryPool,
    pub timings: [u32; 8],
    pub commands: Vec<vk::CommandBuffer>,
    pub ring: Buffer,
    pub slot_fences: [u64; RING],
    pub contexts: Vec<Option<Context>>,
    pub upload: [SharedImage; 2],
    pub fence_unity: SharedSemaphore,
    pub fence_tracer: SharedSemaphore,
    pub scratch: std::cell::Cell<(u32, u32)>,
}

fn module(gpu: &Gpu, code: &[u8]) -> Fallible<vk::ShaderModule> {
    let words: Vec<u32> = code.chunks_exact(4).map(|c| u32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect();
    unsafe { gpu.device.create_shader_module(&vk::ShaderModuleCreateInfo::default().code(&words), None).map_err(err("create shader module")) }
}

pub struct Heap(pub vk::DescriptorSet);

impl Heap {
    pub fn buffer(&self, gpu: &Gpu, index: u32, buffer: vk::Buffer, offset: u64, range: u64) {
        let info = [vk::DescriptorBufferInfo { buffer, offset, range }];
        let write = vk::WriteDescriptorSet::default().dst_set(self.0).dst_binding(0).dst_array_element(index).descriptor_type(vk::DescriptorType::STORAGE_BUFFER).buffer_info(&info);
        unsafe { gpu.device.update_descriptor_sets(&[write], &[]); }
    }
    pub fn sampled(&self, gpu: &Gpu, index: u32, view: vk::ImageView) {
        let info = [vk::DescriptorImageInfo { sampler: vk::Sampler::null(), image_view: view, image_layout: vk::ImageLayout::GENERAL }];
        let write = vk::WriteDescriptorSet::default().dst_set(self.0).dst_binding(0).dst_array_element(index).descriptor_type(vk::DescriptorType::SAMPLED_IMAGE).image_info(&info);
        unsafe { gpu.device.update_descriptor_sets(&[write], &[]); }
    }
    pub fn storage(&self, gpu: &Gpu, index: u32, view: vk::ImageView) {
        let info = [vk::DescriptorImageInfo { sampler: vk::Sampler::null(), image_view: view, image_layout: vk::ImageLayout::GENERAL }];
        let write = vk::WriteDescriptorSet::default().dst_set(self.0).dst_binding(0).dst_array_element(index).descriptor_type(vk::DescriptorType::STORAGE_IMAGE).image_info(&info);
        unsafe { gpu.device.update_descriptor_sets(&[write], &[]); }
    }
}

impl Renderer {
    pub fn new(gpu: &mut Gpu, scene: &Scene, interop: &Interop, link: &os::Link) -> Fallible<Renderer> {
        unsafe {
            let d = &gpu.device;
            let sampler = |filter: vk::Filter, mip: vk::SamplerMipmapMode, address: vk::SamplerAddressMode, anisotropy: bool| d.create_sampler(&vk::SamplerCreateInfo::default().mag_filter(filter).min_filter(filter).mipmap_mode(mip)
                .address_mode_u(address).address_mode_v(address).address_mode_w(address).anisotropy_enable(anisotropy).max_anisotropy(8.0).max_lod(1000.0), None).map_err(err("create sampler"));
            let samplers = [sampler(vk::Filter::LINEAR, vk::SamplerMipmapMode::LINEAR, vk::SamplerAddressMode::REPEAT, true)?, sampler(vk::Filter::LINEAR, vk::SamplerMipmapMode::LINEAR, vk::SamplerAddressMode::CLAMP_TO_EDGE, false)?, sampler(vk::Filter::NEAREST, vk::SamplerMipmapMode::NEAREST, vk::SamplerAddressMode::CLAMP_TO_EDGE, false)?];
            let stages = vk::ShaderStageFlags::COMPUTE | vk::ShaderStageFlags::RAYGEN_KHR | vk::ShaderStageFlags::MISS_KHR | vk::ShaderStageFlags::CLOSEST_HIT_KHR | vk::ShaderStageFlags::ANY_HIT_KHR;
            let s0 = [samplers[0]];
            let s1 = [samplers[1]];
            let s2 = [samplers[2]];
            let bindings0 = [
                vk::DescriptorSetLayoutBinding::default().binding(0).descriptor_type(vk::DescriptorType::UNIFORM_BUFFER_DYNAMIC).descriptor_count(1).stage_flags(stages),
                vk::DescriptorSetLayoutBinding::default().binding(1).descriptor_type(vk::DescriptorType::SAMPLER).descriptor_count(1).stage_flags(stages).immutable_samplers(&s0),
                vk::DescriptorSetLayoutBinding::default().binding(2).descriptor_type(vk::DescriptorType::SAMPLER).descriptor_count(1).stage_flags(stages).immutable_samplers(&s1),
                vk::DescriptorSetLayoutBinding::default().binding(3).descriptor_type(vk::DescriptorType::SAMPLER).descriptor_count(1).stage_flags(stages).immutable_samplers(&s2),
                vk::DescriptorSetLayoutBinding::default().binding(4).descriptor_type(vk::DescriptorType::ACCELERATION_STRUCTURE_KHR).descriptor_count(1).stage_flags(stages),
            ];
            let layout0 = d.create_descriptor_set_layout(&vk::DescriptorSetLayoutCreateInfo::default().bindings(&bindings0), None).map_err(err("create set layout"))?;
            let mutable_types = [vk::DescriptorType::STORAGE_BUFFER, vk::DescriptorType::SAMPLED_IMAGE, vk::DescriptorType::STORAGE_IMAGE];
            let lists = [vk::MutableDescriptorTypeListEXT::default().descriptor_types(&mutable_types)];
            let bindings1 = [vk::DescriptorSetLayoutBinding::default().binding(0).descriptor_type(vk::DescriptorType::MUTABLE_EXT).descriptor_count(HEAP_SIZE).stage_flags(stages)];
            let flags1 = [vk::DescriptorBindingFlags::PARTIALLY_BOUND | vk::DescriptorBindingFlags::UPDATE_AFTER_BIND | vk::DescriptorBindingFlags::UPDATE_UNUSED_WHILE_PENDING];
            let mut binding_flags = vk::DescriptorSetLayoutBindingFlagsCreateInfo::default().binding_flags(&flags1);
            let mut mutable = vk::MutableDescriptorTypeCreateInfoEXT::default().mutable_descriptor_type_lists(&lists);
            let layout1 = d.create_descriptor_set_layout(&vk::DescriptorSetLayoutCreateInfo::default().flags(vk::DescriptorSetLayoutCreateFlags::UPDATE_AFTER_BIND_POOL).bindings(&bindings1).push_next(&mut binding_flags).push_next(&mut mutable), None).map_err(err("create heap layout"))?;
            let set_layouts = [layout0, layout1];
            let push = [vk::PushConstantRange { stage_flags: stages, offset: 0, size: 32 }];
            let layout = d.create_pipeline_layout(&vk::PipelineLayoutCreateInfo::default().set_layouts(&set_layouts).push_constant_ranges(&push), None).map_err(err("create pipeline layout"))?;
            let sizes0 = [vk::DescriptorPoolSize { ty: vk::DescriptorType::UNIFORM_BUFFER_DYNAMIC, descriptor_count: 1 }, vk::DescriptorPoolSize { ty: vk::DescriptorType::SAMPLER, descriptor_count: 3 }, vk::DescriptorPoolSize { ty: vk::DescriptorType::ACCELERATION_STRUCTURE_KHR, descriptor_count: 1 }];
            let pool0 = d.create_descriptor_pool(&vk::DescriptorPoolCreateInfo::default().max_sets(1).pool_sizes(&sizes0), None).map_err(err("create descriptor pool"))?;
            let sizes1 = [vk::DescriptorPoolSize { ty: vk::DescriptorType::MUTABLE_EXT, descriptor_count: HEAP_SIZE }];
            let mut mutable_pool = vk::MutableDescriptorTypeCreateInfoEXT::default().mutable_descriptor_type_lists(&lists);
            let pool1 = d.create_descriptor_pool(&vk::DescriptorPoolCreateInfo::default().flags(vk::DescriptorPoolCreateFlags::UPDATE_AFTER_BIND).max_sets(1).pool_sizes(&sizes1).push_next(&mut mutable_pool), None).map_err(err("create heap pool"))?;
            let set0 = d.allocate_descriptor_sets(&vk::DescriptorSetAllocateInfo::default().descriptor_pool(pool0).set_layouts(&[layout0])).map_err(err("allocate descriptor set"))?[0];
            let set1 = d.allocate_descriptor_sets(&vk::DescriptorSetAllocateInfo::default().descriptor_pool(pool1).set_layouts(&[layout1])).map_err(err("allocate heap"))?[0];

            let mut compute = Vec::new();
            for (name, code) in COMPUTE.iter() {
                let m = module(gpu, code)?;
                let entry = std::ffi::CString::new(*name).unwrap();
                let stage = vk::PipelineShaderStageCreateInfo::default().stage(vk::ShaderStageFlags::COMPUTE).module(m).name(&entry);
                let p = gpu.device.create_compute_pipelines(vk::PipelineCache::null(), &[vk::ComputePipelineCreateInfo::default().stage(stage).layout(layout)], None).map_err(|(_, e)| format!("create compute pipeline {name}: {e}"))?[0];
                gpu.device.destroy_shader_module(m, None);
                compute.push(p);
            }

            let m = module(gpu, TRACE)?;
            let names = [c"RayGen", c"Miss", c"ClosestHit", c"AnyHit"];
            let kinds = [vk::ShaderStageFlags::RAYGEN_KHR, vk::ShaderStageFlags::MISS_KHR, vk::ShaderStageFlags::CLOSEST_HIT_KHR, vk::ShaderStageFlags::ANY_HIT_KHR];
            let rt_stages: Vec<vk::PipelineShaderStageCreateInfo> = (0..4).map(|i| vk::PipelineShaderStageCreateInfo::default().stage(kinds[i]).module(m).name(names[i])).collect();
            let unused = vk::SHADER_UNUSED_KHR;
            let groups = [
                vk::RayTracingShaderGroupCreateInfoKHR::default().ty(vk::RayTracingShaderGroupTypeKHR::GENERAL).general_shader(0).closest_hit_shader(unused).any_hit_shader(unused).intersection_shader(unused),
                vk::RayTracingShaderGroupCreateInfoKHR::default().ty(vk::RayTracingShaderGroupTypeKHR::GENERAL).general_shader(1).closest_hit_shader(unused).any_hit_shader(unused).intersection_shader(unused),
                vk::RayTracingShaderGroupCreateInfoKHR::default().ty(vk::RayTracingShaderGroupTypeKHR::TRIANGLES_HIT_GROUP).general_shader(unused).closest_hit_shader(2).any_hit_shader(3).intersection_shader(unused),
            ];
            let info = vk::RayTracingPipelineCreateInfoKHR::default().stages(&rt_stages).groups(&groups).max_pipeline_ray_recursion_depth(1).layout(layout);
            let rt = gpu.rt.create_ray_tracing_pipelines(vk::DeferredOperationKHR::null(), vk::PipelineCache::null(), &[info], None).map_err(|(_, e)| format!("create ray tracing pipeline: {e}"))?[0];
            gpu.device.destroy_shader_module(m, None);
            let handle = gpu.handle_size as u64;
            let handles = gpu.rt.get_ray_tracing_shader_group_handles(rt, 0, 3, (handle * 3) as usize).map_err(err("get shader handles"))?;
            let stride = align(handle, gpu.handle_alignment as u64);
            let base = gpu.group_base_alignment as u64;
            let sbt = gpu.buffer(base * 4, vk::BufferUsageFlags::SHADER_BINDING_TABLE_KHR, vk::MemoryPropertyFlags::HOST_VISIBLE | vk::MemoryPropertyFlags::HOST_COHERENT)?;
            let sbt_base = align(sbt.address, base);
            let shift = (sbt_base - sbt.address) as usize;
            for i in 0..3 { std::ptr::copy_nonoverlapping(handles.as_ptr().add(i * handle as usize), sbt.mapped.add(shift + i * base as usize), handle as usize); }
            let regions = [
                vk::StridedDeviceAddressRegionKHR { device_address: sbt_base, stride, size: stride },
                vk::StridedDeviceAddressRegionKHR { device_address: sbt_base + base, stride, size: stride },
                vk::StridedDeviceAddressRegionKHR { device_address: sbt_base + base * 2, stride, size: stride },
                vk::StridedDeviceAddressRegionKHR::default(),
            ];

            let commands = gpu.device.allocate_command_buffers(&vk::CommandBufferAllocateInfo::default().command_pool(gpu.command_pool).level(vk::CommandBufferLevel::PRIMARY).command_buffer_count(RING as u32)).map_err(err("allocate command buffers"))?;
            let ring = gpu.buffer(SLOT_STRIDE * RING as u64, vk::BufferUsageFlags::STORAGE_BUFFER | vk::BufferUsageFlags::UNIFORM_BUFFER | vk::BufferUsageFlags::ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_KHR, vk::MemoryPropertyFlags::HOST_VISIBLE | vk::MemoryPropertyFlags::HOST_COHERENT)?;
            let queries = gpu.device.create_query_pool(&vk::QueryPoolCreateInfo::default().query_type(vk::QueryType::TIMESTAMP).query_count(TIMESTAMPS * RING as u32), None).map_err(err("create query pool"))?;
            gpu.device.reset_query_pool(queries, 0, TIMESTAMPS * RING as u32);
            let mut cache = Vec::new();
            for stride in [4u64, 4, 64, 64, 4, 16] { cache.push(gpu.buffer(CACHE_SIZE * stride, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?); }

            let ubo = [vk::DescriptorBufferInfo { buffer: ring.buffer, offset: 0, range: CONSTANTS_BYTES }];
            let structures = [scene.tlas];
            let mut as_write = vk::WriteDescriptorSetAccelerationStructureKHR::default().acceleration_structures(&structures);
            let writes = [
                vk::WriteDescriptorSet::default().dst_set(set0).dst_binding(0).descriptor_type(vk::DescriptorType::UNIFORM_BUFFER_DYNAMIC).buffer_info(&ubo),
                vk::WriteDescriptorSet::default().dst_set(set0).dst_binding(4).descriptor_type(vk::DescriptorType::ACCELERATION_STRUCTURE_KHR).descriptor_count(1).push_next(&mut as_write),
            ];
            gpu.device.update_descriptor_sets(&writes, &[]);
            let heap = Heap(set1);
            heap.buffer(gpu, HEAP_VERTICES, scene.vertices.buffer, 0, VERTEX_CAPACITY * 32);
            heap.buffer(gpu, HEAP_INDICES, scene.indices.buffer, 0, INDEX_CAPACITY * 4);
            heap.buffer(gpu, HEAP_VERTICES_UAV, scene.vertices.buffer, 0, VERTEX_CAPACITY * 32);
            heap.buffer(gpu, HEAP_INDICES_UAV, scene.indices.buffer, 0, INDEX_CAPACITY * 4);
            heap.buffer(gpu, HEAP_MESHES, scene.mesh_table.buffer, 0, MAX_MESHES as u64 * 16);
            heap.buffer(gpu, HEAP_SUBMESHES, scene.submesh_table.buffer, 0, SUBMESH_CAPACITY * 16);
            heap.buffer(gpu, HEAP_BOUNDS, scene.bounds.buffer, 0, SUBMESH_CAPACITY * 32);
            heap.buffer(gpu, HEAP_RING, ring.buffer, 0, SLOT_STRIDE * RING as u64);
            for (i, b) in cache.iter().enumerate() { heap.buffer(gpu, HEAP_CACHE + i as u32, b.buffer, 0, b.size); }
            for slot in 0..RING as u64 {
                let base = SLOT_STRIDE * slot;
                let s = slot as u32;
                heap.buffer(gpu, HEAP_INSTANCES + s, ring.buffer, base + INSTANCES_OFFSET, MAX_INSTANCES as u64 * 96);
                heap.buffer(gpu, HEAP_MATERIALS + s, ring.buffer, base + MATERIALS_OFFSET, MAX_MATERIALS as u64 * 96);
                heap.buffer(gpu, HEAP_LIGHTS + s, ring.buffer, base + LIGHTS_OFFSET, MAX_LIGHTS as u64 * 64);
                heap.buffer(gpu, HEAP_EMITTERS + s, ring.buffer, base + EMITTERS_OFFSET, MAX_EMITTERS as u64 * 16);
                heap.buffer(gpu, HEAP_DIRTY + s, ring.buffer, base + DIRTY_OFFSET, MAX_DIRTY as u64 * 16);
            }
            for i in 0..MAX_TEXTURES { heap.sampled(gpu, HEAP_TEXTURES + i, scene.dummy.2); }

            let upload = [interop.image(gpu, link, UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, SHARED_FORMAT_UINT)?, interop.image(gpu, link, UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, SHARED_FORMAT_UINT)?];
            for i in 0..2 { heap.sampled(gpu, HEAP_UPLOAD + i as u32, upload[i].view); }
            let fence_unity = interop.semaphore(gpu, link)?;
            let fence_tracer = interop.semaphore(gpu, link)?;
            let cmd = gpu.begin()?;
            for b in &cache { gpu.device.cmd_fill_buffer(cmd, b.buffer, 0, vk::WHOLE_SIZE, 0); }
            image_barrier(gpu, cmd, &[(upload[0].image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED), (upload[1].image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED)]);
            gpu.finish_now(cmd)?;
            Ok(Renderer {
                layout, set_layouts, sets: [set0, set1], pools: [pool0, pool1], samplers, rt, sbt, regions, compute, cache, queries, timings: [0; 8], commands, ring, slot_fences: [0; RING],
                contexts: (0..MAX_CONTEXTS).map(|_| None).collect(), upload, fence_unity, fence_tracer, scratch: std::cell::Cell::new((SCRATCH_DESCRIPTORS, 0)),
            })
        }
    }

    fn heap(&self) -> Heap { Heap(self.sets[1]) }

    fn push(&self, gpu: &Gpu, cmd: vk::CommandBuffer, constants: [u32; 8]) {
        let bytes: Vec<u8> = constants.iter().flat_map(|c| c.to_le_bytes()).collect();
        let stages = vk::ShaderStageFlags::COMPUTE | vk::ShaderStageFlags::RAYGEN_KHR | vk::ShaderStageFlags::MISS_KHR | vk::ShaderStageFlags::CLOSEST_HIT_KHR | vk::ShaderStageFlags::ANY_HIT_KHR;
        unsafe { gpu.device.cmd_push_constants(cmd, self.layout, stages, 0, &bytes); }
    }

    fn pipeline(&self, gpu: &Gpu, cmd: vk::CommandBuffer, index: usize) { unsafe { gpu.device.cmd_bind_pipeline(cmd, vk::PipelineBindPoint::COMPUTE, self.compute[index]); } }

    fn dispatch(&self, gpu: &Gpu, cmd: vk::CommandBuffer, x: u32, y: u32) { unsafe { gpu.device.cmd_dispatch(cmd, x, y, 1); } }

    fn trace(&self, gpu: &Gpu, cmd: vk::CommandBuffer, w: u32, h: u32) {
        unsafe {
            gpu.device.cmd_bind_pipeline(cmd, vk::PipelineBindPoint::RAY_TRACING_KHR, self.rt);
            gpu.rt.cmd_trace_rays(cmd, &self.regions[0], &self.regions[1], &self.regions[2], &self.regions[3], w.max(1), h.max(1), 1);
        }
    }

    pub fn record(&mut self, gpu: &mut Gpu, scene: &mut Scene, slot_ptr: *const Slot) -> Fallible<u32> {
        let params = unsafe { (*slot_ptr).params };
        let context = (params.context as usize).min(MAX_CONTEXTS - 1);
        let Some(ctx) = self.contexts[context].as_ref() else { return Err("frame for an unallocated context".into()); };
        let cb = ctx.base;
        let frame = params.frame;
        let slot = (frame % RING as u64) as usize;
        gpu.wait(self.slot_fences[slot])?;
        if self.slot_fences[slot] > 0 {
            let mut t = [0u64; TIMESTAMPS as usize];
            if unsafe { gpu.device.get_query_pool_results(self.queries, slot as u32 * TIMESTAMPS, &mut t, vk::QueryResultFlags::TYPE_64) }.is_ok() {
                let period = gpu.timestamp_period as f64;
                let us = |a: usize, b: usize| -> u32 { let (x, y) = (t[a], t[b]); if y > x { ((y - x) as f64 * period / 1000.0) as u32 } else { 0 } };
                self.timings = [us(0, 7), us(0, 1), us(1, 2), us(2, 3), us(3, 4), us(4, 5), us(5, 6), us(6, 7)];
            }
        }
        scene.collect(gpu, gpu.completed());
        self.scratch.set((SCRATCH_DESCRIPTORS + slot as u32 * SCRATCH_PER_SLOT, 0));
        let base = SLOT_STRIDE * slot as u64;
        let mapped = unsafe { self.ring.mapped.add(base as usize) };
        let address = self.ring.address + base;
        let k = params.parity & 1;
        let upload_index = (frame & 1) as u32;
        let instance_count = (params.instance_count as usize).min(MAX_INSTANCES);
        let reset = params.flags & FLAG_RESET_HISTORY != 0 || !ctx.history_valid;
        let delta_time = if ctx.last_time > 0.0 { (params.time - ctx.last_time).clamp(0.001, 0.1) } else { 1.0 / 90.0 };
        let history_read = ctx.history_index;
        let history_write = history_read ^ 1;
        unsafe {
            let src = &*slot_ptr;
            let mut p = params;
            if reset { p.flags |= FLAG_RESET_HISTORY; }
            std::ptr::copy_nonoverlapping(&p as *const FrameParams as *const u8, mapped, std::mem::size_of::<FrameParams>());
            let extra = Extra { slot: slot as u32, gbuffer_index: k, history_read, history_write, delta_time, debug_view: 0, frame_index: frame as u32, context_base: cb };
            std::ptr::copy_nonoverlapping(&extra as *const Extra as *const u8, mapped.add(std::mem::size_of::<FrameParams>()), std::mem::size_of::<Extra>());
            std::ptr::copy_nonoverlapping(src.instances.as_ptr() as *const u8, mapped.add(INSTANCES_OFFSET as usize), instance_count * 96);
            std::ptr::copy_nonoverlapping(src.materials.as_ptr() as *const u8, mapped.add(MATERIALS_OFFSET as usize), (params.material_count as usize).min(MAX_MATERIALS) * 96);
            std::ptr::copy_nonoverlapping(src.lights.as_ptr() as *const u8, mapped.add(LIGHTS_OFFSET as usize), (params.light_count as usize).min(MAX_LIGHTS) * 64);
            std::ptr::copy_nonoverlapping(src.emitters.as_ptr() as *const u8, mapped.add(EMITTERS_OFFSET as usize), (params.emitter_count as usize).min(MAX_EMITTERS) * 16);
            std::ptr::copy_nonoverlapping(src.dirty.as_ptr() as *const u8, mapped.add(DIRTY_OFFSET as usize), (params.dirty_count as usize).min(MAX_DIRTY) * 16);
        }
        let commands: Vec<Command> = unsafe { (&(*slot_ptr).commands)[..(params.command_count as usize).min(MAX_COMMANDS)].to_vec() };
        let mut tlas_count = 0u32;
        unsafe {
            let descs = mapped.add(TLAS_OFFSET as usize) as *mut InstanceDesc;
            for (i, inst) in (&(*slot_ptr).instances)[..instance_count].iter().enumerate() {
                let Some(m) = scene.meshes.get(&inst.mesh) else { continue; };
                if m.blas_address == 0 && !m.needs_build { continue; }
                *descs.add(tlas_count as usize) = InstanceDesc { transform: inst.transform, index_mask: (i as u32 & 0x00ff_ffff) | ((inst.mask & 0xff) << 24), sbt_flags: (vk::GeometryInstanceFlagsKHR::TRIANGLE_FACING_CULL_DISABLE.as_raw()) << 24, reference: 0 };
                tlas_count += 1;
            }
        }

        let cmd = self.commands[slot];
        let ctx_gbuffer = ctx.gbuffer[k as usize].image;
        let ctx_output = ctx.output[k as usize].image;
        let upload_image = self.upload[upload_index as usize].image;
        unsafe {
            gpu.device.reset_command_buffer(cmd, vk::CommandBufferResetFlags::empty()).map_err(err("reset command buffer"))?;
            gpu.device.begin_command_buffer(cmd, &vk::CommandBufferBeginInfo::default().flags(vk::CommandBufferUsageFlags::ONE_TIME_SUBMIT)).map_err(err("begin command buffer"))?;
            gpu.device.cmd_reset_query_pool(cmd, self.queries, slot as u32 * TIMESTAMPS, TIMESTAMPS);
            for point in [vk::PipelineBindPoint::COMPUTE, vk::PipelineBindPoint::RAY_TRACING_KHR] {
                gpu.device.cmd_bind_descriptor_sets(cmd, point, self.layout, 0, &self.sets, &[base as u32]);
            }
            let external = vk::QUEUE_FAMILY_EXTERNAL;
            let g = vk::ImageLayout::GENERAL;
            image_barrier(gpu, cmd, &[(ctx_gbuffer, g, g, external, gpu.family), (upload_image, g, g, external, gpu.family), (ctx_output, g, g, external, gpu.family)]);
            gpu.device.cmd_write_timestamp2(cmd, vk::PipelineStageFlags2::ALL_COMMANDS, self.queries, slot as u32 * TIMESTAMPS);
        }
        let mut vertices_written = false;
        let mut indices_written = false;
        let mut pending_error = None;
        let fence_hint = gpu.timeline_value + 1;
        for c in &commands {
            if let Err(e) = self.command(gpu, scene, cmd, c, upload_index, &mut vertices_written, &mut indices_written, fence_hint) { pending_error = Some(e); }
        }
        unsafe {
            let mut need_barrier = vertices_written || indices_written;
            if scene.tables_dirty {
                let tables = mapped.add(TABLES_OFFSET as usize);
                std::ptr::copy_nonoverlapping(scene.mesh_cpu.as_ptr() as *const u8, tables, MAX_MESHES as usize * 16);
                std::ptr::copy_nonoverlapping(scene.submesh_cpu.as_ptr() as *const u8, tables.add(MAX_MESHES as usize * 16), SUBMESH_CAPACITY as usize * 16);
                gpu.device.cmd_copy_buffer(cmd, self.ring.buffer, scene.mesh_table.buffer, &[vk::BufferCopy { src_offset: base + TABLES_OFFSET, dst_offset: 0, size: MAX_MESHES as u64 * 16 }]);
                gpu.device.cmd_copy_buffer(cmd, self.ring.buffer, scene.submesh_table.buffer, &[vk::BufferCopy { src_offset: base + TABLES_OFFSET + MAX_MESHES as u64 * 16, dst_offset: 0, size: SUBMESH_CAPACITY * 16 }]);
                need_barrier = true;
                scene.tables_dirty = false;
            }
            if need_barrier { barrier(gpu, cmd); }
            let list_ptr = mapped.add(BOUNDS_LIST_OFFSET as usize) as *mut [u32; 4];
            let mut bounds_count = 0u32;
            let mut bounds_chunks = 0u32;
            for e in &(&(*slot_ptr).emitters)[..(params.emitter_count as usize).min(MAX_EMITTERS)] {
                if e.instance as usize >= instance_count { continue; }
                let Some(m) = scene.meshes.get(&(*slot_ptr).instances[e.instance as usize].mesh) else { continue; };
                let Some(s) = m.submeshes.get(e.geometry as usize) else { continue; };
                let at = m.submesh_offset as usize + e.geometry as usize;
                if !m.committed || at >= scene.bounds_valid.len() || scene.bounds_valid[at] { continue; }
                scene.bounds_valid[at] = true;
                *list_ptr.add(bounds_count as usize) = [at as u32, m.vertex_offset as u32, (m.index_offset + s.index_start as u64) as u32, s.index_count];
                bounds_count += 1;
                bounds_chunks = bounds_chunks.max(s.index_count.div_ceil(1024));
            }
            if bounds_count > 0 {
                self.pipeline(gpu, cmd, P_SUBMESH_BOUNDS_CLEAR);
                self.push(gpu, cmd, [(base + BOUNDS_LIST_OFFSET) as u32, bounds_count, 0, 0, 0, 0, 0, 0]);
                self.dispatch(gpu, cmd, bounds_count.div_ceil(64), 1);
                barrier(gpu, cmd);
                self.pipeline(gpu, cmd, P_SUBMESH_BOUNDS);
                self.push(gpu, cmd, [(base + BOUNDS_LIST_OFFSET) as u32, bounds_count, 0, 0, 0, 0, 0, 0]);
                self.dispatch(gpu, cmd, bounds_chunks.max(1), bounds_count);
                barrier(gpu, cmd);
            }
        }
        scene.build(gpu, cmd, fence_hint)?;
        unsafe {
            let descs = mapped.add(TLAS_OFFSET as usize) as *mut InstanceDesc;
            let mut write = 0u32;
            for read in 0..tlas_count {
                let d = *descs.add(read as usize);
                let index = (d.index_mask & 0x00ff_ffff) as usize;
                let mesh = (*slot_ptr).instances[index].mesh;
                let Some(m) = scene.meshes.get(&mesh) else { continue; };
                if m.blas_address == 0 { continue; }
                let mut d = d;
                d.reference = m.blas_address;
                *descs.add(write as usize) = d;
                write += 1;
            }
            tlas_count = write;
        }
        scene.build_tlas(gpu, cmd, address + TLAS_OFFSET, tlas_count);
        let stamp = |i: u32| unsafe { gpu.device.cmd_write_timestamp2(cmd, vk::PipelineStageFlags2::ALL_COMMANDS, self.queries, slot as u32 * TIMESTAMPS + i); };
        stamp(1);
        let width = params.width.min(ctx.width);
        let height = params.height.min(ctx.height);
        let views = params.view_count.clamp(1, 2).min(ctx.views);
        let full = width * views;
        if tlas_count > 0 {
            let (half_width, half_height) = (full.div_ceil(2).max(1), height.div_ceil(2));
            self.push(gpu, cmd, [0, half_height, 0, 0, 0, 0, 0, 0]);
            self.trace(gpu, cmd, half_width, half_height + 4096u32.div_ceil(half_width));
        }
        barrier(gpu, cmd);
        stamp(2);
        self.pipeline(gpu, cmd, P_RESOLVE);
        self.dispatch(gpu, cmd, (CACHE_SIZE / 64) as u32, 1);
        barrier(gpu, cmd);
        stamp(3);
        let (tiles_x, tiles_y) = tiles(width, height, views);
        if tlas_count > 0 && params.flags & FLAG_PROBES != 0 {
            self.push(gpu, cmd, [2, 0, 0, 0, 0, 0, 0, 0]);
            self.trace(gpu, cmd, tiles_x * PROBE_RAYS, tiles_y);
            barrier(gpu, cmd);
            self.pipeline(gpu, cmd, P_PROBE_RESOLVE);
            self.dispatch(gpu, cmd, tiles_x.div_ceil(8), tiles_y.div_ceil(8));
            barrier(gpu, cmd);
        }
        stamp(4);
        if tlas_count > 0 {
            self.push(gpu, cmd, [1, 0, 0, 0, 0, 0, 0, 0]);
            self.trace(gpu, cmd, full, height);
            barrier(gpu, cmd);
            self.push(gpu, cmd, [4, 0, 0, 0, 0, 0, 0, 0]);
            self.trace(gpu, cmd, full, height);
        }
        barrier(gpu, cmd);
        stamp(5);
        let iterations = if params.flags & FLAG_DENOISE != 0 { 4 } else { 0 };
        let history_target = cb + CTX_HISTORY_UAV + history_write;
        let mut source = if iterations > 0 { cb + CTX_FILTER_UAV + 1 } else { history_target };
        self.pipeline(gpu, cmd, P_TEMPORAL);
        self.push(gpu, cmd, [source, 0, 0, 0, 0, 0, 0, 0]);
        self.dispatch(gpu, cmd, full.div_ceil(8), height.div_ceil(8));
        barrier(gpu, cmd);
        stamp(6);
        self.pipeline(gpu, cmd, P_WAVELET);
        for i in 0..iterations {
            let target = if i == 0 { history_target } else { cb + CTX_FILTER_UAV + (i & 1 ^ 1) };
            self.push(gpu, cmd, [source, target, 1u32 << i, i, 0, 0, 0, 0]);
            self.dispatch(gpu, cmd, full.div_ceil(8), height.div_ceil(8));
            barrier(gpu, cmd);
            source = target;
        }
        let local = source - cb;
        let final_texture = ctx.textures[if local >= CTX_FILTER_UAV { 4 + (local - CTX_FILTER_UAV) as usize * 4 } else { 1 + (local - CTX_HISTORY_UAV) as usize * 4 }].image;
        unsafe {
            let layers = vk::ImageSubresourceLayers { aspect_mask: vk::ImageAspectFlags::COLOR, mip_level: 0, base_array_layer: 0, layer_count: 1 };
            let region = vk::ImageCopy { src_subresource: layers, src_offset: vk::Offset3D::default(), dst_subresource: layers, dst_offset: vk::Offset3D::default(), extent: vk::Extent3D { width: ctx.width * ctx.views, height: ctx.height, depth: 1 } };
            gpu.device.cmd_copy_image(cmd, final_texture, vk::ImageLayout::GENERAL, ctx_output, vk::ImageLayout::GENERAL, &[region]);
            let external = vk::QUEUE_FAMILY_EXTERNAL;
            let g = vk::ImageLayout::GENERAL;
            image_barrier(gpu, cmd, &[(ctx_gbuffer, g, g, gpu.family, external), (upload_image, g, g, gpu.family, external), (ctx_output, g, g, gpu.family, external)]);
        }
        stamp(7);
        let value = gpu.signal_value();
        unsafe {
            gpu.device.end_command_buffer(cmd).map_err(err("end command buffer"))?;
            let cmds = [vk::CommandBufferSubmitInfo::default().command_buffer(cmd)];
            let waits = [vk::SemaphoreSubmitInfo::default().semaphore(self.fence_unity.semaphore).value(frame).stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS)];
            let signals = [vk::SemaphoreSubmitInfo::default().semaphore(self.fence_tracer.semaphore).value(frame).stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS), vk::SemaphoreSubmitInfo::default().semaphore(gpu.timeline).value(value).stage_mask(vk::PipelineStageFlags2::ALL_COMMANDS)];
            gpu.device.queue_submit2(gpu.queue, &[vk::SubmitInfo2::default().wait_semaphore_infos(&waits).command_buffer_infos(&cmds).signal_semaphore_infos(&signals)], vk::Fence::null()).map_err(err("submit frame"))?;
        }
        self.slot_fences[slot] = value;
        if let Some(ctx) = self.contexts[context].as_mut() { ctx.history_index = history_write; ctx.history_valid = true; ctx.last_time = params.time; }
        if let Some(e) = pending_error { eprintln!("{e}"); }
        Ok(tlas_count)
    }

    fn scratch_descriptor(&self) -> u32 { let (base, cursor) = self.scratch.get(); self.scratch.set((base, cursor + 1)); base + cursor % SCRATCH_PER_SLOT }

    fn command(&self, gpu: &mut Gpu, scene: &mut Scene, cmd: vk::CommandBuffer, c: &Command, k: u32, vertices_written: &mut bool, indices_written: &mut bool, fence: u64) -> Fallible<()> {
        let heap = self.heap();
        let result = (|| -> Fallible<()> {
            match c.op {
                OP_MESH_DEFINE => scene.define_mesh(c.id, c.a, c.b, c.c, fence)?,
                OP_MESH_SUBMESH => scene.add_submesh(c.id, c.a, c.b, c.c),
                OP_MESH_POSITIONS => {
                    let Some(m) = scene.meshes.get(&c.id) else { return Ok(()); };
                    if c.a + c.b > m.vertex_count { return Err(format!("position upload past mesh {}", c.id)); }
                    *vertices_written = true;
                    self.pipeline(gpu, cmd, P_COPY_POSITIONS);
                    self.push(gpu, cmd, [c.c, c.b, ((m.vertex_offset + c.a as u64) * 32) as u32, 0, 0, 0, 0, k]);
                    self.dispatch(gpu, cmd, c.b.div_ceil(64).max(1), 1);
                    scene.invalidate_bounds(c.id);
                }
                OP_MESH_VERTICES | OP_MESH_INDICES => {
                    let Some(m) = scene.meshes.get(&c.id) else { return Ok(()); };
                    let vertices = c.op == OP_MESH_VERTICES;
                    if vertices && c.a + c.b > m.vertex_count { return Err(format!("vertex upload past mesh {}", c.id)); }
                    if !vertices && c.a + c.b > m.index_count { return Err(format!("index upload past mesh {}", c.id)); }
                    if vertices { *vertices_written = true; }
                    if !vertices { *indices_written = true; }
                    let (pso, count, groups, dst) = if vertices { (P_COPY_VERTICES, c.b * VERTEX_TEXELS, (c.b * VERTEX_TEXELS).div_ceil(64), (m.vertex_offset + c.a as u64) * 32) } else { (P_COPY_INDICES, c.b, c.b.div_ceil(4).div_ceil(64), (m.index_offset + c.a as u64) * 4) };
                    self.pipeline(gpu, cmd, pso);
                    self.push(gpu, cmd, [c.c, count, dst as u32, 0, 0, 0, 0, k]);
                    self.dispatch(gpu, cmd, groups.max(1), 1);
                    if vertices { scene.invalidate_bounds(c.id); }
                }
                OP_MESH_COMMIT => {
                    if *vertices_written || *indices_written { barrier(gpu, cmd); }
                    scene.commit_mesh(c.id, c.a);
                }
                OP_MESH_REFIT => { scene.refit_mesh(c.id); scene.invalidate_bounds(c.id); }
                OP_MESH_RELEASE => scene.release_mesh(c.id, fence),
                OP_TEXTURE_DEFINE => {
                    if let Some(view) = scene.define_texture(gpu, c.id, c.a, c.b, c.c, fence)? {
                        heap.sampled(gpu, HEAP_TEXTURES + c.id, view);
                        let t = &scene.textures[&c.id];
                        image_barrier(gpu, cmd, &[(t.image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED)]);
                        let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: t.mips, base_array_layer: 0, layer_count: 1 };
                        unsafe { gpu.device.cmd_clear_color_image(cmd, t.image, vk::ImageLayout::GENERAL, &vk::ClearColorValue { float32: [0.0; 4] }, &[range]); }
                        barrier(gpu, cmd);
                    }
                }
                OP_TEXTURE_PIXELS => {
                    let Some(t) = scene.textures.get(&c.id) else { return Ok(()); };
                    let index = self.scratch_descriptor();
                    heap.storage(gpu, index, t.mips_views[0]);
                    self.pipeline(gpu, cmd, P_TEXTURE_PIXELS);
                    self.push(gpu, cmd, [c.c, c.id, t.width, c.a, c.b, t.hdr as u32, index, k]);
                    self.dispatch(gpu, cmd, t.width.div_ceil(8), c.b.div_ceil(8));
                }
                OP_TEXTURE_COMMIT => {
                    let Some(t) = scene.textures.get_mut(&c.id) else { return Ok(()); };
                    barrier(gpu, cmd);
                    self.pipeline(gpu, cmd, P_DOWNSAMPLE);
                    for mip in 1..t.mips {
                        let (sw, sh) = ((t.width >> (mip - 1)).max(1), (t.height >> (mip - 1)).max(1));
                        let (dw, dh) = ((t.width >> mip).max(1), (t.height >> mip).max(1));
                        let src = self.scratch_descriptor();
                        let dst = self.scratch_descriptor();
                        heap.storage(gpu, src, t.mips_views[mip as usize - 1]);
                        heap.storage(gpu, dst, t.mips_views[mip as usize]);
                        self.push(gpu, cmd, [src, dst, dw, dh, sw, sh, t.hdr as u32, k]);
                        self.dispatch(gpu, cmd, dw.div_ceil(8), dh.div_ceil(8));
                        barrier(gpu, cmd);
                    }
                    barrier(gpu, cmd);
                    t.readable = true;
                }
                OP_TEXTURE_RELEASE => { if scene.release_texture(c.id, fence) { heap.sampled(gpu, HEAP_TEXTURES + c.id, scene.dummy.2); } }
                _ => {}
            }
            Ok(())
        })();
        result
    }

    pub fn create_context(&mut self, gpu: &mut Gpu, interop: &Interop, link: &os::Link, context: usize, width: u32, height: u32, views: u32) -> Fallible<[u64; 4]> {
        let full = width * views;
        let base = context_base(context);
        let heap = self.heap();
        let gbuffer = [interop.image(gpu, link, full * 2, height * 2, SHARED_FORMAT_UINT)?, interop.image(gpu, link, full * 2, height * 2, SHARED_FORMAT_UINT)?];
        let output = [interop.image(gpu, link, full, height, SHARED_FORMAT_FLOAT)?, interop.image(gpu, link, full, height, SHARED_FORMAT_FLOAT)?];
        for i in 0..2 { heap.sampled(gpu, base + CTX_GBUFFER + i as u32, gbuffer[i].view); }
        let mut textures = Vec::new();
        let mut make = |gpu: &mut Gpu, format: vk::Format, index: u32| -> Fallible<()> {
            let info = vk::ImageCreateInfo::default().image_type(vk::ImageType::TYPE_2D).format(format).extent(vk::Extent3D { width: full.max(1), height: height.max(1), depth: 1 }).mip_levels(1).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL)
                .usage(vk::ImageUsageFlags::STORAGE | vk::ImageUsageFlags::TRANSFER_SRC | vk::ImageUsageFlags::TRANSFER_DST);
            let (image, allocation) = gpu.image(&info)?;
            let view = gpu.view(image, format, 0, 1, vk::ImageUsageFlags::STORAGE)?;
            heap.storage(gpu, index, view);
            textures.push(Texture { image, allocation, view });
            Ok(())
        };
        make(gpu, vk::Format::R16G16B16A16_SFLOAT, base + CTX_RADIANCE_UAV)?;
        for k in 0..2 {
            make(gpu, vk::Format::R16G16B16A16_SFLOAT, base + CTX_HISTORY_UAV + k)?;
            make(gpu, vk::Format::R32G32B32A32_SFLOAT, base + CTX_MOMENTS_UAV + k)?;
            make(gpu, vk::Format::R32G32B32A32_SFLOAT, base + CTX_GEOMETRY_UAV + k)?;
            make(gpu, vk::Format::R16G16B16A16_SFLOAT, base + CTX_FILTER_UAV + k)?;
        }
        let (tiles_x, tiles_y) = tiles(width, height, views);
        let probe_bytes = ((tiles_x * tiles_y) as u64 * PROBE_STRIDE).max(256);
        let probes = [gpu.buffer(probe_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?, gpu.buffer(probe_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?];
        for k in 0..2 { heap.buffer(gpu, base + CTX_PROBES + k as u32, probes[k].buffer, 0, probe_bytes); }
        let ray_bytes = ((tiles_x * tiles_y * PROBE_RAYS) as u64 * 64).max(256);
        let probe_rays = gpu.buffer(ray_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
        heap.buffer(gpu, base + CTX_PROBE_RAYS, probe_rays.buffer, 0, ray_bytes);
        let reservoir_bytes = (full as u64 * height as u64 * 32).max(256);
        let reservoirs = [gpu.buffer(reservoir_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?, gpu.buffer(reservoir_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?];
        for k in 0..2 { heap.buffer(gpu, base + CTX_RESERVOIRS + k as u32, reservoirs[k].buffer, 0, reservoir_bytes); }
        let scratch_bytes = (full as u64 * height as u64 * 64).max(256);
        let reservoir_scratch = gpu.buffer(scratch_bytes, vk::BufferUsageFlags::STORAGE_BUFFER, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
        heap.buffer(gpu, base + CTX_RESERVOIR_SCRATCH, reservoir_scratch.buffer, 0, scratch_bytes);
        let cmd = gpu.begin()?;
        let mut layouts: Vec<(vk::Image, vk::ImageLayout, vk::ImageLayout, u32, u32)> = textures.iter().map(|t| (t.image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED)).collect();
        for s in gbuffer.iter().chain(output.iter()) { layouts.push((s.image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED)); }
        image_barrier(gpu, cmd, &layouts);
        unsafe {
            let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: 1, base_array_layer: 0, layer_count: 1 };
            for t in &textures { gpu.device.cmd_clear_color_image(cmd, t.image, vk::ImageLayout::GENERAL, &vk::ClearColorValue { float32: [0.0; 4] }, &[range]); }
            for b in probes.iter().chain(std::iter::once(&probe_rays)).chain(reservoirs.iter()).chain(std::iter::once(&reservoir_scratch)) { gpu.device.cmd_fill_buffer(cmd, b.buffer, 0, vk::WHOLE_SIZE, 0); }
        }
        gpu.finish_now(cmd)?;
        let handles = [gbuffer[0].remote, gbuffer[1].remote, output[0].remote, output[1].remote];
        self.contexts[context] = Some(Context { gbuffer, output, textures, width, height, views, base, history_index: 0, history_valid: false, probes, probe_rays, reservoirs, reservoir_scratch, last_time: 0.0 });
        Ok(handles)
    }

    pub fn destroy_context(&mut self, gpu: &mut Gpu, context: usize) {
        let Some(ctx) = self.contexts[context].take() else { return; };
        for s in ctx.gbuffer.into_iter().chain(ctx.output) { Interop::destroy_image(gpu, s); }
        for t in ctx.textures { unsafe { gpu.device.destroy_image_view(t.view, None); gpu.device.destroy_image(t.image, None); } gpu.free(t.allocation); }
        for b in ctx.probes.into_iter().chain(std::iter::once(ctx.probe_rays)).chain(ctx.reservoirs).chain(std::iter::once(ctx.reservoir_scratch)) { gpu.destroy_buffer(b); }
    }
}
