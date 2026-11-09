use crate::{Backend, Fallible, gpu, os, render, scene};
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
}

fn share(gpu: &gpu::Gpu, object: &ID3D12DeviceChild, link: &os::Link) -> Result<u64> {
    unsafe {
        let local = gpu.device.CreateSharedHandle(object, None, GENERIC_ALL.0, PCWSTR::null())?;
        let result = link.share_handle(local);
        let _ = CloseHandle(local);
        result
    }
}

impl Tracer {
    pub fn new(link: &os::Link) -> Fallible<Tracer> {
        let header = link.header();
        let gpu = gpu::Gpu::new(header.luid_low, header.luid_high).map_err(|e| format!("create D3D12 device: {}", e.message()))?;
        let scene = scene::Scene::new(&gpu).map_err(|e| format!("allocate scene: {e}"))?;
        let renderer = render::Renderer::new(&gpu).map_err(|e| format!("build ray tracing pipeline: {}", e.message()))?;
        let fences: [ID3D12Fence; 2] = unsafe { [gpu.device.CreateFence(0, D3D12_FENCE_FLAG_SHARED).map_err(|e| e.to_string())?, gpu.device.CreateFence(0, D3D12_FENCE_FLAG_SHARED).map_err(|e| e.to_string())?] };
        let upload = render::create_upload(&gpu).map_err(|e| format!("allocate upload textures: {e}"))?;
        let publish = || -> Result<[u64; 4]> {
            Ok([share(&gpu, &upload.textures[0].cast()?, link)?, share(&gpu, &upload.textures[1].cast()?, link)?, share(&gpu, &fences[0].cast()?, link)?, share(&gpu, &fences[1].cast()?, link)?])
        };
        let global = publish().map_err(|e| format!("share handles: {e}"))?;
        Ok(Tracer { gpu, scene, renderer, fences, _upload: upload, global })
    }
}

impl Backend for Tracer {
    fn caps(&self) -> u32 { self.gpu.caps }
    fn api(&self) -> u32 { TRACER_DXR }
    fn interop(&self) -> u32 { INTEROP_D3D12 }
    fn global_handles(&self) -> [u64; 4] { self.global }
    fn context_size(&self, context: usize) -> Option<(u32, u32, u32)> { self.renderer.contexts[context].as_ref().map(|x| (x.width, x.height, x.views)) }

    fn release_context(&mut self, context: usize) -> Fallible<()> {
        self.gpu.flush().map_err(|e| e.to_string())?;
        self.renderer.contexts[context] = None;
        Ok(())
    }

    fn create_context(&mut self, link: &os::Link, context: usize, width: u32, height: u32, views: u32) -> Fallible<[u64; 4]> {
        let ctx = render::create_context(&self.gpu, context, width, height, views).map_err(|e| e.to_string())?;
        let mut handles = [0u64; 4];
        for i in 0..2 {
            handles[i] = share(&self.gpu, &ctx.gbuffer[i].cast().map_err(|e: Error| e.to_string())?, link).map_err(|e| e.to_string())?;
            handles[2 + i] = share(&self.gpu, &ctx.output[i].cast().map_err(|e: Error| e.to_string())?, link).map_err(|e| e.to_string())?;
        }
        self.renderer.contexts[context] = Some(ctx);
        Ok(handles)
    }

    fn record(&mut self, slot: *mut Slot) -> Fallible<()> {
        let mut tlas = 0u32;
        self.renderer.record(&mut self.gpu, &mut self.scene, slot, &self.fences[0], &self.fences[1], &mut tlas).map_err(|e| e.message())?;
        Ok(())
    }

    fn timings(&self) -> [u32; 8] { self.renderer.timings }
    fn counts(&self) -> (u32, u32) { (self.scene.meshes.len() as u32, self.scene.textures.len() as u32) }
}
