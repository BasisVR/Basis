use std::sync::atomic::{AtomicU32, AtomicU64};

pub const MAGIC: u32 = 0x3154_5242;
pub const VERSION: u32 = 3;
pub const RING: usize = 8;
pub const MAX_CONTEXTS: usize = 6;
pub const MAX_INSTANCES: usize = 16384;
pub const MAX_MATERIALS: usize = 8192;
pub const MAX_LIGHTS: usize = 64;
pub const MAX_COMMANDS: usize = 8192;
pub const MAX_EMITTERS: usize = 2048;
pub const MAX_DIRTY: usize = 512;
pub const UPLOAD_WIDTH: u32 = 2048;
pub const UPLOAD_HEIGHT: u32 = 512;
pub const UPLOAD_TEXELS: u32 = UPLOAD_WIDTH * UPLOAD_HEIGHT;
pub const UPLOAD_PHYSICAL_HEIGHT: u32 = UPLOAD_HEIGHT * 2;
pub const VERTEX_TEXELS: u32 = 2;
pub const MAX_TEXTURE_SIZE: u32 = 1024;
pub const MAX_TEXTURES: u32 = 4096;
pub const MAX_MESHES: u32 = 16384;

pub const STATUS_STARTING: u32 = 0;
pub const STATUS_READY: u32 = 1;
pub const STATUS_FAILED: u32 = 2;
pub const STATUS_EXITED: u32 = 3;

pub const API_D3D11: u32 = 11;
pub const API_D3D12: u32 = 12;
pub const API_VULKAN: u32 = 21;

pub const TRACER_DXR: u32 = 1;
pub const TRACER_VULKAN: u32 = 2;

pub const INTEROP_D3D12: u32 = 1;
pub const INTEROP_VULKAN_WIN32: u32 = 2;
pub const INTEROP_VULKAN_FD: u32 = 3;

pub const SHARED_FORMAT_UINT: i32 = 95;
pub const SHARED_FORMAT_FLOAT: i32 = 97;
pub const SHARED_USAGE: u32 = 0xF;

pub const CAP_SER: u32 = 1;
pub const CAP_SER_REORDERS: u32 = 2;
pub const CAP_OMM: u32 = 4;
pub const CAP_TIER_1_2: u32 = 8;
pub const CAP_DIRECT: u32 = 16;

pub const RESERVED_UPLOAD_MARK: usize = 26;
pub const RESERVED_UPLOAD_LOST: usize = 28;
pub const RESERVED_UPLOAD_TRACKED: usize = 29;
pub const OP_MESH_DEFINE: u32 = 1;
pub const OP_MESH_SUBMESH: u32 = 2;
pub const OP_MESH_VERTICES: u32 = 3;
pub const OP_MESH_INDICES: u32 = 4;
pub const OP_MESH_COMMIT: u32 = 5;
pub const OP_MESH_REFIT: u32 = 6;
pub const OP_MESH_RELEASE: u32 = 7;
pub const OP_TEXTURE_DEFINE: u32 = 8;
pub const OP_TEXTURE_PIXELS: u32 = 9;
pub const OP_TEXTURE_COMMIT: u32 = 10;
pub const OP_TEXTURE_RELEASE: u32 = 11;
pub const OP_MESH_POSITIONS: u32 = 12;

pub const SUBMESH_OPAQUE: u32 = 1;
pub const SUBMESH_ALPHA_TEST: u32 = 2;
pub const SUBMESH_BLEND: u32 = 4;
pub const TEXTURE_HDR: u32 = 1;
pub const MESH_DYNAMIC: u32 = 1;

pub const ALPHA_OPAQUE: u32 = 0;
pub const ALPHA_CUTOUT: u32 = 1;
pub const ALPHA_BLEND: u32 = 2;

pub const MATERIAL_DOUBLE_SIDED: u32 = 1;
pub const MATERIAL_UNLIT: u32 = 2;
pub const MATERIAL_ANIMATED: u32 = 4;
pub const MATERIAL_EMITTER: u32 = 8;

pub const INSTANCE_AVATAR: u32 = 1;
pub const INSTANCE_NO_SHADOW: u32 = 2;
pub const INSTANCE_EMITTER: u32 = 4;
pub const INSTANCE_DYNAMIC: u32 = 8;
pub const MASK_DYNAMIC: u32 = 2;

pub const LIGHT_DIRECTIONAL: u32 = 0;
pub const LIGHT_POINT: u32 = 1;
pub const LIGHT_SPOT: u32 = 2;

pub const FLAG_RESET_HISTORY: u32 = 1;
pub const FLAG_SCREEN_REUSE: u32 = 2;
pub const FLAG_SHADOWS: u32 = 4;
pub const FLAG_DENOISE: u32 = 8;
pub const FLAG_SAME_FRAME: u32 = 16;
pub const FLAG_CACHE_RESET: u32 = 64;
pub const FLAG_PROBES: u32 = 128;
pub const FLAG_BAKED_HYBRID: u32 = 256;
pub const FLAG_MOTION: u32 = 512;

#[repr(C)]
pub struct Header {
    pub magic: u32,
    pub version: u32,
    pub tracer_pid: AtomicU32,
    pub status: AtomicU32,
    pub unity_pid: u32,
    pub api: u32,
    pub luid_low: u32,
    pub luid_high: i32,
    pub generation: AtomicU32,
    pub caps: u32,
    pub handle_upload: [u64; 2],
    pub handle_fence_unity: u64,
    pub handle_fence_tracer: u64,
    pub frame_requested: AtomicU64,
    pub frame_queued: AtomicU64,
    pub frame_submitted: AtomicU64,
    pub frame_consumed: AtomicU64,
    pub heartbeat: AtomicU64,
    pub gpu_time_us: AtomicU32,
    pub build_time_us: AtomicU32,
    pub mesh_count: AtomicU32,
    pub texture_count: AtomicU32,
    pub vram_mb: AtomicU32,
    pub reserved: [u32; 31],
    pub contexts: [ContextHeader; MAX_CONTEXTS],
    pub error: [u8; 512],
    pub tracer_api: u32,
    pub interop: u32,
    pub device_uuid: [u8; 16],
    pub targets: [Targets; MAX_CONTEXTS],
}

#[repr(C)]
pub struct Targets {
    pub handles: [u64; 4],
    pub generation: AtomicU32,
    pub opened: AtomicU32,
    pub width: u32,
    pub height: u32,
    pub views: u32,
    pub pad: [u32; 3],
}

#[repr(C)]
pub struct ContextHeader {
    pub requested_width: AtomicU32,
    pub requested_height: AtomicU32,
    pub requested_views: AtomicU32,
    pub generation: AtomicU32,
    pub width: u32,
    pub height: u32,
    pub views: u32,
    pub pad: u32,
    pub handle_gbuffer: [u64; 2],
    pub handle_output: [u64; 2],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct View {
    pub view: [f32; 16],
    pub projection: [f32; 16],
    pub inverse_view_projection: [f32; 16],
    pub previous_view_projection: [f32; 16],
    pub position: [f32; 4],
}

#[repr(C)]
#[derive(Clone, Copy)]
pub struct FrameParams {
    pub frame: u64,
    pub instance_count: u32,
    pub material_count: u32,
    pub light_count: u32,
    pub command_count: u32,
    pub upload_texels: u32,
    pub flags: u32,
    pub width: u32,
    pub height: u32,
    pub view_count: u32,
    pub samples: u32,
    pub bounces: u32,
    pub max_distance: f32,
    pub ao_radius: f32,
    pub emission_scale: f32,
    pub sky_intensity: f32,
    pub time: f32,
    pub firefly_clamp: f32,
    pub temporal_response: f32,
    pub light_intensity: f32,
    pub normal_bias: f32,
    pub emitter_count: u32,
    pub ao_intensity: f32,
    pub context: u32,
    pub parity: u32,
    pub cache_cell_size: f32,
    pub dirty_count: u32,
    pub ambient: [[f32; 4]; 7],
    pub views: [View; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Instance {
    pub transform: [f32; 12],
    pub mesh: u32,
    pub material_base: u32,
    pub mask: u32,
    pub flags: u32,
    pub lightmap_st: [f32; 4],
    pub lightmap: i32,
    pub previous: u32,
    pub pad: [u32; 2],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Material {
    pub base_color: [f32; 4],
    pub emission: [f32; 4],
    pub base_st: [f32; 4],
    pub emission_st: [f32; 4],
    pub base_texture: i32,
    pub emission_texture: i32,
    pub alpha_mode: u32,
    pub cutoff: f32,
    pub metallic: f32,
    pub emission2: u32,
    pub flags: u32,
    pub emission_base: f32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Light {
    pub position: [f32; 4],
    pub direction: [f32; 4],
    pub color: [f32; 4],
    pub spot: [f32; 4],
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Command {
    pub op: u32,
    pub id: u32,
    pub a: u32,
    pub b: u32,
    pub c: u32,
    pub d: u32,
    pub e: u32,
    pub f: u32,
}

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct Emitter {
    pub instance: u32,
    pub geometry: u32,
    pub triangle_count: u32,
    pub cdf: f32,
}

#[repr(C)]
pub struct Slot {
    pub params: FrameParams,
    pub instances: [Instance; MAX_INSTANCES],
    pub materials: [Material; MAX_MATERIALS],
    pub lights: [Light; MAX_LIGHTS],
    pub commands: [Command; MAX_COMMANDS],
    pub emitters: [Emitter; MAX_EMITTERS],
    pub dirty: [[f32; 4]; MAX_DIRTY],
}

pub const HEADER_BYTES: usize = 4096;
pub const SLOT_BYTES: usize = (std::mem::size_of::<Slot>() + 4095) & !4095;
pub const MAPPING_BYTES: usize = HEADER_BYTES + SLOT_BYTES * RING;

const _: () = assert!(std::mem::size_of::<Header>() <= HEADER_BYTES);
const _: () = assert!(std::mem::size_of::<ContextHeader>() == 64);
const _: () = assert!(std::mem::size_of::<Targets>() == 64);
const _: () = assert!(std::mem::size_of::<Instance>() == 96);
const _: () = assert!(std::mem::size_of::<Material>() == 96);
const _: () = assert!(std::mem::size_of::<Light>() == 64);
const _: () = assert!(std::mem::size_of::<Command>() == 32);
const _: () = assert!(std::mem::size_of::<View>() == 272);
const _: () = assert!(std::mem::size_of::<FrameParams>() == 224 + 544);

#[derive(Default, Clone, Copy)]
pub struct IdHasher(u64);
impl std::hash::Hasher for IdHasher {
    fn finish(&self) -> u64 { self.0 }
    fn write(&mut self, bytes: &[u8]) { for b in bytes { self.0 = (self.0 ^ *b as u64).wrapping_mul(0x0000_0100_0000_01b3); } }
    fn write_u32(&mut self, v: u32) { self.0 = (v as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15); }
}
pub type IdMap<V> = std::collections::HashMap<u32, V, std::hash::BuildHasherDefault<IdHasher>>;

pub fn mapping_name(unity_pid: u32) -> String { format!("Local\\BasisRayTracer.{unity_pid}") }
pub fn wake_tracer_name(unity_pid: u32) -> String { format!("Local\\BasisRayTracer.{unity_pid}.wake") }

pub unsafe fn header<'a>(base: *mut u8) -> &'a Header { unsafe { &*(base as *const Header) } }
pub unsafe fn slot<'a>(base: *mut u8, index: usize) -> *mut Slot { unsafe { base.add(HEADER_BYTES + SLOT_BYTES * (index % RING)) as *mut Slot } }

pub fn write_error(header: &Header, message: &str) {
    let bytes = message.as_bytes();
    let n = bytes.len().min(511);
    let dst = header.error.as_ptr() as *mut u8;
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), dst, n);
        *dst.add(n) = 0;
    }
}
