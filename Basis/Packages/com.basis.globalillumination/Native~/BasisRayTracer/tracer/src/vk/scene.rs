use super::device::*;
use crate::Fallible;
use ash::vk;
use basis_rt_protocol::*;

pub const VERTEX_CAPACITY: u64 = 8 * 1024 * 1024;
pub const INDEX_CAPACITY: u64 = 64 * 1024 * 1024;
pub const SUBMESH_CAPACITY: u64 = 65536;
pub const SCRATCH_BYTES: u64 = 256 * 1024 * 1024;
pub const REBUILD_INTERVAL: u32 = 60;
pub const COMPACT_SLOTS: u32 = 1024;

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

pub struct Blas { pub buffer: Buffer, pub structure: vk::AccelerationStructureKHR }

pub struct MeshEntry {
    pub vertex_offset: u64,
    pub vertex_count: u32,
    pub index_offset: u64,
    pub index_count: u32,
    pub submesh_offset: u64,
    pub submeshes: Vec<GpuSubmesh>,
    pub dynamic: bool,
    pub blas: Option<Blas>,
    pub blas_address: u64,
    pub update_scratch: u64,
    pub build_scratch: u64,
    pub committed: bool,
    pub needs_build: bool,
    pub needs_refit: bool,
    pub refits: u32,
}

pub struct TextureEntry {
    pub image: vk::Image,
    pub allocation: Allocation,
    pub sampled: vk::ImageView,
    pub mips_views: Vec<vk::ImageView>,
    pub width: u32,
    pub height: u32,
    pub mips: u32,
    pub hdr: bool,
    pub readable: bool,
    pub cleared: bool,
}

pub enum Retired { Blas(Blas), Texture(TextureEntry) }

pub struct Scene {
    pub vertices: Buffer,
    pub indices: Buffer,
    pub mesh_table: Buffer,
    pub submesh_table: Buffer,
    pub scratch: Buffer,
    pub tlas_buffer: Buffer,
    pub tlas: vk::AccelerationStructureKHR,
    pub tlas_scratch: Buffer,
    pub vertex_alloc: Vec<(u64, u64)>,
    pub index_alloc: Vec<(u64, u64)>,
    pub submesh_alloc: Vec<(u64, u64)>,
    pub meshes: IdMap<MeshEntry>,
    pub textures: IdMap<TextureEntry>,
    pub mesh_cpu: Vec<GpuMesh>,
    pub submesh_cpu: Vec<GpuSubmesh>,
    pub tables_dirty: bool,
    pub retired: Vec<(u64, Retired)>,
    pub bounds: Buffer,
    pub bounds_valid: Vec<bool>,
    pub dummy: (vk::Image, Allocation, vk::ImageView),
    scratch_cursor: u64,
    compact_pool: vk::QueryPool,
    compact_free: Vec<u32>,
    compacting: Vec<(u32, u64, u32, u64)>,
    completed: u64,
    pub compacted_bytes: u64,
}

fn alloc(free: &mut Vec<(u64, u64)>, size: u64, align: u64) -> Option<u64> { take(free, size, align) }

impl Scene {
    pub fn new(gpu: &mut Gpu) -> Fallible<Scene> {
        let storage = vk::BufferUsageFlags::STORAGE_BUFFER;
        let input = vk::BufferUsageFlags::ACCELERATION_STRUCTURE_BUILD_INPUT_READ_ONLY_KHR;
        let local = vk::MemoryPropertyFlags::DEVICE_LOCAL;
        let vertices = gpu.buffer(VERTEX_CAPACITY * 32, storage | input, local)?;
        let indices = gpu.buffer(INDEX_CAPACITY * 4, storage | input, local)?;
        let mesh_table = gpu.buffer(MAX_MESHES as u64 * 16, storage, local)?;
        let submesh_table = gpu.buffer(SUBMESH_CAPACITY * 16, storage, local)?;
        let scratch = gpu.buffer(SCRATCH_BYTES, storage, local)?;
        let geometry = [vk::AccelerationStructureGeometryKHR::default().geometry_type(vk::GeometryTypeKHR::INSTANCES).geometry(vk::AccelerationStructureGeometryDataKHR { instances: vk::AccelerationStructureGeometryInstancesDataKHR::default() })];
        let inputs = vk::AccelerationStructureBuildGeometryInfoKHR::default().ty(vk::AccelerationStructureTypeKHR::TOP_LEVEL).flags(vk::BuildAccelerationStructureFlagsKHR::PREFER_FAST_TRACE).geometries(&geometry);
        let mut sizes = vk::AccelerationStructureBuildSizesInfoKHR::default();
        unsafe { gpu.accel.get_acceleration_structure_build_sizes(vk::AccelerationStructureBuildTypeKHR::DEVICE, &inputs, &[MAX_INSTANCES as u32], &mut sizes); }
        let tlas_buffer = gpu.buffer(sizes.acceleration_structure_size, vk::BufferUsageFlags::ACCELERATION_STRUCTURE_STORAGE_KHR, local)?;
        let tlas = unsafe { gpu.accel.create_acceleration_structure(&vk::AccelerationStructureCreateInfoKHR::default().buffer(tlas_buffer.buffer).size(sizes.acceleration_structure_size).ty(vk::AccelerationStructureTypeKHR::TOP_LEVEL), None).map_err(err("create TLAS"))? };
        let tlas_scratch = gpu.buffer(sizes.build_scratch_size + gpu.scratch_alignment, storage, local)?;
        let bounds = gpu.buffer(SUBMESH_CAPACITY * 32, storage, local)?;
        let info = vk::ImageCreateInfo::default().image_type(vk::ImageType::TYPE_2D).format(vk::Format::R8G8B8A8_UNORM).extent(vk::Extent3D { width: 1, height: 1, depth: 1 }).mip_levels(1).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL).usage(vk::ImageUsageFlags::SAMPLED | vk::ImageUsageFlags::TRANSFER_DST);
        let (image, allocation) = gpu.image(&info)?;
        let view = gpu.view(image, vk::Format::R8G8B8A8_UNORM, 0, 1, vk::ImageUsageFlags::SAMPLED)?;
        let cmd = gpu.begin()?;
        unsafe {
            for b in [&vertices, &indices, &mesh_table, &submesh_table, &bounds] { gpu.device.cmd_fill_buffer(cmd, b.buffer, 0, vk::WHOLE_SIZE, 0); }
            image_barrier(gpu, cmd, &[(image, vk::ImageLayout::UNDEFINED, vk::ImageLayout::GENERAL, vk::QUEUE_FAMILY_IGNORED, vk::QUEUE_FAMILY_IGNORED)]);
            let range = vk::ImageSubresourceRange { aspect_mask: vk::ImageAspectFlags::COLOR, base_mip_level: 0, level_count: 1, base_array_layer: 0, layer_count: 1 };
            gpu.device.cmd_clear_color_image(cmd, image, vk::ImageLayout::GENERAL, &vk::ClearColorValue { float32: [0.0; 4] }, &[range]);
        }
        let compact_pool = unsafe { gpu.device.create_query_pool(&vk::QueryPoolCreateInfo::default().query_type(vk::QueryType::ACCELERATION_STRUCTURE_COMPACTED_SIZE_KHR).query_count(COMPACT_SLOTS), None).map_err(err("create compaction query pool"))? };
        unsafe { gpu.device.cmd_reset_query_pool(cmd, compact_pool, 0, COMPACT_SLOTS); }
        gpu.finish_now(cmd)?;
        Ok(Scene {
            vertices, indices, mesh_table, submesh_table, scratch, tlas_buffer, tlas, tlas_scratch,
            vertex_alloc: vec![(0, VERTEX_CAPACITY)], index_alloc: vec![(0, INDEX_CAPACITY)], submesh_alloc: vec![(0, SUBMESH_CAPACITY)],
            meshes: IdMap::default(), textures: IdMap::default(), mesh_cpu: vec![GpuMesh::default(); MAX_MESHES as usize], submesh_cpu: vec![GpuSubmesh::default(); SUBMESH_CAPACITY as usize],
            tables_dirty: true, retired: Vec::new(), bounds, bounds_valid: vec![false; SUBMESH_CAPACITY as usize], dummy: (image, allocation, view), scratch_cursor: 0,
            compact_pool, compact_free: (0..COMPACT_SLOTS).rev().collect(), compacting: Vec::new(), completed: 0, compacted_bytes: 0,
        })
    }

    pub fn tlas_address(&self, gpu: &Gpu) -> u64 { unsafe { gpu.accel.get_acceleration_structure_device_address(&vk::AccelerationStructureDeviceAddressInfoKHR::default().acceleration_structure(self.tlas)) } }

    pub fn release_mesh(&mut self, id: u32, fence: u64) {
        if let Some(m) = self.meshes.remove(&id) {
            give(&mut self.vertex_alloc, m.vertex_offset, m.vertex_count as u64);
            give(&mut self.index_alloc, m.index_offset, m.index_count as u64);
            give(&mut self.submesh_alloc, m.submesh_offset, m.submeshes.len() as u64);
            if let Some(b) = m.blas { self.retired.push((fence, Retired::Blas(b))); }
            self.mesh_cpu[id as usize] = GpuMesh::default();
            self.tables_dirty = true;
        }
    }

    pub fn define_mesh(&mut self, id: u32, vertex_count: u32, index_count: u32, submesh_count: u32, fence: u64) -> Fallible<()> {
        if id >= MAX_MESHES { return Err(format!("mesh id {id} out of range")); }
        self.release_mesh(id, fence);
        let vertex_offset = alloc(&mut self.vertex_alloc, vertex_count as u64, 1).ok_or("vertex arena full")?;
        let index_offset = match alloc(&mut self.index_alloc, index_count as u64, 4) { Some(o) => o, None => { give(&mut self.vertex_alloc, vertex_offset, vertex_count as u64); return Err("index arena full".into()); } };
        let submesh_offset = match alloc(&mut self.submesh_alloc, submesh_count.max(1) as u64, 1) { Some(o) => o, None => { give(&mut self.vertex_alloc, vertex_offset, vertex_count as u64); give(&mut self.index_alloc, index_offset, index_count as u64); return Err("submesh table full".into()); } };
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
            if m.blas.is_none() || !m.dynamic || m.refits >= REBUILD_INTERVAL { m.needs_build = true; m.refits = 0; } else { m.needs_refit = true; }
        }
    }

    fn geometry(&self, m: &MeshEntry) -> (Vec<vk::AccelerationStructureGeometryKHR<'static>>, Vec<vk::AccelerationStructureBuildRangeInfoKHR>, Vec<u32>) {
        let vb = self.vertices.address + m.vertex_offset * 32;
        let ib = self.indices.address + m.index_offset * 4;
        let geometry = m.submeshes.iter().map(|s| vk::AccelerationStructureGeometryKHR::default()
            .geometry_type(vk::GeometryTypeKHR::TRIANGLES)
            .flags(if s.flags & SUBMESH_OPAQUE != 0 { vk::GeometryFlagsKHR::OPAQUE } else { vk::GeometryFlagsKHR::NO_DUPLICATE_ANY_HIT_INVOCATION })
            .geometry(vk::AccelerationStructureGeometryDataKHR { triangles: vk::AccelerationStructureGeometryTrianglesDataKHR::default()
                .vertex_format(vk::Format::R32G32B32_SFLOAT).vertex_data(vk::DeviceOrHostAddressConstKHR { device_address: vb }).vertex_stride(32).max_vertex(m.vertex_count.saturating_sub(1))
                .index_type(vk::IndexType::UINT32).index_data(vk::DeviceOrHostAddressConstKHR { device_address: ib + s.index_start as u64 * 4 }) })).collect();
        let ranges = m.submeshes.iter().map(|s| vk::AccelerationStructureBuildRangeInfoKHR { primitive_count: s.index_count / 3, primitive_offset: 0, first_vertex: 0, transform_offset: 0 }).collect();
        let counts = m.submeshes.iter().map(|s| s.index_count / 3).collect();
        (geometry, ranges, counts)
    }

    fn scratch_alloc(&mut self, gpu: &Gpu, cmd: vk::CommandBuffer, size: u64) -> Option<u64> {
        let align = gpu.scratch_alignment;
        let size = size.div_ceil(align) * align;
        if size > SCRATCH_BYTES - align { return None; }
        if self.scratch_cursor + size > SCRATCH_BYTES - align {
            barrier(gpu, cmd);
            self.scratch_cursor = 0;
        }
        let at = self.scratch_cursor;
        self.scratch_cursor += size;
        Some((self.scratch.address + at).div_ceil(align) * align)
    }

    fn compact(&mut self, gpu: &mut Gpu, cmd: vk::CommandBuffer, fence: u64) -> Fallible<u32> {
        let mut copied = 0;
        let mut i = 0;
        while i < self.compacting.len() {
            let (id, done, slot, address) = self.compacting[i];
            if done > self.completed { i += 1; continue; }
            self.compacting.swap_remove(i);
            self.compact_free.push(slot);
            let mut size = [0u64];
            if unsafe { gpu.device.get_query_pool_results(self.compact_pool, slot, &mut size, vk::QueryResultFlags::TYPE_64) }.is_err() { continue; }
            let Some(m) = self.meshes.get_mut(&id) else { continue; };
            if m.blas_address != address || m.dynamic || m.needs_build || size[0] == 0 { continue; }
            let Some(old) = m.blas.as_ref() else { continue; };
            let current = old.buffer.size;
            let size = (size[0] + 255) & !255;
            if size >= current { continue; }
            let source = old.structure;
            let buffer = gpu.buffer(size, vk::BufferUsageFlags::ACCELERATION_STRUCTURE_STORAGE_KHR, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
            let structure = unsafe { gpu.accel.create_acceleration_structure(&vk::AccelerationStructureCreateInfoKHR::default().buffer(buffer.buffer).size(size).ty(vk::AccelerationStructureTypeKHR::BOTTOM_LEVEL), None).map_err(err("create compacted BLAS"))? };
            unsafe { gpu.accel.cmd_copy_acceleration_structure(cmd, &vk::CopyAccelerationStructureInfoKHR::default().src(source).dst(structure).mode(vk::CopyAccelerationStructureModeKHR::COMPACT)); }
            let target = unsafe { gpu.accel.get_acceleration_structure_device_address(&vk::AccelerationStructureDeviceAddressInfoKHR::default().acceleration_structure(structure)) };
            if let Some(old) = m.blas.replace(Blas { buffer, structure }) { self.retired.push((fence, Retired::Blas(old))); }
            m.blas_address = target;
            m.build_scratch = 0;
            m.update_scratch = 0;
            self.compacted_bytes += current - size;
            copied += 1;
        }
        Ok(copied)
    }

    pub fn build(&mut self, gpu: &mut Gpu, cmd: vk::CommandBuffer, fence: u64) -> Fallible<u32> {
        self.scratch_cursor = 0;
        let mut built = self.compact(gpu, cmd, fence)?;
        let ids: Vec<u32> = self.meshes.iter().filter(|(_, m)| m.committed && (m.needs_build || m.needs_refit)).map(|(k, _)| *k).collect();
        let mut queried: Vec<(u32, vk::AccelerationStructureKHR)> = Vec::new();
        for id in ids {
            let (geometry, ranges, counts, dynamic, rebuild) = { let m = &self.meshes[&id]; let (g, r, c) = self.geometry(m); (g, r, c, m.dynamic, m.needs_build) };
            if geometry.is_empty() { continue; }
            let compactable = !dynamic && rebuild;
            let flags = if dynamic { vk::BuildAccelerationStructureFlagsKHR::ALLOW_UPDATE | vk::BuildAccelerationStructureFlagsKHR::PREFER_FAST_BUILD } else { vk::BuildAccelerationStructureFlagsKHR::PREFER_FAST_TRACE | vk::BuildAccelerationStructureFlagsKHR::ALLOW_COMPACTION };
            let mut info = vk::AccelerationStructureBuildGeometryInfoKHR::default().ty(vk::AccelerationStructureTypeKHR::BOTTOM_LEVEL).flags(flags).geometries(&geometry);
            let scratch_size;
            if rebuild && self.meshes[&id].blas.is_some() && self.meshes[&id].build_scratch > 0 {
                let m = &self.meshes[&id];
                scratch_size = m.build_scratch;
                info = info.mode(vk::BuildAccelerationStructureModeKHR::BUILD).dst_acceleration_structure(m.blas.as_ref().unwrap().structure);
            } else if rebuild {
                let mut sizes = vk::AccelerationStructureBuildSizesInfoKHR::default();
                unsafe { gpu.accel.get_acceleration_structure_build_sizes(vk::AccelerationStructureBuildTypeKHR::DEVICE, &info, &counts, &mut sizes); }
                let buffer = gpu.buffer(sizes.acceleration_structure_size, vk::BufferUsageFlags::ACCELERATION_STRUCTURE_STORAGE_KHR, vk::MemoryPropertyFlags::DEVICE_LOCAL)?;
                let structure = unsafe { gpu.accel.create_acceleration_structure(&vk::AccelerationStructureCreateInfoKHR::default().buffer(buffer.buffer).size(sizes.acceleration_structure_size).ty(vk::AccelerationStructureTypeKHR::BOTTOM_LEVEL), None).map_err(err("create BLAS"))? };
                let address = unsafe { gpu.accel.get_acceleration_structure_device_address(&vk::AccelerationStructureDeviceAddressInfoKHR::default().acceleration_structure(structure)) };
                let m = self.meshes.get_mut(&id).unwrap();
                if let Some(old) = m.blas.take() { self.retired.push((fence, Retired::Blas(old))); }
                m.blas_address = address;
                m.blas = Some(Blas { buffer, structure });
                m.update_scratch = sizes.update_scratch_size;
                m.build_scratch = sizes.build_scratch_size;
                scratch_size = sizes.build_scratch_size;
                info = info.mode(vk::BuildAccelerationStructureModeKHR::BUILD).dst_acceleration_structure(structure);
            } else {
                let m = &self.meshes[&id];
                let structure = m.blas.as_ref().unwrap().structure;
                scratch_size = m.update_scratch;
                info = info.mode(vk::BuildAccelerationStructureModeKHR::UPDATE).src_acceleration_structure(structure).dst_acceleration_structure(structure);
            }
            let Some(scratch) = self.scratch_alloc(gpu, cmd, scratch_size) else { continue; };
            info = info.scratch_data(vk::DeviceOrHostAddressKHR { device_address: scratch });
            unsafe { gpu.accel.cmd_build_acceleration_structures(cmd, &[info], &[&ranges]); }
            let m = self.meshes.get_mut(&id).unwrap();
            m.needs_build = false;
            m.needs_refit = false;
            built += 1;
            if compactable {
                let (address, structure) = (m.blas_address, m.blas.as_ref().unwrap().structure);
                let stale: Vec<u32> = self.compacting.iter().filter(|c| c.0 == id).map(|c| c.2).collect();
                self.compacting.retain(|c| c.0 != id);
                self.compact_free.extend(stale);
                if let Some(slot) = self.compact_free.pop() {
                    self.compacting.push((id, fence, slot, address));
                    queried.push((slot, structure));
                }
            }
        }
        if built > 0 { barrier(gpu, cmd); }
        for (slot, structure) in queried {
            unsafe {
                gpu.device.cmd_reset_query_pool(cmd, self.compact_pool, slot, 1);
                gpu.accel.cmd_write_acceleration_structures_properties(cmd, &[structure], vk::QueryType::ACCELERATION_STRUCTURE_COMPACTED_SIZE_KHR, self.compact_pool, slot);
            }
        }
        Ok(built)
    }

    pub fn build_tlas(&self, gpu: &Gpu, cmd: vk::CommandBuffer, instance_descs: u64, count: u32) {
        let geometry = [vk::AccelerationStructureGeometryKHR::default().geometry_type(vk::GeometryTypeKHR::INSTANCES).geometry(vk::AccelerationStructureGeometryDataKHR { instances: vk::AccelerationStructureGeometryInstancesDataKHR::default().array_of_pointers(false).data(vk::DeviceOrHostAddressConstKHR { device_address: instance_descs }) })];
        let align = gpu.scratch_alignment;
        let info = vk::AccelerationStructureBuildGeometryInfoKHR::default().ty(vk::AccelerationStructureTypeKHR::TOP_LEVEL).flags(vk::BuildAccelerationStructureFlagsKHR::PREFER_FAST_TRACE).mode(vk::BuildAccelerationStructureModeKHR::BUILD)
            .dst_acceleration_structure(self.tlas).geometries(&geometry).scratch_data(vk::DeviceOrHostAddressKHR { device_address: self.tlas_scratch.address.div_ceil(align) * align });
        let range = [vk::AccelerationStructureBuildRangeInfoKHR { primitive_count: count, primitive_offset: 0, first_vertex: 0, transform_offset: 0 }];
        unsafe { gpu.accel.cmd_build_acceleration_structures(cmd, &[info], &[&range]); }
        barrier(gpu, cmd);
    }

    pub fn define_texture(&mut self, gpu: &mut Gpu, id: u32, width: u32, height: u32, flags: u32, fence: u64) -> Fallible<Option<vk::ImageView>> {
        if id >= MAX_TEXTURES { return Ok(None); }
        if let Some(old) = self.textures.remove(&id) { self.retired.push((fence, Retired::Texture(old))); }
        let hdr = flags & TEXTURE_HDR != 0;
        let width = width.clamp(1, MAX_TEXTURE_SIZE);
        let height = height.clamp(1, MAX_TEXTURE_SIZE);
        let mips = 32 - width.max(height).leading_zeros();
        let (storage, sampled) = if hdr { (vk::Format::R16G16B16A16_SFLOAT, vk::Format::R16G16B16A16_SFLOAT) } else { (vk::Format::R8G8B8A8_UNORM, vk::Format::R8G8B8A8_SRGB) };
        let formats = [storage, sampled];
        let mut list = vk::ImageFormatListCreateInfo::default().view_formats(&formats);
        let info = vk::ImageCreateInfo::default().flags(vk::ImageCreateFlags::MUTABLE_FORMAT | vk::ImageCreateFlags::EXTENDED_USAGE).image_type(vk::ImageType::TYPE_2D).format(storage).extent(vk::Extent3D { width, height, depth: 1 })
            .mip_levels(mips).array_layers(1).samples(vk::SampleCountFlags::TYPE_1).tiling(vk::ImageTiling::OPTIMAL).usage(vk::ImageUsageFlags::SAMPLED | vk::ImageUsageFlags::STORAGE | vk::ImageUsageFlags::TRANSFER_DST).push_next(&mut list);
        let (image, allocation) = gpu.image(&info)?;
        let view = gpu.view(image, sampled, 0, mips, vk::ImageUsageFlags::SAMPLED)?;
        let mut mips_views = Vec::with_capacity(mips as usize);
        for m in 0..mips { mips_views.push(gpu.view(image, storage, m, 1, vk::ImageUsageFlags::STORAGE)?); }
        self.textures.insert(id, TextureEntry { image, allocation, sampled: view, mips_views, width, height, mips, hdr, readable: false, cleared: false });
        Ok(Some(view))
    }

    pub fn release_texture(&mut self, id: u32, fence: u64) -> bool {
        if let Some(old) = self.textures.remove(&id) { self.retired.push((fence, Retired::Texture(old))); return true; }
        false
    }

    pub fn collect(&mut self, gpu: &mut Gpu, completed: u64) {
        self.completed = completed;
        let mut keep = Vec::new();
        for (f, r) in self.retired.drain(..) {
            if f > completed { keep.push((f, r)); continue; }
            unsafe {
                match r {
                    Retired::Blas(b) => { gpu.accel.destroy_acceleration_structure(b.structure, None); gpu.destroy_buffer(b.buffer); }
                    Retired::Texture(t) => {
                        gpu.device.destroy_image_view(t.sampled, None);
                        for v in t.mips_views { gpu.device.destroy_image_view(v, None); }
                        gpu.device.destroy_image(t.image, None);
                        gpu.free(t.allocation);
                    }
                }
            }
        }
        self.retired = keep;
    }
}
