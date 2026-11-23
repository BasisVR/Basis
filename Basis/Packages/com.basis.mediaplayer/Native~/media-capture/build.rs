use std::path::PathBuf;

fn main() {
    println!("cargo:rerun-if-env-changed=BASIS_CAPTURE_HELPER");
    let out = PathBuf::from(std::env::var("OUT_DIR").expect("OUT_DIR")).join("capture-helper.bin");
    let windows = std::env::var("CARGO_CFG_TARGET_OS").is_ok_and(|os| os == "windows");
    let helper = std::env::var("BASIS_CAPTURE_HELPER")
        .ok()
        .filter(|p| windows && !p.is_empty());
    let bytes = match helper {
        Some(path) => {
            println!("cargo:rerun-if-changed={path}");
            std::fs::read(&path).unwrap_or_else(|e| panic!("BASIS_CAPTURE_HELPER {path}: {e}"))
        }
        None => Vec::new(),
    };
    let unchanged = std::fs::read(&out).is_ok_and(|old| old == bytes);
    if !unchanged {
        std::fs::write(&out, bytes).expect("write capture-helper.bin");
    }
}
