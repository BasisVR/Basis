use basis_rt_protocol::*;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Instant;

pub const HEAP_LIGHT_GRID: u32 = 256;
pub const GRID_X: usize = 32;
pub const GRID_Y: usize = 16;
pub const GRID_Z: usize = 32;
pub const GRID_CELLS: usize = GRID_X * GRID_Y * GRID_Z;
pub const GRID_LEVELS: usize = 2;
const LEVEL_CELL: [f32; GRID_LEVELS] = [2.0, 16.0];
const LEVEL_DIMS: [[usize; 3]; GRID_LEVELS] = [[GRID_X, GRID_Y, GRID_Z], [16, 8, 16]];
const LEVEL_BLOCKS: [usize; GRID_LEVELS] = [blocks(LEVEL_DIMS[0]), blocks(LEVEL_DIMS[1])];
const UNITS: usize = LEVEL_BLOCKS[0] + LEVEL_BLOCKS[1];
const TOTAL_CELLS: usize = UNITS * BLOCK_CELLS;
const fn blocks(d: [usize; 3]) -> usize { (d[0] / BLOCK) * (d[1] / BLOCK) * (d[2] / BLOCK) }
const K_LIGHTS: usize = 8;
const K_ALL: usize = 12;
const K_NEAR: usize = 8;
const K_FAR: usize = 8;
const K: usize = K_LIGHTS + K_ALL + K_NEAR + K_FAR;
pub const GRID_HEADER_BYTES: usize = 64;
pub const BOUNDS_CAPACITY: usize = 65536;
const GRID_MIN_SOURCES: usize = 5;
const ENTRY_CAPACITY: usize = TOTAL_CELLS * K;
pub const GRID_BYTES: u64 = (GRID_HEADER_BYTES + TOTAL_CELLS * 8 + ENTRY_CAPACITY * 4) as u64;
const BLOCK: usize = 4;
const BLOCK_CELLS: usize = BLOCK * BLOCK * BLOCK;
const CDF_ONE: u32 = 1 << 20;
const EMITTER_ID: u32 = MAX_LIGHTS as u32;
const COMPLETE_LIGHTS: u32 = 1 << 22;
const COMPLETE_ALL: u32 = 1 << 23;
const COMPLETE_NEAR: u32 = 1 << 24;
const COMPLETE_FAR: u32 = 1 << 25;
const SHARED_LIST: u32 = 1 << 26;
const STABLE_FRAMES: u64 = 16;
const _: () = assert!(GRID_CELLS == LEVEL_BLOCKS[0] * BLOCK_CELLS && LEVEL_DIMS[1][0] % BLOCK == 0 && LEVEL_DIMS[1][1] % BLOCK == 0 && LEVEL_DIMS[1][2] % BLOCK == 0 && MAX_LIGHTS + MAX_EMITTERS <= 4096 && K_LIGHTS < 16 && K_ALL < 32 && K_NEAR < 32 && K_FAR < 32 && ENTRY_CAPACITY < 1 << 22);

#[derive(Clone, Copy, Default)]
struct PointSource { position: [f32; 3], range: f32, power: f32, directional: bool, spot: bool, axis: [f32; 3], cos_outer: f32 }

#[derive(Clone, Copy, Default)]
struct BoxSource { lo: [f32; 3], hi: [f32; 3], safe_lo: [f32; 3], safe_hi: [f32; 3], power: f32 }

#[derive(Clone, Copy, Default)]
struct Level { origin: [f32; 3], cell: f32, skip_lo: [f32; 3], skip_hi: [f32; 3], skip_inside: bool, split: bool, off: bool }

#[derive(Default)]
struct Inputs { levels: [Level; GRID_LEVELS], views: Vec<[f32; 3]>, near_scale: f32, near_span: f32, baked: bool, lights: Vec<PointSource>, emitters: Vec<BoxSource>, positive_emitters: usize, exact_split: bool, out: usize }

#[derive(Default)]
struct BlockOut { header: Vec<u32>, entries: Vec<u32> }

struct Shared { inputs: std::cell::UnsafeCell<Inputs>, cursor: AtomicUsize, next: AtomicUsize, active: AtomicUsize, done_at: AtomicU64, epoch: Instant, quit: AtomicBool, wake: Mutex<u64>, start: Condvar, done: Mutex<()>, finished: Condvar }
unsafe impl Sync for Shared {}
unsafe impl Send for Shared {}

pub struct LightGrid {
    shared: Arc<Shared>,
    workers: Vec<std::thread::JoinHandle<()>>,
    enabled: bool,
    alternate: bool,
    coarse_only: bool,
    running: bool,
    begun_at: u64,
    bounds: Vec<[f32; 6]>,
    changed: Vec<u64>,
    pub build_us: u32,
    pub wait_us: u32,
    pub used_bytes: u64,
}

fn luminance(c: &[f32; 4]) -> f32 { (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]).max(0.0) }

fn point_box_distance2(p: &[f32; 3], lo: &[f32; 3], hi: &[f32; 3]) -> f32 {
    let mut d = 0.0;
    for a in 0..3 { let v = (lo[a] - p[a]).max(p[a] - hi[a]).max(0.0); d += v * v; }
    d
}

fn point_box_far2(p: &[f32; 3], lo: &[f32; 3], hi: &[f32; 3]) -> f32 {
    let mut d = 0.0;
    for a in 0..3 { let v = (p[a] - lo[a]).abs().max((hi[a] - p[a]).abs()); d += v * v; }
    d
}

fn box_box_distance2(alo: &[f32; 3], ahi: &[f32; 3], blo: &[f32; 3], bhi: &[f32; 3]) -> f32 {
    let mut d = 0.0;
    for a in 0..3 { let v = (blo[a] - ahi[a]).max(alo[a] - bhi[a]).max(0.0); d += v * v; }
    d
}

fn box_box_far2(alo: &[f32; 3], ahi: &[f32; 3], blo: &[f32; 3], bhi: &[f32; 3]) -> f32 {
    let mut d = 0.0;
    for a in 0..3 { let v = (ahi[a] - blo[a]).abs().max((bhi[a] - alo[a]).abs()); d += v * v; }
    d
}

fn decode_bound(bits: u32) -> f32 { f32::from_bits(if bits & 0x8000_0000 != 0 { bits & 0x7fff_ffff } else { !bits }) }

impl PointSource {
    fn reaches(&self, lo: &[f32; 3], hi: &[f32; 3], cone: bool, near: f32) -> Option<f32> {
        if self.directional { return Some(self.power); }
        let d2 = point_box_distance2(&self.position, lo, hi);
        let r2 = self.range * self.range;
        if d2 >= r2 { return None; }
        if cone && self.spot {
            let c = [(lo[0] + hi[0]) * 0.5 - self.position[0], (lo[1] + hi[1]) * 0.5 - self.position[1], (lo[2] + hi[2]) * 0.5 - self.position[2]];
            let r = 0.5 * ((hi[0] - lo[0]).powi(2) + (hi[1] - lo[1]).powi(2) + (hi[2] - lo[2]).powi(2)).sqrt();
            let dist = (c[0] * c[0] + c[1] * c[1] + c[2] * c[2]).sqrt();
            if dist > r * 1.001 + 1e-3 && self.cos_outer > -1.0 {
                let cos_axis = ((c[0] * self.axis[0] + c[1] * self.axis[1] + c[2] * self.axis[2]) / dist).clamp(-1.0, 1.0);
                let theta = cos_axis.acos();
                let alpha = (r / dist).min(1.0).asin();
                if theta - alpha > self.cos_outer.clamp(-1.0, 1.0).acos() + 1e-3 { return None; }
            }
        }
        let x2 = d2 / r2;
        let falloff = (1.0 - x2 * x2).max(0.0);
        Some(self.power * falloff * falloff / d2.max(near * near))
    }
}

impl Inputs {
    fn near_radius(&self, distance: f32) -> f32 { if self.baked { f32::INFINITY } else { self.near_scale * (1.0 + distance.max(0.0) / self.near_span) } }

    fn near_range(&self, lo: &[f32; 3], hi: &[f32; 3]) -> (f32, f32) {
        let (mut dmin, mut dmax) = (f32::INFINITY, 0.0f32);
        for v in &self.views { dmin = dmin.min(point_box_distance2(v, lo, hi).sqrt()); dmax = dmax.max(point_box_far2(v, lo, hi).sqrt()); }
        ((self.near_radius(dmin - 0.05) * 0.99 - 0.01).max(0.0), self.near_radius(dmax + 0.05) * 1.01 + 0.01)
    }
}

fn write_list(entries: &mut Vec<u32>, list: &[(f32, u32)]) {
    let n = list.len();
    if n == 0 { return; }
    let total: f32 = list.iter().map(|x| x.0).sum();
    let scale = CDF_ONE as f32 / total.max(1e-30);
    let mut running = 0.0f32;
    let mut previous = 0u32;
    for (i, (w, id)) in list.iter().enumerate() {
        running += *w;
        let target = if i == n - 1 { CDF_ONE } else { (running * scale + 0.5) as u32 };
        let v = target.max(previous + 1).min(CDF_ONE - (n - 1 - i) as u32);
        entries.push(*id | ((v - 1) << 12));
        previous = v;
    }
}

fn top(list: &mut Vec<(f32, u32)>, keep: usize) {
    if list.len() > keep {
        if keep == 0 { list.clear(); return; }
        list.select_nth_unstable_by(keep - 1, |a, b| b.0.total_cmp(&a.0));
        list.truncate(keep);
    }
}

#[derive(Default)]
struct Scratch { ids: Vec<u32>, mask: Vec<u8>, ulo: [Vec<f32>; 3], uhi: [Vec<f32>; 3], slo: [Vec<f32>; 3], shi: [Vec<f32>; 3], power: Vec<f32>, d2: Vec<f32>, smin2: Vec<f32>, smax2: Vec<f32>, lights: Vec<u32>, all: Vec<(f32, u32)>, near: Vec<(f32, u32)>, far: Vec<(f32, u32)>, cell_lights: Vec<(f32, u32)>, cell_light_count: Vec<(u8, bool)>, list: Vec<(f32, u32)> }

fn build_block(inputs: &Inputs, unit: usize, t: &mut Scratch, out: &mut BlockOut) -> bool {
    let li = if unit < LEVEL_BLOCKS[0] { 0 } else { 1 };
    let level = &inputs.levels[li];
    let block = unit - if li == 0 { 0 } else { LEVEL_BLOCKS[0] };
    let (bnx, bny) = (LEVEL_DIMS[li][0] / BLOCK, LEVEL_DIMS[li][1] / BLOCK);
    let cell = level.cell;
    let near = 0.5 * cell;
    let bx = block % bnx;
    let by = (block / bnx) % bny;
    let bz = block / (bnx * bny);
    let size = cell * BLOCK as f32;
    let blo = [level.origin[0] + bx as f32 * size, level.origin[1] + by as f32 * size, level.origin[2] + bz as f32 * size];
    let bhi = [blo[0] + size, blo[1] + size, blo[2] + size];
    out.header.clear();
    out.entries.clear();
    if level.off { return false; }
    if level.skip_inside && (0..3).all(|a| blo[a] >= level.skip_lo[a] && bhi[a] <= level.skip_hi[a]) { return false; }
    let cell_box = |local: usize| -> ([f32; 3], [f32; 3]) {
        let (cx, cy, cz) = (local % BLOCK, (local / BLOCK) % BLOCK, local / (BLOCK * BLOCK));
        let lo = [blo[0] + cx as f32 * cell, blo[1] + cy as f32 * cell, blo[2] + cz as f32 * cell];
        (lo, [lo[0] + cell, lo[1] + cell, lo[2] + cell])
    };
    t.lights.clear();
    for (i, l) in inputs.lights.iter().enumerate() { if l.reaches(&blo, &bhi, true, near).is_some() { t.lights.push(i as u32); } }
    t.cell_lights.clear();
    t.cell_lights.resize(BLOCK_CELLS * K_LIGHTS, (0.0, 0));
    t.cell_light_count.clear();
    for local in 0..BLOCK_CELLS {
        let (lo, hi) = cell_box(local);
        t.list.clear();
        for &i in t.lights.iter() { if let Some(w) = inputs.lights[i as usize].reaches(&lo, &hi, false, near) { if w > 0.0 { t.list.push((w, i)); } } }
        let complete = t.list.len() <= K_LIGHTS;
        top(&mut t.list, K_LIGHTS);
        t.list.sort_unstable_by_key(|x| x.1);
        t.cell_lights[local * K_LIGHTS..local * K_LIGHTS + t.list.len()].copy_from_slice(&t.list);
        t.cell_light_count.push((t.list.len() as u8, complete));
    }
    let (block_near_min, block_near_max) = if level.split { inputs.near_range(&blo, &bhi) } else { (0.0, 0.0) };
    t.all.clear();
    t.near.clear();
    t.far.clear();
    for (i, e) in inputs.emitters.iter().enumerate() {
        if e.power <= 0.0 { continue; }
        let d2 = box_box_distance2(&blo, &bhi, &e.lo, &e.hi);
        let w = e.power / d2.max(near * near);
        t.all.push((w, i as u32));
        if !level.split { continue; }
        if box_box_distance2(&blo, &bhi, &e.safe_lo, &e.safe_hi) < block_near_max * block_near_max { t.near.push((w, i as u32)); }
        if !(box_box_far2(&blo, &bhi, &e.safe_lo, &e.safe_hi) < block_near_min * block_near_min) { t.far.push((e.power / d2.max(block_near_min * block_near_min).max(near * near), i as u32)); }
    }
    let all_complete = t.all.len() <= K_ALL && t.all.len() == inputs.positive_emitters;
    let near_complete = inputs.exact_split && t.near.len() <= K_NEAR;
    let far_complete = inputs.exact_split && t.far.len() <= K_FAR;
    for (list, keep) in [(&mut t.all, K_ALL), (&mut t.near, K_NEAR), (&mut t.far, K_FAR)] { top(list, keep); }
    t.ids.clear();
    t.ids.extend(t.all.iter().chain(t.near.iter()).chain(t.far.iter()).map(|x| x.1));
    t.ids.sort_unstable();
    t.ids.dedup();
    let m = t.ids.len();
    t.mask.clear();
    t.mask.resize(m, 0);
    for (k, list) in [&t.all, &t.near, &t.far].into_iter().enumerate() { for x in list.iter() { if let Ok(j) = t.ids.binary_search(&x.1) { t.mask[j] |= 1 << k; } } }
    for a in 0..3 { t.ulo[a].clear(); t.uhi[a].clear(); t.slo[a].clear(); t.shi[a].clear(); }
    t.power.clear();
    for &i in t.ids.iter() {
        let e = &inputs.emitters[i as usize];
        for a in 0..3 { t.ulo[a].push(e.lo[a]); t.uhi[a].push(e.hi[a]); t.slo[a].push(e.safe_lo[a]); t.shi[a].push(e.safe_hi[a]); }
        t.power.push(e.power);
    }
    t.d2.resize(m, 0.0);
    t.smin2.resize(m, 0.0);
    t.smax2.resize(m, 0.0);
    let near2 = near * near;
    for local in 0..BLOCK_CELLS {
        let (lo, hi) = cell_box(local);
        let (nl, lights_complete) = t.cell_light_count[local];
        let nl = nl as usize;
        let base = out.entries.len();
        write_list(&mut out.entries, &t.cell_lights[local * K_LIGHTS..local * K_LIGHTS + nl]);
        for j in 0..m {
            let dx = (t.ulo[0][j] - hi[0]).max(lo[0] - t.uhi[0][j]).max(0.0);
            let dy = (t.ulo[1][j] - hi[1]).max(lo[1] - t.uhi[1][j]).max(0.0);
            let dz = (t.ulo[2][j] - hi[2]).max(lo[2] - t.uhi[2][j]).max(0.0);
            t.d2[j] = dx * dx + dy * dy + dz * dz;
        }
        let (near_min, near_max) = if level.split { inputs.near_range(&lo, &hi) } else { (0.0, 0.0) };
        if level.split {
            for j in 0..m {
                let dx = (t.slo[0][j] - hi[0]).max(lo[0] - t.shi[0][j]).max(0.0);
                let dy = (t.slo[1][j] - hi[1]).max(lo[1] - t.shi[1][j]).max(0.0);
                let dz = (t.slo[2][j] - hi[2]).max(lo[2] - t.shi[2][j]).max(0.0);
                t.smin2[j] = dx * dx + dy * dy + dz * dz;
                let fx = (hi[0] - t.slo[0][j]).abs().max((t.shi[0][j] - lo[0]).abs());
                let fy = (hi[1] - t.slo[1][j]).abs().max((t.shi[1][j] - lo[1]).abs());
                let fz = (hi[2] - t.slo[2][j]).abs().max((t.shi[2][j] - lo[2]).abs());
                t.smax2[j] = fx * fx + fy * fy + fz * fz;
            }
        }
        let (near_max2, far_floor2) = (near_max * near_max, (near_min * near_min).max(near2));
        let near_min2 = near_min * near_min;
        let mut counts = [0usize; 3];
        for k in 0..if level.split { 3 } else { 1 } {
            t.list.clear();
            for j in 0..m {
                if t.mask[j] & (1 << k) == 0 { continue; }
                let w = match k {
                    0 => t.power[j] / t.d2[j].max(near2),
                    1 => { if t.smin2[j] >= near_max2 { continue; } t.power[j] / t.d2[j].max(near2) }
                    _ => { if t.smax2[j] < near_min2 { continue; } t.power[j] / t.d2[j].max(far_floor2) }
                };
                if w > 0.0 && w.is_finite() { t.list.push((w, t.ids[j] + EMITTER_ID)); }
            }
            write_list(&mut out.entries, &t.list);
            counts[k] = t.list.len();
        }
        let flags = if lights_complete { COMPLETE_LIGHTS } else { 0 } | if all_complete { COMPLETE_ALL } else { 0 } | if near_complete { COMPLETE_NEAR } else { 0 } | if far_complete { COMPLETE_FAR } else { 0 } | if level.split { 0 } else { SHARED_LIST };
        out.header.push(base as u32 | flags);
        out.header.push(nl as u32 | (counts[0] << 4) as u32 | (counts[1] << 9) as u32 | (counts[2] << 14) as u32);
    }
    true
}

thread_local! { static SCRATCH: std::cell::RefCell<(Scratch, BlockOut)> = std::cell::RefCell::new(Default::default()); }

fn work(shared: &Shared) {
    let inputs = unsafe { &*shared.inputs.get() };
    SCRATCH.with(|t| {
        let (t, out) = &mut *t.borrow_mut();
        let mut did = false;
        let header = (inputs.out + GRID_HEADER_BYTES) as *mut u32;
        let entries = (inputs.out + GRID_HEADER_BYTES + TOTAL_CELLS * 8) as *mut u32;
        loop {
            let unit = shared.next.fetch_add(1, Ordering::AcqRel);
            if unit >= UNITS { break; }
            let built = build_block(inputs, unit, t, out);
            let at = if built { shared.cursor.fetch_add(out.entries.len(), Ordering::AcqRel) } else { 0 };
            if !built || at + out.entries.len() > ENTRY_CAPACITY {
                out.header.clear();
                out.header.resize(BLOCK_CELLS * 2, 0);
            } else {
                for w in out.header.iter_mut().step_by(2) { *w += at as u32; }
                unsafe { std::ptr::copy_nonoverlapping(out.entries.as_ptr(), entries.add(at), out.entries.len()); }
            }
            unsafe { std::ptr::copy_nonoverlapping(out.header.as_ptr(), header.add(unit * BLOCK_CELLS * 2), BLOCK_CELLS * 2); }
            did = true;
        }
        if did { shared.done_at.fetch_max(shared.epoch.elapsed().as_micros() as u64, Ordering::AcqRel); }
    });
    if shared.active.fetch_sub(1, Ordering::AcqRel) == 1 {
        let _g = shared.done.lock().unwrap();
        shared.finished.notify_all();
    }
}

fn snapped(viewer: &[f32; 4], level: usize) -> [f32; 3] {
    let (cell, d) = (LEVEL_CELL[level], LEVEL_DIMS[level]);
    [((viewer[0] / cell).floor() - (d[0] / 2) as f32) * cell, ((viewer[1] / cell).floor() - (d[1] / 2) as f32) * cell, ((viewer[2] / cell).floor() - (d[2] / 2) as f32) * cell]
}

impl LightGrid {
    pub fn new() -> LightGrid {
        let cores = std::thread::available_parallelism().map_or(4, |n| n.get());
        let threads = std::env::var("BASIS_RT_GRID_THREADS").ok().and_then(|v| v.parse::<usize>().ok()).unwrap_or((cores / 2).clamp(1, 8));
        let shared = Arc::new(Shared { inputs: Default::default(), cursor: AtomicUsize::new(0), next: AtomicUsize::new(0), active: AtomicUsize::new(0), done_at: AtomicU64::new(0), epoch: Instant::now(), quit: AtomicBool::new(false), wake: Mutex::new(0), start: Condvar::new(), done: Mutex::new(()), finished: Condvar::new() });
        let mut workers = Vec::new();
        for i in 0..threads.saturating_sub(1) {
            let s = shared.clone();
            let spawned = std::thread::Builder::new().name(format!("light grid {i}")).spawn(move || {
                let mut seen = 0u64;
                loop {
                    {
                        let mut g = s.wake.lock().unwrap();
                        while *g == seen && !s.quit.load(Ordering::Acquire) { g = s.start.wait(g).unwrap(); }
                        if s.quit.load(Ordering::Acquire) { return; }
                        seen = *g;
                    }
                    work(&s);
                }
            });
            if let Ok(h) = spawned { workers.push(h); }
        }
        LightGrid {
            shared, workers, enabled: threads > 0, alternate: std::env::var("BASIS_RT_GRID_ALTERNATE").is_ok(), coarse_only: std::env::var("BASIS_RT_GRID_COARSE_ONLY").is_ok(),
            running: false, begun_at: 0, bounds: vec![[f32::NAN; 6]; BOUNDS_CAPACITY], changed: vec![0; BOUNDS_CAPACITY], build_us: 0, wait_us: 0, used_bytes: GRID_HEADER_BYTES as u64,
        }
    }

    pub fn absorb_bounds(&mut self, raw: *const u8, first: usize, end: usize, frame: u64) {
        let end = end.min(self.bounds.len());
        for i in first..end {
            let w = unsafe { std::slice::from_raw_parts(raw.add(i * 32) as *const u32, 8) };
            let lo = [decode_bound(w[0]), decode_bound(w[1]), decode_bound(w[2])];
            let hi = [decode_bound(w[4]), decode_bound(w[5]), decode_bound(w[6])];
            let ok = (0..3).all(|a| lo[a].is_finite() && hi[a].is_finite() && lo[a] <= hi[a]);
            let next = if ok { [lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]] } else { [f32::NAN; 6] };
            if next.iter().zip(self.bounds[i].iter()).any(|(a, b)| a.to_bits() != b.to_bits()) { self.changed[i] = frame; }
            self.bounds[i] = next;
        }
    }

    pub fn begin(&mut self, slot: *const Slot, out: *mut u8, submesh_at: impl Fn(u32) -> Option<usize>) {
        self.finish();
        let started = Instant::now();
        let params = unsafe { &(*slot).params };
        let header = out as *mut u32;
        let viewer = params.views[0].position;
        let light_count = (params.light_count as usize).min(MAX_LIGHTS);
        let emitter_count = (params.emitter_count as usize).min(MAX_EMITTERS);
        let instance_count = (params.instance_count as usize).min(MAX_INSTANCES);
        let valid = self.enabled && !(self.alternate && params.frame & 1 == 1) && light_count + emitter_count >= GRID_MIN_SOURCES && viewer[..3].iter().all(|v| v.is_finite());
        unsafe {
            for i in 0..16 { header.add(i).write(0); }
            self.used_bytes = GRID_HEADER_BYTES as u64;
            if !valid { self.build_us = 0; self.wait_us = 0; return; }
            let inputs = &mut *self.shared.inputs.get();
            let fine = if self.coarse_only { [1.0e9; 3] } else { snapped(&viewer, 0) };
            let fine_hi = [fine[0] + GRID_X as f32 * LEVEL_CELL[0], fine[1] + GRID_Y as f32 * LEVEL_CELL[0], fine[2] + GRID_Z as f32 * LEVEL_CELL[0]];
            inputs.levels[0] = Level { origin: fine, cell: LEVEL_CELL[0], split: true, off: self.coarse_only, ..Default::default() };
            inputs.levels[1] = Level { origin: snapped(&viewer, 1), cell: LEVEL_CELL[1], skip_lo: fine, skip_hi: fine_hi, skip_inside: !self.coarse_only, split: false, off: false };
            inputs.views.clear();
            for v in &params.views[..(params.view_count as usize).clamp(1, 2)] { inputs.views.push([v.position[0], v.position[1], v.position[2]]); }
            inputs.near_scale = 3.0 * params.cache_cell_size.max(0.02);
            inputs.near_span = (params.cache_cell_size * 40.0).max(0.5);
            inputs.baked = params.flags & FLAG_BAKED_HYBRID != 0;
            inputs.out = out as usize;
            inputs.lights.clear();
            for l in &(&(*slot).lights)[..light_count] {
                let kind = l.position[3].to_bits();
                inputs.lights.push(PointSource {
                    position: [l.position[0], l.position[1], l.position[2]], range: l.direction[3].max(1e-4), power: luminance(&l.color),
                    directional: kind == LIGHT_DIRECTIONAL, spot: kind == LIGHT_SPOT && l.spot[0] > 0.0,
                    axis: [l.direction[0], l.direction[1], l.direction[2]], cos_outer: if l.spot[0] > 0.0 { -l.spot[1] / l.spot[0] } else { -1.0 },
                });
            }
            inputs.emitters.clear();
            inputs.positive_emitters = 0;
            let mut previous = 0.0f32;
            let mut missing = false;
            for e in &(&(*slot).emitters)[..emitter_count] {
                let selection = (e.cdf - previous).max(0.0);
                previous = e.cdf;
                let mut source = BoxSource::default();
                if (e.instance as usize) < instance_count {
                    let inst = &(*slot).instances[e.instance as usize];
                    let t = &inst.transform;
                    let at = submesh_at(inst.mesh).map(|at| at + e.geometry as usize).filter(|&at| at < self.bounds.len() && self.bounds[at][0].is_finite());
                    let (c, x) = match at { Some(at) => { let b = &self.bounds[at]; ([(b[0] + b[3]) * 0.5, (b[1] + b[4]) * 0.5, (b[2] + b[5]) * 0.5], [(b[3] - b[0]) * 0.5, (b[4] - b[1]) * 0.5, (b[5] - b[2]) * 0.5]) } None => ([0.0; 3], [1.0; 3]) };
                    for a in 0..3 {
                        let r = &t[a * 4..a * 4 + 4];
                        let centre = r[0] * c[0] + r[1] * c[1] + r[2] * c[2] + r[3];
                        let extent = r[0].abs() * x[0] + r[1].abs() * x[1] + r[2].abs() * x[2];
                        source.lo[a] = centre - extent;
                        source.hi[a] = centre + extent;
                    }
                    match at {
                        Some(at) if params.frame.saturating_sub(self.changed[at]) > STABLE_FRAMES => { source.safe_lo = source.lo; source.safe_hi = source.hi; }
                        Some(_) => {
                            let pad = (0..3).map(|a| source.hi[a] - source.lo[a]).fold(1.0f32, f32::max);
                            for a in 0..3 { source.safe_lo[a] = source.lo[a] - pad; source.safe_hi[a] = source.hi[a] + pad; }
                        }
                        None => { source.safe_lo = [f32::NEG_INFINITY; 3]; source.safe_hi = [f32::INFINITY; 3]; }
                    }
                    let centre = [(source.lo[0] + source.hi[0]) * 0.5 - viewer[0], (source.lo[1] + source.hi[1]) * 0.5 - viewer[1], (source.lo[2] + source.hi[2]) * 0.5 - viewer[2]];
                    let d2 = centre[0] * centre[0] + centre[1] * centre[1] + centre[2] * centre[2];
                    if source.lo.iter().chain(source.hi.iter()).all(|v| v.is_finite()) && e.triangle_count > 0 { source.power = selection * d2.max(1.0); }
                }
                if source.power > 0.0 { inputs.positive_emitters += 1; } else if selection > 0.0 && e.triangle_count > 0 { missing = true; }
                inputs.emitters.push(source);
            }
            inputs.exact_split = !missing;
            if missing { inputs.positive_emitters = usize::MAX; }
            let f = [fine[0].to_bits(), fine[1].to_bits(), fine[2].to_bits(), LEVEL_CELL[0].to_bits(), 1u32, inputs.levels[1].origin[0].to_bits(), inputs.levels[1].origin[1].to_bits(), inputs.levels[1].origin[2].to_bits(), LEVEL_CELL[1].to_bits()];
            for (i, v) in f.iter().enumerate() { header.add(i).write(*v); }
        }
        self.shared.next.store(0, Ordering::Release);
        self.shared.cursor.store(0, Ordering::Release);
        self.shared.active.store(self.workers.len() + 1, Ordering::Release);
        self.shared.done_at.store(0, Ordering::Release);
        { let mut g = self.shared.wake.lock().unwrap(); *g += 1; }
        self.shared.start.notify_all();
        self.running = true;
        self.begun_at = self.shared.epoch.elapsed().as_micros() as u64 - started.elapsed().as_micros() as u64;
    }

    pub fn finish(&mut self) {
        if !self.running { return; }
        let waiting = Instant::now();
        work(&self.shared);
        let mut g = self.shared.done.lock().unwrap();
        while self.shared.active.load(Ordering::Acquire) != 0 { g = self.shared.finished.wait(g).unwrap(); }
        drop(g);
        self.running = false;
        let cursor = self.shared.cursor.load(Ordering::Acquire).min(ENTRY_CAPACITY);
        self.used_bytes = (GRID_HEADER_BYTES + TOTAL_CELLS * 8 + cursor * 4) as u64;
        self.wait_us = waiting.elapsed().as_micros() as u32;
        self.build_us = self.shared.done_at.load(Ordering::Acquire).saturating_sub(self.begun_at) as u32;
    }
}

impl Drop for LightGrid {
    fn drop(&mut self) {
        self.finish();
        self.shared.quit.store(true, Ordering::Release);
        { let _g = self.shared.wake.lock().unwrap(); }
        self.shared.start.notify_all();
        for w in self.workers.drain(..) { let _ = w.join(); }
    }
}

#[cfg(test)]
mod bench {
    use super::*;
    #[test]
    fn grid_cost() {
        let mut inputs = Inputs::default();
        inputs.levels[0] = Level { origin: [-32.0, -16.0, -32.0], cell: 2.0, split: true, ..Default::default() };
        inputs.levels[1] = Level { origin: [-128.0, -64.0, -128.0], cell: 16.0, skip_lo: [-32.0, -16.0, -32.0], skip_hi: [32.0, 16.0, 32.0], skip_inside: true, ..Default::default() };
        inputs.views = vec![[0.0, 1.5, -2.5]];
        inputs.near_scale = 0.6; inputs.near_span = 8.0; inputs.exact_split = true;
        for n in [4usize, 200] {
            inputs.lights = (0..n + 1).map(|j| { let a = j as f32 * 2.4; let r = 5.0 + ((j * 7) % 25) as f32; PointSource { position: [r * a.cos(), 2.0, r * a.sin()], range: 8.0, power: 1.0, ..Default::default() } }).collect();
            inputs.emitters = (0..n + 2).map(|j| { let a = j as f32 * 2.4; let r = 5.0 + ((j * 7) % 25) as f32; let c = [r * a.cos(), 1.0, r * a.sin()]; BoxSource { lo: [c[0] - 0.3, c[1] - 0.3, c[2] - 0.3], hi: [c[0] + 0.3, c[1] + 0.3, c[2] + 0.3], safe_lo: [c[0] - 0.3, c[1] - 0.3, c[2] - 0.3], safe_hi: [c[0] + 0.3, c[1] + 0.3, c[2] + 0.3], power: 1.0 } }).collect();
            inputs.positive_emitters = inputs.emitters.len();
            let mut t = Scratch::default();
            let mut out = BlockOut::default();
            for split in [false, true] {
                inputs.levels[0].split = split;
                let start = Instant::now();
                let reps = 5;
                for _ in 0..reps { for u in 0..LEVEL_BLOCKS[0] { build_block(&inputs, u, &mut t, &mut out); } }
                let fine = start.elapsed().as_micros() / reps;
                let start = Instant::now();
                for _ in 0..reps { for u in LEVEL_BLOCKS[0]..UNITS { build_block(&inputs, u, &mut t, &mut out); } }
                println!("n={n} split={split}: fine {fine} us, coarse {} us", start.elapsed().as_micros() / reps);
            }
        }
    }
}
