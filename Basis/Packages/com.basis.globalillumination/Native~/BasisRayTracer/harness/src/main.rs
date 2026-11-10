#![allow(non_snake_case)]
#[cfg(windows)]
mod d3dclient;
mod vkclient;
use BasisRayTracingBridge::*;
use basis_rt_protocol::*;
use std::sync::atomic::Ordering;
use std::time::{Duration, Instant};

type V3 = [f32; 3];
fn add(a: V3, b: V3) -> V3 { [a[0] + b[0], a[1] + b[1], a[2] + b[2]] }
fn sub(a: V3, b: V3) -> V3 { [a[0] - b[0], a[1] - b[1], a[2] - b[2]] }
fn mul(a: V3, s: f32) -> V3 { [a[0] * s, a[1] * s, a[2] * s] }
fn dot(a: V3, b: V3) -> f32 { a[0] * b[0] + a[1] * b[1] + a[2] * b[2] }
fn cross(a: V3, b: V3) -> V3 { [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]] }
fn normalize(a: V3) -> V3 { mul(a, 1.0 / dot(a, a).sqrt()) }

fn mat_mul(a: &[f32; 16], b: &[f32; 16]) -> [f32; 16] {
    let mut r = [0.0; 16];
    for c in 0..4 { for row in 0..4 { r[c * 4 + row] = (0..4).map(|k| a[k * 4 + row] * b[c * 4 + k]).sum(); } }
    r
}

fn perspective(fov_y: f32, aspect: f32, near: f32, far: f32) -> [f32; 16] {
    let f = 1.0 / (fov_y * 0.5).tan();
    let mut m = [0.0; 16];
    m[0] = f / aspect;
    m[5] = f;
    m[10] = -(far + near) / (far - near);
    m[11] = -1.0;
    m[14] = -2.0 * far * near / (far - near);
    m
}

fn view_from(position: V3) -> [f32; 16] {
    let mut m = [0.0; 16];
    m[0] = 1.0; m[5] = 1.0; m[10] = -1.0; m[15] = 1.0;
    m[12] = -position[0]; m[13] = -position[1]; m[14] = position[2];
    m
}

fn half(v: f32) -> u16 {
    let bits = v.to_bits();
    let sign = ((bits >> 16) & 0x8000) as u16;
    let exp = ((bits >> 23) & 0xff) as i32 - 127 + 15;
    if exp <= 0 { return sign; }
    if exp >= 31 { return sign | 0x7c00; }
    sign | ((exp as u16) << 10) | ((bits >> 13) & 0x3ff) as u16
}

fn pack_normal(n: V3) -> u32 {
    let l = n[0].abs() + n[1].abs() + n[2].abs();
    let (mut x, mut y) = (n[0] / l, n[1] / l);
    if n[2] < 0.0 { let (ox, oy) = (x, y); x = (1.0 - oy.abs()) * ox.signum(); y = (1.0 - ox.abs()) * oy.signum(); }
    let qx = (x.clamp(-1.0, 1.0) * 32767.0).round() as i32;
    let qy = (y.clamp(-1.0, 1.0) * 32767.0).round() as i32;
    (qx as u32 & 0xffff) | ((qy as u32) << 16)
}

fn pack_rgba8(c: V3) -> u32 { let q = |v: f32| (v.clamp(0.0, 1.0) * 255.0 + 0.5) as u32; q(c[0]) | (q(c[1]) << 8) | (q(c[2]) << 16) | (255 << 24) }

fn pack_rgb9e5(c: V3) -> u32 {
    let m = c[0].max(c[1]).max(c[2]).max(1e-9).min(65408.0);
    let e = (m.log2().floor() as i32).max(-16) + 1;
    let mut exp = e + 15;
    let mut scale = 2f32.powi(exp - 15 - 9);
    if (m / scale + 0.5) as u32 >= 512 { exp += 1; scale *= 2.0; }
    let q = |v: f32| ((v.max(0.0) / scale + 0.5) as u32).min(511);
    q(c[0]) | (q(c[1]) << 9) | (q(c[2]) << 18) | ((exp.clamp(0, 31) as u32) << 27)
}

struct Quad { corner: V3, u: V3, v: V3, normal: V3, albedo: V3, emission: V3, material: u32 }

fn room() -> Vec<Quad> {
    let white = [0.75, 0.75, 0.75];
    let ls: f32 = std::env::var("LIGHTSIZE").ok().and_then(|v| v.parse().ok()).unwrap_or(1.0);
    let q = |corner: V3, u: V3, v: V3, albedo: V3, emission: V3, material: u32| { let n = normalize(cross(u, v)); Quad { corner, u, v, normal: n, albedo, emission, material } };
    vec![
        q([-3.0, 0.0, -3.0], [0.0, 0.0, 6.0], [6.0, 0.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 3.0, -3.0], [6.0, 0.0, 0.0], [0.0, 0.0, 6.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, 3.0], [0.0, 3.0, 0.0], [6.0, 0.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, -3.0], [6.0, 0.0, 0.0], [0.0, 3.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, -3.0], [0.0, 3.0, 0.0], [0.0, 0.0, 6.0], [0.8, 0.1, 0.1], [0.0; 3], 1),
        q([3.0, 0.0, -3.0], [0.0, 0.0, 6.0], [0.0, 3.0, 0.0], [0.1, 0.8, 0.1], [0.0; 3], 2),
        q([-0.6 * ls, 2.98, 1.0 - 0.6 * ls], [1.2 * ls, 0.0, 0.0], [0.0, 0.0, 1.2 * ls], [1.0; 3], [12.0 / (ls * ls), 11.0 / (ls * ls), 9.0 / (ls * ls)], 3),
    ]
}

#[derive(Clone, Copy)]
struct Block { centre: V3, size: V3, albedo: V3, emission: V3 }

fn block_quads(b: &Block) -> Vec<Quad> {
    let c = sub(b.centre, mul(b.size, 0.5));
    let [sx, sy, sz] = b.size;
    let q = |corner: V3, u: V3, v: V3| { let n = normalize(cross(u, v)); Quad { corner, u, v, normal: n, albedo: b.albedo, emission: b.emission, material: 4 } };
    vec![
        q(c, [0.0, sy, 0.0], [sx, 0.0, 0.0]),
        q(add(c, [0.0, 0.0, sz]), [sx, 0.0, 0.0], [0.0, sy, 0.0]),
        q(c, [0.0, 0.0, sz], [0.0, sy, 0.0]),
        q(add(c, [sx, 0.0, 0.0]), [0.0, sy, 0.0], [0.0, 0.0, sz]),
        q(c, [sx, 0.0, 0.0], [0.0, 0.0, sz]),
        q(add(c, [0.0, sy, 0.0]), [0.0, 0.0, sz], [sx, 0.0, 0.0]),
    ]
}

struct World { quads: Vec<Quad>, blocks: Vec<Block>, lights: Vec<Light>, camera: V3 }

fn rect(corner: V3, u: V3, v: V3, albedo: V3, emission: V3) -> Quad { Quad { corner, u, v, normal: normalize(cross(u, v)), albedo, emission, material: 0 } }

fn world(scene: u32, t: f32, lit: f32) -> World {
    let grey = [0.6, 0.6, 0.6];
    let none = [0.0; 3];
    let point = |p: V3, c: V3, range: f32| Light { position: [p[0], p[1], p[2], f32::from_bits(LIGHT_POINT)], direction: [0.0, -1.0, 0.0, range], color: [c[0] * lit, c[1] * lit, c[2] * lit, 1.0], spot: [0.0; 4] };
    let mut w = World { quads: room(), blocks: Vec::new(), lights: Vec::new(), camera: [0.0, 1.5, -2.5] };
    if scene == 0 {
        w.blocks.push(Block { centre: [1.2, 0.6 + 0.3 * t.sin(), 1.0], size: [0.6; 3], albedo: [0.2, 0.3, 0.9], emission: [0.2, 0.4, 3.0] });
        w.lights.push(point([0.0, 2.5, -1.0], [3.0, 2.7, 2.4], 8.0));
        return w;
    }
    if scene != 4 { w.quads.truncate(6); }
    let pillar = Block { centre: [-1.0, 0.75, 1.0], size: [0.5, 1.5, 0.5], albedo: grey, emission: none };
    let slider = Block { centre: [1.8 * (t * 1.5).sin(), 1.3, 0.8], size: [1.0, 0.3, 1.6], albedo: grey, emission: none };
    match scene {
        1 => { w.quads.push(rect([-2.0, 2.98, -1.5], [4.0, 0.0, 0.0], [0.0, 0.0, 4.0], [1.0; 3], [1.5, 1.5, 1.5])); w.blocks.push(pillar); }
        2 => { w.quads.push(rect([-2.5, 2.6, 2.98], [0.0, 0.03, 0.0], [5.0, 0.0, 0.0], [1.0; 3], [60.0, 50.0, 40.0])); w.blocks.push(pillar); }
        3 => {
            w.blocks.push(Block { centre: [0.0, 0.4, 0.8], size: [0.8; 3], albedo: grey, emission: none });
            for j in 0..32usize {
                let (gx, gz) = ((j % 8) as f32, (j / 8) as f32);
                let hue = j as f32 * 2.4;
                let colour = [0.5 + 0.5 * hue.cos(), 0.5 + 0.5 * (hue + 2.1).cos(), 0.5 + 0.5 * (hue + 4.2).cos()];
                w.blocks.push(Block { centre: [-2.45 + gx * 0.7, 0.3 + ((j * 5) % 7) as f32 * 0.38, -0.6 + gz * 1.05], size: [0.12; 3], albedo: [0.5; 3], emission: mul(colour, 20.0) });
            }
            w.lights.push(point([2.0, 1.0, 2.0], [1.5, 0.3, 0.3], 5.0));
            w.lights.push(point([-2.0, 2.0, -2.0], [0.3, 0.3, 1.5], 5.0));
        }
        4 => { w.blocks.push(slider); }
        5 => { w.blocks.push(Block { centre: [2.2 * (t * 1.2).sin(), 1.0, 0.5], size: [0.4; 3], albedo: [0.5; 3], emission: [8.0, 6.0, 3.0] }); w.blocks.push(pillar); }
        6 => {
            let wall = [0.75, 0.75, 0.75];
            for (z, front) in [(0.5f32, true), (0.55, false)] {
                for (x0, y0, sx, sy) in [(-3.0f32, 0.0f32, 2.5f32, 3.0f32), (0.5, 0.0, 2.5, 3.0), (-0.5, 2.0, 1.0, 1.0)] {
                    w.quads.push(if front { rect([x0, y0, z], [0.0, sy, 0.0], [sx, 0.0, 0.0], wall, none) } else { rect([x0, y0, z], [sx, 0.0, 0.0], [0.0, sy, 0.0], wall, none) });
                }
            }
            w.quads.push(rect([-0.5, 0.0, 0.5], [0.0, 0.0, 0.05], [0.0, 2.0, 0.0], wall, none));
            w.quads.push(rect([0.5, 0.0, 0.5], [0.0, 2.0, 0.0], [0.0, 0.0, 0.05], wall, none));
            w.quads.push(rect([-0.5, 2.0, 0.5], [1.0, 0.0, 0.0], [0.0, 0.0, 0.05], wall, none));
            w.quads.push(rect([-1.0, 2.98, 1.2], [2.0, 0.0, 0.0], [0.0, 0.0, 1.5], [1.0; 3], [8.0, 7.5, 6.5]));
            w.blocks.push(Block { centre: [1.5, 0.4, -0.5], size: [0.8; 3], albedo: grey, emission: none });
        }
        _ => {
            let spot = |p: V3, c: V3| Light { position: [p[0], p[1], p[2], f32::from_bits(LIGHT_SPOT)], direction: [0.0, -1.0, 0.0, 10.0], color: [c[0] * lit, c[1] * lit, c[2] * lit, 1.0], spot: [5.0, -4.0, 0.0, 0.0] };
            w.lights.push(spot([-1.2, 2.9, 0.8], [30.0, 26.0, 20.0]));
            w.lights.push(spot([1.4, 2.9, 0.2], [10.0, 20.0, 30.0]));
            w.blocks.push(slider);
            w.blocks.push(pillar);
        }
    }
    w
}

fn intersect(quads: &[Quad], origin: V3, dir: V3) -> Option<(f32, usize)> {
    let mut best: Option<(f32, usize)> = None;
    for (i, q) in quads.iter().enumerate() {
        let denom = dot(q.normal, dir);
        if denom.abs() < 1e-6 { continue; }
        let t = dot(sub(q.corner, origin), q.normal) / denom;
        if t <= 1e-4 || best.map_or(false, |b| t >= b.0) { continue; }
        let p = sub(add(origin, mul(dir, t)), q.corner);
        let (a, b) = (dot(p, q.u) / dot(q.u, q.u), dot(p, q.v) / dot(q.v, q.v));
        if (0.0..=1.0).contains(&a) && (0.0..=1.0).contains(&b) { best = Some((t, i)); }
    }
    best
}

fn mesh_texels(quads: &[Quad], filter: impl Fn(&Quad) -> bool) -> (Vec<[u32; 4]>, Vec<u32>) {
    let mut vertices = Vec::new();
    let mut indices = Vec::new();
    for q in quads.iter().filter(|q| filter(q)) {
        let base = (vertices.len() / 2) as u32;
        for (k, (a, b)) in [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)].iter().enumerate() {
            let p = add(q.corner, add(mul(q.u, *a), mul(q.v, *b)));
            let _ = k;
            vertices.push([p[0].to_bits(), p[1].to_bits(), p[2].to_bits(), pack_normal(q.normal)]);
            vertices.push([half(*a) as u32 | ((half(*b) as u32) << 16), 0, 0, 0]);
        }
        indices.extend_from_slice(&[base, base + 1, base + 2, base, base + 2, base + 3]);
    }
    (vertices, indices)
}

unsafe fn store128(base: *mut u8, pitch: usize, x: u32, y: u32, v: [u32; 4]) {
    let lo = base.add(pitch * (y as usize * 2) + x as usize * 8) as *mut [u16; 4];
    let hi = base.add(pitch * (y as usize * 2 + 1) + x as usize * 8) as *mut [u16; 4];
    unsafe {
        *lo = [v[0] as u16, (v[0] >> 16) as u16, v[1] as u16, (v[1] >> 16) as u16];
        *hi = [v[2] as u16, (v[2] >> 16) as u16, v[3] as u16, (v[3] >> 16) as u16];
    }
}

fn f16(h: u16) -> f32 {
    let s = if h & 0x8000 != 0 { -1.0 } else { 1.0 };
    let e = ((h >> 10) & 0x1f) as i32;
    let m = (h & 0x3ff) as f32;
    if e == 0 { return s * m * 2f32.powi(-24); }
    if e == 31 { return f32::NAN; }
    s * (1.0 + m / 1024.0) * 2f32.powi(e - 15)
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    let exe = args.get(1).cloned().unwrap_or_else(|| std::env::current_exe().unwrap().with_file_name(if cfg!(windows) { "BasisRayTracer.exe" } else { "BasisRayTracer" }).to_string_lossy().into_owned());
    let out = args.get(2).cloned().unwrap_or_else(|| "harness.ppm".into());
    let frames: u64 = args.get(3).and_then(|s| s.parse().ok()).unwrap_or(48);
    let w: u32 = std::env::var("W").ok().and_then(|v| v.parse().ok()).unwrap_or(480);
    let h: u32 = std::env::var("H").ok().and_then(|v| v.parse().ok()).unwrap_or(270);
    let views: u32 = std::env::var("VIEWS").ok().and_then(|v| v.parse().ok()).unwrap_or(1u32).clamp(1, 2);
    let pw = w * views;
    let mut gpu_sum = [0u64; 8];
    let mut gpu_n = 0u64;
    unsafe {
        let vulkan_client = cfg!(not(windows)) || std::env::var("CLIENT").map_or(false, |v| v.eq_ignore_ascii_case("vulkan"));
        let rpitch = (pw * 8 + 255) & !255;
        let mut vkc: Option<vkclient::Client> = None;
        #[cfg(windows)]
        let mut d3c: Option<d3dclient::Client> = None;
        let mut bridge = if vulkan_client {
            let (client, backend, adapter) = vkclient::Client::new(pw, h, rpitch);
            println!("unity side: Vulkan");
            let mut bridge = Bridge::new(backend, adapter, API_VULKAN).unwrap();
            bridge.unity_upload = client.upload.pointer();
            bridge.unity[0] = UnityTextures { gbuffer: client.gbuffer.pointer(), result: client.result.pointer() };
            bridge.unity[1] = UnityTextures { gbuffer: client.gbuffer.pointer(), result: client.second.pointer() };
            vkc = Some(client);
            bridge
        } else {
            #[cfg(windows)]
            {
                let (client, bridge) = d3dclient::Client::new(pw, h, rpitch);
                d3c = Some(client);
                bridge
            }
            #[cfg(not(windows))]
            unreachable!()
        };
        let tracer_api = if std::env::var("TRACER").map_or(false, |v| v.eq_ignore_ascii_case("vulkan")) { TRACER_VULKAN } else { TRACER_DXR };
        bridge.launch_api(&exe, tracer_api).unwrap();
        bridge.same_frame = true;
        bridge.budget_us = 200_000;
        let header = &*(bridge.base as *const Header);
        let contexts: usize = std::env::var("CONTEXTS").ok().and_then(|v| v.parse().ok()).unwrap_or(1).clamp(1, MAX_CONTEXTS);
        for c in 0..contexts {
            header.contexts[c].requested_width.store(w, Ordering::Release);
            header.contexts[c].requested_height.store(h, Ordering::Release);
            header.contexts[c].requested_views.store(views, Ordering::Release);
        }
        let start = Instant::now();
        while (0..contexts).any(|c| header.contexts[c].generation.load(Ordering::Acquire) == 0) {
            let s = bridge.status();
            if s == STATUS_FAILED || s == STATUS_EXITED || start.elapsed() > Duration::from_secs(20) {
                let end = header.error.iter().position(|c| *c == 0).unwrap_or(0);
                eprintln!("tracer failed to start (status {s}): {}", String::from_utf8_lossy(&header.error[..end]));
                std::process::exit(1);
            }
            std::thread::sleep(Duration::from_millis(5));
        }
        println!("tracer {} ready in {:?}, caps {:#x} (SER {}, reorders {}, OMM tier {})", if header.tracer_api == TRACER_VULKAN { "vulkan" } else { "dxr" }, start.elapsed(), header.caps, header.caps & CAP_SER != 0, header.caps & CAP_SER_REORDERS != 0, header.caps & CAP_OMM != 0);

        let mut protocol = 0u64;
        let mut second_ok = 0u32;
        #[cfg(windows)]
        let (gptr, uptr, rptr) = match (&vkc, &d3c) { (Some(c), _) => (c.gstage.mapped, c.ustage.mapped, c.readback.mapped), (_, Some(c)) => (c.gptr, c.uptr, c.rptr), _ => unreachable!() };
        #[cfg(not(windows))]
        let (gptr, uptr, rptr) = { let c = vkc.as_ref().unwrap(); (c.gstage.mapped, c.ustage.mapped, c.readback.mapped) };

        let sway = std::env::var("SWAY").map_or(false, |v| !v.is_empty());
        let env_f = |name: &str| std::env::var(name).ok().and_then(|v| v.parse::<f32>().ok());
        let scene: u32 = env_f("SCENE").unwrap_or(0.0) as u32;
        let (time_fixed, stop_at) = (env_f("TFIX"), env_f("STOPAT"));
        let acc_from: u64 = std::env::var("ACC").ok().and_then(|v| v.parse().ok()).unwrap_or(u64::MAX);
        let (mut acc, mut acc_n) = (Vec::<f64>::new(), 0u64);
        let mut last_centres: Vec<V3> = Vec::new();
        let mut camera = world(scene, 0.0, 1.0).camera;
        let mut previous_frame: Vec<f32> = Vec::new();
        let mut frame_log = String::new();
        let mut flicker = Vec::new();
        let mut prev_vp = [0f32; 16];
        let fov = 70f32.to_radians();
        let projection = perspective(fov, w as f32 / h as f32, 0.05, 100.0);

        let light_scale: f32 = std::env::var("LIGHT").ok().and_then(|v| v.parse().ok()).unwrap_or(1.0);
        let instances: usize = std::env::var("INSTANCES").ok().and_then(|v| v.parse().ok()).unwrap_or(2).clamp(2, MAX_INSTANCES);
        let mut record_us = 0u64;
        let freeze = std::env::var("FREEZE").map_or(false, |v| !v.is_empty());
        let avatar_rays: u32 = std::env::var("AVATARRAYS").ok().and_then(|v| v.parse().ok()).unwrap_or(0);
        let many: usize = std::env::var("MANY").ok().and_then(|v| v.parse().ok()).unwrap_or(0usize).min(MAX_EMITTERS - 2).min(MAX_LIGHTS - 1);
        let instances = if many > 0 { 2 + many } else { instances };
        let noise_from: u64 = std::env::var("NOISE_FROM").ok().and_then(|v| v.parse().ok()).unwrap_or(u64::MAX);
        let (mut noise_sum, mut noise_sq, mut noise_n) = (Vec::<f64>::new(), Vec::<f64>::new(), 0u64);
        let (mut grid_us, mut grid_wait) = (0u64, 0u64);
        let mut parity_sum = [[0u64; 8]; 2];
        let mut parity_n = [0u64; 2];
        let spot = |j: usize| -> V3 { let a = j as f32 * 2.39996; let r = 5.0 + ((j * 7) % 25) as f32; [r * a.cos(), 0.5 + (j % 3) as f32, r * a.sin()] };
        let mut times = Vec::new();

        for frame in 1..=frames {
            protocol += 1;
            let iteration = frame;
            let frame = protocol;
            while header.frame_submitted.load(Ordering::Acquire) + 2 < frame { std::thread::yield_now(); }
            let t = if freeze { 0.0 } else { frame as f32 * 0.1 };
            if sway { camera[0] = 0.4 * (frame as f32 * 0.25).sin(); }
            let view = view_from(camera);
            let halton = |i: u32, b: u32| { let (mut f, mut r, mut i) = (1.0f32, 0.0f32, i); while i > 0 { f /= b as f32; r += f * (i % b) as f32; i /= b; } r };
            let jitter = if std::env::var("JITTER").is_ok() { [(halton(iteration as u32 % 8 + 1, 2) - 0.5) * 2.0 / w as f32, (halton(iteration as u32 % 8 + 1, 3) - 0.5) * 2.0 / h as f32] } else { [0.0, 0.0] };
            let mut projection = projection;
            for c in 0..4 { projection[c * 4] += jitter[0] * projection[c * 4 + 3]; projection[c * 4 + 1] += jitter[1] * projection[c * 4 + 3]; }
            let vp = mat_mul(&projection, &view);
            if frame == 1 { prev_vp = vp; }
            let light_off_at = std::env::var("LIGHTOFF").ok().and_then(|v| v.parse::<u64>().ok());
            let lit = if light_off_at.map_or(false, |n| iteration as u64 >= n) { 0.0f32 } else { 1.0f32 } * light_scale;
            let scene_time = if scene == 0 { t } else { time_fixed.unwrap_or((iteration as f32).min(stop_at.unwrap_or(f32::MAX)) / 60.0) };
            let wd = world(scene, scene_time, lit);
            let room_quads = &wd.quads;
            let (statics, blocks) = (wd.quads.len(), wd.blocks.len());
            let cube_center = wd.blocks[0].centre;
            let cube_quads: Vec<Quad> = wd.blocks.iter().flat_map(block_quads).collect();
            let slot = &mut *slot(bridge.base, frame as usize);
            let mut texels: Vec<[u32; 4]> = Vec::new();
            let mut commands: Vec<Command> = Vec::new();
            let mut push = |texels: &mut Vec<[u32; 4]>, data: &[[u32; 4]]| -> u32 { let at = texels.len() as u32; texels.extend_from_slice(data); at };
            if iteration == 1 {
                let (rv, ri) = mesh_texels(room_quads, |_| true);
                let mut index_texels: Vec<[u32; 4]> = ri.chunks(4).map(|c| [c[0], *c.get(1).unwrap_or(&0), *c.get(2).unwrap_or(&0), *c.get(3).unwrap_or(&0)]).collect();
                let vc = (rv.len() / 2) as u32;
                commands.push(Command { op: OP_MESH_DEFINE, id: 1, a: vc, b: ri.len() as u32, c: room_quads.len() as u32, ..Default::default() });
                for (i, _) in room_quads.iter().enumerate() { commands.push(Command { op: OP_MESH_SUBMESH, id: 1, a: i as u32 * 6, b: 6, c: SUBMESH_OPAQUE, ..Default::default() }); }
                let at = push(&mut texels, &rv);
                commands.push(Command { op: OP_MESH_VERTICES, id: 1, a: 0, b: vc, c: at, ..Default::default() });
                let at = push(&mut texels, &index_texels);
                commands.push(Command { op: OP_MESH_INDICES, id: 1, a: 0, b: ri.len() as u32, c: at, ..Default::default() });
                commands.push(Command { op: OP_MESH_COMMIT, id: 1, a: 0, ..Default::default() });
                let (_, ci) = mesh_texels(&cube_quads, |_| true);
                index_texels = ci.chunks(4).map(|c| [c[0], *c.get(1).unwrap_or(&0), *c.get(2).unwrap_or(&0), *c.get(3).unwrap_or(&0)]).collect();
                commands.push(Command { op: OP_MESH_DEFINE, id: 2, a: 24 * blocks as u32, b: 36 * blocks as u32, c: blocks as u32, ..Default::default() });
                for b in 0..blocks as u32 { commands.push(Command { op: OP_MESH_SUBMESH, id: 2, a: b * 36, b: 36, c: SUBMESH_OPAQUE, ..Default::default() }); }
                let at = push(&mut texels, &index_texels);
                commands.push(Command { op: OP_MESH_INDICES, id: 2, a: 0, b: 36 * blocks as u32, c: at, ..Default::default() });
            }
            let (cv, _) = mesh_texels(&cube_quads, |_| true);
            let at = push(&mut texels, &cv);
            commands.push(Command { op: OP_MESH_VERTICES, id: 2, a: 0, b: 24 * blocks as u32, c: at, ..Default::default() });
            commands.push(Command { op: if iteration == 1 { OP_MESH_COMMIT } else { OP_MESH_REFIT }, id: 2, a: MESH_DYNAMIC, ..Default::default() });
            for (i, c) in commands.iter().enumerate() { slot.commands[i] = *c; }

            let identity = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0];
            slot.instances[0] = Instance { transform: identity, mesh: 1, material_base: 0, mask: 0xff, flags: 0, lightmap: -1, ..Default::default() };
            slot.instances[1] = Instance { transform: identity, mesh: 2, material_base: statics as u32, mask: 0xff, flags: if scene == 0 { INSTANCE_AVATAR | INSTANCE_EMITTER } else { INSTANCE_EMITTER }, lightmap: -1, ..Default::default() };
            for j in 0..many { let p = spot(j); slot.instances[2 + j] = Instance { transform: [1.0, 0.0, 0.0, p[0] - 1.2, 0.0, 1.0, 0.0, p[1] - 0.6, 0.0, 0.0, 1.0, p[2] - 1.0], mesh: 2, material_base: 7, mask: 0xff, flags: 0, lightmap: -1, ..Default::default() }; }
            for i in (2 + many)..instances { slot.instances[i] = Instance { transform: [1.0, 0.0, 0.0, 500.0 + (i % 128) as f32 * 2.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 500.0 + (i / 128) as f32 * 2.0], mesh: 2, material_base: 7, mask: 0xff, flags: 0, lightmap: -1, ..Default::default() }; }
            for (i, q) in room_quads.iter().enumerate() { slot.materials[i] = Material { base_color: [q.albedo[0], q.albedo[1], q.albedo[2], 1.0], emission: [q.emission[0] * lit, q.emission[1] * lit, q.emission[2] * lit, 0.0], base_st: [1.0, 1.0, 0.0, 0.0], emission_st: [1.0, 1.0, 0.0, 0.0], base_texture: -1, emission_texture: -1, flags: if q.emission.iter().any(|v| *v > 0.0) { MATERIAL_EMITTER } else { 0 }, ..Default::default() }; }
            for (b, k) in wd.blocks.iter().enumerate() { slot.materials[statics + b] = Material { base_color: [k.albedo[0], k.albedo[1], k.albedo[2], 1.0], emission: [k.emission[0] * lit, k.emission[1] * lit, k.emission[2] * lit, 0.0], base_st: [1.0, 1.0, 0.0, 0.0], emission_st: [1.0, 1.0, 0.0, 0.0], base_texture: -1, emission_texture: -1, flags: if k.emission.iter().any(|v| *v > 0.0) { MATERIAL_EMITTER } else { 0 }, ..Default::default() }; }
            for (i, l) in wd.lights.iter().enumerate() { slot.lights[i] = *l; }
            let mut dirty_count = 0usize;
            let mut emitter_count = 2usize;
            if scene == 0 {
                slot.dirty[0] = [cube_center[0], cube_center[1], cube_center[2], if std::env::var("SOFT").is_ok() { -1.5 } else { 1.5 }];
                dirty_count = 1;
                slot.emitters[0] = Emitter { instance: 0, geometry: 6, triangle_count: 2, cdf: 0.7 };
                slot.emitters[1] = Emitter { instance: 1, geometry: 0, triangle_count: 12, cdf: 1.0 };
            } else {
                let lum = |c: V3| c[0] * 0.2126 + c[1] * 0.7152 + c[2] * 0.0722;
                let mut found: Vec<(u32, u32, u32, f32)> = Vec::new();
                for (i, q) in wd.quads.iter().enumerate() { let p = lum(q.emission) * dot(cross(q.u, q.v), q.normal).abs(); if p > 0.0 { let d = sub(add(q.corner, mul(add(q.u, q.v), 0.5)), camera); found.push((0, i as u32, 2, p / dot(d, d).max(1.0))); } }
                for (b, k) in wd.blocks.iter().enumerate() {
                    let area = 2.0 * (k.size[0] * k.size[1] + k.size[1] * k.size[2] + k.size[0] * k.size[2]);
                    let power = lum(k.emission) * area;
                    if power > 0.0 { let d = sub(k.centre, camera); found.push((1, b as u32, 12, power / dot(d, d).max(1.0))); }
                    if let Some(last) = last_centres.get(b) {
                        if *last != k.centre && dirty_count + 2 <= MAX_DIRTY {
                            let reach = dot(k.size, k.size).sqrt() * 0.5 + 1.5 + if power > 0.0 { (power.sqrt() * 2.0).min(8.0) } else { 0.0 };
                            let w = if power > 0.0 { reach } else { -reach };
                            slot.dirty[dirty_count] = [k.centre[0], k.centre[1], k.centre[2], w];
                            slot.dirty[dirty_count + 1] = [last[0], last[1], last[2], w];
                            dirty_count += 2;
                        }
                    }
                }
                last_centres = wd.blocks.iter().map(|k| k.centre).collect();
                let total: f32 = found.iter().map(|f| f.3).sum();
                let mut running = 0.0;
                emitter_count = found.len();
                for (k, f) in found.iter().enumerate() { running += f.3; slot.emitters[k] = Emitter { instance: f.0, geometry: f.1, triangle_count: f.2, cdf: if k + 1 == found.len() { 1.0 } else { running / total } }; }
            }
            if light_off_at == Some(iteration as u64) { slot.dirty[0] = [0.0, 2.5, -1.0, if std::env::var("HARDOFF").is_ok() { 13.0 } else { 100013.0 }]; dirty_count = dirty_count.max(1); }
            if many > 0 {
                let weights: Vec<f32> = [0.7f32, 0.3].into_iter().chain((0..many).map(|j| { let p = spot(j); let d = sub(p, camera); 1.2 / dot(d, d).max(1.0) })).collect();
                let total: f32 = weights.iter().sum();
                let mut running = 0.0;
                for (k, w) in weights.iter().enumerate() {
                    running += w;
                    slot.emitters[k] = Emitter { instance: if k == 0 { 0 } else { k as u32 }, geometry: if k == 0 { 6 } else { 0 }, triangle_count: if k == 0 { 2 } else { 12 }, cdf: if k + 1 == weights.len() { 1.0 } else { running / total } };
                }
                for j in 0..many { let p = spot(j); slot.lights[1 + j] = Light { position: [p[0], p[1] + 1.0, p[2], f32::from_bits(LIGHT_POINT)], direction: [0.0, -1.0, 0.0, 8.0], color: [3.0 * lit, 2.7 * lit, 2.4 * lit, 1.0], spot: [0.0; 4] }; }
            }
            let v = View { view, projection, inverse_view_projection: [0.0; 16], previous_view_projection: prev_vp, position: [camera[0], camera[1], camera[2], 0.0] };
            slot.params = FrameParams {
                frame, instance_count: instances as u32, material_count: (statics + blocks) as u32, light_count: (wd.lights.len() + many) as u32, command_count: commands.len() as u32, upload_texels: texels.len() as u32,
                flags: if scene == 0 || std::env::var("SCREEN").is_ok() { FLAG_SCREEN_REUSE } else { 0 } | FLAG_SHADOWS | if std::env::var("NODENOISE").is_ok() { 0 } else { FLAG_DENOISE } | FLAG_REORDER | if std::env::var("NOPROBES").map_or(false, |v| !v.is_empty()) { 0 } else { FLAG_PROBES }, width: w, height: h, view_count: views, samples: std::env::var("SAMPLES").ok().and_then(|v| v.parse().ok()).unwrap_or(2), bounces: 2 | (avatar_rays << 8),
                max_distance: 100.0, ao_radius: env_f("AORADIUS").unwrap_or(if scene == 0 { 0.5 } else { 0.0 }), emission_scale: 1.0, sky_intensity: 1.0, time: t, firefly_clamp: 30.0, temporal_response: 0.05,
                light_intensity: 1.0, normal_bias: 0.005, emitter_count: (emitter_count + many) as u32, ao_intensity: 0.5, context: 0, parity: (iteration & 1) as u32, cache_cell_size: 0.2, dirty_count: std::env::var("DIRTY").ok().and_then(|v| v.parse().ok()).unwrap_or(dirty_count as u32), ambient: [[0.0; 4]; 7], views: [v, v],
            };

            let all: Vec<Quad> = world(scene, scene_time, 1.0).quads.into_iter().chain(wd.blocks.iter().flat_map(block_quads)).collect();
            let tanf = (fov * 0.5).tan();
            for y in 0..(if freeze && iteration > 2 { 0 } else { h }) {
                for x in 0..w {
                    let ndc = [((x as f32 + 0.5) / w as f32) * 2.0 - 1.0 - jitter[0], ((y as f32 + 0.5) / h as f32) * 2.0 - 1.0 - jitter[1]];
                    let dir = normalize([ndc[0] * tanf * w as f32 / h as f32, ndc[1] * tanf, 1.0]);
                    match intersect(&all, camera, dir) {
                        Some((t, i)) => {
                            let q = &all[i];
                            let rel = mul(dir, t);
                            for vi in 0..views { store128(gptr, (pw * 2 * 8) as usize, (x + vi * w) * 2, y, [rel[0].to_bits(), rel[1].to_bits(), rel[2].to_bits(), pack_normal(q.normal)]); }
                            let lit = mul(add(q.emission, mul(q.albedo, 0.3)), light_scale);
                            for vi in 0..views { store128(gptr, (pw * 2 * 8) as usize, (x + vi * w) * 2 + 1, y, [pack_rgba8(q.albedo), pack_rgb9e5(lit), 0, 0]); }
                        }
                        None => { for vi in 0..views { store128(gptr, (pw * 2 * 8) as usize, (x + vi * w) * 2, y, [0; 4]); store128(gptr, (pw * 2 * 8) as usize, (x + vi * w) * 2 + 1, y, [0; 4]); } }
                    }
                }
            }
            for (i, t) in texels.iter().enumerate() { store128(uptr, (UPLOAD_WIDTH * 8) as usize, i as u32 % UPLOAD_WIDTH, i as u32 / UPLOAD_WIDTH, *t); }
            let rows = (texels.len() as u32).div_ceil(UPLOAD_WIDTH);
            if let Some(c) = &vkc { c.upload(rows); }
            #[cfg(windows)]
            if let Some(c) = d3c.as_mut() { c.upload(rows); }
            header.frame_requested.store(frame, Ordering::Release);
            bridge.wake();
            let started = Instant::now();
            bridge.submit(frame, 0, (iteration & 1) as usize, rows).unwrap();
            let fetched = bridge.fetch(frame, 0, (iteration & 1) as usize).unwrap();
            if contexts > 1 {
                protocol += 1;
                let next = protocol;
                while header.frame_consumed.load(Ordering::Acquire) + (RING as u64) - 1 < next { std::thread::yield_now(); }
                let source = slot as *const Slot;
                let target = basis_rt_protocol::slot(bridge.base, next as usize);
                std::ptr::copy_nonoverlapping(source as *const u8, target as *mut u8, std::mem::size_of::<Slot>());
                (*target).params.frame = next;
                (*target).params.context = 1;
                (*target).params.command_count = 0;
                (*target).params.upload_texels = 0;
                header.frame_requested.store(next, Ordering::Release);
                bridge.wake();
                bridge.submit(next, 1, (iteration & 1) as usize, 0).unwrap();
                if bridge.fetch(next, 1, (iteration & 1) as usize).unwrap() == next { second_ok += 1; }
            }
            if let Some(c) = &vkc { c.read(); }
            #[cfg(windows)]
            if let Some(c) = d3c.as_mut() { c.read(); }
            times.push(started.elapsed().as_secs_f64() * 1000.0);
            if iteration > 16 { for i in 0..8 { gpu_sum[i] += header.reserved[i] as u64; } gpu_n += 1; record_us += header.build_time_us.load(Ordering::Relaxed) as u64; grid_us += header.reserved[8] as u64; grid_wait += header.reserved[9] as u64; let lag = header.frame_submitted.load(Ordering::Acquire); let p = (lag & 1) as usize; for i in 0..8 { parity_sum[p][i] += header.reserved[i] as u64; } parity_n[p] += 1; }
            prev_vp = vp;
            if iteration as u64 >= acc_from {
                if acc.is_empty() { acc = vec![0.0; (pw * h * 3) as usize]; }
                for y in 0..h { for x in 0..pw { let p = rptr.add((y * rpitch + x * 8) as usize) as *const u16; for k in 0..3 { let v = f16(*p.add(k)); if v.is_finite() { acc[((y * pw + x) * 3) as usize + k] += v as f64; } } } }
                acc_n += 1;
            }
            let mut current = Vec::with_capacity((pw * h) as usize);
            for y in 0..h { for x in 0..pw { let p = rptr.add((y * rpitch + x * 8) as usize) as *const u16; current.push(f16(*p) * 0.2126 + f16(*p.add(1)) * 0.7152 + f16(*p.add(2)) * 0.0722); } }
            if iteration as u64 >= noise_from {
                if noise_sum.len() != current.len() { noise_sum = vec![0.0; current.len()]; noise_sq = vec![0.0; current.len()]; }
                for i in 0..current.len() { let v = current[i] as f64; noise_sum[i] += v; noise_sq[i] += v * v; }
                noise_n += 1;
                if let Ok(path) = std::env::var("LUMDUMP") { use std::io::Write; let mut f = std::fs::OpenOptions::new().create(true).append(true).open(path).unwrap(); let bytes: Vec<u8> = current.iter().flat_map(|v| v.to_le_bytes()).collect(); f.write_all(&bytes).unwrap(); }
            }
            if frame > 24 && previous_frame.len() == current.len() {
                let (mut d, mut m) = (0f64, 0f64);
                for i in 0..current.len() { d += (current[i] - previous_frame[i]).abs() as f64; m += current[i] as f64; }
                flicker.push(d / m.max(1e-9));
            }
            frame_log.push_str(&format!("{frame} {fetched} {:.9}
", current.iter().map(|v| *v as f64).sum::<f64>() / current.len().max(1) as f64));
            previous_frame = current;
            if fetched != frame { println!("frame {frame}: fetched {fetched}"); }
            if bridge.status() != STATUS_READY {
                let end = header.error.iter().position(|c| *c == 0).unwrap_or(0);
                eprintln!("tracer stopped at frame {frame}: {}", String::from_utf8_lossy(&header.error[..end]));
                std::process::exit(1);
            }
        }

        let mut ppm = format!("P6\n{pw} {h}\n255\n").into_bytes();
        let exposure: f32 = std::env::var("EXPOSURE").ok().and_then(|v| v.parse().ok()).unwrap_or(1.0);
        let mut floor = [0f64; 3];
        let mut floor_n = 0;
        let mut nan = 0;
        let mut red_side = 0f64;
        let mut green_side = 0f64;
        let mut ao_min = 1f32;
        for y in (0..h).rev() {
            for x in 0..pw {
                let p = rptr.add((y * rpitch + x * 8) as usize) as *const u16;
                let c = [f16(*p), f16(*p.add(1)), f16(*p.add(2)), f16(*p.add(3))];
                if c.iter().any(|v| !v.is_finite()) { nan += 1; }
                ao_min = ao_min.min(c[3]);
                if y < h / 5 {
                    for k in 0..3 { floor[k] += c[k] as f64; }
                    floor_n += 1;
                    if x < w / 4 { red_side += (c[0] - c[1]) as f64; }
                    if x > w * 3 / 4 { green_side += (c[1] - c[0]) as f64; }
                }
                for k in 0..3 { let v = c[k].max(0.0) * exposure; let mapped = (v / (1.0 + v)).powf(1.0 / 2.2); ppm.push((mapped * 255.0) as u8); }
            }
        }
        std::fs::write(&out, ppm).unwrap();
        std::fs::write(format!("{out}.frames.txt"), &frame_log).unwrap();
        let mut raw = Vec::with_capacity((pw * h * 8) as usize);
        for y in 0..h { raw.extend_from_slice(std::slice::from_raw_parts(rptr.add((y * rpitch) as usize), (pw * 8) as usize)); }
        std::fs::write(format!("{out}.raw"), raw).unwrap();
        if acc_n > 0 { let bytes: Vec<u8> = acc.iter().flat_map(|v| ((*v / acc_n as f64) as f32).to_le_bytes()).collect(); std::fs::write(format!("{out}.acc"), bytes).unwrap(); println!("accumulated {acc_n} frames"); }
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let n = floor_n.max(1) as f64;
        println!("floor irradiance avg ({:.3}, {:.3}, {:.3}); red bleed left {:.4}, green bleed right {:.4}; non-finite {nan}; min ao {ao_min:.3}", floor[0] / n, floor[1] / n, floor[2] / n, red_side / n, green_side / n);
        let r = &header.reserved;
        println!("gpu us: total {} | scene {} update {} resolve {} probes {} gather {} denoise {}", r[0], r[1], r[2], r[3], r[4], r[5], r[6]);
        let a = gpu_sum.map(|v| v as f64 / gpu_n.max(1) as f64);
        println!("gpu us mean over {gpu_n}: total {:.0} | scene {:.0} update {:.0} resolve {:.0} probes {:.0} gather {:.0} temporal {:.0} wavelet {:.0}", a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
        println!("round trip ms median {:.3}, p90 {:.3}; tracer cpu record mean {} us; meshes {}", times[times.len() / 2], times[times.len() * 9 / 10], record_us / gpu_n.max(1), header.mesh_count.load(Ordering::Relaxed));
        if std::env::var("BASIS_RT_GRID_ALTERNATE").is_ok() {
            for p in 0..2 { let a = parity_sum[p].map(|v| v as f64 / parity_n[p].max(1) as f64); println!("frames ending {} ({}): total {:.0} | update {:.0} probes {:.0} gather {:.0} temporal {:.0} wavelet {:.0}", p, if p == 0 { "grid" } else { "no grid" }, a[0], a[2], a[4], a[5], a[6], a[7]); }
        }
        println!("light grid cpu build mean {} us, record blocked on it {} us", grid_us / gpu_n.max(1), grid_wait / gpu_n.max(1));
        if noise_n > 1 {
            let n = noise_n as f64;
            let (mut std_sum, mut mean_sum) = (0f64, 0f64);
            for i in 0..noise_sum.len() { let m = noise_sum[i] / n; std_sum += (noise_sq[i] / n - m * m).max(0.0).sqrt(); mean_sum += m; }
            println!("temporal noise (mean per-pixel std / mean luminance) {:.3}% over {noise_n} frames; mean luminance {:.5}", 100.0 * std_sum / mean_sum.max(1e-12), mean_sum / noise_sum.len() as f64);
        }
        if contexts > 1 { println!("second context fetched {second_ok}/{frames} frames"); }
        if !flicker.is_empty() { println!("frame-to-frame change {:.2}% (mean over {} frames, sway {})", 100.0 * flicker.iter().sum::<f64>() / flicker.len() as f64, flicker.len(), sway); }
        println!("wrote {out}");
    }
}
