use crate::gpu::*;
use basis_rt_protocol::*;
use std::collections::HashMap;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::*;

pub const VERTEX_CAPACITY: u64 = 8 * 1024 * 1024;
pub const INDEX_CAPACITY: u64 = 64 * 1024 * 1024;
pub const SUBMESH_CAPACITY: u64 = 65536;
pub const SCRATCH_BYTES: u64 = 256 * 1024 * 1024;
pub const REBUILD_INTERVAL: u32 = 60;
pub const TLAS_REBUILD_INTERVAL: u32 = 30;
const TLAS_FLAGS: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS(D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE.0 | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE.0);

pub const HEAP_TLAS: u32 = 0;
pub const HEAP_VERTICES: u32 = 1;
pub const HEAP_INDICES: u32 = 2;
pub const HEAP_MESHES: u32 = 3;
pub const HEAP_SUBMESHES: u32 = 4;
pub const HEAP_VERTICES_UAV: u32 = 5;
pub const HEAP_INDICES_UAV: u32 = 6;
pub const HEAP_TEXTURES: u32 = 1024;
pub const HEAP_BOUNDS: u32 = 54;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct GpuMesh { pub vertex_offset: u32, pub index_offset: u32, pub submesh_offset: u32, pub submesh_count: u32 }

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct GpuSubmesh { pub index_start: u32, pub index_count: u32, pub flags: u32, pub pad: u32 }

pub struct MeshEntry {
    pub vertex_offset: u64,
    pub vertex_count: u32,
    pub index_offset: u64,
    pub index_count: u32,
    pub submesh_offset: u64,
    pub submeshes: Vec<GpuSubmesh>,
    pub dynamic: bool,
    pub blas: Option<ID3D12Resource>,
    pub blas_address: u64,
    pub update_scratch: u64,
    pub build_scratch: u64,
    pub committed: bool,
    pub needs_build: bool,
    pub needs_refit: bool,
    pub refits: u32,
}

pub struct TextureEntry {
    pub resource: ID3D12Resource,
    pub width: u32,
    pub height: u32,
    pub mips: u32,
    pub hdr: bool,
    pub readable: bool,
}

pub struct Scene {
    pub vertices: ID3D12Resource,
    pub indices: ID3D12Resource,
    pub mesh_table: ID3D12Resource,
    pub submesh_table: ID3D12Resource,
    pub scratch: ID3D12Resource,
    pub tlas: ID3D12Resource,
    pub tlas_scratch: ID3D12Resource,
    pub vertex_alloc: RangeAllocator,
    pub index_alloc: RangeAllocator,
    pub submesh_alloc: RangeAllocator,
    pub meshes: HashMap<u32, MeshEntry>,
    pub textures: HashMap<u32, TextureEntry>,
    pub mesh_cpu: Vec<GpuMesh>,
    pub submesh_cpu: Vec<GpuSubmesh>,
    pub tables_dirty: bool,
    pub retired: Vec<(u64, ID3D12Resource)>,
    pub bounds: ID3D12Resource,
    pub bounds_valid: Vec<bool>,
    scratch_cursor: u64,
    tlas_count: u32,
    tlas_age: u32,
}

impl Scene {
    pub fn new(gpu: &Gpu) -> Result<Scene> {
        let uav = D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS;
        let vertices = gpu.buffer(VERTEX_CAPACITY * 32, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_COMMON)?;
        let indices = gpu.buffer(INDEX_CAPACITY * 4, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_COMMON)?;
        let mesh_table = gpu.buffer(MAX_MESHES as u64 * 16, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COMMON)?;
        let submesh_table = gpu.buffer(SUBMESH_CAPACITY * 16, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COMMON)?;
        let scratch = gpu.buffer(SCRATCH_BYTES, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        let inputs = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS { Type: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL, Flags: TLAS_FLAGS, NumDescs: MAX_INSTANCES as u32, DescsLayout: D3D12_ELEMENTS_LAYOUT_ARRAY, ..Default::default() };
        let mut info = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO::default();
        unsafe { gpu.device.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &mut info); }
        let tlas = gpu.buffer(info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)?;
        let tlas_scratch = gpu.buffer(info.ScratchDataSizeInBytes.max(info.UpdateScratchDataSizeInBytes), D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.srv_raw(&vertices, VERTEX_CAPACITY * 32, HEAP_VERTICES);
        gpu.srv_raw(&indices, INDEX_CAPACITY * 4, HEAP_INDICES);
        gpu.uav_raw(&vertices, VERTEX_CAPACITY * 32, HEAP_VERTICES_UAV);
        gpu.uav_raw(&indices, INDEX_CAPACITY * 4, HEAP_INDICES_UAV);
        gpu.srv_structured(&mesh_table, 0, MAX_MESHES, 16, HEAP_MESHES);
        gpu.srv_structured(&submesh_table, 0, SUBMESH_CAPACITY as u32, 16, HEAP_SUBMESHES);
        gpu.srv_tlas(unsafe { tlas.GetGPUVirtualAddress() }, HEAP_TLAS);
        let bounds = gpu.buffer(SUBMESH_CAPACITY * 32, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.uav_raw(&bounds, SUBMESH_CAPACITY * 32, HEAP_BOUNDS);
        let null = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_R8G8B8A8_UNORM, ViewDimension: D3D12_SRV_DIMENSION_TEXTURE2D, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Texture2D: D3D12_TEX2D_SRV { MipLevels: 1, ..Default::default() } } };
        for i in 0..MAX_TEXTURES { unsafe { gpu.device.CreateShaderResourceView(None::<&ID3D12Resource>, Some(&null), gpu.cpu(HEAP_TEXTURES + i)); } }
        Ok(Scene {
            vertices, indices, mesh_table, submesh_table, scratch, tlas, tlas_scratch,
            vertex_alloc: RangeAllocator::new(VERTEX_CAPACITY), index_alloc: RangeAllocator::new(INDEX_CAPACITY), submesh_alloc: RangeAllocator::new(SUBMESH_CAPACITY),
            meshes: HashMap::new(), textures: HashMap::new(), mesh_cpu: vec![GpuMesh::default(); MAX_MESHES as usize], submesh_cpu: vec![GpuSubmesh::default(); SUBMESH_CAPACITY as usize],
            tables_dirty: true, retired: Vec::new(), bounds, bounds_valid: vec![false; SUBMESH_CAPACITY as usize], scratch_cursor: 0, tlas_count: 0, tlas_age: 0,
        })
    }

    pub fn release_mesh(&mut self, id: u32, fence: u64) {
        if let Some(m) = self.meshes.remove(&id) {
            self.vertex_alloc.release(m.vertex_offset, m.vertex_count as u64);
            self.index_alloc.release(m.index_offset, m.index_count as u64);
            self.submesh_alloc.release(m.submesh_offset, m.submeshes.len() as u64);
            if let Some(b) = m.blas { self.retired.push((fence, b)); }
            self.mesh_cpu[id as usize] = GpuMesh::default();
            self.tables_dirty = true;
        }
    }

    pub fn define_mesh(&mut self, id: u32, vertex_count: u32, index_count: u32, submesh_count: u32, fence: u64) -> std::result::Result<(), String> {
        if id >= MAX_MESHES { return Err(format!("mesh id {id} out of range")); }
        self.release_mesh(id, fence);
        let vertex_offset = self.vertex_alloc.alloc(vertex_count as u64, 1).ok_or("vertex arena full")?;
        let index_offset = match self.index_alloc.alloc(index_count as u64, 4) { Some(o) => o, None => { self.vertex_alloc.release(vertex_offset, vertex_count as u64); return Err("index arena full".into()); } };
        let submesh_offset = match self.submesh_alloc.alloc(submesh_count.max(1) as u64, 1) { Some(o) => o, None => { self.vertex_alloc.release(vertex_offset, vertex_count as u64); self.index_alloc.release(index_offset, index_count as u64); return Err("submesh table full".into()); } };
        self.meshes.insert(id, MeshEntry { vertex_offset, vertex_count, index_offset, index_count, submesh_offset, submeshes: Vec::with_capacity(submesh_count as usize), dynamic: false, blas: None, blas_address: 0, update_scratch: 0, build_scratch: 0, committed: false, needs_build: false, needs_refit: false, refits: 0 });
        Ok(())
    }

    pub fn invalidate_bounds(&mut self, id: u32) {
        if let Some(m) = self.meshes.get(&id) {
            let start = m.submesh_offset as usize;
            let end = (start + m.submeshes.len()).min(self.bounds_valid.len());
            for v in &mut self.bounds_valid[start..end] { *v = false; }
        }
    }

    pub fn add_submesh(&mut self, id: u32, start: u32, count: u32, flags: u32) {
        if let Some(m) = self.meshes.get_mut(&id) {
            let s = GpuSubmesh { index_start: start, index_count: count, flags, pad: 0 };
            let slot = m.submesh_offset as usize + m.submeshes.len();
            if slot < self.submesh_cpu.len() { self.submesh_cpu[slot] = s; m.submeshes.push(s); }
        }
    }

    pub fn commit_mesh(&mut self, id: u32, flags: u32) {
        if let Some(m) = self.meshes.get_mut(&id) {
            m.dynamic = flags & MESH_DYNAMIC != 0;
            m.committed = true;
            m.needs_build = true;
            self.mesh_cpu[id as usize] = GpuMesh { vertex_offset: m.vertex_offset as u32, index_offset: m.index_offset as u32, submesh_offset: m.submesh_offset as u32, submesh_count: m.submeshes.len() as u32 };
            self.tables_dirty = true;
        }
        self.invalidate_bounds(id);
    }

    pub fn refit_mesh(&mut self, id: u32) {
        if let Some(m) = self.meshes.get_mut(&id) {
            if !m.committed { return; }
            m.refits += 1;
            if m.blas.is_none() || m.refits >= REBUILD_INTERVAL { m.needs_build = true; m.refits = 0; } else { m.needs_refit = true; }
        }
    }

    fn geometry(&self, m: &MeshEntry) -> Vec<D3D12_RAYTRACING_GEOMETRY_DESC> {
        let vb = unsafe { self.vertices.GetGPUVirtualAddress() } + m.vertex_offset * 32;
        let ib = unsafe { self.indices.GetGPUVirtualAddress() } + m.index_offset * 4;
        m.submeshes.iter().map(|s| D3D12_RAYTRACING_GEOMETRY_DESC {
            Type: D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES,
            Flags: if s.flags & SUBMESH_OPAQUE != 0 { D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE } else { D3D12_RAYTRACING_GEOMETRY_FLAG_NO_DUPLICATE_ANYHIT_INVOCATION },
            Anonymous: D3D12_RAYTRACING_GEOMETRY_DESC_0 { Triangles: D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC {
                Transform3x4: 0, IndexFormat: DXGI_FORMAT_R32_UINT, VertexFormat: DXGI_FORMAT_R32G32B32_FLOAT, IndexCount: s.index_count, VertexCount: m.vertex_count,
                IndexBuffer: ib + s.index_start as u64 * 4, VertexBuffer: D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE { StartAddress: vb, StrideInBytes: 32 },
            } },
        }).collect()
    }

    fn scratch_alloc(&mut self, list: &ID3D12GraphicsCommandList4, size: u64) -> Option<u64> {
        let size = (size + 255) & !255;
        if size > SCRATCH_BYTES { return None; }
        if self.scratch_cursor + size > SCRATCH_BYTES {
            unsafe { list.ResourceBarrier(&[uav_barrier(Some(&self.scratch))]); }
            self.scratch_cursor = 0;
        }
        let at = self.scratch_cursor;
        self.scratch_cursor += size;
        Some(unsafe { self.scratch.GetGPUVirtualAddress() } + at)
    }

    pub fn build(&mut self, gpu: &Gpu, list: &ID3D12GraphicsCommandList4, fence: u64) -> Result<u32> {
        self.scratch_cursor = 0;
        let ids: Vec<u32> = self.meshes.iter().filter(|(_, m)| m.committed && (m.needs_build || m.needs_refit)).map(|(k, _)| *k).collect();
        let mut built = 0;
        for id in ids {
            let (geometry, dynamic, rebuild) = { let m = &self.meshes[&id]; (self.geometry(m), m.dynamic, m.needs_build) };
            if geometry.is_empty() { continue; }
            let mut flags = if dynamic { D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD } else { D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE };
            let mut inputs = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS { Type: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL, Flags: flags, NumDescs: geometry.len() as u32, DescsLayout: D3D12_ELEMENTS_LAYOUT_ARRAY, Anonymous: D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS_0 { pGeometryDescs: geometry.as_ptr() } };
            let mut source = 0u64;
            let scratch_size;
            if rebuild && self.meshes[&id].blas.is_some() && self.meshes[&id].build_scratch > 0 {
                scratch_size = self.meshes[&id].build_scratch;
            } else if rebuild {
                let mut info = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO::default();
                unsafe { gpu.device.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &mut info); }
                let blas = gpu.buffer(info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE)?;
                let m = self.meshes.get_mut(&id).unwrap();
                if let Some(old) = m.blas.take() { self.retired.push((fence, old)); }
                m.blas_address = unsafe { blas.GetGPUVirtualAddress() };
                m.blas = Some(blas);
                m.update_scratch = info.UpdateScratchDataSizeInBytes;
                m.build_scratch = info.ScratchDataSizeInBytes;
                scratch_size = info.ScratchDataSizeInBytes;
            } else {
                flags |= D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE;
                inputs.Flags = flags;
                let m = &self.meshes[&id];
                source = m.blas_address;
                scratch_size = m.update_scratch;
            }
            let Some(scratch) = self.scratch_alloc(list, scratch_size) else { continue; };
            let m = self.meshes.get_mut(&id).unwrap();
            let desc = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC { DestAccelerationStructureData: m.blas_address, Inputs: inputs, SourceAccelerationStructureData: source, ScratchAccelerationStructureData: scratch };
            unsafe { list.BuildRaytracingAccelerationStructure(&desc, None); }
            m.needs_build = false;
            m.needs_refit = false;
            built += 1;
        }
        if built > 0 { unsafe { list.ResourceBarrier(&[uav_barrier(None)]); } }
        Ok(built)
    }

    pub fn build_tlas(&mut self, list: &ID3D12GraphicsCommandList4, instance_descs: u64, count: u32) {
        let update = count > 0 && count == self.tlas_count && self.tlas_age < TLAS_REBUILD_INTERVAL;
        self.tlas_age = if update { self.tlas_age + 1 } else { 0 };
        self.tlas_count = count;
        let flags = if update { D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS(TLAS_FLAGS.0 | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PERFORM_UPDATE.0) } else { TLAS_FLAGS };
        let address = unsafe { self.tlas.GetGPUVirtualAddress() };
        let inputs = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS { Type: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_TOP_LEVEL, Flags: flags, NumDescs: count, DescsLayout: D3D12_ELEMENTS_LAYOUT_ARRAY, Anonymous: D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS_0 { InstanceDescs: instance_descs } };
        let desc = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC { DestAccelerationStructureData: address, Inputs: inputs, SourceAccelerationStructureData: if update { address } else { 0 }, ScratchAccelerationStructureData: unsafe { self.tlas_scratch.GetGPUVirtualAddress() } };
        unsafe {
            list.BuildRaytracingAccelerationStructure(&desc, None);
            list.ResourceBarrier(&[uav_barrier(Some(&self.tlas))]);
        }
    }

    pub fn define_texture(&mut self, gpu: &Gpu, id: u32, width: u32, height: u32, flags: u32, fence: u64) -> Result<()> {
        if id >= MAX_TEXTURES { return Ok(()); }
        if let Some(old) = self.textures.remove(&id) { self.retired.push((fence, old.resource)); }
        let hdr = flags & TEXTURE_HDR != 0;
        let width = width.clamp(1, MAX_TEXTURE_SIZE);
        let height = height.clamp(1, MAX_TEXTURE_SIZE);
        let mips = 32 - width.max(height).leading_zeros();
        let format = if hdr { DXGI_FORMAT_R16G16B16A16_FLOAT } else { DXGI_FORMAT_R8G8B8A8_TYPELESS };
        let resource = gpu.texture(&texture_desc(width, height, mips as u16, format, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS), D3D12_HEAP_FLAG_NONE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.srv_texture(&resource, if hdr { DXGI_FORMAT_R16G16B16A16_FLOAT } else { DXGI_FORMAT_R8G8B8A8_UNORM_SRGB }, mips, HEAP_TEXTURES + id);
        self.textures.insert(id, TextureEntry { resource, width, height, mips, hdr, readable: false });
        Ok(())
    }

    pub fn uav_format(hdr: bool) -> DXGI_FORMAT { if hdr { DXGI_FORMAT_R16G16B16A16_FLOAT } else { DXGI_FORMAT_R8G8B8A8_UNORM } }

    pub fn release_texture(&mut self, gpu: &Gpu, id: u32, fence: u64) {
        if let Some(old) = self.textures.remove(&id) {
            self.retired.push((fence, old.resource));
            let null = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_R8G8B8A8_UNORM, ViewDimension: D3D12_SRV_DIMENSION_TEXTURE2D, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Texture2D: D3D12_TEX2D_SRV { MipLevels: 1, ..Default::default() } } };
            unsafe { gpu.device.CreateShaderResourceView(None::<&ID3D12Resource>, Some(&null), gpu.cpu(HEAP_TEXTURES + id)); }
        }
    }

    pub fn collect(&mut self, completed: u64) { self.retired.retain(|(f, _)| *f > completed); }
}
