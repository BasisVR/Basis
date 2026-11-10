use crate::gpu::*;
use crate::scene::*;
use basis_rt_protocol::*;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::*;

const TRACE: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/trace.dxil"));
const COPY_VERTICES: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/CopyVertices.dxil"));
const COPY_INDICES: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/CopyIndices.dxil"));
const COPY_POSITIONS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/CopyPositions.dxil"));
const TEXTURE_PIXELS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/TexturePixels.dxil"));
const DOWNSAMPLE: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/Downsample.dxil"));
const TEMPORAL: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/Temporal.dxil"));
const WAVELET: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/Wavelet.dxil"));
const RESOLVE: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/CacheResolve.dxil"));
const MAINTAIN: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/CacheMaintain.dxil"));
const PROBE_RESOLVE: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/ProbeResolve.dxil"));
const SUBMESH_BOUNDS: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/SubmeshBounds.dxil"));
const SUBMESH_BOUNDS_CLEAR: &[u8] = include_bytes!(concat!(env!("OUT_DIR"), "/SubmeshBoundsClear.dxil"));
pub const HEAP_RING: u32 = 9;
pub const HEAP_BLUE_NOISE: u32 = 10;
pub const BLUE_NOISE: &[u8] = include_bytes!("bluenoise.bin");
pub const CACHE_SIZE: u64 = 1 << 20;
pub const CTX_PROBES: u32 = 11;
pub const CTX_PROBE_RAYS: u32 = 13;
pub const CTX_RESERVOIRS: u32 = 14;
pub const CTX_RESERVOIR_SCRATCH: u32 = 16;
pub const PROBE_RAYS: u32 = 16;
pub const PROBE_STRIDE: u64 = 96;
pub const TIMESTAMPS: u32 = 8;
pub const HEAP_CACHE: u32 = 48;
pub const HEAP_CACHE_ARGS: u32 = 55;
pub const CACHE_SLICES: u64 = 16;
pub const HEAP_DIRTY: u32 = 56;

pub const HEAP_UPLOAD: u32 = 7;
pub const HEAP_INSTANCES: u32 = 16;
pub const HEAP_MATERIALS: u32 = 24;
pub const HEAP_LIGHTS: u32 = 32;
pub const HEAP_EMITTERS: u32 = 40;
pub const CTX_GBUFFER: u32 = 0;
pub const CTX_RADIANCE_UAV: u32 = 2;
pub const CTX_HISTORY_UAV: u32 = 3;
pub const CTX_MOMENTS_UAV: u32 = 5;
pub const CTX_GEOMETRY_UAV: u32 = 7;
pub const CTX_FILTER_UAV: u32 = 9;
pub fn context_base(context: usize) -> u32 { 64 + context as u32 * 32 }

const fn align(x: u64, to: u64) -> u64 { x.div_ceil(to) * to }
const CONSTANTS_BYTES: u64 = 1024;
const INSTANCES_OFFSET: u64 = align(CONSTANTS_BYTES, 96);
const MATERIALS_OFFSET: u64 = align(INSTANCES_OFFSET + (MAX_INSTANCES * 96) as u64, 96);
const LIGHTS_OFFSET: u64 = align(MATERIALS_OFFSET + (MAX_MATERIALS * 96) as u64, 64);
const EMITTERS_OFFSET: u64 = align(LIGHTS_OFFSET + (MAX_LIGHTS * 64) as u64, 16);
const DIRTY_OFFSET: u64 = align(EMITTERS_OFFSET + (MAX_EMITTERS * 16) as u64, 16);
const TLAS_OFFSET: u64 = align(DIRTY_OFFSET + (MAX_DIRTY * 16) as u64, 256);
const TABLES_OFFSET: u64 = TLAS_OFFSET + (MAX_INSTANCES * 64) as u64;
const BOUNDS_LIST_OFFSET: u64 = TABLES_OFFSET + MAX_MESHES as u64 * 16 + SUBMESH_CAPACITY * 16;
const SLOT_STRIDE: u64 = align(BOUNDS_LIST_OFFSET + MAX_EMITTERS as u64 * 16, 65536 * 3);
const _: () = assert!(INSTANCES_OFFSET % 96 == 0 && MATERIALS_OFFSET % 96 == 0 && LIGHTS_OFFSET % 64 == 0 && EMITTERS_OFFSET % 16 == 0 && DIRTY_OFFSET % 16 == 0 && SLOT_STRIDE % 96 == 0 && SLOT_STRIDE % 65536 == 0);

#[repr(C)]
#[derive(Clone, Copy)]
pub struct Extra { pub slot: u32, pub gbuffer_index: u32, pub history_read: u32, pub history_write: u32, pub delta_time: f32, pub debug_view: u32, pub frame_index: u32, pub context_base: u32 }

fn tiles(width: u32, height: u32, views: u32) -> (u32, u32) { (width.div_ceil(16) * views, height.div_ceil(16)) }

pub struct Upload {
    pub textures: [ID3D12Resource; 2],
}

pub struct Context {
    pub gbuffer: [ID3D12Resource; 2],
    pub output: [ID3D12Resource; 2],
    pub textures: Vec<ID3D12Resource>,
    pub width: u32,
    pub height: u32,
    pub views: u32,
    pub base: u32,
    pub history_index: u32,
    pub history_valid: bool,
    pub probes: [ID3D12Resource; 2],
    pub probe_rays: ID3D12Resource,
    pub reservoirs: [ID3D12Resource; 2],
    pub reservoir_scratch: ID3D12Resource,
    pub last_time: f32,
}

pub struct Renderer {
    pub root: ID3D12RootSignature,
    pub state: ID3D12StateObject,
    pub table: ID3D12Resource,
    pub table_address: u64,
    pub copy_vertices: ID3D12PipelineState,
    pub copy_indices: ID3D12PipelineState,
    pub copy_positions: ID3D12PipelineState,
    pub texture_pixels: ID3D12PipelineState,
    pub downsample: ID3D12PipelineState,
    pub temporal: ID3D12PipelineState,
    pub wavelet: ID3D12PipelineState,
    pub resolve: ID3D12PipelineState,
    pub maintain: ID3D12PipelineState,
    pub resolve_signature: ID3D12CommandSignature,
    pub cache_args: ID3D12Resource,
    pub probe_resolve: ID3D12PipelineState,
    pub submesh_bounds: ID3D12PipelineState,
    pub submesh_bounds_clear: ID3D12PipelineState,
    pub cache: Vec<ID3D12Resource>,
    pub blue_noise: UploadRing,
    pub queries: ID3D12QueryHeap,
    pub readback: ID3D12Resource,
    pub frequency: u64,
    pub timings: [u32; 8],
    pub allocators: Vec<ID3D12CommandAllocator>,
    pub list: ID3D12GraphicsCommandList4,
    pub ring: UploadRing,
    pub slot_fences: [u64; RING],
    pub contexts: Vec<Option<Context>>,
    pub eligible: Vec<u32>,
}

fn compute_pso(gpu: &Gpu, root: &ID3D12RootSignature, code: &[u8]) -> Result<ID3D12PipelineState> {
    let desc = D3D12_COMPUTE_PIPELINE_STATE_DESC { pRootSignature: unsafe { std::mem::transmute_copy(root) }, CS: D3D12_SHADER_BYTECODE { pShaderBytecode: code.as_ptr() as _, BytecodeLength: code.len() }, ..Default::default() };
    unsafe { gpu.device.CreateComputePipelineState(&desc) }
}

fn sampler(register: u32, filter: D3D12_FILTER, address: D3D12_TEXTURE_ADDRESS_MODE) -> D3D12_STATIC_SAMPLER_DESC {
    D3D12_STATIC_SAMPLER_DESC { Filter: filter, AddressU: address, AddressV: address, AddressW: address, MaxAnisotropy: 8, ComparisonFunc: D3D12_COMPARISON_FUNC_NEVER, MaxLOD: 1000.0, ShaderRegister: register, ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL, ..Default::default() }
}

impl Renderer {
    pub fn new(gpu: &Gpu) -> Result<Renderer> {
        unsafe {
            let parameters = [
                D3D12_ROOT_PARAMETER1 { ParameterType: D3D12_ROOT_PARAMETER_TYPE_CBV, Anonymous: D3D12_ROOT_PARAMETER1_0 { Descriptor: D3D12_ROOT_DESCRIPTOR1 { ShaderRegister: 0, RegisterSpace: 0, Flags: D3D12_ROOT_DESCRIPTOR_FLAG_DATA_STATIC_WHILE_SET_AT_EXECUTE } }, ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL },
                D3D12_ROOT_PARAMETER1 { ParameterType: D3D12_ROOT_PARAMETER_TYPE_32BIT_CONSTANTS, Anonymous: D3D12_ROOT_PARAMETER1_0 { Constants: D3D12_ROOT_CONSTANTS { ShaderRegister: 1, RegisterSpace: 0, Num32BitValues: 8 } }, ShaderVisibility: D3D12_SHADER_VISIBILITY_ALL },
            ];
            let samplers = [sampler(0, D3D12_FILTER_ANISOTROPIC, D3D12_TEXTURE_ADDRESS_MODE_WRAP), sampler(1, D3D12_FILTER_MIN_MAG_MIP_LINEAR, D3D12_TEXTURE_ADDRESS_MODE_CLAMP), sampler(2, D3D12_FILTER_MIN_MAG_MIP_POINT, D3D12_TEXTURE_ADDRESS_MODE_CLAMP)];
            let desc = D3D12_VERSIONED_ROOT_SIGNATURE_DESC { Version: D3D_ROOT_SIGNATURE_VERSION_1_1, Anonymous: D3D12_VERSIONED_ROOT_SIGNATURE_DESC_0 { Desc_1_1: D3D12_ROOT_SIGNATURE_DESC1 {
                NumParameters: parameters.len() as u32, pParameters: parameters.as_ptr(), NumStaticSamplers: samplers.len() as u32, pStaticSamplers: samplers.as_ptr(),
                Flags: D3D12_ROOT_SIGNATURE_FLAG_CBV_SRV_UAV_HEAP_DIRECTLY_INDEXED,
            } } };
            let mut blob: Option<ID3DBlob> = None;
            let mut error: Option<ID3DBlob> = None;
            if let Err(e) = D3D12SerializeVersionedRootSignature(&desc, &mut blob, Some(&mut error)) {
                let message = error.map(|b| String::from_utf8_lossy(std::slice::from_raw_parts(b.GetBufferPointer() as *const u8, b.GetBufferSize())).into_owned()).unwrap_or_default();
                return Err(Error::new(e.code(), message));
            }
            let blob = blob.unwrap();
            let root: ID3D12RootSignature = gpu.device.CreateRootSignature(0, std::slice::from_raw_parts(blob.GetBufferPointer() as *const u8, blob.GetBufferSize()))?;

            let library = D3D12_DXIL_LIBRARY_DESC { DXILLibrary: D3D12_SHADER_BYTECODE { pShaderBytecode: TRACE.as_ptr() as _, BytecodeLength: TRACE.len() }, NumExports: 0, pExports: std::ptr::null_mut() };
            let hit_group = D3D12_HIT_GROUP_DESC { HitGroupExport: w!("Hit"), Type: D3D12_HIT_GROUP_TYPE_TRIANGLES, AnyHitShaderImport: w!("AnyHit"), ClosestHitShaderImport: w!("ClosestHit"), IntersectionShaderImport: PCWSTR::null() };
            let shader_config = D3D12_RAYTRACING_SHADER_CONFIG { MaxPayloadSizeInBytes: 4, MaxAttributeSizeInBytes: 8 };
            let global = D3D12_GLOBAL_ROOT_SIGNATURE { pGlobalRootSignature: std::mem::transmute_copy(&root) };
            let pipeline_config = D3D12_RAYTRACING_PIPELINE_CONFIG { MaxTraceRecursionDepth: 1 };
            let subobjects = [
                D3D12_STATE_SUBOBJECT { Type: D3D12_STATE_SUBOBJECT_TYPE_DXIL_LIBRARY, pDesc: &library as *const _ as _ },
                D3D12_STATE_SUBOBJECT { Type: D3D12_STATE_SUBOBJECT_TYPE_HIT_GROUP, pDesc: &hit_group as *const _ as _ },
                D3D12_STATE_SUBOBJECT { Type: D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_SHADER_CONFIG, pDesc: &shader_config as *const _ as _ },
                D3D12_STATE_SUBOBJECT { Type: D3D12_STATE_SUBOBJECT_TYPE_GLOBAL_ROOT_SIGNATURE, pDesc: &global as *const _ as _ },
                D3D12_STATE_SUBOBJECT { Type: D3D12_STATE_SUBOBJECT_TYPE_RAYTRACING_PIPELINE_CONFIG, pDesc: &pipeline_config as *const _ as _ },
            ];
            let state_desc = D3D12_STATE_OBJECT_DESC { Type: D3D12_STATE_OBJECT_TYPE_RAYTRACING_PIPELINE, NumSubobjects: subobjects.len() as u32, pSubobjects: subobjects.as_ptr() };
            let state: ID3D12StateObject = gpu.device.CreateStateObject(&state_desc)?;
            let properties: ID3D12StateObjectProperties = state.cast()?;
            let table = gpu.buffer(256, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ)?;
            let mut mapped = std::ptr::null_mut();
            table.Map(0, None, Some(&mut mapped))?;
            for (i, name) in [w!("RayGen"), w!("Miss"), w!("Hit")].iter().enumerate() {
                let id = properties.GetShaderIdentifier(*name);
                if id.is_null() { return Err(Error::new(E_FAIL, "missing shader identifier")); }
                std::ptr::copy_nonoverlapping(id as *const u8, (mapped as *mut u8).add(i * 64), 32);
            }
            let table_address = table.GetGPUVirtualAddress();

            let allocators = (0..RING).map(|_| gpu.device.CreateCommandAllocator::<ID3D12CommandAllocator>(D3D12_COMMAND_LIST_TYPE_DIRECT)).collect::<Result<Vec<_>>>()?;
            let list: ID3D12GraphicsCommandList4 = gpu.device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocators[0], None)?;
            list.Close()?;
            let ring = UploadRing::new(gpu, SLOT_STRIDE * RING as u64)?;
            gpu.srv_raw(&ring.resource, SLOT_STRIDE * RING as u64, HEAP_RING);
            let queries: ID3D12QueryHeap = { let mut q: Option<ID3D12QueryHeap> = None; gpu.device.CreateQueryHeap(&D3D12_QUERY_HEAP_DESC { Type: D3D12_QUERY_HEAP_TYPE_TIMESTAMP, Count: TIMESTAMPS * RING as u32, NodeMask: 0 }, &mut q)?; q.unwrap() };
            let readback = gpu.buffer(TIMESTAMPS as u64 * RING as u64 * 8, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST)?;
            let frequency = gpu.queue.GetTimestampFrequency().unwrap_or(1).max(1);
            let mut cache = Vec::new();
            for (i, stride) in [8u64, 4, 64, 64, 4, 16].iter().enumerate() {
                let bytes = CACHE_SIZE * stride + if i == 1 { 256 } else { 0 };
                let b = gpu.buffer(bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
                gpu.uav_raw(&b, bytes, HEAP_CACHE + i as u32);
                cache.push(b);
            }
            let cache_args = gpu.buffer(256, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
            gpu.uav_raw(&cache_args, 256, HEAP_CACHE_ARGS);
            let dispatch_arg = [D3D12_INDIRECT_ARGUMENT_DESC { Type: D3D12_INDIRECT_ARGUMENT_TYPE_DISPATCH, ..Default::default() }];
            let resolve_signature: ID3D12CommandSignature = { let mut s: Option<ID3D12CommandSignature> = None; gpu.device.CreateCommandSignature(&D3D12_COMMAND_SIGNATURE_DESC { ByteStride: 16, NumArgumentDescs: 1, pArgumentDescs: dispatch_arg.as_ptr(), NodeMask: 0 }, None::<&ID3D12RootSignature>, &mut s)?; s.unwrap() };
            let blue_noise = UploadRing::new(gpu, BLUE_NOISE.len() as u64)?;
            std::ptr::copy_nonoverlapping(BLUE_NOISE.as_ptr(), blue_noise.mapped, BLUE_NOISE.len());
            gpu.srv_raw(&blue_noise.resource, BLUE_NOISE.len() as u64, HEAP_BLUE_NOISE);
            Ok(Renderer {
                copy_vertices: compute_pso(gpu, &root, COPY_VERTICES)?, copy_indices: compute_pso(gpu, &root, COPY_INDICES)?, copy_positions: compute_pso(gpu, &root, COPY_POSITIONS)?, texture_pixels: compute_pso(gpu, &root, TEXTURE_PIXELS)?,
                downsample: compute_pso(gpu, &root, DOWNSAMPLE)?, temporal: compute_pso(gpu, &root, TEMPORAL)?, wavelet: compute_pso(gpu, &root, WAVELET)?, resolve: compute_pso(gpu, &root, RESOLVE)?, maintain: compute_pso(gpu, &root, MAINTAIN)?, resolve_signature, cache_args, probe_resolve: compute_pso(gpu, &root, PROBE_RESOLVE)?, submesh_bounds: compute_pso(gpu, &root, SUBMESH_BOUNDS)?, submesh_bounds_clear: compute_pso(gpu, &root, SUBMESH_BOUNDS_CLEAR)?, cache, blue_noise, queries, readback, frequency, timings: [0; 8],
                root, state, table, table_address, allocators, list, ring, slot_fences: [0; RING], contexts: (0..MAX_CONTEXTS).map(|_| None).collect(), eligible: Vec::new(),
            })
        }
    }

    pub fn slot_base(&self, slot: usize) -> u64 { SLOT_STRIDE * slot as u64 }

    pub fn bind_slot(&self, gpu: &Gpu, slot: usize) {
        let base = self.slot_base(slot);
        let r = &self.ring.resource;
        gpu.srv_structured(r, (base + INSTANCES_OFFSET) / 96, MAX_INSTANCES as u32, 96, HEAP_INSTANCES + slot as u32);
        gpu.srv_structured(r, (base + MATERIALS_OFFSET) / 96, MAX_MATERIALS as u32, 96, HEAP_MATERIALS + slot as u32);
        gpu.srv_structured(r, (base + LIGHTS_OFFSET) / 64, MAX_LIGHTS as u32, 64, HEAP_LIGHTS + slot as u32);
        gpu.srv_structured(r, (base + EMITTERS_OFFSET) / 16, MAX_EMITTERS as u32, 16, HEAP_EMITTERS + slot as u32);
        gpu.srv_structured(r, (base + DIRTY_OFFSET) / 16, MAX_DIRTY as u32, 16, HEAP_DIRTY + slot as u32);
    }

    pub fn record(&mut self, gpu: &mut Gpu, scene: &mut Scene, slot_ptr: *const Slot, unity_fence: &ID3D12Fence, tracer_fence: &ID3D12Fence, flags_out: &mut u32) -> Result<u64> {
        let params = unsafe { (*slot_ptr).params };
        let context = (params.context as usize).min(MAX_CONTEXTS - 1);
        let Some(ctx) = self.contexts[context].as_ref() else { return Err(Error::new(E_FAIL, "frame for an unallocated context")); };
        let cb = ctx.base;
        let frame = params.frame;
        let slot = (frame % RING as u64) as usize;
        gpu.wait(self.slot_fences[slot])?;
        gpu.begin_scratch(slot);
        if self.slot_fences[slot] > 0 {
            unsafe {
                let mut mapped = std::ptr::null_mut();
                let range = D3D12_RANGE { Begin: slot * TIMESTAMPS as usize * 8, End: (slot + 1) * TIMESTAMPS as usize * 8 };
                if self.readback.Map(0, Some(&range), Some(&mut mapped)).is_ok() {
                    let t = (mapped as *const u64).add(slot * TIMESTAMPS as usize);
                    let us = |a: usize, b: usize| -> u32 { let (x, y) = (*t.add(a), *t.add(b)); if y > x { ((y - x) * 1_000_000 / self.frequency) as u32 } else { 0 } };
                    self.timings = [us(0, 7), us(0, 1), us(1, 2), us(2, 3), us(3, 4), us(4, 5), us(5, 6), us(6, 7)];
                    self.readback.Unmap(0, Some(&D3D12_RANGE { Begin: 0, End: 0 }));
                }
            }
        }
        scene.collect(unsafe { gpu.fence.GetCompletedValue() });
        let base = self.slot_base(slot);
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
        let mut eligible = std::mem::take(&mut self.eligible);
        eligible.clear();
        unsafe {
            for (i, inst) in (&(*slot_ptr).instances)[..instance_count].iter().enumerate() {
                let Some(m) = scene.meshes.get(&inst.mesh) else { continue; };
                if m.blas_address == 0 && !m.needs_build { continue; }
                eligible.push(i as u32);
            }
        }
        let _ = commands.len();

        let list = self.list.clone();
        unsafe {
            self.allocators[slot].Reset()?;
            list.Reset(&self.allocators[slot], None)?;
            list.SetDescriptorHeaps(&[Some(gpu.heap.clone())]);
            list.SetComputeRootSignature(&self.root);
            list.SetComputeRootConstantBufferView(0, address);
            gpu.queue.Wait(unity_fence, frame)?;
            list.EndQuery(&self.queries, D3D12_QUERY_TYPE_TIMESTAMP, slot as u32 * TIMESTAMPS);
        }
        self.bind_slot(gpu, slot);
        let mut vertices_written = false;
        let mut indices_written = false;
        let mut pending_error = None;
        for c in &commands {
            if let Err(e) = self.command(gpu, scene, &list, c, upload_index, &mut vertices_written, &mut indices_written, slot_fence_value(gpu)) { pending_error = Some(e); }
        }
        unsafe {
            let mut barriers = Vec::new();
            if vertices_written { barriers.push(transition(&scene.vertices, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)); }
            if indices_written { barriers.push(transition(&scene.indices, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)); }
            if scene.tables_dirty {
                let tables = mapped.add(TABLES_OFFSET as usize);
                std::ptr::copy_nonoverlapping(scene.mesh_cpu.as_ptr() as *const u8, tables, MAX_MESHES as usize * 16);
                std::ptr::copy_nonoverlapping(scene.submesh_cpu.as_ptr() as *const u8, tables.add(MAX_MESHES as usize * 16), SUBMESH_CAPACITY as usize * 16);
                list.CopyBufferRegion(&scene.mesh_table, 0, &self.ring.resource, base + TABLES_OFFSET, MAX_MESHES as u64 * 16);
                list.CopyBufferRegion(&scene.submesh_table, 0, &self.ring.resource, base + TABLES_OFFSET + MAX_MESHES as u64 * 16, SUBMESH_CAPACITY * 16);
                barriers.push(transition(&scene.mesh_table, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                barriers.push(transition(&scene.submesh_table, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE));
                scene.tables_dirty = false;
            }
            if !barriers.is_empty() { list.ResourceBarrier(&barriers); }
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
                let constants = [(base + BOUNDS_LIST_OFFSET) as u32, bounds_count, 0, 0, 0, 0, 0, 0];
                list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                list.SetPipelineState(&self.submesh_bounds_clear);
                list.Dispatch(bounds_count.div_ceil(64), 1, 1);
                list.ResourceBarrier(&[uav_barrier(Some(&scene.bounds))]);
                list.SetPipelineState(&self.submesh_bounds);
                list.Dispatch(bounds_chunks.max(1), bounds_count, 1);
                list.ResourceBarrier(&[uav_barrier(Some(&scene.bounds))]);
            }
        }
        let fence_hint = gpu.fence_value + 1;
        scene.build(gpu, &list, fence_hint)?;
        unsafe {
            let descs = mapped.add(TLAS_OFFSET as usize) as *mut D3D12_RAYTRACING_INSTANCE_DESC;
            for &i in &eligible {
                let inst = &(*slot_ptr).instances[i as usize];
                let Some(m) = scene.meshes.get(&inst.mesh) else { continue; };
                if m.blas_address == 0 { continue; }
                descs.add(tlas_count as usize).write(D3D12_RAYTRACING_INSTANCE_DESC { Transform: inst.transform, _bitfield1: (i & 0x00ff_ffff) | ((inst.mask & 0xff) << 24), _bitfield2: (D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE.0 as u32) << 24, AccelerationStructure: m.blas_address });
                tlas_count += 1;
            }
        }
        self.eligible = eligible;
        scene.build_tlas(&list, address + TLAS_OFFSET, tlas_count);
        let stamp = |list: &ID3D12GraphicsCommandList4, i: u32| unsafe { list.EndQuery(&self.queries, D3D12_QUERY_TYPE_TIMESTAMP, slot as u32 * TIMESTAMPS + i); };
        stamp(&list, 1);
        let width = params.width.min(ctx.width);
        let height = params.height.min(ctx.height);
        let views = params.view_count.clamp(1, 2).min(ctx.views);
        let full = width * views;
        unsafe {
            let rays = |w: u32, h: u32| D3D12_DISPATCH_RAYS_DESC {
                RayGenerationShaderRecord: D3D12_GPU_VIRTUAL_ADDRESS_RANGE { StartAddress: self.table_address, SizeInBytes: 32 },
                MissShaderTable: D3D12_GPU_VIRTUAL_ADDRESS_RANGE_AND_STRIDE { StartAddress: self.table_address + 64, SizeInBytes: 32, StrideInBytes: 32 },
                HitGroupTable: D3D12_GPU_VIRTUAL_ADDRESS_RANGE_AND_STRIDE { StartAddress: self.table_address + 128, SizeInBytes: 32, StrideInBytes: 32 },
                CallableShaderTable: D3D12_GPU_VIRTUAL_ADDRESS_RANGE_AND_STRIDE::default(),
                Width: w.max(1), Height: h.max(1), Depth: 1,
            };
            if tlas_count > 0 {
                list.SetPipelineState1(&self.state);
                let (half_width, half_height) = (full.div_ceil(2).max(1), height.div_ceil(2));
                list.SetComputeRoot32BitConstants(1, 8, [0u32, half_height, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
                list.DispatchRays(&rays(half_width, half_height + 4096u32.div_ceil(half_width)));
            }
            list.ResourceBarrier(&[uav_barrier(None)]);
            stamp(&list, 2);
            let full_scan = params.flags & FLAG_CACHE_RESET != 0 || params.dirty_count > 0;
            let (scan_base, scan_count) = if full_scan { (0, CACHE_SIZE) } else { ((params.frame % CACHE_SLICES) * (CACHE_SIZE / CACHE_SLICES), CACHE_SIZE / CACHE_SLICES) };
            list.SetPipelineState(&self.maintain);
            list.SetComputeRoot32BitConstants(1, 8, [scan_base as u32, 0, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
            list.Dispatch((scan_count / 64) as u32, 1, 1);
            list.ResourceBarrier(&[uav_barrier(None), transition(&self.cache_args, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT)]);
            list.SetPipelineState(&self.resolve);
            list.ExecuteIndirect(&self.resolve_signature, 1, &self.cache_args, 0, None::<&ID3D12Resource>, 0);
            list.ResourceBarrier(&[uav_barrier(None), transition(&self.cache_args, D3D12_RESOURCE_STATE_INDIRECT_ARGUMENT, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)]);
            stamp(&list, 3);
            let (tiles_x, tiles_y) = tiles(width, height, views);
            if tlas_count > 0 && params.flags & FLAG_PROBES != 0 {
                list.SetPipelineState1(&self.state);
                list.SetComputeRoot32BitConstants(1, 8, [2u32, 0, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
                list.DispatchRays(&rays(tiles_x * PROBE_RAYS, tiles_y));
                list.ResourceBarrier(&[uav_barrier(None)]);
                list.SetPipelineState(&self.probe_resolve);
                list.Dispatch(tiles_x.div_ceil(8), tiles_y.div_ceil(8), 1);
                list.ResourceBarrier(&[uav_barrier(None)]);
            }
            stamp(&list, 4);
            if tlas_count > 0 {
                list.SetPipelineState1(&self.state);
                list.SetComputeRoot32BitConstants(1, 8, [1u32, 0, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
                list.DispatchRays(&rays(full, height));
                list.ResourceBarrier(&[uav_barrier(None)]);
                list.SetComputeRoot32BitConstants(1, 8, [4u32, 0, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
                list.DispatchRays(&rays(full, height));
            }
            list.ResourceBarrier(&[uav_barrier(None)]);
            stamp(&list, 5);
            let iterations = if params.flags & FLAG_DENOISE != 0 { 4 } else { 0 };
            let history_target = cb + CTX_HISTORY_UAV + history_write;
            let mut source = if iterations > 0 { cb + CTX_FILTER_UAV + 1 } else { history_target };
            list.SetPipelineState(&self.temporal);
            list.SetComputeRoot32BitConstants(1, 8, [source, 0, 0, 0, 0, 0, 0, 0].as_ptr() as _, 0);
            list.Dispatch(full.div_ceil(8), height.div_ceil(8), 1);
            list.ResourceBarrier(&[uav_barrier(None)]);
            stamp(&list, 6);
            list.SetPipelineState(&self.wavelet);
            for i in 0..iterations {
                let target = if i == 0 { history_target } else { cb + CTX_FILTER_UAV + (i & 1 ^ 1) };
                let constants = [source, target, 1u32 << i, i, 0, 0, 0, 0];
                list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                list.Dispatch(full.div_ceil(8), height.div_ceil(8), 1);
                list.ResourceBarrier(&[uav_barrier(None)]);
                source = target;
            }
            let local = source - cb;
            let final_texture = &ctx.textures[if local >= CTX_FILTER_UAV { 4 + (local - CTX_FILTER_UAV) as usize * 4 } else { 1 + (local - CTX_HISTORY_UAV) as usize * 4 }];
            list.ResourceBarrier(&[transition(final_texture, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE)]);
            list.CopyResource(&ctx.output[k as usize], final_texture);
            list.ResourceBarrier(&[transition(final_texture, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)]);
            stamp(&list, 7);
            list.ResolveQueryData(&self.queries, D3D12_QUERY_TYPE_TIMESTAMP, slot as u32 * TIMESTAMPS, TIMESTAMPS, &self.readback, slot as u64 * TIMESTAMPS as u64 * 8);
            list.Close()?;
            gpu.queue.ExecuteCommandLists(&[Some(list.cast()?)]);
            gpu.queue.Signal(tracer_fence, frame)?;
        }
        let value = gpu.signal()?;
        self.slot_fences[slot] = value;
        if let Some(ctx) = self.contexts[context].as_mut() { ctx.history_index = history_write; ctx.history_valid = true; ctx.last_time = params.time; }
        *flags_out = tlas_count;
        if let Some(e) = pending_error { eprintln!("{e}"); }
        Ok(value)
    }

    fn command(&self, gpu: &mut Gpu, scene: &mut Scene, list: &ID3D12GraphicsCommandList4, c: &Command, k: u32, vertices_written: &mut bool, indices_written: &mut bool, fence: u64) -> std::result::Result<(), String> {
        unsafe {
            match c.op {
                OP_MESH_DEFINE => scene.define_mesh(c.id, c.a, c.b, c.c, fence)?,
                OP_MESH_SUBMESH => scene.add_submesh(c.id, c.a, c.b, c.c),
                OP_MESH_POSITIONS => {
                    let Some(m) = scene.meshes.get(&c.id) else { return Ok(()); };
                    if c.a + c.b > m.vertex_count { return Err(format!("position upload past mesh {}", c.id)); }
                    *vertices_written = true;
                    list.SetPipelineState(&self.copy_positions);
                    let constants = [c.c, c.b, ((m.vertex_offset + c.a as u64) * 32) as u32, 0, 0, 0, 0, k];
                    list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                    list.Dispatch(c.b.div_ceil(64).max(1), 1, 1);
                    scene.invalidate_bounds(c.id);
                }
                OP_MESH_VERTICES | OP_MESH_INDICES => {
                    let Some(m) = scene.meshes.get(&c.id) else { return Ok(()); };
                    let vertices = c.op == OP_MESH_VERTICES;
                    if vertices && c.a + c.b > m.vertex_count { return Err(format!("vertex upload past mesh {}", c.id)); }
                    if !vertices && c.a + c.b > m.index_count { return Err(format!("index upload past mesh {}", c.id)); }
                    if vertices { *vertices_written = true; }
                    if !vertices { *indices_written = true; }
                    let (pso, count, groups, dst) = if vertices { (&self.copy_vertices, c.b * VERTEX_TEXELS, (c.b * VERTEX_TEXELS).div_ceil(64), (m.vertex_offset + c.a as u64) * 32) } else { (&self.copy_indices, c.b, c.b.div_ceil(4).div_ceil(64), (m.index_offset + c.a as u64) * 4) };
                    list.SetPipelineState(pso);
                    let constants = [c.c, count, dst as u32, 0, 0, 0, 0, k];
                    list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                    list.Dispatch(groups.max(1), 1, 1);
                    if vertices { scene.invalidate_bounds(c.id); }
                }
                OP_MESH_COMMIT => {
                    if *vertices_written || *indices_written { list.ResourceBarrier(&[uav_barrier(None)]); }
                    scene.commit_mesh(c.id, c.a);
                }
                OP_MESH_REFIT => { scene.refit_mesh(c.id); scene.invalidate_bounds(c.id); }
                OP_MESH_RELEASE => scene.release_mesh(c.id, fence),
                OP_TEXTURE_DEFINE => scene.define_texture(gpu, c.id, c.a, c.b, c.c, fence).map_err(|e| e.to_string())?,
                OP_TEXTURE_PIXELS => {
                    let Some(t) = scene.textures.get(&c.id) else { return Ok(()); };
                    let index = gpu.scratch_descriptor();
                    gpu.uav_texture(&t.resource, Scene::uav_format(t.hdr), 0, index);
                    list.SetPipelineState(&self.texture_pixels);
                    let constants = [c.c, c.id, t.width, c.a, c.b, t.hdr as u32, index, k];
                    list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                    list.Dispatch(t.width.div_ceil(8), c.b.div_ceil(8), 1);
                }
                OP_TEXTURE_COMMIT => {
                    let Some(t) = scene.textures.get_mut(&c.id) else { return Ok(()); };
                    list.ResourceBarrier(&[uav_barrier(Some(&t.resource))]);
                    list.SetPipelineState(&self.downsample);
                    let format = Scene::uav_format(t.hdr);
                    for mip in 1..t.mips {
                        let (sw, sh) = ((t.width >> (mip - 1)).max(1), (t.height >> (mip - 1)).max(1));
                        let (dw, dh) = ((t.width >> mip).max(1), (t.height >> mip).max(1));
                        let src = gpu.scratch_descriptor();
                        let dst = gpu.scratch_descriptor();
                        gpu.uav_texture(&t.resource, format, mip - 1, src);
                        gpu.uav_texture(&t.resource, format, mip, dst);
                        let constants = [src, dst, dw, dh, sw, sh, t.hdr as u32, k];
                        list.SetComputeRoot32BitConstants(1, 8, constants.as_ptr() as _, 0);
                        list.Dispatch(dw.div_ceil(8), dh.div_ceil(8), 1);
                        list.ResourceBarrier(&[uav_barrier(Some(&t.resource))]);
                    }
                    list.ResourceBarrier(&[transition(&t.resource, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)]);
                    t.readable = true;
                }
                OP_TEXTURE_RELEASE => scene.release_texture(gpu, c.id, fence),
                _ => {}
            }
        }
        Ok(())
    }
}

fn slot_fence_value(gpu: &Gpu) -> u64 { gpu.fence_value + 1 }

pub fn create_upload(gpu: &Gpu) -> Result<Upload> {
    let flags = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    let u = |_: u32| gpu.texture(&texture_desc(UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, 1, DXGI_FORMAT_R16G16B16A16_UINT, flags), D3D12_HEAP_FLAG_SHARED, D3D12_RESOURCE_STATE_COMMON);
    let upload = Upload { textures: [u(0)?, u(1)?] };
    for i in 0..2 { gpu.srv_texture(&upload.textures[i], DXGI_FORMAT_R16G16B16A16_UINT, 1, HEAP_UPLOAD + i as u32); }
    Ok(upload)
}

pub fn create_context(gpu: &Gpu, context: usize, width: u32, height: u32, views: u32) -> Result<Context> {
    let shared = D3D12_HEAP_FLAG_SHARED;
    let sim = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    let full = width * views;
    let base = context_base(context);
    let g = |_: u32| gpu.texture(&texture_desc(full * 2, height * 2, 1, DXGI_FORMAT_R16G16B16A16_UINT, sim), shared, D3D12_RESOURCE_STATE_COMMON);
    let o = |_: u32| gpu.texture(&texture_desc(full, height, 1, DXGI_FORMAT_R16G16B16A16_FLOAT, sim), shared, D3D12_RESOURCE_STATE_COMMON);
    let gbuffer = [g(0)?, g(1)?];
    let output = [o(0)?, o(1)?];
    for i in 0..2 { gpu.srv_texture(&gbuffer[i], DXGI_FORMAT_R16G16B16A16_UINT, 1, base + CTX_GBUFFER + i as u32); }
    let uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
    let mut textures = Vec::new();
    let mut make = |format: DXGI_FORMAT, index: u32| -> Result<()> {
        let t = gpu.texture(&texture_desc(full, height, 1, format, uav), D3D12_HEAP_FLAG_NONE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.uav_texture(&t, format, 0, index);
        textures.push(t);
        Ok(())
    };
    make(DXGI_FORMAT_R16G16B16A16_FLOAT, base + CTX_RADIANCE_UAV)?;
    for k in 0..2 {
        make(DXGI_FORMAT_R16G16B16A16_FLOAT, base + CTX_HISTORY_UAV + k)?;
        make(DXGI_FORMAT_R32G32B32A32_FLOAT, base + CTX_MOMENTS_UAV + k)?;
        make(DXGI_FORMAT_R32G32B32A32_FLOAT, base + CTX_GEOMETRY_UAV + k)?;
        make(DXGI_FORMAT_R16G16B16A16_FLOAT, base + CTX_FILTER_UAV + k)?;
    }
    let (tiles_x, tiles_y) = tiles(width, height, views);
    let probe_bytes = (tiles_x * tiles_y) as u64 * PROBE_STRIDE;
    let probe = |k: u32| -> Result<ID3D12Resource> { let b = gpu.buffer(probe_bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?; gpu.uav_raw(&b, probe_bytes.max(256), base + CTX_PROBES + k); Ok(b) };
    let probes = [probe(0)?, probe(1)?];
    let ray_bytes = (tiles_x * tiles_y * PROBE_RAYS) as u64 * 64;
    let probe_rays = gpu.buffer(ray_bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
    gpu.uav_raw(&probe_rays, ray_bytes.max(256), base + CTX_PROBE_RAYS);
    let reservoir_bytes = full as u64 * height as u64 * 32;
    let reservoir = |k: u32| -> Result<ID3D12Resource> { let b = gpu.buffer(reservoir_bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?; gpu.uav_raw(&b, reservoir_bytes, base + CTX_RESERVOIRS + k); Ok(b) };
    let reservoirs = [reservoir(0)?, reservoir(1)?];
    let scratch_bytes = full as u64 * height as u64 * 64;
    let reservoir_scratch = gpu.buffer(scratch_bytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
    gpu.uav_raw(&reservoir_scratch, scratch_bytes, base + CTX_RESERVOIR_SCRATCH);
    Ok(Context { gbuffer, output, textures, width, height, views, base, history_index: 0, history_valid: false, probes, probe_rays, reservoirs, reservoir_scratch, last_time: 0.0 })
}
