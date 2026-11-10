use std::path::PathBuf;
use std::process::Command;

fn compile(dxc: &PathBuf, shaders: &PathBuf, out: &PathBuf, file: &str, profile: &str, entry: &str, extension: &str, flags: &[&str]) {
    let name = if entry.is_empty() { file.trim_end_matches(".hlsl").to_string() } else { entry.to_string() };
    let mut c = Command::new(dxc);
    c.arg("-T").arg(profile).arg("-HV").arg("2021").arg("-O3").args(flags);
    for define in std::env::var("BASIS_RT_DEFINES").unwrap_or_default().split_whitespace() { c.arg("-D").arg(define); }
    c.arg("-Fo").arg(out.join(format!("{name}.{extension}"))).arg(shaders.join(file));
    if !entry.is_empty() { c.arg("-E").arg(entry); }
    let output = c.output().unwrap_or_else(|e| panic!("run dxc at {}: {e}", dxc.display()));
    if !output.status.success() { panic!("dxc failed for {name}.{extension}:\n{}", String::from_utf8_lossy(&output.stderr)); }
}

fn main() {
    let root = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
    let out = PathBuf::from(std::env::var("OUT_DIR").unwrap());
    let target_windows = std::env::var("CARGO_CFG_TARGET_OS").map_or(false, |v| v == "windows");
    let default_dxc = if cfg!(windows) { root.join("../.deps/dxc/build/native/bin/x64/dxc.exe") } else { let local = root.join("../.deps/dxc-linux/bin/dxc"); if local.exists() { local } else { PathBuf::from("dxc") } };
    let dxc = std::env::var("BASIS_RT_DXC").map(PathBuf::from).unwrap_or(default_dxc);
    let shaders = root.join("shaders");
    for f in ["common.hlsli", "cache.hlsli", "hitobject.hlsli", "trace.hlsl", "upload.hlsl", "denoise.hlsl", "resolve.hlsl", "probe.hlsl", "probe.hlsli", "bounds.hlsl"] { println!("cargo:rerun-if-changed=shaders/{f}"); }
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
        ("denoise.hlsl", "Temporal"),
        ("denoise.hlsl", "Wavelet"),
    ];
    if target_windows {
        let dxil = ["-Qstrip_debug", "-Qstrip_reflect"];
        compile(&dxc, &shaders, &out, "trace.hlsl", "lib_6_9", "", "dxil", &dxil);
        for (file, entry) in compute { compile(&dxc, &shaders, &out, file, "cs_6_6", entry, "dxil", &dxil); }
        println!("cargo:rustc-link-arg-bins=/EXPORT:D3D12SDKVersion,DATA");
        println!("cargo:rustc-link-arg-bins=/EXPORT:D3D12SDKPath,DATA");
    }
    let spirv = ["-spirv", "-fspv-target-env=vulkan1.3", "-fvk-use-dx-layout", "-fspv-use-unknown-image-format", "-fvk-bind-resource-heap", "0", "1", "-D", "BASIS_SPIRV"];
    compile(&dxc, &shaders, &out, "trace.hlsl", "lib_6_6", "", "spv", &spirv);
    for (file, entry) in compute { compile(&dxc, &shaders, &out, file, "cs_6_6", entry, "spv", &spirv); }
}
