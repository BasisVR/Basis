use windows::core::BOOL;

#[repr(C)]
#[derive(Default)]
pub struct Options22 {
    pub ser_actually_reorders: BOOL,
    pub create_byte_offset_views: BOOL,
    pub max_1d_dispatch: u32,
    pub max_1d_dispatch_mesh: u32,
}

#[repr(C)]
pub struct PipelineConfig1 {
    pub max_trace_recursion_depth: u32,
    pub flags: u32,
}

pub const SUBOBJECT_RAYTRACING_PIPELINE_CONFIG1: i32 = 12;
pub const PIPELINE_FLAG_ALLOW_OPACITY_MICROMAPS: u32 = 0x400;
pub const GEOMETRY_TYPE_OMM_TRIANGLES: i32 = 2;
pub const AS_TYPE_OPACITY_MICROMAP_ARRAY: i32 = 2;
pub const OMM_FORMAT_OC1_2_STATE: u32 = 1;
pub const OMM_FORMAT_OC1_4_STATE: u32 = 2;
pub const OMM_ARRAY_BYTE_ALIGNMENT: u64 = 128;

#[repr(C)]
#[derive(Clone, Copy)]
pub struct OmmDesc {
    pub byte_offset: u32,
    pub subdivision_and_format: u32,
}

#[repr(C)]
pub struct OmmHistogramEntry {
    pub count: u32,
    pub subdivision_level: u32,
    pub format: u32,
}

#[repr(C)]
pub struct AddressAndStride {
    pub address: u64,
    pub stride: u64,
}

#[repr(C)]
pub struct OmmArrayDesc {
    pub histogram_count: u32,
    pub histogram: *const OmmHistogramEntry,
    pub input_buffer: u64,
    pub per_omm_descs: AddressAndStride,
}

#[repr(C)]
pub struct OmmLinkage {
    pub index_buffer: AddressAndStride,
    pub index_format: i32,
    pub base_location: u32,
    pub array: u64,
}

#[repr(C)]
pub struct OmmTrianglesDesc {
    pub triangles: *const windows::Win32::Graphics::Direct3D12::D3D12_RAYTRACING_GEOMETRY_TRIANGLES_DESC,
    pub linkage: *const OmmLinkage,
}

#[repr(C)]
pub struct GeometryDescOmm {
    pub kind: i32,
    pub flags: i32,
    pub omm: OmmTrianglesDesc,
    pub pad: [u64; 4],
}

const _: () = assert!(std::mem::size_of::<GeometryDescOmm>() == std::mem::size_of::<windows::Win32::Graphics::Direct3D12::D3D12_RAYTRACING_GEOMETRY_DESC>());

#[repr(C)]
pub struct BuildInputsOmm {
    pub kind: i32,
    pub flags: i32,
    pub num_descs: u32,
    pub layout: i32,
    pub desc: *const OmmArrayDesc,
}
