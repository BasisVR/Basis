#[cfg(windows)]
pub mod d3d;
#[cfg(windows)]
mod dx;
#[cfg(windows)]
mod ffi;
#[cfg(windows)]
mod gpu;
mod lightgrid;
pub mod os;
#[cfg(windows)]
mod relay;
#[cfg(windows)]
mod render;
#[cfg(windows)]
mod scene;
mod vk;

use basis_rt_protocol::*;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

pub const AGILITY_SDK_VERSION: u32 = 620;

pub type Fallible<T> = std::result::Result<T, String>;

pub trait Backend {
    fn caps(&self) -> u32;
    fn api(&self) -> u32;
    fn interop(&self) -> u32;
    fn global_handles(&self) -> [u64; 4];
    fn context_size(&self, context: usize) -> Option<(u32, u32, u32)>;
    fn release_context(&mut self, context: usize) -> Fallible<()>;
    fn create_context(&mut self, link: &os::Link, context: usize, width: u32, height: u32, views: u32, targets: Option<[u64; 4]>) -> Fallible<[u64; 4]>;
    fn record(&mut self, slot: *mut Slot) -> Fallible<()>;
    fn timings(&self) -> [u32; 8];
    fn grid_us(&self) -> (u32, u32);
    fn prepare(&mut self, slot: *mut Slot) -> Fallible<()>;
    fn counts(&self) -> (u32, u32);
    fn idle(&mut self) {}
}

#[derive(Default)]
pub struct Stop { pub flag: std::sync::atomic::AtomicBool, #[cfg(windows)] fences: std::sync::Mutex<Vec<windows::Win32::Graphics::Direct3D12::ID3D12Fence>> }

impl Stop {
    pub fn stopping(&self) -> bool { self.flag.load(Ordering::Acquire) }
    #[cfg(windows)]
    pub fn register(&self, fence: &windows::Win32::Graphics::Direct3D12::ID3D12Fence) { self.fences.lock().unwrap().push(fence.clone()); }
    pub fn trigger(&self) {
        self.flag.store(true, Ordering::Release);
        #[cfg(windows)]
        for f in self.fences.lock().unwrap().iter() { unsafe { let _ = f.Signal(u64::MAX); } }
    }
}

fn run(link: &mut os::Link, args: &os::Args) -> Fallible<()> {
    let header = link.header();
    if header.magic != MAGIC || header.version != VERSION { return Err("protocol mismatch between Unity and the tracer".into()); }
    header.tracer_pid.store(std::process::id(), Ordering::Release);
    link.connect(args)?;
    let mut backend: Box<dyn Backend> = if args.vulkan { Box::new(vk::Tracer::new(link)?) } else { new_dx(link)? };
    let result = serve(link, &mut *backend);
    backend.idle();
    result
}

fn serve(link: &mut os::Link, backend: &mut dyn Backend) -> Fallible<()> {
    let header = link.header();
    header.caps = backend.caps();
    header.tracer_api = backend.api();
    header.interop = backend.interop();
    let g = backend.global_handles();
    header.handle_upload = [g[0], g[1]];
    header.handle_fence_unity = g[2];
    header.handle_fence_tracer = g[3];
    header.generation.store(1, Ordering::Release);
    let mut last = header.frame_submitted.load(Ordering::Acquire);
    let mut opened = [0u32; MAX_CONTEXTS];
    header.status.store(STATUS_READY, Ordering::Release);

    loop {
        header.heartbeat.fetch_add(1, Ordering::Relaxed);
        if !link.parent_alive() { return Ok(()); }
        for c in 0..MAX_CONTEXTS {
            let ch = &header.contexts[c];
            let t = &header.targets[c];
            let target = t.generation.load(Ordering::Acquire);
            let (w, h, v) = if target != 0 { (t.width, t.height, t.views.clamp(1, 2)) } else { (ch.requested_width.load(Ordering::Acquire), ch.requested_height.load(Ordering::Acquire), ch.requested_views.load(Ordering::Acquire).clamp(1, 2)) };
            let current = backend.context_size(c);
            if w == 0 || h == 0 {
                if current.is_some() { backend.release_context(c)?; }
                opened[c] = 0;
                header.targets[c].opened.store(target, Ordering::Release);
                continue;
            }
            if current == Some((w, h, v)) && opened[c] == target { continue; }
            backend.release_context(c)?;
            let handles = backend.create_context(link, c, w, h, v, if target != 0 { Some(t.handles) } else { None }).map_err(|e| format!("allocate context {c}: {e}"))?;
            opened[c] = target;
            header.targets[c].opened.store(target, Ordering::Release);
            let ch = &mut header.contexts[c];
            ch.handle_gbuffer = [handles[0], handles[1]];
            ch.handle_output = [handles[2], handles[3]];
            ch.width = w;
            ch.height = h;
            ch.views = v;
            ch.generation.fetch_add(1, Ordering::AcqRel);
        }
        let requested = header.frame_requested.load(Ordering::Acquire);
        if requested <= last { link.wait_wake(2); continue; }
        let frame = last + 1;
        let slot = unsafe { slot(link.base, frame as usize) };
        if unsafe { (*slot).params.frame } != frame { last = frame; header.frame_consumed.store(frame, Ordering::Release); continue; }
        let context = unsafe { (*slot).params.context } as usize;
        if context >= MAX_CONTEXTS || backend.context_size(context).is_none() { link.wait_wake(1); continue; }
        backend.prepare(slot).map_err(|e| format!("frame {frame}: {e}"))?;
        let deadline = Instant::now() + Duration::from_millis(250);
        while header.frame_queued.load(Ordering::Acquire) < frame {
            if Instant::now() > deadline { break; }
            link.wait_wake(1);
        }
        if header.frame_queued.load(Ordering::Acquire) < frame { continue; }
        if header.reserved[RESERVED_UPLOAD_TRACKED] != 0 && header.reserved[RESERVED_UPLOAD_MARK + (frame & 1) as usize] != frame as u32 && unsafe { (*slot).params.command_count } != 0 {
            unsafe { let p = &mut (*(slot as *mut Slot)).params; p.command_count = 0; p.upload_texels = 0; }
            header.reserved[RESERVED_UPLOAD_LOST] = header.reserved[RESERVED_UPLOAD_LOST].wrapping_add(1);
        }
        let started = Instant::now();
        backend.record(slot).map_err(|e| format!("frame {frame}: {e}"))?;
        let timings = backend.timings();
        header.build_time_us.store(started.elapsed().as_micros() as u32, Ordering::Relaxed);
        header.gpu_time_us.store(timings[0], Ordering::Relaxed);
        for (i, t) in timings.iter().enumerate() { header.reserved[i] = *t; }
        let (grid_build, grid_wait) = backend.grid_us();
        header.reserved[8] = grid_build;
        header.reserved[9] = grid_wait;
        header.frame_consumed.store(frame, Ordering::Release);
        header.frame_submitted.store(frame, Ordering::Release);
        let (meshes, textures) = backend.counts();
        header.mesh_count.store(meshes, Ordering::Relaxed);
        header.texture_count.store(textures, Ordering::Relaxed);
        last = frame;
    }
}

#[cfg(windows)]
fn new_dx(link: &os::Link) -> Fallible<Box<dyn Backend>> { Ok(Box::new(dx::Tracer::new(link)?)) }

#[cfg(not(windows))]
fn new_dx(_link: &os::Link) -> Fallible<Box<dyn Backend>> { Err("DirectX ray tracing is only available on Windows".into()) }

#[cfg(windows)]
fn list_adapters(path: &str) {
    let mut text = String::new();
    for a in gpu::adapters() { text.push_str(&format!("{:08x}:{:08x}\t{}\t{}\t{}\n", a.high as u32, a.low, a.usable as u32, a.memory_mb, a.name)); }
    let _ = std::fs::write(path, text);
}

#[cfg(not(windows))]
fn list_adapters(path: &str) { let _ = std::fs::write(path, ""); }


pub fn main_process() {
    let args = os::Args::parse();
    if let Some(path) = &args.list_adapters { list_adapters(path); return; }
    if args.parent == 0 { std::process::exit(2); }
    let mut link = match os::Link::open_mapping(&args) { Ok(l) => l, Err(_) => std::process::exit(3) };
    let base = link.base as usize;
    std::panic::set_hook(Box::new(move |info| {
        let header = unsafe { &*(base as *const Header) };
        write_error(header, &format!("tracer panicked: {info}"));
        header.status.store(STATUS_FAILED, Ordering::Release);
    }));
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(&mut link, &args)));
    let message = match result { Ok(Ok(())) => None, Ok(Err(e)) => Some(e), Err(_) => Some("tracer panicked".to_string()) };
    let header = link.header();
    if let Some(m) = &message { write_error(header, m); }
    header.status.store(if message.is_some() { STATUS_FAILED } else { STATUS_EXITED }, Ordering::Release);
    std::process::exit(if message.is_some() { 1 } else { 0 });
}

#[cfg(windows)]
pub struct Embedded { pub base: *mut u8, pub wake: *mut std::ffi::c_void, pub sdk: String, pub stop: std::sync::Arc<Stop> }
#[cfg(windows)]
unsafe impl Send for Embedded {}

#[cfg(windows)]
pub fn run_embedded(e: Embedded) {
    use windows::Win32::System::Threading::*;
    unsafe { let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_ABOVE_NORMAL); }
    let mut link = os::Link::embedded(e.base, e.wake, e.stop.clone());
    let args = os::Args { parent: std::process::id(), vulkan: false, list_adapters: None };
    let agility = d3d::begin_embedded(&e.sdk);
    let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| run(&mut link, &args)));
    d3d::end_embedded();
    let message = match result {
        Ok(Ok(())) => None,
        Ok(Err(m)) => Some(match &agility { Err(a) => format!("{m} (Agility SDK: {a})"), Ok(()) => m }),
        Err(p) => Some(format!("tracer panicked: {}", p.downcast_ref::<&str>().map(|s| s.to_string()).or_else(|| p.downcast_ref::<String>().cloned()).unwrap_or_default())),
    };
    let header = link.header();
    if let Some(m) = &message { write_error(header, m); }
    header.status.store(if message.is_some() { STATUS_FAILED } else { STATUS_EXITED }, Ordering::Release);
}
