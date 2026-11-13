use crate::{Backend, Fallible, gpu, os, relay, render, scene};
use basis_rt_protocol::*;
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D12::*;
use windows::core::*;

pub struct Tracer {
    gpu: gpu::Gpu,
    scene: scene::Scene,
    renderer: render::Renderer,
    fences: [ID3D12Fence; 2],
    _upload: render::Upload,
    global: [u64; 4],
    relay: Option<relay::Relay>,
}

fn share(device: &ID3D12Device, object: &ID3D12DeviceChild, link: &os::Link) -> Result<u64> {
    unsafe {
        let local = device.CreateSharedHandle(object, None, GENERIC_ALL.0, PCWSTR::null())?;
        let result = link.share_handle(local);
        let _ = CloseHandle(local);
        result
    }
}

impl Tracer {
    pub fn new(link: &os::Link) -> Fallible<Tracer> {
        let header = link.header();
        let home = (header.luid_low, header.luid_high);
        let forced = std::env::var_os("BASIS_RT_RELAY").is_some();
        let chosen = gpu::selected_adapter().filter(|c| home != (0, 0) && (*c != home || forced)).and_then(|c| gpu::Gpu::new(c.0, c.1).ok());
        let relayed = chosen.is_some();
        let gpu = match chosen { Some(g) => g, None => gpu::Gpu::new(home.0, home.1).map_err(|e| format!("create D3D12 device: {}", e.message()))? };
        let scene = scene::Scene::new(&gpu).map_err(|e| format!("allocate scene: {e}"))?;
        let mut renderer = render::Renderer::new(&gpu).map_err(|e| format!("build ray tracing pipeline: {}", e.message()))?;
        let fences: [ID3D12Fence; 2] = unsafe { [gpu.device.CreateFence(0, D3D12_FENCE_FLAG_SHARED).map_err(|e| e.to_string())?, gpu.device.CreateFence(0, D3D12_FENCE_FLAG_SHARED).map_err(|e| e.to_string())?] };
        let upload = render::create_upload(&gpu).map_err(|e| format!("allocate upload textures: {e}"))?;
        let device: ID3D12Device = gpu.device.cast().map_err(|e: Error| e.to_string())?;
        let mut relay = None;
        if relayed {
            let (r, cross) = relay::Relay::new(home.0, home.1, &device).map_err(|e| format!("link the two GPUs: {}", e.message()))?;
            let [a, b] = cross;
            renderer.cross_upload = Some([(upload.textures[0].clone(), a), (upload.textures[1].clone(), b)]);
            relay = Some(r);
        }
        let publish = || -> Result<[u64; 4]> {
            match &relay {
                Some(r) => Ok([share(&r.device, &r.upload[0].cast()?, link)?, share(&r.device, &r.upload[1].cast()?, link)?, share(&r.device, &r.fence_unity.cast()?, link)?, share(&r.device, &r.fence_tracer.cast()?, link)?]),
                None => Ok([share(&device, &upload.textures[0].cast()?, link)?, share(&device, &upload.textures[1].cast()?, link)?, share(&device, &fences[0].cast()?, link)?, share(&device, &fences[1].cast()?, link)?]),
            }
        };
        let global = publish().map_err(|e| format!("share handles: {e}"))?;
        if let Some(stop) = &link.stop {
            for f in &fences { stop.register(f); }
            if let Some(r) = &relay { for f in [&r.fence_unity, &r.fence_tracer, &r.trace_in, &r.trace_out] { stop.register(f); } }
        }
        Ok(Tracer { gpu, scene, renderer, fences, _upload: upload, global, relay })
    }
}

impl Backend for Tracer {
    fn caps(&self) -> u32 { self.gpu.caps | if self.relay.is_none() { CAP_DIRECT } else { 0 } }
    fn api(&self) -> u32 { TRACER_DXR }
    fn interop(&self) -> u32 { INTEROP_D3D12 }
    fn global_handles(&self) -> [u64; 4] { self.global }
    fn context_size(&self, context: usize) -> Option<(u32, u32, u32)> { self.renderer.contexts[context].as_ref().map(|x| (x.width, x.height, x.views)) }

    fn release_context(&mut self, context: usize) -> Fallible<()> {
        self.gpu.flush().map_err(|e| e.to_string())?;
        if let Some(r) = self.relay.as_mut() { r.release_context(context); }
        self.renderer.contexts[context] = None;
        Ok(())
    }

    fn create_context(&mut self, link: &os::Link, context: usize, width: u32, height: u32, views: u32, targets: Option<[u64; 4]>) -> Fallible<[u64; 4]> {
        let device: ID3D12Device = self.gpu.device.cast().map_err(|e: Error| e.to_string())?;
        if let Some(t) = targets {
            if self.relay.is_some() { return Err("Unity-owned targets cannot be used with a second GPU".into()); }
            let open = |h: u64| -> Fallible<ID3D12Resource> { let mut r: Option<ID3D12Resource> = None; unsafe { device.OpenSharedHandle(HANDLE(h as *mut core::ffi::c_void), &mut r).map_err(|e| format!("open a Unity target: {}", e.message()))?; } r.ok_or_else(|| "open a Unity target".to_string()) };
            let opened = ([open(t[0])?, open(t[1])?], [open(t[2])?, open(t[3])?]);
            let full = width * views;
            for (r, w, h) in [(&opened.0[0], full * 2, height * 2), (&opened.0[1], full * 2, height * 2), (&opened.1[0], full, height), (&opened.1[1], full, height)] {
                let d = unsafe { r.GetDesc() };
                if d.Width != w as u64 || d.Height != h { return Err(format!("Unity target is {}x{}, expected {w}x{h}", d.Width, d.Height)); }
            }
            let ctx = render::create_context(&self.gpu, context, width, height, views, Some(opened)).map_err(|e| e.to_string())?;
            self.renderer.contexts[context] = Some(ctx);
            return Ok([0; 4]);
        }
        let mut ctx = render::create_context(&self.gpu, context, width, height, views, None).map_err(|e| e.to_string())?;
        let mut handles = [0u64; 4];
        if let Some(r) = self.relay.as_mut() {
            let (shared, cross) = r.create_context(&device, context, width, height, views).map_err(|e| format!("link the two GPUs: {}", e.message()))?;
            for i in 0..4 { handles[i] = share(&r.device, &shared[i].cast().map_err(|e: Error| e.to_string())?, link).map_err(|e| e.to_string())?; }
            ctx.cross = Some(cross);
        } else {
            for i in 0..2 {
                handles[i] = share(&device, &ctx.gbuffer[i].cast().map_err(|e: Error| e.to_string())?, link).map_err(|e| e.to_string())?;
                handles[2 + i] = share(&device, &ctx.output[i].cast().map_err(|e: Error| e.to_string())?, link).map_err(|e| e.to_string())?;
            }
        }
        self.renderer.contexts[context] = Some(ctx);
        Ok(handles)
    }

    fn record(&mut self, slot: *mut Slot) -> Fallible<()> {
        let mut tlas = 0u32;
        let Some(r) = self.relay.as_mut() else {
            self.renderer.record(&mut self.gpu, &mut self.scene, slot, &self.fences[0], &self.fences[1], &mut tlas).map_err(|e| e.message())?;
            return Ok(());
        };
        let params = unsafe { (*slot).params };
        r.send(&params).map_err(|e| e.message())?;
        self.renderer.record(&mut self.gpu, &mut self.scene, slot, &r.trace_in, &r.trace_out, &mut tlas).map_err(|e| e.message())?;
        r.receive(&params).map_err(|e| e.message())?;
        Ok(())
    }

    fn timings(&self) -> [u32; 8] { self.renderer.timings }
    fn prepare(&mut self, slot: *mut Slot) -> Fallible<()> { self.renderer.prepare(&self.gpu, &self.scene, slot).map_err(|e| e.message()) }
    fn grid_us(&self) -> (u32, u32) { (self.renderer.grid.build_us, self.renderer.grid.wait_us) }
    fn counts(&self) -> (u32, u32) { (self.scene.meshes.len() as u32, self.scene.textures.len() as u32) }
    fn idle(&mut self) { let _ = self.gpu.flush(); }
}
