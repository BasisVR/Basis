//! `bm_drain_log` over the rlib: the process log is drainable with no
//! session open, which is the whole reason the channel exists. Its own
//! test binary, so nothing else in the crate's suite shares the ring.

use basis_media::{BM_ERR_INVALID_ARG, BM_LOG_DETAIL_CAP, BmLogRecord, bm_drain_log};

/// One row: the ring is process-wide state and the harness runs rows in
/// parallel, so a second row here would drain this one's lines.
#[test]
fn a_line_emitted_with_no_session_open_reaches_the_drain() {
    let mut buf = vec![0u8; size_of::<BmLogRecord>() * 8];
    let out = buf.as_mut_ptr() as *mut BmLogRecord;

    // SAFETY: NULL is the documented refusal; nothing is read or written.
    let refused = unsafe { bm_drain_log(std::ptr::null_mut(), 8, std::ptr::null_mut()) };
    assert_eq!(
        refused, BM_ERR_INVALID_ARG,
        "a null buffer is refused rather than written through"
    );

    // SAFETY: out points to 8 writable records; a null out_dropped is
    // documented as "do not report".
    let drained = unsafe { bm_drain_log(out, 8, std::ptr::null_mut()) };
    assert_eq!(
        drained, 0,
        "whatever the process said before is not this row's"
    );

    // Long enough to truncate, with a multi-byte character sat exactly
    // where the mark's room ends, so the step back to a char boundary
    // has to happen after the mark is accounted for, not before it.
    let long = format!("{}é tail", "x".repeat(BM_LOG_DETAIL_CAP - 4));
    media_diag::log("rtsp transport: TCP (interleaved)");
    media_diag::log_at(media_diag::Level::Error, &long);

    let mut dropped = u64::MAX;
    // SAFETY: out points to 8 writable records; dropped is writable.
    let count = unsafe { bm_drain_log(out, 8, &mut dropped) };
    assert_eq!(count, 2, "both lines drained in one call");
    assert_eq!(dropped, 0, "nothing was evicted to make room for two lines");

    // SAFETY: the drain wrote count records at out.
    let records = unsafe { std::slice::from_raw_parts(out, count as usize) };

    let first = &records[0];
    assert_eq!(first.session, 0, "no session owns this line");
    assert_eq!(first.code, 18, "EventCode::Log");
    assert_eq!(first.level, 2, "Level::Info");
    assert_eq!(
        std::str::from_utf8(&first.detail[..first.detail_len as usize]).unwrap(),
        "rtsp transport: TCP (interleaved)"
    );

    let second = &records[1];
    assert_eq!(second.level, 0, "Level::Error");
    assert!(second.detail_len < BM_LOG_DETAIL_CAP as u32);
    let text = std::str::from_utf8(&second.detail[..second.detail_len as usize])
        .expect("truncation lands on a char boundary");
    assert!(text.ends_with('…'), "a cut line says it was cut");
    assert!(long.starts_with(text.trim_end_matches('…')));
    assert!(text.len() < long.len(), "the long line was truncated");

    // SAFETY: out points to 8 writable records.
    let after = unsafe { bm_drain_log(out, 8, std::ptr::null_mut()) };
    assert_eq!(after, 0, "a drained log is empty");
}
