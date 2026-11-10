use basis_rt_protocol::*;
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::time::Instant;

pub const HEAP_LIGHT_GRID: u32 = 256;
pub const GRID_X: usize = 32;
pub const GRID_Y: usize = 16;
pub const GRID_Z: usize = 32;
pub const GRID_CELLS: usize = GRID_X * GRID_Y * GRID_Z;
pub const GRID_K: usize = 24;
pub const GRID_K_LIGHTS: usize = 8;
pub const GRID_CELL: f32 = 2.0;
pub const GRID_HEADER_BYTES: usize = 64;
pub const BOUNDS_CAPACITY: usize = 65536;
const GRID_MIN_SOURCES: usize = 5;
pub const GRID_BYTES: u64 = (GRID_HEADER_BYTES + GRID_CELLS * 4 + GRID_CELLS * GRID_K * 4) as u64;
const BLOCK: usize = 4;
const BLOCKS_X: usize = GRID_X / BLOCK;
const BLOCKS_Y: usize = GRID_Y / BLOCK;
const BLOCKS_Z: usize = GRID_Z / BLOCK;
const BLOCKS: usize = BLOCKS_X * BLOCKS_Y * BLOCKS_Z;
const BLOCK_CELLS: usize = BLOCK * BLOCK * BLOCK;
const CDF_ONE: u32 = 1 << 20;
const EMITTER_ID: u32 = MAX_LIGHTS as u32;
const CELL_LIGHTS_COMPLETE: u32 = 1 << 9;
const CELL_EMITTERS_COMPLETE: u32 = 1 << 10;
const CELL_OFFSET_SHIFT: u32 = 11;
const _: () = assert!(GRID_X % BLOCK == 0 && GRID_Y % BLOCK == 0 && GRID_Z % BLOCK == 0 && MAX_LIGHTS + MAX_EMITTERS <= 4096 && GRID_K < 32 && GRID_K_LIGHTS < 16 && GRID_CELLS * GRID_K < 1 << 21);

#[derive(Clone, Copy, Default)]
struct PointSource { position: [f32; 3], range: f32, power: f32, directional: bool, spot: bool, axis: [f32; 3], cos_outer: f32 }

#[derive(Clone, Copy, Default)]
struct BoxSource { lo: [f32; 3], hi: [f32; 3], power: f32 }

#[derive(Default)]
struct Inputs { origin: [f32; 3], lights: Vec<PointSource>, emitters: Vec<BoxSource>, positive_emitters: usize, out: usize }

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
    running: bool,
    begun_at: u64,
    pub bounds: Vec<[f32; 6]>,
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

fn box_box_distance2(alo: &[f32; 3], ahi: &[f32; 3], blo: &[f32; 3], bhi: &[f32; 3]) -> f32 {
    let mut d = 0.0;
    for a in 0..3 { let v = (blo[a] - ahi[a]).max(alo[a] - bhi[a]).max(0.0); d += v * v; }
    d
}

fn decode_bound(bits: u32) -> f32 { f32::from_bits(if bits & 0x8000_0000 != 0 { bits & 0x7fff_ffff } else { !bits }) }

impl PointSource {
    fn reaches(&self, lo: &[f32; 3], hi: &[f32; 3], cone: bool) -> Option<f32> {
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
        let near = 0.5 * GRID_CELL;
        Some(self.power * falloff * falloff / (d2.max(near * near)))
    }
}

fn emitter_weight(e: &BoxSource, lo: &[f32; 3], hi: &[f32; 3]) -> f32 {
    let near = 0.5 * GRID_CELL;
    e.power / box_box_distance2(lo, hi, &e.lo, &e.hi).max(near * near)
}

fn write_list(entries: &mut [u32], start: usize, list: &[(f32, u32)]) {
    let total: f64 = list.iter().map(|x| x.0 as f64).sum();
    let mut running = 0.0f64;
    let mut previous = 0u32;
    let n = list.len();
    for (i, (w, id)) in list.iter().enumerate() {
        running += *w as f64;
        let room = CDF_ONE - (n - 1 - i) as u32;
        let target = if i == n - 1 { CDF_ONE } else { ((running / total.max(1e-30)) * CDF_ONE as f64).round() as u32 };
        let v = target.max(previous + 1).min(room);
        entries[start + i] = *id | ((v - 1) << 12);
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
struct Scratch { lights: Vec<u32>, emitters: Vec<(f32, u32)>, lo: [Vec<f32>; 3], hi: [Vec<f32>; 3], power: Vec<f32>, ids: Vec<u32>, cell_lights: Vec<(f32, u32)>, cell_light_count: Vec<(u8, bool)>, list: Vec<(f32, u32)> }

fn build_block(inputs: &Inputs, block: usize, t: &mut Scratch, out: &mut BlockOut) {
    let bx = block % BLOCKS_X;
    let by = (block / BLOCKS_X) % BLOCKS_Y;
    let bz = block / (BLOCKS_X * BLOCKS_Y);
    let size = GRID_CELL * BLOCK as f32;
    let blo = [inputs.origin[0] + bx as f32 * size, inputs.origin[1] + by as f32 * size, inputs.origin[2] + bz as f32 * size];
    let bhi = [blo[0] + size, blo[1] + size, blo[2] + size];
    let cell_box = |local: usize| -> ([f32; 3], [f32; 3]) {
        let (cx, cy, cz) = (local % BLOCK, (local / BLOCK) % BLOCK, local / (BLOCK * BLOCK));
        let lo = [blo[0] + cx as f32 * GRID_CELL, blo[1] + cy as f32 * GRID_CELL, blo[2] + cz as f32 * GRID_CELL];
        (lo, [lo[0] + GRID_CELL, lo[1] + GRID_CELL, lo[2] + GRID_CELL])
    };
    t.lights.clear();
    for (i, l) in inputs.lights.iter().enumerate() { if l.reaches(&blo, &bhi, true).is_some() { t.lights.push(i as u32); } }
    t.cell_lights.clear();
    t.cell_lights.resize(BLOCK_CELLS * GRID_K_LIGHTS, (0.0, 0));
    t.cell_light_count.clear();
    let mut most_lights = 0usize;
    for local in 0..BLOCK_CELLS {
        let (lo, hi) = cell_box(local);
        t.list.clear();
        for &i in t.lights.iter() { if let Some(w) = inputs.lights[i as usize].reaches(&lo, &hi, false) { if w > 0.0 { t.list.push((w, i)); } } }
        let complete = t.list.len() <= GRID_K_LIGHTS;
        top(&mut t.list, GRID_K_LIGHTS);
        t.list.sort_unstable_by_key(|x| x.1);
        t.cell_lights[local * GRID_K_LIGHTS..local * GRID_K_LIGHTS + t.list.len()].copy_from_slice(&t.list);
        t.cell_light_count.push((t.list.len() as u8, complete));
        most_lights = most_lights.max(t.list.len());
    }
    let budget = GRID_K - most_lights;
    t.emitters.clear();
    for (i, e) in inputs.emitters.iter().enumerate() { if e.power > 0.0 { t.emitters.push((emitter_weight(e, &blo, &bhi), i as u32)); } }
    let emitters_complete = t.emitters.len() <= budget && t.emitters.len() == inputs.positive_emitters;
    top(&mut t.emitters, budget);
    t.emitters.sort_unstable_by_key(|x| x.1);
    for a in 0..3 { t.lo[a].clear(); t.hi[a].clear(); }
    t.power.clear();
    t.ids.clear();
    for &(_, i) in t.emitters.iter() {
        let e = &inputs.emitters[i as usize];
        for a in 0..3 { t.lo[a].push(e.lo[a]); t.hi[a].push(e.hi[a]); }
        t.power.push(e.power);
        t.ids.push(i + EMITTER_ID);
    }
    out.header.clear();
    out.entries.clear();
    let near2 = 0.25 * GRID_CELL * GRID_CELL;
    let m = t.power.len();
    for local in 0..BLOCK_CELLS {
        let (lo, hi) = cell_box(local);
        let (nl, lights_complete) = t.cell_light_count[local];
        let nl = nl as usize;
        let base = out.entries.len();
        out.entries.resize(base + nl + m, 0);
        if nl > 0 { write_list(&mut out.entries, base, &t.cell_lights[local * GRID_K_LIGHTS..local * GRID_K_LIGHTS + nl]); }
        t.list.clear();
        let (lx, ly, lz) = (&t.lo[0][..m], &t.lo[1][..m], &t.lo[2][..m]);
        let (hx, hy, hz) = (&t.hi[0][..m], &t.hi[1][..m], &t.hi[2][..m]);
        let power = &t.power[..m];
        let ids = &t.ids[..m];
        for j in 0..m {
            let dx = (lx[j] - hi[0]).max(lo[0] - hx[j]).max(0.0);
            let dy = (ly[j] - hi[1]).max(lo[1] - hy[j]).max(0.0);
            let dz = (lz[j] - hi[2]).max(lo[2] - hz[j]).max(0.0);
            t.list.push((power[j] / (dx * dx + dy * dy + dz * dz).max(near2), ids[j]));
        }
        if m > 0 { write_list(&mut out.entries, base + nl, &t.list); }
        out.header.push(((base as u32) << CELL_OFFSET_SHIFT) | (nl + m) as u32 | ((nl as u32) << 5) | if lights_complete { CELL_LIGHTS_COMPLETE } else { 0 } | if emitters_complete { CELL_EMITTERS_COMPLETE } else { 0 });
    }
}

thread_local! { static SCRATCH: std::cell::RefCell<(Scratch, BlockOut)> = std::cell::RefCell::new(Default::default()); }

fn work(shared: &Shared) {
    let inputs = unsafe { &*shared.inputs.get() };
    SCRATCH.with(|t| {
        let (t, out) = &mut *t.borrow_mut();
        let mut did = false;
        loop {
            let block = shared.next.fetch_add(1, Ordering::AcqRel);
            if block >= BLOCKS { break; }
            build_block(inputs, block, t, out);
            let at = shared.cursor.fetch_add(out.entries.len(), Ordering::AcqRel);
            for w in out.header.iter_mut() { *w += (at as u32) << CELL_OFFSET_SHIFT; }
            unsafe {
                std::ptr::copy_nonoverlapping(out.header.as_ptr(), ((inputs.out + GRID_HEADER_BYTES) as *mut u32).add(block * BLOCK_CELLS), BLOCK_CELLS);
                std::ptr::copy_nonoverlapping(out.entries.as_ptr(), ((inputs.out + GRID_HEADER_BYTES + GRID_CELLS * 4) as *mut u32).add(at), out.entries.len());
            }
            did = true;
        }
        if did { shared.done_at.fetch_max(shared.epoch.elapsed().as_micros() as u64, Ordering::AcqRel); }
    });
    if shared.active.fetch_sub(1, Ordering::AcqRel) == 1 {
        let _g = shared.done.lock().unwrap();
        shared.finished.notify_all();
    }
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
        LightGrid { shared, workers, enabled: threads > 0, alternate: std::env::var("BASIS_RT_GRID_ALTERNATE").is_ok(), running: false, begun_at: 0, bounds: vec![[f32::NAN; 6]; BOUNDS_CAPACITY], build_us: 0, wait_us: 0, used_bytes: GRID_HEADER_BYTES as u64 }
    }

    pub fn absorb_bounds(&mut self, raw: *const u8, first: usize, end: usize) {
        let end = end.min(self.bounds.len());
        for i in first..end {
            let w = unsafe { std::slice::from_raw_parts(raw.add(i * 32) as *const u32, 8) };
            let lo = [decode_bound(w[0]), decode_bound(w[1]), decode_bound(w[2])];
            let hi = [decode_bound(w[4]), decode_bound(w[5]), decode_bound(w[6])];
            let ok = (0..3).all(|a| lo[a].is_finite() && hi[a].is_finite() && lo[a] <= hi[a]);
            self.bounds[i] = if ok { [lo[0], lo[1], lo[2], hi[0], hi[1], hi[2]] } else { [f32::NAN; 6] };
        }
    }

    pub fn begin(&mut self, slot: *const Slot, out: *mut u8, submesh_at: impl Fn(u32) -> Option<usize>) {
        self.finish();
        let started = Instant::now();
        let params = unsafe { &(*slot).params };
        let header = out as *mut u32;
        let viewer = params.views[0].position;
        let origin = [((viewer[0] / GRID_CELL).floor() - (GRID_X / 2) as f32) * GRID_CELL, ((viewer[1] / GRID_CELL).floor() - (GRID_Y / 2) as f32) * GRID_CELL, ((viewer[2] / GRID_CELL).floor() - (GRID_Z / 2) as f32) * GRID_CELL];
        let light_count = (params.light_count as usize).min(MAX_LIGHTS);
        let emitter_count = (params.emitter_count as usize).min(MAX_EMITTERS);
        let instance_count = (params.instance_count as usize).min(MAX_INSTANCES);
        let valid = self.enabled && !(self.alternate && params.frame & 1 == 1) && light_count + emitter_count >= GRID_MIN_SOURCES && origin.iter().all(|v| v.is_finite());
        unsafe {
            for i in 0..16 { header.add(i).write(0); }
            self.used_bytes = GRID_HEADER_BYTES as u64;
            if !valid { self.build_us = 0; self.wait_us = 0; return; }
            let inputs = &mut *self.shared.inputs.get();
            inputs.origin = origin;
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
                    let bounds = submesh_at(inst.mesh).and_then(|at| self.bounds.get(at + e.geometry as usize)).filter(|b| b[0].is_finite());
                    let (c, x) = match bounds { Some(b) => ([(b[0] + b[3]) * 0.5, (b[1] + b[4]) * 0.5, (b[2] + b[5]) * 0.5], [(b[3] - b[0]) * 0.5, (b[4] - b[1]) * 0.5, (b[5] - b[2]) * 0.5]), None => ([0.0; 3], [1.0; 3]) };
                    for a in 0..3 {
                        let r = &t[a * 4..a * 4 + 4];
                        let centre = r[0] * c[0] + r[1] * c[1] + r[2] * c[2] + r[3];
                        let extent = r[0].abs() * x[0] + r[1].abs() * x[1] + r[2].abs() * x[2];
                        source.lo[a] = centre - extent;
                        source.hi[a] = centre + extent;
                    }
                    let centre = [(source.lo[0] + source.hi[0]) * 0.5 - viewer[0], (source.lo[1] + source.hi[1]) * 0.5 - viewer[1], (source.lo[2] + source.hi[2]) * 0.5 - viewer[2]];
                    let d2 = centre[0] * centre[0] + centre[1] * centre[1] + centre[2] * centre[2];
                    if source.lo.iter().chain(source.hi.iter()).all(|v| v.is_finite()) && e.triangle_count > 0 { source.power = selection * d2.max(1.0); }
                }
                if source.power > 0.0 { inputs.positive_emitters += 1; } else if selection > 0.0 && e.triangle_count > 0 { missing = true; }
                inputs.emitters.push(source);
            }
            if missing { inputs.positive_emitters = usize::MAX; }
            let f = [origin[0].to_bits(), origin[1].to_bits(), origin[2].to_bits(), GRID_CELL.to_bits(), 1u32];
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
        let cursor = self.shared.cursor.load(Ordering::Acquire);
        self.used_bytes = (GRID_HEADER_BYTES + GRID_CELLS * 4 + cursor * 4) as u64;
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
