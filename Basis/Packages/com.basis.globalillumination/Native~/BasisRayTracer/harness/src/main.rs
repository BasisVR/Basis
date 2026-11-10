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
    let q = |corner: V3, u: V3, v: V3, albedo: V3, emission: V3, material: u32| { let n = normalize(cross(u, v)); Quad { corner, u, v, normal: n, albedo, emission, material } };
    vec![
        q([-3.0, 0.0, -3.0], [0.0, 0.0, 6.0], [6.0, 0.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 3.0, -3.0], [6.0, 0.0, 0.0], [0.0, 0.0, 6.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, 3.0], [0.0, 3.0, 0.0], [6.0, 0.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, -3.0], [6.0, 0.0, 0.0], [0.0, 3.0, 0.0], white, [0.0; 3], 0),
        q([-3.0, 0.0, -3.0], [0.0, 3.0, 0.0], [0.0, 0.0, 6.0], [0.8, 0.1, 0.1], [0.0; 3], 1),
        q([3.0, 0.0, -3.0], [0.0, 0.0, 6.0], [0.0, 3.0, 0.0], [0.1, 0.8, 0.1], [0.0; 3], 2),
        q([-0.6, 2.98, 0.4], [1.2, 0.0, 0.0], [0.0, 0.0, 1.2], [1.0; 3], [12.0, 11.0, 9.0], 3),
    ]
}

fn cube(center: V3, size: f32) -> Vec<Quad> {
    let h = size * 0.5;
    let c = sub(center, [h, h, h]);
    let s = size;
    let e = [0.2, 0.4, 3.0];
    let a = [0.2, 0.3, 0.9];
    let q = |corner: V3, u: V3, v: V3| { let n = normalize(cross(u, v)); Quad { corner, u, v, normal: n, albedo: a, emission: e, material: 4 } };
    vec![
        q(c, [0.0, s, 0.0], [s, 0.0, 0.0]),
        q(add(c, [0.0, 0.0, s]), [s, 0.0, 0.0], [0.0, s, 0.0]),
        q(c, [0.0, 0.0, s], [0.0, s, 0.0]),
        q(add(c, [s, 0.0, 0.0]), [0.0, s, 0.0], [0.0, 0.0, s]),
        q(c, [s, 0.0, 0.0], [0.0, 0.0, s]),
        q(add(c, [0.0, s, 0.0]), [0.0, 0.0, s], [s, 0.0, 0.0]),
    ]
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
        let mut camera = [0.0f32, 1.5, -2.5];
        let mut previous_frame: Vec<f32> = Vec::new();
        let mut frame_log = String::new();
        let mut flicker = Vec::new();
        let mut prev_vp = [0f32; 16];
        let fov = 70f32.to_radians();
        let projection = perspective(fov, w as f32 / h as f32, 0.05, 100.0);

        let room_quads = room();
        let mut times = Vec::new();

        for frame in 1..=frames {
            protocol += 1;
            let iteration = frame;
            let frame = protocol;
            while header.frame_submitted.load(Ordering::Acquire) + 2 < frame { std::thread::yield_now(); }
            let t = frame as f32 * 0.1;
            if sway { camera[0] = 0.4 * (frame as f32 * 0.25).sin(); }
            let view = view_from(camera);
            let vp = mat_mul(&projection, &view);
            if frame == 1 { prev_vp = vp; }
            let cube_center = [1.2, 0.6 + 0.3 * t.sin(), 1.0];
            let cube_quads = cube(cube_center, 0.6);
            let slot = &mut *slot(bridge.base, frame as usize);
            let mut texels: Vec<[u32; 4]> = Vec::new();
            let mut commands: Vec<Command> = Vec::new();
            let mut push = |texels: &mut Vec<[u32; 4]>, data: &[[u32; 4]]| -> u32 { let at = texels.len() as u32; texels.extend_from_slice(data); at };
            if iteration == 1 {
                let (rv, ri) = mesh_texels(&room_quads, |_| true);
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
                commands.push(Command { op: OP_MESH_DEFINE, id: 2, a: 24, b: 36, c: 1, ..Default::default() });
                commands.push(Command { op: OP_MESH_SUBMESH, id: 2, a: 0, b: 36, c: SUBMESH_OPAQUE, ..Default::default() });
                let at = push(&mut texels, &index_texels);
                commands.push(Command { op: OP_MESH_INDICES, id: 2, a: 0, b: 36, c: at, ..Default::default() });
            }
            let (cv, _) = mesh_texels(&cube_quads, |_| true);
            let at = push(&mut texels, &cv);
            commands.push(Command { op: OP_MESH_VERTICES, id: 2, a: 0, b: 24, c: at, ..Default::default() });
            commands.push(Command { op: if iteration == 1 { OP_MESH_COMMIT } else { OP_MESH_REFIT }, id: 2, a: MESH_DYNAMIC, ..Default::default() });
            for (i, c) in commands.iter().enumerate() { slot.commands[i] = *c; }

            let identity = [1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 0.0, 1.0, 0.0];
            slot.instances[0] = Instance { transform: identity, mesh: 1, material_base: 0, mask: 0xff, flags: 0, lightmap: -1, ..Default::default() };
            slot.instances[1] = Instance { transform: identity, mesh: 2, material_base: 7, mask: 0xff, flags: INSTANCE_AVATAR | INSTANCE_EMITTER, lightmap: -1, ..Default::default() };
            let light_off_at = std::env::var("LIGHTOFF").ok().and_then(|v| v.parse::<u64>().ok());
            let lit = if light_off_at.map_or(false, |n| iteration as u64 >= n) { 0.0f32 } else { 1.0f32 };
            for (i, q) in room_quads.iter().enumerate() { slot.materials[i] = Material { base_color: [q.albedo[0], q.albedo[1], q.albedo[2], 1.0], emission: [q.emission[0] * lit, q.emission[1] * lit, q.emission[2] * lit, 0.0], base_st: [1.0, 1.0, 0.0, 0.0], emission_st: [1.0, 1.0, 0.0, 0.0], base_texture: -1, emission_texture: -1, flags: if i == 3 { MATERIAL_EMITTER } else { 0 }, ..Default::default() }; }
            let c0 = &cube_quads[0];
            slot.materials[7] = Material { base_color: [c0.albedo[0], c0.albedo[1], c0.albedo[2], 1.0], emission: [c0.emission[0] * lit, c0.emission[1] * lit, c0.emission[2] * lit, 0.0], base_st: [1.0, 1.0, 0.0, 0.0], emission_st: [1.0, 1.0, 0.0, 0.0], base_texture: -1, emission_texture: -1, flags: MATERIAL_EMITTER, ..Default::default() };
            slot.lights[0] = Light { position: [0.0, 2.5, -1.0, f32::from_bits(LIGHT_POINT)], direction: [0.0, -1.0, 0.0, 8.0], color: [3.0 * lit, 2.7 * lit, 2.4 * lit, 1.0], spot: [0.0; 4] };
            slot.dirty[0] = [cube_center[0], cube_center[1], cube_center[2], if std::env::var("SOFT").is_ok() { -1.5 } else { 1.5 }];
            if light_off_at == Some(iteration as u64) { slot.dirty[0] = [0.0, 2.5, -1.0, if std::env::var("HARDOFF").is_ok() { 13.0 } else { 100013.0 }]; }
            slot.emitters[0] = Emitter { instance: 0, geometry: 6, triangle_count: 2, cdf: 0.7 };
            slot.emitters[1] = Emitter { instance: 1, geometry: 0, triangle_count: 12, cdf: 1.0 };
            let v = View { view, projection, inverse_view_projection: [0.0; 16], previous_view_projection: prev_vp, position: [camera[0], camera[1], camera[2], 0.0] };
            slot.params = FrameParams {
                frame, instance_count: 2, material_count: 8, light_count: 1, command_count: commands.len() as u32, upload_texels: texels.len() as u32,
                flags: FLAG_SCREEN_REUSE | FLAG_SHADOWS | if std::env::var("NODENOISE").is_ok() { 0 } else { FLAG_DENOISE } | FLAG_REORDER | if std::env::var("NOPROBES").map_or(false, |v| !v.is_empty()) { 0 } else { FLAG_PROBES }, width: w, height: h, view_count: views, samples: 2, bounces: 2,
                max_distance: 100.0, ao_radius: 0.5, emission_scale: 1.0, sky_intensity: 1.0, time: t, firefly_clamp: 30.0, temporal_response: 0.05,
                light_intensity: 1.0, normal_bias: 0.005, emitter_count: 2, ao_intensity: 0.5, context: 0, parity: (iteration & 1) as u32, cache_cell_size: 0.2, dirty_count: std::env::var("DIRTY").ok().and_then(|v| v.parse().ok()).unwrap_or(1), ambient: [[0.0; 4]; 7], views: [v, v],
            };

            let all: Vec<Quad> = room().into_iter().chain(cube(cube_center, 0.6)).collect();
            let tanf = (fov * 0.5).tan();
            for y in 0..h {
                for x in 0..w {
                    let ndc = [((x as f32 + 0.5) / w as f32) * 2.0 - 1.0, ((y as f32 + 0.5) / h as f32) * 2.0 - 1.0];
                    let dir = normalize([ndc[0] * tanf * w as f32 / h as f32, ndc[1] * tanf, 1.0]);
                    match intersect(&all, camera, dir) {
                        Some((t, i)) => {
                            let q = &all[i];
                            let rel = mul(dir, t);
                            for vi in 0..views { store128(gptr, (pw * 2 * 8) as usize, (x + vi * w) * 2, y, [rel[0].to_bits(), rel[1].to_bits(), rel[2].to_bits(), pack_normal(q.normal)]); }
                            let lit = add(q.emission, mul(q.albedo, 0.3));
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
            if iteration > 16 { for i in 0..8 { gpu_sum[i] += header.reserved[i] as u64; } gpu_n += 1; }
            prev_vp = vp;
            let mut current = Vec::with_capacity((pw * h) as usize);
            for y in 0..h { for x in 0..pw { let p = rptr.add((y * rpitch + x * 8) as usize) as *const u16; current.push(f16(*p) * 0.2126 + f16(*p.add(1)) * 0.7152 + f16(*p.add(2)) * 0.0722); } }
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
        times.sort_by(|a, b| a.partial_cmp(b).unwrap());
        let n = floor_n.max(1) as f64;
        println!("floor irradiance avg ({:.3}, {:.3}, {:.3}); red bleed left {:.4}, green bleed right {:.4}; non-finite {nan}; min ao {ao_min:.3}", floor[0] / n, floor[1] / n, floor[2] / n, red_side / n, green_side / n);
        let r = &header.reserved;
        println!("gpu us: total {} | scene {} update {} resolve {} probes {} gather {} denoise {}", r[0], r[1], r[2], r[3], r[4], r[5], r[6]);
        let a = gpu_sum.map(|v| v as f64 / gpu_n.max(1) as f64);
        println!("gpu us mean over {gpu_n}: total {:.0} | scene {:.0} update {:.0} resolve {:.0} probes {:.0} gather {:.0} temporal {:.0} wavelet {:.0}", a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7]);
        println!("round trip ms median {:.3}, p90 {:.3}; tracer cpu record {} us; meshes {}", times[times.len() / 2], times[times.len() * 9 / 10], header.build_time_us.load(Ordering::Relaxed), header.mesh_count.load(Ordering::Relaxed));
        if contexts > 1 { println!("second context fetched {second_ok}/{frames} frames"); }
        if !flicker.is_empty() { println!("frame-to-frame change {:.2}% (mean over {} frames, sway {})", 100.0 * flicker.iter().sum::<f64>() / flicker.len() as f64, flicker.len(), sway); }
        println!("wrote {out}");
    }
}
