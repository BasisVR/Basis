use std::mem::ManuallyDrop;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Threading::*;
use windows::core::*;

pub const SCRATCH_DESCRIPTORS: u32 = 1024 + 4096 * 2;
pub const SCRATCH_PER_SLOT: u32 = 4096;
pub const HEAP_SIZE: u32 = SCRATCH_DESCRIPTORS + SCRATCH_PER_SLOT * basis_rt_protocol::RING as u32;

pub struct Gpu {
    pub device: ID3D12Device5,
    pub queue: ID3D12CommandQueue,
    pub heap: ID3D12DescriptorHeap,
    pub heap_cpu: D3D12_CPU_DESCRIPTOR_HANDLE,
    pub heap_gpu: D3D12_GPU_DESCRIPTOR_HANDLE,
    pub increment: u32,
    pub scratch_cursor: u32,
    pub scratch_base: u32,
    pub fence: ID3D12Fence,
    pub fence_value: u64,
    pub fence_event: HANDLE,
    pub caps: u32,
}

pub fn heap_props(kind: D3D12_HEAP_TYPE) -> D3D12_HEAP_PROPERTIES { D3D12_HEAP_PROPERTIES { Type: kind, ..Default::default() } }

pub fn buffer_desc(size: u64, flags: D3D12_RESOURCE_FLAGS) -> D3D12_RESOURCE_DESC {
    D3D12_RESOURCE_DESC { Dimension: D3D12_RESOURCE_DIMENSION_BUFFER, Width: size.max(256), Height: 1, DepthOrArraySize: 1, MipLevels: 1, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR, Flags: flags, ..Default::default() }
}

pub fn texture_desc(width: u32, height: u32, mips: u16, format: DXGI_FORMAT, flags: D3D12_RESOURCE_FLAGS) -> D3D12_RESOURCE_DESC {
    D3D12_RESOURCE_DESC { Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D, Width: width.max(1) as u64, Height: height.max(1), DepthOrArraySize: 1, MipLevels: mips, Format: format, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Layout: D3D12_TEXTURE_LAYOUT_UNKNOWN, Flags: flags, ..Default::default() }
}

pub fn transition(resource: &ID3D12Resource, before: D3D12_RESOURCE_STATES, after: D3D12_RESOURCE_STATES) -> D3D12_RESOURCE_BARRIER {
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 { Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER { pResource: unsafe { std::mem::transmute_copy(resource) }, Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES, StateBefore: before, StateAfter: after }) },
    }
}

pub fn uav_barrier(resource: Option<&ID3D12Resource>) -> D3D12_RESOURCE_BARRIER {
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_UAV,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 { UAV: ManuallyDrop::new(D3D12_RESOURCE_UAV_BARRIER { pResource: match resource { Some(r) => unsafe { std::mem::transmute_copy(r) }, None => ManuallyDrop::new(None) } }) },
    }
}

impl Gpu {
    pub fn new(luid_low: u32, luid_high: i32) -> Result<Gpu> {
        unsafe {
            if std::env::var_os("BASIS_RT_DEBUG").is_some() {
                let mut debug: Option<ID3D12Debug> = None;
                if D3D12GetDebugInterface(&mut debug).is_ok() { if let Some(d) = debug { d.EnableDebugLayer(); } }
            }
            let factory: IDXGIFactory6 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0))?;
            let adapter: IDXGIAdapter1 = if luid_low != 0 || luid_high != 0 { factory.EnumAdapterByLuid(LUID { LowPart: luid_low, HighPart: luid_high })? } else { factory.EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE)? };
            let mut device: Option<ID3D12Device5> = None;
            D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_12_1, &mut device)?;
            let device = device.ok_or_else(|| Error::from(E_FAIL))?;
            let mut options5 = D3D12_FEATURE_DATA_D3D12_OPTIONS5::default();
            device.CheckFeatureSupport(D3D12_FEATURE_D3D12_OPTIONS5, &mut options5 as *mut _ as _, std::mem::size_of_val(&options5) as u32)?;
            if options5.RaytracingTier.0 < 11 { return Err(Error::new(E_NOTIMPL, "this GPU does not support DXR 1.1")); }
            let mut caps = 0u32;
            if options5.RaytracingTier.0 >= 12 { caps |= basis_rt_protocol::CAP_TIER_1_2 | basis_rt_protocol::CAP_OMM; }
            let mut model = D3D12_FEATURE_DATA_SHADER_MODEL { HighestShaderModel: D3D_SHADER_MODEL(0x69) };
            if device.CheckFeatureSupport(D3D12_FEATURE_SHADER_MODEL, &mut model as *mut _ as _, std::mem::size_of_val(&model) as u32).is_err() || model.HighestShaderModel.0 < 0x69 {
                return Err(Error::new(E_NOTIMPL, "the driver does not expose Shader Model 6.9 (update the GPU driver)"));
            }
            caps |= basis_rt_protocol::CAP_SER;
            let mut options22 = crate::ffi::Options22::default();
            if device.CheckFeatureSupport(D3D12_FEATURE(65), &mut options22 as *mut _ as _, std::mem::size_of_val(&options22) as u32).is_ok() && options22.ser_actually_reorders.as_bool() { caps |= basis_rt_protocol::CAP_SER_REORDERS; }
            let queue: ID3D12CommandQueue = device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC { Type: D3D12_COMMAND_LIST_TYPE_DIRECT, Priority: D3D12_COMMAND_QUEUE_PRIORITY_HIGH.0, ..Default::default() })?;
            let heap: ID3D12DescriptorHeap = device.CreateDescriptorHeap(&D3D12_DESCRIPTOR_HEAP_DESC { Type: D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV, NumDescriptors: HEAP_SIZE, Flags: D3D12_DESCRIPTOR_HEAP_FLAG_SHADER_VISIBLE, NodeMask: 0 })?;
            let heap_cpu = heap.GetCPUDescriptorHandleForHeapStart();
            let heap_gpu = heap.GetGPUDescriptorHandleForHeapStart();
            let increment = device.GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_CBV_SRV_UAV);
            let fence: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE)?;
            let fence_event = CreateEventW(None, false, false, None)?;
            Ok(Gpu { device, queue, heap, heap_cpu, heap_gpu, increment, scratch_cursor: 0, scratch_base: SCRATCH_DESCRIPTORS, fence, fence_value: 0, fence_event, caps })
        }
    }

    pub fn cpu(&self, index: u32) -> D3D12_CPU_DESCRIPTOR_HANDLE { D3D12_CPU_DESCRIPTOR_HANDLE { ptr: self.heap_cpu.ptr + (index * self.increment) as usize } }

    pub fn begin_scratch(&mut self, slot: usize) {
        self.scratch_base = SCRATCH_DESCRIPTORS + (slot % basis_rt_protocol::RING) as u32 * SCRATCH_PER_SLOT;
        self.scratch_cursor = 0;
    }

    pub fn scratch_descriptor(&mut self) -> u32 {
        let index = self.scratch_base + (self.scratch_cursor % SCRATCH_PER_SLOT);
        self.scratch_cursor += 1;
        index
    }

    pub fn buffer(&self, size: u64, kind: D3D12_HEAP_TYPE, flags: D3D12_RESOURCE_FLAGS, state: D3D12_RESOURCE_STATES) -> Result<ID3D12Resource> {
        let mut r: Option<ID3D12Resource> = None;
        unsafe { self.device.CreateCommittedResource(&heap_props(kind), D3D12_HEAP_FLAG_NONE, &buffer_desc(size, flags), state, None, &mut r)?; }
        r.ok_or_else(|| Error::from(E_FAIL))
    }

    pub fn texture(&self, desc: &D3D12_RESOURCE_DESC, heap_flags: D3D12_HEAP_FLAGS, state: D3D12_RESOURCE_STATES) -> Result<ID3D12Resource> {
        let mut r: Option<ID3D12Resource> = None;
        unsafe { self.device.CreateCommittedResource(&heap_props(D3D12_HEAP_TYPE_DEFAULT), heap_flags, desc, state, None, &mut r)?; }
        r.ok_or_else(|| Error::from(E_FAIL))
    }

    pub fn srv_texture(&self, resource: &ID3D12Resource, format: DXGI_FORMAT, mips: u32, index: u32) {
        let desc = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: format, ViewDimension: D3D12_SRV_DIMENSION_TEXTURE2D, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Texture2D: D3D12_TEX2D_SRV { MostDetailedMip: 0, MipLevels: mips, PlaneSlice: 0, ResourceMinLODClamp: 0.0 } } };
        unsafe { self.device.CreateShaderResourceView(resource, Some(&desc), self.cpu(index)); }
    }

    pub fn uav_texture(&self, resource: &ID3D12Resource, format: DXGI_FORMAT, mip: u32, index: u32) {
        let desc = D3D12_UNORDERED_ACCESS_VIEW_DESC { Format: format, ViewDimension: D3D12_UAV_DIMENSION_TEXTURE2D, Anonymous: D3D12_UNORDERED_ACCESS_VIEW_DESC_0 { Texture2D: D3D12_TEX2D_UAV { MipSlice: mip, PlaneSlice: 0 } } };
        unsafe { self.device.CreateUnorderedAccessView(resource, None::<&ID3D12Resource>, Some(&desc), self.cpu(index)); }
    }

    pub fn srv_raw(&self, resource: &ID3D12Resource, bytes: u64, index: u32) {
        let desc = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_R32_TYPELESS, ViewDimension: D3D12_SRV_DIMENSION_BUFFER, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Buffer: D3D12_BUFFER_SRV { FirstElement: 0, NumElements: (bytes / 4) as u32, StructureByteStride: 0, Flags: D3D12_BUFFER_SRV_FLAG_RAW } } };
        unsafe { self.device.CreateShaderResourceView(resource, Some(&desc), self.cpu(index)); }
    }

    pub fn uav_raw(&self, resource: &ID3D12Resource, bytes: u64, index: u32) {
        let desc = D3D12_UNORDERED_ACCESS_VIEW_DESC { Format: DXGI_FORMAT_R32_TYPELESS, ViewDimension: D3D12_UAV_DIMENSION_BUFFER, Anonymous: D3D12_UNORDERED_ACCESS_VIEW_DESC_0 { Buffer: D3D12_BUFFER_UAV { FirstElement: 0, NumElements: (bytes / 4) as u32, StructureByteStride: 0, CounterOffsetInBytes: 0, Flags: D3D12_BUFFER_UAV_FLAG_RAW } } };
        unsafe { self.device.CreateUnorderedAccessView(resource, None::<&ID3D12Resource>, Some(&desc), self.cpu(index)); }
    }

    pub fn srv_structured(&self, resource: &ID3D12Resource, first: u64, count: u32, stride: u32, index: u32) {
        let desc = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_UNKNOWN, ViewDimension: D3D12_SRV_DIMENSION_BUFFER, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { Buffer: D3D12_BUFFER_SRV { FirstElement: first, NumElements: count.max(1), StructureByteStride: stride, Flags: D3D12_BUFFER_SRV_FLAG_NONE } } };
        unsafe { self.device.CreateShaderResourceView(resource, Some(&desc), self.cpu(index)); }
    }

    pub fn srv_tlas(&self, address: u64, index: u32) {
        let desc = D3D12_SHADER_RESOURCE_VIEW_DESC { Format: DXGI_FORMAT_UNKNOWN, ViewDimension: D3D12_SRV_DIMENSION_RAYTRACING_ACCELERATION_STRUCTURE, Shader4ComponentMapping: D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING, Anonymous: D3D12_SHADER_RESOURCE_VIEW_DESC_0 { RaytracingAccelerationStructure: D3D12_RAYTRACING_ACCELERATION_STRUCTURE_SRV { Location: address } } };
        unsafe { self.device.CreateShaderResourceView(None::<&ID3D12Resource>, Some(&desc), self.cpu(index)); }
    }

    pub fn flush(&mut self) -> Result<()> {
        unsafe {
            self.fence_value += 1;
            self.queue.Signal(&self.fence, self.fence_value)?;
            if self.fence.GetCompletedValue() < self.fence_value {
                self.fence.SetEventOnCompletion(self.fence_value, self.fence_event)?;
                WaitForSingleObject(self.fence_event, INFINITE);
            }
        }
        Ok(())
    }

    pub fn signal(&mut self) -> Result<u64> {
        self.fence_value += 1;
        unsafe { self.queue.Signal(&self.fence, self.fence_value)?; }
        Ok(self.fence_value)
    }

    pub fn wait(&self, value: u64) -> Result<()> {
        unsafe {
            if self.fence.GetCompletedValue() < value {
                self.fence.SetEventOnCompletion(value, self.fence_event)?;
                WaitForSingleObject(self.fence_event, INFINITE);
            }
        }
        Ok(())
    }
}

pub struct UploadRing {
    pub resource: ID3D12Resource,
    pub mapped: *mut u8,
    pub size: u64,
    pub address: u64,
}

impl UploadRing {
    pub fn new(gpu: &Gpu, size: u64) -> Result<UploadRing> {
        let resource = gpu.buffer(size, D3D12_HEAP_TYPE_UPLOAD, D3D12_RESOURCE_FLAG_NONE, D3D12_RESOURCE_STATE_GENERIC_READ)?;
        let mut mapped = std::ptr::null_mut();
        unsafe { resource.Map(0, Some(&D3D12_RANGE { Begin: 0, End: 0 }), Some(&mut mapped))?; }
        let address = unsafe { resource.GetGPUVirtualAddress() };
        Ok(UploadRing { resource, mapped: mapped as *mut u8, size, address })
    }
}

pub struct RangeAllocator {
    free: Vec<(u64, u64)>,
}

impl RangeAllocator {
    pub fn new(capacity: u64) -> Self { RangeAllocator { free: vec![(0, capacity)] } }
    pub fn alloc(&mut self, size: u64, align: u64) -> Option<u64> {
        for i in 0..self.free.len() {
            let (start, len) = self.free[i];
            let aligned = (start + align - 1) / align * align;
            let pad = aligned - start;
            if len >= size + pad {
                let end = start + len;
                self.free.remove(i);
                if pad > 0 { self.free.insert(i, (start, pad)); }
                let rest = end - (aligned + size);
                if rest > 0 { self.free.push((aligned + size, rest)); self.free.sort_unstable_by_key(|r| r.0); }
                return Some(aligned);
            }
        }
        None
    }
    pub fn release(&mut self, start: u64, size: u64) {
        if size == 0 { return; }
        self.free.push((start, size));
        self.free.sort_unstable_by_key(|r| r.0);
        let mut merged: Vec<(u64, u64)> = Vec::with_capacity(self.free.len());
        for &(s, l) in &self.free {
            if let Some(last) = merged.last_mut() { if last.0 + last.1 == s { last.1 += l; continue; } }
            merged.push((s, l));
        }
        self.free = merged;
    }
}
