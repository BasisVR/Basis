use crate::gpu::*;
use basis_rt_protocol::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::core::*;

pub const VERTEX_CAPACITY: u64 = 8 * 1024 * 1024;
pub const INDEX_CAPACITY: u64 = 64 * 1024 * 1024;
pub const SUBMESH_CAPACITY: u64 = 65536;
pub const SCRATCH_BYTES: u64 = 256 * 1024 * 1024;
pub const REBUILD_INTERVAL: u32 = 60;
pub const TLAS_REBUILD_INTERVAL: u32 = 30;
pub const COMPACT_SLOTS: u32 = 1024;
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
pub const HEAP_TRIANGLE_CDF: u32 = 12;
pub const HEAP_OMM: u32 = 13;
pub const OMM_CAPACITY: u64 = 128 << 20;
pub const OMM_LEVEL: u32 = (env!("BASIS_RT_OMM_LEVEL").as_bytes()[0] - b'0') as u32;
pub const OMM_DATA_BYTES: u64 = (1u64 << (2 * OMM_LEVEL)) / 4;
pub const OMM_CHUNK: u32 = 4096;

pub struct OmmBake { pub data: (u64, u64), pub index: (u64, u64), pub triangles: u32, pub done: u32, pub signature: u64, pub bases: Vec<u32>, pub touched: u64 }

struct Geometry { descs: Vec<D3D12_RAYTRACING_GEOMETRY_DESC>, _triangles: Vec<D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC>, _linkage: Vec<crate::ffi::OmmLinkage> }
pub const CDF_CAPACITY: u64 = 4 * 1024 * 1024;
pub const CDF_MAX_TRIANGLES: u32 = 1 << 20;
pub const CDF_MIN_TRIANGLES: u32 = 16;
pub const CDF_REFRESH: u64 = 120;
pub const CDF_BUDGET: u32 = 1 << 20;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct GpuMesh { pub vertex_offset: u32, pub index_offset: u32, pub submesh_offset: u32, pub submesh_count: u32 }

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct GpuSubmesh { pub index_start: u32, pub index_count: u32, pub flags: u32, pub cdf: u32 }

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
    pub submesh_capacity: u32,
    pub failed: bool,
    pub alpha: bool,
    pub omm: Option<ID3D12Resource>,
    pub omm_address: u64,
    pub omm_index: (u64, u64),
    pub omm_bases: Vec<u32>,
    pub omm_signature: u64,
    pub omm_bake: Option<OmmBake>,
    pub omm_next: u64,
    pub omm_bakes: u32,
    pub omm_used: u64,
}

pub struct TextureEntry {
    pub resource: ID3D12Resource,
    pub width: u32,
    pub height: u32,
    pub mips: u32,
    pub hdr: bool,
    pub readable: bool,
    pub alpha_min: Option<f32>,
    pub serial: u32,
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
    pub meshes: IdMap<MeshEntry>,
    pub textures: IdMap<TextureEntry>,
    pub mesh_cpu: Vec<GpuMesh>,
    pub submesh_cpu: Vec<GpuSubmesh>,
    pub tables_dirty: bool,
    pub retired: Vec<(u64, ID3D12Resource)>,
    pub bounds: ID3D12Resource,
    pub alpha_min: ID3D12Resource,
    pub bounds_valid: Vec<bool>,
    pub alpha_wanted: Vec<(u32, u32)>,
    pub texture_serial: u32,
    pub cdf: ID3D12Resource,
    pub cdf_alloc: RangeAllocator,
    pub cdf_ranges: std::collections::HashMap<usize, (u64, u32, u64)>,
    pub omm: ID3D12Resource,
    pub omm_alloc: RangeAllocator,
    pub omm_retired: Vec<(u64, u64, u64)>,
    pub omm_triangles: u64,
    scratch_cursor: u64,
    tlas_count: u32,
    tlas_age: u32,
    postbuild: ID3D12Resource,
    postbuild_readback: ID3D12Resource,
    postbuild_mapped: *const u64,
    compact_free: Vec<u32>,
    compacting: Vec<(u32, u64, u32, u64)>,
    completed: u64,
    pub compacted_bytes: u64,
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
        let alpha_min = gpu.buffer(crate::opaque::ALPHA_MIN_BYTES, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.uav_raw(&alpha_min, crate::opaque::ALPHA_MIN_BYTES, crate::opaque::HEAP_ALPHA_MIN);
        let cdf = gpu.buffer(CDF_CAPACITY * 4, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.uav_raw(&cdf, CDF_CAPACITY * 4, HEAP_TRIANGLE_CDF);
        let omm_bytes = if gpu.caps & CAP_OMM != 0 && std::env::var_os("BASIS_RT_NO_OMM").is_none() { OMM_CAPACITY } else { 65536 };
        let omm = gpu.buffer(omm_bytes, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_NON_PIXEL_SHADER_RESOURCE)?;
        gpu.uav_raw(&omm, omm_bytes, HEAP_OMM);
        let null = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_R8G8B8A8_UNORM, ViewDimension: D3D12_SRV_DIMENSION_TEXTURE2D, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Texture2D: D3D12_TEX2D_SRV { MipLevels: 1, ..Default::default() } } };
        for i in 0..MAX_TEXTURES { unsafe { gpu.device.CreateShaderResourceView(None::<&ID3D12Resource>, Some(&null), gpu.cpu(HEAP_TEXTURES + i)); } }
        let postbuild = gpu.buffer(COMPACT_SLOTS as u64 * 8, D3D12_HEAP_TYPE_DEFAULT, uav, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        let postbuild_readback = gpu.buffer(COMPACT_SLOTS as u64 * 8, D3D12_HEAP_TYPE_READBACK, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_COPY_DEST)?;
        let mut mapped = std::ptr::null_mut();
        unsafe { postbuild_readback.Map(0, Some(&D3D12_RANGE { Begin: 0, End: COMPACT_SLOTS as usize * 8 }), Some(&mut mapped))?; }
        Ok(Scene {
            vertices, indices, mesh_table, submesh_table, scratch, tlas, tlas_scratch,
            vertex_alloc: RangeAllocator::new(VERTEX_CAPACITY), index_alloc: RangeAllocator::new(INDEX_CAPACITY), submesh_alloc: RangeAllocator::new(SUBMESH_CAPACITY),
            meshes: IdMap::default(), textures: IdMap::default(), mesh_cpu: vec![GpuMesh::default(); MAX_MESHES as usize], submesh_cpu: vec![GpuSubmesh::default(); SUBMESH_CAPACITY as usize],
            tables_dirty: true, retired: Vec::new(), bounds, alpha_min, alpha_wanted: Vec::new(), texture_serial: 0, bounds_valid: vec![false; SUBMESH_CAPACITY as usize], omm, omm_alloc: RangeAllocator::new(omm_bytes), omm_retired: Vec::new(), omm_triangles: 0, cdf, cdf_alloc: RangeAllocator::new(CDF_CAPACITY), cdf_ranges: Default::default(), scratch_cursor: 0, tlas_count: 0, tlas_age: 0,
            postbuild, postbuild_readback, postbuild_mapped: mapped as *const u64, compact_free: (0..COMPACT_SLOTS).rev().collect(), compacting: Vec::new(), completed: 0, compacted_bytes: 0,
        })
    }

    pub fn release_cdf(&mut self, id: u32) {
        let Some(m) = self.meshes.get(&id) else { return; };
        for at in m.submesh_offset as usize..(m.submesh_offset + m.submesh_capacity as u64) as usize {
            if let Some((offset, count, _)) = self.cdf_ranges.remove(&at) { self.cdf_alloc.release(offset, count as u64); }
            if at < self.submesh_cpu.len() && self.submesh_cpu[at].cdf != 0 { self.submesh_cpu[at].cdf = 0; self.tables_dirty = true; }
        }
    }

    pub fn release_mesh(&mut self, id: u32, fence: u64) {
        self.release_cdf(id);
        if let Some(m) = self.meshes.remove(&id) {
            self.vertex_alloc.release(m.vertex_offset, m.vertex_count as u64);
            self.index_alloc.release(m.index_offset, m.index_count as u64);
            self.submesh_alloc.release(m.submesh_offset, m.submesh_capacity as u64);
            if let Some(b) = m.blas { self.retired.push((fence, b)); }
            if let Some(b) = m.omm { self.retired.push((fence, b)); }
            if m.omm_index.1 > 0 { self.omm_retired.push((fence, m.omm_index.0, m.omm_index.1)); }
            if let Some(b) = m.omm_bake { self.omm_retired.push((fence, b.data.0, b.data.1)); self.omm_retired.push((fence, b.index.0, b.index.1)); }
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
        self.meshes.insert(id, MeshEntry { vertex_offset, vertex_count, index_offset, index_count, submesh_offset, submeshes: Vec::with_capacity(submesh_count as usize), dynamic: false, blas: None, blas_address: 0, update_scratch: 0, build_scratch: 0, committed: false, needs_build: false, needs_refit: false, refits: 0, submesh_capacity: submesh_count.max(1), failed: false, alpha: false, omm: None, omm_address: 0, omm_index: (0, 0), omm_bases: Vec::new(), omm_signature: 0, omm_bake: None, omm_next: 0, omm_bakes: 0, omm_used: 0 });
        Ok(())
    }

    fn fail_mesh(&mut self, id: u32, fence: u64) {
        if let Some(m) = self.meshes.get_mut(&id) {
            if let Some(old) = m.blas.take() { self.retired.push((fence, old)); }
            m.blas_address = 0;
            m.build_scratch = 0;
            m.update_scratch = 0;
            m.needs_build = false;
            m.needs_refit = false;
            m.failed = true;
        }
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
            if m.committed || m.submeshes.len() >= m.submesh_capacity as usize { return; }
            let count = count.min(m.index_count.saturating_sub(start));
            let s = GpuSubmesh { index_start: start.min(m.index_count), index_count: count - count % 3, flags, cdf: 0 };
            let slot = m.submesh_offset as usize + m.submeshes.len();
            if slot < self.submesh_cpu.len() { self.submesh_cpu[slot] = s; m.submeshes.push(s); m.alpha |= flags & SUBMESH_OPAQUE == 0; }
        }
    }

    pub fn commit_mesh(&mut self, id: u32, flags: u32) {
        if let Some(m) = self.meshes.get_mut(&id) {
            m.dynamic = flags & MESH_DYNAMIC != 0;
            m.refits = if m.dynamic { id % REBUILD_INTERVAL } else { 0 };
            m.committed = true;
            m.needs_build = true;
            m.failed = false;
            m.build_scratch = 0;
            self.mesh_cpu[id as usize] = GpuMesh { vertex_offset: m.vertex_offset as u32, index_offset: m.index_offset as u32, submesh_offset: m.submesh_offset as u32, submesh_count: m.submeshes.len() as u32 };
            self.tables_dirty = true;
        }
        self.invalidate_bounds(id);
    }

    pub fn refit_mesh(&mut self, id: u32) {
        if let Some(m) = self.meshes.get_mut(&id) {
            if !m.committed || m.failed { return; }
            m.refits += 1;
            if m.blas.is_none() || !m.dynamic || m.refits >= REBUILD_INTERVAL { m.needs_build = true; m.refits = 0; } else { m.needs_refit = true; }
        }
    }

    fn geometry(&self, m: &MeshEntry) -> Geometry {
        let vb = unsafe { self.vertices.GetGPUVirtualAddress() } + m.vertex_offset * 32;
        let ib = unsafe { self.indices.GetGPUVirtualAddress() } + m.index_offset * 4;
        let omm = unsafe { self.omm.GetGPUVirtualAddress() } + m.omm_index.0;
        let triangles: Vec<D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC> = m.submeshes.iter().map(|s| D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC {
            Transform3x4: 0, IndexFormat: DXGI_FORMAT_R32_UINT, VertexFormat: DXGI_FORMAT_R32G32B32_FLOAT, IndexCount: s.index_count, VertexCount: m.vertex_count,
            IndexBuffer: ib + s.index_start as u64 * 4, VertexBuffer: D3D12_GPU_VIRTUAL_ADDRESS_AND_STRIDE { StartAddress: vb, StrideInBytes: 32 },
        }).collect();
        let linked = |i: usize| m.omm_address != 0 && m.omm_bases.get(i).is_some_and(|b| *b != u32::MAX);
        let linkage: Vec<crate::ffi::OmmLinkage> = (0..m.submeshes.len()).map(|i| crate::ffi::OmmLinkage { index_buffer: crate::ffi::AddressAndStride { address: if linked(i) { omm + m.omm_bases[i] as u64 * 4 } else { 0 }, stride: 4 }, index_format: DXGI_FORMAT_R32_UINT.0, base_location: 0, array: m.omm_address }).collect();
        let descs = m.submeshes.iter().enumerate().map(|(i, s)| {
            let flags = if s.flags & SUBMESH_OPAQUE != 0 { D3D12_RAYTRACING_GEOMETRY_FLAG_OPAQUE } else { D3D12_RAYTRACING_GEOMETRY_FLAG_NO_DUPLICATE_ANYHIT_INVOCATION };
            if linked(i) { unsafe { std::mem::transmute(crate::ffi::GeometryDescOmm { kind: crate::ffi::GEOMETRY_TYPE_OMM_TRIANGLES, flags: flags.0, omm: crate::ffi::OmmTrianglesDesc { triangles: &triangles[i], linkage: &linkage[i] }, pad: [0; 4] }) } }
            else { D3D12_RAYTRACING_GEOMETRY_DESC { Type: D3D12_RAYTRACING_GEOMETRY_TYPE_TRIANGLES, Flags: flags, Anonymous: D3D12_RAYTRACING_GEOMETRY_DESC_0 { Triangles: triangles[i] } } }
        }).collect();
        Geometry { descs, _triangles: triangles, _linkage: linkage }
    }

    pub fn omm_signature(&self, m: &MeshEntry, materials: &[Material], base: u32) -> u64 {
        let mut h = 0xcbf29ce484222325u64;
        let mut mix = |v: u32| { h = (h ^ v as u64).wrapping_mul(0x100000001b3); };
        for (g, s) in m.submeshes.iter().enumerate() {
            if s.flags & SUBMESH_OPAQUE != 0 { continue; }
            let Some(mat) = materials.get(base as usize + g) else { return 0; };
            let mask = (mat.surface >> 16) & 0xfff;
            for v in [mat.alpha_mode & 3, mat.cutoff.to_bits(), mat.base_color[3].to_bits(), mat.base_texture as u32, mat.surface >> 16, mat.alpha_op, mat.flags & 0x3e0, if mat.flags & MATERIAL_TILE_DISCARD != 0 { mat.emission_texture as u32 >> 16 } else { 0 }] { mix(v); }
            if mat.base_texture >= 0 || mask != 0 { for v in mat.base_st { mix(v.to_bits()); } }
            if mat.base_texture >= 0 { let Some(t) = self.textures.get(&(mat.base_texture as u32)).filter(|t| t.readable) else { return 0; }; mix(t.serial); }
            if mask != 0 { let Some(t) = self.textures.get(&(mask - 1)).filter(|t| t.readable) else { return 0; }; mix(t.serial); }
        }
        h | 1
    }

    pub fn omm_schedule(&mut self, id: u32, materials: &[Material], material_base: u32, frame: u64, budget: &mut u32, jobs: &mut Vec<[u32; 8]>, room: usize) {
        let Some(m) = self.meshes.get(&id) else { return; };
        if !m.alpha || !m.committed || m.failed || (m.omm_bake.is_none() && frame < m.omm_next) { return; }
        let signature = self.omm_signature(m, materials, material_base);
        if signature == 0 { return; }
        if m.omm_bake.is_none() {
            if signature == m.omm_signature { self.meshes.get_mut(&id).unwrap().omm_used = frame; return; }
            if m.omm_address != 0 && m.omm_used + 1 >= frame { return; }
            let mut bases = Vec::with_capacity(m.submeshes.len());
            let mut triangles = 0u32;
            for s in &m.submeshes { if s.flags & SUBMESH_OPAQUE != 0 { bases.push(u32::MAX); } else { bases.push(triangles); triangles += s.index_count / 3; } }
            if triangles == 0 { return; }
            let data_size = (triangles as u64 * (OMM_DATA_BYTES + 8) + 127) & !127;
            let index_size = (triangles as u64 * 4 + 127) & !127;
            let data = self.omm_alloc.alloc(data_size, 128);
            let index = self.omm_alloc.alloc(index_size, 128);
            let m = self.meshes.get_mut(&id).unwrap();
            let (Some(data), Some(index)) = (data, index) else {
                if let Some(o) = data { self.omm_alloc.release(o, data_size); }
                if let Some(o) = index { self.omm_alloc.release(o, index_size); }
                m.omm_next = frame + 600;
                return;
            };
            m.omm_bakes += 1;
            m.omm_next = frame + (60u64 << m.omm_bakes.min(8));
            m.omm_bake = Some(OmmBake { data: (data, data_size), index: (index, index_size), triangles, done: 0, signature, bases, touched: frame });
        }
        let m = self.meshes.get_mut(&id).unwrap();
        let mut bake = m.omm_bake.take().unwrap();
        if bake.signature != signature {
            if frame > bake.touched + 240 { self.omm_retired.push((u64::MAX, bake.data.0, bake.data.1)); self.omm_retired.push((u64::MAX, bake.index.0, bake.index.1)); } else { m.omm_bake = Some(bake); }
            return;
        }
        while *budget > 0 && jobs.len() < room && bake.done < bake.triangles {
            let g = bake.bases.iter().rposition(|b| *b != u32::MAX && *b <= bake.done).unwrap();
            let s = m.submeshes[g];
            let local = bake.done - bake.bases[g];
            let count = (s.index_count / 3 - local).min(OMM_CHUNK).min(*budget);
            jobs.push([((bake.data.0 + bake.done as u64 * OMM_DATA_BYTES) / 4) as u32, ((bake.data.0 + bake.triangles as u64 * OMM_DATA_BYTES + bake.done as u64 * 8) / 4) as u32, ((bake.index.0 + bake.done as u64 * 4) / 4) as u32, count, m.vertex_offset as u32, (m.index_offset + s.index_start as u64 + local as u64 * 3) as u32, material_base + g as u32, bake.done]);
            bake.done += count;
            *budget -= count;
        }
        bake.touched = frame;
        m.omm_bake = Some(bake);
    }

    fn build_omm(&mut self, gpu: &Gpu, list: &ID3D12GraphicsCommandList4, fence: u64) -> u32 {
        let ids: Vec<u32> = self.meshes.iter().filter(|(_, m)| m.omm_bake.as_ref().is_some_and(|b| b.done >= b.triangles)).map(|(k, _)| *k).collect();
        let base = unsafe { self.omm.GetGPUVirtualAddress() };
        let mut built = 0;
        for id in ids {
            let bake = self.meshes.get_mut(&id).unwrap().omm_bake.take().unwrap();
            self.omm_retired.push((fence, bake.data.0, bake.data.1));
            let histogram = [crate::ffi::OmmHistogramEntry { count: bake.triangles, subdivision_level: OMM_LEVEL, format: crate::ffi::OMM_FORMAT_OC1_4_STATE }];
            let array = crate::ffi::OmmArrayDesc { histogram_count: 1, histogram: histogram.as_ptr(), input_buffer: base + bake.data.0, per_omm_descs: crate::ffi::AddressAndStride { address: base + bake.data.0 + bake.triangles as u64 * OMM_DATA_BYTES, stride: 8 } };
            let inputs: D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS = unsafe { std::mem::transmute(crate::ffi::BuildInputsOmm { kind: crate::ffi::AS_TYPE_OPACITY_MICROMAP_ARRAY, flags: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE.0, num_descs: 1, layout: 0, desc: &array }) };
            let mut info = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO::default();
            unsafe { gpu.device.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &mut info); }
            let buffer = if info.ResultDataMaxSizeInBytes == 0 { None } else { gpu.buffer((info.ResultDataMaxSizeInBytes + 255) & !255, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE).ok() };
            let scratch = if buffer.is_some() { self.scratch_alloc(list, info.ScratchDataSizeInBytes.max(256)) } else { None };
            let (Some(buffer), Some(scratch)) = (buffer, scratch) else {
                self.omm_retired.push((fence, bake.index.0, bake.index.1));
                self.meshes.get_mut(&id).unwrap().omm_next = u64::MAX;
                continue;
            };
            let address = unsafe { buffer.GetGPUVirtualAddress() };
            let desc = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC { DestAccelerationStructureData: address, Inputs: inputs, SourceAccelerationStructureData: 0, ScratchAccelerationStructureData: scratch };
            unsafe { list.BuildRaytracingAccelerationStructure(&desc, None); }
            let m = self.meshes.get_mut(&id).unwrap();
            if let Some(old) = m.omm.replace(buffer) { self.retired.push((fence, old)); }
            if m.omm_index.1 > 0 { self.omm_retired.push((fence, m.omm_index.0, m.omm_index.1)); }
            m.omm_index = bake.index;
            m.omm_bases = bake.bases;
            m.omm_signature = bake.signature;
            m.omm_address = address;
            m.needs_build = true;
            m.build_scratch = 0;
            m.refits = 0;
            self.omm_triangles += bake.triangles as u64;
            if let Some(path) = std::env::var_os("BASIS_RT_OMM_LOG") { if let Ok(mut file) = std::fs::OpenOptions::new().create(true).append(true).open(path) { use std::io::Write; let _ = writeln!(file, "omm mesh {id}: {} triangles, array {} bytes, fence {fence}", bake.triangles, info.ResultDataMaxSizeInBytes); } }
            built += 1;
        }
        if built > 0 { unsafe { list.ResourceBarrier(&[uav_barrier(None)]); } }
        built
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

    fn compact(&mut self, gpu: &Gpu, list: &ID3D12GraphicsCommandList4, fence: u64) -> Result<u32> {
        let mut copied = 0;
        let mut i = 0;
        while i < self.compacting.len() {
            let (id, done, slot, address) = self.compacting[i];
            if done > self.completed { i += 1; continue; }
            self.compacting.swap_remove(i);
            self.compact_free.push(slot);
            let size = unsafe { std::ptr::read_volatile(self.postbuild_mapped.add(slot as usize)) };
            let Some(m) = self.meshes.get_mut(&id) else { continue; };
            if m.blas_address != address || m.dynamic || m.needs_build || size == 0 { continue; }
            let Some(old) = m.blas.as_ref() else { continue; };
            let current = unsafe { old.GetDesc().Width };
            let size = (size + 255) & !255;
            if size >= current { continue; }
            let Ok(blas) = gpu.buffer(size, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE) else { continue; };
            let target = unsafe { blas.GetGPUVirtualAddress() };
            unsafe { list.CopyRaytracingAccelerationStructure(target, address, D3D12_RAYTRACING_ACCELERATION_STRUCTURE_COPY_MODE_COMPACT); }
            if let Some(old) = m.blas.replace(blas) { self.retired.push((fence, old)); }
            m.blas_address = target;
            m.build_scratch = 0;
            m.update_scratch = 0;
            self.compacted_bytes += current - size;
            copied += 1;
        }
        Ok(copied)
    }

    pub fn build(&mut self, gpu: &Gpu, list: &ID3D12GraphicsCommandList4, fence: u64) -> Result<u32> {
        self.scratch_cursor = 0;
        let mut built = self.compact(gpu, list, fence)?;
        built += self.build_omm(gpu, list, fence);
        let ids: Vec<u32> = self.meshes.iter().filter(|(_, m)| m.committed && !m.failed && (m.needs_build || m.needs_refit)).map(|(k, _)| *k).collect();
        let mut queried: Vec<u32> = Vec::new();
        for id in ids {
            let (geometry, dynamic, rebuild) = { let m = &self.meshes[&id]; (self.geometry(m), m.dynamic, m.needs_build) };
            if geometry.descs.is_empty() { continue; }
            let compactable = !dynamic && rebuild;
            let mut flags = if dynamic { D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_UPDATE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_BUILD } else { D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_PREFER_FAST_TRACE | D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAG_ALLOW_COMPACTION };
            if self.meshes[&id].omm_address != 0 { flags |= D3D12_RAYTRACING_ACCELERATION_STRUCTURE_BUILD_FLAGS(0x80); }
            let mut inputs = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS { Type: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_TYPE_BOTTOM_LEVEL, Flags: flags, NumDescs: geometry.descs.len() as u32, DescsLayout: D3D12_ELEMENTS_LAYOUT_ARRAY, Anonymous: D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_INPUTS_0 { pGeometryDescs: geometry.descs.as_ptr() } };
            let mut source = 0u64;
            let scratch_size;
            if rebuild && self.meshes[&id].blas.is_some() && self.meshes[&id].build_scratch > 0 {
                scratch_size = self.meshes[&id].build_scratch;
            } else if rebuild {
                let mut info = D3D12_RAYTRACING_ACCELERATION_STRUCTURE_PREBUILD_INFO::default();
                unsafe { gpu.device.GetRaytracingAccelerationStructurePrebuildInfo(&inputs, &mut info); }
                if info.ResultDataMaxSizeInBytes == 0 || info.ScratchDataSizeInBytes > SCRATCH_BYTES { self.fail_mesh(id, fence); continue; }
                let Ok(blas) = gpu.buffer(info.ResultDataMaxSizeInBytes, D3D12_HEAP_TYPE_DEFAULT, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_RAYTRACING_ACCELERATION_STRUCTURE) else { self.fail_mesh(id, fence); continue; };
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
            let Some(scratch) = self.scratch_alloc(list, scratch_size) else { if rebuild { self.fail_mesh(id, fence); } else if let Some(m) = self.meshes.get_mut(&id) { m.needs_refit = false; } continue; };
            let m = self.meshes.get_mut(&id).unwrap();
            let desc = D3D12_BUILD_RAYTRACING_ACCELERATION_STRUCTURE_DESC { DestAccelerationStructureData: m.blas_address, Inputs: inputs, SourceAccelerationStructureData: source, ScratchAccelerationStructureData: scratch };
            let address = m.blas_address;
            m.needs_build = false;
            m.needs_refit = false;
            built += 1;
            let mut slot = None;
            if compactable {
                let stale: Vec<u32> = self.compacting.iter().filter(|c| c.0 == id).map(|c| c.2).collect();
                self.compacting.retain(|c| c.0 != id);
                self.compact_free.extend(stale);
                slot = self.compact_free.pop();
            }
            match slot {
                Some(slot) => {
                    let info = [D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_DESC { DestBuffer: unsafe { self.postbuild.GetGPUVirtualAddress() } + slot as u64 * 8, InfoType: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_POSTBUILD_INFO_COMPACTED_SIZE }];
                    unsafe { list.BuildRaytracingAccelerationStructure(&desc, Some(&info)); }
                    self.compacting.push((id, fence, slot, address));
                    queried.push(slot);
                }
                None => unsafe { list.BuildRaytracingAccelerationStructure(&desc, None) },
            }
        }
        if built > 0 { unsafe { list.ResourceBarrier(&[uav_barrier(None)]); } }
        if !queried.is_empty() {
            unsafe {
                list.ResourceBarrier(&[transition(&self.postbuild, D3D12_RESOURCE_STATE_UNORDERED_ACCESS, D3D12_RESOURCE_STATE_COPY_SOURCE)]);
                for slot in queried { list.CopyBufferRegion(&self.postbuild_readback, slot as u64 * 8, &self.postbuild, slot as u64 * 8, 8); }
                list.ResourceBarrier(&[transition(&self.postbuild, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)]);
            }
        }
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
        self.texture_serial = self.texture_serial.wrapping_add(1);
        let width = width.clamp(1, MAX_TEXTURE_SIZE);
        let height = height.clamp(1, MAX_TEXTURE_SIZE);
        let mips = 32 - width.max(height).leading_zeros();
        let format = if hdr { DXGI_FORMAT_R16G16B16A16_FLOAT } else { DXGI_FORMAT_R8G8B8A8_TYPELESS };
        let resource = gpu.texture(&texture_desc(width, height, mips as u16, format, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS), D3D12_HEAP_FLAG_NONE, D3D12_RESOURCE_STATE_UNORDERED_ACCESS)?;
        gpu.srv_texture(&resource, if hdr { DXGI_FORMAT_R16G16B16A16_FLOAT } else { DXGI_FORMAT_R8G8B8A8_UNORM_SRGB }, mips, HEAP_TEXTURES + id);
        self.textures.insert(id, TextureEntry { resource, width, height, mips, hdr, readable: false, alpha_min: if hdr { Some(1.0) } else { None }, serial: self.texture_serial });
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

    pub fn collect(&mut self, completed: u64) {
        self.completed = completed;
        self.retired.retain(|(f, _)| *f > completed);
        let mut i = 0;
        while i < self.omm_retired.len() {
            let (f, offset, size) = self.omm_retired[i];
            if f != u64::MAX && f > completed { i += 1; continue; }
            if f == u64::MAX { self.omm_retired[i].0 = 0; i += 1; continue; }
            self.omm_alloc.release(offset, size);
            self.omm_retired.swap_remove(i);
        }
    }
}
