#![windows_subsystem = "windows"]
#[cfg(windows)]
mod dx;
#[cfg(windows)]
mod ffi;
#[cfg(windows)]
mod gpu;
mod os;
#[cfg(windows)]
mod render;
#[cfg(windows)]
mod scene;
mod vk;

use basis_rt_protocol::*;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

#[cfg(windows)]
#[repr(transparent)]
pub struct SdkPath(*const u8);
#[cfg(windows)]
unsafe impl Sync for SdkPath {}

#[cfg(windows)]
#[unsafe(no_mangle)]
pub static D3D12SDKVersion: u32 = 620;
#[cfg(windows)]
#[unsafe(no_mangle)]
pub static D3D12SDKPath: SdkPath = SdkPath(b".\\D3D12\\\0".as_ptr());

pub type Fallible<T> = std::result::Result<T, String>;

pub trait Backend {
    fn caps(&self) -> u32;
    fn api(&self) -> u32;
    fn interop(&self) -> u32;
    fn global_handles(&self) -> [u64; 4];
    fn context_size(&self, context: usize) -> Option<(u32, u32, u32)>;
    fn release_context(&mut self, context: usize) -> Fallible<()>;
    fn create_context(&mut self, link: &os::Link, context: usize, width: u32, height: u32, views: u32) -> Fallible<[u64; 4]>;
    fn record(&mut self, slot: *mut Slot) -> Fallible<()>;
    fn timings(&self) -> [u32; 8];
    fn counts(&self) -> (u32, u32);
}

fn run(link: &mut os::Link, args: &os::Args) -> Fallible<()> {
    let header = link.header();
    if header.magic != MAGIC || header.version != VERSION { return Err("protocol mismatch between Unity and the tracer".into()); }
    header.tracer_pid.store(std::process::id(), Ordering::Release);
    link.connect(args)?;
    let mut backend: Box<dyn Backend> = if args.vulkan { Box::new(vk::Tracer::new(link)?) } else { new_dx(link)? };
    header.caps = backend.caps();
    header.tracer_api = backend.api();
    header.interop = backend.interop();
    let g = backend.global_handles();
    header.handle_upload = [g[0], g[1]];
    header.handle_fence_unity = g[2];
    header.handle_fence_tracer = g[3];
    header.generation.store(1, Ordering::Release);
    let mut last = header.frame_submitted.load(Ordering::Acquire);
    header.status.store(STATUS_READY, Ordering::Release);

    loop {
        header.heartbeat.fetch_add(1, Ordering::Relaxed);
        if !link.parent_alive() { return Ok(()); }
        for c in 0..MAX_CONTEXTS {
            let ch = &header.contexts[c];
            let w = ch.requested_width.load(Ordering::Acquire);
            let h = ch.requested_height.load(Ordering::Acquire);
            let v = ch.requested_views.load(Ordering::Acquire).clamp(1, 2);
            let current = backend.context_size(c);
            if w == 0 || h == 0 {
                if current.is_some() { backend.release_context(c)?; }
                continue;
            }
            if current == Some((w, h, v)) { continue; }
            backend.release_context(c)?;
            let handles = backend.create_context(link, c, w, h, v).map_err(|e| format!("allocate context {c}: {e}"))?;
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
        let deadline = Instant::now() + Duration::from_millis(250);
        while header.frame_queued.load(Ordering::Acquire) < frame {
            if Instant::now() > deadline { break; }
            link.wait_wake(1);
        }
        if header.frame_queued.load(Ordering::Acquire) < frame { continue; }
        let started = Instant::now();
        backend.record(slot).map_err(|e| format!("frame {frame}: {e}"))?;
        let timings = backend.timings();
        header.build_time_us.store(started.elapsed().as_micros() as u32, Ordering::Relaxed);
        header.gpu_time_us.store(timings[0], Ordering::Relaxed);
        for (i, t) in timings.iter().enumerate() { header.reserved[i] = *t; }
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

fn main() {
    let args = os::Args::parse();
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
