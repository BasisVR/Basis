//! With the `librist` feature on: link the staged librist static and compile
//! the C layout-check shim the FFI test asserts against. With it off (the
//! default) this build script does nothing and the crate is a stub.

use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-changed=csrc/layout_check.c");
    println!("cargo:rerun-if-env-changed=BASIS_LIBRIST_DIR");
    if std::env::var_os("CARGO_FEATURE_LIBRIST").is_none() {
        return;
    }

    let manifest = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));
    let staged = match std::env::var_os("BASIS_LIBRIST_DIR") {
        Some(dir) => PathBuf::from(dir),
        None => manifest.join("../third_party/librist"),
    };

    let rid = match (
        std::env::var("CARGO_CFG_TARGET_OS").as_deref(),
        std::env::var("CARGO_CFG_TARGET_ARCH").as_deref(),
    ) {
        (Ok("windows"), Ok("x86_64")) => "win-x64",
        (Ok("linux"), Ok("x86_64")) => "linux-x64",
        (Ok("android"), Ok("aarch64")) => "android-arm64",
        (os, arch) => panic!("media-rist/librist: no staged librist for {os:?}/{arch:?}"),
    };

    let lib_dir = staged.join(rid);
    let lib_name = if rid == "win-x64" {
        "rist.lib"
    } else {
        "librist.a"
    };
    let builder = match rid {
        "win-x64" => "tools/build-librist.ps1",
        "android-arm64" => "tools/build-librist-android.sh",
        _ => "tools/build-librist.sh",
    };
    assert!(
        lib_dir.join(lib_name).exists(),
        "media-rist/librist: {} not found in {}. Build it from source first: \
         run {} (see that script's header).",
        lib_name,
        lib_dir.display(),
        builder
    );

    println!("cargo:rustc-link-search=native={}", lib_dir.display());
    println!("cargo:rustc-link-lib=static=rist");
    if rid == "win-x64" {
        // librist's transitive Windows deps (a static archive doesn't carry
        // them): ws2_32 (UDP sockets), bcrypt (bundled mbedTLS entropy),
        // iphlpapi (GetAdaptersInfo, used for the adapter MAC).
        println!("cargo:rustc-link-lib=ws2_32");
        println!("cargo:rustc-link-lib=bcrypt");
        println!("cargo:rustc-link-lib=iphlpapi");
    }

    cc::Build::new()
        .file(manifest.join("csrc/layout_check.c"))
        .include(staged.join("include"))
        .compile("bm_rist_layout_check");
}
