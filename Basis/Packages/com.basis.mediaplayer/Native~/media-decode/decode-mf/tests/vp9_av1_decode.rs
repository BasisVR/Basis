//! Decode the committed WebM fixtures' VP9/AV1 tracks through the
//! platform decoder MFTs headless, checking the probe and sync driving
//! against the real Store-extension decoders. If an extension is not
//! installed the row skips loudly; the engine reports that absence as a
//! diagnostic.

#![cfg(windows)]

use media_clock::Generation;
use media_decode::{SubmitOutcome, VideoDecoder};
use media_demux::{DemuxLimits, Demuxer, Format, MemSource, MkvDemuxer, StreamEvent, VideoCodec};

struct Track {
    width: u32,
    height: u32,
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
                    ..
                },
            ) => {
                assert_eq!(codec, expect);
                size = Some((coded_width, coded_height));
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
    Track { width, height, aus }
}

fn decode_count(decoder: &mut dyn VideoDecoder, aus: &[(Vec<u8>, i64)]) -> usize {
    let mut frames = 0usize;
    for (au, pts) in aus {
        loop {
            match decoder.submit(au, *pts).expect("submit") {
                SubmitOutcome::Accepted => break,
                SubmitOutcome::NotAccepting => {
                    if let Some(frame) = decoder.try_output().expect("output") {
                        assert!(!frame.as_nv12().expect("nv12").data.is_empty());
                        frames += 1;
                    }
                }
            }
        }
        while let Some(frame) = decoder.try_output().expect("output") {
            assert!(!frame.as_nv12().expect("nv12").data.is_empty());
            frames += 1;
        }
    }
    decoder.begin_drain().expect("drain");
    while let Some(_frame) = decoder.try_output().expect("output") {
        frames += 1;
    }
    frames
}

#[test]
fn vp9_fixture_decodes_through_the_platform_mft() {
    let track = video_track("vp9-opus.webm", VideoCodec::Vp9);
    let mut decoder = match decode_mf::Vp9Decoder::new(track.width, track.height) {
        Ok(decoder) => decoder,
        Err(e) if e.0.contains("no VP9 decoder installed") => {
            eprintln!("SKIPPED: {e} (install the VP9 Video Extensions to run this row)");
            return;
        }
        Err(e) => panic!("VP9 decoder: {e}"),
    };
    let frames = decode_count(&mut decoder, &track.aus);
    // The fixture is 3 s at 30 fps.
    assert!(
        frames >= track.aus.len() - 2,
        "decoded {frames} of {} frames",
        track.aus.len()
    );
}

#[test]
fn av1_fixture_decodes_through_the_platform_mft() {
    let track = video_track("av1-opus.webm", VideoCodec::Av1);
    let mut decoder = match decode_mf::Av1Decoder::new(track.width, track.height) {
        Ok(decoder) => decoder,
        Err(e) if e.0.contains("no AV1 decoder installed") => {
            eprintln!("SKIPPED: {e} (install the AV1 Video Extension to run this row)");
            return;
        }
        Err(e) => panic!("AV1 decoder: {e}"),
    };
    let frames = decode_count(&mut decoder, &track.aus);
    assert!(
        frames >= track.aus.len() - 2,
        "decoded {frames} of {} frames",
        track.aus.len()
    );
}
