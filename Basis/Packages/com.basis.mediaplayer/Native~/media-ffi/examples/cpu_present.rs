use std::time::{Duration, Instant};

use basis_media::capture::{
    BmEncodedFrame, bm_capture_close, bm_capture_open, bm_capture_read_video,
    bm_capture_set_preview, bm_capture_wait,
};
use basis_media::cpu_present::bm_session_take_bgra;
use basis_media::{
    BmSnapshot, bm_session_close, bm_session_open, bm_session_play, bm_session_poll,
};

fn main() {
    let desc = br#"{"url":"basis-push://cpu","decode_preference":"software_only"}"#;
    let mut session = 0u64;
    // SAFETY: valid descriptor bytes and out pointer.
    let opened = unsafe { bm_session_open(desc.as_ptr(), desc.len(), &mut session) };
    assert_eq!(opened, 0);
    bm_session_play(session);
    let config = br#"{"fps":30,"bitrate_kbps":6000,"max_width":1280,"max_height":720}"#;
    let mut capture = 0u64;
    // SAFETY: as above.
    let opened = unsafe { bm_capture_open(config.as_ptr(), config.len(), &mut capture) };
    assert_eq!(opened, 0);
    assert_eq!(bm_capture_set_preview(capture, session), 0);
    let mut encoded = vec![0u8; 1 << 20];
    let mut bgra = vec![0u8; 1280 * 720 * 4];
    let (mut taken, mut sizes, mut brightness) = (0u32, (0u32, 0u32), 0u64);
    let start = Instant::now();
    let mut convert = Duration::ZERO;
    while start.elapsed() < Duration::from_secs(4) {
        if bm_capture_wait(capture, 15) > 0 {
            let mut info = BmEncodedFrame::default();
            // SAFETY: buffer sized as passed.
            while unsafe {
                bm_capture_read_video(capture, encoded.as_mut_ptr(), encoded.len(), &mut info)
            } > 0
            {}
        }
        // SAFETY: BmSnapshot is plain repr(C) data, valid when zeroed.
        let mut snapshot: BmSnapshot = unsafe { std::mem::zeroed() };
        // SAFETY: valid out pointer.
        unsafe { bm_session_poll(session, &mut snapshot) };
        let (mut w, mut h) = (0u32, 0u32);
        let t = Instant::now();
        // SAFETY: bgra holds the stated capacity; out-params are writable.
        let got =
            unsafe { bm_session_take_bgra(session, bgra.as_mut_ptr(), bgra.len(), &mut w, &mut h) };
        if got == 1 {
            convert += t.elapsed();
            taken += 1;
            sizes = (w, h);
            brightness = bgra[..(w * h * 4) as usize]
                .iter()
                .step_by(97)
                .map(|&b| u64::from(b))
                .sum::<u64>()
                / ((w * h * 4) as u64 / 97).max(1);
        }
    }
    // SAFETY: as above.
    let mut snapshot: BmSnapshot = unsafe { std::mem::zeroed() };
    // SAFETY: valid out pointer.
    unsafe { bm_session_poll(session, &mut snapshot) };
    println!(
        "cpu frames taken {taken} at {}x{}, mean byte {brightness}, avg convert {:?}",
        sizes.0,
        sizes.1,
        convert / taken.max(1)
    );
    println!(
        "engine state {} decoded {} presented {}",
        snapshot.state, snapshot.frames_decoded, snapshot.frames_presented
    );
    bm_capture_close(capture);
    // SAFETY: handle from open.
    unsafe { bm_session_close(session) };
}
