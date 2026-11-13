#![allow(non_snake_case)]
use BasisRayTracingBridge::*;
use basis_rt_protocol::*;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D::*;
use windows::Win32::Graphics::Direct3D11::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::Threading::*;
use windows::core::*;

struct Unity {
    device: ID3D12Device,
    queue: ID3D12CommandQueue,
    allocator: ID3D12CommandAllocator,
    list: ID3D12GraphicsCommandList,
    fence: ID3D12Fence,
    value: u64,
    event: HANDLE,
}

impl Unity {
    fn run(&mut self, body: impl FnOnce(&ID3D12GraphicsCommandList)) {
        unsafe {
            self.allocator.Reset().unwrap();
            self.list.Reset(&self.allocator, None).unwrap();
            body(&self.list);
            self.list.Close().unwrap();
            self.queue.ExecuteCommandLists(&[Some(self.list.cast().unwrap())]);
            self.value += 1;
            self.queue.Signal(&self.fence, self.value).unwrap();
        }
    }
    fn finish(&self) { unsafe { if self.fence.GetCompletedValue() < self.value { self.fence.SetEventOnCompletion(self.value, self.event).unwrap(); WaitForSingleObject(self.event, 10000); } } }
    fn texture(&self, w: u32, h: u32, format: DXGI_FORMAT) -> ID3D12Resource {
        let desc = D3D12_RESOURCE_DESC { Dimension: D3D12_RESOURCE_DIMENSION_TEXTURE2D, Width: w as u64, Height: h, DepthOrArraySize: 1, MipLevels: 1, Format: format, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Flags: D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS | D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET, ..Default::default() };
        let mut r: Option<ID3D12Resource> = None;
        unsafe { self.device.CreateCommittedResource(&D3D12_HEAP_PROPERTIES { Type: D3D12_HEAP_TYPE_DEFAULT, ..Default::default() }, D3D12_HEAP_FLAG_NONE, &desc, D3D12_RESOURCE_STATE_COMMON, None, &mut r).unwrap(); }
        r.unwrap()
    }
    fn buffer(&self, size: u64, kind: D3D12_HEAP_TYPE) -> (ID3D12Resource, *mut u8) {
        let desc = D3D12_RESOURCE_DESC { Dimension: D3D12_RESOURCE_DIMENSION_BUFFER, Width: size, Height: 1, DepthOrArraySize: 1, MipLevels: 1, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR, ..Default::default() };
        let mut r: Option<ID3D12Resource> = None;
        let state = if kind == D3D12_HEAP_TYPE_UPLOAD { D3D12_RESOURCE_STATE_GENERIC_READ } else { D3D12_RESOURCE_STATE_COPY_DEST };
        unsafe { self.device.CreateCommittedResource(&D3D12_HEAP_PROPERTIES { Type: kind, ..Default::default() }, D3D12_HEAP_FLAG_NONE, &desc, state, None, &mut r).unwrap(); }
        let r = r.unwrap();
        let mut p = std::ptr::null_mut();
        unsafe { r.Map(0, None, Some(&mut p)).unwrap(); }
        (r, p as *mut u8)
    }
}

fn footprint(buffer: &ID3D12Resource, w: u32, h: u32, format: DXGI_FORMAT, pitch: u32) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(buffer) }, Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { PlacedFootprint: D3D12_PLACED_SUBRESOURCE_FOOTPRINT { Offset: 0, Footprint: D3D12_SUBRESOURCE_FOOTPRINT { Format: format, Width: w, Height: h, Depth: 1, RowPitch: pitch } } } }
}

fn subresource(texture: &ID3D12Resource) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(texture) }, Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { SubresourceIndex: 0 } }
}

pub struct Client {
    unity: Unity,
    device11: Option<ID3D11Device>,
    direct: Option<([ID3D11Texture2D; 2], [ID3D11Texture2D; 2])>,
    d11: Option<(ID3D11DeviceContext, ID3D11Texture2D, ID3D11Texture2D, ID3D11Texture2D, ID3D11Texture2D, ID3D11Texture2D)>,
    gbuffer: ID3D12Resource,
    upload: ID3D12Resource,
    result: ID3D12Resource,
    _second: ID3D12Resource,
    gstage: ID3D12Resource,
    ustage: ID3D12Resource,
    readback: ID3D12Resource,
    pub gptr: *mut u8,
    pub uptr: *mut u8,
    pub rptr: *mut u8,
    w: u32,
    h: u32,
    rpitch: u32,
}

impl Client {
    pub unsafe fn new(w: u32, h: u32, rpitch: u32) -> (Client, Bridge) {
        unsafe {
            let factory: IDXGIFactory6 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0)).unwrap();
            let adapter: IDXGIAdapter1 = if std::env::var("CLIENT_ADAPTER").map_or(false, |v| v.eq_ignore_ascii_case("warp")) { factory.EnumWarpAdapter().unwrap() } else { factory.EnumAdapterByGpuPreference(0, DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE).unwrap() };
            let mut device: Option<ID3D12Device> = None;
            D3D12CreateDevice(&adapter, D3D_FEATURE_LEVEL_12_0, &mut device).unwrap();
            let device = device.unwrap();
            let queue: ID3D12CommandQueue = device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC { Type: D3D12_COMMAND_LIST_TYPE_DIRECT, ..Default::default() }).unwrap();
            let allocator: ID3D12CommandAllocator = device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT).unwrap();
            let list: ID3D12GraphicsCommandList = device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None).unwrap();
            list.Close().unwrap();
            let unity = Unity { device: device.clone(), queue: queue.clone(), allocator, list, fence: device.CreateFence(0, D3D12_FENCE_FLAG_NONE).unwrap(), value: 0, event: CreateEventW(None, false, false, None).unwrap() };
            let q2 = queue.clone();
            let execute: Execute12 = Box::new(move |list, _| q2.ExecuteCommandLists(&[Some(list.cast().unwrap())]));
            let d3d11 = std::env::var("API").map(|v| v == "11").unwrap_or(false);
            let adapter_id = Adapter { luid: luid_bytes(device.GetAdapterLuid()), uuid: [0; 16] };
            let mut d11 = None;
            let mut device11_keep: Option<ID3D11Device> = None;
            let mut bridge = if d3d11 {
                let mut device11: Option<ID3D11Device> = None;
                let mut context11: Option<ID3D11DeviceContext> = None;
                D3D11CreateDevice(&adapter, D3D_DRIVER_TYPE_UNKNOWN, HMODULE::default(), D3D11_CREATE_DEVICE_FLAG(0), Some(&[D3D_FEATURE_LEVEL_11_1]), D3D11_SDK_VERSION, Some(&mut device11), None, Some(&mut context11)).unwrap();
                let d5: ID3D11Device5 = device11.as_ref().unwrap().cast().unwrap();
                let c4: ID3D11DeviceContext4 = context11.as_ref().unwrap().cast().unwrap();
                let tex11 = |w: u32, h: u32, format: DXGI_FORMAT, usage: D3D11_USAGE, bind: u32, cpu: u32| -> ID3D11Texture2D {
                    let desc = D3D11_TEXTURE2D_DESC { Width: w, Height: h, MipLevels: 1, ArraySize: 1, Format: format, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Usage: usage, BindFlags: bind, CPUAccessFlags: cpu, MiscFlags: 0 };
                    let mut t: Option<ID3D11Texture2D> = None;
                    device11.as_ref().unwrap().CreateTexture2D(&desc, None, Some(&mut t)).unwrap();
                    t.unwrap()
                };
                let bind = (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_UNORDERED_ACCESS.0) as u32;
                d11 = Some((context11.clone().unwrap(), tex11(w * 2, h * 2, DXGI_FORMAT_R16G16B16A16_UINT, D3D11_USAGE_DEFAULT, bind, 0), tex11(UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, DXGI_FORMAT_R16G16B16A16_UINT, D3D11_USAGE_DEFAULT, bind, 0), tex11(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_USAGE_DEFAULT, bind, 0), tex11(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_READ.0 as u32), tex11(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, D3D11_USAGE_DEFAULT, bind, 0)));
                println!("unity side: Direct3D 11");
                device11_keep = device11.clone();
                Bridge::new(Backend::D3D11 { device: d5, context: c4 }, adapter_id, API_D3D11).unwrap()
            } else {
                println!("unity side: Direct3D 12");
                Bridge::new(backend_d3d12(device.clone(), queue.clone(), execute).unwrap(), adapter_id, API_D3D12).unwrap()
            };
            let gbuffer = unity.texture(w * 2, h * 2, DXGI_FORMAT_R16G16B16A16_UINT);
            let upload = unity.texture(UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, DXGI_FORMAT_R16G16B16A16_UINT);
            let result = unity.texture(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
            let second = unity.texture(w, h, DXGI_FORMAT_R16G16B16A16_FLOAT);
            if let Some((_, g, u, r, _, s2)) = &d11 {
                bridge.unity_upload = u.as_raw();
                bridge.unity[0] = UnityTextures { gbuffer: g.as_raw(), result: r.as_raw(), gbuffer1: std::ptr::null_mut() };
                bridge.unity[1] = UnityTextures { gbuffer: g.as_raw(), result: s2.as_raw(), gbuffer1: std::ptr::null_mut() };
            } else {
                bridge.unity_upload = upload.as_raw();
                bridge.unity[0] = UnityTextures { gbuffer: gbuffer.as_raw(), result: result.as_raw(), gbuffer1: std::ptr::null_mut() };
                bridge.unity[1] = UnityTextures { gbuffer: gbuffer.as_raw(), result: second.as_raw(), gbuffer1: std::ptr::null_mut() };
            }
            let (gstage, gptr) = unity.buffer((w * 2 * 16) as u64 * h as u64, D3D12_HEAP_TYPE_UPLOAD);
            let (ustage, uptr) = unity.buffer(UPLOAD_WIDTH as u64 * 16 * UPLOAD_HEIGHT as u64, D3D12_HEAP_TYPE_UPLOAD);
            let (readback, rptr) = unity.buffer(rpitch as u64 * h as u64, D3D12_HEAP_TYPE_READBACK);
            (Client { unity, device11: if d3d11 { device11_keep } else { None }, direct: None, d11, gbuffer, upload, result, _second: second, gstage, ustage, readback, gptr, uptr, rptr, w, h, rpitch }, bridge)
        }
    }

    pub fn enable_direct(&mut self, bridge: &mut Bridge, width: u32, views: u32) -> bool {
        let (Some(device), true) = (self.device11.clone(), self.d11.is_some()) else { return false; };
        hook::install(&device);
        let (w, h) = (self.w, self.h);
        hook::arm(&[(w * 2, h * 2), (w * 2, h * 2), (w, h), (w, h)]);
        let bind = (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_UNORDERED_ACCESS.0) as u32;
        let make = |w: u32, h: u32, format: DXGI_FORMAT| -> ID3D11Texture2D {
            let desc = D3D11_TEXTURE2D_DESC { Width: w, Height: h, MipLevels: 1, ArraySize: 1, Format: format, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Usage: D3D11_USAGE_DEFAULT, BindFlags: bind, CPUAccessFlags: 0, MiscFlags: 0 };
            let mut t: Option<ID3D11Texture2D> = None;
            unsafe { device.CreateTexture2D(&desc, None, Some(&mut t)).unwrap(); }
            t.unwrap()
        };
        if std::env::var("RETARGETFLIP").is_ok() {
            let hh = (h * 3 / 4).max(8);
            hook::arm(&[(w * 2, hh * 2), (w * 2, hh * 2), (w, hh), (w, hh)]);
            let g = [make(w * 2, hh * 2, DXGI_FORMAT_R16G16B16A16_TYPELESS), make(w * 2, hh * 2, DXGI_FORMAT_R16G16B16A16_TYPELESS)];
            let r = [make(w, hh, DXGI_FORMAT_R16G16B16A16_TYPELESS), make(w, hh, DXGI_FORMAT_R16G16B16A16_TYPELESS)];
            let _ = bridge.set_targets(0, [g[0].as_raw(), g[1].as_raw(), r[0].as_raw(), r[1].as_raw()], width, hh, views);
            hook::arm(&[(w * 2, h * 2), (w * 2, h * 2), (w, h), (w, h)]);
        }
        let g = [make(w * 2, h * 2, DXGI_FORMAT_R16G16B16A16_TYPELESS), make(w * 2, h * 2, DXGI_FORMAT_R16G16B16A16_TYPELESS)];
        let r = [make(w, h, DXGI_FORMAT_R16G16B16A16_TYPELESS), make(w, h, DXGI_FORMAT_R16G16B16A16_TYPELESS)];
        hook::disarm();
        let ok = bridge.set_targets(0, [g[0].as_raw(), g[1].as_raw(), r[0].as_raw(), r[1].as_raw()], width, h, views).unwrap_or(false);
        if ok { self.direct = Some((g, r)); }
        ok
    }

    pub fn copy_bench(&self) {
        let (Some(device), Some((c, _, _, _, _, _))) = (self.device11.clone(), self.d11.as_ref()) else { return; };
        let (w, h) = (self.w, self.h);
        let bind = (D3D11_BIND_SHADER_RESOURCE.0 | D3D11_BIND_UNORDERED_ACCESS.0) as u32;
        let make = |w: u32, h: u32| -> ID3D11Texture2D {
            let desc = D3D11_TEXTURE2D_DESC { Width: w, Height: h, MipLevels: 1, ArraySize: 1, Format: DXGI_FORMAT_R16G16B16A16_UINT, SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 }, Usage: D3D11_USAGE_DEFAULT, BindFlags: bind, CPUAccessFlags: 0, MiscFlags: 0 };
            let mut t: Option<ID3D11Texture2D> = None;
            unsafe { device.CreateTexture2D(&desc, None, Some(&mut t)).unwrap(); }
            t.unwrap()
        };
        unsafe {
            let query = |kind: D3D11_QUERY| -> ID3D11Query { let mut q: Option<ID3D11Query> = None; device.CreateQuery(&D3D11_QUERY_DESC { Query: kind, MiscFlags: 0 }, Some(&mut q)).unwrap(); q.unwrap() };
            for (name, w, h) in [("gbuffer", w * 2, h * 2), ("output", w, h)] {
                let (a, b) = (make(w, h), make(w, h));
                let mut samples = Vec::new();
                for _ in 0..60 {
                    let (disjoint, t0, t1) = (query(D3D11_QUERY_TIMESTAMP_DISJOINT), query(D3D11_QUERY_TIMESTAMP), query(D3D11_QUERY_TIMESTAMP));
                    c.Begin(&disjoint);
                    c.End(&t0);
                    c.CopyResource(&b, &a);
                    c.End(&t1);
                    c.End(&disjoint);
                    let mut d = D3D11_QUERY_DATA_TIMESTAMP_DISJOINT::default();
                    while { let _ = c.GetData(&disjoint, Some(&mut d as *mut _ as _), std::mem::size_of_val(&d) as u32, 0); d.Frequency == 0 } {}
                    let (mut v0, mut v1) = (0u64, 0u64);
                    while { let _ = c.GetData(&t0, Some(&mut v0 as *mut _ as _), 8, 0); v0 == 0 } {}
                    while { let _ = c.GetData(&t1, Some(&mut v1 as *mut _ as _), 8, 0); v1 == 0 } {}
                    if d.Disjoint.as_bool() == false { samples.push((v1 - v0) as f64 * 1e6 / d.Frequency as f64); }
                }
                samples.sort_by(|a, b| a.partial_cmp(b).unwrap());
                println!("copy bench {name} {w}x{h}: gpu us p10 {:.1} median {:.1}", samples[samples.len() / 10], samples[samples.len() / 2]);
            }
        }
    }

    pub fn upload(&mut self, rows: u32, parity: usize) {
        let (w, h) = (self.w, self.h);
        unsafe {
            if let Some((c, g, u, _, _, _)) = &self.d11 {
                let g = self.direct.as_ref().map_or(g, |d| &d.0[parity & 1]);
                c.UpdateSubresource(g, 0, None, self.gptr as _, w * 2 * 8, 0);
                if rows > 0 { c.UpdateSubresource(u, 0, Some(&D3D11_BOX { left: 0, top: 0, front: 0, right: UPLOAD_WIDTH, bottom: rows * 2, back: 1 }), self.uptr as _, UPLOAD_WIDTH * 8, 0); }
            } else {
                let (gbuffer, gstage, upload, ustage) = (self.gbuffer.clone(), self.gstage.clone(), self.upload.clone(), self.ustage.clone());
                self.unity.run(|l| {
                    l.CopyTextureRegion(&subresource(&gbuffer), 0, 0, 0, &footprint(&gstage, w * 2, h * 2, DXGI_FORMAT_R16G16B16A16_UINT, w * 2 * 8), None);
                    if rows > 0 { l.CopyTextureRegion(&subresource(&upload), 0, 0, 0, &footprint(&ustage, UPLOAD_WIDTH, rows * 2, DXGI_FORMAT_R16G16B16A16_UINT, UPLOAD_WIDTH * 8), None); }
                });
            }
        }
    }

    /// Milliseconds until the D3D11 client's queue drains, or -1 if it is still busy after timeout_ms:
    /// a queue stuck on a fence nobody will signal, which in Unity ends in a TDR. -2 without a D3D11 client.
    pub fn wait_idle_ms(&self, timeout_ms: u64) -> f64 {
        unsafe {
            let (Some(device), Some((context, ..))) = (&self.device11, &self.d11) else { return -2.0; };
            let mut query: Option<ID3D11Query> = None;
            if device.CreateQuery(&D3D11_QUERY_DESC { Query: D3D11_QUERY_EVENT, MiscFlags: 0 }, Some(&mut query)).is_err() { return -2.0; }
            let query = query.unwrap();
            context.End(&query);
            context.Flush();
            let start = std::time::Instant::now();
            loop {
                let mut done = BOOL(0);
                let ok = context.GetData(&query, Some(&mut done as *mut BOOL as *mut core::ffi::c_void), std::mem::size_of::<BOOL>() as u32, 0).is_ok();
                if ok && done.as_bool() { return start.elapsed().as_secs_f64() * 1000.0; }
                if start.elapsed().as_millis() as u64 >= timeout_ms { return -1.0; }
                std::thread::sleep(std::time::Duration::from_millis(1));
            }
        }
    }

    pub fn read(&mut self, parity: usize) {
        let (w, h, rpitch, rptr) = (self.w, self.h, self.rpitch, self.rptr);
        unsafe {
            if let Some((c, _, _, r, staging, _)) = &self.d11 {
                let r = self.direct.as_ref().map_or(r, |d| &d.1[parity & 1]);
                c.CopyResource(staging, r);
                let mut mapped = D3D11_MAPPED_SUBRESOURCE::default();
                c.Map(staging, 0, D3D11_MAP_READ, 0, Some(&mut mapped)).unwrap();
                for y in 0..h { std::ptr::copy_nonoverlapping((mapped.pData as *const u8).add((y * mapped.RowPitch) as usize), rptr.add((y * rpitch) as usize), (w * 8) as usize); }
                c.Unmap(staging, 0);
            } else {
                let (readback, result) = (self.readback.clone(), self.result.clone());
                self.unity.run(|l| l.CopyTextureRegion(&footprint(&readback, w, h, DXGI_FORMAT_R16G16B16A16_FLOAT, rpitch), 0, 0, 0, &subresource(&result), None));
                self.unity.finish();
            }
        }
    }
}
