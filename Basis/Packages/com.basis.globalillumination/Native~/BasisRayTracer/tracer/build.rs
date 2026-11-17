use std::path::PathBuf;
use std::process::Command;

fn compile(dxc: &PathBuf, shaders: &PathBuf, out: &PathBuf, file: &str, profile: &str, entry: &str, extension: &str, flags: &[&str]) {
    let name = if entry.is_empty() { file.trim_end_matches(".hlsl").to_string() } else { entry.to_string() };
    let mut c = Command::new(dxc);
    c.arg("-T").arg(profile).arg("-HV").arg("2021").arg("-O3").args(flags);
    c.arg("-D").arg(format!("OMM_LEVEL={}", omm_level()));
    for define in std::env::var("BASIS_RT_DEFINES").unwrap_or_default().split_whitespace() { c.arg("-D").arg(define); }
    c.arg("-Fo").arg(out.join(format!("{name}.{extension}"))).arg(shaders.join(file));
    if !entry.is_empty() { c.arg("-E").arg(entry); }
    let output = c.output().unwrap_or_else(|e| panic!("run dxc at {}: {e}", dxc.display()));
    if !output.status.success() { panic!("dxc failed for {name}.{extension}:\n{}", String::from_utf8_lossy(&output.stderr)); }
}

fn omm_level() -> u32 { std::env::var("BASIS_RT_OMM_LEVEL").ok().and_then(|v| v.parse().ok()).filter(|v| (2..=5).contains(v)).unwrap_or(3) }
const TRACE_ENTRIES: [&str; 6] = ["TraceClassify", "TraceGather", "TraceProbes", "TraceSphere", "TraceSpatial", "TraceListed"];

fn main() {
    println!("cargo:rerun-if-env-changed=BASIS_RT_OMM_LEVEL");
    println!("cargo:rustc-env=BASIS_RT_OMM_LEVEL={}", omm_level());
    let root = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap());
    let target_windows = std::env::var("CARGO_CFG_TARGET_OS").map_or(false, |v| v == "windows");
    let default_dxc = if cfg!(windows) { root.join("../.deps/dxc/build/native/bin/x64/dxc.exe") } else { let local = root.join("../.deps/dxc-linux/bin/dxc"); if local.exists() { local } else { PathBuf::from("dxc") } };
    let dxc = std::env::var("BASIS_RT_DXC").map(PathBuf::from).unwrap_or(default_dxc);
    let shaders = root.join("shaders");
    for f in ["common.hlsli", "cache.hlsli", "hitobject.hlsli", "trace.hlsl", "upload.hlsl", "denoise.hlsl", "resolve.hlsl", "probe.hlsl", "probe.hlsli", "bounds.hlsl", "material.hlsli", "emitters.hlsl", "omm.hlsl"] { println!("cargo:rerun-if-changed=shaders/{f}"); }
    println!("cargo:rerun-if-env-changed=BASIS_RT_DXC");
    println!("cargo:rerun-if-env-changed=BASIS_RT_DEFINES");
    let compute: &[(&str, &str)] = &[
        ("upload.hlsl", "CopyVertices"),
        ("upload.hlsl", "CopyIndices"),
        ("upload.hlsl", "CopyPositions"),
        ("upload.hlsl", "TexturePixels"),
        ("upload.hlsl", "Downsample"),
        ("resolve.hlsl", "CacheResolve"),
        ("resolve.hlsl", "CacheMaintain"),
        ("probe.hlsl", "ProbeResolve"),
        ("bounds.hlsl", "SubmeshBounds"),
        ("bounds.hlsl", "SubmeshBoundsClear"),
        ("emitters.hlsl", "EmitterWeights"),
        ("emitters.hlsl", "EmitterPrefix"),
        ("emitters.hlsl", "EmitterCuts"),
        ("omm.hlsl", "OmmBake"),
        ("denoise.hlsl", "Temporal"),
        ("denoise.hlsl", "Wavelet"),
    ];
    if target_windows {
        let dxil = ["-Qstrip_debug", "-Qstrip_reflect"];
        for (file, entry) in compute { compile(&dxc, &shaders, &out, file, "cs_6_6", entry, "dxil", &dxil); }
        for (mode, entry) in TRACE_ENTRIES.iter().enumerate() {
            let (m, e) = (format!("TRACE_MODE={mode}"), format!("TRACE_ENTRY={entry}"));
            compile(&dxc, &shaders, &out, "trace.hlsl", "cs_6_6", entry, "dxil", &["-Qstrip_debug", "-Qstrip_reflect", "-D", &m, "-D", &e]);
        }
        println!("cargo:rustc-link-arg-bins=/EXPORT:D3D12SDKVersion,DATA");
        println!("cargo:rustc-link-arg-bins=/EXPORT:D3D12SDKPath,DATA");
    }
    let spirv = ["-spirv", "-fspv-target-env=vulkan1.3", "-fvk-use-dx-layout", "-fspv-use-unknown-image-format", "-fvk-bind-resource-heap", "0", "1", "-D", "BASIS_SPIRV"];
    for (mode, entry) in TRACE_ENTRIES.iter().enumerate() {
        let (m, e) = (format!("TRACE_MODE={mode}"), format!("TRACE_ENTRY={entry}"));
        let mut flags = spirv.to_vec();
        flags.extend(["-D", m.as_str(), "-D", e.as_str()]);
        compile(&dxc, &shaders, &out, "trace.hlsl", "cs_6_6", entry, "spv", &flags);
    }
    for (file, entry) in compute { compile(&dxc, &shaders, &out, file, "cs_6_6", entry, "spv", &spirv); }
}
