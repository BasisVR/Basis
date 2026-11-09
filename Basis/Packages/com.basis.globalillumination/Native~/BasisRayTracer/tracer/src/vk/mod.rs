mod device;
mod interop;
mod render;
mod scene;

use crate::{Backend, Fallible, os};
use basis_rt_protocol::*;

pub struct Tracer {
    renderer: render::Renderer,
    scene: scene::Scene,
    interop: interop::Interop,
    gpu: device::Gpu,
}

impl Tracer {
    pub fn new(link: &os::Link) -> Fallible<Tracer> {
        let header = link.header();
        let mut luid = [0u8; 8];
        luid[0..4].copy_from_slice(&header.luid_low.to_le_bytes());
        luid[4..8].copy_from_slice(&header.luid_high.to_le_bytes());
        let mut gpu = device::Gpu::new(header.device_uuid, luid).map_err(|e| format!("create Vulkan device: {e}"))?;
        let interop = interop::Interop::new(&gpu, header)?;
        let scene = scene::Scene::new(&mut gpu).map_err(|e| format!("allocate scene: {e}"))?;
        let renderer = render::Renderer::new(&mut gpu, &scene, &interop, link).map_err(|e| format!("build Vulkan ray tracing pipeline: {e}"))?;
        Ok(Tracer { renderer, scene, interop, gpu })
    }
}

impl Backend for Tracer {
    fn caps(&self) -> u32 { 0 }
    fn api(&self) -> u32 { TRACER_VULKAN }
    fn interop(&self) -> u32 { self.interop.mode }
    fn global_handles(&self) -> [u64; 4] { [self.renderer.upload[0].remote, self.renderer.upload[1].remote, self.renderer.fence_unity.remote, self.renderer.fence_tracer.remote] }
    fn context_size(&self, context: usize) -> Option<(u32, u32, u32)> { self.renderer.contexts[context].as_ref().map(|x| (x.width, x.height, x.views)) }

    fn release_context(&mut self, context: usize) -> Fallible<()> {
        self.gpu.flush()?;
        self.renderer.destroy_context(&mut self.gpu, context);
        Ok(())
    }

    fn create_context(&mut self, link: &os::Link, context: usize, width: u32, height: u32, views: u32) -> Fallible<[u64; 4]> {
        self.renderer.create_context(&mut self.gpu, &self.interop, link, context, width, height, views)
    }

    fn record(&mut self, slot: *mut Slot) -> Fallible<()> { self.renderer.record(&mut self.gpu, &mut self.scene, slot).map(|_| ()) }
    fn timings(&self) -> [u32; 8] { self.renderer.timings }
    fn counts(&self) -> (u32, u32) { (self.scene.meshes.len() as u32, self.scene.textures.len() as u32) }
}
