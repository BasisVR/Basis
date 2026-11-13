#![allow(non_snake_case)]
#[cfg(windows)]
pub mod hook;
pub mod host;
pub mod unity;
pub mod vulkan;

use ash::vk;
use basis_rt_protocol::*;
pub use host::{Host, Result};
use std::ffi::c_void;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};
use unity::*;
#[cfg(windows)]
use windows::Win32::Foundation::*;
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D11::*;
#[cfg(windows)]
use windows::Win32::Graphics::Direct3D12::*;
#[cfg(windows)]
use windows::Win32::Graphics::Dxgi::*;
#[cfg(windows)]
use windows::Win32::System::Threading::*;
#[cfg(windows)]
use windows::core::Interface;

pub const EVENT_SUBMIT: i32 = 0;
pub const EVENT_FETCH: i32 = 1;
pub const EVENT_ACQUIRE: i32 = 2;

#[cfg(windows)]
pub type Execute12 = Box<dyn Fn(&ID3D12GraphicsCommandList, &[(ID3D12Resource, D3D12_RESOURCE_STATES)]) + Send>;

#[cfg(windows)]
trait Message<T> { fn msg(self) -> Result<T>; }
#[cfg(windows)]
impl<T> Message<T> for windows::core::Result<T> { fn msg(self) -> Result<T> { self.map_err(|e| e.message()) } }

pub enum Backend {
    #[cfg(windows)]
    D3D11 { device: ID3D11Device5, context: ID3D11DeviceContext4 },
    #[cfg(windows)]
    D3D12 { device: ID3D12Device, queue: ID3D12CommandQueue, allocators: Vec<(ID3D12CommandAllocator, u64)>, list: ID3D12GraphicsCommandList, fence: ID3D12Fence, fence_value: u64, event: HANDLE, cursor: usize, execute: Execute12 },
    Vulkan(vulkan::Vulkan),
}
unsafe impl Send for Backend {}

#[derive(Clone)]
pub enum Res {
    #[cfg(windows)]
    D11(ID3D11Texture2D),
    #[cfg(windows)]
    D12(ID3D12Resource),
    Vk { image: vk::Image, layout: vk::ImageLayout, shared: bool, width: u32, height: u32 },
}

#[derive(Clone)]
pub enum Fence {
    #[cfg(windows)]
    D11(ID3D11Fence),
    #[cfg(windows)]
    D12(ID3D12Fence),
    Vk(vk::Semaphore),
}

pub struct Global { pub upload: [Res; 2], pub fence_unity: Fence, pub fence_tracer: Fence, imported: Vec<vulkan::Imported>, semaphores: Vec<vk::Semaphore> }

pub struct Shared { pub gbuffer: [Res; 2], pub output: [Res; 2], pub generation: u32, imported: Vec<vulkan::Imported> }

#[derive(Clone, Copy)]
pub struct UnityTextures { pub gbuffer: *mut c_void, pub result: *mut c_void, pub gbuffer1: *mut c_void }

#[derive(Clone, Copy, Default)]
pub struct Adapter { pub luid: [u8; 8], pub uuid: [u8; 16] }

pub struct Bridge {
    pub backend: Backend,
    pub host: Host,
    pub base: *mut u8,
    pub global: Option<Global>,
    pub shared: [Option<Shared>; MAX_CONTEXTS],
    pub unity: [UnityTextures; MAX_CONTEXTS],
    pub unity_upload: *mut c_void,
    pub used: [[u64; 2]; MAX_CONTEXTS],
    pub same_frame: bool,
    pub budget_us: u32,
    pub fetched: u64,
    pub fetch_failures: u64,
    pub resize_skips: u64,
    pub direct: [Option<Direct>; MAX_CONTEXTS],
    pub target_counter: u32,
    retired: Vec<(usize, Direct)>,
    pub blocked_waits: u64,
    // GPU waits skipped because the tracer frame had not finished within the budget. Unity's queue never
    // waits on unfinished tracer work: a wait past the TDR limit resets the device.
    pub unfinished_skips: u64,
}

pub struct Direct {
    #[cfg(windows)]
    handles: [HANDLE; 4],
}
unsafe impl Send for Bridge {}

pub static BRIDGE: Mutex<Option<Bridge>> = Mutex::new(None);
static INTERFACES: AtomicU64 = AtomicU64::new(0);
static EVENT_BASE: AtomicU64 = AtomicU64::new(0);
static LAST_ERROR: Mutex<String> = Mutex::new(String::new());
static FETCHED: AtomicU64 = AtomicU64::new(0);

pub fn set_error(message: impl Into<String>) { *LAST_ERROR.lock().unwrap() = message.into(); }

fn header<'a>(base: *mut u8) -> &'a mut Header { unsafe { &mut *(base as *mut Header) } }

#[cfg(windows)]
fn adapter_luid_d3d11(device: &ID3D11Device5) -> Result<LUID> {
    let dxgi: IDXGIDevice = device.cast().msg()?;
    unsafe { Ok(dxgi.GetAdapter().msg()?.GetDesc().msg()?.AdapterLuid) }
}

#[cfg(windows)]
pub fn luid_bytes(luid: LUID) -> [u8; 8] {
    let mut b = [0u8; 8];
    b[0..4].copy_from_slice(&luid.LowPart.to_le_bytes());
    b[4..8].copy_from_slice(&luid.HighPart.to_le_bytes());
    b
}

#[cfg(windows)]
fn handle(v: u64) -> HANDLE { HANDLE(v as *mut c_void) }

impl Bridge {
    pub fn new(backend: Backend, adapter: Adapter, api: u32) -> Result<Bridge> {
        let host = Host::new()?;
        let base = host.base;
        unsafe { std::ptr::write_bytes(base, 0, HEADER_BYTES); }
        let h = header(base);
        h.magic = MAGIC;
        h.version = VERSION;
        h.unity_pid = Host::pid();
        h.api = api;
        h.luid_low = u32::from_le_bytes(adapter.luid[0..4].try_into().unwrap());
        h.luid_high = i32::from_le_bytes(adapter.luid[4..8].try_into().unwrap());
        h.device_uuid = adapter.uuid;
        let none = UnityTextures { gbuffer: std::ptr::null_mut(), result: std::ptr::null_mut(), gbuffer1: std::ptr::null_mut() };
        Ok(Bridge { backend, host, base, global: None, shared: Default::default(), unity: [none; MAX_CONTEXTS], unity_upload: std::ptr::null_mut(), used: [[0; 2]; MAX_CONTEXTS], same_frame: true, budget_us: 50_000, fetched: 0, fetch_failures: 0, resize_skips: 0, direct: Default::default(), target_counter: 0, retired: Vec::new(), blocked_waits: 0, unfinished_skips: 0 })
    }

    pub fn launch(&mut self, exe: &str) -> Result<()> { self.launch_api(exe, if cfg!(windows) { TRACER_DXR } else { TRACER_VULKAN }) }

    pub fn launch_api(&mut self, exe: &str, api: u32) -> Result<()> { self.host.launch(exe, api == TRACER_VULKAN) }

    pub fn launch_mode(&mut self, exe: &str, api: u32, embedded: bool) -> Result<()> {
        #[cfg(windows)]
        if embedded && api == TRACER_DXR { return self.host.launch_embedded(exe); }
        let _ = embedded;
        self.launch_api(exe, api)
    }

    pub fn wake(&self) { self.host.wake(); }

    pub fn status(&self) -> u32 {
        let h = header(self.base);
        let s = h.status.load(Ordering::Acquire);
        if (s == STATUS_STARTING || s == STATUS_READY) && self.host.exited() { return STATUS_EXITED; }
        s
    }

    fn open_texture(&mut self, v: u64, width: u32, height: u32, format: i32, imported: &mut Vec<vulkan::Imported>) -> Result<Res> {
        let interop = header(self.base).interop;
        match &self.backend {
            #[cfg(windows)]
            Backend::D3D11 { device, .. } => unsafe { let r = device.OpenSharedResource1(handle(v)); Host::close_handle(handle(v)); Ok(Res::D11(r.map_err(|e| format!("open shared texture: {}", e.message()))?)) },
            #[cfg(windows)]
            Backend::D3D12 { device, .. } => unsafe { let mut r: Option<ID3D12Resource> = None; let opened = device.OpenSharedHandle(handle(v), &mut r); Host::close_handle(handle(v)); opened.msg()?; Ok(Res::D12(r.ok_or("open shared texture")?)) },
            Backend::Vulkan(vk) => {
                let h = self.host.take_handle(v).ok_or("the tracer's texture handle has not arrived")?;
                let i = vk.import_image(h, interop, width, height, format);
                #[cfg(windows)]
                Host::close_handle(h);
                let i = i?;
                let res = Res::Vk { image: i.image, layout: vk::ImageLayout::GENERAL, shared: true, width, height };
                imported.push(i);
                Ok(res)
            }
        }
    }

    fn open_fence(&mut self, v: u64, semaphores: &mut Vec<vk::Semaphore>) -> Result<Fence> {
        let interop = header(self.base).interop;
        match &self.backend {
            #[cfg(windows)]
            Backend::D3D11 { device, .. } => unsafe { let mut r: Option<ID3D11Fence> = None; let opened = device.OpenSharedFence(handle(v), &mut r); Host::close_handle(handle(v)); opened.map_err(|e| format!("open shared fence: {}", e.message()))?; Ok(Fence::D11(r.ok_or("open shared fence")?)) },
            #[cfg(windows)]
            Backend::D3D12 { device, .. } => unsafe { let mut r: Option<ID3D12Fence> = None; let opened = device.OpenSharedHandle(handle(v), &mut r); Host::close_handle(handle(v)); opened.msg()?; Ok(Fence::D12(r.ok_or("open shared fence")?)) },
            Backend::Vulkan(vk) => {
                let h = self.host.take_handle(v).ok_or("the tracer's fence handle has not arrived")?;
                let s = vk.import_semaphore(h, interop);
                #[cfg(windows)]
                Host::close_handle(h);
                let s = s?;
                semaphores.push(s);
                Ok(Fence::Vk(s))
            }
        }
    }

    fn idle(&self) { if let Backend::Vulkan(vk) = &self.backend { unsafe { let _ = vk.device.queue_wait_idle(vk.queue); } } }

    fn release_shared(&mut self, shared: Shared) {
        if shared.imported.is_empty() { return; }
        self.idle();
        if let Backend::Vulkan(vk) = &self.backend { for i in shared.imported { vk.release_image(i); } }
    }

    fn reopen(&mut self, context: usize) -> Result<bool> {
        let h = header(self.base);
        if self.global.is_none() {
            if h.generation.load(Ordering::Acquire) == 0 { return Ok(false); }
            let mut imported = Vec::new();
            let mut semaphores = Vec::new();
            let upload = [self.open_texture(h.handle_upload[0], UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, SHARED_FORMAT_UINT, &mut imported)?, self.open_texture(h.handle_upload[1], UPLOAD_WIDTH, UPLOAD_PHYSICAL_HEIGHT, SHARED_FORMAT_UINT, &mut imported)?];
            let fence_unity = self.open_fence(h.handle_fence_unity, &mut semaphores)?;
            let fence_tracer = self.open_fence(h.handle_fence_tracer, &mut semaphores)?;
            self.global = Some(Global { upload, fence_unity, fence_tracer, imported, semaphores });
        }
        let ch = &h.contexts[context];
        let generation = ch.generation.load(Ordering::Acquire);
        if generation == 0 { return Ok(false); }
        if self.direct[context].is_some() { return Ok(h.targets[context].opened.load(Ordering::Acquire) == h.targets[context].generation.load(Ordering::Acquire)); }
        if self.shared[context].as_ref().map_or(true, |s| s.generation != generation) {
            if let Some(old) = self.shared[context].take() { self.release_shared(old); }
            let full = ch.width * ch.views;
            let mut imported = Vec::new();
            let gbuffer = [self.open_texture(ch.handle_gbuffer[0], full * 2, ch.height * 2, SHARED_FORMAT_UINT, &mut imported)?, self.open_texture(ch.handle_gbuffer[1], full * 2, ch.height * 2, SHARED_FORMAT_UINT, &mut imported)?];
            let output = [self.open_texture(ch.handle_output[0], full, ch.height, SHARED_FORMAT_FLOAT, &mut imported)?, self.open_texture(ch.handle_output[1], full, ch.height, SHARED_FORMAT_FLOAT, &mut imported)?];
            self.shared[context] = Some(Shared { gbuffer, output, generation, imported });
            self.used[context] = [0; 2];
        }
        Ok(true)
    }

    fn completed_of(&self, fence: &Fence) -> u64 {
        unsafe {
            match (&self.backend, fence) {
                #[cfg(windows)]
                (_, Fence::D11(f)) => f.GetCompletedValue(),
                #[cfg(windows)]
                (_, Fence::D12(f)) => f.GetCompletedValue(),
                (Backend::Vulkan(vk), Fence::Vk(s)) => vk.device.get_semaphore_counter_value(*s).unwrap_or(0),
                _ => u64::MAX,
            }
        }
    }

    pub fn completed(&self) -> u64 { self.global.as_ref().map_or(0, |g| self.completed_of(&g.fence_tracer)) }

    fn reached(&self, fence: &Fence, value: u64, deadline: Instant) -> bool {
        loop {
            let done = self.completed_of(fence);
            if done >= value { return true; }
            if self.status() != STATUS_READY || Instant::now() >= deadline { return false; }
            std::thread::yield_now();
        }
    }

    #[cfg(windows)]
    fn wait(&mut self, fence: &Fence, value: u64) -> Result<()> {
        if self.completed_of(fence) < value { self.blocked_waits += 1; }
        unsafe {
            match (&self.backend, fence) {
                (Backend::D3D11 { context, .. }, Fence::D11(f)) => context.Wait(f, value).msg(),
                (Backend::D3D12 { queue, .. }, Fence::D12(f)) => queue.Wait(f, value).msg(),
                _ => Ok(()),
            }
        }
    }

    #[cfg(windows)]
    fn signal(&mut self, fence: &Fence, value: u64) -> Result<()> {
        unsafe {
            match (&self.backend, fence) {
                (Backend::D3D11 { context, .. }, Fence::D11(f)) => { context.Signal(f, value).msg()?; context.Flush(); Ok(()) }
                (Backend::D3D12 { queue, .. }, Fence::D12(f)) => queue.Signal(f, value).msg(),
                _ => Ok(()),
            }
        }
    }

    #[cfg(windows)]
    fn copy(&mut self, dst: &Res, src: &Res, rows: Option<u32>, states: &[(ID3D12Resource, D3D12_RESOURCE_STATES)]) -> Result<()> {
        unsafe {
            match (&self.backend, dst, src) {
                (Backend::D3D11 { context, .. }, Res::D11(d), Res::D11(s)) => {
                    match rows {
                        Some(r) => context.CopySubresourceRegion(d, 0, 0, 0, 0, s, 0, Some(&D3D11_BOX { left: 0, top: 0, front: 0, right: UPLOAD_WIDTH, bottom: r, back: 1 })),
                        None => context.CopyResource(d, s),
                    }
                    Ok(())
                }
                (Backend::D3D12 { .. }, Res::D12(d), Res::D12(s)) => {
                    let (d, s) = (d.clone(), s.clone());
                    self.record12(|list| match rows { Some(r) => copy_rows12(list, &d, &s, r), None => list.CopyResource(&d, &s) }, states)
                }
                _ => Ok(()),
            }
        }
    }

    fn extent(res: &Res) -> Option<(u32, u32)> {
        unsafe {
            match res {
                #[cfg(windows)]
                Res::D11(t) => { let mut d = D3D11_TEXTURE2D_DESC::default(); t.GetDesc(&mut d); Some((d.Width, d.Height)) }
                #[cfg(windows)]
                Res::D12(r) => { let d = r.GetDesc(); Some((d.Width as u32, d.Height)) }
                Res::Vk { shared: true, width, height, .. } => Some((*width, *height)),
                Res::Vk { .. } => None,
            }
        }
    }

    fn same_extent(a: &Res, b: &Res) -> bool { match (Self::extent(a), Self::extent(b)) { (Some(x), Some(y)) => x == y, _ => true } }

    fn unity_res(&self, pointer: *mut c_void) -> Option<Res> {
        if pointer.is_null() { return None; }
        match &self.backend {
            #[cfg(windows)]
            Backend::D3D11 { .. } => unsafe { ID3D11Texture2D::from_raw_borrowed(&pointer).map(|t| Res::D11(t.clone())) },
            #[cfg(windows)]
            Backend::D3D12 { .. } => unsafe { ID3D12Resource::from_raw_borrowed(&pointer).map(|t| Res::D12(t.clone())) },
            Backend::Vulkan(vk) => (vk.resolve)(pointer).map(|(image, layout)| Res::Vk { image, layout, shared: false, width: 0, height: 0 }),
        }
    }

    #[cfg(windows)]
    fn states(res: &Res, state: D3D12_RESOURCE_STATES) -> Vec<(ID3D12Resource, D3D12_RESOURCE_STATES)> { match res { Res::D12(r) => vec![(r.clone(), state)], _ => Vec::new() } }

    pub fn direct_available(&self) -> bool {
        #[cfg(windows)]
        if let Backend::D3D11 { device, .. } = &self.backend {
            if !self.host.embedded() || self.status() != STATUS_READY || header(self.base).caps & CAP_DIRECT == 0 { return false; }
            if let Ok(d) = device.cast::<ID3D11Device>() { hook::install(&d); }
            return hook::installed();
        }
        false
    }

    pub fn clear_targets(&mut self, context: usize) {
        if context >= MAX_CONTEXTS { return; }
        if let Some(d) = self.direct[context].take() {
            let t = &header(self.base).targets[context];
            t.sequence.fetch_add(1, Ordering::AcqRel);
            t.generation.store(0, Ordering::Release);
            t.sequence.fetch_add(1, Ordering::Release);
            self.retired.push((context, d));
            self.used[context] = [0; 2];
            self.host.wake();
        }
        self.reap(false);
    }

    fn reap(&mut self, all: bool) {
        let h = header(self.base);
        self.retired.retain(|(c, _d)| {
            let t = &h.targets[*c];
            if !all && t.opened.load(Ordering::Acquire) != t.generation.load(Ordering::Acquire) { return true; }
            #[cfg(windows)]
            for handle in _d.handles { Host::close_handle(handle); }
            false
        });
    }

    #[cfg(windows)]
    pub fn set_targets(&mut self, context: usize, targets: [*mut c_void; 4], width: u32, height: u32, views: u32) -> Result<bool> {
        if context >= MAX_CONTEXTS { return Ok(false); }
        if targets.iter().any(|t| t.is_null()) || !self.direct_available() { self.clear_targets(context); return Ok(false); }
        if let Some(old) = self.direct[context].take() { self.retired.push((context, old)); }
        let mut handles = Vec::new();
        for t in targets {
            let made = unsafe { ID3D11Texture2D::from_raw_borrowed(&t).and_then(|t| t.cast::<IDXGIResource1>().ok()).and_then(|r| r.CreateSharedHandle(None, DXGI_SHARED_RESOURCE_READ.0 | DXGI_SHARED_RESOURCE_WRITE.0, windows::core::PCWSTR::null()).ok()) };
            match made { Some(h) => handles.push(h), None => { for h in handles { Host::close_handle(h); } return Ok(false); } }
        }
        self.target_counter = self.target_counter.wrapping_add(1).max(1);
        let h = header(self.base);
        let ch = &h.contexts[context];
        ch.requested_width.store(width, Ordering::Release);
        ch.requested_height.store(height, Ordering::Release);
        ch.requested_views.store(views.clamp(1, 2), Ordering::Release);
        let t = &mut h.targets[context];
        t.sequence.fetch_add(1, Ordering::AcqRel);
        t.handles = [handles[0].0 as u64, handles[1].0 as u64, handles[2].0 as u64, handles[3].0 as u64];
        t.width = width;
        t.height = height;
        t.views = views.clamp(1, 2);
        t.opened.store(0, Ordering::Release);
        t.generation.store(self.target_counter, Ordering::Release);
        t.sequence.fetch_add(1, Ordering::Release);
        self.direct[context] = Some(Direct { handles: [handles[0], handles[1], handles[2], handles[3]] });
        self.used[context] = [0; 2];
        self.host.wake();
        Ok(true)
    }

    pub fn direct_ready(&self, context: usize) -> bool {
        let h = header(self.base);
        context < MAX_CONTEXTS && self.direct[context].is_some() && h.contexts[context].generation.load(Ordering::Acquire) > 0 && h.targets[context].opened.load(Ordering::Acquire) == h.targets[context].generation.load(Ordering::Acquire)
    }

    pub fn acquire(&mut self, context: usize, parity: usize) -> Result<()> {
        if !self.retired.is_empty() { self.reap(false); }
        if context >= MAX_CONTEXTS || self.direct[context].is_none() || !self.reopen(context)? { return Ok(()); }
        let previous = self.used[context][parity & 1];
        if previous == 0 { return Ok(()); }
        let h = header(self.base);
        let deadline = Instant::now() + Duration::from_micros(self.budget_us as u64);
        while h.frame_submitted.load(Ordering::Acquire) < previous && Instant::now() < deadline && self.status() == STATUS_READY { std::thread::yield_now(); }
        let wait_for = previous.min(h.frame_submitted.load(Ordering::Acquire));
        #[cfg(windows)]
        if wait_for > 0
        {
            let fence = self.global.as_ref().unwrap().fence_tracer.clone();
            // Still unfinished: Unity writes this target pair while the tracer may read it, one torn frame
            // at worst, rather than its queue stalling on the tracer.
            if !self.reached(&fence, wait_for, deadline) { self.unfinished_skips += 1; return Ok(()); }
            self.wait(&fence, wait_for)?;
        }
        let _ = wait_for;
        Ok(())
    }

    #[cfg(windows)]
    fn submit_direct(&mut self, frame: u64, context: usize, parity: usize, upload_rows: u32) -> Result<bool> {
        if !self.reopen(context)? { return Ok(false); }
        let h = header(self.base);
        let global = self.global.as_ref().unwrap();
        let (fence_unity, fence_tracer) = (global.fence_unity.clone(), global.fence_tracer.clone());
        let upload_dst = global.upload[(frame & 1) as usize].clone();
        let rows = upload_rows.min(UPLOAD_HEIGHT);
        let mut uploaded = true;
        if let Some(up) = if rows > 0 { self.unity_res(self.unity_upload) } else { None } {
            let wait_for = frame.saturating_sub(2).min(h.frame_submitted.load(Ordering::Acquire));
            if wait_for > 0 && !self.reached(&fence_tracer, wait_for, Instant::now() + Duration::from_micros(self.budget_us as u64)) {
                // The upload buffer is still being read: skip the copy and leave its mark stale, which the
                // tracer reports as a lost upload and Unity answers by sending the scene again.
                uploaded = false;
                self.unfinished_skips += 1;
            } else {
                if wait_for > 0 { self.wait(&fence_tracer, wait_for)?; }
                self.copy(&upload_dst, &up, Some(rows * 2), &[])?;
            }
        }
        self.signal(&fence_unity, frame)?;
        self.used[context][parity & 1] = frame;
        h.reserved[RESERVED_UPLOAD_TRACKED] = 1;
        if uploaded { h.reserved[RESERVED_UPLOAD_MARK + (frame & 1) as usize] = frame as u32; }
        h.frame_queued.store(frame, Ordering::Release);
        self.host.wake();
        Ok(true)
    }

    #[cfg(windows)]
    fn fetch_direct(&mut self, frame: u64) -> Result<u64> {
        if self.global.is_none() { return Ok(0); }
        let h = header(self.base);
        let deadline = Instant::now() + Duration::from_micros(self.budget_us as u64);
        while h.frame_submitted.load(Ordering::Acquire) < frame && Instant::now() < deadline && self.status() == STATUS_READY { std::thread::yield_now(); }
        if h.frame_submitted.load(Ordering::Acquire) < frame { self.fetch_failures += 1; return Ok(0); }
        let fence = self.global.as_ref().unwrap().fence_tracer.clone();
        if !self.reached(&fence, frame, deadline) { self.fetch_failures += 1; self.unfinished_skips += 1; return Ok(0); }
        self.wait(&fence, frame)?;
        self.fetched = frame;
        FETCHED.store(frame, Ordering::Release);
        Ok(frame)
    }

    pub fn submit(&mut self, frame: u64, context: usize, parity: usize, upload_rows: u32) -> Result<bool> {
        #[cfg(windows)]
        if context < MAX_CONTEXTS && self.direct[context].is_some() { return self.submit_direct(frame, context, parity, upload_rows); }
        if context >= MAX_CONTEXTS || !self.reopen(context)? { return Ok(false); }
        let source = if parity & 1 == 1 && !self.unity[context].gbuffer1.is_null() { self.unity[context].gbuffer1 } else { self.unity[context].gbuffer };
        let Some(gbuffer_src) = self.unity_res(source) else { return Ok(false); };
        let h = header(self.base);
        let global = self.global.as_ref().unwrap();
        let (fence_unity, fence_tracer) = (global.fence_unity.clone(), global.fence_tracer.clone());
        let upload_dst = global.upload[(frame & 1) as usize].clone();
        let gbuffer_dst = self.shared[context].as_ref().unwrap().gbuffer[parity & 1].clone();
        let submitted = h.frame_submitted.load(Ordering::Acquire);
        let previous = self.used[context][parity & 1];
        let upload_previous = frame.saturating_sub(2);
        let wait_for = previous.max(upload_previous).min(submitted);
        if wait_for > 0 && !self.reached(&fence_tracer, wait_for, Instant::now() + Duration::from_micros(self.budget_us as u64)) { return Ok(false); }
        let rows = upload_rows.min(UPLOAD_HEIGHT);
        let fits = Self::same_extent(&gbuffer_dst, &gbuffer_src);
        if !fits { self.resize_skips += 1; }
        let upload_src = if rows > 0 { self.unity_res(self.unity_upload) } else { None };
        if let Backend::Vulkan(vk) = &mut self.backend {
            let (Fence::Vk(unity_sem), Fence::Vk(tracer_sem)) = (&fence_unity, &fence_tracer) else { return Ok(false); };
            let family = vk.family;
            let wait = if wait_for > 0 { Some((*tracer_sem, wait_for)) } else { None };
            vk.run(wait, Some((*unity_sem, frame)), |device, cmd| {
                if let (Res::Vk { image: s, layout: sl, .. }, Res::Vk { image: d, width, height, .. }) = (&gbuffer_src, &gbuffer_dst) { vulkan::copy(device, cmd, family, (*s, *sl, false), (*d, vk::ImageLayout::GENERAL, true), *width, *height); }
                if let (Some(Res::Vk { image: s, layout: sl, .. }), Res::Vk { image: d, .. }) = (&upload_src, &upload_dst) { vulkan::copy(device, cmd, family, (*s, *sl, false), (*d, vk::ImageLayout::GENERAL, true), UPLOAD_WIDTH, rows * 2); }
            })?;
        } else {
            #[cfg(windows)]
            {
                if wait_for > 0 { self.wait(&fence_tracer, wait_for)?; }
                let states = Self::states(&gbuffer_src, D3D12_RESOURCE_STATE_COPY_SOURCE);
                if fits { self.copy(&gbuffer_dst, &gbuffer_src, None, &states)?; }
                if let Some(up) = &upload_src {
                    let states = Self::states(up, D3D12_RESOURCE_STATE_COPY_SOURCE);
                    self.copy(&upload_dst, up, Some(rows * 2), &states)?;
                }
                self.signal(&fence_unity, frame)?;
            }
        }
        self.used[context][parity & 1] = frame;
        h.reserved[RESERVED_UPLOAD_TRACKED] = 1;
        h.reserved[RESERVED_UPLOAD_MARK + (frame & 1) as usize] = frame as u32;
        h.frame_queued.store(frame, Ordering::Release);
        self.host.wake();
        Ok(true)
    }

    #[cfg(windows)]
    fn record12(&mut self, body: impl FnOnce(&ID3D12GraphicsCommandList), states: &[(ID3D12Resource, D3D12_RESOURCE_STATES)]) -> Result<()> {
        let Backend::D3D12 { queue, allocators, list, fence, fence_value, event, cursor, execute, .. } = &mut self.backend else { return Ok(()); };
        unsafe {
            let (allocator, used) = &mut allocators[*cursor];
            if fence.GetCompletedValue() < *used {
                fence.SetEventOnCompletion(*used, *event).msg()?;
                WaitForSingleObject(*event, 1000);
            }
            allocator.Reset().msg()?;
            list.Reset(&*allocator, None).msg()?;
            body(list);
            list.Close().msg()?;
            execute(list, states);
            *fence_value += 1;
            queue.Signal(&*fence, *fence_value).msg()?;
            *used = *fence_value;
            *cursor = (*cursor + 1) % allocators.len();
        }
        Ok(())
    }

    pub fn fetch(&mut self, frame: u64, context: usize, parity: usize) -> Result<u64> {
        #[cfg(windows)]
        if context < MAX_CONTEXTS && self.direct[context].is_some() { return self.fetch_direct(frame); }
        if context >= MAX_CONTEXTS || self.shared[context].is_none() { return Ok(0); }
        let Some(result) = self.unity_res(self.unity[context].result) else { return Ok(0); };
        let h = header(self.base);
        let deadline = Instant::now() + Duration::from_micros(self.budget_us as u64);
        while h.frame_submitted.load(Ordering::Acquire) < frame && Instant::now() < deadline {
            if self.status() != STATUS_READY { break; }
            std::thread::yield_now();
        }
        if h.frame_submitted.load(Ordering::Acquire) < frame { self.fetch_failures += 1; return Ok(0); }
        let fence_tracer = self.global.as_ref().unwrap().fence_tracer.clone();
        if !self.reached(&fence_tracer, frame, deadline) { self.fetch_failures += 1; return Ok(0); }
        let output = self.shared[context].as_ref().unwrap().output[parity & 1].clone();
        if !Self::same_extent(&result, &output) { self.resize_skips += 1; return Ok(0); }
        if let Backend::Vulkan(vk) = &mut self.backend {
            let Fence::Vk(tracer_sem) = fence_tracer else { return Ok(0); };
            let family = vk.family;
            vk.run(Some((tracer_sem, frame)), None, |device, cmd| {
                if let (Res::Vk { image: s, width, height, .. }, Res::Vk { image: d, layout: dl, .. }) = (&output, &result) { vulkan::copy(device, cmd, family, (*s, vk::ImageLayout::GENERAL, true), (*d, *dl, false), *width, *height); }
            })?;
        } else {
            #[cfg(windows)]
            {
                self.wait(&fence_tracer, frame)?;
                let states = Self::states(&result, D3D12_RESOURCE_STATE_COPY_DEST);
                self.copy(&result, &output, None, &states)?;
            }
        }
        self.fetched = frame;
        FETCHED.store(frame, Ordering::Release);
        Ok(frame)
    }

    pub fn stop(&mut self) { self.host.stop(); }
}

#[cfg(windows)]
unsafe fn copy_rows12(list: &ID3D12GraphicsCommandList, dst: &ID3D12Resource, src: &ID3D12Resource, rows: u32) {
    let d = D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(dst) }, Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { SubresourceIndex: 0 } };
    let s = D3D12_TEXTURE_COPY_LOCATION { pResource: unsafe { std::mem::transmute_copy(src) }, Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX, Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { SubresourceIndex: 0 } };
    let b = D3D12_BOX { left: 0, top: 0, front: 0, right: UPLOAD_WIDTH, bottom: rows, back: 1 };
    unsafe { list.CopyTextureRegion(&d, 0, 0, 0, &s, Some(&b)); }
}

impl Drop for Bridge {
    fn drop(&mut self) {
        self.host.stop();
        for c in 0..MAX_CONTEXTS { if let Some(d) = self.direct[c].take() { self.retired.push((c, d)); } }
        self.reap(true);
        if let Backend::Vulkan(_) = &self.backend {
            self.idle();
            let shared: Vec<Shared> = self.shared.iter_mut().filter_map(|s| s.take()).collect();
            let global = self.global.take();
            if let Backend::Vulkan(vk) = &self.backend {
                for s in shared { for i in s.imported { vk.release_image(i); } }
                if let Some(g) = global { for i in g.imported { vk.release_image(i); } for s in g.semaphores { vk.release_semaphore(s); } }
            }
        }
    }
}

#[cfg(windows)]
pub fn backend_d3d12(device: ID3D12Device, queue: ID3D12CommandQueue, execute: Execute12) -> Result<Backend> {
    unsafe {
        let allocators = (0..4).map(|_| device.CreateCommandAllocator::<ID3D12CommandAllocator>(D3D12_COMMAND_LIST_TYPE_DIRECT).map(|a| (a, 0u64))).collect::<windows::core::Result<Vec<_>>>().msg()?;
        let list: ID3D12GraphicsCommandList = device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocators[0].0, None).msg()?;
        list.Close().msg()?;
        let fence: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE).msg()?;
        let event = CreateEventW(None, false, false, None).msg()?;
        Ok(Backend::D3D12 { device, queue, allocators, list, fence, fence_value: 0, event, cursor: 0, execute })
    }
}

pub fn backend_vulkan(instance: ash::Instance, physical: vk::PhysicalDevice, device: ash::Device, queue: vk::Queue, family: u32, resolve: vulkan::Resolve) -> Result<(Backend, Adapter)> {
    let v = unsafe { vulkan::Vulkan::new(instance, physical, device, queue, family, resolve)? };
    let (uuid, luid) = v.identity();
    Ok((Backend::Vulkan(v), Adapter { uuid, luid: luid.unwrap_or([0; 8]) }))
}

fn create_from_unity() -> Result<Bridge> {
    let interfaces = INTERFACES.load(Ordering::Acquire) as *const UnityInterfaces;
    if interfaces.is_null() { return Err("the plugin was not loaded by Unity".into()); }
    unsafe {
        let graphics = ((*interfaces).get_interface_split)(GUID_GRAPHICS.0, GUID_GRAPHICS.1) as *const UnityGraphics;
        let renderer = ((*graphics).get_renderer)();
        let base = EVENT_BASE.load(Ordering::Acquire) as i32;
        match renderer {
            #[cfg(windows)]
            RENDERER_D3D11 => {
                #[repr(C)]
                struct UnityGraphicsD3D11 { get_device: unsafe extern "system" fn() -> *mut c_void }
                let d = ((*interfaces).get_interface_split)(GUID_D3D11.0, GUID_D3D11.1) as *const UnityGraphicsD3D11;
                let raw = ((*d).get_device)();
                let device: ID3D11Device = ID3D11Device::from_raw_borrowed(&raw).ok_or("no Direct3D 11 device")?.clone();
                let device: ID3D11Device5 = device.cast().map_err(|_| "Direct3D 11.4 shared fences are not available".to_string())?;
                let context: ID3D11DeviceContext4 = device.GetImmediateContext().msg()?.cast().msg()?;
                let luid = adapter_luid_d3d11(&device)?;
                Bridge::new(Backend::D3D11 { device, context }, Adapter { luid: luid_bytes(luid), uuid: [0; 16] }, API_D3D11)
            }
            #[cfg(windows)]
            RENDERER_D3D12 => {
                #[repr(C)]
                struct UnityResourceState { resource: *mut c_void, expected: i32, current: i32 }
                #[repr(C)]
                struct UnityEventConfig { queue_access: i32, flags: u32, ensure_bound: bool }
                #[repr(C)]
                struct UnityGraphicsD3D12v7 {
                    get_device: unsafe extern "system" fn() -> *mut c_void,
                    get_swap_chain: unsafe extern "system" fn() -> *mut c_void,
                    get_sync_interval: unsafe extern "system" fn() -> u32,
                    get_present_flags: unsafe extern "system" fn() -> u32,
                    get_frame_fence: unsafe extern "system" fn() -> *mut c_void,
                    get_next_frame_fence_value: unsafe extern "system" fn() -> u64,
                    execute_command_list: unsafe extern "system" fn(*mut c_void, i32, *mut UnityResourceState) -> u64,
                    set_physical_video_memory_control_values: unsafe extern "system" fn(*const c_void),
                    get_command_queue: unsafe extern "system" fn() -> *mut c_void,
                    texture_from_render_buffer: unsafe extern "system" fn(*mut c_void) -> *mut c_void,
                    texture_from_native_texture: unsafe extern "system" fn(*mut c_void) -> *mut c_void,
                    configure_event: unsafe extern "system" fn(i32, *const UnityEventConfig),
                    command_recording_state: unsafe extern "system" fn(*mut c_void) -> bool,
                }
                let d = ((*interfaces).get_interface_split)(GUID_D3D12V7.0, GUID_D3D12V7.1) as *const UnityGraphicsD3D12v7;
                if d.is_null() { return Err("IUnityGraphicsD3D12v7 is not available".into()); }
                let raw = ((*d).get_device)();
                let device: ID3D12Device = ID3D12Device::from_raw_borrowed(&raw).ok_or("no Direct3D 12 device")?.clone();
                let rawq = ((*d).get_command_queue)();
                let queue: ID3D12CommandQueue = ID3D12CommandQueue::from_raw_borrowed(&rawq).ok_or("no Direct3D 12 queue")?.clone();
                let luid = device.GetAdapterLuid();
                let table = d as usize;
                let execute: Execute12 = Box::new(move |list, states| {
                    let d = table as *const UnityGraphicsD3D12v7;
                    let mut s: Vec<UnityResourceState> = states.iter().map(|(r, st)| UnityResourceState { resource: r.as_raw(), expected: st.0, current: st.0 }).collect();
                    ((*d).execute_command_list)(list.as_raw(), s.len() as i32, s.as_mut_ptr());
                });
                let config = UnityEventConfig { queue_access: 1, flags: 2 | 4, ensure_bound: false };
                ((*d).configure_event)(base + EVENT_SUBMIT, &config);
                ((*d).configure_event)(base + EVENT_FETCH, &config);
                ((*d).configure_event)(base + EVENT_ACQUIRE, &config);
                Bridge::new(backend_d3d12(device, queue, execute)?, Adapter { luid: luid_bytes(luid), uuid: [0; 16] }, API_D3D12)
            }
            RENDERER_VULKAN => {
                let v = ((*interfaces).get_interface_split)(GUID_VULKAN.0, GUID_VULKAN.1) as *const UnityGraphicsVulkan;
                if v.is_null() { return Err("IUnityGraphicsVulkan is not available".into()); }
                if !HOOKED.load(Ordering::Acquire) { return Err("the bridge plugin was not loaded before Unity created its Vulkan device, so external memory is not enabled (mark BasisRayTracingBridge2 as preloaded)".into()); }
                let inst = ((*v).instance)();
                let static_fn = ash::StaticFn { get_instance_proc_addr: inst.get_instance_proc_addr };
                let instance = ash::Instance::load(&static_fn, inst.instance);
                let device = ash::Device::load(instance.fp_v1_0(), inst.device);
                let table = v as usize;
                let resolve: vulkan::Resolve = Box::new(move |pointer| {
                    let v = table as *const UnityGraphicsVulkan;
                    let mut image: UnityVulkanImage = std::mem::zeroed();
                    if ((*v).access_texture)(pointer, std::ptr::null(), vk::ImageLayout::UNDEFINED, 0, 0, 0, &mut image) { Some((image.image, image.layout)) } else { None }
                });
                let config = UnityVulkanPluginEventConfig { render_pass_precondition: 2, graphics_queue_access: 1, flags: 2 | 4 };
                ((*v).configure_event)(base + EVENT_SUBMIT, &config);
                ((*v).configure_event)(base + EVENT_FETCH, &config);
                ((*v).configure_event)(base + EVENT_ACQUIRE, &config);
                let (backend, adapter) = backend_vulkan(instance, inst.physical, device, inst.queue, inst.family, resolve)?;
                Bridge::new(backend, adapter, API_VULKAN)
            }
            _ => Err(if cfg!(windows) { "the external ray tracer needs Direct3D 11, Direct3D 12 or Vulkan".into() } else { "the external ray tracer needs Unity to render with Vulkan".into() }),
        }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "system" fn UnityPluginLoad(interfaces: *const UnityInterfaces) {
    INTERFACES.store(interfaces as u64, Ordering::Release);
    unsafe {
        let graphics = ((*interfaces).get_interface_split)(GUID_GRAPHICS.0, GUID_GRAPHICS.1) as *const UnityGraphics;
        if !graphics.is_null() { EVENT_BASE.store(((*graphics).reserve_event_id_range)(3) as u64, Ordering::Release); }
        install_vulkan_hook(interfaces);
    }
}

#[unsafe(no_mangle)]
pub extern "system" fn UnityPluginUnload() { *BRIDGE.lock().unwrap() = None; }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_EventBase() -> i32 { EVENT_BASE.load(Ordering::Acquire) as i32 }

unsafe fn wide(text: *const u16) -> String {
    if text.is_null() { return String::new(); }
    let mut n = 0;
    unsafe { while *text.add(n) != 0 { n += 1; } }
    String::from_utf16_lossy(unsafe { std::slice::from_raw_parts(text, n) })
}

fn start(path: String, api: u32, embedded: bool) -> i32 {
    let mut guard = BRIDGE.lock().unwrap();
    *guard = None;
    match create_from_unity().and_then(|mut b| { b.launch_mode(&path, api, embedded)?; Ok(b) }) {
        Ok(b) => { *guard = Some(b); set_error(""); 1 }
        Err(e) => { set_error(e); 0 }
    }
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn BasisRT_Start(exe: *const u16) -> i32 { start(unsafe { wide(exe) }, if cfg!(windows) { TRACER_DXR } else { TRACER_VULKAN }, false) }

#[unsafe(no_mangle)]
pub unsafe extern "C" fn BasisRT_StartApi(exe: *const u16, api: i32) -> i32 {
    let api = if api as u32 == TRACER_VULKAN || cfg!(not(windows)) { TRACER_VULKAN } else { TRACER_DXR };
    start(unsafe { wide(exe) }, api, false)
}

#[unsafe(no_mangle)]
pub unsafe extern "C" fn BasisRT_StartMode(exe: *const u16, api: i32, embedded: i32) -> i32 {
    let api = if api as u32 == TRACER_VULKAN || cfg!(not(windows)) { TRACER_VULKAN } else { TRACER_DXR };
    start(unsafe { wide(exe) }, api, embedded != 0)
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Embedded() -> i32 { BRIDGE.lock().unwrap().as_ref().map_or(0, |b| b.host.embedded() as i32) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Stop() { *BRIDGE.lock().unwrap() = None; }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Shared() -> *mut u8 { BRIDGE.lock().unwrap().as_ref().map_or(std::ptr::null_mut(), |b| b.base) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Status() -> i32 { BRIDGE.lock().unwrap().as_ref().map_or(-1, |b| b.status() as i32) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Fetched() -> u64 { FETCHED.load(Ordering::Acquire) }

#[unsafe(no_mangle)]
pub unsafe extern "C" fn BasisRT_Error(buffer: *mut u16, length: i32) -> i32 {
    let mut message = LAST_ERROR.lock().unwrap().clone();
    if let Some(b) = BRIDGE.lock().unwrap().as_ref() {
        let h = header(b.base);
        let end = h.error.iter().position(|c| *c == 0).unwrap_or(0);
        if end > 0 { message = String::from_utf8_lossy(&h.error[..end]).into_owned(); }
    }
    let units: Vec<u16> = message.encode_utf16().collect();
    let n = units.len().min((length.max(1) - 1) as usize);
    unsafe {
        std::ptr::copy_nonoverlapping(units.as_ptr(), buffer, n);
        *buffer.add(n) = 0;
    }
    n as i32
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_SetUpload(upload: *mut c_void) { if let Some(b) = BRIDGE.lock().unwrap().as_mut() { b.unity_upload = upload; } }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Completed() -> u64 { BRIDGE.lock().unwrap().as_ref().map_or(0, |b| b.completed()) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_DirectAvailable() -> i32 { BRIDGE.lock().unwrap().as_ref().map_or(0, |b| b.direct_available() as i32) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_ArmTexture(width: u32, height: u32) {
    #[cfg(windows)]
    { hook::disarm(); hook::arm(&[(width, height)]); }
    let _ = (width, height);
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_DisarmTargets() {
    #[cfg(windows)]
    hook::disarm();
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_SetContextTargets(context: i32, gbuffer0: *mut c_void, gbuffer1: *mut c_void, result0: *mut c_void, result1: *mut c_void, width: u32, height: u32, views: u32) -> i32 {
    let mut guard = BRIDGE.lock().unwrap();
    let Some(b) = guard.as_mut() else { return 0; };
    if !(0..MAX_CONTEXTS as i32).contains(&context) { return 0; }
    #[cfg(windows)]
    return match b.set_targets(context as usize, [gbuffer0, gbuffer1, result0, result1], width, height, views) { Ok(ok) => ok as i32, Err(e) => { set_error(e); 0 } };
    #[cfg(not(windows))]
    { let _ = (gbuffer0, gbuffer1, result0, result1, width, height, views); b.clear_targets(context as usize); 0 }
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_SetContextTextures(context: i32, gbuffer: *mut c_void, result: *mut c_void) {
    if let Some(b) = BRIDGE.lock().unwrap().as_mut() {
        if (0..MAX_CONTEXTS as i32).contains(&context) { b.unity[context as usize] = UnityTextures { gbuffer, result, gbuffer1: std::ptr::null_mut() }; }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_SetContextGBuffer1(context: i32, gbuffer1: *mut c_void) {
    if let Some(b) = BRIDGE.lock().unwrap().as_mut() {
        if (0..MAX_CONTEXTS as i32).contains(&context) { b.unity[context as usize].gbuffer1 = gbuffer1; }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Configure(same_frame: i32, budget_us: i32) {
    if let Some(b) = BRIDGE.lock().unwrap().as_mut() {
        b.same_frame = same_frame != 0;
        b.budget_us = budget_us.max(0) as u32;
    }
}

unsafe extern "system" fn on_render_event(event: i32, data: *mut c_void) {
    let base = EVENT_BASE.load(Ordering::Acquire) as i32;
    let value = data as u64;
    let frame = value & 0xff_ffff_ffff;
    let context = ((value >> 40) & 0xf) as usize;
    let parity = ((value >> 44) & 1) as usize;
    let rows = (value >> 48) as u32;
    let mut guard = BRIDGE.lock().unwrap();
    let Some(b) = guard.as_mut() else { return; };
    let result = match event - base {
        EVENT_SUBMIT => b.submit(frame, context, parity, rows).map(|_| ()),
        EVENT_FETCH => b.fetch(frame, context, parity).map(|_| ()),
        EVENT_ACQUIRE => b.acquire(context, parity),
        _ => Ok(()),
    };
    if let Err(e) = result { set_error(format!("render event {}: {e}", event - base)); }
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_RenderEventFunc() -> *const c_void { on_render_event as *const c_void }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Layout(index: i32) -> i64 {
    let slot = std::mem::offset_of!(Slot, instances);
    match index {
        0 => HEADER_BYTES as i64,
        1 => SLOT_BYTES as i64,
        2 => slot as i64,
        3 => std::mem::offset_of!(Slot, materials) as i64,
        4 => std::mem::offset_of!(Slot, lights) as i64,
        5 => std::mem::offset_of!(Slot, commands) as i64,
        6 => std::mem::offset_of!(Slot, emitters) as i64,
        7 => std::mem::size_of::<FrameParams>() as i64,
        8 => VERSION as i64,
        9 => std::mem::offset_of!(FrameParams, context) as i64,
        10 => std::mem::offset_of!(Slot, dirty) as i64,
        _ => -1,
    }
}

fn with_header<T>(default: T, f: impl FnOnce(&Header, &Bridge) -> T) -> T { BRIDGE.lock().unwrap().as_ref().map_or(default, |b| f(header(b.base), b)) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_SetSize(context: i32, width: u32, height: u32, views: u32) {
    if !(0..MAX_CONTEXTS as i32).contains(&context) { return; }
    with_header((), |h, _| { let c = &h.contexts[context as usize]; c.requested_width.store(width, Ordering::Release); c.requested_height.store(height, Ordering::Release); c.requested_views.store(views.clamp(1, 2), Ordering::Release); });
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Request(frame: u64) { with_header((), |h, b| { h.frame_requested.store(frame, Ordering::Release); b.host.wake(); }); }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Submitted() -> u64 { with_header(0, |h, _| h.frame_submitted.load(Ordering::Acquire)) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Consumed() -> u64 { with_header(0, |h, _| h.frame_consumed.load(Ordering::Acquire)) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Ready(context: i32) -> i32 {
    if !(0..MAX_CONTEXTS as i32).contains(&context) { return 0; }
    with_header(0, |h, b| { let c = &h.contexts[context as usize]; (b.status() == STATUS_READY && (b.direct[context as usize].is_none() || b.direct_ready(context as usize)) && h.generation.load(Ordering::Acquire) > 0 && c.generation.load(Ordering::Acquire) > 0 && c.width == c.requested_width.load(Ordering::Acquire) && c.height == c.requested_height.load(Ordering::Acquire) && c.views == c.requested_views.load(Ordering::Acquire)) as i32 })
}

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Caps() -> u32 { with_header(0, |h, _| h.caps) }

#[unsafe(no_mangle)]
pub extern "C" fn BasisRT_Stat(index: i32) -> u32 {
    with_header(0, |h, b| match index {
        0 => h.gpu_time_us.load(Ordering::Relaxed),
        1 => h.build_time_us.load(Ordering::Relaxed),
        2 => h.mesh_count.load(Ordering::Relaxed),
        3 => h.texture_count.load(Ordering::Relaxed),
        4 => b.fetch_failures as u32,
        9 => b.resize_skips as u32,
        18 => h.reserved[RESERVED_UPLOAD_LOST],
        19 => b.blocked_waits as u32,
        20 => b.unfinished_skips as u32,
        5 => h.generation.load(Ordering::Relaxed),
        6 => MAX_CONTEXTS as u32,
        7 => h.tracer_api,
        8 => h.interop,
        10..=17 => h.reserved[(index - 10) as usize],
        _ => 0,
    })
}
