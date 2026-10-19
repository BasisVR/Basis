//! `bm_capabilities` over the rlib: the size/fill calling convention and
//! the blob's presence on the Windows host (the shape itself is pinned in
//! media-engine).

use basis_media::bm_capabilities;

#[test]
fn size_then_fill_round_trips() {
    // SAFETY: NULL + 0 is the documented sizing call.
    let len = unsafe { bm_capabilities(std::ptr::null_mut(), 0) };
    assert!(len > 0, "sizing call returned {len}");

    let mut buf = vec![0u8; len as usize];
    // SAFETY: buf holds len writable bytes.
    let written = unsafe { bm_capabilities(buf.as_mut_ptr(), buf.len()) };
    assert_eq!(written, len);

    let json: serde_json::Value = serde_json::from_slice(&buf).expect("valid UTF-8 JSON");
    assert_eq!(json["version"], 1);
    #[cfg(windows)]
    assert_eq!(json["platform"], "windows-x64");
    #[cfg(target_os = "linux")]
    assert_eq!(json["platform"], "linux-x64");
    assert!(json["video"].as_array().is_some_and(|v| !v.is_empty()));
}

#[test]
fn short_buffer_writes_nothing_and_still_sizes() {
    let mut buf = [0xAAu8; 4];
    // SAFETY: buf holds 4 writable bytes; shorter than any real blob.
    let len = unsafe { bm_capabilities(buf.as_mut_ptr(), buf.len()) };
    assert!(len > 4);
    assert_eq!(buf, [0xAA; 4], "short buffer must stay untouched");
}
