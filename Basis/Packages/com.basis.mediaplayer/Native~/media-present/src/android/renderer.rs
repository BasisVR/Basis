//! The per-session render pass: import the decoder's `AHardwareBuffer`
//! into Unity's `VkDevice` and draw one fullscreen pass converting into
//! Unity's RGBA RenderTexture, recorded on Unity's current command buffer
//! inside the render event. The driver's suggested
//! `VkSamplerYcbcrConversion` does the matrix/range work, read per buffer
//! because decoder buffers carry their own dataspace. GPU lifetime follows
//! Unity's frame counters (`safeFrameNumber`), so nothing is destroyed
//! while a submitted command buffer might still read it.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};

use ash::vk;
use ash::vk::Handle;
use media_decode::VideoFrame;

use super::fns::DeviceFns;
use super::intercept::{CTX, VkCtx};
use super::unity::{self, logf};

/// Import-cache ceiling: the reader recycles a fixed buffer set (~10), so
/// steady state sits below this; the bound only matters across decoder
/// rebuilds.
const IMPORT_CACHE_CAP: usize = 16;
/// Draws a cached import may go unused before it is retired. A live image
/// reader cycles through its buffers within its depth (10), so an import idle
/// for this long belongs to a reader that is gone, such as the one before a
/// decoder rebuild, and only pins its buffer.
const IMPORT_IDLE_DRAWS: u64 = 32;
/// Descriptor ring depth: must exceed the deepest frames-in-flight Unity
/// runs (double/triple buffering), or an in-use set would be rewritten.
const DESC_RING: u32 = 16;

#[repr(C)]
struct Push {
    dst_w: i32,
    dst_h: i32,
    inv_coded_w: f32,
    inv_coded_h: f32,
    flip_x: i32,
    flip_y: i32,
    src_x: i32,
    src_y: i32,
    src_w: i32,
    src_h: i32,
    linearise: i32,
}

/// Everything keyed by the driver's suggested conversion for the current
/// buffer family. Rebuilt when the suggestion changes (a stream/decoder
/// change), retired through the frame-number queue.
struct ConvertObjects {
    conversion: vk::SamplerYcbcrConversion,
    sampler: vk::Sampler,
    set_layout: vk::DescriptorSetLayout,
    pipe_layout: vk::PipelineLayout,
    pool: vk::DescriptorPool,
    sets: Vec<vk::DescriptorSet>,
    next_set: usize,
    /// The pass for the output's format, built at the first draw into it.
    target: Option<Target>,
}

impl ConvertObjects {
    fn destroy(&self, device: vk::Device, fns: &DeviceFns) {
        if let Some(target) = &self.target {
            target.destroy(device, fns);
        }
        // SAFETY: called only once the retire queue proves no submitted
        // command buffer references these objects.
        unsafe {
            (fns.destroy_descriptor_pool)(device, self.pool, core::ptr::null());
            (fns.destroy_pipeline_layout)(device, self.pipe_layout, core::ptr::null());
            (fns.destroy_descriptor_set_layout)(device, self.set_layout, core::ptr::null());
            (fns.destroy_sampler)(device, self.sampler, core::ptr::null());
            (fns.destroy_ycbcr_conversion)(device, self.conversion, core::ptr::null());
        }
    }
}

/// The render pass and pipeline drawing into one output format.
struct Target {
    format: vk::Format,
    render_pass: vk::RenderPass,
    pipeline: vk::Pipeline,
}

impl Target {
    fn destroy(&self, device: vk::Device, fns: &DeviceFns) {
        // SAFETY: retire-queue discipline as above.
        unsafe {
            (fns.destroy_pipeline)(device, self.pipeline, core::ptr::null());
            (fns.destroy_render_pass)(device, self.render_pass, core::ptr::null());
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct ConvertKey {
    format: i32,
    external_format: u64,
    model: i32,
    range: i32,
    x_chroma: i32,
    y_chroma: i32,
    chroma_filter: i32,
    sampler_filter: i32,
}

struct Imported {
    image: vk::Image,
    memory: vk::DeviceMemory,
    view: vk::ImageView,
    width: u32,
    height: u32,
    conv_gen: u64,
    last_used: u64,
    last_draw: u64,
}

impl Imported {
    fn destroy(&self, device: vk::Device, fns: &DeviceFns) {
        // SAFETY: retire-queue discipline as above. Freeing the memory
        // releases the import's AHardwareBuffer reference.
        unsafe {
            (fns.destroy_image_view)(device, self.view, core::ptr::null());
            (fns.destroy_image)(device, self.image, core::ptr::null());
            (fns.free_memory)(device, self.memory, core::ptr::null());
        }
    }
}

/// A view over the Unity output image, and what it was built for. Unity
/// owns the image: it is free to destroy it and hand a later one the same
/// handle value, so the value alone is not an identity. `generation` is
/// the output-texture registration the view belongs to, which only the
/// managed side can advance, and a new image always arrives through one.
struct DstView {
    generation: u64,
    image: u64,
    format: vk::Format,
    view: vk::ImageView,
    framebuffer: vk::Framebuffer,
}

enum Retired {
    Frame(#[allow(dead_code)] VideoFrame),
    Import(Imported),
    Convert(ConvertObjects),
    Target(Target),
    DstView(vk::ImageView, vk::Framebuffer),
}

impl Retired {
    fn destroy(self, ctx: &VkCtx) {
        match self {
            Retired::Frame(frame) => drop(frame),
            Retired::Import(imported) => imported.destroy(ctx.device, &ctx.fns),
            Retired::Convert(convert) => convert.destroy(ctx.device, &ctx.fns),
            Retired::Target(target) => target.destroy(ctx.device, &ctx.fns),
            // SAFETY: retire discipline as elsewhere.
            Retired::DstView(view, framebuffer) => unsafe {
                (ctx.fns.destroy_framebuffer)(ctx.device, framebuffer, core::ptr::null());
                (ctx.fns.destroy_image_view)(ctx.device, view, core::ptr::null());
            },
        }
    }
}

impl Default for SessionRenderer {
    fn default() -> Self {
        Self::new()
    }
}

pub struct SessionRenderer {
    convert: Option<ConvertObjects>,
    convert_key: Option<ConvertKey>,
    conv_gen: u64,
    imports: HashMap<usize, Imported>,
    /// Draws so far, the clock `Imported::last_draw` is read against.
    draws: u64,
    dst_view: Option<DstView>,
    /// (frame number the item was last referenced in, item).
    retired: VecDeque<(u64, Retired)>,
    warned_no_ctx: bool,
    warned_no_recording: bool,
    warned_format: bool,
    warned_cpu_frame: bool,
}

// SAFETY: only ever driven from Unity's render thread; VideoFrame
// payloads are Send.
unsafe impl Send for SessionRenderer {}

impl SessionRenderer {
    pub fn new() -> Self {
        Self {
            convert: None,
            convert_key: None,
            conv_gen: 0,
            imports: HashMap::new(),
            draws: 0,
            dst_view: None,
            retired: VecDeque::new(),
            warned_no_ctx: false,
            warned_no_recording: false,
            warned_format: false,
            warned_cpu_frame: false,
        }
    }

    /// Draw `frame` (if any) into the registered Unity texture. Returns
    /// true when a fresh frame was recorded. Failures log (once per cause
    /// where repetitive) and drop the frame; the render thread never blocks
    /// and never panics.
    ///
    /// # Safety
    /// `unity_texture` must be the `GetNativeTexturePtr()` value of a live
    /// Unity texture created on the same `VkDevice` as the context this
    /// renderer captured, and it must stay live for the duration of the
    /// call. Unity's `access_texture` dereferences it and writes back the
    /// `VkImage`, layout and extent that drive the view and the draw
    /// below, so a stale or fabricated pointer is a use-after-free inside
    /// Unity's own resource tracker. Only null is rejected here.
    pub unsafe fn render(
        &mut self,
        frame: Option<VideoFrame>,
        unity_texture: *mut core::ffi::c_void,
        generation: u64,
    ) -> bool {
        // The body holds this guard across sites that can panic, and the
        // ABI fence catches those without clearing the poison. Recovering
        // here stops one caught panic disabling the present path (and the
        // device event) for the rest of the process.
        let mut guard = CTX.lock().unwrap_or_else(|e| e.into_inner());
        let Some(ctx) = guard.as_mut() else {
            if !self.warned_no_ctx {
                self.warned_no_ctx = true;
                unity::log("render: no vulkan context (device event not seen) — dropping frames");
            }
            return false;
        };

        // Frame counters first: they drive retirement even on idle calls.
        // SAFETY: Unity vtable calls on the render thread, the documented
        // call site for both.
        let recording = unsafe {
            let mut state = core::mem::zeroed::<unity::UnityVulkanRecordingState>();
            let recording_state = (*ctx.vulkan_iface).command_recording_state;
            if !recording_state.is_some_and(|f| f(&mut state, unity::QUEUE_ACCESS_DONT_CARE))
                || state.command_buffer == vk::CommandBuffer::null()
            {
                if !self.warned_no_recording {
                    self.warned_no_recording = true;
                    unity::log(
                        "render: no command recording state at the event's call site — frames cannot draw",
                    );
                }
                return false;
            }
            state
        };
        self.drain_retired(ctx, recording.safe_frame_number);
        graveyard::collect(
            ctx,
            recording.current_frame_number,
            recording.safe_frame_number,
        );

        let Some(frame) = frame else {
            return false;
        };
        let opaque = match frame {
            VideoFrame::Opaque(ref f) => f,
            VideoFrame::Nv12(_) => {
                if !self.warned_cpu_frame {
                    self.warned_cpu_frame = true;
                    unity::log("render: CPU frame on the Vulkan path (no upload pass) — dropped");
                }
                return false;
            }
        };
        if unity_texture.is_null() {
            return false;
        }

        let drew = self.draw(ctx, &recording, opaque, unity_texture, generation);
        // The frame's buffer must outlive the submitted command buffer.
        self.retired
            .push_back((recording.current_frame_number, Retired::Frame(frame)));
        drew
    }

    fn draw(
        &mut self,
        ctx: &VkCtx,
        recording: &unity::UnityVulkanRecordingState,
        frame: &media_decode::OpaqueFrame,
        unity_texture: *mut core::ffi::c_void,
        generation: u64,
    ) -> bool {
        let buffer = frame.image.hardware_buffer();
        if buffer.is_null() {
            return false;
        }
        let geometry = frame
            .image
            .geometry()
            .unwrap_or_else(|| media_decode::BufferGeometry::whole(frame.width, frame.height));

        // Import properties and the driver's suggested conversion, read for
        // every buffer rather than assumed.
        let (props, fmt_props) = {
            let mut fmt_props = vk::AndroidHardwareBufferFormatPropertiesANDROID::default();
            let mut props = vk::AndroidHardwareBufferPropertiesANDROID {
                p_next: (&raw mut fmt_props).cast(),
                ..Default::default()
            };
            // SAFETY: live device + AHB (owned by the frame in hand).
            let result = unsafe { (ctx.fns.get_ahb_props)(ctx.device, buffer.cast(), &mut props) };
            if result != vk::Result::SUCCESS {
                logf!("render: GetAndroidHardwareBufferProperties {result:?}");
                return false;
            }
            (props, fmt_props)
        };

        let (chroma_filter, sampler_filter) = conversion_filters(fmt_props.format_features);
        let key = ConvertKey {
            format: fmt_props.format.as_raw(),
            external_format: fmt_props.external_format,
            model: fmt_props.suggested_ycbcr_model.as_raw(),
            range: fmt_props.suggested_ycbcr_range.as_raw(),
            x_chroma: fmt_props.suggested_x_chroma_offset.as_raw(),
            y_chroma: fmt_props.suggested_y_chroma_offset.as_raw(),
            chroma_filter: chroma_filter.as_raw(),
            sampler_filter: sampler_filter.as_raw(),
        };
        if self.convert_key != Some(key) {
            if let Some(old) = self.convert.take() {
                self.retired
                    .push_back((recording.current_frame_number, Retired::Convert(old)));
            }
            match build_convert(ctx, &fmt_props, chroma_filter, sampler_filter) {
                Ok(objects) => {
                    self.convert = Some(objects);
                    self.convert_key = Some(key);
                    self.conv_gen += 1;
                    logf!(
                        "render: conversion built (externalFormat=0x{:x} model={:?} range={:?} chroma={:?} sampler={:?})",
                        fmt_props.external_format,
                        fmt_props.suggested_ycbcr_model,
                        fmt_props.suggested_ycbcr_range,
                        chroma_filter,
                        sampler_filter
                    );
                }
                Err(what) => {
                    logf!("render: conversion build failed: {what}");
                    return false;
                }
            }
        }

        // Import (or reuse) this buffer's VkImage. The memory import holds
        // its own AHB reference, so a cached entry stays valid across the
        // reader recycling the buffer.
        let ahb_key = buffer as usize;
        self.draws += 1;
        self.retire_idle_import(ahb_key);
        let stale = self
            .imports
            .get(&ahb_key)
            .is_some_and(|entry| entry.conv_gen != self.conv_gen);
        if stale && let Some(old) = self.imports.remove(&ahb_key) {
            self.retired
                .push_back((old.last_used, Retired::Import(old)));
        }
        if !self.imports.contains_key(&ahb_key) {
            let convert = self.convert.as_ref().expect("built above");
            match import_buffer(
                ctx,
                buffer,
                &props,
                &fmt_props,
                convert.conversion,
                geometry.buffer_width.max(1),
                geometry.buffer_height.max(1),
                self.conv_gen,
            ) {
                Ok(imported) => {
                    self.evict_imports(recording.current_frame_number);
                    self.imports.insert(ahb_key, imported);
                }
                Err(what) => {
                    logf!("render: import failed: {what}");
                    return false;
                }
            }
        }

        // SAFETY: Unity vtable + Vulkan recording below run on the render
        // thread against live handles; barriers/descriptors follow the
        // contract described inline.
        unsafe {
            let (Some(ensure_outside_render_pass), Some(access_texture), Some(recording_state)) = (
                (*ctx.vulkan_iface).ensure_outside_render_pass,
                (*ctx.vulkan_iface).access_texture,
                (*ctx.vulkan_iface).command_recording_state,
            ) else {
                return false;
            };
            ensure_outside_render_pass();

            // Unity handles the destination's transition + tracking.
            let mut dst = core::mem::zeroed::<unity::UnityVulkanImage>();
            if !access_texture(
                unity_texture,
                core::ptr::null(),
                vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL,
                vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                vk::AccessFlags::COLOR_ATTACHMENT_WRITE,
                unity::ACCESS_PIPELINE_BARRIER,
                &mut dst,
            ) {
                return false;
            }
            if dst.image == vk::Image::null() {
                return false;
            }
            // An RGBA8 RenderTexture, drawn into as a colour attachment: sRGB
            // in a Linear colour-space project, UNORM in a Gamma one.
            let linearise = match dst.format {
                vk::Format::R8G8B8A8_SRGB => true,
                vk::Format::R8G8B8A8_UNORM => false,
                _ => {
                    if !self.warned_format {
                        self.warned_format = true;
                        logf!(
                            "render: unsupported output format {:?} (need an RGBA32 RenderTexture)",
                            dst.format
                        );
                    }
                    return false;
                }
            };
            if !dst.usage.contains(vk::ImageUsageFlags::COLOR_ATTACHMENT) {
                if !self.warned_format {
                    self.warned_format = true;
                    logf!(
                        "render: output texture cannot be drawn into (usage {:?})",
                        dst.usage
                    );
                }
                return false;
            }

            // Access calls invalidate the recording state: re-fetch.
            let mut state = core::mem::zeroed::<unity::UnityVulkanRecordingState>();
            if !recording_state(&mut state, unity::QUEUE_ACCESS_DONT_CARE)
                || state.command_buffer == vk::CommandBuffer::null()
            {
                return false;
            }
            let cb = state.command_buffer;

            let convert = self.convert.as_mut().expect("built above");
            if convert
                .target
                .as_ref()
                .is_none_or(|t| t.format != dst.format)
            {
                if let Some(old) = convert.target.take() {
                    self.retired
                        .push_back((state.current_frame_number, Retired::Target(old)));
                }
                match build_target(ctx, convert.pipe_layout, dst.format) {
                    Ok(target) => {
                        logf!("render: output pass built for {:?}", dst.format);
                        convert.target = Some(target);
                    }
                    Err(what) => {
                        logf!("render: output pass build failed: {what}");
                        return false;
                    }
                }
            }
            let (render_pass, pipeline) = {
                let target = convert.target.as_ref().expect("built above");
                (target.render_pass, target.pipeline)
            };

            // The format in this key is what retires the framebuffer along
            // with the render pass it was built for, when a new format
            // replaces the `Target` above.
            let wanted = (generation, dst.image.as_raw(), dst.format);
            let cached = self
                .dst_view
                .as_ref()
                .filter(|v| (v.generation, v.image, v.format) == wanted)
                .map(|v| v.framebuffer);
            let framebuffer = match cached {
                Some(framebuffer) => framebuffer,
                None => {
                    // The image this view was made for is gone (or was
                    // never this one): the old view goes through the frame
                    // queue rather than being reused or dropped in flight.
                    if let Some(stale) = self.dst_view.take() {
                        self.retired.push_back((
                            state.current_frame_number,
                            Retired::DstView(stale.view, stale.framebuffer),
                        ));
                    }
                    let view_ci = vk::ImageViewCreateInfo {
                        image: dst.image,
                        view_type: vk::ImageViewType::TYPE_2D,
                        format: dst.format,
                        subresource_range: vk::ImageSubresourceRange {
                            aspect_mask: vk::ImageAspectFlags::COLOR,
                            base_mip_level: 0,
                            level_count: 1,
                            base_array_layer: 0,
                            layer_count: 1,
                        },
                        ..Default::default()
                    };
                    let mut view = vk::ImageView::null();
                    let result = (ctx.fns.create_image_view)(
                        ctx.device,
                        &view_ci,
                        core::ptr::null(),
                        &mut view,
                    );
                    if result != vk::Result::SUCCESS {
                        logf!("render: dst view {result:?}");
                        return false;
                    }
                    let fb_ci = vk::FramebufferCreateInfo {
                        render_pass,
                        attachment_count: 1,
                        p_attachments: &view,
                        width: dst.extent.width,
                        height: dst.extent.height,
                        layers: 1,
                        ..Default::default()
                    };
                    let mut framebuffer = vk::Framebuffer::null();
                    let result = (ctx.fns.create_framebuffer)(
                        ctx.device,
                        &fb_ci,
                        core::ptr::null(),
                        &mut framebuffer,
                    );
                    if result != vk::Result::SUCCESS {
                        (ctx.fns.destroy_image_view)(ctx.device, view, core::ptr::null());
                        logf!("render: framebuffer {result:?}");
                        return false;
                    }
                    self.dst_view = Some(DstView {
                        generation,
                        image: wanted.1,
                        format: dst.format,
                        view,
                        framebuffer,
                    });
                    framebuffer
                }
            };

            let imported = self.imports.get_mut(&ahb_key).expect("imported above");
            imported.last_used = state.current_frame_number;
            imported.last_draw = self.draws;

            // Acquire the buffer from the codec (foreign queue family). The
            // contents were written outside Vulkan and arrive through the
            // external-memory guarantee, not the layout transition, so
            // UNDEFINED as the old layout loses nothing.
            let barrier = vk::ImageMemoryBarrier {
                src_access_mask: vk::AccessFlags::empty(),
                dst_access_mask: vk::AccessFlags::SHADER_READ,
                old_layout: vk::ImageLayout::UNDEFINED,
                new_layout: vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL,
                src_queue_family_index: vk::QUEUE_FAMILY_FOREIGN_EXT,
                dst_queue_family_index: ctx.queue_family,
                image: imported.image,
                subresource_range: vk::ImageSubresourceRange {
                    aspect_mask: vk::ImageAspectFlags::COLOR,
                    base_mip_level: 0,
                    level_count: 1,
                    base_array_layer: 0,
                    layer_count: 1,
                },
                ..Default::default()
            };
            (ctx.fns.cmd_pipeline_barrier)(
                cb,
                vk::PipelineStageFlags::TOP_OF_PIPE,
                vk::PipelineStageFlags::FRAGMENT_SHADER,
                vk::DependencyFlags::empty(),
                0,
                core::ptr::null(),
                0,
                core::ptr::null(),
                1,
                &barrier,
            );

            let convert = self.convert.as_mut().expect("built above");
            let set = convert.sets[convert.next_set % convert.sets.len()];
            convert.next_set = convert.next_set.wrapping_add(1);

            let src_info = vk::DescriptorImageInfo {
                sampler: convert.sampler,
                image_view: imported.view,
                image_layout: vk::ImageLayout::SHADER_READ_ONLY_OPTIMAL,
            };
            let write = vk::WriteDescriptorSet {
                dst_set: set,
                dst_binding: 0,
                descriptor_count: 1,
                descriptor_type: vk::DescriptorType::COMBINED_IMAGE_SAMPLER,
                p_image_info: &src_info,
                ..Default::default()
            };
            (ctx.fns.update_descriptor_sets)(ctx.device, 1, &write, 0, core::ptr::null());

            let extent = vk::Extent2D {
                width: dst.extent.width,
                height: dst.extent.height,
            };
            let begin = vk::RenderPassBeginInfo {
                render_pass,
                framebuffer,
                render_area: vk::Rect2D {
                    offset: vk::Offset2D::default(),
                    extent,
                },
                ..Default::default()
            };
            (ctx.fns.cmd_begin_render_pass)(cb, &begin, vk::SubpassContents::INLINE);
            let viewport = vk::Viewport {
                x: 0.0,
                y: 0.0,
                width: extent.width as f32,
                height: extent.height as f32,
                min_depth: 0.0,
                max_depth: 1.0,
            };
            (ctx.fns.cmd_set_viewport)(cb, 0, 1, &viewport);
            let scissor = vk::Rect2D {
                offset: vk::Offset2D::default(),
                extent,
            };
            (ctx.fns.cmd_set_scissor)(cb, 0, 1, &scissor);
            (ctx.fns.cmd_bind_pipeline)(cb, vk::PipelineBindPoint::GRAPHICS, pipeline);
            (ctx.fns.cmd_bind_descriptor_sets)(
                cb,
                vk::PipelineBindPoint::GRAPHICS,
                convert.pipe_layout,
                0,
                1,
                &set,
                0,
                core::ptr::null(),
            );
            let push = Push {
                dst_w: extent.width as i32,
                dst_h: extent.height as i32,
                inv_coded_w: 1.0 / imported.width.max(1) as f32,
                inv_coded_h: 1.0 / imported.height.max(1) as f32,
                // Unity samples an externally written RenderTexture
                // vertically flipped on Vulkan (row 0 is the on-screen
                // bottom; observed on Quest), so the pass writes rows
                // inverted.
                flip_x: 0,
                flip_y: 1,
                src_x: geometry.left as i32,
                src_y: geometry.top as i32,
                src_w: geometry.width.max(1) as i32,
                src_h: geometry.height.max(1) as i32,
                linearise: i32::from(linearise),
            };
            (ctx.fns.cmd_push_constants)(
                cb,
                convert.pipe_layout,
                vk::ShaderStageFlags::FRAGMENT,
                0,
                core::mem::size_of::<Push>() as u32,
                (&raw const push).cast(),
            );
            (ctx.fns.cmd_draw)(cb, 3, 1, 0, 0);
            (ctx.fns.cmd_end_render_pass)(cb);
        }
        true
    }

    /// Retire one import that has sat unused for `IMPORT_IDLE_DRAWS`, never
    /// the one about to be drawn. One per draw is enough to clear a dead
    /// reader's buffers within a few frames, without a scan that allocates.
    fn retire_idle_import(&mut self, drawing: usize) {
        let draws = self.draws;
        let idle = self
            .imports
            .iter()
            .find(|(key, entry)| {
                **key != drawing && draws.saturating_sub(entry.last_draw) > IMPORT_IDLE_DRAWS
            })
            .map(|(key, _)| *key);
        if let Some(old) = idle.and_then(|key| self.imports.remove(&key)) {
            self.retired
                .push_back((old.last_used, Retired::Import(old)));
        }
    }

    fn evict_imports(&mut self, frame_number: u64) {
        while self.imports.len() >= IMPORT_CACHE_CAP {
            let Some((&key, _)) = self.imports.iter().min_by_key(|(_, entry)| entry.last_used)
            else {
                return;
            };
            let old = self.imports.remove(&key).expect("key from iteration");
            self.retired.push_back((frame_number, Retired::Import(old)));
        }
    }

    fn drain_retired(&mut self, ctx: &VkCtx, safe_frame_number: u64) {
        while let Some((frame_number, _)) = self.retired.front() {
            if *frame_number > safe_frame_number {
                return;
            }
            self.retired
                .pop_front()
                .expect("front checked")
                .1
                .destroy(ctx);
        }
    }
}

impl Drop for SessionRenderer {
    fn drop(&mut self) {
        // Sessions close from the main thread while Unity may still be
        // submitting, so Vulkan objects go to the process-wide graveyard,
        // drained by later render events once their frames are provably
        // retired.
        let mut items: Vec<Retired> = Vec::new();
        if let Some(convert) = self.convert.take() {
            items.push(Retired::Convert(convert));
        }
        for (_, imported) in self.imports.drain() {
            items.push(Retired::Import(imported));
        }
        if let Some(dst_view) = self.dst_view.take() {
            items.push(Retired::DstView(dst_view.view, dst_view.framebuffer));
        }
        for (_, item) in self.retired.drain(..) {
            items.push(item);
        }
        graveyard::bury(items);
    }
}

/// Chroma reconstruction and sampler filters the format allows. Each
/// LINEAR has its own feature bit, and without separate reconstruction the
/// two must match, so chroma drops to NEAREST when the sampler cannot filter.
/// Qualcomm's UBWC external formats (VP9 and AV1 on Adreno) advertise uneven
/// sets.
fn conversion_filters(features: vk::FormatFeatureFlags) -> (vk::Filter, vk::Filter) {
    let filter = |linear: bool| {
        if linear {
            vk::Filter::LINEAR
        } else {
            vk::Filter::NEAREST
        }
    };
    let sampler_linear = features.contains(vk::FormatFeatureFlags::SAMPLED_IMAGE_FILTER_LINEAR);
    let chroma_linear =
        features.contains(vk::FormatFeatureFlags::SAMPLED_IMAGE_YCBCR_CONVERSION_LINEAR_FILTER);
    if features.contains(
        vk::FormatFeatureFlags::SAMPLED_IMAGE_YCBCR_CONVERSION_SEPARATE_RECONSTRUCTION_FILTER,
    ) {
        (filter(chroma_linear), filter(sampler_linear))
    } else {
        let both = filter(chroma_linear && sampler_linear);
        (both, both)
    }
}

fn build_convert(
    ctx: &VkCtx,
    fmt_props: &vk::AndroidHardwareBufferFormatPropertiesANDROID<'_>,
    chroma_filter: vk::Filter,
    sampler_filter: vk::Filter,
) -> Result<ConvertObjects, String> {
    let external = fmt_props.format == vk::Format::UNDEFINED;
    // SAFETY: object creation against the live device; every result is
    // checked and partially built objects are destroyed on the error
    // paths (see `cleanup_base`).
    unsafe {
        let ext_format = vk::ExternalFormatANDROID {
            external_format: fmt_props.external_format,
            ..Default::default()
        };
        let conv_ci = vk::SamplerYcbcrConversionCreateInfo {
            p_next: if external {
                (&raw const ext_format).cast()
            } else {
                core::ptr::null()
            },
            format: fmt_props.format,
            ycbcr_model: fmt_props.suggested_ycbcr_model,
            ycbcr_range: fmt_props.suggested_ycbcr_range,
            components: fmt_props.sampler_ycbcr_conversion_components,
            x_chroma_offset: fmt_props.suggested_x_chroma_offset,
            y_chroma_offset: fmt_props.suggested_y_chroma_offset,
            chroma_filter,
            ..Default::default()
        };
        let mut conversion = vk::SamplerYcbcrConversion::null();
        let result = (ctx.fns.create_ycbcr_conversion)(
            ctx.device,
            &conv_ci,
            core::ptr::null(),
            &mut conversion,
        );
        if result != vk::Result::SUCCESS {
            return Err(format!("CreateSamplerYcbcrConversion {result:?}"));
        }

        let conv_info = vk::SamplerYcbcrConversionInfo {
            conversion,
            ..Default::default()
        };
        let samp_ci = vk::SamplerCreateInfo {
            p_next: (&raw const conv_info).cast(),
            mag_filter: sampler_filter,
            min_filter: sampler_filter,
            address_mode_u: vk::SamplerAddressMode::CLAMP_TO_EDGE,
            address_mode_v: vk::SamplerAddressMode::CLAMP_TO_EDGE,
            address_mode_w: vk::SamplerAddressMode::CLAMP_TO_EDGE,
            max_anisotropy: 1.0,
            ..Default::default()
        };
        let mut sampler = vk::Sampler::null();
        let result =
            (ctx.fns.create_sampler)(ctx.device, &samp_ci, core::ptr::null(), &mut sampler);
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_ycbcr_conversion)(ctx.device, conversion, core::ptr::null());
            return Err(format!("CreateSampler {result:?}"));
        }

        // Set layout: binding 0 samples YCbCr through the immutable
        // sampler (required for conversion sampling).
        let binding = vk::DescriptorSetLayoutBinding {
            binding: 0,
            descriptor_type: vk::DescriptorType::COMBINED_IMAGE_SAMPLER,
            descriptor_count: 1,
            stage_flags: vk::ShaderStageFlags::FRAGMENT,
            p_immutable_samplers: &sampler,
            ..Default::default()
        };
        let layout_ci = vk::DescriptorSetLayoutCreateInfo {
            binding_count: 1,
            p_bindings: &binding,
            ..Default::default()
        };
        let mut set_layout = vk::DescriptorSetLayout::null();
        let result = (ctx.fns.create_descriptor_set_layout)(
            ctx.device,
            &layout_ci,
            core::ptr::null(),
            &mut set_layout,
        );
        let cleanup_base = |upto: u32| {
            if upto >= 2 {
                (ctx.fns.destroy_descriptor_set_layout)(ctx.device, set_layout, core::ptr::null());
            }
            (ctx.fns.destroy_sampler)(ctx.device, sampler, core::ptr::null());
            (ctx.fns.destroy_ycbcr_conversion)(ctx.device, conversion, core::ptr::null());
        };
        if result != vk::Result::SUCCESS {
            cleanup_base(1);
            return Err(format!("CreateDescriptorSetLayout {result:?}"));
        }

        let push_range = vk::PushConstantRange {
            stage_flags: vk::ShaderStageFlags::FRAGMENT,
            offset: 0,
            size: core::mem::size_of::<Push>() as u32,
        };
        let pipe_layout_ci = vk::PipelineLayoutCreateInfo {
            set_layout_count: 1,
            p_set_layouts: &set_layout,
            push_constant_range_count: 1,
            p_push_constant_ranges: &push_range,
            ..Default::default()
        };
        let mut pipe_layout = vk::PipelineLayout::null();
        let result = (ctx.fns.create_pipeline_layout)(
            ctx.device,
            &pipe_layout_ci,
            core::ptr::null(),
            &mut pipe_layout,
        );
        if result != vk::Result::SUCCESS {
            cleanup_base(2);
            return Err(format!("CreatePipelineLayout {result:?}"));
        }

        let pool_size = vk::DescriptorPoolSize {
            ty: vk::DescriptorType::COMBINED_IMAGE_SAMPLER,
            descriptor_count: DESC_RING,
        };
        let pool_ci = vk::DescriptorPoolCreateInfo {
            max_sets: DESC_RING,
            pool_size_count: 1,
            p_pool_sizes: &pool_size,
            ..Default::default()
        };
        let mut pool = vk::DescriptorPool::null();
        let result =
            (ctx.fns.create_descriptor_pool)(ctx.device, &pool_ci, core::ptr::null(), &mut pool);
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_pipeline_layout)(ctx.device, pipe_layout, core::ptr::null());
            cleanup_base(2);
            return Err(format!("CreateDescriptorPool {result:?}"));
        }

        let layouts = vec![set_layout; DESC_RING as usize];
        let alloc = vk::DescriptorSetAllocateInfo {
            descriptor_pool: pool,
            descriptor_set_count: DESC_RING,
            p_set_layouts: layouts.as_ptr(),
            ..Default::default()
        };
        let mut sets = vec![vk::DescriptorSet::null(); DESC_RING as usize];
        let result = (ctx.fns.allocate_descriptor_sets)(ctx.device, &alloc, sets.as_mut_ptr());
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_descriptor_pool)(ctx.device, pool, core::ptr::null());
            (ctx.fns.destroy_pipeline_layout)(ctx.device, pipe_layout, core::ptr::null());
            cleanup_base(2);
            return Err(format!("AllocateDescriptorSets {result:?}"));
        }

        Ok(ConvertObjects {
            conversion,
            sampler,
            set_layout,
            pipe_layout,
            pool,
            sets,
            next_set: 0,
            target: None,
        })
    }
}

/// The render pass and pipeline for an output of `format`. The image stays
/// in the layout Unity was told (`COLOR_ATTACHMENT_OPTIMAL`), since Unity
/// tracks it from there; every pixel is drawn, so nothing is loaded.
fn build_target(
    ctx: &VkCtx,
    pipe_layout: vk::PipelineLayout,
    format: vk::Format,
) -> Result<Target, String> {
    // SAFETY: object creation against the live device; every result is
    // checked, and what was built before a failure is destroyed.
    unsafe {
        let attachment = vk::AttachmentDescription {
            format,
            samples: vk::SampleCountFlags::TYPE_1,
            load_op: vk::AttachmentLoadOp::DONT_CARE,
            store_op: vk::AttachmentStoreOp::STORE,
            stencil_load_op: vk::AttachmentLoadOp::DONT_CARE,
            stencil_store_op: vk::AttachmentStoreOp::DONT_CARE,
            initial_layout: vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL,
            final_layout: vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL,
            ..Default::default()
        };
        let colour = vk::AttachmentReference {
            attachment: 0,
            layout: vk::ImageLayout::COLOR_ATTACHMENT_OPTIMAL,
        };
        let subpass = vk::SubpassDescription {
            pipeline_bind_point: vk::PipelineBindPoint::GRAPHICS,
            color_attachment_count: 1,
            p_color_attachments: &colour,
            ..Default::default()
        };
        let dependencies = [
            vk::SubpassDependency {
                src_subpass: vk::SUBPASS_EXTERNAL,
                dst_subpass: 0,
                src_stage_mask: vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                dst_stage_mask: vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                src_access_mask: vk::AccessFlags::empty(),
                dst_access_mask: vk::AccessFlags::COLOR_ATTACHMENT_WRITE,
                ..Default::default()
            },
            vk::SubpassDependency {
                src_subpass: 0,
                dst_subpass: vk::SUBPASS_EXTERNAL,
                src_stage_mask: vk::PipelineStageFlags::COLOR_ATTACHMENT_OUTPUT,
                dst_stage_mask: vk::PipelineStageFlags::FRAGMENT_SHADER,
                src_access_mask: vk::AccessFlags::COLOR_ATTACHMENT_WRITE,
                dst_access_mask: vk::AccessFlags::SHADER_READ,
                ..Default::default()
            },
        ];
        let pass_ci = vk::RenderPassCreateInfo {
            attachment_count: 1,
            p_attachments: &attachment,
            subpass_count: 1,
            p_subpasses: &subpass,
            dependency_count: dependencies.len() as u32,
            p_dependencies: dependencies.as_ptr(),
            ..Default::default()
        };
        let mut render_pass = vk::RenderPass::null();
        let result =
            (ctx.fns.create_render_pass)(ctx.device, &pass_ci, core::ptr::null(), &mut render_pass);
        if result != vk::Result::SUCCESS {
            return Err(format!("CreateRenderPass {result:?}"));
        }

        let module = |spv: &[u8]| -> Result<vk::ShaderModule, vk::Result> {
            let code: Vec<u32> = spv
                .as_chunks::<4>()
                .0
                .iter()
                .map(|b| u32::from_le_bytes(*b))
                .collect();
            let module_ci = vk::ShaderModuleCreateInfo {
                code_size: code.len() * 4,
                p_code: code.as_ptr(),
                ..Default::default()
            };
            let mut module = vk::ShaderModule::null();
            let result = (ctx.fns.create_shader_module)(
                ctx.device,
                &module_ci,
                core::ptr::null(),
                &mut module,
            );
            if result == vk::Result::SUCCESS {
                Ok(module)
            } else {
                Err(result)
            }
        };
        let vert = match module(include_bytes!("../../shaders/yuv_to_rgba.vert.spv")) {
            Ok(m) => m,
            Err(result) => {
                (ctx.fns.destroy_render_pass)(ctx.device, render_pass, core::ptr::null());
                return Err(format!("CreateShaderModule (vertex) {result:?}"));
            }
        };
        let frag = match module(include_bytes!("../../shaders/yuv_to_rgba.frag.spv")) {
            Ok(m) => m,
            Err(result) => {
                (ctx.fns.destroy_shader_module)(ctx.device, vert, core::ptr::null());
                (ctx.fns.destroy_render_pass)(ctx.device, render_pass, core::ptr::null());
                return Err(format!("CreateShaderModule (fragment) {result:?}"));
            }
        };

        let stages = [
            vk::PipelineShaderStageCreateInfo {
                stage: vk::ShaderStageFlags::VERTEX,
                module: vert,
                p_name: c"main".as_ptr(),
                ..Default::default()
            },
            vk::PipelineShaderStageCreateInfo {
                stage: vk::ShaderStageFlags::FRAGMENT,
                module: frag,
                p_name: c"main".as_ptr(),
                ..Default::default()
            },
        ];
        let vertex_input = vk::PipelineVertexInputStateCreateInfo::default();
        let input_assembly = vk::PipelineInputAssemblyStateCreateInfo {
            topology: vk::PrimitiveTopology::TRIANGLE_LIST,
            ..Default::default()
        };
        let viewport = vk::PipelineViewportStateCreateInfo {
            viewport_count: 1,
            scissor_count: 1,
            ..Default::default()
        };
        let rasterization = vk::PipelineRasterizationStateCreateInfo {
            polygon_mode: vk::PolygonMode::FILL,
            cull_mode: vk::CullModeFlags::NONE,
            front_face: vk::FrontFace::COUNTER_CLOCKWISE,
            line_width: 1.0,
            ..Default::default()
        };
        let multisample = vk::PipelineMultisampleStateCreateInfo {
            rasterization_samples: vk::SampleCountFlags::TYPE_1,
            ..Default::default()
        };
        let blend_attachment = vk::PipelineColorBlendAttachmentState {
            color_write_mask: vk::ColorComponentFlags::RGBA,
            ..Default::default()
        };
        let blend = vk::PipelineColorBlendStateCreateInfo {
            attachment_count: 1,
            p_attachments: &blend_attachment,
            ..Default::default()
        };
        let dynamic_states = [vk::DynamicState::VIEWPORT, vk::DynamicState::SCISSOR];
        let dynamic = vk::PipelineDynamicStateCreateInfo {
            dynamic_state_count: dynamic_states.len() as u32,
            p_dynamic_states: dynamic_states.as_ptr(),
            ..Default::default()
        };
        let pipeline_ci = vk::GraphicsPipelineCreateInfo {
            stage_count: stages.len() as u32,
            p_stages: stages.as_ptr(),
            p_vertex_input_state: &vertex_input,
            p_input_assembly_state: &input_assembly,
            p_viewport_state: &viewport,
            p_rasterization_state: &rasterization,
            p_multisample_state: &multisample,
            p_color_blend_state: &blend,
            p_dynamic_state: &dynamic,
            layout: pipe_layout,
            render_pass,
            subpass: 0,
            ..Default::default()
        };
        let mut pipeline = vk::Pipeline::null();
        let result = (ctx.fns.create_graphics_pipelines)(
            ctx.device,
            vk::PipelineCache::null(),
            1,
            &pipeline_ci,
            core::ptr::null(),
            &mut pipeline,
        );
        (ctx.fns.destroy_shader_module)(ctx.device, frag, core::ptr::null());
        (ctx.fns.destroy_shader_module)(ctx.device, vert, core::ptr::null());
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_render_pass)(ctx.device, render_pass, core::ptr::null());
            return Err(format!("CreateGraphicsPipelines {result:?}"));
        }
        Ok(Target {
            format,
            render_pass,
            pipeline,
        })
    }
}

#[allow(clippy::too_many_arguments)]
fn import_buffer(
    ctx: &VkCtx,
    buffer: *mut core::ffi::c_void,
    props: &vk::AndroidHardwareBufferPropertiesANDROID<'_>,
    fmt_props: &vk::AndroidHardwareBufferFormatPropertiesANDROID<'_>,
    conversion: vk::SamplerYcbcrConversion,
    width: u32,
    height: u32,
    conv_gen: u64,
) -> Result<Imported, String> {
    let external = fmt_props.format == vk::Format::UNDEFINED;
    // SAFETY: the import sequence is external-format image,
    // dedicated allocation importing the AHB (which takes its own AHB
    // reference), bind, then a view carrying the conversion. All results
    // checked; partial objects destroyed on error.
    unsafe {
        let img_ext_format = vk::ExternalFormatANDROID {
            external_format: fmt_props.external_format,
            ..Default::default()
        };
        let ext_mem = vk::ExternalMemoryImageCreateInfo {
            p_next: if external {
                (&raw const img_ext_format).cast()
            } else {
                core::ptr::null()
            },
            handle_types: vk::ExternalMemoryHandleTypeFlags::ANDROID_HARDWARE_BUFFER_ANDROID,
            ..Default::default()
        };
        let img_ci = vk::ImageCreateInfo {
            p_next: (&raw const ext_mem).cast(),
            image_type: vk::ImageType::TYPE_2D,
            format: fmt_props.format,
            extent: vk::Extent3D {
                width,
                height,
                depth: 1,
            },
            mip_levels: 1,
            array_layers: 1,
            samples: vk::SampleCountFlags::TYPE_1,
            tiling: vk::ImageTiling::OPTIMAL,
            usage: vk::ImageUsageFlags::SAMPLED,
            sharing_mode: vk::SharingMode::EXCLUSIVE,
            initial_layout: vk::ImageLayout::UNDEFINED,
            ..Default::default()
        };
        let mut image = vk::Image::null();
        let result = (ctx.fns.create_image)(ctx.device, &img_ci, core::ptr::null(), &mut image);
        if result != vk::Result::SUCCESS {
            return Err(format!("CreateImage {result:?}"));
        }

        let import_info = vk::ImportAndroidHardwareBufferInfoANDROID {
            buffer: buffer.cast(),
            ..Default::default()
        };
        // No usable type would make `trailing_zeros` 32, an index past the
        // driver's list.
        if props.memory_type_bits == 0 {
            (ctx.fns.destroy_image)(ctx.device, image, core::ptr::null());
            return Err("no memory type can import this buffer".into());
        }
        let dedicated = vk::MemoryDedicatedAllocateInfo {
            p_next: (&raw const import_info).cast(),
            image,
            ..Default::default()
        };
        let alloc = vk::MemoryAllocateInfo {
            p_next: (&raw const dedicated).cast(),
            allocation_size: props.allocation_size,
            memory_type_index: props.memory_type_bits.trailing_zeros(),
            ..Default::default()
        };
        let mut memory = vk::DeviceMemory::null();
        let result = (ctx.fns.allocate_memory)(ctx.device, &alloc, core::ptr::null(), &mut memory);
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_image)(ctx.device, image, core::ptr::null());
            return Err(format!("AllocateMemory (import) {result:?}"));
        }
        let result = (ctx.fns.bind_image_memory)(ctx.device, image, memory, 0);
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_image)(ctx.device, image, core::ptr::null());
            (ctx.fns.free_memory)(ctx.device, memory, core::ptr::null());
            return Err(format!("BindImageMemory {result:?}"));
        }

        let conv_info = vk::SamplerYcbcrConversionInfo {
            conversion,
            ..Default::default()
        };
        let view_ci = vk::ImageViewCreateInfo {
            p_next: (&raw const conv_info).cast(),
            image,
            view_type: vk::ImageViewType::TYPE_2D,
            format: fmt_props.format,
            subresource_range: vk::ImageSubresourceRange {
                aspect_mask: vk::ImageAspectFlags::COLOR,
                base_mip_level: 0,
                level_count: 1,
                base_array_layer: 0,
                layer_count: 1,
            },
            ..Default::default()
        };
        let mut view = vk::ImageView::null();
        let result =
            (ctx.fns.create_image_view)(ctx.device, &view_ci, core::ptr::null(), &mut view);
        if result != vk::Result::SUCCESS {
            (ctx.fns.destroy_image)(ctx.device, image, core::ptr::null());
            (ctx.fns.free_memory)(ctx.device, memory, core::ptr::null());
            return Err(format!("CreateImageView {result:?}"));
        }

        Ok(Imported {
            image,
            memory,
            view,
            width,
            height,
            conv_gen,
            last_used: 0,
            last_draw: 0,
        })
    }
}

/// Destroy graveyard entries whose frames are provably retired, with no
/// session involved.
///
/// `SessionRenderer::render` collects as part of its own pass, but only a
/// live session reaches it. After the last session closes nothing would run
/// the collector, and its buried view would outlive the image it was made
/// over. The caller issues this drain for that case.
///
/// Silent where the device context or the recording state is absent,
/// except for one line the first time the recording state is missing: the
/// drain then destroyed nothing, while its caller goes on to release the
/// texture regardless.
pub fn drain_graveyard() {
    let mut guard = CTX.lock().unwrap_or_else(|e| e.into_inner());
    let Some(ctx) = guard.as_mut() else {
        return;
    };
    // SAFETY: Unity vtable call on the render thread, the documented call
    // site. As in the render pass, the query and a non-null command buffer
    // are how Unity signals the call is inside a render event; the frame
    // numbers below mean nothing anywhere else.
    let recording = unsafe {
        let mut state = core::mem::zeroed::<unity::UnityVulkanRecordingState>();
        let recording_state = (*ctx.vulkan_iface).command_recording_state;
        if !recording_state.is_some_and(|f| f(&mut state, unity::QUEUE_ACCESS_DONT_CARE))
            || state.command_buffer == vk::CommandBuffer::null()
        {
            if !WARNED_NO_RECORDING.swap(true, Ordering::Relaxed) {
                unity::log(
                    "collect: no command recording state at the event's call site — retired views cannot be destroyed",
                );
            }
            return;
        }
        state
    };
    graveyard::collect(
        ctx,
        recording.current_frame_number,
        recording.safe_frame_number,
    );
}

static WARNED_NO_RECORDING: AtomicBool = AtomicBool::new(false);

mod graveyard {
    //! Vulkan objects whose owner (a closing session) cannot prove GPU
    //! quiescence, parked here and destroyed by later render events once
    //! `safeFrameNumber` passes the burial frame. If no render event ever
    //! runs again they leak until process end, which is bounded and better
    //! than destroying in-flight resources.

    use std::sync::Mutex;

    use super::{Retired, VkCtx};

    static GRAVE: Mutex<Vec<(Option<u64>, Vec<Retired>)>> = Mutex::new(Vec::new());

    /// Reached from `SessionRenderer::drop`, so it must not panic: a
    /// destructor unwinding while another panic is in flight aborts the
    /// process rather than meeting the ABI's fences.
    pub fn bury(items: Vec<Retired>) {
        if items.is_empty() {
            return;
        }
        GRAVE
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .push((None, items));
    }

    /// Called from render events: stamp new burials with the current
    /// frame, destroy those safely past.
    pub fn collect(ctx: &VkCtx, current_frame: u64, safe_frame: u64) {
        // The destruction below is the panic-capable half, so it runs
        // after the guard is released: nothing can poison the lock that
        // `bury` has to take from a destructor.
        let due: Vec<Retired> = {
            let mut grave = GRAVE.lock().unwrap_or_else(|e| e.into_inner());
            for (stamp, _) in grave.iter_mut() {
                stamp.get_or_insert(current_frame);
            }
            let mut due = Vec::new();
            let mut index = 0;
            while index < grave.len() {
                if grave[index].0.is_some_and(|stamp| stamp <= safe_frame) {
                    due.extend(grave.swap_remove(index).1);
                } else {
                    index += 1;
                }
            }
            due
        };
        for item in due {
            item.destroy(ctx);
        }
    }
}
