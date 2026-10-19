//! The DXVA hardware route against the software route, per codec.
//! H.264, VP9 and AV1 decoding is bit-exact by specification, so the
//! hardware output (read back, test-only) must byte-match the CPU path
//! frame for frame. That catches slice, aperture and stride mistakes. Rows
//! skip loudly where this machine's GPU has no profile for the codec.
//!
//! Also covered: the sizeless-HEVC refusal (before the MFT is configured),
//! the opaque payload's slice exposure, AV1 config-OBU carriage,
//! flush/restart through `reset`, and H.264 low-latency decode for live
//! sources.

#![cfg(windows)]

use media_clock::Generation;
use media_decode::{SubmitOutcome, VideoDecoder, VideoFrame};
use media_demux::{DemuxLimits, Demuxer, Format, MemSource, MkvDemuxer, StreamEvent, VideoCodec};

struct Track {
    width: u32,
    height: u32,
    display_width: u32,
    display_height: u32,
    codec_private: Vec<u8>,
    aus: Vec<(Vec<u8>, i64)>,
}

fn video_track(fixture: &str, expect: VideoCodec) -> Track {
    let path = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../fixtures/mkv/")
        .join(fixture);
    let bytes = std::fs::read(path).expect("fixture readable");
    let mut demux = MkvDemuxer::open(
        Box::new(MemSource(bytes)),
        DemuxLimits::default(),
        Generation(1),
    )
    .expect("demux opens");
    let mut size = None;
    let mut display = None;
    let mut private = Vec::new();
    let mut video = None;
    let mut aus = Vec::new();
    loop {
        match demux.next_event().expect("event") {
            StreamEvent::Format(
                track,
                Format::Video {
                    codec,
                    coded_width,
                    coded_height,
                    display_width,
                    display_height,
                    codec_private,
                },
            ) => {
                assert_eq!(codec, expect);
                size = Some((coded_width, coded_height));
                display = Some((display_width, display_height));
                private = codec_private;
                video = Some(track);
            }
            StreamEvent::Au(au) if Some(au.track) == video => {
                aus.push((au.data, au.pts.as_micros()));
            }
            StreamEvent::Eos(_) => break,
            _ => {}
        }
    }
    let (width, height) = size.expect("video format");
    let (display_width, display_height) = display.expect("video format");
    Track {
        width,
        height,
        display_width,
        display_height,
        codec_private: private,
        aus,
    }
}

/// Crop a packed NV12 buffer to the display region: the coded pad (e.g.
/// 360 → 368 macroblock rounding) is unspecified content the two routes
/// may fill differently, so decode is bit-exact only over the visible
/// frame.
fn visible_nv12(data: &[u8], coded_w: u32, coded_h: u32, disp_w: u32, disp_h: u32) -> Vec<u8> {
    let (cw, _ch) = (coded_w as usize, coded_h as usize);
    let (dw, dh) = (disp_w as usize, disp_h as usize);
    let mut out = Vec::with_capacity(dw * dh * 3 / 2);
    for row in 0..dh {
        out.extend_from_slice(&data[row * cw..row * cw + dw]);
    }
    let uv = &data[cw * coded_h as usize..];
    for row in 0..dh / 2 {
        out.extend_from_slice(&uv[row * cw..row * cw + dw]);
    }
    out
}

/// Drive a decoder over the track, collecting every output cropped to
/// the display region as packed NV12 with its pts. Hardware frames are
/// read back and dropped as they emerge; holding them would drain the
/// MFT's small surface pool.
fn decode_all(
    decoder: &mut dyn VideoDecoder,
    aus: &[(Vec<u8>, i64)],
    display: (u32, u32),
) -> Vec<(i64, Vec<u8>)> {
    let mut frames = Vec::new();
    let mut take = |frame: VideoFrame| {
        let nv12 = match &frame {
            VideoFrame::Nv12(f) => (
                f.pts_us,
                visible_nv12(&f.data, f.width, f.height, display.0, display.1),
            ),
            VideoFrame::Opaque(f) => {
                let read = decode_mf::read_back_nv12(f).expect("readback");
                (
                    read.pts_us,
                    visible_nv12(&read.data, read.width, read.height, display.0, display.1),
                )
            }
        };
        frames.push(nv12);
    };
    for (au, pts) in aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if let Some(frame) = decoder.try_output().expect("output") {
                        take(frame);
                    }
                }
            }
        }
        while let Some(frame) = decoder.try_output().expect("output") {
            take(frame);
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(frame) = decoder.try_output().expect("output") {
        take(frame);
    }
    frames
}

fn assert_streams_match(codec: &str, hw: &[(i64, Vec<u8>)], sw: &[(i64, Vec<u8>)]) {
    assert_eq!(
        hw.len(),
        sw.len(),
        "{codec}: hardware decoded {} frames, software {}",
        hw.len(),
        sw.len()
    );
    for (i, (h, s)) in hw.iter().zip(sw.iter()).enumerate() {
        assert_eq!(h.0, s.0, "{codec}: frame {i} pts diverges");
        assert_eq!(
            h.1, s.1,
            "{codec}: frame {i} (pts {}) pixel data diverges",
            h.0
        );
    }
}

#[test]
fn h264_hardware_matches_software() {
    let track = video_track("h264-aac.mkv", VideoCodec::H264);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::H264,
        track.width,
        track.height,
        &track.codec_private,
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware H.264 on this machine ({e})");
            return;
        }
    };
    let hw_frames = decode_all(
        &mut hw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    let mut sw = decode_mf::H264Decoder::new(false).expect("software H.264");
    let sw_frames = decode_all(
        &mut sw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    assert!(sw_frames.len() >= track.aus.len() - 2);
    assert_streams_match("h264", &hw_frames, &sw_frames);

    // Flush/restart (the seek path): after reset the same stream decodes
    // again from its keyframe, and the post-reset output gate lets the
    // fresh timeline straight through.
    VideoDecoder::reset(&mut hw).expect("reset");
    let again = decode_all(
        &mut hw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    assert_streams_match("h264 after reset", &again, &sw_frames);
}

#[test]
fn vp9_hardware_matches_software() {
    let track = video_track("vp9-opus.webm", VideoCodec::Vp9);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::Vp9,
        track.width,
        track.height,
        &track.codec_private,
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware VP9 on this machine ({e})");
            return;
        }
    };
    let mut sw = match decode_mf::Vp9Decoder::new(track.width, track.height) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no software VP9 oracle ({e})");
            return;
        }
    };
    let hw_frames = decode_all(
        &mut hw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    let sw_frames = decode_all(
        &mut sw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    assert!(sw_frames.len() >= track.aus.len() - 2);
    assert_streams_match("vp9", &hw_frames, &sw_frames);
}

#[test]
fn av1_hardware_matches_rav1d() {
    let track = video_track("av1-opus.webm", VideoCodec::Av1);
    // Config OBUs ride the first real AU: the demuxer surfaces them for
    // AV1, and the constructor takes them.
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::Av1,
        track.width,
        track.height,
        &track.codec_private,
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware AV1 on this machine ({e})");
            return;
        }
    };
    let mut sw = decode_sw::SwAv1Decoder::new(1920 * 1088).expect("rav1d");
    let hw_frames = decode_all(
        &mut hw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    let sw_frames = decode_all(
        &mut sw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    assert!(sw_frames.len() >= track.aus.len() - 2);
    assert_streams_match("av1", &hw_frames, &sw_frames);
}

/// The engine feeds a decoder whose output it cannot take yet (a full
/// frame pool), one output per pass. The Store AV1 MFT answers
/// `ProcessInput` on a full output queue by waiting a second and then
/// refusing, and `GetInputStatus` reports accept-data throughout, so the
/// adapter must refuse on its own account before the MFT is asked.
#[test]
fn av1_submit_never_waits_on_undrained_output() {
    let track = video_track("av1-opus.webm", VideoCodec::Av1);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::Av1,
        track.width,
        track.height,
        &track.codec_private,
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware AV1 on this machine ({e})");
            return;
        }
    };
    let mut frames = 0usize;
    for (au, pts) in &track.aus {
        loop {
            let asked = std::time::Instant::now();
            let outcome = hw.submit(au, *pts).expect("submit");
            let waited = asked.elapsed();
            assert!(
                waited < std::time::Duration::from_millis(250),
                "submit at pts {pts} waited {waited:?} inside the decoder"
            );
            match outcome {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if hw.try_output().expect("output").is_some() {
                        frames += 1;
                    }
                }
            }
        }
    }
    hw.begin_drain().expect("drain");
    while hw.try_output().expect("output").is_some() {
        frames += 1;
    }
    assert!(
        frames >= track.aus.len() - 2,
        "decoded {frames} of {} frames",
        track.aus.len()
    );
}

/// HEVC on Windows has no software oracle, since DXVA is the only route.
/// The fixture decodes to the expected frame count at the coded
/// dimensions, pts monotonic in display order.
#[test]
fn hevc_decodes_through_dxva() {
    let track = video_track("h265-aac.mkv", VideoCodec::H265);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::H265,
        track.width,
        track.height,
        &track.codec_private,
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware HEVC on this machine ({e})");
            return;
        }
    };
    let frames = decode_all(
        &mut hw,
        &track.aus,
        (track.display_width, track.display_height),
    );
    assert!(
        frames.len() >= track.aus.len() - 2,
        "decoded {} of {} frames",
        frames.len(),
        track.aus.len()
    );
    assert!(
        frames.windows(2).all(|w| w[0].0 < w[1].0),
        "pts must be monotonic in display order"
    );
    let (w, h) = hw.output_size();
    assert_eq!(w, 640);
    assert!(h == 360 || h == 368, "coded height {h}");
}

/// The Store HEVC MFT null-derefs on its own worker thread if data arrives
/// on a sizeless input type. The refusal happens before the MFT is
/// configured and before any device exists, so this row runs on every
/// machine.
#[test]
fn sizeless_hevc_refuses_before_configure() {
    let err = decode_mf::HwVideoDecoder::new(decode_mf::HwCodec::H265, 0, 0, &[], false)
        .err()
        .expect("sizeless HEVC must refuse");
    assert!(err.0.contains("no frame size"), "{}", err.0);
}

/// The opaque payload contract: a DXVA frame exposes its texture-array
/// slice (honouring the MFT's subresource index) and the owning sample.
#[test]
fn dxva_payload_exposes_slice() {
    let track = video_track("h264-aac.mkv", VideoCodec::H264);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::H264,
        track.width,
        track.height,
        &[],
        false,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware H.264 on this machine ({e})");
            return;
        }
    };
    let mut seen = false;
    for (au, pts) in &track.aus {
        loop {
            match hw.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    let _ = hw.try_output().expect("output");
                }
            }
        }
        if let Some(VideoFrame::Opaque(frame)) = hw.try_output().expect("output") {
            let (texture, _subresource) = frame.image.d3d11_slice().expect("slice");
            assert!(!texture.is_null());
            assert!(!frame.image.hardware_buffer().is_null());
            seen = true;
            break;
        }
    }
    assert!(seen, "no opaque frame emerged");
}

/// How many access units a decoder takes in before its first frame
/// comes out.
fn inputs_before_first_output(decoder: &mut dyn VideoDecoder, aus: &[(Vec<u8>, i64)]) -> usize {
    for (i, (au, pts)) in aus.iter().enumerate() {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if decoder.try_output().expect("output").is_some() {
                        return i;
                    }
                }
            }
        }
        if decoder.try_output().expect("output").is_some() {
            return i + 1;
        }
    }
    aus.len()
}

/// Live decode hands each frame out as soon as the stream's reordering
/// allows: this fixture reorders by two frames, so the first frame is due
/// by the third access unit. Held until the decoder's reorder window
/// fills instead, it arrives behind a clock that the sound started.
#[test]
fn live_h264_hands_out_frames_without_waiting_for_the_reorder_window() {
    let track = video_track("h264-aac.mkv", VideoCodec::H264);
    let mut sw = decode_mf::H264Decoder::new(true).expect("software H.264");
    assert!(inputs_before_first_output(&mut sw, &track.aus) <= 3);
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::H264,
        track.width,
        track.height,
        &track.codec_private,
        true,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware H.264 on this machine ({e})");
            return;
        }
    };
    assert!(inputs_before_first_output(&mut hw, &track.aus) <= 3);
}

/// Low latency changes when frames leave the decoder, not which frames or
/// in what order.
#[test]
fn live_h264_decodes_the_same_frames_in_the_same_order() {
    let track = video_track("h264-aac.mkv", VideoCodec::H264);
    let display = (track.display_width, track.display_height);
    let mut reference = decode_mf::H264Decoder::new(false).expect("software H.264");
    let expected = decode_all(&mut reference, &track.aus, display);
    let mut sw = decode_mf::H264Decoder::new(true).expect("software H.264");
    assert_streams_match(
        "live h264",
        &decode_all(&mut sw, &track.aus, display),
        &expected,
    );
    let mut hw = match decode_mf::HwVideoDecoder::new(
        decode_mf::HwCodec::H264,
        track.width,
        track.height,
        &track.codec_private,
        true,
    ) {
        Ok(d) => d,
        Err(e) => {
            eprintln!("SKIPPED: no hardware H.264 on this machine ({e})");
            return;
        }
    };
    assert_streams_match(
        "live dxva h264",
        &decode_all(&mut hw, &track.aus, display),
        &expected,
    );
}
