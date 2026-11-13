use crate::gpu::*;
use basis_rt_protocol::*;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Threading::*;
use windows::core::*;

pub const fn row_pitch(width: u32) -> u32 { (width * 8).div_ceil(256) * 256 }

pub fn footprint(buffer: &ID3D12Resource, width: u32, height: u32, format: DXGI_FORMAT) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(buffer) }, Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { PlacedFootprint: D3D12_PLACED_SUBRESOURCE_FOOTPRINT { Offset: 0, Footprint: D3D12_SUBRESOURCE_FOOTPRINT { Format: format, Width: width, Height: height, Depth: 1, RowPitch: row_pitch(width) } } } }
}

pub fn subresource(texture: &ID3D12Resource) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(texture) }, Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { SubresourceIndex: 0 } }
}

pub fn upload_rows(texels: u32) -> u32 { texels.div_ceil(UPLOAD_WIDTH).min(UPLOAD_HEIGHT) * 2 }

pub struct Cross {
    pub gbuffer: [ID3D12Resource; 2],
    pub output: [ID3D12Resource; 2],
}

struct RelayContext {
    gbuffer: [ID3D12Resource; 2],
    output: [ID3D12Resource; 2],
    cross_gbuffer: [ID3D12Resource; 2],
    cross_output: [ID3D12Resource; 2],
    full: u32,
    height: u32,
}

pub struct Relay {
    pub device: ID3D12Device,
    queue_in: ID3D12CommandQueue,
    queue_out: ID3D12CommandQueue,
    allocators_in: Vec<ID3D12CommandAllocator>,
    allocators_out: Vec<ID3D12CommandAllocator>,
    list: ID3D12GraphicsCommandList,
    event: HANDLE,
    slot_frames: [u64; RING],
    pub fence_unity: ID3D12Fence,
    pub fence_tracer: ID3D12Fence,
    fence_in: ID3D12Fence,
    fence_out: ID3D12Fence,
    pub trace_in: ID3D12Fence,
    pub trace_out: ID3D12Fence,
    pub upload: [ID3D12Resource; 2],
    cross_upload: [ID3D12Resource; 2],
    contexts: Vec<Option<RelayContext>>,
}

fn cross_buffer(creator: &ID3D12Device, opener: &ID3D12Device, bytes: u64) -> Result<(ID3D12Resource, ID3D12Resource)> {
    unsafe {
        let desc = buffer_desc(bytes, D3D12_RESOURCE_FLAG_ALLOW_CROSS_ADAPTER);
        let heap_desc = D3D12_HEAP_DESC { SizeInBytes: desc.Width.div_ceil(65536) * 65536, Properties: heap_props(D3D12_HEAP_TYPE_DEFAULT), Alignment: 0, Flags: D3D12_HEAP_FLAG_SHARED | D3D12_HEAP_FLAG_SHARED_CROSS_ADAPTER };
        let mut heap: Option<ID3D12Heap> = None;
        creator.CreateHeap(&heap_desc, &mut heap)?;
        let heap = heap.ok_or_else(|| Error::from(E_FAIL))?;
        let handle = creator.CreateSharedHandle(&heap, None, GENERIC_ALL.0, PCWSTR::null())?;
        let mut opened: Option<ID3D12Heap> = None;
        let result = opener.OpenSharedHandle(handle, &mut opened);
        let _ = CloseHandle(handle);
        result?;
        let opened = opened.ok_or_else(|| Error::from(E_FAIL))?;
        let (mut near, mut far): (Option<ID3D12Resource>, Option<ID3D12Resource>) = (None, None);
        creator.CreatePlacedResource(&heap, 0, &desc, D3D12_RESOURCE_STATE_COMMON, None, &mut near)?;
        opener.CreatePlacedResource(&opened, 0, &desc, D3D12_RESOURCE_STATE_COMMON, None, &mut far)?;
        Ok((near.ok_or_else(|| Error::from(E_FAIL))?, far.ok_or_else(|| Error::from(E_FAIL))?))
    }
}

fn cross_fence(creator: &ID3D12Device, opener: &ID3D12Device) -> Result<(ID3D12Fence, ID3D12Fence)> {
    unsafe {
        let created: ID3D12Fence = creator.CreateFence(0, D3D12_FENCE_FLAG_SHARED | D3D12_FENCE_FLAG_SHARED_CROSS_ADAPTER)?;
        let handle = creator.CreateSharedHandle(&created, None, GENERIC_ALL.0, PCWSTR::null())?;
        let mut opened: Option<ID3D12Fence> = None;
        let result = opener.OpenSharedHandle(handle, &mut opened);
        let _ = CloseHandle(handle);
        result?;
        Ok((created, opened.ok_or_else(|| Error::from(E_FAIL))?))
    }
}

fn shared_texture(device: &ID3D12Device, width: u32, height: u32, format: DXGI_FORMAT) -> Result<ID3D12Resource> {
    let flags = D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET;
    let mut r: Option<ID3D12Resource> = None;
    unsafe { device.CreateCommittedResource(&heap_props(D3D12_HEAP_TYPE_DEFAULT), D3D12_HEAP_FLAG_SHARED, &texture_desc(width, height, 1, format, flags), D3D12_RESOURCE_STATE_COMMON, None, &mut r)?; }
    r.ok_or_else(|| Error::from(E_FAIL))
}

impl Relay {
    pub fn new(luid_low: u32, luid_high: i32, trace: &ID3D12Device) -> Result<(Relay, [ID3D12Resource; 2])> {
        unsafe {
            let factory: IDXGIFactory6 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0))?;
            let adapter: IDXGIAdapter1 = factory.EnumAdapterByLuid(LUID { LowPart: luid_low, HighPart: luid_high })?;
            let device: ID3D12Device = crate::d3d::create_device(&adapter.cast()?, D3D_FEATURE_LEVEL_11_0)?;
            let queue = || device.CreateCommandQueue::<ID3D12CommandQueue>(&D3D12_COMMAND_QUEUE_DESC { Type: D3D12_COMMAND_LIST_TYPE_DIRECT, Priority: D3D12_COMMAND_QUEUE_PRIORITY_HIGH.0, ..Default::default() });
            let (queue_in, queue_out) = (queue()?, queue()?);
            let allocators = || (0..RING).map(|_| device.CreateCommandAllocator::<ID3D12CommandAllocator>(D3D12_COMMAND_LIST_TYPE_DIRECT)).collect::<Result<Vec<_>>>();
            let (allocators_in, allocators_out) = (allocators()?, allocators()?);
            let list: ID3D12GraphicsCommandList = device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocators_in[0], None)?;
            list.Close()?;
            let fence_unity: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_SHARED)?;
            let fence_tracer: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_SHARED)?;
            let (fence_in, trace_in) = cross_fence(&device, trace)?;
            let (fence_out, trace_out) = cross_fence(&device, trace)?;
            let upload = [shared_texture(&device, UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, DXGI_FORMAT_R16G16B16A16_UINT)?, shared_texture(&device, UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, DXGI_FORMAT_R16G16B16A16_UINT)?];
            let bytes = row_pitch(UPLOAD_WIDTH) as u64 * UPLOAD_PHYSICAL_HEIGHT as u64;
            let (a, b) = (cross_buffer(&device, trace, bytes)?, cross_buffer(&device, trace, bytes)?);
            let event = CreateEventW(None, false, false, None)?;
            Ok((Relay { device, queue_in, queue_out, allocators_in, allocators_out, list, event, slot_frames: [0; RING], fence_unity, fence_tracer, fence_in, fence_out, trace_in, trace_out, upload, cross_upload: [a.0, b.0], contexts: (0..MAX_CONTEXTS).map(|_| None).collect() }, [a.1, b.1]))
        }
    }

    pub fn create_context(&mut self, trace: &ID3D12Device, context: usize, width: u32, height: u32, views: u32) -> Result<([ID3D12Resource; 4], Cross)> {
        let full = width * views;
        let g = || shared_texture(&self.device, full * 2, height * 2, DXGI_FORMAT_R16G16B16A16_UINT);
        let o = || shared_texture(&self.device, full, height, DXGI_FORMAT_R16G16B16A16_FLOAT);
        let gbuffer = [g()?, g()?];
        let output = [o()?, o()?];
        let gbytes = row_pitch(full * 2) as u64 * (height * 2) as u64;
        let obytes = row_pitch(full) as u64 * height as u64;
        let (g0, g1) = (cross_buffer(&self.device, trace, gbytes)?, cross_buffer(&self.device, trace, gbytes)?);
        let (o0, o1) = (cross_buffer(&self.device, trace, obytes)?, cross_buffer(&self.device, trace, obytes)?);
        let shared = [gbuffer[0].clone(), gbuffer[1].clone(), output[0].clone(), output[1].clone()];
        self.contexts[context] = Some(RelayContext { gbuffer, output, cross_gbuffer: [g0.0, g1.0], cross_output: [o0.0, o1.0], full, height });
        Ok((shared, Cross { gbuffer: [g0.1, g1.1], output: [o0.1, o1.1] }))
    }

    fn wait(&self, fence: &ID3D12Fence, value: u64) {
        unsafe {
            if value > 0 && fence.GetCompletedValue() < value && fence.SetEventOnCompletion(value, self.event).is_ok() { WaitForSingleObject(self.event, 5000); }
        }
    }

    pub fn release_context(&mut self, context: usize) {
        let last = self.slot_frames.iter().copied().max().unwrap_or(0);
        self.wait(&self.fence_in, last);
        self.wait(&self.fence_tracer, last);
        self.contexts[context] = None;
    }

    pub fn send(&mut self, params: &FrameParams) -> Result<()> {
        let frame = params.frame;
        let slot = (frame % RING as u64) as usize;
        let k = (params.parity & 1) as usize;
        let Some(ctx) = self.contexts[(params.context as usize).min(MAX_CONTEXTS - 1)].as_ref() else { return Err(Error::new(E_FAIL, "frame for an unallocated context")); };
        self.wait(&self.fence_in, self.slot_frames[slot]);
        self.wait(&self.fence_tracer, self.slot_frames[slot]);
        unsafe {
            self.allocators_in[slot].Reset()?;
            self.list.Reset(&self.allocators_in[slot], None)?;
            self.list.CopyTextureRegion(&footprint(&ctx.cross_gbuffer[k], ctx.full * 2, ctx.height * 2, DXGI_FORMAT_R16G16B16A16_UINT), 0, 0, 0, &subresource(&ctx.gbuffer[k]), None);
            let rows = upload_rows(params.upload_texels);
            if rows > 0 {
                let index = (frame & 1) as usize;
                let area = D3D12_BOX { left: 0, top: 0, front: 0, right: UPLOAD_WIDTH, bottom: rows, back: 1 };
                self.list.CopyTextureRegion(&footprint(&self.cross_upload[index], UPLOAD_WIDTH, rows, DXGI_FORMAT_R16G16B16A16_UINT), 0, 0, 0, &subresource(&self.upload[index]), Some(&area));
            }
            self.list.Close()?;
            self.queue_in.Wait(&self.fence_unity, frame)?;
            self.queue_in.ExecuteCommandLists(&[Some(self.list.cast()?)]);
            self.queue_in.Signal(&self.fence_in, frame)?;
        }
        Ok(())
    }

    pub fn receive(&mut self, params: &FrameParams) -> Result<()> {
        let frame = params.frame;
        let slot = (frame % RING as u64) as usize;
        let k = (params.parity & 1) as usize;
        let Some(ctx) = self.contexts[(params.context as usize).min(MAX_CONTEXTS - 1)].as_ref() else { return Err(Error::new(E_FAIL, "frame for an unallocated context")); };
        unsafe {
            self.allocators_out[slot].Reset()?;
            self.list.Reset(&self.allocators_out[slot], None)?;
            self.list.CopyTextureRegion(&subresource(&ctx.output[k]), 0, 0, 0, &footprint(&ctx.cross_output[k], ctx.full, ctx.height, DXGI_FORMAT_R16G16B16A16_FLOAT), None);
            self.list.Close()?;
            self.queue_out.Wait(&self.fence_out, frame)?;
            self.queue_out.ExecuteCommandLists(&[Some(self.list.cast()?)]);
            self.queue_out.Signal(&self.fence_tracer, frame)?;
        }
        self.slot_frames[slot] = frame;
        Ok(())
    }
}
