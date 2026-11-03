use std::time::{Duration, Instant};

use basis_media::capture::{
    BmEncodedFrame, bm_capture_close, bm_capture_open, bm_capture_read_audio,
    bm_capture_read_video, bm_capture_set_preview, bm_capture_wait,
};
use basis_media::{
    BmSnapshot, bm_session_close, bm_session_open, bm_session_play, bm_session_poll,
};

fn main() {
    let desc = br#"{"url":"basis-push://preview"}"#;
    let mut session = 0u64;
    // SAFETY: valid descriptor bytes and out pointer.
    let opened = unsafe { bm_session_open(desc.as_ptr(), desc.len(), &mut session) };
    assert_eq!(opened, 0);
    bm_session_play(session);
    let config = br#"{"fps":30,"bitrate_kbps":6000,"audio":true,"pts_base_us":5000000}"#;
    let mut capture = 0u64;
    // SAFETY: as above.
    let opened = unsafe { bm_capture_open(config.as_ptr(), config.len(), &mut capture) };
    assert_eq!(opened, 0);
    assert_eq!(bm_capture_set_preview(capture, session), 0);
    let mut buf = vec![0u8; 1 << 20];
    let mut audio = vec![0u8; 4000];
    let (mut frames, mut keys, mut audio_packets, mut waits, mut first_pts) =
        (0, 0, 0, 0, i64::MIN);
    let start = Instant::now();
    while start.elapsed() < Duration::from_secs(4) {
        waits += 1;
        if bm_capture_wait(capture, 100) <= 0 {
            continue;
        }
        let mut info = BmEncodedFrame::default();
        // SAFETY: buffers sized as passed.
        while unsafe { bm_capture_read_video(capture, buf.as_mut_ptr(), buf.len(), &mut info) } > 0
        {
            frames += 1;
            keys += info.key;
            if first_pts == i64::MIN {
                first_pts = info.pts_us;
            }
        }
        let mut pts = 0i64;
        // SAFETY: as above.
        while unsafe { bm_capture_read_audio(capture, audio.as_mut_ptr(), audio.len(), &mut pts) }
            > 0
        {
            audio_packets += 1;
        }
    }
    // SAFETY: BmSnapshot is a plain repr(C) struct of integers, valid when zeroed.
    let mut snapshot: BmSnapshot = unsafe { std::mem::zeroed() };
    // SAFETY: valid out pointer.
    unsafe { bm_session_poll(session, &mut snapshot) };
    println!(
        "frames {frames} keys {keys} audio {audio_packets} waits {waits} first_pts {first_pts}"
    );
    println!(
        "preview state {} decoded {} size {}x{}",
        snapshot.state, snapshot.frames_decoded, snapshot.width, snapshot.height
    );
    bm_capture_close(capture);
    // SAFETY: handle from open.
    unsafe { bm_session_close(session) };
}
