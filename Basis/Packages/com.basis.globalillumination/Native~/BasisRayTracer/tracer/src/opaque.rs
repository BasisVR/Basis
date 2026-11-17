use basis_rt_protocol::*;

pub const MATERIAL_KNOWN_OPAQUE: u32 = 16;
pub const HEAP_ALPHA_MIN: u32 = 11;
pub const ALPHA_MIN_BYTES: u64 = MAX_TEXTURES as u64 * 4;

pub fn known_opaque(m: &Material, texture_min: impl Fn(u32) -> Option<f32>) -> bool {
    if m.flags & MATERIAL_TILE_DISCARD != 0 { return false; }
    let mode = m.alpha_mode & 3;
    if mode == ALPHA_OPAQUE { return true; }
    let mut alpha = m.base_color[3];
    if !(alpha >= 0.0) { return false; }
    if m.base_texture >= 0 { let Some(t) = texture_min(m.base_texture as u32) else { return false; }; alpha *= t; }
    alpha >= m.cutoff && (mode == ALPHA_CUTOUT || alpha >= 1.0)
}

#[derive(Default)]
pub struct Opaque { pub materials: Vec<bool>, pub pending: Option<(u64, Vec<(u32, u32)>)>, pub forced: u32, pub off: Option<bool> }

impl Opaque {
    pub unsafe fn classify(&mut self, src: &Slot, count: usize, mapped: *mut u8, texture_min: impl Fn(u32) -> Option<f32>) {
        let off = *self.off.get_or_insert_with(|| std::env::var_os("BASIS_RT_NO_OPAQUE_DETECT").is_some());
        self.materials.clear();
        self.materials.resize(count, true);
        self.forced = 0;
        for (i, m) in src.materials[..count].iter().enumerate() {
            if m.alpha_mode & 3 == ALPHA_OPAQUE && m.flags & MATERIAL_TILE_DISCARD == 0 { continue; }
            let known = !off && known_opaque(m, &texture_min);
            let p = unsafe { mapped.add(i * 96 + 88) } as *mut u32;
            unsafe { p.write_unaligned((p.read_unaligned() & !MATERIAL_KNOWN_OPAQUE) | if known { MATERIAL_KNOWN_OPAQUE } else { 0 }); }
            self.materials[i] = known;
        }
    }

    pub fn instance(&mut self, material_base: u32, submeshes: impl Iterator<Item = u32>) -> bool {
        if self.off == Some(true) { return false; }
        for (g, flags) in submeshes.enumerate() {
            if flags & SUBMESH_OPAQUE != 0 { continue; }
            if self.materials.get(material_base as usize + g) != Some(&true) { return false; }
        }
        self.forced += 1;
        true
    }
}
